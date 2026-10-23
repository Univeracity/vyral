using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Local;

/// <summary>Opt-in bounded native completion. No tools, process launch, downloads or source writes.</summary>
public sealed class LocalLlamaCppProviderTarget : IProviderProgressTarget
{
    public const int DefaultExecutionSeconds = 120;
    public const int MaximumExecutionSeconds = 3600;
    private readonly LlamaCppRuntimeSessionFactory _factory;
    private readonly LlamaCppRuntimeOptions _options;
    public LocalLlamaCppProviderTarget(LlamaCppRuntimeOptions options) : this(options, DefaultExecutionSeconds) { }
    public LocalLlamaCppProviderTarget(LlamaCppRuntimeOptions options, int maxExecutionSeconds)
        : this(options, maxExecutionSeconds, null) { }
    internal LocalLlamaCppProviderTarget(LlamaCppRuntimeOptions options, int maxExecutionSeconds,
        Func<HttpMessageHandler>? handlerFactory)
    {
        if (maxExecutionSeconds is < 1 or > MaximumExecutionSeconds)
            throw new ArgumentOutOfRangeException(nameof(maxExecutionSeconds));
        MaxExecutionSeconds = maxExecutionSeconds;
        _options = options; _factory = new(options, handlerFactory);
        Profile = new() { Id = "local-llamacpp", DisplayName = "Local llama.cpp native completion", Family = "local",
            Local = true, RequiresNetwork = true, Auth = "none", ConfigHash = _factory.BindingId };
        Capabilities = new[] { new ProviderCapabilityDescriptor { Id = ProviderCapabilityIds.AiChat,
            Operations = new() { "run" }, ToolPolicy = ProviderToolPolicies.CallerOwned,
            InputLimits = new() { ["maxContextTokens"] = options.ContextTokens, ["maxPayloadBytes"] = 70000 },
            OutputLimits = new() { ["maxOutputTokens"] = options.MaxOutputTokens, ["maxOutputBytes"] = 262144 },
            ModePolicies = new() { new() { Id = ProviderModes.Advisory, AllowNetwork = true,
                AllowSourceWrites = false, MaxInputBytes = 70000, MaxOutputBytes = 262144, TimeoutSeconds = maxExecutionSeconds } },
            UnsupportedFeatures = new() { "tools", "source_writes", "attachments", "multimodal", "internal_retries", "trusted_issuance" } } };
        if (options.ChatOutputFormat == LlamaCppChatOutputFormats.JsonObjectV1)
        {
            Capabilities[0].OutputLimits["chatOutputFormat"] = options.ChatOutputFormat;
            Capabilities[0].OutputLimits["constraintPolicySha256"] = NativeJsonObjectOutput.PolicySha256;
        }
    }
    public ProviderProfile Profile { get; }
    public int MaxExecutionSeconds { get; }
    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }

    public Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
        => RunWithProgressAsync(request, null, ct: ct);

    public async Task<ProviderRunResult> RunWithProgressAsync(ProviderRunRequest request,
        Func<ProviderProgressObservation, CancellationToken, ValueTask>? observe,
        DateTimeOffset? deadlineUtc = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew(); int? input = null, generated = null; bool effectStarted = false;
        var stage = "request_validation"; var responseReceived = false;
        var trace = new ProviderTraceEvent { Provider = Profile.Id, Capability = request.Capability,
            Operation = request.Operation, Mode = request.Mode, ModelId = _options.ModelId,
            AdapterId = "local-llamacpp-native-v2", ConfigHash = Profile.ConfigHash,
            InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)) };
        ProviderRunResult Result(ProviderRunStatus status, string reason, string? failure = null, JsonObject? output = null,
            string? diagnostic = null)
        {
            trace.DurationMs = clock.Elapsed.TotalMilliseconds; trace.FailureClass = failure;
            trace.OutputHash = output is null ? null : ProviderHash.Sha256(output.ToJsonString(ProviderJson.Options));
            var result = new ProviderRunResult { Provider = Profile.Id, Capability = request.Capability,
                Operation = request.Operation, Mode = request.Mode, Status = status, ProviderStatus = reason,
                FailureClass = failure, Output = output ?? new(), Trace = trace };
            if (status != ProviderRunStatus.Succeeded)
                result.Rejection = new() { Source = ProviderRejectionSources.ProviderRuntime,
                    Category = diagnostic ?? reason, Message = $"Native request stopped during {stage}; completion effects " +
                        (effectStarted ? "may remain unresolved." : "were not dispatched."),
                    ParsedOutputPresent = responseReceived, StructuredOutputAccepted = false,
                    StructuredOutputValidationStatus = diagnostic ?? reason,
                    ParsedOutputDisposition = ProviderParsedOutputDispositions.None,
                    DecisionAuthority = ProviderRejectionDecisionAuthorities.ProviderResponseStatus,
                    ProcessOutcome = responseReceived ? ProviderProcessOutcomes.HttpSuccess : ProviderProcessOutcomes.Unknown,
                    Retryable = false, RetryRecommendation = ProviderRetryRecommendations.DoNotRetry,
                    OperatorReviewRecommended = effectStarted };
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
        if (request.TimeoutSeconds > MaxExecutionSeconds)
            return Result(ProviderRunStatus.Rejected, "timeout_exceeds_policy", ProviderFailureClasses.Policy);
        if (Encoding.UTF8.GetByteCount(request.Payload.ToJsonString(ProviderJson.Options)) > 70000)
            return Result(ProviderRunStatus.Rejected, "input_limit", ProviderFailureClasses.Policy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var startedAt = DateTimeOffset.UtcNow;
        var expiresAt = startedAt.AddSeconds(Math.Min(request.TimeoutSeconds ?? DefaultExecutionSeconds, MaxExecutionSeconds));
        if (deadlineUtc is { } suppliedDeadline)
        {
            if (suppliedDeadline.Offset != TimeSpan.Zero)
                return Result(ProviderRunStatus.Rejected, "invalid_execution_deadline", ProviderFailureClasses.Policy);
            if (suppliedDeadline < expiresAt) expiresAt = suppliedDeadline;
        }
        var remaining = expiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return Result(ProviderRunStatus.TimedOut, "no_native_completion_started", ProviderFailureClasses.Timeout);
        deadline.CancelAfter(remaining);
        long sequence = 0;
        int? runtimeContext = null, promptTokens = null, reservedOutput = null;
        bool? fits = null;
        void CheckDeadline()
        {
            if (DateTimeOffset.UtcNow >= expiresAt) deadline.Cancel();
            deadline.Token.ThrowIfCancellationRequested();
        }
        async ValueTask ObserveAsync(ProviderProgressPhase phase)
        {
            CheckDeadline();
            if (observe is null) return;
            try
            {
                await observe(new ProviderProgressObservation
                {
                    Phase = phase, Sequence = ++sequence, ObservedAtUtc = DateTimeOffset.UtcNow,
                    StartedAtUtc = startedAt, DeadlineUtc = expiresAt, RuntimeContextTokens = runtimeContext,
                    PromptTokens = promptTokens, ReservedOutputTokens = reservedOutput, PromptFitsContext = fits
                }, deadline.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // Recorder failures are closed refusals; no recorder exception text crosses the boundary.
                throw new LocalScoringException("progress_observation_failed", ProviderFailureClasses.Unknown);
            }
            CheckDeadline();
        }
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
            stage = "runtime_binding";
            await ObserveAsync(ProviderProgressPhase.RuntimeBinding);
            await using var session = (LlamaCppRuntimeSession)await _factory.OpenAsync(deadline.Token);
            runtimeContext = session.ContextTokens;
            reservedOutput = ceiling;
            stage = "prompt_preparation";
            await ObserveAsync(ProviderProgressPhase.PromptPreparation);
            var prompt = await session.PrepareChatAsync(chat.Messages, deadline.Token);
            promptTokens = prompt.TokenIds.Length;
            fits = promptTokens <= session.ContextTokens - ceiling;
            await ObserveAsync(ProviderProgressPhase.PromptPrepared);
            if (fits == false)
                throw new LocalScoringException("context_limit_exceeded", ProviderFailureClasses.Policy);
            deadline.Token.ThrowIfCancellationRequested();
            await ObserveAsync(ProviderProgressPhase.CompletionPending);
            effectStarted = true;
            stage = "completion_response";
            var response = await session.CompleteAsync(prompt, ceiling, false, deadline.Token);
            responseReceived = true; stage = "completion_validation";
            await ObserveAsync(ProviderProgressPhase.CompletionValidation);
            var evaluated = Counter(response, "tokens_evaluated"); var predicted = Counter(response, "tokens_predicted");
            if (evaluated is null || predicted is null)
                return Result(ProviderRunStatus.Failed, "invalid_native_usage", ProviderFailureClasses.Schema);
            if (response["truncated"] is not JsonValue flag || !flag.TryGetValue<bool>(out var truncated))
                return Result(ProviderRunStatus.Failed, "invalid_native_truncation", ProviderFailureClasses.Schema);
            if (evaluated != prompt.TokenIds.Length || predicted > ceiling || truncated)
                return Result(ProviderRunStatus.Failed, "native_bound_violated", ProviderFailureClasses.Policy);
            if (response["content"] is not JsonValue value || !value.TryGetValue<string>(out var text))
                return Result(ProviderRunStatus.Failed, "invalid_native_content", ProviderFailureClasses.Schema);
            string? stopReason = null;
            if (response["stop_type"] is not null &&
                (response["stop_type"] is not JsonValue stop || !stop.TryGetValue<string>(out stopReason)))
                return Result(ProviderRunStatus.Failed, "invalid_native_stop_reason", ProviderFailureClasses.Schema);
            input = evaluated; generated = predicted;
            if (chat.MaxOutputChars is int charLimit && text.Length > charLimit)
                return Result(ProviderRunStatus.Failed, "output_character_limit", ProviderFailureClasses.Policy);
            if (_options.ChatOutputFormat == LlamaCppChatOutputFormats.JsonObjectV1)
            {
                // Echo is an operator-bound consistency check, not independent
                // evidence of native grammar enforcement. Never retry or repair.
                if (!NativeJsonObjectOutput.Confirmed(response))
                    return Result(ProviderRunStatus.Failed, "native_output_constraint_unconfirmed", ProviderFailureClasses.Unsupported);
                if (NativeJsonObjectOutput.RejectionReason(text) is string jsonReason)
                    return Result(ProviderRunStatus.Failed, "native_json_object_invalid", ProviderFailureClasses.Schema, diagnostic: jsonReason);
            }
            var output = ProviderJson.ToJsonObject(new AiChatResult { Message = new() { Role = AiRoles.Assistant, Content = text },
                StopReason = stopReason ?? "unknown" });
            if (Encoding.UTF8.GetByteCount(output.ToJsonString(ProviderJson.Options)) > Math.Min(request.MaxOutputBytes ?? 262144, 262144))
                return Result(ProviderRunStatus.Failed, "output_limit", ProviderFailureClasses.Policy);
            deadline.Token.ThrowIfCancellationRequested();
            return Result(ProviderRunStatus.Succeeded, "native_completion", output: output);
        }
        catch (LocalScoringException ex) { return Result(effectStarted ? ProviderRunStatus.Failed : ProviderRunStatus.Rejected,
            effectStarted ? "native_effect_unresolved" : ex.Reason, ex.FailureClass, diagnostic: ex.Reason); }
        catch (OperationCanceledException)
        {
            return Result(ct.IsCancellationRequested ? ProviderRunStatus.Cancelled : ProviderRunStatus.TimedOut,
                effectStarted ? "native_effect_unresolved" : "no_native_completion_started",
                ct.IsCancellationRequested ? ProviderFailureClasses.Cancelled : ProviderFailureClasses.Timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException or ArgumentException)
        {
            // Only fixed vocabulary crosses the public boundary. Never expose an
            // exception message, URL, response bytes, model content or request.
            var diagnostic = ex switch { HttpRequestException => "native_transport_failed", IOException => "native_io_failed",
                JsonException => "native_json_invalid", _ => "native_response_shape_invalid" };
            return Result(ProviderRunStatus.Failed, effectStarted ? "native_effect_unresolved" : "native_preflight_failed",
                ex is HttpRequestException or IOException ? ProviderFailureClasses.ProviderUnavailable : ProviderFailureClasses.Schema,
                diagnostic: diagnostic);
        }
    }
    private static int? Counter(JsonObject response, string name) =>
        response[name] is JsonValue v && v.TryGetValue<int>(out var value) && value >= 0 ? value : null;
}
