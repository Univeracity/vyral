using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Cli;
using Vyral.Providers.Local;

// Thin owner entry point over the existing workspace host's namespace/lifetime
// engine. This does not admit tasks, invoke a model, or implement recovery.
if (args.Length != 2 || args[0] != "--profile") return 2;
try
{
    var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    json.Converters.Add(new JsonStringEnumConverter());
    async Task<byte[]> ReadProfile(string path, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        if (file.Length > 16384) throw new ArgumentException("profile_limit");
        var buffer = new byte[16385];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await file.ReadAsync(buffer.AsMemory(length), ct);
            if (count == 0) break;
            length += count;
        }
        if (length > 16384) throw new ArgumentException("profile_limit");
        var data = buffer[..length];
        using var document = JsonDocument.Parse(data);
        var keys = document.RootElement.EnumerateObject().Select(value => value.Name).ToArray();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length) throw new ArgumentException("duplicate_profile_key");
        return data;
    }
    var raw = await ReadProfile(args[1]);
    var p = JsonSerializer.Deserialize<OwnedProfile>(raw, json)!;
    if (!OperatingSystem.IsLinux() || p.SchemaVersion != "vyral.owned-native-runtime.v1" ||
        p.MaxExecutionSeconds is < 1 or > 120 || p.MaxOutputBytes is < 1024 or > 65536)
        throw new ArgumentException("unsupported_profile");
    NativeLifetime.BindToParent();
    var remaining = p.DeadlineUtc - DateTimeOffset.UtcNow;
    if (p.DeadlineUtc.Offset != TimeSpan.Zero || remaining <= TimeSpan.Zero || remaining > TimeSpan.FromSeconds(p.MaxExecutionSeconds))
        throw new ArgumentException("immutable_deadline");
    using var lifetime = new CancellationTokenSource(remaining);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
    async Task Verify(string path, string expected)
    {
        if (!Path.IsPathFullyQualified(path) || !expected.StartsWith("sha256:", StringComparison.Ordinal) || expected.Length != 71)
            throw new ArgumentException("invalid_pin");
        await using var stream = File.OpenRead(path);
        if ("sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, lifetime.Token)) != expected)
            throw new ArgumentException("pin_mismatch");
    }
    await Verify(p.NativeProfileFile, p.NativeProfileSha256);
    await Verify(p.NativeCommand, p.NativeCommandSha256);
    var nativeRaw = await ReadProfile(p.NativeProfileFile, lifetime.Token);
    var native = JsonSerializer.Deserialize<NativeProfile>(nativeRaw, json)!;
    var options = new LlamaCppRuntimeOptions {
        Endpoint = new Uri(native.Endpoint), ModelId = native.ModelId, ModelPath = native.ModelPath,
        ModelSha256 = native.ModelSha256, BuildInfo = native.BuildInfo, TemplateSha256 = native.TemplateSha256,
        TemplateProbeSha256 = native.TemplateProbeSha256, ContextTokens = native.ContextTokens,
        MaxOutputTokens = native.MaxOutputTokens, MaxResponseBytes = native.MaxResponseBytes };
    var binding = new LlamaCppRuntimeSessionFactory(options).BindingId;
    if (binding != p.NativeBindingId || native.ContextTokens > 4096)
        throw new ArgumentException("native_binding");
    await Verify(native.ModelPath, "sha256:" + native.ModelSha256);
    // Native libraries are explicitly pinned, then mounted read-only. No host
    // environment or arbitrary command arguments are accepted by the profile.
    if (!Path.IsPathFullyQualified(p.RuntimeLibraryDirectory) || p.RuntimeLibraryDirectory.Contains(':') ||
        p.RuntimeFiles.Count is < 1 or > 128 || new DirectoryInfo(p.RuntimeLibraryDirectory).LinkTarget is not null)
        throw new ArgumentException("runtime_manifest");
    var runtimeFiles = Directory.GetFiles(p.RuntimeLibraryDirectory).Order(StringComparer.Ordinal).ToArray();
    if (!runtimeFiles.SequenceEqual(p.RuntimeFiles.Select(file => file.Path).Distinct().Order(StringComparer.Ordinal)))
        throw new ArgumentException("runtime_manifest");
    foreach (var file in p.RuntimeFiles)
    {
        var info = new FileInfo(file.Path);
        if (info.LinkTarget is not null && info.ResolveLinkTarget(true)?.FullName is string target &&
            !target.StartsWith(p.RuntimeLibraryDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("runtime_manifest");
        await Verify(file.Path, file.Sha256);
    }
    if (!p.RuntimeFiles.Any(file => file.Path == p.NativeCommand)) throw new ArgumentException("runtime_manifest");
    var runtime = new CliWorkspaceCodingAgentOptions {
        ProviderId = "owned-native-runtime", AgentProfile = "owned-native-runtime-v1",
        AgentCommand = "/usr/bin/env", AgentArguments = new() { "LD_LIBRARY_PATH=" + p.RuntimeLibraryDirectory,
            p.NativeCommand, "--model", native.ModelPath, "--host", options.Endpoint.Host, "--port", options.Endpoint.Port.ToString(),
            "--ctx-size", native.ContextTokens.ToString(), "--parallel", "1", "--no-context-shift", "--no-warmup", "--no-webui",
            "--jinja", "--n-predict", native.MaxOutputTokens.ToString(), "--threads", "4", "--threads-batch", "4", "--n-gpu-layers", "0" },
        PromptTransport = CliPromptTransports.StandardInput, SerializeRuns = true,
        MaxExecutionSeconds = p.MaxExecutionSeconds, MaxOutputBytes = p.MaxOutputBytes,
        AllowedWorkspaceRoots = new() { p.ControlWorkspaceRoot }, StagingRoot = p.StagingRoot,
        BubblewrapCommand = p.BubblewrapCommand, GitCommand = "/usr/bin/git", ToolSearchPaths = new() { "/usr/bin", "/bin" },
        RuntimeReadOnlyPaths = p.RuntimeReadOnlyPaths,
        AgentReadOnlyPaths = new() { p.RuntimeLibraryDirectory, native.ModelPath }, RequiresNetwork = true
    };
    var validator = new WorkspaceCommand { Id = "control-only", FileName = "true", Arguments = new() };
    var request = new WorkspaceCodingAgentRequest { Task = "Serve only this pinned native runtime until the immutable owner deadline.",
        WorkspaceRoot = p.ControlWorkspaceRoot, WriteMode = WorkspaceCodingAgentWriteModes.Write, AllowedPaths = new() { "control" },
        ToolPolicy = new() { Enforcement = WorkspaceToolPolicyEnforcements.HostEnforced, AllowNetwork = true,
            MaxCommands = 1, AllowedCommands = new() { validator } }, ValidationCommands = new() { validator } };
    var seconds = Math.Max(1, (int)Math.Ceiling((p.DeadlineUtc - DateTimeOffset.UtcNow).TotalSeconds));
    lifetime.Token.ThrowIfCancellationRequested();
    Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = "vyral.owned-native-runtime-outcome.v1", state = "starting",
        profileSha256 = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(raw)), nativeProfileSha256 = p.NativeProfileSha256,
        nativeBindingId = binding, deadlineUtc = p.DeadlineUtc }, json));
    var execution = CliProviderTargets.CreateWorkspaceCodingAgent(runtime).RunAsync(
        ProviderRunRequests.ForWorkspaceCodingAgent(request, timeoutSeconds: seconds), lifetime.Token);
    var ready = false;
    try
    {
        while (!execution.IsCompleted && !lifetime.IsCancellationRequested)
        {
            try
            {
                // Public factory validation loads no model tokens: it hashes the
                // model, checks live props and verifies actual template rendering.
                await using var session = await new LlamaCppRuntimeSessionFactory(options).OpenAsync(lifetime.Token);
                ready = true;
                Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = "vyral.owned-native-runtime-outcome.v1",
                    state = "ready", nativeProfileSha256 = p.NativeProfileSha256, nativeBindingId = binding,
                    publicFactoryValidated = true, deadlineUtc = p.DeadlineUtc }, json));
                break;
            }
            catch (HttpRequestException) { await Task.Delay(100, lifetime.Token); }
            catch (LocalScoringException e) when (e.Reason == "runtime_unavailable") { await Task.Delay(100, lifetime.Token); }
        }
    }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    catch { lifetime.Cancel(); await execution; throw; }
    var result = await execution;
    Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = "vyral.owned-native-runtime-outcome.v1", state = "stopped",
        result.Status, result.ProviderStatus, result.FailureClass, ready, remainingProviderEffects = "inspect the original gateway invocation; no resend authorized" }, json));
    return result.Status is ProviderRunStatus.TimedOut or ProviderRunStatus.Cancelled ? 0 : 1;
}
catch (Exception)
{
    // Input paths, credentials and native/model content stay out of errors.
    Console.Error.WriteLine("{\"schemaVersion\":\"vyral.owned-native-runtime-outcome.v1\",\"state\":\"held\"}");
    return 20;
}

