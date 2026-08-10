using System.Diagnostics;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Cli;

namespace Vyral.Tests.Providers;

public sealed class BubblewrapFactAttribute : FactAttribute
{
    public BubblewrapFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(BubblewrapTestEnvironment.Command))
        {
            Skip = "Bubblewrap integration tests require Linux and the configured Bubblewrap binary.";
        }
    }
}

internal static class BubblewrapTestEnvironment
{
    public static string Command =>
        Environment.GetEnvironmentVariable("VYRAL_TEST_BUBBLEWRAP") ?? "/usr/bin/bwrap";
}

public class CliWorkspaceCodingAgentRunnerTests
{
    [BubblewrapFact]
    public async Task Runner_AppliesAllowedUntrackedChangesOnlyAfterDeclaredValidation()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("write-untracked");
        var target = fixture.CreateTarget();

        var result = await target.RunWorkspaceAsync(fixture.CreateRequest());

        Assert.Contains("applied", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.ChangedPaths, change => change.Path == "src/generated.txt" && change.Kind == "untracked");
        Assert.Equal(WorkspaceValidationStatuses.Passed, Assert.Single(result.Validation).Status);
        Assert.Contains("verify", result.ExecutedCommandIds);
        Assert.True(File.Exists(Path.Combine(fixture.Workspace, "src", "generated.txt")));
        Assert.Equal("generated", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "src", "generated.txt")));
    }

    [BubblewrapFact]
    public async Task Runner_ProvidesProcAndDevInsideTheBubblewrapSandbox()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("verify-runtime");

        var result = await fixture.CreateTarget().RunWorkspaceAsync(fixture.CreateRequest());

        Assert.Contains("applied", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("runtime-ready", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "src", "runtime.txt")));
    }

    [BubblewrapFact]
    public async Task Runner_BlocksOutOfScopeWritesAndDoesNotTouchTheSourceWorkspace()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("write-outside");

        var result = await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(fixture.CreateRequest()));

        Assert.Equal(ProviderRunStatus.Failed, result.Status);
        Assert.Equal("agent_process_failed", result.ProviderStatus);
        Assert.Contains("exit code", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "src", "generated.txt")));
    }

    [BubblewrapFact]
    public async Task Runner_RejectsSymlinkedWorkspacesBeforeTheAgentStarts()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("write-untracked");
        var outside = Path.Combine(fixture.Root, "outside-target.txt");
        await File.WriteAllTextAsync(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(fixture.Workspace, "src", "escape"), outside);
        await fixture.GitAsync("add", "src/escape");
        await fixture.GitAsync("commit", "-qm", "add symlink");

        var result = await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(fixture.CreateRequest()));

        Assert.Equal(ProviderRunStatus.Failed, result.Status);
        Assert.Equal("workspace_symlink_present", result.ProviderStatus);
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "src", "generated.txt")));
    }

    [BubblewrapFact]
    public async Task Runner_BlocksGitMutationAndUndeclaredCommands()
    {
        await using var gitFixture = await WorkspaceFixture.CreateAsync("git-mutation", includeGitTool: true);
        var gitResult = await gitFixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(gitFixture.CreateRequest(includeGitTool: true)));

        Assert.Equal(ProviderRunStatus.Failed, gitResult.Status);
        Assert.Equal("agent_process_failed", gitResult.ProviderStatus);
        Assert.Contains("exit code", gitResult.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(gitFixture.BaseCommit, await gitFixture.GitAsync("rev-parse", "HEAD"));

        await using var undeclaredFixture = await WorkspaceFixture.CreateAsync("undeclared-command");
        var undeclaredResult = await undeclaredFixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(undeclaredFixture.CreateRequest()));

        Assert.Equal(ProviderRunStatus.Failed, undeclaredResult.Status);
        Assert.Equal("agent_process_failed", undeclaredResult.ProviderStatus);
        Assert.Contains("exit code", undeclaredResult.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(undeclaredFixture.Workspace, "src", "undeclared.txt")));
    }

    [BubblewrapFact]
    public async Task Runner_RejectsStagedSymlinksAndValidationMutations()
    {
        await using var symlinkFixture = await WorkspaceFixture.CreateAsync("write-symlink");
        var symlinkTarget = symlinkFixture.CreateTarget(
            agentCommand: "/usr/bin/ln",
            agentArguments: new List<string> { "-s", "/etc/passwd", "/workspace/src/escape" },
            promptTransport: CliPromptTransports.StandardInput);
        var symlinkResult = await symlinkTarget.RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(symlinkFixture.CreateRequest()));

        Assert.Equal(ProviderRunStatus.Failed, symlinkResult.Status);
        Assert.Equal("sandbox_symlink_present", symlinkResult.ProviderStatus);
        Assert.False(File.Exists(Path.Combine(symlinkFixture.Workspace, "src", "escape")));

        await using var mutationFixture = await WorkspaceFixture.CreateAsync("write-untracked");
        var mutationResult = await mutationFixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(mutationFixture.CreateRequest(validationCommand: "mutate")));

        Assert.Equal(ProviderRunStatus.Rejected, mutationResult.Status);
        Assert.Equal("validation_mutated_workspace", mutationResult.ProviderStatus);
        Assert.False(File.Exists(Path.Combine(mutationFixture.Workspace, "src", "generated.txt")));
        Assert.False(File.Exists(Path.Combine(mutationFixture.Workspace, "src", "validator.txt")));
    }

    [BubblewrapFact]
    public async Task Runner_DiscardsChangesOnValidationFailureAndCancellation()
    {
        await using var partialFixture = await WorkspaceFixture.CreateAsync("write-untracked");
        var failedValidation = partialFixture.CreateRequest(validationCommand: "false");

        var failedResult = await partialFixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(failedValidation));

        Assert.Equal(ProviderRunStatus.Failed, failedResult.Status);
        Assert.Equal("validation_failed", failedResult.ProviderStatus);
        Assert.False(File.Exists(Path.Combine(partialFixture.Workspace, "src", "generated.txt")));

        await using var timeoutFixture = await WorkspaceFixture.CreateAsync("loop");
        var timeoutRequest = timeoutFixture.CreateRequest();
        var timeoutResult = await timeoutFixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(timeoutRequest, timeoutSeconds: 1));

        Assert.Equal(ProviderRunStatus.TimedOut, timeoutResult.Status);
        Assert.False(File.Exists(Path.Combine(timeoutFixture.Workspace, "src", "generated.txt")));

        await using var outputFixture = await WorkspaceFixture.CreateAsync("write-untracked");
        var outputLimitedRequest = ProviderRunRequests.ForWorkspaceCodingAgent(outputFixture.CreateRequest());
        outputLimitedRequest.MaxOutputBytes = 1;
        var outputLimitedResult = await outputFixture.CreateTarget().RunAsync(outputLimitedRequest);

        Assert.Equal(ProviderRunStatus.Rejected, outputLimitedResult.Status);
        Assert.Equal("output_limit", outputLimitedResult.ProviderStatus);
        Assert.False(File.Exists(Path.Combine(outputFixture.Workspace, "src", "generated.txt")));
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        private WorkspaceFixture(string root, string workspace, string agentScript, string baseCommit, string behavior)
        {
            Root = root;
            Workspace = workspace;
            AgentScript = agentScript;
            BaseCommit = baseCommit;
            Behavior = behavior;
        }

        public string Root { get; }
        public string Workspace { get; }
        public string AgentScript { get; }
        public string BaseCommit { get; }
        public string Behavior { get; }

        public static async Task<WorkspaceFixture> CreateAsync(string behavior, bool includeGitTool = false)
        {
            if (!OperatingSystem.IsLinux() || !File.Exists(BubblewrapTestEnvironment.Command))
            {
                throw new InvalidOperationException("The Bubblewrap test precondition changed after discovery.");
            }

            var root = Path.Combine(Path.GetTempPath(), $"vyral-workspace-agent-{Guid.NewGuid():N}");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(Path.Combine(workspace, "src"));
            await File.WriteAllTextAsync(Path.Combine(workspace, "src", "seed.txt"), "seed");
            await RunProcessAsync(workspace, "git", "init", "-q");
            await RunProcessAsync(workspace, "git", "config", "user.email", "tests@example.com");
            await RunProcessAsync(workspace, "git", "config", "user.name", "Vyral Tests");
            await RunProcessAsync(workspace, "git", "add", ".");
            await RunProcessAsync(workspace, "git", "commit", "-qm", "seed");
            var baseCommit = await RunProcessAsync(workspace, "git", "rev-parse", "HEAD");

            var agentScript = Path.Combine(root, "fixture-agent.sh");
            await File.WriteAllTextAsync(agentScript, $$"""
                #!/bin/sh
                case "$1" in
                  *write-untracked*) printf generated > /workspace/src/generated.txt ;;
                  *write-outside*) printf blocked > /workspace/outside.txt ;;
                  *git-mutation*) git commit --allow-empty -m blocked ;;
                  *undeclared-command*) touch /workspace/src/undeclared.txt ;;
                  *verify-runtime*) test -e /proc/self/exe && test -c /dev/null && printf runtime-ready > /workspace/src/runtime.txt ;;
                  *loop*) while :; do :; done ;;
                esac
                """);
            File.SetUnixFileMode(agentScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(root, "staging"));
            return new WorkspaceFixture(root, workspace, agentScript, baseCommit.Trim(), behavior);
        }

        public WorkspaceCodingAgentProviderTarget CreateTarget(string? agentCommand = null, List<string>? agentArguments = null, string? promptTransport = null)
        {
            var runnerOptions = new CliWorkspaceCodingAgentOptions
            {
                ProviderId = "fixture-workspace-cli",
                AgentProfile = "fixture",
                AgentCommand = agentCommand ?? AgentScript,
                AgentArguments = agentArguments ?? new List<string> { "{prompt}" },
                PromptTransport = promptTransport ?? CliPromptTransports.Argument,
                AllowedWorkspaceRoots = new List<string> { Root },
                StagingRoot = Path.Combine(Root, "staging"),
                BubblewrapCommand = BubblewrapTestEnvironment.Command,
                GitCommand = "git",
                RuntimeReadOnlyPaths = new List<string> { "/lib", "/lib64" },
                ToolSearchPaths = new List<string> { "/usr/bin", "/bin" }
            };
            return new WorkspaceCodingAgentProviderTarget(new WorkspaceCodingAgentProviderTargetOptions
            {
                ProviderId = runnerOptions.ProviderId,
                DisplayName = "Fixture workspace CLI",
                AllowedWorkspaceRoots = runnerOptions.AllowedWorkspaceRoots.ToList()
            }, new CliWorkspaceCodingAgentRunner(runnerOptions));
        }

        public WorkspaceCodingAgentRequest CreateRequest(bool includeGitTool = false, string validationCommand = "verify")
        {
            var validation = validationCommand switch
            {
                "false" => new WorkspaceCommand { Id = "verify", FileName = "sh", Arguments = new List<string> { "-c", "exit 1" } },
                "mutate" => new WorkspaceCommand { Id = "verify", FileName = "sh", Arguments = new List<string> { "-c", "printf validator > src/validator.txt" } },
                _ => new WorkspaceCommand { Id = "verify", FileName = "sh", Arguments = new List<string> { "-c", "test -f src/seed.txt" } }
            };
            var commands = new List<WorkspaceCommand> { validation };
            if (includeGitTool)
            {
                commands.Add(new WorkspaceCommand { Id = "git", FileName = "git", Arguments = new List<string> { "status", "--short" } });
            }

            return new WorkspaceCodingAgentRequest
            {
                Task = Behavior,
                WorkspaceRoot = Workspace,
                WriteMode = WorkspaceCodingAgentWriteModes.Write,
                AllowedPaths = new List<string> { "src" },
                ToolPolicy = new WorkspaceToolPolicy
                {
                    Enforcement = WorkspaceToolPolicyEnforcements.HostEnforced,
                    MaxCommands = 8,
                    AllowedCommands = commands
                },
                ValidationCommands = new List<WorkspaceCommand> { validation }
            };
        }

        public async Task<string> GitAsync(params string[] args) => (await RunProcessAsync(Workspace, "git", args)).Trim();

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
            return ValueTask.CompletedTask;
        }

    }

    private static async Task<string> RunProcessAsync(string workingDirectory, string command, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(command) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
