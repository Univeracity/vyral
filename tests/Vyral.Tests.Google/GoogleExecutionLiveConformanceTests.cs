using Google.Cloud.Firestore;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Google;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Google;

/// <summary>
/// Runs the provider-neutral external-worker regime against the production Firestore state store.
/// Queue delivery is intentionally inert here: the companion deployment smoke owns the real Cloud
/// Tasks transport, while this fixture makes every lease, checkpoint, event, wait, completion, and
/// stale-write assertion independently reproducible against Firestore transactions.
/// </summary>
public sealed class GoogleExecutionLiveConformanceTests : ExternalExecutionWorkerRuntimeConformanceTests, IAsyncLifetime
{
    private readonly List<LiveFixtureState> _fixtures = [];

    protected override Task<ExternalExecutionWorkerRuntimeFixture> CreateExternalWorkerRuntimeAsync()
    {
        var root = GoogleLiveSettings.UniquePrefix(GoogleLiveSettings.RequireExecutionFirestoreRoot());
        if (!root.StartsWith("vyral_handoff_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Live execution conformance requires a uniquely prefixed 'vyral_handoff_' Firestore root.");
        }

        var firestore = new FirestoreDbBuilder
        {
            ProjectId = GoogleLiveSettings.ExecutionProjectId!,
            DatabaseId = GoogleLiveSettings.ExecutionDatabaseId,
            Credential = GoogleLiveSettings.CreateExecutionCredential()
        }.Build();
        var state = new FirestoreExecutionStateStore(firestore, root);
        _fixtures.Add(new LiveFixtureState(firestore, state, root));

        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "live.google.external.worker",
            PluginId = "live.google.external",
            DisplayName = "Google live external-worker conformance handler"
        };
        var dispatch = new NoopDispatcher();
        var runtime = new GoogleCloudExecutionRuntimeAdapter(
            state,
            dispatch,
            new GoogleCloudExecutionRuntimeOptions
            {
                WorkerDispatchers =
                [
                    new GoogleCloudExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatch }
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

    [GoogleExecutionLiveFact]
    public Task GoogleFirestoreExternalWorker_AdvertisesExternalExecutionModel() =>
        RunExternalWorker_AdvertisesExternalExecutionModel();

    [GoogleExecutionLiveFact]
    public Task GoogleFirestoreExternalWorker_ReportsArtifactsEventsAndCheckpointsUnderLease() =>
        RunExternalWorker_ReportsArtifactsEventsAndCheckpointsUnderLease();

    [GoogleExecutionLiveFact]
    public Task GoogleFirestoreExternalWorker_DeduplicatesClaimsAndResumesDurableEvents() =>
        RunExternalWorker_DeduplicatesClaimsAndResumesDurableEvents();

    [GoogleExecutionLiveFact]
    public Task GoogleFirestoreExternalWorker_CompletionIsReplaySafeForTheSameLeaseToken() =>
        RunExternalWorker_CompletionIsReplaySafeForTheSameLeaseToken();

    [GoogleExecutionLiveFact]
    public Task GoogleFirestoreExternalWorker_RejectsStaleLeaseEvents() =>
        RunExternalWorker_RejectsStaleLeaseEvents();

    [GoogleExecutionLiveFact]
    public async Task GoogleFirestoreExternalWorker_ResumesWaitingEventAfterRecreatingAdapter()
    {
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "live.google.restart.worker",
            PluginId = "live.google.restart",
            DisplayName = "Google live restart conformance handler"
        };
        var root = GoogleLiveSettings.UniquePrefix("vyral_handoff_restart");
        var firstFirestore = CreateFirestore();
        var firstState = new FirestoreExecutionStateStore(firstFirestore, root);
        var first = CreateRuntime(firstState, handler);
        first.RegisterExternalHandler(handler);

        try
        {
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

            var secondFirestore = CreateFirestore();
            var secondState = new FirestoreExecutionStateStore(secondFirestore, root);
            var second = CreateRuntime(secondState, handler);
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
        finally
        {
            await DeleteFixtureStateAsync(new LiveFixtureState(firstFirestore, firstState, root));
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var fixture in _fixtures)
        {
            await DeleteFixtureStateAsync(fixture);
        }
    }

    private static FirestoreDb CreateFirestore() => new FirestoreDbBuilder
    {
        ProjectId = GoogleLiveSettings.ExecutionProjectId!,
        DatabaseId = GoogleLiveSettings.ExecutionDatabaseId,
        Credential = GoogleLiveSettings.CreateExecutionCredential()
    }.Build();

    private static GoogleCloudExecutionRuntimeAdapter CreateRuntime(
        FirestoreExecutionStateStore state,
        ExecutionHandlerDescriptor handler)
    {
        var dispatch = new NoopDispatcher();
        return new GoogleCloudExecutionRuntimeAdapter(
            state,
            dispatch,
            new GoogleCloudExecutionRuntimeOptions
            {
                WorkerDispatchers =
                [
                    new GoogleCloudExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatch }
                ]
            });
    }

    private static async Task DeleteFixtureStateAsync(LiveFixtureState fixture)
    {
        var runs = await fixture.State.ListRunsAsync(new ExecutionRunQuery { IncludeResult = false, Limit = 100 });
        foreach (var run in runs)
        {
            await fixture.State.DeleteRunAsync(run);
        }

        await fixture.Firestore.Collection(fixture.Root).Document("state").Collection("metadata")
            .Document("active-run-count").DeleteAsync();
        await fixture.Firestore.Collection(fixture.Root).Document("state").DeleteAsync();
    }

    private sealed class NoopDispatcher : IExecutionRunDispatcher
    {
        public Task DispatchAsync(ExecutionDispatchRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record LiveFixtureState(FirestoreDb Firestore, FirestoreExecutionStateStore State, string Root);
}
