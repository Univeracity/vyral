using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Vyral.Pgvector;

public class PgvectorMigrationManager
{
    private readonly Func<CancellationToken, Task<NpgsqlConnection>> _open;

    public PgvectorMigrationManager(string connectionString)
    {
        _open = async ct =>
        {
            var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            return conn;
        };
    }

    public PgvectorMigrationManager(NpgsqlDataSource dataSource)
    {
        _open = async ct => await dataSource.OpenConnectionAsync(ct);
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var connection = await _open(ct);

        // Enable pgvector extension
        await ExecuteAsync(connection, "CREATE EXTENSION IF NOT EXISTS vector;", ct);

        // Migrations table
        await ExecuteAsync(connection, @"
            CREATE TABLE IF NOT EXISTS vyral_migrations (
                id TEXT PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );", ct);

        // Collections (stores policy JSON)
        await ExecuteAsync(connection, @"
            CREATE TABLE IF NOT EXISTS vyral_collections (
                name TEXT PRIMARY KEY,
                policy_json JSONB NOT NULL
            );", ct);

        // Records
        await ExecuteAsync(connection, @"
            CREATE TABLE IF NOT EXISTS vyral_records (
                collection TEXT NOT NULL REFERENCES vyral_collections(name),
                partition_key TEXT NOT NULL,
                id TEXT NOT NULL,
                content_json JSONB NOT NULL,
                etag TEXT,
                revision BIGINT,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (collection, partition_key, id)
            );", ct);

        await ExecuteAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_vyral_records_collection
            ON vyral_records(collection);", ct);

        // Record vectors — one row per (collection, partition_key, record_id, vector_name)
        // The vector column is untyped here; dimension is enforced per collection policy
        // and cast at insert time using vector(N) syntax.
        await ExecuteAsync(connection, @"
            CREATE TABLE IF NOT EXISTS vyral_record_vectors (
                collection TEXT NOT NULL,
                partition_key TEXT NOT NULL,
                record_id TEXT NOT NULL,
                vector_name TEXT NOT NULL,
                vector_data vector NOT NULL,
                dimensions INT NOT NULL,
                PRIMARY KEY (collection, partition_key, record_id, vector_name),
                FOREIGN KEY (collection, partition_key, record_id)
                    REFERENCES vyral_records(collection, partition_key, id)
                    ON DELETE CASCADE
            );", ct);

        // Metadata index for fast filter/order on configured JSON paths
        await ExecuteAsync(connection, @"
            CREATE TABLE IF NOT EXISTS vyral_record_metadata_index (
                collection TEXT NOT NULL,
                partition_key TEXT NOT NULL,
                record_id TEXT NOT NULL,
                path TEXT NOT NULL,
                value_text TEXT,
                value_number DOUBLE PRECISION,
                value_bool BOOLEAN,
                value_json TEXT NOT NULL,
                PRIMARY KEY (collection, partition_key, record_id, path),
                FOREIGN KEY (collection, partition_key, record_id)
                    REFERENCES vyral_records(collection, partition_key, id)
                    ON DELETE CASCADE
            );", ct);

        await ExecuteAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_vyral_metadata_text
            ON vyral_record_metadata_index(collection, path, value_text);", ct);

        await ExecuteAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_vyral_metadata_number
            ON vyral_record_metadata_index(collection, path, value_number);", ct);

        // Object store
        await ExecuteAsync(connection, @"
            CREATE TABLE IF NOT EXISTS vyral_objects (
                container TEXT NOT NULL,
                key TEXT NOT NULL,
                content BYTEA NOT NULL,
                content_type TEXT,
                content_hash TEXT NOT NULL,
                etag TEXT NOT NULL,
                metadata_json JSONB,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (container, key)
            );", ct);

        // Traces
        await ExecuteAsync(connection, @"
            CREATE TABLE IF NOT EXISTS vyral_traces (
                id TEXT PRIMARY KEY,
                operation TEXT NOT NULL,
                adapter TEXT,
                request_json JSONB NOT NULL,
                result_summary_json JSONB NOT NULL,
                started_at TIMESTAMPTZ NOT NULL,
                duration_ms DOUBLE PRECISION NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );", ct);

        await ExecuteAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_vyral_traces_operation_created
            ON vyral_traces(operation, created_at DESC);", ct);

        await ExecuteAsync(connection, @"
            INSERT INTO vyral_migrations (id) VALUES ('schema:1')
            ON CONFLICT (id) DO NOTHING;", ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
