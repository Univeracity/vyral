namespace Vyral.Providers.Abstractions;

public interface IProviderTarget
{
    ProviderProfile Profile { get; }
    IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }
    Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default);
}
