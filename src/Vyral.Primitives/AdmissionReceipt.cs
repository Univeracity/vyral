using System.Text.Json.Serialization;

namespace Vyral.Primitives;

/// <summary>
/// Durable proof that one exact request was admitted for asynchronous work.
/// The referenced resource remains authoritative for execution progress and result state.
/// </summary>
public sealed class AdmissionReceipt
{
    public const string CurrentVersion = "vyral.admission.v1";

    [JsonPropertyName("version")]
    public string Version { get; set; } = CurrentVersion;

    [JsonPropertyName("admissionId")]
    public string AdmissionId { get; set; } = string.Empty;

    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = AdmissionStatuses.Accepted;

    [JsonPropertyName("resourceId")]
    public string ResourceId { get; set; } = string.Empty;

    [JsonPropertyName("requestHash")]
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the caller idempotency key. The raw key is deliberately not reflected.
    /// Null means the server allocated a unique admission without a caller replay key.
    /// </summary>
    [JsonPropertyName("idempotencyKeyHash")]
    public string? IdempotencyKeyHash { get; set; }

    [JsonPropertyName("replayed")]
    public bool Replayed { get; set; }

    [JsonPropertyName("admittedAtUtc")]
    public DateTime AdmittedAtUtc { get; set; }

    [JsonPropertyName("statusUri")]
    public string StatusUri { get; set; } = string.Empty;

    [JsonPropertyName("resultUri")]
    public string? ResultUri { get; set; }

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public static class AdmissionStatuses
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}
