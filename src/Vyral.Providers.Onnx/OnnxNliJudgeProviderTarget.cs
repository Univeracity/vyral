using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Vyral.Embeddings.Onnx;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Onnx;

/// <summary>
/// Local, in-process implementer of <see cref="ProviderCapabilityIds.AiJudge"/> using a zero-shot
/// NLI/entailment ONNX model: each candidate option is scored as an entailment hypothesis against
/// the question's premise, and per-option entailment logits are (optionally temperature-scaled and)
/// softmaxed into a probability distribution. This is a heuristic proxy for a purpose-trained
/// judgment model, not an equivalent — see the "calibrated" flag on every answer and the package
/// README before treating its probabilities as validated likelihoods.
/// </summary>
public sealed class OnnxNliJudgeProviderTarget : IProviderTarget, IProviderQualificationPlanner, IProviderModelCatalog, IProviderDoctor
{
    private readonly OnnxNliJudgeProviderOptions _options;
    private readonly IReadOnlyDictionary<string, ProviderModePolicy> _policies;
    private readonly object _runtimeLock = new();
    private OnnxNliClassifier? _runtime;
    private OnnxJudgeCalibration? _calibration;
    private bool _calibrationLoaded;

    public OnnxNliJudgeProviderTarget(OnnxNliJudgeProviderOptions options, IEnumerable<ProviderModePolicy>? policies = null)
    {
        _options = OnnxNliJudgeProviderTargets.ApplyDefaults(options, options.CpuOnly);
        _policies = ProviderModePolicies.Index(policies ?? ProviderModePolicies.Defaults(allowNetwork: false));
        Profile = new ProviderProfile
        {
            Id = _options.ProviderId,
            DisplayName = _options.DisplayName,
            Family = "onnx",
            Local = true,
            RequiresNetwork = false,
            Auth = ProviderAuthTypes.None,
            ConfigHash = ProviderHash.Sha256(BuildConfigIdentity(_options))
        };
        Capabilities = new List<ProviderCapabilityDescriptor>
        {
            new()
            {
                Id = ProviderCapabilityIds.AiJudge,
                Operations = new List<string> { "run" },
                ToolPolicy = ProviderToolPolicies.CallerOwned,
                InputLimits = new Dictionary<string, object?>
                {
                    ["maxPayloadBytes"] = _policies.Values.Max(policy => policy.MaxInputBytes),
                    ["maxTokens"] = _options.MaxTokens,
                    ["batchSize"] = _options.BatchSize
                },
                OutputLimits = new Dictionary<string, object?> { ["maxOutputBytes"] = _policies.Values.Max(policy => policy.MaxOutputBytes) },
                ModePolicies = _policies.Values.OrderBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                UnsupportedFeatures = new List<string> { "network", "source_writes", "caller_tool_execution", "model_download", "validated_calibration" }
            }
        };
    }

