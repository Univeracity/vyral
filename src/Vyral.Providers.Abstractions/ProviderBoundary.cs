namespace Vyral.Providers.Abstractions;

public static class ProviderBoundary
{
    public const string AuthorityBoundary =
        "Provider output is proposal or evidence only. It does not authorize source writes, production readiness, governance satisfaction, or final acceptance.";

    public static readonly string[] DoesNotAuthorize =
    {
        "mutate_source",
        "promote_output",
        "satisfy_governance_invariant",
        "claim_readiness",
        "widen_scope",
        "execute_tools"
    };
}
