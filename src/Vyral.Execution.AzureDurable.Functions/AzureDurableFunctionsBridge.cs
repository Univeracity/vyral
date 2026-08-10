using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using DurableTask.Core.Exceptions;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Execution.AzureDurable;

namespace Vyral.Execution.AzureDurable.Functions;

/// <summary>
/// Connects the SDK-neutral Vyral host to Microsoft Durable Task. The orchestration entry point
/// uses only Durable Task APIs; status-store transitions and handler execution are delegated to
/// explicit activities.
/// </summary>
public static class AzureDurableFunctionsBridge
{
    public static AzureDurableExecutionClient CreateClient(
        AzureDurableExecutionHost host,
        DurableTaskClient durableTaskClient) =>
        new(host, new AzureDurableFunctionsScheduler(durableTaskClient));

    public static Task<ExecutionRun> OrchestrateAsync(
        AzureDurableExecutionHost host,
        TaskOrchestrationContext orchestrationContext,
        AzureDurableStartCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(orchestrationContext);
        ArgumentNullException.ThrowIfNull(command);
        return host.OrchestrateAsync(command, new AzureDurableFunctionsOrchestrationDriver(orchestrationContext), ct);
    }

    public static Task<AzureDurableRunCreation> StartActivityAsync(
        AzureDurableExecutionHost host,
        AzureDurableStartCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(command);
        return host.StartRunWithReservationAsync(command, ct);
    }

    public static Task<AzureDurableOrchestrationStepResult> StepActivityAsync(
        AzureDurableExecutionHost host,
        AzureDurableActivityCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(command);
        return host.ExecuteOrchestrationStepAsync(command, ct);
    }
}

/// <summary>
/// Scheduler implementation for a Functions trigger that receives a <see cref="DurableTaskClient"/>.
/// </summary>
public sealed class AzureDurableFunctionsScheduler : IAzureDurableExecutionOrchestrationScheduler
{
    private readonly DurableTaskClient _client;

