using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.CanonicalProjectionStarter;

public sealed class SqliteCanonicalProjectionOptions
{
    public required string DatabasePath { get; init; }
    public string ConsumerId { get; init; } = "canonical-relational-projection";
    public string Topic { get; init; } = "canonical.document.changed";
    public int MaxBatchSize { get; init; } = 25;
    public double LeaseSeconds { get; init; } = 60;
}

public sealed class CanonicalProjectionDocument
{
    public string TenantId { get; init; } = string.Empty;
    public string DocumentType { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string SchemaVersion { get; init; } = string.Empty;
    public long Revision { get; init; }
    public JsonNode? Data { get; init; }
}

public sealed class CanonicalProjectionRebuildResult
{
    public string TenantId { get; init; } = string.Empty;
    public string SnapshotContentHash { get; init; } = string.Empty;
    public int DocumentCount { get; init; }
    public int CheckpointedEventCount { get; init; }
}

public sealed class CanonicalProjectionPumpResult
{
    public int Leased { get; init; }
    public int Applied { get; init; }
    public int Duplicate { get; init; }
    public int Released { get; init; }
}

/// <summary>
/// Consumer-owned relational projection starter. Each event id is committed in the same SQLite
/// transaction as its read-model change, so a crash between projection commit and CanonicalStore
/// acknowledgement is replay-safe. CanonicalStore remains authoritative.
/// </summary>
public sealed class SqliteCanonicalProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteCanonicalProjectionOptions _options;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public SqliteCanonicalProjection(SqliteCanonicalProjectionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            throw new InvalidOperationException("Projection database path is required.");
        if (string.IsNullOrWhiteSpace(options.ConsumerId))
            throw new InvalidOperationException("Projection consumer id is required.");
        if (string.IsNullOrWhiteSpace(options.Topic))
            throw new InvalidOperationException("Projection topic is required.");
        if (options.MaxBatchSize is < 1 or > 100)
            throw new InvalidOperationException("Projection batch size must be between 1 and 100.");
        if (!double.IsFinite(options.LeaseSeconds) || options.LeaseSeconds is <= 0 or > 86_400)
            throw new InvalidOperationException("Projection lease duration must be greater than zero and no more than one day.");

        var fullPath = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString;
    }