    public ProviderProfile Profile { get; }
    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }

    public async Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var trace = new ProviderTraceEvent
        {
            Provider = Profile.Id,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            AdapterId = Profile.Id,
            ConfigHash = Profile.ConfigHash,
            InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)),
            AuthorityBoundary = ProviderBoundary.AuthorityBoundary,
            ModelId = _options.ModelId
        };

        if (!string.Equals(request.Capability, ProviderCapabilityIds.AiJudge, StringComparison.OrdinalIgnoreCase))
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Unsupported, null, ProviderFailureClasses.Unsupported, "unsupported_capability");
        }

        if (!string.Equals(request.Operation, "run", StringComparison.OrdinalIgnoreCase))
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Unsupported, null, ProviderFailureClasses.Unsupported, "unsupported_operation");
        }

        var policy = ProviderModePolicies.Resolve(_policies, string.IsNullOrWhiteSpace(request.Mode) ? "advisory" : request.Mode);
        if (policy is null)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Rejected, "Unknown provider mode.", ProviderFailureClasses.Policy, "unknown_mode");
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(request.Payload.ToJsonString(ProviderJson.Options));
        if (payloadBytes > policy.MaxInputBytes)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Rejected, "Payload exceeds mode input limit.", ProviderFailureClasses.Policy, "input_limit");
        }

        try
        {
            var judge = await RunJudgeAsync(ProviderJson.DeserializePayload<AiJudgeRequest>(request), ct);
            var output = ProviderJson.ToJsonObject(judge);
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Succeeded, output.ToJsonString(ProviderJson.Options), null, "ok", output);
        }
        catch (OperationCanceledException)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Cancelled, "ONNX NLI judge run was cancelled.", ProviderFailureClasses.Cancelled, "cancelled");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.NotConfigured, ex.Message, ProviderFailureClasses.Configuration, "model_files_missing");
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Rejected, ex.Message, ProviderFailureClasses.Schema, "invalid_request");
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Failed, ex.Message, ProviderFailureClasses.Configuration, "runtime_configuration_error");
        }
        catch (Exception ex)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Failed, ex.Message, ProviderFailureClasses.ProviderUnavailable, "runtime_failure");
        }
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
                Operation = "run",
                Mode = string.IsNullOrWhiteSpace(request.Mode) ? "mechanics" : request.Mode,
                Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
                {
                    Context = "records subject to a retention hold may be deleted after an authorized release",
                    Questions = new List<AiJudgeQuestion>
                    {
                        new()
                        {
                            Id = "smoke-1",
                            Type = AiJudgeQuestionTypes.Choice,
                            Prompt = "what topic does this text describe",
                            Options = new List<AiJudgeOption>
                            {
                                new() { Id = "retention", Label = "records retention and deletion" },
                                new() { Id = "travel", Label = "employee travel reimbursement" }
                            }
                        }
                    }
                })
            }
        };
    }

    public Task<ProviderModelListResult> ListModelsAsync(CancellationToken ct = default)
    {
        _ = ct;
        var modelPath = TryResolveModelPath(_options.ModelPath);
        var vocabPath = TryResolveVocabPath(_options.VocabPath, modelPath ?? _options.ModelPath);
        var calibration = LoadCalibration();
        return Task.FromResult(new ProviderModelListResult
        {
            Provider = Profile.Id,
            Source = "local-config",
            DefaultModelId = _options.ModelId,
            Items = new List<ProviderModelDescriptor>
            {
                new()
                {
                    Id = _options.ModelId ?? Profile.Id,
                    DisplayName = _options.DisplayName,
                    Default = true,
                    Capabilities = new List<string> { ProviderCapabilityIds.AiJudge },
                    Metadata = new Dictionary<string, object?>
                    {
                        ["source"] = "local-config",
                        ["semantic"] = true,
                        ["algorithm"] = "onnx-nli-zero-shot",
                        ["modelPath"] = _options.ModelPath,
                        ["vocabPath"] = _options.VocabPath,
                        ["executionProvider"] = _options.ExecutionProvider,
                        ["cpuOnly"] = _options.CpuOnly,
                        ["entailmentIndex"] = _options.EntailmentIndex,
                        ["network"] = false,
                        ["modelFilePresent"] = modelPath != null,
                        ["vocabFilePresent"] = vocabPath != null,
                        ["calibrated"] = calibration?.IsFitted ?? false,
                        ["calibrationTemperature"] = calibration?.Temperature ?? 1.0
                    }
                }
            }
        });
    }

    public Task<ProviderDoctorResult> DiagnoseAsync(CancellationToken ct = default)
    {
        _ = ct;
        var modelPath = TryResolveModelPath(_options.ModelPath);
        var vocabPath = TryResolveVocabPath(_options.VocabPath, modelPath ?? _options.ModelPath);
        OnnxJudgeCalibration? calibration = null;
        string? calibrationError = null;
        try
        {
            calibration = LoadCalibration();
        }
        catch (InvalidOperationException ex)
        {
            calibrationError = ex.Message;
        }

        var checks = new List<ProviderDoctorCheck>
        {
            new()
            {
                Id = "local.availability",
                Status = ProviderDoctorStatuses.Ok,
                Message = "ONNX NLI judge runs in-process without auth or network access.",
                Details = new Dictionary<string, object?>
                {
                    ["requiresNetwork"] = false,
                    ["auth"] = "none",
                    ["executionProvider"] = _options.ExecutionProvider,
                    ["cpuOnly"] = _options.CpuOnly
                }
            },
            new()
            {
                Id = "model.file",
                Status = modelPath == null ? ProviderDoctorStatuses.Failed : ProviderDoctorStatuses.Ok,
                Message = modelPath == null
                    ? $"ONNX NLI model file was not found: {_options.ModelPath}"
                    : $"ONNX NLI model file is present: {modelPath}",
                Details = new Dictionary<string, object?> { ["configuredPath"] = _options.ModelPath, ["resolvedPath"] = modelPath, ["modelId"] = _options.ModelId }
            },
            new()
            {
                Id = "tokenizer.vocab",
                Status = vocabPath == null ? ProviderDoctorStatuses.Failed : ProviderDoctorStatuses.Ok,
                Message = vocabPath == null
                    ? $"ONNX NLI vocabulary file was not found: {_options.VocabPath}"
                    : $"ONNX NLI vocabulary file is present: {vocabPath}",
                Details = new Dictionary<string, object?> { ["configuredPath"] = _options.VocabPath, ["resolvedPath"] = vocabPath }
            },
            new()
            {
                Id = "calibration.status",
                Status = calibrationError != null
                    ? ProviderDoctorStatuses.Failed
                    : (calibration?.IsFitted ?? false) ? ProviderDoctorStatuses.Ok : ProviderDoctorStatuses.Warning,
                Message = calibrationError != null
                    ? $"Calibration file is present but invalid: {calibrationError}"
                    : (calibration?.IsFitted ?? false)
                        ? $"Loaded a fitted calibration (temperature {calibration!.Temperature:F3} from {calibration.SampleCount} labeled examples)."
                        : "No fitted calibration is loaded; entailment probabilities are an uncalibrated heuristic proxy, not a validated likelihood. Fit one with OnnxJudgeCalibrationFitter and set CalibrationPath.",
                Details = new Dictionary<string, object?>
                {
                    ["calibrationPath"] = _options.CalibrationPath,
                    ["calibrated"] = calibration?.IsFitted ?? false,
                    ["temperature"] = calibration?.Temperature ?? 1.0
                }
            },
            new()
            {
                Id = "judgment.equivalence",
                Status = ProviderDoctorStatuses.Warning,
                Message = "This is a repurposed zero-shot NLI classifier, not an equivalent to a purpose-trained judgment provider (e.g. a remote ai.judge API). Its probabilities are heuristic even when calibrated; verify the loaded checkpoint's class order matches EntailmentIndex before trusting scores.",
                Details = new Dictionary<string, object?> { ["entailmentIndex"] = _options.EntailmentIndex }
            }
        };

        var status = ProviderDoctorStatuses.Aggregate(checks);
        return Task.FromResult(new ProviderDoctorResult
        {
            Provider = Profile.Id,
            Status = status,
            Summary = status == ProviderDoctorStatuses.Failed
                ? "ONNX NLI judge is configured but missing local untracked model files."
                : "ONNX NLI judge model files are available; see judgment.equivalence and calibration.status before treating output as calibrated.",
            Checks = checks,
            Metadata = new Dictionary<string, object?> { ["family"] = Profile.Family, ["configHash"] = Profile.ConfigHash, ["modelId"] = _options.ModelId }
        });
    }

    private async Task<AiJudgeResult> RunJudgeAsync(AiJudgeRequest request, CancellationToken ct)
    {
        if (request.Questions.Count == 0)
        {
            throw new ArgumentException("ai.judge requires at least one question.");
        }

        var runtime = GetRuntime();
        var calibration = LoadCalibration() ?? OnnxJudgeCalibration.Identity(_options.ModelId);
        var premise = request.Context ?? string.Empty;
        var answers = new List<AiJudgeAnswer>(request.Questions.Count);

        foreach (var question in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.Id))
            {
                throw new ArgumentException("ai.judge questions require an id.");
            }

            if (string.Equals(question.Type, AiJudgeQuestionTypes.Noul, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(premise))
                {
                    throw new ArgumentException($"ai.judge question '{question.Id}' is type 'noul' and requires request.context as the passage to classify against.");
                }

                // Premise is the passage alone; the prompt IS the hypothesis being tested against it.
                // Concatenating the prompt into the premise (as Choice questions do below, for framing)
                // would make premise and hypothesis identical here, producing a trivially-true
                // self-entailment instead of a real judgment.
                var hypotheses = new[] { new OnnxNliHypothesis { Id = question.Id, Text = question.Prompt } };
                var classifications = await runtime.ClassifyAsync(premise, hypotheses, ct);
                var classification = classifications[0];
                RequireEntailmentIndex(classification.Logits.Length);
                var calibrated = calibration.ApplyToLogits(classification.Logits);
                var probabilities = OnnxJudgeCalibrationFitter.Softmax(calibrated);
                var probability = probabilities[_options.EntailmentIndex];
                answers.Add(new AiJudgeAnswer
                {
                    QuestionId = question.Id,
                    Probability = Math.Round(probability, 6),
                    Confidence = Math.Round(Math.Max(probability, 1 - probability), 6),
                    Calibrated = calibration.IsFitted,
                    RawClassProbabilities = BuildRawClassProbabilities(probabilities)
                });
                continue;
            }

            if (string.Equals(question.Type, AiJudgeQuestionTypes.Choice, StringComparison.OrdinalIgnoreCase))
            {
                if (question.Options.Count == 0)
                {
                    throw new ArgumentException($"ai.judge question '{question.Id}' is type 'choice' and requires at least one option.");
                }

                if (string.IsNullOrWhiteSpace(premise))
                {
                    throw new ArgumentException($"ai.judge question '{question.Id}' is type 'choice' and requires request.context as the passage to classify against.");
                }

                var optionProbabilities = await ScoreOptionsByEntailmentAsync(runtime, premise, question, _options.ChoiceHypothesisTemplate, calibration, ct);
                var probabilities = new Dictionary<string, double>();
                for (var i = 0; i < question.Options.Count; i++)
                {
                    probabilities[question.Options[i].Id] = Math.Round(optionProbabilities[i], 6);
                }

                var winner = probabilities.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First();
                answers.Add(new AiJudgeAnswer
                {
                    QuestionId = question.Id,
                    Choice = winner.Key,
                    Probabilities = probabilities,
                    Confidence = winner.Value,
                    Calibrated = calibration.IsFitted
                });
                continue;
            }

            if (string.Equals(question.Type, AiJudgeQuestionTypes.Score, StringComparison.OrdinalIgnoreCase))
            {
                if (question.Options.Count < 2)
                {
                    throw new ArgumentException($"ai.judge question '{question.Id}' is type 'score' and requires at least two ordered levels.");
                }

                if (string.IsNullOrWhiteSpace(premise))
                {
                    throw new ArgumentException($"ai.judge question '{question.Id}' is type 'score' and requires request.context as the passage to classify against.");
                }

                var levelProbabilities = await ScoreOptionsByEntailmentAsync(runtime, premise, question, _options.ScoreHypothesisTemplate, calibration, ct);
                var probabilities = new Dictionary<string, double>();
                var legend = new Dictionary<string, string>();
                var expectedIndex = 0.0;
                for (var i = 0; i < question.Options.Count; i++)
                {
                    probabilities[question.Options[i].Id] = Math.Round(levelProbabilities[i], 6);
                    legend[question.Options[i].Id] = question.Options[i].Label;
                    expectedIndex += i * levelProbabilities[i];
                }

                answers.Add(new AiJudgeAnswer
                {
                    QuestionId = question.Id,
                    Score = Math.Round(expectedIndex, 6),
                    Probabilities = probabilities,
                    Legend = legend,
                    Confidence = Math.Round(levelProbabilities.Max(), 6),
                    Calibrated = calibration.IsFitted
                });
                continue;
            }

            throw new ArgumentException($"ai.judge question '{question.Id}' has unsupported type '{question.Type}'.");
        }

        return new AiJudgeResult { Answers = answers, ValidationStatus = AiValidationStatuses.ProviderJson, ModelId = _options.ModelId };
    }

    /// <summary>
    /// Scores each of a question's options/levels as an entailment hypothesis against
    /// <paramref name="premise"/> and returns calibrated probabilities in <c>question.Options</c>
    /// order. Shared by Choice (unordered options) and Score (ordered levels) — both reduce to the
    /// same "which hypothesis does the premise entail" mechanism; only how the caller interprets the
    /// resulting distribution differs (argmax for Choice, a weighted position for Score). Premise is
    /// the passage alone, matching the standard zero-shot-classification NLI recipe
    /// (premise=passage, hypothesis="This example is {label}."), not the passage with the question's
    /// instructions appended. A task that needs the claim/question text inside the hypothesis itself
    /// (e.g. citation-relation checking: "the passage {label} the claim: {prompt}") can express that
    /// via {prompt} in the template.
    /// </summary>
    private async Task<double[]> ScoreOptionsByEntailmentAsync(
        OnnxNliClassifier runtime,
        string premise,
        AiJudgeQuestion question,
        string hypothesisTemplate,
        OnnxJudgeCalibration calibration,
        CancellationToken ct)
    {
        var hypotheses = question.Options
            .Select(option => new OnnxNliHypothesis
            {
                Id = option.Id,
                Text = hypothesisTemplate.Replace("{label}", option.Label).Replace("{prompt}", question.Prompt)
            })
            .ToList();
        var classifications = await runtime.ClassifyAsync(premise, hypotheses, ct);
        RequireEntailmentIndex(classifications[0].Logits.Length);

        var entailmentLogits = classifications
            .OrderBy(item => item.OriginalIndex)
            .Select(item => calibration.ApplyToLogit(item.Logits[_options.EntailmentIndex]))
            .ToArray();
        return OnnxJudgeCalibrationFitter.Softmax(entailmentLogits);
    }

    private void RequireEntailmentIndex(int numClasses)
    {
        if (_options.EntailmentIndex < 0 || _options.EntailmentIndex >= numClasses)
        {
            throw new InvalidOperationException(
                $"Configured EntailmentIndex {_options.EntailmentIndex} is out of range for a {numClasses}-class NLI output. Confirm the loaded checkpoint's class order.");
        }
    }

    /// <summary>
    /// Labels a Noul answer's full calibrated class distribution using <see cref="OnnxNliJudgeProviderOptions.ClassLabels"/>.
    /// Returns null (not a partial or index-keyed map) when ClassLabels is unset or its length
    /// doesn't match the model's actual class count — an honest "not confirmed for this checkpoint"
    /// rather than a guessed labeling. See SUG-20260918-102018-D06D72: for a genuine entailment
    /// checkpoint this distribution answers a claim-verification question (supports/contradicts/says
    /// nothing) directly, which the Choice path cannot — Choice compares N different hypotheses
    /// against each other for topic selection; this is one hypothesis's own native class breakdown.
    /// </summary>
    private Dictionary<string, double>? BuildRawClassProbabilities(double[] probabilities)
    {
        if (_options.ClassLabels is not { Length: > 0 } labels || labels.Length != probabilities.Length)
        {
            return null;
        }

        var result = new Dictionary<string, double>();
        for (var i = 0; i < probabilities.Length; i++)
        {
            result[labels[i]] = Math.Round(probabilities[i], 6);
        }

        return result;
    }

    private OnnxJudgeCalibration? LoadCalibration()
    {
        if (_calibrationLoaded)
        {
            return _calibration;
        }

        lock (_runtimeLock)
        {
            if (!_calibrationLoaded)
            {
                // Resolve the same way as model/vocab paths (ancestor-walk from cwd and base
                // directory), not a bare File.Exists against the process's current directory — a
                // relative CalibrationPath must be found the same regardless of whether the caller
                // runs from the repo root or a test runner's output directory.
                var resolvedPath = TryResolveCalibrationPath(_options.CalibrationPath);
                _calibration = resolvedPath is null ? null : OnnxJudgeCalibration.TryLoad(resolvedPath);
                _calibrationLoaded = true;
            }

            return _calibration;
        }
    }

    private static string? TryResolveCalibrationPath(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        return ExpandConfiguredPaths(configured).FirstOrDefault(File.Exists);
    }

    private OnnxNliClassifier GetRuntime()
    {
        if (_runtime != null)
        {
            return _runtime;
        }

        lock (_runtimeLock)
        {
            _runtime ??= new OnnxNliClassifier(_options.ToRuntimeOptions());
            return _runtime;
        }
    }

    private static string BuildConfigIdentity(OnnxNliJudgeProviderOptions options)
    {
        return string.Join("|", new[]
        {
            options.ProviderId,
            options.ModelId,
            options.ModelPath,
            options.VocabPath,
            options.ExecutionProvider,
            options.MaxTokens.ToString(),
            options.BatchSize.ToString(),
            options.EntailmentIndex.ToString(),
            options.ClassLabels is null ? "null" : string.Join(",", options.ClassLabels),
            options.CalibrationPath,
            options.IntraOpNumThreads?.ToString(),
            options.InterOpNumThreads?.ToString(),
            options.ExecutionMode,
            options.CudaDeviceId?.ToString(),
            options.CudaMemoryLimitMb?.ToString()
        });
    }

    private static ProviderRunResult CreateResult(
        ProviderRunRequest request,
        ProviderTraceEvent trace,
        Stopwatch stopwatch,
        ProviderRunStatus status,
        string? textOutput,
        string? failureClass,
        string providerStatus,
        JsonObject? output = null)
    {
        stopwatch.Stop();
        trace.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
        trace.FailureClass = failureClass;
        trace.OutputHash = ProviderHash.Sha256(textOutput);

        var obj = output ?? new JsonObject();
        if (textOutput != null) obj["text"] = textOutput;
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

    private static string? TryResolveModelPath(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        foreach (var path in ExpandConfiguredPaths(configured))
        {
            if (File.Exists(path))
            {
                return path;
            }

            if (Directory.Exists(path))
            {
                var candidates = new[]
                {
                    Path.Combine(path, "model.onnx"),
                    Path.Combine(path, "model_quantized.onnx"),
                    Path.Combine(path, "onnx", "model.onnx"),
                    Path.Combine(path, "onnx", "model_quantized.onnx")
                };
                var match = candidates.FirstOrDefault(File.Exists);
                if (match != null)
                {
                    return Path.GetFullPath(match);
                }
            }
        }

        return null;
    }

    private static string? TryResolveVocabPath(string? configured, string? modelPath)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var configuredPath in ExpandConfiguredPaths(configured))
            {
                if (File.Exists(configuredPath))
                {
                    return configuredPath;
                }
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        var modelDirectory = Path.GetDirectoryName(modelPath) ?? string.Empty;
        var candidates = new[]
        {
            Path.Combine(modelDirectory, "vocab.txt"),
            Path.Combine(modelDirectory, "..", "vocab.txt")
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> ExpandConfiguredPaths(string configured)
    {
        yield return Path.GetFullPath(configured);
        if (Path.IsPathRooted(configured))
        {
            yield break;
        }

        foreach (var path in ExpandFromAncestors(Directory.GetCurrentDirectory(), configured))
        {
            yield return path;
        }

        foreach (var path in ExpandFromAncestors(AppContext.BaseDirectory, configured))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> ExpandFromAncestors(string startPath, string relativePath)
    {
        var directory = new DirectoryInfo(startPath);
        if (File.Exists(startPath))
        {
            directory = new FileInfo(startPath).Directory!;
        }

        while (directory != null)
        {
            yield return Path.GetFullPath(Path.Combine(directory.FullName, relativePath));
            directory = directory.Parent;
        }
    }
}
