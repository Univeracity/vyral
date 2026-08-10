using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class ProviderTargetRegistry
{
    private readonly Dictionary<string, IProviderTarget> _targets;

    public ProviderTargetRegistry(IEnumerable<IProviderTarget> targets)
    {
        _targets = targets.ToDictionary(target => target.Profile.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ProviderProfile> GetProfiles()
    {
        return _targets.Values
            .Select(target => target.Profile)
            .OrderBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ProviderTargetDescriptor? GetDescriptor(string id)
    {
        return _targets.TryGetValue(id, out var target)
            ? new ProviderTargetDescriptor
            {
                Profile = target.Profile,
                Capabilities = target.Capabilities.ToList()
            }
            : null;
    }

    public IReadOnlyList<ProviderQualification>? GetQualifications(string id)
    {
        return _targets.TryGetValue(id, out var target)
            ? ProviderQualificationBuilder.Describe(target)
            : null;
    }

    public IProviderTarget? GetTarget(string id)
    {
        return _targets.GetValueOrDefault(id);
    }
}

public sealed class ProviderTargetDescriptor
{
    [JsonPropertyName("profile")]
    public ProviderProfile Profile { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public List<ProviderCapabilityDescriptor> Capabilities { get; set; } = new();
}
