using Vyral.Abstractions.Interfaces;
using Vyral.Local;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Local;

public sealed class LocalCanonicalStoreConformanceTests : CanonicalStoreConformanceTests
{
    [Fact]
    public Task CanonicalStore_CommitsDocumentFenceAndOutboxAtomically() => RunCanonicalStore_CommitsDocumentFenceAndOutboxAtomically();

    [Fact]
    public Task CanonicalStore_ReplaysIdempotentCommitAndRejectsDifferentRequest() => RunCanonicalStore_ReplaysIdempotentCommitAndRejectsDifferentRequest();

    [Fact]
    public Task CanonicalStore_ConcurrentlyReplaysTheSameIdempotentCommit() => RunCanonicalStore_ConcurrentlyReplaysTheSameIdempotentCommit();

    [Fact]
    public Task CanonicalStore_EnforcesConditionalWritesAndFenceAtomicity() => RunCanonicalStore_EnforcesConditionalWritesAndFenceAtomicity();

    [Fact]
    public Task CanonicalStore_RetainsRevisionsAndTombstones() => RunCanonicalStore_RetainsRevisionsAndTombstones();

    [Fact]
    public Task CanonicalStore_LeasesAcknowledgesAndReleasesOutbox() => RunCanonicalStore_LeasesAcknowledgesAndReleasesOutbox();

    [Fact]
    public Task CanonicalStore_ParksAndReplaysDeadLetteredOutbox() => RunCanonicalStore_ParksAndReplaysDeadLetteredOutbox();

    [Fact]
    public Task CanonicalStore_PreservesHashVerifiedActiveLeaseSnapshot() => RunCanonicalStore_PreservesHashVerifiedActiveLeaseSnapshot();

    [Fact]
    public Task CanonicalStore_RoundTripsHashVerifiedChunkedTenantArchive() => RunCanonicalStore_RoundTripsHashVerifiedChunkedTenantArchive();

    [Fact]
    public Task CanonicalStore_DataPlanePreflightRestoresIsolatesAndCleansUp() => RunCanonicalStore_DataPlanePreflightRestoresIsolatesAndCleansUp();

    [Fact]
    public Task CanonicalStore_CanonicalizesEquivalentIdempotentRequests() => RunCanonicalStore_CanonicalizesEquivalentIdempotentRequests();

    [Fact]
    public Task CanonicalStore_QueriesProjectedRangeAndStableOrder() => RunCanonicalStore_QueriesProjectedRangeAndStableOrder();

    [Fact]
    public Task CanonicalStore_MigrationsAndTenantSnapshotAreDurable() => RunCanonicalStore_MigrationsAndTenantSnapshotAreDurable();

    [Fact]
    public Task CanonicalStore_IsolatesTenants() => RunCanonicalStore_IsolatesTenants();

    protected override Task<ICanonicalStore> CreateStoreAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-canonical-{Guid.NewGuid():N}.sqlite");
        return Task.FromResult<ICanonicalStore>(new SqliteCanonicalStore(path));
    }
}
