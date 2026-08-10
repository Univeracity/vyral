using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Execution.Local;

var runOnce = args.Contains("--once", StringComparer.Ordinal);
var databasePath = ResolveDatabasePath(args);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

IExecutionRuntimeAdapter runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
{
    AdapterId = "local-sample",
    DatabasePath = databasePath,
    MaxActiveRuns = 8,
    MaxRetainedTerminalRuns = 20,
    DefaultListLimit = 20,
    MaxListLimit = 100
});

runtime.RegisterPlugin(new SampleWorkPlugin());

var status = await runtime.GetAdapterStatusAsync();
Console.WriteLine($"adapter={status.Adapter.AdapterId} kind={status.Adapter.RuntimeKind} available={status.Available}");

var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
{
    HandlerId = SampleWorkPlugin.HandlerId,
    PluginId = SampleWorkPlugin.PluginId,
    IdempotencyKey = "local-sample:alpha",
    CorrelationId = "local-sample:demo",
    Payload = new JsonObject
    {
        ["items"] = new JsonArray("alpha", "beta", "gamma")
    },
    RetryPolicy = new ExecutionRetryPolicy
    {
        MaxAttempts = 1
    },
    Tags =
    {
        ["sample"] = "local"
    }
});

var duplicate = await runtime.StartRunAsync(new ExecutionRunRequest
{
    HandlerId = SampleWorkPlugin.HandlerId,
    PluginId = SampleWorkPlugin.PluginId,
    IdempotencyKey = "local-sample:alpha",
    CorrelationId = "local-sample:demo",
    Payload = new JsonObject
    {
        ["items"] = new JsonArray("alpha", "beta", "gamma")
    },
    RetryPolicy = new ExecutionRetryPolicy
    {
        MaxAttempts = 1
    },
    Tags =
    {
        ["sample"] = "local"
    }
});

Console.WriteLine($"run={accepted.Id} duplicateReplay={accepted.Id == duplicate.Id}");

var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
var history = await runtime.GetHistoryAsync(completed.Id);
var artifacts = await runtime.ListArtifactsAsync(completed.Id);
var sawTimer = history.Any(item => item.Type == ExecutionEventTypes.TimerScheduled);
var sawExternalEvent = history.Any(item => item.Type == ExecutionEventTypes.ExternalEventRaised);

Console.WriteLine($"status={completed.Status} attempted={completed.Attempted} progress={completed.Progress:0.00}");
Console.WriteLine($"result={completed.Result?.ToJsonString(ExecutionJson.Options)}");
Console.WriteLine($"history={history.Count} timer={sawTimer} externalEvent={sawExternalEvent} artifacts={string.Join(",", artifacts.Select(artifact => artifact.Name))}");
Console.WriteLine($"database={databasePath}");

if (!runOnce)
{
    Console.WriteLine("Run with --once for validation-friendly output.");
}

static string ResolveDatabasePath(string[] args)
{
    var explicitPath = args
        .Select((value, index) => new { value, index })
        .FirstOrDefault(item => item.value == "--db")?.index;
    if (explicitPath.HasValue && explicitPath.Value + 1 < args.Length)
    {
        return Path.GetFullPath(args[explicitPath.Value + 1]);
    }

    return Path.Combine(Path.GetTempPath(), $"vyral-execution-local-sample-{Guid.NewGuid():N}.sqlite");
}

static async Task<ExecutionRun> WaitForRunAsync(IExecutionRuntime runtime, string runId, string expectedStatus)
{
    ExecutionRun? run = null;
    for (var i = 0; i < 100; i++)
    {
        run = await runtime.GetRunAsync(runId);
        if (run?.Status == expectedStatus)
        {
            return run;
        }

        await Task.Delay(25);
    }

    throw new InvalidOperationException($"Run {runId} did not reach {expectedStatus}. Last status: {run?.Status ?? "(missing)"}");
}

sealed class SampleWorkPlugin : IExecutionPlugin
{
    public const string PluginId = "sample.local.work";
    public const string HandlerId = "sample.local.work.digest";

    private readonly IExecutionHandler[] _handlers = { new DigestHandler() };

    public SampleWorkPlugin()
    {
        Descriptor = ExecutionDescriptors.Plugin(
            PluginId,
            "Local sample work",
            "1.0.0",
            plugin => plugin.AddHandler(_handlers[0].Descriptor));
    }

    public ExecutionPluginDescriptor Descriptor { get; }
    public IReadOnlyList<IExecutionHandler> Handlers => _handlers;

    private sealed class DigestHandler : IExecutionHandler
    {
        public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
            HandlerId,
            "Digest sample items",
            handler => handler
                .WithPluginId(PluginId)
                .WithDescription("Creates a digest and writes a summary artifact.")
                .WithTag("sample", "local"));

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var items = context.Run.Payload?["items"]?.AsArray()
                .Select(item => item?.GetValue<string>() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray() ?? Array.Empty<string>();
            var digest = BuildDigest(items);
            var timer = await context.ScheduleTimerAsync(
                "local-sample.digest.followup",
                DateTime.UtcNow.AddMinutes(5),
                new JsonObject
                {
                    ["digest"] = digest
                },
                ct);
            var readyEvent = await context.RaiseEventAsync(
                "local-sample.digest.ready",
                new JsonObject
                {
                    ["digest"] = digest,
                    ["timerId"] = timer.Id
                },
                ct);

            for (var i = 0; i < items.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                await context.ReportAsync(new ExecutionRunUpdate
                {
                    Requested = items.Length,
                    Attempted = i + 1,
                    Succeeded = i + 1,
                    Failed = 0,
                    Progress = (i + 1) / (double)Math.Max(1, items.Length),
                    CurrentStep = $"item:{items[i]}",
                    StatusDetails = new JsonObject
                    {
                        ["phase"] = "processing",
                        ["lastItem"] = items[i]
                    }
                }, ct);
            }

            var result = new JsonObject
            {
                ["itemCount"] = items.Length,
                ["digest"] = digest,
                ["timerId"] = timer.Id,
                ["eventId"] = readyEvent.Id
            };

            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "local-sample-summary",
                Kind = ExecutionArtifactKinds.Json,
                MediaType = "application/json",
                Content = result.DeepClone(),
                Metadata =
                {
                    ["sample"] = "local"
                }
            }, ct);

            return ExecutionRunResult.Succeeded(
                result,
                new JsonObject
                {
                    ["phase"] = "completed",
                    ["itemCount"] = items.Length
                });
        }

        private static string BuildDigest(IEnumerable<string> items)
        {
            var text = string.Join("\n", items);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash).ToLowerInvariant()[..16];
        }
    }
}
