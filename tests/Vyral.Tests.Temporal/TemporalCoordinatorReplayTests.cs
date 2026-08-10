using System.Security.Cryptography;
using System.Text;
using Temporalio.Common;
using Temporalio.Worker;
using Temporalio.Workflows;
using Vyral.Execution.Temporal;
using Vyral.Execution.Temporal.Hosting;

namespace Vyral.Tests.Temporal;

public sealed class TemporalCoordinatorReplayTests
{
    private const string LegacyCompletionFixture = "vyral-run-coordinator-v1-legacy-completion.json";
    private const string LegacyCompletionFixtureSha256 =
        "8a9456a05edd168a93cd653021434bc67244ec91193539a6607c0e962cf41ef1";

    [Fact]
    public async Task Coordinator_ReplaysHistoryFromBeforeContinueAsNewPatch()
    {
        var history = await LoadLegacyCompletionFixtureAsync();
        var replayer = new WorkflowReplayer(
            new WorkflowReplayerOptions().AddWorkflow<TemporalRunCoordinatorWorkflow>());

        var result = await replayer.ReplayWorkflowAsync(history);

        Assert.Null(result.ReplayFailure);
    }

    [Fact]
    public async Task LegacyHistoryFixture_DetectsAnIncompatibleCommandStream()
    {
        var history = await LoadLegacyCompletionFixtureAsync();
        var replayer = new WorkflowReplayer(
            new WorkflowReplayerOptions().AddWorkflow<IntentionallyIncompatibleCoordinatorWorkflow>());

        var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

        Assert.NotNull(result.ReplayFailure);
    }

    private static async Task<WorkflowHistory> LoadLegacyCompletionFixtureAsync()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", LegacyCompletionFixture);
        Assert.True(File.Exists(fixturePath), "The legacy Temporal coordinator replay fixture is missing.");
        var fixtureBytes = await File.ReadAllBytesAsync(fixturePath);
        Assert.Equal(
            LegacyCompletionFixtureSha256,
            Convert.ToHexStringLower(SHA256.HashData(fixtureBytes)));
        var fixtureJson = Encoding.UTF8.GetString(fixtureBytes);
        Assert.DoesNotContain(TemporalRunCoordinatorWorkflow.ContinueAsNewPatchId, fixtureJson, StringComparison.Ordinal);
        var history = WorkflowHistory.FromJson("vyral-replay-fixture", fixtureJson);
        Assert.Equal(11, history.Events.Count);
        return history;
    }

    [Workflow(TemporalExecutionProtocolNames.CoordinatorWorkflow)]
    private sealed class IntentionallyIncompatibleCoordinatorWorkflow
    {
        [WorkflowRun]
        public async Task<TemporalCoordinatorResult> RunAsync(TemporalCoordinatorInput input)
        {
            await Workflow.DelayAsync(TimeSpan.FromSeconds(1));
            return new TemporalCoordinatorResult
            {
                RunId = input.RunId,
                Generation = input.Generation,
                Status = TemporalAttemptDispositions.Completed,
                CoordinationTransitions = input.CoordinationTransitions
            };
        }
    }
}
