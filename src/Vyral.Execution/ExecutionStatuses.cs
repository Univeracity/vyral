namespace Vyral.Execution;

public static class ExecutionRunStatuses
{
    public const string Queued = "queued";
    public const string Waiting = "waiting";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
    public const string TimedOut = "timed_out";

    public static bool IsKnown(string? status)
    {
        return status is Queued or Waiting or Running or Succeeded or Failed or Cancelled or Rejected or TimedOut;
    }

    public static bool IsTerminal(string? status)
    {
        return status is Succeeded or Failed or Cancelled or Rejected or TimedOut;
    }
}

public static class ExecutionEventTypes
{
    public const string RunCreated = "run.created";
    public const string RunStarted = "run.started";
    public const string RunStatus = "run.status";
    public const string RunCancellationRequested = "run.cancellation_requested";
    public const string RunCompleted = "run.completed";
    public const string RunFailed = "run.failed";
    public const string RunRejected = "run.rejected";
    public const string Log = "log";
    public const string StepStarted = "step.started";
    public const string StepCompleted = "step.completed";
    public const string ArtifactWritten = "artifact.written";
    public const string CheckpointWritten = "checkpoint.written";
    public const string RetryScheduled = "retry.scheduled";
    public const string LeaseAcquired = "lease.acquired";
    public const string LeaseReleased = "lease.released";
    public const string TimerScheduled = "timer.scheduled";
    public const string ExternalEventRaised = "external_event.raised";
    public const string WaitRegistered = "wait.registered";
    public const string WaitResumed = "wait.resumed";
    public const string WaitTimedOut = "wait.timed_out";
}

public static class ExecutionArtifactKinds
{
    public const string Json = "json";
    public const string Text = "text";
    public const string ObjectReference = "object_reference";
}

public static class ExecutionFailureClasses
{
    public const string Cancelled = "cancelled";
    public const string HandlerMissing = "handler_missing";
    public const string PluginMismatch = "plugin_mismatch";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string QueueFull = "queue_full";
    public const string Timeout = "timeout";
    public const string Transient = "transient";
    public const string Validation = "validation";
    public const string Platform = "platform";
    public const string Unknown = "unknown";
}

public static class ExecutionCapabilityIds
{
    public const string LocalDispatch = "local.dispatch";
    public const string RemoteOrchestration = "remote.orchestration";
    /// <summary>Registered <see cref="IExecutionHandler"/> delegates execute in this host process.</summary>
    public const string InProcessHandlers = "in_process.handlers";
    public const string DurableRuns = "durable.runs";
    public const string DurableTimers = "durable.timers";
    public const string ExternalEvents = "external.events";
    public const string Cancellation = "cancellation";
    public const string Retries = "retries";
    public const string RestartResume = "restart.resume";
    public const string Leases = "leases";
    public const string Artifacts = "artifacts";
    public const string TraceHistory = "trace.history";
    public const string Idempotency = "idempotency";
    public const string ExternalWorkers = "external.workers";
    public const string DurableWaits = "durable.waits";
}

/// <summary>
/// Portable causes for dispatching a durable run. Providers may expose additional diagnostic
/// reasons, but consumers must not branch on provider-specific queue or orchestration details.
/// </summary>
public static class ExecutionDispatchReasons
{
    public const string RunReady = "run_ready";
    public const string TimerDue = "timer_due";
    public const string ExternalEvent = "external_event";
    public const string LeaseExpired = "lease_expired";
}

public static class ExecutionWaitOutcomes
{
    public const string ExternalEvent = "external_event";
    public const string Timer = "timer";
    public const string TimedOut = "timed_out";
}

public static class ExecutionResumePolicyModes
{
    public const string RestartRecovery = "restart_recovery";
}

public static class ExecutionResumePolicyBehaviors
{
    public const string MayReexecuteHandler = "may_reexecute_handler";
    public const string DispatchWhenDue = "dispatch_when_due";
    public const string NeverResume = "never_resume";
    public const string PluginOwned = "plugin_owned";
}
