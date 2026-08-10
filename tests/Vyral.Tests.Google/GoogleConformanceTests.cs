using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Google;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Google;

public class GoogleRecordStoreConformanceTests : RecordCollectionStoreConformanceTests
{
    protected override async Task<IRecordCollectionStore> CreateStoreAsync()
    {
        var cs = GoogleLiveSettings.AlloyDbConnectionString!;
        var inner = new AlloyDbRecordCollectionStore(cs);
        await inner.InitializeAsync();
        var prefix = GoogleLiveSettings.UniquePrefix();
        return new ScopedAlloyDbRecordCollectionStore(inner, prefix);
    }

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_RoundTripsCollectionPolicyAndListsDeterministically() =>
        RunRecordStore_RoundTripsCollectionPolicyAndListsDeterministically();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_AllowsIdempotentCollectionCreateAndRejectsPolicyChange() =>
        RunRecordStore_AllowsIdempotentCollectionCreateAndRejectsPolicyChange();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_RoundTripsRecordsWithRevisionAndEtag() =>
        RunRecordStore_RoundTripsRecordsWithRevisionAndEtag();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_IncrementsRevisionAndPreservesCreatedAtOnUpdate() =>
        RunRecordStore_IncrementsRevisionAndPreservesCreatedAtOnUpdate();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_BatchUpsertHonorsErrorPolicyAndRevisionSemantics() =>
        RunRecordStore_BatchUpsertHonorsErrorPolicyAndRevisionSemantics();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_EnforcesWritePreconditions() =>
        RunRecordStore_EnforcesWritePreconditions();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_EnforcesConcurrentWritePreconditions() =>
        RunRecordStore_EnforcesConcurrentWritePreconditions();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_DeletesRecordsIdempotently() =>
        RunRecordStore_DeletesRecordsIdempotently();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_DeletesCollectionsIdempotently() =>
        RunRecordStore_DeletesCollectionsIdempotently();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_RejectsNonPortableCollectionPolicyShape() =>
        RunRecordStore_RejectsNonPortableCollectionPolicyShape();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_RejectsNonPortableIdentities() =>
        RunRecordStore_RejectsNonPortableIdentities();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_QueriesByPortableMetadataFilter() =>
        RunRecordStore_QueriesByPortableMetadataFilter();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_QueriesPortableLogicalNullAndOrderingPredicates() =>
        RunRecordStore_QueriesPortableLogicalNullAndOrderingPredicates();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_RejectsNonScalarFilterValues() =>
        RunRecordStore_RejectsNonScalarFilterValues();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_RejectsInvalidRecordVectors() =>
        RunRecordStore_RejectsInvalidRecordVectors();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_PaginatesQueriesWithContinuationToken() =>
        RunRecordStore_PaginatesQueriesWithContinuationToken();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_QueryConvenienceHonorsBoundedAndUnboundedPaging() =>
        RunRecordStore_QueryConvenienceHonorsBoundedAndUnboundedPaging();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_RejectsInvalidQueryLimit() =>
        RunRecordStore_RejectsInvalidQueryLimit();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_RejectsInvalidSearchLimitsAndVectorTop() =>
        RunRecordStore_RejectsInvalidSearchLimitsAndVectorTop();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_FiltersWithPortableStringPredicates() =>
        RunRecordStore_FiltersWithPortableStringPredicates();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_SearchesVectorsWithFilters() =>
        RunRecordStore_SearchesVectorsWithFilters();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_PaginatesVectorSearchWithContinuationToken() =>
        RunRecordStore_PaginatesVectorSearchWithContinuationToken();

    [GoogleAlloyDbLiveFact]
    public Task RecordStore_VectorSearchConvenienceHonorsBoundedAndUnboundedPaging() =>
        RunRecordStore_VectorSearchConvenienceHonorsBoundedAndUnboundedPaging();
}

