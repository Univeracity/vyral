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

    private static ProviderTraceEvent RequireTrace(ProviderRunResult result) =>
        Assert.IsType<ProviderTraceEvent>(result.Trace);
}
