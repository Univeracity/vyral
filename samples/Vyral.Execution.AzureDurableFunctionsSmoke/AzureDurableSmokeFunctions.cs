using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Vyral.Execution;
using Vyral.Execution.AzureDurable;
using Vyral.Execution.AzureDurable.Functions;

namespace Vyral.Execution.AzureDurableFunctionsSmoke;

/// <summary>
/// Intentionally small, deployable Durable Functions composition. The orchestrator performs only
/// deterministic Durable calls; Cosmos reads/writes and handler execution occur in activities.
/// </summary>
public sealed class AzureDurableSmokeFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AzureDurableExecutionHost _host;

    public AzureDurableSmokeFunctions(AzureDurableExecutionHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    [Function(AzureDurableExecutionNames.Orchestrator)]
    public Task<ExecutionRun> OrchestrateAsync(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var command = context.GetInput<AzureDurableStartCommand>()
            ?? throw new InvalidOperationException("Vyral orchestration command is required.");
        return AzureDurableFunctionsBridge.OrchestrateAsync(_host, context, command);
    }

    [Function(AzureDurableExecutionNames.StartActivity)]
    public Task<AzureDurableRunCreation> StartActivityAsync(
        [ActivityTrigger] AzureDurableStartCommand command) =>
        AzureDurableFunctionsBridge.StartActivityAsync(_host, command);

    [Function(AzureDurableExecutionNames.StepActivity)]
    public Task<AzureDurableOrchestrationStepResult> StepActivityAsync(
        [ActivityTrigger] AzureDurableActivityCommand command) =>
        AzureDurableFunctionsBridge.StepActivityAsync(_host, command);

    [Function("VyralAzureDurableSmokeStart")]
    public async Task<HttpResponseData> StartAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "vyral-smoke/runs")] HttpRequestData request,
        [DurableClient] DurableTaskClient durableClient,
        CancellationToken ct)
    {
        var submitted = await JsonSerializer.DeserializeAsync<SmokeStartRequest>(request.Body, JsonOptions, ct)
            ?? new SmokeStartRequest();
        var runRequest = new ExecutionRunRequest
        {
            HandlerId = AzureDurableSmokeHandler.HandlerId,
            PluginId = AzureDurableSmokeHandler.PluginId,
            IdempotencyKey = submitted.IdempotencyKey,
            CorrelationId = submitted.CorrelationId,
            Payload = submitted.Payload?.DeepClone(),
            ScheduledAtUtc = submitted.ScheduledAtUtc,
            Tags = submitted.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };
        var command = AzureDurableExecutionDialect.BuildStartCommand(runRequest, _host.ListHandlers(), _host.Options);
        var run = await AzureDurableFunctionsBridge.CreateClient(_host, durableClient).StartRunAsync(command, ct);
        return await JsonResponseAsync(request, HttpStatusCode.Accepted, run, ct);
    }

    [Function("VyralAzureDurableSmokeGet")]
    public async Task<HttpResponseData> GetAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "vyral-smoke/runs/{runId}")] HttpRequestData request,
        string runId,
        CancellationToken ct)
    {
        var run = await _host.GetRunAsync(runId, ct: ct);
        return run is null
            ? await JsonResponseAsync(request, HttpStatusCode.NotFound, new { error = "Run not found." }, ct)
            : await JsonResponseAsync(request, HttpStatusCode.OK, run, ct);
    }

    [Function("VyralAzureDurableSmokeCancel")]
    public async Task<HttpResponseData> CancelAsync(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "vyral-smoke/runs/{runId}")] HttpRequestData request,
        string runId,
        [DurableClient] DurableTaskClient durableClient,
        CancellationToken ct)
    {
        var run = await AzureDurableFunctionsBridge.CreateClient(_host, durableClient).CancelRunAsync(runId, ct);
        return run is null
            ? await JsonResponseAsync(request, HttpStatusCode.NotFound, new { error = "Run not found." }, ct)
            : await JsonResponseAsync(request, HttpStatusCode.OK, run, ct);
    }

    [Function("VyralAzureDurableSmokeRaiseEvent")]
    public async Task<HttpResponseData> RaiseEventAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "vyral-smoke/runs/{runId}/events/{name}")] HttpRequestData request,
        string runId,
        string name,
        [DurableClient] DurableTaskClient durableClient,
        CancellationToken ct)
    {
        var payload = await JsonSerializer.DeserializeAsync<JsonNode?>(request.Body, JsonOptions, ct);
        var externalEvent = await AzureDurableFunctionsBridge.CreateClient(_host, durableClient).RaiseEventAsync(
            new ExecutionExternalEventRequest
            {
                RunId = runId,
                Name = name,
                Payload = payload
            },
            ct);
        return await JsonResponseAsync(request, HttpStatusCode.Accepted, externalEvent, ct);
    }

    private static async Task<HttpResponseData> JsonResponseAsync(
        HttpRequestData request,
        HttpStatusCode status,
        object value,
        CancellationToken ct)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await JsonSerializer.SerializeAsync(response.Body, value, value.GetType(), JsonOptions, ct);
        return response;
    }

    private sealed class SmokeStartRequest
    {
        public string? IdempotencyKey { get; set; }
        public string? CorrelationId { get; set; }
        public JsonNode? Payload { get; set; }
        public DateTime? ScheduledAtUtc { get; set; }
        public Dictionary<string, string>? Tags { get; set; }
    }
}

