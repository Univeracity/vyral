using Vyral.Execution;
using Vyral.Execution.Local;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Local;

public sealed class LocalExecutionRuntimeAdapterConformanceTests : Vyral.Tests.Conformance.ExecutionRuntimeConformanceTests
{
    [Fact]
    public async Task LocalExecutionRuntime_MatchesPublishedQualificationProfile()
    {
        var runtime = await CreateRuntimeAsync();
        ExecutionAdapterQualificationAssertions.AssertMatchesPublishedProfile(runtime.Adapter);
    }

    protected override Task<IExecutionRuntimeAdapter> CreateRuntimeAsync()
    {
        IExecutionRuntimeAdapter runtime = CreateRuntime(
            Path.Combine(Path.GetTempPath(), $"vyral-execution-conformance-{Guid.NewGuid():N}.sqlite"));
        return Task.FromResult(runtime);
    }

    protected override Task<(IExecutionRuntimeAdapter First, IExecutionRuntimeAdapter Second)> CreateRestartableRuntimePairAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-execution-conformance-resume-{Guid.NewGuid():N}.sqlite");
        return Task.FromResult<(IExecutionRuntimeAdapter First, IExecutionRuntimeAdapter Second)>((CreateRuntime(path), CreateRuntime(path)));
    }

    protected override Task DispatchReadyRunsAsync(IExecutionRuntimeAdapter runtime)
    {
        return ((LocalExecutionRuntime)runtime).DispatchReadyRunsAsync();
    }

    [Fact]
    public Task LocalExecutionRuntime_ExposesAdapterCapabilities() =>
        RunExecutionRuntime_ExposesAdapterCapabilities();

    [Fact]
    public Task LocalExecutionRuntime_RunsPluginRecordsProgressArtifactsHistoryAndIdempotency() =>
        RunExecutionRuntime_RunsPluginRecordsProgressArtifactsHistoryAndIdempotency();

    [Fact]
    public Task LocalExecutionRuntime_RejectsMissingHandlersAndPluginMismatches() =>
        RunExecutionRuntime_RejectsMissingHandlersAndPluginMismatches();

    [Fact]
    public Task LocalExecutionRuntime_RejectsIdempotencyKeyReuseForDifferentRequest() =>
        RunExecutionRuntime_RejectsIdempotencyKeyReuseForDifferentRequest();

    [Fact]
    public Task LocalExecutionRuntime_ExposesConsumerErrorSemantics() =>
        RunExecutionRuntime_ExposesConsumerErrorSemantics();

    [Fact]
    public Task LocalExecutionRuntime_CancelsRunningPlugin() =>
        RunExecutionRuntime_CancelsRunningPlugin();

    [Fact]
    public Task LocalExecutionRuntime_CancelAfterTerminalRunIsStable() =>
        RunExecutionRuntime_CancelAfterTerminalRunIsStable();

    [Fact]
    public Task LocalExecutionRuntime_RetriesFailedRunUntilSuccess() =>
        RunExecutionRuntime_RetriesFailedRunUntilSuccess();

    [Fact]
    public Task LocalExecutionRuntime_StopsRetryingAfterMaxAttempts() =>
        RunExecutionRuntime_StopsRetryingAfterMaxAttempts();

    [Fact]
    public Task LocalExecutionRuntime_RetriesTimedOutRunUntilSuccess() =>
        RunExecutionRuntime_RetriesTimedOutRunUntilSuccess();

    [Fact]
    public Task LocalExecutionRuntime_RunsScheduledWorkWhenDue() =>
        RunExecutionRuntime_RunsScheduledWorkWhenDue();

    [Fact]
    public Task LocalExecutionRuntime_ResumesPersistedScheduledWorkFromNewAdapter() =>
        RunExecutionRuntime_ResumesPersistedScheduledWorkFromNewAdapter();

    [Fact]
    public Task LocalExecutionRuntime_PluginCanUseCoordinationPrimitives() =>
        RunExecutionRuntime_PluginCanUseCoordinationPrimitives();

    [Fact]
    public Task LocalExecutionRuntime_EnforcesLeaseOwnershipReleaseAndExpiry() =>
        RunExecutionRuntime_EnforcesLeaseOwnershipReleaseAndExpiry();

    [Fact]
    public Task LocalExecutionRuntime_RecordsTimerAndExternalEventShape() =>
        RunExecutionRuntime_RecordsTimerAndExternalEventShape();

    [Fact]
    public Task LocalExecutionRuntime_BoundsRunListingAndHistoryQueries() =>
        RunExecutionRuntime_BoundsRunListingAndHistoryQueries();

    [Fact]
    public Task LocalExecutionRuntime_RunsWindowedBatchPluginFixture() =>
        RunExecutionRuntime_RunsWindowedBatchPluginFixture();

    [Fact]
    public Task LocalExecutionRuntime_RunsPressureAwarePipelineFixture() =>
        RunExecutionRuntime_RunsPressureAwarePipelineFixture();

    private static LocalExecutionRuntime CreateRuntime(string path)
    {
        return new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = path,
            MaxActiveRuns = 8,
            MaxRetainedTerminalRuns = 50,
            DefaultListLimit = 20,
            MaxListLimit = 100
        });
    }
}
