using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Vyral.Execution;

namespace Vyral.Execution.WorkerClient;

/// <summary>Lease and heartbeat policy for <see cref="ExecutionPluginWorker"/>.</summary>
public sealed class ExecutionPluginWorkerOptions
{
    /// <summary>Duration requested for the external execution lease.</summary>
    public double LeaseTtlSeconds { get; init; } = 60;

    /// <summary>
    /// Interval between lease heartbeats while a handler is executing. It must be shorter than
    /// <see cref="LeaseTtlSeconds"/>. Set it to <see cref="Timeout.InfiniteTimeSpan"/> only for a
    /// bounded invocation whose hosting platform guarantees completion before the lease expires.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Delay after a polling iteration finds no eligible work.</summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Executes ordinary <see cref="IExecutionPlugin"/> handlers through Vyral's external-worker
/// protocol. The plugin continues to depend only on Vyral.Execution; provider queues, HTTP, lease
/// tokens, replay suspension, and heartbeats stay in this host boundary.
/// </summary>
public sealed class ExecutionPluginWorker
{
    private const string HandlerFailureMessage = "External plugin handler failed.";
    private readonly IExecutionWorkerTransport _transport;
    private readonly ExecutionPluginWorkerOptions _options;
    private readonly IReadOnlyDictionary<string, RegisteredHandler> _handlers;

    public ExecutionPluginWorker(
        IExecutionWorkerTransport transport,
        IEnumerable<IExecutionPlugin> plugins,
        ExecutionPluginWorkerOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(plugins);
        _options = options ?? new ExecutionPluginWorkerOptions();
        ValidateOptions(_options);

        var handlers = new Dictionary<string, RegisteredHandler>(StringComparer.Ordinal);
        foreach (var plugin in plugins)
        {
            ArgumentNullException.ThrowIfNull(plugin);
            ExecutionContractValidator.ValidatePluginDescriptor(plugin.Descriptor);
            foreach (var handler in plugin.Handlers)
            {
                ArgumentNullException.ThrowIfNull(handler);
                ExecutionContractValidator.ValidateHandlerDescriptor(handler.Descriptor);
                if (!string.Equals(handler.Descriptor.PluginId, plugin.Descriptor.PluginId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Execution handler '{handler.Descriptor.HandlerId}' does not belong to plugin '{plugin.Descriptor.PluginId}'.");
                }

                if (!handlers.TryAdd(
                    handler.Descriptor.HandlerId,
                    new RegisteredHandler(plugin.Descriptor.PluginId, handler)))
                {
                    throw new InvalidOperationException(
                        $"Execution handler '{handler.Descriptor.HandlerId}' is registered by more than one worker plugin.");
                }
            }
        }

        if (handlers.Count == 0)
        {
            throw new InvalidOperationException("At least one execution plugin handler is required.");
        }

        _handlers = handlers;
    }

    public IReadOnlyList<ExecutionHandlerDescriptor> Handlers =>
        _handlers.Values
            .Select(item => item.Handler.Descriptor)
            .OrderBy(item => item.HandlerId, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Claims and executes at most one run. Returns <see langword="null"/> when no eligible work
    /// exists. A durable wait returns the newly waiting run without completing it; replay after the
    /// event or timer re-executes the handler and returns the stored wait outcome.
    /// </summary>
    public async Task<ExecutionRun?> RunOnceAsync(string? runId = null, CancellationToken ct = default)
    {
        var lease = await _transport.LeaseNextAsync(runId, _options.LeaseTtlSeconds, ct);
        if (lease is null)
        {
            return null;
        }

        return await ExecuteAsync(lease, ct);
    }

    /// <summary>
    /// Polls and executes eligible runs until cancellation. Queue-triggered and serverless hosts
    /// should call <see cref="RunOnceAsync"/> with the delivered run id instead.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await RunOnceAsync(ct: ct) is null && _options.IdleDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.IdleDelay, ct);
            }
        }
    }

