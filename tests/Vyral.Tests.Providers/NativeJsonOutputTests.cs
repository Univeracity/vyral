using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;

namespace Vyral.Tests.Providers;

// No sockets, native process or actual model. The internal transport seam routes
// the real factory and target through an independently authored HTTP handler.
public class NativeJsonOutputTests
{
    [Fact]
    public async Task ProgressObservesActualPreparationWithOneImmutableDeadlineAndNoContent()
    {
        using var fixture = new Fixture(); var handler = fixture.Handler(); var options = fixture.Options();
        var observations = new List<ProviderProgressObservation>(); var expiry = DateTimeOffset.UtcNow.AddSeconds(60);
        var result = await fixture.Target(options, handler).RunWithProgressAsync(Request(options),
            (item, _) => { observations.Add(item); return ValueTask.CompletedTask; }, expiry);
        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(new[] { ProviderProgressPhase.RuntimeBinding, ProviderProgressPhase.PromptPreparation,
            ProviderProgressPhase.PromptPrepared, ProviderProgressPhase.CompletionPending, ProviderProgressPhase.CompletionValidation },
            observations.Select(item => item.Phase));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, observations.Select(item => item.Sequence));
        Assert.All(observations, item => { Assert.Equal(expiry, item.DeadlineUtc);
            Assert.Equal(observations[0].StartedAtUtc, item.StartedAtUtc);
            Assert.InRange(item.ObservedAtUtc, item.StartedAtUtc, expiry); });
        Assert.Null(observations[0].RuntimeContextTokens); Assert.Null(observations[1].PromptTokens);
        Assert.Equal(8, observations[2].RuntimeContextTokens); Assert.Equal(3, observations[2].PromptTokens);
        Assert.Equal(2, observations[2].ReservedOutputTokens); Assert.True(observations[2].PromptFitsContext);
        Assert.Single(handler.Paths, item => item == "/completion");
        var json = JsonSerializer.Serialize(observations);
        Assert.DoesNotContain("synthetic", json); Assert.DoesNotContain("prompt\"", json);
        Assert.DoesNotContain("content", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ProviderProgressPhase.PromptPrepared, false)]
    [InlineData(ProviderProgressPhase.CompletionPending, false)]
    [InlineData(ProviderProgressPhase.CompletionValidation, true)]
    public async Task FailedProgressRecorderCannotDispatchOrReplay(ProviderProgressPhase failAt, bool dispatched)
    {
        using var fixture = new Fixture(); var handler = fixture.Handler(); var options = fixture.Options();
        var result = await fixture.Target(options, handler).RunWithProgressAsync(Request(options), (item, _) =>
        {
            if (item.Phase == failAt) throw new IOException("PRIVATE-RECORDER-SENTINEL");
            return ValueTask.CompletedTask;
        });
        Assert.Equal(dispatched ? ProviderRunStatus.Failed : ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(dispatched ? "native_effect_unresolved" : "progress_observation_failed", result.ProviderStatus);
        Assert.Equal("progress_observation_failed", result.Rejection!.Category);
        Assert.Equal(dispatched ? 1 : 0, handler.Paths.Count(item => item == "/completion"));
        Assert.False(result.Rejection.Retryable);
        Assert.DoesNotContain("PRIVATE-RECORDER-SENTINEL", ProviderJson.ToJsonObject(result).ToJsonString());
    }

    [Fact]
    public async Task ProgressDeadlineExpiryDuringPreparationCannotDispatchCompletion()
    {
        using var fixture = new Fixture(); var handler = fixture.Handler(); var options = fixture.Options();
        var result = await fixture.Target(options, handler).RunWithProgressAsync(Request(options), async (item, ct) =>
        {
            if (item.Phase == ProviderProgressPhase.PromptPrepared) await Task.Delay(Timeout.Infinite, ct);
        }, DateTimeOffset.UtcNow.AddMilliseconds(200));
        Assert.Equal(ProviderRunStatus.TimedOut, result.Status);
        Assert.Equal("no_native_completion_started", result.ProviderStatus);
        Assert.DoesNotContain("/completion", handler.Paths);
    }

    [Fact]
    public async Task OriginalDeadlineRefusesExpiredAndNonUtcAndNeverWidensProviderPolicy()
    {
        using var fixture = new Fixture(); var options = fixture.Options();
        foreach (var expiry in new[] { DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(1)).AddSeconds(60) })
        {
            var handler = fixture.Handler();
            var result = await fixture.Target(options, handler).RunWithProgressAsync(Request(options), null, expiry);
            Assert.NotEqual(ProviderRunStatus.Succeeded, result.Status); Assert.Empty(handler.Paths);
        }
        var observations = new List<ProviderProgressObservation>(); var active = fixture.Handler();
        var completed = await fixture.Target(options, active).RunWithProgressAsync(Request(options),
            (item, _) => { observations.Add(item); return ValueTask.CompletedTask; }, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(ProviderRunStatus.Succeeded, completed.Status);
        Assert.All(observations, item => Assert.Equal(TimeSpan.FromSeconds(120), item.DeadlineUtc - item.StartedAtUtc));
    }

    [Theory]
    [InlineData("{\"private\":1,}", "parse_or_depth_invalid")]
    [InlineData("[]", "non_object_root")]
    [InlineData("{\"secret\":1,\"secret\":2}", "duplicate_property")]
    [InlineData("{\"secret\":1e400}", "nonfinite_number")]
    [InlineData("{\"secret\":\"\\uD800\"}", "invalid_unicode")]
    public async Task JsonRefusalCategoryIsClosedAndNeverExposesContent(string content, string reason)
    {
        using var fixture = new Fixture(); var handler = fixture.Handler(); handler.Content = content;
        var options = fixture.Options("json-object-v1");
        var result = await fixture.Target(options, handler).RunAsync(Request(options));
        Assert.Equal("native_json_object_invalid", result.ProviderStatus);
        Assert.Equal(reason, result.Rejection!.Category);
        Assert.Equal(reason, result.Rejection.StructuredOutputValidationStatus);
        AssertHeld(result, handler); AssertUsage(result);
        var evidence = ProviderJson.ToJsonObject(result).ToJsonString();
        Assert.DoesNotContain("secret", evidence); Assert.DoesNotContain("private", evidence);
        Assert.Equal(NativeJsonObjectOutput.IsCompleteObject(content), NativeJsonObjectOutput.RejectionReason(content) is null);
    }

    [Fact]
    public void JsonDiagnosticPreservesDepthAndStrictUnicodeRefusal()
    {
        var nested = "{\"secret\":" + new string('[', 65) + "0" + new string(']', 65) + "}";
        Assert.Equal("parse_or_depth_invalid", NativeJsonObjectOutput.RejectionReason(nested));
        Assert.Equal("invalid_unicode", NativeJsonObjectOutput.RejectionReason("{\"a\":\"" + '\ud800' + "\"}"));
        Assert.Null(NativeJsonObjectOutput.RejectionReason("{\"a\":[1,true,null,{}]}"));
    }

    [Fact]
    public async Task DefaultAndExplicitTextRetainLegacyBindingAndExactCompletionWire()
    {
        using var fixture = new Fixture();
        var options = fixture.Options();
        var expected = ProviderHash.Sha256($"llamacpp-native-v1|{options.Endpoint}|{options.ModelId}|{options.ModelSha256}|{options.BuildInfo}|{options.TemplateSha256}|{options.TemplateProbeSha256}|{options.ContextTokens}|{options.MaxOutputTokens}|{options.MaxResponseBytes}");
        Assert.Equal(expected, new LlamaCppRuntimeSessionFactory(options).BindingId);
        Assert.Equal(expected, new LlamaCppRuntimeSessionFactory(fixture.Options("text")).BindingId);
        foreach (var mode in new[] { "text", "json-object-v1" })
        {
            var handler = fixture.Handler();
            using var session = new SessionScope(fixture.Options(mode), handler);
            await session.Value.CompleteAsync(new(new[] { 10, 11, 12 }), 2, mode == "json-object-v1", default);
            // In JSON mode this call is scoring, which remains unconstrained.
            var expectedPayload = new JsonObject { ["prompt"] = new JsonArray(10, 11, 12), ["n_predict"] = 2,
                ["cache_prompt"] = false, ["stream"] = false, ["temperature"] = -1, ["seed"] = 0,
                ["n_probs"] = mode == "text" ? 0 : 256, ["post_sampling_probs"] = false,
                ["samplers"] = new JsonArray(), ["return_tokens"] = true };
            Assert.Equal(expectedPayload.ToJsonString(ProviderJson.Options), handler.Payload!.ToJsonString(ProviderJson.Options));
        }
    }

    [Fact]
    public async Task JsonModeBindsFixedPolicySendsOnlyFixedGrammarAndPreservesOutputAndUsage()
    {
        using var fixture = new Fixture(); var handler = fixture.Handler();
        handler.Content = " \r\n{\"a\":[1,true,null,\"quoted \\\" string\"],\"o\":{}}\t";
        var options = fixture.Options("json-object-v1"); var target = fixture.Target(options, handler);
        Assert.NotEqual(new LlamaCppRuntimeSessionFactory(fixture.Options()).BindingId, target.Profile.ConfigHash);
        Assert.Equal("json-object-v1", target.Capabilities[0].OutputLimits["chatOutputFormat"]);
        Assert.Equal(NativeJsonObjectOutput.PolicySha256, target.Capabilities[0].OutputLimits["constraintPolicySha256"]);
        var result = await target.RunAsync(Request(options));
        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(handler.Content, result.Output["message"]!["content"]!.GetValue<string>());
        Assert.Equal(NativeJsonObjectOutput.Grammar, handler.Payload!["grammar"]!.GetValue<string>());
        Assert.False(handler.Payload["grammar_lazy"]!.GetValue<bool>());
        Assert.False(handler.Payload["backend_sampling"]!.GetValue<bool>());
        Assert.Null(handler.Payload["json_schema"]); Assert.Equal(0, handler.Payload["n_probs"]!.GetValue<int>());
        Assert.Equal(2, handler.Payload["n_predict"]!.GetValue<int>()); Assert.Empty(handler.Payload["samplers"]!.AsArray());
        Assert.Single(handler.Paths, p => p == "/completion"); AssertUsage(result);
    }

    [Fact]
    public async Task PublicNativeScoringResultsAndWireRemainIdenticalAcrossChatModes()
    {
        using var fixture = new Fixture(); var scores = new List<LocalDecisionScore>(); var payloads = new List<string>();
        foreach (var mode in new[] { "text", "json-object-v1" })
        {
            var handler = fixture.Handler(); using var session = new SessionScope(fixture.Options(mode), handler);
            scores.Add(await session.Value.ScoreAsync(new(new[] { 10, 11, 12 }),
                new[] { new LocalDecisionToken(42, Encoding.UTF8.GetBytes("A")) }, default));
            payloads.Add(handler.Payload!.ToJsonString(ProviderJson.Options));
            Assert.Null(handler.Payload["grammar"]); Assert.Single(handler.Paths);
        }
        Assert.Equal(payloads[0], payloads[1]);
        Assert.Equal(ProviderJson.ToJsonObject(scores[0]).ToJsonString(), ProviderJson.ToJsonObject(scores[1]).ToJsonString());
        Assert.Single(scores[0].Labels);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("json")] [InlineData("JSON-OBJECT-V1")]
    [InlineData(" json-object-v1")] [InlineData("text\n")]
    public void UnknownOrNullModeFailsConfigurationBeforeTransport(string? mode)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Target(fixture.Options(mode!), fixture.Handler()));
    }

    [Theory]
    [InlineData("null")] [InlineData("false")] [InlineData("1")] [InlineData("[]")]
    [InlineData("{}")][InlineData("\"unknown\"")]
    public void ClosedOwnedProfileRefusesInvalidModeTypesOrVocabulary(string value)
    {
        using var fixture = new Fixture(); var raw = Profile(fixture.Options()); raw["chatOutputFormat"] = JsonNode.Parse(value);
        Assert.ThrowsAny<Exception>(() => new LlamaCppRuntimeSessionFactory(ParseProfile(raw).ToOptions()));
    }

    [Fact]
    public void OwnedProfileOmissionAndExplicitTextPreserveBindingAndUnknownPropertyRefuses()
    {
        using var fixture = new Fixture(); var raw = Profile(fixture.Options());
        var legacy = new LlamaCppRuntimeSessionFactory(fixture.Options()).BindingId;
        Assert.Equal(legacy, new LlamaCppRuntimeSessionFactory(ParseProfile(raw).ToOptions()).BindingId);
        raw["chatOutputFormat"] = "text";
        Assert.Equal(legacy, new LlamaCppRuntimeSessionFactory(ParseProfile(raw).ToOptions()).BindingId);
        raw["chatOutputFormat"] = "json-object-v1";
        Assert.NotEqual(legacy, new LlamaCppRuntimeSessionFactory(ParseProfile(raw).ToOptions()).BindingId);
        raw["grammar"] = "caller controlled";
        Assert.Throws<JsonException>(() => ParseProfile(raw));
    }

    [Theory]
    [InlineData("missing")] [InlineData("wrong")] [InlineData("null")] [InlineData("lazy")]
    [InlineData("samplers")] [InlineData("backend")] [InlineData("types")]
    public async Task UnconfirmedConstraintHoldsKnownUsageWithoutFallbackRetryOrOutput(string fault)
    {
        using var fixture = new Fixture(); var handler = fixture.Handler(); handler.Fault = fault;
        var options = fixture.Options("json-object-v1");
        var result = await fixture.Target(options, handler).RunAsync(Request(options));
        Assert.Equal("native_output_constraint_unconfirmed", result.ProviderStatus);
        Assert.Equal(ProviderFailureClasses.Unsupported, result.FailureClass); AssertHeld(result, handler); AssertUsage(result);
    }

    [Theory]
    [InlineData("{\"a\":1,}")] [InlineData("{\"a\":\"a\"+\"b\"}")]
    [InlineData("```json\n{}\n```")] [InlineData("prose {}")][InlineData("{}{}")]
    [InlineData("{} ")] [InlineData("[]")][InlineData("null")][InlineData("{\"a\":NaN}")]
    [InlineData("{\"a\":1e400}")] [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":{\"x\":1,\"x\":2}}")] [InlineData("{\"a\":1,\"\\u0061\":2}")]
    [InlineData("{\"a\":\"\\uD800\"}")] [InlineData("{\"a\":1/*comment*/}")]
    [InlineData("{\"a\":01}")][InlineData("{\"a\":1")]
    public async Task InvalidObjectIsHeldUnmodifiedWithKnownUsage(string content)
    {
        using var fixture = new Fixture(); var handler = fixture.Handler(); handler.Content = content;
        var options = fixture.Options("json-object-v1"); var result = await fixture.Target(options, handler).RunAsync(Request(options));
        Assert.Equal("native_json_object_invalid", result.ProviderStatus); AssertHeld(result, handler); AssertUsage(result);
    }

    [Fact]
    public void DeepObjectsAndInvalidOriginalUnicodeRefuseInsteadOfRepairing()
    {
        Assert.False(NativeJsonObjectOutput.IsCompleteObject(new string('{', 65).Replace("{", "{\"a\":") + "0" + new string('}', 65)));
        Assert.False(NativeJsonObjectOutput.IsCompleteObject("{\"x\":\"" + '\ud800' + "\"}"));
        Assert.True(NativeJsonObjectOutput.IsCompleteObject("{\"emoji\":\"\\uD83D\\uDE00\",\"n\":-1.25e+2}"));
    }

    [Fact]
    public async Task TextModeStillAcceptsNonJsonAndDoesNotRequireEcho()
    {
        using var fixture = new Fixture(); var handler = fixture.Handler(); handler.Content = "ordinary text"; handler.Fault = "missing";
        var options = fixture.Options(); var result = await fixture.Target(options, handler).RunAsync(Request(options));
        Assert.Equal(ProviderRunStatus.Succeeded, result.Status); Assert.Equal(handler.Content, result.Output["message"]!["content"]!.GetValue<string>());
        Assert.Null(handler.Payload!["grammar"]); Assert.DoesNotContain("chatOutputFormat", fixture.Target(options, fixture.Handler()).Capabilities[0].OutputLimits.Keys);
    }

    [Theory]
    [InlineData("token")] [InlineData("context")] [InlineData("timeout")] [InlineData("payload")]
    [InlineData("inputBytes")]
    public async Task LimitsAndCallerConstraintInjectionRefuseBeforeCompletion(string fault)
    {
        using var fixture = new Fixture(); var handler = fixture.Handler(); var options = fixture.Options("json-object-v1");
        var request = Request(options);
        if (fault == "token") request.Payload["maxOutputTokens"] = 6;
        if (fault == "context") handler.TokenCount = 7;
        if (fault == "timeout") request.TimeoutSeconds = 121;
        if (fault == "payload") request.Payload["grammar"] = "caller";
        if (fault == "inputBytes") request.Payload["messages"]![0]!["content"] = new string('x', 70001);
        var result = await fixture.Target(options, handler).RunAsync(request);
        Assert.NotEqual(ProviderRunStatus.Succeeded, result.Status); Assert.DoesNotContain("/completion", handler.Paths);
        Assert.Empty(result.MeteringMeasurements); Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData("chars")] [InlineData("bytes")] [InlineData("truncated")] [InlineData("counter")]
    [InlineData("http")][InlineData("responseBytes")]
    public async Task ExistingOutputUsageAndTransportGuardsStillHoldWithoutRetry(string fault)
    {
        using var fixture = new Fixture(); var handler = fixture.Handler(); var options = fixture.Options("json-object-v1");
        var request = Request(options); handler.Fault = fault;
        if (fault == "chars") request.Payload["maxOutputChars"] = 1;
        if (fault == "bytes") request.MaxOutputBytes = 1;
        if (fault == "responseBytes") handler.Content = new string('x', 1048576);
        var result = await fixture.Target(options, handler).RunAsync(request); AssertHeld(result, handler);
        if (fault is "chars" or "bytes") AssertUsage(result); else Assert.Empty(result.MeteringMeasurements);
    }

    [Fact]
    public async Task CallerCancellationBeforeDispatchHasNoCompletionOrUsage()
    {
        using var fixture = new Fixture(); var handler = fixture.Handler(); var options = fixture.Options("json-object-v1");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await fixture.Target(options, handler).RunAsync(Request(options), cancel.Token);
        Assert.Equal(ProviderRunStatus.Cancelled, result.Status); Assert.Empty(handler.Paths); Assert.Empty(result.MeteringMeasurements);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task CancellationAndOriginalDeadlineAfterSendLeaveOneUnresolvedEffectUnknownUsage(bool caller)
    {
        using var fixture = new Fixture(); var handler = fixture.Handler(); handler.Fault = "wait";
        var options = fixture.Options("json-object-v1"); using var cancel = new CancellationTokenSource();
        if (caller) cancel.CancelAfter(100); var request = Request(options); request.TimeoutSeconds = 1;
        var result = await fixture.Target(options, handler).RunAsync(request, cancel.Token);
        Assert.Equal(caller ? ProviderRunStatus.Cancelled : ProviderRunStatus.TimedOut, result.Status);
        Assert.Equal("native_effect_unresolved", result.ProviderStatus); AssertHeld(result, handler); Assert.Empty(result.MeteringMeasurements);
    }

    private static void AssertUsage(ProviderRunResult result) =>
        Assert.Equal(4, Assert.Single(result.MeteringMeasurements, m => m.Name == AiMeteringMeasurementNames.TotalTokens).Value);
    private static void AssertHeld(ProviderRunResult result, FixtureHandler handler)
    {
        Assert.NotEqual(ProviderRunStatus.Succeeded, result.Status); Assert.Empty(result.Output);
        Assert.Single(handler.Paths, p => p == "/completion"); Assert.False(result.Rejection!.Retryable);
        Assert.Equal(ProviderRetryRecommendations.DoNotRetry, result.Rejection.RetryRecommendation);
        var json = ProviderJson.ToJsonObject(result).ToJsonString();
        Assert.DoesNotContain("PRIVATE-SENTINEL", json); Assert.DoesNotContain(handler.ModelPath, json);
    }
    private static ProviderRunRequest Request(LlamaCppRuntimeOptions options) => new() { Capability = ProviderCapabilityIds.AiChat,
        Operation = "run", Mode = ProviderModes.Advisory, ModelId = options.ModelId, TimeoutSeconds = 120, MaxOutputBytes = 262144,
        Payload = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "synthetic" }), ["maxOutputTokens"] = 2 } };
    private static NativeProfile ParseProfile(JsonObject raw) => JsonSerializer.Deserialize<NativeProfile>(raw.ToJsonString(), new JsonSerializerOptions {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })!;
    private static JsonObject Profile(LlamaCppRuntimeOptions o) => new() { ["endpoint"] = o.Endpoint.ToString(), ["modelId"] = o.ModelId,
        ["modelPath"] = o.ModelPath, ["modelSha256"] = o.ModelSha256, ["buildInfo"] = o.BuildInfo, ["templateSha256"] = o.TemplateSha256,
        ["templateProbeSha256"] = o.TemplateProbeSha256, ["contextTokens"] = o.ContextTokens, ["maxOutputTokens"] = o.MaxOutputTokens, ["maxResponseBytes"] = o.MaxResponseBytes };

    private sealed class SessionScope(LlamaCppRuntimeOptions options, HttpMessageHandler handler) : IDisposable
    {
        public LlamaCppRuntimeSession Value { get; } = new(options, handler);
        public void Dispose() => Value.DisposeAsync().GetAwaiter().GetResult();
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "vyral-native-json-" + Guid.NewGuid());
        public Fixture() { Directory.CreateDirectory(_dir); File.WriteAllText(Path.Combine(_dir, "synthetic.bin"), "not a model"); }
        public LlamaCppRuntimeOptions Options(string mode = "text") => new() { Endpoint = new Uri("http://127.0.0.1:1/"), ModelId = "synthetic",
            ModelPath = Path.Combine(_dir, "synthetic.bin"), ModelSha256 = Hash("not a model"), BuildInfo = "fixture", TemplateSha256 = Hash("template"),
            TemplateProbeSha256 = Hash("probe"), ContextTokens = 8, MaxOutputTokens = 5, ChatOutputFormat = mode };
        public FixtureHandler Handler() => new() { ModelPath = Path.Combine(_dir, "synthetic.bin") };
        public LocalLlamaCppProviderTarget Target(LlamaCppRuntimeOptions options, FixtureHandler handler) => new(options, 120, () => handler);
        public void Dispose() => Directory.Delete(_dir, true);
        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
    private sealed class FixtureHandler : HttpMessageHandler
    {
        public required string ModelPath { get; init; }
        public List<string> Paths { get; } = new(); public JsonObject? Payload { get; private set; }
        public string Content { get; set; } = "{}"; public string? Fault { get; set; } public int TokenCount { get; set; } = 3;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            var payload = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!.AsObject();
            JsonObject output;
            if (path == "/props") output = new() { ["model_path"] = ModelPath, ["build_info"] = "fixture", ["chat_template"] = "template",
                ["default_generation_settings"] = new JsonObject { ["n_ctx"] = 8 }, ["total_slots"] = 1 };
            else if (path == "/apply-template") output = new() { ["prompt"] = payload!["messages"]![0]!["content"]!.GetValue<string>() == "vyral-template-binding-v1" ? "probe" : "prompt" };
            else if (path == "/tokenize") output = new() { ["tokens"] = new JsonArray(Enumerable.Range(10, TokenCount).Select(i => JsonValue.Create(i)).ToArray()) };
            else if (path == "/completion")
            {
                Payload = payload;
                if (Fault == "wait") await Task.Delay(Timeout.Infinite, ct);
                if (Fault == "http") return new(HttpStatusCode.BadRequest) { Content = new StringContent("PRIVATE-SENTINEL") };
                var settings = new JsonObject { ["grammar"] = payload!["grammar"]?.DeepClone(), ["grammar_lazy"] = false,
                    ["samplers"] = new JsonArray(), ["backend_sampling"] = false };
                if (Fault == "wrong") settings["grammar"] = "PRIVATE-SENTINEL";
                if (Fault == "null") settings["grammar"] = null;
                if (Fault == "lazy") settings["grammar_lazy"] = true;
                if (Fault == "samplers") settings["samplers"] = new JsonArray("temperature");
                if (Fault == "backend") settings["backend_sampling"] = true;
                if (Fault == "types") settings["grammar_lazy"] = "false";
                output = new() { ["content"] = Content, ["tokens_evaluated"] = 3, ["tokens_predicted"] = 1,
                    ["truncated"] = Fault == "truncated", ["stop_type"] = "eos", ["generation_settings"] = settings };
                if (payload["n_probs"]!.GetValue<int>() == 256) output["completion_probabilities"] = new JsonArray(new JsonObject {
                    ["top_logprobs"] = new JsonArray(new JsonObject { ["id"] = 42, ["bytes"] = new JsonArray(65), ["logprob"] = -0.5 }) });
                if (Fault == "missing") output.Remove("generation_settings");
                if (Fault == "counter") output.Remove("tokens_predicted");
            }
            else throw new InvalidOperationException("Unexpected fixture route.");
            return new(HttpStatusCode.OK) { Content = new StringContent(output.ToJsonString(ProviderJson.Options), Encoding.UTF8, "application/json") };
        }
    }
}
