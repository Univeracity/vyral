namespace Vyral.Providers.Abstractions;

public static class ProviderQualificationBuilder
{
    public static IReadOnlyList<ProviderQualification> Describe(IProviderTarget target, string status = ProviderQualificationStatuses.Unvalidated)
    {
        return target.Capabilities
            .Select(capability => Create(target, capability, status))
            .OrderBy(qualification => qualification.Capability, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ProviderQualification Create(
        IProviderTarget target,
        ProviderCapabilityDescriptor capability,
        string status,
        DateTime? lastValidatedAt = null,
        IEnumerable<string>? evidenceRefs = null)
    {
        var driftTriggers = new List<string>
        {
            "config_hash_changed",
            "implementation_version_changed",
            "capability_descriptor_changed"
        };
        if (target.Profile.RequiresNetwork)
        {
            driftTriggers.Add("network_reachability_changed");
        }

        if (target.Profile.Auth != ProviderAuthTypes.None)
        {
            driftTriggers.Add("auth_context_changed");
        }

        return new ProviderQualification
        {
            Provider = target.Profile.Id,
            Capability = capability.Id,
            OperationSet = capability.Operations.ToList(),
            ConfigHash = target.Profile.ConfigHash,
            Status = status,
            LastValidatedAt = lastValidatedAt,
            DriftTriggers = driftTriggers,
            UnsupportedFeatures = capability.UnsupportedFeatures.ToList(),
            EvidenceRefs = evidenceRefs?.ToList() ?? new List<string>()
        };
    }
}
