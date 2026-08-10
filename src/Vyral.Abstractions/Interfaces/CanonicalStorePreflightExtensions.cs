using System.Diagnostics;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

/// <summary>Runs reversible, provider-neutral CanonicalStore deployment probes.</summary>
public static class CanonicalStorePreflightExtensions
{
    private const string ProbeDocumentType = "vyral-preflight";
    private const string BaselineDocumentId = "baseline";
    private const string PostArchiveDocumentId = "post-archive";

    /// <summary>
    /// Proves archive restore and tenant isolation with two random probe tenants, then replaces
    /// both tenants with hash-verified empty snapshots. The returned evidence deliberately omits
    /// probe identities, payloads, hashes, provider errors, and connection details.
    /// </summary>
    public static async Task<CanonicalDataPlanePreflightResult> RunDataPlanePreflightAsync(
        this ICanonicalStore store,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var suffix = Guid.NewGuid().ToString("N");
        var firstTenant = $"vyral-preflight-{suffix}-a";
        var secondTenant = $"vyral-preflight-{suffix}-b";
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var archiveChunkCount = 0;
        var backupRestoreVerified = false;
        var tenantIsolationVerified = false;
        var cleanupVerified = false;

        try
        {
            await store.CommitAsync(CreateSeed(firstTenant, "alpha"), ct);
            await store.CommitAsync(CreateSeed(secondTenant, "beta"), ct);

            var secondBefore = await store.ExportTenantAsync(secondTenant, ct);
            var archive = await store.ExportTenantArchiveAsync(firstTenant, chunkBytes: 128, ct);
            archiveChunkCount = archive.Chunks.Count;
            var decoded = CanonicalTenantArchiveCodec.Read(new CanonicalArchiveRestoreRequest
            {
                Archive = archive,
                ExpectedContentHash = archive.ContentHash
            });

            await store.CommitAsync(CreatePostArchiveMutation(firstTenant), ct);
            await store.RestoreTenantArchiveAsync(new CanonicalArchiveRestoreRequest
            {
                Archive = archive,
                ExpectedContentHash = archive.ContentHash
            }, ct);

            var restored = await store.GetDocumentAsync(firstTenant, ProbeDocumentType, BaselineDocumentId, ct: ct);
            var removed = await store.GetDocumentAsync(firstTenant, ProbeDocumentType, PostArchiveDocumentId, ct: ct);
            var restoredSnapshot = await store.ExportTenantAsync(firstTenant, ct);
            backupRestoreVerified = archiveChunkCount > 0 &&
                string.Equals(ReadMarker(restored), "alpha", StringComparison.Ordinal) &&
                removed is null &&
                decoded.Fences.Count == 1 &&
                decoded.Outbox.Count == 1 &&
                decoded.Transactions.Count == 1 &&
                string.Equals(decoded.ContentHash, restoredSnapshot.ContentHash, StringComparison.Ordinal);

            var secondAfter = await store.ExportTenantAsync(secondTenant, ct);
            var secondDocument = await store.GetDocumentAsync(secondTenant, ProbeDocumentType, BaselineDocumentId, ct: ct);
            tenantIsolationVerified = SnapshotBelongsTo(decoded, firstTenant) &&
                string.Equals(ReadMarker(secondDocument), "beta", StringComparison.Ordinal) &&
                string.Equals(secondBefore.ContentHash, secondAfter.ContentHash, StringComparison.Ordinal) &&
                SnapshotBelongsTo(secondAfter, secondTenant);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Reports are intentionally fixed and redacted. Provider exception messages may
            // contain endpoints, schema names, or connection details and must not escape here.
        }
        finally
        {
            cleanupVerified = await CleanupAsync(store, firstTenant, secondTenant);
        }

        stopwatch.Stop();
        var ready = backupRestoreVerified && tenantIsolationVerified && cleanupVerified;
        return new CanonicalDataPlanePreflightResult
        {
            Ready = ready,
            Status = ready ? CanonicalPreflightCheckStatuses.Passed : CanonicalPreflightCheckStatuses.Failed,
            CheckedAtUtc = startedAt,
            DurationMs = Math.Max(0, stopwatch.ElapsedMilliseconds),
            ArchiveChunkCount = archiveChunkCount,
            BackupRestoreVerified = backupRestoreVerified,
            TenantIsolationVerified = tenantIsolationVerified,
            CleanupVerified = cleanupVerified,
            Checks =
            [
                Check(
                    "canonical.archive_restore",
                    backupRestoreVerified,
                    "Isolated canonical archive export and restore passed.",
                    "Isolated canonical archive export or restore failed."),
                Check(
                    "canonical.tenant_isolation",
                    tenantIsolationVerified,
                    "The second probe tenant remained unchanged during restore.",
                    "Tenant-isolation verification failed during the isolated restore."),
                Check(
                    "canonical.probe_cleanup",
                    cleanupVerified,
                    "Both ephemeral probe tenants were cleared to verified empty canonical state.",
                    "One or more ephemeral probe tenants could not be verified empty.")
            ]
        };
    }

