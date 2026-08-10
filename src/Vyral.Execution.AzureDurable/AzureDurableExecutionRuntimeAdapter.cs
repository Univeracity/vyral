using Vyral.Execution;

namespace Vyral.Execution.AzureDurable;

public interface IAzureDurableExecutionClient
{
    Task<ExecutionRun> StartRunAsync(AzureDurableStartCommand command, CancellationToken ct = default);
    Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default);
    Task<ExecutionRun?> CancelRunAsync(string runId, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default);
    Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default);
    Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default);
    Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default);
    Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default);
    Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimerRequest request, CancellationToken ct = default);
    Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEventRequest request, CancellationToken ct = default);
    Task<ExecutionRuntimeAdapterStatus> GetAdapterStatusAsync(ExecutionRuntimeAdapterDescriptor adapter, CancellationToken ct = default);
}

public sealed class AzureDurableExecutionRuntimeAdapter : IExecutionRuntimeAdapter
{
    private readonly IAzureDurableExecutionClient _client;
    private readonly AzureDurableExecutionRegistry _registry;

    public AzureDurableExecutionRuntimeAdapter(
        IAzureDurableExecutionClient client,
        AzureDurableExecutionOptions? options = null,
        AzureDurableExecutionRegistry? registry = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Options = options ?? new AzureDurableExecutionOptions();
        _registry = registry ?? new AzureDurableExecutionRegistry(Options.Limits);
        Adapter = AzureDurableExecutionDialect.BuildAdapterDescriptor(Options);
    }

    public AzureDurableExecutionOptions Options { get; }
    public ExecutionRuntimeAdapterDescriptor Adapter { get; }

    public void RegisterHandler(IExecutionHandler handler)
    {
        _registry.RegisterHandler(handler);
    }

    public void RegisterPlugin(IExecutionPlugin plugin)
    {
        _registry.RegisterPlugin(plugin);
    }

    public IReadOnlyList<ExecutionPluginDescriptor> ListPlugins()
    {
        return _registry.ListPlugins();
    }

    public IReadOnlyList<ExecutionHandlerDescriptor> ListHandlers()
    {
        return _registry.ListHandlers();
    }

    public Task<ExecutionRun> StartRunAsync(ExecutionRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateRunRequest(request, Options.Limits);
        var handlers = ListHandlers();
        var handler = handlers.FirstOrDefault(candidate =>
            string.Equals(candidate.HandlerId, request.HandlerId, StringComparison.Ordinal));
        if (handler is null)
        {
            return RecordRejectedRunAsync(AzureDurableExecutionDialect.CreateRejectedRun(
                request,
                ExecutionFailureClasses.HandlerMissing,
                $"Execution handler '{request.HandlerId}' is not registered.",
                Options),
                ct);
        }

        var requestedPluginId = NormalizeOptional(request.PluginId);
        var handlerPluginId = NormalizeOptional(handler.PluginId);
        if (requestedPluginId is not null &&
            handlerPluginId is not null &&
            !string.Equals(requestedPluginId, handlerPluginId, StringComparison.Ordinal))
        {
            return RecordRejectedRunAsync(AzureDurableExecutionDialect.CreateRejectedRun(
                request,
                ExecutionFailureClasses.PluginMismatch,
                $"Execution handler '{request.HandlerId}' belongs to plugin '{handlerPluginId}', not '{requestedPluginId}'.",
                Options),
                ct);
        }

        var command = AzureDurableExecutionDialect.BuildStartCommand(request, handlers, Options);
        return _client.StartRunAsync(command, ct);
    }

    public Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default)
    {
        return _client.GetRunAsync(runId, includeResult, ct);
    }

    public Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default)
    {
        return _client.ListRunsAsync(query, ct);
    }

    public Task<ExecutionRun?> CancelRunAsync(string runId, CancellationToken ct = default)
    {
        return _client.CancelRunAsync(runId, ct);
    }

    public Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default)
    {
        return _client.GetHistoryAsync(runId, query, ct);
    }

    public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default)
    {
        return _client.ListArtifactsAsync(runId, ct);
    }

    public Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default)
    {
        return _client.GetArtifactAsync(runId, artifactRef, ct);
    }

    public Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default)
    {
        return _client.GetCheckpointAsync(runId, key, ct);
    }

    public Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateLeaseRequest(request, Options.Limits);
        return _client.TryAcquireLeaseAsync(request, ct);
    }

    public Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default)
    {
        return _client.ReleaseLeaseAsync(leaseKey, ownerId, ct);
    }

    public Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimerRequest request, CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateTimerRequest(request, Options.Limits);
        return _client.ScheduleTimerAsync(request, ct);
    }

    public Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEventRequest request, CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateExternalEventRequest(request, Options.Limits);
        return _client.RaiseEventAsync(request, ct);
    }

    public Task<ExecutionRuntimeAdapterStatus> GetAdapterStatusAsync(CancellationToken ct = default)
    {
        return _client.GetAdapterStatusAsync(Adapter, ct);
    }

    private Task<ExecutionRun> RecordRejectedRunAsync(ExecutionRun run, CancellationToken ct)
    {
        return _client is IAzureDurableExecutionRejectedRunSink sink
            ? sink.RecordRejectedRunAsync(run, ct)
            : Task.FromResult(run);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
