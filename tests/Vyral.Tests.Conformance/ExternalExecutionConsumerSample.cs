using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Execution.WorkerClient;

namespace Vyral.Tests.Conformance;

public static class ExternalExecutionConsumerSample
{
    public const string PluginId = "sample.external.work";
    public const string HandlerId = "sample.external.work.audit";
    public const string ArtifactName = "external-sample-summary";

    public static IExecutionPlugin CreatePlugin()
    {
        var handlerDescriptor = ExecutionDescriptors.Handler(
            HandlerId,
            "Audit external work items",
            builder => builder
                .WithPluginId(PluginId)
                .WithDescription("Consumer-owned sample handler for runtime portability checks.")
                .WithMaxAttempts(1)
                .WithTag("sample", "external-consumer"));

        var handler = new DelegateExecutionHandler(handlerDescriptor, ExecuteAsync);
        var pluginDescriptor = ExecutionDescriptors.Plugin(
            PluginId,
            "External work sample",
            "1.0.0",
            builder => builder.AddHandler(handlerDescriptor));

        return new StaticExecutionPlugin(pluginDescriptor, new[] { handler });
    }

    public static ExecutionRunRequest CreateRunRequest(string idempotencyKey)
    {
        return new ExecutionRunRequest
        {
            HandlerId = HandlerId,
            PluginId = PluginId,
            IdempotencyKey = idempotencyKey,
            CorrelationId = $"sample-correlation:{idempotencyKey}",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("alpha", "beta", "gamma"),
                ["requestedBy"] = "consumer-sample"
            },
            Tags =
            {
                ["sample"] = "external-consumer"
            },
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = 1
            }
        };
    }

    public static async Task<ExecutionRun> RunAsync(
        IExecutionRuntime runtime,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        runtime.RegisterPlugin(CreatePlugin());

        var first = await runtime.StartRunAsync(CreateRunRequest(idempotencyKey), ct);
        var duplicate = await runtime.StartRunAsync(CreateRunRequest(idempotencyKey), ct);
        if (!string.Equals(first.Id, duplicate.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Runtime did not preserve idempotent replay for the sample request.");
        }

        return await WaitForStatusAsync(runtime, first.Id, ExecutionRunStatuses.Succeeded, ct);
    }

    public static async Task<ExecutionRun> RunExternalAsync(
        IExecutionRuntimeAdapter adapter,
        IExternalExecutionWorkerRuntime externalWorkerRuntime,
        string idempotencyKey,
        string workerId = "portable-consumer-worker",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(externalWorkerRuntime);
        var plugin = CreatePlugin();
        foreach (var handler in plugin.Handlers)
        {
            externalWorkerRuntime.RegisterExternalHandler(handler.Descriptor);
        }

        var first = await adapter.StartRunAsync(CreateRunRequest(idempotencyKey), ct);
        var duplicate = await adapter.StartRunAsync(CreateRunRequest(idempotencyKey), ct);
        if (!string.Equals(first.Id, duplicate.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Runtime did not preserve idempotent replay for the external sample request.");
        }

        var transport = new InProcessExecutionWorkerTransport(
            externalWorkerRuntime,
            workerId,
            plugin.Handlers.Select(handler => handler.Descriptor.HandlerId));
        var host = new ExecutionPluginWorker(
            transport,
            [plugin],
            new ExecutionPluginWorkerOptions { HeartbeatInterval = Timeout.InfiniteTimeSpan });
        var completed = await host.RunOnceAsync(first.Id, ct)
            ?? throw new InvalidOperationException("External sample worker did not claim its requested run.");
        if (completed.Status != ExecutionRunStatuses.Succeeded)
        {
            throw new InvalidOperationException($"External sample run did not succeed. Status: {completed.Status}.");
        }

        return completed;
    }

    public static async Task AssertRunShapeAsync(IExecutionRuntime runtime, ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(run);

        if (run.Result is null)
        {
            throw new InvalidOperationException("Completed sample run did not include a result body.");
        }

        if (run.StatusDetails is null)
        {
            throw new InvalidOperationException("Completed sample run did not include status details.");
        }

        if (run.HandlerId != HandlerId || run.PluginId != PluginId)
        {
            throw new InvalidOperationException("Completed sample run does not preserve handler/plugin identity.");
        }

        if (run.Status != ExecutionRunStatuses.Succeeded ||
            run.Requested != 3 ||
            run.Attempted != 3 ||
            run.Succeeded != 3 ||
            run.Failed != 0 ||
            run.Progress != 1)
        {
            throw new InvalidOperationException("Completed sample run does not expose the expected portable counters.");
        }

        var digest = run.Result["digest"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(digest))
        {
            throw new InvalidOperationException("Completed sample run did not include the expected digest result.");
        }

        var artifacts = await runtime.ListArtifactsAsync(run.Id, ct);
        if (!artifacts.Any(artifact =>
            artifact.Name == ArtifactName &&
            artifact.Kind == ExecutionArtifactKinds.Json &&
            artifact.Content?["digest"]?.GetValue<string>() == digest))
        {
            throw new InvalidOperationException("Completed sample run did not expose the expected artifact.");
        }

        var history = await runtime.GetHistoryAsync(run.Id, ct: ct);
        if (!history.Any(item => item.Type == ExecutionEventTypes.StepStarted) ||
            !history.Any(item => item.Type == ExecutionEventTypes.StepCompleted) ||
            !history.Any(item => item.Type == ExecutionEventTypes.ArtifactWritten))
        {
            throw new InvalidOperationException("Completed sample run did not expose the expected trace history.");
        }
    }

    private static async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct)
    {
        var items = ReadItems(context.Run.Payload);
        var digest = BuildDigest(items);

        await context.RecordEventAsync(
            ExecutionEventTypes.StepStarted,
            "External sample work started.",
            details: new JsonObject
            {
                ["itemCount"] = items.Length
            },
            ct: ct);

        for (var i = 0; i < items.Length; i++)
        {
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = items.Length,
                Attempted = i + 1,
                Succeeded = i + 1,
                Failed = 0,
                Progress = (i + 1) / (double)items.Length,
                CurrentStep = $"item:{items[i]}",
                StatusDetails = new JsonObject
                {
                    ["phase"] = "processing",
                    ["lastItem"] = items[i],
                    ["itemCount"] = items.Length
                }
            }, ct);
        }

        var summary = new JsonObject
        {
            ["itemCount"] = items.Length,
            ["digest"] = digest,
            ["requestedBy"] = context.Run.Payload?["requestedBy"]?.GetValue<string>() ?? "unknown"
        };

        await context.PutArtifactAsync(new ExecutionArtifactWrite
        {
            Name = ArtifactName,
            Kind = ExecutionArtifactKinds.Json,
            MediaType = "application/json",
            Content = summary,
            Metadata =
            {
                ["sample"] = "external-consumer"
            }
        }, ct);

        await context.RecordEventAsync(
            ExecutionEventTypes.StepCompleted,
            "External sample work completed.",
            details: new JsonObject
            {
                ["itemCount"] = items.Length,
                ["digest"] = digest
            },
            ct: ct);

        return ExecutionRunResult.Succeeded(
            new JsonObject
            {
                ["itemCount"] = items.Length,
                ["digest"] = digest
            },
            new JsonObject
            {
                ["phase"] = "completed",
                ["itemCount"] = items.Length
            });
    }

    private static async Task<ExecutionRun> WaitForStatusAsync(
        IExecutionRuntime runtime,
        string runId,
        string status,
        CancellationToken ct)
    {
        ExecutionRun? run = null;
        for (var i = 0; i < 100; i++)
        {
            ct.ThrowIfCancellationRequested();
            run = await runtime.GetRunAsync(runId, ct: ct);
            if (run?.Status == status)
            {
                return run;
            }

            await Task.Delay(25, ct);
        }

        throw new InvalidOperationException($"Run {runId} did not reach {status}. Last status: {run?.Status ?? "(missing)"}");
    }

    private static string[] ReadItems(JsonNode? payload)
    {
        return payload?["items"]?.AsArray()
            .Select(item => item?.GetValue<string>() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray() ?? Array.Empty<string>();
    }

    private static string BuildDigest(IEnumerable<string> items)
    {
        var text = string.Join("\n", items);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
