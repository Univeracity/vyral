using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Azure;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Azure;

public class AzureRecordCollectionStoreConformanceTests : RecordCollectionStoreConformanceTests
{
    [AzureCosmosLiveFact]
    public Task RecordStore_RoundTripsCollectionPolicyAndListsDeterministically() =>
        RunRecordStore_RoundTripsCollectionPolicyAndListsDeterministically();

    [AzureCosmosLiveFact]
    public Task RecordStore_AllowsIdempotentCollectionCreateAndRejectsPolicyChange() =>
        RunRecordStore_AllowsIdempotentCollectionCreateAndRejectsPolicyChange();

    [AzureCosmosLiveFact]
    public Task RecordStore_RoundTripsRecordsWithRevisionAndEtag() =>
        RunRecordStore_RoundTripsRecordsWithRevisionAndEtag();

    [AzureCosmosLiveFact]
    public Task RecordStore_IncrementsRevisionAndPreservesCreatedAtOnUpdate() =>
        RunRecordStore_IncrementsRevisionAndPreservesCreatedAtOnUpdate();

    [AzureCosmosLiveFact]
    public Task RecordStore_BatchUpsertHonorsErrorPolicyAndRevisionSemantics() =>
        RunRecordStore_BatchUpsertHonorsErrorPolicyAndRevisionSemantics();

    [AzureCosmosLiveFact]
    public Task RecordStore_EnforcesWritePreconditions() =>
        RunRecordStore_EnforcesWritePreconditions();

    [AzureCosmosLiveFact]
    public Task RecordStore_EnforcesConcurrentWritePreconditions() =>
        RunRecordStore_EnforcesConcurrentWritePreconditions();

    [AzureCosmosLiveFact]
    public Task RecordStore_DeletesRecordsIdempotently() =>
        RunRecordStore_DeletesRecordsIdempotently();

    [AzureCosmosLiveFact]
    public Task RecordStore_DeletesCollectionsIdempotently() =>
        RunRecordStore_DeletesCollectionsIdempotently();

    [AzureCosmosLiveFact]
    public Task RecordStore_RejectsNonPortableCollectionPolicyShape() =>
        RunRecordStore_RejectsNonPortableCollectionPolicyShape();

    [AzureCosmosLiveFact]
    public Task RecordStore_RejectsNonPortableIdentities() =>
        RunRecordStore_RejectsNonPortableIdentities();

    [AzureCosmosLiveFact]
    public Task RecordStore_QueriesByPortableMetadataFilter() =>
        RunRecordStore_QueriesByPortableMetadataFilter();

    [AzureCosmosLiveFact]
    public Task RecordStore_QueriesPortableLogicalNullAndOrderingPredicates() =>
        RunRecordStore_QueriesPortableLogicalNullAndOrderingPredicates();

    [AzureCosmosLiveFact]
    public Task RecordStore_RejectsNonScalarFilterValues() =>
        RunRecordStore_RejectsNonScalarFilterValues();

    [AzureCosmosLiveFact]
    public Task RecordStore_RejectsInvalidRecordVectors() =>
        RunRecordStore_RejectsInvalidRecordVectors();

    [AzureCosmosLiveFact]
    public Task RecordStore_PaginatesQueriesWithContinuationToken() =>
        RunRecordStore_PaginatesQueriesWithContinuationToken();

    [AzureCosmosLiveFact]
    public Task RecordStore_QueryConvenienceHonorsBoundedAndUnboundedPaging() =>
        RunRecordStore_QueryConvenienceHonorsBoundedAndUnboundedPaging();

    [AzureCosmosLiveFact]
    public Task RecordStore_RejectsInvalidQueryLimit() =>
        RunRecordStore_RejectsInvalidQueryLimit();

    [AzureCosmosLiveFact]
    public Task RecordStore_RejectsInvalidSearchLimitsAndVectorTop() =>
        RunRecordStore_RejectsInvalidSearchLimitsAndVectorTop();

    [AzureCosmosLiveFact]
    public Task RecordStore_FiltersWithPortableStringPredicates() =>
        RunRecordStore_FiltersWithPortableStringPredicates();

    [AzureCosmosLiveFact]
    public Task RecordStore_SearchesVectorsWithFilters() =>
        RunRecordStore_SearchesVectorsWithFilters();

