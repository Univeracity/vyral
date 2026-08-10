using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Cli;

public static class CliProviderTargets
{
    public const string DefaultCodexModelId = "gpt-5.3-codex-spark";
    public const string CodexModelId = "gpt-5.3-codex";
    public const string DefaultGeminiModelId = "gemini-2.5-flash-lite";
    public const string GeminiFlashModelId = "gemini-2.5-flash";
    public const string GeminiProModelId = "gemini-2.5-pro";
    public const string Gemini3FlashPreviewModelId = "gemini-3-flash-preview";
    public const string Gemini3ProPreviewModelId = "gemini-3-pro-preview";
    public const string Gemini31ProPreviewModelId = "gemini-3.1-pro-preview";
    public const string Gemini31FlashLitePreviewModelId = "gemini-3.1-flash-lite-preview";
    public const string Gemma4_31BModelId = "gemma-4-31b-it";
    public const string Gemma4_26BMoeModelId = "gemma-4-26b-a4b-it";
    public const string DefaultClaudeModelId = "claude-sonnet-4-5";
    public const string ClaudeOpus4ModelId = "claude-opus-4-8";
    public const string ClaudeHaiku4ModelId = "claude-haiku-4-5-20251001";

    /// <summary>
    /// Creates the explicit, Bubblewrap-backed <c>agent.workspace</c> target.
    /// It intentionally does not alter any advisory CLI target.
    /// </summary>
    public static WorkspaceCodingAgentProviderTarget CreateWorkspaceCodingAgent(
        CliWorkspaceCodingAgentOptions options,
        IProviderProcessRunner? processRunner = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var runner = new CliWorkspaceCodingAgentRunner(options, processRunner);
        return new WorkspaceCodingAgentProviderTarget(new WorkspaceCodingAgentProviderTargetOptions
        {
            ProviderId = options.ProviderId,
            DisplayName = options.DisplayName,
            Family = "workspace-cli",
            Local = true,
            RequiresNetwork = options.RequiresNetwork,
            Auth = options.Auth,
            ConfigIdentity = options.AgentProfile,
            AllowedWorkspaceRoots = options.AllowedWorkspaceRoots.ToList(),
            MaxOutputBytes = options.MaxOutputBytes,
            ModePolicies = new List<ProviderModePolicy>
            {
                new()
                {
                    Id = ProviderModes.Autonomous,
                    AllowedOutputKinds = new List<string> { ProviderOutputKinds.Action, ProviderOutputKinds.Patch, ProviderOutputKinds.Artifact, ProviderOutputKinds.Evidence },
                    MaxInputBytes = 96 * 1024,
                    MaxOutputBytes = options.MaxOutputBytes,
                    ToolPolicy = ProviderToolPolicies.HostEnforced,
                    AllowNetwork = options.RequiresNetwork,
                    AllowSourceWrites = true,
                    ReviewRequired = true,
                    TraceRequired = true,
                    TimeoutSeconds = 300
                }
            }
        }, runner);
    }

    public static CliProviderTarget CreateCodex(IProviderProcessRunner? runner = null, CliProviderOptions? overrides = null, ICodexAppServerQuotaClient? quotaClient = null)
    {
        var options = CreateBase(
            providerId: "codex-cli",
            displayName: "Codex CLI",
            command: "codex",
            arguments: new[] { "exec", "-m", "{model}", "{prompt}" },
            capabilities: new[] { ProviderCapabilityIds.AiChat, ProviderCapabilityIds.AiExtract, ProviderCapabilityIds.AiRerank, ProviderCapabilityIds.AiReview, ProviderCapabilityIds.AiScaffold, ProviderCapabilityIds.AiToolPlan });
        options.ModelId = DefaultCodexModelId;
        options.KnownModels = new List<string> { DefaultCodexModelId, CodexModelId };
        options.PromptTransport = CliPromptTransports.StandardInput;
        options.QuotaSource = CliQuotaSources.CodexAppServer;
        options.QuotaCommand = "codex";
        options.QuotaArguments = new List<string> { "app-server", "proxy" };
        options.QuotaWebSocketLaunchArguments = new List<string> { "app-server", "--listen", "ws://127.0.0.1:0" };
        options.QuotaTimeoutSeconds = 20;
        ApplyOverrides(options, overrides);
        return new CliProviderTarget(options, runner, quotaClient: quotaClient);
    }

