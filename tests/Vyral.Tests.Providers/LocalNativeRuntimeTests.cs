using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;

namespace Vyral.Tests.Providers;

public class LocalNativeRuntimeTests
{
    [Fact]
    public async Task ExactTokenCeilingTemplateAndModelBindingReachNativeAndTotalUsageIsDerived()
    {
        await using var server = new NativeFixture();
        var result = await new LocalLlamaCppProviderTarget(server.Options).RunAsync(Request(server.Options));
        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(new[] { "/props", "/apply-template", "/apply-template", "/tokenize", "/completion" }, server.Paths);
        Assert.Equal(2, server.Completion!["n_predict"]!.GetValue<int>());
        Assert.False(server.Completion["cache_prompt"]!.GetValue<bool>());
        Assert.Equal(new[] { 10, 11, 12 }, server.Completion["prompt"]!.AsArray().Select(n => n!.GetValue<int>()));
        var total = Assert.Single(result.MeteringMeasurements, m => m.Name == AiMeteringMeasurementNames.TotalTokens);
        Assert.Equal(4, total.Value);
        Assert.Equal(AiMeteringSources.ConsumerInference, total.Source);
        Assert.Equal(AiMeteringQualities.Estimated, total.Quality);
    }

    [Theory]
    [InlineData("build")] [InlineData("template")] [InlineData("context")]
    public async Task ChangedRuntimeBindingRefusesBeforeCompletion(string fault)
    {
        await using var server = new NativeFixture { Fault = fault };
        var result = await new LocalLlamaCppProviderTarget(server.Options).RunAsync(Request(server.Options));
        Assert.Equal("runtime_binding_mismatch", result.ProviderStatus);
        Assert.DoesNotContain("/completion", server.Paths);
    }

    [Fact]
    public async Task ChangedEffectiveTemplateRefusesBeforeCompletion()
    {
        await using var server = new NativeFixture { Fault = "probe" };
        var result = await new LocalLlamaCppProviderTarget(server.Options).RunAsync(Request(server.Options));
        Assert.Equal("template_probe_mismatch", result.ProviderStatus);
        Assert.DoesNotContain("/completion", server.Paths);
    }

    [Theory]
    [InlineData("negative")] [InlineData("missing")] [InlineData("truncated")]
    [InlineData("overflow")] [InlineData("missingContent")]
    public async Task InvalidOrMissingCountersAndContentNeverBecomeSuccessfulOrInventedZeroUsage(string fault)
    {
        await using var server = new NativeFixture { Fault = fault };
        var result = await new LocalLlamaCppProviderTarget(server.Options).RunAsync(Request(server.Options));
        Assert.NotEqual(ProviderRunStatus.Succeeded, result.Status); Assert.Empty(result.Output);
        if (fault is "negative" or "missing")
        {
            Assert.DoesNotContain(result.MeteringMeasurements, m => m.Name == AiMeteringMeasurementNames.InputTokens);
            Assert.DoesNotContain(result.MeteringMeasurements, m => m.Name == AiMeteringMeasurementNames.TotalTokens);
            Assert.Single(result.MeteringMeasurements);
        }
    }

    [Fact]
    public async Task ExplicitTokenCeilingAndOuterByteLimitRemainDifferentControls()
    {
        await using var server = new NativeFixture();
        var request = Request(server.Options); request.Payload.Remove("maxOutputTokens");
        var result = await new LocalLlamaCppProviderTarget(server.Options).RunAsync(request);
        Assert.Equal("explicit_token_ceiling_required", result.ProviderStatus); Assert.Empty(server.Paths);
        request = Request(server.Options); request.MaxOutputBytes = 1;
        result = await new LocalLlamaCppProviderTarget(server.Options).RunAsync(request);
        Assert.Equal("output_limit", result.ProviderStatus);
        Assert.NotEmpty(result.MeteringMeasurements);
    }

    [Fact]
    public async Task ContextBudgetIsCheckedBeforeCompletion()
    {
        await using var server = new NativeFixture { Fault = "longPrompt" };
        var result = await new LocalLlamaCppProviderTarget(server.Options).RunAsync(Request(server.Options));
        Assert.Equal("context_limit_exceeded", result.ProviderStatus);
        Assert.DoesNotContain("/completion", server.Paths);
    }

    [Fact]
    public async Task CallerCancellationBeforeEffectDoesNotInvokeRuntime()
    {
        await using var server = new NativeFixture(); using var ct = new CancellationTokenSource(); ct.Cancel();
        var result = await new LocalLlamaCppProviderTarget(server.Options).RunAsync(Request(server.Options), ct.Token);
        Assert.Equal(ProviderRunStatus.Cancelled, result.Status); Assert.Empty(server.Paths);
    }

