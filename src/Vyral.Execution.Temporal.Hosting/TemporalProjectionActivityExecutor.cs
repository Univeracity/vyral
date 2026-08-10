namespace Vyral.Execution.Temporal.Hosting;

public interface ITemporalExecutionAttemptHandler
{
    Task<TemporalExecutionAttemptOutcome> ExecuteAttemptAsync(
        TemporalExecutionAttemptRequest request,
        CancellationToken ct = default);
}

public sealed class TemporalProjectionActivityExecutor : ITemporalExecutionActivityExecutor
{
    private readonly ITemporalExecutionAttemptHandler _attemptHandler;
    private readonly ITemporalExecutionProjectionStore _projectionStore;

    public TemporalProjectionActivityExecutor(
        ITemporalExecutionAttemptHandler attemptHandler,
        ITemporalExecutionProjectionStore projectionStore)
    {
        _attemptHandler = attemptHandler ?? throw new ArgumentNullException(nameof(attemptHandler));
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
    }

    public Task<TemporalExecutionAttemptOutcome> ExecuteAttemptAsync(
        TemporalExecutionAttemptRequest request,
        CancellationToken ct = default) => _attemptHandler.ExecuteAttemptAsync(request, ct);

    public Task<TemporalExecutionWaitProjectionResult> ProjectWaitResolutionAsync(
        TemporalExecutionWaitResolution resolution,
        CancellationToken ct = default) => _projectionStore.ProjectWaitResolutionAsync(resolution, ct);

    public Task ProjectCancellationAsync(
        TemporalExecutionCancellation cancellation,
        CancellationToken ct = default) => _projectionStore.ProjectCancellationAsync(cancellation, ct);
}
