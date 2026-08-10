using Vyral.Abstractions.Interfaces;
using Vyral.Local;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Local;

public class LocalRecordCollectionStoreConformanceTests : RecordCollectionStoreConformanceTests
{
    [Fact]
    public Task RecordStore_RoundTripsCollectionPolicyAndListsDeterministically() =>
        RunRecordStore_RoundTripsCollectionPolicyAndListsDeterministically();

    [Fact]
    public Task RecordStore_AllowsIdempotentCollectionCreateAndRejectsPolicyChange() =>
        RunRecordStore_AllowsIdempotentCollectionCreateAndRejectsPolicyChange();

    [Fact]
    public Task RecordStore_RoundTripsRecordsWithRevisionAndEtag() =>
        RunRecordStore_RoundTripsRecordsWithRevisionAndEtag();

    [Fact]
    public Task RecordStore_IncrementsRevisionAndPreservesCreatedAtOnUpdate() =>
        RunRecordStore_IncrementsRevisionAndPreservesCreatedAtOnUpdate();

    [Fact]
    public Task RecordStore_BatchUpsertHonorsErrorPolicyAndRevisionSemantics() =>
        RunRecordStore_BatchUpsertHonorsErrorPolicyAndRevisionSemantics();

    [Fact]
    public Task RecordStore_EnforcesWritePreconditions() =>
        RunRecordStore_EnforcesWritePreconditions();

    [Fact]
    public Task RecordStore_DeletesRecordsIdempotently() =>
        RunRecordStore_DeletesRecordsIdempotently();

    [Fact]
    public Task RecordStore_DeletesCollectionsIdempotently() =>
        RunRecordStore_DeletesCollectionsIdempotently();

    [Fact]
    public Task RecordStore_RejectsNonPortableCollectionPolicyShape() =>
        RunRecordStore_RejectsNonPortableCollectionPolicyShape();

    [Fact]
    public Task RecordStore_RejectsNonPortableIdentities() =>
        RunRecordStore_RejectsNonPortableIdentities();

    [Fact]
    public Task RecordStore_QueriesByPortableMetadataFilter() =>
        RunRecordStore_QueriesByPortableMetadataFilter();

    [Fact]
    public Task RecordStore_QueriesPortableLogicalNullAndOrderingPredicates() =>
        RunRecordStore_QueriesPortableLogicalNullAndOrderingPredicates();

    [Fact]
    public Task RecordStore_RejectsNonScalarFilterValues() =>
        RunRecordStore_RejectsNonScalarFilterValues();

    [Fact]
    public Task RecordStore_RejectsInvalidRecordVectors() =>
        RunRecordStore_RejectsInvalidRecordVectors();

    [Fact]
    public Task RecordStore_PaginatesQueriesWithContinuationToken() =>
        RunRecordStore_PaginatesQueriesWithContinuationToken();

    [Fact]
    public Task RecordStore_QueryConvenienceHonorsBoundedAndUnboundedPaging() =>
        RunRecordStore_QueryConvenienceHonorsBoundedAndUnboundedPaging();

    [Fact]
    public Task RecordStore_RejectsInvalidQueryLimit() =>
        RunRecordStore_RejectsInvalidQueryLimit();

    [Fact]
    public Task RecordStore_RejectsInvalidSearchLimitsAndVectorTop() =>
        RunRecordStore_RejectsInvalidSearchLimitsAndVectorTop();

    [Fact]
    public Task RecordStore_FiltersWithPortableStringPredicates() =>
        RunRecordStore_FiltersWithPortableStringPredicates();

    [Fact]
    public Task RecordStore_SearchesVectorsWithFilters() =>
        RunRecordStore_SearchesVectorsWithFilters();

    [Fact]
    public Task RecordStore_PaginatesVectorSearchWithContinuationToken() =>
        RunRecordStore_PaginatesVectorSearchWithContinuationToken();

    [Fact]
    public Task RecordStore_VectorSearchConvenienceHonorsBoundedAndUnboundedPaging() =>
        RunRecordStore_VectorSearchConvenienceHonorsBoundedAndUnboundedPaging();

    protected override async Task<IRecordCollectionStore> CreateStoreAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-conformance-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        return store;
    }
}

public class LocalObjectStoreConformanceTests : ObjectStoreConformanceTests
{
    [Fact]
    public Task ObjectStore_RoundTripsContentMetadataAndEtag() =>
        RunObjectStore_RoundTripsContentMetadataAndEtag();

    [Fact]
    public Task ObjectStore_EnforcesWritePreconditions() =>
        RunObjectStore_EnforcesWritePreconditions();

    [Fact]
    public Task ObjectStore_RejectsNonPortableMetadataKeys() =>
        RunObjectStore_RejectsNonPortableMetadataKeys();

    [Fact]
    public Task ObjectStore_RejectsNonPortableNames() =>
        RunObjectStore_RejectsNonPortableNames();

    [Fact]
    public Task ObjectStore_DeletesObjectsIdempotentlyAndEnforcesPreconditions() =>
        RunObjectStore_DeletesObjectsIdempotentlyAndEnforcesPreconditions();

    [Fact]
    public Task ObjectStore_ListsWithContinuationToken() =>
        RunObjectStore_ListsWithContinuationToken();

    [Fact]
    public Task ObjectStore_RejectsInvalidListLimit() =>
        RunObjectStore_RejectsInvalidListLimit();

    protected override IObjectStore CreateObjectStore()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"vyral-objects-conformance-{Guid.NewGuid():N}");
        return new FileObjectStore(rootPath);
    }
}
