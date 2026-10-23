using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Vyral.Execution;
using Vyral.Execution.Local;
using Vyral.Local;
using Vyral.Providers.Abstractions;
using Vyral.Server;

namespace Vyral.Tests.Local;

// Seeded crash windows exercise the real SQLite restart dispatcher and provider plugin.
// Actual process death and native containment need the separate consumer qualification.
public class ProviderInvocationRecoveryTests
{
    [Theory]
    [InlineData("intent", false)]
    [InlineData("expired", false)]
    [InlineData("missing", false)]
    [InlineData("malformed", false)]
    [InlineData("binding", false)]
    [InlineData("corrupt-result", false)]
    [InlineData("wrong-provider", false)]
    [InlineData("completed", true)]
    [InlineData("completed-expired", true)]
    public async Task SQLiteRestartReconcilesOriginalInvocationWithoutCallingProvider(string scenario, bool accepted)
    {
        var root = Path.Combine(Path.GetTempPath(), "vyral-reconciliation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.sqlite");
        try
        {
            var options = new LocalExecutionRuntimeOptions { DatabasePath = path, ArtifactDirectory = Path.Combine(root, "artifacts") };
            var initial = new LocalExecutionRuntime(options);
            initial.RegisterExternalHandler(new ExecutionHandlerDescriptor
            { HandlerId = ExecutionRuntimeProviderRunJobAdapter.HandlerId, PluginId = ExecutionRuntimeProviderRunJobAdapter.PluginId,
                DisplayName = "Retained provider fixture" });
            var target = new NeverCalledTarget();
            var request = new ProviderRunRequest { Provider = target.Profile.Id, Capability = ProviderCapabilityIds.AiChat,
                Operation = "run", Mode = ProviderModes.Advisory, CorrelationId = "original-correlation", TimeoutSeconds = 20 };
            var run = await initial.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = ExecutionRuntimeProviderRunJobAdapter.HandlerId,
                PluginId = ExecutionRuntimeProviderRunJobAdapter.PluginId, IdempotencyKey = "original-key",
                Payload = new JsonObject { ["provider"] = target.Profile.Id,
                    ["request"] = JsonSerializer.SerializeToNode(request, ExecutionJson.Options) }
            });
            Assert.Equal(ExecutionRunStatuses.Queued, run.Status);
            run.Status = ExecutionRunStatuses.Running; run.Attempt = 1;
            var originalStart = DateTimeOffset.UtcNow.AddSeconds(-30);
            var expiry = scenario.Contains("expired", StringComparison.Ordinal) ? originalStart.AddSeconds(5) : originalStart.AddSeconds(120);
            var saved = JsonSerializer.SerializeToNode(new ProviderRunResult { Provider = target.Profile.Id,
                Capability = request.Capability, Operation = request.Operation, Mode = request.Mode,
                Status = ProviderRunStatus.Succeeded, ProviderStatus = "original_saved_response",
                Output = new JsonObject { ["text"] = "PRIVATE-ORIGINAL-RESULT" } }, ExecutionJson.Options)!;
            var completed = scenario.StartsWith("completed", StringComparison.Ordinal) || scenario is "corrupt-result" or "wrong-provider";
            var journal = new JsonObject
            {
                ["schemaVersion"] = "vyral.provider-invocation.v1", ["jobId"] = run.Id, ["originalAttempt"] = 1,
                ["payloadHash"] = scenario == "binding" ? "wrong-binding" : run.PayloadHash,
                ["provider"] = target.Profile.Id, ["configHash"] = target.Profile.ConfigHash,
                ["startedAtUtc"] = originalStart, ["deadlineUtc"] = expiry,
                ["state"] = completed ? "completed" : "intent",
                ["resultHash"] = completed ? ProviderHash.Sha256(saved.ToJsonString(ExecutionJson.Options)) : null
            };
            if (completed) run.Result = saved.DeepClone();
            if (scenario == "corrupt-result") run.Result!["output"]!["text"] = "CHANGED";
            if (scenario == "wrong-provider")
            {
                run.Result!["provider"] = "wrong-provider";
                journal["resultHash"] = ProviderHash.Sha256(run.Result.ToJsonString(ExecutionJson.Options));
            }
            run.StatusDetails = new JsonObject { ["provider"] = target.Profile.Id, ["resultStatus"] = "Succeeded" };
            if (scenario != "missing") run.StatusDetails["providerInvocation"] = scenario == "malformed" ? JsonValue.Create("invalid") : journal;
            var retainedDetails = run.StatusDetails.DeepClone();
            await WriteInterruptedAsync(path, run);

            var recovered = new LocalExecutionRuntime(options);
            var traces = new SqliteTraceStore(path);
            await traces.InitializeAsync();
            var adapter = new ExecutionRuntimeProviderRunJobAdapter(recovered, new ProviderTargetRegistry(new[] { target }),
                traces, new ProviderRunGuard(new ProviderRunGuardOptions()));
            await recovered.DispatchReadyRunsAsync(recoverInterruptedRuns: true);
            ExecutionRun? final = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                final = await recovered.GetRunAsync(run.Id);
                if (final is not null && ExecutionRunStatuses.IsTerminal(final.Status)) break;
                await Task.Delay(20);
            }
            Assert.NotNull(final); Assert.Equal(accepted ? ExecutionRunStatuses.Succeeded : ExecutionRunStatuses.Failed, final!.Status);
            Assert.Equal(0, target.Calls); Assert.Equal(2, final.Attempt); Assert.Equal(run.Id, final.Id);
            Assert.Equal(run.PayloadHash, final.PayloadHash);
            Assert.True(JsonNode.DeepEquals(retainedDetails["providerInvocation"], final.StatusDetails!["providerInvocation"]));
            if (accepted) Assert.Equal(saved.ToJsonString(), final.Result!.ToJsonString());
            else
            {
                var held = final.Result!.Deserialize<ProviderRunResult>(ExecutionJson.Options)!;
                Assert.Equal("provider_effect_unresolved", held.ProviderStatus);
                Assert.True(held.Rejection!.OperatorReviewRecommended); Assert.False(held.Rejection.Retryable);
                Assert.Empty(held.Metering); Assert.Empty(held.MeteringMeasurements);
            }
            var replay = await adapter.StartAsync(target.Profile.Id, request, idempotencyKey: "original-key");
            Assert.Equal(run.Id, replay.Id); Assert.Equal(0, target.Calls);
            Assert.Equal(2, (await recovered.GetRunAsync(run.Id))!.Attempt);
            var progress = (await adapter.GetProgressAsync(run.Id))!;
            Assert.Equal("terminal", progress["phase"]!.GetValue<string>());
            Assert.Equal(!accepted, progress["reconciliationRequired"]!.GetValue<bool>());
            Assert.DoesNotContain("PRIVATE-ORIGINAL-RESULT", progress.ToJsonString());
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task WriteInterruptedAsync(string path, ExecutionRun run)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE vyral_execution_runs SET status=$status,run_json=$json WHERE id=$id;";
        command.Parameters.AddWithValue("$status", run.Status); command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(run, ExecutionJson.Options));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private sealed class NeverCalledTarget : IProviderTarget
    {
        public int Calls;
        public ProviderProfile Profile { get; } = new() { Id = "fixture-recovery", ConfigHash = "sha256:" + new string('a', 64) };
        public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; } = Array.Empty<ProviderCapabilityDescriptor>();
        public Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Recovered provider must not be invoked."); }
    }
}
