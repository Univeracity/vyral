using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

/// <summary>
/// Non-secret evidence from an isolated CanonicalStore archive/restore and tenant-isolation probe.
/// Probe tenant ids, payload markers, hashes, connection details, and exception messages are never
/// included in this result.
/// </summary>
public sealed class CanonicalDataPlanePreflightResult
{
    public const string ProfileV1 = "vyral.canonical.data-plane-preflight.v1";

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = ProfileV1;

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = CanonicalPreflightCheckStatuses.Failed;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "isolated_ephemeral_tenants";

    [JsonPropertyName("checkedAtUtc")]
    public DateTime CheckedAtUtc { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("archiveChunkCount")]
    public int ArchiveChunkCount { get; set; }

    [JsonPropertyName("backupRestoreVerified")]
    public bool BackupRestoreVerified { get; set; }

    [JsonPropertyName("tenantIsolationVerified")]
    public bool TenantIsolationVerified { get; set; }

    [JsonPropertyName("cleanupVerified")]
    public bool CleanupVerified { get; set; }

    [JsonPropertyName("checks")]
    public List<CanonicalDataPlanePreflightCheck> Checks { get; set; } = new();
}

public sealed class CanonicalDataPlanePreflightCheck
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = CanonicalPreflightCheckStatuses.Failed;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public static class CanonicalPreflightCheckStatuses
{
    public const string Passed = "passed";
    public const string Failed = "failed";
}
