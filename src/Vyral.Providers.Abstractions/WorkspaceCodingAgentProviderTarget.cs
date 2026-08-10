using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Vyral.Providers.Abstractions;

/// <summary>
/// A host implementation for the workspace coding-agent contract. The host owns
/// isolation, tool execution, Git capture, and change-set reconciliation; Vyral
/// validates the evidence before returning a successful provider run.
/// </summary>
public interface IWorkspaceCodingAgentRunner
{
    string AdapterId { get; }
    Task<WorkspaceCodingAgentExecution> RunAsync(WorkspaceCodingAgentExecutionRequest request, CancellationToken ct = default);
}

public sealed class WorkspaceCodingAgentExecutionRequest
{
    public WorkspaceCodingAgentRequest Request { get; init; } = new();
    public string? ModelId { get; init; }
    public TimeSpan Timeout { get; init; }
    public int MaxOutputBytes { get; init; }
}

public sealed class WorkspaceCodingAgentExecution
{
    public ProviderRunStatus Status { get; init; } = ProviderRunStatus.Succeeded;
    public WorkspaceCodingAgentResult? Result { get; init; }
    public string? FailureClass { get; init; }
    public string? ProviderStatus { get; init; }
    public string? Error { get; init; }
}

public interface IWorkspaceCodingAgentTarget : IProviderTarget
{
    Task<WorkspaceCodingAgentResult> RunWorkspaceAsync(
        WorkspaceCodingAgentRequest request,
        string? modelId = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default);
}

public sealed class WorkspaceCodingAgentProviderTargetOptions
{
    public string ProviderId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Family { get; set; } = "workspace-coding-agent";
    public bool Local { get; set; } = true;
    public bool RequiresNetwork { get; set; }
    public string Auth { get; set; } = ProviderAuthTypes.None;
    public string? ConfigIdentity { get; set; }
    /// <summary>
    /// Absolute roots mounted for this target. The host must still canonicalize
    /// paths and enforce this boundary against symlink escape.
    /// </summary>
    public List<string> AllowedWorkspaceRoots { get; set; } = new();
    public int MaxOutputBytes { get; set; } = 128 * 1024;
    public List<ProviderModePolicy> ModePolicies { get; set; } = new()
    {
        new ProviderModePolicy
        {
            Id = ProviderModes.Autonomous,
            AllowedOutputKinds = new List<string> { ProviderOutputKinds.Action, ProviderOutputKinds.Patch, ProviderOutputKinds.Artifact, ProviderOutputKinds.Evidence },
            MaxInputBytes = 96 * 1024,
            MaxOutputBytes = 128 * 1024,
            ToolPolicy = ProviderToolPolicies.HostEnforced,
            AllowNetwork = false,
            AllowSourceWrites = true,
            ReviewRequired = true,
            TraceRequired = true,
            TimeoutSeconds = 300
        }
    };
}

/// <summary>
/// Explicit, write-capable provider target. This is not used by advisory CLI
/// providers: a concrete host must implement <see cref="IWorkspaceCodingAgentRunner"/>
/// and prove its bounded execution in the returned change set.
/// </summary>
public sealed class WorkspaceCodingAgentProviderTarget : IWorkspaceCodingAgentTarget
{
    private readonly IWorkspaceCodingAgentRunner _runner;
    private readonly WorkspaceCodingAgentProviderTargetOptions _options;
    private readonly IReadOnlyDictionary<string, ProviderModePolicy> _policies;

    public WorkspaceCodingAgentProviderTarget(WorkspaceCodingAgentProviderTargetOptions options, IWorkspaceCodingAgentRunner runner)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        if (string.IsNullOrWhiteSpace(options.ProviderId))
        {
            throw new ArgumentException("Workspace coding-agent provider id is required.", nameof(options));
        }

        if (options.MaxOutputBytes <= 0)
        {
            throw new ArgumentException("Workspace coding-agent max output bytes must be positive.", nameof(options));
        }

