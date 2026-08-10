using System.ComponentModel;
using System.Text.Json.Serialization;
using Temporalio.Workflows;

namespace Vyral.Execution.Temporal;

internal static class TemporalExecutionProtocolNames
{
    public const string CoordinatorWorkflow = "Vyral.RunCoordinator.v1";
    public const string ExternalEventSignal = "Vyral.ExternalEvent.v1";
    public const string IdentityQuery = "Vyral.Identity.v1";
    public const string ExecuteAttemptActivity = "Vyral.ExecuteAttempt.v1";
    public const string ProjectWaitActivity = "Vyral.ProjectWait.v1";
    public const string ProjectCancellationActivity = "Vyral.ProjectCancellation.v1";
}

[Workflow(TemporalExecutionProtocolNames.CoordinatorWorkflow)]
internal interface ITemporalRunCoordinatorWorkflow
{
    [WorkflowRun]
    Task<TemporalCoordinatorResult> RunAsync(TemporalCoordinatorInput input);

    [WorkflowSignal(TemporalExecutionProtocolNames.ExternalEventSignal)]
    Task NotifyExternalEventAsync(TemporalCoordinatorSignal signal);

    [WorkflowQuery(TemporalExecutionProtocolNames.IdentityQuery)]
    TemporalCoordinatorIdentity GetIdentity();
}

internal sealed record TemporalCoordinatorInput
{
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("projectionRevision")]
    public required long ProjectionRevision { get; init; }

    [JsonPropertyName("generation")]
    public required int Generation { get; init; }

    [JsonPropertyName("attemptOffset")]
    public int AttemptOffset { get; init; }

    [JsonPropertyName("coordinationTransitions")]
    public int CoordinationTransitions { get; init; }

    [JsonPropertyName("bufferedSignals")]
    public IReadOnlyList<TemporalCoordinatorSignal> BufferedSignals { get; init; } = [];
}

internal sealed record TemporalCoordinatorSignal
{
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    [JsonPropertyName("eventRevision")]
    public required long EventRevision { get; init; }
}

internal sealed record TemporalCoordinatorIdentity
{
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("generation")]
    public required int Generation { get; init; }
}

