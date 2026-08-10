using Amazon.DynamoDBv2;
using Amazon.SQS;
using Vyral.Execution;
using Vyral.Execution.Aws;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Aws;

/// <summary>
/// Runs the provider-neutral external-worker contract through the production DynamoDB state
/// store and SQS dispatcher. The isolated gate owns and deletes the table and queue, so each
/// fixture uses a distinct root rather than relying on shared test state.
/// </summary>
public sealed class AwsDynamoExecutionLiveConformanceTests : ExternalExecutionWorkerRuntimeConformanceTests
{
    protected override Task<ExternalExecutionWorkerRuntimeFixture> CreateExternalWorkerRuntimeAsync()
    {
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "live.aws.external.worker",
            PluginId = "live.aws.external",
            DisplayName = "AWS live external-worker conformance handler"
        };
        var state = new DynamoDbExecutionStateStore(
            new AmazonDynamoDBClient(),
            new DynamoDbExecutionStateStoreOptions
            {
                TableName = AwsLiveSettings.ExecutionDynamoDbTable!,
                Root = AwsLiveSettings.UniquePrefix("vyral-live-external-conformance")
            });
        // SQS delivery is exercised by the dedicated real-queue smoke. Keeping this fixture's
        // dispatch boundary inert lets the shared state conformance cases run independently and
        // prevents their intentionally unconsumed envelopes from affecting that smoke.
        var dispatch = new NoopDispatcher();
        var runtime = new AwsDynamoExecutionRuntimeAdapter(
            state,
            dispatch,
            new AwsDynamoExecutionRuntimeOptions
            {
                WorkerDispatchers =
                [
                    new AwsDynamoExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatch }
                ]
            });
        return Task.FromResult(new ExternalExecutionWorkerRuntimeFixture
        {
            Adapter = runtime,
            Worker = runtime,
            Handler = handler,
            WorkerId = "live-conformance-worker"
        });
    }

    [AwsExecutionLiveFact]
    public Task AwsDynamoExternalWorker_ReportsArtifactsEventsAndCheckpointsUnderLease() =>
        RunExternalWorker_ReportsArtifactsEventsAndCheckpointsUnderLease();

    [AwsExecutionLiveFact]
    public Task AwsDynamoExternalWorker_DeduplicatesClaimsAndResumesDurableEvents() =>
        RunExternalWorker_DeduplicatesClaimsAndResumesDurableEvents();

    [AwsExecutionLiveFact]
    public Task AwsDynamoExternalWorker_CompletionIsReplaySafeForTheSameLeaseToken() =>
        RunExternalWorker_CompletionIsReplaySafeForTheSameLeaseToken();

    [AwsExecutionLiveFact]
    public Task AwsDynamoExternalWorker_RejectsStaleLeaseEvents() =>
        RunExternalWorker_RejectsStaleLeaseEvents();

    private sealed class NoopDispatcher : IExecutionRunDispatcher
    {
        public Task DispatchAsync(ExecutionDispatchRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }
}
