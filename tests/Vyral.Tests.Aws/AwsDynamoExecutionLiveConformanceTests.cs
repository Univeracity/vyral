using Amazon.DynamoDBv2;
using Amazon.SQS;
using System.Text.Json.Nodes;
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

    [AwsExecutionLiveFact]
    public async Task AwsDynamoExternalWorker_ResumesWaitingEventAfterRecreatingAdapter()
    {
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "live.aws.restart.worker",
            PluginId = "live.aws.restart",
            DisplayName = "AWS live restart conformance handler"
        };
        var root = AwsLiveSettings.UniquePrefix("vyral-live-external-restart");
        var first = CreateRuntime(handler, root);
        first.RegisterExternalHandler(handler);

        var accepted = await first.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = handler.HandlerId
        });
        var lease = await first.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "live-restart-first",
            HandlerIds = { handler.HandlerId },
            RunId = accepted.Id,
            TtlSeconds = 30
        });
        Assert.NotNull(lease);

        var waiting = await first.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = lease!.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
            Name = "approval",
            TimeoutAtUtc = DateTime.UtcNow.AddMinutes(1)
        });
        Assert.True(waiting.Suspended);

        var second = CreateRuntime(handler, root);
        second.RegisterExternalHandler(handler);
        await second.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = "approval",
            Payload = new JsonObject { ["approved"] = true }
        });
        var resumedLease = await second.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "live-restart-second",
            HandlerIds = { handler.HandlerId },
            RunId = accepted.Id,
            TtlSeconds = 30
        });
        Assert.NotNull(resumedLease);

        var outcome = await second.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = resumedLease!.LeaseKey,
            LeaseToken = resumedLease.LeaseToken,
            WorkerId = resumedLease.WorkerId,
            Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
            Name = "approval",
            TimeoutAtUtc = DateTime.UtcNow.AddMinutes(1)
        });
        Assert.False(outcome.Suspended);
        Assert.True(outcome.Outcome!.Event!.Payload!["approved"]!.GetValue<bool>());

        var completed = await second.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = resumedLease.LeaseKey,
            LeaseToken = resumedLease.LeaseToken,
            WorkerId = resumedLease.WorkerId,
            Result = ExecutionRunResult.Succeeded()
        });
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
    }

    private static AwsDynamoExecutionRuntimeAdapter CreateRuntime(
        ExecutionHandlerDescriptor handler,
        string root)
    {
        var state = new DynamoDbExecutionStateStore(
            new AmazonDynamoDBClient(),
            new DynamoDbExecutionStateStoreOptions
            {
                TableName = AwsLiveSettings.ExecutionDynamoDbTable!,
                Root = root
            });
        var dispatcher = new NoopDispatcher();
        return new AwsDynamoExecutionRuntimeAdapter(
            state,
            dispatcher,
            new AwsDynamoExecutionRuntimeOptions
            {
                WorkerDispatchers =
                [
                    new AwsDynamoExecutionWorkerDispatcher
                    {
                        HandlerId = handler.HandlerId,
                        Dispatcher = dispatcher
                    }
                ]
            });
    }

    private sealed class NoopDispatcher : IExecutionRunDispatcher
    {
        public Task DispatchAsync(ExecutionDispatchRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }
}
