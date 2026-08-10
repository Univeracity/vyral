namespace Vyral.Server;

internal sealed class ExecutionRuntimeProductPolicyShape
{
    public required string ProductId { get; init; }
    public required IReadOnlySet<string> AllowedServiceIdentities { get; init; }
}
