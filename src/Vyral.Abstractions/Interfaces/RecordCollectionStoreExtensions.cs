using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public static class RecordCollectionStoreExtensions
{
    private static readonly JsonSerializerOptions SnapshotHashJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static async Task<IEnumerable<VyralRecord>> QueryRecordsAsync(
        this IRecordCollectionStore store,
        string collection,
        QueryEnvelope query,
        CancellationToken ct = default)
        => (await store.QueryRecordsPageAsync(collection, query, ct)).Items;

    public static async Task<IEnumerable<VyralRecordMatch>> SearchRecordsAsync(
        this IRecordCollectionStore store,
        string collection,
        QueryEnvelope query,
        CancellationToken ct = default)
        => (await store.SearchRecordsPageAsync(collection, query, ct)).Items;

    public static async Task<IEnumerable<VyralRecord>> QueryAllRecordsAsync(
        this IRecordCollectionStore store,
        string collection,
        QueryEnvelope query,
        CancellationToken ct = default)
    {
        var results = new List<VyralRecord>();
        var token = query.ContinuationToken;

        do
        {
            var q = CloneWithToken(query, token);
            var page = await store.QueryRecordsPageAsync(collection, q, ct);
            results.AddRange(page.Items);
            token = page.ContinuationToken;
        }
        while (token != null);

        return results;
    }

    public static async Task<CollectionExportEnvelope?> ExportCollectionAsync(
        this IRecordCollectionStore store,
        string collection,
        QueryEnvelope? query = null,
        CancellationToken ct = default)
    {
        return await store.ExportCollectionAsync(collection, new CollectionExportRequest
        {
            Query = query
        }, ct);
    }

    public static async Task<CollectionExportEnvelope?> ExportCollectionAsync(
        this IRecordCollectionStore store,
        string collection,
        CollectionExportRequest? request,
        CancellationToken ct = default)
    {
        var policy = await store.GetCollectionPolicyAsync(collection, ct);
        if (policy is null)
        {
            return null;
        }

        ValidateCollectionExportRequest(request);
        var exportQuery = request?.Query ?? new QueryEnvelope();
        var maxRecords = request?.MaxRecords ?? CollectionSnapshotLimits.MaxRecords;
        var failOnLimitExceeded = request?.FailOnLimitExceeded ?? true;
        var (records, truncated, continuationToken) = await QueryRecordsForExportAsync(store, collection, exportQuery, maxRecords, ct);
        if (truncated && failOnLimitExceeded)
        {
            throw new System.InvalidOperationException($"Collection export exceeded maxRecords ({maxRecords}). Increase maxRecords or set failOnLimitExceeded to false to return a truncated snapshot.");
        }

        var envelope = new CollectionExportEnvelope
        {
            Collection = collection,
            Policy = policy,
            Records = records,
            Query = request?.Query,
            MaxRecords = maxRecords,
            RecordCount = records.Count,
            Truncated = truncated,
            ContinuationToken = continuationToken,
            ExportedAt = System.DateTime.UtcNow
        };
        envelope.ContentHash = ComputeCollectionSnapshotHash(envelope);
        return envelope;
    }

    public static async Task<CollectionImportResult> ImportCollectionAsync(
        this IRecordCollectionStore store,
        string targetCollection,
        CollectionImportRequest request,
        CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(request);
        RecordIdentityValidator.ValidateCollectionName(targetCollection);
        var snapshot = request.Snapshot ?? throw new System.ArgumentException("Collection import request requires a snapshot.", nameof(request));
        var sourceCollection = string.IsNullOrWhiteSpace(snapshot.Collection)
            ? snapshot.Policy?.Name
            : snapshot.Collection.Trim();
        if (string.IsNullOrWhiteSpace(sourceCollection))
        {
            throw new System.InvalidOperationException("Collection import snapshot requires a source collection name.");
        }

        if (!request.AllowCollectionRename && !string.Equals(sourceCollection, targetCollection, System.StringComparison.Ordinal))
        {
            throw new System.InvalidOperationException($"Collection import snapshot is for '{sourceCollection}', but target collection is '{targetCollection}'. Set allowCollectionRename to true to import under a different collection name.");
        }

        if (snapshot.Policy is null || string.IsNullOrWhiteSpace(snapshot.Policy.Name))
        {
            throw new System.InvalidOperationException("Collection import snapshot requires a collection policy.");
        }

        if (snapshot.RecordCount.HasValue && snapshot.RecordCount.Value != snapshot.Records.Count)
        {
            throw new System.InvalidOperationException($"Collection import snapshot recordCount is {snapshot.RecordCount.Value}, but records contains {snapshot.Records.Count} item(s).");
        }

        if (snapshot.Truncated && !request.AllowPartialSnapshot)
        {
            throw new System.InvalidOperationException("Collection import snapshot is truncated. Set allowPartialSnapshot to true to import a partial snapshot intentionally.");
        }

        var actualHash = ComputeCollectionSnapshotHash(snapshot);
        var expectedHash = string.IsNullOrWhiteSpace(request.ExpectedContentHash)
            ? snapshot.ContentHash
            : request.ExpectedContentHash.Trim();
        var hashComparison = BuildHashComparison(expectedHash, actualHash);
        if (hashComparison.Compared && !hashComparison.Matches)
        {
            throw new System.InvalidOperationException($"Collection import content hash mismatch. Expected {hashComparison.ExpectedHash}, actual {hashComparison.ActualHash}.");
        }

        var targetPolicy = ClonePolicy(snapshot.Policy);
        targetPolicy.Name = targetCollection;

        var policyStatus = CollectionImportPolicyStatuses.Created;
        var existingPolicy = await store.GetCollectionPolicyAsync(targetCollection, ct);
        if (existingPolicy is not null)
        {
            if (request.ReplaceExisting)
            {
                await store.DeleteCollectionAsync(targetCollection, ct);
                policyStatus = CollectionImportPolicyStatuses.Replaced;
            }
            else
            {
                if (!RecordCollectionPolicyComparer.AreEquivalent(existingPolicy, targetPolicy))
                {
                    throw new System.InvalidOperationException($"Collection '{targetCollection}' already exists with a different policy. Set replaceExisting to true to replace it.");
                }

                policyStatus = CollectionImportPolicyStatuses.ExistingEquivalent;
            }
        }

        await store.CreateCollectionAsync(targetPolicy, ct);
        var records = await store.UpsertRecordsAsync(targetCollection, new RecordBatchUpsertRequest
        {
            Records = snapshot.Records,
            ContinueOnError = request.ContinueOnError
        }, ct);

        return new CollectionImportResult
        {
            Collection = targetCollection,
            SourceCollection = sourceCollection,
            PolicyStatus = policyStatus,
            RecordCount = snapshot.Records.Count,
            ContentHash = actualHash,
            ContentHashComparison = hashComparison,
            Records = records
        };
    }

    public static string ComputeCollectionSnapshotHash(CollectionExportEnvelope envelope)
    {
        var material = new
        {
            collection = envelope.Collection,
            policy = envelope.Policy,
            records = envelope.Records
        };
        var json = JsonSerializer.Serialize(material, SnapshotHashJsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return "sha256:" + System.Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static CollectionSnapshotHashComparison BuildHashComparison(string? expectedHash, string? actualHash)
    {
        var normalizedExpected = string.IsNullOrWhiteSpace(expectedHash) ? null : expectedHash.Trim();
        var normalizedActual = string.IsNullOrWhiteSpace(actualHash) ? null : actualHash.Trim();
        if (normalizedExpected is null)
        {
            return new CollectionSnapshotHashComparison
            {
                ExpectedHash = null,
                ActualHash = normalizedActual,
                Compared = false,
                Matches = false,
                Status = CollectionSnapshotHashStatuses.NotProvided
            };
        }

        var matches = string.Equals(normalizedExpected, normalizedActual, System.StringComparison.Ordinal);
        return new CollectionSnapshotHashComparison
        {
            ExpectedHash = normalizedExpected,
            ActualHash = normalizedActual,
            Compared = true,
            Matches = matches,
            Status = normalizedActual is null ? CollectionSnapshotHashStatuses.ActualMissing : matches ? CollectionSnapshotHashStatuses.Matched : CollectionSnapshotHashStatuses.Drifted
        };
    }

    private static RecordCollectionPolicy ClonePolicy(RecordCollectionPolicy policy)
    {
        var json = JsonSerializer.Serialize(policy, SnapshotHashJsonOptions);
        return JsonSerializer.Deserialize<RecordCollectionPolicy>(json, SnapshotHashJsonOptions)
            ?? throw new System.InvalidOperationException("Collection import snapshot policy could not be cloned.");
    }

    private static void ValidateCollectionExportRequest(CollectionExportRequest? request)
    {
        if (request?.MaxRecords is null)
        {
            return;
        }

        if (request.MaxRecords <= 0)
        {
            throw new System.InvalidOperationException("Collection export maxRecords must be greater than zero.");
        }

        if (request.MaxRecords > CollectionSnapshotLimits.MaxRecords)
        {
            throw new System.InvalidOperationException($"Collection export maxRecords cannot exceed {CollectionSnapshotLimits.MaxRecords}.");
        }
    }

    private static async Task<(List<VyralRecord> Records, bool Truncated, string? ContinuationToken)> QueryRecordsForExportAsync(
        IRecordCollectionStore store,
        string collection,
        QueryEnvelope query,
        int? maxRecords,
        CancellationToken ct)
    {
        if (maxRecords is null)
        {
            return ((await store.QueryAllRecordsAsync(collection, query, ct)).ToList(), false, null);
        }

        var records = new List<VyralRecord>();
        var token = query.ContinuationToken;
        string? continuationToken = null;

        do
        {
            ct.ThrowIfCancellationRequested();
            var remaining = maxRecords.Value - records.Count;
            if (remaining <= 0)
            {
                continuationToken = token;
                break;
            }

            var pageQuery = CloneWithToken(query, token);
            pageQuery.Limit = pageQuery.Limit.HasValue
                ? System.Math.Min(pageQuery.Limit.Value, remaining)
                : remaining;
            var page = await store.QueryRecordsPageAsync(collection, pageQuery, ct);
            records.AddRange(page.Items);
            token = page.ContinuationToken;
            if (records.Count >= maxRecords.Value && token is not null)
            {
                continuationToken = token;
                break;
            }
        }
        while (token is not null);

        return (records, continuationToken is not null, continuationToken);
    }

    public static async Task<IEnumerable<VyralRecordMatch>> SearchAllRecordsAsync(
        this IRecordCollectionStore store,
        string collection,
        QueryEnvelope query,
        CancellationToken ct = default)
    {
        var results = new List<VyralRecordMatch>();
        var token = query.ContinuationToken;

        do
        {
            var q = CloneWithToken(query, token);
            var page = await store.SearchRecordsPageAsync(collection, q, ct);
            results.AddRange(page.Items);
            token = page.ContinuationToken;
        }
        while (token != null);

        return results;
    }

    private static QueryEnvelope CloneWithToken(QueryEnvelope q, string? token) => new()
    {
        PartitionKeys = q.PartitionKeys,
        Filter = q.Filter,
        Vector = q.Vector,
        Lexical = q.Lexical,
        OrderBy = q.OrderBy,
        Limit = q.Limit,
        ContinuationToken = token
    };
}