    [AzureCosmosLiveFact]
    public Task RecordStore_PaginatesVectorSearchWithContinuationToken() =>
        RunRecordStore_PaginatesVectorSearchWithContinuationToken();

    [AzureCosmosLiveFact]
    public Task RecordStore_VectorSearchConvenienceHonorsBoundedAndUnboundedPaging() =>
        RunRecordStore_VectorSearchConvenienceHonorsBoundedAndUnboundedPaging();

    protected override async Task<IRecordCollectionStore> CreateStoreAsync()
    {
        var settings = AzureLiveSettings.Cosmos();
        var client = new CosmosClient(settings.ConnectionString);
        await client.CreateDatabaseIfNotExistsAsync(settings.DatabaseId);
        var prefix = AzureLiveSettings.UniqueContainerName(settings.ContainerPrefix);
        return new ScopedCosmosRecordCollectionStore(client, settings.DatabaseId, prefix);
    }
}

public class AzureObjectStoreConformanceTests : ObjectStoreConformanceTests
{
    [AzureBlobLiveFact]
    public Task ObjectStore_RoundTripsContentMetadataAndEtag() =>
        RunObjectStore_RoundTripsContentMetadataAndEtag();

    [AzureBlobLiveFact]
    public Task ObjectStore_EnforcesWritePreconditions() =>
        RunObjectStore_EnforcesWritePreconditions();

    [AzureBlobLiveFact]
    public Task ObjectStore_RejectsNonPortableMetadataKeys() =>
        RunObjectStore_RejectsNonPortableMetadataKeys();

    [AzureBlobLiveFact]
    public Task ObjectStore_RejectsNonPortableNames() =>
        RunObjectStore_RejectsNonPortableNames();

    [AzureBlobLiveFact]
    public Task ObjectStore_DeletesObjectsIdempotentlyAndEnforcesPreconditions() =>
        RunObjectStore_DeletesObjectsIdempotentlyAndEnforcesPreconditions();

    [AzureBlobLiveFact]
    public Task ObjectStore_ListsWithContinuationToken() =>
        RunObjectStore_ListsWithContinuationToken();

    [AzureBlobLiveFact]
    public Task ObjectStore_RejectsInvalidListLimit() =>
        RunObjectStore_RejectsInvalidListLimit();

    protected override IObjectStore CreateObjectStore()
    {
        var settings = AzureLiveSettings.Blob();
        var serviceClient = new BlobServiceClient(settings.ConnectionString);
        var containerName = AzureLiveSettings.UniqueContainerName(settings.ContainerPrefix);
        return new ScopedAzureBlobObjectStore(serviceClient, containerName);
    }
}

internal sealed class ScopedCosmosRecordCollectionStore : IRecordCollectionStore, IAsyncDisposable
{
    private readonly CosmosClient _client;
    private readonly string _databaseId;
    private readonly string _prefix;
    private readonly CosmosRecordCollectionStore _inner;
    private readonly HashSet<string> _createdCollections = new(StringComparer.Ordinal);

    public ScopedCosmosRecordCollectionStore(CosmosClient client, string databaseId, string prefix)
    {
        _client = client;
        _databaseId = databaseId;
        _prefix = prefix;
        _inner = new CosmosRecordCollectionStore(client, databaseId);
    }

