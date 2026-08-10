using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class ProviderQualification
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = string.Empty;

    [JsonPropertyName("operationSet")]
    public List<string> OperationSet { get; set; } = new();

    [JsonPropertyName("configHash")]
    public string? ConfigHash { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = ProviderQualificationStatuses.Unvalidated;

    [JsonPropertyName("lastValidatedAt")]
    public DateTime? LastValidatedAt { get; set; }

    [JsonPropertyName("driftTriggers")]
    public List<string> DriftTriggers { get; set; } = new();

    [JsonPropertyName("unsupportedFeatures")]
    public List<string> UnsupportedFeatures { get; set; } = new();

    [JsonPropertyName("evidenceRefs")]
    public List<string> EvidenceRefs { get; set; } = new();
}
