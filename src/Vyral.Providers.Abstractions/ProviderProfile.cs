using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class ProviderProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("profileVersion")]
    public string ProfileVersion { get; set; } = "0.1";

    [JsonPropertyName("implementationVersion")]
    public string ImplementationVersion { get; set; } = "0.1";

    [JsonPropertyName("local")]
    public bool Local { get; set; }

    [JsonPropertyName("requiresNetwork")]
    public bool RequiresNetwork { get; set; }

    [JsonPropertyName("auth")]
    public string Auth { get; set; } = ProviderAuthTypes.None;

    [JsonPropertyName("configHash")]
    public string? ConfigHash { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public static class ProviderAuthTypes
{
    /// <summary>No authentication required.</summary>
    public const string None = "none";
    /// <summary>Authenticated via API key (typically from an environment variable or config).</summary>
    public const string ApiKey = "api-key";
    /// <summary>Authenticated via an external CLI tool that manages credentials.</summary>
    public const string ExternalCli = "external-cli";
}
