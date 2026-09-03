using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class ProviderRunResult
{
    [JsonPropertyName("status")]
    public ProviderRunStatus Status { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = string.Empty;

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("output")]
    public JsonObject Output { get; set; } = new();

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("providerStatus")]
    public string? ProviderStatus { get; set; }

    /// <summary>Human-readable error message. Set by providers on failure; surfaced in job status and exception messages.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Safe non-raw diagnostic for non-success provider runs.</summary>
    [JsonPropertyName("rejection")]
    public ProviderRunRejectionDiagnostic? Rejection { get; set; }

    [JsonPropertyName("trace")]
    public ProviderTraceEvent? Trace { get; set; }

    /// <summary>
    /// Independently verifiable observations about this run. Each receipt identifies its own
    /// source and evidentiary strength; consumers must not infer trust from presence alone.
    /// </summary>
    [JsonPropertyName("metering")]
    public List<AiMeteringReceipt> Metering { get; set; } = new();

    /// <summary>
    /// In-process adapter handoff for provider-reported usage that the Vyral runner copies into
    /// its terminal summary before signing. It is intentionally not a second wire representation.
    /// </summary>
    [JsonIgnore]
    public List<AiMeteringMeasurement> MeteringMeasurements { get; set; } = new();
}