public class GoogleObjectStoreConformanceTests : ObjectStoreConformanceTests
{
    protected override IObjectStore CreateObjectStore()
    {
        var bucket = GoogleLiveSettings.GcsBucket!;
        var storageClient = StorageClient.Create(GoogleLiveSettings.CreateLiveCredential());
        var keyPrefix = GoogleLiveSettings.UniquePrefix("vyral-obj");
        return new ScopedCloudStorageObjectStore(new CloudStorageObjectStore(storageClient), storageClient, bucket, keyPrefix);
    }

    [GoogleGcsLiveFact]
    public Task ObjectStore_RoundTripsContentMetadataAndEtag() =>
        RunObjectStore_RoundTripsContentMetadataAndEtag();

    [GoogleGcsLiveFact]
    public Task ObjectStore_EnforcesWritePreconditions() =>
        RunObjectStore_EnforcesWritePreconditions();

    [GoogleGcsLiveFact]
    public Task ObjectStore_RejectsNonPortableMetadataKeys() =>
        RunObjectStore_RejectsNonPortableMetadataKeys();

    [GoogleGcsLiveFact]
    public Task ObjectStore_RejectsNonPortableNames() =>
        RunObjectStore_RejectsNonPortableNames();

    [GoogleGcsLiveFact]
    public Task ObjectStore_DeletesObjectsIdempotentlyAndEnforcesPreconditions() =>
        RunObjectStore_DeletesObjectsIdempotentlyAndEnforcesPreconditions();

    [GoogleGcsLiveFact]
    public Task ObjectStore_ListsWithContinuationToken() =>
        RunObjectStore_ListsWithContinuationToken();

    [GoogleGcsLiveFact]
    public Task ObjectStore_RejectsInvalidListLimit() =>
        RunObjectStore_RejectsInvalidListLimit();
}

// ---------------------------------------------------------------------------
// Scoped wrappers
// ---------------------------------------------------------------------------

internal sealed class ScopedAlloyDbRecordCollectionStore : IRecordCollectionStore, IAsyncDisposable
{
    private readonly AlloyDbRecordCollectionStore _inner;
    private readonly string _prefix;
    private readonly HashSet<string> _created = new(StringComparer.Ordinal);

    public ScopedAlloyDbRecordCollectionStore(AlloyDbRecordCollectionStore inner, string prefix)
    {
        _inner = inner;
        _prefix = prefix;
    }

    public async Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default)
    {
        var mapped = Clone(policy);
        mapped.Name = Map(policy.Name);
        _created.Add(mapped.Name);
        await _inner.CreateCollectionAsync(mapped, ct);
    }

    public async Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default)
    {
        var all = await _inner.GetCollectionsAsync(ct);
        return all
            .Where(n => n.StartsWith(_prefix + "-", StringComparison.Ordinal))
            .Select(Unmap)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default)
    {
        var policy = await _inner.GetCollectionPolicyAsync(Map(collection), ct);
        if (policy == null) return null;
        policy.Name = collection;
        return policy;
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        var mapped = Map(collection);
        await _inner.DeleteCollectionAsync(mapped, ct);
        _created.Remove(mapped);
    }

    public Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default) =>
        _inner.UpsertRecordAsync(Map(collection), record, ct);

    public Task UpsertRecordAsync(string collection, VyralRecord record, RecordWritePrecondition? precondition, CancellationToken ct = default) =>
        _inner.UpsertRecordAsync(Map(collection), record, precondition, ct);

    public Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default) =>
        _inner.UpsertRecordsAsync(Map(collection), request, ct);

    public Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) =>
        _inner.GetRecordAsync(Map(collection), partitionKey, id, ct);

    public Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) =>
        _inner.DeleteRecordAsync(Map(collection), partitionKey, id, ct);

    public Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) =>
        _inner.QueryRecordsPageAsync(Map(collection), query, ct);

    public Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) =>
        _inner.SearchRecordsPageAsync(Map(collection), query, ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var collection in _created.ToList())
        {
            try { await _inner.DeleteCollectionAsync(collection); }
            catch { /* best-effort */ }
        }
    }

    private string Map(string collection) => $"{_prefix}-{collection}";
    private string Unmap(string name) => name[(_prefix.Length + 1)..];

    private static RecordCollectionPolicy Clone(RecordCollectionPolicy p) =>
        new()
        {
            Name = p.Name,
            PartitionKeyPath = p.PartitionKeyPath,
            IndexedMetadata = p.IndexedMetadata.ToList(),
            VectorPolicies = p.VectorPolicies.Select(v => new VectorFieldPolicy
            {
                Name = v.Name,
                Path = v.Path,
                Dimensions = v.Dimensions,
                Datatype = v.Datatype,
                DistanceFunction = v.DistanceFunction,
                IndexType = v.IndexType
            }).ToList()
        };
}