sealed record Pin { public required string Path { get; init; } public required string Sha256 { get; init; } }
sealed record OwnedProfile {
    public required string SchemaVersion { get; init; }
    public required string NativeProfileFile { get; init; } public required string NativeProfileSha256 { get; init; }
    public required string NativeCommand { get; init; } public required string NativeCommandSha256 { get; init; }
    public required string NativeBindingId { get; init; } public required string RuntimeLibraryDirectory { get; init; }
    public required List<Pin> RuntimeFiles { get; init; } public required List<string> RuntimeReadOnlyPaths { get; init; }
    public required string ControlWorkspaceRoot { get; init; } public required string StagingRoot { get; init; }
    public required string BubblewrapCommand { get; init; } public required DateTimeOffset DeadlineUtc { get; init; }
    public required int MaxExecutionSeconds { get; init; } public required int MaxOutputBytes { get; init; }
}
sealed record NativeProfile {
    public required string Endpoint { get; init; } public required string ModelId { get; init; }
    public required string ModelPath { get; init; } public required string ModelSha256 { get; init; }
    public required string BuildInfo { get; init; } public required string TemplateSha256 { get; init; }
    public required string TemplateProbeSha256 { get; init; } public required int ContextTokens { get; init; }
    public required int MaxOutputTokens { get; init; } public required int MaxResponseBytes { get; init; }
}
static class NativeLifetime {
    [DllImport("libc", SetLastError = true)] private static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
    [DllImport("libc")] private static extern int getppid();
    public static void BindToParent() {
        var owner = getppid();
        if (owner <= 1 || prctl(1, 9, 0, 0, 0) != 0 || getppid() != owner)
            throw new InvalidOperationException("owner_lifetime_unavailable");
    }
}
