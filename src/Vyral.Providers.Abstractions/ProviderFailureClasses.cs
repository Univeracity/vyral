namespace Vyral.Providers.Abstractions;

public static class ProviderFailureClasses
{
    public const string Auth = "auth";
    public const string Quota = "quota";
    public const string RateLimit = "rate_limit";
    public const string Timeout = "timeout";
    public const string Cancelled = "cancelled";
    public const string Network = "network";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string Schema = "schema";
    public const string Tool = "tool";
    public const string Trust = "trust";
    public const string Policy = "policy";
    public const string Unsupported = "unsupported";
    public const string Configuration = "configuration";
    public const string Unknown = "unknown";
}
