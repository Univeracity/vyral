using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Jules;

public sealed class JulesProviderTarget : IProviderTarget, IProviderDoctor, IProviderQualificationPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly JulesProviderOptions _options;
    private readonly HttpClient _client;
    private readonly IReadOnlyDictionary<string, ProviderModePolicy> _policies;

    public JulesProviderTarget(JulesProviderOptions options, HttpClient? client = null, IEnumerable<ProviderModePolicy>? policies = null)
    {
        _options = options;
        _client = client ?? new HttpClient { BaseAddress = options.BaseUri };
        _client.BaseAddress ??= options.BaseUri;
        _policies = ProviderModePolicies.Index(policies ?? ProviderModePolicies.Defaults(allowNetwork: true));

        Profile = new ProviderProfile
        {
            Id = options.ProviderId,
            DisplayName = options.DisplayName,
            Family = "jules",
            Local = false,
            RequiresNetwork = true,
            Auth = "api-key",
            ConfigHash = ProviderHash.Sha256($"{options.ProviderId}|{options.BaseUri}|{options.Source}|{options.StartingBranch}|{options.QualificationSessionId}|{options.DefaultAutomationMode}|{options.RequirePlanApproval}")
        };

        Capabilities = new List<ProviderCapabilityDescriptor>
        {
            new()
            {
                Id = ProviderCapabilityIds.AgentJob,
                Operations = new List<string> { "createSession", "sendMessage", "getSession", "probeSession", "listActivities" },
                ToolPolicy = "provider-managed/caller-approved",
                InputLimits = new Dictionary<string, object?> { ["maxPromptBytes"] = _policies.Values.Max(policy => policy.MaxInputBytes) },
                OutputLimits = new Dictionary<string, object?> { ["maxOutputBytes"] = options.MaxOutputBytes },
                ModePolicies = _policies.Values.OrderBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                UnsupportedFeatures = new List<string> { "direct_source_write_authority", "local_execution", "pause_resume_publish_decision_affordances" }
            },
            new()
            {
                Id = ProviderCapabilityIds.ComputeJob,
                Operations = new List<string> { "createSession", "getSession", "probeSession", "listActivities" },
                ToolPolicy = "provider-managed/caller-approved",
                InputLimits = new Dictionary<string, object?> { ["maxPromptBytes"] = _policies.Values.Max(policy => policy.MaxInputBytes) },
                OutputLimits = new Dictionary<string, object?> { ["maxOutputBytes"] = options.MaxOutputBytes },
                ModePolicies = _policies.Values.OrderBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                UnsupportedFeatures = new List<string> { "direct_source_write_authority", "local_execution", "pause_resume_publish_decision_affordances" }
            }
        };
    }

    public ProviderProfile Profile { get; }
    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }

    public Task<ProviderDoctorResult> DiagnoseAsync(CancellationToken ct = default)
    {
        _ = ct;
        var checks = new List<ProviderDoctorCheck>
        {
            new()
            {
                Id = "auth.api_key",
                Status = string.IsNullOrWhiteSpace(_options.ApiKey) ? ProviderDoctorStatuses.Failed : ProviderDoctorStatuses.Ok,
                Message = string.IsNullOrWhiteSpace(_options.ApiKey)
                    ? "Jules API key is not configured."
                    : "Jules API key is configured.",
                Details = new Dictionary<string, object?>
                {
                    ["auth"] = Profile.Auth
                }
            },
            new()
            {
                Id = "source.binding",
                Status = string.IsNullOrWhiteSpace(_options.Source) ? ProviderDoctorStatuses.Failed : ProviderDoctorStatuses.Ok,
                Message = string.IsNullOrWhiteSpace(_options.Source)
                    ? "Jules source binding is not configured."
                    : "Jules source binding is configured.",
                Details = new Dictionary<string, object?>
                {
                    ["sourceConfigured"] = !string.IsNullOrWhiteSpace(_options.Source),
                    ["startingBranch"] = _options.StartingBranch
                }
            },
            new()
            {
                Id = "network.target",
                Status = string.Equals(_options.BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? ProviderDoctorStatuses.Ok
                    : ProviderDoctorStatuses.Warning,
                Message = $"Jules API base URI is '{_options.BaseUri}'.",
                Details = new Dictionary<string, object?>
                {
                    ["baseUri"] = _options.BaseUri.ToString(),
                    ["requiresNetwork"] = true
                }
            },
            new()
            {
                Id = "plan.approval",
                Status = _options.RequirePlanApproval ? ProviderDoctorStatuses.Ok : ProviderDoctorStatuses.Warning,
                Message = _options.RequirePlanApproval
                    ? "Plan approval is required by default."
                    : "Plan approval is not required by default.",
                Details = new Dictionary<string, object?>
                {
                    ["requirePlanApproval"] = _options.RequirePlanApproval,
                    ["automationMode"] = _options.DefaultAutomationMode
                }
            },
            new()
            {
                Id = "lifecycle.surface",
                Status = ProviderDoctorStatuses.Warning,
                Message = "Jules lifecycle output is normalized for local consumers, but getSession remains the session-state source of truth and pause/resume/publish decisions remain experimental.",
                Details = new Dictionary<string, object?>
                {
                    ["operations"] = new[] { "createSession", "sendMessage", "getSession", "probeSession", "listActivities" },
                    ["sourceOfTruthOperation"] = "getSession",
                    ["normalizedFields"] = new[] { "sessionId", "lifecycleStatus", "stateSource", "authoritativeSessionState", "requiresSessionRefresh", "requiresCallerAction", "pendingQuestionCount", "nextActions", "headRef", "pullRequestUrl" },
                    ["normalizedStatuses"] = new[] { "queued", "running", "awaitingInput", "awaitingFeedback", "awaitingPlanApproval", "awaitingPublishDecision", "failedRecoverable", "failed", "cancelled", "completed", "unknown" },
                    ["unsupported"] = new[] { "pause", "resume", "publishDecision" }
                }
            },
            new()
            {
                Id = "qualification.probe",
                Status = string.IsNullOrWhiteSpace(_options.QualificationSessionId)
                    ? ProviderDoctorStatuses.Warning
                    : ProviderDoctorStatuses.Ok,
                Message = string.IsNullOrWhiteSpace(_options.QualificationSessionId)
                    ? "Jules qualification will create a conservative smoke session because no qualification session id is configured."
                    : "Jules qualification will use a non-mutating getSession probe.",
                Details = new Dictionary<string, object?>
                {
                    ["probeOperation"] = string.IsNullOrWhiteSpace(_options.QualificationSessionId) ? "createSession" : "probeSession",
                    ["nonMutating"] = !string.IsNullOrWhiteSpace(_options.QualificationSessionId),
                    ["qualificationSessionConfigured"] = !string.IsNullOrWhiteSpace(_options.QualificationSessionId)
                }
            }
        };

        var status = ProviderDoctorStatuses.Aggregate(checks);
        return Task.FromResult(new ProviderDoctorResult
        {
            Provider = Profile.Id,
            Status = status,
            Summary = status == ProviderDoctorStatuses.Failed
                ? "Jules API target is not fully configured."
                : "Jules API target is configured, with experimental lifecycle affordances.",
            Checks = checks,
            Metadata = new Dictionary<string, object?>
            {
                ["family"] = Profile.Family,
                ["configHash"] = Profile.ConfigHash,
                ["experimental"] = true
            }
        });
    }

    public IReadOnlyList<ProviderRunRequest> CreateQualificationRequests(ProviderQualificationRequest request)
    {
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "advisory" : request.Mode;
        var capabilities = Capabilities
            .Where(capability => string.IsNullOrWhiteSpace(request.Capability) || string.Equals(capability.Id, request.Capability, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrWhiteSpace(_options.QualificationSessionId))
        {
            return capabilities
                .Select(capability => new ProviderRunRequest
                {
                    Capability = capability.Id,
                    Operation = "probeSession",
                    Mode = mode,
                    TimeoutSeconds = 30,
                    MaxOutputBytes = Math.Min(_options.MaxOutputBytes, 16 * 1024),
                    Payload = new JsonObject
                    {
                        ["sessionId"] = _options.QualificationSessionId
                    }
                })
                .ToList();
        }

        return capabilities
            .Select(capability => new ProviderRunRequest
            {
                Capability = capability.Id,
                Operation = "createSession",
                Mode = mode,
                TimeoutSeconds = 60,
                MaxOutputBytes = Math.Min(_options.MaxOutputBytes, 16 * 1024),
                Payload = new JsonObject
                {
                    ["prompt"] = "Vyral Jules qualification smoke. Create a minimal advisory session only. Do not publish changes, merge code, or claim readiness.",
                    ["title"] = "Vyral Jules qualification smoke",
                    ["source"] = _options.Source,
                    ["startingBranch"] = _options.StartingBranch,
                    ["requirePlanApproval"] = true
                }
            })
            .ToList();
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
            AdapterId = "jules-api",
            ConfigHash = Profile.ConfigHash
        };
        var policy = ProviderModePolicies.Resolve(_policies, request.Mode);

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return CreateResult(request, trace, ProviderRunStatus.NotConfigured, stopwatch.Elapsed, null, ProviderFailureClasses.Configuration, "missing_api_key");
        }

        if (!Capabilities.Any(c => string.Equals(c.Id, request.Capability, StringComparison.OrdinalIgnoreCase)))
        {
            return CreateResult(request, trace, ProviderRunStatus.Unsupported, stopwatch.Elapsed, null, ProviderFailureClasses.Unsupported, "unsupported_capability");
        }

        if (policy is null)
        {
            return CreateResult(request, trace, ProviderRunStatus.Rejected, stopwatch.Elapsed, "Unknown provider mode.", ProviderFailureClasses.Policy, "unknown_mode");
        }

        var outputLimitStatus = ValidateOutputLimit(request);
        if (outputLimitStatus is not null)
        {
            return CreateResult(request, trace, ProviderRunStatus.Rejected, stopwatch.Elapsed, "Requested output limit is invalid.", ProviderFailureClasses.Policy, outputLimitStatus);
        }

        var modeOutputLimit = Math.Min(policy.MaxOutputBytes, _options.MaxOutputBytes);
        request.MaxOutputBytes = Math.Min(request.MaxOutputBytes ?? modeOutputLimit, modeOutputLimit);

        try
        {
            var operation = request.Operation.Trim();
            return operation switch
            {
                var op when string.Equals(op, "createSession", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "start", StringComparison.OrdinalIgnoreCase) => await CreateSessionAsync(request, trace, stopwatch, ct),
                var op when string.Equals(op, "sendMessage", StringComparison.OrdinalIgnoreCase) => await SendMessageAsync(request, trace, stopwatch, ct),
                var op when string.Equals(op, "getSession", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(op, "probeSession", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(op, "refreshSession", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(op, "sessionState", StringComparison.OrdinalIgnoreCase) => await GetSessionAsync(request, trace, stopwatch, ct),
                var op when string.Equals(op, "listActivities", StringComparison.OrdinalIgnoreCase) => await ListActivitiesAsync(request, trace, stopwatch, ct),
                _ => CreateResult(request, trace, ProviderRunStatus.Unsupported, stopwatch.Elapsed, null, ProviderFailureClasses.Unsupported, "unsupported_operation")
            };
        }
        catch (ArgumentException ex)
        {
            return CreateResult(request, trace, ProviderRunStatus.Rejected, stopwatch.Elapsed, ex.Message, ProviderFailureClasses.Schema, "invalid_request");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return CreateResult(request, trace, ProviderRunStatus.TimedOut, stopwatch.Elapsed, null, ProviderFailureClasses.Timeout, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return CreateResult(request, trace, ProviderRunStatus.Failed, stopwatch.Elapsed, ex.Message, ProviderFailureClasses.Network, "network");
        }
    }

    private async Task<ProviderRunResult> CreateSessionAsync(ProviderRunRequest request, ProviderTraceEvent trace, Stopwatch stopwatch, CancellationToken ct)
    {
        var prompt = ProviderPayload.RequiredString(request.Payload, "prompt");
        var guardedPrompt = ComposePrompt(prompt);
        var source = ProviderPayload.GetString(request.Payload, "source") ?? _options.Source;
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Jules createSession requires payload.source or configured Source.");
        }

        var startingBranch = ProviderPayload.GetString(request.Payload, "startingBranch") ?? _options.StartingBranch;
        var body = new JsonObject
        {
            ["prompt"] = guardedPrompt,
            ["sourceContext"] = new JsonObject
            {
                ["source"] = source,
                ["githubRepoContext"] = new JsonObject
                {
                    ["startingBranch"] = startingBranch
                }
            }
        };

        AddOptional(body, "title", ProviderPayload.GetString(request.Payload, "title"));
        AddOptional(body, "automationMode", ProviderPayload.GetString(request.Payload, "automationMode") ?? _options.DefaultAutomationMode);
        body["requirePlanApproval"] = ProviderPayload.GetBoolean(request.Payload, "requirePlanApproval") ?? _options.RequirePlanApproval;

        trace.InputHash = ProviderHash.Sha256(body.ToJsonString(JsonOptions));
        return await SendJsonAsync(request, trace, stopwatch, HttpMethod.Post, "sessions", body, guardedPrompt, ct);
    }

    private static string? ValidateOutputLimit(ProviderRunRequest request)
    {
        if (request.MaxOutputBytes.HasValue && request.MaxOutputBytes.Value <= 0)
        {
            return "invalid_output_limit";
        }

        return null;
    }

    private async Task<ProviderRunResult> SendMessageAsync(ProviderRunRequest request, ProviderTraceEvent trace, Stopwatch stopwatch, CancellationToken ct)
    {
        var sessionId = NormalizeSessionId(ProviderPayload.RequiredString(request.Payload, "sessionId"));
        var prompt = ComposePrompt(ProviderPayload.RequiredString(request.Payload, "prompt"));
        var body = new JsonObject { ["prompt"] = prompt };
        trace.InputHash = ProviderHash.Sha256(body.ToJsonString(JsonOptions));
        return await SendJsonAsync(request, trace, stopwatch, HttpMethod.Post, $"sessions/{Uri.EscapeDataString(sessionId)}:sendMessage", body, prompt, ct);
    }

    private async Task<ProviderRunResult> GetSessionAsync(ProviderRunRequest request, ProviderTraceEvent trace, Stopwatch stopwatch, CancellationToken ct)
    {
        var sessionId = NormalizeSessionId(ProviderPayload.RequiredString(request.Payload, "sessionId"));
        trace.InputHash = ProviderHash.Sha256(sessionId);
        return await SendJsonAsync(request, trace, stopwatch, HttpMethod.Get, $"sessions/{Uri.EscapeDataString(sessionId)}", null, string.Empty, ct);
    }

    private async Task<ProviderRunResult> ListActivitiesAsync(ProviderRunRequest request, ProviderTraceEvent trace, Stopwatch stopwatch, CancellationToken ct)
    {
        var sessionId = NormalizeSessionId(ProviderPayload.RequiredString(request.Payload, "sessionId"));
        trace.InputHash = ProviderHash.Sha256(sessionId);
        return await SendJsonAsync(request, trace, stopwatch, HttpMethod.Get, $"sessions/{Uri.EscapeDataString(sessionId)}/activities", null, string.Empty, ct);
    }

    private async Task<ProviderRunResult> SendJsonAsync(
        ProviderRunRequest request,
        ProviderTraceEvent trace,
        Stopwatch stopwatch,
        HttpMethod method,
        string path,
        JsonObject? body,
        string promptArtifact,
        CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds ?? _options.TimeoutSeconds));

        using var httpRequest = new HttpRequestMessage(method, path);
        httpRequest.Headers.TryAddWithoutValidation("X-Goog-Api-Key", _options.ApiKey);
        if (body is not null)
        {
            httpRequest.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
        var outputLimit = request.MaxOutputBytes ?? _options.MaxOutputBytes;
        var read = await ReadContentWithLimitAsync(response.Content, outputLimit, linkedCts.Token);
        var text = read.Text;
        stopwatch.Stop();

        trace.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
        trace.OutputHash = ProviderHash.Sha256(text);

        var output = ParseOutput(text);
        var lifecycle = JulesLifecycleNormalizer.BuildSummary(output, request.Operation);
        if (lifecycle.Count > 0)
        {
            output["jules"] = lifecycle;
        }

        output["httpStatus"] = (int)response.StatusCode;
        output["outputTruncated"] = read.Truncated;

        var status = read.Truncated
            ? ProviderRunStatus.Rejected
            : response.IsSuccessStatusCode
                ? ProviderRunStatus.Succeeded
                : ProviderRunStatus.Failed;
        var failure = read.Truncated
            ? ProviderFailureClasses.Policy
            : response.IsSuccessStatusCode
                ? null
                : JulesFailureClassifier.Classify(response.StatusCode, text);
        var providerStatus = read.Truncated ? "output_limit" : ((int)response.StatusCode).ToString();
        var julesOutput = output ?? new JsonObject();
        var parsedOutputPresent = julesOutput.Count > 0 && !julesOutput.ContainsKey("raw");
        if (text != null) julesOutput["text"] = text;
        var result = new ProviderRunResult
        {
            Status = status,
            Provider = Profile.Id,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            FailureClass = failure,
            ProviderStatus = providerStatus,
            Rejection = ProviderRunRejectionDiagnostics.Create(status, failure, providerStatus, request.Capability, parsedOutputPresent),
            Trace = trace,
            Output = julesOutput
        };
        if (result.Rejection is not null)
        {
            julesOutput["rejection"] = ProviderJson.ToJsonObject(result.Rejection);
        }

        trace.FailureClass = failure;

        ProviderRunArtifactWriter.WriteArtifacts(request.ArtifactDirectory, request, promptArtifact, text ?? string.Empty, result);
        return result;
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
        if (bytes.Length <= maxBytes)
        {
            return (Encoding.UTF8.GetString(bytes), false);
        }

        return (Encoding.UTF8.GetString(bytes, 0, maxBytes), true);
    }

    private ProviderRunResult CreateResult(
        ProviderRunRequest request,
        ProviderTraceEvent trace,
        ProviderRunStatus status,
        TimeSpan duration,
        string? text,
        string? failureClass,
        string? providerStatus)
    {
        trace.DurationMs = duration.TotalMilliseconds;
        trace.FailureClass = failureClass;
        trace.OutputHash = ProviderHash.Sha256(text);

        var obj = new JsonObject();
        if (text != null) obj["text"] = text;
        return new ProviderRunResult
        {
            Status = status,
            Provider = Profile.Id,
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

    private static string ComposePrompt(string prompt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vyral provider boundary:");
        builder.AppendLine(ProviderBoundary.AuthorityBoundary);
        builder.AppendLine("Jules output must be treated as proposal/evidence until the caller reviews and adopts it.");
        builder.AppendLine();
        builder.AppendLine("Task:");
        builder.Append(prompt);
        return builder.ToString();
    }

    private static void AddOptional(JsonObject body, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            body[key] = value;
        }
    }

    private static JsonObject ParseOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        try
        {
            var node = JsonNode.Parse(text);
            return node switch
            {
                JsonObject obj => obj,
                JsonArray array => new JsonObject { ["items"] = array },
                JsonValue value => new JsonObject { ["value"] = value },
                _ => new JsonObject { ["raw"] = text }
            };
        }
        catch (JsonException)
        {
            return new JsonObject { ["raw"] = text };
        }
    }

    public static string NormalizeSessionId(string sessionId)
    {
        return JulesLifecycleNormalizer.NormalizeSessionId(sessionId);
    }
}