    public static CliProviderTarget CreateClaude(IProviderProcessRunner? runner = null, CliProviderOptions? overrides = null)
    {
        var options = CreateBase(
            providerId: "claude-cli",
            displayName: "Claude CLI",
            command: "claude",
            arguments: new[] { "-p", "{prompt}", "{modelArgs}", "--permission-mode", "plan", "--tools", string.Empty, "--output-format", "json" },
            capabilities: new[] { ProviderCapabilityIds.AiChat, ProviderCapabilityIds.AiExtract, ProviderCapabilityIds.AiRerank, ProviderCapabilityIds.AiReview, ProviderCapabilityIds.AiScaffold, ProviderCapabilityIds.AiToolPlan });
        options.ModelId = DefaultClaudeModelId;
        options.KnownModels = new List<string> { DefaultClaudeModelId, ClaudeOpus4ModelId, ClaudeHaiku4ModelId };
        ApplyOverrides(options, overrides);
        return new CliProviderTarget(options, runner);
    }

    public static CliProviderTarget CreateGemini(IProviderProcessRunner? runner = null, CliProviderOptions? overrides = null)
    {
        var options = CreateBase(
            providerId: "gemini-cli",
            displayName: "Gemini CLI",
            command: "gemini",
            arguments: new[] { "--model", "{model}", "--approval-mode", "plan", "--output-format", "text", "--prompt", "{prompt}" },
            capabilities: new[] { ProviderCapabilityIds.AiChat, ProviderCapabilityIds.AiExtract, ProviderCapabilityIds.AiRerank, ProviderCapabilityIds.AiReview, ProviderCapabilityIds.AiScaffold, ProviderCapabilityIds.AiToolPlan });
        options.ModelId = DefaultGeminiModelId;
        options.KnownModels = new List<string>
        {
            DefaultGeminiModelId, GeminiFlashModelId, GeminiProModelId,
            Gemini3FlashPreviewModelId, Gemini3ProPreviewModelId,
            Gemini31ProPreviewModelId, Gemini31FlashLitePreviewModelId,
            Gemma4_31BModelId, Gemma4_26BMoeModelId
        };
        ApplyOverrides(options, overrides);
        return new CliProviderTarget(options, runner);
    }

    public static CliProviderTarget CreateAntigravity(IProviderProcessRunner? runner = null, CliProviderOptions? overrides = null)
    {
        var options = CreateBase(
            providerId: "antigravity-cli",
            displayName: "Antigravity",
            command: "agy",
            arguments: new[] { "--print", "{prompt}" },
            capabilities: new[] { ProviderCapabilityIds.AiChat, ProviderCapabilityIds.AiExtract, ProviderCapabilityIds.AiRerank, ProviderCapabilityIds.AiReview, ProviderCapabilityIds.AiScaffold, ProviderCapabilityIds.AiToolPlan });
        options.ModelId = DefaultGeminiModelId;
        options.KnownModels = new List<string>
        {
            DefaultGeminiModelId, GeminiFlashModelId, GeminiProModelId,
            Gemini3FlashPreviewModelId, Gemini3ProPreviewModelId,
            Gemini31ProPreviewModelId, Gemini31FlashLitePreviewModelId,
            Gemma4_31BModelId, Gemma4_26BMoeModelId
        };
        ApplyOverrides(options, overrides);
        return new CliProviderTarget(options, runner);
    }