    public async Task<CanonicalProjectionRebuildResult> RebuildAsync(
        ICanonicalStore canonicalStore,
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalStore);
        CanonicalContractValidator.ValidateTenantId(tenantId);
        var archive = await canonicalStore.ExportTenantArchiveAsync(tenantId, ct: ct);
        var snapshot = CanonicalTenantArchiveCodec.Read(new CanonicalArchiveRestoreRequest
        {
            Archive = archive,
            ExpectedContentHash = archive.ContentHash
        });
        return await RebuildAsync(snapshot, ct);
    }

    /// <summary>Rebuilds directly from an already verified/exported tenant snapshot.</summary>
    public async Task<CanonicalProjectionRebuildResult> RebuildAsync(
        CanonicalTenantSnapshot snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        CanonicalContractValidator.ValidateSnapshot(snapshot, enforcePortableSize: false);
        var contentHash = CanonicalSnapshotHasher.Compute(snapshot);
        if (!string.Equals(snapshot.ContentHash, contentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical projection rebuild snapshot failed content-hash verification.");

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await DeleteTenantAsync(connection, transaction, snapshot.TenantId, ct);

        var documents = snapshot.Documents.Where(document => !document.Deleted).ToList();
        foreach (var document in documents)
            await UpsertDocumentAsync(connection, transaction, document, ct);

        // The snapshot already contains the effect of every enclosed outbox event. Recording all
        // event ids is the rebuild fence: ready events restored with the archive can be safely
        // acknowledged without applying the same mutation a second time.
        foreach (var item in snapshot.Outbox)
            await InsertCheckpointAsync(connection, transaction, item, ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO canonical_projection_fences (tenant_id, snapshot_content_hash, rebuilt_at_utc)
                VALUES ($tenant_id, $snapshot_content_hash, $rebuilt_at_utc);
                """;
            command.Parameters.AddWithValue("$tenant_id", snapshot.TenantId);
            command.Parameters.AddWithValue("$snapshot_content_hash", contentHash);
            command.Parameters.AddWithValue("$rebuilt_at_utc", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new CanonicalProjectionRebuildResult
        {
            TenantId = snapshot.TenantId,
            SnapshotContentHash = contentHash,
            DocumentCount = documents.Count,
            CheckpointedEventCount = snapshot.Outbox.Count
        };
    }

    public async Task<CanonicalProjectionPumpResult> PumpOnceAsync(
        ICanonicalStore canonicalStore,
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalStore);
        CanonicalContractValidator.ValidateTenantId(tenantId);
        var leases = await canonicalStore.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest
        {
            TenantId = tenantId,
            ConsumerId = _options.ConsumerId,
            MaxItems = _options.MaxBatchSize,
            LeaseSeconds = _options.LeaseSeconds
        }, ct);
        var applied = 0;
        var duplicate = 0;
        var released = 0;

        foreach (var lease in leases)
        {
            try
            {
                if (await ApplyAsync(canonicalStore, lease.Event, ct)) duplicate++;
                else applied++;
                await canonicalStore.AcknowledgeOutboxAsync(tenantId, lease.Event.Id, lease.LeaseToken, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await canonicalStore.NackOutboxAsync(new CanonicalOutboxNackRequest
                {
                    TenantId = tenantId,
                    EventId = lease.Event.Id,
                    LeaseToken = lease.LeaseToken,
                    Error = "projection_failure"
                }, ct);
                released++;
            }
        }

        return new CanonicalProjectionPumpResult
        {
            Leased = leases.Count,
            Applied = applied,
            Duplicate = duplicate,
            Released = released
        };
    }

    /// <returns><see langword="true"/> when the event was already transactionally checkpointed.</returns>
    public async Task<bool> ApplyAsync(
        ICanonicalStore canonicalStore,
        CanonicalOutboxEvent item,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalStore);
        ArgumentNullException.ThrowIfNull(item);

        // Rebuild fences cover every event enclosed by the verified snapshot, including events
        // routed to other handlers by a tenant-wide dispatcher. Check that fence before parsing
        // this projection's topic or reading the source so restored deliveries stay harmless.
        await EnsureInitializedAsync(ct);
        await using (var checkpointConnection = await OpenAsync(ct))
        {
            if (await IsCheckpointedAsync(checkpointConnection, transaction: null, item.TenantId, item.Id, ct))
                return true;
        }

        if (!string.Equals(item.Topic, _options.Topic, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical projection received an unsupported outbox topic.");

        var documentType = RequiredPayloadString(item.Payload, "documentType");
        var documentId = RequiredPayloadString(item.Payload, "documentId");
        var expectedRevision = item.Payload?["revision"]?.GetValue<long?>();
        var document = await canonicalStore.GetDocumentAsync(
            item.TenantId,
            documentType,
            documentId,
            includeDeleted: true,
            ct);
        if (expectedRevision.HasValue && (document is null || document.Revision < expectedRevision.Value))
            throw new InvalidOperationException("Canonical projection source revision is behind its outbox event.");

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        if (await IsCheckpointedAsync(connection, transaction, item.TenantId, item.Id, ct))
        {
            await transaction.CommitAsync(ct);
            return true;
        }

        if (document is null || document.Deleted)
            await DeleteDocumentAsync(connection, transaction, item.TenantId, documentType, documentId, ct);
        else
            await UpsertDocumentAsync(connection, transaction, document, ct);
        await InsertCheckpointAsync(connection, transaction, item, ct);
        await transaction.CommitAsync(ct);
        return false;
    }

    public async Task<CanonicalProjectionDocument?> GetAsync(
        string tenantId,
        string documentType,
        string documentId,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, revision, data_json
            FROM canonical_projection_documents
            WHERE tenant_id = $tenant_id AND document_type = $document_type AND document_id = $document_id;
            """;
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        command.Parameters.AddWithValue("$document_type", documentType);
        command.Parameters.AddWithValue("$document_id", documentId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new CanonicalProjectionDocument
        {
            TenantId = tenantId,
            DocumentType = documentType,
            Id = documentId,
            SchemaVersion = reader.GetString(0),
            Revision = reader.GetInt64(1),
            Data = JsonNode.Parse(reader.GetString(2))
        };
    }

    public async Task<string?> GetRebuildFenceAsync(string tenantId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_content_hash FROM canonical_projection_fences WHERE tenant_id = $tenant_id;";
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initializeGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS canonical_projection_documents (
                    tenant_id TEXT NOT NULL,
                    document_type TEXT NOT NULL,
                    document_id TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    data_json TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (tenant_id, document_type, document_id)
                );
                CREATE TABLE IF NOT EXISTS canonical_projection_events (
                    tenant_id TEXT NOT NULL,
                    event_id TEXT NOT NULL,
                    transaction_id TEXT NOT NULL,
                    topic TEXT NOT NULL,
                    projected_at_utc TEXT NOT NULL,
                    PRIMARY KEY (tenant_id, event_id)
                );
                CREATE TABLE IF NOT EXISTS canonical_projection_fences (
                    tenant_id TEXT PRIMARY KEY,
                    snapshot_content_hash TEXT NOT NULL,
                    rebuilt_at_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task DeleteTenantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        CancellationToken ct)
    {
        foreach (var table in new[] { "canonical_projection_documents", "canonical_projection_events", "canonical_projection_fences" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE tenant_id = $tenant_id;";
            command.Parameters.AddWithValue("$tenant_id", tenantId);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task UpsertDocumentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CanonicalDocument document,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO canonical_projection_documents
                (tenant_id, document_type, document_id, schema_version, revision, data_json, updated_at_utc)
            VALUES
                ($tenant_id, $document_type, $document_id, $schema_version, $revision, $data_json, $updated_at_utc)
            ON CONFLICT (tenant_id, document_type, document_id) DO UPDATE SET
                schema_version = excluded.schema_version,
                revision = excluded.revision,
                data_json = excluded.data_json,
                updated_at_utc = excluded.updated_at_utc
            WHERE excluded.revision >= canonical_projection_documents.revision;
            """;
        command.Parameters.AddWithValue("$tenant_id", document.TenantId);
        command.Parameters.AddWithValue("$document_type", document.DocumentType);
        command.Parameters.AddWithValue("$document_id", document.Id);
        command.Parameters.AddWithValue("$schema_version", document.SchemaVersion);
        command.Parameters.AddWithValue("$revision", document.Revision);
        command.Parameters.AddWithValue("$data_json", JsonSerializer.Serialize(document.Data, JsonOptions));
        command.Parameters.AddWithValue("$updated_at_utc", document.UpdatedAtUtc.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteDocumentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string documentType,
        string documentId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM canonical_projection_documents
            WHERE tenant_id = $tenant_id AND document_type = $document_type AND document_id = $document_id;
            """;
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        command.Parameters.AddWithValue("$document_type", documentType);
        command.Parameters.AddWithValue("$document_id", documentId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> IsCheckpointedAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tenantId,
        string eventId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM canonical_projection_events
            WHERE tenant_id = $tenant_id AND event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        command.Parameters.AddWithValue("$event_id", eventId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task InsertCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CanonicalOutboxEvent item,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO canonical_projection_events
                (tenant_id, event_id, transaction_id, topic, projected_at_utc)
            VALUES ($tenant_id, $event_id, $transaction_id, $topic, $projected_at_utc);
            """;
        command.Parameters.AddWithValue("$tenant_id", item.TenantId);
        command.Parameters.AddWithValue("$event_id", item.Id);
        command.Parameters.AddWithValue("$transaction_id", item.TransactionId);
        command.Parameters.AddWithValue("$topic", item.Topic);
        command.Parameters.AddWithValue("$projected_at_utc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string RequiredPayloadString(JsonNode? payload, string name)
    {
        var value = payload?[name]?.GetValue<string>()?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Canonical projection event requires payload.{name}.")
            : value;
    }
}
