using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;

namespace Vyral.Tests.Providers;

public class LocalTokenOverlapRerankerProviderTargetTests
{
    [Fact]
    public async Task LocalRerankerProvider_ExposesRerankOnlyLocalCapability()
    {
        var provider = new LocalTokenOverlapRerankerProviderTarget();

        Assert.Equal(LocalTokenOverlapRerankerProviderTarget.ProviderId, provider.Profile.Id);
        Assert.True(provider.Profile.Local);
        Assert.False(provider.Profile.RequiresNetwork);
        Assert.Contains(ProviderCapabilityIds.AiRerank, provider.Capabilities.Select(c => c.Id));
        Assert.Single(provider.Capabilities);

        var catalog = await provider.ListModelsAsync();

        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, catalog.Status);
        Assert.Equal(LocalTokenOverlapRerankerProviderTarget.ProviderId, catalog.DefaultModelId);
        var model = Assert.Single(catalog.Items);
        Assert.Equal(LocalTokenOverlapRerankerProviderTarget.ProviderId, model.Id);
        Assert.Contains(ProviderCapabilityIds.AiRerank, model.Capabilities);

        var doctor = await provider.DiagnoseAsync();

        Assert.Equal(ProviderDoctorStatuses.Ok, doctor.Status);
        Assert.Contains(doctor.Checks, check => check.Id == "local.availability" && check.Status == ProviderDoctorStatuses.Ok);
    }

    [Fact]
    public async Task LocalRerankerProvider_ReranksByQueryTokenOverlap()
    {
        var provider = new LocalTokenOverlapRerankerProviderTarget();

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiRerank,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiRerankRequest
            {
                Query = "retention policy",
                Candidates = new List<AiRerankCandidate>
                {
                    new() { Id = "travel", Text = "travel reimbursement rules" },
                    new() { Id = "retention", Text = "active retention policy details" }
                },
                Limit = 2
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(LocalTokenOverlapRerankerProviderTarget.ProviderId, result.Provider);
        Assert.Equal(LocalTokenOverlapRerankerProviderTarget.ProviderId, RequireTrace(result).ModelId);
        var items = result.Output["items"]!.AsArray();
        Assert.Equal("retention", items[0]!["id"]!.GetValue<string>());
        Assert.True(items[0]!["score"]!.GetValue<double>() > items[1]!["score"]!.GetValue<double>());
    }

    [Fact]
    public void LocalRerankerProvider_CreatesRerankQualificationSmokeRequest()
    {
        var provider = new LocalTokenOverlapRerankerProviderTarget();

        var requests = provider.CreateQualificationRequests(new ProviderQualificationRequest
        {
            Capability = ProviderCapabilityIds.AiRerank,
            Mode = "mechanics"
        });

        var request = Assert.Single(requests);
        Assert.Equal(ProviderCapabilityIds.AiRerank, request.Capability);
        Assert.Equal("mechanics", request.Mode);
    }

    private static ProviderTraceEvent RequireTrace(ProviderRunResult result) =>
        Assert.IsType<ProviderTraceEvent>(result.Trace);
}
