using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

/// <summary>
/// A provider-neutral coding-agent request for an already checked-out workspace.
/// Source mutation is permitted only through a target whose host enforces this
/// request. A prompt-only path or command restriction is not sufficient.
/// </summary>
public sealed class WorkspaceCodingAgentRequest
{
    [JsonPropertyName("task")]
    public string Task { get; set; } = string.Empty;

    /// <summary>Absolute root of the checked-out workspace available to the host.</summary>
    [JsonPropertyName("workspaceRoot")]
    public string WorkspaceRoot { get; set; } = string.Empty;

    /// <summary>
    /// Must be <see cref="WorkspaceCodingAgentWriteModes.Write"/>. A separate
    /// field makes write authority visible in every durable request and trace.
    /// </summary>
    [JsonPropertyName("writeMode")]
    public string WriteMode { get; set; } = WorkspaceCodingAgentWriteModes.ReadOnly;

    /// <summary>Relative workspace paths the agent may access or alter. A single "." is explicit whole-workspace authority.</summary>
    [JsonPropertyName("allowedPaths")]
    public List<string> AllowedPaths { get; set; } = new();

    private List<string>? _newFilePaths;

    /// <summary>
    /// Opt-in exact files to create. Each must be an absent canonical relative
    /// allowed path with an existing real parent. Omission keeps legacy scope.
    /// A supporting host reconciles the whole private stage before validation.
    /// </summary>
    [JsonPropertyName("newFilePaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? NewFilePaths
    {
        get => _newFilePaths;
        set { NewFilePathsSpecified = true; _newFilePaths = value; }
    }

    [JsonIgnore]
    public bool NewFilePathsSpecified { get; private set; }

    /// <summary>Relative workspace paths the agent must not alter; these override allowed paths.</summary>
    [JsonPropertyName("avoidedPaths")]
    public List<string> AvoidedPaths { get; set; } = new();

    [JsonPropertyName("toolPolicy")]
    public WorkspaceToolPolicy ToolPolicy { get; set; } = new();

    /// <summary>
    /// Commands the host must run after the agent work, in order. Every command
    /// must also be present in <see cref="WorkspaceToolPolicy.AllowedCommands"/>.
    /// Commands are tokenized rather than shell strings.
    /// </summary>
    [JsonPropertyName("validationCommands")]
    public List<WorkspaceCommand> ValidationCommands { get; set; } = new();
}

public static class WorkspaceCodingAgentWriteModes
{
    public const string ReadOnly = "read_only";
    public const string Write = "write";
}

public static class WorkspaceToolPolicyEnforcements
{
    /// <summary>The host isolates the workspace and enforces commands before they execute.</summary>
    public const string HostEnforced = "host_enforced";

    /// <summary>
    /// Reserved for proposal-only adapters. It is not accepted for a write-capable
    /// workspace coding-agent target.
    /// </summary>
    public const string AuditedOnly = "audited_only";
}

public sealed class WorkspaceToolPolicy
{
    /// <summary>
    /// Write-capable targets accept only <see cref="WorkspaceToolPolicyEnforcements.HostEnforced"/>.
    /// This prevents an adapter from claiming that instructions in a prompt are a sandbox.
    /// </summary>
    [JsonPropertyName("enforcement")]
    public string Enforcement { get; set; } = WorkspaceToolPolicyEnforcements.AuditedOnly;

    [JsonPropertyName("allowNetwork")]
    public bool AllowNetwork { get; set; }

    /// <summary>
    /// Maximum number of declared commands the host may execute. It bounds the
    /// host-run validation phase; the selected coding-agent runtime remains
    /// constrained by the sandbox filesystem and network boundary.
    /// </summary>
    [JsonPropertyName("maxCommands")]
    public int MaxCommands { get; set; }

    [JsonPropertyName("allowedCommands")]
    public List<WorkspaceCommand> AllowedCommands { get; set; } = new();
}

/// <summary>
/// One tokenized command declaration. The host must execute the file name and
/// arguments directly rather than interpreting a shell command string.
/// </summary>
public sealed class WorkspaceCommand
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = new();
}

public sealed class WorkspaceCodingAgentResult
{
    /// <summary>Git commit captured by the host immediately before the agent starts.</summary>
    [JsonPropertyName("baseCommit")]
    public string BaseCommit { get; set; } = string.Empty;

