using Google.Cloud.Firestore;
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

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var fixture in _fixtures)
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
    }

    private sealed class NoopDispatcher : IExecutionRunDispatcher
    {
        public Task DispatchAsync(ExecutionDispatchRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record LiveFixtureState(FirestoreDb Firestore, FirestoreExecutionStateStore State, string Root);
}
