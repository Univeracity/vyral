using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;
using Vyral.Azure;
using Vyral.Execution;
using Vyral.Execution.AzureDurable;

namespace Vyral.Tests.Azure;

/// <summary>
/// Read-only verification for the deployable Functions smoke host. The caller supplies the
/// disposable status container and run id after invoking the HTTP start endpoint.
/// </summary>
public sealed class AzureDurableFunctionsSmokeLiveTests
{
    [AzureDurableFunctionsSmokeLiveFact]
    public async Task DurableFunctionsSmokeRun_PersistsRunOwnedState()
    {
        var settings = AzureLiveSettings.Cosmos();
        var containerId = Required("VYRAL_AZURE_DURABLE_STATUS_CONTAINER");
        var runId = Required("VYRAL_AZURE_DURABLE_SMOKE_RUN_ID");
        using var client = new CosmosClient(settings.ConnectionString);
        var store = new AzureCosmosExecutionStatusStore(
            client,
            settings.DatabaseId,
            containerId,
            new AzureDurableExecutionOptions { StatusStoreName = containerId });

        var run = await store.GetRunAsync(runId);
        Assert.NotNull(run);
        Assert.Equal(ExecutionRunStatuses.Succeeded, run!.Status);
        Assert.True(run.Result!["ok"]!.GetValue<bool>());
        Assert.True(run.StatusDetails!["smoke"]!.GetValue<bool>());

        var checkpoint = await store.GetCheckpointAsync(runId, "smoke");
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.Content!["completed"]!.GetValue<bool>());

        var artifact = await store.GetArtifactAsync(runId, "smoke-result");
        Assert.NotNull(artifact);
        Assert.True(artifact!.Content!["ok"]!.GetValue<bool>());

        var history = await store.GetHistoryAsync(runId);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunCreated);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunStarted);
        Assert.Contains(history, item => item.Type == "smoke.completed");
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunCompleted);
    }

    [AzureDurableFunctionsSmokeWaitLiveFact]
    public async Task DurableFunctionsSmokeWaitRun_ReplaysHandlerAfterExternalEvent()
    {
        var settings = AzureLiveSettings.Cosmos();
        var containerId = Required("VYRAL_AZURE_DURABLE_STATUS_CONTAINER");
        var runId = Required("VYRAL_AZURE_DURABLE_SMOKE_WAIT_RUN_ID");
        using var client = new CosmosClient(settings.ConnectionString);
        var store = new AzureCosmosExecutionStatusStore(
            client,
            settings.DatabaseId,
            containerId,
            new AzureDurableExecutionOptions { StatusStoreName = containerId });

        var run = await store.GetRunAsync(runId);
        Assert.NotNull(run);
        Assert.Equal(ExecutionRunStatuses.Succeeded, run!.Status);
        Assert.True(run.Result!["ok"]!.GetValue<bool>());

        var checkpoint = await store.GetCheckpointAsync(runId, "smoke-wait");
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.Content!["registered"]!.GetValue<bool>());

        var history = await store.GetHistoryAsync(runId);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitRegistered);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitResumed);
        Assert.Contains(history, item => item.Type == "smoke.wait.completed");
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunCompleted);
    }

    [AzureDurableFunctionsSmokeTimerLiveFact]
    public async Task DurableFunctionsSmokeTimerRun_ReplaysHandlerAfterTimer()
    {
        var settings = AzureLiveSettings.Cosmos();
        var containerId = Required("VYRAL_AZURE_DURABLE_STATUS_CONTAINER");
        var runId = Required("VYRAL_AZURE_DURABLE_SMOKE_TIMER_RUN_ID");
        using var client = new CosmosClient(settings.ConnectionString);
        var store = new AzureCosmosExecutionStatusStore(
            client,
            settings.DatabaseId,
            containerId,
            new AzureDurableExecutionOptions { StatusStoreName = containerId });

        var run = await store.GetRunAsync(runId);
        Assert.NotNull(run);
        Assert.Equal(ExecutionRunStatuses.Succeeded, run!.Status);
        Assert.True(run.Result!["ok"]!.GetValue<bool>());

        var checkpoint = await store.GetCheckpointAsync(runId, "smoke-wait");
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.Content!["registered"]!.GetValue<bool>());

        var history = await store.GetHistoryAsync(runId);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitRegistered &&
            item.Message!.Contains("timer 'smoke-timer'", StringComparison.Ordinal));
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitResumed &&
            item.Message!.Contains("'smoke-timer'", StringComparison.Ordinal));
        Assert.Contains(history, item => item.Type == "smoke.wait.completed");
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunCompleted);
    }

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is required.")
            : value.Trim();
    }
}
