using Vyral.Providers.Local;

// Pure profile validation shared with no-launch qualification. The controller
// still owns the original absolute deadline; this never renews its allowance.
internal static class OwnedNativeDeadline
{
    internal static TimeSpan Remaining(DateTimeOffset deadlineUtc, int maxExecutionSeconds, DateTimeOffset nowUtc)
    {
        if (maxExecutionSeconds is < 1 or > LocalLlamaCppProviderTarget.MaximumExecutionSeconds)
            throw new ArgumentException("unsupported_profile");
        var remaining = deadlineUtc - nowUtc;
        if (deadlineUtc.Offset != TimeSpan.Zero || remaining <= TimeSpan.Zero || remaining > TimeSpan.FromSeconds(maxExecutionSeconds))
            throw new ArgumentException("immutable_deadline");
        return remaining;
    }
}
