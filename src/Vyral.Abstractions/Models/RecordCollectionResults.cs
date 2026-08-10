using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public class RecordQueryResult
{
    [JsonPropertyName("items")]
    public List<VyralRecord> Items { get; set; } = new();

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}

public class RecordSearchResult
{
    [JsonPropertyName("items")]
    public List<VyralRecordMatch> Items { get; set; } = new();

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}

public class RecordBatchUpsertRequest
{
    [JsonPropertyName("records")]
    public List<VyralRecord> Records { get; set; } = new();

    [JsonPropertyName("preconditions")]
    public List<RecordWritePrecondition?> Preconditions { get; set; } = new();

    [JsonPropertyName("continueOnError")]
    public bool ContinueOnError { get; set; }

    public RecordWritePrecondition? GetPrecondition(int index) =>
        index >= 0 && index < Preconditions.Count ? Preconditions[index] : null;

    public void ValidatePreconditionAlignment()
    {
        if (Preconditions.Count > 0 && Preconditions.Count != Records.Count)
        {
            throw new InvalidOperationException("Batch upsert preconditions must be omitted or match the records count.");
        }
    }
}

public class RecordBatchUpsertResult
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("requested")]
    public int Requested { get; set; }

    [JsonPropertyName("attempted")]
    public int Attempted { get; set; }

    [JsonPropertyName("succeeded")]
    public int Succeeded { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("stoppedOnError")]
    public bool StoppedOnError { get; set; }

    [JsonPropertyName("items")]
    public List<RecordBatchUpsertItemResult> Items { get; set; } = new();
}

public class RecordBatchUpsertItemResult
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    /// <summary>Outcome of this record's upsert. Valid values are defined in <see cref="RecordUpsertStatuses"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("etag")]
    public string? Etag { get; set; }

    [JsonPropertyName("revision")]
    public int? Revision { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public static class RecordUpsertStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public class RecordWritePrecondition
{
    [JsonPropertyName("ifMatch")]
    public string? IfMatch { get; set; }

    [JsonPropertyName("ifNoneMatch")]
    public string? IfNoneMatch { get; set; }

    [JsonPropertyName("expectedEtag")]
    public string? ExpectedEtag { get; set; }

    [JsonPropertyName("expectedRevision")]
    public int? ExpectedRevision { get; set; }

    [JsonIgnore]
    public bool HasConditions =>
        !string.IsNullOrWhiteSpace(IfMatch) ||
        !string.IsNullOrWhiteSpace(IfNoneMatch) ||
        !string.IsNullOrWhiteSpace(ExpectedEtag) ||
        ExpectedRevision.HasValue;
}

public static class RecordWritePreconditionValidator
{
    public static void EnsureSatisfied(
        RecordWritePrecondition? precondition,
        bool exists,
        string? currentEtag,
        int? currentRevision)
    {
        if (precondition is not { HasConditions: true })
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(precondition.IfMatch) &&
            !string.IsNullOrWhiteSpace(precondition.IfNoneMatch))
        {
            throw new InvalidOperationException("Record write precondition failed: If-Match and If-None-Match cannot both be supplied.");
        }

        var expectedEtag = NormalizeEtag(precondition.ExpectedEtag);
        var ifMatch = NormalizeEtag(precondition.IfMatch);
        if (!string.IsNullOrWhiteSpace(expectedEtag) && !string.IsNullOrWhiteSpace(ifMatch) &&
            !string.Equals(expectedEtag, ifMatch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Record write precondition failed: expectedEtag and If-Match disagree.");
        }

        var matchEtag = expectedEtag ?? ifMatch;
        if (!string.IsNullOrWhiteSpace(matchEtag))
        {
            if (matchEtag == "*")
            {
                if (!exists)
                {
                    throw new InvalidOperationException("Record write precondition failed: record does not exist.");
                }
            }
            else if (!exists || !string.Equals(NormalizeEtag(currentEtag), matchEtag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Record write precondition failed: current ETag does not match.");
            }
        }

        if (precondition.ExpectedRevision.HasValue)
        {
            if (!exists || currentRevision != precondition.ExpectedRevision.Value)
            {
                throw new InvalidOperationException("Record write precondition failed: current revision does not match.");
            }
        }

        var noneMatch = NormalizeEtag(precondition.IfNoneMatch);
        if (!string.IsNullOrWhiteSpace(noneMatch))
        {
            if (noneMatch == "*")
            {
                if (exists)
                {
                    throw new InvalidOperationException("Record write precondition failed: record already exists.");
                }
            }
            else if (exists && string.Equals(NormalizeEtag(currentEtag), noneMatch, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Record write precondition failed: current ETag matches If-None-Match.");
            }
        }
    }

    public static void ThrowIfUnsupported(RecordWritePrecondition? precondition, string adapterName)
    {
        if (precondition is { HasConditions: true })
        {
            throw new NotSupportedException($"{adapterName} does not support record write preconditions.");
        }
    }

    public static void ThrowIfUnsupported(RecordBatchUpsertRequest request, string adapterName)
    {
        if (request.Preconditions.Any(item => item is { HasConditions: true }))
        {
            throw new NotSupportedException($"{adapterName} does not support record write preconditions.");
        }
    }

    private static string? NormalizeEtag(string? etag)
    {
        var value = etag?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
    }
}
