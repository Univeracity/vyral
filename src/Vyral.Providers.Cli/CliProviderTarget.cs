using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Cli;

public sealed class CliProviderTarget : IProviderTarget, IProviderQualificationPlanner, IProviderModelCatalog, IProviderQuotaReporter, IProviderDoctor
{
    private readonly CliProviderOptions _options;
    private readonly IProviderProcessRunner _runner;
    private readonly ICodexAppServerQuotaClient _quotaClient;
    private readonly IReadOnlyDictionary<string, ProviderModePolicy> _policies;
    private readonly CliExecutableIdentity _configuredExecutableIdentity;

    public CliProviderTarget(CliProviderOptions options, IProviderProcessRunner? runner = null, IEnumerable<ProviderModePolicy>? policies = null, ICodexAppServerQuotaClient? quotaClient = null)
    {
        if (string.IsNullOrWhiteSpace(options.ProviderId))
        {
            throw new ArgumentException("Provider id is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Command))
        {
            throw new ArgumentException("CLI command is required.", nameof(options));
        }

        _options = options;
        _runner = runner ?? new SystemProviderProcessRunner();
        _quotaClient = quotaClient ?? new CodexAppServerQuotaClient();
        _policies = ProviderModePolicies.Index(policies ?? ProviderModePolicies.Defaults(allowNetwork: true));
        _configuredExecutableIdentity = CliExecutableIdentity.Capture(options.Command);

        Profile = new ProviderProfile
        {
            Id = options.ProviderId,
            DisplayName = options.DisplayName,
            Family = options.Family,
            Local = true,
            RequiresNetwork = true,
            Auth = options.Auth,
            ConfigHash = ProviderHash.Sha256(CreateConfigMaterial(options, _configuredExecutableIdentity))
        };

        Capabilities = options.Capabilities
            .Select(capability => new ProviderCapabilityDescriptor
            {
                Id = capability,
                Operations = new List<string> { "run" },
                ToolPolicy = options.ToolPolicy,
                InputLimits = new Dictionary<string, object?> { ["maxPromptBytes"] = _policies.Values.Max(policy => policy.MaxInputBytes) },
                OutputLimits = new Dictionary<string, object?> { ["maxOutputBytes"] = options.MaxOutputBytes },
                ModePolicies = _policies.Values.OrderBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                UnsupportedFeatures = new List<string> { "source_writes", "caller_tool_execution" }
            })
            .ToList();
    }

    public ProviderProfile Profile { get; }
    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }

    public async Task<ProviderDoctorResult> DiagnoseAsync(CancellationToken ct = default)
    {
        var checks = new List<ProviderDoctorCheck>();
        var executableIdentity = CliExecutableIdentity.Capture(_options.Command);
        var commandPath = executableIdentity.ResolvedPath;
        checks.Add(new ProviderDoctorCheck
        {
            Id = "command.resolution",
            Status = commandPath is null ? ProviderDoctorStatuses.Failed : ProviderDoctorStatuses.Ok,
            Message = commandPath is null
                ? $"Command '{_options.Command}' is not visible to the Vyral process."
                : $"Command '{_options.Command}' resolves for the Vyral process.",
            Details = new Dictionary<string, object?>
            {
                ["command"] = _options.Command,
                ["resolvedPath"] = commandPath,
                ["workingDirectory"] = _options.WorkingDirectory
            }
        });

        var identityMatches = executableIdentity.Available &&
            string.Equals(executableIdentity.ConfigMaterial, _configuredExecutableIdentity.ConfigMaterial, StringComparison.Ordinal);
        var identityStatus = executableIdentity.Available && identityMatches
            ? ProviderDoctorStatuses.Ok
            : _options.RequireExecutableIdentity ? ProviderDoctorStatuses.Failed : ProviderDoctorStatuses.Warning;
        checks.Add(new ProviderDoctorCheck
        {
            Id = "executable.identity",
            Status = identityStatus,
            Message = !executableIdentity.Available
                ? "The CLI executable identity could not be captured."
                : !identityMatches
                    ? "The CLI executable changed after this provider was registered; re-qualify the current executable."
                    : "The CLI executable is content-addressed for qualification binding.",
            Details = new Dictionary<string, object?>
            {
                ["resolvedPath"] = executableIdentity.ResolvedPath,
                ["length"] = executableIdentity.Length,
                ["sha256"] = executableIdentity.Sha256,
                ["configuredSha256"] = _configuredExecutableIdentity.Sha256,
                ["error"] = executableIdentity.Error
            }
        });

        checks.Add(new ProviderDoctorCheck
        {
            Id = "model.binding",
            Status = string.IsNullOrWhiteSpace(_options.ModelId) ? ProviderDoctorStatuses.Warning : ProviderDoctorStatuses.Ok,
            Message = string.IsNullOrWhiteSpace(_options.ModelId)
                ? "No explicit model id is configured for this CLI provider."
                : $"Model id '{_options.ModelId}' is configured.",
            Details = new Dictionary<string, object?>
            {
                ["modelId"] = _options.ModelId,
                ["knownModels"] = _options.KnownModels
            }
        });

        var promptTransport = NormalizePromptTransport(_options.PromptTransport);
        var containmentFailures = GetContainmentFailures(includeExecutableIdentity: false);
        if (_options.RequireWorkingDirectory || _options.RequireClearedEnvironment ||
            _options.RequirePromptFileTransport || _options.RequireSandboxProfile || _options.RequireToolDenyRules)
        {
            checks.Add(new ProviderDoctorCheck
            {
                Id = "execution.containment",
                Status = containmentFailures.Count == 0 ? ProviderDoctorStatuses.Ok : ProviderDoctorStatuses.Failed,
                Message = containmentFailures.Count == 0
                    ? "Required CLI containment is configured."
                    : "Required CLI containment is incomplete: " + string.Join("; ", containmentFailures),
                Details = new Dictionary<string, object?>
                {
                    ["workingDirectory"] = _options.WorkingDirectory,
                    ["promptFileDirectory"] = _options.PromptFileDirectory,
                    ["clearEnvironment"] = _options.ClearEnvironment,
                    ["environmentKeys"] = _options.Environment.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList(),
                    ["sandboxProfile"] = _options.RequiredSandboxProfile,
                    ["toolDenyRuleCount"] = _options.RequiredToolDenyRules.Count
                }
            });
        }

        checks.Add(new ProviderDoctorCheck
        {
            Id = "prompt.transport",
            Status = promptTransport == CliPromptTransports.Argument ? ProviderDoctorStatuses.Warning : ProviderDoctorStatuses.Ok,
            Message = promptTransport == CliPromptTransports.Argument
                ? "Prompt transport uses process arguments; prompts may be visible in process listings."
                : promptTransport == CliPromptTransports.File
                    ? "Prompt transport uses a short-lived file outside process arguments."
                    : "Prompt transport uses standard input.",
            Details = new Dictionary<string, object?>
            {
                ["promptTransport"] = promptTransport
            }
        });

        if (_options.VersionArguments.Count > 0)
        {
            var versionRequest = new ProviderProcessRunRequest
            {
                Command = ResolveExecutableCommand(),
                Arguments = _options.VersionArguments,
                WorkingDirectory = _options.WorkingDirectory,
                Environment = _options.Environment,
                ClearEnvironment = _options.ClearEnvironment,
                Timeout = TimeSpan.FromSeconds(5),
                MaxOutputBytes = 4096
            };
            var versionResult = await _runner.RunAsync(versionRequest, ct);
            var versionOk = versionResult.ExitCode == 0 && !versionResult.TimedOut && !versionResult.Cancelled &&
                !versionResult.OutputTruncated && string.IsNullOrWhiteSpace(versionResult.StartError);
            checks.Add(new ProviderDoctorCheck
            {
                Id = "executable.version",
                Status = versionOk ? ProviderDoctorStatuses.Ok : _options.RequireVersionProbe ? ProviderDoctorStatuses.Failed : ProviderDoctorStatuses.Warning,
                Message = versionOk ? "CLI version probe completed." : "CLI version probe failed.",
                Details = new Dictionary<string, object?>
                {
                    ["exitCode"] = versionResult.ExitCode,
                    ["timedOut"] = versionResult.TimedOut,
                    ["cancelled"] = versionResult.Cancelled,
                    ["outputTruncated"] = versionResult.OutputTruncated,
                    ["version"] = versionOk ? RedactDiagnosticValue(versionResult.StandardOutput) : null
                }
            });
        }

        if (_options.ContainmentProbeArguments.Count > 0 && containmentFailures.Count == 0)
        {
            var containmentRequest = new ProviderProcessRunRequest
            {
                Command = ResolveExecutableCommand(),
                Arguments = _options.ContainmentProbeArguments,
                WorkingDirectory = _options.WorkingDirectory,
                Environment = _options.Environment,
                ClearEnvironment = _options.ClearEnvironment,
                Timeout = TimeSpan.FromSeconds(5),
                MaxOutputBytes = 16 * 1024
            };
            var containmentResult = await _runner.RunAsync(containmentRequest, ct);
            var containmentProbeOk = containmentResult.ExitCode == 0 && !containmentResult.TimedOut && !containmentResult.Cancelled &&
                !containmentResult.OutputTruncated && string.IsNullOrWhiteSpace(containmentResult.StartError);
            checks.Add(new ProviderDoctorCheck
            {
                Id = "sandbox.profile",
                Status = containmentProbeOk ? ProviderDoctorStatuses.Ok : _options.RequireContainmentProbe ? ProviderDoctorStatuses.Failed : ProviderDoctorStatuses.Warning,
                Message = containmentProbeOk ? "CLI sandbox profile probe completed." : "CLI sandbox profile probe failed.",
                Details = new Dictionary<string, object?>
                {
                    ["exitCode"] = containmentResult.ExitCode,
                    ["timedOut"] = containmentResult.TimedOut,
                    ["cancelled"] = containmentResult.Cancelled,
                    ["outputTruncated"] = containmentResult.OutputTruncated
                }
            });
        }

        var result = new ProviderDoctorResult
        {
            Provider = Profile.Id,
            Checks = checks,
            Metadata = new Dictionary<string, object?>
            {
                ["family"] = Profile.Family,
                ["local"] = Profile.Local,
                ["requiresNetwork"] = Profile.RequiresNetwork,
                ["auth"] = Profile.Auth,
                ["configHash"] = Profile.ConfigHash
            }
        };
        result.Status = ProviderDoctorStatuses.Aggregate(result.Checks);
        result.Summary = CreateDoctorSummary(result.Status);
        return result;
    }

    public Task<ProviderModelListResult> ListModelsAsync(CancellationToken ct = default)
    {
        _ = ct;
        var models = _options.KnownModels
            .Append(_options.ModelId)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Select(model => new ProviderModelDescriptor
            {
                Id = model,
                DisplayName = model,
                Default = string.Equals(model, _options.ModelId, StringComparison.Ordinal),
                Capabilities = Capabilities.Select(c => c.Id).ToList(),
                Metadata = CreateModelMetadata(model)
            })
            .ToList();

        if (models.Count == 0)
        {
            return Task.FromResult(ProviderModelListResult.Unsupported(Profile.Id));
        }

        return Task.FromResult(new ProviderModelListResult
        {
            Provider = Profile.Id,
            Source = "configured-static",
            DefaultModelId = _options.ModelId,
            Items = models
        });
    }

    public async Task<ProviderQuotaResult> GetQuotaAsync(CancellationToken ct = default)
    {
        var quotaSource = NormalizeQuotaSource(_options.QuotaSource);
        if (quotaSource is null)
        {
            return ProviderQuotaResult.Unsupported(Profile.Id);
        }

        if (quotaSource == CliQuotaSources.CodexAppServerWebSocket ||
            (quotaSource == CliQuotaSources.CodexAppServer &&
                (_options.QuotaAutoStartWebSocket || !string.IsNullOrWhiteSpace(_options.QuotaWebSocketUri))))
        {
            return await GetQuotaFromWebSocketAsync(ct);
        }

        return await GetQuotaFromProxyAsync(quotaSource, ct);
    }

    private async Task<ProviderQuotaResult> GetQuotaFromWebSocketAsync(CancellationToken ct)
    {
        if (!TryCreateWebSocketUri(_options.QuotaWebSocketUri, out var uri, out var uriError))
        {
            var invalidUriResult = CreateQuotaUnavailable(ProviderFailureClasses.Configuration, "invalid_websocket_uri", new ProviderProcessRunResult());
            invalidUriResult.Metadata["message"] = uriError;
            invalidUriResult.Source = CliQuotaSources.CodexAppServerWebSocket;
            return invalidUriResult;
        }

        var processResult = await _quotaClient.ReadRateLimitsAsync(new CodexAppServerQuotaRequest
        {
            WebSocketUri = uri,
            AutoStartWebSocket = _options.QuotaAutoStartWebSocket,
            Command = string.IsNullOrWhiteSpace(_options.QuotaCommand) ? _options.Command : _options.QuotaCommand,
            LaunchArguments = _options.QuotaWebSocketLaunchArguments.Count == 0
                ? new[] { "app-server", "--listen", "ws://127.0.0.1:0" }
                : _options.QuotaWebSocketLaunchArguments,
            WorkingDirectory = _options.WorkingDirectory,
            Environment = _options.Environment,
            Timeout = TimeSpan.FromSeconds(_options.QuotaTimeoutSeconds),
            MaxOutputBytes = _options.QuotaMaxOutputBytes
        }, ct);

        return CreateQuotaFromProcessResult(processResult, CliQuotaSources.CodexAppServerWebSocket);
    }

    private async Task<ProviderQuotaResult> GetQuotaFromProxyAsync(string quotaSource, CancellationToken ct)
    {
        var processRequest = new ProviderProcessRunRequest
        {
            Command = string.IsNullOrWhiteSpace(_options.QuotaCommand) ? _options.Command : _options.QuotaCommand,
            Arguments = BuildQuotaArguments(),
            WorkingDirectory = _options.WorkingDirectory,
            Environment = _options.Environment,
            StandardInput = "{\"method\":\"account/rateLimits/read\",\"id\":1}\n",
            Timeout = TimeSpan.FromSeconds(_options.QuotaTimeoutSeconds),
            MaxOutputBytes = _options.QuotaMaxOutputBytes
        };

        var processResult = await _runner.RunAsync(processRequest, ct);
        return CreateQuotaFromProcessResult(processResult, quotaSource);
    }

    private ProviderQuotaResult CreateQuotaFromProcessResult(ProviderProcessRunResult processResult, string source)
    {
        if (processResult.Cancelled)
        {
            return CreateQuotaUnavailable(ProviderFailureClasses.Cancelled, "cancelled", processResult, source);
        }

        if (processResult.TimedOut)
        {
            return CreateQuotaUnavailable(ProviderFailureClasses.Timeout, "timeout", processResult, source);
        }

        if (processResult.OutputTruncated)
        {
            return CreateQuotaFailed(ProviderFailureClasses.Policy, "output_limit", processResult, source: source);
        }

        if (processResult.ExitCode != 0 || !string.IsNullOrWhiteSpace(processResult.StartError))
        {
            return CreateQuotaUnavailable(CliFailureClassifier.Classify(processResult), processResult.ExitCode.ToString(), processResult, source);
        }

        try
        {
            var result = ParseCodexRateLimitResult(processResult.StandardOutput);
            result.Provider = Profile.Id;
            result.Source = source;
            return result;
        }
        catch (JsonException ex)
        {
            return CreateQuotaFailed(ProviderFailureClasses.Schema, "invalid_json", processResult, ex.Message, source);
        }
        catch (InvalidOperationException ex)
        {
            return CreateQuotaFailed(ProviderFailureClasses.Schema, "missing_rate_limits", processResult, ex.Message, source);
        }
    }

    public IReadOnlyList<ProviderRunRequest> CreateQualificationRequests(ProviderQualificationRequest request)
    {
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "mechanics" : request.Mode;
        return Capabilities
            .Where(capability => string.IsNullOrWhiteSpace(request.Capability) || string.Equals(capability.Id, request.Capability, StringComparison.OrdinalIgnoreCase))
            .Select(capability => new ProviderRunRequest
            {
                Capability = capability.Id,
                Operation = "run",
                Mode = mode,
                TimeoutSeconds = 30,
                MaxOutputBytes = 4096,
                Payload = CreateQualificationPayload(capability.Id)
            })
            .ToList();
    }

    public async Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var modelId = ResolveModelId(request);
        var trace = CreateTrace(request, modelId);
        string guardedPrompt;
        var policy = ProviderModePolicies.Resolve(_policies, request.Mode);

        if (!Capabilities.Any(c => string.Equals(c.Id, request.Capability, StringComparison.OrdinalIgnoreCase)))
        {
            return CreateResult(request, ProviderRunStatus.Unsupported, trace, stopwatch.Elapsed, null, ProviderFailureClasses.Unsupported, "unsupported_capability");
        }

        if (!string.Equals(request.Operation, "run", StringComparison.OrdinalIgnoreCase))
        {
            return CreateResult(request, ProviderRunStatus.Unsupported, trace, stopwatch.Elapsed, null, ProviderFailureClasses.Unsupported, "unsupported_operation");
        }

        if (policy is null)
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, "Unknown provider mode.", ProviderFailureClasses.Policy, "unknown_mode");
        }

        try
        {
            var prompt = ResolvePrompt(request);
            guardedPrompt = ProviderPromptComposer.Compose(prompt, policy, request.Capability);
        }
        catch (ArgumentException ex)
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, ex.Message, ProviderFailureClasses.Schema, "invalid_request");
        }

        if (Encoding.UTF8.GetByteCount(guardedPrompt) > policy.MaxInputBytes)
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, "Prompt exceeds mode input limit.", ProviderFailureClasses.Policy, "input_limit");
        }

        var outputLimitStatus = ValidateOutputLimit(request);
        if (outputLimitStatus is not null)
        {
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, "Requested output limit is invalid.", ProviderFailureClasses.Policy, outputLimitStatus);
        }

        var containmentFailures = GetContainmentFailures(includeExecutableIdentity: true);
        if (containmentFailures.Count > 0)
        {
            return CreateResult(
                request,
                ProviderRunStatus.Rejected,
                trace,
                stopwatch.Elapsed,
                "CLI containment preflight failed: " + string.Join("; ", containmentFailures),
                ProviderFailureClasses.Policy,
                "containment_preflight_failed");
        }

        var modeOutputLimit = Math.Min(policy.MaxOutputBytes, _options.MaxOutputBytes);
        var maxOutputBytes = Math.Min(request.MaxOutputBytes ?? modeOutputLimit, modeOutputLimit);
        var promptTransport = NormalizePromptTransport(_options.PromptTransport);
        TemporaryPromptFile? promptFile = null;
        ProviderProcessRunResult processResult;
        try
        {
            if (promptTransport == CliPromptTransports.File)
            {
                promptFile = await TemporaryPromptFile.CreateAsync(_options.PromptFileDirectory!, guardedPrompt, ct);
            }

            var processRequest = new ProviderProcessRunRequest
            {
                Command = ResolveExecutableCommand(),
                Arguments = BuildArguments(_options.Arguments, guardedPrompt, modelId, promptTransport, promptFile?.Path),
                WorkingDirectory = _options.WorkingDirectory,
                Environment = _options.Environment,
                ClearEnvironment = _options.ClearEnvironment,
                StandardInput = promptTransport == CliPromptTransports.StandardInput ? guardedPrompt : null,
                Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds ?? policy.TimeoutSeconds),
                MaxOutputBytes = maxOutputBytes
            };

            processResult = await _runner.RunAsync(processRequest, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return CreateResult(request, ProviderRunStatus.Rejected, trace, stopwatch.Elapsed, "Prompt-file transport could not be prepared.", ProviderFailureClasses.Policy, "prompt_file_unavailable");
        }
        finally
        {
            if (promptFile is not null)
            {
                await promptFile.DisposeAsync();
            }
        }
        stopwatch.Stop();

        var status = processResult.Cancelled
            ? ProviderRunStatus.Cancelled
            : processResult.TimedOut
            ? ProviderRunStatus.TimedOut
            : processResult.OutputTruncated
            ? ProviderRunStatus.Rejected
            : processResult.ExitCode == 0 && string.IsNullOrWhiteSpace(processResult.StartError)
                ? ProviderRunStatus.Succeeded
                : ProviderRunStatus.Failed;
        var failure = status == ProviderRunStatus.Succeeded
            ? null
            : processResult.OutputTruncated
                ? ProviderFailureClasses.Policy
                : CliFailureClassifier.Classify(processResult);
        if (status == ProviderRunStatus.Failed && string.Equals(failure, ProviderFailureClasses.Policy, StringComparison.Ordinal))
        {
            status = ProviderRunStatus.Rejected;
        }

        var textOutput = processResult.StandardOutput;
        var providerStatus = processResult.Cancelled
            ? "cancelled"
            : processResult.OutputTruncated
                ? "output_limit"
                : processResult.ExitCode.ToString();
        var structuredOutputValidationStatus = "not_validated";
        var parsedStructuredOutput = SupportsStructuredJsonOutput(request.Capability)
            ? TryParseJsonObject(processResult.StandardOutput, out structuredOutputValidationStatus)
            : null;
        var boundaryError = DetectExtractToolPlanLeakage(request.Capability, processResult.StandardOutput, parsedStructuredOutput);

        if (boundaryError is not null)
        {
            status = ProviderRunStatus.Rejected;
            failure = ProviderFailureClasses.Trust;
            providerStatus = "tool_plan_leakage";
        }
        else if (status == ProviderRunStatus.Succeeded &&
            RequiresStructuredJson(request.Capability) &&
            parsedStructuredOutput is null)
        {
            status = ProviderRunStatus.Rejected;
            failure = ProviderFailureClasses.Schema;
            providerStatus = "invalid_provider_json";
        }

        var result = CreateResult(
            request,
            status,
            trace,
            stopwatch.Elapsed,
            textOutput,
            failure,
            providerStatus,
            boundaryError,
            parsedStructuredOutput is not null,
            parsedStructuredOutput is not null,
            parsedStructuredOutput is null ? null : structuredOutputValidationStatus,
            ResolveDecisionAuthority(processResult, status, failure, providerStatus),
            ResolveProcessOutcome(processResult));

        var structuredOutput = CreateOutput(request.Capability, processResult);
        if (!string.IsNullOrEmpty(textOutput)) structuredOutput["text"] = textOutput;
        if (boundaryError is not null)
        {
            structuredOutput["validationStatus"] = "rejected_tool_plan_leakage";
            structuredOutput["boundaryViolation"] = "tool_plan_leakage";
            structuredOutput["boundaryMessage"] = boundaryError;
        }

        if (result.Rejection is not null)
        {
            structuredOutput["rejection"] = ProviderJson.ToJsonObject(result.Rejection);
        }

        result.Output = structuredOutput;
        result.Trace!.InputHash = ProviderHash.Sha256(guardedPrompt);
        result.Trace!.OutputHash = ProviderHash.Sha256($"{processResult.StandardOutput}\n{processResult.StandardError}");

        ProviderRunArtifactWriter.WriteArtifacts(request.ArtifactDirectory, request, guardedPrompt, processResult.StandardOutput, result);
        return result;
    }

    private string? ResolveModelId(ProviderRunRequest request)
    {
        return string.IsNullOrWhiteSpace(request.ModelId)
            ? _options.ModelId
            : request.ModelId.Trim();
    }

    private Dictionary<string, object?> CreateModelMetadata(string model)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["source"] = "configured-static",
            ["configuredDefault"] = string.Equals(model, _options.ModelId, StringComparison.Ordinal)
        };

        if (string.Equals(Profile.Id, "codex-cli", StringComparison.Ordinal) &&
            model.Contains("spark", StringComparison.OrdinalIgnoreCase))
        {
            metadata["knownUnsupportedTools"] = new[] { "image_generation" };
            metadata["toolCompatibility"] = "limited";
            metadata["toolCompatibilitySource"] = "consumer-observed";
        }

        if (string.Equals(Profile.Id, "gemini-cli", StringComparison.Ordinal))
        {
            if (model.StartsWith("gemma-", StringComparison.OrdinalIgnoreCase))
            {
                metadata["family"] = "gemma";
                metadata["experimental"] = true;
            }
            else if (model.StartsWith("gemini-3.1", StringComparison.OrdinalIgnoreCase))
            {
                metadata["generation"] = "3.1";
            }
            else if (model.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase))
            {
                metadata["generation"] = "3.0";
            }
            else if (model.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase))
            {
                metadata["generation"] = "2.5";
            }

            if (!model.StartsWith("gemma-", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = model.EndsWith("-preview", StringComparison.OrdinalIgnoreCase)
                    ? model[..^"-preview".Length]
                    : model;
                if (stripped.EndsWith("-pro", StringComparison.OrdinalIgnoreCase))
                {
                    metadata["tier"] = "pro";
                }
                else if (stripped.EndsWith("-flash-lite", StringComparison.OrdinalIgnoreCase))
                {
                    metadata["tier"] = "flash-lite";
                }
                else if (stripped.EndsWith("-flash", StringComparison.OrdinalIgnoreCase))
                {
                    metadata["tier"] = "flash";
                }

                if (model.EndsWith("-preview", StringComparison.OrdinalIgnoreCase))
                {
                    metadata["preview"] = true;
                }
            }
        }

        if (string.Equals(Profile.Id, "antigravity-cli", StringComparison.Ordinal))
        {
            if (model.StartsWith("gemma-", StringComparison.OrdinalIgnoreCase))
            {
                metadata["family"] = "gemma";
                metadata["experimental"] = true;
            }
            else if (model.StartsWith("gemini-3.1", StringComparison.OrdinalIgnoreCase))
            {
                metadata["generation"] = "3.1";
            }
            else if (model.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase))
            {
                metadata["generation"] = "3.0";
            }
            else if (model.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase))
            {
                metadata["generation"] = "2.5";
            }

            if (!model.StartsWith("gemma-", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = model.EndsWith("-preview", StringComparison.OrdinalIgnoreCase)
                    ? model[..^"-preview".Length]
                    : model;
                if (stripped.EndsWith("-pro", StringComparison.OrdinalIgnoreCase))
                {
                    metadata["tier"] = "pro";
                }
                else if (stripped.EndsWith("-flash-lite", StringComparison.OrdinalIgnoreCase))
                {
                    metadata["tier"] = "flash-lite";
                }
                else if (stripped.EndsWith("-flash", StringComparison.OrdinalIgnoreCase))
                {
                    metadata["tier"] = "flash";
                }

                if (model.EndsWith("-preview", StringComparison.OrdinalIgnoreCase))
                {
                    metadata["preview"] = true;
                }
            }
        }

        if (string.Equals(Profile.Id, "claude-cli", StringComparison.Ordinal))
        {
            if (model.Contains("opus", StringComparison.OrdinalIgnoreCase))
            {
                metadata["tier"] = "pro";
            }
            else if (model.Contains("sonnet", StringComparison.OrdinalIgnoreCase))
            {
                metadata["tier"] = "standard";
            }
            else if (model.Contains("haiku", StringComparison.OrdinalIgnoreCase))
            {
                metadata["tier"] = "lite";
            }
        }

        return metadata;
    }

    private ProviderTraceEvent CreateTrace(ProviderRunRequest request, string? modelId)
    {
        return new ProviderTraceEvent
        {
            Provider = Profile.Id,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            AdapterId = "cli",
            ModelId = modelId,
            ConfigHash = ResolveConfigHash(modelId)
        };
    }

    private string? ResolveConfigHash(string? modelId)
    {
        if (string.Equals(modelId, _options.ModelId, StringComparison.Ordinal))
        {
            return Profile.ConfigHash;
        }

        return ProviderHash.Sha256($"{Profile.ConfigHash}|run-model:{modelId}");
    }

    private static string? ValidateOutputLimit(ProviderRunRequest request)
    {
        if (request.MaxOutputBytes.HasValue && request.MaxOutputBytes.Value <= 0)
        {
            return "invalid_output_limit";
        }

        return null;
    }

    private string ResolveExecutableCommand() => _configuredExecutableIdentity.Available
        ? _configuredExecutableIdentity.ResolvedPath!
        : _options.Command;

    private static bool SupportsStructuredJsonOutput(string capability)
    {
        return RequiresStructuredJson(capability) ||
            string.Equals(capability, ProviderCapabilityIds.AiReview, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(capability, ProviderCapabilityIds.AiScaffold, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresStructuredJson(string capability)
    {
        return string.Equals(capability, ProviderCapabilityIds.AiExtract, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(capability, ProviderCapabilityIds.AiRerank, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(capability, ProviderCapabilityIds.AiToolPlan, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveProcessOutcome(ProviderProcessRunResult processResult)
    {
        if (processResult.Cancelled)
        {
            return ProviderProcessOutcomes.Cancelled;
        }

        if (processResult.TimedOut)
        {
            return ProviderProcessOutcomes.TimedOut;
        }

        if (processResult.OutputTruncated)
        {
            return ProviderProcessOutcomes.OutputTruncated;
        }

        return processResult.ExitCode == 0 ? ProviderProcessOutcomes.ExitZero : ProviderProcessOutcomes.ExitNonZero;
    }

    private static string ResolveDecisionAuthority(
        ProviderProcessRunResult processResult,
        ProviderRunStatus status,
        string? failureClass,
        string? providerStatus)
    {
        if (string.Equals(providerStatus, "tool_plan_leakage", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderRejectionDecisionAuthorities.VyralGuardrail;
        }

        if (string.Equals(providerStatus, "invalid_provider_json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(failureClass, ProviderFailureClasses.Schema, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderRejectionDecisionAuthorities.VyralStructuredOutputValidation;
        }

        if (processResult.OutputTruncated)
        {
            return ProviderRejectionDecisionAuthorities.VyralPolicy;
        }

        if (processResult.ExitCode != 0 && status != ProviderRunStatus.Succeeded)
        {
            return ProviderRejectionDecisionAuthorities.ProviderProcessExit;
        }

        return ProviderRejectionDecisionAuthorities.Unknown;
    }

    private static string? DetectExtractToolPlanLeakage(string capability, string textOutput, JsonObject? parsedOutput)
    {
        if (!string.Equals(capability, ProviderCapabilityIds.AiExtract, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (parsedOutput is not null &&
            (parsedOutput["calls"] is JsonArray ||
             parsedOutput["toolCalls"] is JsonArray ||
             parsedOutput["tool_calls"] is JsonArray ||
             parsedOutput["tool"] is not null))
        {
            return "Provider returned a tool-plan shaped response for ai.extract. Tool planning belongs in ai.toolPlan and tool execution remains caller-owned.";
        }

        if (LooksLikeWorkspaceExplorationPlan(textOutput))
        {
            return "Provider described workspace/tool exploration for ai.extract instead of returning structured extraction output. Tool planning belongs in ai.toolPlan and source access remains caller-owned.";
        }

        return null;
    }

    private static bool LooksLikeWorkspaceExplorationPlan(string textOutput)
    {
        if (string.IsNullOrWhiteSpace(textOutput))
        {
            return false;
        }

        var lower = textOutput.ToLowerInvariant();
        var strongIndicators = new[]
        {
            "list directories",
            "listing directories",
            "list the directory",
            "query sqlite",
            "query the sqlite",
            "read transcripts",
            "read transcript",
            "read provider-run",
            "inspect the workspace",
            "workspace exploration",
            "run a command",
            "execute a command",
            "shell command",
            "call a tool",
            "tool call"
        };

        return strongIndicators.Any(indicator => lower.Contains(indicator, StringComparison.Ordinal));
    }

    private ProviderRunResult CreateResult(
        ProviderRunRequest request,
        ProviderRunStatus status,
        ProviderTraceEvent trace,
        TimeSpan duration,
        string? textOutput,
        string? failureClass,
        string? providerStatus,
        string? error = null,
        bool parsedOutputPresent = false,
        bool structuredOutputAccepted = false,
        string? structuredOutputValidationStatus = null,
        string? decisionAuthority = null,
        string? processOutcome = null)
    {
        trace.DurationMs = duration.TotalMilliseconds;
        trace.FailureClass = failureClass;

        var obj = new JsonObject();
        if (textOutput != null) obj["text"] = textOutput;
        return new ProviderRunResult
        {
            Status = status,
            Provider = Profile.Id,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            FailureClass = failureClass,
            ProviderStatus = providerStatus,
            Error = error ?? (status == ProviderRunStatus.Succeeded ? null : textOutput),
            Rejection = ProviderRunRejectionDiagnostics.Create(
                status,
                failureClass,
                providerStatus,
                request.Capability,
                parsedOutputPresent,
                structuredOutputAccepted,
                structuredOutputValidationStatus,
                decisionAuthority,
                processOutcome),
            Trace = trace,
            Output = obj
        };
    }

    private IReadOnlyList<string> BuildQuotaArguments()
    {
        var args = _options.QuotaArguments.Count == 0
            ? new List<string> { "app-server", "proxy" }
            : _options.QuotaArguments.ToList();

        if (!string.IsNullOrWhiteSpace(_options.QuotaSocketPath) &&
            !args.Any(arg => string.Equals(arg, "--sock", StringComparison.Ordinal)))
        {
            args.Add("--sock");
            args.Add(_options.QuotaSocketPath);
        }

        return args;
    }

    private ProviderQuotaResult CreateQuotaUnavailable(string failureClass, string providerStatus, ProviderProcessRunResult processResult, string? source = null)
    {
        return new ProviderQuotaResult
        {
            Provider = Profile.Id,
            Status = ProviderQuotaStatuses.Unavailable,
            Source = source ?? _options.QuotaSource,
            FailureClass = failureClass,
            ProviderStatus = providerStatus,
            Metadata = CreateQuotaMetadata(processResult)
        };
    }

    private ProviderQuotaResult CreateQuotaFailed(string failureClass, string providerStatus, ProviderProcessRunResult processResult, string? message = null, string? source = null)
    {
        var metadata = CreateQuotaMetadata(processResult);
        if (!string.IsNullOrWhiteSpace(message))
        {
            metadata["message"] = message;
        }

        return new ProviderQuotaResult
        {
            Provider = Profile.Id,
            Status = ProviderQuotaStatuses.Failed,
            Source = source ?? _options.QuotaSource,
            FailureClass = failureClass,
            ProviderStatus = providerStatus,
            Metadata = metadata
        };
    }

    private static Dictionary<string, object?> CreateQuotaMetadata(ProviderProcessRunResult processResult)
    {
        return new Dictionary<string, object?>
        {
            ["exitCode"] = processResult.ExitCode,
            ["startError"] = string.IsNullOrWhiteSpace(processResult.StartError) ? null : processResult.StartError,
            ["stderr"] = string.IsNullOrWhiteSpace(processResult.StandardError) ? null : processResult.StandardError
        };
    }

    private static IReadOnlyList<string> BuildArguments(IEnumerable<string> templates, string prompt, string? model, string promptTransport, string? promptFilePath)
    {
        var args = new List<string>();
        foreach (var template in templates)
        {
            if (string.Equals(template, "{modelArgs}", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(model))
                {
                    args.Add("--model");
                    args.Add(model);
                }

                continue;
            }

            var promptArgument = promptTransport switch
            {
                CliPromptTransports.StandardInput => "-",
                CliPromptTransports.File => promptFilePath ?? throw new InvalidOperationException("A prompt file is required for file prompt transport."),
                _ => prompt
            };
            args.Add(template
                .Replace("{promptFile}", promptFilePath ?? string.Empty, StringComparison.Ordinal)
                .Replace("{prompt}", promptArgument, StringComparison.Ordinal)
                .Replace("{model}", model ?? string.Empty, StringComparison.Ordinal));
        }

        if (promptTransport == CliPromptTransports.Argument && !args.Any(arg => arg.Contains(prompt, StringComparison.Ordinal)))
        {
            args.Add(prompt);
        }

        return args;
    }

    private static string NormalizePromptTransport(string promptTransport)
    {
        if (string.IsNullOrWhiteSpace(promptTransport))
        {
            return CliPromptTransports.Argument;
        }

        return promptTransport.Trim().ToLowerInvariant() switch
        {
            CliPromptTransports.Argument => CliPromptTransports.Argument,
            CliPromptTransports.StandardInput => CliPromptTransports.StandardInput,
            "standardinput" => CliPromptTransports.StandardInput,
            "standard-input" => CliPromptTransports.StandardInput,
            CliPromptTransports.File => CliPromptTransports.File,
            "prompt-file" => CliPromptTransports.File,
            _ => throw new InvalidOperationException("CLI prompt transport must be 'argument', 'stdin', or 'file'.")
        };
    }

    private static string? NormalizeQuotaSource(string quotaSource)
    {
        if (string.IsNullOrWhiteSpace(quotaSource))
        {
            return null;
        }

        return quotaSource.Trim().ToLowerInvariant() switch
        {
            CliQuotaSources.CodexAppServer => CliQuotaSources.CodexAppServer,
            CliQuotaSources.CodexAppServerProxy => CliQuotaSources.CodexAppServerProxy,
            CliQuotaSources.CodexAppServerWebSocket => CliQuotaSources.CodexAppServerWebSocket,
            "codex-app-server-ws" => CliQuotaSources.CodexAppServerWebSocket,
            _ => null
        };
    }

    private static bool TryCreateWebSocketUri(string? value, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
            (uri.Scheme != "ws" && uri.Scheme != "wss"))
        {
            error = "Codex app-server quota WebSocket URI must be an absolute ws:// or wss:// URI.";
            return false;
        }

        return true;
    }

    private List<string> GetContainmentFailures(bool includeExecutableIdentity)
    {
        var failures = new List<string>();
        var workingDirectory = _options.WorkingDirectory;
        if (_options.RequireWorkingDirectory && !IsRootedExistingDirectory(workingDirectory))
        {
            failures.Add("an existing absolute working directory is required");
        }

        if (_options.RequireDedicatedEmptyWorkingDirectory &&
            IsRootedExistingDirectory(workingDirectory) &&
            IsRootedExistingDirectory(_options.PromptFileDirectory))
        {
            try
            {
                var workspaceEntries = Directory.EnumerateFileSystemEntries(workingDirectory!)
                    .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(_options.PromptFileDirectory!), StringComparison.Ordinal))
                    .ToList();
                if (workspaceEntries.Count > 0)
                {
                    failures.Add("the advisory working directory must contain no source or data files");
                }

                if (Directory.EnumerateFileSystemEntries(_options.PromptFileDirectory!).Any())
                {
                    failures.Add("the prompt-file directory must be empty before a run");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add("the advisory working directory contents cannot be verified");
            }
        }

        if (_options.RequireClearedEnvironment && !_options.ClearEnvironment)
        {
            failures.Add("the inherited environment must be cleared");
        }

        foreach (var name in _options.RequiredEnvironmentVariables)
        {
            if (!_options.Environment.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                failures.Add($"environment variable '{name}' must be explicitly configured");
            }
            else if (string.Equals(name, "HOME", StringComparison.Ordinal) && !IsRootedExistingDirectory(value))
            {
                failures.Add("environment variable 'HOME' must name an existing absolute directory");
            }
        }

        var promptTransport = NormalizePromptTransport(_options.PromptTransport);
        if (_options.RequirePromptFileTransport && promptTransport != CliPromptTransports.File)
        {
            failures.Add("file prompt transport is required");
        }

        if (promptTransport == CliPromptTransports.File && !IsRootedExistingDirectory(_options.PromptFileDirectory))
        {
            failures.Add("an existing absolute prompt-file directory is required");
        }

        if (_options.RequirePromptFileWithinWorkingDirectory &&
            IsRootedExistingDirectory(workingDirectory) &&
            IsRootedExistingDirectory(_options.PromptFileDirectory) &&
            !IsContainedBy(_options.PromptFileDirectory!, workingDirectory!))
        {
            failures.Add("the prompt-file directory must be contained by the working directory");
        }

        if (_options.RequireSandboxProfile && string.IsNullOrWhiteSpace(_options.RequiredSandboxProfile))
        {
            failures.Add("a Grok sandbox profile must be explicitly configured");
        }
        else if (!string.IsNullOrWhiteSpace(_options.RequiredSandboxProfile) &&
            !HasOptionValue(_options.Arguments, "--sandbox", _options.RequiredSandboxProfile))
        {
            failures.Add("the configured Grok sandbox profile is not applied");
        }

        if (_options.RequireToolDenyRules && _options.RequiredToolDenyRules.Count == 0)
        {
            failures.Add("at least one Grok tool deny rule must be explicitly configured");
        }

        foreach (var denyRule in _options.RequiredToolDenyRules)
        {
            if (!HasOptionValue(_options.Arguments, "--deny", denyRule))
            {
                failures.Add("a configured Grok tool deny rule is not applied");
                break;
            }
        }

        if (includeExecutableIdentity && _options.RequireExecutableIdentity)
        {
            var current = CliExecutableIdentity.Capture(_options.Command);
            if (!current.Available)
            {
                failures.Add("the CLI executable identity is unavailable");
            }
            else if (!string.Equals(current.ConfigMaterial, _configuredExecutableIdentity.ConfigMaterial, StringComparison.Ordinal))
            {
                failures.Add("the CLI executable changed since provider registration");
            }
        }

        return failures;
    }

    private static bool IsRootedExistingDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && Directory.Exists(path);

    private static bool IsContainedBy(string child, string parent)
    {
        var childPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        var parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return string.Equals(childPath, parentPath, StringComparison.Ordinal) ||
            childPath.StartsWith(parentPath + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool HasOptionValue(IReadOnlyList<string> arguments, string option, string value)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal) &&
                string.Equals(arguments[index + 1], value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateConfigMaterial(CliProviderOptions options, CliExecutableIdentity identity)
    {
        var environment = string.Join('\u001f', options.Environment.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}={item.Value}"));
        return string.Join('|', new[]
        {
            options.ProviderId, options.Command, string.Join('\u001f', options.Arguments), options.ModelId,
            options.QuotaSource, options.QuotaCommand, string.Join('\u001f', options.QuotaArguments), options.QuotaWebSocketUri,
            options.QuotaAutoStartWebSocket.ToString(), string.Join('\u001f', options.QuotaWebSocketLaunchArguments),
            options.WorkingDirectory, environment, options.ClearEnvironment.ToString(), options.PromptTransport, options.PromptFileDirectory,
            options.ToolPolicy, options.RequireWorkingDirectory.ToString(), options.RequireClearedEnvironment.ToString(),
            options.RequireDedicatedEmptyWorkingDirectory.ToString(), options.RequirePromptFileTransport.ToString(), options.RequirePromptFileWithinWorkingDirectory.ToString(),
            string.Join('\u001f', options.RequiredEnvironmentVariables), options.RequireExecutableIdentity.ToString(),
            string.Join('\u001f', options.VersionArguments), options.RequireVersionProbe.ToString(), options.RequiredSandboxProfile,
            options.RequireSandboxProfile.ToString(), options.RequireToolDenyRules.ToString(), string.Join('\u001f', options.RequiredToolDenyRules),
            string.Join('\u001f', options.ContainmentProbeArguments), options.RequireContainmentProbe.ToString(), identity.ConfigMaterial
        });
    }

    private static string? RedactDiagnosticValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var firstLine = value.Trim().Split(['\r', '\n'], 2)[0];
        if (firstLine.Length > 256)
        {
            firstLine = firstLine[..256];
        }

        foreach (var marker in new[] { "token=", "token:", "api_key=", "api-key=", "secret=", "password=" })
        {
            var index = firstLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return firstLine[..(index + marker.Length)] + "[redacted]";
            }
        }

        return firstLine;
    }

    private sealed class TemporaryPromptFile : IAsyncDisposable
    {
        private TemporaryPromptFile(string path) => Path = path;

        public string Path { get; }

        public static async Task<TemporaryPromptFile> CreateAsync(string directory, string content, CancellationToken ct)
        {
            var path = System.IO.Path.Combine(directory, $".vyral-prompt-{Guid.NewGuid():N}");
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(path, streamOptions))
            {

                await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true);
                await writer.WriteAsync(content.AsMemory(), ct);
                await writer.FlushAsync(ct);
            }

            return new TemporaryPromptFile(path);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // The file was created in an operator-owned, isolated directory.
                // A later cleanup pass can remove it if an external process still holds it.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private static string CreateDoctorSummary(string status)
    {
        return status switch
        {
            ProviderDoctorStatuses.Ok => "Provider passed local configuration checks.",
            ProviderDoctorStatuses.Warning => "Provider is callable but has configuration warnings.",
            ProviderDoctorStatuses.Failed => "Provider failed one or more local configuration checks.",
            _ => "Provider local configuration state is unknown."
        };
    }

    private static JsonObject CreateOutput(string capability, ProviderProcessRunResult processResult)
    {
        var extractedData = capability == ProviderCapabilityIds.AiExtract ? TryParseJsonObject(processResult.StandardOutput) : null;
        var structuredValidationStatus = "not_validated";
        var structuredData = capability is ProviderCapabilityIds.AiRerank or ProviderCapabilityIds.AiReview or ProviderCapabilityIds.AiScaffold or ProviderCapabilityIds.AiToolPlan
            ? TryParseJsonObject(processResult.StandardOutput, out structuredValidationStatus)
            : null;
        var output = capability switch
        {
            ProviderCapabilityIds.AiChat => ProviderJson.ToJsonObject(new AiChatResult
            {
                Message = new AiMessage { Role = "assistant", Content = processResult.StandardOutput },
                StopReason = processResult.Cancelled ? "cancelled" : processResult.TimedOut ? "timeout" : processResult.OutputTruncated ? "length" : "complete"
            }),
            ProviderCapabilityIds.AiExtract => ProviderJson.ToJsonObject(new AiExtractResult
            {
                Data = extractedData ?? new JsonObject { ["rawText"] = processResult.StandardOutput },
                ValidationStatus = extractedData is null ? "not_validated" : "provider_json"
            }),
            ProviderCapabilityIds.AiRerank => NormalizeRerankOutput(structuredData, processResult.StandardOutput, structuredValidationStatus),
            ProviderCapabilityIds.AiReview => NormalizeReviewOutput(structuredData, processResult.StandardOutput, structuredValidationStatus),
            ProviderCapabilityIds.AiScaffold => NormalizeScaffoldOutput(structuredData, processResult.StandardOutput, structuredValidationStatus),
            ProviderCapabilityIds.AiToolPlan => NormalizeToolPlanOutput(structuredData, processResult.StandardOutput, structuredValidationStatus),
            _ => new JsonObject()
        };

        output["text"] = processResult.StandardOutput;
        output["stderr"] = processResult.StandardError;
        output["exitCode"] = processResult.ExitCode;
        output["timedOut"] = processResult.TimedOut;
        output["cancelled"] = processResult.Cancelled;
        output["outputTruncated"] = processResult.OutputTruncated;
        return output;
    }

    private static JsonObject? TryParseJsonObject(string text)
    {
        return TryParseJsonObject(text, out _);
    }

    private static ProviderQuotaResult ParseCodexRateLimitResult(string text)
    {
        JsonObject? response = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonObject? parsed;
            try
            {
                parsed = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed is null)
            {
                continue;
            }

            if (JsonNodeValueEquals(parsed["id"], 1))
            {
                response = parsed;
                break;
            }
        }

        response ??= JsonNode.Parse(text.Trim()) as JsonObject;
        if (response?["error"] is JsonNode error)
        {
            return new ProviderQuotaResult
            {
                Status = ProviderQuotaStatuses.Unavailable,
                Source = CliQuotaSources.CodexAppServer,
                FailureClass = ProviderFailureClasses.ProviderUnavailable,
                ProviderStatus = "codex_app_server_error",
                Metadata = new Dictionary<string, object?>
                {
                    ["error"] = error.DeepClone()
                }
            };
        }

        var result = response?["result"] as JsonObject;
        if (result is null)
        {
            throw new InvalidOperationException("Codex app-server response did not include a result object.");
        }

        var buckets = new List<ProviderQuotaBucket>();
        if (result["rateLimitsByLimitId"] is JsonObject byLimitId)
        {
            foreach (var (key, value) in byLimitId)
            {
                if (value is JsonObject bucket)
                {
                    buckets.Add(ParseCodexQuotaBucket(bucket, key));
                }
            }
        }

        if (buckets.Count == 0 && result["rateLimits"] is JsonObject singleBucket)
        {
            buckets.Add(ParseCodexQuotaBucket(singleBucket, "codex"));
        }

        if (buckets.Count == 0)
        {
            throw new InvalidOperationException("Codex app-server response did not include rate limit buckets.");
        }

        return new ProviderQuotaResult
        {
            Status = ProviderQuotaStatuses.Succeeded,
            Source = CliQuotaSources.CodexAppServer,
            Items = buckets.OrderBy(bucket => bucket.LimitId, StringComparer.Ordinal).ToList(),
            Metadata = new Dictionary<string, object?>
            {
                ["precision"] = "percent_window",
                ["contract"] = "account/rateLimits/read"
            }
        };
    }

    private static ProviderQuotaBucket ParseCodexQuotaBucket(JsonObject bucket, string fallbackLimitId)
    {
        return new ProviderQuotaBucket
        {
            LimitId = GetString(bucket["limitId"]) ?? fallbackLimitId,
            LimitName = GetString(bucket["limitName"]),
            Primary = ParseCodexQuotaWindow(bucket["primary"]),
            Secondary = ParseCodexQuotaWindow(bucket["secondary"]),
            RateLimitReachedType = GetString(bucket["rateLimitReachedType"]),
            PlanType = GetString(bucket["planType"]),
            Credits = bucket["credits"]?.DeepClone(),
            Metadata = new Dictionary<string, object?>
            {
                ["source"] = "codex_app_server"
            }
        };
    }

    private static ProviderQuotaWindow? ParseCodexQuotaWindow(JsonNode? node)
    {
        if (node is not JsonObject window)
        {
            return null;
        }

        var usedPercent = GetDouble(window["usedPercent"]);
        var resetsAtUnixSeconds = GetLong(window["resetsAt"]);
        return new ProviderQuotaWindow
        {
            UsedPercent = usedPercent,
            RemainingPercent = usedPercent.HasValue ? Math.Clamp(100 - usedPercent.Value, 0, 100) : null,
            WindowDurationMins = GetInt(window["windowDurationMins"]),
            ResetsAtUnixSeconds = resetsAtUnixSeconds,
            ResetsAt = ToUnixDateTimeOffset(resetsAtUnixSeconds)
        };
    }

    private static bool JsonNodeValueEquals(JsonNode? node, int expected)
    {
        try
        {
            return node?.GetValue<int>() == expected;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return string.Equals(GetString(node), expected.ToString(), StringComparison.Ordinal);
        }
    }

    private static string? GetString(JsonNode? node)
    {
        try
        {
            return node?.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return node?.ToJsonString(ProviderJson.Options);
        }
    }

    private static double? GetDouble(JsonNode? node)
    {
        try
        {
            return node?.GetValue<double>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static int? GetInt(JsonNode? node)
    {
        try
        {
            return node?.GetValue<int>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static long? GetLong(JsonNode? node)
    {
        try
        {
            return node?.GetValue<long>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ToUnixDateTimeOffset(long? seconds)
    {
        if (!seconds.HasValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static JsonObject? TryParseJsonObject(string text, out string validationStatus)
    {
        validationStatus = "not_validated";
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var candidates = new[]
        {
            text.Trim(),
            TryExtractFencedPayload(text),
            TryExtractObjectSlice(text)
        };

        foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var parsed = JsonNode.Parse(candidate!) as JsonObject;
                if (parsed is not null)
                {
                    validationStatus = ReferenceEquals(candidate, candidates[0]) || string.Equals(candidate, candidates[0], StringComparison.Ordinal)
                        ? "provider_json"
                        : "provider_jsonish";
                    return parsed;
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static string? TryExtractFencedPayload(string text)
    {
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart < 0)
        {
            return null;
        }

        var contentStart = text.IndexOf('\n', fenceStart + 3);
        if (contentStart < 0)
        {
            return null;
        }

        var fenceEnd = text.IndexOf("```", contentStart + 1, StringComparison.Ordinal);
        if (fenceEnd < 0)
        {
            return null;
        }

        return text[(contentStart + 1)..fenceEnd].Trim();
    }

    private static string? TryExtractObjectSlice(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)].Trim() : null;
    }

    private static string ResolvePrompt(ProviderRunRequest request)
    {
        if (string.Equals(request.Capability, ProviderCapabilityIds.AiReview, StringComparison.OrdinalIgnoreCase))
        {
            return ComposeReviewPrompt(ProviderJson.DeserializePayload<AiReviewRequest>(request));
        }

        if (string.Equals(request.Capability, ProviderCapabilityIds.AiScaffold, StringComparison.OrdinalIgnoreCase))
        {
            return ComposeScaffoldPrompt(ProviderJson.DeserializePayload<AiScaffoldRequest>(request));
        }

        if (string.Equals(request.Capability, ProviderCapabilityIds.AiToolPlan, StringComparison.OrdinalIgnoreCase))
        {
            return ComposeToolPlanPrompt(ProviderJson.DeserializePayload<AiToolPlanRequest>(request));
        }

        var rawPrompt = ProviderPayload.GetString(request.Payload, "prompt");
        if (!string.IsNullOrWhiteSpace(rawPrompt))
        {
            return rawPrompt;
        }

        return request.Capability switch
        {
            ProviderCapabilityIds.AiChat => ComposeChatPrompt(ProviderJson.DeserializePayload<AiChatRequest>(request)),
            ProviderCapabilityIds.AiExtract => ComposeExtractPrompt(ProviderJson.DeserializePayload<AiExtractRequest>(request)),
            ProviderCapabilityIds.AiRerank => ComposeRerankPrompt(ProviderJson.DeserializePayload<AiRerankRequest>(request)),
            _ => throw new ArgumentException("Provider payload requires 'prompt'.")
        };
    }

    private static JsonObject CreateQualificationPayload(string capability)
    {
        if (string.Equals(capability, ProviderCapabilityIds.AiRerank, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderJson.ToJsonObject(new AiRerankRequest
            {
                Query = "retention policy",
                Limit = 1,
                Candidates = new List<AiRerankCandidate>
                {
                    new() { Id = "a", Text = "retention policy details" },
                    new() { Id = "b", Text = "travel reimbursement" }
                }
            });
        }

        if (string.Equals(capability, ProviderCapabilityIds.AiExtract, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderJson.ToJsonObject(new AiExtractRequest
            {
                Text = "Vyral provider qualification smoke: product name Example Lamp; status received.",
                Instructions = "Return JSON only with fields received and status.",
                Schema = new JsonObject
                {
                    ["received"] = "boolean",
                    ["status"] = "string"
                }
            });
        }

        if (string.Equals(capability, ProviderCapabilityIds.AiToolPlan, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderJson.ToJsonObject(new AiToolPlanRequest
            {
                Prompt = "Plan whether a no-op diagnostic tool call is needed. Return JSON only.",
                Tools = new List<AiToolDefinition>
                {
                    new()
                    {
                        Name = "noop",
                        Description = "Diagnostic no-op tool. Do not execute; only propose when necessary.",
                        InputSchema = new JsonObject { ["type"] = "object" }
                    }
                }
            });
        }

        return new JsonObject
        {
            ["prompt"] = $"Vyral provider qualification smoke for {capability}. Reply with one short sentence confirming the request was received. Do not write files, execute tools, or make external calls."
        };
    }

    private static string ComposeChatPrompt(AiChatRequest request)
    {
        if (request.Messages.Count == 0 || request.Messages.All(message => string.IsNullOrWhiteSpace(message.Content)))
        {
            throw new ArgumentException("ai.chat requires messages or prompt.");
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            builder.AppendLine("System:");
            builder.AppendLine(request.System);
            builder.AppendLine();
        }

        builder.AppendLine("Conversation:");
        foreach (var message in request.Messages.Where(message => !string.IsNullOrWhiteSpace(message.Content)))
        {
            builder.Append(message.Role);
            builder.Append(": ");
            builder.AppendLine(message.Content);
        }

        return builder.ToString();
    }

    private static string ComposeExtractPrompt(AiExtractRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new ArgumentException("ai.extract requires text or prompt.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("Extract structured information from the following text.");
        builder.AppendLine("Return a JSON object only. Match the requested schema where possible and do not wrap the response in Markdown.");
        builder.AppendLine("Do not propose, describe, or attempt tool calls, workspace exploration, file reads, shell commands, database queries, or source inspection.");
        builder.AppendLine("If the supplied text is insufficient, return JSON fields that clearly mark review needs instead of planning additional work.");
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            builder.AppendLine();
            builder.AppendLine("Instructions:");
            builder.AppendLine(request.Instructions);
        }

        if (request.Schema is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Schema:");
            builder.AppendLine(request.Schema.ToJsonString(ProviderJson.Options));
        }

        builder.AppendLine();
        builder.AppendLine("Text:");
        builder.Append(request.Text);
        return builder.ToString();
    }

    private static string ComposeRerankPrompt(AiRerankRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("ai.rerank requires query.");
        }

        if (request.Candidates.Count == 0)
        {
            throw new ArgumentException("ai.rerank requires candidates.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("Rerank the candidates for the query.");
        builder.AppendLine("Return JSON only with this shape: {\"items\":[{\"id\":\"candidate-id\",\"rank\":1,\"score\":0.0}]}.");
        builder.AppendLine("Use only candidate ids supplied here. Higher score means more relevant.");
        if (request.Limit.HasValue)
        {
            builder.AppendLine($"Return at most {request.Limit.Value} items.");
        }

        builder.AppendLine();
        builder.AppendLine("Query:");
        builder.AppendLine(request.Query);
        builder.AppendLine();
        builder.AppendLine("Candidates:");
        foreach (var candidate in request.Candidates)
        {
            builder.Append("- id: ");
            builder.AppendLine(candidate.Id);
            builder.AppendLine("  text: |");
            foreach (var line in candidate.Text.Split('\n'))
            {
                builder.Append("    ");
                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }

    private static string ComposeReviewPrompt(AiReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) &&
            string.IsNullOrWhiteSpace(request.Subject) &&
            string.IsNullOrWhiteSpace(request.Instructions) &&
            request.References.Count == 0)
        {
            throw new ArgumentException("ai.review requires prompt, subject, instructions, or references.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("Review the following target and return a bounded review result.");
        builder.AppendLine("Expected JSON fields when possible: summary, findings[], validationStatus, references[].");
        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            builder.AppendLine();
            builder.AppendLine("Prompt:");
            builder.AppendLine(request.Prompt);
        }

        if (!string.IsNullOrWhiteSpace(request.Subject))
        {
            builder.AppendLine();
            builder.AppendLine("Subject:");
            builder.AppendLine(request.Subject);
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            builder.AppendLine();
            builder.AppendLine("Instructions:");
            builder.AppendLine(request.Instructions);
        }

        if (request.MaxFindings.HasValue)
        {
            builder.AppendLine();
            builder.AppendLine($"Max findings: {request.MaxFindings.Value}");
        }

        AppendReferences(builder, request.References);
        return builder.ToString();
    }

    private static string ComposeScaffoldPrompt(AiScaffoldRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("ai.scaffold requires prompt.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("Propose scaffold artifacts without writing files.");
        builder.AppendLine("Expected JSON fields when possible: summary, artifacts[], validationStatus, references[].");
        builder.AppendLine();
        builder.AppendLine("Prompt:");
        builder.AppendLine(request.Prompt);

        if (!string.IsNullOrWhiteSpace(request.Target))
        {
            builder.AppendLine();
            builder.AppendLine("Target:");
            builder.AppendLine(request.Target);
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            builder.AppendLine();
            builder.AppendLine("Instructions:");
            builder.AppendLine(request.Instructions);
        }

        if (request.AllowedPaths.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Allowed paths:");
            foreach (var path in request.AllowedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Take(64))
            {
                builder.AppendLine($"- {path}");
            }
        }

        if (request.MaxArtifacts.HasValue)
        {
            builder.AppendLine();
            builder.AppendLine($"Max artifacts: {request.MaxArtifacts.Value}");
        }

        AppendReferences(builder, request.References);
        return builder.ToString();
    }

    private static void AppendReferences(StringBuilder builder, IReadOnlyList<AiReference> references)
    {
        if (references.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("References:");
        foreach (var reference in references.Take(64))
        {
            builder.Append("- ");
            builder.Append(string.IsNullOrWhiteSpace(reference.Id) ? "(no id)" : reference.Id);
            builder.Append(" | kind=");
            builder.Append(string.IsNullOrWhiteSpace(reference.Kind) ? "context" : reference.Kind);
            if (!string.IsNullOrWhiteSpace(reference.Uri))
            {
                builder.Append(" | uri=");
                builder.Append(reference.Uri);
            }

            if (!string.IsNullOrWhiteSpace(reference.ContentHash))
            {
                builder.Append(" | hash=");
                builder.Append(reference.ContentHash);
            }

            if (!string.IsNullOrWhiteSpace(reference.Label))
            {
                builder.Append(" | label=");
                builder.Append(reference.Label);
            }

            builder.AppendLine();
        }
    }

    private static string ComposeToolPlanPrompt(AiToolPlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("ai.toolPlan requires prompt.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("Plan tool calls for the request without executing any tools.");
        builder.AppendLine("Return JSON only with this shape: {\"calls\":[{\"tool\":\"tool-name\",\"arguments\":{},\"requiresApproval\":true,\"rationale\":\"...\"}]}.");
        builder.AppendLine("Only use tools supplied here. Return an empty calls array if no tool should be proposed.");
        builder.AppendLine();
        builder.AppendLine("Prompt:");
        builder.AppendLine(request.Prompt);

        if (request.Tools.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Tools:");
            foreach (var tool in request.Tools.Where(tool => !string.IsNullOrWhiteSpace(tool.Name)).Take(64))
            {
                builder.Append("- name: ");
                builder.AppendLine(tool.Name);
                if (!string.IsNullOrWhiteSpace(tool.Description))
                {
                    builder.Append("  description: ");
                    builder.AppendLine(tool.Description);
                }

                if (tool.InputSchema is not null)
                {
                    builder.Append("  inputSchema: ");
                    builder.AppendLine(tool.InputSchema.ToJsonString(ProviderJson.Options));
                }
            }
        }

        return builder.ToString();
    }

    private static JsonObject NormalizeReviewOutput(JsonObject? parsed, string rawText, string validationStatus)
    {
        var output = parsed ?? ProviderJson.ToJsonObject(new AiReviewResult
        {
            Summary = Trim(rawText, 2000),
            ValidationStatus = "not_validated"
        });

        EnsureString(output, "summary", ResolveSummary(output, rawText));
        EnsureArray(output, "findings");
        EnsureString(output, "validationStatus", parsed is null ? "not_validated" : validationStatus);
        EnsureArray(output, "references");
        EnsureString(output, "rawText", rawText);
        return output;
    }

    private static JsonObject NormalizeRerankOutput(JsonObject? parsed, string rawText, string validationStatus)
    {
        var output = parsed ?? ProviderJson.ToJsonObject(new AiRerankResult());
        EnsureArray(output, "items");
        EnsureString(output, "validationStatus", parsed is null ? "not_validated" : validationStatus);
        EnsureString(output, "rawText", rawText);
        return output;
    }

    private static JsonObject NormalizeScaffoldOutput(JsonObject? parsed, string rawText, string validationStatus)
    {
        var output = parsed ?? ProviderJson.ToJsonObject(new AiScaffoldResult
        {
            Summary = Trim(rawText, 2000),
            ValidationStatus = "not_validated"
        });

        EnsureString(output, "summary", ResolveSummary(output, rawText));
        EnsureArray(output, "artifacts");
        EnsureString(output, "validationStatus", parsed is null ? "not_validated" : validationStatus);
        EnsureArray(output, "references");
        EnsureString(output, "rawText", rawText);
        return output;
    }

    private static JsonObject NormalizeToolPlanOutput(JsonObject? parsed, string rawText, string validationStatus)
    {
        var output = parsed ?? ProviderJson.ToJsonObject(new AiToolPlanResult());
        EnsureArray(output, "calls");
        EnsureString(output, "validationStatus", parsed is null ? "not_validated" : validationStatus);
        EnsureString(output, "rawText", rawText);
        return output;
    }

    private static void EnsureString(JsonObject output, string propertyName, string value)
    {
        if (!output.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            output[propertyName] = value;
        }
    }

    private static void EnsureArray(JsonObject output, string propertyName)
    {
        if (!output.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            output[propertyName] = new JsonArray();
        }
    }

    private static string ResolveSummary(JsonObject output, string rawText)
    {
        foreach (var propertyName in new[] { "summary", "content", "message", "text" })
        {
            if (output.TryGetPropertyValue(propertyName, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                return Trim(text, 2000);
            }
        }

        return Trim(rawText, 2000);
    }

    private static string Trim(string text, int maxChars)
    {
        return text.Length <= maxChars ? text : text[..maxChars];
    }
}
