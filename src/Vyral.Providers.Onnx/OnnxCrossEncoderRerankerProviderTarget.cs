using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Vyral.Embeddings.Onnx;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Onnx;

public sealed class OnnxCrossEncoderRerankerProviderTarget : IProviderTarget, IProviderQualificationPlanner, IProviderModelCatalog, IProviderDoctor
{
    private readonly OnnxCrossEncoderRerankerProviderOptions _options;
    private readonly IReadOnlyDictionary<string, ProviderModePolicy> _policies;
    private readonly object _runtimeLock = new();
    private OnnxCrossEncoderReranker? _runtime;

    public OnnxCrossEncoderRerankerProviderTarget(
        OnnxCrossEncoderRerankerProviderOptions options,
        IEnumerable<ProviderModePolicy>? policies = null)
    {
        _options = OnnxCrossEncoderRerankerProviderTargets.ApplyDefaults(options, options.CpuOnly);
        _policies = ProviderModePolicies.Index(policies ?? ProviderModePolicies.Defaults(allowNetwork: false));
        Profile = new ProviderProfile
        {
            Id = _options.ProviderId,
            DisplayName = _options.DisplayName,
            Family = "onnx",
            Local = true,
            RequiresNetwork = false,
            Auth = "none",
            ConfigHash = ProviderHash.Sha256(BuildConfigIdentity(_options))
        };
        Capabilities = new List<ProviderCapabilityDescriptor>
        {
            new()
            {
                Id = ProviderCapabilityIds.AiRerank,
                Operations = new List<string> { "run" },
                ToolPolicy = "none",
                InputLimits = new Dictionary<string, object?>
                {
                    ["maxPayloadBytes"] = _policies.Values.Max(policy => policy.MaxInputBytes),
                    ["maxTokens"] = _options.MaxTokens,
                    ["batchSize"] = _options.BatchSize
                },
                OutputLimits = new Dictionary<string, object?> { ["maxOutputBytes"] = _policies.Values.Max(policy => policy.MaxOutputBytes) },
                ModePolicies = _policies.Values.OrderBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                UnsupportedFeatures = new List<string> { "network", "source_writes", "caller_tool_execution", "model_download" }
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

        if (!string.Equals(request.Capability, ProviderCapabilityIds.AiRerank, StringComparison.OrdinalIgnoreCase))
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
            var rerank = await RunRerankAsync(ProviderJson.DeserializePayload<AiRerankRequest>(request), ct);
            var output = ProviderJson.ToJsonObject(rerank);
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Succeeded, output.ToJsonString(ProviderJson.Options), null, "ok", output);
        }
        catch (OperationCanceledException)
        {
            return CreateResult(request, trace, stopwatch, ProviderRunStatus.Cancelled, "ONNX cross-encoder rerank was cancelled.", ProviderFailureClasses.Cancelled, "cancelled");
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
            !string.Equals(request.Capability, ProviderCapabilityIds.AiRerank, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<ProviderRunRequest>();
        }

        return new List<ProviderRunRequest>
        {
            new()
            {
                Capability = ProviderCapabilityIds.AiRerank,
                Operation = "run",
                Mode = string.IsNullOrWhiteSpace(request.Mode) ? "mechanics" : request.Mode,
                Payload = ProviderJson.ToJsonObject(new AiRerankRequest
                {
                    Query = "retention hold release deletion",
                    Candidates = new List<AiRerankCandidate>
                    {
                        new() { Id = "match", Text = "records subject to a retention hold may be deleted after an authorized release" },
                        new() { Id = "other", Text = "employee travel reimbursements require a cost center and receipts" }
                    },
                    Limit = 1
                })
            }
        };
    }

    public Task<ProviderModelListResult> ListModelsAsync(CancellationToken ct = default)
    {
        _ = ct;
        var modelPath = TryResolveModelPath(_options.ModelPath);
        var vocabPath = TryResolveVocabPath(_options.VocabPath, modelPath ?? _options.ModelPath);
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
                    Capabilities = new List<string> { ProviderCapabilityIds.AiRerank },
                    Metadata = new Dictionary<string, object?>
                    {
                        ["source"] = "local-config",
                        ["semantic"] = true,
                        ["algorithm"] = "onnx-cross-encoder",
                        ["modelPath"] = _options.ModelPath,
                        ["vocabPath"] = _options.VocabPath,
                        ["executionProvider"] = _options.ExecutionProvider,
                        ["cpuOnly"] = _options.CpuOnly,
                        ["maxTokens"] = _options.MaxTokens,
                        ["batchSize"] = _options.BatchSize,
                        ["scoreMode"] = _options.ScoreMode,
                        ["network"] = false,
                        ["modelFilePresent"] = modelPath != null,
                        ["vocabFilePresent"] = vocabPath != null
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
        var checks = new List<ProviderDoctorCheck>
        {
            new()
            {
                Id = "local.availability",
                Status = ProviderDoctorStatuses.Ok,
                Message = "ONNX cross-encoder reranker runs in-process without auth or network access.",
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
                    ? $"ONNX cross-encoder model file was not found: {_options.ModelPath}"
                    : $"ONNX cross-encoder model file is present: {modelPath}",
                Details = new Dictionary<string, object?>
                {
                    ["configuredPath"] = _options.ModelPath,
                    ["resolvedPath"] = modelPath,
                    ["modelId"] = _options.ModelId
                }
            },
            new()
            {
                Id = "tokenizer.vocab",
                Status = vocabPath == null ? ProviderDoctorStatuses.Failed : ProviderDoctorStatuses.Ok,
                Message = vocabPath == null
                    ? $"ONNX cross-encoder vocabulary file was not found: {_options.VocabPath}"
                    : $"ONNX cross-encoder vocabulary file is present: {vocabPath}",
                Details = new Dictionary<string, object?>
                {
                    ["configuredPath"] = _options.VocabPath,
                    ["resolvedPath"] = vocabPath
                }
            }
        };

        return Task.FromResult(new ProviderDoctorResult
        {
            Provider = Profile.Id,
            Status = ProviderDoctorStatuses.Aggregate(checks),
            Summary = modelPath == null || vocabPath == null
                ? "ONNX cross-encoder reranker is configured but missing local untracked model files."
                : "ONNX cross-encoder reranker model files are available.",
            Checks = checks,
            Metadata = new Dictionary<string, object?>
            {
                ["family"] = Profile.Family,
                ["configHash"] = Profile.ConfigHash,
                ["modelId"] = _options.ModelId,
                ["maxTokens"] = _options.MaxTokens,
                ["batchSize"] = _options.BatchSize,
                ["scoreMode"] = _options.ScoreMode
            }
        });
    }

    private async Task<AiRerankResult> RunRerankAsync(AiRerankRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("ai.rerank requires query.");
        }

        if (request.Candidates.Count == 0)
        {
            throw new ArgumentException("ai.rerank requires at least one candidate.");
        }

        var limit = request.Limit.GetValueOrDefault(request.Candidates.Count);
        if (limit <= 0)
        {
            throw new ArgumentException("ai.rerank limit must be positive.");
        }

        var runtime = GetRuntime();
        var scores = await runtime.RerankAsync(
            request.Query,
            request.Candidates.Select(candidate => new OnnxCrossEncoderRerankCandidate
            {
                Id = candidate.Id,
                Text = candidate.Text
            }).ToList(),
            limit,
            ct);

        return new AiRerankResult
        {
            Items = scores.Select((item, index) => new AiRerankItem
            {
                Id = item.Id,
                Rank = index + 1,
                Score = Math.Round(item.Score, 6)
            }).ToList()
        };
    }

    private OnnxCrossEncoderReranker GetRuntime()
    {
        if (_runtime != null)
        {
            return _runtime;
        }

        lock (_runtimeLock)
        {
            _runtime ??= new OnnxCrossEncoderReranker(_options.ToRuntimeOptions());
            return _runtime;
        }
    }

    private static string BuildConfigIdentity(OnnxCrossEncoderRerankerProviderOptions options)
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
            options.OutputName,
            options.ScoreMode,
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