    public AzureDurableFunctionsScheduler(DurableTaskClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task ScheduleNewAsync(AzureDurableStartCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var options = new StartOrchestrationOptions(command.InstanceId)
            .WithDedupeStatuses(OrchestrationRuntimeStatus.Pending, OrchestrationRuntimeStatus.Running);
        try
        {
            await _client.ScheduleNewOrchestrationInstanceAsync(
                new TaskName(command.OrchestratorName),
                command,
                options,
                ct);
        }
        catch (OrchestrationAlreadyExistsException)
        {
            // The provider has already accepted a prior attempt to schedule this active Vyral run.
            // Treat it as the successful idempotent outcome, rather than stranding a replayed run.
        }
    }

    public async Task TerminateAsync(string instanceId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Instance id is required.", nameof(instanceId));
        await _client.TerminateInstanceAsync(instanceId.Trim(), reason, ct);
    }

    public async Task RaiseEventAsync(string instanceId, string eventName, System.Text.Json.Nodes.JsonNode? payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Instance id is required.", nameof(instanceId));
        if (string.IsNullOrWhiteSpace(eventName)) throw new ArgumentException("Event name is required.", nameof(eventName));
        await _client.RaiseEventAsync(instanceId.Trim(), eventName.Trim(), payload, ct);
    }
}

/// <summary>
/// Replay-safe Durable Task driver. Its state methods call named activities, never Cosmos or
/// handlers directly from the orchestrator.
/// </summary>
public sealed class AzureDurableFunctionsOrchestrationDriver :
    IAzureDurableExecutionOrchestrationDriver,
    IAzureDurableExecutionOrchestrationStateDriver
{
    // Durable Task validates the retry interval even when MaxNumberOfAttempts is one. One
    // millisecond preserves Vyral's exactly-once activity invocation while satisfying that API.
    private static readonly TaskOptions NoRetry = TaskOptions.FromRetryPolicy(new RetryPolicy(1, TimeSpan.FromMilliseconds(1)));
    private readonly TaskOrchestrationContext _context;

    public AzureDurableFunctionsOrchestrationDriver(TaskOrchestrationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public DateTime CurrentUtc => _context.CurrentUtcDateTime;

    public Task CreateTimerAsync(DateTime fireAtUtc, CancellationToken ct = default) =>
        _context.CreateTimer(fireAtUtc, ct);

    public Task<AzureDurableRunCreation> StartRunAsync(AzureDurableStartCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _context.CallActivityAsync<AzureDurableRunCreation>(
            new TaskName(command.StartActivityName), command, NoRetry);
    }

    public Task<AzureDurableOrchestrationStepResult> RunStepAsync(
        AzureDurableStartCommand command,
        ExecutionRun run,
        ExecutionWaitResult? waitOutcome = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(run);
        return _context.CallActivityAsync<AzureDurableOrchestrationStepResult>(
            new TaskName(command.StepActivityName),
            new AzureDurableActivityCommand
            {
                RunId = run.Id,
                HandlerId = run.HandlerId,
                PluginId = run.PluginId,
                Attempt = run.Attempt,
                Payload = run.Payload,
                CorrelationId = run.CorrelationId,
                WaitOutcome = waitOutcome
            },
            NoRetry);
    }

    public async Task<ExecutionWaitResult> WaitForDurableWaitAsync(AzureDurableWait wait, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wait);
        if (string.IsNullOrWhiteSpace(wait.Name)) throw new InvalidOperationException("Durable wait name is required.");

        if (string.Equals(wait.Kind, AzureDurableWaitKinds.Timer, StringComparison.Ordinal))
        {
            if (!wait.FireAtUtc.HasValue || wait.Timer is null)
            {
                throw new InvalidOperationException("A durable timer wait requires a timer and fire time.");
            }

            await _context.CreateTimer(wait.FireAtUtc.Value, ct);
            return new ExecutionWaitResult
            {
                Name = wait.Name,
                Outcome = ExecutionWaitOutcomes.Timer,
                Timer = wait.Timer
            };
        }

        if (!string.Equals(wait.Kind, AzureDurableWaitKinds.ExternalEvent, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Durable wait kind '{wait.Kind}' is not supported.");
        }

        var eventTask = _context.WaitForExternalEvent<JsonNode?>(wait.Name, ct);
        if (!wait.FireAtUtc.HasValue)
        {
            return ToExternalEventOutcome(wait.Name, await eventTask);
        }

        // Durable Task requires every created timer to expire or be cancelled. If the event wins,
        // cancelling the timeout prevents a stale timer from waking an already-completed
        // orchestration later.
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutTask = _context.CreateTimer(wait.FireAtUtc.Value, timeoutCancellation.Token);
        if (await Task.WhenAny(eventTask, timeoutTask) == eventTask)
        {
            timeoutCancellation.Cancel();
            return ToExternalEventOutcome(wait.Name, await eventTask);
        }

        await timeoutTask;
        return new ExecutionWaitResult { Name = wait.Name, Outcome = ExecutionWaitOutcomes.TimedOut };
    }

    public Task<AzureDurableActivityResult> CallActivityAsync(
        string activityName,
        AzureDurableActivityCommand command,
        AzureDurableRetryOptions retryOptions,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityName);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(retryOptions);
        var firstRetryInterval = TimeSpan.FromSeconds(Math.Max(0.001, retryOptions.InitialDelaySeconds));
        var maxRetryInterval = TimeSpan.FromSeconds(Math.Max(firstRetryInterval.TotalSeconds, retryOptions.MaxDelaySeconds));
        var retry = new RetryPolicy(
            Math.Max(1, retryOptions.MaxAttempts),
            firstRetryInterval,
            retryOptions.BackoffMultiplier <= 0 ? 1 : retryOptions.BackoffMultiplier,
            maxRetryInterval);
        return _context.CallActivityAsync<AzureDurableActivityResult>(
            new TaskName(activityName), command, TaskOptions.FromRetryPolicy(retry));
    }

    public Task SetCustomStatusAsync(AzureDurableStatusSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();
        _context.SetCustomStatus(snapshot);
        return Task.CompletedTask;
    }

    private ExecutionWaitResult ToExternalEventOutcome(string name, JsonNode? payload)
    {
        return new ExecutionWaitResult
        {
            Name = name,
            Outcome = ExecutionWaitOutcomes.ExternalEvent,
            Event = new ExecutionExternalEvent
            {
                Id = "azure-durable:" + _context.InstanceId + ":" + name,
                Name = name,
                RunId = _context.InstanceId,
                RaisedAtUtc = _context.CurrentUtcDateTime,
                Payload = payload
            }
        };
    }
}