    /// <summary>
    /// Creates the advisory-only Grok Build CLI target. Grok is not enabled for
    /// execution until an operator provides a dedicated directory, scrubbed
    /// environment, sandbox profile, deny rules, and prompt-file directory.
    /// This target never grants source-write authority; use the separately
    /// host-enforced workspace contract for that.
    /// </summary>
    public static CliProviderTarget CreateGrokBuild(IProviderProcessRunner? runner = null, GrokBuildProviderOptions? overrides = null)
    {
        overrides ??= new GrokBuildProviderOptions();
        var options = CreateBase(
            providerId: "grok-build-cli",
            displayName: "Grok Build CLI",
            command: string.IsNullOrWhiteSpace(overrides.Command) ? "grok" : overrides.Command,
            arguments: CreateGrokArguments(overrides),
            capabilities: new[] { ProviderCapabilityIds.AiChat, ProviderCapabilityIds.AiExtract, ProviderCapabilityIds.AiRerank, ProviderCapabilityIds.AiReview, ProviderCapabilityIds.AiScaffold, ProviderCapabilityIds.AiToolPlan });
        options.Family = "grok-build";
        options.ModelId = overrides.ModelId;
        options.KnownModels = overrides.KnownModels
            .Append(overrides.ModelId)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        options.WorkingDirectory = overrides.WorkingDirectory;
        options.PromptFileDirectory = overrides.PromptFileDirectory;
        options.Environment = overrides.Environment;
        options.ClearEnvironment = true;
        options.PromptTransport = CliPromptTransports.File;
        options.ToolPolicy = ProviderToolPolicies.ProviderOwned;
        options.TimeoutSeconds = overrides.TimeoutSeconds;
        options.MaxOutputBytes = overrides.MaxOutputBytes;
        options.Auth = overrides.Auth;
        options.RequireWorkingDirectory = true;
        options.RequireDedicatedEmptyWorkingDirectory = true;
        options.RequireClearedEnvironment = true;
        options.RequirePromptFileTransport = true;
        options.RequirePromptFileWithinWorkingDirectory = true;
        options.RequiredEnvironmentVariables = new List<string> { "HOME" };
        options.RequireExecutableIdentity = true;
        options.VersionArguments = new List<string> { "--version" };
        options.RequireVersionProbe = true;
        options.RequireSandboxProfile = true;
        options.RequiredSandboxProfile = overrides.SandboxProfile;
        options.RequireToolDenyRules = true;
        options.RequiredToolDenyRules = overrides.ToolDenyRules;
        if (!string.IsNullOrWhiteSpace(overrides.SandboxProfile))
        {
            options.ContainmentProbeArguments = new List<string> { "--sandbox", overrides.SandboxProfile.Trim(), "inspect", "--json" };
            options.RequireContainmentProbe = true;
        }
        return new CliProviderTarget(options, runner);
    }

    private static IReadOnlyList<string> CreateGrokArguments(GrokBuildProviderOptions options)
    {
        var arguments = new List<string> { "{modelArgs}" };
        if (!string.IsNullOrWhiteSpace(options.SandboxProfile))
        {
            arguments.Add("--sandbox");
            arguments.Add(options.SandboxProfile.Trim());
        }

        foreach (var denyRule in options.ToolDenyRules.Where(rule => !string.IsNullOrWhiteSpace(rule)))
        {
            arguments.Add("--deny");
            arguments.Add(denyRule.Trim());
        }

        arguments.AddRange(new[] { "--permission-mode", "plan", "--disable-web-search", "--no-subagents", "--no-memory", "--prompt-file", "{promptFile}" });
        return arguments;
    }

    private static CliProviderOptions CreateBase(string providerId, string displayName, string command, IEnumerable<string> arguments, IEnumerable<string> capabilities)
    {
        return new CliProviderOptions
        {
            ProviderId = providerId,
            DisplayName = displayName,
            Command = command,
            Arguments = arguments.ToList(),
            Capabilities = capabilities.ToList()
        };
    }

