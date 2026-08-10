using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Vyral.Execution.Temporal.Hosting;

[Workflow(TemporalExecutionProtocolNames.CoordinatorWorkflow)]
internal sealed class TemporalRunCoordinatorWorkflow : ITemporalRunCoordinatorWorkflow
{
    internal const int ContinueAsNewTransitionThreshold = 32;
    internal const string ContinueAsNewPatchId = "vyral-run-coordinator-continue-as-new-v1";
    private const int ActivityStartToCloseMinutes = 10;
    private const int ActivityHeartbeatSeconds = 30;
    private const int ActivityTransportAttempts = 100;
    private readonly List<TemporalCoordinatorSignal> _signals = [];
    private TemporalCoordinatorInput? _input;

    [WorkflowRun]
    public async Task<TemporalCoordinatorResult> RunAsync(TemporalCoordinatorInput input)
    {
        ValidateInput(input);
        _input = input;
        foreach (var signal in input.BufferedSignals) AddSignal(signal);
        var attempt = input.AttemptOffset;
        var transitions = 0;
        var totalTransitions = input.CoordinationTransitions;
        var continueAsNewEnabled = Workflow.Patched(ContinueAsNewPatchId);

        try
        {
            while (true)
            {
                if (continueAsNewEnabled &&
                    ShouldContinueAsNew(transitions, Workflow.ContinueAsNewSuggested))
                {
                    var continuation = CreateContinuationInput(
                        input,
                        attempt,
                        totalTransitions,
                        _signals);
                    throw Workflow.CreateContinueAsNewException<ITemporalRunCoordinatorWorkflow, TemporalCoordinatorResult>(
                        workflow => workflow.RunAsync(continuation),
                        new ContinueAsNewOptions());
                }
                attempt++;
                transitions++;
                totalTransitions++;
                var outcome = await Workflow.ExecuteActivityAsync(
                    (TemporalExecutionActivities activities) => activities.ExecuteAttemptAsync(new TemporalExecutionAttemptRequest
                    {
                        RunId = input.RunId,
                        Generation = input.Generation,
                        Attempt = attempt
                    }),
                    ActivityOptions());
                ValidateOutcome(outcome);

                switch (outcome.Disposition)
                {
                    case TemporalAttemptDispositions.Completed:
                    case TemporalAttemptDispositions.Terminal:
                        return new TemporalCoordinatorResult
                        {
                            RunId = input.RunId,
                            Generation = input.Generation,
                            Status = outcome.TerminalStatus ?? outcome.Disposition,
                            CoordinationTransitions = totalTransitions
                        };
                    case TemporalAttemptDispositions.Retryable:
                        await Workflow.DelayAsync(TimeSpan.FromMilliseconds(outcome.RetryDelayMilliseconds!.Value));
                        break;
                    case TemporalAttemptDispositions.Suspended:
                        await WaitAndProjectAsync(input, outcome);
                        transitions++;
                        totalTransitions++;
                        break;
                }
            }
        }
        catch (Exception ex) when (TemporalException.IsCanceledException(ex))
        {
            await Workflow.ExecuteActivityAsync(
                (TemporalExecutionActivities activities) => activities.ProjectCancellationAsync(new TemporalExecutionCancellation
                {
                    RunId = input.RunId,
                    Generation = input.Generation
                }),
                ActivityOptions(CancellationToken.None));
            throw;
        }
    }

    [WorkflowSignal(TemporalExecutionProtocolNames.ExternalEventSignal)]
    public Task NotifyExternalEventAsync(TemporalCoordinatorSignal signal)
    {
        AddSignal(signal);
        return Task.CompletedTask;
    }

    [WorkflowQuery(TemporalExecutionProtocolNames.IdentityQuery)]
    public TemporalCoordinatorIdentity GetIdentity()
    {
        var input = _input ?? throw new InvalidOperationException("Temporal coordinator has not started.");
        return new TemporalCoordinatorIdentity { RunId = input.RunId, Generation = input.Generation };
    }

    private async Task WaitAndProjectAsync(
        TemporalCoordinatorInput input,
        TemporalExecutionAttemptOutcome outcome)
    {
        if (string.Equals(outcome.WaitKind, TemporalWaitKinds.Timer, StringComparison.Ordinal))
        {
            var delay = outcome.ResumeAtUtc!.Value - Workflow.UtcNow;
            if (delay > TimeSpan.Zero) await Workflow.DelayAsync(delay);
            await ProjectRequiredResolutionAsync(Resolution(input, outcome, TemporalWaitResolutions.Timer));
            return;
        }

        while (true)
        {
            foreach (var signal in _signals.ToList())
            {
                var result = await ProjectResolutionAsync(Resolution(
                    input,
                    outcome,
                    TemporalWaitResolutions.ExternalEvent,
                    signal));
                if (!result.Accepted) continue;
                _signals.Remove(signal);
                return;
            }

            var observedSignalCount = _signals.Count;
            if (outcome.ResumeAtUtc.HasValue)
            {
                var timeout = outcome.ResumeAtUtc.Value - Workflow.UtcNow;
                if (timeout <= TimeSpan.Zero ||
                    !await Workflow.WaitConditionAsync(() => _signals.Count > observedSignalCount, timeout))
                {
                    await ProjectRequiredResolutionAsync(Resolution(input, outcome, TemporalWaitResolutions.Timeout));
                    return;
                }
            }
            else
            {
                await Workflow.WaitConditionAsync(() => _signals.Count > observedSignalCount);
            }
        }
    }

