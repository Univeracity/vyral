using System.Text.Json.Nodes;
using MySqlConnector;
using Npgsql;
using Vyral.Abstractions.Models;
using Vyral.CanonicalProjectionStarter;
using Vyral.MySql;
using Vyral.Pgvector;

namespace Vyral.Tests.MySql;

public sealed class CanonicalCutoverFactAttribute : FactAttribute
{
    public CanonicalCutoverFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_MYSQL_CONNECTION_STRING")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_PGVECTOR_CONNECTION_STRING")))
        {
            Skip = "Set VYRAL_MYSQL_CONNECTION_STRING and VYRAL_PGVECTOR_CONNECTION_STRING to run the CanonicalStore cutover gate.";
        }
    }
}

public sealed class MySqlToPostgresCanonicalCutoverTests
{
    [CanonicalCutoverFact]
    public async Task Cutover_MigratesArchiveAndRebuildsProjectionWithPortableCorrectness()
    {
        var mysqlSource = Environment.GetEnvironmentVariable("VYRAL_MYSQL_CONNECTION_STRING")!;
        var postgresSource = Environment.GetEnvironmentVariable("VYRAL_PGVECTOR_CONNECTION_STRING")!;
        var mysqlDatabase = "vyral_cutover_" + Guid.NewGuid().ToString("N")[..16];
        var postgresSchema = "vyral_cutover_" + Guid.NewGuid().ToString("N")[..16];

        try
        {
            var mysql = await CreateMySqlStoreAsync(mysqlSource, mysqlDatabase);
            var postgres = await CreatePostgresStoreAsync(postgresSource, postgresSchema);
            var migration = new CanonicalMigration
            {
                Namespace = "projection-starter",
                Id = "20260728.customer-v1",
                Checksum = "sha256:projection-starter-customer-v1",
                Description = "Customer aggregate and relational projection v1"
            };
            await mysql.ApplyMigrationsAsync([migration]);
            var seed = CustomerUpsert("cutover:create", "Ada", 1);
            var committed = await mysql.CommitAsync(seed);
            var sourceLease = Assert.Single(await mysql.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest
            {
                TenantId = "tenant-cutover",
                ConsumerId = "source-projector",
                LeaseSeconds = 60
            }));
            Assert.Equal(Assert.Single(committed.Outbox).Id, sourceLease.Event.Id);

            var archive = await mysql.ExportTenantArchiveAsync("tenant-cutover", chunkBytes: 128);
            Assert.True(archive.Chunks.Count > 1);
            Assert.Equal(
                archive.SnapshotContentHash,
                CanonicalTenantArchiveCodec.Read(new CanonicalArchiveRestoreRequest
                {
                    Archive = archive,
                    ExpectedContentHash = archive.ContentHash
                }).ContentHash);

            await postgres.ApplyMigrationsAsync([migration]);
            await postgres.RestoreTenantArchiveAsync(new CanonicalArchiveRestoreRequest
            {
                Archive = archive,
                ExpectedContentHash = archive.ContentHash
            });

            var restored = await postgres.ExportTenantAsync("tenant-cutover");
            Assert.Single(restored.Documents);
            Assert.Single(restored.Fences);
            Assert.Single(restored.Transactions);
            Assert.Single(restored.Outbox);
            Assert.Null(restored.Outbox[0].LeaseOwner);
            Assert.Null(restored.Outbox[0].LeaseExpiresAtUtc);
            Assert.True((await postgres.CommitAsync(seed)).Replayed);
            Assert.Single(await postgres.ListMigrationsAsync(), item =>
                item.Namespace == migration.Namespace && item.Id == migration.Id && item.Checksum == migration.Checksum);

            var conflictingFence = CustomerUpsert("cutover:fence-conflict", "Grace", 1, "customer-2");
            await Assert.ThrowsAsync<InvalidOperationException>(() => postgres.CommitAsync(conflictingFence));
            Assert.Null(await postgres.GetDocumentAsync("tenant-cutover", "customer", "customer-2"));

            var projection = new SqliteCanonicalProjection(new SqliteCanonicalProjectionOptions
            {
                DatabasePath = Path.Combine(Path.GetTempPath(), $"vyral-cutover-projection-{Guid.NewGuid():N}.sqlite")
            });
            var rebuild = await projection.RebuildAsync(postgres, "tenant-cutover");
            Assert.Equal(restored.ContentHash, rebuild.SnapshotContentHash);
            Assert.Equal(rebuild.SnapshotContentHash, await projection.GetRebuildFenceAsync("tenant-cutover"));
            Assert.Equal(1, (await projection.PumpOnceAsync(postgres, "tenant-cutover")).Duplicate);

            await postgres.CommitAsync(CustomerUpsert("cutover:update", "Ada Lovelace", 2));
            var pump = await projection.PumpOnceAsync(postgres, "tenant-cutover");
            Assert.Equal(1, pump.Applied);
            var projected = Assert.IsType<CanonicalProjectionDocument>(
                await projection.GetAsync("tenant-cutover", "customer", "customer-1"));
            Assert.Equal(2, projected.Revision);
            Assert.Equal("Ada Lovelace", projected.Data!["name"]!.GetValue<string>());
            Assert.Empty((await postgres.QueryOutboxAsync(new CanonicalOutboxQuery
            {
                TenantId = "tenant-cutover",
                State = CanonicalOutboxStates.Ready
            })).Items);
        }
        finally
        {
            try
            {
                await DropPostgresSchemaAsync(postgresSource, postgresSchema);
            }
            finally
            {
                await DropMySqlDatabaseAsync(mysqlSource, mysqlDatabase);
            }
        }
    }

    private static async Task<MySqlCanonicalStore> CreateMySqlStoreAsync(string source, string database)
    {
        var admin = new MySqlConnectionStringBuilder(source) { Database = string.Empty };
        await using var connection = new MySqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;";
        await command.ExecuteNonQueryAsync();
        return new MySqlCanonicalStore(new MySqlConnectionStringBuilder(source) { Database = database }.ConnectionString);
    }

    private static async Task<PostgresCanonicalStore> CreatePostgresStoreAsync(string source, string schema)
    {
        await using var connection = new NpgsqlConnection(source);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA {schema};";
        await command.ExecuteNonQueryAsync();
        return new PostgresCanonicalStore(new NpgsqlConnectionStringBuilder(source) { SearchPath = schema }.ConnectionString);
    }

    private static async Task DropMySqlDatabaseAsync(string source, string database)
    {
        var admin = new MySqlConnectionStringBuilder(source) { Database = string.Empty };
        await using var connection = new MySqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS `{database}`;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropPostgresSchemaAsync(string source, string schema)
    {
        await using var connection = new NpgsqlConnection(source);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE;";
        await command.ExecuteNonQueryAsync();
    }

    private static CanonicalTransactionRequest CustomerUpsert(
        string idempotencyKey,
        string name,
        long revision,
        string id = "customer-1") =>
        new()
        {
            TenantId = "tenant-cutover",
            IdempotencyKey = idempotencyKey,
            Mutations =
            [
                new CanonicalMutation
                {
                    Document = new CanonicalDocument
                    {
                        TenantId = "tenant-cutover",
                        DocumentType = "customer",
                        Id = id,
                        SchemaVersion = "v1",
                        Data = new JsonObject { ["name"] = name, ["email"] = "ada@example.test" },
                        Indexes = new Dictionary<string, string> { ["name"] = name }
                    },
                    Precondition = revision > 1
                        ? new CanonicalWritePrecondition { ExpectedRevision = revision - 1, MustExist = true }
                        : null
                }
            ],
            Fences = revision == 1
                ?
                [
                    new CanonicalFenceMutation
                    {
                        Name = "customer-email",
                        Value = "ada@example.test",
                        OwnerDocumentType = "customer",
                        OwnerDocumentId = id
                    }
                ]
                : [],
            Outbox =
            [
                new CanonicalOutboxWrite
                {
                    Topic = "canonical.document.changed",
                    Key = id,
                    Payload = new JsonObject
                    {
                        ["documentType"] = "customer",
                        ["documentId"] = id,
                        ["revision"] = revision
                    }
                }
            ]
        };
}
