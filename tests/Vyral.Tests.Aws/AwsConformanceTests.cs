using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Aws;

public class AwsRecordStoreConformanceTests : RecordCollectionStoreConformanceTests
{
    [AwsDynamoDbLiveFact]
    public Task RecordStore_RoundTripsCollectionPolicyAndListsDeterministically() =>
        RunRecordStore_RoundTripsCollectionPolicyAndListsDeterministically();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_AllowsIdempotentCollectionCreateAndRejectsPolicyChange() =>
        RunRecordStore_AllowsIdempotentCollectionCreateAndRejectsPolicyChange();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_RoundTripsRecordsWithRevisionAndEtag() =>
        RunRecordStore_RoundTripsRecordsWithRevisionAndEtag();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_IncrementsRevisionAndPreservesCreatedAtOnUpdate() =>
        RunRecordStore_IncrementsRevisionAndPreservesCreatedAtOnUpdate();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_BatchUpsertHonorsErrorPolicyAndRevisionSemantics() =>
        RunRecordStore_BatchUpsertHonorsErrorPolicyAndRevisionSemantics();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_DeletesRecordsIdempotently() =>
        RunRecordStore_DeletesRecordsIdempotently();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_DeletesCollectionsIdempotently() =>
        RunRecordStore_DeletesCollectionsIdempotently();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_RejectsNonPortableCollectionPolicyShape() =>
        RunRecordStore_RejectsNonPortableCollectionPolicyShape();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_RejectsNonPortableIdentities() =>
        RunRecordStore_RejectsNonPortableIdentities();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_QueriesByPortableMetadataFilter() =>
        RunRecordStore_QueriesByPortableMetadataFilter();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_QueriesPortableLogicalNullAndOrderingPredicates() =>
        RunRecordStore_QueriesPortableLogicalNullAndOrderingPredicates();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_RejectsNonScalarFilterValues() =>
        RunRecordStore_RejectsNonScalarFilterValues();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_RejectsInvalidRecordVectors() =>
        RunRecordStore_RejectsInvalidRecordVectors();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_PaginatesQueriesWithContinuationToken() =>
        RunRecordStore_PaginatesQueriesWithContinuationToken();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_QueryConvenienceHonorsBoundedAndUnboundedPaging() =>
        RunRecordStore_QueryConvenienceHonorsBoundedAndUnboundedPaging();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_RejectsInvalidQueryLimit() =>
        RunRecordStore_RejectsInvalidQueryLimit();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_RejectsInvalidSearchLimitsAndVectorTop() =>
        RunRecordStore_RejectsInvalidSearchLimitsAndVectorTop();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_FiltersWithPortableStringPredicates() =>
        RunRecordStore_FiltersWithPortableStringPredicates();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_SearchesVectorsWithFilters() =>
        RunRecordStore_SearchesVectorsWithFilters();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_PaginatesVectorSearchWithContinuationToken() =>
        RunRecordStore_PaginatesVectorSearchWithContinuationToken();

    [AwsDynamoDbLiveFact]
    public Task RecordStore_VectorSearchConvenienceHonorsBoundedAndUnboundedPaging() =>
        RunRecordStore_VectorSearchConvenienceHonorsBoundedAndUnboundedPaging();

    protected override Task<IRecordCollectionStore> CreateStoreAsync()
    {
        var tablePrefix = AwsLiveSettings.DynamoDbTablePrefix!;
        var uniquePrefix = AwsLiveSettings.UniquePrefix();
        var catalogTableName = $"{tablePrefix}-{uniquePrefix}-catalog";
        var collectionTablePrefix = $"{tablePrefix}-{uniquePrefix}-";

        var client = new AmazonDynamoDBClient();
        IRecordCollectionStore store = new ScopedDynamoDbRecordCollectionStore(
            client, catalogTableName, collectionTablePrefix);

        return Task.FromResult(store);
    }
}

