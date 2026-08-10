using System.ComponentModel;
using Vyral.Execution;

namespace Vyral.Execution.Temporal;

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalProjectionRunStart
{
    public required ExecutionRun Run { get; init; }
    public required string WorkflowId { get; init; }
    public required int Generation { get; init; }
    public required long ProjectionRevision { get; init; }
    public required string DispatchId { get; init; }
    public required string RequestHash { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalProjectionRunCreation
{
    public required ExecutionRun Run { get; init; }
    public required string WorkflowId { get; init; }
    public required int Generation { get; init; }
    public required long ProjectionRevision { get; init; }
    public required string RequestHash { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalProjectionRunCreationResult
{
    public required ExecutionRun Run { get; init; }
    public required bool Replayed { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalProjectionRunStartResult
{
    public required ExecutionRun Run { get; init; }
    public required string DispatchId { get; init; }
    public required bool Replayed { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalProjectionWaitRegistration
{
    public required string RunId { get; init; }
    public required int Generation { get; init; }
    public required string WaitId { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public DateTime? ResumeAtUtc { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalProjectionExternalEventWrite
{
    public required ExecutionExternalEvent Event { get; init; }
    public required long EventRevision { get; init; }
    public required string WorkflowId { get; init; }
    public required int Generation { get; init; }
    public required string DispatchId { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalProjectionExternalEventDispatch
{
    public required ExecutionExternalEvent Event { get; init; }
    public required long EventRevision { get; init; }
    public required string WorkflowId { get; init; }
    public required int Generation { get; init; }
    public required string DispatchId { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalProjectionCancellationRequestResult
{
    public ExecutionRun? Run { get; init; }
    public string? WorkflowId { get; init; }
    public int? Generation { get; init; }
    public required bool NewlyRequested { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalExecutionProjectionStatus
{
    public required int SchemaVersion { get; init; }
    public required int PendingStartDispatches { get; init; }
    public required int PendingSignalDispatches { get; init; }
    public required int PendingCancellationDispatches { get; init; }
    public DateTime? OldestPendingDispatchAtUtc { get; init; }
    public required int ActiveRuns { get; init; }
    public required int ActiveCoordinators { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalActiveCoordinator
{
    public required string WorkflowId { get; init; }
    public required int Generation { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalActiveCoordinatorSnapshot
{
    public required int TotalCount { get; init; }
    public required IReadOnlyList<TemporalActiveCoordinator> Coordinators { get; init; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record TemporalProjectionAttemptCompletion
{
    public required ExecutionRun Run { get; init; }
    public int? RetryDelayMilliseconds { get; init; }
}
