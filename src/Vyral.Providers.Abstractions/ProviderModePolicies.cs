namespace Vyral.Providers.Abstractions;

public static class ProviderModePolicies
{
    public static IReadOnlyList<ProviderModePolicy> Defaults(bool allowNetwork = true)
    {
        return new List<ProviderModePolicy>
        {
            new() { Id = ProviderModes.Mechanics, AllowedOutputKinds = new List<string> { ProviderOutputKinds.Evidence }, MaxInputBytes = 32 * 1024, MaxOutputBytes = 64 * 1024, TimeoutSeconds = 60, AllowNetwork = allowNetwork },
            new() { Id = ProviderModes.Development, AllowedOutputKinds = new List<string> { ProviderOutputKinds.Proposal, ProviderOutputKinds.Evidence }, MaxInputBytes = 64 * 1024, MaxOutputBytes = 128 * 1024, TimeoutSeconds = 120, AllowNetwork = allowNetwork },
            new() { Id = ProviderModes.Advisory, AllowedOutputKinds = new List<string> { ProviderOutputKinds.Proposal, ProviderOutputKinds.Evidence }, MaxInputBytes = 64 * 1024, MaxOutputBytes = 128 * 1024, TimeoutSeconds = 120, AllowNetwork = allowNetwork },
            new() { Id = ProviderModes.Research, AllowedOutputKinds = new List<string> { ProviderOutputKinds.Proposal, ProviderOutputKinds.Evidence }, MaxInputBytes = 64 * 1024, MaxOutputBytes = 128 * 1024, ToolPolicy = ProviderToolPolicies.ProviderOwned, TimeoutSeconds = 180, AllowNetwork = allowNetwork },
            new() { Id = ProviderModes.Review, AllowedOutputKinds = new List<string> { ProviderOutputKinds.Finding, ProviderOutputKinds.Evidence, ProviderOutputKinds.Question }, MaxInputBytes = 96 * 1024, MaxOutputBytes = 128 * 1024, TimeoutSeconds = 180, AllowNetwork = allowNetwork },
            new() { Id = ProviderModes.Scaffold, AllowedOutputKinds = new List<string> { ProviderOutputKinds.Proposal, ProviderOutputKinds.Patch, ProviderOutputKinds.Artifact }, MaxInputBytes = 96 * 1024, MaxOutputBytes = 192 * 1024, TimeoutSeconds = 240, AllowNetwork = allowNetwork }
        };
    }

    public static IReadOnlyDictionary<string, ProviderModePolicy> Index(IEnumerable<ProviderModePolicy> policies)
    {
        return policies.ToDictionary(policy => policy.Id, StringComparer.OrdinalIgnoreCase);
    }

    public static ProviderModePolicy? Resolve(IReadOnlyDictionary<string, ProviderModePolicy> policies, string mode)
    {
        return policies.TryGetValue(mode, out var policy) ? policy : null;
    }
}
