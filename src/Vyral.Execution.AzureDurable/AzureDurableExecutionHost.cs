using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Execution.AzureDurable;

public interface IAzureDurableExecutionOrchestrationScheduler
{
    /// <summary>
    /// Ensures that the provider orchestration for <paramref name="command"/> exists. Implementations
    /// must make repeat calls for the same active instance safe: a client retry can occur after the
    /// Vyral run reservation commits but before the caller receives the provider scheduling result.
    /// </summary>
    Task ScheduleNewAsync(AzureDurableStartCommand command, CancellationToken ct = default);
    Task TerminateAsync(string instanceId, string reason, CancellationToken ct = default);
    Task RaiseEventAsync(string instanceId, string eventName, JsonNode? payload, CancellationToken ct = default);
}

public interface IAzureDurableExecutionOrchestrationDriver
{
    DateTime CurrentUtc { get; }
    Task CreateTimerAsync(DateTime fireAtUtc, CancellationToken ct = default);
    Task<AzureDurableActivityResult> CallActivityAsync(
        string activityName,
        AzureDurableActivityCommand command,
        AzureDurableRetryOptions retryOptions,
        CancellationToken ct = default);
    Task SetCustomStatusAsync(AzureDurableStatusSnapshot snapshot, CancellationToken ct = default);
}

/// <summary>
/// Optional orchestration extension used by a real Durable Functions host. Its operations are
/// implemented as Durable activities, keeping the replayed orchestrator free of store I/O and
/// arbitrary handler execution.
/// </summary>
public interface IAzureDurableExecutionOrchestrationStateDriver
{
    Task<AzureDurableRunCreation> StartRunAsync(AzureDurableStartCommand command, CancellationToken ct = default);
    Task<AzureDurableOrchestrationStepResult> RunStepAsync(
        AzureDurableStartCommand command,
        ExecutionRun run,
        ExecutionWaitResult? waitOutcome = null,
        CancellationToken ct = default);
    Task<ExecutionWaitResult> WaitForDurableWaitAsync(AzureDurableWait wait, CancellationToken ct = default);
}

public interface IAzureDurableExecutionRejectedRunSink
{
    Task<ExecutionRun> RecordRejectedRunAsync(ExecutionRun run, CancellationToken ct = default);
}

public sealed class AzureDurableExecutionClient : IAzureDurableExecutionClient, IAzureDurableExecutionRejectedRunSink
{
    private readonly AzureDurableExecutionHost _host;
    private readonly IAzureDurableExecutionOrchestrationScheduler _scheduler;

    public AzureDurableExecutionClient(
        AzureDurableExecutionHost host,
        IAzureDurableExecutionOrchestrationScheduler scheduler)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    public async Task<ExecutionRun> StartRunAsync(AzureDurableStartCommand command, CancellationToken ct = default)
    {
        var started = await _host.StartRunWithReservationAsync(command, ct);
        if (ExecutionRunLifecycle.IsActive(started.Run.Status))
        {
            await _scheduler.ScheduleNewAsync(command, ct);
        }

        started.Run.AdmissionReplayed = !started.Created;
        return started.Run;
    }

    public Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default) =>
        _host.GetRunAsync(runId, includeResult, ct);

    public Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default) =>
        _host.ListRunsAsync(query, ct);

    public async Task<ExecutionRun?> CancelRunAsync(string runId, CancellationToken ct = default)
    {
        var run = await _host.CancelRunAsync(runId, ct);
        if (run is not null && run.Status == ExecutionRunStatuses.Cancelled)
        {
            await _scheduler.TerminateAsync(runId, "Execution cancellation requested.", ct);
        }

        return run;
    }

    public Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default) =>
        _host.GetHistoryAsync(runId, query, ct);

    public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default) =>
        _host.ListArtifactsAsync(runId, ct);

    public Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default) =>
        _host.GetArtifactAsync(runId, artifactRef, ct);

    public Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default) =>
        _host.GetCheckpointAsync(runId, key, ct);

    public Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default) =>
        _host.TryAcquireLeaseAsync(request, ct);

    public Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default) =>
        _host.ReleaseLeaseAsync(leaseKey, ownerId, ct);

    public Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimerRequest request, CancellationToken ct = default) =>
        _host.ScheduleTimerAsync(request, ct);

    public async Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEventRequest request, CancellationToken ct = default)
    {
        var externalEvent = await _host.RaiseEventAsync(request, ct);
        if (!string.IsNullOrWhiteSpace(request.RunId))
        {
            await _scheduler.RaiseEventAsync(request.RunId!, request.Name, request.Payload, ct);
        }

        return externalEvent;
    }

    public Task<ExecutionRuntimeAdapterStatus> GetAdapterStatusAsync(ExecutionRuntimeAdapterDescriptor adapter, CancellationToken ct = default) =>
        _host.GetAdapterStatusAsync(adapter, ct);

    public Task<ExecutionRun> RecordRejectedRunAsync(ExecutionRun run, CancellationToken ct = default) =>
        _host.RecordRejectedRunAsync(run, ct);
}

