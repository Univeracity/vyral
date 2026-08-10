using Vyral.Providers.Abstractions;

namespace Vyral.Tests.Providers;

public class WorkspaceCodingAgentProviderTargetTests
{
    [Fact]
    public async Task Target_RequiresHostEnforcedAutonomousWritePolicyAndReturnsReconciledChanges()
    {
        var runner = new CapturingWorkspaceRunner
        {
            Result = new WorkspaceCodingAgentExecution
            {
                Result = new WorkspaceCodingAgentResult
                {
                    BaseCommit = "0123456789abcdef",
                    ChangeSetReconciled = true,
                    ToolPolicyEnforcement = WorkspaceToolPolicyEnforcements.HostEnforced,
                    ExecutedCommandIds = new List<string> { "test" },
                    ChangedPaths = new List<WorkspaceChangedPath>
                    {
                        new() { Path = "src/Worker.cs", Kind = "modified" }
                    },
                    Validation = new List<WorkspaceValidationResult>
                    {
                        new() { CommandId = "test", Status = WorkspaceValidationStatuses.Passed, ExitCode = 0 }
                    },
                    Summary = "Updated the requested worker."
                }
            }
        };
        var target = CreateTarget(runner);
        var request = CreateRequest();

        var result = await target.RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(request, provider: target.Profile.Id));

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(ProviderCapabilityIds.AgentWorkspace, Assert.Single(target.Capabilities).Id);
        Assert.Equal(ProviderToolPolicies.HostEnforced, Assert.Single(target.Capabilities).ToolPolicy);
        Assert.Contains("direct_push", target.Capabilities.Single().UnsupportedFeatures);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), runner.LastRequest!.Request.WorkspaceRoot);
        Assert.Equal("0123456789abcdef", ProviderRunResults.GetWorkspaceCodingAgent(result).BaseCommit);
        Assert.Contains("uncommitted changes", result.Trace!.AuthorityBoundary);
    }

    [Fact]
    public async Task Target_RejectsAdvisoryModeBeforeCallingTheWorkspaceRunner()
    {
        var runner = new CapturingWorkspaceRunner();
        var target = CreateTarget(runner);
        var request = ProviderRunRequests.ForWorkspaceCodingAgent(CreateRequest());
        request.Mode = ProviderModes.Advisory;

        var result = await target.RunAsync(request);

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal("write_mode_required", result.ProviderStatus);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public void RequestBuilder_RejectsPromptOnlyToolPoliciesAndNonMatchingValidationDeclarations()
    {
        var promptOnly = CreateRequest();
        promptOnly.ToolPolicy.Enforcement = WorkspaceToolPolicyEnforcements.AuditedOnly;
        Assert.Throws<ArgumentException>(() => ProviderRunRequests.ForWorkspaceCodingAgent(promptOnly));

        var undeclaredValidation = CreateRequest();
        undeclaredValidation.ValidationCommands.Add(new WorkspaceCommand { Id = "lint", FileName = "dotnet", Arguments = new List<string> { "format", "--verify-no-changes" } });
        Assert.Throws<ArgumentException>(() => ProviderRunRequests.ForWorkspaceCodingAgent(undeclaredValidation));

        var nonMatchingValidation = CreateRequest();
        nonMatchingValidation.ValidationCommands[0].Arguments = new List<string> { "test", "--no-build" };
        Assert.Throws<ArgumentException>(() => ProviderRunRequests.ForWorkspaceCodingAgent(nonMatchingValidation));

        var gitMetadata = CreateRequest();
        gitMetadata.AllowedPaths.Add(".git/config");
        Assert.Throws<ArgumentException>(() => ProviderRunRequests.ForWorkspaceCodingAgent(gitMetadata));
    }

    [Fact]
    public async Task Target_RejectsAChangeOutsideTheAllowedPathsAfterRunnerEvidenceIsReturned()
    {
        var runner = new CapturingWorkspaceRunner
        {
            Result = new WorkspaceCodingAgentExecution
            {
                Result = new WorkspaceCodingAgentResult
                {
                    BaseCommit = "0123456789abcdef",
                    ChangeSetReconciled = true,
                    ToolPolicyEnforcement = WorkspaceToolPolicyEnforcements.HostEnforced,
                    ExecutedCommandIds = new List<string> { "test" },
                    ChangedPaths = new List<WorkspaceChangedPath>
                    {
                        new() { Path = "secrets/credentials.json", Kind = "modified" }
                    }
                }
            }
        };
        var target = CreateTarget(runner);

        var result = await target.RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(CreateRequest()));

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Trust, result.FailureClass);
        Assert.Equal("unreconciled_workspace_change_set", result.ProviderStatus);
        Assert.Contains("outside the allowed paths", result.Error);
    }

    [Fact]
    public void AdvisoryCliTargets_RemainDistinctFromWorkspaceCodingAgentTargets()
    {
        var cli = Vyral.Providers.Cli.CliProviderTargets.CreateCodex(new NoopProcessRunner());

        Assert.DoesNotContain(cli.Capabilities, capability => capability.Id == ProviderCapabilityIds.AgentWorkspace);
        Assert.All(cli.Capabilities, capability => Assert.Contains("source_writes", capability.UnsupportedFeatures));
    }

    private static WorkspaceCodingAgentProviderTarget CreateTarget(CapturingWorkspaceRunner runner) => new(
        new WorkspaceCodingAgentProviderTargetOptions
        {
            ProviderId = "test-workspace-agent",
            DisplayName = "Test workspace agent",
            ConfigIdentity = "test-host",
            AllowedWorkspaceRoots = new List<string> { Path.GetFullPath(Path.GetTempPath()) }
        },
        runner);

    private static WorkspaceCodingAgentRequest CreateRequest() => new()
    {
        Task = "Update the worker and leave changes for review.",
        WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()),
        WriteMode = WorkspaceCodingAgentWriteModes.Write,
        AllowedPaths = new List<string> { "src" },
        AvoidedPaths = new List<string> { "src/secrets" },
        ToolPolicy = new WorkspaceToolPolicy
        {
            Enforcement = WorkspaceToolPolicyEnforcements.HostEnforced,
            MaxCommands = 8,
            AllowedCommands = new List<WorkspaceCommand>
            {
                new() { Id = "test", FileName = "dotnet", Arguments = new List<string> { "test", "--no-restore" } }
            }
        },
        ValidationCommands = new List<WorkspaceCommand>
        {
            new() { Id = "test", FileName = "dotnet", Arguments = new List<string> { "test", "--no-restore" } }
        }
    };

    private sealed class CapturingWorkspaceRunner : IWorkspaceCodingAgentRunner
    {
        public string AdapterId => "test-workspace-host";
        public WorkspaceCodingAgentExecutionRequest? LastRequest { get; private set; }
        public WorkspaceCodingAgentExecution Result { get; set; } = new()
        {
            Status = ProviderRunStatus.Failed,
            FailureClass = ProviderFailureClasses.Unknown,
            ProviderStatus = "not_configured"
        };

        public Task<WorkspaceCodingAgentExecution> RunAsync(WorkspaceCodingAgentExecutionRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class NoopProcessRunner : Vyral.Providers.Cli.IProviderProcessRunner
    {
        public Task<Vyral.Providers.Cli.ProviderProcessRunResult> RunAsync(Vyral.Providers.Cli.ProviderProcessRunRequest request, CancellationToken ct = default) =>
            Task.FromResult(new Vyral.Providers.Cli.ProviderProcessRunResult { ExitCode = 0, StandardOutput = "ok" });
    }
}
