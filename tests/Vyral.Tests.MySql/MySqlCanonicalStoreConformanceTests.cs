using System.Text.Json;
using System.Text.Json.Nodes;
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
    [MySqlLiveFact] public Task CanonicalStore_PurgesWithSnapshotFenceLeaseRejectionAndLifecycleGeneration() => RunCanonicalStore_PurgesWithSnapshotFenceLeaseRejectionAndLifecycleGeneration();
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
            TenantId = "tenant-a",
            IdempotencyKey = "seed",
            Mutations = [new CanonicalMutation { Document = new CanonicalDocument { TenantId = "tenant-a", DocumentType = "entity", Id = "seed", SchemaVersion = "v1", Data = new System.Text.Json.Nodes.JsonObject { ["value"] = "seed" } } }]
        });
        var snapshot = await store.ExportTenantAsync("tenant-a");
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT generation FROM vyral_mysql_canonical_tenant_lifecycle WHERE tenant_id = 'tenant-a' FOR UPDATE";
            await command.ExecuteScalarAsync();
        }
        var restore = store.RestoreTenantAsync(new CanonicalRestoreRequest { Snapshot = snapshot, ExpectedContentHash = snapshot.ContentHash });
        await Task.Delay(150);
        Assert.False(restore.IsCompleted);
        await transaction.CommitAsync();
        await restore;
    }

    [MySqlLiveFact]
    public async Task CanonicalStore_OutboxQueryHonorsLimitAcrossPages()
    {
        var store = await CreateStoreAsync();
        for (var index = 0; index < 3; index++)
        {
            await store.CommitAsync(new CanonicalTransactionRequest
            {
                TenantId = "tenant-page",
                IdempotencyKey = $"page-{index}",
                Mutations =
                [
                    new CanonicalMutation
                    {
                        Document = new CanonicalDocument
                        {
                            TenantId = "tenant-page",
                            DocumentType = "page",
                            Id = $"document-{index}",
                            SchemaVersion = "v1",
                            Data = new System.Text.Json.Nodes.JsonObject
                            {
                                ["index"] = index
                            }
                        }
                    }
                ],
                Outbox =
                [
                    new CanonicalOutboxWrite
                    {
                        Topic = "page.changed",
                        Key = $"document-{index}",
                        Payload = new System.Text.Json.Nodes.JsonObject
                        {
                            ["index"] = index
                        }
                    }
                ]
            });
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        for (var pageIndex = 0; pageIndex < 3; pageIndex++)
        {
            var page = await store.QueryOutboxAsync(new CanonicalOutboxQuery
            {
                TenantId = "tenant-page",
                Topic = "page.changed",
                State = CanonicalOutboxStates.Ready,
                Limit = 1,
                ContinuationToken = continuation
            });
            Assert.Single(page.Items);
            Assert.True(ids.Add(page.Items[0].Id));
            continuation = page.ContinuationToken;
            Assert.Equal(pageIndex < 2, continuation is not null);
        }

        Assert.Equal(3, ids.Count);
    }

    [MySqlLiveFact]
    public async Task CanonicalStore_RejectsOversizedLegacyTenantBeforeReturningJson()
    {
        var store = await CreateStoreAsync();
        _ = await store.ListMigrationsAsync();
        await using (var connection = new MySqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO vyral_mysql_canonical_tenants (tenant_id, state_json, updated_at_utc) VALUES ('tenant-oversized-read', JSON_OBJECT('padding', REPEAT('x', 24 * 1024 * 1024)), UTC_TIMESTAMP(6))";
            await command.ExecuteNonQueryAsync();
        }

        var limitedStore = new MySqlCanonicalStore(_connectionString!);
        var exception = await Assert.ThrowsAsync<MySqlCanonicalTenantStateLimitExceededException>(() =>
            limitedStore.GetDocumentAsync("tenant-oversized-read", "entity", "one"));

        Assert.Equal("tenant-oversized-read", exception.TenantId);
        Assert.True(exception.ContentLength > exception.MaxContentLength);
        Assert.Equal(MySqlCanonicalStore.DefaultMaxLegacyTenantStateBytes, exception.MaxContentLength);
    }

    [MySqlLiveFact]
    public async Task CanonicalStore_RejectsOversizedLegacyTenantWithoutPartialImport()
    {
        var store = await CreateStoreAsync();
        _ = await store.ListMigrationsAsync();
        await using (var seedConnection = new MySqlConnection(_connectionString))
        {
            await seedConnection.OpenAsync();
            await using var seedCommand = seedConnection.CreateCommand();
            seedCommand.CommandText = "INSERT INTO vyral_mysql_canonical_tenants (tenant_id, state_json, updated_at_utc) VALUES ('tenant-oversized-write', JSON_OBJECT('padding', REPEAT('x', 8192)), UTC_TIMESTAMP(6))";
            await seedCommand.ExecuteNonQueryAsync();
        }
        var limitedStore = new MySqlCanonicalStore(_connectionString!, maxTenantStateBytes: 2048);
        var exception = await Assert.ThrowsAsync<MySqlCanonicalTenantStateLimitExceededException>(() =>
            limitedStore.CommitAsync(new CanonicalTransactionRequest
            {
                TenantId = "tenant-oversized-write",
                IdempotencyKey = "oversized",
                Mutations =
                [
                    new CanonicalMutation
                    {
                        Document = new CanonicalDocument
                        {
                            TenantId = "tenant-oversized-write",
                            DocumentType = "entity",
                            Id = "one",
                            SchemaVersion = "v1",
                            Data = new JsonObject
                            {
                                ["padding"] = new string('x', 8192)
                            }
                        }
                    }
                ]
            }));

        Assert.Equal("tenant-oversized-write", exception.TenantId);
        Assert.True(exception.ContentLength > exception.MaxContentLength);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM vyral_mysql_canonical_tenants WHERE tenant_id = 'tenant-oversized-write'),
                (SELECT COUNT(*) FROM vyral_mysql_canonical_documents WHERE tenant_id = 'tenant-oversized-write')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    [MySqlLiveFact]
    public async Task CanonicalStore_NoOpReplayAndEmptyLeaseDoNotRewriteNormalizedRows()
    {
        var store = await CreateStoreAsync();
        var request = new CanonicalTransactionRequest
        {
            TenantId = "tenant-no-op",
            IdempotencyKey = "seed",
            Mutations =
            [
                new CanonicalMutation
                {
                    Document = new CanonicalDocument
                    {
                        TenantId = "tenant-no-op",
                        DocumentType = "entity",
                        Id = "one",
                        SchemaVersion = "v1",
                        Data = new JsonObject { ["value"] = 1 }
                    }
                }
            ]
        };
        await store.CommitAsync(request);
        var before = await ReadTenantStateIdentityAsync("tenant-no-op");

        var replay = await store.CommitAsync(request);
        var leases = await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest
        {
            TenantId = "tenant-no-op",
            ConsumerId = "no-op-test",
            MaxItems = 1,
            LeaseSeconds = 30
        });
        var after = await ReadTenantStateIdentityAsync("tenant-no-op");

        Assert.True(replay.Replayed);
        Assert.Empty(leases);
        Assert.Equal(before, after);
    }

    [MySqlLiveFact]
    public async Task CanonicalStore_AtomicallyImportsLegacyTenantStateAndPreservesActiveLease()
    {
        var store = await CreateStoreAsync();
        var request = new CanonicalTransactionRequest
        {
            TenantId = "tenant-legacy",
            IdempotencyKey = "legacy-seed",
            Mutations =
            [
                new CanonicalMutation
                {
                    Document = new CanonicalDocument
                    {
                        TenantId = "tenant-legacy",
                        DocumentType = "entity",
                        Id = "one",
                        SchemaVersion = "v1",
                        Data = new JsonObject { ["value"] = "preserved" }
                    }
                }
            ],
            Outbox =
            [
                new CanonicalOutboxWrite
                {
                    Id = "legacy-event",
                    Topic = "legacy.changed",
                    Key = "one",
                    Payload = new JsonObject { ["value"] = "preserved" }
                }
            ]
        };
        await store.CommitAsync(request);
        var lease = Assert.Single(await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest
        {
            TenantId = "tenant-legacy",
            ConsumerId = "legacy-worker",
            MaxItems = 1,
            LeaseSeconds = 300
        }));
        var snapshot = await store.ExportTenantAsync("tenant-legacy");

        string leaseTokenHash;
        await using (var connection = new MySqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using (var hashCommand = connection.CreateCommand())
            {
                hashCommand.CommandText = "SELECT lease_token_hash FROM vyral_mysql_canonical_outbox WHERE tenant_id = 'tenant-legacy' AND event_id = 'legacy-event'";
                leaseTokenHash = Convert.ToString(await hashCommand.ExecuteScalarAsync())!;
            }
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var table in new[]
                     {
                         "vyral_mysql_canonical_document_indexes", "vyral_mysql_canonical_revisions",
                         "vyral_mysql_canonical_fences", "vyral_mysql_canonical_outbox",
                         "vyral_mysql_canonical_transactions", "vyral_mysql_canonical_documents"
                     })
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table} WHERE tenant_id = 'tenant-legacy'";
                await delete.ExecuteNonQueryAsync();
            }
            var legacyJson = JsonSerializer.Serialize(new
            {
                snapshot.Documents,
                snapshot.Revisions,
                snapshot.Fences,
                snapshot.Outbox,
                snapshot.Transactions,
                LeaseTokenHashes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["legacy-event"] = leaseTokenHash
                }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO vyral_mysql_canonical_tenants (tenant_id, state_json, updated_at_utc) VALUES ('tenant-legacy', @state_json, UTC_TIMESTAMP(6))";
            insert.Parameters.AddWithValue("state_json", legacyJson);
            await insert.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        var imported = await store.GetDocumentAsync("tenant-legacy", "entity", "one");
        Assert.Equal("preserved", imported!.Data!["value"]!.GetValue<string>());
        Assert.True((await store.CommitAsync(request)).Replayed);
        await store.AcknowledgeOutboxAsync("tenant-legacy", "legacy-event", lease.LeaseToken);

        await using var verification = new MySqlConnection(_connectionString);
        await verification.OpenAsync();
        await using var command = verification.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM vyral_mysql_canonical_tenants WHERE tenant_id = 'tenant-legacy'),
                (SELECT COUNT(*) FROM vyral_mysql_canonical_documents WHERE tenant_id = 'tenant-legacy'),
                (SELECT COUNT(*) FROM vyral_mysql_canonical_transactions WHERE tenant_id = 'tenant-legacy')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [MySqlLiveFact]
    public async Task CanonicalStore_RejectsAmbiguousLegacyAndNormalizedTenantState()
    {
        var store = await CreateStoreAsync();
        await store.CommitAsync(new CanonicalTransactionRequest
        {
            TenantId = "tenant-layout-conflict",
            IdempotencyKey = "normalized-seed",
            Mutations =
            [
                new CanonicalMutation
                {
                    Document = new CanonicalDocument
                    {
                        TenantId = "tenant-layout-conflict",
                        DocumentType = "entity",
                        Id = "one",
                        SchemaVersion = "v1",
                        Data = new JsonObject { ["value"] = "normalized" }
                    }
                }
            ]
        });
        await using (var connection = new MySqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO vyral_mysql_canonical_tenants (tenant_id, state_json, updated_at_utc)
                VALUES ('tenant-layout-conflict', JSON_OBJECT(), UTC_TIMESTAMP(6))
                """;
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetDocumentAsync("tenant-layout-conflict", "entity", "one"));

        Assert.Contains("both legacy and normalized state", exception.Message, StringComparison.Ordinal);
        await using var verification = new MySqlConnection(_connectionString);
        await verification.OpenAsync();
        await using var count = verification.CreateCommand();
        count.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM vyral_mysql_canonical_tenants WHERE tenant_id = 'tenant-layout-conflict'),
                (SELECT COUNT(*) FROM vyral_mysql_canonical_documents WHERE tenant_id = 'tenant-layout-conflict')
            """;
        await using var reader = await count.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [MySqlLiveFact]
    public async Task CanonicalStore_LargeTenantConcurrentReadsAndEmptyLeasesStayWithinAllocationBudget()
    {
        var store = await CreateStoreAsync();
        const string tenantId = "tenant-large-normalized";
        const int documentCount = 12;
        const int payloadChars = 700_000;
        for (var index = 0; index < documentCount; index++)
        {
            await store.CommitAsync(new CanonicalTransactionRequest
            {
                TenantId = tenantId,
                IdempotencyKey = $"large-{index:D2}",
                Mutations =
                [
                    new CanonicalMutation
                    {
                        Document = new CanonicalDocument
                        {
                            TenantId = tenantId,
                            DocumentType = "fragment",
                            Id = $"fragment-{index:D2}",
                            SchemaVersion = "v1",
                            Data = new JsonObject { ["content"] = new string((char)('a' + index), payloadChars) }
                        }
                    }
                ]
            });
        }

        await using (var connection = new MySqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COALESCE(SUM(OCTET_LENGTH(document_json)), 0) FROM vyral_mysql_canonical_documents WHERE tenant_id = @tenant_id) +
                    (SELECT COALESCE(SUM(OCTET_LENGTH(revision_json)), 0) FROM vyral_mysql_canonical_revisions WHERE tenant_id = @tenant_id) +
                    (SELECT COALESCE(SUM(OCTET_LENGTH(result_json)), 0) FROM vyral_mysql_canonical_transactions WHERE tenant_id = @tenant_id)
                """;
            command.Parameters.AddWithValue("tenant_id", tenantId);
            var retainedBytes = Convert.ToInt64(await command.ExecuteScalarAsync());
            Assert.InRange(retainedBytes, 20L * 1024 * 1024, 30L * 1024 * 1024);
        }

        _ = await store.GetDocumentAsync(tenantId, "fragment", "fragment-00");
        _ = await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest
        {
            TenantId = tenantId,
            ConsumerId = "large-warmup",
            MaxItems = 1,
            LeaseSeconds = 30
        });
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        await Task.WhenAll(Enumerable.Range(0, documentCount).Select(async index =>
        {
            var document = await store.GetDocumentAsync(tenantId, "fragment", $"fragment-{index:D2}");
            Assert.Equal(payloadChars, document!.Data!["content"]!.GetValue<string>().Length);
            Assert.Empty(await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest
            {
                TenantId = tenantId,
                ConsumerId = $"large-reader-{index:D2}",
                MaxItems = 1,
                LeaseSeconds = 30
            }));
        }));
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        Assert.InRange(allocatedBytes, 1, 192L * 1024 * 1024);
    }

    private async Task<(string Hash, string UpdatedAtUtc)> ReadTenantStateIdentityAsync(string tenantId)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SHA2(document_json, 256), updated_at_utc FROM vyral_mysql_canonical_documents WHERE tenant_id = @tenant_id AND document_type = 'entity' AND document_id = 'one'";
        command.Parameters.AddWithValue("@tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
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
