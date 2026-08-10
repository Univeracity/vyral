namespace Vyral.Tests.Conformance;

public sealed class ExternalExecutionConsumerSampleTests
{
    [Fact]
    public async Task ExternalExecutionConsumerSample_RunsThroughContractOnlyHarness()
    {
        var plugin = ExternalExecutionConsumerSample.CreatePlugin();
        var harness = new ExecutionPluginTestHarness();

        var handler = Assert.Single(plugin.Handlers);
        var result = await harness.ExecuteAsync(
            handler,
            ExternalExecutionConsumerSample.CreateRunRequest("sample:harness").Payload);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(3, result.Result!["itemCount"]!.GetValue<int>());
        Assert.NotNull(result.Result["digest"]);
        Assert.Equal(3, harness.Reports.Count);
        Assert.Contains(harness.Events, item => item.Type == "step.started");
        Assert.Contains(harness.Events, item => item.Type == "step.completed");
        Assert.Single(harness.Artifacts, artifact => artifact.Name == ExternalExecutionConsumerSample.ArtifactName);
    }
}
