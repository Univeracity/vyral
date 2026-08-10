using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;

namespace Vyral.Execution.Aws;

/// <summary>
/// DynamoDB and SQS implementation of the portable execution runtime boundary. Handler code runs
/// in an external worker selected by the dispatcher; this adapter owns durable state and never
/// executes a handler in the API process.
/// </summary>
public sealed class AwsDynamoExecutionRuntimeAdapter : IExecutionRuntimeAdapter, IExecutionRuntimeMaintenance, IExternalExecutionWorkerRuntime
{
    private readonly IAwsDynamoExecutionStateStore _state;
    private readonly IExecutionRunDispatcher _dispatcher;
    private readonly IReadOnlyDictionary<string, IExecutionRunDispatcher> _workerDispatchers;
    private readonly IObjectStore? _artifactObjectStore;
    private readonly ConcurrentDictionary<string, IExecutionHandler> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExecutionHandlerDescriptor> _externalHandlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExecutionPluginDescriptor> _plugins = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, ExecutionProductPolicy> _productPolicies;

    public AwsDynamoExecutionRuntimeAdapter(
        IAwsDynamoExecutionStateStore state,
        IExecutionRunDispatcher dispatcher,
        AwsDynamoExecutionRuntimeOptions? options = null,
        IObjectStore? artifactObjectStore = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Options = options ?? new AwsDynamoExecutionRuntimeOptions();
        _artifactObjectStore = artifactObjectStore;
        ValidateOptions();
        _productPolicies = BuildProductPolicies(Options.ProductPolicies);
        _workerDispatchers = BuildWorkerDispatchers(Options.WorkerDispatchers);
        Adapter = new ExecutionRuntimeAdapterDescriptor
        {
            AdapterId = Options.AdapterId,
            RuntimeKind = "aws.dynamodb-sqs",
            DisplayName = "AWS DynamoDB and SQS execution runtime",
            Version = "0.2.0",
            Capabilities =
            {
                ExecutionCapabilityIds.RemoteOrchestration,
                ExecutionCapabilityIds.ExternalWorkers,
                ExecutionCapabilityIds.DurableRuns,
                ExecutionCapabilityIds.DurableTimers,
                ExecutionCapabilityIds.ExternalEvents,
                ExecutionCapabilityIds.DurableWaits,
                ExecutionCapabilityIds.Cancellation,
                ExecutionCapabilityIds.Retries,
                ExecutionCapabilityIds.Leases,
                ExecutionCapabilityIds.Artifacts,
                ExecutionCapabilityIds.TraceHistory,
                ExecutionCapabilityIds.Idempotency
            },
            Metadata =
            {
                ["stateStore"] = "dynamodb",
                ["dispatcher"] = "sqs",
                ["workerTarget"] = "external-worker",
                ["timerDispatch"] = "maintenance-for-delays-over-900-seconds"
            }
        };
        ExecutionContractValidator.ValidateAdapterDescriptor(Adapter, Options.Limits);
    }

    public AwsDynamoExecutionRuntimeOptions Options { get; }
    public ExecutionRuntimeAdapterDescriptor Adapter { get; }

    public void RegisterHandler(IExecutionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ExecutionContractValidator.ValidateHandlerDescriptor(handler.Descriptor, Options.Limits);
        var handlerId = handler.Descriptor.HandlerId.Trim();
        // Deployment configuration can deliberately replace a server-local handler with an
        // external worker implementation of the same portable handler id.
        if (_externalHandlers.ContainsKey(handlerId)) return;
        if (_handlers.ContainsKey(handlerId)) throw new InvalidOperationException($"Execution handler '{handlerId}' is already registered.");

        // Keep server-hosted handlers distinct from explicitly configured external handlers.
        // Remote orchestration never runs this delegate in the API process; StartRunAsync rejects
        // it unless deployment configuration also registers an external worker route.
        _handlers[handlerId] = handler;
    }

    public void RegisterPlugin(IExecutionPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ExecutionContractValidator.ValidatePluginDescriptor(plugin.Descriptor, Options.Limits);
        foreach (var handler in plugin.Handlers) RegisterHandler(handler);
        _plugins[plugin.Descriptor.PluginId.Trim()] = Clone(plugin.Descriptor);
    }

    public void RegisterExternalHandler(ExecutionHandlerDescriptor handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ExecutionContractValidator.ValidateHandlerDescriptor(handler, Options.Limits);
        var handlerId = handler.HandlerId.Trim();
        if (_handlers.ContainsKey(handlerId) || _externalHandlers.ContainsKey(handlerId)) throw new InvalidOperationException($"Execution handler '{handlerId}' is already registered.");
        if (Options.RequireExplicitWorkerRoutes && !_workerDispatchers.ContainsKey(handlerId))
        {
            throw new InvalidOperationException($"External execution handler '{handlerId}' requires an explicit AWS worker route.");
        }
        _externalHandlers[handlerId] = Clone(handler);
    }

    public IReadOnlyList<ExecutionPluginDescriptor> ListPlugins() => _plugins.Values.Select(Clone).OrderBy(item => item.PluginId, StringComparer.Ordinal).ToList();
    public IReadOnlyList<ExecutionHandlerDescriptor> ListHandlers() => _handlers.Values.Select(item => item.Descriptor).Concat(_externalHandlers.Values).Select(Clone).OrderBy(item => item.HandlerId, StringComparer.Ordinal).ToList();

