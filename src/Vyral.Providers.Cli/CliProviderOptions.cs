using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Cli;

public sealed class CliProviderOptions
{
    public string ProviderId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Family { get; set; } = "cli";
    public string Command { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = new();
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string?> Environment { get; set; } = new();
    /// <summary>Clears the inherited process environment before applying <see cref="Environment"/>.</summary>
    public bool ClearEnvironment { get; set; }
    public string? ModelId { get; set; }
    public List<string> KnownModels { get; set; } = new();
    public string PromptTransport { get; set; } = string.Empty;
    /// <summary>Directory used for short-lived prompt files when the file transport is selected.</summary>
    public string? PromptFileDirectory { get; set; }
    public string ToolPolicy { get; set; } = ProviderToolPolicies.CallerOwned;

    // These requirements are opt-in. They allow a provider adapter to fail closed
    // when its CLI's own containment settings have not been configured.
    public bool RequireWorkingDirectory { get; set; }
    public bool RequireDedicatedEmptyWorkingDirectory { get; set; }
    public bool RequireClearedEnvironment { get; set; }
    public bool RequirePromptFileTransport { get; set; }
    public bool RequirePromptFileWithinWorkingDirectory { get; set; }
    public List<string> RequiredEnvironmentVariables { get; set; } = new();
    public bool RequireExecutableIdentity { get; set; }
    public List<string> VersionArguments { get; set; } = new();
    public bool RequireVersionProbe { get; set; }
    public List<string> ContainmentProbeArguments { get; set; } = new();
    public bool RequireContainmentProbe { get; set; }
    public bool RequireSandboxProfile { get; set; }
    public string? RequiredSandboxProfile { get; set; }
    public bool RequireToolDenyRules { get; set; }
    public List<string> RequiredToolDenyRules { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxOutputBytes { get; set; } = 128 * 1024;
    public List<string> Capabilities { get; set; } = new();
    public string Auth { get; set; } = "external-cli";
    public string QuotaSource { get; set; } = string.Empty;
    public string QuotaCommand { get; set; } = string.Empty;
    public List<string> QuotaArguments { get; set; } = new();
    public string? QuotaSocketPath { get; set; }
    public string? QuotaWebSocketUri { get; set; }
    public bool QuotaAutoStartWebSocket { get; set; } = true;
    public List<string> QuotaWebSocketLaunchArguments { get; set; } = new();
    public int QuotaTimeoutSeconds { get; set; } = 5;
    public int QuotaMaxOutputBytes { get; set; } = 64 * 1024;
}

public static class CliPromptTransports
{
    public const string Argument = "argument";
    public const string StandardInput = "stdin";
    public const string File = "file";
}

public static class CliQuotaSources
{
    public const string CodexAppServer = "codex-app-server";
    public const string CodexAppServerProxy = "codex-app-server-proxy";
    public const string CodexAppServerWebSocket = "codex-app-server-websocket";
}
