using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Aws;

/// <summary>
/// Converts a DynamoDB Streams Lambda event into revision-fenced projection
/// mutations. Configure each source table with NEW_AND_OLD_IMAGES. The caller
/// supplies the table-to-collection mapping because Vyral collection table
/// prefixes are deployment-owned rather than part of the portable contract.
/// </summary>
public sealed class DynamoDbStreamsRecordProjectionConsumer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IRecordSearchProjection _projection;
    private readonly Func<string, string?> _collectionFromTableArn;

    public DynamoDbStreamsRecordProjectionConsumer(
        IRecordSearchProjection projection,
        Func<string, string?> collectionFromTableArn)
    {
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _collectionFromTableArn = collectionFromTableArn ?? throw new ArgumentNullException(nameof(collectionFromTableArn));
    }

    /// <summary>
    /// Processes the complete JSON payload of a DynamoDB Streams Lambda event.
    /// A malformed record throws so the Lambda integration retries the batch;
    /// no raw stream payload is included in the exception or logs.
    /// </summary>
    public async Task ProcessAsync(string streamEventJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(streamEventJson))
            throw new ArgumentException("A DynamoDB Streams event payload is required.", nameof(streamEventJson));

        try
        {
            using var document = JsonDocument.Parse(streamEventJson);
            if (!document.RootElement.TryGetProperty("Records", out var records) || records.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("DynamoDB Streams event has no Records array.");

            foreach (var streamRecord in records.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                await ProcessRecordAsync(streamRecord, ct).ConfigureAwait(false);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("DynamoDB Streams event is not valid JSON.", exception);
        }
    }

    private async Task ProcessRecordAsync(JsonElement streamRecord, CancellationToken ct)
    {
        var eventName = streamRecord.GetProperty("eventName").GetString();
        var tableArn = streamRecord.GetProperty("eventSourceARN").GetString();
        if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(tableArn))
            throw new InvalidOperationException("DynamoDB Streams record does not identify its mutation and source table.");
        var collection = _collectionFromTableArn(tableArn);
        if (string.IsNullOrWhiteSpace(collection))
            throw new InvalidOperationException("DynamoDB Streams record source table is not configured for a Vyral projection.");

        var dynamodb = streamRecord.GetProperty("dynamodb");
        var imageName = eventName == "REMOVE" ? "OldImage" : "NewImage";
        if (!dynamodb.TryGetProperty(imageName, out var image) ||
            !image.TryGetProperty("doc", out var docAttribute) ||
            !docAttribute.TryGetProperty("S", out var documentJson) ||
            documentJson.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"DynamoDB Streams record requires {imageName}.doc.S. Configure NEW_AND_OLD_IMAGES for the canonical collection table.");
        }

        var record = JsonSerializer.Deserialize<VyralRecord>(documentJson.GetString()!, JsonOptions)
            ?? throw new InvalidOperationException("DynamoDB Streams record does not contain a Vyral record document.");
        var sequence = dynamodb.TryGetProperty("SequenceNumber", out var sequenceElement) && sequenceElement.ValueKind == JsonValueKind.String
            ? sequenceElement.GetString()
            : null;

        var change = eventName switch
        {
            "INSERT" or "MODIFY" => RecordSearchProjectionChange.Upsert(collection, record, sequence),
            "REMOVE" => RecordSearchProjectionChange.Delete(collection, record.PartitionKey, record.Id, record.Revision ?? 0, sequence),
            _ => throw new InvalidOperationException("DynamoDB Streams record has an unsupported event name.")
        };
        await _projection.ProjectAsync(change, ct).ConfigureAwait(false);
    }
}
