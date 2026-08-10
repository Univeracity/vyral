using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Cli;

public static class CliFailureClassifier
{
    public static string Classify(ProviderProcessRunResult result)
    {
        if (result.TimedOut)
        {
            return ProviderFailureClasses.Timeout;
        }

        if (result.Cancelled)
        {
            return ProviderFailureClasses.Cancelled;
        }

        if (!string.IsNullOrWhiteSpace(result.StartError))
        {
            return ProviderFailureClasses.Configuration;
        }

        var text = $"{result.StandardError}\n{result.StandardOutput}".ToLowerInvariant();
        if (text.Contains("unauthorized") || text.Contains("authentication") || text.Contains("api key") || text.Contains("login"))
        {
            return ProviderFailureClasses.Auth;
        }

        if (text.Contains("quota") || text.Contains("credit") || text.Contains("billing"))
        {
            return ProviderFailureClasses.Quota;
        }

        if (text.Contains("rate limit") || text.Contains("too many requests") || text.Contains("throttl"))
        {
            return ProviderFailureClasses.RateLimit;
        }

        if (text.Contains("content policy") ||
            text.Contains("policy violation") ||
            text.Contains("policy rejected") ||
            text.Contains("safety policy") ||
            text.Contains("safety filter") ||
            text.Contains("content filter"))
        {
            return ProviderFailureClasses.Policy;
        }

        if (text.Contains("stream disconnected") ||
            text.Contains("disconnected before completion") ||
            text.Contains("connection reset") ||
            text.Contains("connection refused") ||
            text.Contains("socket hang up") ||
            text.Contains("server disconnected") ||
            text.Contains("reconnect") ||
            text.Contains("network error") ||
            text.Contains("temporary failure"))
        {
            return ProviderFailureClasses.Network;
        }

        if (text.Contains("trust") || text.Contains("approval") || text.Contains("permission"))
        {
            return ProviderFailureClasses.Trust;
        }

        return result.ExitCode == 0 ? ProviderFailureClasses.Unknown : ProviderFailureClasses.ProviderUnavailable;
    }
}
