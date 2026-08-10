using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Execution.Local;
using Vyral.Execution.WorkerClient;

namespace Vyral.Tests.Conformance;

public sealed class PortableExternalWorkerLifecycleFixtureTests
{
    private const string ScenarioId = "external-workers.handler-lifecycle.v1";
    private const string ManifestResource = "Vyral.Tests.Conformance.runtime-v1-manifest.json";
    private const string ScenarioResource = "Vyral.Tests.Conformance.runtime-v1-external-worker-lifecycle.json";

    [Fact]
    public async Task ExternalWorkerLifecycleMatchesThePortableFixture()
    {
        var manifestBytes = ReadResource(ManifestResource);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var descriptor = manifest.RootElement
            .GetProperty("scenarios")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == ScenarioId);
        Assert.Equal("stateful", descriptor.GetProperty("kind").GetString());
        Assert.Equal(
            "vyral.runtime.external-worker.v1",
            descriptor.GetProperty("profile").GetString());

        var scenarioBytes = ReadResource(ScenarioResource);
        var actualDigest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(scenarioBytes));
        Assert.Equal(descriptor.GetProperty("sha256").GetString(), actualDigest);

        using var scenario = JsonDocument.Parse(scenarioBytes);
        foreach (var step in scenario.RootElement.GetProperty("steps").EnumerateArray())
        {
            var actual = await ExecuteStepAsync(
                step.GetProperty("operation").GetString()!,
                step.GetProperty("arguments"));
            var expected = JsonNode.Parse(
                step.GetProperty("expect").GetProperty("value").GetRawText());
            Assert.True(
                JsonNode.DeepEquals(expected, actual),
                $"External-worker step '{step.GetProperty("id").GetString()}' " +
                $"produced {actual.ToJsonString()}, expected " +
                $"{expected?.ToJsonString() ?? "null"}.");
        }
    }

    private static async Task<JsonObject> ExecuteStepAsync(
        string operation,
        JsonElement arguments)
    {
        var pluginId = arguments.GetProperty("pluginId").GetString()!;
        var handlerId = arguments.GetProperty("handlerId").GetString()!;
        return operation switch
        {
            "external-worker.handler-success" => await RunSuccessAsync(
                pluginId,
                handlerId,
                JsonNode.Parse(arguments.GetProperty("payload").GetRawText())),
            "external-worker.durable-wait-replay" => await RunWaitReplayAsync(
                pluginId,
                handlerId,
                arguments.GetProperty("eventName").GetString()!,
                JsonNode.Parse(arguments.GetProperty("eventPayload").GetRawText())),
            "external-worker.handler-failure" => await RunFailureAsync(
                pluginId,
                handlerId,
                arguments.GetProperty("secretMessage").GetString()!),
            _ => throw new InvalidOperationException(
                $"Unsupported external-worker fixture operation '{operation}'.")
        };
    }

    private static async Task<JsonObject> RunSuccessAsync(
        string pluginId,
        string handlerId,
        JsonNode? payload)
    {
        var plugin = CreatePlugin(
            pluginId,
            handlerId,
            async (context, ct) =>
            {
                await context.PutCheckpointAsync(
                    new ExecutionCheckpointWrite
                    {
                        Key = "state",
                        Content = new JsonObject { ["ready"] = true }
                    },
                    ct);
                await context.ReportAsync(
                    new ExecutionRunUpdate
                    {
                        Progress = 0.5,
                        CurrentStep = "work"
                    },
                    ct);
                await context.RecordEventAsync(
                    ExecutionEventTypes.StepCompleted,
                    "done",
                    ct: ct);
                await context.PutArtifactAsync(
                    new ExecutionArtifactWrite
                    {
                        Name = "summary",
                        Kind = ExecutionArtifactKinds.Json,
                        Content = new JsonObject { ["ok"] = true }
                    },
                    ct);
                return ExecutionRunResult.Succeeded(
                    context.Run.Payload?.DeepClone());
            });
        var (runtime, worker, transport) = CreateRuntime(plugin);
        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = handlerId,
            PluginId = pluginId,
            Payload = payload,
            IdempotencyKey = "portable-success"
        });
        var completed = Assert.IsType<ExecutionRun>(
            await worker.RunOnceAsync(accepted.Id));
        var checkpoint = await runtime.GetCheckpointAsync(completed.Id, "state");
        var artifacts = await runtime.ListArtifactsAsync(completed.Id);
        var history = await runtime.GetHistoryAsync(completed.Id);
        return new JsonObject
        {
            ["status"] = completed.Status,
            ["result"] = completed.Result?.DeepClone(),
            ["progress"] = completed.Progress,
            ["currentStep"] = completed.CurrentStep,
            ["artifactNames"] = Strings(
                artifacts.Select(item => item.Name)),
            ["checkpointKeys"] = Strings(
                checkpoint is null ? [] : [checkpoint.Key]),
            ["eventTypes"] = Strings(
                history
                    .Where(item => item.Type == ExecutionEventTypes.StepCompleted)
                    .Select(item => item.Type)),
            ["completionCount"] = transport.CompletionCount
        };
    }

    private static async Task<JsonObject> RunWaitReplayAsync(
        string pluginId,
        string handlerId,
        string eventName,
        JsonNode? eventPayload)
    {
        var plugin = CreatePlugin(
            pluginId,
            handlerId,
            async (context, ct) =>
            {
                if (await context.GetCheckpointAsync("before-wait", ct) is null)
                {
                    await context.PutCheckpointAsync(
                        new ExecutionCheckpointWrite
                        {
                            Key = "before-wait",
                            Content = new JsonObject { ["ready"] = true }
                        },
                        ct);
                }

                var outcome = await context.WaitForExternalEventAsync(
                    eventName,
                    ct: ct);
                var approved =
                    outcome.Event?.Payload?["approved"]?.GetValue<bool>() == true;
                await context.PutArtifactAsync(
                    new ExecutionArtifactWrite
                    {
                        Name = "approval-summary",
                        Kind = ExecutionArtifactKinds.Json,
                        Content = new JsonObject { ["approved"] = approved }
                    },
                    ct);
                return ExecutionRunResult.Succeeded(
                    new JsonObject { ["approved"] = approved });
            });
        var (runtime, worker, transport) = CreateRuntime(plugin);
        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = handlerId,
            PluginId = pluginId,
            IdempotencyKey = "portable-wait"
        });
        var waiting = Assert.IsType<ExecutionRun>(
            await worker.RunOnceAsync(accepted.Id));
        await runtime.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = eventName,
            Payload = eventPayload
        });
        var completed = Assert.IsType<ExecutionRun>(
            await worker.RunOnceAsync(accepted.Id));
        var checkpoint = await runtime.GetCheckpointAsync(
            completed.Id,
            "before-wait");
        var artifacts = await runtime.ListArtifactsAsync(completed.Id);
        return new JsonObject
        {
            ["waitingStatus"] = waiting.Status,
            ["status"] = completed.Status,
            ["result"] = completed.Result?.DeepClone(),
            ["artifactNames"] = Strings(
                artifacts.Select(item => item.Name)),
            ["checkpointKeys"] = Strings(
                checkpoint is null ? [] : [checkpoint.Key]),
            ["completionCount"] = transport.CompletionCount
        };
    }

    private static async Task<JsonObject> RunFailureAsync(
        string pluginId,
        string handlerId,
        string secretMessage)
    {
        var plugin = CreatePlugin(
            pluginId,
            handlerId,
            (_, _) => throw new InvalidOperationException(secretMessage));
        var (runtime, worker, transport) = CreateRuntime(plugin);
        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = handlerId,
            PluginId = pluginId,
            IdempotencyKey = "portable-failure"
        });
        var completed = Assert.IsType<ExecutionRun>(
            await worker.RunOnceAsync(accepted.Id));
        return new JsonObject
        {
            ["status"] = completed.Status,
            ["failureClass"] = completed.FailureClass,
            ["error"] = completed.Error,
            ["completionCount"] = transport.CompletionCount
        };
    }

    private static (
        LocalExecutionRuntime Runtime,
        ExecutionPluginWorker Worker,
        CountingTransport Transport)
        CreateRuntime(IExecutionPlugin plugin)
    {
        var handler = Assert.Single(plugin.Handlers);
        var runtime = new LocalExecutionRuntime(
            new LocalExecutionRuntimeOptions
            {
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    $"vyral-portable-worker-{Guid.NewGuid():N}.sqlite")
            });
        runtime.RegisterExternalHandler(handler.Descriptor);
        var inner = new InProcessExecutionWorkerTransport(
            runtime,
            "portable-worker",
            [handler.Descriptor.HandlerId]);
        var transport = new CountingTransport(inner);
        var worker = new ExecutionPluginWorker(
            transport,
            [plugin],
            new ExecutionPluginWorkerOptions
            {
                HeartbeatInterval = Timeout.InfiniteTimeSpan
            });
        return (runtime, worker, transport);
    }

    private static IExecutionPlugin CreatePlugin(
        string pluginId,
        string handlerId,
        Func<IExecutionRunContext, CancellationToken, Task<ExecutionRunResult>>
            execute)
    {
        var descriptor = ExecutionDescriptors.Handler(
            handlerId,
            handlerId,
            builder => builder.WithPluginId(pluginId));
        return new StaticExecutionPlugin(
            ExecutionDescriptors.Plugin(
                pluginId,
                pluginId,
                "1.0.0",
                builder => builder.AddHandler(descriptor)),
            [new DelegateExecutionHandler(descriptor, execute)]);
    }

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => JsonValue.Create(value))
            .ToArray());

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PortableExternalWorkerLifecycleFixtureTests)
            .Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed class CountingTransport(
        IExecutionWorkerTransport inner) : IExecutionWorkerTransport
    {
        public int CompletionCount { get; private set; }

        public Task<ExecutionExternalWorkerLease?> LeaseNextAsync(
            string? runId = null,
            double ttlSeconds = 60,
            CancellationToken ct = default) =>
            inner.LeaseNextAsync(runId, ttlSeconds, ct);

        public Task<ExecutionExternalWorkerLease> HeartbeatAsync(
            ExecutionExternalWorkerLease lease,
            double ttlSeconds = 60,
            CancellationToken ct = default) =>
            inner.HeartbeatAsync(lease, ttlSeconds, ct);

        public Task<ExecutionCheckpoint> CheckpointAsync(
            ExecutionExternalWorkerLease lease,
            ExecutionCheckpointWrite checkpoint,
            CancellationToken ct = default) =>
            inner.CheckpointAsync(lease, checkpoint, ct);

        public Task<ExecutionCheckpoint?> GetCheckpointAsync(
            ExecutionExternalWorkerLease lease,
            string key,
            CancellationToken ct = default) =>
            inner.GetCheckpointAsync(lease, key, ct);

        public Task<ExecutionRun> ReportAsync(
            ExecutionExternalWorkerLease lease,
            ExecutionRunUpdate update,
            CancellationToken ct = default) =>
            inner.ReportAsync(lease, update, ct);

        public Task RecordEventAsync(
            ExecutionExternalWorkerLease lease,
            ExecutionExternalWorkerEventRequest request,
            CancellationToken ct = default) =>
            inner.RecordEventAsync(lease, request, ct);

        public Task<ExecutionArtifact> PutArtifactAsync(
            ExecutionExternalWorkerLease lease,
            ExecutionArtifactWrite artifact,
            CancellationToken ct = default) =>
            inner.PutArtifactAsync(lease, artifact, ct);

        public Task<ExecutionExternalWorkerWaitResponse> WaitAsync(
            ExecutionExternalWorkerLease lease,
            ExecutionExternalWorkerWaitRequest request,
            CancellationToken ct = default) =>
            inner.WaitAsync(lease, request, ct);

        public Task<ExecutionRun> CompleteAsync(
            ExecutionExternalWorkerLease lease,
            ExecutionRunResult result,
            CancellationToken ct = default)
        {
            CompletionCount++;
            return inner.CompleteAsync(lease, result, ct);
        }
    }
}
