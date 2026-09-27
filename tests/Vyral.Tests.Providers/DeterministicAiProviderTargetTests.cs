using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;

namespace Vyral.Tests.Providers;

public class DeterministicAiProviderTargetTests
{
    [Fact]
    public async Task DeterministicProvider_ExposesLocalNetworkFreeAiCapabilities()
    {
        var provider = new DeterministicAiProviderTarget();

        Assert.Equal(DeterministicAiProviderTarget.ProviderId, provider.Profile.Id);
        Assert.True(provider.Profile.Local);
        Assert.False(provider.Profile.RequiresNetwork);
        Assert.Contains(ProviderCapabilityIds.AiChat, provider.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiExtract, provider.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiRerank, provider.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiReview, provider.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiScaffold, provider.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiToolPlan, provider.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiJudge, provider.Capabilities.Select(c => c.Id));

        var chatCapability = provider.Capabilities.Single(capability => capability.Id == ProviderCapabilityIds.AiChat);
        Assert.Contains(chatCapability.ModePolicies, policy => policy.Id == "advisory" && !policy.AllowNetwork);
        Assert.Equal("caller-owned", chatCapability.ToolPolicy);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Payload = ProviderJson.ToJsonObject(new AiChatRequest
            {
                Messages = new List<AiMessage>
                {
                    new() { Role = "user", Content = "Summarize the retrieval context." }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Contains("deterministic advisory response", result.Output["message"]?["content"]?.GetValue<string>());
        Assert.Equal(ProviderBoundary.AuthorityBoundary, RequireTrace(result).AuthorityBoundary);
    }

    [Fact]
    public async Task DeterministicProvider_ExposesLocalStaticModelCatalog()
    {
        var provider = new DeterministicAiProviderTarget();

        var catalog = await provider.ListModelsAsync();

        Assert.Equal(DeterministicAiProviderTarget.ProviderId, catalog.Provider);
        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, catalog.Status);
        Assert.Equal("local-static", catalog.Source);
        Assert.Equal(DeterministicAiProviderTarget.ProviderId, catalog.DefaultModelId);
        var model = Assert.Single(catalog.Items);
        Assert.Equal(DeterministicAiProviderTarget.ProviderId, model.Id);
        Assert.True(model.Default);
        Assert.Contains(ProviderCapabilityIds.AiChat, model.Capabilities);
        Assert.False((bool)model.Metadata["semantic"]!);
    }

    [Fact]
    public async Task DeterministicProvider_ReturnsTypedReviewAndScaffoldResults()
    {
        var provider = new DeterministicAiProviderTarget();

        var review = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiReview,
            Payload = ProviderJson.ToJsonObject(new AiReviewRequest
            {
                Prompt = "Review provider adapter changes.",
                References = new List<AiReference>
                {
                    new() { Id = "commit:abc123", Kind = "commit", ContentHash = "sha256:test" }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, review.Status);
        Assert.Equal("deterministic", review.Output["validationStatus"]?.GetValue<string>());
        Assert.NotEmpty(review.Output["findings"]!.AsArray());
        Assert.Equal("commit:abc123", review.Output["references"]!.AsArray()[0]!["id"]!.GetValue<string>());

        var scaffold = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiScaffold,
            Payload = ProviderJson.ToJsonObject(new AiScaffoldRequest
            {
                Prompt = "Propose provider readiness adapter.",
                AllowedPaths = new List<string> { "src/provider-adapter.md" },
                References = new List<AiReference>
                {
                    new() { Id = "trace:123", Kind = "trace" }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, scaffold.Status);
        Assert.Equal("deterministic", scaffold.Output["validationStatus"]?.GetValue<string>());
        var artifact = scaffold.Output["artifacts"]!.AsArray()[0]!;
        Assert.Equal("src/provider-adapter.md", artifact["path"]!.GetValue<string>());
        Assert.Equal("propose", artifact["action"]!.GetValue<string>());
        Assert.NotNull(artifact["contentHash"]);
    }

    [Fact]
    public async Task DeterministicProvider_RejectsUnknownModeWithPolicyFailure()
    {
        var provider = new DeterministicAiProviderTarget();

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Mode = "unknown-mode",
            Payload = ProviderJson.ToJsonObject(new AiChatRequest
            {
                Messages = new List<AiMessage>
                {
                    new() { Role = "user", Content = "hello" }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Policy, result.FailureClass);
        Assert.Equal("unknown_mode", result.ProviderStatus);
    }

    [Fact]
    public async Task DeterministicProvider_EnforcesModeInputLimit()
    {
        var provider = new DeterministicAiProviderTarget(new[]
        {
            new ProviderModePolicy { Id = "tiny", MaxInputBytes = 8, MaxOutputBytes = 64, AllowNetwork = false }
        });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Mode = "tiny",
            Payload = ProviderJson.ToJsonObject(new AiChatRequest
            {
                Messages = new List<AiMessage>
                {
                    new() { Role = "user", Content = "this payload is intentionally too large" }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Policy, result.FailureClass);
        Assert.Equal("input_limit", result.ProviderStatus);
    }

    [Fact]
    public void DeterministicProvider_CreatesQualificationSmokeRequests()
    {
        var provider = new DeterministicAiProviderTarget();

        var requests = provider.CreateQualificationRequests(new ProviderQualificationRequest
        {
            Capability = ProviderCapabilityIds.AiRerank,
            Mode = "mechanics"
        });

        var request = Assert.Single(requests);
        Assert.Equal(ProviderCapabilityIds.AiRerank, request.Capability);
        Assert.Equal("run", request.Operation);
        Assert.Equal("mechanics", request.Mode);
        Assert.True(request.Payload.ContainsKey("query"));
    }

    [Fact]
    public async Task DeterministicProvider_ReranksByLexicalOverlap()
    {
        var provider = new DeterministicAiProviderTarget();

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiRerank,
            Payload = ProviderJson.ToJsonObject(new AiRerankRequest
            {
                Query = "retention policy",
                Candidates = new List<AiRerankCandidate>
                {
                    new() { Id = "travel", Text = "travel reimbursement rules" },
                    new() { Id = "retention", Text = "active retention policy details" }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var items = result.Output["items"]!.AsArray();
        Assert.Equal("retention", items[0]!["id"]!.GetValue<string>());
        Assert.True(items[0]!["score"]!.GetValue<double>() > items[1]!["score"]!.GetValue<double>());
    }

    [Fact]
    public async Task DeterministicProvider_RejectsInvalidPayloadWithSchemaFailure()
    {
        var provider = new DeterministicAiProviderTarget();

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiExtract,
            Payload = new JsonObject()
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Schema, result.FailureClass);
    }

    [Fact]
    public async Task DeterministicProvider_JudgeChoiceReturnsNormalizedProbabilitiesAndStableChoice()
    {
        var provider = new DeterministicAiProviderTarget();
        var request = new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "records subject to a retention hold may be deleted after an authorized release",
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Choice,
                        Prompt = "what topic does this describe",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "retention", Label = "records retention" },
                            new() { Id = "travel", Label = "travel reimbursement" }
                        }
                    }
                }
            })
        };

        var first = await provider.RunAsync(request);
        var second = await provider.RunAsync(request);

        Assert.Equal(ProviderRunStatus.Succeeded, first.Status);
        Assert.Equal("deterministic", first.Output["validationStatus"]?.GetValue<string>());
        var answer = first.Output["answers"]!.AsArray()[0]!;
        Assert.Equal("q1", answer["questionId"]?.GetValue<string>());
        Assert.False(answer["calibrated"]?.GetValue<bool>());
        var probabilities = answer["probabilities"]!.AsObject();
        var sum = probabilities["retention"]!.GetValue<double>() + probabilities["travel"]!.GetValue<double>();
        Assert.InRange(sum, 0.999, 1.001);
        Assert.Equal(answer["choice"]?.GetValue<string>(), second.Output["answers"]!.AsArray()[0]!["choice"]?.GetValue<string>());
    }

    [Fact]
    public async Task DeterministicProvider_JudgeNoulReturnsProbabilityInUnitRange()
    {
        var provider = new DeterministicAiProviderTarget();

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "the invoice total is $412.50",
                Questions = new List<AiJudgeQuestion>
                {
                    new() { Id = "q1", Type = AiJudgeQuestionTypes.Noul, Prompt = "is this invoice over $400" }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var answer = result.Output["answers"]!.AsArray()[0]!;
        var probability = answer["probability"]!.GetValue<double>();
        Assert.InRange(probability, 0.0, 1.0);
        Assert.Null(answer["choice"]);
        Assert.False(answer["calibrated"]?.GetValue<bool>());
    }

    [Fact]
    public async Task DeterministicProvider_JudgeScoreReturnsWeightedPositionAndLegend()
    {
        var provider = new DeterministicAiProviderTarget();
        var request = new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "the incident caused a two-hour outage affecting all customers",
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Score,
                        Prompt = "rate the severity of this incident",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "low", Label = "cosmetic, no functional impact" },
                            new() { Id = "medium", Label = "degraded but workaround exists" },
                            new() { Id = "high", Label = "blocking, no workaround" }
                        }
                    }
                }
            })
        };

