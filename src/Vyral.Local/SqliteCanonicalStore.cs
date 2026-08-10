using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

/// <summary>
/// SQLite reference implementation of the strong CanonicalStore profile. Every domain commit uses
/// one SQLite transaction for document state, immutable revisions, uniqueness fences, outbox, and
/// idempotency receipt. It is suitable for local development and single-node deployments.
/// </summary>
public sealed class SqliteCanonicalStore : ICanonicalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public SqliteCanonicalStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Canonical SQLite database path is required.", nameof(databasePath));
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = true,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task ApplyMigrationsAsync(IReadOnlyList<CanonicalMigration> migrations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        await EnsureInitializedAsync(ct);
        foreach (var migration in migrations) CanonicalContractValidator.ValidateMigration(migration);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var migration in migrations.OrderBy(item => item.Namespace, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                var storageId = CanonicalMigrationIdentity.Create(migration.Namespace, migration.Id);
                var existing = await GetMigrationAsync(connection, transaction, storageId, ct);
                if (existing is not null)
                {
                    if (!string.Equals(existing.Checksum, migration.Checksum, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Canonical migration '{migration.Namespace}/{migration.Id}' was already applied with a different checksum.");
                    continue;
                }

                await ExecuteAsync(connection, transaction, """
                    INSERT INTO vyral_canonical_migrations (id, checksum, description, applied_at_utc)
                    VALUES ($id, $checksum, $description, $applied_at_utc);
                    """, ct,
                    ("$id", storageId),
                    ("$checksum", migration.Checksum.Trim()),
                    ("$description", migration.Description),
                    ("$applied_at_utc", DateTime.UtcNow.ToString("O")));
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<CanonicalMigrationReceipt>> ListMigrationsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, checksum, description, applied_at_utc FROM vyral_canonical_migrations ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<CanonicalMigrationReceipt>();
        while (await reader.ReadAsync(ct))
        {
            var identity = CanonicalMigrationIdentity.Parse(reader.GetString(0));
            results.Add(new CanonicalMigrationReceipt
            {
                Namespace = identity.Namespace,
                Id = identity.Id,
                Checksum = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                AppliedAtUtc = ParseUtc(reader.GetString(3))
            });
        }
        return results;
    }

    public async Task<CanonicalTransactionResult> CommitAsync(CanonicalTransactionRequest request, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateTransaction(request);
        await EnsureInitializedAsync(ct);
        var tenantId = request.TenantId.Trim();
        var idempotencyKey = request.IdempotencyKey.Trim();
        var requestHash = CanonicalTransactionHasher.ComputeRequestHash(request);
        var transactionId = CanonicalTransactionHasher.CreateTransactionId(tenantId, idempotencyKey);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var existing = await GetTransactionReceiptAsync(connection, transaction, tenantId, idempotencyKey, ct);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Canonical idempotency key '{idempotencyKey}' already belongs to a different transaction request.");
                var replay = Clone<CanonicalTransactionResult>(existing.Result);
                replay.Replayed = true;
                await transaction.CommitAsync(ct);
                return replay;
            }

            var now = DateTime.UtcNow;
            var result = new CanonicalTransactionResult
            {
                TransactionId = transactionId,
                TenantId = tenantId,
                IdempotencyKey = idempotencyKey,
                CorrelationId = request.CorrelationId?.Trim(),
                Actor = request.Actor?.Trim(),
                CommittedAtUtc = now
            };

            foreach (var mutation in request.Mutations)
            {
                var document = mutation.Operation == CanonicalMutationOperations.Upsert
                    ? await ApplyUpsertAsync(connection, transaction, tenantId, transactionId, mutation, now, ct)
                    : await ApplyDeleteAsync(connection, transaction, tenantId, transactionId, mutation, now, ct);
                result.Documents.Add(document);
            }

            foreach (var fence in request.Fences)
            {
                await ApplyFenceAsync(connection, transaction, tenantId, fence, now, ct);
            }

            for (var index = 0; index < request.Outbox.Count; index++)
            {
                var item = request.Outbox[index];
                var eventId = string.IsNullOrWhiteSpace(item.Id) ? $"{transactionId}:{index:D3}" : item.Id.Trim();
                var outbox = new CanonicalOutboxEvent
                {
                    Id = eventId,
                    TenantId = tenantId,
                    TransactionId = transactionId,
                    Topic = item.Topic.Trim(),
                    Key = item.Key.Trim(),
                    Payload = CloneNode(item.Payload),
                    Headers = new Dictionary<string, string>(item.Headers, StringComparer.Ordinal),
                    NotBeforeUtc = item.NotBeforeUtc?.ToUniversalTime(),
                    MaxDeliveryAttempts = item.MaxDeliveryAttempts
                };
                await InsertOutboxAsync(connection, transaction, outbox, ct);
                result.Outbox.Add(Clone(outbox));
            }

            var receipt = new CanonicalTransactionReceipt
            {
                TransactionId = transactionId,
                TenantId = tenantId,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                Result = Clone(result),
                CommittedAtUtc = now
            };
            await InsertTransactionReceiptAsync(connection, transaction, receipt, ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<CanonicalDocument?> GetDocumentAsync(string tenantId, string documentType, string id, bool includeDeleted = false, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateDocumentIdentity(tenantId, documentType, id);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var document = await GetDocumentAsync(connection, null, tenantId.Trim(), documentType.Trim(), id.Trim(), ct);
        return document is not null && (!document.Deleted || includeDeleted) ? document : null;
    }

    public async Task<CanonicalDocumentQueryResult> QueryDocumentsAsync(CanonicalDocumentQuery query, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateQuery(query);
        await EnsureInitializedAsync(ct);
        var tenantId = query.TenantId.Trim();
        var limit = query.Limit ?? 100;
        var hasIndexOrder = !string.IsNullOrWhiteSpace(query.OrderByIndex);
        var (lastOrderValue, lastType, lastId) = hasIndexOrder ? DecodeOrderedContinuation(query.ContinuationToken) : (null, null, null);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var predicates = new List<string> { "d.tenant_id = $tenant_id" };
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        if (!query.IncludeDeleted) predicates.Add("d.deleted = 0");
        if (!string.IsNullOrWhiteSpace(query.DocumentType))
        {
            predicates.Add("d.document_type = $document_type");
            command.Parameters.AddWithValue("$document_type", query.DocumentType.Trim());
        }
        if (hasIndexOrder && lastOrderValue is not null)
        {
            var comparison = query.OrderDirection == CanonicalDocumentOrderDirections.Descending ? "<" : ">";
            predicates.Add($"(oi.index_value {comparison} $last_order_value OR (oi.index_value = $last_order_value AND (d.document_type {comparison} $last_type OR (d.document_type = $last_type AND d.document_id {comparison} $last_id))))");
            command.Parameters.AddWithValue("$last_order_value", lastOrderValue);
            command.Parameters.AddWithValue("$last_type", lastType!);
            command.Parameters.AddWithValue("$last_id", lastId!);
        }
        else if (lastType is not null)
        {
            predicates.Add("(d.document_type > $last_type OR (d.document_type = $last_type AND d.document_id > $last_id))");
            command.Parameters.AddWithValue("$last_type", lastType);
            command.Parameters.AddWithValue("$last_id", lastId!);
        }
        var index = 0;
        foreach (var (name, value) in query.Indexes.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            predicates.Add($"EXISTS (SELECT 1 FROM vyral_canonical_document_indexes ci{index} WHERE ci{index}.tenant_id = d.tenant_id AND ci{index}.document_type = d.document_type AND ci{index}.document_id = d.document_id AND ci{index}.index_name = $index_name_{index} AND ci{index}.index_value = $index_value_{index})");
            command.Parameters.AddWithValue($"$index_name_{index}", name);
            command.Parameters.AddWithValue($"$index_value_{index}", value);
            index++;
        }
        if (query.IndexRange is not null)
        {
            var rangePredicates = new List<string> { "cir.tenant_id = d.tenant_id", "cir.document_type = d.document_type", "cir.document_id = d.document_id", "cir.index_name = $range_index_name" };
            command.Parameters.AddWithValue("$range_index_name", query.IndexRange.Name.Trim());
            if (query.IndexRange.GreaterThanOrEqual is not null)
            {
                rangePredicates.Add("cir.index_value >= $range_lower");
                command.Parameters.AddWithValue("$range_lower", query.IndexRange.GreaterThanOrEqual);
            }
            if (query.IndexRange.LessThanOrEqual is not null)
            {
                rangePredicates.Add("cir.index_value <= $range_upper");
                command.Parameters.AddWithValue("$range_upper", query.IndexRange.LessThanOrEqual);
            }
            predicates.Add($"EXISTS (SELECT 1 FROM vyral_canonical_document_indexes cir WHERE {string.Join(" AND ", rangePredicates)})");
        }
        command.Parameters.AddWithValue("$limit", limit + 1);
        var join = string.Empty;
        var orderBy = "d.document_type, d.document_id";
        var select = "d.document_json";
        if (hasIndexOrder)
        {
            join = " INNER JOIN vyral_canonical_document_indexes oi ON oi.tenant_id = d.tenant_id AND oi.document_type = d.document_type AND oi.document_id = d.document_id AND oi.index_name = $order_index_name";
            command.Parameters.AddWithValue("$order_index_name", query.OrderByIndex!.Trim());
            var direction = query.OrderDirection == CanonicalDocumentOrderDirections.Descending ? "DESC" : "ASC";
            orderBy = $"oi.index_value {direction}, d.document_type {direction}, d.document_id {direction}";
            select = "d.document_json, oi.index_value";
        }
        command.CommandText = $"SELECT {select} FROM vyral_canonical_documents d{join} WHERE {string.Join(" AND ", predicates)} ORDER BY {orderBy} LIMIT $limit";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<(CanonicalDocument Document, string? OrderValue)>();
        while (await reader.ReadAsync(ct)) rows.Add((Deserialize<CanonicalDocument>(reader.GetString(0)), hasIndexOrder ? reader.GetString(1) : null));
        var continuation = rows.Count > limit
            ? hasIndexOrder ? EncodeOrderedContinuation(rows[limit - 1].OrderValue!, rows[limit - 1].Document.DocumentType, rows[limit - 1].Document.Id) : EncodeContinuation(rows[limit - 1].Document.DocumentType, rows[limit - 1].Document.Id)
            : null;
        if (rows.Count > limit) rows.RemoveAt(rows.Count - 1);
        var items = rows.Select(row => row.Document).ToList();
        return new CanonicalDocumentQueryResult { Items = items, ContinuationToken = continuation };
    }

    public async Task<IReadOnlyList<CanonicalDocumentRevision>> GetRevisionsAsync(string tenantId, string documentType, string id, int limit = 100, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateDocumentIdentity(tenantId, documentType, id);
        if (limit is <= 0 or > CanonicalContractValidator.MaxQueryLimit) throw new InvalidOperationException($"Canonical revision limit must be between 1 and {CanonicalContractValidator.MaxQueryLimit}.");
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision_json FROM vyral_canonical_revisions
            WHERE tenant_id = $tenant_id AND document_type = $document_type AND document_id = $document_id
            ORDER BY revision DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$tenant_id", tenantId.Trim());
        command.Parameters.AddWithValue("$document_type", documentType.Trim());
        command.Parameters.AddWithValue("$document_id", id.Trim());
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<CanonicalDocumentRevision>();
        while (await reader.ReadAsync(ct)) items.Add(Deserialize<CanonicalDocumentRevision>(reader.GetString(0)));
        return items;
    }

    public async Task<IReadOnlyList<CanonicalOutboxLease>> LeaseOutboxAsync(CanonicalOutboxLeaseRequest request, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateOutboxLease(request);
        await EnsureInitializedAsync(ct);
        var now = DateTime.UtcNow;
        var expiresAt = now.AddSeconds(request.LeaseSeconds);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var candidates = await SelectLeaseableOutboxAsync(connection, transaction, request.TenantId.Trim(), now, request.MaxItems, ct);
            var leases = new List<CanonicalOutboxLease>();
            foreach (var candidate in candidates)
            {
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var changed = await ExecuteAsync(connection, transaction, """
                    UPDATE vyral_canonical_outbox
                    SET lease_owner = $lease_owner, lease_token_hash = $lease_token_hash,
                        lease_expires_at_utc = $lease_expires_at_utc, delivery_count = delivery_count + 1
                    WHERE tenant_id = $tenant_id AND event_id = $event_id
                      AND delivered_at_utc IS NULL
                      AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= $now);
                    """, ct,
                    ("$lease_owner", request.ConsumerId.Trim()),
                    ("$lease_token_hash", CanonicalTransactionHasher.HashLeaseToken(token)),
                    ("$lease_expires_at_utc", expiresAt.ToString("O")),
                    ("$tenant_id", request.TenantId.Trim()),
                    ("$event_id", candidate.Id),
                    ("$now", now.ToString("O")));
                if (changed == 0) continue;
                candidate.LeaseOwner = request.ConsumerId.Trim();
                candidate.LeaseExpiresAtUtc = expiresAt;
                candidate.DeliveryCount++;
                leases.Add(new CanonicalOutboxLease { Event = candidate, LeaseToken = token, ExpiresAtUtc = expiresAt });
            }
            await transaction.CommitAsync(ct);
            return leases;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<CanonicalOutboxQueryResult> QueryOutboxAsync(CanonicalOutboxQuery query, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateOutboxQuery(query);
        await EnsureInitializedAsync(ct);
        var now = DateTime.UtcNow;
        var limit = query.Limit ?? 100;
        var lastEventId = DecodeOutboxContinuation(query.ContinuationToken);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var predicates = new List<string> { "tenant_id = $tenant_id" };
        command.Parameters.AddWithValue("$tenant_id", query.TenantId.Trim());
        if (lastEventId is not null)
        {
            predicates.Add("event_id > $event_id");
            command.Parameters.AddWithValue("$event_id", lastEventId);
        }
        if (!string.IsNullOrWhiteSpace(query.Topic))
        {
            predicates.Add("json_extract(event_json, '$.topic') = $topic");
            command.Parameters.AddWithValue("$topic", query.Topic.Trim());
        }
        switch (query.State)
        {
            case CanonicalOutboxStates.Ready:
                predicates.Add("delivered_at_utc IS NULL AND dead_lettered_at_utc IS NULL AND (not_before_utc IS NULL OR not_before_utc <= $now) AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= $now)");
                break;
            case CanonicalOutboxStates.Leased:
                predicates.Add("delivered_at_utc IS NULL AND dead_lettered_at_utc IS NULL AND lease_expires_at_utc > $now");
                break;
            case CanonicalOutboxStates.Scheduled:
                predicates.Add("delivered_at_utc IS NULL AND dead_lettered_at_utc IS NULL AND not_before_utc > $now AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= $now)");
                break;
            case CanonicalOutboxStates.Delivered:
                predicates.Add("delivered_at_utc IS NOT NULL");
                break;
            case CanonicalOutboxStates.DeadLetter:
                predicates.Add("dead_lettered_at_utc IS NOT NULL");
                break;
        }
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.CommandText = $"SELECT event_json, delivery_count, delivered_at_utc, lease_owner, lease_expires_at_utc, last_error, max_delivery_attempts, dead_lettered_at_utc FROM vyral_canonical_outbox WHERE {string.Join(" AND ", predicates)} ORDER BY event_id LIMIT $limit";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<CanonicalOutboxEvent>();
        while (await reader.ReadAsync(ct)) items.Add(ReadOutboxEvent(reader));
        var continuation = items.Count > limit ? EncodeOutboxContinuation(items[limit - 1].Id) : null;
        if (items.Count > limit) items.RemoveAt(items.Count - 1);
        return new CanonicalOutboxQueryResult { Items = items, ContinuationToken = continuation };
    }

    public async Task AcknowledgeOutboxAsync(string tenantId, string eventId, string leaseToken, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateOutboxAcknowledgement(tenantId, eventId, leaseToken);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var changed = await ExecuteAsync(connection, null, """
            UPDATE vyral_canonical_outbox
            SET delivered_at_utc = COALESCE(delivered_at_utc, $now), lease_owner = NULL, lease_expires_at_utc = NULL
            WHERE tenant_id = $tenant_id AND event_id = $event_id AND lease_token_hash = $lease_token_hash
              AND (delivered_at_utc IS NOT NULL OR lease_expires_at_utc > $now);
            """, ct,
            ("$now", DateTime.UtcNow.ToString("O")),
            ("$tenant_id", tenantId.Trim()),
            ("$event_id", eventId.Trim()),
            ("$lease_token_hash", CanonicalTransactionHasher.HashLeaseToken(leaseToken)));
        if (changed == 0) throw new InvalidOperationException("Canonical outbox lease is not active for this acknowledgement.");
    }

    public async Task<CanonicalOutboxLeaseRenewal> RenewOutboxLeaseAsync(CanonicalOutboxLeaseRenewalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CanonicalContractValidator.ValidateOutboxLeaseRenewal(request);
        await EnsureInitializedAsync(ct);
        var now = DateTime.UtcNow;
        var expiresAt = now.AddSeconds(request.LeaseSeconds);
        await using var connection = await OpenAsync(ct);
        var changed = await ExecuteAsync(connection, null, """
            UPDATE vyral_canonical_outbox
            SET lease_expires_at_utc = $lease_expires_at_utc
            WHERE tenant_id = $tenant_id AND event_id = $event_id AND delivered_at_utc IS NULL
              AND lease_token_hash = $lease_token_hash AND lease_expires_at_utc > $now;
            """, ct,
            ("$lease_expires_at_utc", expiresAt.ToString("O")),
            ("$tenant_id", request.TenantId.Trim()),
            ("$event_id", request.EventId.Trim()),
            ("$lease_token_hash", CanonicalTransactionHasher.HashLeaseToken(request.LeaseToken)),
            ("$now", now.ToString("O")));
        if (changed == 0) throw new InvalidOperationException("Canonical outbox lease is not active for this renewal.");
        return new CanonicalOutboxLeaseRenewal { ExpiresAtUtc = expiresAt };
    }

    public async Task NackOutboxAsync(CanonicalOutboxNackRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CanonicalContractValidator.ValidateOutboxNack(request);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var now = DateTime.UtcNow;
        var notBefore = request.NotBeforeUtc?.ToUniversalTime() ?? now.AddSeconds(request.RetryAfterSeconds ?? CanonicalContractValidator.DefaultOutboxRetryDelaySeconds);
        var changed = await ExecuteAsync(connection, null, """
            UPDATE vyral_canonical_outbox
            SET lease_owner = NULL, lease_token_hash = NULL, lease_expires_at_utc = NULL,
                not_before_utc = CASE WHEN max_delivery_attempts IS NOT NULL AND delivery_count >= max_delivery_attempts THEN NULL ELSE $not_before_utc END,
                dead_lettered_at_utc = CASE WHEN max_delivery_attempts IS NOT NULL AND delivery_count >= max_delivery_attempts THEN $now ELSE dead_lettered_at_utc END,
                last_error = $last_error
            WHERE tenant_id = $tenant_id AND event_id = $event_id AND delivered_at_utc IS NULL
              AND lease_token_hash = $lease_token_hash AND lease_expires_at_utc > $now;
            """, ct,
            ("$not_before_utc", notBefore.ToString("O")),
            ("$last_error", TrimError(request.Error)),
            ("$tenant_id", request.TenantId.Trim()),
            ("$event_id", request.EventId.Trim()),
            ("$lease_token_hash", CanonicalTransactionHasher.HashLeaseToken(request.LeaseToken)),
            ("$now", now.ToString("O")));
        if (changed == 0) throw new InvalidOperationException("Canonical outbox lease is not active for this release.");
    }

    public async Task ReplayOutboxAsync(CanonicalOutboxReplayRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CanonicalContractValidator.ValidateOutboxReplay(request);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var changed = await ExecuteAsync(connection, null, """
            UPDATE vyral_canonical_outbox
            SET lease_owner = NULL, lease_token_hash = NULL, lease_expires_at_utc = NULL,
                not_before_utc = $now, dead_lettered_at_utc = NULL, last_error = NULL,
                delivery_count = CASE WHEN $reset_delivery_count THEN 0 ELSE delivery_count END
            WHERE tenant_id = $tenant_id AND event_id = $event_id AND dead_lettered_at_utc IS NOT NULL;
            """, ct,
            ("$now", DateTime.UtcNow.ToString("O")), ("$reset_delivery_count", request.ResetDeliveryCount ? 1 : 0),
            ("$tenant_id", request.TenantId.Trim()), ("$event_id", request.EventId.Trim()));
        if (changed == 0) throw new InvalidOperationException("Canonical outbox event is not dead-lettered for this replay.");
    }

    public async Task<CanonicalTenantSnapshot> ExportTenantAsync(string tenantId, CancellationToken ct = default)
    {
        var snapshot = await ExportTenantSnapshotAsync(tenantId, ct);
        CanonicalContractValidator.ValidateSnapshotSize(snapshot);
        return snapshot;
    }

    public async Task<CanonicalTenantArchive> ExportTenantArchiveAsync(string tenantId, int chunkBytes = CanonicalTenantArchive.DefaultChunkBytes, CancellationToken ct = default)
        => CanonicalTenantArchiveCodec.Create(await ExportTenantSnapshotAsync(tenantId, ct), chunkBytes);

    private async Task<CanonicalTenantSnapshot> ExportTenantSnapshotAsync(string tenantId, CancellationToken ct)
    {
        CanonicalContractValidator.ValidateTenantId(tenantId);
        await EnsureInitializedAsync(ct);
        var tenant = tenantId.Trim();
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var snapshot = new CanonicalTenantSnapshot
            {
                TenantId = tenant,
                Documents = await ReadJsonListAsync<CanonicalDocument>(connection, transaction, "SELECT document_json FROM vyral_canonical_documents WHERE tenant_id = $tenant_id ORDER BY document_type, document_id", tenant, ct),
                Revisions = await ReadJsonListAsync<CanonicalDocumentRevision>(connection, transaction, "SELECT revision_json FROM vyral_canonical_revisions WHERE tenant_id = $tenant_id ORDER BY document_type, document_id, revision", tenant, ct),
                Fences = await ReadFencesAsync(connection, transaction, tenant, ct),
                Outbox = await ReadOutboxAsync(connection, transaction, tenant, ct),
                Transactions = await ReadTransactionsAsync(connection, transaction, tenant, ct),
                ExportedAtUtc = DateTime.UtcNow
            };
            snapshot.ContentHash = CanonicalSnapshotHasher.Compute(snapshot);
            await transaction.CommitAsync(ct);
            return snapshot;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task RestoreTenantAsync(CanonicalRestoreRequest request, CancellationToken ct = default)
        => await RestoreTenantSnapshotAsync(request, enforcePortableSize: true, ct);

    public async Task RestoreTenantArchiveAsync(CanonicalArchiveRestoreRequest request, CancellationToken ct = default)
    {
        var snapshot = CanonicalTenantArchiveCodec.Read(request);
        await RestoreTenantSnapshotAsync(new CanonicalRestoreRequest
        {
            Snapshot = snapshot,
            ExpectedContentHash = snapshot.ContentHash
        }, enforcePortableSize: false, ct);
    }

    private async Task RestoreTenantSnapshotAsync(CanonicalRestoreRequest request, bool enforcePortableSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        CanonicalContractValidator.ValidateSnapshot(request.Snapshot, enforcePortableSize);
        var actualHash = CanonicalSnapshotHasher.Compute(request.Snapshot);
        var expectedHash = string.IsNullOrWhiteSpace(request.ExpectedContentHash) ? request.Snapshot.ContentHash : request.ExpectedContentHash.Trim();
        if (string.IsNullOrWhiteSpace(expectedHash) || !string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical snapshot content hash does not match the requested restore.");
        await EnsureInitializedAsync(ct);
        var tenantId = request.Snapshot.TenantId.Trim();
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await DeleteTenantAsync(connection, transaction, tenantId, ct);
            foreach (var document in request.Snapshot.Documents)
            {
                await UpsertDocumentAsync(connection, transaction, document, ct);
                await ReplaceDocumentIndexesAsync(connection, transaction, document, ct);
            }
            foreach (var revision in request.Snapshot.Revisions) await InsertRevisionAsync(connection, transaction, revision, ct);
            foreach (var fence in request.Snapshot.Fences) await InsertFenceAsync(connection, transaction, fence, ct);
            foreach (var item in request.Snapshot.Outbox)
            {
                // A restore clears ephemeral delivery leases, but must not mutate the caller's
                // hash-verified snapshot object.
                var restored = Clone(item);
                restored.LeaseOwner = null;
                restored.LeaseExpiresAtUtc = null;
                await InsertOutboxAsync(connection, transaction, restored, ct);
            }
            foreach (var receipt in request.Snapshot.Transactions) await InsertTransactionReceiptAsync(connection, transaction, receipt, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<CanonicalDocument> ApplyUpsertAsync(SqliteConnection connection, SqliteTransaction transaction, string tenantId, string transactionId, CanonicalMutation mutation, DateTime now, CancellationToken ct)
    {
        var source = mutation.Document!;
        var existing = await GetDocumentAsync(connection, transaction, tenantId, source.DocumentType.Trim(), source.Id.Trim(), ct);
        EnsurePrecondition(existing, mutation.Precondition, source.DocumentType, source.Id);
        var document = Clone(source);
        document.TenantId = tenantId;
        document.DocumentType = document.DocumentType.Trim();
        document.Id = document.Id.Trim();
        document.SchemaVersion = document.SchemaVersion.Trim();
        document.Revision = (existing?.Revision ?? 0) + 1;
        document.Etag = $"rev:{document.Revision}";
        document.Deleted = false;
        document.CreatedAtUtc = existing?.CreatedAtUtc ?? now;
        document.UpdatedAtUtc = now;
        await UpsertDocumentAsync(connection, transaction, document, ct);
        await ReplaceDocumentIndexesAsync(connection, transaction, document, ct);
        await InsertRevisionAsync(connection, transaction, new CanonicalDocumentRevision
        {
            TenantId = tenantId,
            DocumentType = document.DocumentType,
            Id = document.Id,
            Revision = document.Revision,
            TransactionId = transactionId,
            Operation = CanonicalMutationOperations.Upsert,
            Document = Clone(document),
            RecordedAtUtc = now
        }, ct);
        return document;
    }

    private async Task<CanonicalDocument> ApplyDeleteAsync(SqliteConnection connection, SqliteTransaction transaction, string tenantId, string transactionId, CanonicalMutation mutation, DateTime now, CancellationToken ct)
    {
        var (documentType, id) = CanonicalContractValidator.MutationKey(mutation);
        var existing = await GetDocumentAsync(connection, transaction, tenantId, documentType, id, ct);
        EnsurePrecondition(existing, mutation.Precondition, documentType, id);
        if (existing is null || existing.Deleted) throw new InvalidOperationException($"Canonical document '{documentType}/{id}' cannot be deleted because it does not exist.");
        var document = Clone(existing);
        document.Revision++;
        document.Etag = $"rev:{document.Revision}";
        document.Deleted = true;
        document.Data = null;
        document.Indexes.Clear();
        document.UpdatedAtUtc = now;
        await UpsertDocumentAsync(connection, transaction, document, ct);
        await ReplaceDocumentIndexesAsync(connection, transaction, document, ct);
        await InsertRevisionAsync(connection, transaction, new CanonicalDocumentRevision
        {
            TenantId = tenantId,
            DocumentType = document.DocumentType,
            Id = document.Id,
            Revision = document.Revision,
            TransactionId = transactionId,
            Operation = CanonicalMutationOperations.Delete,
            Document = Clone(document),
            RecordedAtUtc = now
        }, ct);
        return document;
    }

    private static void EnsurePrecondition(CanonicalDocument? existing, CanonicalWritePrecondition? precondition, string documentType, string id)
    {
        if (precondition is null) return;
        if (precondition.MustNotExist && existing is not null) throw new InvalidOperationException($"Canonical write precondition failed: document '{documentType}/{id}' already exists.");
        if (precondition.MustExist && existing is null) throw new InvalidOperationException($"Canonical write precondition failed: document '{documentType}/{id}' does not exist.");
        if (precondition.ExpectedRevision.HasValue && (existing is null || existing.Revision != precondition.ExpectedRevision.Value))
            throw new InvalidOperationException($"Canonical write precondition failed: document '{documentType}/{id}' revision does not match.");
    }

    private async Task ApplyFenceAsync(SqliteConnection connection, SqliteTransaction transaction, string tenantId, CanonicalFenceMutation mutation, DateTime now, CancellationToken ct)
    {
        var existing = await GetFenceAsync(connection, transaction, tenantId, mutation.Name.Trim(), mutation.Value.Trim(), ct);
        if (mutation.Operation == CanonicalFenceOperations.Claim)
        {
            if (existing is not null && (!string.Equals(existing.OwnerDocumentType, mutation.OwnerDocumentType.Trim(), StringComparison.Ordinal) || !string.Equals(existing.OwnerDocumentId, mutation.OwnerDocumentId.Trim(), StringComparison.Ordinal)))
                throw new InvalidOperationException($"Canonical fence '{mutation.Name}/{mutation.Value}' is already owned by '{existing.OwnerDocumentType}/{existing.OwnerDocumentId}'.");
            if (existing is null)
            {
                await InsertFenceAsync(connection, transaction, new CanonicalFence
                {
                    TenantId = tenantId,
                    Name = mutation.Name.Trim(),
                    Value = mutation.Value.Trim(),
                    OwnerDocumentType = mutation.OwnerDocumentType.Trim(),
                    OwnerDocumentId = mutation.OwnerDocumentId.Trim(),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                }, ct);
            }
            return;
        }

        if (existing is null || !string.Equals(existing.OwnerDocumentType, mutation.OwnerDocumentType.Trim(), StringComparison.Ordinal) || !string.Equals(existing.OwnerDocumentId, mutation.OwnerDocumentId.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Canonical fence '{mutation.Name}/{mutation.Value}' cannot be released by this owner.");
        await ExecuteAsync(connection, transaction, "DELETE FROM vyral_canonical_fences WHERE tenant_id = $tenant_id AND name = $name AND value = $value", ct,
            ("$tenant_id", tenantId), ("$name", mutation.Name.Trim()), ("$value", mutation.Value.Trim()));
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var connection = await OpenAsync(ct);
            var schema = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS vyral_canonical_migrations (
                    id TEXT PRIMARY KEY,
                    checksum TEXT NOT NULL,
                    description TEXT NULL,
                    applied_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS vyral_canonical_documents (
                    tenant_id TEXT NOT NULL,
                    document_type TEXT NOT NULL,
                    document_id TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    etag TEXT NOT NULL,
                    deleted INTEGER NOT NULL,
                    document_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (tenant_id, document_type, document_id)
                );
                CREATE TABLE IF NOT EXISTS vyral_canonical_document_indexes (
                    tenant_id TEXT NOT NULL,
                    document_type TEXT NOT NULL,
                    document_id TEXT NOT NULL,
                    index_name TEXT NOT NULL,
                    index_value TEXT NOT NULL,
                    PRIMARY KEY (tenant_id, document_type, document_id, index_name),
                    FOREIGN KEY (tenant_id, document_type, document_id) REFERENCES vyral_canonical_documents(tenant_id, document_type, document_id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_vyral_canonical_document_indexes_lookup ON vyral_canonical_document_indexes(tenant_id, document_type, index_name, index_value, document_id);
                CREATE TABLE IF NOT EXISTS vyral_canonical_revisions (
                    tenant_id TEXT NOT NULL,
                    document_type TEXT NOT NULL,
                    document_id TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    transaction_id TEXT NOT NULL,
                    revision_json TEXT NOT NULL,
                    recorded_at_utc TEXT NOT NULL,
                    PRIMARY KEY (tenant_id, document_type, document_id, revision)
                );
                CREATE TABLE IF NOT EXISTS vyral_canonical_fences (
                    tenant_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    value TEXT NOT NULL,
                    owner_document_type TEXT NOT NULL,
                    owner_document_id TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (tenant_id, name, value)
                );
                CREATE TABLE IF NOT EXISTS vyral_canonical_outbox (
                    tenant_id TEXT NOT NULL,
                    event_id TEXT NOT NULL,
                    transaction_id TEXT NOT NULL,
                    event_json TEXT NOT NULL,
                    not_before_utc TEXT NULL,
                    delivery_count INTEGER NOT NULL,
                    delivered_at_utc TEXT NULL,
                    lease_owner TEXT NULL,
                    lease_token_hash TEXT NULL,
                    lease_expires_at_utc TEXT NULL,
                    last_error TEXT NULL,
                    max_delivery_attempts INTEGER NULL,
                    dead_lettered_at_utc TEXT NULL,
                    PRIMARY KEY (tenant_id, event_id)
                );
                CREATE INDEX IF NOT EXISTS ix_vyral_canonical_outbox_due ON vyral_canonical_outbox(tenant_id, delivered_at_utc, not_before_utc, lease_expires_at_utc, event_id);
                CREATE TABLE IF NOT EXISTS vyral_canonical_transactions (
                    tenant_id TEXT NOT NULL,
                    idempotency_key TEXT NOT NULL,
                    transaction_id TEXT NOT NULL,
                    request_hash TEXT NOT NULL,
                    result_json TEXT NOT NULL,
                    committed_at_utc TEXT NOT NULL,
                    PRIMARY KEY (tenant_id, idempotency_key),
                    UNIQUE (tenant_id, transaction_id)
                );
                """;
            await using var command = connection.CreateCommand();
            command.CommandText = schema;
            await command.ExecuteNonQueryAsync(ct);
            await EnsureColumnAsync(connection, "vyral_canonical_outbox", "max_delivery_attempts", "INTEGER NULL", ct);
            await EnsureColumnAsync(connection, "vyral_canonical_outbox", "dead_lettered_at_utc", "TEXT NULL", ct);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken ct)
    {
        await using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await info.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(ct);
    }

    private static async Task<CanonicalMigrationReceipt?> GetMigrationAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, checksum, description, applied_at_utc FROM vyral_canonical_migrations WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var identity = CanonicalMigrationIdentity.Parse(reader.GetString(0));
        return new CanonicalMigrationReceipt { Namespace = identity.Namespace, Id = identity.Id, Checksum = reader.GetString(1), Description = reader.IsDBNull(2) ? null : reader.GetString(2), AppliedAtUtc = ParseUtc(reader.GetString(3)) };
    }

    private static async Task<CanonicalDocument?> GetDocumentAsync(SqliteConnection connection, SqliteTransaction? transaction, string tenantId, string documentType, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT document_json FROM vyral_canonical_documents WHERE tenant_id = $tenant_id AND document_type = $document_type AND document_id = $document_id";
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        command.Parameters.AddWithValue("$document_type", documentType);
        command.Parameters.AddWithValue("$document_id", id);
        var value = await command.ExecuteScalarAsync(ct) as string;
        return value is null ? null : Deserialize<CanonicalDocument>(value);
    }

    private static async Task UpsertDocumentAsync(SqliteConnection connection, SqliteTransaction transaction, CanonicalDocument document, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_canonical_documents (tenant_id, document_type, document_id, revision, etag, deleted, document_json, created_at_utc, updated_at_utc)
            VALUES ($tenant_id, $document_type, $document_id, $revision, $etag, $deleted, $document_json, $created_at_utc, $updated_at_utc)
            ON CONFLICT(tenant_id, document_type, document_id) DO UPDATE SET
                revision = excluded.revision, etag = excluded.etag, deleted = excluded.deleted,
                document_json = excluded.document_json, updated_at_utc = excluded.updated_at_utc;
            """, ct,
            ("$tenant_id", document.TenantId),
            ("$document_type", document.DocumentType),
            ("$document_id", document.Id),
            ("$revision", document.Revision),
            ("$etag", document.Etag),
            ("$deleted", document.Deleted ? 1 : 0),
            ("$document_json", Serialize(document)),
            ("$created_at_utc", document.CreatedAtUtc.ToString("O")),
            ("$updated_at_utc", document.UpdatedAtUtc.ToString("O")));
    }

    private static async Task ReplaceDocumentIndexesAsync(SqliteConnection connection, SqliteTransaction transaction, CanonicalDocument document, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM vyral_canonical_document_indexes WHERE tenant_id = $tenant_id AND document_type = $document_type AND document_id = $document_id", ct,
            ("$tenant_id", document.TenantId), ("$document_type", document.DocumentType), ("$document_id", document.Id));
        if (document.Deleted) return;
        foreach (var (name, value) in document.Indexes)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_canonical_document_indexes (tenant_id, document_type, document_id, index_name, index_value)
                VALUES ($tenant_id, $document_type, $document_id, $index_name, $index_value);
                """, ct,
                ("$tenant_id", document.TenantId), ("$document_type", document.DocumentType), ("$document_id", document.Id),
                ("$index_name", name), ("$index_value", value));
        }
    }

    private static Task InsertRevisionAsync(SqliteConnection connection, SqliteTransaction transaction, CanonicalDocumentRevision revision, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_canonical_revisions (tenant_id, document_type, document_id, revision, transaction_id, revision_json, recorded_at_utc)
            VALUES ($tenant_id, $document_type, $document_id, $revision, $transaction_id, $revision_json, $recorded_at_utc);
            """, ct,
            ("$tenant_id", revision.TenantId), ("$document_type", revision.DocumentType), ("$document_id", revision.Id),
            ("$revision", revision.Revision), ("$transaction_id", revision.TransactionId), ("$revision_json", Serialize(revision)),
            ("$recorded_at_utc", revision.RecordedAtUtc.ToString("O")));

    private static async Task<CanonicalFence?> GetFenceAsync(SqliteConnection connection, SqliteTransaction transaction, string tenantId, string name, string value, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT owner_document_type, owner_document_id, created_at_utc, updated_at_utc FROM vyral_canonical_fences WHERE tenant_id = $tenant_id AND name = $name AND value = $value";
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new CanonicalFence { TenantId = tenantId, Name = name, Value = value, OwnerDocumentType = reader.GetString(0), OwnerDocumentId = reader.GetString(1), CreatedAtUtc = ParseUtc(reader.GetString(2)), UpdatedAtUtc = ParseUtc(reader.GetString(3)) }
            : null;
    }

    private static Task InsertFenceAsync(SqliteConnection connection, SqliteTransaction transaction, CanonicalFence fence, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_canonical_fences (tenant_id, name, value, owner_document_type, owner_document_id, created_at_utc, updated_at_utc)
            VALUES ($tenant_id, $name, $value, $owner_document_type, $owner_document_id, $created_at_utc, $updated_at_utc);
            """, ct,
            ("$tenant_id", fence.TenantId), ("$name", fence.Name), ("$value", fence.Value),
            ("$owner_document_type", fence.OwnerDocumentType), ("$owner_document_id", fence.OwnerDocumentId),
            ("$created_at_utc", fence.CreatedAtUtc.ToString("O")), ("$updated_at_utc", fence.UpdatedAtUtc.ToString("O")));

    private static Task InsertOutboxAsync(SqliteConnection connection, SqliteTransaction transaction, CanonicalOutboxEvent item, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_canonical_outbox (tenant_id, event_id, transaction_id, event_json, not_before_utc, delivery_count, delivered_at_utc, lease_owner, lease_token_hash, lease_expires_at_utc, last_error, max_delivery_attempts, dead_lettered_at_utc)
            VALUES ($tenant_id, $event_id, $transaction_id, $event_json, $not_before_utc, $delivery_count, $delivered_at_utc, $lease_owner, NULL, $lease_expires_at_utc, $last_error, $max_delivery_attempts, $dead_lettered_at_utc);
            """, ct,
            ("$tenant_id", item.TenantId), ("$event_id", item.Id), ("$transaction_id", item.TransactionId), ("$event_json", Serialize(item)),
            ("$not_before_utc", item.NotBeforeUtc?.ToString("O")), ("$delivery_count", item.DeliveryCount),
            ("$delivered_at_utc", item.DeliveredAtUtc?.ToString("O")), ("$lease_owner", item.LeaseOwner), ("$lease_expires_at_utc", item.LeaseExpiresAtUtc?.ToString("O")),
            ("$last_error", TrimError(item.LastError)), ("$max_delivery_attempts", item.MaxDeliveryAttempts), ("$dead_lettered_at_utc", item.DeadLetteredAtUtc?.ToString("O")));

    private static Task InsertTransactionReceiptAsync(SqliteConnection connection, SqliteTransaction transaction, CanonicalTransactionReceipt receipt, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_canonical_transactions (tenant_id, idempotency_key, transaction_id, request_hash, result_json, committed_at_utc)
            VALUES ($tenant_id, $idempotency_key, $transaction_id, $request_hash, $result_json, $committed_at_utc);
            """, ct,
            ("$tenant_id", receipt.TenantId), ("$idempotency_key", receipt.IdempotencyKey), ("$transaction_id", receipt.TransactionId),
            ("$request_hash", receipt.RequestHash), ("$result_json", Serialize(receipt.Result)), ("$committed_at_utc", receipt.CommittedAtUtc.ToString("O")));

    private static async Task<CanonicalTransactionReceipt?> GetTransactionReceiptAsync(SqliteConnection connection, SqliteTransaction transaction, string tenantId, string idempotencyKey, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT transaction_id, request_hash, result_json, committed_at_utc FROM vyral_canonical_transactions WHERE tenant_id = $tenant_id AND idempotency_key = $idempotency_key";
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new CanonicalTransactionReceipt
            {
                TenantId = tenantId,
                IdempotencyKey = idempotencyKey,
                TransactionId = reader.GetString(0),
                RequestHash = reader.GetString(1),
                Result = Deserialize<CanonicalTransactionResult>(reader.GetString(2)),
                CommittedAtUtc = ParseUtc(reader.GetString(3))
            }
            : null;
    }

    private static async Task<List<CanonicalOutboxEvent>> SelectLeaseableOutboxAsync(SqliteConnection connection, SqliteTransaction transaction, string tenantId, DateTime now, int limit, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_json, delivery_count, delivered_at_utc, lease_owner, lease_expires_at_utc, last_error, max_delivery_attempts, dead_lettered_at_utc
            FROM vyral_canonical_outbox
            WHERE tenant_id = $tenant_id AND delivered_at_utc IS NULL AND dead_lettered_at_utc IS NULL
              AND (not_before_utc IS NULL OR not_before_utc <= $now)
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= $now)
            ORDER BY COALESCE(not_before_utc, ''), event_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<CanonicalOutboxEvent>();
        while (await reader.ReadAsync(ct)) items.Add(ReadOutboxEvent(reader));
        return items;
    }

    private static CanonicalOutboxEvent ReadOutboxEvent(SqliteDataReader reader)
    {
        var item = Deserialize<CanonicalOutboxEvent>(reader.GetString(0));
        item.DeliveryCount = reader.GetInt32(1);
        item.DeliveredAtUtc = reader.IsDBNull(2) ? null : ParseUtc(reader.GetString(2));
        item.LeaseOwner = reader.IsDBNull(3) ? null : reader.GetString(3);
        item.LeaseExpiresAtUtc = reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4));
        item.LastError = reader.IsDBNull(5) ? null : reader.GetString(5);
        item.MaxDeliveryAttempts = reader.IsDBNull(6) ? null : reader.GetInt32(6);
        item.DeadLetteredAtUtc = reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7));
        return item;
    }

    private static async Task<List<T>> ReadJsonListAsync<T>(SqliteConnection connection, SqliteTransaction transaction, string sql, string tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<T>();
        while (await reader.ReadAsync(ct)) items.Add(Deserialize<T>(reader.GetString(0)));
        return items;
    }

    private static async Task<List<CanonicalFence>> ReadFencesAsync(SqliteConnection connection, SqliteTransaction transaction, string tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name, value, owner_document_type, owner_document_id, created_at_utc, updated_at_utc FROM vyral_canonical_fences WHERE tenant_id = $tenant_id ORDER BY name, value";
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<CanonicalFence>();
        while (await reader.ReadAsync(ct)) items.Add(new CanonicalFence { TenantId = tenantId, Name = reader.GetString(0), Value = reader.GetString(1), OwnerDocumentType = reader.GetString(2), OwnerDocumentId = reader.GetString(3), CreatedAtUtc = ParseUtc(reader.GetString(4)), UpdatedAtUtc = ParseUtc(reader.GetString(5)) });
        return items;
    }

    private static async Task<List<CanonicalOutboxEvent>> ReadOutboxAsync(SqliteConnection connection, SqliteTransaction transaction, string tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT event_json, delivery_count, delivered_at_utc, lease_owner, lease_expires_at_utc, last_error, max_delivery_attempts, dead_lettered_at_utc FROM vyral_canonical_outbox WHERE tenant_id = $tenant_id ORDER BY event_id";
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<CanonicalOutboxEvent>();
        while (await reader.ReadAsync(ct)) items.Add(ReadOutboxEvent(reader));
        return items;
    }

    private static async Task<List<CanonicalTransactionReceipt>> ReadTransactionsAsync(SqliteConnection connection, SqliteTransaction transaction, string tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT transaction_id, idempotency_key, request_hash, result_json, committed_at_utc FROM vyral_canonical_transactions WHERE tenant_id = $tenant_id ORDER BY transaction_id";
        command.Parameters.AddWithValue("$tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<CanonicalTransactionReceipt>();
        while (await reader.ReadAsync(ct)) items.Add(new CanonicalTransactionReceipt { TenantId = tenantId, TransactionId = reader.GetString(0), IdempotencyKey = reader.GetString(1), RequestHash = reader.GetString(2), Result = Deserialize<CanonicalTransactionResult>(reader.GetString(3)), CommittedAtUtc = ParseUtc(reader.GetString(4)) });
        return items;
    }

    private static async Task DeleteTenantAsync(SqliteConnection connection, SqliteTransaction transaction, string tenantId, CancellationToken ct)
    {
        foreach (var table in new[] { "vyral_canonical_document_indexes", "vyral_canonical_revisions", "vyral_canonical_fences", "vyral_canonical_outbox", "vyral_canonical_transactions", "vyral_canonical_documents" })
        {
            await ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE tenant_id = $tenant_id", ct, ("$tenant_id", tenantId));
        }
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException("Canonical store JSON could not be deserialized.");
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T Clone<T>(T value) => Deserialize<T>(Serialize(value));
    private static System.Text.Json.Nodes.JsonNode? CloneNode(System.Text.Json.Nodes.JsonNode? value) => value is null ? null : System.Text.Json.Nodes.JsonNode.Parse(value.ToJsonString(JsonOptions));
    private static DateTime ParseUtc(string value) => DateTime.SpecifyKind(DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(), DateTimeKind.Utc);
    private static string? TrimError(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 4096)];

    private static string EncodeContinuation(string documentType, string id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(documentType + "\n" + id));

    private static string EncodeOrderedContinuation(string orderValue, string documentType, string id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(orderValue + "\n" + documentType + "\n" + id));

    private static (string? OrderValue, string? DocumentType, string? Id) DecodeOrderedContinuation(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, null, null);
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(token)).Split('\n', 3);
            return parts.Length == 3 && parts.All(value => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl))
                ? (parts[0], parts[1], parts[2])
                : throw new FormatException();
        }
        catch (FormatException) { throw new InvalidOperationException("Canonical ordered document continuation token is not valid."); }
    }

    private static string EncodeOutboxContinuation(string eventId) => Convert.ToBase64String(Encoding.UTF8.GetBytes(eventId));

    private static string? DecodeOutboxContinuation(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var eventId = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            if (string.IsNullOrWhiteSpace(eventId) || eventId.Any(char.IsControl)) throw new InvalidOperationException();
            return eventId;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            throw new InvalidOperationException("Canonical outbox continuation token is invalid.", ex);
        }
    }
    private static (string? DocumentType, string? Id) DecodeContinuation(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, null);
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(token)).Split('\n', 2);
            return parts.Length == 2 && parts.All(value => !string.IsNullOrWhiteSpace(value)) ? (parts[0], parts[1]) : throw new FormatException();
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Canonical document continuation token is not valid.");
        }
    }
}