    public async Task<ExecutionRun> StartRunAsync(ExecutionRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateRunRequest(request, Options.Limits);
        var handlerId = request.HandlerId.Trim();
        EnsureRunBoundary(request, handlerId);
        var externalHandler = _externalHandlers.GetValueOrDefault(handlerId);
        var handler = externalHandler ?? (_handlers.TryGetValue(handlerId, out var registered) ? registered.Descriptor : null);
        var retry = ExecutionContractValidator.NormalizeRetryPolicy(request.RetryPolicy, Options.Limits);
        var payloadHash = Sha256(request.Payload?.ToJsonString(ExecutionJson.Options) ?? "{}");
        var now = DateTime.UtcNow;
        var run = new ExecutionRun
        {
            Id = Guid.NewGuid().ToString("N"),
            HandlerId = handlerId,
            PluginId = handler?.PluginId ?? request.PluginId,
            Status = ExecutionRunStatuses.Queued,
            MaxAttempts = retry.MaxAttempts,
            RetryPolicy = retry,
            IdempotencyKey = request.IdempotencyKey,
            CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? Guid.NewGuid().ToString("N") : request.CorrelationId,
            Scope = request.Scope is null ? null : Clone(request.Scope),
            Payload = CloneNode(request.Payload),
            PayloadHash = payloadHash,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ScheduledAtUtc = request.ScheduledAtUtc,
            Tags = new Dictionary<string, string>(request.Tags, StringComparer.Ordinal)
        };
        if (handler is null)
        {
            run.Status = ExecutionRunStatuses.Rejected;
            run.FailureClass = ExecutionFailureClasses.HandlerMissing;
            run.Error = $"Execution handler '{handlerId}' is not registered.";
            run.CompletedAtUtc = now;
            run.DurationMs = 0;
        }
        else if (externalHandler is null)
        {
            run.Status = ExecutionRunStatuses.Rejected;
            run.FailureClass = ExecutionFailureClasses.Platform;
            run.Error = $"Execution handler '{handlerId}' is registered in the API process but has no configured external AWS worker route.";
            run.CompletedAtUtc = now;
            run.DurationMs = 0;
        }
        else if (!string.IsNullOrWhiteSpace(request.PluginId) && !string.Equals(request.PluginId, handler.PluginId, StringComparison.Ordinal))
        {
            run.Status = ExecutionRunStatuses.Rejected;
            run.FailureClass = ExecutionFailureClasses.PluginMismatch;
            run.Error = $"Execution handler '{handlerId}' belongs to plugin '{handler.PluginId}', not '{request.PluginId}'.";
            run.CompletedAtUtc = now;
            run.DurationMs = 0;
        }
        else if (request.ScheduledAtUtc is { } scheduled && scheduled > now)
        {
            run.Status = ExecutionRunStatuses.Waiting;
        }

        ExecutionRun? capacityRejected = null;
        if (ExecutionRunLifecycle.IsActive(run.Status))
        {
            capacityRejected = Clone(run);
            capacityRejected.Status = ExecutionRunStatuses.Rejected;
            capacityRejected.FailureClass = ExecutionFailureClasses.QueueFull;
            capacityRejected.Error = $"Execution run queue is full. Max active runs: {Options.MaxActiveRuns}.";
            capacityRejected.CompletedAtUtc = now;
            capacityRejected.UpdatedAtUtc = now;
            capacityRejected.DurationMs = 0;
        }

        var creation = await _state.CreateRunAtomicallyAsync(
            run,
            capacityRejected,
            Options.MaxActiveRuns,
            string.IsNullOrWhiteSpace(run.IdempotencyKey) ? null : BuildIdempotencyScopeKey(run),
            ct);
        if (!creation.Created)
        {
            if (!string.Equals(creation.HandlerId, handlerId, StringComparison.Ordinal) || !string.Equals(creation.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Execution idempotency key '{request.IdempotencyKey}' already belongs to a different run request.");
            }

            var replayed = await _state.GetRunAsync(creation.RunId, true, ct)
                ?? throw new InvalidOperationException("Execution idempotency reservation references a missing run.");
            replayed.AdmissionReplayed = true;
            return replayed;
        }
        run = creation.CreatedRun ?? throw new InvalidOperationException("Execution run creation did not return its durable run.");
        await AppendAsync(run, run.Status == ExecutionRunStatuses.Rejected ? ExecutionEventTypes.RunRejected : ExecutionEventTypes.RunCreated, run.Error ?? "Execution run created.", ct);
        if (run.Status is ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting)
        {
            await DispatchAsync(run, ExecutionDispatchReasons.RunReady, run.ScheduledAtUtc, ct);
        }

        return run;
    }

    public Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default) => _state.GetRunAsync(runId, includeResult, ct);
    public Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default) => _state.ListRunsAsync(NormalizeRunQuery(query), ct);

    public async Task<ExecutionRun?> CancelRunAsync(string runId, CancellationToken ct = default)
    {
        var before = await _state.GetRunAsync(runId, true, ct);
        if (before is null || ExecutionRunStatuses.IsTerminal(before.Status)) return before;
        var run = await _state.CancelRunAtomicallyAsync(runId, ct);
        if (run is null || (!run.CancellationRequested && run.Status != ExecutionRunStatuses.Cancelled)) return run;
        await AppendAsync(run, ExecutionEventTypes.RunCancellationRequested, "Execution cancellation requested.", ct);
        if (run.Status == ExecutionRunStatuses.Cancelled)
            await AppendAsync(run, ExecutionEventTypes.RunCompleted, run.Error ?? "Execution run was cancelled.", ct);
        return run;
    }

    public Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default)
    {
        var limit = query?.Limit ?? Options.DefaultHistoryLimit;
        if (limit <= 0 || limit > Options.MaxHistoryLimit) throw new InvalidOperationException($"Execution history limit must be between 1 and {Options.MaxHistoryLimit}.");
        return _state.GetHistoryAsync(runId, limit, ct);
    }
    public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default) => _state.ListArtifactsAsync(runId, ct);
    public Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default) => _state.GetArtifactAsync(runId, artifactRef, ct);
    public Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default) => _state.GetCheckpointAsync(runId, key, ct);
    public Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default) => _state.TryAcquireLeaseAsync(request, ct);
    public Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default) => _state.ReleaseLeaseAsync(leaseKey, ownerId, ct);

    public async Task<ExecutionExternalWorkerLease?> LeaseNextRunAsync(ExecutionExternalWorkerLeaseRequest request, CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateExternalWorkerLeaseRequest(request, Options.Limits);
        var allowed = new HashSet<string>(request.HandlerIds, StringComparer.Ordinal);
        IReadOnlyList<ExecutionRun> candidates;
        if (!string.IsNullOrWhiteSpace(request.RunId))
        {
            var requested = await _state.GetRunAsync(request.RunId, true, ct);
            candidates = requested is null ? Array.Empty<ExecutionRun>() : [requested];
        }
        else
        {
            var dueRunIds = await _state.ListDueExternalRunIdsAsync(allowed, 200, ct);
            var loaded = new List<ExecutionRun>(dueRunIds.Count);
            foreach (var runId in dueRunIds)
            {
                var run = await _state.GetRunAsync(runId, true, ct);
                if (run is not null) loaded.Add(run);
            }

            candidates = loaded;
        }
        foreach (var candidate in candidates.OrderBy(run => run.CreatedAtUtc))
        {
            await ResumeDueWaitAsync(candidate.Id, ct);
            var current = await _state.GetRunAsync(candidate.Id, true, ct) ?? candidate;
            if (!allowed.Contains(current.HandlerId) || !_externalHandlers.ContainsKey(current.HandlerId) ||
                !IsWorkerPermitted(current, request.WorkerId))
            {
                continue;
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var owner = ExternalLeaseOwner(request.WorkerId, token);
            var claim = await _state.TryClaimExternalRunAsync(current.Id, new ExecutionLeaseRequest
            {
                LeaseKey = ExternalLeaseKey(current.Id),
                OwnerId = owner,
                RunId = current.Id,
                TtlSeconds = request.TtlSeconds,
                Metadata = new JsonObject { ["protocol"] = "external_worker", ["workerId"] = request.WorkerId, ["tokenHash"] = Hash(token) }
            }, ct);
            if (claim is null) continue;

            if (claim.Recovered)
            {
                await AppendAsync(claim.Run, ExecutionEventTypes.RunStatus, "Expired external worker lease recovered.", ct);
            }
            await AppendAsync(claim.Run, ExecutionEventTypes.LeaseAcquired, "External worker lease acquired.", ct);
            await AppendAsync(claim.Run, ExecutionEventTypes.RunStarted, "External worker run started.", ct);
            // An SQS delivery can be acknowledged by a concurrent worker before this
            // lease expires. Schedule an independent expiry probe so a crashed worker cannot
            // leave a run running forever after its original task has been acknowledged.
            await DispatchAsync(claim.Run, ExecutionDispatchReasons.LeaseExpired, claim.Lease.ExpiresAtUtc, ct);
            return new ExecutionExternalWorkerLease { LeaseKey = claim.Lease.LeaseKey, LeaseToken = token, WorkerId = request.WorkerId, Run = claim.Run, AcquiredAtUtc = claim.Lease.AcquiredAtUtc, ExpiresAtUtc = claim.Lease.ExpiresAtUtc };
        }

        return null;
    }

    public async Task<ExecutionExternalWorkerLease> HeartbeatExternalLeaseAsync(ExecutionExternalWorkerHeartbeatRequest request, CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateExternalWorkerHeartbeatRequest(request, Options.Limits);
        var existing = await RequireExternalLeaseAsync(request.LeaseKey, request.LeaseToken, request.WorkerId, ct);
        var renewed = await _state.RenewLeaseAsync(new ExecutionLeaseRequest { LeaseKey = existing.LeaseKey, OwnerId = existing.OwnerId, RunId = existing.RunId, TtlSeconds = request.TtlSeconds, Metadata = existing.Metadata }, ct)
            ?? throw new InvalidOperationException("External worker lease is no longer active.");
        var run = await _state.GetRunAsync(renewed.RunId!, true, ct) ?? throw new InvalidOperationException("External worker run was not found.");
        await DispatchAsync(run, ExecutionDispatchReasons.LeaseExpired, renewed.ExpiresAtUtc, ct);
        return new ExecutionExternalWorkerLease { LeaseKey = renewed.LeaseKey, LeaseToken = request.LeaseToken, WorkerId = request.WorkerId, Run = run, AcquiredAtUtc = renewed.AcquiredAtUtc, ExpiresAtUtc = renewed.ExpiresAtUtc };
    }

    public async Task<ExecutionRun> ReportExternalLeaseAsync(ExecutionExternalWorkerReportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerReportRequest(request, Options.Limits);
        var run = await _state.UpdateExternalRunUnderLeaseAsync(
            request.LeaseKey,
            ExternalLeaseOwner(request.WorkerId, request.LeaseToken),
            request.Update,
            ct);
        await AppendAsync(run, ExecutionEventTypes.RunStatus, run.CurrentStep ?? "External worker progress reported.", ct);
        return run;
    }

    public async Task RecordExternalLeaseEventAsync(ExecutionExternalWorkerEventRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerEventRequest(request, Options.Limits);
        var lease = await RequireExternalLeaseAsync(request.LeaseKey, request.LeaseToken, request.WorkerId, ct);
        var run = await _state.GetRunAsync(lease.RunId!, true, ct) ?? throw new InvalidOperationException("External worker run was not found.");
        await _state.AppendHistoryUnderLeaseAsync(request.LeaseKey, lease.OwnerId, new ExecutionTraceEvent
        {
            RunId = run.Id,
            Attempt = run.Attempt,
            Status = run.Status,
            Type = request.Type,
            Severity = request.Severity,
            Message = request.Message,
            Details = RedactTraceDetails(run, request.Details),
            Context = BuildTraceContext(run, request.WorkerId)
        }, ct);
    }

    public async Task<ExecutionArtifact> PutExternalLeaseArtifactAsync(ExecutionExternalWorkerArtifactRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerArtifactRequest(request, Options.Limits);
        var lease = await RequireExternalLeaseAsync(request.LeaseKey, request.LeaseToken, request.WorkerId, ct);
        var run = await _state.GetRunAsync(lease.RunId!, true, ct) ?? throw new InvalidOperationException("External worker run was not found.");
        EnsureArtifactBoundary(run, request.Artifact.Name);
        var artifact = await CreateArtifactAsync(run, request.Artifact, ct);
        await _state.PutArtifactUnderLeaseAsync(request.LeaseKey, lease.OwnerId, artifact, ct);
        await AppendAsync(run, ExecutionEventTypes.ArtifactWritten, $"External worker wrote artifact '{artifact.Name}'.", ct);
        return artifact;
    }

    public async Task<ExecutionCheckpoint> CheckpointExternalLeaseAsync(ExecutionExternalWorkerCheckpointRequest request, CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateExternalWorkerCheckpointRequest(request, Options.Limits);
        var lease = await RequireExternalLeaseAsync(request.LeaseKey, request.LeaseToken, request.WorkerId, ct);
        var checkpoint = new ExecutionCheckpoint { RunId = lease.RunId!, Key = request.Checkpoint.Key, Content = CloneNode(request.Checkpoint.Content), Metadata = new Dictionary<string, string>(request.Checkpoint.Metadata, StringComparer.Ordinal), UpdatedAtUtc = DateTime.UtcNow };
        checkpoint.ContentHash = Hash(checkpoint.Content?.ToJsonString(ExecutionJson.Options) ?? "{}");
        await _state.PutCheckpointUnderLeaseAsync(lease.LeaseKey, lease.OwnerId, checkpoint, ct);
        return checkpoint;
    }

    public async Task<ExecutionCheckpoint?> GetExternalLeaseCheckpointAsync(ExecutionExternalWorkerCheckpointReadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerCheckpointReadRequest(request, Options.Limits);
        return await _state.GetCheckpointUnderLeaseAsync(
            request.LeaseKey,
            ExternalLeaseOwner(request.WorkerId, request.LeaseToken),
            request.Key,
            ct);
    }

    public async Task<ExecutionExternalWorkerWaitResponse> WaitExternalLeaseAsync(ExecutionExternalWorkerWaitRequest request, CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateExternalWorkerWaitRequest(request, Options.Limits);
        var lease = await RequireExternalLeaseAsync(request.LeaseKey, request.LeaseToken, request.WorkerId, ct);
        var run = await _state.GetRunAsync(lease.RunId!, true, ct) ?? throw new InvalidOperationException("External worker run was not found.");
        var kind = request.Kind;
        var outcome = await _state.TakeWaitOutcomeUnderLeaseAsync(lease.LeaseKey, lease.OwnerId, run.Id, kind, request.Name, ct);
        if (outcome is not null) return new ExecutionExternalWorkerWaitResponse { Run = run, Suspended = false, Outcome = outcome };
        if (kind == ExecutionExternalWorkerWaitKinds.ExternalEvent)
        {
            var existingEvent = await _state.TakeExternalEventUnderLeaseAsync(lease.LeaseKey, lease.OwnerId, run.Id, request.Name, ct);
            if (existingEvent is not null) return new ExecutionExternalWorkerWaitResponse { Run = run, Suspended = false, Outcome = new ExecutionWaitResult { Name = request.Name, Outcome = ExecutionWaitOutcomes.ExternalEvent, Event = existingEvent } };
        }

        var fireAt = kind == ExecutionExternalWorkerWaitKinds.Timer ? request.FireAtUtc!.Value.ToUniversalTime() : request.TimeoutAtUtc?.ToUniversalTime();
        var timer = kind == ExecutionExternalWorkerWaitKinds.Timer ? new ExecutionTimer { Id = Guid.NewGuid().ToString("N"), Name = request.Name, RunId = run.Id, FireAtUtc = fireAt!.Value, Payload = CloneNode(request.Payload) } : null;
        if (fireAt is { } due && due <= DateTime.UtcNow)
        {
            return new ExecutionExternalWorkerWaitResponse { Run = run, Suspended = false, Outcome = kind == ExecutionExternalWorkerWaitKinds.Timer ? new ExecutionWaitResult { Name = request.Name, Outcome = ExecutionWaitOutcomes.Timer, Timer = timer } : new ExecutionWaitResult { Name = request.Name, Outcome = ExecutionWaitOutcomes.TimedOut } };
        }

        run = await _state.SuspendExternalRunUnderLeaseAsync(
            lease.LeaseKey,
            lease.OwnerId,
            new AwsDynamoExecutionWait { RunId = run.Id, Kind = kind, Name = request.Name, FireAtUtc = fireAt, Timer = timer },
            ct);
        if (fireAt.HasValue) await DispatchAsync(run, ExecutionDispatchReasons.TimerDue, fireAt, ct);
        await AppendAsync(run, ExecutionEventTypes.WaitRegistered, $"External worker waiting for '{request.Name}'.", ct);

        // An event can arrive after the first TakeExternalEventAsync call above but before the
        // wait document is visible to RaiseEventAsync. Consume that narrow race after the wait
        // is durable and dispatch a fresh delivery; returning suspended keeps the old lease from
        // completing after it has been deliberately released.
        if (kind == ExecutionExternalWorkerWaitKinds.ExternalEvent)
        {
            var racedEvent = await _state.TakeExternalEventAsync(run.Id, request.Name, ct);
            if (racedEvent is not null)
            {
                var racedOutcome = new ExecutionWaitResult { Name = request.Name, Outcome = ExecutionWaitOutcomes.ExternalEvent, Event = racedEvent };
                var resumed = await _state.TryResumeWaitAsync(run.Id, kind, request.Name, racedOutcome, ct: ct);
                if (resumed is not null)
                    await DispatchAsync(resumed, ExecutionDispatchReasons.ExternalEvent, null, ct);
            }
        }

        return new ExecutionExternalWorkerWaitResponse { Run = run, Suspended = true };
    }

    public async Task<ExecutionRun> CompleteExternalLeaseAsync(ExecutionExternalWorkerCompletionRequest request, CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateExternalWorkerCompletionRequest(request, Options.Limits);
        var completion = await _state.CompleteExternalRunUnderLeaseAsync(
            request.LeaseKey,
            ExternalLeaseOwner(request.WorkerId, request.LeaseToken),
            request.Result,
            ct);
        var run = completion.Run;
        if (completion.AlreadyCompleted) return run;
        if (completion.RetryScheduled)
        {
            await DispatchAsync(run, ExecutionDispatchReasons.RunReady, run.ScheduledAtUtc, ct);
            await AppendAsync(run, ExecutionEventTypes.RetryScheduled, $"Retry {run.Attempt + 1} of {run.MaxAttempts} scheduled.", ct);
            return run;
        }

        await AppendAsync(run, run.Status == ExecutionRunStatuses.Succeeded ? ExecutionEventTypes.RunCompleted : ExecutionEventTypes.RunFailed, run.Error ?? $"Execution run {run.Status}.", ct);
        return run;
    }

    public async Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimerRequest request, CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateTimerRequest(request, Options.Limits);
        var timer = new ExecutionTimer { Id = Guid.NewGuid().ToString("N"), Name = request.Name.Trim(), RunId = request.RunId, FireAtUtc = request.FireAtUtc.ToUniversalTime(), Payload = CloneNode(request.Payload) };
        await _state.PutTimerAsync(timer, ct);
        var run = await _state.GetRunAsync(timer.RunId!, true, ct) ?? throw new InvalidOperationException("Execution timer run was not found.");
        await DispatchAsync(run, ExecutionDispatchReasons.TimerDue, timer.FireAtUtc, ct);
        return timer;
    }

    public async Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEventRequest request, CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateExternalEventRequest(request, Options.Limits);
        var externalEvent = new ExecutionExternalEvent { Id = Guid.NewGuid().ToString("N"), Name = request.Name.Trim(), RunId = request.RunId, RaisedAtUtc = DateTime.UtcNow, Payload = CloneNode(request.Payload) };
        await _state.PutExternalEventAsync(externalEvent, ct);
        var wait = await _state.GetWaitAsync(externalEvent.RunId!, ct);
        ExecutionRun? resumed = null;
        if (wait is not null && wait.Kind == ExecutionExternalWorkerWaitKinds.ExternalEvent && wait.Name == externalEvent.Name)
        {
            resumed = await _state.TryResumeWaitAsync(
                externalEvent.RunId!,
                wait.Kind,
                wait.Name,
                new ExecutionWaitResult { Name = wait.Name, Outcome = ExecutionWaitOutcomes.ExternalEvent, Event = externalEvent },
                externalEvent.Id,
                ct);
        }
        var eventRun = resumed ?? await _state.GetRunAsync(externalEvent.RunId!, true, ct) ?? throw new InvalidOperationException("Execution event run was not found.");
        await DispatchAsync(eventRun, ExecutionDispatchReasons.ExternalEvent, null, ct);
        return externalEvent;
    }

    public async Task<ExecutionRuntimeAdapterStatus> GetAdapterStatusAsync(CancellationToken ct = default) => new()
    {
        Adapter = Clone(Adapter),
        Available = true,
        Status = "ok",
        CheckedAtUtc = DateTime.UtcNow,
        ActiveRuns = await _state.GetActiveRunCountAsync(ct),
        OperationalPolicy = BuildOperationalPolicy(),
        ResumePolicy = BuildResumePolicy(),
        Details = new JsonObject { ["registeredHandlers"] = _handlers.Count + _externalHandlers.Count }
    };

    public async Task<ExecutionMaintenanceStatus> GetMaintenanceStatusAsync(CancellationToken ct = default)
    {
        var runs = await _state.ListRunsAsync(new ExecutionRunQuery { IncludeResult = false, Limit = Options.MaintenanceScanLimit }, ct);
        return new ExecutionMaintenanceStatus
        {
            AdapterId = Adapter.AdapterId,
            RuntimeKind = Adapter.RuntimeKind,
            CheckedAtUtc = DateTime.UtcNow,
            RetentionScope = "run_owned_dynamodb",
            MaxRetainedTerminalRuns = Options.MaxRetainedTerminalRuns,
            RunCounts = runs.GroupBy(run => run.Status, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            RowCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["runs"] = runs.Count }
        };
    }

    public async Task<ExecutionMaintenancePruneResult> PruneAsync(ExecutionMaintenancePruneRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RetainTerminalRuns is < 0) throw new InvalidOperationException("Execution maintenance retainTerminalRuns must be non-negative.");
        var retain = request.RetainTerminalRuns ?? Options.MaxRetainedTerminalRuns;
        var runs = await _state.ListRunsAsync(new ExecutionRunQuery { IncludeResult = false, Limit = Options.MaintenanceScanLimit }, ct);
        var targets = runs.Where(run => ExecutionRunStatuses.IsTerminal(run.Status))
            .OrderByDescending(run => run.CompletedAtUtc ?? run.UpdatedAtUtc)
            .ThenBy(run => run.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, retain))
            .ToList();
        var result = new ExecutionMaintenancePruneResult
        {
            DryRun = request.DryRun,
            RetainTerminalRuns = Math.Max(0, retain),
            PrunedAtUtc = DateTime.UtcNow,
            RunIds = targets.Select(run => run.Id).ToList(),
            Runs = targets.Count
        };
        if (request.DryRun) return result;

        result.Runs = 0;
        foreach (var target in targets)
        {
            var deletion = await _state.DeleteRunAsync(target, ct);
            result.Runs += deletion.Runs;
            result.Events += deletion.History;
            result.Artifacts += deletion.Artifacts;
            result.Checkpoints += deletion.Checkpoints;
            result.Timers += deletion.Timers;
            result.ExternalEvents += deletion.ExternalEvents;
        }

        return result;
    }

    public async Task<ExecutionMaintenanceDispatchReconcileResult> ReconcileDispatchAsync(ExecutionMaintenanceDispatchReconcileRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit = request.Limit ?? Math.Min(1_000, Options.MaintenanceScanLimit);
        if (limit <= 0 || limit > Options.MaintenanceScanLimit)
            throw new InvalidOperationException($"Execution maintenance reconcile limit must be between 1 and {Options.MaintenanceScanLimit}.");

        var candidates = new Dictionary<string, (ExecutionRun Run, string Reason, DateTime? ScheduledAtUtc)>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        // An SQS delivery may have been acknowledged just before a worker crashed. A
        // runnable-work item is removed while leased, so separately inspect expired running
        // leases and redrive their idempotent expiry probes.
        var running = await _state.ListRunsAsync(new ExecutionRunQuery
        {
            Status = ExecutionRunStatuses.Running,
            IncludeResult = false,
            Limit = limit
        }, ct);
        foreach (var run in running)
        {
            if (candidates.Count >= limit || !_externalHandlers.ContainsKey(run.HandlerId)) continue;
            var lease = await _state.GetLeaseAsync(ExternalLeaseKey(run.Id), ct);
            if (lease is not null && lease.ExpiresAtUtc <= now)
                candidates.TryAdd(run.Id, (run, ExecutionDispatchReasons.LeaseExpired, null));
        }

        foreach (var handlerId in _externalHandlers.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (candidates.Count >= limit) break;
            var dueIds = await _state.ListDueExternalRunIdsAsync([handlerId], limit - candidates.Count, ct);
            foreach (var runId in dueIds)
            {
                if (candidates.Count >= limit) break;
                var run = await _state.GetRunAsync(runId, false, ct);
                if (run is null || !_externalHandlers.ContainsKey(run.HandlerId) || !ExecutionRunLifecycle.IsActive(run.Status)) continue;
                var reason = run.Status == ExecutionRunStatuses.Waiting ? ExecutionDispatchReasons.TimerDue : ExecutionDispatchReasons.RunReady;
                candidates.TryAdd(run.Id, (run, reason, run.ScheduledAtUtc));
            }
        }

        var result = new ExecutionMaintenanceDispatchReconcileResult
        {
            DryRun = request.DryRun,
            Limit = limit,
            ReconciledAtUtc = DateTime.UtcNow,
            CandidateRunIds = candidates.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList()
        };
        if (request.DryRun) return result;

        foreach (var candidate in candidates.Values)
        {
            try
            {
                await DispatchAsync(candidate.Run, candidate.Reason, candidate.ScheduledAtUtc, ct);
                result.Dispatched++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Failures[candidate.Run.Id] = ExecutionContractValidator.BoundText(ex.Message, Options.Limits.MaxTraceMessageChars) ?? "Dispatch reconciliation failed.";
            }
        }

        return result;
    }

    private Task AppendAsync(ExecutionRun run, string type, string message, CancellationToken ct) => _state.AppendHistoryAsync(new ExecutionTraceEvent
    {
        RunId = run.Id,
        Attempt = run.Attempt,
        Status = run.Status,
        Type = type,
        Message = message,
        Context = BuildTraceContext(run)
    }, ct);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ExecutionJson.Options), ExecutionJson.Options)!;
    private static JsonNode? CloneNode(JsonNode? value) => value is null ? null : JsonNode.Parse(value.ToJsonString(ExecutionJson.Options));
    private static string Sha256(string value) => $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";
    private static string Hash(string value) => Sha256(value);
    private static string ExternalLeaseKey(string runId) => "external-worker-run-" + runId;
    private static string ExternalLeaseOwner(string workerId, string token) => "external-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workerId + "\n" + token))).ToLowerInvariant();

    private ExecutionOperationalPolicy BuildOperationalPolicy() => new()
    {
        MaxActiveRuns = Options.MaxActiveRuns,
        MaxRetainedTerminalRuns = Options.MaxRetainedTerminalRuns,
        DefaultListLimit = Options.DefaultListLimit,
        MaxListLimit = Options.MaxListLimit,
        DefaultHistoryLimit = Options.DefaultHistoryLimit,
        MaxHistoryLimit = Options.MaxHistoryLimit,
        MaxPayloadBytes = Options.Limits.MaxPayloadBytes,
        MaxResultBytes = Options.Limits.MaxResultBytes,
        MaxStatusDetailsBytes = Options.Limits.MaxStatusDetailsBytes,
        MaxArtifactBytes = Options.Limits.MaxArtifactBytes,
        MaxArtifactInlineBytes = Options.Limits.MaxArtifactInlineBytes,
        MaxTraceMessageChars = Options.Limits.MaxTraceMessageChars,
        MaxTraceDetailsBytes = Options.Limits.MaxTraceDetailsBytes,
        MaxRetryAttempts = Options.Limits.MaxRetryAttempts,
        MaxRetryDelaySeconds = Options.Limits.MaxRetryDelaySeconds,
        MaxLeaseTtlSeconds = Options.Limits.MaxLeaseTtlSeconds,
        ConcurrencyKeyPolicy = "external_worker_lease",
        ConcurrencyRetryDelayMs = null,
        DefaultTraceSeverity = "info",
        RetentionScope = "run_owned_dynamodb"
    };

    private static ExecutionResumePolicy BuildResumePolicy() => new()
    {
        Mode = ExecutionResumePolicyModes.RestartRecovery,
        InterruptedRunningBehavior = ExecutionResumePolicyBehaviors.MayReexecuteHandler,
        ScheduledWaitingBehavior = ExecutionResumePolicyBehaviors.DispatchWhenDue,
        TerminalBehavior = ExecutionResumePolicyBehaviors.NeverResume,
        PluginCheckpointBehavior = ExecutionResumePolicyBehaviors.PluginOwned,
        IdempotencyScope = "handler_plugin_payload",
        CreatesLinkedFollowUpRuns = false
    };

    private ExecutionRunQuery NormalizeRunQuery(ExecutionRunQuery? query)
    {
        query ??= new ExecutionRunQuery();
        var limit = query.Limit ?? Options.DefaultListLimit;
        if (limit <= 0 || limit > Options.MaxListLimit) throw new InvalidOperationException($"Execution run list limit must be between 1 and {Options.MaxListLimit}.");
        return new ExecutionRunQuery
        {
            HandlerId = query.HandlerId,
            PluginId = query.PluginId,
            Status = query.Status,
            CorrelationId = query.CorrelationId,
            IdempotencyKey = query.IdempotencyKey,
            CreatedAfterUtc = query.CreatedAfterUtc,
            CreatedBeforeUtc = query.CreatedBeforeUtc,
            UpdatedAfterUtc = query.UpdatedAfterUtc,
            UpdatedBeforeUtc = query.UpdatedBeforeUtc,
            Tags = new Dictionary<string, string>(query.Tags, StringComparer.Ordinal),
            IncludeResult = query.IncludeResult,
            Limit = limit
        };
    }

    private static IReadOnlyDictionary<string, ExecutionProductPolicy> BuildProductPolicies(IReadOnlyList<ExecutionProductPolicy>? policies)
    {
        var result = new Dictionary<string, ExecutionProductPolicy>(StringComparer.Ordinal);
        foreach (var policy in policies ?? Array.Empty<ExecutionProductPolicy>())
        {
            if (string.IsNullOrWhiteSpace(policy.ProductId)) throw new InvalidOperationException("AWS execution product policy product id is required.");
            if (policy.MaxPayloadBytes is <= 0) throw new InvalidOperationException("Execution product policy max payload bytes must be positive.");
            if (!result.TryAdd(policy.ProductId.Trim(), policy)) throw new InvalidOperationException($"AWS execution product policy '{policy.ProductId}' is duplicated.");
        }

        return result;
    }

    private void ValidateOptions()
    {
        if (Options.MaxActiveRuns <= 0) throw new InvalidOperationException("AWS execution max active runs must be positive.");
        if (Options.MaxRetainedTerminalRuns < 0) throw new InvalidOperationException("AWS execution max retained terminal runs must be non-negative.");
        if (Options.DefaultListLimit <= 0 || Options.MaxListLimit < Options.DefaultListLimit)
            throw new InvalidOperationException("AWS execution list limits are invalid.");
        if (Options.DefaultHistoryLimit <= 0 || Options.MaxHistoryLimit < Options.DefaultHistoryLimit)
            throw new InvalidOperationException("AWS execution history limits are invalid.");
        if (Options.MaintenanceScanLimit <= 0) throw new InvalidOperationException("AWS execution maintenance scan limit must be positive.");
        if (Options.MaxActiveRuns > Options.MaintenanceScanLimit)
            throw new InvalidOperationException("AWS execution max active runs cannot exceed maintenance scan limit.");
        AwsDynamoExecutionLimits.Validate(Options.Limits);
        if (_artifactObjectStore is null && Options.Limits.MaxArtifactBytes > Options.Limits.MaxArtifactInlineBytes)
            throw new InvalidOperationException("AWS execution artifact limits above the inline ceiling require a configured durable artifact object store.");
        if (_artifactObjectStore is not null && string.IsNullOrWhiteSpace(Options.ArtifactObjectContainer))
            throw new InvalidOperationException("AWS execution artifact object storage requires ArtifactObjectContainer.");
    }

    private static IReadOnlyDictionary<string, IExecutionRunDispatcher> BuildWorkerDispatchers(IReadOnlyList<AwsDynamoExecutionWorkerDispatcher>? dispatchers)
    {
        var result = new Dictionary<string, IExecutionRunDispatcher>(StringComparer.Ordinal);
        foreach (var route in dispatchers ?? Array.Empty<AwsDynamoExecutionWorkerDispatcher>())
        {
            if (string.IsNullOrWhiteSpace(route.HandlerId)) throw new InvalidOperationException("AWS execution worker route handler id is required.");
            if (route.Dispatcher is null) throw new InvalidOperationException($"AWS execution worker route '{route.HandlerId}' requires a dispatcher.");
            if (!result.TryAdd(route.HandlerId.Trim(), route.Dispatcher)) throw new InvalidOperationException($"AWS execution worker route '{route.HandlerId}' is duplicated.");
        }

        return result;
    }

    private IExecutionRunDispatcher DispatcherFor(ExecutionRun run)
    {
        if (_workerDispatchers.TryGetValue(run.HandlerId, out var dispatcher)) return dispatcher;
        if (!Options.RequireExplicitWorkerRoutes) return _dispatcher;
        throw new InvalidOperationException($"Execution handler '{run.HandlerId}' has no configured AWS worker route.");
    }

    private Task DispatchAsync(ExecutionRun run, string reason, DateTime? scheduledAtUtc, CancellationToken ct)
    {
        var dispatcher = DispatcherFor(run);
        // SQS has a 15-minute delivery-delay ceiling. The DynamoDB work item remains durable and
        // ReconcileDispatchAsync delivers it once it is within that window (or immediately due),
        // preserving portable timer semantics without pretending SQS is a long-delay scheduler.
        if (dispatcher is AwsSqsExecutionDispatcher sqs && scheduledAtUtc is { } scheduled &&
            scheduled.ToUniversalTime() > DateTime.UtcNow.AddSeconds(sqs.Options.MaximumDelaySeconds))
        {
            return Task.CompletedTask;
        }

        return dispatcher.DispatchAsync(new ExecutionDispatchRequest
        {
            RunId = run.Id,
            Reason = reason,
            ScheduledAtUtc = scheduledAtUtc
        }, ct);
    }

    private void EnsureRunBoundary(ExecutionRunRequest request, string handlerId)
    {
        if (_productPolicies.Count == 0) return;
        var scope = request.Scope ?? throw new InvalidOperationException("Execution scope is required when AWS product policies are configured.");
        if (!_productPolicies.TryGetValue(scope.ProductId, out var policy)) throw new InvalidOperationException($"Execution product '{scope.ProductId}' is not configured.");
        if (policy.AllowedHandlerIds.Count > 0 && !policy.AllowedHandlerIds.Contains(handlerId)) throw new InvalidOperationException($"Handler '{handlerId}' is not allowed for product '{scope.ProductId}'.");
        if (policy.AllowedTenantIds.Count > 0 && !policy.AllowedTenantIds.Contains(scope.TenantId)) throw new InvalidOperationException($"Tenant '{scope.TenantId}' is not allowed for product '{scope.ProductId}'.");
        if (policy.MaxPayloadBytes.HasValue && Encoding.UTF8.GetByteCount(request.Payload?.ToJsonString(ExecutionJson.Options) ?? "{}") > policy.MaxPayloadBytes.Value) throw new InvalidOperationException($"Run payload exceeds the limit for product '{scope.ProductId}'.");
    }

    private bool IsWorkerPermitted(ExecutionRun run, string workerId)
    {
        if (_productPolicies.Count == 0) return true;
        return run.Scope is not null && _productPolicies.TryGetValue(run.Scope.ProductId, out var policy) &&
            (policy.AllowedServiceIdentities.Count == 0 || policy.AllowedServiceIdentities.Contains(workerId));
    }

    private void EnsureArtifactBoundary(ExecutionRun run, string artifactName)
    {
        if (_productPolicies.Count == 0) return;
        var scope = run.Scope ?? throw new InvalidOperationException("Scoped execution run is missing its scope.");
        if (!_productPolicies.TryGetValue(scope.ProductId, out var policy)) throw new InvalidOperationException($"Execution product '{scope.ProductId}' is not configured.");
        if (!string.IsNullOrWhiteSpace(policy.ArtifactPrefix) && !artifactName.StartsWith(policy.ArtifactPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Artifact '{artifactName}' must use product '{scope.ProductId}' prefix '{policy.ArtifactPrefix}'.");
    }

    private async Task<ExecutionArtifact> CreateArtifactAsync(ExecutionRun run, ExecutionArtifactWrite write, CancellationToken ct)
    {
        var content = CloneNode(write.Content);
        var material = write.Text ?? content?.ToJsonString(ExecutionJson.Options) ?? write.Uri ?? string.Empty;
        var artifact = new ExecutionArtifact
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = run.Id,
            Name = write.Name.Trim(),
            Kind = string.IsNullOrWhiteSpace(write.Kind) ? ExecutionArtifactKinds.Json : write.Kind,
            MediaType = write.MediaType,
            Text = write.Text,
            Content = content,
            Uri = write.Uri,
            ContentHash = Sha256(material),
            SizeBytes = Encoding.UTF8.GetByteCount(material),
            CreatedAtUtc = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(write.Metadata, StringComparer.Ordinal)
        };
        if (artifact.Uri is not null || artifact.SizeBytes <= Options.Limits.MaxArtifactInlineBytes)
        {
            AddArtifactStorageMetadata(artifact.Metadata, "inline", "true");
            return artifact;
        }

        if (_artifactObjectStore is null || string.IsNullOrWhiteSpace(Options.ArtifactObjectContainer))
            throw new InvalidOperationException($"AWS execution artifacts larger than {Options.Limits.MaxArtifactInlineBytes} bytes require configured durable artifact object storage.");

        var key = $"execution-artifacts/{artifact.RunId}/{artifact.Id}" +
            (string.Equals(artifact.Kind, ExecutionArtifactKinds.Json, StringComparison.Ordinal) ? ".json" : ".txt");
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(material), writable: false);
        var stored = await _artifactObjectStore.PutObjectAsync(new ObjectWriteRequest
        {
            Container = Options.ArtifactObjectContainer,
            Key = key,
            Content = stream,
            ContentType = artifact.MediaType ?? (string.Equals(artifact.Kind, ExecutionArtifactKinds.Json, StringComparison.Ordinal) ? "application/json" : "text/plain"),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["execution_run_id"] = artifact.RunId,
                ["execution_artifact_id"] = artifact.Id,
                ["execution_content_hash"] = artifact.ContentHash
            },
            IfNoneMatch = "*"
        }, ct);
        artifact.Uri = $"vyral-object://{stored.Container}/{stored.Key}";
        artifact.Text = null;
        artifact.Content = null;
        AddArtifactStorageMetadata(artifact.Metadata, "storage", "object-store");
        AddArtifactStorageMetadata(artifact.Metadata, "offloaded", "true");
        AddArtifactStorageMetadata(artifact.Metadata, "inline", "false");
        return artifact;
    }

    private void AddArtifactStorageMetadata(Dictionary<string, string> metadata, string key, string value)
    {
        if (metadata.ContainsKey(key) || metadata.Count < Options.Limits.MaxTagCount) metadata[key] = value;
    }

    private JsonObject? RedactTraceDetails(ExecutionRun run, JsonObject? details)
    {
        var clone = details is null ? null : Clone(details);
        if (clone is null || run.Scope is null || _productPolicies.Count == 0 || !_productPolicies.TryGetValue(run.Scope.ProductId, out var policy)) return clone;
        foreach (var key in policy.RedactedJsonPropertyNames)
        {
            if (clone.ContainsKey(key)) clone[key] = "[redacted]";
        }

        return clone;
    }

    private Dictionary<string, string> BuildTraceContext(ExecutionRun run, string? workerId = null)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["adapterId"] = Adapter.AdapterId,
            ["runtimeKind"] = Adapter.RuntimeKind,
            ["runId"] = run.Id,
            ["handlerId"] = run.HandlerId
        };
        AddContextValue(context, "pluginId", run.PluginId);
        AddContextValue(context, "correlationId", run.CorrelationId);
        AddContextValue(context, "productId", run.Scope?.ProductId);
        AddContextValue(context, "tenantId", run.Scope?.TenantId);
        AddContextValue(context, "serviceIdentity", run.Scope?.ServiceIdentity);
        AddContextValue(context, "workerId", workerId);
        return context;
    }

    private static void AddContextValue(Dictionary<string, string> context, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) context[key] = value;
    }

    private static string BuildIdempotencyScopeKey(ExecutionRun run) => string.Join("\n",
        run.Scope?.ProductId ?? string.Empty,
        run.Scope?.TenantId ?? string.Empty,
        run.HandlerId,
        run.IdempotencyKey ?? string.Empty);

    private async Task<ExecutionLease> RequireExternalLeaseAsync(string leaseKey, string leaseToken, string workerId, CancellationToken ct)
    {
        var lease = await _state.GetLeaseAsync(leaseKey, ct);
        if (lease is null || lease.ExpiresAtUtc <= DateTime.UtcNow || !string.Equals(lease.OwnerId, ExternalLeaseOwner(workerId, leaseToken), StringComparison.Ordinal) || lease.RunId is null)
        {
            throw new InvalidOperationException("External worker lease is no longer active.");
        }

        return lease;
    }

    private async Task ResumeDueWaitAsync(string runId, CancellationToken ct)
    {
        var wait = await _state.GetWaitAsync(runId, ct);
        if (wait?.FireAtUtc is null || wait.FireAtUtc > DateTime.UtcNow) return;
        var outcome = wait.Kind == ExecutionExternalWorkerWaitKinds.Timer
            ? new ExecutionWaitResult { Name = wait.Name, Outcome = ExecutionWaitOutcomes.Timer, Timer = wait.Timer }
            : new ExecutionWaitResult { Name = wait.Name, Outcome = ExecutionWaitOutcomes.TimedOut };
        await _state.TryResumeWaitAsync(runId, wait.Kind, wait.Name, outcome, ct: ct);
    }
}

