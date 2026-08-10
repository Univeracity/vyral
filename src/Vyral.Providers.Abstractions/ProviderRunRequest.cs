using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class ProviderRunRequest
{
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = ProviderCapabilityIds.AiChat;

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "run";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = ProviderModes.Advisory;

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("payload")]
    public JsonObject Payload { get; set; } = new();

    /// <summary>
    /// Caller-supplied correlation token for log/trace correlation across retries or related requests.
    /// Leave empty if not needed; the server uses the job Id for its own tracking.
    /// Setting this on a retry allows correlating all attempts for the same logical operation.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Content reference URIs or IDs that should be made available to the provider
    /// as context attachments (e.g. "object://bucket/key", "record://collection/id").
    /// Providers that support context attachment resolve these and include them in the run.
    /// Providers that do not support it ignore the list.
    /// </summary>
    [JsonPropertyName("contextRefs")]
    public List<string> ContextRefs { get; set; } = new();

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; set; }

    [JsonPropertyName("maxOutputBytes")]
    public int? MaxOutputBytes { get; set; }

    [JsonPropertyName("artifactDirectory")]
    public string? ArtifactDirectory { get; set; }
}
