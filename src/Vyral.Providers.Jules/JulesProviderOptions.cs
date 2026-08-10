namespace Vyral.Providers.Jules;

public sealed class JulesProviderOptions
{
    public string ProviderId { get; set; } = "jules-api";
    public string DisplayName { get; set; } = "Jules API";
    public Uri BaseUri { get; set; } = new("https://jules.googleapis.com/v1alpha/");
    public string? ApiKey { get; set; }
    public string? Source { get; set; }
    public string StartingBranch { get; set; } = "master";
    public string? QualificationSessionId { get; set; }
    public string? DefaultAutomationMode { get; set; }
    public bool RequirePlanApproval { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxOutputBytes { get; set; } = 128 * 1024;
}