    private static void ApplyOverrides(CliProviderOptions options, CliProviderOptions? overrides)
    {
        if (overrides is null)
        {
            return;
        }

        options.ProviderId = string.IsNullOrWhiteSpace(overrides.ProviderId) ? options.ProviderId : overrides.ProviderId;
        options.DisplayName = string.IsNullOrWhiteSpace(overrides.DisplayName) ? options.DisplayName : overrides.DisplayName;
        options.Family = string.IsNullOrWhiteSpace(overrides.Family) ? options.Family : overrides.Family;
        options.Command = string.IsNullOrWhiteSpace(overrides.Command) ? options.Command : overrides.Command;
        options.Arguments = overrides.Arguments.Count == 0 ? options.Arguments : overrides.Arguments;
        options.WorkingDirectory = overrides.WorkingDirectory ?? options.WorkingDirectory;
        options.Environment = overrides.Environment.Count == 0 ? options.Environment : overrides.Environment;
        options.ClearEnvironment = overrides.ClearEnvironment;
        options.ModelId = string.IsNullOrWhiteSpace(overrides.ModelId) ? options.ModelId : overrides.ModelId.Trim();
        options.KnownModels = MergeKnownModels(options.KnownModels, overrides.KnownModels, options.ModelId);
        options.PromptTransport = string.IsNullOrWhiteSpace(overrides.PromptTransport) ? options.PromptTransport : NormalizePromptTransport(overrides.PromptTransport);
        options.PromptFileDirectory = overrides.PromptFileDirectory ?? options.PromptFileDirectory;
        options.ToolPolicy = string.IsNullOrWhiteSpace(overrides.ToolPolicy) ? options.ToolPolicy : overrides.ToolPolicy;
        options.TimeoutSeconds = overrides.TimeoutSeconds == 120 ? options.TimeoutSeconds : overrides.TimeoutSeconds;
        options.MaxOutputBytes = overrides.MaxOutputBytes == 128 * 1024 ? options.MaxOutputBytes : overrides.MaxOutputBytes;
        options.Capabilities = overrides.Capabilities.Count == 0 ? options.Capabilities : overrides.Capabilities;
        options.Auth = string.IsNullOrWhiteSpace(overrides.Auth) ? options.Auth : overrides.Auth;
        options.QuotaSource = string.IsNullOrWhiteSpace(overrides.QuotaSource) ? options.QuotaSource : overrides.QuotaSource;
        options.QuotaCommand = string.IsNullOrWhiteSpace(overrides.QuotaCommand) ? options.QuotaCommand : overrides.QuotaCommand;
        options.QuotaArguments = overrides.QuotaArguments.Count == 0 ? options.QuotaArguments : overrides.QuotaArguments;
        options.QuotaSocketPath = overrides.QuotaSocketPath ?? options.QuotaSocketPath;
        options.QuotaWebSocketUri = overrides.QuotaWebSocketUri ?? options.QuotaWebSocketUri;
        options.QuotaAutoStartWebSocket = overrides.QuotaAutoStartWebSocket;
        options.QuotaWebSocketLaunchArguments = overrides.QuotaWebSocketLaunchArguments.Count == 0 ? options.QuotaWebSocketLaunchArguments : overrides.QuotaWebSocketLaunchArguments;
        options.QuotaTimeoutSeconds = overrides.QuotaTimeoutSeconds == 5 ? options.QuotaTimeoutSeconds : overrides.QuotaTimeoutSeconds;
        options.QuotaMaxOutputBytes = overrides.QuotaMaxOutputBytes == 64 * 1024 ? options.QuotaMaxOutputBytes : overrides.QuotaMaxOutputBytes;
    }

    private static List<string> MergeKnownModels(IEnumerable<string> defaults, IEnumerable<string> overrides, string? configuredModel)
    {
        var models = defaults
            .Concat(overrides)
            .Append(configuredModel)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return models;
    }

    private static string NormalizePromptTransport(string promptTransport)
    {
        return promptTransport.Trim().ToLowerInvariant() switch
        {
            CliPromptTransports.Argument => CliPromptTransports.Argument,
            CliPromptTransports.StandardInput => CliPromptTransports.StandardInput,
            "standardinput" => CliPromptTransports.StandardInput,
            "standard-input" => CliPromptTransports.StandardInput,
            CliPromptTransports.File => CliPromptTransports.File,
            "prompt-file" => CliPromptTransports.File,
            _ => throw new ArgumentException("CLI prompt transport must be 'argument', 'stdin', or 'file'.", nameof(promptTransport))
        };
    }
}
