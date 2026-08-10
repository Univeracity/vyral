using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Azure;

public class CosmosRecordCollectionStore : IRecordCollectionStore
{
    private const int MaxUnconditionalWriteAttempts = 5;
    private readonly Database _database;
    private readonly CosmosQueryBuilder _queryBuilder = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public CosmosRecordCollectionStore(CosmosClient client, string databaseId)
    {
        _database = client.GetDatabase(databaseId);
    }

    public async Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default)
    {
        var properties = CosmosVectorPolicyMapper.ToContainerProperties(policy);
        var existingPolicy = await GetCollectionPolicyAsync(policy.Name, ct);
        if (existingPolicy != null)
        {
            if (!RecordCollectionPolicyComparer.AreEquivalent(existingPolicy, policy, compareIndexedMetadata: false))
            {
                throw new InvalidOperationException($"Collection '{policy.Name}' already exists with a different policy.");
            }

            return;
        }

        var created = await _database.CreateContainerIfNotExistsAsync(properties, cancellationToken: ct);
        var actualPolicy = CosmosVectorPolicyMapper.FromContainerProperties(created.Resource);
        if (!RecordCollectionPolicyComparer.AreEquivalent(actualPolicy, policy, compareIndexedMetadata: false))
        {
            throw new InvalidOperationException($"Collection '{policy.Name}' already exists with a different policy.");
        }
    }

    public async Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default)
    {
        var names = new List<string>();
        var iterator = _database.GetContainerQueryIterator<ContainerProperties>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            names.AddRange(response.Select(container => container.Id));
        }

        return names.OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    public async Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);

        var container = _database.GetContainer(collection);
        try
        {
            var response = await container.ReadContainerAsync(cancellationToken: ct);
            return CosmosVectorPolicyMapper.FromContainerProperties(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);

        try
        {
            await _database.GetContainer(collection).DeleteContainerAsync(cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

    public async Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default)
    {
        await UpsertRecordAsync(collection, record, precondition: null, ct);
    }

    public async Task UpsertRecordAsync(string collection, VyralRecord record, RecordWritePrecondition? precondition, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidateRecord(record);

        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null) throw new InvalidOperationException($"Collection '{collection}' does not exist.");
        RecordVectorValidator.ValidateRecordVectors(collection, policy, record);

        var container = _database.GetContainer(collection);
        var partitionKey = new PartitionKey(record.PartitionKey);
        for (var attempt = 0; attempt < MaxUnconditionalWriteAttempts; attempt++)
        {
            var existing = await GetStoredRecordAsync(container, record.PartitionKey, record.Id, ct);
            RecordWritePreconditionValidator.EnsureSatisfied(
                precondition,
                existing is not null,
                existing?.Record.Etag,
                existing?.Record.Revision);

            ApplyPortableVersion(record, existing?.Record, DateTime.UtcNow);
            try
            {
                using var stream = new MemoryStream(SerializeRecord(record));
                using var response = existing is null
                    ? await container.CreateItemStreamAsync(stream, partitionKey, cancellationToken: ct)
                    : await container.ReplaceItemStreamAsync(
                        stream,
                        record.Id,
                        partitionKey,
                        new ItemRequestOptions { IfMatchEtag = existing.ProviderEtag },
                        ct);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (CosmosException ex) when (IsWriteConflict(ex))
            {
                if (precondition is { HasConditions: true })
                {
                    throw new InvalidOperationException("Record write precondition failed: record changed before the write could be committed.", ex);
                }

                if (attempt == MaxUnconditionalWriteAttempts - 1)
                {
                    throw new InvalidOperationException("Record write failed after repeated concurrent Cosmos updates.", ex);
                }
            }
        }

        throw new InvalidOperationException("Record write did not complete.");
    }

    public async Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidatePartitionKey(partitionKey);
        RecordIdentityValidator.ValidateRecordId(id);

        var container = _database.GetContainer(collection);
        return (await GetStoredRecordAsync(container, partitionKey, id, ct))?.Record;
    }

    public async Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidatePartitionKey(partitionKey);
        RecordIdentityValidator.ValidateRecordId(id);

        var container = _database.GetContainer(collection);
        try
        {
            await container.DeleteItemAsync<VyralRecord>(id, new PartitionKey(partitionKey), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

    public async Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        request.ValidatePreconditionAlignment();
        var itemResults = new List<RecordBatchUpsertItemResult>(request.Records.Count);
        int succeeded = 0, failed = 0;
        bool stopped = false;

        for (int i = 0; i < request.Records.Count; i++)
        {
            var record = request.Records[i];
            try
            {
                await UpsertRecordAsync(collection, record, request.GetPrecondition(i), ct);
                itemResults.Add(new RecordBatchUpsertItemResult
                {
                    Index = i, Id = record.Id, PartitionKey = record.PartitionKey,
                    Status = RecordUpsertStatuses.Succeeded, Etag = record.Etag, Revision = record.Revision
                });
                succeeded++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or CosmosException)
            {
                itemResults.Add(new RecordBatchUpsertItemResult
                {
                    Index = i, Id = record.Id, PartitionKey = record.PartitionKey,
                    Status = RecordUpsertStatuses.Failed, Error = ex.Message
                });
                failed++;
                if (!request.ContinueOnError) { stopped = i + 1 < request.Records.Count; break; }
            }
        }

        return new RecordBatchUpsertResult
        {
            Collection = collection, Requested = request.Records.Count,
            Attempted = succeeded + failed, Succeeded = succeeded,
            Failed = failed, StoppedOnError = stopped, Items = itemResults
        };
    }

    public async Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        ValidatePageLimit(query.Limit, "Query page size");

        var container = _database.GetContainer(collection);
        var plan = _queryBuilder.BuildRecordQuery(query);
        var queryDefinition = plan.ToQueryDefinition();

        var iterator = container.GetItemQueryStreamIterator(
            queryDefinition,
            continuationToken: query.ContinuationToken,
            requestOptions: new QueryRequestOptions { MaxItemCount = query.Limit });
        if (iterator.HasMoreResults)
        {
            using var response = await iterator.ReadNextAsync(ct);
            response.EnsureSuccessStatusCode();
            return new RecordQueryResult
            {
                Items = await ReadQueryDocumentsAsync<VyralRecord>(response.Content, ct),
                ContinuationToken = response.Headers.ContinuationToken
            };
        }

        return new RecordQueryResult();
    }

    public async Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        if (query.Vector == null)
        {
            var records = await QueryRecordsPageAsync(collection, query, ct);
            return new RecordSearchResult
            {
                Items = records.Items.Select(r => new VyralRecordMatch { Record = r, Score = 1.0f }).ToList(),
                ContinuationToken = records.ContinuationToken
            };
        }

        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null) throw new InvalidOperationException($"Collection '{collection}' does not exist.");
        var fieldPolicy = RecordVectorValidator.ValidateSearchVector(collection, policy, query.Vector);
        ValidatePageLimit(query.Limit, "Search page size");

        var container = _database.GetContainer(collection);
        var plan = _queryBuilder.BuildVectorSearchQuery(query);
        var queryDefinition = plan.ToQueryDefinition();

        var results = new List<VyralRecordMatch>();
        // Azure Cosmos DB's VectorDistance ORDER BY pipeline does not issue continuation tokens.
        // Query the portable Vector.Top candidate set once, then page that bounded, ranked set with
        // an adapter-owned offset token. Passing a token to Cosmos (or reading one from the response)
        // throws rather than producing a portable page.
        var iterator = container.GetItemQueryStreamIterator(
            queryDefinition,
            continuationToken: null,
            requestOptions: new QueryRequestOptions { MaxItemCount = query.Vector.Top });
        if (iterator.HasMoreResults)
        {
            using var response = await iterator.ReadNextAsync(ct);
            response.EnsureSuccessStatusCode();
            var documents = await ReadQueryDocumentsAsync<JsonElement>(response.Content, ct);
            foreach (var item in documents)
            {
                var record = item.GetProperty("c").Deserialize<VyralRecord>(JsonOptions)!;
                StripCosmosSystemProperties(record);
                var score = CosmosVectorPolicyMapper.DistanceToScore(
                    fieldPolicy.DistanceFunction,
                    item.GetProperty("SimilarityScore").GetSingle());
                if (query.Vector.MinScore is null || score >= query.Vector.MinScore)
                {
                    results.Add(new VyralRecordMatch
                    {
                        Record = record,
                        Score = score
                    });
                }
            }

            var ranked = results
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Record.PartitionKey, StringComparer.Ordinal)
                .ThenBy(match => match.Record.Id, StringComparer.Ordinal)
                .ToList();
            var offset = DecodeVectorContinuationToken(query.ContinuationToken);
            var pageSize = query.Limit ?? query.Vector.Top;
            var items = ranked.Skip(offset).Take(pageSize).ToList();
            var next = offset + items.Count < ranked.Count
                ? EncodeVectorContinuationToken(offset + items.Count)
                : null;
            return new RecordSearchResult { Items = items, ContinuationToken = next };
        }

        return new RecordSearchResult();
    }

    private static async Task<List<T>> ReadQueryDocumentsAsync<T>(Stream content, CancellationToken ct)
    {
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("Documents", out var documents) ||
            documents.ValueKind != JsonValueKind.Array)
        {
            return new List<T>();
        }

        var results = new List<T>();
        foreach (var item in documents.EnumerateArray())
        {
            var value = item.Deserialize<T>(JsonOptions);
            if (value != null)
            {
                if (value is VyralRecord record)
                {
                    StripCosmosSystemProperties(record);
                }

                results.Add(value);
            }
        }

        return results;
    }

    public static void ApplyPortableVersion(VyralRecord record, VyralRecord? existing, DateTime now)
    {
        record.UpdatedAt = now;
        record.CreatedAt = existing?.CreatedAt ?? record.CreatedAt ?? now;
        record.Revision = (existing?.Revision ?? 0) + 1;
        record.Etag = $"rev:{record.Revision}";
    }

    private static void ValidatePageLimit(int? limit, string description)
    {
        if (limit <= 0)
            throw new InvalidOperationException($"{description} must be greater than zero.");
    }

    private static string EncodeVectorContinuationToken(int offset) =>
        "cosmos-vector-v1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static int DecodeVectorContinuationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;
        const string prefix = "cosmos-vector-v1:";
        if (!token.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Continuation token is not valid for Cosmos vector search.");
        }

        try
        {
            var value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token[prefix.Length..]));
            var offset = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            return offset >= 0
                ? offset
                : throw new InvalidOperationException("Continuation token offset must be non-negative.");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new InvalidOperationException("Continuation token is not valid for Cosmos vector search.", ex);
        }
    }

    private static async Task<CosmosStoredRecord?> GetStoredRecordAsync(
        Container container,
        string partitionKey,
        string id,
        CancellationToken ct)
    {
        using var response = await container.ReadItemStreamAsync(id, new PartitionKey(partitionKey), cancellationToken: ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var record = await JsonSerializer.DeserializeAsync<VyralRecord>(response.Content, JsonOptions, ct)
            ?? throw new InvalidOperationException($"Cosmos record '{id}' could not be deserialized.");
        StripCosmosSystemProperties(record);
        if (string.IsNullOrWhiteSpace(response.Headers.ETag))
        {
            throw new InvalidOperationException($"Cosmos record '{id}' did not include an ETag.");
        }

        return new CosmosStoredRecord(record, response.Headers.ETag);
    }

    private static byte[] SerializeRecord(VyralRecord record)
    {
        var node = JsonSerializer.SerializeToNode(record, JsonOptions)?.AsObject()
            ?? throw new InvalidOperationException("Record could not be serialized for Cosmos.");
        RemoveCosmosSystemProperties(node);
        return JsonSerializer.SerializeToUtf8Bytes(node, JsonOptions);
    }

    private static void StripCosmosSystemProperties(VyralRecord record)
    {
        if (record.AdditionalProperties is null)
        {
            return;
        }

        foreach (var key in record.AdditionalProperties.Keys.Where(IsCosmosSystemProperty).ToList())
        {
            record.AdditionalProperties.Remove(key);
        }

        if (record.AdditionalProperties.Count == 0)
        {
            record.AdditionalProperties = null;
        }
    }

    private static void RemoveCosmosSystemProperties(JsonObject document)
    {
        foreach (var key in document.Select(item => item.Key).Where(IsCosmosSystemProperty).ToList())
        {
            document.Remove(key);
        }
    }

    private static bool IsCosmosSystemProperty(string key) =>
        key.StartsWith("_", StringComparison.Ordinal);

    private static bool IsWriteConflict(CosmosException exception) =>
        exception.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed;

    private sealed record CosmosStoredRecord(VyralRecord Record, string ProviderEtag);
}
