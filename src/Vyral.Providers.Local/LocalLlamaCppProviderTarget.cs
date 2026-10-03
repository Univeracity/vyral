using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Local;

/// <summary>Opt-in bounded native completion. No tools, process launch, downloads or source writes.</summary>
public sealed class LocalLlamaCppProviderTarget : IProviderTarget
{
    private readonly LlamaCppRuntimeSessionFactory _factory;
    private readonly LlamaCppRuntimeOptions _options;
    public LocalLlamaCppProviderTarget(LlamaCppRuntimeOptions options)
    {
        _options = options; _factory = new(options);
        Profile = new() { Id = "local-llamacpp", DisplayName = "Local llama.cpp native completion", Family = "local",
            Local = true, RequiresNetwork = true, Auth = "none", ConfigHash = _factory.BindingId };
        Capabilities = new[] { new ProviderCapabilityDescriptor { Id = ProviderCapabilityIds.AiChat,
            Operations = new() { "run" }, ToolPolicy = ProviderToolPolicies.CallerOwned,
            InputLimits = new() { ["maxContextTokens"] = options.ContextTokens, ["maxPayloadBytes"] = 70000 },
            OutputLimits = new() { ["maxOutputTokens"] = options.MaxOutputTokens, ["maxOutputBytes"] = 262144 },
            ModePolicies = new() { new() { Id = ProviderModes.Advisory, AllowNetwork = true,
                AllowSourceWrites = false, MaxInputBytes = 70000, MaxOutputBytes = 262144, TimeoutSeconds = 120 } },
            UnsupportedFeatures = new() { "tools", "source_writes", "attachments", "multimodal", "internal_retries", "trusted_issuance" } } };
    }
    public ProviderProfile Profile { get; }
    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }

    public async Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew(); int? input = null, generated = null; bool effectStarted = false;
        var trace = new ProviderTraceEvent { Provider = Profile.Id, Capability = request.Capability,
            Operation = request.Operation, Mode = request.Mode, ModelId = _options.ModelId,
            AdapterId = "local-llamacpp-native-v1", ConfigHash = Profile.ConfigHash,
            InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)) };
        ProviderRunResult Result(ProviderRunStatus status, string reason, string? failure = null, JsonObject? output = null)
        {
            trace.DurationMs = clock.Elapsed.TotalMilliseconds; trace.FailureClass = failure;
            trace.OutputHash = output is null ? null : ProviderHash.Sha256(output.ToJsonString(ProviderJson.Options));
            var result = new ProviderRunResult { Provider = Profile.Id, Capability = request.Capability,
                Operation = request.Operation, Mode = request.Mode, Status = status, ProviderStatus = reason,
                FailureClass = failure, Output = output ?? new(), Trace = trace };
            var usage = new JsonObject();
            if (input is not null) usage["input_tokens"] = input;
            if (generated is not null) usage["output_tokens"] = generated;
            result.MeteringMeasurements.AddRange(AiMeteringUsageNormalizer.Normalize(usage, sourceId: "llama.cpp.native"));
            return result;
        }
        if (request.Capability != ProviderCapabilityIds.AiChat || request.Operation != "run" || request.Mode != ProviderModes.Advisory
            || request.ModelId != _options.ModelId || request.ContextRefs.Count != 0 || request.ArtifactDirectory is not null)
            return Result(ProviderRunStatus.Unsupported, "unsupported_request", ProviderFailureClasses.Unsupported);
        if (request.TimeoutSeconds is <= 0 || request.MaxOutputBytes is <= 0)
            return Result(ProviderRunStatus.Rejected, "invalid_request_limits", ProviderFailureClasses.Policy);
        if (Encoding.UTF8.GetByteCount(request.Payload.ToJsonString(ProviderJson.Options)) > 70000)
            return Result(ProviderRunStatus.Rejected, "input_limit", ProviderFailureClasses.Policy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(request.TimeoutSeconds ?? 120, 120)));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (request.Payload.Any(p => p.Key is not ("messages" or "maxOutputTokens" or "maxOutputChars")))
                throw new LocalScoringException("unsupported_payload", ProviderFailureClasses.Unsupported);
            var chat = ProviderJson.DeserializePayload<AiChatRequest>(request);
            if (chat.Messages is null || chat.Messages.Count != 1 || chat.Messages[0] is null
                || chat.Messages[0].Role != AiRoles.User || string.IsNullOrWhiteSpace(chat.Messages[0].Content)
                || chat.Messages[0].ContentBlocks is not null)
                throw new LocalScoringException("single_user_message_required", ProviderFailureClasses.Unsupported);
            if (chat.MaxOutputTokens is not int ceiling || ceiling < 1 || ceiling > _options.MaxOutputTokens
                || chat.MaxOutputChars is <= 0)
                throw new LocalScoringException("explicit_token_ceiling_required", ProviderFailureClasses.Policy);
            await using var session = (LlamaCppRuntimeSession)await _factory.OpenAsync(deadline.Token);
            var prompt = await session.PrepareChatAsync(chat.Messages, deadline.Token);
            if (prompt.TokenIds.Length > session.ContextTokens - ceiling)
                throw new LocalScoringException("context_limit_exceeded", ProviderFailureClasses.Policy);
            deadline.Token.ThrowIfCancellationRequested();
            effectStarted = true;
            var response = await session.CompleteAsync(prompt, ceiling, false, deadline.Token);
            input = Counter(response, "tokens_evaluated"); generated = Counter(response, "tokens_predicted");
            if (input is null || generated is null)
                return Result(ProviderRunStatus.Failed, "invalid_native_usage", ProviderFailureClasses.Schema);
            if (input != prompt.TokenIds.Length || generated > ceiling || response["truncated"]?.GetValue<bool>() != false)
                return Result(ProviderRunStatus.Failed, "native_bound_violated", ProviderFailureClasses.Policy);
            if (response["content"] is not JsonValue value || !value.TryGetValue<string>(out var text))
                return Result(ProviderRunStatus.Failed, "invalid_native_content", ProviderFailureClasses.Schema);
            if (chat.MaxOutputChars is int charLimit && text.Length > charLimit)
                return Result(ProviderRunStatus.Failed, "output_character_limit", ProviderFailureClasses.Policy);
            var output = ProviderJson.ToJsonObject(new AiChatResult { Message = new() { Role = AiRoles.Assistant, Content = text },
                StopReason = response["stop_type"]?.GetValue<string>() ?? "unknown" });
            if (Encoding.UTF8.GetByteCount(output.ToJsonString(ProviderJson.Options)) > Math.Min(request.MaxOutputBytes ?? 262144, 262144))
                return Result(ProviderRunStatus.Failed, "output_limit", ProviderFailureClasses.Policy);
            deadline.Token.ThrowIfCancellationRequested();
            return Result(ProviderRunStatus.Succeeded, "native_completion", output: output);
        }
        catch (LocalScoringException ex) { return Result(ProviderRunStatus.Rejected, ex.Reason, ex.FailureClass); }
        catch (OperationCanceledException)
        {
            return Result(ct.IsCancellationRequested ? ProviderRunStatus.Cancelled : ProviderRunStatus.TimedOut,
                effectStarted ? "native_effect_unresolved" : "no_native_completion_started",
                ct.IsCancellationRequested ? ProviderFailureClasses.Cancelled : ProviderFailureClasses.Timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException or ArgumentException)
        { return Result(ProviderRunStatus.Failed, effectStarted ? "native_effect_unresolved" : "native_preflight_failed", ProviderFailureClasses.Schema); }
    }
    private static int? Counter(JsonObject response, string name) =>
        response[name] is JsonValue v && v.TryGetValue<int>(out var value) && value >= 0 ? value : null;
}
