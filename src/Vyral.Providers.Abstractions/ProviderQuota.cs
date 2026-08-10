using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public interface IProviderQuotaReporter
{
    Task<ProviderQuotaResult> GetQuotaAsync(CancellationToken ct = default);
}

public sealed class ProviderQuotaResult
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = ProviderQuotaStatuses.Succeeded;

    [JsonPropertyName("source")]
    public string Source { get; set; } = "provider";

    [JsonPropertyName("advisory")]
    public bool Advisory { get; set; } = true;

    [JsonPropertyName("items")]
    public List<ProviderQuotaBucket> Items { get; set; } = new();

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("providerStatus")]
    public string? ProviderStatus { get; set; }

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("metadata")]
    public Dictionary<string, object?> Metadata { get; set; } = new();

    public static ProviderQuotaResult Unsupported(string provider)
    {
        return new ProviderQuotaResult
        {
            Provider = provider,
            Status = ProviderQuotaStatuses.Unsupported,
            Source = "none",
            FailureClass = ProviderFailureClasses.Unsupported,
            ProviderStatus = "quota_unsupported"
        };
    }

    public static ProviderQuotaResult NotRegistered(string provider)
    {
        return new ProviderQuotaResult
        {
            Provider = provider,
            Status = ProviderQuotaStatuses.NotRegistered,
            Source = "none",
            FailureClass = ProviderFailureClasses.Unsupported,
            ProviderStatus = "provider_not_registered"
        };
    }
}

public sealed class ProviderQuotaBucket
{
    [JsonPropertyName("limitId")]
    public string LimitId { get; set; } = string.Empty;

    [JsonPropertyName("limitName")]
    public string? LimitName { get; set; }

    [JsonPropertyName("primary")]
    public ProviderQuotaWindow? Primary { get; set; }

    [JsonPropertyName("secondary")]
    public ProviderQuotaWindow? Secondary { get; set; }

    [JsonPropertyName("rateLimitReachedType")]
    public string? RateLimitReachedType { get; set; }

    [JsonPropertyName("planType")]
    public string? PlanType { get; set; }

    [JsonPropertyName("credits")]
    public JsonNode? Credits { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

public sealed class ProviderQuotaWindow
{
    [JsonPropertyName("usedPercent")]
    public double? UsedPercent { get; set; }

    [JsonPropertyName("remainingPercent")]
    public double? RemainingPercent { get; set; }

    [JsonPropertyName("windowDurationMins")]
    public int? WindowDurationMins { get; set; }

    [JsonPropertyName("resetsAtUnixSeconds")]
    public long? ResetsAtUnixSeconds { get; set; }

    [JsonPropertyName("resetsAt")]
    public DateTimeOffset? ResetsAt { get; set; }
}

public static class ProviderQuotaStatuses
{
    public const string Succeeded = "succeeded";
    public const string Unsupported = "unsupported";
    public const string NotRegistered = "not_registered";
    public const string Unavailable = "unavailable";
    public const string Failed = "failed";
}
