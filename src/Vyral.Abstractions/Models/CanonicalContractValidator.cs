using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vyral.Abstractions.Models;

/// <summary>Shared semantic validation for the strong CanonicalStore profile.</summary>
public static class CanonicalContractValidator
{
    public const int MaxTransactionMutations = 100;
    public const int MaxTransactionFences = 100;
    public const int MaxTransactionOutboxEvents = 100;
    public const int MaxDocumentBytes = 1_048_576;
    public const int MaxOutboxPayloadBytes = 1_048_576;
    public const int MaxQueryLimit = 1_000;
    public const int MaxSnapshotBytes = 67_108_864;
    public const double DefaultOutboxRetryDelaySeconds = 5;

    public static void ValidateTransaction(CanonicalTransactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTenantId(request.TenantId);
        ValidateRequiredId(request.IdempotencyKey, "Canonical idempotency key");
        if (!string.IsNullOrWhiteSpace(request.CorrelationId)) ValidateRequiredId(request.CorrelationId, "Canonical correlation id");
        if (!string.IsNullOrWhiteSpace(request.Actor)) ValidateRequiredId(request.Actor, "Canonical actor");
        if (request.Mutations.Count + request.Fences.Count + request.Outbox.Count == 0)
            throw new InvalidOperationException("Canonical transaction requires a mutation, fence, or outbox event.");
        if (request.Mutations.Count > MaxTransactionMutations) throw new InvalidOperationException($"Canonical transaction cannot contain more than {MaxTransactionMutations} document mutations.");
        if (request.Fences.Count > MaxTransactionFences) throw new InvalidOperationException($"Canonical transaction cannot contain more than {MaxTransactionFences} fence mutations.");
        if (request.Outbox.Count > MaxTransactionOutboxEvents) throw new InvalidOperationException($"Canonical transaction cannot contain more than {MaxTransactionOutboxEvents} outbox events.");

        var documentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mutation in request.Mutations)
        {
            ValidateMutation(request.TenantId, mutation);
            var (documentType, id) = MutationKey(mutation);
            if (!documentKeys.Add(documentType + "\n" + id)) throw new InvalidOperationException($"Canonical transaction duplicates document '{documentType}/{id}'.");
        }

