using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Local;

public sealed class DeterministicAiProviderTarget : IProviderTarget, IProviderQualificationPlanner, IProviderModelCatalog, IProviderDoctor
{
    public const string ProviderId = "local-deterministic-ai";
    private readonly IReadOnlyDictionary<string, ProviderModePolicy> _policies;

    public DeterministicAiProviderTarget(IEnumerable<ProviderModePolicy>? policies = null)
    {
        _policies = ProviderModePolicies.Index(policies ?? ProviderModePolicies.Defaults(allowNetwork: false));

        Profile = new ProviderProfile
        {
            Id = ProviderId,
            DisplayName = "Local deterministic AI provider",
            Family = "local",
            Local = true,
            RequiresNetwork = false,
            Auth = "none",
            ConfigHash = ProviderHash.Sha256($"{ProviderId}|0.1")
        };

        var capabilityIds = new[]
        {
            ProviderCapabilityIds.AiChat,
            ProviderCapabilityIds.AiExtract,
            ProviderCapabilityIds.AiRerank,
            ProviderCapabilityIds.AiReview,
            ProviderCapabilityIds.AiScaffold,
            ProviderCapabilityIds.AiToolPlan
        };

        Capabilities = capabilityIds.Select(id => new ProviderCapabilityDescriptor
        {
            Id = id,
            Operations = new List<string> { "run" },
            ToolPolicy = "caller-owned",
            InputLimits = new Dictionary<string, object?> { ["maxPayloadBytes"] = _policies.Values.Max(policy => policy.MaxInputBytes) },
            OutputLimits = new Dictionary<string, object?> { ["maxOutputBytes"] = _policies.Values.Max(policy => policy.MaxOutputBytes) },
            ModePolicies = _policies.Values.OrderBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            UnsupportedFeatures = new List<string> { "semantic_reasoning", "source_writes", "caller_tool_execution" }
        }).ToList();
    }

