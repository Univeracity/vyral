using Temporalio.Client;
using Temporalio.Worker;
using Vyral.Abstractions.Interfaces;

namespace Vyral.Execution.Temporal.Hosting;

public sealed class TemporalExecutionWorker : IDisposable
{
    private readonly TemporalWorker _worker;

    public TemporalExecutionWorker(
        ITemporalClient client,
        string taskQueue,
        ITemporalExecutionActivityExecutor executor)
        : this(client, taskQueue, executor, TemporalWorkerCompatibility.Resolve())
    {
    }

    private TemporalExecutionWorker(
        ITemporalClient client,
        string taskQueue,
        ITemporalExecutionActivityExecutor executor,
        TemporalWorkerDeploymentDescriptor deployment)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(executor);
        if (string.IsNullOrWhiteSpace(taskQueue) || taskQueue.Length > 255 || taskQueue.Any(char.IsControl))
            throw new InvalidOperationException("Temporal worker task queue must be 1-255 non-control characters.");

        var activities = new TemporalExecutionActivities(executor);
        var workerOptions = new TemporalWorkerOptions(taskQueue)
        {
            DeploymentOptions = TemporalWorkerCompatibility.CreateWorkerDeploymentOptions(deployment)
        };
        _worker = new TemporalWorker(
            client,
            workerOptions
                .AddWorkflow<TemporalRunCoordinatorWorkflow>()
                .AddActivity(activities.ExecuteAttemptAsync)
                .AddActivity(activities.ProjectWaitResolutionAsync)
                .AddActivity(activities.ProjectCancellationAsync));
    }

    public TemporalExecutionWorker(
        ITemporalClient client,
        string taskQueue,
        ITemporalExecutionAttemptHandler attemptHandler,
        ITemporalExecutionProjectionStore projectionStore)
        : this(client, taskQueue, new TemporalProjectionActivityExecutor(attemptHandler, projectionStore))
    {
    }

    public TemporalExecutionWorker(
        ITemporalClient client,
        string taskQueue,
        ITemporalExecutionHandlerResolver handlerResolver,
        ITemporalExecutionRuntimeStore runtimeStore,
        TemporalExecutionOptions options,
        string workerId,
        IObjectStore? artifactObjectStore = null)
        : this(
            client,
            taskQueue,
            new TemporalProjectionActivityExecutor(
                new TemporalExecutionAttemptHandler(
                    handlerResolver,
                    runtimeStore,
                    options,
                    workerId,
                    artifactObjectStore),
                runtimeStore),
            TemporalWorkerCompatibility.Resolve(options))
    {
    }

    public Task ExecuteAsync(CancellationToken ct = default) => _worker.ExecuteAsync(ct);

    public void Dispose() => _worker.Dispose();
}
