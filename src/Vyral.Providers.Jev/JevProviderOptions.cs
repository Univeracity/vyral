namespace Vyral.Providers.Jev;

public sealed class JevProviderOptions
{
    public string ProviderId { get; set; } = "jev-api";
    public string DisplayName { get; set; } = "TypeSafe AI Jev";
    public Uri BaseUri { get; set; } = new("https://api.typesafe.ai/");
    public string? ApiKey { get; set; }

    /// <summary>
    /// Exact model version (e.g. "jev-1.13.0"), not a rolling alias like "jev-latest". Readiness/doctor
    /// checks warn if this looks unpinned, since silent drift between qualification time and production
    /// time is exactly the risk a consumer doing its own downstream qualification cannot see or control.
    /// </summary>
    public string ModelId { get; set; } = "jev-1.13.0";

    public int TimeoutSeconds { get; set; } = 60;
    public int MaxOutputBytes { get; set; } = 64 * 1024;
}
