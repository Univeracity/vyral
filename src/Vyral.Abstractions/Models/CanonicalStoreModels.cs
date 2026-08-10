using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

/// <summary>
/// CanonicalStore is the transactional, tenant-scoped domain-state profile. It is intentionally
/// distinct from retrieval records: a commit atomically applies documents, uniqueness fences, and
/// an outbox in one durable provider transaction.
/// </summary>
public sealed class CanonicalDocument
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("documentType")]
    public string DocumentType { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public JsonNode? Data { get; set; }

    /// <summary>
    /// Consumer-owned, explicitly projected equality indexes. Values are not inferred from JSON
    /// so index evolution is visible in a migration and portable across SQL providers.
    /// </summary>
    [JsonPropertyName("indexes")]
    public Dictionary<string, string> Indexes { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("etag")]
    public string Etag { get; set; } = string.Empty;

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class CanonicalDocumentRevision
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("documentType")]
    public string DocumentType { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("transactionId")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = CanonicalMutationOperations.Upsert;

    [JsonPropertyName("document")]
    public CanonicalDocument Document { get; set; } = new();

    [JsonPropertyName("recordedAtUtc")]
    public DateTime RecordedAtUtc { get; set; }
}

public static class CanonicalMutationOperations
{
    public const string Upsert = "upsert";
    public const string Delete = "delete";
}

public sealed class CanonicalWritePrecondition
{
    /// <summary>Require an exact current revision. Omit for an unconditional write.</summary>
    [JsonPropertyName("expectedRevision")]
    public long? ExpectedRevision { get; set; }

    /// <summary>Require the document not to exist, including no retained tombstone.</summary>
    [JsonPropertyName("mustNotExist")]
    public bool MustNotExist { get; set; }

    /// <summary>Require the document to exist, including a retained tombstone.</summary>
    [JsonPropertyName("mustExist")]
    public bool MustExist { get; set; }
}

public sealed class CanonicalMutation
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = CanonicalMutationOperations.Upsert;

    /// <summary>Required for upserts. TenantId must match the enclosing transaction.</summary>
    [JsonPropertyName("document")]
    public CanonicalDocument? Document { get; set; }

    [JsonPropertyName("documentType")]
    public string? DocumentType { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("precondition")]
    public CanonicalWritePrecondition? Precondition { get; set; }
}

public static class CanonicalFenceOperations
{
    public const string Claim = "claim";
    public const string Release = "release";
}

/// <summary>
/// A durable, tenant-scoped uniqueness or command fence. Claims can be replayed by the same
/// owner, but never silently transferred to a different canonical document.
/// </summary>
public sealed class CanonicalFenceMutation
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = CanonicalFenceOperations.Claim;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("ownerDocumentType")]
    public string OwnerDocumentType { get; set; } = string.Empty;

    [JsonPropertyName("ownerDocumentId")]
    public string OwnerDocumentId { get; set; } = string.Empty;
}

public sealed class CanonicalFence
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("ownerDocumentType")]
    public string OwnerDocumentType { get; set; } = string.Empty;

    [JsonPropertyName("ownerDocumentId")]
    public string OwnerDocumentId { get; set; } = string.Empty;

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class CanonicalOutboxWrite
{
    /// <summary>Optional caller-provided id. Otherwise the transaction assigns a stable id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("notBeforeUtc")]
    public DateTime? NotBeforeUtc { get; set; }

    /// <summary>
    /// Optional durable retry ceiling. When a release reaches this count the event is parked in
    /// the dead-letter state until an operator explicitly requeues it.
    /// </summary>
    [JsonPropertyName("maxDeliveryAttempts")]
    public int? MaxDeliveryAttempts { get; set; }
}

public sealed class CanonicalOutboxEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("transactionId")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("notBeforeUtc")]
    public DateTime? NotBeforeUtc { get; set; }

    [JsonPropertyName("deliveryCount")]
    public int DeliveryCount { get; set; }

    [JsonPropertyName("deliveredAtUtc")]
    public DateTime? DeliveredAtUtc { get; set; }

    [JsonPropertyName("leaseOwner")]
    public string? LeaseOwner { get; set; }

    [JsonPropertyName("leaseExpiresAtUtc")]
    public DateTime? LeaseExpiresAtUtc { get; set; }

    [JsonPropertyName("maxDeliveryAttempts")]
    public int? MaxDeliveryAttempts { get; set; }

    [JsonPropertyName("deadLetteredAtUtc")]
    public DateTime? DeadLetteredAtUtc { get; set; }

    /// <summary>Trimmed operator/consumer diagnostic from the last release; never include secrets.</summary>
    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }
}

