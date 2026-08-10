using Vyral.Execution;

namespace Vyral.Execution.Aws;

/// <summary>
/// Persistence seam for the AWS queue-first execution adapter. The production implementation is
/// DynamoDB; tests may use a deterministic implementation without emulating AWS APIs. Every
/// method operates on portable execution state, never on SQS delivery payloads.
/// </summary>
public interface IAwsDynamoExecutionStateStore
{
    Task CreateRunAsync(ExecutionRun run, CancellationToken ct = default);
    Task<bool> TryCreateRunWithActiveCapacityAsync(ExecutionRun run, int maxActiveRuns, CancellationToken ct = default);
    Task<AwsDynamoExecutionRunCreation> CreateRunAtomicallyAsync(ExecutionRun run, ExecutionRun? capacityRejectedRun, int maxActiveRuns, string? idempotencyScopeKey, CancellationToken ct = default);
    Task UpsertRunAsync(ExecutionRun run, CancellationToken ct = default);
    /// <summary>
    /// Applies a cancellation request against the latest durable run version. Queued and waiting
    /// runs become terminal in the same transaction that removes their runnable work/wait state;
    /// running runs retain their lease but gain a durable cancellation fence for completion.
    /// </summary>
    Task<ExecutionRun?> CancelRunAtomicallyAsync(string runId, CancellationToken ct = default);
    /// <summary>
    /// Atomically turns one durable wait into its outcome and requeues the waiting run. Returns
    /// null when another event, timer, cancellation, or worker transition has already consumed
    /// that wait. When supplied, <paramref name="consumedEventId"/> is fenced as part of the
    /// same transition so an event cannot be replayed by a later wait with the same name.
    /// </summary>
    Task<ExecutionRun?> TryResumeWaitAsync(string runId, string expectedKind, string expectedName, ExecutionWaitResult outcome, string? consumedEventId = null, CancellationToken ct = default);
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
    Task<AwsDynamoExecutionLeaseClaim?> TryClaimExternalRunAsync(string runId, ExecutionLeaseRequest request, CancellationToken ct = default);
    Task<ExecutionLease?> RenewLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default);
    Task<ExecutionRun> UpdateExternalRunUnderLeaseAsync(string leaseKey, string ownerId, ExecutionRunUpdate update, CancellationToken ct = default);
    Task AppendHistoryUnderLeaseAsync(string leaseKey, string ownerId, ExecutionTraceEvent item, CancellationToken ct = default);
    Task PutArtifactUnderLeaseAsync(string leaseKey, string ownerId, ExecutionArtifact artifact, CancellationToken ct = default);
    Task PutCheckpointUnderLeaseAsync(string leaseKey, string ownerId, ExecutionCheckpoint checkpoint, CancellationToken ct = default);
    Task<ExecutionCheckpoint?> GetCheckpointUnderLeaseAsync(string leaseKey, string ownerId, string key, CancellationToken ct = default);
    Task<ExecutionWaitResult?> TakeWaitOutcomeUnderLeaseAsync(string leaseKey, string ownerId, string runId, string kind, string name, CancellationToken ct = default);
    Task<ExecutionExternalEvent?> TakeExternalEventUnderLeaseAsync(string leaseKey, string ownerId, string runId, string name, CancellationToken ct = default);
    Task<ExecutionRun> SuspendExternalRunUnderLeaseAsync(string leaseKey, string ownerId, AwsDynamoExecutionWait wait, CancellationToken ct = default);
    Task<AwsDynamoExecutionExternalCompletion> CompleteExternalRunUnderLeaseAsync(string leaseKey, string ownerId, ExecutionRunResult result, CancellationToken ct = default);
    Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default);
    Task<ExecutionLease?> GetLeaseAsync(string leaseKey, CancellationToken ct = default);
    Task PutTimerAsync(ExecutionTimer timer, CancellationToken ct = default);
    Task PutExternalEventAsync(ExecutionExternalEvent externalEvent, CancellationToken ct = default);
    Task PutWaitAsync(AwsDynamoExecutionWait wait, CancellationToken ct = default);
    Task<AwsDynamoExecutionWait?> GetWaitAsync(string runId, CancellationToken ct = default);
    Task DeleteWaitAsync(string runId, CancellationToken ct = default);
    Task PutWaitOutcomeAsync(string runId, AwsDynamoExecutionWait wait, ExecutionWaitResult outcome, CancellationToken ct = default);
    Task<ExecutionWaitResult?> TakeWaitOutcomeAsync(string runId, string kind, string name, CancellationToken ct = default);
    Task<ExecutionExternalEvent?> TakeExternalEventAsync(string runId, string name, CancellationToken ct = default);
    Task<AwsDynamoExecutionRunDeletion> DeleteRunAsync(ExecutionRun run, CancellationToken ct = default);
}

public sealed class AwsDynamoExecutionRunCreation
{
    public bool Created { get; init; }
    public required string RunId { get; init; }
    public required string HandlerId { get; init; }
    public required string PayloadHash { get; init; }
    public ExecutionRun? CreatedRun { get; init; }
}

public sealed class AwsDynamoExecutionWait
{
    public string RunId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime? FireAtUtc { get; set; }
    public ExecutionTimer? Timer { get; set; }
}

public sealed class AwsDynamoExecutionLeaseClaim
{
    public required ExecutionRun Run { get; init; }
    public required ExecutionLease Lease { get; init; }
    public bool Recovered { get; init; }
}

public sealed class AwsDynamoExecutionExternalCompletion
{
    public required ExecutionRun Run { get; init; }
    public bool RetryScheduled { get; init; }
    public bool AlreadyCompleted { get; init; }
}

public sealed class AwsDynamoExecutionRunDeletion
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
