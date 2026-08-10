using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySqlConnector;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.MySql;

/// <summary>
/// MySQL 8/InnoDB implementation of the strong CanonicalStore profile. One InnoDB row stores a
/// tenant's canonical state and is locked for every tenant mutation. That deliberate granularity
/// makes document/fence/outbox/idempotency commits and restores atomic without relying on a
/// provider-specific JSON query feature; projected-index queries are evaluated over the durable
/// tenant snapshot with the same portable ordering semantics as the other providers.
/// </summary>
public sealed class MySqlCanonicalStore : ICanonicalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public MySqlCanonicalStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("MySQL connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
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
                var existing = await ReadMigrationAsync(connection, transaction, migration.Namespace.Trim(), migration.Id.Trim(), lockRow: true, ct);
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
                    ("@namespace", migration.Namespace.Trim()), ("@id", migration.Id.Trim()), ("@checksum", migration.Checksum.Trim()), ("@description", migration.Description?.Trim()));
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
        command.CommandText = "SELECT migration_namespace, migration_id, checksum, description, applied_at_utc FROM vyral_mysql_canonical_migrations ORDER BY migration_namespace, migration_id";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<CanonicalMigrationReceipt>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CanonicalMigrationReceipt
            {
                Namespace = reader.GetString(0), Id = reader.GetString(1), Checksum = reader.GetString(2),
                Description = reader.IsDBNull(3) ? null : reader.GetString(3), AppliedAtUtc = reader.GetDateTime(4).ToUniversalTime()
            });
        }
        return results;
    }

    public async Task<CanonicalTransactionResult> CommitAsync(CanonicalTransactionRequest request, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateTransaction(request);
        var tenantId = request.TenantId.Trim();
        var idempotencyKey = request.IdempotencyKey.Trim();
        var requestHash = CanonicalTransactionHasher.ComputeRequestHash(request);
        return await MutateTenantAsync(tenantId, state =>
        {
            var receipt = state.Transactions.FirstOrDefault(item => string.Equals(item.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
            if (receipt is not null)
            {
                if (!string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Canonical idempotency key '{idempotencyKey}' already belongs to a different transaction request.");
                var replay = Clone(receipt.Result);
                replay.Replayed = true;
                return replay;
            }

            var now = DateTime.UtcNow;
            var transactionId = CanonicalTransactionHasher.CreateTransactionId(tenantId, idempotencyKey);
            var result = new CanonicalTransactionResult
            {
                TransactionId = transactionId, TenantId = tenantId, IdempotencyKey = idempotencyKey,
                CorrelationId = request.CorrelationId?.Trim(), Actor = request.Actor?.Trim(), CommittedAtUtc = now
            };
            foreach (var mutation in request.Mutations)
                result.Documents.Add(ApplyMutation(state, tenantId, transactionId, mutation, now));
            foreach (var fence in request.Fences) ApplyFence(state, tenantId, fence, now);
            for (var index = 0; index < request.Outbox.Count; index++)
            {
                var write = request.Outbox[index];
                var eventId = string.IsNullOrWhiteSpace(write.Id) ? $"{transactionId}:{index:D3}" : write.Id.Trim();
                if (state.Outbox.Any(item => string.Equals(item.Id, eventId, StringComparison.Ordinal)))
                    throw new InvalidOperationException($"Canonical outbox event '{eventId}' already exists.");
                var item = new CanonicalOutboxEvent
                {
                    Id = eventId, TenantId = tenantId, TransactionId = transactionId, Topic = write.Topic.Trim(), Key = write.Key.Trim(),
                    Payload = CloneNode(write.Payload), Headers = new Dictionary<string, string>(write.Headers, StringComparer.Ordinal),
                    NotBeforeUtc = write.NotBeforeUtc?.ToUniversalTime(), MaxDeliveryAttempts = write.MaxDeliveryAttempts
                };
                state.Outbox.Add(item);
                result.Outbox.Add(Clone(item));
            }
            state.Transactions.Add(new CanonicalTransactionReceipt
            {
                TransactionId = transactionId, TenantId = tenantId, IdempotencyKey = idempotencyKey, RequestHash = requestHash,
                Result = Clone(result), CommittedAtUtc = now
            });
            return result;
        }, ct);
    }

    public async Task<CanonicalDocument?> GetDocumentAsync(string tenantId, string documentType, string id, bool includeDeleted = false, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateDocumentIdentity(tenantId, documentType, id);
        var state = await ReadTenantAsync(tenantId.Trim(), ct);
        var document = FindDocument(state, documentType.Trim(), id.Trim());
        return document is null || (document.Deleted && !includeDeleted) ? null : Clone(document);
    }

    public async Task<CanonicalDocumentQueryResult> QueryDocumentsAsync(CanonicalDocumentQuery query, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateQuery(query);
        var state = await ReadTenantAsync(query.TenantId.Trim(), ct);
        var filtered = state.Documents.Where(item => query.IncludeDeleted || !item.Deleted)
            .Where(item => string.IsNullOrWhiteSpace(query.DocumentType) || string.Equals(item.DocumentType, query.DocumentType.Trim(), StringComparison.Ordinal))
            .Where(item => query.Indexes.All(pair => item.Indexes.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.Ordinal)));
        if (query.IndexRange is not null)
        {
            var range = query.IndexRange;
            filtered = filtered.Where(item => item.Indexes.TryGetValue(range.Name.Trim(), out var value)
                && (string.IsNullOrWhiteSpace(range.GreaterThanOrEqual) || string.CompareOrdinal(value, range.GreaterThanOrEqual) >= 0)
                && (string.IsNullOrWhiteSpace(range.LessThanOrEqual) || string.CompareOrdinal(value, range.LessThanOrEqual) <= 0));
        }
        var hasIndexOrder = !string.IsNullOrWhiteSpace(query.OrderByIndex);
        if (hasIndexOrder) filtered = filtered.Where(item => item.Indexes.ContainsKey(query.OrderByIndex!.Trim()));
        var rows = OrderDocuments(filtered, query).ToList();
        var cursor = DecodeDocumentContinuation(query.ContinuationToken, hasIndexOrder);
        if (cursor is not null) rows = rows.Where(item => IsAfter(item, cursor, query)).ToList();
        var limit = query.Limit ?? 100;
        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        var continuation = hasMore ? EncodeDocumentContinuation(page[^1], query) : null;
        return new CanonicalDocumentQueryResult { Items = page.Select(Clone).ToList(), ContinuationToken = continuation };
    }

    public async Task<IReadOnlyList<CanonicalDocumentRevision>> GetRevisionsAsync(string tenantId, string documentType, string id, int limit = 100, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateDocumentIdentity(tenantId, documentType, id);
        if (limit is <= 0 or > CanonicalContractValidator.MaxQueryLimit) throw new InvalidOperationException($"Canonical revision limit must be between 1 and {CanonicalContractValidator.MaxQueryLimit}.");
        var state = await ReadTenantAsync(tenantId.Trim(), ct);
        return state.Revisions.Where(item => string.Equals(item.DocumentType, documentType.Trim(), StringComparison.Ordinal) && string.Equals(item.Id, id.Trim(), StringComparison.Ordinal))
            .OrderByDescending(item => item.Revision).Take(limit).Select(Clone).ToList();
    }

    public async Task<IReadOnlyList<CanonicalOutboxLease>> LeaseOutboxAsync(CanonicalOutboxLeaseRequest request, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateOutboxLease(request);
        var tenantId = request.TenantId.Trim();
        return await MutateTenantAsync(tenantId, state =>
        {
            var now = DateTime.UtcNow;
            var expiresAt = now.AddSeconds(request.LeaseSeconds);
            var leases = new List<CanonicalOutboxLease>();
            foreach (var item in state.Outbox.Where(item => IsLeaseable(item, now)).OrderBy(item => item.NotBeforeUtc ?? DateTime.MinValue).ThenBy(item => item.Id, StringComparer.Ordinal).Take(request.MaxItems))
            {
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                item.LeaseOwner = request.ConsumerId.Trim();
                item.LeaseExpiresAtUtc = expiresAt;
                item.DeliveryCount++;
                state.LeaseTokenHashes[item.Id] = CanonicalTransactionHasher.HashLeaseToken(token);
                leases.Add(new CanonicalOutboxLease { Event = Clone(item), LeaseToken = token, ExpiresAtUtc = expiresAt });
            }
            return (IReadOnlyList<CanonicalOutboxLease>)leases;
        }, ct);
    }

    public async Task<CanonicalOutboxQueryResult> QueryOutboxAsync(CanonicalOutboxQuery query, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateOutboxQuery(query);
        var state = await ReadTenantAsync(query.TenantId.Trim(), ct);
        var now = DateTime.UtcNow;
        var lastId = DecodeOutboxContinuation(query.ContinuationToken);
        var items = state.Outbox.Where(item => lastId is null || string.CompareOrdinal(item.Id, lastId) > 0)
            .Where(item => string.IsNullOrWhiteSpace(query.Topic) || string.Equals(item.Topic, query.Topic.Trim(), StringComparison.Ordinal))
            .Where(item => OutboxStateMatches(item, query.State, now)).OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
        var limit = query.Limit ?? 100;
        var continuation = items.Count > limit ? EncodeOutboxContinuation(items[limit - 1].Id) : null;
        if (items.Count > limit) items.RemoveAt(items.Count - 1);
        return new CanonicalOutboxQueryResult { Items = items.Select(Clone).ToList(), ContinuationToken = continuation };
    }

    public async Task<CanonicalOutboxLeaseRenewal> RenewOutboxLeaseAsync(CanonicalOutboxLeaseRenewalRequest request, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateOutboxLeaseRenewal(request);
        var expiresAt = await MutateTenantAsync(request.TenantId.Trim(), state =>
        {
            var now = DateTime.UtcNow;
            var item = RequireActiveLease(state, request.EventId.Trim(), request.LeaseToken, now, "renewal");
            item.LeaseExpiresAtUtc = now.AddSeconds(request.LeaseSeconds);
            return item.LeaseExpiresAtUtc.Value;
        }, ct);
        return new CanonicalOutboxLeaseRenewal { ExpiresAtUtc = expiresAt };
    }

    public async Task AcknowledgeOutboxAsync(string tenantId, string eventId, string leaseToken, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateOutboxAcknowledgement(tenantId, eventId, leaseToken);
        await MutateTenantAsync(tenantId.Trim(), state =>
        {
            var item = state.Outbox.FirstOrDefault(value => string.Equals(value.Id, eventId.Trim(), StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Canonical outbox lease is not active for this acknowledgement.");
            var tokenHash = CanonicalTransactionHasher.HashLeaseToken(leaseToken);
            var validDeliveredRetry = item.DeliveredAtUtc is not null && state.LeaseTokenHashes.TryGetValue(item.Id, out var deliveredHash) && string.Equals(deliveredHash, tokenHash, StringComparison.Ordinal);
            var validActiveLease = item.DeliveredAtUtc is null && item.LeaseExpiresAtUtc > DateTime.UtcNow && state.LeaseTokenHashes.TryGetValue(item.Id, out var activeHash) && string.Equals(activeHash, tokenHash, StringComparison.Ordinal);
            if (!validDeliveredRetry && !validActiveLease) throw new InvalidOperationException("Canonical outbox lease is not active for this acknowledgement.");
            item.DeliveredAtUtc ??= DateTime.UtcNow;
            item.LeaseOwner = null;
            item.LeaseExpiresAtUtc = null;
            return 0;
        }, ct);
    }

    public async Task NackOutboxAsync(CanonicalOutboxNackRequest request, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateOutboxNack(request);
        await MutateTenantAsync(request.TenantId.Trim(), state =>
        {
            var now = DateTime.UtcNow;
            var item = RequireActiveLease(state, request.EventId.Trim(), request.LeaseToken, now, "release");
            item.LeaseOwner = null;
            item.LeaseExpiresAtUtc = null;
            state.LeaseTokenHashes.Remove(item.Id);
            item.LastError = request.Error?.Trim();
            if (item.MaxDeliveryAttempts.HasValue && item.DeliveryCount >= item.MaxDeliveryAttempts.Value)
            {
                item.NotBeforeUtc = null;
                item.DeadLetteredAtUtc = now;
            }
            else item.NotBeforeUtc = request.NotBeforeUtc?.ToUniversalTime() ?? now.AddSeconds(request.RetryAfterSeconds ?? CanonicalContractValidator.DefaultOutboxRetryDelaySeconds);
            return 0;
        }, ct);
    }

    public async Task ReplayOutboxAsync(CanonicalOutboxReplayRequest request, CancellationToken ct = default)
    {
        CanonicalContractValidator.ValidateOutboxReplay(request);
        await MutateTenantAsync(request.TenantId.Trim(), state =>
        {
            var item = state.Outbox.FirstOrDefault(value => string.Equals(value.Id, request.EventId.Trim(), StringComparison.Ordinal) && value.DeadLetteredAtUtc is not null)
                ?? throw new InvalidOperationException("Canonical outbox event is not dead-lettered for this replay.");
            item.LeaseOwner = null;
            item.LeaseExpiresAtUtc = null;
            item.NotBeforeUtc = DateTime.UtcNow;
            item.DeadLetteredAtUtc = null;
            item.LastError = null;
            if (request.ResetDeliveryCount) item.DeliveryCount = 0;
            state.LeaseTokenHashes.Remove(item.Id);
            return 0;
        }, ct);
    }

    public async Task<CanonicalTenantSnapshot> ExportTenantAsync(string tenantId, CancellationToken ct = default)
    {
        var snapshot = await ExportTenantSnapshotAsync(tenantId, ct);
        CanonicalContractValidator.ValidateSnapshotSize(snapshot);
        return snapshot;
    }

    public async Task<CanonicalTenantArchive> ExportTenantArchiveAsync(string tenantId, int chunkBytes = CanonicalTenantArchive.DefaultChunkBytes, CancellationToken ct = default)
        => CanonicalTenantArchiveCodec.Create(await ExportTenantSnapshotAsync(tenantId, ct), chunkBytes);

    public async Task RestoreTenantAsync(CanonicalRestoreRequest request, CancellationToken ct = default)
        => await RestoreTenantSnapshotAsync(request, enforcePortableSize: true, ct);

    public async Task RestoreTenantArchiveAsync(CanonicalArchiveRestoreRequest request, CancellationToken ct = default)
    {
        var snapshot = CanonicalTenantArchiveCodec.Read(request);
        await RestoreTenantSnapshotAsync(new CanonicalRestoreRequest { Snapshot = snapshot, ExpectedContentHash = snapshot.ContentHash }, enforcePortableSize: false, ct);
    }

    private async Task<CanonicalTenantSnapshot> ExportTenantSnapshotAsync(string tenantId, CancellationToken ct)
    {
        CanonicalContractValidator.ValidateTenantId(tenantId);
        var state = await ReadTenantAsync(tenantId.Trim(), ct);
        var snapshot = new CanonicalTenantSnapshot
        {
            TenantId = tenantId.Trim(), Documents = state.Documents.OrderBy(item => item.DocumentType, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).Select(Clone).ToList(),
            Revisions = state.Revisions.OrderBy(item => item.DocumentType, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ThenBy(item => item.Revision).Select(Clone).ToList(),
            Fences = state.Fences.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Value, StringComparer.Ordinal).Select(Clone).ToList(),
            Outbox = state.Outbox.OrderBy(item => item.Id, StringComparer.Ordinal).Select(Clone).ToList(),
            Transactions = state.Transactions.OrderBy(item => item.TransactionId, StringComparer.Ordinal).Select(Clone).ToList(), ExportedAtUtc = DateTime.UtcNow
        };
        snapshot.ContentHash = CanonicalSnapshotHasher.Compute(snapshot);
        return snapshot;
    }

    private async Task RestoreTenantSnapshotAsync(CanonicalRestoreRequest request, bool enforcePortableSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        CanonicalContractValidator.ValidateSnapshot(request.Snapshot, enforcePortableSize);
        var hash = CanonicalSnapshotHasher.Compute(request.Snapshot);
        var expected = string.IsNullOrWhiteSpace(request.ExpectedContentHash) ? request.Snapshot.ContentHash : request.ExpectedContentHash.Trim();
        if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, hash, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical snapshot content hash does not match the requested restore.");
        var tenantId = request.Snapshot.TenantId.Trim();
        await MutateTenantAsync(tenantId, state =>
        {
            state.Documents = request.Snapshot.Documents.Select(Clone).ToList();
            state.Revisions = request.Snapshot.Revisions.Select(Clone).ToList();
            state.Fences = request.Snapshot.Fences.Select(Clone).ToList();
            state.Outbox = request.Snapshot.Outbox.Select(Clone).ToList();
            foreach (var item in state.Outbox) { item.LeaseOwner = null; item.LeaseExpiresAtUtc = null; }
            state.Transactions = request.Snapshot.Transactions.Select(Clone).ToList();
            state.LeaseTokenHashes.Clear();
            return 0;
        }, ct);
    }

    private static CanonicalDocument ApplyMutation(TenantState state, string tenantId, string transactionId, CanonicalMutation mutation, DateTime now)
    {
        var (documentType, id) = CanonicalContractValidator.MutationKey(mutation);
        var existing = FindDocument(state, documentType, id);
        EnsurePrecondition(existing, mutation.Precondition, documentType, id);
        CanonicalDocument document;
        if (mutation.Operation == CanonicalMutationOperations.Upsert)
        {
            document = Clone(mutation.Document!);
            document.TenantId = tenantId;
            document.DocumentType = document.DocumentType.Trim();
            document.Id = document.Id.Trim();
            document.SchemaVersion = document.SchemaVersion.Trim();
            document.Revision = (existing?.Revision ?? 0) + 1;
            document.Etag = $"rev:{document.Revision}";
            document.Deleted = false;
            document.CreatedAtUtc = existing?.CreatedAtUtc ?? now;
            document.UpdatedAtUtc = now;
        }
        else
        {
            if (existing is null || existing.Deleted) throw new InvalidOperationException($"Canonical document '{documentType}/{id}' cannot be deleted because it does not exist.");
            document = Clone(existing);
            document.Revision++;
            document.Etag = $"rev:{document.Revision}";
            document.Deleted = true;
            document.Data = null;
            document.Indexes.Clear();
            document.UpdatedAtUtc = now;
        }
        var old = state.Documents.FindIndex(item => string.Equals(item.DocumentType, document.DocumentType, StringComparison.Ordinal) && string.Equals(item.Id, document.Id, StringComparison.Ordinal));
        if (old >= 0) state.Documents[old] = document; else state.Documents.Add(document);
        state.Revisions.Add(new CanonicalDocumentRevision { TenantId = tenantId, DocumentType = document.DocumentType, Id = document.Id, Revision = document.Revision, TransactionId = transactionId, Operation = mutation.Operation, Document = Clone(document), RecordedAtUtc = now });
        return Clone(document);
    }

    private static void ApplyFence(TenantState state, string tenantId, CanonicalFenceMutation mutation, DateTime now)
    {
        var name = mutation.Name.Trim();
        var value = mutation.Value.Trim();
        var ownerType = mutation.OwnerDocumentType.Trim();
        var ownerId = mutation.OwnerDocumentId.Trim();
        var index = state.Fences.FindIndex(item => string.Equals(item.Name, name, StringComparison.Ordinal) && string.Equals(item.Value, value, StringComparison.Ordinal));
        var existing = index < 0 ? null : state.Fences[index];
        if (mutation.Operation == CanonicalFenceOperations.Claim)
        {
            if (existing is not null && (!string.Equals(existing.OwnerDocumentType, ownerType, StringComparison.Ordinal) || !string.Equals(existing.OwnerDocumentId, ownerId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Canonical fence '{name}/{value}' is already owned by '{existing.OwnerDocumentType}/{existing.OwnerDocumentId}'.");
            if (existing is null) state.Fences.Add(new CanonicalFence { TenantId = tenantId, Name = name, Value = value, OwnerDocumentType = ownerType, OwnerDocumentId = ownerId, CreatedAtUtc = now, UpdatedAtUtc = now });
            return;
        }
        if (existing is null || !string.Equals(existing.OwnerDocumentType, ownerType, StringComparison.Ordinal) || !string.Equals(existing.OwnerDocumentId, ownerId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Canonical fence '{name}/{value}' cannot be released by this owner.");
        state.Fences.RemoveAt(index);
    }

    private static void EnsurePrecondition(CanonicalDocument? existing, CanonicalWritePrecondition? precondition, string documentType, string id)
    {
        if (precondition is null) return;
        if (precondition.MustNotExist && existing is not null) throw new InvalidOperationException($"Canonical write precondition failed: document '{documentType}/{id}' already exists.");
        if (precondition.MustExist && existing is null) throw new InvalidOperationException($"Canonical write precondition failed: document '{documentType}/{id}' does not exist.");
        if (precondition.ExpectedRevision.HasValue && (existing is null || existing.Revision != precondition.ExpectedRevision.Value)) throw new InvalidOperationException($"Canonical write precondition failed: document '{documentType}/{id}' revision does not match.");
    }

    private static CanonicalOutboxEvent RequireActiveLease(TenantState state, string eventId, string leaseToken, DateTime now, string operation)
    {
        var item = state.Outbox.FirstOrDefault(value => string.Equals(value.Id, eventId, StringComparison.Ordinal));
        if (item is null || item.DeliveredAtUtc is not null || item.LeaseExpiresAtUtc <= now || !state.LeaseTokenHashes.TryGetValue(eventId, out var hash) || !string.Equals(hash, CanonicalTransactionHasher.HashLeaseToken(leaseToken), StringComparison.Ordinal))
            throw new InvalidOperationException($"Canonical outbox lease is not active for this {operation}.");
        return item;
    }

    private static bool IsLeaseable(CanonicalOutboxEvent item, DateTime now) => item.DeliveredAtUtc is null && item.DeadLetteredAtUtc is null && (item.NotBeforeUtc is null || item.NotBeforeUtc <= now) && (item.LeaseExpiresAtUtc is null || item.LeaseExpiresAtUtc <= now);

    private static bool OutboxStateMatches(CanonicalOutboxEvent item, string? state, DateTime now) => state switch
    {
        null => true,
        CanonicalOutboxStates.Ready => IsLeaseable(item, now),
        CanonicalOutboxStates.Leased => item.DeliveredAtUtc is null && item.DeadLetteredAtUtc is null && item.LeaseExpiresAtUtc > now,
        CanonicalOutboxStates.Scheduled => item.DeliveredAtUtc is null && item.DeadLetteredAtUtc is null && item.NotBeforeUtc > now && (item.LeaseExpiresAtUtc is null || item.LeaseExpiresAtUtc <= now),
        CanonicalOutboxStates.Delivered => item.DeliveredAtUtc is not null,
        CanonicalOutboxStates.DeadLetter => item.DeadLetteredAtUtc is not null,
        _ => false
    };

    private static CanonicalDocument? FindDocument(TenantState state, string documentType, string id) => state.Documents.FirstOrDefault(item => string.Equals(item.DocumentType, documentType, StringComparison.Ordinal) && string.Equals(item.Id, id, StringComparison.Ordinal));

    private static IEnumerable<CanonicalDocument> OrderDocuments(IEnumerable<CanonicalDocument> documents, CanonicalDocumentQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.OrderByIndex)) return documents.OrderBy(item => item.DocumentType, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal);
        var index = query.OrderByIndex.Trim();
        return query.OrderDirection == CanonicalDocumentOrderDirections.Descending
            ? documents.OrderByDescending(item => item.Indexes[index], StringComparer.Ordinal).ThenByDescending(item => item.DocumentType, StringComparer.Ordinal).ThenByDescending(item => item.Id, StringComparer.Ordinal)
            : documents.OrderBy(item => item.Indexes[index], StringComparer.Ordinal).ThenBy(item => item.DocumentType, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal);
    }

    private static bool IsAfter(CanonicalDocument item, DocumentCursor cursor, CanonicalDocumentQuery query)
    {
        var comparison = string.IsNullOrWhiteSpace(query.OrderByIndex)
            ? CompareTuple(item.DocumentType, item.Id, cursor.DocumentType, cursor.Id)
            : CompareTuple(item.Indexes[query.OrderByIndex.Trim()], item.DocumentType, item.Id, cursor.OrderValue!, cursor.DocumentType, cursor.Id);
        return query.OrderDirection == CanonicalDocumentOrderDirections.Descending && !string.IsNullOrWhiteSpace(query.OrderByIndex) ? comparison < 0 : comparison > 0;
    }

    private static int CompareTuple(string firstA, string secondA, string firstB, string secondB)
    {
        var comparison = string.CompareOrdinal(firstA, firstB);
        return comparison != 0 ? comparison : string.CompareOrdinal(secondA, secondB);
    }

    private static int CompareTuple(string firstA, string secondA, string thirdA, string firstB, string secondB, string thirdB)
    {
        var comparison = string.CompareOrdinal(firstA, firstB);
        if (comparison != 0) return comparison;
        comparison = string.CompareOrdinal(secondA, secondB);
        return comparison != 0 ? comparison : string.CompareOrdinal(thirdA, thirdB);
    }

    private static string EncodeDocumentContinuation(CanonicalDocument item, CanonicalDocumentQuery query)
    {
        var cursor = new DocumentCursor { DocumentType = item.DocumentType, Id = item.Id, OrderValue = string.IsNullOrWhiteSpace(query.OrderByIndex) ? null : item.Indexes[query.OrderByIndex.Trim()] };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor, JsonOptions)));
    }

    private static DocumentCursor? DecodeDocumentContinuation(string? value, bool requiresOrder)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<DocumentCursor>(Encoding.UTF8.GetString(Convert.FromBase64String(value)), JsonOptions) ?? throw new InvalidOperationException();
            if (string.IsNullOrWhiteSpace(cursor.DocumentType) || string.IsNullOrWhiteSpace(cursor.Id) || (requiresOrder && cursor.OrderValue is null)) throw new InvalidOperationException();
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("Canonical document continuation token is invalid.", exception);
        }
    }

    private static string EncodeOutboxContinuation(string eventId) => Convert.ToBase64String(Encoding.UTF8.GetBytes(eventId));
    private static string? DecodeOutboxContinuation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException exception) { throw new InvalidOperationException("Canonical outbox continuation token is invalid.", exception); }
    }

    private async Task<T> MutateTenantAsync<T>(string tenantId, Func<TenantState, T> mutation, CancellationToken ct)
    {
        CanonicalContractValidator.ValidateTenantId(tenantId);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var state = await LoadTenantAsync(connection, transaction, tenantId, lockForWrite: true, ct);
            var result = mutation(state);
            await SaveTenantAsync(connection, transaction, tenantId, state, ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<TenantState> ReadTenantAsync(string tenantId, CancellationToken ct)
    {
        CanonicalContractValidator.ValidateTenantId(tenantId);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        return await LoadTenantAsync(connection, null, tenantId, lockForWrite: false, ct);
    }

    private async Task<TenantState> LoadTenantAsync(MySqlConnection connection, MySqlTransaction? transaction, string tenantId, bool lockForWrite, CancellationToken ct)
    {
        if (lockForWrite)
        {
            await ExecuteAsync(connection, transaction!, """
                INSERT INTO vyral_mysql_canonical_tenants (tenant_id, state_json, updated_at_utc)
                VALUES (@tenant_id, @state_json, UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE tenant_id = VALUES(tenant_id);
                """, ct, ("@tenant_id", tenantId), ("@state_json", Serialize(new TenantState())));
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT state_json FROM vyral_mysql_canonical_tenants WHERE tenant_id = @tenant_id{(lockForWrite ? " FOR UPDATE" : string.Empty)}";
        Add(command, "@tenant_id", tenantId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null || value is DBNull ? new TenantState() : Deserialize<TenantState>(Convert.ToString(value)!);
    }

    private static Task SaveTenantAsync(MySqlConnection connection, MySqlTransaction transaction, string tenantId, TenantState state, CancellationToken ct) => ExecuteAsync(connection, transaction, "UPDATE vyral_mysql_canonical_tenants SET state_json = @state_json, updated_at_utc = UTC_TIMESTAMP(6) WHERE tenant_id = @tenant_id", ct, ("@tenant_id", tenantId), ("@state_json", Serialize(state)));

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var connection = await OpenAsync(ct);
            await ExecuteAsync(connection, null, """
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_tenants (
                    tenant_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    state_json JSON NOT NULL,
                    updated_at_utc DATETIME(6) NOT NULL,
                    PRIMARY KEY (tenant_id)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                """, ct);
            await ExecuteAsync(connection, null, """
                CREATE TABLE IF NOT EXISTS vyral_mysql_canonical_migrations (
                    migration_namespace VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    migration_id VARCHAR(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                    checksum VARCHAR(160) NOT NULL,
                    description TEXT NULL,
                    applied_at_utc DATETIME(6) NOT NULL,
                    PRIMARY KEY (migration_namespace, migration_id)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
                """, ct);
            _initialized = true;
        }
        finally { _initializationGate.Release(); }
    }

    private async Task<CanonicalMigrationReceipt?> ReadMigrationAsync(MySqlConnection connection, MySqlTransaction transaction, string @namespace, string id, bool lockRow, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT checksum, description, applied_at_utc FROM vyral_mysql_canonical_migrations WHERE migration_namespace = @namespace AND migration_id = @id{(lockRow ? " FOR UPDATE" : string.Empty)}";
        Add(command, "@namespace", @namespace); Add(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new CanonicalMigrationReceipt { Namespace = @namespace, Id = id, Checksum = reader.GetString(0), Description = reader.IsDBNull(1) ? null : reader.GetString(1), AppliedAtUtc = reader.GetDateTime(2).ToUniversalTime() } : null;
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Add(MySqlCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions) ?? throw new InvalidOperationException("Canonical MySQL state could not be deserialized.");
    private static T Clone<T>(T value) => Deserialize<T>(Serialize(value));
    private static JsonNode? CloneNode(JsonNode? value) => value?.DeepClone();

    private sealed class TenantState
    {
        public List<CanonicalDocument> Documents { get; set; } = new();
        public List<CanonicalDocumentRevision> Revisions { get; set; } = new();
        public List<CanonicalFence> Fences { get; set; } = new();
        public List<CanonicalOutboxEvent> Outbox { get; set; } = new();
        public List<CanonicalTransactionReceipt> Transactions { get; set; } = new();
        public Dictionary<string, string> LeaseTokenHashes { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class DocumentCursor
    {
        public string DocumentType { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string? OrderValue { get; set; }
    }
}