        var fenceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fence in request.Fences)
        {
            ValidateFence(fence);
            if (!fenceKeys.Add(fence.Name.Trim() + "\n" + fence.Value.Trim())) throw new InvalidOperationException($"Canonical transaction duplicates fence '{fence.Name}/{fence.Value}'.");
        }

        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in request.Outbox)
        {
            ValidateOutboxWrite(item);
            if (!string.IsNullOrWhiteSpace(item.Id) && !eventIds.Add(item.Id.Trim())) throw new InvalidOperationException($"Canonical transaction duplicates outbox event '{item.Id}'.");
        }
    }

    public static void ValidateDocument(CanonicalDocument document, bool allowDeleted = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDocumentIdentity(document.TenantId, document.DocumentType, document.Id);
        ValidateRequiredId(document.SchemaVersion, "Canonical document schema version");
        if (!allowDeleted && document.Deleted) throw new InvalidOperationException("Canonical upsert document cannot be marked deleted.");
        if (!document.Deleted && document.Data is null) throw new InvalidOperationException("Canonical document data is required for an upsert.");
        ValidateJsonBytes(document.Data, MaxDocumentBytes, "Canonical document data");
        ValidateStringMap(document.Indexes, "Canonical document indexes");
    }

    public static void ValidateQuery(CanonicalDocumentQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateTenantId(query.TenantId);
        if (!string.IsNullOrWhiteSpace(query.DocumentType)) ValidateRequiredId(query.DocumentType, "Canonical document type");
        if (query.Limit is <= 0 or > MaxQueryLimit) throw new InvalidOperationException($"Canonical query limit must be between 1 and {MaxQueryLimit}.");
        ValidateStringMap(query.Indexes, "Canonical query indexes");
        if (query.OrderDirection is not (CanonicalDocumentOrderDirections.Ascending or CanonicalDocumentOrderDirections.Descending))
            throw new InvalidOperationException($"Canonical document order direction '{query.OrderDirection}' is not supported.");
        if (!string.IsNullOrWhiteSpace(query.OrderByIndex)) ValidateRequiredId(query.OrderByIndex, "Canonical document order index");
        if (query.IndexRange is not null)
        {
            ValidateRequiredId(query.IndexRange.Name, "Canonical document range index");
            if (string.IsNullOrWhiteSpace(query.IndexRange.GreaterThanOrEqual) && string.IsNullOrWhiteSpace(query.IndexRange.LessThanOrEqual))
                throw new InvalidOperationException("Canonical document range requires a lower or upper bound.");
            ValidateIndexBound(query.IndexRange.GreaterThanOrEqual, "Canonical document range lower bound");
            ValidateIndexBound(query.IndexRange.LessThanOrEqual, "Canonical document range upper bound");
        }
    }

    public static void ValidateDocumentRead(CanonicalDocumentReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDocumentIdentity(request.TenantId, request.DocumentType, request.Id);
    }

    public static void ValidateDocumentRevisionQuery(CanonicalDocumentRevisionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateDocumentIdentity(query.TenantId, query.DocumentType, query.Id);
        if (query.Limit is <= 0 or > MaxQueryLimit) throw new InvalidOperationException($"Canonical revision limit must be between 1 and {MaxQueryLimit}.");
    }

    public static void ValidateOutboxLease(CanonicalOutboxLeaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTenantId(request.TenantId);
        ValidateRequiredId(request.ConsumerId, "Canonical outbox consumer id");
        if (request.MaxItems is <= 0 or > 100) throw new InvalidOperationException("Canonical outbox lease maxItems must be between 1 and 100.");
        if (request.LeaseSeconds <= 0 || request.LeaseSeconds > 86_400) throw new InvalidOperationException("Canonical outbox leaseSeconds must be between 0 and 86400.");
    }

    public static void ValidateOutboxAcknowledgement(string tenantId, string eventId, string leaseToken)
    {
        ValidateTenantId(tenantId);
        ValidateOutboxEventId(eventId);
        ValidateRequiredId(leaseToken, "Canonical outbox lease token");
    }

    public static void ValidateOutboxLeaseRenewal(CanonicalOutboxLeaseRenewalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOutboxAcknowledgement(request.TenantId, request.EventId, request.LeaseToken);
        if (request.LeaseSeconds <= 0 || request.LeaseSeconds > 86_400)
            throw new InvalidOperationException("Canonical outbox leaseSeconds must be between 0 and 86400.");
    }

    public static void ValidateOutboxNack(CanonicalOutboxNackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOutboxAcknowledgement(request.TenantId, request.EventId, request.LeaseToken);
        if (request.NotBeforeUtc.HasValue && request.RetryAfterSeconds.HasValue)
            throw new InvalidOperationException("Canonical outbox release cannot specify both notBeforeUtc and retryAfterSeconds.");
        if (request.RetryAfterSeconds is <= 0 or > 86_400)
            throw new InvalidOperationException("Canonical outbox retryAfterSeconds must be between 0 and 86400.");
        if (request.Error?.Length > 4_096) throw new InvalidOperationException("Canonical outbox error cannot exceed 4096 characters.");
    }

    public static void ValidateOutboxQuery(CanonicalOutboxQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateTenantId(query.TenantId);
        if (query.State is not null && query.State is not (CanonicalOutboxStates.Ready or CanonicalOutboxStates.Leased or CanonicalOutboxStates.Scheduled or CanonicalOutboxStates.Delivered or CanonicalOutboxStates.DeadLetter))
            throw new InvalidOperationException($"Canonical outbox state '{query.State}' is not supported.");
        if (!string.IsNullOrWhiteSpace(query.Topic)) ValidateRequiredId(query.Topic, "Canonical outbox topic");
        if (query.Limit is <= 0 or > MaxQueryLimit) throw new InvalidOperationException($"Canonical outbox query limit must be between 1 and {MaxQueryLimit}.");
    }

    public static void ValidateOutboxReplay(CanonicalOutboxReplayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTenantId(request.TenantId);
        ValidateOutboxEventId(request.EventId);
    }

    public static void ValidateMigration(CanonicalMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ValidateRequiredId(migration.Namespace, "Canonical migration namespace");
        ValidateRequiredId(migration.Id, "Canonical migration id");
        ValidateRequiredId(migration.Checksum, "Canonical migration checksum");
    }

    public static void ValidateSnapshot(CanonicalTenantSnapshot snapshot, bool enforcePortableSize = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateTenantId(snapshot.TenantId);
        var documentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in snapshot.Documents)
        {
            ValidateDocument(document);
            if (!string.Equals(document.TenantId, snapshot.TenantId, StringComparison.Ordinal)) throw new InvalidOperationException("Canonical snapshot document tenant does not match snapshot tenant.");
            if (document.Revision <= 0 || string.IsNullOrWhiteSpace(document.Etag)) throw new InvalidOperationException("Canonical snapshot document must have a revision and etag.");
            if (!documentKeys.Add(document.DocumentType + "\n" + document.Id)) throw new InvalidOperationException("Canonical snapshot duplicates a document.");
        }
        var revisionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var revision in snapshot.Revisions)
        {
            ValidateDocumentRevision(revision, snapshot.TenantId);
            if (!revisionKeys.Add(revision.DocumentType + "\n" + revision.Id + "\n" + revision.Revision)) throw new InvalidOperationException("Canonical snapshot duplicates a document revision.");
        }
        var fenceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fence in snapshot.Fences)
        {
            ValidateFence(fence);
            if (!string.Equals(fence.TenantId, snapshot.TenantId, StringComparison.Ordinal)) throw new InvalidOperationException("Canonical snapshot fence tenant does not match snapshot tenant.");
            if (!fenceKeys.Add(fence.Name + "\n" + fence.Value)) throw new InvalidOperationException("Canonical snapshot duplicates a fence.");
        }
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Outbox)
        {
            ValidateOutboxEvent(item);
            if (!string.Equals(item.TenantId, snapshot.TenantId, StringComparison.Ordinal)) throw new InvalidOperationException("Canonical snapshot outbox tenant does not match snapshot tenant.");
            if (!eventIds.Add(item.Id)) throw new InvalidOperationException("Canonical snapshot duplicates an outbox event.");
        }
        var receiptKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receipt in snapshot.Transactions)
        {
            ValidateTransactionReceipt(receipt, snapshot.TenantId);
            if (!receiptKeys.Add(receipt.IdempotencyKey)) throw new InvalidOperationException("Canonical snapshot duplicates an idempotency receipt.");
        }
        if (enforcePortableSize) ValidateSnapshotSize(snapshot);
    }

    public static void ValidateSnapshotSize(CanonicalTenantSnapshot snapshot)
    {
        if (CanonicalSnapshotHasher.GetCanonicalByteCount(snapshot) > MaxSnapshotBytes)
            throw new InvalidOperationException($"Canonical snapshot exceeds the {MaxSnapshotBytes}-byte portable limit.");
    }

    /// <summary>
    /// Tenant ids are carried in every canonical HTTP route, so they must be a single unescaped
    /// path segment. Document identities remain opaque and have body-form HTTP operations.
    /// </summary>
    public static void ValidateTenantId(string value) => ValidatePathSegmentId(value, "Canonical tenant id");

    public static void ValidateDocumentIdentity(string tenantId, string documentType, string id)
    {
        ValidateTenantId(tenantId);
        ValidateRequiredId(documentType, "Canonical document type");
        ValidateRequiredId(id, "Canonical document id");
    }

    public static (string DocumentType, string Id) MutationKey(CanonicalMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return mutation.Operation == CanonicalMutationOperations.Upsert && mutation.Document is not null
            ? (mutation.Document.DocumentType.Trim(), mutation.Document.Id.Trim())
            : (mutation.DocumentType?.Trim() ?? string.Empty, mutation.Id?.Trim() ?? string.Empty);
    }

    private static void ValidateMutation(string tenantId, CanonicalMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.Operation == CanonicalMutationOperations.Upsert)
        {
            if (mutation.Document is null) throw new InvalidOperationException("Canonical upsert mutation requires a document.");
            ValidateDocument(mutation.Document, allowDeleted: false);
            if (!string.Equals(tenantId, mutation.Document.TenantId, StringComparison.Ordinal)) throw new InvalidOperationException("Canonical upsert document tenant must match the enclosing transaction tenant.");
        }
        else if (mutation.Operation == CanonicalMutationOperations.Delete)
        {
            ValidateRequiredId(mutation.DocumentType, "Canonical delete document type");
            ValidateRequiredId(mutation.Id, "Canonical delete document id");
            if (mutation.Document is not null) throw new InvalidOperationException("Canonical delete mutation cannot include a document.");
        }
        else
        {
            throw new InvalidOperationException($"Canonical mutation operation '{mutation.Operation}' is not supported.");
        }

        var precondition = mutation.Precondition;
        if (precondition is not null && precondition.MustExist && precondition.MustNotExist)
            throw new InvalidOperationException("Canonical write precondition cannot require both existence and non-existence.");
        if (precondition?.ExpectedRevision is < 0) throw new InvalidOperationException("Canonical expected revision cannot be negative.");
    }

    private static void ValidateFence(CanonicalFenceMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.Operation is not (CanonicalFenceOperations.Claim or CanonicalFenceOperations.Release))
            throw new InvalidOperationException($"Canonical fence operation '{mutation.Operation}' is not supported.");
        ValidateRequiredId(mutation.Name, "Canonical fence name");
        ValidateRequiredId(mutation.Value, "Canonical fence value");
        ValidateRequiredId(mutation.OwnerDocumentType, "Canonical fence owner document type");
        ValidateRequiredId(mutation.OwnerDocumentId, "Canonical fence owner document id");
    }

    private static void ValidateFence(CanonicalFence fence)
    {
        ValidateTenantId(fence.TenantId);
        ValidateFence(new CanonicalFenceMutation
        {
            Name = fence.Name,
            Value = fence.Value,
            OwnerDocumentType = fence.OwnerDocumentType,
            OwnerDocumentId = fence.OwnerDocumentId
        });
    }

    private static void ValidateOutboxWrite(CanonicalOutboxWrite item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!string.IsNullOrWhiteSpace(item.Id)) ValidateOutboxEventId(item.Id);
        ValidateRequiredId(item.Topic, "Canonical outbox topic");
        ValidateRequiredId(item.Key, "Canonical outbox key");
        ValidateJsonBytes(item.Payload, MaxOutboxPayloadBytes, "Canonical outbox payload");
        ValidateStringMap(item.Headers, "Canonical outbox headers");
        if (item.MaxDeliveryAttempts is <= 0 or > 100_000)
            throw new InvalidOperationException("Canonical outbox maxDeliveryAttempts must be between 1 and 100000.");
    }

    private static void ValidateOutboxEvent(CanonicalOutboxEvent item)
    {
        ValidateTenantId(item.TenantId);
        ValidateOutboxEventId(item.Id);
        ValidateRequiredId(item.TransactionId, "Canonical outbox transaction id");
        ValidateOutboxWrite(new CanonicalOutboxWrite
        {
            Id = item.Id,
            Topic = item.Topic,
            Key = item.Key,
            Payload = item.Payload,
            Headers = item.Headers,
            NotBeforeUtc = item.NotBeforeUtc,
            MaxDeliveryAttempts = item.MaxDeliveryAttempts
        });
    }

    private static void ValidateDocumentRevision(CanonicalDocumentRevision revision, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ValidateTenantId(revision.TenantId);
        ValidateRequiredId(revision.DocumentType, "Canonical revision document type");
        ValidateRequiredId(revision.Id, "Canonical revision document id");
        ValidateRequiredId(revision.TransactionId, "Canonical revision transaction id");
        if (revision.Revision <= 0) throw new InvalidOperationException("Canonical revision must be positive.");
        if (revision.Operation is not (CanonicalMutationOperations.Upsert or CanonicalMutationOperations.Delete)) throw new InvalidOperationException("Canonical revision operation is not supported.");
        ValidateDocument(revision.Document);
        if (!string.Equals(revision.TenantId, tenantId, StringComparison.Ordinal) || !string.Equals(revision.Document.TenantId, tenantId, StringComparison.Ordinal)) throw new InvalidOperationException("Canonical snapshot revision tenant does not match snapshot tenant.");
        if (!string.Equals(revision.DocumentType, revision.Document.DocumentType, StringComparison.Ordinal) || !string.Equals(revision.Id, revision.Document.Id, StringComparison.Ordinal) || revision.Revision != revision.Document.Revision)
            throw new InvalidOperationException("Canonical revision does not match its document state.");
    }

    private static void ValidateTransactionReceipt(CanonicalTransactionReceipt receipt, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateTenantId(receipt.TenantId);
        ValidateRequiredId(receipt.TransactionId, "Canonical receipt transaction id");
        ValidateRequiredId(receipt.IdempotencyKey, "Canonical receipt idempotency key");
        ValidateRequiredId(receipt.RequestHash, "Canonical receipt request hash");
        if (!string.Equals(receipt.TenantId, tenantId, StringComparison.Ordinal)) throw new InvalidOperationException("Canonical snapshot receipt tenant does not match snapshot tenant.");
        ArgumentNullException.ThrowIfNull(receipt.Result);
        if (!string.Equals(receipt.Result.TenantId, tenantId, StringComparison.Ordinal) || !string.Equals(receipt.Result.TransactionId, receipt.TransactionId, StringComparison.Ordinal) || !string.Equals(receipt.Result.IdempotencyKey, receipt.IdempotencyKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical receipt result does not match its receipt identity.");
        foreach (var document in receipt.Result.Documents)
        {
            ValidateDocument(document);
            if (!string.Equals(document.TenantId, tenantId, StringComparison.Ordinal)) throw new InvalidOperationException("Canonical receipt document tenant does not match snapshot tenant.");
        }
        foreach (var item in receipt.Result.Outbox)
        {
            ValidateOutboxEvent(item);
            if (!string.Equals(item.TenantId, tenantId, StringComparison.Ordinal) || !string.Equals(item.TransactionId, receipt.TransactionId, StringComparison.Ordinal)) throw new InvalidOperationException("Canonical receipt outbox event does not match snapshot tenant or transaction.");
        }
    }

    private static void ValidateStringMap(IReadOnlyDictionary<string, string> values, string name)
    {
        if (values.Count > 64) throw new InvalidOperationException($"{name} cannot contain more than 64 values.");
        foreach (var (key, value) in values)
        {
            ValidateRequiredId(key, $"{name} key");
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4_096) throw new InvalidOperationException($"{name} value is required and must not exceed 4096 characters.");
            if (value.Any(char.IsControl)) throw new InvalidOperationException($"{name} value cannot contain control characters.");
        }
    }

    private static void ValidateJsonBytes(JsonNode? value, int limit, string name)
    {
        if (value is null) return;
        var byteCount = Encoding.UTF8.GetByteCount(value.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        if (byteCount > limit) throw new InvalidOperationException($"{name} exceeds {limit} bytes.");
    }

    private static void ValidateRequiredId(string? value, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 160)
            throw new InvalidOperationException($"{name} is required and must not exceed 160 characters.");
        if (normalized.Any(char.IsControl)) throw new InvalidOperationException($"{name} cannot contain control characters.");
    }

    private static void ValidateIndexBound(string? value, string name)
    {
        if (value is null) return;
        if (value.Length > 4_096 || value.Any(char.IsControl)) throw new InvalidOperationException($"{name} must not exceed 4096 characters or contain control characters.");
    }

    private static void ValidateOutboxEventId(string value) => ValidatePathSegmentId(value, "Canonical outbox event id");

    private static void ValidatePathSegmentId(string? value, string name)
    {
        ValidateRequiredId(value, name);
        var normalized = value!.Trim();
        if (normalized.IndexOfAny(['/', '\\', '?', '#', '%']) >= 0)
            throw new InvalidOperationException($"{name} must be safe for a single HTTP path segment.");
    }
}