    private static CanonicalTransactionRequest CreateSeed(string tenantId, string marker) => new()
    {
        TenantId = tenantId,
        IdempotencyKey = "seed",
        Mutations =
        [
            new CanonicalMutation
            {
                Document = new CanonicalDocument
                {
                    TenantId = tenantId,
                    DocumentType = ProbeDocumentType,
                    Id = BaselineDocumentId,
                    SchemaVersion = "v1",
                    Data = new JsonObject { ["marker"] = marker },
                    Indexes = new Dictionary<string, string>(StringComparer.Ordinal) { ["marker"] = marker }
                },
                Precondition = new CanonicalWritePrecondition { MustNotExist = true }
            }
        ],
        Fences =
        [
            new CanonicalFenceMutation
            {
                Name = "probe-marker",
                Value = marker,
                OwnerDocumentType = ProbeDocumentType,
                OwnerDocumentId = BaselineDocumentId
            }
        ],
        Outbox =
        [
            new CanonicalOutboxWrite
            {
                Topic = "vyral.preflight",
                Key = BaselineDocumentId,
                Payload = new JsonObject { ["marker"] = marker }
            }
        ]
    };

    private static CanonicalTransactionRequest CreatePostArchiveMutation(string tenantId) => new()
    {
        TenantId = tenantId,
        IdempotencyKey = "post-archive",
        Mutations =
        [
            new CanonicalMutation
            {
                Document = new CanonicalDocument
                {
                    TenantId = tenantId,
                    DocumentType = ProbeDocumentType,
                    Id = PostArchiveDocumentId,
                    SchemaVersion = "v1",
                    Data = new JsonObject { ["marker"] = "must-be-removed" }
                },
                Precondition = new CanonicalWritePrecondition { MustNotExist = true }
            }
        ]
    };

    private static async Task<bool> CleanupAsync(ICanonicalStore store, params string[] tenantIds)
    {
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var successful = true;
        foreach (var tenantId in tenantIds)
        {
            try
            {
                var empty = CreateEmptySnapshot(tenantId);
                await store.RestoreTenantAsync(new CanonicalRestoreRequest
                {
                    Snapshot = empty,
                    ExpectedContentHash = empty.ContentHash
                }, cleanupCts.Token);
            }
            catch
            {
                successful = false;
            }
        }

        foreach (var tenantId in tenantIds)
        {
            try
            {
                successful &= IsEmpty(await store.ExportTenantAsync(tenantId, cleanupCts.Token));
            }
            catch
            {
                successful = false;
            }
        }

        return successful;
    }

    private static CanonicalTenantSnapshot CreateEmptySnapshot(string tenantId)
    {
        var snapshot = new CanonicalTenantSnapshot
        {
            TenantId = tenantId,
            ExportedAtUtc = DateTime.UtcNow
        };
        snapshot.ContentHash = CanonicalSnapshotHasher.Compute(snapshot);
        return snapshot;
    }

    private static bool IsEmpty(CanonicalTenantSnapshot snapshot) =>
        snapshot.Documents.Count == 0 &&
        snapshot.Revisions.Count == 0 &&
        snapshot.Fences.Count == 0 &&
        snapshot.Outbox.Count == 0 &&
        snapshot.Transactions.Count == 0;

    private static string? ReadMarker(CanonicalDocument? document) =>
        document?.Data?["marker"]?.GetValue<string>();

    private static bool SnapshotBelongsTo(CanonicalTenantSnapshot snapshot, string tenantId) =>
        string.Equals(snapshot.TenantId, tenantId, StringComparison.Ordinal) &&
        snapshot.Documents.All(item => string.Equals(item.TenantId, tenantId, StringComparison.Ordinal)) &&
        snapshot.Revisions.All(item =>
            string.Equals(item.TenantId, tenantId, StringComparison.Ordinal) &&
            string.Equals(item.Document.TenantId, tenantId, StringComparison.Ordinal)) &&
        snapshot.Fences.All(item => string.Equals(item.TenantId, tenantId, StringComparison.Ordinal)) &&
        snapshot.Outbox.All(item => string.Equals(item.TenantId, tenantId, StringComparison.Ordinal)) &&
        snapshot.Transactions.All(item => string.Equals(item.TenantId, tenantId, StringComparison.Ordinal));

    private static CanonicalDataPlanePreflightCheck Check(string id, bool passed, string passedMessage, string failedMessage) => new()
    {
        Id = id,
        Status = passed ? CanonicalPreflightCheckStatuses.Passed : CanonicalPreflightCheckStatuses.Failed,
        Message = passed ? passedMessage : failedMessage
    };
}
