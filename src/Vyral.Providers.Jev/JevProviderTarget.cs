using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Jev;

/// <summary>
/// Remote-HTTP implementer of <see cref="ProviderCapabilityIds.AiJudge"/> for TypeSafe AI's Jev
/// (System One typed-judgment API): stateless HTTP, api-key auth, no local process — the same shape
/// as <c>JulesProviderTarget</c> rather than a CLI provider target.
/// <para>
/// The wire contract (<c>POST https://api.typesafe.ai/v1/systemone</c>, <c>Authorization: Bearer
/// &lt;key&gt;</c>, a <c>questions</c> map keyed by question id with <c>{type, instructions,
/// criteria}</c>, answers keyed the same way with <c>{type, noul}</c>, <c>{type, choice,
/// probabilities, confidence}</c>, or <c>{type, score, legend, probabilities, confidence}</c>) is
/// confirmed against TypeSafe's own published docs (docs.typesafe.ai/api.md, /primitives/choice.md,
/// /primitives/noul.md, /primitives/score.md), and — as of
/// 2026-09-18 — confirmed by running <see cref="JevProviderTarget"/> itself against the live endpoint
/// with a real credential, covering all three question types in one exchange
/// (<c>JevApi_LiveSmokeTestCoversAllThreeQuestionTypesAgainstTheRealEndpoint</c> in
/// <c>JevProviderTargetTests.cs</c>; re-runnable with <c>JEV_API_KEY</c> or
/// <c>TYPESAFE_API_TOKEN_KEY</c> set). That is one verified exchange at one point in time, not a
/// repeatable isolated live-qualification gate (see <c>integration.exercise</c> in
/// <see cref="DiagnoseAsync"/> and the package README's qualification posture) — TypeSafe could change
/// the API without this adapter's tests catching it until someone runs the live test again. It still
/// parses leniently and always surfaces the raw provider response under <c>output.raw</c> alongside
/// the mapped fields, with <see cref="AiValidationStatuses.ProviderJson"/> rather than a stronger
/// validation status.
/// </para>
/// </summary>
public sealed class JevProviderTarget : IProviderTarget, IProviderDoctor, IProviderQualificationPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly JevProviderOptions _options;
    private readonly HttpClient _client;
    private readonly IReadOnlyDictionary<string, ProviderModePolicy> _policies;

    public JevProviderTarget(JevProviderOptions options, HttpClient? client = null, IEnumerable<ProviderModePolicy>? policies = null)
    {
        _options = options;
        _client = client ?? new HttpClient { BaseAddress = options.BaseUri };
        _client.BaseAddress ??= options.BaseUri;
        _policies = ProviderModePolicies.Index(policies ?? ProviderModePolicies.Defaults(allowNetwork: true));

        Profile = new ProviderProfile
        {
            Id = options.ProviderId,
            DisplayName = options.DisplayName,
            Family = "jev",
            Local = false,
            RequiresNetwork = true,
            Auth = ProviderAuthTypes.ApiKey,
            ConfigHash = ProviderHash.Sha256($"{options.ProviderId}|{options.BaseUri}|{options.ModelId}")
        };

        Capabilities = new List<ProviderCapabilityDescriptor>
        {
            new()
            {
                Id = ProviderCapabilityIds.AiJudge,
                Operations = new List<string> { "judge", "run" },
                ToolPolicy = ProviderToolPolicies.ProviderOwned,
                InputLimits = new Dictionary<string, object?> { ["maxPromptBytes"] = _policies.Values.Max(policy => policy.MaxInputBytes) },
                OutputLimits = new Dictionary<string, object?> { ["maxOutputBytes"] = options.MaxOutputBytes },
                ModePolicies = _policies.Values.OrderBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                UnsupportedFeatures = new List<string> { "local_execution", "source_writes", "caller_tool_execution", "verified_response_schema" }
            }
        };
    }

    public ProviderProfile Profile { get; }
    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }

    public Task<ProviderDoctorResult> DiagnoseAsync(CancellationToken ct = default)
    {
        _ = ct;
        var isPinned = !_options.ModelId.Contains("latest", StringComparison.OrdinalIgnoreCase);
        var checks = new List<ProviderDoctorCheck>
        {
            new()
            {
                Id = "auth.api_key",
                Status = string.IsNullOrWhiteSpace(_options.ApiKey) ? ProviderDoctorStatuses.Failed : ProviderDoctorStatuses.Ok,
                Message = string.IsNullOrWhiteSpace(_options.ApiKey) ? "Jev API key is not configured." : "Jev API key is configured.",
                Details = new Dictionary<string, object?> { ["auth"] = Profile.Auth }
            },
            new()
            {
                Id = "model.pin",
                Status = isPinned ? ProviderDoctorStatuses.Ok : ProviderDoctorStatuses.Warning,
                Message = isPinned
                    ? $"Jev model id is pinned to an exact version: '{_options.ModelId}'."
                    : $"Jev model id '{_options.ModelId}' looks like a rolling alias, not a pinned version. Drift between qualification time and production time will not be visible to a consumer doing its own downstream qualification.",
                Details = new Dictionary<string, object?> { ["modelId"] = _options.ModelId }
            },
            new()
            {
                Id = "network.target",
                Status = string.Equals(_options.BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? ProviderDoctorStatuses.Ok
                    : ProviderDoctorStatuses.Warning,
                Message = $"Jev API base URI is '{_options.BaseUri}'.",
                Details = new Dictionary<string, object?> { ["baseUri"] = _options.BaseUri.ToString(), ["requiresNetwork"] = true }
            },
            new()
            {
                Id = "integration.exercise",
                Status = ProviderDoctorStatuses.Ok,
                Message = "This adapter has been run against the live endpoint at least once (2026-09-18, all three question types), not only the wire schema checked against docs. This check does not itself make a network call, so it cannot confirm the API still matches today — re-run JevApi_LiveSmokeTestCoversAllThreeQuestionTypesAgainstTheRealEndpoint periodically or before a release, not just once.",
                Details = new Dictionary<string, object?>
                {
                    ["endpoint"] = "v1/systemone",
                    ["schemaSource"] = "docs.typesafe.ai",
                    ["lastKnownLiveExercise"] = "2026-09-18",
                    ["liveTest"] = "JevProviderTargetTests.JevApi_LiveSmokeTestCoversAllThreeQuestionTypesAgainstTheRealEndpoint"
                }
            }
        };

        var status = ProviderDoctorStatuses.Aggregate(checks);
        return Task.FromResult(new ProviderDoctorResult
        {
            Provider = Profile.Id,
            Status = status,
            Summary = status == ProviderDoctorStatuses.Failed ? "Jev API target is not fully configured." : "Jev API target is configured, with a wire schema and at least one live exchange both confirmed (see integration.exercise); not yet a repeatable live-qualification gate.",
            Checks = checks,
            Metadata = new Dictionary<string, object?> { ["family"] = Profile.Family, ["configHash"] = Profile.ConfigHash, ["modelId"] = _options.ModelId }
        });
    }

    public IReadOnlyList<ProviderRunRequest> CreateQualificationRequests(ProviderQualificationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Capability) &&
            !string.Equals(request.Capability, ProviderCapabilityIds.AiJudge, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<ProviderRunRequest>();
        }

        return new List<ProviderRunRequest>
        {
            new()
            {
                Capability = ProviderCapabilityIds.AiJudge,
                Operation = "judge",
                Mode = string.IsNullOrWhiteSpace(request.Mode) ? "mechanics" : request.Mode,
                TimeoutSeconds = 30,
                MaxOutputBytes = Math.Min(_options.MaxOutputBytes, 16 * 1024),
                Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
                {
                    Context = "Vyral Jev qualification smoke.",
                    Questions = new List<AiJudgeQuestion>
                    {
                        new() { Id = "smoke-1", Type = AiJudgeQuestionTypes.Noul, Prompt = "Answer only this fixed smoke question. Is this a smoke test?" }
                    }
                })
            }
        };
    }

    public async Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var trace = new ProviderTraceEvent
        {
            Provider = Profile.Id,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            AdapterId = "jev-api",
            ConfigHash = Profile.ConfigHash,
            ModelId = _options.ModelId,
            AuthorityBoundary = ProviderBoundary.AuthorityBoundary
        };

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.NotConfigured, null, ProviderFailureClasses.Configuration, "missing_api_key");
        }

        if (!string.Equals(request.Capability, ProviderCapabilityIds.AiJudge, StringComparison.OrdinalIgnoreCase))
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Unsupported, null, ProviderFailureClasses.Unsupported, "unsupported_capability");
        }

        var operation = request.Operation.Trim();
        if (!string.Equals(operation, "judge", StringComparison.OrdinalIgnoreCase) && !string.Equals(operation, "run", StringComparison.OrdinalIgnoreCase))
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Unsupported, null, ProviderFailureClasses.Unsupported, "unsupported_operation");
        }

        var policy = ProviderModePolicies.Resolve(_policies, string.IsNullOrWhiteSpace(request.Mode) ? "advisory" : request.Mode);
        if (policy is null)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Rejected, "Unknown provider mode.", ProviderFailureClasses.Policy, "unknown_mode");
        }

        AiJudgeRequest judgeRequest;
        try
        {
            judgeRequest = ProviderJson.DeserializePayload<AiJudgeRequest>(request);
        }
        catch (ArgumentException ex)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Rejected, ex.Message, ProviderFailureClasses.Schema, "invalid_request");
        }

        if (judgeRequest.Questions.Count == 0)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Rejected, "ai.judge requires at least one question.", ProviderFailureClasses.Schema, "invalid_request");
        }

        // Structural validation the portable contract requires of every implementer (see
        // DeterministicAiProviderTarget.RunJudge and OnnxNliJudgeProviderTarget.RunJudgeAsync for the
        // identical checks) — fail closed locally rather than spending a network call and a real API
        // charge to discover a client-side shape error, and rather than letting a request TypeSafe's
        // own API might reject in some other, less specific way pass through as if it were a normal
        // provider failure. AiJudgeProviderConformanceTests (Vyral.Tests.Conformance) asserts all three
        // implementers behave identically here.
        var structuralError = ValidateQuestions(judgeRequest.Questions);
        if (structuralError is not null)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Rejected, structuralError, ProviderFailureClasses.Schema, "invalid_request");
        }

        var body = BuildRequestBody(judgeRequest, _options.ModelId);
        trace.InputHash = ProviderHash.Sha256(body.ToJsonString(JsonOptions));

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds ?? _options.TimeoutSeconds));

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/systemone");
            httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");
            httpRequest.Content = JsonContent.Create(body, options: JsonOptions);

            using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            var outputLimit = request.MaxOutputBytes ?? _options.MaxOutputBytes;
            var read = await ReadContentWithLimitAsync(response.Content, outputLimit, linkedCts.Token);
            stopwatch.Stop();
            trace.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            trace.OutputHash = ProviderHash.Sha256(read.Text);

            if (read.Truncated)
            {
                var truncatedOutput = new JsonObject { ["text"] = read.Text, ["outputTruncated"] = true };
                trace.FailureClass = ProviderFailureClasses.Policy;
                return new ProviderRunResult
                {
                    Status = ProviderRunStatus.Rejected,
                    Provider = Profile.Id,
                    Capability = request.Capability,
                    Operation = request.Operation,
                    Mode = request.Mode,
                    FailureClass = ProviderFailureClasses.Policy,
                    ProviderStatus = "output_limit",
                    Rejection = ProviderRunRejectionDiagnostics.Create(ProviderRunStatus.Rejected, ProviderFailureClasses.Policy, "output_limit", request.Capability, parsedOutputPresent: true),
                    Trace = trace,
                    Output = truncatedOutput
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                var failure = ClassifyFailure(response.StatusCode, read.Text);
                trace.FailureClass = failure;
                var failureOutput = new JsonObject { ["httpStatus"] = (int)response.StatusCode, ["raw"] = read.Text };
                return new ProviderRunResult
                {
                    Status = ProviderRunStatus.Failed,
                    Provider = Profile.Id,
                    Capability = request.Capability,
                    Operation = request.Operation,
                    Mode = request.Mode,
                    FailureClass = failure,
                    ProviderStatus = ((int)response.StatusCode).ToString(),
                    Rejection = ProviderRunRejectionDiagnostics.Create(ProviderRunStatus.Failed, failure, ((int)response.StatusCode).ToString(), request.Capability, parsedOutputPresent: true),
                    Trace = trace,
                    Output = failureOutput
                };
            }

            var output = ParseJudgeResponse(read.Text, judgeRequest);
            output["httpStatus"] = (int)response.StatusCode;
            return new ProviderRunResult
            {
                Status = ProviderRunStatus.Succeeded,
                Provider = Profile.Id,
                Capability = request.Capability,
                Operation = request.Operation,
                Mode = request.Mode,
                Trace = trace,
                Output = output
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.TimedOut, null, ProviderFailureClasses.Timeout, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Failed, ex.Message, ProviderFailureClasses.Network, "network");
        }
    }

    /// <summary>
    /// Builds the verified TypeSafe <c>POST /v1/systemone</c> request body: <c>{state, model, questions}</c>
    /// where <c>questions</c> is a map keyed by question id (not an array), each question is
    /// <c>{type, instructions, criteria?}</c>, and for a Choice question <c>criteria</c> maps each
    /// option id to its description. Confirmed against https://docs.typesafe.ai/api.md,
    /// /primitives/choice.md and /primitives/noul.md, and exercised by this adapter's opt-in live test.
    /// </summary>
    private static string? ValidateQuestions(List<AiJudgeQuestion> questions)
    {
        foreach (var question in questions)
        {
            if (string.IsNullOrWhiteSpace(question.Id))
            {
                return "ai.judge questions require an id.";
            }

            if (string.Equals(question.Type, AiJudgeQuestionTypes.Choice, StringComparison.OrdinalIgnoreCase))
            {
                if (question.Options.Count == 0)
                {
                    return $"ai.judge question '{question.Id}' is type 'choice' and requires at least one option.";
                }
            }
            else if (string.Equals(question.Type, AiJudgeQuestionTypes.Score, StringComparison.OrdinalIgnoreCase))
            {
                if (question.Options.Count < 2)
                {
                    return $"ai.judge question '{question.Id}' is type 'score' and requires at least two ordered levels.";
                }
            }
            else if (!string.Equals(question.Type, AiJudgeQuestionTypes.Noul, StringComparison.OrdinalIgnoreCase))
            {
                return $"ai.judge question '{question.Id}' has unsupported type '{question.Type}'.";
            }
        }

        return null;
    }

    private static JsonObject BuildRequestBody(AiJudgeRequest request, string modelId)
    {
        var questions = new JsonObject();
        foreach (var question in request.Questions)
        {
            var questionNode = new JsonObject
            {
                ["type"] = question.Type,
                ["instructions"] = question.Prompt
            };

            if (string.Equals(question.Type, AiJudgeQuestionTypes.Choice, StringComparison.OrdinalIgnoreCase) && question.Options.Count > 0)
            {
                // Choice criteria is a map: option id -> description. Order carries no meaning for Jev
                // here (the response identifies the winner by id), so a map is the right shape.
                var criteria = new JsonObject();
                foreach (var option in question.Options)
                {
                    criteria[option.Id] = string.IsNullOrWhiteSpace(option.Label) ? null : option.Label;
                }

                questionNode["criteria"] = criteria;
            }
            else if (string.Equals(question.Type, AiJudgeQuestionTypes.Score, StringComparison.OrdinalIgnoreCase) && question.Options.Count > 0)
            {
                // Score criteria is an ORDERED ARRAY of level descriptions (low to high), not a map —
                // Jev has no concept of our option ids for Score; it answers by array index, and
                // ParseJudgeResponse maps that index back to question.Options[index].Id.
                var criteria = new JsonArray();
                foreach (var option in question.Options)
                {
                    criteria.Add(string.IsNullOrWhiteSpace(option.Label) ? null : option.Label);
                }

                questionNode["criteria"] = criteria;
            }

            questions[question.Id] = questionNode;
        }

        return new JsonObject
        {
            ["state"] = request.Context ?? string.Empty,
            ["model"] = modelId,
            ["questions"] = questions
        };
    }

    /// <summary>
    /// Parses the verified TypeSafe response shape: <c>{model, answers, usage}</c> where <c>answers</c>
    /// is a map keyed by question id, each answer <c>{type, noul}</c>, <c>{type, choice, probabilities,
    /// confidence}</c>, or <c>{type, score, legend, probabilities, confidence}</c>. Raw response is
    /// always preserved under "raw". A Noul answer has no provider-side confidence (per
    /// /primitives/noul.md); this adapter derives one as max(p, 1-p), the same convention the local
    /// judge targets use, so all three ai.judge implementers are comparable. For a Score answer, Jev's
    /// <c>legend</c>/<c>probabilities</c> are keyed by level-index strings ("0", "1", ...) matching the
    /// order <c>criteria</c> was sent in — this adapter remaps those to the caller's own
    /// <c>question.Options[index].Id</c> so all three implementers share one id-keyed convention; if a
    /// matching question or option can't be found (a wire-shape surprise), the raw index-keyed maps are
    /// passed through unchanged rather than dropped. "calibrated" is always false here: TypeSafe's
    /// calibration claim is the provider's own and this adapter has not independently verified it (see
    /// README) — never asserted as fact in the output.
    /// </summary>
    private static JsonObject ParseJudgeResponse(string text, AiJudgeRequest request)
    {
        var output = new JsonObject { ["raw"] = text, ["validationStatus"] = AiValidationStatuses.ProviderJson };

        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return output;
        }

        if (node is not JsonObject responseObject || responseObject["answers"] is not JsonObject answersObject)
        {
            return output;
        }

        var questionsById = request.Questions
            .Where(question => !string.IsNullOrWhiteSpace(question.Id))
            .ToDictionary(question => question.Id, StringComparer.Ordinal);

        var mapped = new JsonArray();
        foreach (var (questionId, answerNode) in answersObject)
        {
            if (answerNode is not JsonObject answerObject)
            {
                continue;
            }

            var mappedAnswer = new JsonObject { ["questionId"] = questionId, ["calibrated"] = false };

            if (answerObject["noul"] is JsonValue noulValue && noulValue.TryGetValue<double>(out var noul))
            {
                mappedAnswer["probability"] = noul;
                mappedAnswer["confidence"] = Math.Round(Math.Max(noul, 1 - noul), 6);
            }
            else if (answerObject["score"] is not null)
            {
                var byIndexLegend = answerObject["legend"] as JsonObject;
                var byIndexProbabilities = answerObject["probabilities"] as JsonObject;
                var options = questionsById.TryGetValue(questionId, out var scoreQuestion) ? scoreQuestion.Options : null;

                mappedAnswer["score"] = answerObject["score"]?.DeepClone();
                mappedAnswer["confidence"] = answerObject["confidence"]?.DeepClone();
                mappedAnswer["legend"] = RemapIndexKeyedObject(byIndexLegend, options) ?? byIndexLegend?.DeepClone();
                mappedAnswer["probabilities"] = RemapIndexKeyedObject(byIndexProbabilities, options) ?? byIndexProbabilities?.DeepClone();
            }
            else
            {
                mappedAnswer["choice"] = answerObject["choice"]?.DeepClone();
                mappedAnswer["probabilities"] = answerObject["probabilities"]?.DeepClone();
                mappedAnswer["confidence"] = answerObject["confidence"]?.DeepClone();
            }

            mapped.Add(mappedAnswer);
        }

        output["answers"] = mapped;
        output["modelId"] = responseObject["model"]?.DeepClone();
        return output;
    }

    /// <summary>
    /// Rebuilds an index-keyed JSON object ("0", "1", ...) with keys replaced by
    /// <c>options[index].Id</c>. Returns null (caller falls back to the raw object) when there is no
    /// matching question, an index doesn't parse, or an index is out of range for <paramref name="options"/> —
    /// this never partially remaps and never throws on an unexpected wire shape.
    /// </summary>
    private static JsonObject? RemapIndexKeyedObject(JsonObject? indexKeyed, List<AiJudgeOption>? options)
    {
        if (indexKeyed is null || options is null || options.Count == 0)
        {
            return null;
        }

        var remapped = new JsonObject();
        foreach (var (key, value) in indexKeyed)
        {
            if (!int.TryParse(key, out var index) || index < 0 || index >= options.Count)
            {
                return null;
            }

            remapped[options[index].Id] = value?.DeepClone();
        }

        return remapped;
    }

    private static string ClassifyFailure(HttpStatusCode statusCode, string responseText)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ProviderFailureClasses.Auth;
        }

        if ((int)statusCode == 429)
        {
            return responseText.Contains("quota", StringComparison.OrdinalIgnoreCase) ? ProviderFailureClasses.Quota : ProviderFailureClasses.RateLimit;
        }

        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            return ProviderFailureClasses.Schema;
        }

        if ((int)statusCode >= 500)
        {
            return ProviderFailureClasses.ProviderUnavailable;
        }

        return ProviderFailureClasses.Unknown;
    }

    private static async Task<(string Text, bool Truncated)> ReadContentWithLimitAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream(capacity: Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[8192];
        var remaining = maxBytes + 1;

        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
            {
                break;
            }

            memory.Write(buffer, 0, read);
            remaining -= read;
        }

        var bytes = memory.ToArray();
        return bytes.Length <= maxBytes ? (Encoding.UTF8.GetString(bytes), false) : (Encoding.UTF8.GetString(bytes, 0, maxBytes), true);
    }

    private static ProviderRunResult CreateResult(
        ProviderRunRequest request,
        ProviderTraceEvent trace,
        Stopwatch stopwatch,
        ProviderRunStatus status,
        string? text,
        string? failureClass,
        string? providerStatus)
    {
        stopwatch.Stop();
        trace.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
        trace.FailureClass = failureClass;
        trace.OutputHash = ProviderHash.Sha256(text);

        var obj = new JsonObject();
        if (text != null) obj["text"] = text;
        return new ProviderRunResult
        {
            Status = status,
            Provider = trace.Provider,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            FailureClass = failureClass,
            ProviderStatus = providerStatus,
            Rejection = ProviderRunRejectionDiagnostics.Create(status, failureClass, providerStatus, request.Capability),
            Trace = trace,
            Output = obj
        };
    }
}
