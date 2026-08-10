using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public interface IProviderModelCatalog
{
    Task<ProviderModelListResult> ListModelsAsync(CancellationToken ct = default);
}

public sealed class ProviderModelListResult
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = ProviderModelCatalogStatuses.Succeeded;

    [JsonPropertyName("source")]
    public string Source { get; set; } = "provider";

    [JsonPropertyName("defaultModelId")]
    public string? DefaultModelId { get; set; }

    [JsonPropertyName("items")]
    public List<ProviderModelDescriptor> Items { get; set; } = new();

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("providerStatus")]
    public string? ProviderStatus { get; set; }

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    public static ProviderModelListResult Unsupported(string provider)
    {
        return new ProviderModelListResult
        {
            Provider = provider,
            Status = ProviderModelCatalogStatuses.Unsupported,
            Source = "none",
            FailureClass = ProviderFailureClasses.Unsupported,
            ProviderStatus = "model_catalog_unsupported"
        };
    }

    public static ProviderModelListResult NotRegistered(string provider)
    {
        return new ProviderModelListResult
        {
            Provider = provider,
            Status = ProviderModelCatalogStatuses.NotRegistered,
            Source = "none",
            FailureClass = ProviderFailureClasses.Unsupported,
            ProviderStatus = "provider_not_registered"
        };
    }
}

public sealed class ProviderModelDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("default")]
    public bool Default { get; set; }

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();

    [JsonPropertyName("metadata")]
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

public static class ProviderModelCatalogStatuses
{
    public const string Succeeded = "succeeded";
    public const string Unsupported = "unsupported";
    public const string NotRegistered = "not_registered";
    public const string Failed = "failed";
}
