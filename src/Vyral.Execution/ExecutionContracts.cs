using System.Text.Json.Nodes;

namespace Vyral.Execution;

public interface IExecutionPlugin
{
    ExecutionPluginDescriptor Descriptor { get; }
    IReadOnlyList<IExecutionHandler> Handlers { get; }
}

public interface IExecutionHandler
{
    ExecutionHandlerDescriptor Descriptor { get; }
    Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default);
}

public interface IExecutionRunContext
{
    ExecutionRun Run { get; }
    CancellationToken CancellationToken { get; }

    Task<ExecutionRun> ReportAsync(ExecutionRunUpdate update, CancellationToken ct = default);
    Task RecordEventAsync(string type, string? message = null, string severity = "info", JsonObject? details = null, CancellationToken ct = default);
    Task<ExecutionArtifact> PutArtifactAsync(ExecutionArtifactWrite artifact, CancellationToken ct = default);
    Task<ExecutionCheckpoint> PutCheckpointAsync(ExecutionCheckpointWrite checkpoint, CancellationToken ct = default);
    Task<ExecutionCheckpoint?> GetCheckpointAsync(string key, CancellationToken ct = default);
    Task<ExecutionLease?> TryAcquireLeaseAsync(string leaseKey, double ttlSeconds = 60, JsonObject? metadata = null, CancellationToken ct = default);
    Task<bool> ReleaseLeaseAsync(string leaseKey, CancellationToken ct = default);
    Task<ExecutionTimer> ScheduleTimerAsync(string name, DateTime fireAtUtc, JsonNode? payload = null, CancellationToken ct = default);
    Task<ExecutionExternalEvent> RaiseEventAsync(string name, JsonNode? payload = null, CancellationToken ct = default);
    Task<ExecutionWaitResult> WaitForExternalEventAsync(string name, DateTime? timeoutAtUtc = null, CancellationToken ct = default);
    Task<ExecutionWaitResult> WaitForTimerAsync(string name, DateTime fireAtUtc, JsonNode? payload = null, CancellationToken ct = default);
}

public interface IExecutionRuntime
{
    void RegisterHandler(IExecutionHandler handler);
    void RegisterPlugin(IExecutionPlugin plugin);
    IReadOnlyList<ExecutionPluginDescriptor> ListPlugins();
    IReadOnlyList<ExecutionHandlerDescriptor> ListHandlers();

    Task<ExecutionRun> StartRunAsync(ExecutionRunRequest request, CancellationToken ct = default);
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
}

public interface IExecutionRuntimeAdapter : IExecutionRuntime
{
    ExecutionRuntimeAdapterDescriptor Adapter { get; }
    Task<ExecutionRuntimeAdapterStatus> GetAdapterStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// Host-composition seam for provider adapters. A factory reads only a flattened, provider-owned
/// settings map, so the core contract does not depend on ASP.NET, Google Cloud, Azure, AWS, or a
/// particular dependency-injection package.
/// </summary>
public interface IExecutionRuntimeAdapterFactory
{
    IExecutionRuntimeAdapter Create(ExecutionRuntimeAdapterFactoryContext context);
}

/// <summary>
/// Schedules durable work after its state transition has been persisted. Implementations are
/// provider-specific (for example, a queue, durable orchestration engine, or local dispatcher),
/// while the request deliberately contains only portable execution state.
/// </summary>
public interface IExecutionRunDispatcher
{
    Task DispatchAsync(ExecutionDispatchRequest request, CancellationToken ct = default);
}

public interface IExecutionRuntimeMaintenance
{
    Task<ExecutionMaintenanceStatus> GetMaintenanceStatusAsync(CancellationToken ct = default);
    Task<ExecutionMaintenancePruneResult> PruneAsync(ExecutionMaintenancePruneRequest request, CancellationToken ct = default);
    Task<ExecutionMaintenanceDispatchReconcileResult> ReconcileDispatchAsync(ExecutionMaintenanceDispatchReconcileRequest request, CancellationToken ct = default);
}

/// <summary>
/// Durable protocol for workers that execute registered handlers out of process.
/// The worker does not load an <see cref="IExecutionPlugin"/>; it holds an opaque lease and
/// uses this protocol to report durable progress.
/// </summary>
public interface IExternalExecutionWorkerRuntime
{
    void RegisterExternalHandler(ExecutionHandlerDescriptor handler);

    Task<ExecutionExternalWorkerLease?> LeaseNextRunAsync(
        ExecutionExternalWorkerLeaseRequest request,
        CancellationToken ct = default);

    Task<ExecutionExternalWorkerLease> HeartbeatExternalLeaseAsync(
        ExecutionExternalWorkerHeartbeatRequest request,
        CancellationToken ct = default);

    Task<ExecutionRun> ReportExternalLeaseAsync(
        ExecutionExternalWorkerReportRequest request,
        CancellationToken ct = default);

    Task RecordExternalLeaseEventAsync(
        ExecutionExternalWorkerEventRequest request,
        CancellationToken ct = default);

    Task<ExecutionArtifact> PutExternalLeaseArtifactAsync(
        ExecutionExternalWorkerArtifactRequest request,
        CancellationToken ct = default);

    Task<ExecutionCheckpoint> CheckpointExternalLeaseAsync(
        ExecutionExternalWorkerCheckpointRequest request,
        CancellationToken ct = default);

    Task<ExecutionCheckpoint?> GetExternalLeaseCheckpointAsync(
        ExecutionExternalWorkerCheckpointReadRequest request,
        CancellationToken ct = default);

    Task<ExecutionExternalWorkerWaitResponse> WaitExternalLeaseAsync(
        ExecutionExternalWorkerWaitRequest request,
        CancellationToken ct = default);

    Task<ExecutionRun> CompleteExternalLeaseAsync(
        ExecutionExternalWorkerCompletionRequest request,
        CancellationToken ct = default);
}