public sealed class CanonicalTransactionRequest
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Tenant-scoped idempotency key for the entire write set, fences, and outbox.</summary>
    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Stable actor/audit principal. Server-hosted CanonicalStore overwrites this with the verified
    /// workload identity when tenant policies are enabled; direct stores accept the caller value.
    /// </summary>
    [JsonPropertyName("actor")]
    public string? Actor { get; set; }

    [JsonPropertyName("mutations")]
    public List<CanonicalMutation> Mutations { get; set; } = new();

    [JsonPropertyName("fences")]
    public List<CanonicalFenceMutation> Fences { get; set; } = new();

    [JsonPropertyName("outbox")]
    public List<CanonicalOutboxWrite> Outbox { get; set; } = new();
}

public sealed class CanonicalTransactionResult
{
    [JsonPropertyName("transactionId")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Caller-provided audit/correlation value retained with the idempotency receipt.</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("actor")]
    public string? Actor { get; set; }

    [JsonPropertyName("replayed")]
    public bool Replayed { get; set; }

    [JsonPropertyName("committedAtUtc")]
    public DateTime CommittedAtUtc { get; set; }

    [JsonPropertyName("documents")]
    public List<CanonicalDocument> Documents { get; set; } = new();

    [JsonPropertyName("outbox")]
    public List<CanonicalOutboxEvent> Outbox { get; set; } = new();
}

public sealed class CanonicalDocumentQuery
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("documentType")]
    public string? DocumentType { get; set; }

    [JsonPropertyName("indexes")]
    public Dictionary<string, string> Indexes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Optional lexicographic range over one explicitly projected index. Consumers encode numeric
    /// or temporal values in sortable form (for example fixed-width integers or UTC ISO-8601).
    /// </summary>
    [JsonPropertyName("indexRange")]
    public CanonicalDocumentIndexRange? IndexRange { get; set; }

    /// <summary>Optional ordering by one explicitly projected index, with document identity as a stable tie-breaker.</summary>
    [JsonPropertyName("orderByIndex")]
    public string? OrderByIndex { get; set; }

    [JsonPropertyName("orderDirection")]
    public string OrderDirection { get; set; } = CanonicalDocumentOrderDirections.Ascending;

    [JsonPropertyName("includeDeleted")]
    public bool IncludeDeleted { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}

public static class CanonicalDocumentOrderDirections
{
    public const string Ascending = "ascending";
    public const string Descending = "descending";
}

public sealed class CanonicalDocumentIndexRange
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("greaterThanOrEqual")]
    public string? GreaterThanOrEqual { get; set; }

    [JsonPropertyName("lessThanOrEqual")]
    public string? LessThanOrEqual { get; set; }
}

public sealed class CanonicalDocumentQueryResult
{
    [JsonPropertyName("items")]
    public List<CanonicalDocument> Items { get; set; } = new();

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}

/// <summary>
/// Body form of an exact canonical document read. It permits opaque provider-neutral identifiers
/// (including slash-containing IDs) without depending on HTTP path-segment escaping behavior.
/// </summary>
public sealed class CanonicalDocumentReadRequest
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("documentType")]
    public string DocumentType { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("includeDeleted")]
    public bool IncludeDeleted { get; set; }
}

public sealed class CanonicalDocumentRevisionQuery
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("documentType")]
    public string DocumentType { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

public sealed class CanonicalOutboxLeaseRequest
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("consumerId")]
    public string ConsumerId { get; set; } = string.Empty;

    [JsonPropertyName("maxItems")]
    public int MaxItems { get; set; } = 10;

    [JsonPropertyName("leaseSeconds")]
    public double LeaseSeconds { get; set; } = 60;
}

public sealed class CanonicalOutboxLease
{
    [JsonPropertyName("event")]
    public CanonicalOutboxEvent Event { get; set; } = new();

    /// <summary>Opaque token required for acknowledge or release; never log it.</summary>
    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("expiresAtUtc")]
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>
/// Extends an active outbox lease without issuing a second delivery. The existing opaque lease
/// token remains valid; callers must never log or put it in a URL.
/// </summary>
public sealed class CanonicalOutboxLeaseRenewalRequest
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("leaseSeconds")]
    public double LeaseSeconds { get; set; } = 60;
}