    [Fact]
    public async Task CancellationAfterDispatchLeavesNativeEffectUnresolved()
    {
        await using var server = new NativeFixture { Fault = "blockedCompletion" };
        using var cancellation = new CancellationTokenSource();
        var task = new LocalLlamaCppProviderTarget(server.Options).RunAsync(Request(server.Options), cancellation.Token);
        await server.CompletionSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProviderRunStatus.Cancelled, result.Status);
        Assert.Equal("native_effect_unresolved", result.ProviderStatus);
        Assert.Empty(result.Output);
        Assert.DoesNotContain(result.MeteringMeasurements, m => m.Name == AiMeteringMeasurementNames.TotalTokens);
        Assert.Single(server.Paths, path => path == "/completion");
    }

    [LocalRuntimeLiveFact]
    public async Task PinnedLocalRuntimeChatSmoke()
    {
        var options = ReadLiveOptions();
        var result = await new LocalLlamaCppProviderTarget(options).RunAsync(ProviderRunRequests.ForChat(
            new() { Messages = new() { new() { Role = AiRoles.User, Content = "Reply with the single word OK." } }, MaxOutputTokens = 12 },
            modelId: options.ModelId));
        Assert.True(result.Status == ProviderRunStatus.Succeeded, result.ProviderStatus);
        Assert.NotEmpty(ProviderRunResults.GetChat(result).Message.Content);
        Assert.Contains(result.MeteringMeasurements, m => m.Name == AiMeteringMeasurementNames.TotalTokens);
    }

    [LocalRuntimeLiveFact]
    public async Task PinnedLocalRuntimeChoiceScoringSmoke()
    {
        var options = ReadLiveOptions();
        var result = await new LocalLogprobJudgeProviderTarget(new LlamaCppRuntimeSessionFactory(options)).RunAsync(
            ProviderRunRequests.ForJudge(new() { Context = "A cat is an animal.", Questions = new() { new() {
                Id = "q", Prompt = "What is a cat?", Options = new() {
                    new() { Id = "animal", Label = "An animal" }, new() { Id = "plant", Label = "A plant" }
                } } } }, modelId: options.ModelId));
        Assert.True(result.Status == ProviderRunStatus.Succeeded, result.ProviderStatus);
        var answer = ProviderRunResults.GetJudge(result).Answers.Single();
        Assert.False(answer.Calibrated); Assert.InRange(answer.LabelMassCoverage!.Value, 0, 1);
        Assert.InRange(answer.RotationAgreement!.Value, 0, 1); Assert.Equal(1, answer.Probabilities!.Values.Sum(), 8);
    }

    private static LlamaCppRuntimeOptions ReadLiveOptions() => JsonSerializer.Deserialize<LlamaCppRuntimeOptions>(
        File.ReadAllText(Environment.GetEnvironmentVariable("VYRAL_LOCAL_RUNTIME_PROFILE")!), ProviderJson.Options)!;
    private static ProviderRunRequest Request(LlamaCppRuntimeOptions options) =>
        ProviderRunRequests.ForChat(new() { Messages = new() { new() { Role = AiRoles.User, Content = "Generic fixture prompt." } },
            MaxOutputTokens = 2 }, modelId: options.ModelId);

    private sealed class NativeFixture : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly string _file = Path.GetTempFileName();
        public List<string> Paths { get; } = new();
        public JsonObject? Completion;
        public TaskCompletionSource CompletionSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _shutdown = new();
        public string? Fault;
        public LlamaCppRuntimeOptions Options { get; }
        public NativeFixture()
        {
            File.WriteAllText(_file, "generic model fixture");
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            var endpoint = new Uri($"http://127.0.0.1:{port}/");
            Options = new() { Endpoint = endpoint, ModelId = "fixture", ModelPath = Path.GetFullPath(_file),
                ModelSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_file))), BuildInfo = "fixture-build",
                TemplateSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("fixture-template"))),
                TemplateProbeSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("templated fixture"))),
                ContextTokens = 8, MaxOutputTokens = 4 };
            _listener.Prefixes.Add(endpoint.ToString()); _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var context = await _listener.GetContextAsync(); var path = context.Request.Url!.AbsolutePath;
                        Paths.Add(path);
                        using var reader = new StreamReader(context.Request.InputStream);
                        var body = await reader.ReadToEndAsync();
                        if (path == "/completion")
                        {
                            Completion = JsonNode.Parse(body)!.AsObject();
                            CompletionSeen.TrySetResult();
                            if (Fault == "blockedCompletion") await Task.Delay(Timeout.Infinite, _shutdown.Token);
                        }
                        JsonObject response = path switch {
                            "/props" => new() { ["model_path"] = Options.ModelPath, ["build_info"] = Fault == "build" ? "other" : "fixture-build",
                                ["chat_template"] = Fault == "template" ? "other" : "fixture-template",
                                ["default_generation_settings"] = new JsonObject { ["n_ctx"] = Fault == "context" ? 9 : 8 }, ["total_slots"] = 1 },
                            "/apply-template" => new() { ["prompt"] = Fault == "probe" ? "changed renderer" : "templated fixture" },
                            "/tokenize" => new() { ["tokens"] = Fault == "longPrompt" ? new JsonArray(1,2,3,4,5,6,7) : new JsonArray(10,11,12) },
                            _ => new() { ["content"] = "OK", ["tokens_evaluated"] = 3, ["tokens_predicted"] = 1, ["truncated"] = false }
                        };
                        if (path == "/completion")
                        {
                            if (Fault == "negative") response["tokens_evaluated"] = -1;
                            if (Fault == "missing") response.Remove("tokens_evaluated");
                            if (Fault == "truncated") response["truncated"] = true;
                            if (Fault == "overflow") response["tokens_predicted"] = 3;
                            if (Fault == "missingContent") response.Remove("content");
                        }
                        var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
                        context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
            });
        }
        public async ValueTask DisposeAsync() { _shutdown.Cancel(); _listener.Close(); await _loop.WaitAsync(TimeSpan.FromSeconds(2)); _shutdown.Dispose(); File.Delete(_file); }
    }
}

public sealed class LocalRuntimeLiveFactAttribute : FactAttribute
{
    public LocalRuntimeLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_LOCAL_RUNTIME_PROFILE")))
            Skip = "Set VYRAL_LOCAL_RUNTIME_PROFILE to a private pinned local-runtime profile.";
    }
}
