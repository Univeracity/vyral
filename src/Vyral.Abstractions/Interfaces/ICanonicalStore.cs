using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

/// <summary>
/// Strong canonical-domain storage profile. Implementations must atomically commit a transaction's
/// documents, revision history, fences, outbox, and idempotency receipt, or commit none of them.
/// Providers that cannot offer those semantics must not implement this interface.
/// </summary>
public interface ICanonicalStore
{
    Task ApplyMigrationsAsync(IReadOnlyList<CanonicalMigration> migrations, CancellationToken ct = default);
    Task<IReadOnlyList<CanonicalMigrationReceipt>> ListMigrationsAsync(CancellationToken ct = default);

    Task<CanonicalTransactionResult> CommitAsync(CanonicalTransactionRequest request, CancellationToken ct = default);
    Task<CanonicalDocument?> GetDocumentAsync(string tenantId, string documentType, string id, bool includeDeleted = false, CancellationToken ct = default);
    Task<CanonicalDocumentQueryResult> QueryDocumentsAsync(CanonicalDocumentQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<CanonicalDocumentRevision>> GetRevisionsAsync(string tenantId, string documentType, string id, int limit = 100, CancellationToken ct = default);

    Task<IReadOnlyList<CanonicalOutboxLease>> LeaseOutboxAsync(CanonicalOutboxLeaseRequest request, CancellationToken ct = default);
    Task<CanonicalOutboxQueryResult> QueryOutboxAsync(CanonicalOutboxQuery query, CancellationToken ct = default);
    Task<CanonicalOutboxLeaseRenewal> RenewOutboxLeaseAsync(CanonicalOutboxLeaseRenewalRequest request, CancellationToken ct = default);
    Task AcknowledgeOutboxAsync(string tenantId, string eventId, string leaseToken, CancellationToken ct = default);
    Task NackOutboxAsync(CanonicalOutboxNackRequest request, CancellationToken ct = default);
    Task ReplayOutboxAsync(CanonicalOutboxReplayRequest request, CancellationToken ct = default);

    Task<CanonicalTenantSnapshot> ExportTenantAsync(string tenantId, CancellationToken ct = default);
    Task RestoreTenantAsync(CanonicalRestoreRequest request, CancellationToken ct = default);
    Task<CanonicalTenantArchive> ExportTenantArchiveAsync(string tenantId, int chunkBytes = CanonicalTenantArchive.DefaultChunkBytes, CancellationToken ct = default);
    Task RestoreTenantArchiveAsync(CanonicalArchiveRestoreRequest request, CancellationToken ct = default);
}
