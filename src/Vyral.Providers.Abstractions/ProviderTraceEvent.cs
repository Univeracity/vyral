using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class ProviderTraceEvent
{
    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = string.Empty;

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("inputHash")]
    public string? InputHash { get; set; }

    [JsonPropertyName("outputHash")]
    public string? OutputHash { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("adapterId")]
    public string? AdapterId { get; set; }

    [JsonPropertyName("configHash")]
    public string? ConfigHash { get; set; }

    [JsonPropertyName("durationMs")]
    public double DurationMs { get; set; }

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("authorityBoundary")]
    public string AuthorityBoundary { get; set; } = ProviderBoundary.AuthorityBoundary;

    [JsonPropertyName("artifactRefs")]
    public List<string> ArtifactRefs { get; set; } = new();

    /// <summary>Full-envelope hashes of metering receipts associated with this trace.</summary>
    [JsonPropertyName("meteringReceiptHashes")]
    public List<string> MeteringReceiptHashes { get; set; } = new();
}