public class AwsObjectStoreConformanceTests : ObjectStoreConformanceTests
{
    [AwsS3LiveFact]
    public Task ObjectStore_RoundTripsContentMetadataAndEtag() =>
        RunObjectStore_RoundTripsContentMetadataAndEtag();

    [AwsS3LiveFact]
    public Task ObjectStore_EnforcesWritePreconditions() =>
        RunObjectStore_EnforcesWritePreconditions();

    [AwsS3LiveFact]
    public Task ObjectStore_RejectsNonPortableMetadataKeys() =>
        RunObjectStore_RejectsNonPortableMetadataKeys();

    [AwsS3LiveFact]
    public Task ObjectStore_RejectsNonPortableNames() =>
        RunObjectStore_RejectsNonPortableNames();

    [AwsS3LiveFact]
    public Task ObjectStore_DeletesObjectsIdempotentlyAndEnforcesPreconditions() =>
        RunObjectStore_DeletesObjectsIdempotentlyAndEnforcesPreconditions();

    [AwsS3LiveFact]
    public Task ObjectStore_ListsWithContinuationToken() =>
        RunObjectStore_ListsWithContinuationToken();

    [AwsS3LiveFact]
    public Task ObjectStore_RejectsInvalidListLimit() =>
        RunObjectStore_RejectsInvalidListLimit();

    protected override IObjectStore CreateObjectStore()
    {
        var bucket = AwsLiveSettings.S3Bucket!;
        var keyPrefix = AwsLiveSettings.UniquePrefix("vyral-obj");
        var client = new AmazonS3Client();
        return new ScopedS3ObjectStore(new S3ObjectStore(client), client, bucket, keyPrefix);
    }
}

// ---------------------------------------------------------------------------
// Scoped wrappers
// ---------------------------------------------------------------------------

/// <summary>
/// Wraps DynamoDbRecordCollectionStore with a unique catalog + table prefix per test run.
/// On dispose, deletes all catalog and collection tables created during the test.
/// </summary>
internal sealed class ScopedDynamoDbRecordCollectionStore : IRecordCollectionStore, IAsyncDisposable
{
    private readonly IAmazonDynamoDB _client;
    private readonly DynamoDbRecordCollectionStore _inner;
    private readonly string _catalogTableName;
    private readonly Dictionary<string, string> _collectionTables = new(StringComparer.Ordinal);

    public ScopedDynamoDbRecordCollectionStore(
        IAmazonDynamoDB client,
        string catalogTableName,
        string collectionTablePrefix)
    {
        _client = client;
        _catalogTableName = catalogTableName;
        _inner = new DynamoDbRecordCollectionStore(client, catalogTableName, collectionTablePrefix);
        CollectionTablePrefix = collectionTablePrefix;
    }

    private string CollectionTablePrefix { get; }

    public async Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default)
    {
        await _inner.CreateCollectionAsync(policy, ct);
        _collectionTables[policy.Name] = CollectionTablePrefix + policy.Name;
    }

    public Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default) =>
        _inner.GetCollectionsAsync(ct);

    public Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default) =>
        _inner.GetCollectionPolicyAsync(collection, ct);

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        await _inner.DeleteCollectionAsync(collection, ct);
    }

    public Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default) =>
        _inner.UpsertRecordAsync(collection, record, ct);

    public Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default) =>
        _inner.UpsertRecordsAsync(collection, request, ct);

    public Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) =>
        _inner.GetRecordAsync(collection, partitionKey, id, ct);

    public Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) =>
        _inner.DeleteRecordAsync(collection, partitionKey, id, ct);

    public Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) =>
        _inner.QueryRecordsPageAsync(collection, query, ct);

    public Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) =>
        _inner.SearchRecordsPageAsync(collection, query, ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var collection in _collectionTables.Keys.ToList())
        {
            try { await _inner.DeleteCollectionAsync(collection); }
            catch { /* best-effort */ }
        }

        foreach (var tableName in _collectionTables.Values)
        {
            await DeleteTableBestEffortAsync(tableName);
        }

        await DeleteTableBestEffortAsync(_catalogTableName);
    }

    private async Task DeleteTableBestEffortAsync(string tableName)
    {
        try
        {
            await _client.DeleteTableAsync(new DeleteTableRequest { TableName = tableName });
        }
        catch (ResourceNotFoundException)
        {
            // The adapter may not have reached the table-creation point.
        }
        catch (ResourceInUseException)
        {
            // A prior cleanup request is already deleting this unique test table.
        }
        catch
        {
            // Test cleanup must not conceal the original conformance failure.
        }
    }
}

