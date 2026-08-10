using Vyral.Execution;

namespace Vyral.Server;

public sealed record ExecutionRuntimeSurface(
    ExecutionRuntimeAdapterStatus Status,
    IReadOnlyList<ExecutionPluginDescriptor> Plugins,
    IReadOnlyList<ExecutionHandlerDescriptor> Handlers);

/// <summary>Safe, identity-scoped execution discovery for shared consumer deployments.</summary>
public sealed record EffectiveExecutionRuntimeSurface(
    ExecutionRuntimeAdapterStatus Status,
    ExecutionRuntimeDiscoveryScope Scope,
    IReadOnlyList<ExecutionHandlerDescriptor> Handlers);

public sealed class ExecutionRuntimeDiscoveryScope
{
    public bool SharedExecution { get; init; }
    public bool ScopeRequired { get; init; }
    public string? ProductId { get; init; }
    public string? TenantId { get; init; }
}
