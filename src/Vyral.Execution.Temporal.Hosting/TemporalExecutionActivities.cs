using Temporalio.Activities;

namespace Vyral.Execution.Temporal.Hosting;

internal sealed class TemporalExecutionActivities
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private readonly ITemporalExecutionActivityExecutor _executor;

    public TemporalExecutionActivities(ITemporalExecutionActivityExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    [Activity(TemporalExecutionProtocolNames.ExecuteAttemptActivity)]
    public Task<TemporalExecutionAttemptOutcome> ExecuteAttemptAsync(TemporalExecutionAttemptRequest request) =>
        ExecuteWithHeartbeatAsync(ct => _executor.ExecuteAttemptAsync(request, ct));

    [Activity(TemporalExecutionProtocolNames.ProjectWaitActivity)]
    public Task<TemporalExecutionWaitProjectionResult> ProjectWaitResolutionAsync(TemporalExecutionWaitResolution resolution) =>
        ExecuteWithHeartbeatAsync(ct => _executor.ProjectWaitResolutionAsync(resolution, ct));

    [Activity(TemporalExecutionProtocolNames.ProjectCancellationActivity)]
    public Task ProjectCancellationAsync(TemporalExecutionCancellation cancellation) =>
        ExecuteWithHeartbeatAsync(async ct =>
        {
            await _executor.ProjectCancellationAsync(cancellation, ct);
            return true;
        });

    private static async Task<T> ExecuteWithHeartbeatAsync<T>(Func<CancellationToken, Task<T>> operation)
    {
        var context = ActivityExecutionContext.Current;
        using var completed = new CancellationTokenSource();
        using var activity = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken,
            context.WorkerShutdownToken);
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(
            activity.Token,
            completed.Token);
        context.Heartbeat();
        var heartbeatTask = HeartbeatUntilCompletedAsync(context, heartbeat.Token);
        try
        {
            return await operation(activity.Token);
        }
        finally
        {
            completed.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (heartbeat.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task HeartbeatUntilCompletedAsync(
        ActivityExecutionContext context,
        CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(HeartbeatInterval, ct);
            context.Heartbeat();
        }
    }
}
