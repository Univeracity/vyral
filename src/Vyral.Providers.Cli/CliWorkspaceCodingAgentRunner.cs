using System.Security.Cryptography;
using System.Text;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Cli;

/// <summary>
/// A concrete Linux CLI host for <c>agent.workspace</c>. It copies a clean Git
/// workspace into a disposable staging tree, runs the configured agent in a
/// Bubblewrap namespace with a read-only root and only declared workspace paths
/// remounted writable, then applies a reconciled, validated change set back to
/// the original checkout. If isolation or reconciliation cannot be established,
/// it fails closed and leaves the original checkout untouched.
/// </summary>
public sealed class CliWorkspaceCodingAgentRunner : IWorkspaceCodingAgentRunner
{
    private static readonly string[] SensitiveEnvironmentNames =
    {
        "PATH", "HOME", "TMPDIR", "TMP", "TEMP", "BASH_ENV", "ENV", "GIT_DIR", "GIT_WORK_TREE", "LD_PRELOAD", "LD_LIBRARY_PATH"
    };

    private static readonly string[] UnsafeRuntimePaths =
    {
        "/", "/home", "/root", "/etc", "/bin", "/sbin", "/usr/bin", "/usr/sbin", "/usr/local/bin", "/usr/local/sbin"
    };

    private readonly CliWorkspaceCodingAgentOptions _options;
    private readonly IProviderProcessRunner _processRunner;

    public CliWorkspaceCodingAgentRunner(CliWorkspaceCodingAgentOptions options, IProviderProcessRunner? processRunner = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The concrete CLI workspace host requires Linux Bubblewrap isolation.");
        }

        if (string.IsNullOrWhiteSpace(options.AgentCommand))
        {
            throw new ArgumentException("Workspace coding-agent AgentCommand is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.StagingRoot) || !Path.IsPathRooted(options.StagingRoot))
        {
            throw new ArgumentException("Workspace coding-agent StagingRoot must be an absolute path.", nameof(options));
        }

        if (options.AllowedWorkspaceRoots.Count == 0 || options.AllowedWorkspaceRoots.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)))
        {
            throw new ArgumentException("Workspace coding-agent requires configured absolute AllowedWorkspaceRoots.", nameof(options));
        }

