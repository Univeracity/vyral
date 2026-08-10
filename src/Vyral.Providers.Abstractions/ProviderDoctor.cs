using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public interface IProviderDoctor
{
    Task<ProviderDoctorResult> DiagnoseAsync(CancellationToken ct = default);
}

public sealed class ProviderDoctorResult
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = ProviderDoctorStatuses.Unknown;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("checks")]
    public List<ProviderDoctorCheck> Checks { get; set; } = new();

    [JsonPropertyName("metadata")]
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

public sealed class ProviderDoctorCheck
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = ProviderDoctorStatuses.Unknown;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public Dictionary<string, object?> Details { get; set; } = new();
}

public static class ProviderDoctorStatuses
{
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Failed = "failed";
    public const string Unknown = "unknown";

    public static string Aggregate(IEnumerable<ProviderDoctorCheck> checks)
    {
        var statuses = checks.Select(check => check.Status).ToList();
        if (statuses.Any(status => string.Equals(status, Failed, StringComparison.OrdinalIgnoreCase)))
        {
            return Failed;
        }

        if (statuses.Any(status => string.Equals(status, Warning, StringComparison.OrdinalIgnoreCase)))
        {
            return Warning;
        }

        return statuses.Count == 0 ? Unknown : Ok;
    }
}
