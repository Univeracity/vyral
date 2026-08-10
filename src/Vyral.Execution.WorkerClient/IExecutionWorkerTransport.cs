using Vyral.Execution;

namespace Vyral.Execution.WorkerClient;

/// <summary>
/// Provider-neutral transport used by <see cref="ExecutionPluginWorker"/>. The supported HTTP
/// client implements this interface; tests and embedded worker hosts may supply an in-process
/// implementation without coupling portable plugin code to an HTTP stack.
/// </summary>
public interface IExecutionWorkerTransport
{
    Task<ExecutionExternalWorkerLease?> LeaseNextAsync(
        string? runId = null,
        double ttlSeconds = 60,
        CancellationToken ct = default);

    Task<ExecutionExternalWorkerLease> HeartbeatAsync(
        ExecutionExternalWorkerLease lease,
        double ttlSeconds = 60,
        CancellationToken ct = default);

    Task<ExecutionCheckpoint> CheckpointAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionCheckpointWrite checkpoint,
        CancellationToken ct = default);

    Task<ExecutionCheckpoint?> GetCheckpointAsync(
        ExecutionExternalWorkerLease lease,
        string key,
        CancellationToken ct = default);

    Task<ExecutionRun> ReportAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionRunUpdate update,
        CancellationToken ct = default);

    Task RecordEventAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionExternalWorkerEventRequest request,
        CancellationToken ct = default);

    Task<ExecutionArtifact> PutArtifactAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionArtifactWrite artifact,
        CancellationToken ct = default);

    Task<ExecutionExternalWorkerWaitResponse> WaitAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionExternalWorkerWaitRequest request,
        CancellationToken ct = default);

    Task<ExecutionRun> CompleteAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionRunResult result,
        CancellationToken ct = default);
}