    private static Task<TemporalExecutionWaitProjectionResult> ProjectResolutionAsync(TemporalExecutionWaitResolution resolution) =>
        Workflow.ExecuteActivityAsync(
            (TemporalExecutionActivities activities) => activities.ProjectWaitResolutionAsync(resolution),
            ActivityOptions());

    private static async Task ProjectRequiredResolutionAsync(TemporalExecutionWaitResolution resolution)
    {
        var result = await ProjectResolutionAsync(resolution);
        if (!result.Accepted)
            throw new InvalidOperationException("Temporal projection rejected a timer or timeout resolution for the active wait.");
    }

    private static TemporalExecutionWaitResolution Resolution(
        TemporalCoordinatorInput input,
        TemporalExecutionAttemptOutcome outcome,
        string resolution,
        TemporalCoordinatorSignal? signal = null) => new()
    {
        RunId = input.RunId,
        Generation = input.Generation,
        WaitId = outcome.WaitId!,
        Resolution = resolution,
        EventId = signal?.EventId,
        EventRevision = signal?.EventRevision
    };

    internal static ActivityOptions CreateActivityOptions(CancellationToken? cancellationToken = null) => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(ActivityStartToCloseMinutes),
        HeartbeatTimeout = TimeSpan.FromSeconds(ActivityHeartbeatSeconds),
        CancellationToken = cancellationToken ?? Workflow.CancellationToken,
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = ActivityTransportAttempts,
            InitialInterval = TimeSpan.FromSeconds(1),
            MaximumInterval = TimeSpan.FromSeconds(30),
            BackoffCoefficient = 2
        }
    };

    private static ActivityOptions ActivityOptions(CancellationToken? cancellationToken = null) =>
        CreateActivityOptions(cancellationToken);

    internal static void ValidateInput(TemporalCoordinatorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.RunId) || input.ProjectionRevision < 1 || input.Generation < 1 ||
            input.AttemptOffset < 0 || input.CoordinationTransitions < 0 || input.BufferedSignals is null)
            throw new InvalidOperationException("Temporal coordinator input is invalid.");
        foreach (var signal in input.BufferedSignals) ValidateSignal(signal);
    }

    internal static bool ShouldContinueAsNew(int transitions, bool serverSuggested) =>
        transitions >= ContinueAsNewTransitionThreshold || transitions > 0 && serverSuggested;

    internal static TemporalCoordinatorInput CreateContinuationInput(
        TemporalCoordinatorInput input,
        int attemptOffset,
        int coordinationTransitions,
        IReadOnlyCollection<TemporalCoordinatorSignal> bufferedSignals)
    {
        ValidateInput(input);
        if (attemptOffset < 0 || coordinationTransitions < 0)
            throw new InvalidOperationException("Temporal continuation offsets are invalid.");
        ArgumentNullException.ThrowIfNull(bufferedSignals);
        foreach (var signal in bufferedSignals) ValidateSignal(signal);
        return input with
        {
            AttemptOffset = attemptOffset,
            CoordinationTransitions = coordinationTransitions,
            BufferedSignals = bufferedSignals
                .Select(signal => new TemporalCoordinatorSignal
                {
                    EventId = signal.EventId,
                    EventRevision = signal.EventRevision
                })
                .ToArray()
        };
    }

    internal static void ValidateOutcome(TemporalExecutionAttemptOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        switch (outcome.Disposition)
        {
            case TemporalAttemptDispositions.Completed:
            case TemporalAttemptDispositions.Terminal:
                if (string.IsNullOrWhiteSpace(outcome.TerminalStatus))
                    throw new InvalidOperationException("A terminal Temporal attempt outcome requires a portable status.");
                return;
            case TemporalAttemptDispositions.Retryable:
                if (outcome.RetryDelayMilliseconds is < 1 or > 86_400_000)
                    throw new InvalidOperationException("A retryable Temporal attempt outcome requires a bounded delay.");
                return;
            case TemporalAttemptDispositions.Suspended:
                if (string.IsNullOrWhiteSpace(outcome.WaitId) ||
                    outcome.WaitKind is not (TemporalWaitKinds.ExternalEvent or TemporalWaitKinds.Timer) ||
                    outcome.WaitKind == TemporalWaitKinds.Timer && !outcome.ResumeAtUtc.HasValue)
                    throw new InvalidOperationException("A suspended Temporal attempt outcome requires a valid durable wait directive.");
                return;
            default:
                throw new InvalidOperationException("Temporal attempt outcome disposition is invalid.");
        }
    }

    private void AddSignal(TemporalCoordinatorSignal signal)
    {
        ValidateSignal(signal);
        if (!_signals.Any(candidate =>
            string.Equals(candidate.EventId, signal.EventId, StringComparison.Ordinal) &&
            candidate.EventRevision == signal.EventRevision))
        {
            _signals.Add(signal);
        }
    }

    private static void ValidateSignal(TemporalCoordinatorSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (string.IsNullOrWhiteSpace(signal.EventId) || signal.EventRevision < 1)
            throw new InvalidOperationException("Temporal coordinator signal identity is invalid.");
    }
}
