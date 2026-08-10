using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Pgvector;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Pgvector;

public class PgvectorRecordStoreConformanceTests : RecordCollectionStoreConformanceTests
{
    protected override async Task<IRecordCollectionStore> CreateStoreAsync()
    {
        var cs = PgvectorLiveSettings.ConnectionString!;
        var inner = new PgvectorRecordCollectionStore(cs);
        await inner.InitializeAsync();
        var prefix = PgvectorLiveSettings.UniquePrefix();
        return new ScopedPgvectorRecordCollectionStore(inner, cs, prefix);
    }

    [PgvectorLiveFact]
    public Task RecordStore_RoundTripsCollectionPolicyAndListsDeterministically() =>
        RunRecordStore_RoundTripsCollectionPolicyAndListsDeterministically();

    [PgvectorLiveFact]
    public Task RecordStore_AllowsIdempotentCollectionCreateAndRejectsPolicyChange() =>
        RunRecordStore_AllowsIdempotentCollectionCreateAndRejectsPolicyChange();

    [PgvectorLiveFact]
    public Task RecordStore_RoundTripsRecordsWithRevisionAndEtag() =>
        RunRecordStore_RoundTripsRecordsWithRevisionAndEtag();

    [PgvectorLiveFact]
    public Task RecordStore_IncrementsRevisionAndPreservesCreatedAtOnUpdate() =>
        RunRecordStore_IncrementsRevisionAndPreservesCreatedAtOnUpdate();

    [PgvectorLiveFact]
    public Task RecordStore_BatchUpsertHonorsErrorPolicyAndRevisionSemantics() =>
        RunRecordStore_BatchUpsertHonorsErrorPolicyAndRevisionSemantics();

    [PgvectorLiveFact]
    public Task RecordStore_EnforcesWritePreconditions() =>
        RunRecordStore_EnforcesWritePreconditions();

    [PgvectorLiveFact]
    public Task RecordStore_EnforcesConcurrentWritePreconditions() =>
        RunRecordStore_EnforcesConcurrentWritePreconditions();

    [PgvectorLiveFact]
    public Task RecordStore_DeletesRecordsIdempotently() =>
        RunRecordStore_DeletesRecordsIdempotently();

    [PgvectorLiveFact]
    public Task RecordStore_DeletesCollectionsIdempotently() =>
        RunRecordStore_DeletesCollectionsIdempotently();

    [PgvectorLiveFact]
    public Task RecordStore_RejectsNonPortableCollectionPolicyShape() =>
        RunRecordStore_RejectsNonPortableCollectionPolicyShape();

    [PgvectorLiveFact]
    public Task RecordStore_RejectsNonPortableIdentities() =>
        RunRecordStore_RejectsNonPortableIdentities();

    [PgvectorLiveFact]
    public Task RecordStore_QueriesByPortableMetadataFilter() =>
        RunRecordStore_QueriesByPortableMetadataFilter();

    [PgvectorLiveFact]
    public Task RecordStore_QueriesPortableLogicalNullAndOrderingPredicates() =>
        RunRecordStore_QueriesPortableLogicalNullAndOrderingPredicates();

    [PgvectorLiveFact]
    public Task RecordStore_RejectsNonScalarFilterValues() =>
        RunRecordStore_RejectsNonScalarFilterValues();

    [PgvectorLiveFact]
    public Task RecordStore_RejectsInvalidRecordVectors() =>
        RunRecordStore_RejectsInvalidRecordVectors();

    [PgvectorLiveFact]
    public Task RecordStore_PaginatesQueriesWithContinuationToken() =>
        RunRecordStore_PaginatesQueriesWithContinuationToken();

    [PgvectorLiveFact]
    public Task RecordStore_QueryConvenienceHonorsBoundedAndUnboundedPaging() =>
        RunRecordStore_QueryConvenienceHonorsBoundedAndUnboundedPaging();

    [PgvectorLiveFact]
    public Task RecordStore_RejectsInvalidQueryLimit() =>
        RunRecordStore_RejectsInvalidQueryLimit();

    [PgvectorLiveFact]
    public Task RecordStore_RejectsInvalidSearchLimitsAndVectorTop() =>
        RunRecordStore_RejectsInvalidSearchLimitsAndVectorTop();

    [PgvectorLiveFact]
    public Task RecordStore_FiltersWithPortableStringPredicates() =>
        RunRecordStore_FiltersWithPortableStringPredicates();

    [PgvectorLiveFact]
    public Task RecordStore_SearchesVectorsWithFilters() =>
        RunRecordStore_SearchesVectorsWithFilters();