public sealed class AwsDynamoExecutionRuntimeOptions
{
    public string AdapterId { get; init; } = "aws-dynamodb-sqs";
    public ExecutionRuntimeLimits Limits { get; init; } = AwsDynamoExecutionLimits.Default;
    public string? ArtifactObjectContainer { get; init; }
    public IReadOnlyList<ExecutionProductPolicy> ProductPolicies { get; init; } = Array.Empty<ExecutionProductPolicy>();
    public IReadOnlyList<AwsDynamoExecutionWorkerDispatcher> WorkerDispatchers { get; init; } = Array.Empty<AwsDynamoExecutionWorkerDispatcher>();
    public bool RequireExplicitWorkerRoutes { get; init; } = true;
    public int MaxActiveRuns { get; init; } = 1_000;
    public int MaxRetainedTerminalRuns { get; init; } = 500;
    public int DefaultListLimit { get; init; } = 100;
    public int MaxListLimit { get; init; } = 1_000;
    public int DefaultHistoryLimit { get; init; } = 100;
    public int MaxHistoryLimit { get; init; } = 1_000;
    public int MaintenanceScanLimit { get; init; } = 10_000;
}

/// <summary>
/// Conservative DynamoDB item limits. Runs retain payload, result, and status details in one
/// item, so their individual limits deliberately reserve headroom beneath DynamoDB's 400 KiB
/// ceiling. Checkpoints and artifact metadata are separate items and can use the larger document
/// ceiling. Artifact content above that ceiling requires an explicitly configured object store.
/// </summary>
public static class AwsDynamoExecutionLimits
{
    public const int MaxInlineRunValueBytes = 131_072;
    public const int MaxInlineRunStatusDetailsBytes = 32_768;
    public const int MaxInlineDocumentValueBytes = 262_144;

