namespace Vyral.Execution;

public static class ExecutionCapabilityCatalog
{
    public static IReadOnlySet<string> PortableCapabilityIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ExecutionCapabilityIds.LocalDispatch,
        ExecutionCapabilityIds.RemoteOrchestration,
        ExecutionCapabilityIds.InProcessHandlers,
        ExecutionCapabilityIds.DurableRuns,
        ExecutionCapabilityIds.DurableTimers,
        ExecutionCapabilityIds.ExternalEvents,
        ExecutionCapabilityIds.DurableWaits,
        ExecutionCapabilityIds.Cancellation,
        ExecutionCapabilityIds.Retries,
        ExecutionCapabilityIds.RestartResume,
        ExecutionCapabilityIds.Leases,
        ExecutionCapabilityIds.Artifacts,
        ExecutionCapabilityIds.TraceHistory,
        ExecutionCapabilityIds.Idempotency,
        ExecutionCapabilityIds.ExternalWorkers
    };

    public static IReadOnlyList<string> RequiredAdapterCapabilities { get; } =
    [
        ExecutionCapabilityIds.DurableRuns,
        ExecutionCapabilityIds.Cancellation,
        ExecutionCapabilityIds.Retries,
        ExecutionCapabilityIds.Artifacts,
        ExecutionCapabilityIds.TraceHistory,
        ExecutionCapabilityIds.Idempotency
    ];

    public static IReadOnlyList<string> DispatchCapabilities { get; } =
    [
        ExecutionCapabilityIds.LocalDispatch,
        ExecutionCapabilityIds.RemoteOrchestration
    ];

    /// <summary>
    /// Every adapter must state how registered work is executed. An adapter may support both
    /// in-process handlers and externally deployed workers, but callers must not assume that a
    /// registered .NET delegate can run when only <c>external.workers</c> is advertised.
    /// </summary>
    public static IReadOnlyList<string> ExecutionModelCapabilities { get; } =
    [
        ExecutionCapabilityIds.InProcessHandlers,
        ExecutionCapabilityIds.ExternalWorkers
    ];

    public static IReadOnlyList<string> CoordinationCapabilities { get; } =
    [
        ExecutionCapabilityIds.DurableTimers,
        ExecutionCapabilityIds.ExternalEvents,
        ExecutionCapabilityIds.Leases,
        ExecutionCapabilityIds.RestartResume
    ];

    public static bool IsPortableCapability(string? capability)
    {
        return !string.IsNullOrWhiteSpace(capability) &&
            PortableCapabilityIds.Contains(capability.Trim());
    }

    public static bool Supports(IEnumerable<string>? capabilities, string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return false;
        }

        return capabilities?.Any(candidate =>
            string.Equals(candidate?.Trim(), capability.Trim(), StringComparison.Ordinal)) == true;
    }

    public static IReadOnlyList<string> MissingRequiredCapabilities(IEnumerable<string>? capabilities)
    {
        var advertised = new HashSet<string>(
            capabilities?.Where(capability => !string.IsNullOrWhiteSpace(capability)).Select(capability => capability.Trim()) ??
            Enumerable.Empty<string>(),
            StringComparer.Ordinal);
        return RequiredAdapterCapabilities
            .Where(required => !advertised.Contains(required))
            .ToList();
    }

    public static bool HasDispatchCapability(IEnumerable<string>? capabilities)
    {
        var advertised = new HashSet<string>(
            capabilities?.Where(capability => !string.IsNullOrWhiteSpace(capability)).Select(capability => capability.Trim()) ??
            Enumerable.Empty<string>(),
            StringComparer.Ordinal);
        return DispatchCapabilities.Any(advertised.Contains);
    }

    public static bool HasExecutionModelCapability(IEnumerable<string>? capabilities)
    {
        var advertised = new HashSet<string>(
            capabilities?.Where(capability => !string.IsNullOrWhiteSpace(capability)).Select(capability => capability.Trim()) ??
            Enumerable.Empty<string>(),
            StringComparer.Ordinal);
        return ExecutionModelCapabilities.Any(advertised.Contains);
    }
}
