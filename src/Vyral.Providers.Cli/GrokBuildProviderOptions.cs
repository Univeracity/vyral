namespace Vyral.Providers.Cli;

/// <summary>
/// The deliberately narrow configuration surface for Vyral's advisory Grok
/// target. Every containment input is explicit; there is no inherited-workspace
/// or inherited-environment fallback.
/// </summary>
public sealed class GrokBuildProviderOptions
{
    public string Command { get; set; } = string.Empty;
    public string? ModelId { get; set; }
    public List<string> KnownModels { get; set; } = new();
    public string? WorkingDirectory { get; set; }
    public string? PromptFileDirectory { get; set; }
    public Dictionary<string, string?> Environment { get; set; } = new();
    public string? SandboxProfile { get; set; }
    public List<string> ToolDenyRules { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxOutputBytes { get; set; } = 128 * 1024;
    public string Auth { get; set; } = "external-cli";
}
