using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Local;

public sealed class LocalTokenOverlapRerankerProviderTarget : IProviderTarget, IProviderQualificationPlanner, IProviderModelCatalog, IProviderDoctor
{
    public const string ProviderId = "local-token-overlap-reranker";
    private readonly IReadOnlyDictionary<string, ProviderModePolicy> _policies;

    public LocalTokenOverlapRerankerProviderTarget(IEnumerable<ProviderModePolicy>? policies = null)
    {
        _policies = ProviderModePolicies.Index(policies ?? ProviderModePolicies.Defaults(allowNetwork: false));
        Profile = new ProviderProfile
        {
            Id = ProviderId,
            DisplayName = "Local token-overlap reranker",
            Family = "local",
            Local = true,
            RequiresNetwork = false,
            Auth = "none",
            ConfigHash = ProviderHash.Sha256($"{ProviderId}|0.1")
        };
        Capabilities = new List<ProviderCapabilityDescriptor>
        {
            new()
            {
                Id = ProviderCapabilityIds.AiRerank,
                Operations = new List<string> { "run" },
                ToolPolicy = "none",
                InputLimits = new Dictionary<string, object?> { ["maxPayloadBytes"] = _policies.Values.Max(policy => policy.MaxInputBytes) },
                OutputLimits = new Dictionary<string, object?> { ["maxOutputBytes"] = _policies.Values.Max(policy => policy.MaxOutputBytes) },
                ModePolicies = _policies.Values.OrderBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                UnsupportedFeatures = new List<string> { "semantic_cross_encoder", "model_files", "network", "source_writes", "caller_tool_execution" }
            }
        };
    }

    public ProviderProfile Profile { get; }
    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }

    public Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
    {
        _ = ct;
        var stopwatch = Stopwatch.StartNew();
        var trace = new ProviderTraceEvent
        {
            Provider = Profile.Id,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            AdapterId = ProviderId,
            ConfigHash = Profile.ConfigHash,
            InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)),
            AuthorityBoundary = ProviderBoundary.AuthorityBoundary,
            ModelId = ProviderId
        };

        if (!string.Equals(request.Capability, ProviderCapabilityIds.AiRerank, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(CreateResult(request, trace, stopwatch, ProviderRunStatus.Unsupported, null, ProviderFailureClasses.Unsupported, "unsupported_capability"));
        }

        if (!string.Equals(request.Operation, "run", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(CreateResult(request, trace, stopwatch, ProviderRunStatus.Unsupported, null, ProviderFailureClasses.Unsupported, "unsupported_operation"));
        }

        var policy = ProviderModePolicies.Resolve(_policies, string.IsNullOrWhiteSpace(request.Mode) ? "advisory" : request.Mode);
        if (policy is null)
        {
            return Task.FromResult(CreateResult(request, trace, stopwatch, ProviderRunStatus.Rejected, "Unknown provider mode.", ProviderFailureClasses.Policy, "unknown_mode"));
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(request.Payload.ToJsonString(ProviderJson.Options));
        if (payloadBytes > policy.MaxInputBytes)
        {
            return Task.FromResult(CreateResult(request, trace, stopwatch, ProviderRunStatus.Rejected, "Payload exceeds mode input limit.", ProviderFailureClasses.Policy, "input_limit"));
        }

        try
        {
            var rerank = RunRerank(ProviderJson.DeserializePayload<AiRerankRequest>(request));
            var output = ProviderJson.ToJsonObject(rerank);
            return Task.FromResult(CreateResult(request, trace, stopwatch, ProviderRunStatus.Succeeded, output.ToJsonString(ProviderJson.Options), null, "ok", output));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(CreateResult(request, trace, stopwatch, ProviderRunStatus.Rejected, ex.Message, ProviderFailureClasses.Schema, "invalid_request"));
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
                    Query = "qualification smoke",
                    Candidates = new List<AiRerankCandidate>
                    {
                        new() { Id = "match", Text = "qualification smoke candidate" },
                        new() { Id = "other", Text = "unrelated candidate" }
                    },
                    Limit = 1
                })
            }
        };
    }

    public Task<ProviderModelListResult> ListModelsAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(new ProviderModelListResult
        {
            Provider = ProviderId,
            Source = "local-static",
            DefaultModelId = ProviderId,
            Items = new List<ProviderModelDescriptor>
            {
                new()
                {
                    Id = ProviderId,
                    DisplayName = "Local token-overlap reranker",
                    Default = true,
                    Capabilities = new List<string> { ProviderCapabilityIds.AiRerank },
                    Metadata = new Dictionary<string, object?>
                    {
                        ["source"] = "local-static",
                        ["semantic"] = false,
                        ["algorithm"] = "query-token-overlap",
                        ["network"] = false
                    }
                }
            }
        });
    }

    public Task<ProviderDoctorResult> DiagnoseAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(new ProviderDoctorResult
        {
            Provider = ProviderId,
            Status = ProviderDoctorStatuses.Ok,
            Summary = "Local token-overlap reranker is available without model files, network, or auth dependencies.",
            Checks = new List<ProviderDoctorCheck>
            {
                new()
                {
                    Id = "local.availability",
                    Status = ProviderDoctorStatuses.Ok,
                    Message = "Reranker is in-process and deterministic.",
                    Details = new Dictionary<string, object?>
                    {
                        ["requiresNetwork"] = false,
                        ["auth"] = "none"
                    }
                },
                new()
                {
                    Id = "model.binding",
                    Status = ProviderDoctorStatuses.Ok,
                    Message = $"Model id '{ProviderId}' is configured.",
                    Details = new Dictionary<string, object?>
                    {
                        ["modelId"] = ProviderId,
                        ["semantic"] = false
                    }
                }
            },
            Metadata = new Dictionary<string, object?>
            {
                ["family"] = Profile.Family,
                ["configHash"] = Profile.ConfigHash
            }
        });
    }

    private static AiRerankResult RunRerank(AiRerankRequest request)
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

        var queryTerms = Tokenize(request.Query);
        var ranked = request.Candidates
            .Select(candidate =>
            {
                var terms = Tokenize(candidate.Text);
                var overlap = terms.Count == 0 ? 0 : queryTerms.Intersect(terms).Count();
                var score = queryTerms.Count == 0 ? 0 : (double)overlap / queryTerms.Count;
                return new { candidate.Id, Score = Math.Round(score, 6), Tie = ProviderHash.Sha256(candidate.Id + candidate.Text) };
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Tie, StringComparer.Ordinal)
            .Take(limit)
            .Select((item, index) => new AiRerankItem { Id = item.Id, Rank = index + 1, Score = item.Score })
            .ToList();

        return new AiRerankResult { Items = ranked };
    }

    private static HashSet<string> Tokenize(string text)
    {
        return text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
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
            Provider = ProviderId,
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
