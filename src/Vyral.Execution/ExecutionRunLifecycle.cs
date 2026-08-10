namespace Vyral.Execution;

public enum ExecutionTransitionKind
{
    Standard,
    Retry,
    Recovery,
    DurableWait
}

public static class ExecutionRunLifecycle
{
    public static IReadOnlyList<string> ActiveStatuses { get; } =
    [
        ExecutionRunStatuses.Queued,
        ExecutionRunStatuses.Waiting,
        ExecutionRunStatuses.Running
    ];

    public static IReadOnlyList<string> TerminalStatuses { get; } =
    [
        ExecutionRunStatuses.Succeeded,
        ExecutionRunStatuses.Failed,
        ExecutionRunStatuses.Cancelled,
        ExecutionRunStatuses.Rejected,
        ExecutionRunStatuses.TimedOut
    ];

    public static bool IsActive(string? status)
    {
        return status is ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting or ExecutionRunStatuses.Running;
    }

    public static bool CanCreateAs(string? status)
    {
        return status is ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting or ExecutionRunStatuses.Rejected;
    }

    public static void EnsureCreationStatus(string? status)
    {
        if (!CanCreateAs(status))
        {
            throw new InvalidOperationException($"Execution run cannot be created with status '{status ?? "(null)"}'.");
        }
    }

    public static bool CanTransition(string? from, string? to, ExecutionTransitionKind kind = ExecutionTransitionKind.Standard)
    {
        if (string.IsNullOrWhiteSpace(to) || !ExecutionRunStatuses.IsKnown(to))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(from))
        {
            return CanCreateAs(to);
        }

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return true;
        }

        if (kind == ExecutionTransitionKind.Recovery)
        {
            return from == ExecutionRunStatuses.Running && to == ExecutionRunStatuses.Queued;
        }

        if (kind == ExecutionTransitionKind.Retry)
        {
            return from is ExecutionRunStatuses.Failed or ExecutionRunStatuses.TimedOut &&
                to == ExecutionRunStatuses.Waiting;
        }

        if (kind == ExecutionTransitionKind.DurableWait)
        {
            return from == ExecutionRunStatuses.Running && to == ExecutionRunStatuses.Waiting;
        }

        if (ExecutionRunStatuses.IsTerminal(from))
        {
            return false;
        }

        return from switch
        {
            ExecutionRunStatuses.Queued => to is ExecutionRunStatuses.Waiting or ExecutionRunStatuses.Running or ExecutionRunStatuses.Cancelled or ExecutionRunStatuses.Rejected,
            ExecutionRunStatuses.Waiting => to is ExecutionRunStatuses.Queued or ExecutionRunStatuses.Running or ExecutionRunStatuses.Cancelled or ExecutionRunStatuses.Rejected,
        ExecutionRunStatuses.Running => to is ExecutionRunStatuses.Succeeded
                or ExecutionRunStatuses.Failed
                or ExecutionRunStatuses.Cancelled
                or ExecutionRunStatuses.Rejected
                or ExecutionRunStatuses.TimedOut,
            _ => false
        };
    }

    public static void EnsureTransition(string? from, string? to, ExecutionTransitionKind kind = ExecutionTransitionKind.Standard)
    {
        if (!CanTransition(from, to, kind))
        {
            throw new InvalidOperationException($"Execution run status cannot transition from '{from ?? "(none)"}' to '{to ?? "(null)"}'.");
        }
    }
}
