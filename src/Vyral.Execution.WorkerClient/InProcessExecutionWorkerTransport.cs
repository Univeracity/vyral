using Vyral.Execution;

namespace Vyral.Execution.WorkerClient;

/// <summary>
/// Connects <see cref="ExecutionPluginWorker"/> directly to an
/// <see cref="IExternalExecutionWorkerRuntime"/> in the same process. This is useful for embedded
/// worker deployments and deterministic qualification; remote workers should use
/// <see cref="ExecutionWorkerClient"/> instead.
/// </summary>
public sealed class InProcessExecutionWorkerTransport : IExecutionWorkerTransport
{
    private readonly IExternalExecutionWorkerRuntime _runtime;
    private readonly string _workerId;
    private readonly IReadOnlyList<string> _handlerIds;

    public InProcessExecutionWorkerTransport(
        IExternalExecutionWorkerRuntime runtime,
        string workerId,
        IEnumerable<string> handlerIds)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _workerId = Require(workerId, "Execution worker id");
        ArgumentNullException.ThrowIfNull(handlerIds);
        _handlerIds = handlerIds
            .Select(handlerId => Require(handlerId, "Execution worker handler id"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(handlerId => handlerId, StringComparer.Ordinal)
            .ToList();
        if (_handlerIds.Count == 0)
        {
            throw new InvalidOperationException("At least one execution worker handler id is required.");
        }
    }

    public Task<ExecutionExternalWorkerLease?> LeaseNextAsync(
        string? runId = null,
        double ttlSeconds = 60,
        CancellationToken ct = default) =>
        _runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = _workerId,
            HandlerIds = _handlerIds.ToList(),
            RunId = string.IsNullOrWhiteSpace(runId) ? null : runId.Trim(),
            TtlSeconds = ttlSeconds
        }, ct);

    public Task<ExecutionExternalWorkerLease> HeartbeatAsync(
        ExecutionExternalWorkerLease lease,
        double ttlSeconds = 60,
        CancellationToken ct = default) =>
        _runtime.HeartbeatExternalLeaseAsync(new ExecutionExternalWorkerHeartbeatRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = WorkerId(lease),
            TtlSeconds = ttlSeconds
        }, ct);

    public Task<ExecutionCheckpoint> CheckpointAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionCheckpointWrite checkpoint,
        CancellationToken ct = default) =>
        _runtime.CheckpointExternalLeaseAsync(new ExecutionExternalWorkerCheckpointRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = WorkerId(lease),
            Checkpoint = checkpoint
        }, ct);

    public Task<ExecutionCheckpoint?> GetCheckpointAsync(
        ExecutionExternalWorkerLease lease,
        string key,
        CancellationToken ct = default) =>
        _runtime.GetExternalLeaseCheckpointAsync(new ExecutionExternalWorkerCheckpointReadRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = WorkerId(lease),
            Key = key
        }, ct);

    public Task<ExecutionRun> ReportAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionRunUpdate update,
        CancellationToken ct = default) =>
        _runtime.ReportExternalLeaseAsync(new ExecutionExternalWorkerReportRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = WorkerId(lease),
            Update = update
        }, ct);

    public Task RecordEventAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionExternalWorkerEventRequest request,
        CancellationToken ct = default) =>
        _runtime.RecordExternalLeaseEventAsync(new ExecutionExternalWorkerEventRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = WorkerId(lease),
            Type = request.Type,
            Message = request.Message,
            Severity = request.Severity,
            Details = request.Details
        }, ct);

    public Task<ExecutionArtifact> PutArtifactAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionArtifactWrite artifact,
        CancellationToken ct = default) =>
        _runtime.PutExternalLeaseArtifactAsync(new ExecutionExternalWorkerArtifactRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = WorkerId(lease),
            Artifact = artifact
        }, ct);

    public Task<ExecutionExternalWorkerWaitResponse> WaitAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionExternalWorkerWaitRequest request,
        CancellationToken ct = default) =>
        _runtime.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = WorkerId(lease),
            Kind = request.Kind,
            Name = request.Name,
            TimeoutAtUtc = request.TimeoutAtUtc,
            FireAtUtc = request.FireAtUtc,
            Payload = request.Payload
        }, ct);

    public Task<ExecutionRun> CompleteAsync(
        ExecutionExternalWorkerLease lease,
        ExecutionRunResult result,
        CancellationToken ct = default) =>
        _runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = WorkerId(lease),
            Result = result
        }, ct);

    private string WorkerId(ExecutionExternalWorkerLease lease) =>
        string.IsNullOrWhiteSpace(lease.WorkerId) ? _workerId : lease.WorkerId;

    private static string Require(string value, string description) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{description} is required.")
            : value.Trim();
}
