using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public class SqliteMigrationManager
{
    private readonly string _connectionString;

    public SqliteMigrationManager(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var transaction = connection.BeginTransaction();

        try
        {
            await ExecuteCommandAsync(connection, @"
                CREATE TABLE IF NOT EXISTS vyral_migrations (
                    id TEXT PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE TABLE IF NOT EXISTS vyral_collections (
                    name TEXT PRIMARY KEY,
                    policy_json TEXT NOT NULL
                );", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE TABLE IF NOT EXISTS vyral_records (
                    collection TEXT NOT NULL,
                    partitionKey TEXT NOT NULL,
                    id TEXT NOT NULL,
                    content_json TEXT NOT NULL,
                    etag TEXT,
                    revision INTEGER,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (collection, partitionKey, id),
                    FOREIGN KEY (collection) REFERENCES vyral_collections(name)
                );", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE TABLE IF NOT EXISTS vyral_record_vectors (
                    collection TEXT NOT NULL,
                    partitionKey TEXT NOT NULL,
                    record_id TEXT NOT NULL,
                    vector_name TEXT NOT NULL,
                    vector_data BLOB NOT NULL,
                    dimensions INTEGER NOT NULL,
                    PRIMARY KEY (collection, partitionKey, record_id, vector_name),
                    FOREIGN KEY (collection, partitionKey, record_id) REFERENCES vyral_records(collection, partitionKey, id) ON DELETE CASCADE
                );", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE TABLE IF NOT EXISTS vyral_record_metadata_index (
                    collection TEXT NOT NULL,
                    partitionKey TEXT NOT NULL,
                    record_id TEXT NOT NULL,
                    path TEXT NOT NULL,
                    value_text TEXT,
                    value_number REAL,
                    value_bool INTEGER,
                    value_json TEXT NOT NULL,
                    PRIMARY KEY (collection, partitionKey, record_id, path),
                    FOREIGN KEY (collection, partitionKey, record_id) REFERENCES vyral_records(collection, partitionKey, id) ON DELETE CASCADE
                );", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE INDEX IF NOT EXISTS idx_vyral_record_metadata_text
                ON vyral_record_metadata_index(collection, path, value_text);", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE INDEX IF NOT EXISTS idx_vyral_record_metadata_number
                ON vyral_record_metadata_index(collection, path, value_number);", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE VIRTUAL TABLE IF NOT EXISTS vyral_record_fts
                USING fts5(
                    collection UNINDEXED,
                    partitionKey UNINDEXED,
                    record_id UNINDEXED,
                    text,
                    tokenize='unicode61'
                );", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                INSERT INTO vyral_record_fts (collection, partitionKey, record_id, text)
                SELECT r.collection, r.partitionKey, r.id, r.content_json
                FROM vyral_records r
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM vyral_record_fts f
                    WHERE f.collection = r.collection
                        AND f.partitionKey = r.partitionKey
                        AND f.record_id = r.id
                );", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE TABLE IF NOT EXISTS vyral_traces (
                    id TEXT PRIMARY KEY,
                    operation TEXT NOT NULL,
                    adapter TEXT,
                    request_json TEXT NOT NULL,
                    result_summary_json TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    duration_ms REAL NOT NULL,
                    created_at TEXT NOT NULL
                );", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE INDEX IF NOT EXISTS idx_vyral_traces_operation_created
                ON vyral_traces(operation, created_at DESC);", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE TABLE IF NOT EXISTS vyral_provider_run_jobs (
                    id TEXT PRIMARY KEY,
                    provider TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    completed_at TEXT,
                    job_json TEXT NOT NULL
                );", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE INDEX IF NOT EXISTS idx_vyral_provider_run_jobs_provider_created
                ON vyral_provider_run_jobs(provider, created_at DESC);", transaction, ct);

            await ExecuteCommandAsync(connection, @"
                CREATE INDEX IF NOT EXISTS idx_vyral_provider_run_jobs_status_created
                ON vyral_provider_run_jobs(status, created_at DESC);", transaction, ct);

            await ExecuteCommandAsync(connection, $@"
                INSERT OR IGNORE INTO vyral_migrations (id, applied_at)
                VALUES ('schema:1', '{DateTime.UtcNow:O}');", transaction, ct);

            await ExecuteCommandAsync(connection, $@"
                INSERT OR IGNORE INTO vyral_migrations (id, applied_at)
                VALUES ('schema:2', '{DateTime.UtcNow:O}');", transaction, ct);

            await ExecuteCommandAsync(connection, $@"
                INSERT OR IGNORE INTO vyral_migrations (id, applied_at)
                VALUES ('schema:3', '{DateTime.UtcNow:O}');", transaction, ct);

            await ExecuteCommandAsync(connection, $@"
                INSERT OR IGNORE INTO vyral_migrations (id, applied_at)
                VALUES ('schema:4', '{DateTime.UtcNow:O}');", transaction, ct);

            await ApplyFtsAtomicValueBoundaryMigrationAsync(connection, transaction, ct);

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task ExecuteCommandAsync(SqliteConnection connection, string commandText, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ApplyFtsAtomicValueBoundaryMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        const string migrationId = "fts:atomic-json-values:1";
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT 1 FROM vyral_migrations WHERE id = $id LIMIT 1;";
            check.Parameters.AddWithValue("$id", migrationId);
            if (await check.ExecuteScalarAsync(ct) is not null)
            {
                return;
            }
        }

        var records = new List<(string Collection, VyralRecord Record)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT collection, content_json FROM vyral_records ORDER BY collection, partitionKey, id;";
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                records.Add((
                    reader.GetString(0),
                    JsonSerializer.Deserialize<VyralRecord>(reader.GetString(1))
                        ?? throw new InvalidOperationException("Stored Vyral record could not be deserialized while rebuilding FTS.")));
            }
        }

        await ExecuteCommandAsync(connection, "DELETE FROM vyral_record_fts;", transaction, ct);
        foreach (var (collection, record) in records)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
                INSERT INTO vyral_record_fts (collection, partitionKey, record_id, text)
                VALUES ($collection, $partitionKey, $record_id, $text);";
            insert.Parameters.AddWithValue("$collection", collection);
            insert.Parameters.AddWithValue("$partitionKey", record.PartitionKey);
            insert.Parameters.AddWithValue("$record_id", record.Id);
            insert.Parameters.AddWithValue("$text", SqliteRecordCollectionStore.BuildLexicalIndexText(record));
            await insert.ExecuteNonQueryAsync(ct);
        }

        using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = "INSERT INTO vyral_migrations (id, applied_at) VALUES ($id, $appliedAt);";
        mark.Parameters.AddWithValue("$id", migrationId);
        mark.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("O"));
        await mark.ExecuteNonQueryAsync(ct);
    }
}
