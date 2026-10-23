using System.Reflection;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Providers.Abstractions;
using Vyral.Server;

namespace Vyral.Tests.Local;

public class ProviderJobProgressStatusTests
{
    [Theory]
    [InlineData("fresh", "prompt_prepared", true, true)]
    [InlineData("stale", "unknown", false, true)]
    [InlineData("expired", "unknown", false, true)]
    [InlineData("attempt", "unknown", false, false)]
    [InlineData("binding", "unknown", false, false)]
    [InlineData("payload", "unknown", false, false)]
    [InlineData("identity", "unknown", false, false)]
    [InlineData("shape", "unknown", false, false)]
    [InlineData("phase", "unknown", false, false)]
    [InlineData("fit", "unknown", false, false)]
    [InlineData("failed", "terminal", false, true)]
    [InlineData("cancelled", "terminal", false, true)]
    public void ProgressProjectionRejectsDriftAndStalenessWithoutPromotingSuccess(string scenario,
        string expectedPhase, bool current, bool retained)
    {
        var now = DateTimeOffset.UtcNow;
        var observation = new JsonObject
        {
            ["phase"] = scenario == "phase" ? 999 : (int)ProviderProgressPhase.PromptPrepared,
            ["sequence"] = 3, ["observedAtUtc"] = now, ["startedAtUtc"] = now.AddSeconds(-1),
            ["deadlineUtc"] = now.AddSeconds(120), ["runtimeContextTokens"] = 8,
            ["promptTokens"] = 3, ["reservedOutputTokens"] = 2, ["promptFitsContext"] = scenario != "fit",
            ["private"] = "PRIVATE-SENTINEL"
        };
        var snapshot = new JsonObject
        {
            ["jobId"] = scenario == "identity" ? "other-job" : "original-job",
            ["attempt"] = scenario == "attempt" ? 2 : 1,
            ["payloadHash"] = scenario == "payload" ? "other-payload" : "bound-payload",
            ["providerConfigHash"] = scenario == "binding" ? "other-config" : "bound-config",
            ["observation"] = scenario == "shape" ? null : observation
        };
        var lifecycle = scenario is "failed" or "cancelled" ? scenario : "running";
        var run = new ExecutionRun { Id = "original-job", Attempt = 1, PayloadHash = "bound-payload", Status = lifecycle,
            StatusDetails = new JsonObject { ["providerProgress"] = snapshot, ["resultStatus"] = "Succeeded" } };
        var map = typeof(ExecutionRuntimeProviderRunJobAdapter).GetMethod("MapRun", BindingFlags.NonPublic | BindingFlags.Static)!;
        var job = (ProviderRunJob)map.Invoke(null, new object[] { run, false })!;
        var projection = typeof(ExecutionRuntimeProviderRunJobAdapter).Assembly.GetType("Vyral.Server.ProviderProgressSnapshot")!
            .GetMethod("Project", BindingFlags.Public | BindingFlags.Static)!;
        var at = scenario == "stale" ? now.AddSeconds(31) : scenario == "expired" ? now.AddSeconds(121) : now;
        var value = (JsonObject)projection.Invoke(null, new object[] { run, job, "bound-config", at })!;
        Assert.Equal(expectedPhase, value["phase"]!.GetValue<string>());
        Assert.Equal(current, value["nativePhaseAvailable"]!.GetValue<bool>());
        Assert.Equal(retained, value["observation"] is not null);
        Assert.DoesNotContain("PRIVATE-SENTINEL", value.ToJsonString());
        Assert.DoesNotContain("bound-config", value.ToJsonString());
        Assert.NotEqual("Succeeded", value["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("failed", "Succeeded", ProviderJobStatus.Failed)]
    [InlineData("running", "Succeeded", ProviderJobStatus.Running)]
    [InlineData("cancelled", "Unsupported", ProviderJobStatus.Cancelled)]
    [InlineData("timed_out", "Succeeded", ProviderJobStatus.TimedOut)]
    [InlineData("failed", "Unsupported", ProviderJobStatus.Unsupported)]
    [InlineData("failed", "NotConfigured", ProviderJobStatus.NotConfigured)]
    [InlineData("failed", "0", ProviderJobStatus.Failed)]
    public void LifecycleCannotBePromotedByPersistedResultObservation(string lifecycle, string observation, ProviderJobStatus expected)
    {
        var run = new ExecutionRun
        {
            Id = "original-job", Status = lifecycle,
            StatusDetails = new JsonObject { ["resultStatus"] = observation }
        };
        var map = typeof(ExecutionRuntimeProviderRunJobAdapter).GetMethod("MapRun", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var includeResult in new[] { false, true })
        {
            run.Result = includeResult ? System.Text.Json.JsonSerializer.SerializeToNode(
                new ProviderRunResult { Status = Enum.Parse<ProviderRunStatus>(observation) }, ExecutionJson.Options) : null;
            var job = (ProviderRunJob)map.Invoke(null, new object[] { run, false })!;
            Assert.Equal(expected, job.Status);
            Assert.Equal(includeResult, job.Result is not null);
        }
    }
}
