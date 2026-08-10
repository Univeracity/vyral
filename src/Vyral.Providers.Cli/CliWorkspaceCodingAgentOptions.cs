using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Cli;

/// <summary>
/// Configuration for the concrete Bubblewrap-backed CLI workspace host. The
/// operator supplies the minimal read-only runtime closure required by the
/// selected coding-agent profile; Vyral never bind-mounts the host root or home
/// directory into an agent sandbox.
/// </summary>
public sealed class CliWorkspaceCodingAgentOptions
{
    public string ProviderId { get; set; } = "workspace-cli";
    public string DisplayName { get; set; } = "Workspace CLI coding agent";
    public string AgentProfile { get; set; } = "configured-cli";
    public string AgentCommand { get; set; } = string.Empty;
    public List<string> AgentArguments { get; set; } = new();
    public string PromptTransport { get; set; } = CliPromptTransports.StandardInput;
    public string? ModelId { get; set; }
    public Dictionary<string, string?> Environment { get; set; } = new();
    public List<string> AllowedWorkspaceRoots { get; set; } = new();
    public string StagingRoot { get; set; } = string.Empty;
    public string BubblewrapCommand { get; set; } = "bwrap";
    public string GitCommand { get; set; } = "git";

    /// <summary>
    /// Read-only runtime files and directories mounted at their original paths.
    /// Do not include host roots, homes, or executable directories; individual
    /// agent and allowed tool executables are mounted separately.
    /// </summary>
    public List<string> RuntimeReadOnlyPaths { get; set; } = new();

    /// <summary>Host directories searched for bare tool names declared by a request.</summary>
    public List<string> ToolSearchPaths { get; set; } = new();

    public int MaxOutputBytes { get; set; } = 128 * 1024;
    public int PreparationTimeoutSeconds { get; set; } = 30;
    public bool RequiresNetwork { get; set; }
    public string Auth { get; set; } = ProviderAuthTypes.ExternalCli;
}
