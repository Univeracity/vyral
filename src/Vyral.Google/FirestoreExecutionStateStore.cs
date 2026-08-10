using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Google.Cloud.Firestore;
using Grpc.Core;
using Vyral.Execution;

namespace Vyral.Google;

/// <summary>
/// Persistence seam for the Google execution adapter. It models only portable execution state;
/// Firestore is the production implementation, while conformance fixtures can supply a
/// deterministic state store without emulating Google APIs.
/// </summary>
public interface IGoogleCloudExecutionStateStore
{
    Task CreateRunAsync(ExecutionRun run, CancellationToken ct = default);
    Task<bool> TryCreateRunWithActiveCapacityAsync(ExecutionRun run, int maxActiveRuns, CancellationToken ct = default);
    Task<GoogleCloudExecutionRunCreation> CreateRunAtomicallyAsync(
        ExecutionRun run,
        ExecutionRun? capacityRejectedRun,
        int maxActiveRuns,
        string? idempotencyScopeKey,
        CancellationToken ct = default);
    Task UpsertRunAsync(ExecutionRun run, CancellationToken ct = default);
    Task<int> GetActiveRunCountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListDueExternalRunIdsAsync(IEnumerable<string> handlerIds, int limit, CancellationToken ct = default);
    Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default);
    Task AppendHistoryAsync(ExecutionTraceEvent item, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, int limit = 100, CancellationToken ct = default);
    Task PutCheckpointAsync(ExecutionCheckpoint checkpoint, CancellationToken ct = default);
    Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default);
    Task PutArtifactAsync(ExecutionArtifact artifact, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default);
    Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default);
    Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default);
    Task<GoogleCloudExecutionLeaseClaim?> TryClaimExternalRunAsync(string runId, ExecutionLeaseRequest request, CancellationToken ct = default);
    Task<ExecutionLease?> RenewLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default);
    Task<ExecutionRun> UpdateExternalRunUnderLeaseAsync(string leaseKey, string ownerId, ExecutionRunUpdate update, CancellationToken ct = default);
    Task AppendHistoryUnderLeaseAsync(string leaseKey, string ownerId, ExecutionTraceEvent item, CancellationToken ct = default);
    Task PutArtifactUnderLeaseAsync(string leaseKey, string ownerId, ExecutionArtifact artifact, CancellationToken ct = default);
    Task PutCheckpointUnderLeaseAsync(string leaseKey, string ownerId, ExecutionCheckpoint checkpoint, CancellationToken ct = default);
    Task<ExecutionCheckpoint?> GetCheckpointUnderLeaseAsync(string leaseKey, string ownerId, string key, CancellationToken ct = default);
    Task<ExecutionWaitResult?> TakeWaitOutcomeUnderLeaseAsync(string leaseKey, string ownerId, string runId, string kind, string name, CancellationToken ct = default);
    Task<ExecutionExternalEvent?> TakeExternalEventUnderLeaseAsync(string leaseKey, string ownerId, string runId, string name, CancellationToken ct = default);
    Task<ExecutionRun> SuspendExternalRunUnderLeaseAsync(string leaseKey, string ownerId, GoogleCloudExecutionWait wait, CancellationToken ct = default);
    Task<GoogleCloudExecutionExternalCompletion> CompleteExternalRunUnderLeaseAsync(string leaseKey, string ownerId, ExecutionRunResult result, CancellationToken ct = default);
    Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default);
    Task<ExecutionLease?> GetLeaseAsync(string leaseKey, CancellationToken ct = default);
    Task PutTimerAsync(ExecutionTimer timer, CancellationToken ct = default);
    Task PutExternalEventAsync(ExecutionExternalEvent externalEvent, CancellationToken ct = default);
    Task PutWaitAsync(GoogleCloudExecutionWait wait, CancellationToken ct = default);
    Task<GoogleCloudExecutionWait?> GetWaitAsync(string runId, CancellationToken ct = default);
    Task DeleteWaitAsync(string runId, CancellationToken ct = default);
    Task PutWaitOutcomeAsync(string runId, GoogleCloudExecutionWait wait, ExecutionWaitResult outcome, CancellationToken ct = default);
    Task<ExecutionWaitResult?> TakeWaitOutcomeAsync(string runId, string kind, string name, CancellationToken ct = default);
    Task<ExecutionExternalEvent?> TakeExternalEventAsync(string runId, string name, CancellationToken ct = default);
    Task<GoogleCloudExecutionRunDeletion> DeleteRunAsync(ExecutionRun run, CancellationToken ct = default);
}

/// <summary>
/// Result of atomically reserving an idempotency key and creating its run. An existing reservation
/// carries only the fields required to validate a safe replay; it never exposes a raw key.
/// </summary>
public sealed class GoogleCloudExecutionRunCreation
{
    public bool Created { get; init; }
    public required string RunId { get; init; }
    public required string HandlerId { get; init; }
    public required string PayloadHash { get; init; }
    public ExecutionRun? CreatedRun { get; init; }
}

/// <summary>
/// Controls Firestore behavior at Vyral's deliberately contended admission and lease-claim
/// boundaries. The Google client already retries aborted transactions; this setting raises that
/// finite attempt budget without adding a second, competing retry loop.
/// </summary>
public sealed class FirestoreExecutionStateStoreOptions
{
    public const int DefaultContentionTransactionMaxAttempts = 20;
    public const int MaximumContentionTransactionMaxAttempts = 50;

    public int ContentionTransactionMaxAttempts { get; init; } = DefaultContentionTransactionMaxAttempts;

    internal TransactionOptions BuildContentionTransactionOptions()
    {
        if (ContentionTransactionMaxAttempts is < 1 or > MaximumContentionTransactionMaxAttempts)
        {
            throw new InvalidOperationException(
                $"Firestore contention transaction attempts must be between 1 and {MaximumContentionTransactionMaxAttempts}.");
        }

        return TransactionOptions.ForMaxAttempts(ContentionTransactionMaxAttempts);
    }
}

