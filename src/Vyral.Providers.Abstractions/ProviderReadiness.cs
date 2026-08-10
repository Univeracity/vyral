using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class ProviderReadinessEnvelope
{
    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("items")]
    public List<ProviderCapabilityReadiness> Items { get; set; } = new();

    [JsonPropertyName("disabledProviders")]
    public List<ProviderDisabledInfo> DisabledProviders { get; set; } = new();
}

public sealed class ProviderDisabledInfo
{
    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("registrationStatus")]
    public string RegistrationStatus { get; set; } = "disabled";

    [JsonPropertyName("enableLiveTargets")]
    public bool EnableLiveTargets { get; set; }

    [JsonPropertyName("hint")]
    public string Hint { get; set; } = string.Empty;
}

public sealed class ProviderCapabilityReadiness
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = string.Empty;

    [JsonPropertyName("registrationStatus")]
    public string RegistrationStatus { get; set; } = "registered";

    [JsonPropertyName("registrationHint")]
    public string? RegistrationHint { get; set; }

    [JsonPropertyName("operations")]
    public List<string> Operations { get; set; } = new();

    [JsonPropertyName("modes")]
    public List<string> Modes { get; set; } = new();

    [JsonPropertyName("configHash")]
    public string? ConfigHash { get; set; }

    [JsonPropertyName("qualificationStatus")]
    public string QualificationStatus { get; set; } = ProviderQualificationStatuses.Unvalidated;

    [JsonPropertyName("lastValidatedAt")]
    public DateTime? LastValidatedAt { get; set; }

    [JsonPropertyName("callable")]
    public bool Callable { get; set; }

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("canRunUnvalidated")]
    public bool CanRunUnvalidated { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("evidenceRefs")]
    public List<string> EvidenceRefs { get; set; } = new();

    [JsonPropertyName("driftTriggers")]
    public List<string> DriftTriggers { get; set; } = new();

    [JsonPropertyName("unsupportedFeatures")]
    public List<string> UnsupportedFeatures { get; set; } = new();

    [JsonPropertyName("local")]
    public bool Local { get; set; }

    [JsonPropertyName("requiresNetwork")]
    public bool RequiresNetwork { get; set; }

    [JsonPropertyName("auth")]
    public string Auth { get; set; } = ProviderAuthTypes.None;

    [JsonPropertyName("authRequired")]
    public bool AuthRequired { get; set; }

    [JsonPropertyName("operationalLimits")]
    public Dictionary<string, object?> OperationalLimits { get; set; } = new();
}