public sealed class CanonicalOutboxLeaseRenewal
{
    [JsonPropertyName("expiresAtUtc")]
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class CanonicalOutboxAcknowledgement
{
    /// <summary>Opaque lease token from the lease response. Never place it in a URL or log it.</summary>
    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;
}

public sealed class CanonicalOutboxNackRequest
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("notBeforeUtc")]
    public DateTime? NotBeforeUtc { get; set; }

    /// <summary>
    /// Delay from the release time before retry. Mutually exclusive with notBeforeUtc. If neither
    /// is supplied, the portable five-second safety delay prevents a failing consumer hot-loop.
    /// </summary>
    [JsonPropertyName("retryAfterSeconds")]
    public double? RetryAfterSeconds { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public static class CanonicalOutboxStates
{
    public const string Ready = "ready";
    public const string Leased = "leased";
    public const string Scheduled = "scheduled";
    public const string Delivered = "delivered";
    public const string DeadLetter = "dead-letter";
}

/// <summary>Operational outbox inspection. Payloads are returned, so this is intentionally a dispatch privilege.</summary>
public sealed class CanonicalOutboxQuery
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("topic")]
    public string? Topic { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}

public sealed class CanonicalOutboxQueryResult
{
    [JsonPropertyName("items")]
    public List<CanonicalOutboxEvent> Items { get; set; } = new();

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}

public sealed class CanonicalOutboxReplayRequest
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    /// <summary>When true, discard historical attempts before requeueing a dead-lettered event.</summary>
    [JsonPropertyName("resetDeliveryCount")]
    public bool ResetDeliveryCount { get; set; }
}

public class CanonicalMigration
{
    /// <summary>
    /// Consumer/application namespace for the migration ledger. A shared CanonicalStore may host
    /// many tenant populations; migration identifiers are unique only within this namespace.
    /// </summary>
    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("checksum")]
    public string Checksum { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class CanonicalMigrationReceipt : CanonicalMigration
{
    [JsonPropertyName("appliedAtUtc")]
    public DateTime AppliedAtUtc { get; set; }
}

public static class CanonicalMigrationIdentity
{
    private const string Prefix = "canon-v1.";

    public static string Create(string @namespace, string id) => Prefix + Encode(@namespace.Trim()) + "." + Encode(id.Trim());

    public static (string Namespace, string Id) Parse(string storedId)
    {
        if (!storedId.StartsWith(Prefix, StringComparison.Ordinal)) return ("legacy", storedId);
        var parts = storedId[Prefix.Length..].Split('.', 2);
        if (parts.Length != 2) throw new InvalidOperationException("Canonical migration storage identity is invalid.");
        try
        {
            return (Decode(parts[0]), Decode(parts[1]));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Canonical migration storage identity is invalid.", ex);
        }
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Decode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}

public sealed class CanonicalTenantSnapshot
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("documents")]
    public List<CanonicalDocument> Documents { get; set; } = new();

    [JsonPropertyName("revisions")]
    public List<CanonicalDocumentRevision> Revisions { get; set; } = new();

    [JsonPropertyName("fences")]
    public List<CanonicalFence> Fences { get; set; } = new();

    [JsonPropertyName("outbox")]
    public List<CanonicalOutboxEvent> Outbox { get; set; } = new();

    [JsonPropertyName("transactions")]
    public List<CanonicalTransactionReceipt> Transactions { get; set; } = new();

    [JsonPropertyName("exportedAtUtc")]
    public DateTime ExportedAtUtc { get; set; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;
}

public sealed class CanonicalTransactionReceipt
{
    [JsonPropertyName("transactionId")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [JsonPropertyName("requestHash")]
    public string RequestHash { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public CanonicalTransactionResult Result { get; set; } = new();

    [JsonPropertyName("committedAtUtc")]
    public DateTime CommittedAtUtc { get; set; }
}

public sealed class CanonicalRestoreRequest
{
    [JsonPropertyName("snapshot")]
    public CanonicalTenantSnapshot Snapshot { get; set; } = new();

    [JsonPropertyName("expectedContentHash")]
    public string? ExpectedContentHash { get; set; }
}

/// <summary>
/// A portable large-tenant backup profile. The usual JSON snapshot remains bounded for shared
/// HTTP use; archives are split into independently hash-checked binary chunks so a single tenant
/// can be preserved without weakening the snapshot boundary.
/// </summary>
public sealed class CanonicalTenantArchive
{
    public const string ProfileV1 = "vyral.canonical.archive.v1";
    public const int DefaultChunkBytes = 8 * 1024 * 1024;
    public const int MaxChunkBytes = 16 * 1024 * 1024;

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = ProfileV1;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("exportedAtUtc")]
    public DateTime ExportedAtUtc { get; set; }

    /// <summary>The stable hash of the contained canonical tenant snapshot.</summary>
    [JsonPropertyName("snapshotContentHash")]
    public string SnapshotContentHash { get; set; } = string.Empty;

    /// <summary>Hash of profile metadata and every numbered chunk hash/length.</summary>
    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("chunks")]
    public List<CanonicalTenantArchiveChunk> Chunks { get; set; } = new();
}

public sealed class CanonicalTenantArchiveChunk
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("content")]
    public byte[] Content { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;
}

