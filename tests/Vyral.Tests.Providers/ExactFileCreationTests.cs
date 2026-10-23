using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Cli;

namespace Vyral.Tests.Providers;

public partial class CliWorkspaceCodingAgentRunnerTests
{
    private static WorkspaceCodingAgentRequest ExactRequest(WorkspaceFixture fixture, params string[] paths)
    {
        var request = fixture.CreateRequest();
        request.AllowedPaths = paths.ToList(); request.NewFilePaths = paths.ToList();
        return request;
    }

    private static async Task SetPythonAgent(WorkspaceFixture fixture, string body) =>
        await File.WriteAllTextAsync(fixture.AgentScript, "#!/usr/bin/python3\nimport os, time\n" + body + "\n");

    private static async Task AssertUnchanged(WorkspaceFixture fixture, ProviderRunResult result, string status)
    {
        Assert.Equal(status, result.ProviderStatus);
        Assert.NotEqual(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal("seed", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "src/seed.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "src/generated.txt")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace, "src/generated.txt")));
        Assert.Equal("", await fixture.GitAsync("status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Root, "staging")));
        if (result.Output.Count > 0)
        {
            var evidence = result.Output.Deserialize<WorkspaceCodingAgentResult>(ProviderJson.Options)!;
            Assert.Empty(evidence.ExecutedCommandIds);
            Assert.All(evidence.Validation, validation => Assert.Equal(WorkspaceValidationStatuses.Skipped, validation.Status));
        }
    }

    [BubblewrapFact]
    public async Task ExactCreation_TargetAbsentUntilAgentCreatesRegularFile_ExistingSiblingReadOnly()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
        await SetPythonAgent(fixture, "assert not os.path.lexists('/workspace/src/generated.txt')\ntry:\n open('/workspace/src/seed.txt', 'w').write('blocked')\n raise AssertionError('sibling writable')\nexcept OSError: pass\nopen('/workspace/src/generated.txt', 'w').write('exact-created')");
        var result = await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(ExactRequest(fixture, "src/generated.txt")));
        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var evidence = ProviderRunResults.GetWorkspaceCodingAgent(result);
        Assert.Equal("src/generated.txt", Assert.Single(evidence.ChangedPaths).Path);
        Assert.Equal("verify", Assert.Single(evidence.ExecutedCommandIds));
        Assert.Equal(WorkspaceValidationStatuses.Passed, Assert.Single(evidence.Validation).Status);
        Assert.Equal("exact-created", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "src/generated.txt")));
        Assert.Equal("seed", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "src/seed.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Root, "staging")));
    }

    [BubblewrapFact]
    public async Task ExactCreation_RootFilenameRemainsAbsentAndGitProtected()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
        await SetPythonAgent(fixture, "assert not os.path.lexists('/workspace/new.txt')\ntry:\n open('/workspace/.git/config', 'w').write('blocked')\n raise AssertionError('Git writable')\nexcept OSError: pass\nopen('/workspace/new.txt', 'w').write('root-file')");
        var result = await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(ExactRequest(fixture, "new.txt")));
        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal("root-file", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "new.txt")));
        Assert.Equal(fixture.BaseCommit, await fixture.GitAsync("rev-parse", "HEAD"));
        Assert.Equal("new.txt", Assert.Single(ProviderRunResults.GetWorkspaceCodingAgent(result).ChangedPaths).Path);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Root, "staging")));
    }

    [BubblewrapFact]
    public async Task ExactCreation_RootAndNestedParentsMountInOrderRegardlessOfDeclaredOrder()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
        Directory.CreateDirectory(Path.Combine(fixture.Workspace, "src/nested"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "src/nested/seed.txt"), "nested-seed");
        await fixture.GitAsync("add", "."); await fixture.GitAsync("commit", "-qm", "nested parent seed");
        await SetPythonAgent(fixture, "assert not os.path.lexists('/workspace/new.txt')\nassert not os.path.lexists('/workspace/src/nested/new.txt')\nopen('/workspace/new.txt', 'w').write('root')\nopen('/workspace/src/nested/new.txt', 'w').write('nested')");
        var result = await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(ExactRequest(fixture, "src/nested/new.txt", "new.txt")));
        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal("root", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "new.txt")));
        Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "src/nested/new.txt")));
        Assert.Equal("nested-seed", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "src/nested/seed.txt")));
        Assert.Equal(2, ProviderRunResults.GetWorkspaceCodingAgent(result).ChangedPaths.Count);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Root, "staging")));
    }

    [BubblewrapFact]
    public async Task ExactCreation_OmittedFieldRetainsLegacyAbsentDirectoryBehavior()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
        await SetPythonAgent(fixture, "assert os.path.isdir('/workspace/src/generated.txt')\nraise SystemExit(73)");
        var request = fixture.CreateRequest(); request.AllowedPaths = ["src/generated.txt"];
        await AssertUnchanged(fixture, await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(request)), "agent_process_failed");
    }

    [Theory]
    [InlineData("sibling", "open('/workspace/src/other.txt', 'w').write('undeclared')", "out_of_scope_change")]
    [InlineData("ignored", "open('/workspace/src/ignored.txt', 'w').write('undeclared')", "out_of_scope_change")]
    [InlineData("emptydir", "os.mkdir('/workspace/src/empty')", "out_of_scope_change")]
    [InlineData("targetdir", "os.mkdir('/workspace/src/generated.txt')", "new_file_not_regular")]
    [InlineData("targetfifo", "os.mkfifo('/workspace/src/generated.txt')", "workspace_special_entry")]
    [InlineData("siblingfifo", "os.mkfifo('/workspace/src/other.txt')", "workspace_special_entry")]
    [InlineData("symlink", "os.symlink('/etc/passwd', '/workspace/src/generated.txt')", "sandbox_symlink_present")]
    public async Task ExactCreation_PrivateFilesystemDeltaRefusesBeforeValidation(string name, string action, string status)
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, ".gitignore"), "src/ignored.txt\n");
        await fixture.GitAsync("add", ".gitignore"); await fixture.GitAsync("commit", "-qm", "ignore control");
        await SetPythonAgent(fixture, action + (name.StartsWith("target") || name == "symlink" ? "" : "\nopen('/workspace/src/generated.txt', 'w').write('valid')"));
        await AssertUnchanged(fixture, await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(ExactRequest(fixture, "src/generated.txt"))), status);
    }

    [BubblewrapFact]
    public async Task ExactCreation_IgnoredDeclaredTargetRefusesUnreconciledGitChange()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, ".gitignore"), "src/generated.txt\n");
        await fixture.GitAsync("add", ".gitignore"); await fixture.GitAsync("commit", "-qm", "ignore exact target");
        await SetPythonAgent(fixture, "open('/workspace/src/generated.txt', 'w').write('valid')");
        await AssertUnchanged(fixture, await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(ExactRequest(fixture, "src/generated.txt"))), "filesystem_change_unreconciled");
    }

    [BubblewrapFact]
    public async Task ExactCreation_ExistingTargetAndMissingParentRefuseBeforeAgent()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
        await SetPythonAgent(fixture, "raise AssertionError('agent must not start')");
        foreach (var (path, status) in new[] { ("src/seed.txt", "new_file_already_exists"), ("missing/generated.txt", "new_file_parent_missing"), ("src", "new_file_already_exists") })
            await AssertUnchanged(fixture, await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(ExactRequest(fixture, path))), status);
    }

    [BubblewrapFact]
    public async Task ExactCreation_MultipleTargetsAndExistingFileApplyAsOneValidatedBatch()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
        await SetPythonAgent(fixture, "open('/workspace/src/generated.txt', 'w').write('one')\nopen('/workspace/src/second.txt', 'w').write('two')\nopen('/workspace/src/seed.txt', 'w').write('changed')");
        var request = ExactRequest(fixture, "src/generated.txt", "src/second.txt"); request.AllowedPaths.Add("src/seed.txt");
        var result = await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(request));
        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(3, ProviderRunResults.GetWorkspaceCodingAgent(result).ChangedPaths.Count);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "src/generated.txt")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "src/second.txt")));
        Assert.Equal("changed", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "src/seed.txt")));
    }

    [BubblewrapFact]
    public async Task ExactCreation_InvalidSiblingLeavesAllBatchSourceFilesUnchanged()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
        await SetPythonAgent(fixture, "open('/workspace/src/generated.txt', 'w').write('one')\nopen('/workspace/src/seed.txt', 'w').write('changed')\nopen('/workspace/src/other.txt', 'w').write('undeclared')");
        var request = ExactRequest(fixture, "src/generated.txt"); request.AllowedPaths.Add("src/seed.txt");
        await AssertUnchanged(fixture, await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(request)), "out_of_scope_change");
    }

    [BubblewrapFact]
    public async Task ExactCreation_ValidationFailureMutationAndDeadlineNeverApply()
    {
        foreach (var mode in new[] { "false", "mutate", "timeout", "cancel" })
        {
            await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
            await SetPythonAgent(fixture, "open('/workspace/src/generated.txt', 'w').write('valid')" + (mode is "timeout" or "cancel" ? "\ntime.sleep(5)" : ""));
            var request = ExactRequest(fixture, "src/generated.txt");
            if (mode is "false" or "mutate")
            {
                var validation = request.ValidationCommands[0];
                validation.Arguments = ["-c", mode == "false" ? "exit 1" : "printf changed > src/generated.txt"];
            }
            using var cancellation = new CancellationTokenSource(); if (mode == "cancel") cancellation.CancelAfter(200);
            var result = await fixture.CreateTarget().RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(request, timeoutSeconds: mode == "timeout" ? 1 : 10), cancellation.Token);
            Assert.NotEqual(ProviderRunStatus.Succeeded, result.Status);
            Assert.False(File.Exists(Path.Combine(fixture.Workspace, "src/generated.txt")));
            Assert.Equal("seed", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "src/seed.txt")));
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Root, "staging")));
            Assert.Equal(mode switch { "false" => "validation_failed", "mutate" => "validation_mutated_workspace", "timeout" => "timeout", _ => "cancelled" }, result.ProviderStatus);
        }
    }

    [Fact]
    public async Task ExactCreation_CapabilityIsExplicitAndUnsupportedRunnerRefuses()
    {
        var runner = new UnsupportedCreationRunner();
        var target = new WorkspaceCodingAgentProviderTarget(new() { ProviderId = "synthetic-host", AllowedWorkspaceRoots = ["/synthetic"] }, runner);
        Assert.False(target.Capabilities[0].InputLimits.ContainsKey("newFilePaths"));
        var validation = new WorkspaceCommand { Id = "check", FileName = "true" };
        var request = new WorkspaceCodingAgentRequest { Task = "synthetic task", WorkspaceRoot = "/synthetic", WriteMode = "write", AllowedPaths = ["new.txt"], NewFilePaths = ["new.txt"],
            ToolPolicy = new() { Enforcement = "host_enforced", MaxCommands = 1, AllowedCommands = [validation] }, ValidationCommands = [validation] };
        var result = await target.RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(request));
        Assert.Equal("exact_file_creation_unsupported", result.ProviderStatus); Assert.False(runner.Started);
    }

    private sealed class UnsupportedCreationRunner : IWorkspaceCodingAgentRunner
    {
        public string AdapterId => "synthetic-unsupported";
        public bool Started { get; private set; }
        public Task<WorkspaceCodingAgentExecution> RunAsync(WorkspaceCodingAgentExecutionRequest request, CancellationToken ct = default)
        { Started = true; throw new InvalidOperationException("Must refuse before unsupported host starts"); }
    }

    [BubblewrapFact]
    public async Task ExactCreation_OriginalIgnoredFilesystemMutationRefusesBeforeApply()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, ".gitignore"), "external.txt\n");
        await fixture.GitAsync("add", ".gitignore"); await fixture.GitAsync("commit", "-qm", "ignored race control");
        await SetPythonAgent(fixture, "open('/workspace/src/generated.txt', 'w').write('valid')");
        var runner = new OriginalMutationRunner(fixture.Workspace);
        var result = await fixture.CreateTarget(processRunner: runner).RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(ExactRequest(fixture, "src/generated.txt")));
        Assert.Equal("workspace_changed_during_run", result.ProviderStatus); Assert.True(runner.Mutated);
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "src/generated.txt")));
        Assert.Equal("external", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "external.txt")));
        Assert.Equal("", await fixture.GitAsync("status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Root, "staging")));
    }

    private sealed class OriginalMutationRunner(string workspace) : IProviderProcessRunner
    {
        public bool Mutated { get; private set; }
        public async Task<ProviderProcessRunResult> RunAsync(ProviderProcessRunRequest request, CancellationToken ct = default)
        {
            var result = await new SystemProviderProcessRunner().RunAsync(request, ct);
            if (request.Arguments.Contains("/vyral-tools/sh") && !Mutated)
            { await File.WriteAllTextAsync(Path.Combine(workspace, "external.txt"), "external", ct); Mutated = true; }
            return result;
        }
    }

    [BubblewrapFact]
    public async Task ExactCreation_InvalidDeliveryAndAgentFailureRefuseBeforeValidation()
    {
        foreach (var failedAgent in new[] { true, false })
        {
            await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
            await SetPythonAgent(fixture, "open('/workspace/src/generated.txt', 'w').write('valid')\n" + (failedAgent ? "raise SystemExit(73)" : "print('invalid receipt')"));
            var target = failedAgent ? fixture.CreateTarget() : fixture.CreateTarget(inputFormat: "workspace-json-v1", promptTransport: CliPromptTransports.StandardInput, agentOutcomeFormat: "workspace-ipc-v1");
            await AssertUnchanged(fixture, await target.RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(ExactRequest(fixture, "src/generated.txt"))), failedAgent ? "agent_process_failed" : "agent_delivery_mismatch");
        }
    }

    [BubblewrapFact]
    public async Task ExactCreation_DescriptorPinsContractAndSourceSymlinkRefuses()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
        var target = fixture.CreateTarget(); var limits = target.Capabilities[0].InputLimits;
        Assert.Equal("exact-file-creation-v1", limits["newFilePaths"]); Assert.Equal(32, limits["newFilePathLimit"]);
        Assert.Equal(512, limits["newFilePathMaxUtf8Bytes"]); Assert.Equal("private-parent-whole-stage-reconciled", limits["creationStagePolicy"]);
        await SetPythonAgent(fixture, "raise AssertionError('must not start')");
        File.CreateSymbolicLink(Path.Combine(fixture.Workspace, "src/link"), fixture.Root);
        await fixture.GitAsync("add", "src/link"); await fixture.GitAsync("commit", "-qm", "symlink control");
        var result = await target.RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(ExactRequest(fixture, "src/generated.txt")));
        Assert.Equal("workspace_symlink_present", result.ProviderStatus); Assert.False(File.Exists(Path.Combine(fixture.Workspace, "src/generated.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Root, "staging")));
    }

    [Fact]
    public void ExactCreation_ClosedLexicalRequestRefusalsAndOmissionBytes()
    {
        var request = new WorkspaceCodingAgentRequest { AllowedPaths = ["src/new.txt"] };
        var omitted = JsonSerializer.SerializeToNode(request, ProviderJson.Options)!.AsObject();
        Assert.False(omitted.ContainsKey("newFilePaths"));
        foreach (var value in new[] { "null", "false", "1", "{}", "\"src/new.txt\"", "[]", "[null]", "[false]", "[1]" })
        {
            var wire = (JsonObject)omitted.DeepClone(); wire["newFilePaths"] = JsonNode.Parse(value);
            Assert.ThrowsAny<Exception>(() => WorkspaceCodingAgentContract.ValidateNewFilePaths(wire.Deserialize<WorkspaceCodingAgentRequest>(ProviderJson.Options)!));
        }
        foreach (var path in new[] { "", ".", "../new.txt", "/new.txt", "src/../new.txt", "src//new.txt", "src/./new.txt", "src\\new.txt", " src/new.txt", ".git/new.txt", "src/.git/new.txt", "src/\nnew.txt", new string('x', 513) })
        {
            request.AllowedPaths = [path]; request.NewFilePaths = [path];
            Assert.Throws<ArgumentException>(() => WorkspaceCodingAgentContract.ValidateNewFilePaths(request));
        }
        request.AllowedPaths = ["src/new.txt"]; request.NewFilePaths = ["src/new.txt"]; WorkspaceCodingAgentContract.ValidateNewFilePaths(request);
        foreach (var allowed in new[] { new List<string> { "src" }, new List<string> { "src", "src/new.txt" }, new List<string> { "src/new.txt", "src/new.txt/sub" }, new List<string> { "src/new.txt", "src/new.txt" }, new List<string> { "src/new.txt", "src/./new.txt" } })
        {
            request.AllowedPaths = allowed; Assert.Throws<ArgumentException>(() => WorkspaceCodingAgentContract.ValidateNewFilePaths(request));
        }
        request.AllowedPaths = ["src/new.txt"]; request.NewFilePaths = ["src/new.txt", "src/new.txt"];
        Assert.Throws<ArgumentException>(() => WorkspaceCodingAgentContract.ValidateNewFilePaths(request));
        request.NewFilePaths = ["src/new.txt"]; request.AvoidedPaths = ["src"];
        Assert.Throws<ArgumentException>(() => WorkspaceCodingAgentContract.ValidateNewFilePaths(request));
        request.AvoidedPaths = ["src/new.txt/sub"]; Assert.Throws<ArgumentException>(() => WorkspaceCodingAgentContract.ValidateNewFilePaths(request));
        request.AvoidedPaths = []; request.NewFilePaths = Enumerable.Range(0, 33).Select(i => $"src/new{i}.txt").ToList(); request.AllowedPaths = request.NewFilePaths.ToList();
        Assert.Throws<ArgumentException>(() => WorkspaceCodingAgentContract.ValidateNewFilePaths(request));
    }
}
