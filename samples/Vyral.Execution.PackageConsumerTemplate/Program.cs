using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Execution.Local;
using Vyral.Primitives;

const string PluginId = "consumer.template";
const string HandlerId = "consumer.template.digest";

var databasePath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(Path.GetTempPath(), $"vyral-template-{Guid.NewGuid():N}.sqlite");

IExecutionRuntimeAdapter runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
{
    AdapterId = "consumer-template-local",
    DatabasePath = databasePath,
    MaxActiveRuns = 4,
    MaxRetainedTerminalRuns = 25
});

var descriptor = ExecutionDescriptors.Handler(
    HandlerId,
    "Digest template input",
    handler => handler
        .WithPluginId(PluginId)
        .WithDescription("Creates a digest, reports progress, and writes a JSON artifact.")
        .WithTag("template", "package-consumer"));

var handler = new DelegateExecutionHandler(descriptor, async (context, ct) =>
{
    var items = context.Run.Payload?["items"]?.AsArray()
        .Select(item => item?.GetValue<string>() ?? string.Empty)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .ToArray() ?? Array.Empty<string>();

    for (var i = 0; i < items.Length; i++)
    {
        await context.ReportAsync(new ExecutionRunUpdate
        {
            Requested = items.Length,
            Attempted = i + 1,
            Succeeded = i + 1,
            Failed = 0,
            Progress = (i + 1) / (double)Math.Max(1, items.Length),
            CurrentStep = $"item:{items[i]}",
            StatusDetails = new JsonObject { ["phase"] = "digesting" }
        }, ct);
    }

    var result = new JsonObject
    {
        ["count"] = items.Length,
        ["digest"] = Digest(items)
    };

    await context.PutArtifactAsync(new ExecutionArtifactWrite
    {
        Name = "digest-summary",
        Kind = ExecutionArtifactKinds.Json,
        MediaType = "application/json",
        Content = result.DeepClone()
    }, ct);

    await context.PutCheckpointAsync(new ExecutionCheckpointWrite
    {
        Key = "digest",
        Content = result.DeepClone()
    }, ct);

    return ExecutionRunResult.Succeeded(result, new JsonObject { ["phase"] = "completed" });
});

runtime.RegisterPlugin(new StaticExecutionPlugin(
    ExecutionDescriptors.Plugin(PluginId, "Package consumer template", "1.0.0", plugin => plugin.AddHandler(descriptor)),
    new[] { handler }));

var status = await runtime.GetAdapterStatusAsync();
Console.WriteLine($"adapter={status.Adapter.RuntimeKind} available={status.Available}");

var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
{
    HandlerId = HandlerId,
    PluginId = PluginId,
    IdempotencyKey = "template:items:v1",
    CorrelationId = OrderedId.CreateString(),
    Payload = new JsonObject { ["items"] = new JsonArray("alpha", "beta", "gamma") },
    RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 1 }
});

var completed = await WaitForRunAsync(runtime, accepted.Id);
var history = await runtime.GetHistoryAsync(completed.Id);
var artifacts = await runtime.ListArtifactsAsync(completed.Id);
var artifact = artifacts.Single(artifact => artifact.Name == "digest-summary");
var artifactByName = await runtime.GetArtifactAsync(completed.Id, artifact.Name);
var artifactById = await runtime.GetArtifactAsync(completed.Id, artifact.Id);
var checkpoint = await runtime.GetCheckpointAsync(completed.Id, "digest");
var maintenance = (IExecutionRuntimeMaintenance)runtime;
var maintenanceStatus = await maintenance.GetMaintenanceStatusAsync();
var prunePreview = await maintenance.PruneAsync(new ExecutionMaintenancePruneRequest
{
    DryRun = true,
    RetainTerminalRuns = 10
});
var reconcilePreview = await maintenance.ReconcileDispatchAsync(new ExecutionMaintenanceDispatchReconcileRequest
{
    DryRun = true,
    Limit = 10
});

Console.WriteLine($"run={completed.Id} status={completed.Status} progress={completed.Progress:0.00}");
Console.WriteLine($"result={completed.Result?.ToJsonString(ExecutionJson.Options)}");
Console.WriteLine($"history={history.Count} artifacts={string.Join(",", artifacts.Select(artifact => artifact.Name))}");
Console.WriteLine($"artifactFetchByName={artifactByName?.Id == artifact.Id} artifactFetchById={artifactById?.Name == artifact.Name}");
Console.WriteLine($"checkpoint={checkpoint?.Key} maintenanceRows={maintenanceStatus.RowCounts["runs"]} pruneDryRun={prunePreview.DryRun} reconcileDryRun={reconcilePreview.DryRun}");
Console.WriteLine($"database={databasePath}");

static async Task<ExecutionRun> WaitForRunAsync(IExecutionRuntime runtime, string runId)
{
    ExecutionRun? run = null;
    for (var attempt = 0; attempt < 100; attempt++)
    {
        run = await runtime.GetRunAsync(runId);
        if (run?.Status == ExecutionRunStatuses.Succeeded)
        {
            return run;
        }

        if (run?.Status is ExecutionRunStatuses.Failed or ExecutionRunStatuses.Cancelled or ExecutionRunStatuses.Rejected)
        {
            throw new InvalidOperationException($"Run ended as {run.Status}: {run.Error}");
        }

        await Task.Delay(25);
    }

    throw new InvalidOperationException($"Run {runId} did not complete. Last status: {run?.Status ?? "(missing)"}");
}

static string Digest(IEnumerable<string> items)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", items)));
    return Convert.ToHexString(hash).ToLowerInvariant()[..16];
}
