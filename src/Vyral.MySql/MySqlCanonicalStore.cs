using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySqlConnector;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.MySql;

public sealed class MySqlCanonicalTenantStateLimitExceededException(
    string tenantId,
    long contentLength,
    long maxContentLength)
    : InvalidOperationException($"MySQL legacy canonical tenant '{tenantId}' state is {contentLength} bytes, exceeding the configured {maxContentLength}-byte import limit.")
{
    public string TenantId { get; } = tenantId;
    public long ContentLength { get; } = contentLength;
    public long MaxContentLength { get; } = maxContentLength;
}

/// <summary>
/// MySQL 8/InnoDB implementation of the strong CanonicalStore profile. Canonical entities are
/// stored as independently addressable rows; tenant mutations still serialize on the lifecycle
/// row so multi-entity commits, restores, and purges remain atomic. Legacy whole-tenant snapshot
/// rows are imported once, under the same tenant lock, and then removed.
/// </summary>
public sealed class MySqlCanonicalStore : ICanonicalStore
{
    public const long DefaultMaxLegacyTenantStateBytes = 16L * 1024 * 1024;
    public const long AbsoluteMaxLegacyTenantStateBytes = 1024L * 1024 * 1024;
    public const long DefaultMaxTenantStateBytes = DefaultMaxLegacyTenantStateBytes;
    public const long AbsoluteMaxTenantStateBytes = AbsoluteMaxLegacyTenantStateBytes;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly long _maxLegacyTenantStateBytes;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public MySqlCanonicalStore(string connectionString, long maxTenantStateBytes = DefaultMaxLegacyTenantStateBytes)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("MySQL connection string is required.", nameof(connectionString));
        if (maxTenantStateBytes is < 1 or > AbsoluteMaxLegacyTenantStateBytes)
            throw new ArgumentOutOfRangeException(nameof(maxTenantStateBytes), $"MySQL legacy canonical tenant-state limit must be between 1 and {AbsoluteMaxLegacyTenantStateBytes} bytes.");
        _connectionString = connectionString;
        _maxLegacyTenantStateBytes = maxTenantStateBytes;
    }

    public async Task ApplyMigrationsAsync(IReadOnlyList<CanonicalMigration> migrations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        foreach (var migration in migrations) CanonicalContractValidator.ValidateMigration(migration);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            foreach (var migration in migrations)
            {
                var existing = await GetMigrationAsync(connection, transaction, migration.Namespace.Trim(), migration.Id.Trim(), lockRow: true, ct);
                if (existing is not null)
                {
                    if (!string.Equals(existing.Checksum, migration.Checksum.Trim(), StringComparison.Ordinal))
                        throw new InvalidOperationException($"Canonical migration '{migration.Namespace}/{migration.Id}' already has a different checksum.");
                    continue;
                }
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO vyral_mysql_canonical_migrations (migration_namespace, migration_id, checksum, description, applied_at_utc)
                    VALUES (@namespace, @id, @checksum, @description, UTC_TIMESTAMP(6));
                    """, ct,
                    ("namespace", migration.Namespace.Trim()), ("id", migration.Id.Trim()),
                    ("checksum", migration.Checksum.Trim()), ("description", migration.Description?.Trim()));
            }
            await transaction.CommitAsync(ct);
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            await transaction.RollbackAsync(ct);
            throw new InvalidOperationException("Canonical migration conflicts with an existing migration receipt.", ex);
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
        command.CommandText = "SELECT migration_namespace, migration_id, checksum, description, applied_at_utc FROM vyral_mysql_canonical_migrations ORDER BY migration_namespace, migration_id";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<CanonicalMigrationReceipt>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CanonicalMigrationReceipt
            {
                Namespace = reader.GetString(0),
                Id = reader.GetString(1),
                Checksum = reader.GetString(2),
                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                AppliedAtUtc = reader.GetDateTime(4).ToUniversalTime()
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
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await AcquireTenantWriteLockAsync(connection, transaction, tenantId, ct);
            await ImportLegacyTenantAsync(connection, transaction, tenantId, ct);
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
            var result = new CanonicalTransactionResult { TransactionId = transactionId, TenantId = tenantId, IdempotencyKey = idempotencyKey, CorrelationId = request.CorrelationId?.Trim(), Actor = request.Actor?.Trim(), CommittedAtUtc = now };
            foreach (var mutation in request.Mutations)
            {
                var document = mutation.Operation == CanonicalMutationOperations.Upsert
                    ? await ApplyUpsertAsync(connection, transaction, tenantId, transactionId, mutation, now, ct)
                    : await ApplyDeleteAsync(connection, transaction, tenantId, transactionId, mutation, now, ct);
                result.Documents.Add(document);
            }
            foreach (var fence in request.Fences) await ApplyFenceAsync(connection, transaction, tenantId, fence, now, ct);
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
            await InsertTransactionReceiptAsync(connection, transaction, new CanonicalTransactionReceipt
            {
                TransactionId = transactionId,
                TenantId = tenantId,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                Result = Clone(result),
                CommittedAtUtc = now
            }, ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            await transaction.RollbackAsync(ct);
            throw new InvalidOperationException("Canonical transaction conflicts with an existing document, fence, outbox event, or idempotency receipt.", ex);
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
        await EnsureLegacyTenantImportedAsync(tenantId.Trim(), ct);
        await using var connection = await OpenAsync(ct);
        var document = await GetDocumentAsync(connection, null, tenantId.Trim(), documentType.Trim(), id.Trim(), lockForUpdate: false, ct);
        return document is not null && (!document.Deleted || includeDeleted) ? document : null;
    }

    public async Task<CanonicalDocumentQueryResult> QueryDocumentsAsync(CanonicalDocumentQuery query, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateQuery(query);
        await EnsureInitializedAsync(ct);
        var tenantId = query.TenantId.Trim();
        await EnsureLegacyTenantImportedAsync(tenantId, ct);
        var limit = query.Limit ?? 100;
        var hasIndexOrder = !string.IsNullOrWhiteSpace(query.OrderByIndex);
        var (lastOrderValue, lastType, lastId) = hasIndexOrder ? DecodeOrderedContinuation(query.ContinuationToken) : (null, null, null);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var predicates = new List<string> { "d.tenant_id = @tenant_id" };
        command.Parameters.AddWithValue("tenant_id", tenantId);
        if (!query.IncludeDeleted) predicates.Add("d.deleted = FALSE");
        if (!string.IsNullOrWhiteSpace(query.DocumentType))
        {
            predicates.Add("d.document_type = @document_type");
            command.Parameters.AddWithValue("document_type", query.DocumentType.Trim());
        }
        if (hasIndexOrder && lastOrderValue is not null)
        {
            var comparison = query.OrderDirection == CanonicalDocumentOrderDirections.Descending ? "<" : ">";
            predicates.Add($"(oi.index_value {comparison} @last_order_value OR (oi.index_value = @last_order_value AND (d.document_type {comparison} @last_type OR (d.document_type = @last_type AND d.document_id {comparison} @last_id))))");
            command.Parameters.AddWithValue("last_order_value", lastOrderValue);
            command.Parameters.AddWithValue("last_type", lastType!);
            command.Parameters.AddWithValue("last_id", lastId!);
        }
        else if (lastType is not null)
        {
            predicates.Add("(d.document_type > @last_type OR (d.document_type = @last_type AND d.document_id > @last_id))");
            command.Parameters.AddWithValue("last_type", lastType);
            command.Parameters.AddWithValue("last_id", lastId!);
        }
        var index = 0;
        foreach (var (name, value) in query.Indexes.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            predicates.Add($"EXISTS (SELECT 1 FROM vyral_mysql_canonical_document_indexes ci{index} WHERE ci{index}.tenant_id = d.tenant_id AND ci{index}.document_type = d.document_type AND ci{index}.document_id = d.document_id AND ci{index}.index_name = @index_name_{index} AND ci{index}.index_value = @index_value_{index})");
            command.Parameters.AddWithValue($"index_name_{index}", name);
            command.Parameters.AddWithValue($"index_value_{index}", value);
            index++;
        }
        if (query.IndexRange is not null)
        {
            var rangePredicates = new List<string> { "cir.tenant_id = d.tenant_id", "cir.document_type = d.document_type", "cir.document_id = d.document_id", "cir.index_name = @range_index_name" };
            command.Parameters.AddWithValue("range_index_name", query.IndexRange.Name.Trim());
            if (query.IndexRange.GreaterThanOrEqual is not null)
            {
                rangePredicates.Add("cir.index_value >= @range_lower");
                command.Parameters.AddWithValue("range_lower", query.IndexRange.GreaterThanOrEqual);
            }
            if (query.IndexRange.LessThanOrEqual is not null)
            {
                rangePredicates.Add("cir.index_value <= @range_upper");
                command.Parameters.AddWithValue("range_upper", query.IndexRange.LessThanOrEqual);
            }
            predicates.Add($"EXISTS (SELECT 1 FROM vyral_mysql_canonical_document_indexes cir WHERE {string.Join(" AND ", rangePredicates)})");
        }
        command.Parameters.AddWithValue("limit", limit + 1);
        var join = string.Empty;
        var orderBy = "d.document_type, d.document_id";
        var select = "d.document_json";
        if (hasIndexOrder)
        {
            join = " INNER JOIN vyral_mysql_canonical_document_indexes oi ON oi.tenant_id = d.tenant_id AND oi.document_type = d.document_type AND oi.document_id = d.document_id AND oi.index_name = @order_index_name";
            command.Parameters.AddWithValue("order_index_name", query.OrderByIndex!.Trim());
            var direction = query.OrderDirection == CanonicalDocumentOrderDirections.Descending ? "DESC" : "ASC";
            orderBy = $"oi.index_value {direction}, d.document_type {direction}, d.document_id {direction}";
            select = "d.document_json, oi.index_value";
        }
        command.CommandText = $"SELECT {select} FROM vyral_mysql_canonical_documents d{join} WHERE {string.Join(" AND ", predicates)} ORDER BY {orderBy} LIMIT @limit";
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
        await EnsureLegacyTenantImportedAsync(tenantId.Trim(), ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision_json FROM vyral_mysql_canonical_revisions
            WHERE tenant_id = @tenant_id AND document_type = @document_type AND document_id = @document_id
            ORDER BY revision DESC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("tenant_id", tenantId.Trim());
        command.Parameters.AddWithValue("document_type", documentType.Trim());
        command.Parameters.AddWithValue("document_id", id.Trim());
        command.Parameters.AddWithValue("limit", limit);
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
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await AcquireTenantWriteLockAsync(connection, transaction, request.TenantId.Trim(), ct);
            await ImportLegacyTenantAsync(connection, transaction, request.TenantId.Trim(), ct);
            var candidates = await SelectLeaseableOutboxAsync(connection, transaction, request.TenantId.Trim(), now, request.MaxItems, ct);
            var leases = new List<CanonicalOutboxLease>();
            foreach (var candidate in candidates)
            {
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var changed = await ExecuteAsync(connection, transaction, """
                    UPDATE vyral_mysql_canonical_outbox
                    SET lease_owner = @lease_owner, lease_token_hash = @lease_token_hash,
                        lease_expires_at_utc = @lease_expires_at_utc, delivery_count = delivery_count + 1
                    WHERE tenant_id = @tenant_id AND event_id = @event_id AND delivered_at_utc IS NULL
                      AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= @now);
                    """, ct,
                    ("lease_owner", request.ConsumerId.Trim()), ("lease_token_hash", CanonicalTransactionHasher.HashLeaseToken(token)),
                    ("lease_expires_at_utc", expiresAt.ToString("O")), ("tenant_id", request.TenantId.Trim()), ("event_id", candidate.Id), ("now", now.ToString("O")));
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
        await EnsureLegacyTenantImportedAsync(query.TenantId.Trim(), ct);
        var now = DateTime.UtcNow;
        var limit = query.Limit ?? 100;
        var lastEventId = DecodeOutboxContinuation(query.ContinuationToken);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var predicates = new List<string> { "tenant_id = @tenant_id" };
        command.Parameters.AddWithValue("tenant_id", query.TenantId.Trim());
        if (lastEventId is not null)
        {
            predicates.Add("event_id > @event_id");
            command.Parameters.AddWithValue("event_id", lastEventId);
        }
        if (!string.IsNullOrWhiteSpace(query.Topic))
        {
            predicates.Add("JSON_UNQUOTE(JSON_EXTRACT(event_json, '$.topic')) = @topic");
            command.Parameters.AddWithValue("topic", query.Topic.Trim());
        }
        switch (query.State)
        {
            case CanonicalOutboxStates.Ready:
                predicates.Add("delivered_at_utc IS NULL AND dead_lettered_at_utc IS NULL AND (not_before_utc IS NULL OR not_before_utc <= @now) AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= @now)");
                break;
            case CanonicalOutboxStates.Leased:
                predicates.Add("delivered_at_utc IS NULL AND dead_lettered_at_utc IS NULL AND lease_expires_at_utc > @now");
                break;
            case CanonicalOutboxStates.Scheduled:
                predicates.Add("delivered_at_utc IS NULL AND dead_lettered_at_utc IS NULL AND not_before_utc > @now AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= @now)");
                break;
            case CanonicalOutboxStates.Delivered:
                predicates.Add("delivered_at_utc IS NOT NULL");
                break;
            case CanonicalOutboxStates.DeadLetter:
                predicates.Add("dead_lettered_at_utc IS NOT NULL");
                break;
        }
        command.Parameters.AddWithValue("now", now.ToString("O"));
        command.Parameters.AddWithValue("limit", limit + 1);
        command.CommandText = $"SELECT event_json, delivery_count, delivered_at_utc, lease_owner, lease_expires_at_utc, last_error, max_delivery_attempts, dead_lettered_at_utc FROM vyral_mysql_canonical_outbox WHERE {string.Join(" AND ", predicates)} ORDER BY event_id LIMIT @limit";
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
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await AcquireTenantWriteLockAsync(connection, transaction, tenantId.Trim(), ct);
            await ImportLegacyTenantAsync(connection, transaction, tenantId.Trim(), ct);
            var changed = await ExecuteAsync(connection, transaction, """
                UPDATE vyral_mysql_canonical_outbox
                SET delivered_at_utc = COALESCE(delivered_at_utc, @now), lease_owner = NULL, lease_expires_at_utc = NULL
                WHERE tenant_id = @tenant_id AND event_id = @event_id AND lease_token_hash = @lease_token_hash
                  AND (delivered_at_utc IS NOT NULL OR lease_expires_at_utc > @now);
                """, ct,
                ("now", DateTime.UtcNow.ToString("O")), ("tenant_id", tenantId.Trim()), ("event_id", eventId.Trim()), ("lease_token_hash", CanonicalTransactionHasher.HashLeaseToken(leaseToken)));
            if (changed == 0) throw new InvalidOperationException("Canonical outbox lease is not active for this acknowledgement.");
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<CanonicalOutboxLeaseRenewal> RenewOutboxLeaseAsync(CanonicalOutboxLeaseRenewalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CanonicalContractValidator.ValidateOutboxLeaseRenewal(request);
        await EnsureInitializedAsync(ct);
        var now = DateTime.UtcNow;
        var expiresAt = now.AddSeconds(request.LeaseSeconds);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await AcquireTenantWriteLockAsync(connection, transaction, request.TenantId.Trim(), ct);
            await ImportLegacyTenantAsync(connection, transaction, request.TenantId.Trim(), ct);
            var changed = await ExecuteAsync(connection, transaction, """
                UPDATE vyral_mysql_canonical_outbox
                SET lease_expires_at_utc = @lease_expires_at_utc
                WHERE tenant_id = @tenant_id AND event_id = @event_id AND delivered_at_utc IS NULL
                  AND lease_token_hash = @lease_token_hash AND lease_expires_at_utc > @now;
                """, ct,
                ("lease_expires_at_utc", expiresAt.ToString("O")), ("tenant_id", request.TenantId.Trim()),
                ("event_id", request.EventId.Trim()), ("lease_token_hash", CanonicalTransactionHasher.HashLeaseToken(request.LeaseToken)), ("now", now.ToString("O")));
            if (changed == 0) throw new InvalidOperationException("Canonical outbox lease is not active for this renewal.");
            await transaction.CommitAsync(ct);
            return new CanonicalOutboxLeaseRenewal { ExpiresAtUtc = expiresAt };
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task NackOutboxAsync(CanonicalOutboxNackRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CanonicalContractValidator.ValidateOutboxNack(request);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var now = DateTime.UtcNow;
        var notBefore = request.NotBeforeUtc?.ToUniversalTime() ?? now.AddSeconds(request.RetryAfterSeconds ?? CanonicalContractValidator.DefaultOutboxRetryDelaySeconds);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await AcquireTenantWriteLockAsync(connection, transaction, request.TenantId.Trim(), ct);
            await ImportLegacyTenantAsync(connection, transaction, request.TenantId.Trim(), ct);
            var changed = await ExecuteAsync(connection, transaction, """
                UPDATE vyral_mysql_canonical_outbox
                SET lease_owner = NULL, lease_token_hash = NULL, lease_expires_at_utc = NULL,
                    not_before_utc = CASE WHEN max_delivery_attempts IS NOT NULL AND delivery_count >= max_delivery_attempts THEN NULL ELSE @not_before_utc END,
                    dead_lettered_at_utc = CASE WHEN max_delivery_attempts IS NOT NULL AND delivery_count >= max_delivery_attempts THEN @now ELSE dead_lettered_at_utc END,
                    last_error = @last_error
                WHERE tenant_id = @tenant_id AND event_id = @event_id AND delivered_at_utc IS NULL
                  AND lease_token_hash = @lease_token_hash AND lease_expires_at_utc > @now;
                """, ct,
                ("not_before_utc", notBefore.ToString("O")), ("last_error", TrimError(request.Error)),
                ("tenant_id", request.TenantId.Trim()), ("event_id", request.EventId.Trim()), ("lease_token_hash", CanonicalTransactionHasher.HashLeaseToken(request.LeaseToken)), ("now", now.ToString("O")));
            if (changed == 0) throw new InvalidOperationException("Canonical outbox lease is not active for this release.");
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task ReplayOutboxAsync(CanonicalOutboxReplayRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CanonicalContractValidator.ValidateOutboxReplay(request);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await AcquireTenantWriteLockAsync(connection, transaction, request.TenantId.Trim(), ct);
            await ImportLegacyTenantAsync(connection, transaction, request.TenantId.Trim(), ct);
            var changed = await ExecuteAsync(connection, transaction, """
                UPDATE vyral_mysql_canonical_outbox
                SET lease_owner = NULL, lease_token_hash = NULL, lease_expires_at_utc = NULL,
                    not_before_utc = @now, dead_lettered_at_utc = NULL, last_error = NULL,
                    delivery_count = CASE WHEN @reset_delivery_count THEN 0 ELSE delivery_count END
                WHERE tenant_id = @tenant_id AND event_id = @event_id AND dead_lettered_at_utc IS NOT NULL;
                """, ct,
                ("now", DateTime.UtcNow.ToString("O")), ("reset_delivery_count", request.ResetDeliveryCount),
                ("tenant_id", request.TenantId.Trim()), ("event_id", request.EventId.Trim()));
            if (changed == 0) throw new InvalidOperationException("Canonical outbox event is not dead-lettered for this replay.");
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
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
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        try
        {
            await AcquireTenantWriteLockAsync(connection, transaction, tenant, ct);
            await ImportLegacyTenantAsync(connection, transaction, tenant, ct);
            var snapshot = new CanonicalTenantSnapshot
            {
                TenantId = tenant,
                Generation = await ReadTenantGenerationAsync(connection, transaction, tenant, ct),
                Documents = await ReadJsonListAsync<CanonicalDocument>(connection, transaction, "SELECT document_json FROM vyral_mysql_canonical_documents WHERE tenant_id = @tenant_id ORDER BY document_type, document_id", tenant, ct),
                Revisions = await ReadJsonListAsync<CanonicalDocumentRevision>(connection, transaction, "SELECT revision_json FROM vyral_mysql_canonical_revisions WHERE tenant_id = @tenant_id ORDER BY document_type, document_id, revision", tenant, ct),
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

    public async Task<CanonicalTenantPurgeReceipt> PurgeTenantAsync(CanonicalTenantPurgeRequest request, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateTenantPurge(request);
        await EnsureInitializedAsync(ct);
        var tenantId = request.TenantId.Trim();
        var idempotencyKey = request.IdempotencyKey.Trim();
        var requestHash = CanonicalTenantPurgeHasher.ComputeRequestHash(request);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await AcquireTenantWriteLockAsync(connection, transaction, tenantId, ct);
            await ImportLegacyTenantAsync(connection, transaction, tenantId, ct);
            var existing = await ReadPurgeReceiptAsync(connection, transaction, tenantId, idempotencyKey, ct);
            if (existing is not null)
            {
                if (!string.Equals(existing.Value.RequestHash, requestHash, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Canonical tenant purge idempotency key '{idempotencyKey}' already belongs to a different request.");
                var replay = existing.Value.Receipt;
                replay.Replayed = true;
                await transaction.CommitAsync(ct);
                return replay;
            }

            await EnsureTenantLifecycleAsync(connection, transaction, tenantId, ct);
            var generation = await ReadTenantGenerationAsync(connection, transaction, tenantId, ct);
            if (generation != request.ExpectedGeneration)
                throw new InvalidOperationException($"Canonical tenant purge generation mismatch. Expected {request.ExpectedGeneration}, actual {generation}.");
            await using (var leaseCommand = connection.CreateCommand())
            {
                leaseCommand.Transaction = transaction;
                leaseCommand.CommandText = "SELECT COUNT(*) FROM vyral_mysql_canonical_outbox WHERE tenant_id = @tenant_id AND delivered_at_utc IS NULL AND lease_expires_at_utc > @now";
                leaseCommand.Parameters.AddWithValue("tenant_id", tenantId);
                leaseCommand.Parameters.AddWithValue("now", DateTime.UtcNow.ToString("O"));
                if (Convert.ToInt64(await leaseCommand.ExecuteScalarAsync(ct)) > 0)
                    throw new InvalidOperationException("Canonical tenant purge is blocked while an outbox lease is active.");
            }

            var snapshot = new CanonicalTenantSnapshot
            {
                TenantId = tenantId,
                Generation = generation,
                Documents = await ReadJsonListAsync<CanonicalDocument>(connection, transaction, "SELECT document_json FROM vyral_mysql_canonical_documents WHERE tenant_id = @tenant_id ORDER BY document_type, document_id", tenantId, ct),
                Revisions = await ReadJsonListAsync<CanonicalDocumentRevision>(connection, transaction, "SELECT revision_json FROM vyral_mysql_canonical_revisions WHERE tenant_id = @tenant_id ORDER BY document_type, document_id, revision", tenantId, ct),
                Fences = await ReadFencesAsync(connection, transaction, tenantId, ct),
                Outbox = await ReadOutboxAsync(connection, transaction, tenantId, ct),
                Transactions = await ReadTransactionsAsync(connection, transaction, tenantId, ct),
                ExportedAtUtc = DateTime.UtcNow
            };
            snapshot.ContentHash = CanonicalSnapshotHasher.Compute(snapshot);
            if (!string.Equals(snapshot.ContentHash, request.ExpectedContentHash.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("Canonical tenant purge content hash does not match current tenant state.");

            var nextGeneration = checked(generation + 1);
            await DeleteTenantAsync(connection, transaction, tenantId, ct);
            await ExecuteAsync(connection, transaction,
                "UPDATE vyral_mysql_canonical_tenant_lifecycle SET generation = @generation, updated_at_utc = @updated_at_utc WHERE tenant_id = @tenant_id", ct,
                ("generation", nextGeneration), ("updated_at_utc", DateTime.UtcNow), ("tenant_id", tenantId));
            var receipt = new CanonicalTenantPurgeReceipt
            {
                TenantId = tenantId,
                IdempotencyKey = idempotencyKey,
                PurgedGeneration = generation,
                Generation = nextGeneration,
                PurgedContentHash = snapshot.ContentHash,
                PurgedAtUtc = DateTime.UtcNow
            };
            await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_mysql_canonical_tenant_purges
                    (tenant_id, idempotency_key, request_hash, receipt_json, purged_at_utc)
                VALUES (@tenant_id, @idempotency_key, @request_hash, @receipt_json, @purged_at_utc)
                """, ct,
                ("tenant_id", tenantId), ("idempotency_key", idempotencyKey), ("request_hash", requestHash),
                ("receipt_json", Serialize(receipt)), ("purged_at_utc", receipt.PurgedAtUtc));
            await transaction.CommitAsync(ct);
            return receipt;
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
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await AcquireTenantWriteLockAsync(connection, transaction, tenantId, ct);
            await ImportLegacyTenantAsync(connection, transaction, tenantId, ct);
            await EnsureTenantLifecycleAsync(connection, transaction, tenantId, ct);
            var currentGeneration = await ReadTenantGenerationAsync(connection, transaction, tenantId, ct);
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
            var nextGeneration = checked(Math.Max(currentGeneration, request.Snapshot.Generation) + 1);
            await ExecuteAsync(connection, transaction,
                "UPDATE vyral_mysql_canonical_tenant_lifecycle SET generation = @generation, updated_at_utc = @updated_at_utc WHERE tenant_id = @tenant_id", ct,
                ("generation", nextGeneration), ("updated_at_utc", DateTime.UtcNow), ("tenant_id", tenantId));
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<CanonicalDocument> ApplyUpsertAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, string transactionId, CanonicalMutation mutation, DateTime now, CancellationToken ct)
    {
        var source = mutation.Document!;
        var existing = await GetDocumentAsync(connection, transaction, tenantId, source.DocumentType.Trim(), source.Id.Trim(), lockForUpdate: true, ct);
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

    private async Task<CanonicalDocument> ApplyDeleteAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, string transactionId, CanonicalMutation mutation, DateTime now, CancellationToken ct)
    {
        var (documentType, id) = CanonicalContractValidator.MutationKey(mutation);
        var existing = await GetDocumentAsync(connection, transaction, tenantId, documentType, id, lockForUpdate: true, ct);
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

    private async Task ApplyFenceAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, CanonicalFenceMutation mutation, DateTime now, CancellationToken ct)
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
        await ExecuteAsync(connection, transaction, "DELETE FROM vyral_mysql_canonical_fences WHERE tenant_id = @tenant_id AND name = @name AND value = @value", ct,
            ("tenant_id", tenantId), ("name", mutation.Name.Trim()), ("value", mutation.Value.Trim()));
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_migrations (
                    migration_namespace VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    migration_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    checksum VARCHAR(255) NOT NULL, description TEXT NULL, applied_at_utc DATETIME(6) NOT NULL,
                    PRIMARY KEY (migration_namespace, migration_id)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_tenants (
                    tenant_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    state_json JSON NOT NULL, updated_at_utc DATETIME(6) NOT NULL,
                    PRIMARY KEY (tenant_id)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_documents (
                    tenant_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    document_type VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    document_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    revision BIGINT NOT NULL, etag VARCHAR(255) NOT NULL, deleted BOOLEAN NOT NULL,
                    document_json LONGTEXT NOT NULL, created_at_utc VARCHAR(40) NOT NULL, updated_at_utc VARCHAR(40) NOT NULL,
                    PRIMARY KEY (tenant_id, document_type, document_id)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_document_indexes (
                    tenant_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    document_type VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    document_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    index_name VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    index_value TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    PRIMARY KEY (tenant_id, document_type, document_id, index_name),
                    KEY ix_vyral_mysql_canonical_document_indexes_lookup
                        (tenant_id, document_type, index_name, index_value(128)),
                    FOREIGN KEY (tenant_id, document_type, document_id)
                        REFERENCES vyral_mysql_canonical_documents(tenant_id, document_type, document_id) ON DELETE CASCADE
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_revisions (
                    tenant_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    document_type VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    document_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    revision BIGINT NOT NULL, transaction_id VARCHAR(160) NOT NULL,
                    revision_json LONGTEXT NOT NULL, recorded_at_utc VARCHAR(40) NOT NULL,
                    PRIMARY KEY (tenant_id, document_type, document_id, revision)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_fences (
                    tenant_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    name VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    value VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    owner_document_type VARCHAR(160) NOT NULL, owner_document_id VARCHAR(160) NOT NULL,
                    created_at_utc VARCHAR(40) NOT NULL, updated_at_utc VARCHAR(40) NOT NULL,
                    PRIMARY KEY (tenant_id, name, value)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_outbox (
                    tenant_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    event_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    transaction_id VARCHAR(160) NOT NULL, event_json LONGTEXT NOT NULL,
                    not_before_utc VARCHAR(40) NULL, delivery_count INTEGER NOT NULL, delivered_at_utc VARCHAR(40) NULL,
                    lease_owner VARCHAR(160) NULL, lease_token_hash VARCHAR(71) NULL, lease_expires_at_utc VARCHAR(40) NULL,
                    last_error TEXT NULL, max_delivery_attempts INTEGER NULL, dead_lettered_at_utc VARCHAR(40) NULL,
                    PRIMARY KEY (tenant_id, event_id),
                    KEY ix_vyral_mysql_canonical_outbox_due
                        (tenant_id, delivered_at_utc, not_before_utc, lease_expires_at_utc, event_id)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_transactions (
                    tenant_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    idempotency_key VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    transaction_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    request_hash VARCHAR(255) NOT NULL, result_json LONGTEXT NOT NULL, committed_at_utc VARCHAR(40) NOT NULL,
                    PRIMARY KEY (tenant_id, idempotency_key), UNIQUE (tenant_id, transaction_id)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_tenant_lifecycle (
                    tenant_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    generation BIGINT NOT NULL, updated_at_utc DATETIME(6) NOT NULL,
                    PRIMARY KEY (tenant_id)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_tenant_purges (
                    tenant_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    idempotency_key VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    request_hash VARCHAR(71) NOT NULL, receipt_json JSON NOT NULL, purged_at_utc DATETIME(6) NOT NULL,
                    PRIMARY KEY (tenant_id, idempotency_key)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                """;
            await command.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally { _initializationGate.Release(); }
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<CanonicalMigrationReceipt?> GetMigrationAsync(
        MySqlConnection connection, MySqlTransaction transaction, string @namespace, string id, bool lockRow, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT checksum, description, applied_at_utc FROM vyral_mysql_canonical_migrations WHERE migration_namespace = @namespace AND migration_id = @id{(lockRow ? " FOR UPDATE" : string.Empty)}";
        command.Parameters.AddWithValue("namespace", @namespace);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new CanonicalMigrationReceipt
        {
            Namespace = @namespace,
            Id = id,
            Checksum = reader.GetString(0),
            Description = reader.IsDBNull(1) ? null : reader.GetString(1),
            AppliedAtUtc = reader.GetDateTime(2).ToUniversalTime()
        };
    }

    private static async Task<CanonicalDocument?> GetDocumentAsync(MySqlConnection connection, MySqlTransaction? transaction, string tenantId, string documentType, string id, bool lockForUpdate, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT document_json FROM vyral_mysql_canonical_documents WHERE tenant_id = @tenant_id AND document_type = @document_type AND document_id = @document_id{(lockForUpdate ? " FOR UPDATE" : string.Empty)}";
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("document_type", documentType);
        command.Parameters.AddWithValue("document_id", id);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Deserialize<CanonicalDocument>(value.ToString()!);
    }

    private static Task UpsertDocumentAsync(MySqlConnection connection, MySqlTransaction transaction, CanonicalDocument document, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_mysql_canonical_documents (tenant_id, document_type, document_id, revision, etag, deleted, document_json, created_at_utc, updated_at_utc)
            VALUES (@tenant_id, @document_type, @document_id, @revision, @etag, @deleted, @document_json, @created_at_utc, @updated_at_utc)
            ON DUPLICATE KEY UPDATE
                revision = VALUES(revision), etag = VALUES(etag), deleted = VALUES(deleted),
                document_json = VALUES(document_json), updated_at_utc = VALUES(updated_at_utc);
            """, ct,
            ("tenant_id", document.TenantId), ("document_type", document.DocumentType), ("document_id", document.Id), ("revision", document.Revision),
            ("etag", document.Etag), ("deleted", document.Deleted), ("document_json", Serialize(document)),
            ("created_at_utc", document.CreatedAtUtc.ToString("O")), ("updated_at_utc", document.UpdatedAtUtc.ToString("O")));

    private static async Task ReplaceDocumentIndexesAsync(MySqlConnection connection, MySqlTransaction transaction, CanonicalDocument document, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM vyral_mysql_canonical_document_indexes WHERE tenant_id = @tenant_id AND document_type = @document_type AND document_id = @document_id", ct,
            ("tenant_id", document.TenantId), ("document_type", document.DocumentType), ("document_id", document.Id));
        if (document.Deleted) return;
        foreach (var (name, value) in document.Indexes)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_mysql_canonical_document_indexes (tenant_id, document_type, document_id, index_name, index_value)
                VALUES (@tenant_id, @document_type, @document_id, @index_name, @index_value);
                """, ct,
                ("tenant_id", document.TenantId), ("document_type", document.DocumentType), ("document_id", document.Id), ("index_name", name), ("index_value", value));
        }
    }

    private static Task InsertRevisionAsync(MySqlConnection connection, MySqlTransaction transaction, CanonicalDocumentRevision revision, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_mysql_canonical_revisions (tenant_id, document_type, document_id, revision, transaction_id, revision_json, recorded_at_utc)
            VALUES (@tenant_id, @document_type, @document_id, @revision, @transaction_id, @revision_json, @recorded_at_utc);
            """, ct,
            ("tenant_id", revision.TenantId), ("document_type", revision.DocumentType), ("document_id", revision.Id), ("revision", revision.Revision),
            ("transaction_id", revision.TransactionId), ("revision_json", Serialize(revision)), ("recorded_at_utc", revision.RecordedAtUtc.ToString("O")));

    private static async Task<CanonicalFence?> GetFenceAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, string name, string value, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT owner_document_type, owner_document_id, created_at_utc, updated_at_utc FROM vyral_mysql_canonical_fences WHERE tenant_id = @tenant_id AND name = @name AND value = @value FOR UPDATE";
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new CanonicalFence { TenantId = tenantId, Name = name, Value = value, OwnerDocumentType = reader.GetString(0), OwnerDocumentId = reader.GetString(1), CreatedAtUtc = ParseUtc(reader.GetString(2)), UpdatedAtUtc = ParseUtc(reader.GetString(3)) } : null;
    }

    private static Task InsertFenceAsync(MySqlConnection connection, MySqlTransaction transaction, CanonicalFence fence, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_mysql_canonical_fences (tenant_id, name, value, owner_document_type, owner_document_id, created_at_utc, updated_at_utc)
            VALUES (@tenant_id, @name, @value, @owner_document_type, @owner_document_id, @created_at_utc, @updated_at_utc);
            """, ct,
            ("tenant_id", fence.TenantId), ("name", fence.Name), ("value", fence.Value), ("owner_document_type", fence.OwnerDocumentType),
            ("owner_document_id", fence.OwnerDocumentId), ("created_at_utc", fence.CreatedAtUtc.ToString("O")), ("updated_at_utc", fence.UpdatedAtUtc.ToString("O")));

    private static Task InsertOutboxAsync(MySqlConnection connection, MySqlTransaction transaction, CanonicalOutboxEvent item, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_mysql_canonical_outbox (tenant_id, event_id, transaction_id, event_json, not_before_utc, delivery_count, delivered_at_utc, lease_owner, lease_token_hash, lease_expires_at_utc, last_error, max_delivery_attempts, dead_lettered_at_utc)
            VALUES (@tenant_id, @event_id, @transaction_id, @event_json, @not_before_utc, @delivery_count, @delivered_at_utc, @lease_owner, NULL, @lease_expires_at_utc, @last_error, @max_delivery_attempts, @dead_lettered_at_utc);
            """, ct,
            ("tenant_id", item.TenantId), ("event_id", item.Id), ("transaction_id", item.TransactionId), ("event_json", Serialize(item)),
            ("not_before_utc", item.NotBeforeUtc?.ToString("O")), ("delivery_count", item.DeliveryCount), ("delivered_at_utc", item.DeliveredAtUtc?.ToString("O")),
            ("lease_owner", item.LeaseOwner), ("lease_expires_at_utc", item.LeaseExpiresAtUtc?.ToString("O")),
            ("last_error", TrimError(item.LastError)), ("max_delivery_attempts", item.MaxDeliveryAttempts), ("dead_lettered_at_utc", item.DeadLetteredAtUtc?.ToString("O")));

    private static Task InsertTransactionReceiptAsync(MySqlConnection connection, MySqlTransaction transaction, CanonicalTransactionReceipt receipt, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_mysql_canonical_transactions (tenant_id, idempotency_key, transaction_id, request_hash, result_json, committed_at_utc)
            VALUES (@tenant_id, @idempotency_key, @transaction_id, @request_hash, @result_json, @committed_at_utc);
            """, ct,
            ("tenant_id", receipt.TenantId), ("idempotency_key", receipt.IdempotencyKey), ("transaction_id", receipt.TransactionId),
            ("request_hash", receipt.RequestHash), ("result_json", Serialize(receipt.Result)), ("committed_at_utc", receipt.CommittedAtUtc.ToString("O")));

    private static async Task<CanonicalTransactionReceipt?> GetTransactionReceiptAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, string idempotencyKey, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT transaction_id, request_hash, result_json, committed_at_utc FROM vyral_mysql_canonical_transactions WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key";
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new CanonicalTransactionReceipt { TenantId = tenantId, IdempotencyKey = idempotencyKey, TransactionId = reader.GetString(0), RequestHash = reader.GetString(1), Result = Deserialize<CanonicalTransactionResult>(reader.GetString(2)), CommittedAtUtc = ParseUtc(reader.GetString(3)) }
            : null;
    }

    private static async Task<List<CanonicalOutboxEvent>> SelectLeaseableOutboxAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, DateTime now, int limit, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_json, delivery_count, delivered_at_utc, lease_owner, lease_expires_at_utc, last_error, max_delivery_attempts, dead_lettered_at_utc
            FROM vyral_mysql_canonical_outbox
            WHERE tenant_id = @tenant_id AND delivered_at_utc IS NULL AND dead_lettered_at_utc IS NULL
              AND (not_before_utc IS NULL OR not_before_utc <= @now)
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= @now)
            ORDER BY COALESCE(not_before_utc, ''), event_id LIMIT @limit
            FOR UPDATE SKIP LOCKED;
            """;
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("now", now.ToString("O"));
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<CanonicalOutboxEvent>();
        while (await reader.ReadAsync(ct)) items.Add(ReadOutboxEvent(reader));
        return items;
    }

    private static CanonicalOutboxEvent ReadOutboxEvent(MySqlDataReader reader)
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

    private static async Task<List<T>> ReadJsonListAsync<T>(MySqlConnection connection, MySqlTransaction transaction, string sql, string tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<T>();
        while (await reader.ReadAsync(ct)) items.Add(Deserialize<T>(reader.GetString(0)));
        return items;
    }

    private static async Task<List<CanonicalFence>> ReadFencesAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name, value, owner_document_type, owner_document_id, created_at_utc, updated_at_utc FROM vyral_mysql_canonical_fences WHERE tenant_id = @tenant_id ORDER BY name, value";
        command.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<CanonicalFence>();
        while (await reader.ReadAsync(ct)) items.Add(new CanonicalFence { TenantId = tenantId, Name = reader.GetString(0), Value = reader.GetString(1), OwnerDocumentType = reader.GetString(2), OwnerDocumentId = reader.GetString(3), CreatedAtUtc = ParseUtc(reader.GetString(4)), UpdatedAtUtc = ParseUtc(reader.GetString(5)) });
        return items;
    }

    private static async Task<List<CanonicalOutboxEvent>> ReadOutboxAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT event_json, delivery_count, delivered_at_utc, lease_owner, lease_expires_at_utc, last_error, max_delivery_attempts, dead_lettered_at_utc FROM vyral_mysql_canonical_outbox WHERE tenant_id = @tenant_id ORDER BY event_id";
        command.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<CanonicalOutboxEvent>();
        while (await reader.ReadAsync(ct)) items.Add(ReadOutboxEvent(reader));
        return items;
    }

    private static async Task<List<CanonicalTransactionReceipt>> ReadTransactionsAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT transaction_id, idempotency_key, request_hash, result_json, committed_at_utc FROM vyral_mysql_canonical_transactions WHERE tenant_id = @tenant_id ORDER BY transaction_id";
        command.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<CanonicalTransactionReceipt>();
        while (await reader.ReadAsync(ct)) items.Add(new CanonicalTransactionReceipt { TenantId = tenantId, TransactionId = reader.GetString(0), IdempotencyKey = reader.GetString(1), RequestHash = reader.GetString(2), Result = Deserialize<CanonicalTransactionResult>(reader.GetString(3)), CommittedAtUtc = ParseUtc(reader.GetString(4)) });
        return items;
    }

    private static async Task DeleteTenantAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, CancellationToken ct)
    {
        foreach (var table in new[] { "vyral_mysql_canonical_document_indexes", "vyral_mysql_canonical_revisions", "vyral_mysql_canonical_fences", "vyral_mysql_canonical_outbox", "vyral_mysql_canonical_transactions", "vyral_mysql_canonical_documents" })
            await ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE tenant_id = @tenant_id", ct, ("tenant_id", tenantId));
    }

    /// <summary>
    /// Serializes every tenant mutation, including restore and one-time legacy import. The
    /// lifecycle row exists independently of tenant content, so it also protects first writes
    /// and replace-all operations where no document row exists yet.
    /// </summary>
    private static async Task AcquireTenantWriteLockAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, CancellationToken ct)
    {
        await EnsureTenantLifecycleAsync(connection, transaction, tenantId, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT generation FROM vyral_mysql_canonical_tenant_lifecycle WHERE tenant_id = @tenant_id FOR UPDATE";
        command.Parameters.AddWithValue("tenant_id", tenantId);
        _ = await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Canonical tenant lifecycle lock could not be acquired.");
    }

    private async Task EnsureLegacyTenantImportedAsync(string tenantId, CancellationToken ct)
    {
        await using (var connection = await OpenAsync(ct))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1 FROM vyral_mysql_canonical_tenants WHERE tenant_id = @tenant_id LIMIT 1";
            command.Parameters.AddWithValue("tenant_id", tenantId);
            if (await command.ExecuteScalarAsync(ct) is null) return;
        }

        await using var migrationConnection = await OpenAsync(ct);
        await using var transaction = await migrationConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await AcquireTenantWriteLockAsync(migrationConnection, transaction, tenantId, ct);
            await ImportLegacyTenantAsync(migrationConnection, transaction, tenantId, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task ImportLegacyTenantAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, CancellationToken ct)
    {
        TenantState? state;
        long contentLength;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    CASE WHEN OCTET_LENGTH(state_json) <= @max_state_bytes THEN state_json ELSE NULL END,
                    OCTET_LENGTH(state_json)
                FROM vyral_mysql_canonical_tenants
                WHERE tenant_id = @tenant_id
                FOR UPDATE
                """;
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("max_state_bytes", _maxLegacyTenantStateBytes);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return;
            contentLength = reader.GetInt64(1);
            if (reader.IsDBNull(0))
                throw new MySqlCanonicalTenantStateLimitExceededException(tenantId, contentLength, _maxLegacyTenantStateBytes);
            state = Deserialize<TenantState>(reader.GetString(0));
        }

        if (await HasNormalizedTenantContentAsync(connection, transaction, tenantId, ct))
            throw new InvalidOperationException($"MySQL canonical tenant '{tenantId}' has both legacy and normalized state; refusing an ambiguous merge.");

        var generation = await ReadTenantGenerationAsync(connection, transaction, tenantId, ct);
        var snapshot = new CanonicalTenantSnapshot
        {
            TenantId = tenantId,
            Generation = generation,
            Documents = state.Documents,
            Revisions = state.Revisions,
            Fences = state.Fences,
            Outbox = state.Outbox,
            Transactions = state.Transactions,
            ExportedAtUtc = DateTime.UtcNow
        };
        snapshot.ContentHash = CanonicalSnapshotHasher.Compute(snapshot);
        CanonicalContractValidator.ValidateSnapshot(snapshot, enforcePortableSize: false);

        foreach (var document in state.Documents)
        {
            await UpsertDocumentAsync(connection, transaction, document, ct);
            await ReplaceDocumentIndexesAsync(connection, transaction, document, ct);
        }
        foreach (var revision in state.Revisions) await InsertRevisionAsync(connection, transaction, revision, ct);
        foreach (var fence in state.Fences) await InsertFenceAsync(connection, transaction, fence, ct);
        foreach (var item in state.Outbox)
        {
            await InsertOutboxAsync(connection, transaction, item, ct);
            if (state.LeaseTokenHashes.TryGetValue(item.Id, out var tokenHash))
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE vyral_mysql_canonical_outbox
                    SET lease_token_hash = @lease_token_hash
                    WHERE tenant_id = @tenant_id AND event_id = @event_id
                    """, ct, ("lease_token_hash", tokenHash), ("tenant_id", tenantId), ("event_id", item.Id));
            }
        }
        foreach (var receipt in state.Transactions) await InsertTransactionReceiptAsync(connection, transaction, receipt, ct);

        await ExecuteAsync(connection, transaction,
            "DELETE FROM vyral_mysql_canonical_tenants WHERE tenant_id = @tenant_id", ct, ("tenant_id", tenantId));
    }

    private static async Task<bool> HasNormalizedTenantContentAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM vyral_mysql_canonical_documents WHERE tenant_id = @tenant_id
                UNION ALL SELECT 1 FROM vyral_mysql_canonical_revisions WHERE tenant_id = @tenant_id
                UNION ALL SELECT 1 FROM vyral_mysql_canonical_fences WHERE tenant_id = @tenant_id
                UNION ALL SELECT 1 FROM vyral_mysql_canonical_outbox WHERE tenant_id = @tenant_id
                UNION ALL SELECT 1 FROM vyral_mysql_canonical_transactions WHERE tenant_id = @tenant_id
            )
            """;
        command.Parameters.AddWithValue("tenant_id", tenantId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0;
    }

    private static async Task<int> ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
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
    private static JsonNode? CloneNode(JsonNode? value) => value is null ? null : JsonNode.Parse(value.ToJsonString(JsonOptions));
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

    private static Task EnsureTenantLifecycleAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_mysql_canonical_tenant_lifecycle (tenant_id, generation, updated_at_utc)
            VALUES (@tenant_id, 1, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE tenant_id = VALUES(tenant_id)
            """, ct, ("tenant_id", tenantId));

    private static async Task<long> ReadTenantGenerationAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT generation FROM vyral_mysql_canonical_tenant_lifecycle WHERE tenant_id = @tenant_id";
        command.Parameters.AddWithValue("tenant_id", tenantId);
        return await command.ExecuteScalarAsync(ct) is { } value ? Convert.ToInt64(value) : 1;
    }

    private static async Task<(string RequestHash, CanonicalTenantPurgeReceipt Receipt)?> ReadPurgeReceiptAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, string idempotencyKey, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_hash, receipt_json FROM vyral_mysql_canonical_tenant_purges WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key";
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), Deserialize<CanonicalTenantPurgeReceipt>(reader.GetString(1)))
            : null;
    }
    private static (string? DocumentType, string? Id) DecodeContinuation(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, null);
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(token)).Split('\n', 2);
            return parts.Length == 2 && parts.All(value => !string.IsNullOrWhiteSpace(value)) ? (parts[0], parts[1]) : throw new FormatException();
        }
        catch (FormatException) { throw new InvalidOperationException("Canonical document continuation token is not valid."); }
    }

    private sealed class TenantState
    {
        public List<CanonicalDocument> Documents { get; set; } = new();
        public List<CanonicalDocumentRevision> Revisions { get; set; } = new();
        public List<CanonicalFence> Fences { get; set; } = new();
        public List<CanonicalOutboxEvent> Outbox { get; set; } = new();
        public List<CanonicalTransactionReceipt> Transactions { get; set; } = new();
        public Dictionary<string, string> LeaseTokenHashes { get; set; } = new(StringComparer.Ordinal);
    }
}
