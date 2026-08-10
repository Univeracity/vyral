using Npgsql;
using Vyral.Abstractions.Interfaces;
using Vyral.Pgvector;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Pgvector;

/// <summary>
/// Runs the same strong-storage contract against PostgreSQL. It is opt-in because callers choose
/// the database through VYRAL_PGVECTOR_CONNECTION_STRING; each test gets an isolated schema.
/// </summary>
public sealed class PostgresCanonicalStoreConformanceTests : CanonicalStoreConformanceTests, IAsyncLifetime
{
    private string? _schema;
    private string? _connectionString;

    [PgvectorLiveFact]
    public Task CanonicalStore_CommitsDocumentFenceAndOutboxAtomically() => RunCanonicalStore_CommitsDocumentFenceAndOutboxAtomically();

    [PgvectorLiveFact]
    public Task CanonicalStore_ReplaysIdempotentCommitAndRejectsDifferentRequest() => RunCanonicalStore_ReplaysIdempotentCommitAndRejectsDifferentRequest();

    [PgvectorLiveFact]
    public Task CanonicalStore_ConcurrentlyReplaysTheSameIdempotentCommit() => RunCanonicalStore_ConcurrentlyReplaysTheSameIdempotentCommit();

    [PgvectorLiveFact]
    public Task CanonicalStore_EnforcesConditionalWritesAndFenceAtomicity() => RunCanonicalStore_EnforcesConditionalWritesAndFenceAtomicity();

    [PgvectorLiveFact]
    public Task CanonicalStore_RetainsRevisionsAndTombstones() => RunCanonicalStore_RetainsRevisionsAndTombstones();

    [PgvectorLiveFact]
    public Task CanonicalStore_LeasesAcknowledgesAndReleasesOutbox() => RunCanonicalStore_LeasesAcknowledgesAndReleasesOutbox();

    [PgvectorLiveFact]
    public Task CanonicalStore_ParksAndReplaysDeadLetteredOutbox() => RunCanonicalStore_ParksAndReplaysDeadLetteredOutbox();

    [PgvectorLiveFact]
    public Task CanonicalStore_PreservesHashVerifiedActiveLeaseSnapshot() => RunCanonicalStore_PreservesHashVerifiedActiveLeaseSnapshot();

    [PgvectorLiveFact]
    public Task CanonicalStore_RoundTripsHashVerifiedChunkedTenantArchive() => RunCanonicalStore_RoundTripsHashVerifiedChunkedTenantArchive();

    [PgvectorLiveFact]
    public Task CanonicalStore_DataPlanePreflightRestoresIsolatesAndCleansUp() => RunCanonicalStore_DataPlanePreflightRestoresIsolatesAndCleansUp();

    [PgvectorLiveFact]
    public Task CanonicalStore_CanonicalizesEquivalentIdempotentRequests() => RunCanonicalStore_CanonicalizesEquivalentIdempotentRequests();

    [PgvectorLiveFact]
    public Task CanonicalStore_QueriesProjectedRangeAndStableOrder() => RunCanonicalStore_QueriesProjectedRangeAndStableOrder();

    [PgvectorLiveFact]
    public Task CanonicalStore_MigrationsAndTenantSnapshotAreDurable() => RunCanonicalStore_MigrationsAndTenantSnapshotAreDurable();

    [PgvectorLiveFact]
    public Task CanonicalStore_IsolatesTenants() => RunCanonicalStore_IsolatesTenants();

    [PgvectorLiveFact]
    public async Task CanonicalStore_RestoreSerializesWithTenantWrites()
    {
        var store = await CreateStoreAsync();
        var snapshot = await store.ExportTenantAsync("tenant-a");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended('tenant-a', 827364823));";
            await command.ExecuteNonQueryAsync();
        }

        var restore = store.RestoreTenantAsync(new CanonicalRestoreRequest { Snapshot = snapshot, ExpectedContentHash = snapshot.ContentHash });
        await Task.Delay(150);
        Assert.False(restore.IsCompleted);
        await transaction.CommitAsync();
        await restore;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_schema) || string.IsNullOrWhiteSpace(_connectionString)) return;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {_schema} CASCADE;";
        await command.ExecuteNonQueryAsync();
    }

    protected override async Task<ICanonicalStore> CreateStoreAsync()
    {
        var connectionString = PgvectorLiveSettings.ConnectionString
            ?? throw new InvalidOperationException("VYRAL_PGVECTOR_CONNECTION_STRING is required for PostgreSQL CanonicalStore conformance.");
        _schema = "vyral_canonical_" + Guid.NewGuid().ToString("N")[..16];
        _connectionString = connectionString;
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA {_schema};";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = _schema
        };
        return new PostgresCanonicalStore(builder.ConnectionString);
    }
}