    /// <summary>
    /// True only after the host has compared the current workspace with
    /// <see cref="BaseCommit"/> and included every changed or untracked path.
    /// </summary>
    [JsonPropertyName("changeSetReconciled")]
    public bool ChangeSetReconciled { get; set; }

    [JsonPropertyName("changedPaths")]
    public List<WorkspaceChangedPath> ChangedPaths { get; set; } = new();

    [JsonPropertyName("validation")]
    public List<WorkspaceValidationResult> Validation { get; set; } = new();

    [JsonPropertyName("toolPolicyEnforcement")]
    public string ToolPolicyEnforcement { get; set; } = string.Empty;

    /// <summary>
    /// Every declared validation command the host executed, identified by its
    /// command id. This gives the caller evidence that the bounded host command
    /// budget was reconciled rather than merely requested.
    /// </summary>
    [JsonPropertyName("executedCommandIds")]
    public List<string> ExecutedCommandIds { get; set; } = new();

    /// <summary>Short, token-redacted runner summary. It is not a substitute for the change set.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    /// <summary>Bounded agent acknowledgment of exact stdin task delivery; not model attention or independent metering.</summary>
    [JsonPropertyName("agentDelivery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceAgentDelivery? AgentDelivery { get; set; }
}

public sealed class WorkspaceAgentDelivery
{
    public string SchemaVersion { get; set; } = "vyral.workspace-agent-delivery.v1";
    public string Source { get; set; } = "agent_stdout";
    public string State { get; set; } = "acknowledged";
    public bool TaskBindingVerified { get; set; } = true;
    public string TaskSha256 { get; set; } = string.Empty;
    public string ProfileSha256 { get; set; } = string.Empty;
    public string RequestSha256 { get; set; } = string.Empty;
    public string ResultSha256 { get; set; } = string.Empty;
    public string OutcomeSha256 { get; set; } = string.Empty;
}

/// <summary>
/// A host-filtered observation from a failed agent's stderr. The reason is not
/// independently verified and establishes no provider effects, usage, delivery,
/// acceptance or permission to resend. Unknown reasons are not projected.
/// </summary>
public sealed class WorkspaceAgentDiagnostic
{
    public string SchemaVersion => "vyral.workspace-agent-diagnostic.v1";
    public string Source => "agent_stderr";
    public string State => "held";
    public string Reason { get; init; } = string.Empty;
    public bool ResendAuthorized => false;
    public string ProviderEffects => "unresolved";

    private static readonly HashSet<string> KnownReasons = new(StringComparer.Ordinal)
    {
        "absolute_private_path_required", "anchor_not_unique", "anchor_outside_context", "base_binding",
        "context_binding", "context_limit", "context_read_manifest", "context_read_overlap", "context_window",
        "context_window_scope", "credential_in_response", "deadline", "duplicate_json_key", "edit_limit",
        "edit_precondition", "gateway_held_or_deadline", "host_input_limit", "input_limit", "invalid_binding",
        "invalid_context", "invalid_context_read_scope", "invalid_credential", "invalid_digest", "invalid_edit",
        "invalid_edits", "invalid_gateway_response", "invalid_invocation_deadline", "invalid_json", "invalid_limit",
        "invalid_path", "invalid_profile", "invalid_response_framing", "invalid_scope", "invocation_binding",
        "invocation_deadline_required", "invocation_not_admitted", "invocation_unresolved", "model_binding",
        "overlapping_context", "overlapping_edits", "overlapping_replacements", "owner_binding", "path_ancestor",
        "path_scope", "private_file_limit", "private_file_required", "private_socket_required", "provider_binding",
        "provider_config_binding_required", "readable_prompt_requires_windows", "regular_file_required",
        "reservation_limit", "response_binding", "response_limit", "symlink", "task_binding", "tcp_fallback_denied",
        "unsupported_host_input", "unsupported_model_output", "unsupported_profile", "unsupported_prompt_format",
        "unsupported_reservation", "unsupported_response_framing", "usage_bound", "usage_unknown",
        "windowed_patch_required", "workspace_binding", "workspace_host_required", "private_ipc_or_input_failure"
    };

    public static bool IsKnownReason(string? reason) => reason is not null && KnownReasons.Contains(reason);
}

public sealed class WorkspaceChangedPath
{
    /// <summary>Normalized relative path from the workspace root.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>One of added, modified, deleted, renamed, or untracked.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;
}

public sealed class WorkspaceValidationResult
{
    [JsonPropertyName("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
}

public static class WorkspaceValidationStatuses
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

/// <summary>Shared validation for workspace-coding-agent request and result envelopes.</summary>
public static class WorkspaceCodingAgentContract
{
    private static readonly HashSet<string> ChangedPathKinds = new(StringComparer.Ordinal)
    {
        "added", "modified", "deleted", "renamed", "untracked"
    };

    public static void ValidateRequest(WorkspaceCodingAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Task))
        {
            throw new ArgumentException("Workspace coding-agent task is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot) || !Path.IsPathRooted(request.WorkspaceRoot))
        {
            throw new ArgumentException("Workspace coding-agent workspaceRoot must be an absolute path.", nameof(request));
        }

        if (!string.Equals(request.WriteMode, WorkspaceCodingAgentWriteModes.Write, StringComparison.Ordinal))
        {
            throw new ArgumentException("Workspace coding-agent runs require explicit writeMode 'write'.", nameof(request));
        }

        if (request.AllowedPaths.Count == 0)
        {
            throw new ArgumentException("Workspace coding-agent runs require at least one allowed path.", nameof(request));
        }

        var allowedPaths = request.AllowedPaths.Select(NormalizeRelativePath).ToList();
        var avoidedPaths = request.AvoidedPaths.Select(NormalizeRelativePath).ToList();
        ValidateNewFilePaths(request);
        if (allowedPaths.Concat(avoidedPaths).Any(path => string.Equals(path, ".git", StringComparison.Ordinal) || path.StartsWith(".git/", StringComparison.Ordinal)))
        {
            throw new ArgumentException("The Git metadata directory is always protected and must not be declared.", nameof(request));
        }

        if (!string.Equals(request.ToolPolicy.Enforcement, WorkspaceToolPolicyEnforcements.HostEnforced, StringComparison.Ordinal))
        {
            throw new ArgumentException("Write-capable workspace coding-agent runs require host_enforced tool policy.", nameof(request));
        }

        if (request.ToolPolicy.MaxCommands <= 0)
        {
            throw new ArgumentException("Workspace coding-agent toolPolicy.maxCommands must be positive.", nameof(request));
        }

        if (request.ToolPolicy.AllowedCommands.Count == 0)
        {
            throw new ArgumentException("Workspace coding-agent toolPolicy requires at least one allowed command.", nameof(request));
        }

        if (request.ValidationCommands.Count == 0)
        {
            throw new ArgumentException("Workspace coding-agent runs require at least one declared validation command.", nameof(request));
        }

        var allowedCommands = ValidateCommands(request.ToolPolicy.AllowedCommands, "toolPolicy.allowedCommands");
        var validationCommands = ValidateCommands(request.ValidationCommands, "validationCommands");
        if (!validationCommands.Keys.ToHashSet(StringComparer.Ordinal).IsSubsetOf(allowedCommands.Keys))
        {
            throw new ArgumentException("Each validation command must also be declared in toolPolicy.allowedCommands.", nameof(request));
        }

        if (validationCommands.Count > request.ToolPolicy.MaxCommands)
        {
            throw new ArgumentException("Workspace coding-agent command budget cannot be smaller than its declared validation commands.", nameof(request));
        }

        foreach (var (id, validation) in validationCommands)
        {
            var allowed = allowedCommands[id];
            if (!string.Equals(validation.FileName, allowed.FileName, StringComparison.Ordinal) ||
                !validation.Arguments.SequenceEqual(allowed.Arguments, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Validation command '{id}' must exactly match its toolPolicy.allowedCommands declaration.", nameof(request));
            }
        }
    }

    public static void ValidateResult(WorkspaceCodingAgentRequest request, WorkspaceCodingAgentResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (!IsGitCommit(result.BaseCommit))
        {
            throw new ArgumentException("Workspace coding-agent result must contain the captured Git baseCommit.", nameof(result));
        }

        if (result.AgentDelivery is { } delivery)
        {
            bool Digest(string value) => value is not null && value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal)
                && value[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
            var taskHash = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Task))).ToLowerInvariant();
            if (delivery.SchemaVersion != "vyral.workspace-agent-delivery.v1" || delivery.Source != "agent_stdout" ||
                delivery.State != "acknowledged" || !delivery.TaskBindingVerified || delivery.TaskSha256 != taskHash ||
                !new[] { delivery.TaskSha256, delivery.ProfileSha256, delivery.RequestSha256, delivery.ResultSha256, delivery.OutcomeSha256 }.All(Digest))
                throw new ArgumentException("Workspace delivery acknowledgment is invalid or differs from the task.", nameof(result));
        }

        if (!result.ChangeSetReconciled)
        {
            throw new ArgumentException("Workspace coding-agent result must reconcile changed paths against its baseCommit.", nameof(result));
        }

        if (!string.Equals(result.ToolPolicyEnforcement, WorkspaceToolPolicyEnforcements.HostEnforced, StringComparison.Ordinal))
        {
            throw new ArgumentException("Workspace coding-agent result did not confirm host-enforced tool policy.", nameof(result));
        }

        if (result.ExecutedCommandIds.Count > request.ToolPolicy.MaxCommands)
        {
            throw new ArgumentException("Workspace coding-agent result exceeds the declared command budget.", nameof(result));
        }

        var allowedCommandIds = request.ToolPolicy.AllowedCommands.Select(command => command.Id).ToHashSet(StringComparer.Ordinal);
        if (result.ExecutedCommandIds.Any(id => !allowedCommandIds.Contains(id)))
        {
            throw new ArgumentException("Workspace coding-agent result reports an undeclared executed command.", nameof(result));
        }

        var allowedPaths = request.AllowedPaths.Select(NormalizeRelativePath).ToList();
        var avoidedPaths = request.AvoidedPaths.Select(NormalizeRelativePath).ToList();
        foreach (var changed in result.ChangedPaths)
        {
            var path = NormalizeRelativePath(changed.Path);
            if (path == ".git" || path.StartsWith(".git/", StringComparison.Ordinal))
            {
                throw new ArgumentException("Workspace coding-agent result includes protected Git metadata.", nameof(result));
            }

            if (!ChangedPathKinds.Contains(changed.Kind))
            {
                throw new ArgumentException($"Workspace coding-agent result has unsupported changed-path kind '{changed.Kind}'.", nameof(result));
            }

            if (!allowedPaths.Any(allowed => request.NewFilePaths?.Contains(allowed, StringComparer.Ordinal) == true ? path == allowed : IsWithin(path, allowed)))
            {
                throw new ArgumentException($"Workspace coding-agent changed path '{path}' is outside the allowed paths.", nameof(result));
            }

            if (avoidedPaths.Any(avoided => IsWithin(path, avoided)))
            {
                throw new ArgumentException($"Workspace coding-agent changed path '{path}' is within an avoided path.", nameof(result));
            }
        }

        var commandIds = request.ValidationCommands.Select(command => command.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var validation in result.Validation)
        {
            if (!commandIds.Contains(validation.CommandId))
            {
                throw new ArgumentException($"Workspace coding-agent result reports undeclared validation command '{validation.CommandId}'.", nameof(result));
            }

            if (validation.Status is not (WorkspaceValidationStatuses.Passed or WorkspaceValidationStatuses.Failed or WorkspaceValidationStatuses.Skipped))
            {
                throw new ArgumentException($"Workspace coding-agent result has unsupported validation status '{validation.Status}'.", nameof(result));
            }
        }

        var reportedValidationIds = result.Validation.Select(validation => validation.CommandId).ToHashSet(StringComparer.Ordinal);
        if (result.Validation.Count != reportedValidationIds.Count || !commandIds.SetEquals(reportedValidationIds))
        {
            throw new ArgumentException("Workspace coding-agent result must report every declared validation command exactly once.", nameof(result));
        }

        var executedValidationIds = result.Validation
            .Where(validation => validation.Status != WorkspaceValidationStatuses.Skipped)
            .Select(validation => validation.CommandId)
            .ToHashSet(StringComparer.Ordinal);
        if (!executedValidationIds.IsSubsetOf(result.ExecutedCommandIds))
        {
            throw new ArgumentException("Workspace coding-agent result must account for every non-skipped validation command as executed.", nameof(result));
        }
    }

    /// <summary>Lexical authority check; absence and real parents belong to the concrete host.</summary>
    public static void ValidateNewFilePaths(WorkspaceCodingAgentRequest request)
    {
        if (!request.NewFilePathsSpecified) return;
        if (request.NewFilePaths is not { Count: >= 1 and <= 32 } paths)
            throw new ArgumentException("newFilePaths must contain between one and 32 exact file paths.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (path is null || System.Text.Encoding.UTF8.GetByteCount(path) > 512 || path.Any(char.IsControl) ||
                path.Contains('\\') || path == "." || NormalizeRelativePath(path) != path ||
                path.Split('/').Contains(".git", StringComparer.Ordinal) || !seen.Add(path) ||
                request.AllowedPaths.Count(allowed => allowed == path) != 1 || request.AllowedPaths.Select(NormalizeRelativePath).Count(allowed => allowed == path) != 1)
                throw new ArgumentException("newFilePaths requires unique canonical exact allowed file paths.");
            if (request.AllowedPaths.Select(NormalizeRelativePath).Any(other => other != path && (IsWithin(path, other) || IsWithin(other, path))) ||
                request.AvoidedPaths.Select(NormalizeRelativePath).Any(other => IsWithin(path, other) || IsWithin(other, path)))
                throw new ArgumentException("newFilePaths must not overlap another allowed or avoided path.");
        }
    }

    private static Dictionary<string, WorkspaceCommand> ValidateCommands(IEnumerable<WorkspaceCommand> commands, string field)
    {
        var commandsById = new Dictionary<string, WorkspaceCommand>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command.Id) || !commandsById.TryAdd(command.Id, command))
            {
                throw new ArgumentException($"Workspace coding-agent {field} requires unique command ids.");
            }

            if (string.IsNullOrWhiteSpace(command.FileName) ||
                Path.IsPathRooted(command.FileName) ||
                command.FileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                throw new ArgumentException($"Workspace coding-agent {field} command '{command.Id}' must use a bare executable name.");
            }
        }

        return commandsById;
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Workspace paths must not be empty.");
        }

        var normalized = value.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is ".."))
        {
            throw new ArgumentException($"Workspace path '{value}' must be relative and must not contain '..'.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();
        return segments.Length == 0 ? "." : string.Join('/', segments);
    }

    private static bool IsWithin(string path, string boundary) =>
        boundary == "." ||
        string.Equals(path, boundary, StringComparison.Ordinal) ||
        path.StartsWith(boundary + "/", StringComparison.Ordinal);

    private static bool IsGitCommit(string value) =>
        value.Length is >= 7 and <= 64 && value.All(character => char.IsAsciiHexDigit(character));
}