internal sealed record TemporalCoordinatorResult
{
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("generation")]
    public required int Generation { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("coordinationTransitions")]
    public required int CoordinationTransitions { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalExecutionAttemptRequest
{
    public required string RunId { get; init; }
    public required int Generation { get; init; }
    public required int Attempt { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalExecutionAttemptOutcome
{
    public required string Disposition { get; init; }
    public string? WaitId { get; init; }
    public string? WaitKind { get; init; }
    public DateTime? ResumeAtUtc { get; init; }
    public int? RetryDelayMilliseconds { get; init; }
    public string? TerminalStatus { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalExecutionWaitResolution
{
    public required string RunId { get; init; }
    public required int Generation { get; init; }
    public required string WaitId { get; init; }
    public required string Resolution { get; init; }
    public string? EventId { get; init; }
    public long? EventRevision { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalExecutionWaitProjectionResult
{
    public required bool Accepted { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalExecutionCancellation
{
    public required string RunId { get; init; }
    public required int Generation { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public interface ITemporalExecutionActivityExecutor
{
    Task<TemporalExecutionAttemptOutcome> ExecuteAttemptAsync(TemporalExecutionAttemptRequest request, CancellationToken ct = default);
    Task<TemporalExecutionWaitProjectionResult> ProjectWaitResolutionAsync(TemporalExecutionWaitResolution resolution, CancellationToken ct = default);
    Task ProjectCancellationAsync(TemporalExecutionCancellation cancellation, CancellationToken ct = default);
}

[EditorBrowsable(EditorBrowsableState.Never)]
public interface ITemporalExecutionHandlerResolver
{
    IExecutionHandler? ResolveHandler(string handlerId);
}

[EditorBrowsable(EditorBrowsableState.Never)]
public interface ITemporalExecutionProjectionStore :
    ITemporalExecutionDispatchOutbox,
    ITemporalExecutionSignalOutbox
{
    Task<TemporalProjectionRunStartResult> CreateRunWithPendingStartAsync(
        TemporalProjectionRunStart start,
        CancellationToken ct = default);

    Task RegisterWaitAsync(
        TemporalProjectionWaitRegistration registration,
        ExecutionTraceEvent registeredEvent,
        CancellationToken ct = default);

    Task PersistExternalEventWithPendingSignalAsync(
        TemporalProjectionExternalEventWrite write,
        CancellationToken ct = default);

    Task<TemporalExecutionWaitProjectionResult> ProjectWaitResolutionAsync(
        TemporalExecutionWaitResolution resolution,
        CancellationToken ct = default);

    Task ProjectCancellationAsync(TemporalExecutionCancellation cancellation, CancellationToken ct = default);
}

[EditorBrowsable(EditorBrowsableState.Never)]
public interface ITemporalExecutionRuntimeStore :
    ITemporalExecutionProjectionStore,
    ITemporalExecutionCancellationOutbox
{
    Task<TemporalProjectionRunCreationResult> CreateRunWithoutPendingStartAsync(
        TemporalProjectionRunCreation creation,
        CancellationToken ct = default);

    Task<ExecutionRun> BeginAttemptAsync(
        TemporalExecutionAttemptRequest request,
        ExecutionTraceEvent startedEvent,
        CancellationToken ct = default);

    Task<TemporalExecutionAttemptOutcome?> GetAttemptOutcomeAsync(
        TemporalExecutionAttemptRequest request,
        CancellationToken ct = default);

    Task<ExecutionRun> ReportRunAsync(
        string runId,
        int generation,
        ExecutionRunUpdate update,
        ExecutionTraceEvent statusEvent,
        CancellationToken ct = default);

    Task RecordHistoryAsync(ExecutionTraceEvent traceEvent, CancellationToken ct = default);

    Task<ExecutionArtifact> PutArtifactMetadataAsync(
        string runId,
        int generation,
        ExecutionArtifact artifact,
        ExecutionTraceEvent writtenEvent,
        CancellationToken ct = default);

    Task<ExecutionCheckpoint> PutCheckpointAsync(
        string runId,
        int generation,
        ExecutionCheckpoint checkpoint,
        ExecutionTraceEvent writtenEvent,
        CancellationToken ct = default);

    Task<ExecutionWaitResult?> ConsumeWaitResultAsync(
        string runId,
        int generation,
        int attempt,
        string kind,
        string name,
        CancellationToken ct = default);

    Task<TemporalProjectionAttemptCompletion> CompleteAttemptAsync(
        string runId,
        int generation,
        ExecutionRunResult result,
        ExecutionTraceEvent retryEvent,
        ExecutionTraceEvent terminalEvent,
        CancellationToken ct = default);

    Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default);

    Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(
        ExecutionRunQuery? query = null,
        CancellationToken ct = default);

    Task<TemporalActiveCoordinatorSnapshot> GetActiveCoordinatorSnapshotAsync(
        int limit,
        CancellationToken ct = default);

    Task<bool> IsActiveCoordinatorAsync(
        string workflowId,
        int generation,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(
        string runId,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default);

    Task<ExecutionArtifact?> GetArtifactAsync(
        string runId,
        string artifactRef,
        CancellationToken ct = default);

    Task<ExecutionCheckpoint?> GetCheckpointAsync(
        string runId,
        string key,
        CancellationToken ct = default);

    Task<TemporalProjectionCancellationRequestResult> RequestCancellationAsync(
        string runId,
        CancellationToken ct = default);

    Task<TemporalProjectionExternalEventDispatch> CreateExternalEventWithPendingSignalAsync(
        ExecutionExternalEvent externalEvent,
        string dispatchId,
        CancellationToken ct = default);

    Task<ExecutionLease?> TryAcquireLeaseAsync(
        ExecutionLeaseRequest request,
        CancellationToken ct = default);

    Task<bool> ReleaseLeaseAsync(
        string leaseKey,
        string ownerId,
        CancellationToken ct = default);

    Task<ExecutionTimer> ScheduleTimerAsync(
        ExecutionTimerRequest request,
        CancellationToken ct = default);

    Task<TemporalExecutionProjectionStatus> GetRuntimeStatusAsync(CancellationToken ct = default);
}

internal static class TemporalAttemptDispositions
{
    public const string Completed = "completed";
    public const string Suspended = "suspended";
    public const string Retryable = "retryable";
    public const string Terminal = "terminal";
}

internal static class TemporalWaitKinds
{
    public const string ExternalEvent = "external_event";
    public const string Timer = "timer";
}

internal static class TemporalWaitResolutions
{
    public const string ExternalEvent = "external_event";
    public const string Timer = "timer";
    public const string Timeout = "timeout";
}