        var first = await provider.RunAsync(request);
        var second = await provider.RunAsync(request);

        Assert.Equal(ProviderRunStatus.Succeeded, first.Status);
        var answer = first.Output["answers"]!.AsArray()[0]!;
        Assert.Equal("q1", answer["questionId"]?.GetValue<string>());
        Assert.Null(answer["choice"]);
        var score = answer["score"]!.GetValue<double>();
        Assert.InRange(score, 0.0, 2.0);
        Assert.Equal(score, second.Output["answers"]!.AsArray()[0]!["score"]!.GetValue<double>());

        var probabilities = answer["probabilities"]!.AsObject();
        var sum = probabilities["low"]!.GetValue<double>() + probabilities["medium"]!.GetValue<double>() + probabilities["high"]!.GetValue<double>();
        Assert.InRange(sum, 0.999, 1.001);

        var legend = answer["legend"]!.AsObject();
        Assert.Equal("cosmetic, no functional impact", legend["low"]!.GetValue<string>());
        Assert.Equal("blocking, no workaround", legend["high"]!.GetValue<string>());
        Assert.False(answer["calibrated"]?.GetValue<bool>());
    }

    [Fact]
    public async Task DeterministicProvider_JudgeRejectsScoreQuestionWithFewerThanTwoLevels()
    {
        var provider = new DeterministicAiProviderTarget();

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "context",
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Score,
                        Prompt = "rate it",
                        Options = new List<AiJudgeOption> { new() { Id = "only", Label = "only level" } }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Schema, result.FailureClass);
    }

    [Fact]
    public async Task DeterministicProvider_JudgeRejectsChoiceQuestionWithNoOptions()
    {
        var provider = new DeterministicAiProviderTarget();

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Questions = new List<AiJudgeQuestion>
                {
                    new() { Id = "q1", Type = AiJudgeQuestionTypes.Choice, Prompt = "no options given" }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Schema, result.FailureClass);
    }

    private static ProviderTraceEvent RequireTrace(ProviderRunResult result) =>
        Assert.IsType<ProviderTraceEvent>(result.Trace);
}
