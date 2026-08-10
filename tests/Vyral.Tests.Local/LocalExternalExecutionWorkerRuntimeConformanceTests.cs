using Vyral.Execution;
using Vyral.Execution.Local;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Local;

public sealed class LocalExternalExecutionWorkerRuntimeConformanceTests : ExternalExecutionWorkerRuntimeConformanceTests
{
    protected override Task<ExternalExecutionWorkerRuntimeFixture> CreateExternalWorkerRuntimeAsync()
    {
        var runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"vyral-external-worker-conformance-{Guid.NewGuid():N}.sqlite")
        });
        return Task.FromResult(new ExternalExecutionWorkerRuntimeFixture
        {
            Adapter = runtime,
            Worker = runtime,
            Handler = new ExecutionHandlerDescriptor
            {
                HandlerId = "conformance.external.worker",
                PluginId = "conformance.external",
                DisplayName = "External worker conformance handler"
            }
        });
    }

    [Fact]
    public Task LocalExternalWorker_AdvertisesExternalExecutionModel() =>
        RunExternalWorker_AdvertisesExternalExecutionModel();

    [Fact]
    public Task LocalExternalWorker_ReportsArtifactsEventsAndCheckpointsUnderLease() =>
        RunExternalWorker_ReportsArtifactsEventsAndCheckpointsUnderLease();

    [Fact]
    public Task LocalExternalWorker_DeduplicatesClaimsAndResumesDurableEvents() =>
        RunExternalWorker_DeduplicatesClaimsAndResumesDurableEvents();

    [Fact]
    public Task LocalExternalWorker_CompletionIsReplaySafeForTheSameLeaseToken() =>
        RunExternalWorker_CompletionIsReplaySafeForTheSameLeaseToken();

    [Fact]
    public Task LocalExternalWorker_RejectsStaleLeaseEvents() =>
        RunExternalWorker_RejectsStaleLeaseEvents();

    [Fact]
    public async Task LocalExternalWorker_RunsPortableConsumerPluginUnchanged()
    {
        var fixture = await CreateExternalWorkerRuntimeAsync();
        var completed = await ExternalExecutionConsumerSample.RunExternalAsync(
            fixture.Adapter,
            fixture.Worker,
            "portable-consumer:local-external");

        await ExternalExecutionConsumerSample.AssertRunShapeAsync(fixture.Adapter, completed);
    }
}