    public ProviderProfile Profile { get; }
    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }

    public Task<ProviderDoctorResult> DiagnoseAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(new ProviderDoctorResult
        {
            Provider = Profile.Id,
            Status = ProviderDoctorStatuses.Ok,
            Summary = "Local deterministic provider is available without external command, network, or auth dependencies.",
            Checks = new List<ProviderDoctorCheck>
            {
                new()
                {
                    Id = "local.availability",
                    Status = ProviderDoctorStatuses.Ok,
                    Message = "Provider is in-process and deterministic.",
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

    public Task<ProviderModelListResult> ListModelsAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(new ProviderModelListResult
        {
            Provider = Profile.Id,
            Source = "local-static",
            DefaultModelId = ProviderId,
            Items = new List<ProviderModelDescriptor>
            {
                new()
                {
                    Id = ProviderId,
                    DisplayName = "Local deterministic mechanics model",
                    Default = true,
                    Capabilities = Capabilities.Select(c => c.Id).ToList(),
                    Metadata = new Dictionary<string, object?>
                    {
                        ["source"] = "local-static",
                        ["semantic"] = false
                    }
                }
            }
        });
    }

    public IReadOnlyList<ProviderRunRequest> CreateQualificationRequests(ProviderQualificationRequest request)
    {
        return Capabilities
            .Where(capability => string.IsNullOrWhiteSpace(request.Capability) || string.Equals(capability.Id, request.Capability, StringComparison.OrdinalIgnoreCase))
            .Select(capability => CreateQualificationRequest(capability.Id, string.IsNullOrWhiteSpace(request.Mode) ? "mechanics" : request.Mode))
            .Where(qualificationRequest => qualificationRequest is not null)
            .Select(qualificationRequest => qualificationRequest!)
            .ToList();
    }

    public Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var policy = ProviderModePolicies.Resolve(_policies, request.Mode);
        var trace = new ProviderTraceEvent
        {
            Provider = Profile.Id,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            AdapterId = "local-deterministic",
            ConfigHash = Profile.ConfigHash,
            InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options))
        };

        if (!Capabilities.Any(c => string.Equals(c.Id, request.Capability, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(CreateResult(request, trace, stopwatch, ProviderRunStatus.Unsupported, null, ProviderFailureClasses.Unsupported, "unsupported_capability"));
        }

        if (!string.Equals(request.Operation, "run", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(CreateResult(request, trace, stopwatch, ProviderRunStatus.Unsupported, null, ProviderFailureClasses.Unsupported, "unsupported_operation"));
        }

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
            var output = request.Capability switch
            {
                ProviderCapabilityIds.AiChat => ProviderJson.ToJsonObject(RunChat(ProviderJson.DeserializePayload<AiChatRequest>(request))),
                ProviderCapabilityIds.AiExtract => ProviderJson.ToJsonObject(RunExtract(ProviderJson.DeserializePayload<AiExtractRequest>(request))),
                ProviderCapabilityIds.AiRerank => ProviderJson.ToJsonObject(RunRerank(ProviderJson.DeserializePayload<AiRerankRequest>(request))),
                ProviderCapabilityIds.AiReview => ProviderJson.ToJsonObject(RunReview(ProviderJson.DeserializePayload<AiReviewRequest>(request))),
                ProviderCapabilityIds.AiScaffold => ProviderJson.ToJsonObject(RunScaffold(ProviderJson.DeserializePayload<AiScaffoldRequest>(request))),
                ProviderCapabilityIds.AiToolPlan => ProviderJson.ToJsonObject(RunToolPlan(ProviderJson.DeserializePayload<AiToolPlanRequest>(request))),
                _ => new JsonObject()
            };
            var text = output.ToJsonString(ProviderJson.Options);
            return Task.FromResult(CreateResult(request, trace, stopwatch, ProviderRunStatus.Succeeded, text, null, "ok", output));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(CreateResult(request, trace, stopwatch, ProviderRunStatus.Rejected, ex.Message, ProviderFailureClasses.Schema, "invalid_request"));
        }
    }

    private static AiChatResult RunChat(AiChatRequest request)
    {
        var userText = request.Messages.LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content
            ?? request.Messages.LastOrDefault()?.Content
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userText))
        {
            throw new ArgumentException("ai.chat requires at least one message with content.");
        }

        var content = $"deterministic advisory response [{ProviderHash.Sha256(userText)[..19]}]: {Trim(userText, request.MaxOutputChars ?? 512)}";
        return new AiChatResult
        {
            Message = new AiMessage { Role = "assistant", Content = content },
            StopReason = "complete"
        };
    }

    private static AiExtractResult RunExtract(AiExtractRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new ArgumentException("ai.extract requires text.");
        }

        var data = new JsonObject
        {
            ["inputHash"] = ProviderHash.Sha256(request.Text),
            ["textLength"] = request.Text.Length,
            ["firstLine"] = Trim(request.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? request.Text, 240)
        };

        if (request.Schema is not null)
        {
            data["schemaKeys"] = new JsonArray(request.Schema.Select(property => JsonValue.Create(property.Key)).ToArray());
        }

        return new AiExtractResult { Data = data };
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

        var queryTerms = Tokenize(request.Query);
        var limit = request.Limit.GetValueOrDefault(request.Candidates.Count);
        if (limit <= 0)
        {
            throw new ArgumentException("ai.rerank limit must be positive.");
        }

        var ranked = request.Candidates
            .Select(candidate =>
            {
                var terms = Tokenize(candidate.Text);
                var overlap = terms.Count == 0 ? 0 : queryTerms.Intersect(terms).Count();
                var score = queryTerms.Count == 0 ? 0 : (double)overlap / queryTerms.Count;
                return new { candidate.Id, Score = score, Tie = ProviderHash.Sha256(candidate.Id + candidate.Text) };
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Tie, StringComparer.Ordinal)
            .Take(limit)
            .Select((item, index) => new AiRerankItem { Id = item.Id, Rank = index + 1, Score = Math.Round(item.Score, 6) })
            .ToList();

        return new AiRerankResult { Items = ranked, ValidationStatus = "deterministic" };
    }

    private static AiReviewResult RunReview(AiReviewRequest request)
    {
        var subject = FirstNonEmpty(request.Subject, request.Prompt, request.Instructions, string.Join(' ', request.References.Select(reference => reference.Id)));
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("ai.review requires prompt, subject, instructions, or references.");
        }

        var maxFindings = request.MaxFindings.GetValueOrDefault(1);
        if (maxFindings <= 0)
        {
            throw new ArgumentException("ai.review maxFindings must be positive.");
        }

        var finding = new AiReviewFinding
        {
            Id = $"finding-{ProviderHash.Sha256(subject)[..12]}",
            Severity = "info",
            Category = "deterministic-review",
            Message = $"Deterministic review marker: caller must validate provider output before adoption. Subject hash {ProviderHash.Sha256(subject)[..12]}.",
            EvidenceRefs = request.References.Select(reference => reference.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Take(8).ToList(),
            Confidence = 0.0
        };

        return new AiReviewResult
        {
            Summary = $"Deterministic review produced {Math.Min(1, maxFindings)} advisory finding.",
            Findings = maxFindings > 0 ? new List<AiReviewFinding> { finding } : new List<AiReviewFinding>(),
            ValidationStatus = "deterministic",
            References = request.References.Take(16).ToList()
        };
    }

    private static AiScaffoldResult RunScaffold(AiScaffoldRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("ai.scaffold requires prompt.");
        }

        var maxArtifacts = request.MaxArtifacts.GetValueOrDefault(1);
        if (maxArtifacts <= 0)
        {
            throw new ArgumentException("ai.scaffold maxArtifacts must be positive.");
        }

        var path = request.AllowedPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? "scaffold/proposal.md";
        var content = $"# Scaffold Proposal\n\n{Trim(request.Prompt, 512)}\n\nValidation remains caller-owned.";
        var artifacts = maxArtifacts > 0
            ? new List<AiScaffoldArtifact>
            {
                new()
                {
                    Path = path,
                    Kind = "file",
                    Action = "propose",
                    Content = content,
                    ContentHash = ProviderHash.Sha256(content),
                    EvidenceRefs = request.References.Select(reference => reference.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Take(8).ToList()
                }
            }
            : new List<AiScaffoldArtifact>();

        return new AiScaffoldResult
        {
            Summary = $"Deterministic scaffold proposed {artifacts.Count} artifact.",
            Artifacts = artifacts,
            ValidationStatus = "deterministic",
            References = request.References.Take(16).ToList()
        };
    }

    private static AiToolPlanResult RunToolPlan(AiToolPlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("ai.toolPlan requires prompt.");
        }

        var calls = new List<AiToolCallProposal>();
        foreach (var tool in request.Tools.Where(tool => !string.IsNullOrWhiteSpace(tool.Name)))
        {
            if (request.Prompt.Contains(tool.Name, StringComparison.OrdinalIgnoreCase))
            {
                calls.Add(new AiToolCallProposal
                {
                    Tool = tool.Name,
                    Arguments = new JsonObject(),
                    RequiresApproval = true,
                    Rationale = "Deterministic match on tool name; caller must validate before execution."
                });
                break;
            }
        }

        return new AiToolPlanResult { Calls = calls, ValidationStatus = "deterministic" };
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

    private static ProviderRunRequest? CreateQualificationRequest(string capability, string mode)
    {
        return capability switch
        {
            ProviderCapabilityIds.AiChat => new ProviderRunRequest
            {
                Capability = ProviderCapabilityIds.AiChat,
                Operation = "run",
                Mode = mode,
                Payload = ProviderJson.ToJsonObject(new AiChatRequest
                {
                    Messages = new List<AiMessage>
                    {
                        new() { Role = "user", Content = "vyral provider qualification smoke" }
                    }
                })
            },
            ProviderCapabilityIds.AiExtract => new ProviderRunRequest
            {
                Capability = ProviderCapabilityIds.AiExtract,
                Operation = "run",
                Mode = mode,
                Payload = ProviderJson.ToJsonObject(new AiExtractRequest
                {
                    Text = "vyral provider qualification smoke"
                })
            },
            ProviderCapabilityIds.AiRerank => new ProviderRunRequest
            {
                Capability = ProviderCapabilityIds.AiRerank,
                Operation = "run",
                Mode = mode,
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
            },
            ProviderCapabilityIds.AiReview => new ProviderRunRequest
            {
                Capability = ProviderCapabilityIds.AiReview,
                Operation = "run",
                Mode = mode,
                Payload = ProviderJson.ToJsonObject(new AiReviewRequest
                {
                    Prompt = "vyral provider qualification smoke review"
                })
            },
            ProviderCapabilityIds.AiScaffold => new ProviderRunRequest
            {
                Capability = ProviderCapabilityIds.AiScaffold,
                Operation = "run",
                Mode = mode,
                Payload = ProviderJson.ToJsonObject(new AiScaffoldRequest
                {
                    Prompt = "vyral provider qualification smoke scaffold",
                    AllowedPaths = new List<string> { "qualification/proposal.md" }
                })
            },
            ProviderCapabilityIds.AiToolPlan => new ProviderRunRequest
            {
                Capability = ProviderCapabilityIds.AiToolPlan,
                Operation = "run",
                Mode = mode,
                Payload = ProviderJson.ToJsonObject(new AiToolPlanRequest
                {
                    Prompt = "plan use of lookup",
                    Tools = new List<AiToolDefinition>
                    {
                        new() { Name = "lookup", Description = "Qualification smoke tool." }
                    }
                })
            },
            _ => null
        };
    }

    private static HashSet<string> Tokenize(string text)
    {
        return text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string Trim(string text, int maxChars)
    {
        return text.Length <= maxChars ? text : text[..maxChars];
    }
}