        if (options.ToolSearchPaths.Count == 0 || options.ToolSearchPaths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)))
        {
            throw new ArgumentException("Workspace coding-agent requires configured absolute ToolSearchPaths.", nameof(options));
        }

        if (options.MaxOutputBytes <= 0 || options.PreparationTimeoutSeconds <= 0)
        {
            throw new ArgumentException("Workspace coding-agent output and preparation limits must be positive.", nameof(options));
        }

        if (options.RuntimeReadOnlyPaths.Any(IsUnsafeRuntimePath))
        {
            throw new ArgumentException("Workspace coding-agent runtime paths must not expose host roots, homes, configuration roots, or executable directories.", nameof(options));
        }

        if (options.Environment.Keys.Any(key => SensitiveEnvironmentNames.Contains(key, StringComparer.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Workspace coding-agent environment may not override sandbox-controlled variables.", nameof(options));
        }

        _options = options;
        _processRunner = processRunner ?? new SystemProviderProcessRunner();
        AdapterId = $"cli-bwrap:{options.AgentProfile}";
    }

    public string AdapterId { get; }

    public async Task<WorkspaceCodingAgentExecution> RunAsync(WorkspaceCodingAgentExecutionRequest executionRequest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executionRequest);
        var request = executionRequest.Request;
        string? staging = null;
        try
        {
            var sourceRoot = await GetCleanWorkspaceRootAsync(request.WorkspaceRoot, ct);
            if (!IsUnderConfiguredRoot(sourceRoot))
            {
                return Failure("workspace_not_mounted", "The requested workspace is not under a configured host root.", ProviderFailureClasses.Policy);
            }

            if (IsUnderPath(Path.GetFullPath(_options.StagingRoot), sourceRoot))
            {
                return Failure("staging_root_inside_workspace", "The configured staging root must not be inside the source workspace.", ProviderFailureClasses.Configuration);
            }

            EnsureNoSymlinks(sourceRoot);
            var baseCommit = (await GetGitOutputAsync(sourceRoot, "rev-parse", "HEAD", ct)).Trim();
            if (!IsCommit(baseCommit))
            {
                return Failure("base_commit_unavailable", "The workspace did not yield a Git base commit.", ProviderFailureClasses.Configuration);
            }

            staging = Path.Combine(Path.GetFullPath(_options.StagingRoot), $"vyral-workspace-agent-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            CopyWorkspace(sourceRoot, staging);
            PrepareWritablePaths(staging, request.AllowedPaths);
            PrepareAvoidedPaths(staging, request.AvoidedPaths);

            var agentPrompt = ComposeAgentPrompt(request);
            var agentResult = await RunSandboxedAsync(
                staging,
                request,
                executionRequest,
                ResolveAgentCommand(),
                "/vyral-agent/agent",
                BuildAgentArguments(agentPrompt, executionRequest.ModelId),
                _options.PromptTransport == CliPromptTransports.StandardInput ? agentPrompt : null,
                includeDeclaredTools: false,
                ct: ct);

            var changes = await DiscoverChangesAsync(staging, baseCommit, ct);
            var validations = new List<WorkspaceValidationResult>();
            var executed = new List<string>();
            var result = CreateEvidence(baseCommit, changes, validations, executed);

            if (agentResult.Cancelled)
            {
                FillSkippedValidations(request, validations);
                return new WorkspaceCodingAgentExecution
                {
                    Status = ProviderRunStatus.Cancelled,
                    Result = result,
                    FailureClass = ProviderFailureClasses.Cancelled,
                    ProviderStatus = "cancelled",
                    Error = "Workspace coding-agent run was cancelled before changes were applied."
                };
            }

            if (agentResult.TimedOut)
            {
                FillSkippedValidations(request, validations);
                return new WorkspaceCodingAgentExecution
                {
                    Status = ProviderRunStatus.TimedOut,
                    Result = result,
                    FailureClass = ProviderFailureClasses.Timeout,
                    ProviderStatus = "timeout",
                    Error = "Workspace coding-agent run timed out before changes were applied."
                };
            }

            if (agentResult.OutputTruncated)
            {
                FillSkippedValidations(request, validations);
                return new WorkspaceCodingAgentExecution
                {
                    Status = ProviderRunStatus.Rejected,
                    Result = result,
                    FailureClass = ProviderFailureClasses.Policy,
                    ProviderStatus = "output_limit",
                    Error = "Workspace coding-agent output exceeded the configured limit; changes were not applied."
                };
            }

            if (agentResult.ExitCode != 0 || !string.IsNullOrWhiteSpace(agentResult.StartError))
            {
                FillSkippedValidations(request, validations);
                return new WorkspaceCodingAgentExecution
                {
                    Status = ProviderRunStatus.Failed,
                    Result = result,
                    FailureClass = ProviderFailureClasses.Tool,
                    ProviderStatus = "agent_process_failed",
                    Error = $"Workspace coding-agent process failed with exit code {agentResult.ExitCode}; changes were not applied."
                };
            }

            // The host later reads the staged paths directly. Reject links before
            // reconciling so a sandbox-created link cannot cause the host to read
            // a file outside the disposable staging tree after Bubblewrap exits.
            EnsureNoSymlinks(staging, "sandbox_symlink_present", "The agent created a symbolic link in the staged workspace; changes were not applied.");

            var stagedHead = (await GetGitOutputAsync(staging, "rev-parse", "HEAD", ct)).Trim();
            if (!string.Equals(baseCommit, stagedHead, StringComparison.Ordinal))
            {
                FillSkippedValidations(request, validations);
                return new WorkspaceCodingAgentExecution
                {
                    Status = ProviderRunStatus.Rejected,
                    Result = result,
                    FailureClass = ProviderFailureClasses.Trust,
                    ProviderStatus = "git_mutation_attempted",
                    Error = "The agent attempted to change Git history; changes were not applied."
                };
            }

            var stagedSnapshot = SnapshotWorkspace(staging);

            foreach (var validationCommand in request.ValidationCommands)
            {
                var tool = ResolveAllowedTool(validationCommand.FileName);
                var validationResult = await RunSandboxedAsync(
                    staging,
                    request,
                    executionRequest,
                    tool,
                    $"/vyral-tools/{validationCommand.FileName}",
                    validationCommand.Arguments,
                    standardInput: null,
                    includeDeclaredTools: true,
                    ct: ct);
                executed.Add(validationCommand.Id);
                var status = validationResult.Cancelled
                    ? WorkspaceValidationStatuses.Skipped
                    : validationResult.TimedOut || validationResult.ExitCode != 0 || validationResult.OutputTruncated || !string.IsNullOrWhiteSpace(validationResult.StartError)
                        ? WorkspaceValidationStatuses.Failed
                        : WorkspaceValidationStatuses.Passed;
                validations.Add(new WorkspaceValidationResult
                {
                    CommandId = validationCommand.Id,
                    Status = status,
                    ExitCode = validationResult.ExitCode,
                    Summary = status == WorkspaceValidationStatuses.Passed ? "passed" : status == WorkspaceValidationStatuses.Skipped ? "skipped" : "failed"
                });

                if (status != WorkspaceValidationStatuses.Passed)
                {
                    FillSkippedValidations(request, validations);
                    return new WorkspaceCodingAgentExecution
                    {
                        Status = validationResult.Cancelled ? ProviderRunStatus.Cancelled : validationResult.TimedOut ? ProviderRunStatus.TimedOut : ProviderRunStatus.Failed,
                        Result = result,
                        FailureClass = validationResult.Cancelled ? ProviderFailureClasses.Cancelled : validationResult.TimedOut ? ProviderFailureClasses.Timeout : ProviderFailureClasses.Tool,
                        ProviderStatus = validationResult.Cancelled ? "validation_cancelled" : validationResult.TimedOut ? "validation_timeout" : "validation_failed",
                        Error = "A declared workspace validation did not pass; changes were not applied."
                    };
                }
            }

            EnsureNoSymlinks(staging, "sandbox_symlink_present", "A validation created a symbolic link in the staged workspace; changes were not applied.");
            if (!SnapshotEquals(stagedSnapshot, SnapshotWorkspace(staging)))
            {
                return new WorkspaceCodingAgentExecution
                {
                    Status = ProviderRunStatus.Rejected,
                    Result = result,
                    FailureClass = ProviderFailureClasses.Trust,
                    ProviderStatus = "validation_mutated_workspace",
                    Error = "A declared validation changed the staged workspace; changes were not applied."
                };
            }

            result.Summary = "Agent changes were sandboxed, validated, reconciled, and applied for review.";
            EnsureResultFitsOutputLimit(result, executionRequest.MaxOutputBytes);
            await ApplyChangesAsync(sourceRoot, staging, baseCommit, request, changes, ct);
            return new WorkspaceCodingAgentExecution { Status = ProviderRunStatus.Succeeded, Result = result, ProviderStatus = "succeeded" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failure("cancelled", "Workspace coding-agent run was cancelled.", ProviderFailureClasses.Cancelled, ProviderRunStatus.Cancelled);
        }
        catch (WorkspaceHostException ex)
        {
            return Failure(ex.Status, ex.Message, ex.FailureClass, ex.RunStatus);
        }
        catch (Exception)
        {
            return Failure("workspace_host_failed", "Workspace coding-agent host failed before applying changes.", ProviderFailureClasses.Unknown);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(staging))
            {
                TryDeleteDirectory(staging);
            }
        }
    }

    private async Task<string> GetCleanWorkspaceRootAsync(string workspaceRoot, CancellationToken ct)
    {
        if (!Directory.Exists(workspaceRoot))
        {
            throw new WorkspaceHostException("workspace_missing", "The requested workspace root does not exist.", ProviderFailureClasses.Configuration);
        }

        var root = (await GetGitOutputAsync(workspaceRoot, "rev-parse", "--show-toplevel", ct)).Trim();
        if (!PathEquals(root, workspaceRoot))
        {
            throw new WorkspaceHostException("workspace_not_git_root", "Workspace coding-agent runs require the requested workspace root to be a Git worktree root.", ProviderFailureClasses.Configuration);
        }

        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            throw new WorkspaceHostException("workspace_gitdir_not_supported", "Workspace coding-agent runs currently require a checkout with an in-tree .git directory.", ProviderFailureClasses.Configuration);
        }

        var status = await GetGitOutputAsync(root, "status", "--porcelain=v1", "--untracked-files=all", ct);
        if (!string.IsNullOrWhiteSpace(status))
        {
            throw new WorkspaceHostException("workspace_not_clean", "Workspace coding-agent runs require a clean Git worktree before staging.", ProviderFailureClasses.Policy);
        }

        return Path.GetFullPath(root);
    }

    private async Task<ProviderProcessRunResult> RunSandboxedAsync(
        string staging,
        WorkspaceCodingAgentRequest request,
        WorkspaceCodingAgentExecutionRequest executionRequest,
        string command,
        string executableDestination,
        IReadOnlyList<string> commandArguments,
        string? standardInput,
        bool includeDeclaredTools,
        CancellationToken ct)
    {
        var arguments = BuildSandboxArguments(staging, request, command, executableDestination, commandArguments, includeDeclaredTools);
        return await _processRunner.RunAsync(new ProviderProcessRunRequest
        {
            Command = _options.BubblewrapCommand,
            Arguments = arguments,
            StandardInput = standardInput,
            Timeout = executionRequest.Timeout,
            MaxOutputBytes = executionRequest.MaxOutputBytes
        }, ct);
    }

    private IReadOnlyList<string> BuildSandboxArguments(
        string staging,
        WorkspaceCodingAgentRequest request,
        string command,
        string executableDestination,
        IReadOnlyList<string> commandArguments,
        bool includeDeclaredTools)
    {
        var arguments = new List<string>
        {
            "--die-with-parent", "--new-session", "--unshare-user", "--disable-userns", "--unshare-pid", "--unshare-uts", "--unshare-ipc", "--cap-drop", "ALL",
            // Bubblewrap applies mounts in order. The tmpfs root must come first
            // so these runtime mounts remain visible to the coding-agent CLI.
            "--tmpfs", "/", "--proc", "/proc", "--dev", "/dev",
            "--dir", "/workspace", "--dir", "/vyral-agent", "--dir", "/vyral-tools", "--dir", "/tmp", "--dir", "/tmp/home"
        };
        if (!request.ToolPolicy.AllowNetwork)
        {
            arguments.Add("--unshare-net");
        }

        var mounts = new List<(string Source, string Destination, bool Writable)>();
        foreach (var runtimePath in _options.RuntimeReadOnlyPaths)
        {
            var source = ResolveExistingPath(runtimePath);
            mounts.Add((source, Path.GetFullPath(runtimePath), false));
        }

        var agentCommand = ResolveExistingPath(command);
        mounts.Add((agentCommand, "/vyral-agent/agent", false));
        AddShebangMounts(agentCommand, mounts);

        if (includeDeclaredTools)
        {
            foreach (var tool in request.ToolPolicy.AllowedCommands)
            {
                var toolPath = ResolveAllowedTool(tool.FileName);
                mounts.Add((toolPath, $"/vyral-tools/{tool.FileName}", false));
            }
        }
        // Declared tools are mounted only for Vyral's direct validation phase.
        // An agent cannot turn a validation allowance into a Git push, merge, or
        // deployment command with different arguments.

        foreach (var directory in GetParentDirectories(mounts.Select(mount => mount.Destination)))
        {
            if (directory is not "/" and not "/workspace" and not "/vyral-agent" and not "/vyral-tools" and not "/tmp" and not "/tmp/home")
            {
                arguments.Add("--dir");
                arguments.Add(directory);
            }
        }

        foreach (var mount in mounts.DistinctBy(mount => mount.Destination, StringComparer.Ordinal))
        {
            arguments.Add("--ro-bind");
            arguments.Add(mount.Source);
            arguments.Add(mount.Destination);
        }

        arguments.Add("--ro-bind");
        arguments.Add(staging);
        arguments.Add("/workspace");
        foreach (var path in request.AllowedPaths.Select(NormalizeRelativePath).OrderBy(path => path.Count(character => character == '/')))
        {
            arguments.Add("--bind");
            arguments.Add(Path.Combine(staging, path == "." ? string.Empty : path));
            arguments.Add(path == "." ? "/workspace" : $"/workspace/{path}");
        }

        foreach (var path in request.AvoidedPaths.Select(NormalizeRelativePath))
        {
            var source = Path.Combine(staging, path);
            var destination = $"/workspace/{path}";
            arguments.Add(Directory.Exists(source) || File.Exists(source) ? "--ro-bind" : "--tmpfs");
            if (Directory.Exists(source) || File.Exists(source))
            {
                arguments.Add(source);
            }

            arguments.Add(destination);
        }

        arguments.Add("--ro-bind");
        arguments.Add(Path.Combine(staging, ".git"));
        arguments.Add("/workspace/.git");
        arguments.Add("--chdir");
        arguments.Add("/workspace");
        arguments.Add("--clearenv");
        arguments.Add("--setenv");
        arguments.Add("HOME");
        arguments.Add("/tmp/home");
        arguments.Add("--setenv");
        arguments.Add("TMPDIR");
        arguments.Add("/tmp");
        arguments.Add("--setenv");
        arguments.Add("PATH");
        arguments.Add(includeDeclaredTools ? "/vyral-agent:/vyral-tools" : "/vyral-agent");
        arguments.Add("--setenv");
        arguments.Add("SHELL");
        arguments.Add("/bin/sh");
        foreach (var (key, value) in _options.Environment)
        {
            if (value is not null)
            {
                arguments.Add("--setenv");
                arguments.Add(key);
                arguments.Add(value);
            }
        }

        arguments.Add("--");
        arguments.Add(executableDestination);
        arguments.AddRange(commandArguments);
        return arguments;
    }

    private async Task<List<WorkspaceChangedPath>> DiscoverChangesAsync(string staging, string baseCommit, CancellationToken ct)
    {
        var changes = new Dictionary<string, WorkspaceChangedPath>(StringComparer.Ordinal);
        var diff = await GetGitOutputAsync(staging, "diff", "--name-status", "-z", baseCommit, "--", ct);
        var tokens = diff.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < tokens.Length;)
        {
            var status = tokens[index++];
            if (status.StartsWith('R') || status.StartsWith('C'))
            {
                if (index + 1 >= tokens.Length) throw new WorkspaceHostException("change_set_unparseable", "Git returned an incomplete rename change set.", ProviderFailureClasses.Trust);
                var previous = NormalizeRelativePath(tokens[index++]);
                var current = NormalizeRelativePath(tokens[index++]);
                changes[previous] = new WorkspaceChangedPath { Path = previous, Kind = "deleted" };
                changes[current] = new WorkspaceChangedPath { Path = current, Kind = "added" };
                continue;
            }

            if (index >= tokens.Length) throw new WorkspaceHostException("change_set_unparseable", "Git returned an incomplete change set.", ProviderFailureClasses.Trust);
            var path = NormalizeRelativePath(tokens[index++]);
            changes[path] = new WorkspaceChangedPath
            {
                Path = path,
                Kind = status.StartsWith('D') ? "deleted" : status.StartsWith('A') ? "added" : "modified"
            };
        }

        var untracked = await GetGitOutputAsync(staging, "ls-files", "--others", "--exclude-standard", "-z", ct);
        foreach (var path in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = NormalizeRelativePath(path);
            changes[normalized] = new WorkspaceChangedPath { Path = normalized, Kind = "untracked" };
        }

        return changes.Values.OrderBy(change => change.Path, StringComparer.Ordinal).ToList();
    }

    private async Task ApplyChangesAsync(
        string sourceRoot,
        string staging,
        string baseCommit,
        WorkspaceCodingAgentRequest request,
        IReadOnlyList<WorkspaceChangedPath> changes,
        CancellationToken ct)
    {
        var sourceHead = (await GetGitOutputAsync(sourceRoot, "rev-parse", "HEAD", ct)).Trim();
        var sourceStatus = await GetGitOutputAsync(sourceRoot, "status", "--porcelain=v1", "--untracked-files=all", ct);
        if (!string.Equals(sourceHead, baseCommit, StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(sourceStatus))
        {
            throw new WorkspaceHostException("workspace_changed_during_run", "The source workspace changed during the agent run; sandbox changes were not applied.", ProviderFailureClasses.Trust);
        }

        var rollbackDirectory = Path.Combine(staging, $".vyral-rollback-{Guid.NewGuid():N}");
        var rollback = new List<WorkspaceFileBackup>();
        var createdDirectories = new List<string>();
        try
        {
            foreach (var changed in changes)
            {
                var path = NormalizeRelativePath(changed.Path);
                if (!IsWithinAllowedPath(path, request.AllowedPaths) || IsWithinAnyAvoidedPath(path, request.AvoidedPaths))
                {
                    throw new WorkspaceHostException("out_of_scope_change", "The sandbox produced a change outside its declared workspace paths; changes were not applied.", ProviderFailureClasses.Trust);
                }

                var sourcePath = Path.Combine(sourceRoot, path);
                var stagingPath = Path.Combine(staging, path);
                if (changed.Kind != "deleted" && !File.Exists(stagingPath))
                {
                    throw new WorkspaceHostException("change_set_missing_file", "The sandbox change set referenced a missing file; changes were not applied.", ProviderFailureClasses.Trust);
                }

                rollback.Add(CreateBackup(sourcePath, rollbackDirectory, path));
                if (changed.Kind == "deleted")
                {
                    if (File.Exists(sourcePath)) File.Delete(sourcePath);
                    continue;
                }

                EnsureDestinationDirectory(sourceRoot, sourcePath, createdDirectories);
                CopyFilePreservingMode(stagingPath, sourcePath);
            }
        }
        catch
        {
            RestoreSourceWorkspace(rollback, createdDirectories);
            throw;
        }
    }

    private static WorkspaceFileBackup CreateBackup(string sourcePath, string rollbackDirectory, string relativePath)
    {
        if (!File.Exists(sourcePath))
        {
            return new WorkspaceFileBackup(sourcePath, null, null);
        }

        var backupPath = Path.Combine(rollbackDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(sourcePath, backupPath, overwrite: false);
        return new WorkspaceFileBackup(sourcePath, backupPath, GetUnixFileMode(sourcePath));
    }

    private static void EnsureDestinationDirectory(string sourceRoot, string sourcePath, List<string> createdDirectories)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var missing = new Stack<string>();
        while (!PathEquals(directory, sourceRoot) && !Directory.Exists(directory))
        {
            missing.Push(directory);
            directory = Path.GetDirectoryName(directory)!;
        }

        while (missing.TryPop(out var path))
        {
            Directory.CreateDirectory(path);
            createdDirectories.Add(path);
        }
    }

    private static void CopyFilePreservingMode(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
        SetUnixFileMode(destination, GetUnixFileMode(source));
    }

    private static void RestoreSourceWorkspace(IEnumerable<WorkspaceFileBackup> rollback, IEnumerable<string> createdDirectories)
    {
        foreach (var backup in rollback.Reverse())
        {
            try
            {
                if (backup.BackupPath is null)
                {
                    if (File.Exists(backup.SourcePath)) File.Delete(backup.SourcePath);
                }
                else
                {
                    File.Copy(backup.BackupPath, backup.SourcePath, overwrite: true);
                    if (backup.UnixMode is not null) SetUnixFileMode(backup.SourcePath, backup.UnixMode.Value);
                }
            }
            catch { }
        }

        foreach (var directory in createdDirectories.Reverse())
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch { }
        }
    }

    private async Task<string> GetGitOutputAsync(string workingDirectory, params object[] values)
    {
        var ct = values.OfType<CancellationToken>().SingleOrDefault();
        var arguments = values.OfType<string>().ToArray();
        var result = await _processRunner.RunAsync(new ProviderProcessRunRequest
        {
            Command = _options.GitCommand,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Timeout = TimeSpan.FromSeconds(_options.PreparationTimeoutSeconds),
            MaxOutputBytes = _options.MaxOutputBytes
        }, ct);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (result.TimedOut) throw new WorkspaceHostException("git_timeout", "Git preflight timed out.", ProviderFailureClasses.Timeout, ProviderRunStatus.TimedOut);
        if (result.OutputTruncated || result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StartError))
        {
            throw new WorkspaceHostException("git_preflight_failed", "Git preflight failed.", ProviderFailureClasses.Configuration);
        }

        return result.StandardOutput;
    }

    private static WorkspaceCodingAgentResult CreateEvidence(string baseCommit, IReadOnlyList<WorkspaceChangedPath> changes, List<WorkspaceValidationResult> validations, List<string> executed) => new()
    {
        BaseCommit = baseCommit,
        ChangeSetReconciled = true,
        ChangedPaths = changes.ToList(),
        Validation = validations,
        ExecutedCommandIds = executed,
        ToolPolicyEnforcement = WorkspaceToolPolicyEnforcements.HostEnforced,
        Summary = "Sandboxed workspace changes are pending validation."
    };

    private static void FillSkippedValidations(WorkspaceCodingAgentRequest request, List<WorkspaceValidationResult> validations)
    {
        foreach (var command in request.ValidationCommands.Where(command => validations.All(validation => !string.Equals(validation.CommandId, command.Id, StringComparison.Ordinal))))
        {
            validations.Add(new WorkspaceValidationResult { CommandId = command.Id, Status = WorkspaceValidationStatuses.Skipped, Summary = "not run" });
        }
    }

    private string ResolveAgentCommand() => ResolveHostExecutable(_options.AgentCommand, new[] { "/usr/local/bin", "/usr/bin", "/bin" });

    private string ResolveAllowedTool(string fileName) => ResolveHostExecutable(fileName, _options.ToolSearchPaths);

    private static string ResolveHostExecutable(string command, IEnumerable<string> searchPaths)
    {
        if (Path.IsPathRooted(command))
        {
            return ResolveExistingPath(command);
        }

        foreach (var directory in searchPaths)
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return ResolveExistingPath(candidate);
            }
        }

        throw new WorkspaceHostException("tool_not_available", "A configured workspace-agent executable is unavailable to the host.", ProviderFailureClasses.Configuration);
    }

    private static string ResolveExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new WorkspaceHostException("runtime_path_missing", "A configured workspace-agent runtime path is unavailable.", ProviderFailureClasses.Configuration);
        }

        FileSystemInfo info = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        return (info.ResolveLinkTarget(returnFinalTarget: true) ?? info).FullName;
    }

    private static void AddShebangMounts(string command, List<(string Source, string Destination, bool Writable)> mounts)
    {
        using var reader = new StreamReader(command, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var firstLine = reader.ReadLine();
        if (firstLine is null || !firstLine.StartsWith("#!", StringComparison.Ordinal)) return;
        var parts = firstLine[2..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Path.IsPathRooted(parts[0])) return;
        var interpreter = ResolveExistingPath(parts[0]);
        mounts.Add((interpreter, parts[0], false));
        if (Path.GetFileName(parts[0]) == "env" && parts.Length > 1)
        {
            var runtime = ResolveHostExecutable(parts[1], new[] { "/usr/local/bin", "/usr/bin", "/bin" });
            mounts.Add((runtime, $"/vyral-agent/{parts[1]}", false));
        }
    }

    private static IEnumerable<string> GetParentDirectories(IEnumerable<string> paths)
    {
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var current = Path.GetDirectoryName(path);
            while (!string.IsNullOrWhiteSpace(current) && current != "/")
            {
                parents.Add(current);
                current = Path.GetDirectoryName(current);
            }
        }

        return parents.OrderBy(path => path.Count(character => character == '/')).ThenBy(path => path, StringComparer.Ordinal);
    }

    private static string ComposeAgentPrompt(WorkspaceCodingAgentRequest request) =>
        $"Work on the assigned task in the mounted workspace. The host, not this prompt, enforces paths, Git protection, network policy, and declared validation execution. Do not attempt commit, push, merge, deployment, or policy bypass. Task:\n{request.Task}";

    private IReadOnlyList<string> BuildAgentArguments(string prompt, string? modelId)
    {
        var transport = NormalizePromptTransport(_options.PromptTransport);
        var arguments = new List<string>();
        foreach (var template in _options.AgentArguments)
        {
            if (template == "{modelArgs}")
            {
                if (!string.IsNullOrWhiteSpace(modelId ?? _options.ModelId))
                {
                    arguments.Add("--model");
                    arguments.Add(modelId ?? _options.ModelId!);
                }

                continue;
            }

            arguments.Add(template.Replace("{prompt}", transport == CliPromptTransports.StandardInput ? "-" : prompt, StringComparison.Ordinal).Replace("{model}", modelId ?? _options.ModelId ?? string.Empty, StringComparison.Ordinal));
        }

        if (transport == CliPromptTransports.Argument && !arguments.Any(argument => argument.Contains(prompt, StringComparison.Ordinal)))
        {
            arguments.Add(prompt);
        }

        return arguments;
    }

    private static string NormalizePromptTransport(string value) => value.Trim().ToLowerInvariant() switch
    {
        CliPromptTransports.Argument => CliPromptTransports.Argument,
        CliPromptTransports.StandardInput or "standardinput" or "standard-input" => CliPromptTransports.StandardInput,
        _ => throw new WorkspaceHostException("invalid_prompt_transport", "Workspace coding-agent prompt transport must be argument or stdin.", ProviderFailureClasses.Configuration)
    };

    private static void PrepareWritablePaths(string staging, IEnumerable<string> allowedPaths)
    {
        foreach (var path in allowedPaths.Select(NormalizeRelativePath))
        {
            if (path == ".") continue;
            var fullPath = Path.Combine(staging, path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }
        }
    }

    private static void PrepareAvoidedPaths(string staging, IEnumerable<string> avoidedPaths)
    {
        foreach (var path in avoidedPaths.Select(NormalizeRelativePath))
        {
            var fullPath = Path.Combine(staging, path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }
        }
    }

    private static void EnsureNoSymlinks(string root, string status = "workspace_symlink_present", string message = "Workspace coding-agent runs reject source worktrees containing symlinks to prevent path escape.")
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new WorkspaceHostException(status, message, ProviderFailureClasses.Policy);
            }

            if (Directory.Exists(path))
            {
                EnsureNoSymlinks(path, status, message);
            }
        }
    }

    private static Dictionary<string, string> SnapshotWorkspace(string root)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(root, entry));
            if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal)) continue;

            if (Directory.Exists(entry))
            {
                snapshot[relative] = "directory";
                continue;
            }

            using var stream = File.OpenRead(entry);
            snapshot[relative] = $"file:{Convert.ToHexString(SHA256.HashData(stream))}:{GetUnixFileMode(entry)}";
        }

        return snapshot;
    }

    private static bool SnapshotEquals(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static void EnsureResultFitsOutputLimit(WorkspaceCodingAgentResult result, int maxOutputBytes)
    {
        if (maxOutputBytes <= 0 || Encoding.UTF8.GetByteCount(ProviderJson.ToJsonObject(result).ToJsonString(ProviderJson.Options)) > maxOutputBytes)
        {
            throw new WorkspaceHostException("output_limit", "Workspace coding-agent result exceeds the requested output limit; changes were not applied.", ProviderFailureClasses.Policy, ProviderRunStatus.Rejected);
        }
    }

    private static UnixFileMode GetUnixFileMode(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The concrete CLI workspace host requires Linux Bubblewrap isolation.");
        return File.GetUnixFileMode(path);
    }

    private static void SetUnixFileMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The concrete CLI workspace host requires Linux Bubblewrap isolation.");
        File.SetUnixFileMode(path, mode);
    }

    private static void CopyWorkspace(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            var target = Path.Combine(destination, name);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
                CopyWorkspace(entry, target);
            }
            else
            {
                File.Copy(entry, target, overwrite: false);
            }
        }
    }

    private bool IsUnderConfiguredRoot(string path) => _options.AllowedWorkspaceRoots.Any(root => IsUnderPath(path, Path.GetFullPath(root)));

    private static bool IsWithinAllowedPath(string path, IEnumerable<string> allowed) => allowed.Select(NormalizeRelativePath).Any(root => IsWithinRelativePath(path, root));
    private static bool IsWithinAnyAvoidedPath(string path, IEnumerable<string> avoided) => avoided.Select(NormalizeRelativePath).Any(root => IsWithinRelativePath(path, root));
    private static bool IsWithinRelativePath(string path, string root) => root == "." || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    private static bool IsUnderPath(string candidate, string root) => PathEquals(candidate, root) || candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
    private static bool IsUnsafeRuntimePath(string path) => UnsafeRuntimePaths.Contains(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), StringComparer.Ordinal);
    private static bool IsCommit(string value) => value.Length is >= 7 and <= 64 && value.All(character => char.IsAsciiHexDigit(character));

    private static string NormalizeRelativePath(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
        {
            throw new WorkspaceHostException("invalid_workspace_path", "Workspace-agent paths must remain relative to the checked-out root.", ProviderFailureClasses.Schema);
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(segment => segment != ".").ToArray();
        return segments.Length == 0 ? "." : string.Join('/', segments);
    }

    private static WorkspaceCodingAgentExecution Failure(string providerStatus, string error, string failureClass, ProviderRunStatus status = ProviderRunStatus.Failed) => new()
    {
        Status = status,
        ProviderStatus = providerStatus,
        Error = error,
        FailureClass = failureClass
    };

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed class WorkspaceHostException : Exception
    {
        public WorkspaceHostException(string status, string message, string failureClass, ProviderRunStatus runStatus = ProviderRunStatus.Failed) : base(message)
        {
            Status = status;
            FailureClass = failureClass;
            RunStatus = runStatus;
        }

        public string Status { get; }
        public string FailureClass { get; }
        public ProviderRunStatus RunStatus { get; }
    }

    private sealed record WorkspaceFileBackup(string SourcePath, string? BackupPath, UnixFileMode? UnixMode);
}