public sealed class AzureDurableExecutionHost
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);

    public AzureDurableExecutionHost(
        AzureDurableExecutionOptions? options = null,
        AzureDurableExecutionRegistry? registry = null,
        IAzureDurableExecutionStatusStore? store = null)
    {
        Options = options ?? new AzureDurableExecutionOptions();
        Registry = registry ?? new AzureDurableExecutionRegistry(Options.Limits);
        Store = store ?? new AzureDurableInMemoryExecutionStatusStore(Options);
        Adapter = AzureDurableExecutionDialect.BuildAdapterDescriptor(Options);
    }

    public AzureDurableExecutionOptions Options { get; }
    public AzureDurableExecutionRegistry Registry { get; }
    public IAzureDurableExecutionStatusStore Store { get; }
    public ExecutionRuntimeAdapterDescriptor Adapter { get; }

    private ExecutionRuntimeLimits Limits => Options.Limits ?? ExecutionRuntimeLimits.Default;

    public void RegisterHandler(IExecutionHandler handler) => Registry.RegisterHandler(handler);

    public void RegisterPlugin(IExecutionPlugin plugin) => Registry.RegisterPlugin(plugin);

    public IReadOnlyList<ExecutionPluginDescriptor> ListPlugins() => Registry.ListPlugins();

    public IReadOnlyList<ExecutionHandlerDescriptor> ListHandlers() => Registry.ListHandlers();

    public async Task<ExecutionRun> StartRunAsync(AzureDurableStartCommand command, CancellationToken ct = default)
    {
        var started = await StartRunWithReservationAsync(command, ct);
        started.Run.AdmissionReplayed = !started.Created;
        return started.Run;
    }

    /// <summary>
    /// Starts or replays a run while preserving whether this caller won the durable run-id
    /// reservation. The client uses this to distinguish new state from a replay, while its
    /// scheduler still idempotently ensures every active replay has a provider orchestration.
    /// </summary>
    public async Task<AzureDurableRunCreation> StartRunWithReservationAsync(AzureDurableStartCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ExecutionContractValidator.ValidateRunRequest(command.Request, Limits);
        ExecutionContractValidator.ValidateHandlerDescriptor(command.Handler, Limits);

        var retryPolicy = ExecutionContractValidator.NormalizeRetryPolicy(command.Request.RetryPolicy, Limits);
        command.Request.RetryPolicy = Clone(retryPolicy);
        command.Request.PluginId = ResolvePluginId(command.Request.HandlerId, command.Request.PluginId, command.Handler.PluginId);
        var runId = string.IsNullOrWhiteSpace(command.InstanceId)
            ? AzureDurableExecutionDialect.BuildInstanceId(command.Request, Options)
            : command.InstanceId.Trim();
        command.InstanceId = runId;
        var payloadHash = Sha256(SerializeNode(command.Request.Payload));

        var existingById = await Store.GetRunAsync(runId, ct: ct);
        if (existingById is not null)
        {
            EnsureIdempotentReplay(existingById, command.Request.HandlerId, command.Request.PluginId, payloadHash, command.Request.IdempotencyKey ?? runId);
            return new AzureDurableRunCreation { Created = false, Run = existingById };
        }

        if (!string.IsNullOrWhiteSpace(command.Request.IdempotencyKey))
        {
            var existingByKey = await Store.FindRunByIdempotencyKeyAsync(command.Request.IdempotencyKey!, ct);
            if (existingByKey is not null)
            {
                EnsureIdempotentReplay(existingByKey, command.Request.HandlerId, command.Request.PluginId, payloadHash, command.Request.IdempotencyKey!);
                return new AzureDurableRunCreation { Created = false, Run = existingByKey };
            }
        }

        var now = DateTime.UtcNow;
        var run = AzureDurableExecutionDialect.CreateQueuedRun(command, now);
        if (!Registry.TryGetHandler(run.HandlerId, out _))
        {
            ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Rejected);
            Reject(run, ExecutionFailureClasses.HandlerMissing, $"Execution handler '{run.HandlerId}' is not registered.", now);
            return await PersistRejectedRunAsync(run, ct);
        }

        if (await Store.CountActiveRunsAsync(ct) >= Options.MaxActiveRuns)
        {
            ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Rejected);
            Reject(run, ExecutionFailureClasses.QueueFull, $"Execution run queue is full. Max active runs: {Options.MaxActiveRuns}.", now);
            return await PersistRejectedRunAsync(run, ct);
        }

        var created = await Store.CreateRunIfAbsentAsync(run, ct);
        if (!created.Created)
        {
            EnsureIdempotentReplay(
                created.Run,
                command.Request.HandlerId,
                command.Request.PluginId,
                payloadHash,
                command.Request.IdempotencyKey ?? runId);
            return new AzureDurableRunCreation { Created = false, Run = created.Run };
        }

        run = created.Run;
        await AppendEventAsync(run.Id, ExecutionEventTypes.RunCreated, run.Attempt, run.Status, "Execution run created.", "info", null, ct);
        return new AzureDurableRunCreation { Created = true, Run = Clone(run) };
    }

    public async Task<ExecutionRun> RecordRejectedRunAsync(ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Status != ExecutionRunStatuses.Rejected)
        {
            throw new InvalidOperationException("Only rejected execution runs can be recorded through the rejected-run sink.");
        }

        var started = await PersistRejectedRunAsync(run, ct);
        started.Run.AdmissionReplayed = !started.Created;
        return started.Run;
    }

    private async Task<AzureDurableRunCreation> PersistRejectedRunAsync(ExecutionRun rejected, CancellationToken ct)
    {
        var created = await Store.CreateRunIfAbsentAsync(rejected, ct);
        if (!created.Created)
        {
            EnsureIdempotentReplay(
                created.Run,
                rejected.HandlerId,
                rejected.PluginId,
                rejected.PayloadHash,
                rejected.IdempotencyKey ?? rejected.Id);
            return new AzureDurableRunCreation { Created = false, Run = Clone(created.Run) };
        }

        await AppendEventAsync(
            created.Run.Id,
            ExecutionEventTypes.RunRejected,
            created.Run.Attempt,
            created.Run.Status,
            created.Run.Error,
            "warning",
            null,
            ct);
        return new AzureDurableRunCreation { Created = true, Run = Clone(created.Run) };
    }

    public async Task<ExecutionRun> OrchestrateAsync(
        AzureDurableStartCommand command,
        IAzureDurableExecutionOrchestrationDriver driver,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(driver);

        if (driver is IAzureDurableExecutionOrchestrationStateDriver stateDriver)
        {
            return await OrchestrateWithStateActivitiesAsync(command, driver, stateDriver, ct);
        }

        var run = await StartRunAsync(command, ct);
        await driver.SetCustomStatusAsync(AzureDurableExecutionDialect.ToStatusSnapshot(run), ct);
        while (ExecutionRunLifecycle.IsActive(run.Status))
        {
            if (run.Status == ExecutionRunStatuses.Waiting &&
                run.ScheduledAtUtc.HasValue &&
                run.ScheduledAtUtc.Value > driver.CurrentUtc)
            {
                await driver.CreateTimerAsync(run.ScheduledAtUtc.Value, ct);
                run = await Store.GetRunAsync(run.Id, ct: ct) ?? run;
            }

            if (run.CancellationRequested)
            {
                run = await CancelActiveRunAsync(run, ct);
                await driver.SetCustomStatusAsync(AzureDurableExecutionDialect.ToStatusSnapshot(run), ct);
                return run;
            }

            var running = AzureDurableExecutionDialect.StartActivityAttempt(run, driver.CurrentUtc);
            running = await Store.UpsertRunAsync(running, ct);
            if (ExecutionRunStatuses.IsTerminal(running.Status))
            {
                await driver.SetCustomStatusAsync(AzureDurableExecutionDialect.ToStatusSnapshot(running), ct);
                return running;
            }

            if (running.CancellationRequested)
            {
                run = await CancelActiveRunAsync(running, ct);
                await driver.SetCustomStatusAsync(AzureDurableExecutionDialect.ToStatusSnapshot(run), ct);
                return run;
            }

            await AppendEventAsync(running.Id, ExecutionEventTypes.RunStarted, running.Attempt, running.Status, "Execution run started.", "info", null, ct);
            await driver.SetCustomStatusAsync(AzureDurableExecutionDialect.ToStatusSnapshot(running), ct);

            var activityResult = await driver.CallActivityAsync(
                command.ActivityName,
                AzureDurableExecutionDialect.BuildActivityCommand(running),
                command.RetryOptions,
                ct);
            if (activityResult.Wait is not null)
            {
                throw new InvalidOperationException(
                    "Portable durable waits require an orchestration state driver that can await provider timers and external events.");
            }
            run = await CompleteActivityAsync(activityResult, driver.CurrentUtc, ct);
            await driver.SetCustomStatusAsync(AzureDurableExecutionDialect.ToStatusSnapshot(run), ct);
        }

        return run;
    }

    /// <summary>
    /// Runs the orchestration protocol without accessing <see cref="Store"/>. A Durable Functions
    /// driver implements start and step calls as activities; this method may therefore be replayed
    /// safely by the provider.
    /// </summary>
    private static async Task<ExecutionRun> OrchestrateWithStateActivitiesAsync(
        AzureDurableStartCommand command,
        IAzureDurableExecutionOrchestrationDriver driver,
        IAzureDurableExecutionOrchestrationStateDriver stateDriver,
        CancellationToken ct)
    {
        var started = await stateDriver.StartRunAsync(command, ct);
        var run = started.Run;
        await driver.SetCustomStatusAsync(AzureDurableExecutionDialect.ToStatusSnapshot(run), ct);

        while (ExecutionRunLifecycle.IsActive(run.Status))
        {
            if (run.Status == ExecutionRunStatuses.Waiting &&
                run.ScheduledAtUtc.HasValue &&
                run.ScheduledAtUtc.Value > driver.CurrentUtc)
            {
                await driver.CreateTimerAsync(run.ScheduledAtUtc.Value, ct);
            }

            var step = await stateDriver.RunStepAsync(command, run, waitOutcome: null, ct: ct);
            run = step.Run;
            await driver.SetCustomStatusAsync(AzureDurableExecutionDialect.ToStatusSnapshot(run), ct);

            while (step.Wait is not null)
            {
                var outcome = await stateDriver.WaitForDurableWaitAsync(step.Wait, ct);
                step = await stateDriver.RunStepAsync(command, run, outcome, ct);
                run = step.Run;
                await driver.SetCustomStatusAsync(AzureDurableExecutionDialect.ToStatusSnapshot(run), ct);
            }
        }

        return run;
    }

    /// <summary>
    /// Performs one persisted execution transition. This method is intended to run in a Durable
    /// activity when a provider host is used; it deliberately owns all store access and handler
    /// invocation for that reason.
    /// </summary>
    public async Task<AzureDurableOrchestrationStepResult> ExecuteOrchestrationStepAsync(
        AzureDurableActivityCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var run = await Store.GetRunAsync(command.RunId, ct: ct) ??
            throw new InvalidOperationException($"Execution run '{command.RunId}' was not found.");

        if (ExecutionRunStatuses.IsTerminal(run.Status))
        {
            return new AzureDurableOrchestrationStepResult { Run = Clone(run) };
        }

        if (command.WaitOutcome is not null)
        {
            var waiting = run;
            run = await Store.ResumeDurableWaitAsync(run.Id, command.WaitOutcome, ct);
            if (waiting.Status == ExecutionRunStatuses.Waiting && run.Status == ExecutionRunStatuses.Queued)
            {
                var eventType = command.WaitOutcome.Outcome == ExecutionWaitOutcomes.TimedOut
                    ? ExecutionEventTypes.WaitTimedOut
                    : ExecutionEventTypes.WaitResumed;
                var message = command.WaitOutcome.Outcome == ExecutionWaitOutcomes.TimedOut
                    ? $"Wait for external event '{command.WaitOutcome.Name}' timed out."
                    : $"Durable wait '{command.WaitOutcome.Name}' resumed execution.";
                await AppendEventAsync(run.Id, eventType, run.Attempt, run.Status, message, "info", null, ct);
            }
        }

        if (run.CancellationRequested)
        {
            return new AzureDurableOrchestrationStepResult { Run = await CancelActiveRunAsync(run, ct) };
        }

        if (run.Status == ExecutionRunStatuses.Waiting && command.WaitOutcome is null)
        {
            var durableWait = await Store.GetDurableWaitAsync(run.Id, ct);
            if (durableWait is not null)
            {
                return new AzureDurableOrchestrationStepResult { Run = Clone(run), Wait = durableWait };
            }

            if (run.ScheduledAtUtc.HasValue && run.ScheduledAtUtc.Value > DateTime.UtcNow)
            {
                return new AzureDurableOrchestrationStepResult { Run = Clone(run) };
            }
        }

        var running = AzureDurableExecutionDialect.StartActivityAttempt(run, DateTime.UtcNow);
        running = await Store.UpsertRunAsync(running, ct);
        if (ExecutionRunStatuses.IsTerminal(running.Status))
        {
            return new AzureDurableOrchestrationStepResult { Run = Clone(running) };
        }

        if (running.CancellationRequested)
        {
            return new AzureDurableOrchestrationStepResult { Run = await CancelActiveRunAsync(running, ct) };
        }

        await AppendEventAsync(running.Id, ExecutionEventTypes.RunStarted, running.Attempt, running.Status, "Execution run started.", "info", null, ct);

        var activityResult = await DispatchActivityAsync(
            new AzureDurableActivityCommand
            {
                RunId = running.Id,
                HandlerId = running.HandlerId,
                PluginId = running.PluginId,
                Attempt = running.Attempt,
                Payload = CloneNode(running.Payload),
                CorrelationId = running.CorrelationId,
                WaitOutcome = command.WaitOutcome
            },
            ct);
        if (activityResult.Wait is not null)
        {
            var waiting = await Store.GetRunAsync(running.Id, ct: ct) ?? running;
            return new AzureDurableOrchestrationStepResult { Run = Clone(waiting), Wait = Clone(activityResult.Wait) };
        }

        var completed = await CompleteActivityAsync(activityResult, DateTime.UtcNow, ct);
        return new AzureDurableOrchestrationStepResult { Run = completed };
    }

    public async Task<AzureDurableActivityResult> DispatchActivityAsync(AzureDurableActivityCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var run = await Store.GetRunAsync(command.RunId, ct: ct) ??
            throw new InvalidOperationException($"Execution run '{command.RunId}' was not found.");

        if (ExecutionRunStatuses.IsTerminal(run.Status))
        {
            return new AzureDurableActivityResult
            {
                RunId = run.Id,
                Result = ToRunResult(run)
            };
        }

        if (run.Status is ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting)
        {
            run = AzureDurableExecutionDialect.StartActivityAttempt(run);
            run = await Store.UpsertRunAsync(run, ct);
            if (ExecutionRunStatuses.IsTerminal(run.Status) || run.CancellationRequested)
            {
                return new AzureDurableActivityResult
                {
                    RunId = run.Id,
                    Result = run.CancellationRequested
                        ? ExecutionRunResult.Cancelled(run.Result)
                        : ToRunResult(run)
                };
            }

            await AppendEventAsync(run.Id, ExecutionEventTypes.RunStarted, run.Attempt, run.Status, "Execution run started.", "info", null, ct);
        }

        if (!Registry.TryGetHandler(run.HandlerId, out var handler))
        {
            return new AzureDurableActivityResult
            {
                RunId = run.Id,
                Result = ExecutionRunResult.Failed(ExecutionFailureClasses.HandlerMissing, $"Execution handler '{run.HandlerId}' is not registered.")
            };
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancellations[run.Id] = cts;
        try
        {
            if (run.CancellationRequested)
            {
                throw new OperationCanceledException(cts.Token);
            }

            var context = new AzureDurableExecutionRunContext(this, run, cts.Token);
            var result = await handler.ExecuteAsync(context, cts.Token);
            var latest = await Store.GetRunAsync(run.Id, ct: CancellationToken.None) ?? run;
            if (latest.CancellationRequested && result.Status != ExecutionRunStatuses.TimedOut)
            {
                result = ExecutionRunResult.Cancelled(result.Result);
            }

            ExecutionContractValidator.ValidateRunResult(result, Limits);
            return new AzureDurableActivityResult
            {
                RunId = run.Id,
                Result = result
            };
        }
        catch (OperationCanceledException)
        {
            var latest = await Store.GetRunAsync(run.Id, ct: CancellationToken.None) ?? run;
            return new AzureDurableActivityResult
            {
                RunId = run.Id,
                Result = ExecutionRunResult.Cancelled(latest.Result)
            };
        }
        catch (AzureDurableExecutionSuspendedException suspended)
        {
            return new AzureDurableActivityResult
            {
                RunId = run.Id,
                Wait = Clone(suspended.Wait)
            };
        }
        catch (Exception ex)
        {
            return new AzureDurableActivityResult
            {
                RunId = run.Id,
                Result = ExecutionRunResult.Failed(
                    ExecutionFailureClasses.Unknown,
                    ExecutionContractValidator.BoundText(ex.Message, Limits.MaxTraceMessageChars) ?? "Execution handler failed.")
            };
        }
        finally
        {
            _cancellations.TryRemove(run.Id, out _);
            cts.Dispose();
        }
    }

    public Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default) =>
        Store.GetRunAsync(runId, includeResult, ct);

    public Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default) =>
        Store.ListRunsAsync(query, ct);

    public async Task<ExecutionRun?> CancelRunAsync(string runId, CancellationToken ct = default)
    {
        var run = await Store.GetRunAsync(runId, ct: ct);
        if (run is null)
        {
            return null;
        }

        if (ExecutionRunStatuses.IsTerminal(run.Status))
        {
            return run;
        }

        run.CancellationRequested = true;
        run.UpdatedAtUtc = DateTime.UtcNow;
        run = await Store.UpsertRunAsync(run, ct);
        if (ExecutionRunStatuses.IsTerminal(run.Status))
        {
            return run;
        }

        await AppendEventAsync(run.Id, ExecutionEventTypes.RunCancellationRequested, run.Attempt, run.Status, "Execution cancellation requested.", "info", null, ct);
        if (_cancellations.TryGetValue(run.Id, out var cancellation))
        {
            await cancellation.CancelAsync();
        }

        return await CancelActiveRunAsync(run, ct);
    }

    public Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default) =>
        Store.GetHistoryAsync(runId, query, ct);

    public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default) =>
        Store.ListArtifactsAsync(runId, ct);

    public Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default) =>
        Store.GetArtifactAsync(runId, artifactRef, ct);

    public Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default) =>
        Store.GetCheckpointAsync(runId, key, ct);

    public async Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateLeaseRequest(request, Limits);
        var now = DateTime.UtcNow;
        var lease = new ExecutionLease
        {
            LeaseKey = NormalizeRequired(request.LeaseKey, "Lease key"),
            OwnerId = NormalizeRequired(request.OwnerId, "Lease owner id"),
            RunId = NormalizeOptional(request.RunId),
            AcquiredAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(Math.Max(1, request.TtlSeconds)),
            Metadata = CloneObject(request.Metadata)
        };

        var acquired = await Store.TryAcquireLeaseAsync(lease, ct);
        if (acquired is not null && !string.IsNullOrWhiteSpace(acquired.RunId))
        {
            await AppendEventAsync(acquired.RunId!, ExecutionEventTypes.LeaseAcquired, 0, null, $"Lease '{acquired.LeaseKey}' acquired.", "info", null, ct);
        }

        return acquired;
    }

    public async Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default)
    {
        leaseKey = NormalizeRequired(leaseKey, "Lease key");
        ownerId = NormalizeRequired(ownerId, "Lease owner id");
        var heldLease = await Store.GetLeaseAsync(leaseKey, ct);
        var released = await Store.ReleaseLeaseAsync(leaseKey, ownerId, ct);
        if (released &&
            heldLease is not null &&
            string.Equals(heldLease.OwnerId, ownerId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(heldLease.RunId))
        {
            await AppendEventAsync(heldLease.RunId, ExecutionEventTypes.LeaseReleased, 0, null, $"Lease '{leaseKey}' released.", "info", null, ct);
        }

        return released;
    }

    public async Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimerRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateTimerRequest(request, Limits);
        var timer = new ExecutionTimer
        {
            Id = OrderedId.CreateString(),
            Name = NormalizeRequired(request.Name, "Timer name"),
            RunId = NormalizeOptional(request.RunId),
            FireAtUtc = request.FireAtUtc,
            CreatedAtUtc = DateTime.UtcNow,
            Payload = CloneNode(request.Payload)
        };

        timer = await Store.ScheduleTimerAsync(timer, ct);
        if (!string.IsNullOrWhiteSpace(timer.RunId))
        {
            await AppendEventAsync(timer.RunId!, ExecutionEventTypes.TimerScheduled, 0, null, $"Timer '{timer.Name}' scheduled.", "info", null, ct);
        }

        return timer;
    }

    public async Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEventRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalEventRequest(request, Limits);
        var externalEvent = new ExecutionExternalEvent
        {
            Id = OrderedId.CreateString(),
            Name = NormalizeRequired(request.Name, "External event name"),
            RunId = NormalizeOptional(request.RunId),
            RaisedAtUtc = DateTime.UtcNow,
            Payload = CloneNode(request.Payload)
        };

        externalEvent = await Store.RaiseEventAsync(externalEvent, ct);
        if (!string.IsNullOrWhiteSpace(externalEvent.RunId))
        {
            await AppendEventAsync(externalEvent.RunId!, ExecutionEventTypes.ExternalEventRaised, 0, null, $"External event '{externalEvent.Name}' raised.", "info", null, ct);
        }

        return externalEvent;
    }

    public async Task<ExecutionRuntimeAdapterStatus> GetAdapterStatusAsync(ExecutionRuntimeAdapterDescriptor? adapter = null, CancellationToken ct = default)
    {
        return new ExecutionRuntimeAdapterStatus
        {
            Adapter = Clone(adapter ?? Adapter),
            Available = true,
            Status = "ok",
            CheckedAtUtc = DateTime.UtcNow,
            ActiveRuns = await Store.CountActiveRunsAsync(ct),
            OperationalPolicy = AzureDurableExecutionDialect.BuildOperationalPolicy(Options),
            ResumePolicy = AzureDurableExecutionDialect.BuildResumePolicy(),
            Details = new JsonObject
            {
                ["registeredHandlers"] = Registry.ListHandlers().Count,
                ["taskHubName"] = Options.TaskHubName,
                ["orchestratorName"] = Options.OrchestratorName,
                ["activityName"] = Options.ActivityName,
                ["statusStoreName"] = Options.StatusStoreName,
                ["artifactContainerName"] = Options.ArtifactContainerName,
                ["hostBridge"] = "sdk-neutral"
            }
        };
    }

    private async Task<ExecutionRun> CompleteActivityAsync(AzureDurableActivityResult activityResult, DateTime nowUtc, CancellationToken ct)
    {
        var run = await Store.GetRunAsync(activityResult.RunId, ct: ct) ??
            throw new InvalidOperationException($"Execution run '{activityResult.RunId}' was not found.");

        if (ExecutionRunStatuses.IsTerminal(run.Status))
        {
            return Clone(run);
        }

        if (run.CancellationRequested)
        {
            return await CancelActiveRunAsync(run, ct);
        }

        foreach (var traceEvent in activityResult.Events)
        {
            await Store.AppendEventAsync(traceEvent, ct);
        }

        foreach (var artifact in activityResult.Artifacts)
        {
            await Store.PutArtifactAsync(artifact, ct);
        }

        var latest = AzureDurableExecutionDialect.ApplyActivityResult(run, activityResult, nowUtc, Limits);
        if (AzureDurableExecutionDialect.ShouldRetry(latest))
        {
            var retryDelay = AzureDurableExecutionDialect.CalculateRetryDelay(latest);
            latest = AzureDurableExecutionDialect.ScheduleRetry(latest, nowUtc.Add(retryDelay));
            latest = await Store.UpsertRunAsync(latest, ct);
            if (ExecutionRunStatuses.IsTerminal(latest.Status))
            {
                return Clone(latest);
            }

            if (latest.CancellationRequested)
            {
                return await CancelActiveRunAsync(latest, ct);
            }

            await AppendEventAsync(
                latest.Id,
                ExecutionEventTypes.RetryScheduled,
                latest.Attempt,
                latest.Status,
                $"Retry {latest.Attempt + 1} of {latest.MaxAttempts} scheduled.",
                "warning",
                new JsonObject
                {
                    ["delaySeconds"] = retryDelay.TotalSeconds,
                    ["failureClass"] = latest.FailureClass,
                    ["error"] = latest.Error
                },
                ct);
            return Clone(latest);
        }

        latest = await Store.UpsertRunAsync(latest, ct);
        if (ExecutionRunStatuses.IsTerminal(latest.Status) && latest.Status != activityResult.Result.Status)
        {
            return Clone(latest);
        }

        if (latest.CancellationRequested && latest.Status != ExecutionRunStatuses.Cancelled)
        {
            return await CancelActiveRunAsync(latest, ct);
        }

        await AppendEventAsync(
            latest.Id,
            latest.Status == ExecutionRunStatuses.Failed ? ExecutionEventTypes.RunFailed : ExecutionEventTypes.RunCompleted,
            latest.Attempt,
            latest.Status,
            latest.Error ?? $"Execution run {latest.Status}.",
            latest.Status == ExecutionRunStatuses.Succeeded ? "info" : "warning",
            null,
            ct);
        return Clone(latest);
    }

    private async Task<ExecutionRun> ReportAsync(string runId, ExecutionRunUpdate update, CancellationToken ct)
    {
        ExecutionContractValidator.ValidateRunUpdate(update, Limits);
        var run = await Store.GetRunAsync(runId, ct: ct) ??
            throw new InvalidOperationException($"Execution run '{runId}' was not found.");
        if (ExecutionRunStatuses.IsTerminal(run.Status))
        {
            return run;
        }

        ApplyUpdate(run, update);
        run.UpdatedAtUtc = DateTime.UtcNow;
        run = await Store.UpsertRunAsync(run, ct);
        if (ExecutionRunStatuses.IsTerminal(run.Status) || run.CancellationRequested)
        {
            return Clone(run);
        }

        await AppendEventAsync(run.Id, ExecutionEventTypes.RunStatus, run.Attempt, run.Status, run.CurrentStep, "info", update.StatusDetails, ct);
        return Clone(run);
    }

    private async Task RecordEventAsync(string runId, string type, string? message, string severity, JsonObject? details, CancellationToken ct)
    {
        var run = await Store.GetRunAsync(runId, ct: ct);
        await AppendEventAsync(runId, type, run?.Attempt ?? 0, run?.Status, message, severity, details, ct);
    }

    private async Task<ExecutionArtifact> PutArtifactAsync(string runId, ExecutionArtifactWrite artifactWrite, CancellationToken ct)
    {
        ExecutionContractValidator.ValidateArtifactWrite(artifactWrite, Limits);
        var text = artifactWrite.Text;
        var content = CloneNode(artifactWrite.Content);
        if (text is null && content is not null)
        {
            text = content.ToJsonString(ExecutionJson.Options);
        }

        text ??= artifactWrite.Uri ?? string.Empty;
        var artifact = new ExecutionArtifact
        {
            Id = OrderedId.CreateString(),
            RunId = runId,
            Name = NormalizeRequired(artifactWrite.Name, "Artifact name"),
            Kind = string.IsNullOrWhiteSpace(artifactWrite.Kind) ? ExecutionArtifactKinds.Json : artifactWrite.Kind,
            MediaType = artifactWrite.MediaType,
            Content = content,
            Text = artifactWrite.Text,
            Uri = artifactWrite.Uri,
            ContentHash = Sha256(text),
            SizeBytes = Encoding.UTF8.GetByteCount(text),
            CreatedAtUtc = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(artifactWrite.Metadata, StringComparer.Ordinal)
        };
        AddArtifactMetadata(artifact.Metadata, "inline", (artifact.SizeBytes <= Limits.MaxArtifactInlineBytes).ToString().ToLowerInvariant());
        AddArtifactMetadata(artifact.Metadata, "storage", string.IsNullOrWhiteSpace(artifact.Uri) ? Options.StatusStoreName : "external-uri");

        await Store.PutArtifactAsync(artifact, ct);
        await AppendEventAsync(runId, ExecutionEventTypes.ArtifactWritten, 0, null, $"Artifact '{artifact.Name}' written.", "info", null, ct);
        return Clone(artifact);
    }

    private async Task<ExecutionCheckpoint> PutCheckpointAsync(string runId, ExecutionCheckpointWrite checkpointWrite, CancellationToken ct)
    {
        ExecutionContractValidator.ValidateCheckpointWrite(checkpointWrite, Limits);
        var content = CloneNode(checkpointWrite.Content) ?? new JsonObject();
        var contentText = content.ToJsonString(ExecutionJson.Options);
        var checkpoint = new ExecutionCheckpoint
        {
            RunId = NormalizeRequired(runId, "Run id"),
            Key = NormalizeRequired(checkpointWrite.Key, "Checkpoint key"),
            Content = content,
            ContentHash = Sha256(contentText),
            UpdatedAtUtc = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(checkpointWrite.Metadata, StringComparer.Ordinal)
        };

        await Store.PutCheckpointAsync(checkpoint, ct);
        await AppendEventAsync(runId, ExecutionEventTypes.CheckpointWritten, 0, null, $"Checkpoint '{checkpoint.Key}' written.", "info", null, ct);
        return Clone(checkpoint);
    }

    private async Task AppendEventAsync(
        string runId,
        string type,
        int attempt,
        string? status,
        string? message,
        string severity,
        JsonObject? details,
        CancellationToken ct)
    {
        message = ExecutionContractValidator.BoundText(message, Limits.MaxTraceMessageChars);
        var item = new ExecutionTraceEvent
        {
            Id = OrderedId.CreateString(),
            SequenceId = OrderedId.CreateString(),
            RunId = runId,
            Type = type,
            TimestampUtc = DateTime.UtcNow,
            Attempt = attempt,
            Status = status,
            Message = message,
            Severity = string.IsNullOrWhiteSpace(severity) ? "info" : severity,
            Details = CloneObject(details),
            Context = await BuildEventContextAsync(runId, ct)
        };
        ExecutionContractValidator.ValidateTraceEvent(item, Limits);
        await Store.AppendEventAsync(item, ct);
    }

    private async Task<Dictionary<string, string>> BuildEventContextAsync(string runId, CancellationToken ct)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["adapterId"] = Adapter.AdapterId,
            ["runtimeKind"] = Adapter.RuntimeKind,
            ["workerId"] = string.IsNullOrWhiteSpace(Options.WorkerId) ? Environment.MachineName : Options.WorkerId,
            ["runId"] = runId
        };

        var run = await Store.GetRunAsync(runId, includeResult: false, ct);
        if (run is null)
        {
            return context;
        }

        AddContextValue(context, "correlationId", run.CorrelationId);
        AddContextValue(context, "handlerId", run.HandlerId);
        AddContextValue(context, "pluginId", run.PluginId);
        return context;
    }

    private async Task<ExecutionRun> CancelActiveRunAsync(ExecutionRun run, CancellationToken ct)
    {
        run = await Store.GetRunAsync(run.Id, ct: ct) ?? run;
        if (ExecutionRunStatuses.IsTerminal(run.Status))
        {
            return Clone(run);
        }

        if (run.Status != ExecutionRunStatuses.Running)
        {
            run = AzureDurableExecutionDialect.StartActivityAttempt(run);
        }

        var cancelled = AzureDurableExecutionDialect.ApplyActivityResult(
            run,
            new AzureDurableActivityResult
            {
                RunId = run.Id,
                Result = ExecutionRunResult.Cancelled(run.Result)
            },
            DateTime.UtcNow,
            Limits);
        cancelled = await Store.UpsertRunAsync(cancelled, ct);
        if (cancelled.Status != ExecutionRunStatuses.Cancelled)
        {
            return Clone(cancelled);
        }

        await AppendEventAsync(cancelled.Id, ExecutionEventTypes.RunCompleted, cancelled.Attempt, cancelled.Status, cancelled.Error, "warning", null, ct);
        return Clone(cancelled);
    }

    private static void Reject(ExecutionRun run, string failureClass, string error, DateTime now)
    {
        run.Status = ExecutionRunStatuses.Rejected;
        run.CompletedAtUtc = now;
        run.UpdatedAtUtc = now;
        run.DurationMs = 0;
        run.FailureClass = failureClass;
        run.Error = error;
        run.CancellationRequested = false;
    }

    private static ExecutionRunResult ToRunResult(ExecutionRun run)
    {
        if (run.Status == ExecutionRunStatuses.Succeeded)
        {
            return ExecutionRunResult.Succeeded(CloneNode(run.Result), CloneObject(run.StatusDetails));
        }

        if (run.Status == ExecutionRunStatuses.Cancelled)
        {
            return ExecutionRunResult.Cancelled(CloneNode(run.Result));
        }

        return ExecutionRunResult.Failed(
            run.FailureClass ?? ExecutionFailureClasses.Unknown,
            run.Error ?? $"Execution run is {run.Status}.",
            CloneNode(run.Result));
    }

    private static void ApplyUpdate(ExecutionRun run, ExecutionRunUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.Status))
        {
            ExecutionRunLifecycle.EnsureTransition(run.Status, update.Status!);
            run.Status = update.Status!;
        }

        run.Requested = update.Requested ?? run.Requested;
        run.Attempted = update.Attempted ?? run.Attempted;
        run.Succeeded = update.Succeeded ?? run.Succeeded;
        run.Failed = update.Failed ?? run.Failed;
        run.Progress = update.Progress.HasValue ? Math.Clamp(update.Progress.Value, 0, 1) : run.Progress;
        run.CurrentStep = update.CurrentStep ?? run.CurrentStep;
        run.FailureClass = update.FailureClass ?? run.FailureClass;
        run.Error = update.Error ?? run.Error;
        run.Result = CloneNode(update.Result ?? run.Result);
        run.StatusDetails = CloneObject(update.StatusDetails ?? run.StatusDetails);
    }

    private static void EnsureIdempotentReplay(
        ExecutionRun existing,
        string handlerId,
        string? pluginId,
        string payloadHash,
        string idempotencyKey)
    {
        if (string.Equals(existing.HandlerId, handlerId, StringComparison.Ordinal) &&
            string.Equals(existing.PluginId, pluginId, StringComparison.Ordinal) &&
            string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Execution idempotency key '{idempotencyKey}' already belongs to a different run request.");
    }

    private static string? ResolvePluginId(string handlerId, string? requestedPluginId, string? handlerPluginId)
    {
        requestedPluginId = NormalizeOptional(requestedPluginId);
        handlerPluginId = NormalizeOptional(handlerPluginId);
        if (requestedPluginId is not null &&
            handlerPluginId is not null &&
            !string.Equals(requestedPluginId, handlerPluginId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Execution handler '{handlerId}' belongs to plugin '{handlerPluginId}', not '{requestedPluginId}'.");
        }

        return requestedPluginId ?? handlerPluginId;
    }

    private static void AddArtifactMetadata(Dictionary<string, string> metadata, string key, string value)
    {
        if (!metadata.ContainsKey(key))
        {
            metadata[key] = value;
        }
    }

    private static void AddContextValue(Dictionary<string, string> context, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            context[key] = value;
        }
    }

    private static string NormalizeRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static JsonNode? CloneNode(JsonNode? value)
    {
        return value is null ? null : JsonNode.Parse(value.ToJsonString(ExecutionJson.Options));
    }

    private static JsonObject? CloneObject(JsonObject? value)
    {
        return CloneNode(value) as JsonObject;
    }

    private static T Clone<T>(T value)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ExecutionJson.Options), ExecutionJson.Options)!;
    }

    private static string SerializeNode(JsonNode? node)
    {
        return node?.ToJsonString(ExecutionJson.Options) ?? string.Empty;
    }

    private static string Sha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private sealed class AzureDurableExecutionRunContext : IExecutionRunContext
    {
        private readonly AzureDurableExecutionHost _host;

        public AzureDurableExecutionRunContext(AzureDurableExecutionHost host, ExecutionRun run, CancellationToken cancellationToken)
        {
            _host = host;
            Run = Clone(run);
            CancellationToken = cancellationToken;
        }

        public ExecutionRun Run { get; private set; }
        public CancellationToken CancellationToken { get; }

        public async Task<ExecutionRun> ReportAsync(ExecutionRunUpdate update, CancellationToken ct = default)
        {
            Run = await _host.ReportAsync(Run.Id, update, Link(ct));
            return Clone(Run);
        }

        public Task RecordEventAsync(string type, string? message = null, string severity = "info", JsonObject? details = null, CancellationToken ct = default)
        {
            return _host.RecordEventAsync(Run.Id, type, message, severity, details, Link(ct));
        }

        public Task<ExecutionArtifact> PutArtifactAsync(ExecutionArtifactWrite artifact, CancellationToken ct = default)
        {
            return _host.PutArtifactAsync(Run.Id, artifact, Link(ct));
        }

        public Task<ExecutionCheckpoint> PutCheckpointAsync(ExecutionCheckpointWrite checkpoint, CancellationToken ct = default)
        {
            return _host.PutCheckpointAsync(Run.Id, checkpoint, Link(ct));
        }

        public Task<ExecutionCheckpoint?> GetCheckpointAsync(string key, CancellationToken ct = default)
        {
            return _host.GetCheckpointAsync(Run.Id, key, Link(ct));
        }

        public Task<ExecutionLease?> TryAcquireLeaseAsync(string leaseKey, double ttlSeconds = 60, JsonObject? metadata = null, CancellationToken ct = default)
        {
            return _host.TryAcquireLeaseAsync(new ExecutionLeaseRequest
            {
                LeaseKey = leaseKey,
                OwnerId = Run.Id,
                RunId = Run.Id,
                TtlSeconds = ttlSeconds,
                Metadata = metadata
            }, Link(ct));
        }

        public Task<bool> ReleaseLeaseAsync(string leaseKey, CancellationToken ct = default)
        {
            return _host.ReleaseLeaseAsync(leaseKey, Run.Id, Link(ct));
        }

        public Task<ExecutionTimer> ScheduleTimerAsync(string name, DateTime fireAtUtc, JsonNode? payload = null, CancellationToken ct = default)
        {
            return _host.ScheduleTimerAsync(new ExecutionTimerRequest
            {
                Name = name,
                RunId = Run.Id,
                FireAtUtc = fireAtUtc,
                Payload = payload
            }, Link(ct));
        }

        public Task<ExecutionExternalEvent> RaiseEventAsync(string name, JsonNode? payload = null, CancellationToken ct = default)
        {
            return _host.RaiseEventAsync(new ExecutionExternalEventRequest
            {
                Name = name,
                RunId = Run.Id,
                Payload = payload
            }, Link(ct));
        }

        public Task<ExecutionWaitResult> WaitForExternalEventAsync(string name, DateTime? timeoutAtUtc = null, CancellationToken ct = default)
        {
            return WaitForExternalEventCoreAsync(name, timeoutAtUtc, Link(ct));
        }

        public Task<ExecutionWaitResult> WaitForTimerAsync(string name, DateTime fireAtUtc, JsonNode? payload = null, CancellationToken ct = default)
        {
            return WaitForTimerCoreAsync(name, fireAtUtc, payload, Link(ct));
        }

        private async Task<ExecutionWaitResult> WaitForExternalEventCoreAsync(string name, DateTime? timeoutAtUtc, CancellationToken ct)
        {
            ExecutionContractValidator.ValidateExternalEventRequest(new ExecutionExternalEventRequest
            {
                RunId = Run.Id,
                Name = name
            }, _host.Limits);
            var normalizedName = NormalizeRequired(name, "External event name");
            var outcome = await _host.Store.TakeDurableWaitOutcomeAsync(
                Run.Id,
                AzureDurableWaitKinds.ExternalEvent,
                normalizedName,
                ct);
            if (outcome is not null) return outcome;

            var timeout = timeoutAtUtc?.ToUniversalTime();
            if (timeout.HasValue && timeout.Value <= DateTime.UtcNow)
            {
                return new ExecutionWaitResult { Name = normalizedName, Outcome = ExecutionWaitOutcomes.TimedOut };
            }

            var wait = await _host.Store.RegisterDurableWaitAsync(
                new AzureDurableWait
                {
                    Kind = AzureDurableWaitKinds.ExternalEvent,
                    Name = normalizedName,
                    FireAtUtc = timeout
                },
                Run.Id,
                ct);
            await _host.AppendEventAsync(
                Run.Id,
                ExecutionEventTypes.WaitRegistered,
                Run.Attempt,
                ExecutionRunStatuses.Waiting,
                $"Waiting for external event '{normalizedName}'.",
                "info",
                null,
                ct);
            throw new AzureDurableExecutionSuspendedException(wait);
        }

        private async Task<ExecutionWaitResult> WaitForTimerCoreAsync(string name, DateTime fireAtUtc, JsonNode? payload, CancellationToken ct)
        {
            ExecutionContractValidator.ValidateTimerRequest(new ExecutionTimerRequest
            {
                RunId = Run.Id,
                Name = name,
                FireAtUtc = fireAtUtc,
                Payload = payload
            }, _host.Limits);
            var normalizedName = NormalizeRequired(name, "Timer name");
            var outcome = await _host.Store.TakeDurableWaitOutcomeAsync(
                Run.Id,
                AzureDurableWaitKinds.Timer,
                normalizedName,
                ct);
            if (outcome is not null) return outcome;

            var fireAt = fireAtUtc.ToUniversalTime();
            var timer = new ExecutionTimer
            {
                Id = OrderedId.CreateString(),
                Name = normalizedName,
                RunId = Run.Id,
                FireAtUtc = fireAt,
                CreatedAtUtc = DateTime.UtcNow,
                Payload = CloneNode(payload)
            };
            if (fireAt <= DateTime.UtcNow)
            {
                return new ExecutionWaitResult
                {
                    Name = normalizedName,
                    Outcome = ExecutionWaitOutcomes.Timer,
                    Timer = timer
                };
            }

            var wait = await _host.Store.RegisterDurableWaitAsync(
                new AzureDurableWait
                {
                    Kind = AzureDurableWaitKinds.Timer,
                    Name = normalizedName,
                    FireAtUtc = fireAt,
                    Timer = timer
                },
                Run.Id,
                ct);
            await _host.AppendEventAsync(
                Run.Id,
                ExecutionEventTypes.WaitRegistered,
                Run.Attempt,
                ExecutionRunStatuses.Waiting,
                $"Waiting for timer '{normalizedName}'.",
                "info",
                null,
                ct);
            throw new AzureDurableExecutionSuspendedException(wait);
        }

        private CancellationToken Link(CancellationToken ct)
        {
            return ct.CanBeCanceled ? ct : CancellationToken;
        }
    }

    private sealed class AzureDurableExecutionSuspendedException : Exception
    {
        public AzureDurableExecutionSuspendedException(AzureDurableWait wait)
        {
            Wait = wait ?? throw new ArgumentNullException(nameof(wait));
        }

        public AzureDurableWait Wait { get; }
    }
}