        if (options.AllowedWorkspaceRoots.Count == 0 || options.AllowedWorkspaceRoots.Any(root => string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)))
        {
            throw new ArgumentException("Workspace coding-agent targets require one or more absolute allowed workspace roots.", nameof(options));
        }

        _runner = runner;
        _options = options;
        _policies = ProviderModePolicies.Index(options.ModePolicies);
        Profile = new ProviderProfile
        {
            Id = options.ProviderId,
            DisplayName = string.IsNullOrWhiteSpace(options.DisplayName) ? options.ProviderId : options.DisplayName,
            Family = options.Family,
            Local = options.Local,
            RequiresNetwork = options.RequiresNetwork,
            Auth = options.Auth,
            ConfigHash = ProviderHash.Sha256($"{options.ProviderId}|{options.Family}|{options.Local}|{options.RequiresNetwork}|{options.Auth}|{options.ConfigIdentity}|{string.Join('|', options.AllowedWorkspaceRoots.Select(Path.GetFullPath).OrderBy(root => root, StringComparer.Ordinal))}|{runner.AdapterId}")
        };
        Capabilities = new List<ProviderCapabilityDescriptor>
        {
            new()
            {
                Id = ProviderCapabilityIds.AgentWorkspace,
                Operations = new List<string> { "run" },
                ToolPolicy = ProviderToolPolicies.HostEnforced,
                InputLimits = new Dictionary<string, object?>
                {
                    ["maxPromptBytes"] = _policies.Values.Max(policy => policy.MaxInputBytes),
                    ["pathPolicy"] = "host_enforced",
                    ["commandPolicy"] = "host_enforced",
                    ["changeSet"] = "base_commit_reconciled"
                },
                OutputLimits = new Dictionary<string, object?> { ["maxOutputBytes"] = options.MaxOutputBytes },
                ModePolicies = _policies.Values.OrderBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                UnsupportedFeatures = new List<string> { "unbounded_workspace_access", "unbounded_tool_execution", "direct_commit", "direct_push" }
            }
        };
    }

    public ProviderProfile Profile { get; }
    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }

    public async Task<WorkspaceCodingAgentResult> RunWorkspaceAsync(
        WorkspaceCodingAgentRequest request,
        string? modelId = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        var result = await RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(request, Profile.Id, modelId, timeoutSeconds: timeoutSeconds), ct);
        return ProviderRunResults.GetWorkspaceCodingAgent(result);
    }

    public async Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var trace = CreateTrace(request);
        if (!string.Equals(request.Capability, ProviderCapabilityIds.AgentWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            return CreateResult(request, ProviderRunStatus.Unsupported, trace, stopwatch.Elapsed, ProviderFailureClasses.Unsupported, "unsupported_capability", "This target only supports agent.workspace.");
        }

        if (!string.Equals(request.Operation, "run", StringComparison.OrdinalIgnoreCase))
        {
            return CreateResult(request, ProviderRunStatus.Unsupported, trace, stopwatch.Elapsed, ProviderFailureClasses.Unsupported, "unsupported_operation", "This target only supports the run operation.");
        }

        if (!string.Equals(request.Mode, ProviderModes.Autonomous, StringComparison.OrdinalIgnoreCase))
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, ProviderFailureClasses.Policy, "write_mode_required", "Workspace coding-agent runs require autonomous mode.");
        }

        var policy = ProviderModePolicies.Resolve(_policies, request.Mode);
        if (policy is null || !policy.AllowSourceWrites || !string.Equals(policy.ToolPolicy, ProviderToolPolicies.HostEnforced, StringComparison.Ordinal))
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, ProviderFailureClasses.Policy, "write_policy_not_configured", "The target has no host-enforced, write-capable autonomous policy.");
        }

        WorkspaceCodingAgentRequest workspaceRequest;
        try
        {
            workspaceRequest = ProviderJson.DeserializePayload<WorkspaceCodingAgentRequest>(request);
            WorkspaceCodingAgentContract.ValidateRequest(workspaceRequest);
            if (!IsWithinAnAllowedWorkspaceRoot(workspaceRequest.WorkspaceRoot))
            {
                throw new ArgumentException("Workspace coding-agent workspaceRoot is not mounted for this target.");
            }
        }
        catch (ArgumentException ex)
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, ProviderFailureClasses.Schema, "invalid_workspace_request", ex.Message);
        }

        var inputBytes = Encoding.UTF8.GetByteCount(request.Payload.ToJsonString(ProviderJson.Options));
        if (inputBytes > policy.MaxInputBytes)
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, ProviderFailureClasses.Policy, "input_limit", "Workspace coding-agent request exceeds the mode input limit.");
        }

        if (workspaceRequest.ToolPolicy.AllowNetwork && !policy.AllowNetwork)
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, ProviderFailureClasses.Policy, "network_not_allowed", "The target policy does not allow workspace coding-agent network access.");
        }

        if (request.MaxOutputBytes is <= 0)
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, ProviderFailureClasses.Policy, "invalid_output_limit", "Requested output limit is invalid.");
        }

        var maxOutputBytes = Math.Min(request.MaxOutputBytes ?? Math.Min(policy.MaxOutputBytes, _options.MaxOutputBytes), Math.Min(policy.MaxOutputBytes, _options.MaxOutputBytes));
        var timeoutSeconds = request.TimeoutSeconds ?? policy.TimeoutSeconds;
        if (timeoutSeconds <= 0)
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, ProviderFailureClasses.Policy, "invalid_timeout", "Requested timeout is invalid.");
        }

        WorkspaceCodingAgentExecution execution;
        try
        {
            execution = await _runner.RunAsync(new WorkspaceCodingAgentExecutionRequest
            {
                Request = workspaceRequest,
                ModelId = request.ModelId,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                MaxOutputBytes = maxOutputBytes
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CreateResult(request, ProviderRunStatus.Cancelled, trace, stopwatch.Elapsed, ProviderFailureClasses.Cancelled, "cancelled", "Workspace coding-agent run was cancelled.");
        }
        catch (Exception ex)
        {
            return CreateResult(request, ProviderRunStatus.Failed, trace, stopwatch.Elapsed, ProviderFailureClasses.Unknown, "workspace_runner_exception", ex.Message);
        }

        stopwatch.Stop();
        if (execution.Result is not null)
        {
            try
            {
                WorkspaceCodingAgentContract.ValidateResult(workspaceRequest, execution.Result);
                if (Encoding.UTF8.GetByteCount(ProviderJson.ToJsonObject(execution.Result).ToJsonString(ProviderJson.Options)) > maxOutputBytes)
                {
                    throw new ArgumentException("Workspace coding-agent result exceeds the requested output limit.");
                }
            }
            catch (ArgumentException ex)
            {
                var outputLimit = ex.Message.Contains("output limit", StringComparison.Ordinal);
                return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, outputLimit ? ProviderFailureClasses.Policy : ProviderFailureClasses.Trust, outputLimit ? "output_limit" : "unreconciled_workspace_change_set", ex.Message);
            }
        }
        else if (execution.Status == ProviderRunStatus.Succeeded)
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, ProviderFailureClasses.Trust, "unreconciled_workspace_change_set", "Workspace coding-agent runner returned success without a reconciled result.");
        }

        var failureClass = execution.Status == ProviderRunStatus.Succeeded ? null : execution.FailureClass ?? DefaultFailureClass(execution.Status);
        var providerStatus = execution.ProviderStatus ?? (execution.Status == ProviderRunStatus.Succeeded ? "succeeded" : "workspace_runner_failed");
        var result = CreateResult(request, execution.Status, trace, stopwatch.Elapsed, failureClass, providerStatus, execution.Error, execution.Result);
        result.Trace!.InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options));
        result.Trace.OutputHash = ProviderHash.Sha256(result.Output.ToJsonString(ProviderJson.Options));
        return result;
    }

    private ProviderTraceEvent CreateTrace(ProviderRunRequest request) => new()
    {
        Provider = Profile.Id,
        Capability = request.Capability,
        Operation = request.Operation,
        Mode = request.Mode,
        AdapterId = _runner.AdapterId,
        ModelId = request.ModelId,
        ConfigHash = Profile.ConfigHash,
        AuthorityBoundary = "This target may create uncommitted changes only inside the host-enforced workspace and declared paths. It does not authorize commits, pushes, merges, deployments, or acceptance."
    };

    private ProviderRunResult CreateResult(
        ProviderRunRequest request,
        ProviderRunStatus status,
        ProviderTraceEvent trace,
        TimeSpan duration,
        string? failureClass,
        string providerStatus,
        string? error,
        WorkspaceCodingAgentResult? output = null)
    {
        trace.DurationMs = duration.TotalMilliseconds;
        trace.FailureClass = failureClass;
        return new ProviderRunResult
        {
            Status = status,
            Provider = Profile.Id,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            FailureClass = failureClass,
            ProviderStatus = providerStatus,
            Error = error,
            Output = output is null ? new JsonObject() : ProviderJson.ToJsonObject(output),
            Rejection = ProviderRunRejectionDiagnostics.Create(
                status,
                failureClass,
                providerStatus,
                request.Capability,
                decisionAuthority: status == ProviderRunStatus.Rejected
                    ? ProviderRejectionDecisionAuthorities.VyralGuardrail
                    : null),
            Trace = trace
        };
    }

    private static string DefaultFailureClass(ProviderRunStatus status) => status switch
    {
        ProviderRunStatus.TimedOut => ProviderFailureClasses.Timeout,
        ProviderRunStatus.Cancelled => ProviderFailureClasses.Cancelled,
        ProviderRunStatus.Rejected => ProviderFailureClasses.Policy,
        ProviderRunStatus.Unsupported => ProviderFailureClasses.Unsupported,
        ProviderRunStatus.NotConfigured => ProviderFailureClasses.Configuration,
        _ => ProviderFailureClasses.Unknown
    };

    private bool IsWithinAnAllowedWorkspaceRoot(string workspaceRoot)
    {
        var candidate = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return _options.AllowedWorkspaceRoots.Any(root =>
        {
            var allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(candidate, allowed, StringComparison.Ordinal) ||
                candidate.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                candidate.StartsWith(allowed + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        });
    }
}
