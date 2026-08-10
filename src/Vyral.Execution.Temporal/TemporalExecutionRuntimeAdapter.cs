using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Execution.Temporal;

/// <summary>
/// Portable API-side facade over a Temporal coordinator and a durable Vyral projection store.
/// Handler code remains worker-host composition and never executes inside a workflow method.
/// </summary>
public sealed class TemporalExecutionRuntimeAdapter : IExecutionRuntimeAdapter, ITemporalExecutionHandlerResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITemporalExecutionRuntimeStore _store;
    private readonly ITemporalCoordinatorClient _coordinator;
    private readonly TemporalExecutionRegistry _registry;

    public TemporalExecutionRuntimeAdapter(
        ITemporalExecutionRuntimeStore store,
        ITemporalCoordinatorClient coordinator,
        TemporalExecutionOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        _registry = new TemporalExecutionRegistry(Options.Limits);
        Adapter = TemporalExecutionDialect.BuildAdapterDescriptor(Options);
    }

    public TemporalExecutionOptions Options { get; }
    public ExecutionRuntimeAdapterDescriptor Adapter { get; }

    public void RegisterHandler(IExecutionHandler handler) => _registry.RegisterHandler(handler);

    public void RegisterPlugin(IExecutionPlugin plugin) => _registry.RegisterPlugin(plugin);

    public IReadOnlyList<ExecutionPluginDescriptor> ListPlugins() => _registry.ListPlugins();

    public IReadOnlyList<ExecutionHandlerDescriptor> ListHandlers() => _registry.ListHandlers();

    public async Task<ExecutionRun> StartRunAsync(ExecutionRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateRunRequest(request, Options.Limits);
        var handler = _registry.FindHandler(request.HandlerId);
        var run = TemporalExecutionDialect.CreateRun(request, handler?.Descriptor, Options);
        var requestHash = TemporalExecutionDialect.CreateRequestHash(run);
        var workflowId = TemporalExecutionIdentity.CreateWorkflowId(Options.AdapterNamespace, run.Id);

        if (ExecutionRunStatuses.IsTerminal(run.Status))
        {
            var rejected = await _store.CreateRunWithoutPendingStartAsync(new TemporalProjectionRunCreation
            {
                Run = run,
                WorkflowId = workflowId,
                Generation = 1,
                ProjectionRevision = 1,
                RequestHash = requestHash
            }, ct);
            rejected.Run.AdmissionReplayed = rejected.Replayed;
            return rejected.Run;
        }

        var creation = await _store.CreateRunWithPendingStartAsync(new TemporalProjectionRunStart
        {
            Run = run,
            WorkflowId = workflowId,
            Generation = 1,
            ProjectionRevision = 1,
            DispatchId = OrderedId.CreateString(),
            RequestHash = requestHash
        }, ct);
        creation.Run.AdmissionReplayed = creation.Replayed;
        var reconciler = new TemporalExecutionDispatchReconciler(_store, _coordinator, Options.AdapterNamespace);
        _ = await reconciler.ReconcileAsync(Options.ReconciliationBatchSize, ct);
        return creation.Run;
    }

    public Task<ExecutionRun?> GetRunAsync(
        string runId,
        bool includeResult = true,
        CancellationToken ct = default) => _store.GetRunAsync(runId, includeResult, ct);

    public Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(
        ExecutionRunQuery? query = null,
        CancellationToken ct = default)
    {
        var normalized = Clone(query) ?? new ExecutionRunQuery();
        normalized.Limit ??= Options.DefaultListLimit;
        if (normalized.Limit < 1 || normalized.Limit > Options.MaxListLimit)
        {
            throw new InvalidOperationException(
                $"Execution run list limit must be between 1 and {Options.MaxListLimit}.");
        }
        return _store.ListRunsAsync(normalized, ct);
    }

    public async Task<ExecutionRun?> CancelRunAsync(string runId, CancellationToken ct = default)
    {
        var cancellation = await _store.RequestCancellationAsync(runId, ct);
        if (cancellation.Run is null || ExecutionRunStatuses.IsTerminal(cancellation.Run.Status))
            return cancellation.Run;
        if (string.IsNullOrWhiteSpace(cancellation.WorkflowId) || cancellation.Generation is null)
            throw new InvalidOperationException("Temporal projection cancellation has no coordinator identity.");

        var reconciler = new TemporalExecutionCancellationReconciler(
            _store,
            _coordinator,
            Options.AdapterNamespace);
        _ = await reconciler.ReconcileAsync(Options.ReconciliationBatchSize, ct);
        return cancellation.Run;
    }

    public Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(
        string runId,
        ExecutionHistoryQuery? query = null,
        CancellationToken ct = default)
    {
        var limit = query?.Limit ?? Options.DefaultHistoryLimit;
        if (limit < 1 || limit > Options.MaxHistoryLimit)
        {
            throw new InvalidOperationException(
                $"Execution history limit must be between 1 and {Options.MaxHistoryLimit}.");
        }
        return _store.GetHistoryAsync(runId, limit, ct);
    }

    public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(
        string runId,
        CancellationToken ct = default) => _store.ListArtifactsAsync(runId, ct);

    public Task<ExecutionArtifact?> GetArtifactAsync(
        string runId,
        string artifactRef,
        CancellationToken ct = default) => _store.GetArtifactAsync(runId, artifactRef, ct);

    public Task<ExecutionCheckpoint?> GetCheckpointAsync(
        string runId,
        string key,
        CancellationToken ct = default) => _store.GetCheckpointAsync(runId, key, ct);

    public Task<ExecutionLease?> TryAcquireLeaseAsync(
        ExecutionLeaseRequest request,
        CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateLeaseRequest(request, Options.Limits);
        return _store.TryAcquireLeaseAsync(request, ct);
    }

    public Task<bool> ReleaseLeaseAsync(
        string leaseKey,
        string ownerId,
        CancellationToken ct = default) => _store.ReleaseLeaseAsync(leaseKey, ownerId, ct);

    public Task<ExecutionTimer> ScheduleTimerAsync(
        ExecutionTimerRequest request,
        CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateTimerRequest(request, Options.Limits);
        return _store.ScheduleTimerAsync(request, ct);
    }

    public async Task<ExecutionExternalEvent> RaiseEventAsync(
        ExecutionExternalEventRequest request,
        CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateExternalEventRequest(request, Options.Limits);
        if (string.IsNullOrWhiteSpace(request.RunId))
            throw new InvalidOperationException("Temporal external events require a run id.");
        var externalEvent = new ExecutionExternalEvent
        {
            Id = OrderedId.CreateString(),
            Name = request.Name.Trim(),
            RunId = request.RunId.Trim(),
            RaisedAtUtc = DateTime.UtcNow,
            Payload = request.Payload?.DeepClone()
        };
        var dispatch = await _store.CreateExternalEventWithPendingSignalAsync(
            externalEvent,
            OrderedId.CreateString(),
            ct);
        var reconciler = new TemporalExecutionSignalReconciler(_store, _coordinator, Options.AdapterNamespace);
        _ = await reconciler.ReconcileAsync(Options.ReconciliationBatchSize, ct);
        return dispatch.Event;
    }

    public async Task<ExecutionRuntimeAdapterStatus> GetAdapterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var projection = await _store.GetRuntimeStatusAsync(ct);
            return new ExecutionRuntimeAdapterStatus
            {
                Adapter = Adapter,
                Available = true,
                Status = "ok",
                CheckedAtUtc = DateTime.UtcNow,
                ActiveRuns = projection.ActiveRuns,
                OperationalPolicy = TemporalExecutionDialect.BuildOperationalPolicy(Options),
                ResumePolicy = TemporalExecutionDialect.BuildResumePolicy(),
                Details = new JsonObject
                {
                    ["projectionSchemaVersion"] = projection.SchemaVersion,
                    ["pendingStartDispatches"] = projection.PendingStartDispatches,
                    ["pendingSignalDispatches"] = projection.PendingSignalDispatches,
                    ["pendingCancellationDispatches"] = projection.PendingCancellationDispatches,
                    ["oldestPendingDispatchAtUtc"] = projection.OldestPendingDispatchAtUtc,
                    ["activeCoordinators"] = projection.ActiveCoordinators,
                    ["qualification"] = "prototype_unqualified"
                }
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ExecutionRuntimeAdapterStatus
            {
                Adapter = Adapter,
                Available = false,
                Status = "unavailable",
                CheckedAtUtc = DateTime.UtcNow,
                OperationalPolicy = TemporalExecutionDialect.BuildOperationalPolicy(Options),
                ResumePolicy = TemporalExecutionDialect.BuildResumePolicy(),
                Details = new JsonObject { ["qualification"] = "prototype_unqualified" }
            };
        }
    }

    public IExecutionHandler? ResolveHandler(string handlerId) => _registry.FindHandler(handlerId);

    private static T? Clone<T>(T? value) => value is null
        ? default
        : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions);
}
