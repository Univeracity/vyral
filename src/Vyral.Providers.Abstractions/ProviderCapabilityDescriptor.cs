using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class ProviderCapabilityDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.1";

    [JsonPropertyName("operations")]
    public List<string> Operations { get; set; } = new();

    /// <summary>
    /// Static capability-level input limits, independent of mode. Intended keys:
    /// "maxContextTokens", "maxContextBytes" — physical model limits that cannot be exceeded
    /// regardless of mode policy. Per-mode enforcement limits (which may be stricter) are
    /// in ModePolicies[mode].MaxInputBytes. Providers populate only the keys they can assert.
    /// </summary>
    [JsonPropertyName("inputLimits")]
    public Dictionary<string, object?> InputLimits { get; set; } = new();

    /// <summary>
    /// Static capability-level output limits. Intended keys: "maxOutputTokens", "maxOutputBytes".
    /// Per-mode enforcement limits are in ModePolicies[mode].MaxOutputBytes.
    /// </summary>
    [JsonPropertyName("outputLimits")]
    public Dictionary<string, object?> OutputLimits { get; set; } = new();

    [JsonPropertyName("toolPolicy")]
    public string ToolPolicy { get; set; } = ProviderToolPolicies.CallerOwned;

    [JsonPropertyName("modePolicies")]
    public List<ProviderModePolicy> ModePolicies { get; set; } = new();

    [JsonPropertyName("unsupportedFeatures")]
    public List<string> UnsupportedFeatures { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
