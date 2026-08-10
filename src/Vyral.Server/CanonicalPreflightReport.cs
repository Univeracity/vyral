using Vyral.Abstractions.Models;

namespace Vyral.Server;

/// <summary>
/// Non-secret deployment evidence for an administrator before a CanonicalStore rollout. It is
/// intentionally operational rather than provider-specific: cloud IAM and network probes belong
/// to the configured identity/authentication mechanism, while this reports Vyral's own boundary.
/// </summary>
public sealed class CanonicalPreflightReport
{
    public required string Store { get; init; }
    public required string Provider { get; init; }
    public required string AuthenticationMode { get; init; }
    public required bool TenantPoliciesEnforced { get; init; }
    public required int IdentityPolicyCount { get; init; }
    public required int MigrationReceiptCount { get; init; }
    public required int RateLimitPermitLimit { get; init; }
    public required int RateLimitWindowSeconds { get; init; }
    public required IReadOnlyList<string> RequiredOperations { get; init; }

    /// <summary>
    /// Present only for the explicit POST data-plane probe. The passive GET remains read-only and
    /// reports configuration/ledger evidence without creating probe tenants.
    /// </summary>
    public CanonicalDataPlanePreflightResult? DataPlaneProbe { get; init; }
}