/// <summary>
/// Routes all requests to a pre-existing S3 bucket, scoped by a unique key prefix.
/// Deletes all objects under the prefix on dispose.
/// </summary>
internal sealed class ScopedS3ObjectStore : IObjectStore, IAsyncDisposable
{
    private readonly S3ObjectStore _inner;
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _keyPrefix;

    public ScopedS3ObjectStore(S3ObjectStore inner, IAmazonS3 client, string bucket, string keyPrefix)
    {
        _inner = inner;
        _client = client;
        _bucket = bucket;
        _keyPrefix = keyPrefix;
    }

    public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);
        var result = await _inner.PutObjectAsync(MapWrite(request, key), ct);
        result.Container = request.Container;
        result.Key = request.Key;
        return result;
    }

    public async Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);
        var result = await _inner.GetObjectAsync(
            new ObjectReadRequest { Container = _bucket, Key = ScopeKey(key) }, ct);
        if (result != null)
        {
            result.Container = request.Container;
            result.Key = request.Key;
        }

        return result;
    }

    public Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);
        return _inner.DeleteObjectAsync(new ObjectDeleteRequest
        {
            Container = _bucket,
            Key = ScopeKey(key),
            IfMatch = request.IfMatch
        }, ct);
    }

    public async Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default)
    {
        if (request.Limit <= 0 && request.Limit.HasValue)
            throw new InvalidOperationException("Object list limit must be greater than zero.");

        ObjectNameValidator.ValidateContainer(request.Container);
        var logicalPrefix = string.IsNullOrEmpty(request.Prefix)
            ? null
            : ObjectNameValidator.NormalizeObjectKey(request.Prefix, allowTrailingSlash: true);
        var scopePrefix = _keyPrefix + "/";
        var prefix = logicalPrefix is null ? scopePrefix : scopePrefix + logicalPrefix;

        var result = await _inner.ListObjectsAsync(new ObjectListRequest
        {
            Container = _bucket,
            Prefix = prefix,
            Limit = request.Limit,
            ContinuationToken = request.ContinuationToken
        }, ct);

        foreach (var item in result.Items)
        {
            item.Container = request.Container;
            item.Key = item.Key[scopePrefix.Length..];
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // List and delete all objects under the prefix
            string? continuationToken = null;
            do
            {
                var listResponse = await _client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    Prefix = _keyPrefix + "/",
                    ContinuationToken = continuationToken
                });

                foreach (var obj in listResponse.S3Objects)
                {
                    try
                    {
                        await _client.DeleteObjectAsync(new DeleteObjectRequest
                        {
                            BucketName = _bucket,
                            Key = obj.Key
                        });
                    }
                    catch { /* best-effort */ }
                }

                continuationToken = listResponse.IsTruncated == true
                    ? listResponse.NextContinuationToken
                    : null;
            }
            while (continuationToken != null);
        }
        catch { /* best-effort */ }
    }

    private string ScopeKey(string key) => $"{_keyPrefix}/{key.TrimStart('/')}";

    private ObjectWriteRequest MapWrite(ObjectWriteRequest r, string key) =>
        new()
        {
            Container = _bucket,
            Key = ScopeKey(key),
            Content = r.Content,
            ContentType = r.ContentType,
            Metadata = r.Metadata,
            IfMatch = r.IfMatch,
            IfNoneMatch = r.IfNoneMatch
        };
}
