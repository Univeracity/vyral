using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class ProviderQualificationRequest
{
    [JsonPropertyName("capability")]
    public string? Capability { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = ProviderModes.Mechanics;
}