/// <summary>
/// Routes all requests to a single pre-existing GCS bucket, scoped by a
/// unique key prefix so tests don't interfere with each other.
/// Deletes all prefixed objects on dispose.
/// </summary>
internal sealed class ScopedCloudStorageObjectStore : IObjectStore, IAsyncDisposable
{
    private readonly CloudStorageObjectStore _inner;
    private readonly StorageClient _client;
    private readonly string _bucket;
    private readonly string _keyPrefix;

    public ScopedCloudStorageObjectStore(
        CloudStorageObjectStore inner,
        StorageClient client,
        string bucket,
        string keyPrefix)
    {
        _inner = inner;
        _client = client;
        _bucket = bucket;
        _keyPrefix = keyPrefix;
    }

    public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
    {
        ValidateLogicalName(request.Container, request.Key);
        var result = await _inner.PutObjectAsync(MapWrite(request), ct);
        result.Container = request.Container;
        result.Key = ObjectNameValidator.NormalizeObjectKey(request.Key);
        return result;
    }

    public async Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default)
    {
        ValidateLogicalName(request.Container, request.Key);
        var result = await _inner.GetObjectAsync(
            new ObjectReadRequest { Container = _bucket, Key = ScopeKey(request.Key) }, ct);
        if (result != null)
        {
            result.Container = request.Container;
            result.Key = ObjectNameValidator.NormalizeObjectKey(request.Key);
        }
        return result;
    }

    public Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default)
    {
        ValidateLogicalName(request.Container, request.Key);
        return _inner.DeleteObjectAsync(new ObjectDeleteRequest
        {
            Container = _bucket,
            Key = ScopeKey(request.Key),
            IfMatch = request.IfMatch
        }, ct);
    }

    public async Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var logicalPrefix = string.IsNullOrEmpty(request.Prefix)
            ? null
            : ObjectNameValidator.NormalizeObjectKey(request.Prefix, allowTrailingSlash: true);
        var scopePrefix = _keyPrefix + "/";
        var prefix = string.IsNullOrEmpty(request.Prefix)
            ? scopePrefix
            : scopePrefix + logicalPrefix;
        var result = await _inner.ListObjectsAsync(new ObjectListRequest
        {
            Container = _bucket,
            Prefix = prefix,
            Limit = request.Limit,
            ContinuationToken = request.ContinuationToken
        }, ct);
        foreach (var item in result.Items)
        {
            if (!item.Key.StartsWith(scopePrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Google Cloud Storage returned an object outside the live-test scope.");
            }

            item.Container = request.Container;
            item.Key = item.Key[scopePrefix.Length..];
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        // Delete all objects under the key prefix
        try
        {
            await foreach (var obj in _client.ListObjectsAsync(_bucket, _keyPrefix + "/"))
            {
                try { await _client.DeleteObjectAsync(_bucket, obj.Name); }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }

    private string ScopeKey(string key) => $"{_keyPrefix}/{ObjectNameValidator.NormalizeObjectKey(key)}";

    private static void ValidateLogicalName(string container, string key)
    {
        ObjectNameValidator.ValidateContainer(container);
        ObjectNameValidator.NormalizeObjectKey(key);
    }

    private ObjectWriteRequest MapWrite(ObjectWriteRequest r) =>
        new()
        {
            Container = _bucket,
            Key = ScopeKey(r.Key),
            Content = r.Content,
            ContentType = r.ContentType,
            Metadata = r.Metadata,
            IfMatch = r.IfMatch,
            IfNoneMatch = r.IfNoneMatch
        };
}
