using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class ProviderCapabilityMatrix
{
    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("capabilityIds")]
    public List<string> CapabilityIds { get; set; } = new();

    [JsonPropertyName("items")]
    public List<ProviderCapabilityMatrixItem> Items { get; set; } = new();

    [JsonPropertyName("disabledProviders")]
    public List<ProviderDisabledInfo> DisabledProviders { get; set; } = new();

    [JsonPropertyName("failureClasses")]
    public List<string> FailureClasses { get; set; } = new();

    [JsonPropertyName("operationalLimits")]
    public Dictionary<string, object?> OperationalLimits { get; set; } = new();

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();
}

public sealed class ProviderCapabilityMatrixItem
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("registered")]
    public bool Registered { get; set; } = true;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("registrationStatus")]
    public string RegistrationStatus { get; set; } = "registered";

    [JsonPropertyName("registrationHint")]
    public string? RegistrationHint { get; set; }

    [JsonPropertyName("local")]
    public bool Local { get; set; }

    [JsonPropertyName("requiresNetwork")]
    public bool RequiresNetwork { get; set; }

    [JsonPropertyName("auth")]
    public string Auth { get; set; } = ProviderAuthTypes.None;

    [JsonPropertyName("capabilityIds")]
    public List<string> CapabilityIds { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public Dictionary<string, ProviderCapabilitySupport> Capabilities { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("supportsModelListing")]
    public bool SupportsModelListing { get; set; }

    [JsonPropertyName("supportsQuota")]
    public bool SupportsQuota { get; set; }

    [JsonPropertyName("supportsAsyncJobs")]
    public bool SupportsAsyncJobs { get; set; } = true;

    [JsonPropertyName("supportsArtifacts")]
    public bool SupportsArtifacts { get; set; }

    [JsonPropertyName("artifactPolicy")]
    public string ArtifactPolicy { get; set; } = "provider_or_capability_specific";

    [JsonPropertyName("failureSemantics")]
    public List<string> FailureSemantics { get; set; } = new()
    {
        "status",
        "failureClass",
        "providerStatus",
        "trace.traceId"
    };
}

public sealed class ProviderCapabilitySupport
{
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    [JsonPropertyName("operations")]
    public List<string> Operations { get; set; } = new();

    [JsonPropertyName("modes")]
    public List<string> Modes { get; set; } = new();

    [JsonPropertyName("toolPolicy")]
    public string ToolPolicy { get; set; } = ProviderToolPolicies.CallerOwned;

    [JsonPropertyName("unsupportedFeatures")]
    public List<string> UnsupportedFeatures { get; set; } = new();
}
