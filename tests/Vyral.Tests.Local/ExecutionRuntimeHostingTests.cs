using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vyral.Execution;
using Vyral.Execution.Local;

namespace Vyral.Tests.Local;

public sealed class ExecutionRuntimeHostingTests
{
    [Fact]
    public async Task LocalRuntimeServiceCollectionExtensions_RegisterNeutralInterfacesAndPlugins()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-execution-hosting-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();

        services.AddVyralExecutionPlugin<HostedSamplePlugin>();
        services.AddVyralLocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = dbPath,
            AdapterId = "local-di-test"
        });

        await using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<LocalExecutionRuntime>();
        var adapter = provider.GetRequiredService<IExecutionRuntimeAdapter>();
        var runtime = provider.GetRequiredService<IExecutionRuntime>();

        Assert.Same(concrete, adapter);
        Assert.Same(concrete, runtime);
        Assert.Contains(runtime.ListHandlers(), handler => handler.HandlerId == HostedSamplePlugin.HandlerId);

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = HostedSamplePlugin.HandlerId,
            Payload = new JsonObject
            {
                ["message"] = "hello"
            }
        });

        var completed = await WaitForRunAsync(runtime, accepted.Id);
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
        Assert.Equal("hello", completed.Result!["message"]!.GetValue<string>());
    }

    private static async Task<ExecutionRun> WaitForRunAsync(IExecutionRuntime runtime, string runId)
    {
        ExecutionRun? run = null;
        for (var i = 0; i < 100; i++)
        {
            run = await runtime.GetRunAsync(runId);
            if (run?.Status is ExecutionRunStatuses.Succeeded or ExecutionRunStatuses.Failed)
            {
                return run;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"Run {runId} did not complete. Last status: {run?.Status ?? "(missing)"}");
    }

    private sealed class HostedSamplePlugin : IExecutionPlugin
    {
        public const string PluginId = "test.hosted.sample";
        public const string HandlerId = "test.hosted.sample.echo";

        private readonly IExecutionHandler[] _handlers = { new HostedSampleHandler() };

        public HostedSamplePlugin()
        {
            Descriptor = ExecutionDescriptors.Plugin(
                PluginId,
                "Hosted sample plugin",
                "1.0.0",
                plugin => plugin.AddHandler(_handlers[0].Descriptor));
        }

        public ExecutionPluginDescriptor Descriptor { get; }
        public IReadOnlyList<IExecutionHandler> Handlers => _handlers;
    }

    private sealed class HostedSampleHandler : IExecutionHandler
    {
        public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
            HostedSamplePlugin.HandlerId,
            "Hosted sample echo",
            handler => handler.WithPluginId(HostedSamplePlugin.PluginId));

        public Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var message = context.Run.Payload?["message"]?.GetValue<string>() ?? string.Empty;
            return Task.FromResult(ExecutionRunResult.Succeeded(new JsonObject
            {
                ["message"] = message
            }));
        }
    }
}