    [PgvectorLiveFact]
    public Task RecordStore_PaginatesVectorSearchWithContinuationToken() =>
        RunRecordStore_PaginatesVectorSearchWithContinuationToken();

    [PgvectorLiveFact]
    public Task RecordStore_VectorSearchConvenienceHonorsBoundedAndUnboundedPaging() =>
        RunRecordStore_VectorSearchConvenienceHonorsBoundedAndUnboundedPaging();
}

public class PgvectorObjectStoreConformanceTests : ObjectStoreConformanceTests
{
    protected override IObjectStore CreateObjectStore()
    {
        var cs = PgvectorLiveSettings.ConnectionString!;
        var inner = new PgvectorObjectStore(cs);
        var container = PgvectorLiveSettings.UniquePrefix("vyral-obj");
        return new ScopedPgvectorObjectStore(inner, cs, container);
    }

    [PgvectorLiveFact]
    public Task ObjectStore_RoundTripsContentMetadataAndEtag() =>
        RunObjectStore_RoundTripsContentMetadataAndEtag();

    [PgvectorLiveFact]
    public Task ObjectStore_EnforcesWritePreconditions() =>
        RunObjectStore_EnforcesWritePreconditions();

    [PgvectorLiveFact]
    public Task ObjectStore_RejectsNonPortableMetadataKeys() =>
        RunObjectStore_RejectsNonPortableMetadataKeys();

    [PgvectorLiveFact]
    public Task ObjectStore_RejectsNonPortableNames() =>
        RunObjectStore_RejectsNonPortableNames();

    [PgvectorLiveFact]
    public Task ObjectStore_DeletesObjectsIdempotentlyAndEnforcesPreconditions() =>
        RunObjectStore_DeletesObjectsIdempotentlyAndEnforcesPreconditions();

    [PgvectorLiveFact]
    public Task ObjectStore_ListsWithContinuationToken() =>
        RunObjectStore_ListsWithContinuationToken();

    [PgvectorLiveFact]
    public Task ObjectStore_RejectsInvalidListLimit() =>
        RunObjectStore_RejectsInvalidListLimit();
}

// ---------------------------------------------------------------------------
// Scoped wrappers — isolate each test run with a unique collection prefix /
// container name, then clean up on dispose.
// ---------------------------------------------------------------------------

internal sealed class ScopedPgvectorRecordCollectionStore : IRecordCollectionStore, IAsyncDisposable
{
    private readonly PgvectorRecordCollectionStore _inner;
    private readonly string _connectionString;
    private readonly string _prefix;
    private readonly HashSet<string> _created = new(StringComparer.Ordinal);

    public ScopedPgvectorRecordCollectionStore(
        PgvectorRecordCollectionStore inner,
        string connectionString,
        string prefix)
    {
        _inner = inner;
        _connectionString = connectionString;
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

internal sealed class ScopedPgvectorObjectStore : IObjectStore, IAsyncDisposable
{
    private readonly PgvectorObjectStore _inner;
    private readonly string _connectionString;
    private readonly string _physicalContainer;

    public ScopedPgvectorObjectStore(PgvectorObjectStore inner, string connectionString, string physicalContainer)
    {
        _inner = inner;
        _connectionString = connectionString;
        _physicalContainer = physicalContainer;
    }

    public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var result = await _inner.PutObjectAsync(Map(request), ct);
        result.Container = request.Container;
        return result;
    }

    public async Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default)
    {
        var result = await _inner.GetObjectAsync(
            new ObjectReadRequest { Container = _physicalContainer, Key = request.Key }, ct);
        if (result != null) result.Container = request.Container;
        return result;
    }

    public Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default) =>
        _inner.DeleteObjectAsync(new ObjectDeleteRequest
        {
            Container = _physicalContainer,
            Key = request.Key,
            IfMatch = request.IfMatch
        }, ct);

    public async Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default)
    {
        var result = await _inner.ListObjectsAsync(new ObjectListRequest
        {
            Container = _physicalContainer,
            Prefix = request.Prefix,
            Limit = request.Limit,
            ContinuationToken = request.ContinuationToken
        }, ct);
        foreach (var item in result.Items) item.Container = request.Container;
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        // Delete all objects in the physical container
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM vyral_objects WHERE container = $1";
            cmd.Parameters.AddWithValue(_physicalContainer);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort */ }
    }

    private ObjectWriteRequest Map(ObjectWriteRequest r) =>
        new()
        {
            Container = _physicalContainer,
            Key = r.Key,
            Content = r.Content,
            ContentType = r.ContentType,
            Metadata = r.Metadata,
            IfMatch = r.IfMatch,
            IfNoneMatch = r.IfNoneMatch
        };
}