internal static class AzureDurableSmokeHandler
{
    public const string HandlerId = "vyral.azure.smoke";
    public const string PluginId = "vyral.azure.smoke-plugin";

    public static async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct)
    {
        var waitForEvent = ReadString(context.Run.Payload?["waitForEvent"]);
        var waitForTimerAtUtc = ReadUtcTimestamp(context.Run.Payload?["waitForTimerAtUtc"]);
        if (!string.IsNullOrWhiteSpace(waitForEvent) || waitForTimerAtUtc.HasValue)
        {
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Progress = 0.25,
                CurrentStep = "waiting-for-smoke-input",
                StatusDetails = new JsonObject { ["smoke"] = true, ["waiting"] = true }
            }, ct);
            await context.PutCheckpointAsync(new ExecutionCheckpointWrite
            {
                Key = "smoke-wait",
                Content = new JsonObject { ["registered"] = true }
            }, ct);

            var outcome = waitForTimerAtUtc.HasValue
                ? await context.WaitForTimerAsync("smoke-timer", waitForTimerAtUtc.Value, null, ct)
                : await context.WaitForExternalEventAsync(waitForEvent!, ReadUtcTimestamp(context.Run.Payload?["waitTimeoutAtUtc"]), ct);
            await context.RecordEventAsync("smoke.wait.completed", $"Smoke durable wait '{outcome.Name}' completed with '{outcome.Outcome}'.", ct: ct);
        }

        await context.ReportAsync(new ExecutionRunUpdate
        {
            Progress = 0.5,
            CurrentStep = "persisting-smoke-state",
            StatusDetails = new JsonObject { ["smoke"] = true }
        }, ct);
        await context.PutCheckpointAsync(new ExecutionCheckpointWrite
        {
            Key = "smoke",
            Content = new JsonObject { ["completed"] = true }
        }, ct);
        await context.PutArtifactAsync(new ExecutionArtifactWrite
        {
            Name = "smoke-result",
            Kind = ExecutionArtifactKinds.Json,
            Content = new JsonObject { ["ok"] = true }
        }, ct);
        await context.RecordEventAsync("smoke.completed", "Azure Durable smoke handler completed.", ct: ct);
        return ExecutionRunResult.Succeeded(new JsonObject { ["ok"] = true });
    }

    private static string? ReadString(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static DateTime? ReadUtcTimestamp(JsonNode? value)
    {
        var text = ReadString(value);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }
}
