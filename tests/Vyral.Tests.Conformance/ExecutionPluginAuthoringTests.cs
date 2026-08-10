using System.Text.Json.Nodes;
using Vyral.Execution;

namespace Vyral.Tests.Conformance;

public sealed class ExecutionPluginAuthoringTests
{
    [Fact]
    public void ExecutionDescriptors_BuildsValidatedPluginAndHandlerDescriptors()
    {
        var descriptor = ExecutionDescriptors.Plugin(
            "sample.plugin",
            "Sample plugin",
            "1.2.3",
            plugin => plugin.AddHandler(
                "sample.count",
                "Count records",
                handler => handler
                    .WithDescription("Counts records without depending on a runtime provider.")
                    .WithMaxAttempts(3)
                    .WithConcurrencyKey("sample.count")
                    .WithTag("category", "test")));

        ExecutionContractValidator.ValidatePluginDescriptor(descriptor);
        Assert.Equal("sample.plugin", descriptor.PluginId);
        var handler = Assert.Single(descriptor.Handlers);
        Assert.Equal("sample.plugin", handler.PluginId);
        Assert.Equal(3, handler.MaxAttempts);
        Assert.Equal("sample.count", handler.ConcurrencyKey);
        Assert.Equal("test", handler.Tags["category"]);
    }

    [Fact]
    public void ExecutionDescriptors_RejectsPluginHandlerMismatch()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExecutionDescriptors.Plugin(
                "sample.plugin",
                "Sample plugin",
                "1.0.0",
                plugin => plugin.AddHandler(ExecutionDescriptors.Handler(
                    "sample.mismatch",
                    "Mismatch",
                    handler => handler.WithPluginId("other.plugin")))));

        Assert.Contains("does not match", error.Message);
    }

    [Fact]
    public async Task DelegateExecutionHandler_ExecutesThroughProviderNeutralHarness()
    {
        var descriptor = ExecutionDescriptors.Handler(
            "sample.delegate",
            "Sample delegate",
            handler => handler.WithPluginId("sample.plugin"));
        var handler = new DelegateExecutionHandler(descriptor, async (context, ct) =>
        {
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 2,
                Attempted = 1,
                Progress = 0.5,
                CurrentStep = "half"
            }, ct);
            await context.RecordEventAsync(
                ExecutionEventTypes.StepStarted,
                "Halfway",
                details: new JsonObject { ["phase"] = "half" },
                ct: ct);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "sample-summary",
                Kind = ExecutionArtifactKinds.Json,
                Content = new JsonObject { ["items"] = 2 }
            }, ct);

            return ExecutionRunResult.Succeeded(new JsonObject { ["ok"] = true });
        });

        var harness = new ExecutionPluginTestHarness();
        var result = await harness.ExecuteAsync(handler, new JsonObject { ["input"] = "value" });

        Assert.Equal(ExecutionRunStatuses.Succeeded, result.Status);
        Assert.True(result.Result!["ok"]!.GetValue<bool>());
        Assert.Single(harness.Reports);
        Assert.Single(harness.Events);
        Assert.Single(harness.Artifacts);
        Assert.Equal("sample-summary", harness.Artifacts[0].Name);
        Assert.Equal(ExecutionEventTypes.StepStarted, harness.Events[0].Type);
    }

    [Fact]
    public async Task ExecutionLoggingExtensions_RecordLayeredLogEvents()
    {
        var handler = new DelegateExecutionHandler(
            ExecutionDescriptors.Handler("sample.logging", "Sample logging"),
            async (context, ct) =>
            {
                await context.LogInfoAsync(
                    "Inspect retrieval batch",
                    layer: "plugin",
                    operation: "batch.inspect",
                    stepId: "inspect",
                    details: new JsonObject { ["items"] = 3 },
                    attributes: new Dictionary<string, string>
                    {
                        ["tenant"] = "demo"
                    },
                    ct: ct);
                return ExecutionRunResult.Succeeded();
            });

        var harness = new ExecutionPluginTestHarness();
        await harness.ExecuteAsync(handler);

        var log = Assert.Single(harness.Events);
        Assert.Equal(ExecutionEventTypes.Log, log.Type);
        Assert.Equal("Inspect retrieval batch", log.Message);
        Assert.Equal("plugin", log.Details!["layer"]!.GetValue<string>());
        Assert.Equal("batch.inspect", log.Details["operation"]!.GetValue<string>());
        Assert.Equal("inspect", log.Details["stepId"]!.GetValue<string>());
        Assert.Equal("demo", log.Details["attributes"]!["tenant"]!.GetValue<string>());
        Assert.Equal(3, log.Details["details"]!["items"]!.GetValue<int>());
    }

    [Fact]
    public async Task StaticExecutionPlugin_CanComposeHandlersWithoutBackendTypes()
    {
        var handler = new DelegateExecutionHandler(
            ExecutionDescriptors.Handler("sample.composed", "Sample composed"),
            (context, ct) => Task.FromResult(ExecutionRunResult.Succeeded(new JsonObject
            {
                ["handlerId"] = context.Run.HandlerId
            })));
        var plugin = new StaticExecutionPlugin(
            ExecutionDescriptors.Plugin("sample.plugin", "Sample plugin", "1.0.0"),
            new[] { handler });

        ExecutionContractValidator.ValidatePluginDescriptor(plugin.Descriptor);
        var descriptor = Assert.Single(plugin.Descriptor.Handlers);
        Assert.Equal("sample.plugin", descriptor.PluginId);

        var harness = new ExecutionPluginTestHarness();
        var result = await harness.ExecuteAsync(plugin.Handlers[0]);
        Assert.Equal("sample.composed", result.Result!["handlerId"]!.GetValue<string>());
    }
}