public sealed class CanonicalArchiveRestoreRequest
{
    [JsonPropertyName("archive")]
    public CanonicalTenantArchive Archive { get; set; } = new();

    [JsonPropertyName("expectedContentHash")]
    public string? ExpectedContentHash { get; set; }
}

/// <summary>Constructs and verifies the manifest-plus-chunks archive representation.</summary>
public static class CanonicalTenantArchiveCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static CanonicalTenantArchive Create(CanonicalTenantSnapshot snapshot, int chunkBytes = CanonicalTenantArchive.DefaultChunkBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        CanonicalContractValidator.ValidateSnapshot(snapshot, enforcePortableSize: false);
        if (chunkBytes <= 0 || chunkBytes > CanonicalTenantArchive.MaxChunkBytes)
            throw new InvalidOperationException($"Canonical archive chunkBytes must be between 1 and {CanonicalTenantArchive.MaxChunkBytes}.");

        var snapshotHash = CanonicalSnapshotHasher.Compute(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.ContentHash) || !string.Equals(snapshot.ContentHash, snapshotHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical archive source snapshot content hash does not match its contents.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var archive = new CanonicalTenantArchive
        {
            TenantId = snapshot.TenantId,
            ExportedAtUtc = snapshot.ExportedAtUtc,
            SnapshotContentHash = snapshotHash
        };
        for (var offset = 0; offset < payload.Length; offset += chunkBytes)
        {
            var length = Math.Min(chunkBytes, payload.Length - offset);
            var content = payload.AsSpan(offset, length).ToArray();
            archive.Chunks.Add(new CanonicalTenantArchiveChunk
            {
                Index = archive.Chunks.Count,
                Content = content,
                Length = content.Length,
                ContentHash = Hash(content)
            });
        }
        archive.ContentHash = ComputeArchiveHash(archive);
        return archive;
    }

    public static CanonicalTenantSnapshot Read(CanonicalArchiveRestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var archive = request.Archive ?? throw new InvalidOperationException("Canonical archive restore requires an archive.");
        CanonicalContractValidator.ValidateTenantId(archive.TenantId);
        if (!string.Equals(archive.Profile, CanonicalTenantArchive.ProfileV1, StringComparison.Ordinal))
            throw new InvalidOperationException($"Canonical archive profile '{archive.Profile}' is not supported.");
        if (archive.Chunks.Count == 0) throw new InvalidOperationException("Canonical archive contains no chunks.");
        var expectedArchiveHash = string.IsNullOrWhiteSpace(request.ExpectedContentHash) ? archive.ContentHash : request.ExpectedContentHash.Trim();
        var actualArchiveHash = ComputeArchiveHash(archive);
        if (string.IsNullOrWhiteSpace(expectedArchiveHash) || !string.Equals(expectedArchiveHash, actualArchiveHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical archive content hash does not match the requested restore.");

        using var payload = new MemoryStream();
        foreach (var chunk in archive.Chunks.OrderBy(item => item.Index))
        {
            if (chunk.Index < 0 || chunk.Length <= 0 || chunk.Length > CanonicalTenantArchive.MaxChunkBytes || chunk.Content.Length != chunk.Length || !string.Equals(chunk.ContentHash, Hash(chunk.Content), StringComparison.Ordinal))
                throw new InvalidOperationException("Canonical archive contains an invalid chunk.");
            payload.Write(chunk.Content);
        }
        var ordered = archive.Chunks.OrderBy(item => item.Index).ToList();
        if (!ordered.Select((chunk, index) => chunk.Index == index).All(valid => valid))
            throw new InvalidOperationException("Canonical archive chunk indexes must be contiguous and start at zero.");
        var snapshot = JsonSerializer.Deserialize<CanonicalTenantSnapshot>(payload.ToArray(), JsonOptions)
            ?? throw new InvalidOperationException("Canonical archive did not contain a tenant snapshot.");
        CanonicalContractValidator.ValidateSnapshot(snapshot, enforcePortableSize: false);
        var snapshotHash = CanonicalSnapshotHasher.Compute(snapshot);
        if (!string.Equals(archive.TenantId, snapshot.TenantId, StringComparison.Ordinal) ||
            !string.Equals(archive.SnapshotContentHash, snapshotHash, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ContentHash, snapshotHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical archive snapshot integrity check failed.");
        return snapshot;
    }

    private static string ComputeArchiveHash(CanonicalTenantArchive archive)
    {
        var material = string.Join("\n", new[]
        {
            archive.Profile,
            archive.TenantId,
            archive.ExportedAtUtc.ToUniversalTime().ToString("O"),
            archive.SnapshotContentHash
        }.Concat(archive.Chunks.OrderBy(item => item.Index).Select(item => $"{item.Index}:{item.Length}:{item.ContentHash}")));
        return Hash(Encoding.UTF8.GetBytes(material));
    }

    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public static class CanonicalSnapshotHasher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string Compute(CanonicalTenantSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var bytes = SHA256.HashData(GetCanonicalBytes(snapshot));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static int GetCanonicalByteCount(CanonicalTenantSnapshot snapshot) => GetCanonicalBytes(snapshot).Length;

    private static byte[] GetCanonicalBytes(CanonicalTenantSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var material = new
        {
            tenantId = snapshot.TenantId,
            documents = snapshot.Documents.OrderBy(item => item.DocumentType, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal),
            revisions = snapshot.Revisions.OrderBy(item => item.DocumentType, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ThenBy(item => item.Revision),
            fences = snapshot.Fences.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Value, StringComparer.Ordinal),
            outbox = snapshot.Outbox.OrderBy(item => item.Id, StringComparer.Ordinal),
            transactions = snapshot.Transactions.OrderBy(item => item.TransactionId, StringComparer.Ordinal)
        };
        return CanonicalJson.SerializeUtf8(material, JsonOptions);
    }
}

public static class CanonicalTransactionHasher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string ComputeRequestHash(CanonicalTransactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = SHA256.HashData(CanonicalJson.SerializeUtf8(NormalizeForHash(request), JsonOptions));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string CreateTransactionId(string tenantId, string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId.Trim() + "\n" + idempotencyKey.Trim()));
        return "ctx_" + Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    public static string HashLeaseToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static CanonicalTransactionRequest NormalizeForHash(CanonicalTransactionRequest request)
    {
        var normalized = JsonSerializer.Deserialize<CanonicalTransactionRequest>(JsonSerializer.Serialize(request, JsonOptions), JsonOptions)
            ?? throw new InvalidOperationException("Canonical transaction request could not be normalized.");
        normalized.TenantId = normalized.TenantId.Trim();
        normalized.IdempotencyKey = normalized.IdempotencyKey.Trim();
        normalized.CorrelationId = normalized.CorrelationId?.Trim();
        normalized.Actor = normalized.Actor?.Trim();
        foreach (var mutation in normalized.Mutations)
        {
            mutation.Operation = mutation.Operation.Trim();
            mutation.DocumentType = mutation.DocumentType?.Trim();
            mutation.Id = mutation.Id?.Trim();
            if (mutation.Document is null) continue;
            mutation.Document.TenantId = mutation.Document.TenantId.Trim();
            mutation.Document.DocumentType = mutation.Document.DocumentType.Trim();
            mutation.Document.Id = mutation.Document.Id.Trim();
            mutation.Document.SchemaVersion = mutation.Document.SchemaVersion.Trim();
        }
        foreach (var fence in normalized.Fences)
        {
            fence.Operation = fence.Operation.Trim();
            fence.Name = fence.Name.Trim();
            fence.Value = fence.Value.Trim();
            fence.OwnerDocumentType = fence.OwnerDocumentType.Trim();
            fence.OwnerDocumentId = fence.OwnerDocumentId.Trim();
        }
        foreach (var item in normalized.Outbox)
        {
            item.Id = item.Id?.Trim();
            item.Topic = item.Topic.Trim();
            item.Key = item.Key.Trim();
            item.NotBeforeUtc = item.NotBeforeUtc?.ToUniversalTime();
        }
        return normalized;
    }
}

/// <summary>
/// Deterministic JSON materialization for hashes. JSON object/dictionary property order is not a
/// domain semantic, so hashes must be stable across serializers, retries, and backup tooling that
/// emit the same object with a different property order. Array order remains significant.
/// </summary>
internal static class CanonicalJson
{
    public static byte[] SerializeUtf8<T>(T value, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(value, options))
            ?? throw new InvalidOperationException("Canonical JSON material cannot be null.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteNode(writer, node);
        }
        return stream.ToArray();
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteNode(writer, property.Value);
                }
                writer.WriteEndObject();
                return;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) WriteNode(writer, item);
                writer.WriteEndArray();
                return;
            default:
                node.WriteTo(writer);
                return;
        }
    }
}