    private async Task<ExecutionRun> ExecuteAsync(ExecutionExternalWorkerLease lease, CancellationToken callerToken)
    {
        if (!_handlers.TryGetValue(lease.Run.HandlerId, out var registration))
        {
            return await _transport.CompleteAsync(
                lease,
                ExecutionRunResult.Failed(
                    ExecutionFailureClasses.HandlerMissing,
                    $"Execution handler '{lease.Run.HandlerId}' is not registered in this worker."),
                callerToken);
        }

        if (!string.IsNullOrWhiteSpace(lease.Run.PluginId) &&
            !string.Equals(lease.Run.PluginId, registration.PluginId, StringComparison.Ordinal))
        {
            return await _transport.CompleteAsync(
                lease,
                ExecutionRunResult.Failed(
                    ExecutionFailureClasses.PluginMismatch,
                    $"Execution handler '{lease.Run.HandlerId}' belongs to plugin '{registration.PluginId}'."),
                callerToken);
        }

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        var context = new ExternalExecutionRunContext(_transport, lease, execution.Token);
        var heartbeatFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = RunHeartbeatLoopAsync(context, execution, heartbeatFailure);

        try
        {
            var handlerTask = ExecuteHandlerAsync(registration.Handler, context, execution.Token);
            var winner = await Task.WhenAny(handlerTask, heartbeatFailure.Task);
            if (winner == heartbeatFailure.Task)
            {
                execution.Cancel();
                try
                {
                    await handlerTask;
                }
                catch (OperationCanceledException) when (execution.IsCancellationRequested)
                {
                }
                catch
                {
                    // The heartbeat failure is authoritative because the worker no longer knows
                    // whether it owns the lease.
                }

                ExceptionDispatchInfo.Capture(await heartbeatFailure.Task).Throw();
            }

            ExecutionRunResult result;
            try
            {
                result = await handlerTask;
                ExecutionContractValidator.ValidateRunResult(result);
            }
            catch (ExecutionWorkerSuspendedException suspended)
            {
                return suspended.Run;
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result = ExecutionRunResult.Cancelled(context.Run.Result);
            }
            catch
            {
                result = ExecutionRunResult.Failed(ExecutionFailureClasses.Unknown, HandlerFailureMessage);
            }

            if (heartbeatFailure.Task.IsCompleted)
            {
                ExceptionDispatchInfo.Capture(await heartbeatFailure.Task).Throw();
            }

            // Transport/lease failures remain infrastructure failures. Never reinterpret a failed
            // completion call as a plugin failure or retry it with a different terminal result.
            return await _transport.CompleteAsync(context.Lease, result, callerToken);
        }
        finally
        {
            execution.Cancel();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException) when (execution.IsCancellationRequested)
            {
            }
        }
    }

    private static Task<ExecutionRunResult> ExecuteHandlerAsync(
        IExecutionHandler handler,
        IExecutionRunContext context,
        CancellationToken ct)
    {
        try
        {
            return handler.ExecuteAsync(context, ct);
        }
        catch (Exception ex)
        {
            return Task.FromException<ExecutionRunResult>(ex);
        }
    }

    private async Task RunHeartbeatLoopAsync(
        ExternalExecutionRunContext context,
        CancellationTokenSource execution,
        TaskCompletionSource<Exception> failure)
    {
        if (_options.HeartbeatInterval == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        try
        {
            while (true)
            {
                await Task.Delay(_options.HeartbeatInterval, execution.Token);
                var refreshed = await _transport.HeartbeatAsync(
                    context.Lease,
                    _options.LeaseTtlSeconds,
                    execution.Token);
                context.UpdateLease(refreshed);
                if (refreshed.Run.CancellationRequested)
                {
                    execution.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure.TrySetResult(ex);
        }
    }

    private static void ValidateOptions(ExecutionPluginWorkerOptions options)
    {
        if (!double.IsFinite(options.LeaseTtlSeconds) || options.LeaseTtlSeconds <= 0 ||
            options.LeaseTtlSeconds > ExecutionRuntimeLimits.Default.MaxLeaseTtlSeconds)
        {
            throw new InvalidOperationException(
                $"Worker lease TTL must be greater than zero and no more than {ExecutionRuntimeLimits.Default.MaxLeaseTtlSeconds} seconds.");
        }

        if (options.HeartbeatInterval != Timeout.InfiniteTimeSpan &&
            (options.HeartbeatInterval <= TimeSpan.Zero ||
             options.HeartbeatInterval >= TimeSpan.FromSeconds(options.LeaseTtlSeconds)))
        {
            throw new InvalidOperationException("Worker heartbeat interval must be positive and shorter than the lease TTL.");
        }

        if (options.IdleDelay < TimeSpan.Zero || options.IdleDelay > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("Worker idle delay must be between zero and one minute.");
        }
    }

    private sealed record RegisteredHandler(string PluginId, IExecutionHandler Handler);

    private sealed class ExecutionWorkerSuspendedException(ExecutionRun run) : Exception
    {
        public ExecutionRun Run { get; } = run;
    }

    private sealed class ExternalExecutionRunContext : IExecutionRunContext
    {
        private readonly IExecutionWorkerTransport _transport;
        private readonly object _sync = new();
        private ExecutionExternalWorkerLease _lease;

        public ExternalExecutionRunContext(
            IExecutionWorkerTransport transport,
            ExecutionExternalWorkerLease lease,
            CancellationToken cancellationToken)
        {
            _transport = transport;
            _lease = lease;
            CancellationToken = cancellationToken;
        }

        public ExecutionExternalWorkerLease Lease
        {
            get
            {
                lock (_sync)
                {
                    return _lease;
                }
            }
        }

        public ExecutionRun Run => Lease.Run;
        public CancellationToken CancellationToken { get; }

        public void UpdateLease(ExecutionExternalWorkerLease lease)
        {
            lock (_sync)
            {
                _lease = lease;
            }
        }

        public async Task<ExecutionRun> ReportAsync(ExecutionRunUpdate update, CancellationToken ct = default)
        {
            var run = await _transport.ReportAsync(Lease, update, Link(ct));
            UpdateRun(run);
            return run;
        }

        public Task RecordEventAsync(
            string type,
            string? message = null,
            string severity = "info",
            JsonObject? details = null,
            CancellationToken ct = default) =>
            _transport.RecordEventAsync(Lease, new ExecutionExternalWorkerEventRequest
            {
                Type = type,
                Message = message,
                Severity = severity,
                Details = details
            }, Link(ct));

        public Task<ExecutionArtifact> PutArtifactAsync(ExecutionArtifactWrite artifact, CancellationToken ct = default) =>
            _transport.PutArtifactAsync(Lease, artifact, Link(ct));

        public Task<ExecutionCheckpoint> PutCheckpointAsync(ExecutionCheckpointWrite checkpoint, CancellationToken ct = default) =>
            _transport.CheckpointAsync(Lease, checkpoint, Link(ct));

        public Task<ExecutionCheckpoint?> GetCheckpointAsync(string key, CancellationToken ct = default) =>
            _transport.GetCheckpointAsync(Lease, key, Link(ct));

        public Task<ExecutionLease?> TryAcquireLeaseAsync(
            string leaseKey,
            double ttlSeconds = 60,
            JsonObject? metadata = null,
            CancellationToken ct = default) =>
            throw Unsupported("coordination leases");

        public Task<bool> ReleaseLeaseAsync(string leaseKey, CancellationToken ct = default) =>
            throw Unsupported("coordination leases");

        public Task<ExecutionTimer> ScheduleTimerAsync(
            string name,
            DateTime fireAtUtc,
            JsonNode? payload = null,
            CancellationToken ct = default) =>
            throw Unsupported("standalone timers");

        public Task<ExecutionExternalEvent> RaiseEventAsync(
            string name,
            JsonNode? payload = null,
            CancellationToken ct = default) =>
            throw Unsupported("raising external events from a worker");

        public Task<ExecutionWaitResult> WaitForExternalEventAsync(
            string name,
            DateTime? timeoutAtUtc = null,
            CancellationToken ct = default) =>
            WaitAsync(new ExecutionExternalWorkerWaitRequest
            {
                Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
                Name = name,
                TimeoutAtUtc = timeoutAtUtc
            }, ct);

        public Task<ExecutionWaitResult> WaitForTimerAsync(
            string name,
            DateTime fireAtUtc,
            JsonNode? payload = null,
            CancellationToken ct = default) =>
            WaitAsync(new ExecutionExternalWorkerWaitRequest
            {
                Kind = ExecutionExternalWorkerWaitKinds.Timer,
                Name = name,
                FireAtUtc = fireAtUtc,
                Payload = payload
            }, ct);

        private async Task<ExecutionWaitResult> WaitAsync(
            ExecutionExternalWorkerWaitRequest request,
            CancellationToken ct)
        {
            var response = await _transport.WaitAsync(Lease, request, Link(ct));
            UpdateRun(response.Run);
            if (response.Suspended)
            {
                throw new ExecutionWorkerSuspendedException(response.Run);
            }

            return response.Outcome
                ?? throw new InvalidOperationException("External worker wait completed without an outcome.");
        }

        private CancellationToken Link(CancellationToken ct)
        {
            if (!ct.CanBeCanceled)
            {
                return CancellationToken;
            }

            return ct;
        }

        private void UpdateRun(ExecutionRun run)
        {
            lock (_sync)
            {
                _lease.Run = run;
            }
        }

        private static NotSupportedException Unsupported(string capability) =>
            new($"The external-worker protocol does not expose {capability} to plugin handlers.");
    }
}
