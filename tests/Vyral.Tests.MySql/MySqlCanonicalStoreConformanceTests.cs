using MySqlConnector;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.MySql;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.MySql;

/// <summary>Runs the executable CanonicalStore profile against an isolated MySQL 8 database.</summary>
public sealed class MySqlCanonicalStoreConformanceTests : CanonicalStoreConformanceTests, IAsyncLifetime
{
    private string? _database;
    private string? _connectionString;

    [MySqlLiveFact] public Task CanonicalStore_CommitsDocumentFenceAndOutboxAtomically() => RunCanonicalStore_CommitsDocumentFenceAndOutboxAtomically();
    [MySqlLiveFact] public Task CanonicalStore_ReplaysIdempotentCommitAndRejectsDifferentRequest() => RunCanonicalStore_ReplaysIdempotentCommitAndRejectsDifferentRequest();
    [MySqlLiveFact] public Task CanonicalStore_ConcurrentlyReplaysTheSameIdempotentCommit() => RunCanonicalStore_ConcurrentlyReplaysTheSameIdempotentCommit();
    [MySqlLiveFact] public Task CanonicalStore_EnforcesConditionalWritesAndFenceAtomicity() => RunCanonicalStore_EnforcesConditionalWritesAndFenceAtomicity();
    [MySqlLiveFact] public Task CanonicalStore_RetainsRevisionsAndTombstones() => RunCanonicalStore_RetainsRevisionsAndTombstones();
    [MySqlLiveFact] public Task CanonicalStore_LeasesAcknowledgesAndReleasesOutbox() => RunCanonicalStore_LeasesAcknowledgesAndReleasesOutbox();
    [MySqlLiveFact] public Task CanonicalStore_ParksAndReplaysDeadLetteredOutbox() => RunCanonicalStore_ParksAndReplaysDeadLetteredOutbox();
    [MySqlLiveFact] public Task CanonicalStore_PreservesHashVerifiedActiveLeaseSnapshot() => RunCanonicalStore_PreservesHashVerifiedActiveLeaseSnapshot();
    [MySqlLiveFact] public Task CanonicalStore_RoundTripsHashVerifiedChunkedTenantArchive() => RunCanonicalStore_RoundTripsHashVerifiedChunkedTenantArchive();
    [MySqlLiveFact] public Task CanonicalStore_DataPlanePreflightRestoresIsolatesAndCleansUp() => RunCanonicalStore_DataPlanePreflightRestoresIsolatesAndCleansUp();
    [MySqlLiveFact] public Task CanonicalStore_CanonicalizesEquivalentIdempotentRequests() => RunCanonicalStore_CanonicalizesEquivalentIdempotentRequests();
    [MySqlLiveFact] public Task CanonicalStore_QueriesProjectedRangeAndStableOrder() => RunCanonicalStore_QueriesProjectedRangeAndStableOrder();
    [MySqlLiveFact] public Task CanonicalStore_MigrationsAndTenantSnapshotAreDurable() => RunCanonicalStore_MigrationsAndTenantSnapshotAreDurable();
    [MySqlLiveFact] public Task CanonicalStore_IsolatesTenants() => RunCanonicalStore_IsolatesTenants();

    [MySqlLiveFact]
    public async Task CanonicalStore_RestoreSerializesWithTenantWrites()
    {
        var store = await CreateStoreAsync();
        await store.CommitAsync(new CanonicalTransactionRequest
        {
            TenantId = "tenant-a", IdempotencyKey = "seed",
            Mutations = [new CanonicalMutation { Document = new CanonicalDocument { TenantId = "tenant-a", DocumentType = "entity", Id = "seed", SchemaVersion = "v1", Data = new System.Text.Json.Nodes.JsonObject { ["value"] = "seed" } } }]
        });
        var snapshot = await store.ExportTenantAsync("tenant-a");
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT state_json FROM vyral_mysql_canonical_tenants WHERE tenant_id = 'tenant-a' FOR UPDATE";
            await command.ExecuteScalarAsync();
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
        if (string.IsNullOrWhiteSpace(_database) || string.IsNullOrWhiteSpace(_connectionString)) return;
        var builder = new MySqlConnectionStringBuilder(_connectionString) { Database = string.Empty };
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS `{_database}`;";
        await command.ExecuteNonQueryAsync();
    }

    protected override async Task<ICanonicalStore> CreateStoreAsync()
    {
        var source = MySqlLiveSettings.ConnectionString ?? throw new InvalidOperationException("VYRAL_MYSQL_CONNECTION_STRING is required for MySQL CanonicalStore conformance.");
        _database = "vyral_canonical_" + Guid.NewGuid().ToString("N")[..16];
        var admin = new MySqlConnectionStringBuilder(source) { Database = string.Empty };
        await using (var connection = new MySqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;";
            await command.ExecuteNonQueryAsync();
        }
        _connectionString = new MySqlConnectionStringBuilder(source) { Database = _database }.ConnectionString;
        return new MySqlCanonicalStore(_connectionString);
    }
}