    public static ExecutionRuntimeLimits Default => new()
    {
        MaxPayloadBytes = MaxInlineRunValueBytes,
        MaxResultBytes = MaxInlineRunValueBytes,
        MaxStatusDetailsBytes = MaxInlineRunStatusDetailsBytes,
        MaxArtifactBytes = MaxInlineDocumentValueBytes,
        MaxArtifactInlineBytes = MaxInlineDocumentValueBytes,
        MaxCheckpointBytes = MaxInlineDocumentValueBytes,
        MaxTraceDetailsBytes = 65_536
    };

    public static ExecutionRuntimeLimits WithArtifactOffload => new()
    {
        MaxPayloadBytes = MaxInlineRunValueBytes,
        MaxResultBytes = MaxInlineRunValueBytes,
        MaxStatusDetailsBytes = MaxInlineRunStatusDetailsBytes,
        MaxArtifactBytes = ExecutionRuntimeLimits.Default.MaxArtifactBytes,
        MaxArtifactInlineBytes = MaxInlineDocumentValueBytes,
        MaxCheckpointBytes = MaxInlineDocumentValueBytes,
        MaxTraceDetailsBytes = 65_536
    };

    public static void Validate(ExecutionRuntimeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxPayloadBytes > MaxInlineRunValueBytes ||
            limits.MaxResultBytes > MaxInlineRunValueBytes ||
            limits.MaxStatusDetailsBytes > MaxInlineRunStatusDetailsBytes ||
            limits.MaxCheckpointBytes > MaxInlineDocumentValueBytes ||
            limits.MaxArtifactInlineBytes > MaxInlineDocumentValueBytes)
        {
            throw new InvalidOperationException($"AWS DynamoDB execution embedded values must not exceed {MaxInlineDocumentValueBytes} bytes.");
        }
        if (limits.MaxArtifactBytes < limits.MaxArtifactInlineBytes)
            throw new InvalidOperationException("AWS execution max artifact bytes cannot be lower than the inline artifact ceiling.");
    }
}

public sealed class AwsDynamoExecutionWorkerDispatcher
{
    public required string HandlerId { get; init; }
    public required IExecutionRunDispatcher Dispatcher { get; init; }
}

/// <remarks>
/// Use <see cref="ExecutionProductPolicy"/> for new hosts. This compatibility type remains so
/// existing AWS-host configuration code can migrate without a source break.
/// </remarks>
public sealed class AwsDynamoExecutionProductPolicy : ExecutionProductPolicy
{
}