    public async Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default)
    {
        var mapped = ClonePolicy(policy);
        mapped.Name = Map(policy.Name);
        await _inner.CreateCollectionAsync(mapped, ct);
        _createdCollections.Add(mapped.Name);
    }

    public async Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default)
    {
        var physicalNames = await _inner.GetCollectionsAsync(ct);
        return physicalNames
            .Where(name => name.StartsWith(_prefix + "-", StringComparison.Ordinal))
            .Select(Unmap)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default)
    {
        var policy = await _inner.GetCollectionPolicyAsync(Map(collection), ct);
        if (policy == null) return null;

        policy.Name = collection;
        return policy;
    }

    public Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default) =>
        _inner.UpsertRecordAsync(Map(collection), record, ct);

    public Task UpsertRecordAsync(
        string collection,
        VyralRecord record,
        RecordWritePrecondition? precondition,
        CancellationToken ct = default) =>
        _inner.UpsertRecordAsync(Map(collection), record, precondition, ct);

    public Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default) =>
        _inner.UpsertRecordsAsync(Map(collection), request, ct);

    public Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) =>
        _inner.GetRecordAsync(Map(collection), partitionKey, id, ct);

    public Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) =>
        _inner.DeleteRecordAsync(Map(collection), partitionKey, id, ct);

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        var mapped = Map(collection);
        await _inner.DeleteCollectionAsync(mapped, ct);
        _createdCollections.Remove(mapped);
    }

    public Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) =>
        _inner.QueryRecordsPageAsync(Map(collection), query, ct);

    public Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) =>
        _inner.SearchRecordsPageAsync(Map(collection), query, ct);

    public async ValueTask DisposeAsync()
    {
        var database = _client.GetDatabase(_databaseId);
        foreach (var collection in _createdCollections)
        {
            try
            {
                await database.GetContainer(collection).DeleteContainerAsync();
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }
        }

        _client.Dispose();
    }

    private string Map(string collection)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        return $"{_prefix}-{collection}";
    }

    private string Unmap(string collection) => collection[(_prefix.Length + 1)..];

    private static RecordCollectionPolicy ClonePolicy(RecordCollectionPolicy policy)
    {
        return new RecordCollectionPolicy
        {
            Name = policy.Name,
            PartitionKeyPath = policy.PartitionKeyPath,
            IndexedMetadata = policy.IndexedMetadata.ToList(),
            VectorPolicies = policy.VectorPolicies.Select(vectorPolicy => new VectorFieldPolicy
            {
                Name = vectorPolicy.Name,
                Path = vectorPolicy.Path,
                Dimensions = vectorPolicy.Dimensions,
                Datatype = vectorPolicy.Datatype,
                DistanceFunction = vectorPolicy.DistanceFunction,
                IndexType = vectorPolicy.IndexType
            }).ToList()
        };
    }
}

internal sealed class ScopedAzureBlobObjectStore : IObjectStore, IAsyncDisposable
{
    private readonly BlobServiceClient _serviceClient;
    private readonly string _containerName;
    private readonly AzureBlobObjectStore _inner;

    public ScopedAzureBlobObjectStore(BlobServiceClient serviceClient, string containerName)
    {
        _serviceClient = serviceClient;
        _containerName = containerName;
        _inner = new AzureBlobObjectStore(serviceClient);
        _serviceClient.GetBlobContainerClient(_containerName).CreateIfNotExists();
    }

    public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        _ = ObjectNameValidator.NormalizeObjectKey(request.Key);
        var result = await _inner.PutObjectAsync(Map(request), ct);
        result.Container = request.Container;
        return result;
    }

    public async Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        _ = ObjectNameValidator.NormalizeObjectKey(request.Key);
        var result = await _inner.GetObjectAsync(new ObjectReadRequest { Container = _containerName, Key = request.Key }, ct);
        if (result == null) return null;

        result.Container = request.Container;
        return result;
    }

    public Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        _ = ObjectNameValidator.NormalizeObjectKey(request.Key);
        return _inner.DeleteObjectAsync(new ObjectDeleteRequest
        {
            Container = _containerName,
            Key = request.Key,
            IfMatch = request.IfMatch
        }, ct);
    }

    public async Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        if (!string.IsNullOrEmpty(request.Prefix))
        {
            _ = ObjectNameValidator.NormalizeObjectKey(request.Prefix, allowTrailingSlash: true);
        }
        var result = await _inner.ListObjectsAsync(new ObjectListRequest
        {
            Container = _containerName,
            Prefix = request.Prefix,
            Limit = request.Limit,
            ContinuationToken = request.ContinuationToken
        }, ct);
        foreach (var item in result.Items)
        {
            item.Container = request.Container;
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _serviceClient.GetBlobContainerClient(_containerName).DeleteIfExistsAsync();
    }

    private ObjectWriteRequest Map(ObjectWriteRequest request)
    {
        return new ObjectWriteRequest
        {
            Container = _containerName,
            Key = request.Key,
            Content = request.Content,
            ContentType = request.ContentType,
            Metadata = request.Metadata,
            IfMatch = request.IfMatch,
            IfNoneMatch = request.IfNoneMatch
        };
    }
}