/// <summary>
/// Firestore persistence primitives for a Google execution adapter. Documents retain the portable
/// JSON contract alongside indexed lifecycle fields; Cloud Tasks messages therefore never carry
/// mutable payload, result, checkpoint, or lease state.
/// </summary>
public sealed class FirestoreExecutionStateStore : IGoogleCloudExecutionStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = ExecutionJson.Options;
    private readonly FirestoreDb _db;
    private readonly string _rootCollection;
    private readonly TransactionOptions _contentionTransactionOptions;

    public FirestoreExecutionStateStore(
        FirestoreDb db,
        string rootCollection = "vyral_execution",
        FirestoreExecutionStateStoreOptions? options = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _rootCollection = NormalizeCollection(rootCollection);
        _contentionTransactionOptions = (options ?? new FirestoreExecutionStateStoreOptions())
            .BuildContentionTransactionOptions();
    }

    public async Task CreateRunAsync(ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ExecutionRunLifecycle.EnsureCreationStatus(run.Status);
        var batch = _db.StartBatch();
        var document = Runs.Document(RequireId(run.Id, "Execution run id"));
        batch.Create(document, BuildRunDocument(run));
        ApplyWorkItem(batch, run);
        await batch.CommitAsync(ct);
    }

    public async Task<bool> TryCreateRunWithActiveCapacityAsync(ExecutionRun run, int maxActiveRuns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (maxActiveRuns <= 0) throw new InvalidOperationException("Execution max active runs must be positive.");
        if (!ExecutionRunLifecycle.IsActive(run.Status)) throw new InvalidOperationException("Capacity-controlled execution creation requires an active run.");
        ExecutionRunLifecycle.EnsureCreationStatus(run.Status);
        var runDocument = Runs.Document(RequireId(run.Id, "Execution run id"));
        return await _db.RunTransactionAsync(async transaction =>
        {
            var counterSnapshot = await transaction.GetSnapshotAsync(ActiveRunCounter, transaction.CancellationToken);
            var activeRuns = counterSnapshot.Exists
                ? ReadActiveRunCount(counterSnapshot)
                : await CountActiveRunsForCounterInitializationAsync(transaction, maxActiveRuns, transaction.CancellationToken);
            if (activeRuns >= maxActiveRuns) return false;

            transaction.Create(runDocument, BuildRunDocument(run));
            ApplyWorkItem(transaction, run);
            transaction.Set(ActiveRunCounter, new Dictionary<string, object?> { ["activeRuns"] = activeRuns + 1 });
            return true;
        }, options: _contentionTransactionOptions, cancellationToken: ct);
    }

    public async Task<GoogleCloudExecutionRunCreation> CreateRunAtomicallyAsync(
        ExecutionRun run,
        ExecutionRun? capacityRejectedRun,
        int maxActiveRuns,
        string? idempotencyScopeKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (maxActiveRuns <= 0) throw new InvalidOperationException("Execution max active runs must be positive.");
        ExecutionRunLifecycle.EnsureCreationStatus(run.Status);
        if (capacityRejectedRun is not null)
        {
            ExecutionRunLifecycle.EnsureCreationStatus(capacityRejectedRun.Status);
            if (capacityRejectedRun.Id != run.Id) throw new InvalidOperationException("Capacity rejection must retain the proposed execution run id.");
        }

        var runDocument = Runs.Document(RequireId(run.Id, "Execution run id"));
        var reservationDocument = string.IsNullOrWhiteSpace(idempotencyScopeKey)
            ? null
            : IdempotencyReservations.Document(IdempotencyDocumentId(idempotencyScopeKey));
        return await _db.RunTransactionAsync(async transaction =>
        {
            if (reservationDocument is not null)
            {
                var reservationSnapshot = await transaction.GetSnapshotAsync(reservationDocument, transaction.CancellationToken);
                if (reservationSnapshot.Exists)
                {
                    return ReadRunCreationReservation(reservationSnapshot);
                }
            }

            var persisted = run;
            if (ExecutionRunLifecycle.IsActive(run.Status))
            {
                var counterSnapshot = await transaction.GetSnapshotAsync(ActiveRunCounter, transaction.CancellationToken);
                var activeRuns = counterSnapshot.Exists
                    ? ReadActiveRunCount(counterSnapshot)
                    : await CountActiveRunsForCounterInitializationAsync(transaction, maxActiveRuns, transaction.CancellationToken);
                if (activeRuns >= maxActiveRuns)
                {
                    persisted = capacityRejectedRun ?? throw new InvalidOperationException("An active execution run requires a capacity rejection shape.");
                }
                else
                {
                    transaction.Set(ActiveRunCounter, new Dictionary<string, object?> { ["activeRuns"] = activeRuns + 1 });
                }
            }

            transaction.Create(runDocument, BuildRunDocument(persisted));
            ApplyWorkItem(transaction, persisted);
            if (reservationDocument is not null)
            {
                transaction.Create(reservationDocument, BuildRunCreationReservation(persisted));
            }

            return new GoogleCloudExecutionRunCreation
            {
                Created = true,
                RunId = persisted.Id,
                HandlerId = persisted.HandlerId,
                PayloadHash = persisted.PayloadHash,
                CreatedRun = persisted
            };
        }, options: _contentionTransactionOptions, cancellationToken: ct);
    }

    public async Task UpsertRunAsync(ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var runDocument = Runs.Document(RequireId(run.Id, "Execution run id"));
        await _db.RunTransactionAsync(async transaction =>
        {
            var previousSnapshot = await transaction.GetSnapshotAsync(runDocument, transaction.CancellationToken);
            var previous = previousSnapshot.Exists ? Read<ExecutionRun>(previousSnapshot, "runJson") : null;
            await AdjustActiveRunCountIfTrackedAsync(transaction, previous, run, transaction.CancellationToken);
            transaction.Set(runDocument, BuildRunDocument(run));
            ApplyWorkItem(transaction, run);
        }, options: _contentionTransactionOptions, cancellationToken: ct);
    }

    public async Task<int> GetActiveRunCountAsync(CancellationToken ct = default)
    {
        var counter = await ActiveRunCounter.GetSnapshotAsync(ct);
        if (counter.Exists) return ReadActiveRunCount(counter);

        var activeRuns = 0;
        foreach (var status in ExecutionRunLifecycle.ActiveStatuses)
        {
            var snapshot = await Runs.WhereEqualTo("status", status).Count().GetSnapshotAsync(ct);
            activeRuns += checked((int)(snapshot.Count ?? 0));
        }

        return activeRuns;
    }

    /// <summary>Reads due work from per-handler subcollections, avoiding a global run scan.</summary>
    public async Task<IReadOnlyList<string>> ListDueExternalRunIdsAsync(IEnumerable<string> handlerIds, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) throw new InvalidOperationException("Execution due-work limit must be positive.");
        var now = Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc));
        var candidates = new List<(string RunId, DateTime DueAtUtc)>();
        foreach (var handlerId in handlerIds.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.Ordinal))
        {
            var snapshot = await WorkItems(handlerId).WhereLessThanOrEqualTo("dueAtUtc", now).OrderBy("dueAtUtc").Limit(limit).GetSnapshotAsync(ct);
            foreach (var document in snapshot.Documents)
            {
                if (document.TryGetValue<Timestamp>("dueAtUtc", out var dueAtUtc))
                {
                    candidates.Add((document.Id, dueAtUtc.ToDateTime()));
                }
            }
        }

        return candidates.OrderBy(item => item.DueAtUtc).ThenBy(item => item.RunId, StringComparer.Ordinal).Select(item => item.RunId).Distinct(StringComparer.Ordinal).Take(limit).ToList();
    }

    public async Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default)
    {
        var snapshot = await Runs.Document(RequireId(runId, "Execution run id")).GetSnapshotAsync(ct);
        var run = snapshot.Exists ? Read<ExecutionRun>(snapshot, "runJson") : null;
        if (run is not null && !includeResult)
        {
            run.Result = null;
        }

        return run;
    }

    public async Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default)
    {
        query ??= new ExecutionRunQuery();
        Query firestoreQuery = Runs;
        // Apply one portable Firestore field filter and perform the remaining contract filters on
        // the compact run document. This keeps the public list surface deployable without an
        // unbounded matrix of caller-provisioned composite indexes.
        if (!string.IsNullOrWhiteSpace(query.IdempotencyKey)) firestoreQuery = firestoreQuery.WhereEqualTo("idempotencyKey", query.IdempotencyKey);
        else if (!string.IsNullOrWhiteSpace(query.CorrelationId)) firestoreQuery = firestoreQuery.WhereEqualTo("correlationId", query.CorrelationId);
        else if (!string.IsNullOrWhiteSpace(query.HandlerId)) firestoreQuery = firestoreQuery.WhereEqualTo("handlerId", query.HandlerId);
        else if (!string.IsNullOrWhiteSpace(query.PluginId)) firestoreQuery = firestoreQuery.WhereEqualTo("pluginId", query.PluginId);
        else if (!string.IsNullOrWhiteSpace(query.Status)) firestoreQuery = firestoreQuery.WhereEqualTo("status", query.Status);
        var limit = query.Limit ?? 100;
        // Filtering after retrieval keeps this portable surface free from an unbounded composite
        // index matrix, but the provider read itself must still honour the caller's bound.
        var snapshot = await firestoreQuery.Limit(limit).GetSnapshotAsync(ct);
        return snapshot.Documents
            .Select(document => Read<ExecutionRun>(document, "runJson"))
            .Where(run => run is not null)
            .Select(run => run!)
            .Where(run => Matches(run, query))
            .OrderByDescending(run => run.CreatedAtUtc)
            .ThenBy(run => run.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(run =>
            {
                if (!query.IncludeResult) run.Result = null;
                return run;
            })
            .ToList();
    }

    public Task AppendHistoryAsync(ExecutionTraceEvent item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ExecutionContractValidator.ValidateTraceEvent(item);
        return RunDocument(item.RunId).Collection("history").Document(item.Id).SetAsync(new Dictionary<string, object?>
        {
            ["timestampUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(item.TimestampUtc.ToUniversalTime(), DateTimeKind.Utc)),
            ["eventJson"] = JsonSerializer.Serialize(item, JsonOptions)
        }, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, int limit = 100, CancellationToken ct = default)
    {
        var snapshot = await RunDocument(runId).Collection("history").OrderBy("timestampUtc").Limit(limit).GetSnapshotAsync(ct);
        return snapshot.Documents.Select(document => Read<ExecutionTraceEvent>(document, "eventJson"))
            .Where(item => item is not null).Select(item => item!).ToList();
    }

    public Task PutCheckpointAsync(ExecutionCheckpoint checkpoint, CancellationToken ct = default) =>
        RunDocument(checkpoint.RunId).Collection("checkpoints").Document(RequireId(checkpoint.Key, "Checkpoint key")).SetAsync(new Dictionary<string, object?>
        {
            ["checkpointJson"] = JsonSerializer.Serialize(checkpoint, JsonOptions),
            ["updatedAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(checkpoint.UpdatedAtUtc.ToUniversalTime(), DateTimeKind.Utc))
        }, cancellationToken: ct);

    public async Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default)
    {
        var snapshot = await RunDocument(runId).Collection("checkpoints").Document(RequireId(key, "Checkpoint key")).GetSnapshotAsync(ct);
        return snapshot.Exists ? Read<ExecutionCheckpoint>(snapshot, "checkpointJson") : null;
    }

    public Task PutArtifactAsync(ExecutionArtifact artifact, CancellationToken ct = default) =>
        RunDocument(artifact.RunId).Collection("artifacts").Document(RequireId(artifact.Id, "Artifact id")).SetAsync(new Dictionary<string, object?>
        {
            ["name"] = artifact.Name,
            ["createdAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(artifact.CreatedAtUtc.ToUniversalTime(), DateTimeKind.Utc)),
            ["artifactJson"] = JsonSerializer.Serialize(artifact, JsonOptions)
        }, cancellationToken: ct);

    public async Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default)
    {
        var snapshot = await RunDocument(runId).Collection("artifacts").OrderBy("createdAtUtc").GetSnapshotAsync(ct);
        return snapshot.Documents.Select(document => Read<ExecutionArtifact>(document, "artifactJson"))
            .Where(item => item is not null).Select(item => item!).ToList();
    }

    public async Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default)
    {
        var artifacts = await ListArtifactsAsync(runId, ct);
        return artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.Id, artifactRef, StringComparison.Ordinal) ||
            string.Equals(artifact.Name, artifactRef, StringComparison.Ordinal));
    }

    public async Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateLeaseRequest(request);
        var leaseKey = request.LeaseKey.Trim();
        var ownerId = request.OwnerId.Trim();
        var document = Leases.Document(LeaseDocumentId(leaseKey));
        try
        {
            return await _db.RunTransactionAsync(async transaction =>
            {
                var now = DateTime.UtcNow;
                var snapshot = await transaction.GetSnapshotAsync(document, transaction.CancellationToken);
                var existing = snapshot.Exists ? Read<ExecutionLease>(snapshot, "leaseJson") : null;
                if (existing is not null && existing.ExpiresAtUtc > now && !string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal))
                {
                    return null;
                }

                var lease = new ExecutionLease
                {
                    LeaseKey = leaseKey,
                    OwnerId = ownerId,
                    RunId = request.RunId,
                    AcquiredAtUtc = now,
                    ExpiresAtUtc = now.AddSeconds(request.TtlSeconds),
                    Metadata = CloneObject(request.Metadata)
                };
                transaction.Set(document, new Dictionary<string, object?>
                {
                    ["leaseKey"] = lease.LeaseKey,
                    ["expiresAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(lease.ExpiresAtUtc, DateTimeKind.Utc)),
                    ["leaseJson"] = JsonSerializer.Serialize(lease, JsonOptions)
                });
                return lease;
            }, options: _contentionTransactionOptions, cancellationToken: ct);
        }
        catch (RpcException ex) when (IsTransactionContention(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Atomically recovers a run whose worker lease has expired, or claims a due queued/waiting
    /// run. The run state and lease are committed in the same Firestore transaction so two Cloud
    /// Run workers cannot both receive the same run.
    /// </summary>
    public async Task<GoogleCloudExecutionLeaseClaim?> TryClaimExternalRunAsync(
        string runId,
        ExecutionLeaseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateLeaseRequest(request);
        var normalizedRunId = RequireId(runId, "Execution run id");
        var leaseKey = request.LeaseKey.Trim();
        var ownerId = request.OwnerId.Trim();
        var runDocument = RunDocument(normalizedRunId);
        var leaseDocument = Leases.Document(LeaseDocumentId(leaseKey));

        try
        {
            return await _db.RunTransactionAsync(async transaction =>
            {
                var now = DateTime.UtcNow;
                var snapshots = await transaction.GetAllSnapshotsAsync(
                    [runDocument, leaseDocument],
                    transaction.CancellationToken);
                var runSnapshot = snapshots[0];
                var leaseSnapshot = snapshots[1];
                var run = runSnapshot.Exists ? Read<ExecutionRun>(runSnapshot, "runJson") : null;
                var existingLease = leaseSnapshot.Exists ? Read<ExecutionLease>(leaseSnapshot, "leaseJson") : null;
                if (run is null)
                {
                    return null;
                }

                var recovered = false;
                if (run.Status == ExecutionRunStatuses.Running)
                {
                    if (existingLease is not null && existingLease.ExpiresAtUtc > now)
                    {
                        return null;
                    }

                    ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Queued, ExecutionTransitionKind.Recovery);
                    run.Status = ExecutionRunStatuses.Queued;
                    run.CurrentStep = null;
                    run.UpdatedAtUtc = now;
                    recovered = true;
                }
                else
                {
                    if (existingLease is not null && existingLease.ExpiresAtUtc > now)
                    {
                        return null;
                    }

                    if (run.Status is not (ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting) ||
                        (run.ScheduledAtUtc.HasValue && run.ScheduledAtUtc.Value > now) ||
                        run.CancellationRequested)
                    {
                        return null;
                    }
                }

                // A running worker observes cancellation through its lease payload. Once that lease
                // has expired, cancellation wins over recovery: do not re-execute a cancelled run.
                if (run.CancellationRequested)
                {
                    ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Cancelled);
                    run.Status = ExecutionRunStatuses.Cancelled;
                    run.FailureClass = ExecutionFailureClasses.Cancelled;
                    run.Error = "Execution run was cancelled.";
                    run.CompletedAtUtc = now;
                    run.UpdatedAtUtc = now;
                    run.DurationMs = (now - (run.StartedAtUtc ?? now)).TotalMilliseconds;
                    transaction.Set(runDocument, BuildRunDocument(run));
                    if (leaseSnapshot.Exists) transaction.Delete(leaseDocument);
                    return null;
                }

                var lease = new ExecutionLease
                {
                    LeaseKey = leaseKey,
                    OwnerId = ownerId,
                    RunId = normalizedRunId,
                    AcquiredAtUtc = now,
                    ExpiresAtUtc = now.AddSeconds(request.TtlSeconds),
                    Metadata = CloneObject(request.Metadata)
                };
                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Running);
                run.Status = ExecutionRunStatuses.Running;
                run.Attempt++;
                run.StartedAtUtc ??= now;
                run.UpdatedAtUtc = now;
                transaction.Set(runDocument, BuildRunDocument(run));
                transaction.Set(leaseDocument, BuildLeaseDocument(lease));
                transaction.Delete(WorkItemDocument(run));
                return new GoogleCloudExecutionLeaseClaim
                {
                    Run = run,
                    Lease = lease,
                    Recovered = recovered
                };
            }, options: _contentionTransactionOptions, cancellationToken: ct);
        }
        catch (RpcException ex) when (IsTransactionContention(ex))
        {
            // A claim is a competitive admission attempt. If the SDK exhausts its bounded retry
            // budget, normalize the losing contender to "not acquired" so callers can poll again
            // instead of surfacing a provider exception after another worker has won the lease.
            return null;
        }
    }

    internal static bool IsTransactionContention(Exception exception) =>
        exception is RpcException { StatusCode: StatusCode.Aborted };

    /// <summary>Renews an active lease only when it still belongs to the same owner.</summary>
    public async Task<ExecutionLease?> RenewLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateLeaseRequest(request);
        var document = Leases.Document(LeaseDocumentId(request.LeaseKey.Trim()));
        return await _db.RunTransactionAsync(async transaction =>
        {
            var now = DateTime.UtcNow;
            var snapshot = await transaction.GetSnapshotAsync(document, transaction.CancellationToken);
            var existing = snapshot.Exists ? Read<ExecutionLease>(snapshot, "leaseJson") : null;
            if (existing is null || existing.ExpiresAtUtc <= now || !string.Equals(existing.OwnerId, request.OwnerId.Trim(), StringComparison.Ordinal))
            {
                return null;
            }

            existing.ExpiresAtUtc = now.AddSeconds(request.TtlSeconds);
            existing.Metadata = request.Metadata is null ? existing.Metadata : JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(request.Metadata.ToJsonString(JsonOptions), JsonOptions);
            transaction.Set(document, BuildLeaseDocument(existing));
            return existing;
        }, cancellationToken: ct);
    }

    public async Task<ExecutionRun> UpdateExternalRunUnderLeaseAsync(
        string leaseKey,
        string ownerId,
        ExecutionRunUpdate update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var leaseDocument = Leases.Document(LeaseDocumentId(RequireId(leaseKey, "Lease key")));
        return await _db.RunTransactionAsync(async transaction =>
        {
            var now = DateTime.UtcNow;
            var leaseSnapshot = await transaction.GetSnapshotAsync(leaseDocument, transaction.CancellationToken);
            var lease = leaseSnapshot.Exists ? Read<ExecutionLease>(leaseSnapshot, "leaseJson") : null;
            EnsureActiveExternalLease(lease, ownerId, now);
            var runDocument = RunDocument(lease!.RunId!);
            var runSnapshot = await transaction.GetSnapshotAsync(runDocument, transaction.CancellationToken);
            var run = runSnapshot.Exists ? Read<ExecutionRun>(runSnapshot, "runJson") : null;
            if (run is null || run.Status != ExecutionRunStatuses.Running) throw new InvalidOperationException("External worker run is not running.");
            ApplyRunUpdate(run, update);
            run.UpdatedAtUtc = now;
            transaction.Set(runDocument, BuildRunDocument(run));
            return run;
        }, cancellationToken: ct);
    }

    public async Task AppendHistoryUnderLeaseAsync(
        string leaseKey,
        string ownerId,
        ExecutionTraceEvent item,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ExecutionContractValidator.ValidateTraceEvent(item);
        var leaseDocument = Leases.Document(LeaseDocumentId(RequireId(leaseKey, "Lease key")));
        await _db.RunTransactionAsync(async transaction =>
        {
            var leaseSnapshot = await transaction.GetSnapshotAsync(leaseDocument, transaction.CancellationToken);
            var lease = leaseSnapshot.Exists ? Read<ExecutionLease>(leaseSnapshot, "leaseJson") : null;
            EnsureActiveExternalLease(lease, ownerId, DateTime.UtcNow);
            if (!string.Equals(lease!.RunId, item.RunId, StringComparison.Ordinal)) throw new InvalidOperationException("External worker history run does not match its lease.");
            transaction.Set(RunDocument(item.RunId).Collection("history").Document(item.Id), BuildHistoryDocument(item));
        }, cancellationToken: ct);
    }

    public async Task PutArtifactUnderLeaseAsync(
        string leaseKey,
        string ownerId,
        ExecutionArtifact artifact,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var leaseDocument = Leases.Document(LeaseDocumentId(RequireId(leaseKey, "Lease key")));
        await _db.RunTransactionAsync(async transaction =>
        {
            var leaseSnapshot = await transaction.GetSnapshotAsync(leaseDocument, transaction.CancellationToken);
            var lease = leaseSnapshot.Exists ? Read<ExecutionLease>(leaseSnapshot, "leaseJson") : null;
            EnsureActiveExternalLease(lease, ownerId, DateTime.UtcNow);
            if (!string.Equals(lease!.RunId, artifact.RunId, StringComparison.Ordinal)) throw new InvalidOperationException("External worker artifact run does not match its lease.");
            transaction.Set(RunDocument(artifact.RunId).Collection("artifacts").Document(RequireId(artifact.Id, "Artifact id")), BuildArtifactDocument(artifact));
        }, cancellationToken: ct);
    }

    /// <summary>Writes a checkpoint only while the worker's lease is still active.</summary>
    public async Task PutCheckpointUnderLeaseAsync(
        string leaseKey,
        string ownerId,
        ExecutionCheckpoint checkpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var leaseDocument = Leases.Document(LeaseDocumentId(RequireId(leaseKey, "Lease key")));
        var checkpointDocument = RunDocument(checkpoint.RunId).Collection("checkpoints").Document(RequireId(checkpoint.Key, "Checkpoint key"));
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(leaseDocument, transaction.CancellationToken);
            var lease = snapshot.Exists ? Read<ExecutionLease>(snapshot, "leaseJson") : null;
            if (lease is null || lease.ExpiresAtUtc <= DateTime.UtcNow || !string.Equals(lease.OwnerId, ownerId, StringComparison.Ordinal) || !string.Equals(lease.RunId, checkpoint.RunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("External worker lease is no longer active.");
            }

            transaction.Set(checkpointDocument, new Dictionary<string, object?>
            {
                ["checkpointJson"] = JsonSerializer.Serialize(checkpoint, JsonOptions),
                ["updatedAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(checkpoint.UpdatedAtUtc.ToUniversalTime(), DateTimeKind.Utc))
            });
        }, cancellationToken: ct);
    }

    public async Task<ExecutionCheckpoint?> GetCheckpointUnderLeaseAsync(
        string leaseKey,
        string ownerId,
        string key,
        CancellationToken ct = default)
    {
        var leaseDocument = Leases.Document(LeaseDocumentId(RequireId(leaseKey, "Lease key")));
        var checkpointKey = RequireId(key, "Checkpoint key");
        return await _db.RunTransactionAsync(async transaction =>
        {
            var leaseSnapshot = await transaction.GetSnapshotAsync(leaseDocument, transaction.CancellationToken);
            var lease = leaseSnapshot.Exists ? Read<ExecutionLease>(leaseSnapshot, "leaseJson") : null;
            EnsureActiveExternalLease(lease, ownerId, DateTime.UtcNow);
            var checkpointSnapshot = await transaction.GetSnapshotAsync(
                RunDocument(lease!.RunId!).Collection("checkpoints").Document(checkpointKey),
                transaction.CancellationToken);
            return checkpointSnapshot.Exists ? Read<ExecutionCheckpoint>(checkpointSnapshot, "checkpointJson") : null;
        }, cancellationToken: ct);
    }

    public async Task<ExecutionWaitResult?> TakeWaitOutcomeUnderLeaseAsync(
        string leaseKey,
        string ownerId,
        string runId,
        string kind,
        string name,
        CancellationToken ct = default)
    {
        var normalizedRunId = RequireId(runId, "Execution run id");
        var leaseDocument = Leases.Document(LeaseDocumentId(RequireId(leaseKey, "Lease key")));
        var outcomeDocument = RunDocument(normalizedRunId).Collection("coordination").Document("outcome-" + WaitDocumentId(kind, name));
        return await _db.RunTransactionAsync(async transaction =>
        {
            var leaseSnapshot = await transaction.GetSnapshotAsync(leaseDocument, transaction.CancellationToken);
            var lease = leaseSnapshot.Exists ? Read<ExecutionLease>(leaseSnapshot, "leaseJson") : null;
            EnsureActiveExternalLease(lease, ownerId, DateTime.UtcNow);
            if (!string.Equals(lease!.RunId, normalizedRunId, StringComparison.Ordinal)) throw new InvalidOperationException("External worker wait run does not match its lease.");
            var outcomeSnapshot = await transaction.GetSnapshotAsync(outcomeDocument, transaction.CancellationToken);
            var outcome = outcomeSnapshot.Exists ? Read<ExecutionWaitResult>(outcomeSnapshot, "outcomeJson") : null;
            if (outcome is not null) transaction.Delete(outcomeDocument);
            return outcome;
        }, cancellationToken: ct);
    }

    public async Task<ExecutionExternalEvent?> TakeExternalEventUnderLeaseAsync(
        string leaseKey,
        string ownerId,
        string runId,
        string name,
        CancellationToken ct = default)
    {
        var normalizedRunId = RequireId(runId, "Execution run id");
        var leaseDocument = Leases.Document(LeaseDocumentId(RequireId(leaseKey, "Lease key")));
        var events = await RunDocument(normalizedRunId).Collection("externalEvents").WhereEqualTo("name", name).GetSnapshotAsync(ct);
        foreach (var candidate in events.Documents
            .Select(document => Read<ExecutionExternalEvent>(document, "eventJson"))
            .Where(externalEvent => externalEvent is not null)
            .Select(externalEvent => externalEvent!)
            .OrderBy(externalEvent => externalEvent.RaisedAtUtc)
            .ThenBy(externalEvent => externalEvent.Id, StringComparer.Ordinal))
        {
            var consumption = RunDocument(normalizedRunId).Collection("coordination").Document("event-" + WaitDocumentId("event", candidate.Id));
            var taken = await _db.RunTransactionAsync(async transaction =>
            {
                var leaseSnapshot = await transaction.GetSnapshotAsync(leaseDocument, transaction.CancellationToken);
                var lease = leaseSnapshot.Exists ? Read<ExecutionLease>(leaseSnapshot, "leaseJson") : null;
                EnsureActiveExternalLease(lease, ownerId, DateTime.UtcNow);
                if (!string.Equals(lease!.RunId, normalizedRunId, StringComparison.Ordinal)) throw new InvalidOperationException("External worker event run does not match its lease.");
                var consumed = await transaction.GetSnapshotAsync(consumption, transaction.CancellationToken);
                if (consumed.Exists) return false;
                transaction.Create(consumption, new Dictionary<string, object?> { ["eventId"] = candidate.Id });
                return true;
            }, cancellationToken: ct);
            if (taken) return candidate;
        }

        // No durable event was available, but still verify the worker did not lose the lease while
        // polling the inbox.
        await _db.RunTransactionAsync(async transaction =>
        {
            var leaseSnapshot = await transaction.GetSnapshotAsync(leaseDocument, transaction.CancellationToken);
            var lease = leaseSnapshot.Exists ? Read<ExecutionLease>(leaseSnapshot, "leaseJson") : null;
            EnsureActiveExternalLease(lease, ownerId, DateTime.UtcNow);
            if (!string.Equals(lease!.RunId, normalizedRunId, StringComparison.Ordinal)) throw new InvalidOperationException("External worker event run does not match its lease.");
        }, cancellationToken: ct);
        return null;
    }

    public async Task<ExecutionRun> SuspendExternalRunUnderLeaseAsync(
        string leaseKey,
        string ownerId,
        GoogleCloudExecutionWait wait,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wait);
        var leaseDocument = Leases.Document(LeaseDocumentId(RequireId(leaseKey, "Lease key")));
        return await _db.RunTransactionAsync(async transaction =>
        {
            var now = DateTime.UtcNow;
            var leaseSnapshot = await transaction.GetSnapshotAsync(leaseDocument, transaction.CancellationToken);
            var lease = leaseSnapshot.Exists ? Read<ExecutionLease>(leaseSnapshot, "leaseJson") : null;
            EnsureActiveExternalLease(lease, ownerId, now);
            if (!string.Equals(lease!.RunId, wait.RunId, StringComparison.Ordinal)) throw new InvalidOperationException("External worker wait run does not match its lease.");
            var runDocument = RunDocument(wait.RunId);
            var runSnapshot = await transaction.GetSnapshotAsync(runDocument, transaction.CancellationToken);
            var run = runSnapshot.Exists ? Read<ExecutionRun>(runSnapshot, "runJson") : null;
            if (run is null || run.Status != ExecutionRunStatuses.Running) throw new InvalidOperationException("External worker run is not running.");

            if (wait.Timer is not null)
            {
                transaction.Set(runDocument.Collection("timers").Document(wait.Timer.Id), new Dictionary<string, object?>
                {
                    ["fireAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(wait.Timer.FireAtUtc.ToUniversalTime(), DateTimeKind.Utc)),
                    ["timerJson"] = JsonSerializer.Serialize(wait.Timer, JsonOptions)
                });
            }
            transaction.Set(runDocument.Collection("coordination").Document("active-wait"), new Dictionary<string, object?>
            {
                ["waitJson"] = JsonSerializer.Serialize(wait, JsonOptions),
                ["fireAtUtc"] = wait.FireAtUtc.HasValue ? Timestamp.FromDateTime(DateTime.SpecifyKind(wait.FireAtUtc.Value.ToUniversalTime(), DateTimeKind.Utc)) : null
            });
            ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.DurableWait);
            run.Status = ExecutionRunStatuses.Waiting;
            run.ScheduledAtUtc = wait.FireAtUtc;
            run.CurrentStep = $"waiting:{wait.Kind}:{wait.Name}";
            run.UpdatedAtUtc = now;
            transaction.Set(runDocument, BuildRunDocument(run));
            ApplyWorkItem(transaction, run);
            transaction.Delete(leaseDocument);
            return run;
        }, cancellationToken: ct);
    }

    public async Task<GoogleCloudExecutionExternalCompletion> CompleteExternalRunUnderLeaseAsync(
        string leaseKey,
        string ownerId,
        ExecutionRunResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var leaseDocument = Leases.Document(LeaseDocumentId(RequireId(leaseKey, "Lease key")));
        return await _db.RunTransactionAsync(async transaction =>
        {
            var now = DateTime.UtcNow;
            var leaseSnapshot = await transaction.GetSnapshotAsync(leaseDocument, transaction.CancellationToken);
            var lease = leaseSnapshot.Exists ? Read<ExecutionLease>(leaseSnapshot, "leaseJson") : null;
            if (lease is null || !string.Equals(lease.OwnerId, ownerId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(lease.RunId))
                throw new InvalidOperationException("External worker lease is no longer active.");
            var runDocument = RunDocument(lease.RunId);
            var runSnapshot = await transaction.GetSnapshotAsync(runDocument, transaction.CancellationToken);
            var run = (runSnapshot.Exists ? Read<ExecutionRun>(runSnapshot, "runJson") : null)
                ?? throw new InvalidOperationException("External worker run was not found.");
            if (IsCompletedExternalLease(lease) && (ExecutionRunStatuses.IsTerminal(run.Status) || run.Status == ExecutionRunStatuses.Waiting))
                return new GoogleCloudExecutionExternalCompletion { Run = run, AlreadyCompleted = true };
            EnsureActiveExternalLease(lease, ownerId, now);
            if (run.Status != ExecutionRunStatuses.Running) throw new InvalidOperationException("External worker run is not running.");

            var terminal = run.CancellationRequested && result.Status != ExecutionRunStatuses.TimedOut
                ? ExecutionRunStatuses.Cancelled
                : result.Status;
            ExecutionRunLifecycle.EnsureTransition(run.Status, terminal);
            run.Status = terminal;
            run.Result = CloneNode(result.Result);
            run.StatusDetails = CloneObject(result.StatusDetails);
            run.FailureClass = terminal == ExecutionRunStatuses.Cancelled ? ExecutionFailureClasses.Cancelled : result.FailureClass;
            run.Error = terminal == ExecutionRunStatuses.Cancelled ? "Execution run was cancelled." : result.Error;
            var retryScheduled = !run.CancellationRequested && (terminal is ExecutionRunStatuses.Failed or ExecutionRunStatuses.TimedOut) && run.Attempt < Math.Max(1, run.MaxAttempts);
            if (retryScheduled)
            {
                var delay = RetryDelay(run);
                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.Retry);
                run.Status = ExecutionRunStatuses.Waiting;
                run.ScheduledAtUtc = now.Add(delay);
                run.UpdatedAtUtc = now;
                run.CurrentStep = null;
            }
            else
            {
                run.CompletedAtUtc = now;
                run.UpdatedAtUtc = now;
                run.DurationMs = (now - (run.StartedAtUtc ?? now)).TotalMilliseconds;
                if (run.Status == ExecutionRunStatuses.Succeeded) run.Progress = 1;
            }

            lease.ExpiresAtUtc = now;
            lease.Metadata ??= new JsonObject();
            lease.Metadata["state"] = "completed";
            await AdjustActiveRunCountIfTrackedAsync(transaction, runSnapshot.Exists ? Read<ExecutionRun>(runSnapshot, "runJson") : null, run, transaction.CancellationToken);
            transaction.Set(runDocument, BuildRunDocument(run));
            ApplyWorkItem(transaction, run);
            transaction.Set(leaseDocument, BuildLeaseDocument(lease));
            return new GoogleCloudExecutionExternalCompletion { Run = run, RetryScheduled = retryScheduled };
        }, cancellationToken: ct);
    }

    public async Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default)
    {
        var normalizedKey = RequireId(leaseKey, "Lease key");
        var normalizedOwner = RequireId(ownerId, "Lease owner id");
        var document = Leases.Document(LeaseDocumentId(normalizedKey));
        return await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(document, transaction.CancellationToken);
            var existing = snapshot.Exists ? Read<ExecutionLease>(snapshot, "leaseJson") : null;
            if (existing is null || !string.Equals(existing.OwnerId, normalizedOwner, StringComparison.Ordinal))
            {
                return false;
            }

            transaction.Delete(document);
            return true;
        }, cancellationToken: ct);
    }

    public async Task<ExecutionLease?> GetLeaseAsync(string leaseKey, CancellationToken ct = default)
    {
        var snapshot = await Leases.Document(LeaseDocumentId(RequireId(leaseKey, "Lease key"))).GetSnapshotAsync(ct);
        return snapshot.Exists ? Read<ExecutionLease>(snapshot, "leaseJson") : null;
    }

    public Task PutTimerAsync(ExecutionTimer timer, CancellationToken ct = default) =>
        RunDocument(timer.RunId ?? throw new InvalidOperationException("Google execution timers must be run-owned.")).Collection("timers").Document(timer.Id).SetAsync(new Dictionary<string, object?>
        {
            ["fireAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(timer.FireAtUtc.ToUniversalTime(), DateTimeKind.Utc)),
            ["timerJson"] = JsonSerializer.Serialize(timer, JsonOptions)
        }, cancellationToken: ct);

    public Task PutExternalEventAsync(ExecutionExternalEvent externalEvent, CancellationToken ct = default) =>
        RunDocument(externalEvent.RunId ?? throw new InvalidOperationException("Google execution events must be run-owned.")).Collection("externalEvents").Document(externalEvent.Id).SetAsync(new Dictionary<string, object?>
        {
            ["name"] = externalEvent.Name,
            ["raisedAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(externalEvent.RaisedAtUtc.ToUniversalTime(), DateTimeKind.Utc)),
            ["eventJson"] = JsonSerializer.Serialize(externalEvent, JsonOptions)
        }, cancellationToken: ct);

    public Task PutWaitAsync(GoogleCloudExecutionWait wait, CancellationToken ct = default) =>
        RunDocument(wait.RunId).Collection("coordination").Document("active-wait").SetAsync(new Dictionary<string, object?>
        {
            ["waitJson"] = JsonSerializer.Serialize(wait, JsonOptions),
            ["fireAtUtc"] = wait.FireAtUtc.HasValue ? Timestamp.FromDateTime(DateTime.SpecifyKind(wait.FireAtUtc.Value.ToUniversalTime(), DateTimeKind.Utc)) : null
        }, cancellationToken: ct);

    public async Task<GoogleCloudExecutionWait?> GetWaitAsync(string runId, CancellationToken ct = default)
    {
        var snapshot = await RunDocument(runId).Collection("coordination").Document("active-wait").GetSnapshotAsync(ct);
        return snapshot.Exists ? Read<GoogleCloudExecutionWait>(snapshot, "waitJson") : null;
    }

    public Task DeleteWaitAsync(string runId, CancellationToken ct = default) =>
        RunDocument(runId).Collection("coordination").Document("active-wait").DeleteAsync(cancellationToken: ct);

    public Task PutWaitOutcomeAsync(string runId, GoogleCloudExecutionWait wait, ExecutionWaitResult outcome, CancellationToken ct = default) =>
        RunDocument(runId).Collection("coordination").Document("outcome-" + WaitDocumentId(wait.Kind, wait.Name)).SetAsync(new Dictionary<string, object?>
        {
            ["outcomeJson"] = JsonSerializer.Serialize(outcome, JsonOptions)
        }, cancellationToken: ct);

    public async Task<ExecutionWaitResult?> TakeWaitOutcomeAsync(string runId, string kind, string name, CancellationToken ct = default)
    {
        var document = RunDocument(runId).Collection("coordination").Document("outcome-" + WaitDocumentId(kind, name));
        return await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(document, transaction.CancellationToken);
            var outcome = snapshot.Exists ? Read<ExecutionWaitResult>(snapshot, "outcomeJson") : null;
            if (outcome is not null) transaction.Delete(document);
            return outcome;
        }, cancellationToken: ct);
    }

    public async Task<ExecutionExternalEvent?> TakeExternalEventAsync(string runId, string name, CancellationToken ct = default)
    {
        // Do not require a caller-provisioned composite index merely to consume an event. The
        // event subcollection is bounded by run retention; select the oldest matching document
        // in memory, then use a consumption marker transaction for exactly-once ownership.
        var events = await RunDocument(runId).Collection("externalEvents").WhereEqualTo("name", name).GetSnapshotAsync(ct);
        foreach (var candidate in events.Documents
            .Select(document => new { Document = document, Event = Read<ExecutionExternalEvent>(document, "eventJson") })
            .Where(item => item.Event is not null)
            .OrderBy(item => item.Event!.RaisedAtUtc)
            .ThenBy(item => item.Event!.Id, StringComparer.Ordinal))
        {
            var externalEvent = candidate.Event!;
            var consumption = RunDocument(runId).Collection("coordination").Document("event-" + WaitDocumentId("event", externalEvent.Id));
            var taken = await _db.RunTransactionAsync(async transaction =>
            {
                var consumed = await transaction.GetSnapshotAsync(consumption, transaction.CancellationToken);
                if (consumed.Exists) return false;
                transaction.Create(consumption, new Dictionary<string, object?> { ["eventId"] = externalEvent.Id });
                return true;
            }, cancellationToken: ct);
            if (taken) return externalEvent;
        }

        return null;
    }

    /// <summary>Deletes a terminal run and its fixed run-owned subcollections.</summary>
    public async Task<GoogleCloudExecutionRunDeletion> DeleteRunAsync(ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var result = new GoogleCloudExecutionRunDeletion { RunId = run.Id };
        var runDocument = RunDocument(run.Id);
        result.History = await DeleteCollectionAsync(runDocument.Collection("history"), ct);
        result.Checkpoints = await DeleteCollectionAsync(runDocument.Collection("checkpoints"), ct);
        result.Artifacts = await DeleteCollectionAsync(runDocument.Collection("artifacts"), ct);
        result.Timers = await DeleteCollectionAsync(runDocument.Collection("timers"), ct);
        result.ExternalEvents = await DeleteCollectionAsync(runDocument.Collection("externalEvents"), ct);
        result.Coordination = await DeleteCollectionAsync(runDocument.Collection("coordination"), ct);

        var batch = _db.StartBatch();
        batch.Delete(WorkItemDocument(run));
        batch.Delete(Leases.Document(LeaseDocumentId("external-worker-run-" + run.Id)));
        if (!string.IsNullOrWhiteSpace(run.IdempotencyKey)) batch.Delete(IdempotencyReservations.Document(IdempotencyDocumentId(BuildIdempotencyScopeKey(run))));
        batch.Delete(runDocument);
        await batch.CommitAsync(ct);
        result.Runs = 1;
        return result;
    }

    private async Task<int> DeleteCollectionAsync(CollectionReference collection, CancellationToken ct)
    {
        var snapshot = await collection.GetSnapshotAsync(ct);
        if (snapshot.Count == 0) return 0;
        var batch = _db.StartBatch();
        foreach (var document in snapshot.Documents) batch.Delete(document.Reference);
        await batch.CommitAsync(ct);
        return snapshot.Count;
    }

    private CollectionReference Runs => _db.Collection(_rootCollection).Document("state").Collection("runs");
    private CollectionReference Leases => _db.Collection(_rootCollection).Document("state").Collection("leases");
    private CollectionReference IdempotencyReservations => _db.Collection(_rootCollection).Document("state").Collection("idempotency");
    private DocumentReference ActiveRunCounter => _db.Collection(_rootCollection).Document("state").Collection("metadata").Document("active-run-count");
    private CollectionReference WorkItems(string handlerId) => _db.Collection(_rootCollection).Document("state").Collection("workers").Document(HandlerDocumentId(handlerId)).Collection("runnable");
    private DocumentReference RunDocument(string runId) => Runs.Document(RequireId(runId, "Execution run id"));
    private DocumentReference WorkItemDocument(ExecutionRun run) => WorkItems(run.HandlerId).Document(RequireId(run.Id, "Execution run id"));

    private void ApplyWorkItem(WriteBatch batch, ExecutionRun run)
    {
        var workItem = WorkItemDocument(run);
        if (!IsRunnable(run))
        {
            batch.Delete(workItem);
            return;
        }

        var dueAtUtc = run.ScheduledAtUtc?.ToUniversalTime() ?? run.UpdatedAtUtc.ToUniversalTime();
        batch.Set(workItem, new Dictionary<string, object?>
        {
            ["runId"] = run.Id,
            ["dueAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(dueAtUtc, DateTimeKind.Utc)),
            ["status"] = run.Status,
            ["updatedAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(run.UpdatedAtUtc.ToUniversalTime(), DateTimeKind.Utc))
        });
    }

    private void ApplyWorkItem(Transaction transaction, ExecutionRun run)
    {
        var workItem = WorkItemDocument(run);
        if (!IsRunnable(run))
        {
            transaction.Delete(workItem);
            return;
        }

        var dueAtUtc = run.ScheduledAtUtc?.ToUniversalTime() ?? run.UpdatedAtUtc.ToUniversalTime();
        transaction.Set(workItem, new Dictionary<string, object?>
        {
            ["runId"] = run.Id,
            ["dueAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(dueAtUtc, DateTimeKind.Utc)),
            ["status"] = run.Status,
            ["updatedAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(run.UpdatedAtUtc.ToUniversalTime(), DateTimeKind.Utc))
        });
    }

    private static bool IsRunnable(ExecutionRun run) => run.Status == ExecutionRunStatuses.Queued ||
        (run.Status == ExecutionRunStatuses.Waiting && run.ScheduledAtUtc.HasValue);

    private async Task<int> CountActiveRunsForCounterInitializationAsync(Transaction transaction, int maxActiveRuns, CancellationToken ct)
    {
        var activeRuns = 0;
        foreach (var status in ExecutionRunLifecycle.ActiveStatuses)
        {
            if (activeRuns >= maxActiveRuns) break;
            var snapshot = await transaction.GetSnapshotAsync(Runs.WhereEqualTo("status", status).Limit(maxActiveRuns - activeRuns), ct);
            activeRuns += snapshot.Count;
        }

        return activeRuns;
    }

    private async Task AdjustActiveRunCountIfTrackedAsync(
        Transaction transaction,
        ExecutionRun? previous,
        ExecutionRun current,
        CancellationToken ct)
    {
        var counterSnapshot = await transaction.GetSnapshotAsync(ActiveRunCounter, ct);
        if (!counterSnapshot.Exists) return;
        var delta = (ExecutionRunLifecycle.IsActive(current.Status) ? 1 : 0) -
            (previous is not null && ExecutionRunLifecycle.IsActive(previous.Status) ? 1 : 0);
        if (delta == 0) return;
        transaction.Set(ActiveRunCounter, new Dictionary<string, object?>
        {
            ["activeRuns"] = Math.Max(0, ReadActiveRunCount(counterSnapshot) + delta)
        });
    }

    private static int ReadActiveRunCount(DocumentSnapshot snapshot) =>
        snapshot.TryGetValue<long>("activeRuns", out var activeRuns)
            ? checked((int)Math.Max(0, activeRuns))
            : 0;

    private static Dictionary<string, object?> BuildRunDocument(ExecutionRun run) => new()
    {
        ["handlerId"] = run.HandlerId,
        ["pluginId"] = run.PluginId,
        ["status"] = run.Status,
        ["correlationId"] = run.CorrelationId,
        ["idempotencyKey"] = run.IdempotencyKey,
        ["createdAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(run.CreatedAtUtc.ToUniversalTime(), DateTimeKind.Utc)),
        ["updatedAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(run.UpdatedAtUtc.ToUniversalTime(), DateTimeKind.Utc)),
        ["runJson"] = JsonSerializer.Serialize(run, JsonOptions)
    };

    private static Dictionary<string, object?> BuildRunCreationReservation(ExecutionRun run) => new()
    {
        ["runId"] = run.Id,
        ["handlerId"] = run.HandlerId,
        ["payloadHash"] = run.PayloadHash,
        ["createdAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(run.CreatedAtUtc.ToUniversalTime(), DateTimeKind.Utc))
    };

    private static GoogleCloudExecutionRunCreation ReadRunCreationReservation(DocumentSnapshot snapshot)
    {
        if (!snapshot.TryGetValue<string>("runId", out var runId) ||
            !snapshot.TryGetValue<string>("handlerId", out var handlerId) ||
            !snapshot.TryGetValue<string>("payloadHash", out var payloadHash) ||
            string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(handlerId) || string.IsNullOrWhiteSpace(payloadHash))
        {
            throw new InvalidOperationException("Execution idempotency reservation is malformed.");
        }

        return new GoogleCloudExecutionRunCreation
        {
            Created = false,
            RunId = runId,
            HandlerId = handlerId,
            PayloadHash = payloadHash
        };
    }

    private static Dictionary<string, object?> BuildLeaseDocument(ExecutionLease lease) => new()
    {
        ["leaseKey"] = lease.LeaseKey,
        ["expiresAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(lease.ExpiresAtUtc.ToUniversalTime(), DateTimeKind.Utc)),
        ["leaseJson"] = JsonSerializer.Serialize(lease, JsonOptions)
    };

    private static Dictionary<string, object?> BuildHistoryDocument(ExecutionTraceEvent item) => new()
    {
        ["timestampUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(item.TimestampUtc.ToUniversalTime(), DateTimeKind.Utc)),
        ["eventJson"] = JsonSerializer.Serialize(item, JsonOptions)
    };

    private static Dictionary<string, object?> BuildArtifactDocument(ExecutionArtifact artifact) => new()
    {
        ["name"] = artifact.Name,
        ["createdAtUtc"] = Timestamp.FromDateTime(DateTime.SpecifyKind(artifact.CreatedAtUtc.ToUniversalTime(), DateTimeKind.Utc)),
        ["artifactJson"] = JsonSerializer.Serialize(artifact, JsonOptions)
    };

    private static void EnsureActiveExternalLease(ExecutionLease? lease, string ownerId, DateTime now)
    {
        if (lease is null || lease.ExpiresAtUtc <= now || !string.Equals(lease.OwnerId, ownerId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(lease.RunId))
            throw new InvalidOperationException("External worker lease is no longer active.");
    }

    private static bool IsCompletedExternalLease(ExecutionLease lease) =>
        string.Equals(lease.Metadata?["state"]?.GetValue<string>(), "completed", StringComparison.Ordinal);

    private static void ApplyRunUpdate(ExecutionRun run, ExecutionRunUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.Status) && update.Status != ExecutionRunStatuses.Running)
            throw new InvalidOperationException("External worker progress updates may only report the running status.");
        run.Requested = update.Requested ?? run.Requested;
        run.Attempted = update.Attempted ?? run.Attempted;
        run.Succeeded = update.Succeeded ?? run.Succeeded;
        run.Failed = update.Failed ?? run.Failed;
        run.Progress = update.Progress.HasValue ? Math.Clamp(update.Progress.Value, 0, 1) : run.Progress;
        run.CurrentStep = update.CurrentStep ?? run.CurrentStep;
        run.FailureClass = update.FailureClass ?? run.FailureClass;
        run.Error = update.Error ?? run.Error;
        run.Result = update.Result is null ? run.Result : CloneNode(update.Result);
        run.StatusDetails = update.StatusDetails is null ? run.StatusDetails : CloneObject(update.StatusDetails);
    }

    private static TimeSpan RetryDelay(ExecutionRun run)
    {
        var policy = run.RetryPolicy ?? new ExecutionRetryPolicy();
        var seconds = Math.Min(
            Math.Max(policy.InitialDelaySeconds, 0) * Math.Pow(Math.Max(policy.BackoffMultiplier, 1), Math.Max(0, run.Attempt - 1)),
            Math.Max(policy.InitialDelaySeconds, policy.MaxDelaySeconds));
        return TimeSpan.FromSeconds(seconds);
    }

    private static JsonNode? CloneNode(JsonNode? value) =>
        value is null ? null : JsonNode.Parse(value.ToJsonString(JsonOptions));

    private static JsonObject? CloneObject(JsonObject? value) =>
        value is null ? null : JsonSerializer.Deserialize<JsonObject>(value.ToJsonString(JsonOptions), JsonOptions);

    private static T? Read<T>(DocumentSnapshot snapshot, string field) =>
        snapshot.TryGetValue<string>(field, out var json) ? JsonSerializer.Deserialize<T>(json, JsonOptions) : default;

    private static bool Matches(ExecutionRun run, ExecutionRunQuery query) =>
        (string.IsNullOrWhiteSpace(query.HandlerId) || run.HandlerId == query.HandlerId) &&
        (string.IsNullOrWhiteSpace(query.PluginId) || run.PluginId == query.PluginId) &&
        (string.IsNullOrWhiteSpace(query.Status) || run.Status == query.Status) &&
        (string.IsNullOrWhiteSpace(query.CorrelationId) || run.CorrelationId == query.CorrelationId) &&
        (string.IsNullOrWhiteSpace(query.IdempotencyKey) || run.IdempotencyKey == query.IdempotencyKey) &&
        (!query.CreatedAfterUtc.HasValue || run.CreatedAtUtc >= query.CreatedAfterUtc.Value) &&
        (!query.CreatedBeforeUtc.HasValue || run.CreatedAtUtc <= query.CreatedBeforeUtc.Value) &&
        query.Tags.All(filter => run.Tags.TryGetValue(filter.Key, out var value) && value == filter.Value);

    private static string RequireId(string value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new InvalidOperationException($"{name} is required.");

    private static string NormalizeCollection(string value)
    {
        var normalized = RequireId(value, "Firestore execution root collection");
        if (normalized.Contains('/')) throw new InvalidOperationException("Firestore execution root collection cannot contain '/'.");
        return normalized;
    }

    private static string LeaseDocumentId(string leaseKey)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(leaseKey))).ToLowerInvariant();
    }

    private static string IdempotencyDocumentId(string scopeKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeKey))).ToLowerInvariant();

    private static string BuildIdempotencyScopeKey(ExecutionRun run) => string.Join("\n",
        run.Scope?.ProductId ?? string.Empty,
        run.Scope?.TenantId ?? string.Empty,
        run.HandlerId,
        run.IdempotencyKey ?? string.Empty);

    private static string WaitDocumentId(string kind, string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + name))).ToLowerInvariant();

    private static string HandlerDocumentId(string handlerId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RequireId(handlerId, "Execution handler id")))).ToLowerInvariant();
}

public sealed class GoogleCloudExecutionWait
{
    public string RunId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime? FireAtUtc { get; set; }
    public ExecutionTimer? Timer { get; set; }
}

public sealed class GoogleCloudExecutionLeaseClaim
{
    public required ExecutionRun Run { get; init; }
    public required ExecutionLease Lease { get; init; }
    public bool Recovered { get; init; }
}

public sealed class GoogleCloudExecutionExternalCompletion
{
    public required ExecutionRun Run { get; init; }
    public bool RetryScheduled { get; init; }
    public bool AlreadyCompleted { get; init; }
}

public sealed class GoogleCloudExecutionRunDeletion
{
    public required string RunId { get; init; }
    public int Runs { get; set; }
    public int History { get; set; }
    public int Checkpoints { get; set; }
    public int Artifacts { get; set; }
    public int Timers { get; set; }
    public int ExternalEvents { get; set; }
    public int Coordination { get; set; }
}
