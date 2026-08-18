using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Temporalio.Client;
using Temporalio.Client.Interceptors;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Execution.Temporal;
using Vyral.Execution.Temporal.Hosting;
using Vyral.Execution.Temporal.Postgres;
using Vyral.Local;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Temporal;

public sealed class TemporalContainerExecutionRuntimeConformanceTests :
    ExecutionRuntimeConformanceTests,
    IAsyncLifetime
{
    private const int MaxContinueAsNewChainLength = 8;
    private const int MaxHistoryEventsPerTemporalRun = 512;
    private const int MaxEncodedHistoryBytesPerTemporalRun = 1_048_576;
    private const int MaxHistoryPagesPerTemporalRun = 16;
    private readonly List<ContainerRuntimeHost> _hosts = [];

    [TemporalContainerFact]
    public Task TemporalContainer_ExposesAdapterCapabilities() =>
        RunExecutionRuntime_ExposesAdapterCapabilities();

    [TemporalContainerFact]
    public Task TemporalContainer_RunsPluginRecordsProgressArtifactsHistoryAndIdempotency() =>
        RunExecutionRuntime_RunsPluginRecordsProgressArtifactsHistoryAndIdempotency();

    [TemporalContainerFact]
    public Task TemporalContainer_RejectsMissingHandlersAndPluginMismatches() =>
        RunExecutionRuntime_RejectsMissingHandlersAndPluginMismatches();

    [TemporalContainerFact]
    public Task TemporalContainer_RejectsIdempotencyKeyReuseForDifferentRequest() =>
        RunExecutionRuntime_RejectsIdempotencyKeyReuseForDifferentRequest();

    [TemporalContainerFact]
    public Task TemporalContainer_ExposesConsumerErrorSemantics() =>
        RunExecutionRuntime_ExposesConsumerErrorSemantics();

    [TemporalContainerFact]
    public Task TemporalContainer_CancelsRunningPlugin() =>
        RunExecutionRuntime_CancelsRunningPlugin();

    [TemporalContainerFact]
    public Task TemporalContainer_CancelAfterTerminalRunIsStable() =>
        RunExecutionRuntime_CancelAfterTerminalRunIsStable();

    [TemporalContainerFact]
    public Task TemporalContainer_RetriesFailedRunUntilSuccess() =>
        RunExecutionRuntime_RetriesFailedRunUntilSuccess();

    [TemporalContainerFact]
    public Task TemporalContainer_StopsRetryingAfterMaxAttempts() =>
        RunExecutionRuntime_StopsRetryingAfterMaxAttempts();

    [TemporalContainerFact]
    public Task TemporalContainer_RetriesTimedOutRunUntilSuccess() =>
        RunExecutionRuntime_RetriesTimedOutRunUntilSuccess();

    [TemporalContainerFact]
    public Task TemporalContainer_RunsScheduledWorkWhenDue() =>
        RunExecutionRuntime_RunsScheduledWorkWhenDue();

    [TemporalContainerFact]
    public Task TemporalContainer_ResumesPersistedScheduledWorkFromNewAdapter() =>
        RunExecutionRuntime_ResumesPersistedScheduledWorkFromNewAdapter();

    [TemporalContainerFact]
    public Task TemporalContainer_PluginCanUseCoordinationPrimitives() =>
        RunExecutionRuntime_PluginCanUseCoordinationPrimitives();

    [TemporalContainerFact]
    public Task TemporalContainer_EnforcesLeaseOwnershipReleaseAndExpiry() =>
        RunExecutionRuntime_EnforcesLeaseOwnershipReleaseAndExpiry();

    [TemporalContainerFact]
    public Task TemporalContainer_RecordsTimerAndExternalEventShape() =>
        RunExecutionRuntime_RecordsTimerAndExternalEventShape();

    [TemporalContainerFact]
    public Task TemporalContainer_BoundsRunListingAndHistoryQueries() =>
        RunExecutionRuntime_BoundsRunListingAndHistoryQueries();

    [TemporalContainerFact]
    public Task TemporalContainer_RunsWindowedBatchPluginFixture() =>
        RunExecutionRuntime_RunsWindowedBatchPluginFixture();

    [TemporalContainerFact]
    public Task TemporalContainer_RunsPressureAwarePipelineFixture() =>
        RunExecutionRuntime_RunsPressureAwarePipelineFixture();

    [TemporalContainerFact]
    public async Task TemporalContainer_RunsPortableConsumerPluginUnchanged()
    {
        var runtime = await CreateRuntimeAsync();
        var completed = await ExternalExecutionConsumerSample.RunAsync(
            runtime,
            "portable-consumer:temporal");

        await ExternalExecutionConsumerSample.AssertRunShapeAsync(runtime, completed);
    }

    [TemporalContainerFact]
    public async Task TemporalContainer_GenericHostCompositionExecutesPortableHandlerAndDrainsOutbox()
    {
        var dependencyHost = await CreateHostAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var schema = TemporalGateSettings.SchemaName(
            $"vyral_temporal_generic_{suffix[..20]}",
            "generic-host",
            suffix);
        var store = new PostgresTemporalExecutionProjectionStore(new PostgresTemporalProjectionOptions
        {
            ConnectionString = dependencyHost.ConnectionString,
            DatabaseSchema = schema,
            RequireTls = TemporalGateSettings.PostgresTlsRequired,
            DispatchClaimSeconds = 5,
            DispatchRetrySeconds = 1
        });
        await store.InitializeAsync();
        var options = new TemporalExecutionOptions
        {
            AdapterId = "temporal-generic-host-gate",
            AdapterNamespace = $"{dependencyHost.Options.AdapterNamespace}-generic",
            TargetHost = dependencyHost.Options.TargetHost,
            Namespace = dependencyHost.Options.Namespace,
            TaskQueue = $"{dependencyHost.Options.TaskQueue}-generic",
            WorkerDeploymentName = "vyral-generic-host-gate",
            ArtifactObjectContainer = dependencyHost.Options.ArtifactObjectContainer,
            RequireTls = dependencyHost.Options.RequireTls,
            ReconciliationBatchSize = dependencyHost.Options.ReconciliationBatchSize,
            Limits = dependencyHost.Options.Limits
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(dependencyHost.Client);
        builder.Services.AddSingleton<ITemporalExecutionRuntimeStore>(store);
        builder.Services.AddSingleton<IObjectStore>(dependencyHost.Objects);
        builder.Services
            .AddVyralTemporalExecution(options)
            .AddHandler<GenericHostProbeHandler>()
            .AddHostedWorker(new TemporalExecutionWorkerHostOptions
            {
                WorkerId = "generic-host-gate-worker",
                ReconciliationInterval = TimeSpan.FromMilliseconds(100),
                ReconciliationFailureDelay = TimeSpan.FromMilliseconds(250)
            });

        using var host = builder.Build();
        var started = false;
        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.StartAsync(startupTimeout.Token);
            started = true;
            var runtime = host.Services.GetRequiredService<TemporalExecutionRuntimeAdapter>();
            var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = GenericHostProbeHandler.HandlerId,
                IdempotencyKey = $"generic-host:{Guid.NewGuid():N}"
            });
            var completed = await WaitForStatusAsync(
                runtime,
                accepted.Id,
                ExecutionRunStatuses.Succeeded);

            Assert.Equal("generic-host", completed.Result!["composition"]!.GetValue<string>());
            Assert.Equal(1, completed.Attempt);
        }
        finally
        {
            try
            {
                if (started)
                {
                    using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await host.StopAsync(shutdownTimeout.Token);
                }
            }
            finally
            {
                await using var connection = new NpgsqlConnection(dependencyHost.ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [TemporalContainerFact]
    public async Task TemporalContainer_ReplaysHandlerAfterNativeTimerWait()
    {
        var host = await CreateHostAsync();
        host.Primary.RegisterHandler(new TimerWaitHandler());
        var accepted = await host.Primary.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = TimerWaitHandler.HandlerId,
            IdempotencyKey = $"timer:{Guid.NewGuid():N}"
        });

        var completed = await WaitForStatusAsync(host.Primary, accepted.Id, ExecutionRunStatuses.Succeeded);

        Assert.Equal(2, completed.Attempt);
        Assert.Equal(ExecutionWaitOutcomes.Timer, completed.Result!["outcome"]!.GetValue<string>());
        Assert.Equal("qualification-timer", completed.Result["timer"]!.GetValue<string>());
        var history = await host.Primary.GetHistoryAsync(completed.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitRegistered);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitResumed);
    }

    [TemporalContainerFact]
    public async Task TemporalContainer_DeliversExternalEventRaisedBeforeWaitRegistration()
    {
        var host = await CreateHostAsync();
        host.Primary.RegisterHandler(new DelayedExternalEventWaitHandler());
        var accepted = await host.Primary.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = DelayedExternalEventWaitHandler.HandlerId,
            IdempotencyKey = $"signal-before-wait:{Guid.NewGuid():N}"
        });

        _ = await host.Primary.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = "approval",
            Payload = new JsonObject { ["decision"] = "approved" }
        });
        var completed = await WaitForStatusAsync(host.Primary, accepted.Id, ExecutionRunStatuses.Succeeded);

        Assert.Equal(2, completed.Attempt);
        Assert.Equal(ExecutionWaitOutcomes.ExternalEvent, completed.Result!["outcome"]!.GetValue<string>());
        Assert.Equal("approved", completed.Result["decision"]!.GetValue<string>());
        var history = await host.Primary.GetHistoryAsync(completed.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.ExternalEventRaised);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitRegistered);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitResumed);
    }

    [TemporalContainerFact]
    public async Task TemporalContainer_PreflightVerifiesWorkerAndActiveCoordinator()
    {
        var host = await CreateHostAsync();
        host.Primary.RegisterHandler(new DelayedExternalEventWaitHandler());
        var accepted = await host.Primary.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = DelayedExternalEventWaitHandler.HandlerId,
            IdempotencyKey = $"preflight:{Guid.NewGuid():N}"
        });
        _ = await WaitForStatusAsync(host.Primary, accepted.Id, ExecutionRunStatuses.Waiting);

        var result = await new TemporalExecutionPreflight(
            host.Client,
            host.Store,
            host.Objects,
            host.Options).RunAsync();

        Assert.True(result.Ready);
        Assert.Equal(0, result.BlockerCount);
        Assert.Equal(0, result.WarningCount);
        Assert.Contains(result.Checks, item => item.Code == "worker.pollers_active");
        Assert.Contains(result.Checks, item => item.Code == "worker.current_build_pollers_active");
        Assert.Contains(result.Checks, item => item.Code == "worker.versioning_mode_consistent");
        Assert.Equal("unversioned", result.Details["workerVersioningMode"]);
        Assert.NotEqual("0", result.Details["workflowCurrentBuildPollers"]);
        Assert.NotEqual("0", result.Details["activityCurrentBuildPollers"]);
        Assert.Contains(result.Checks, item => item.Code == "projection.coordinators_consistent");
        Assert.Contains(result.Checks, item => item.Code == "projection.coordinator_check_complete");
        Assert.Equal("1", result.Details["activeCoordinators"]);
        Assert.Equal("1", result.Details["coordinatorsExamined"]);
        Assert.Equal("0", result.Details["staleRuns"]);

        _ = await host.Primary.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = "approval",
            Payload = new JsonObject { ["decision"] = "approved" }
        });
        _ = await WaitForStatusAsync(host.Primary, accepted.Id, ExecutionRunStatuses.Succeeded);
    }

    [TemporalContainerFact]
    public async Task TemporalContainer_OffloadsLargeArtifactToDisposableObjectStore()
    {
        var host = await CreateHostAsync();
        host.Primary.RegisterHandler(new LargeArtifactHandler());
        var accepted = await host.Primary.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = LargeArtifactHandler.HandlerId,
            IdempotencyKey = $"large-artifact:{Guid.NewGuid():N}"
        });

        var completed = await WaitForStatusAsync(host.Primary, accepted.Id, ExecutionRunStatuses.Succeeded);
        var artifact = Assert.Single(await host.Primary.ListArtifactsAsync(completed.Id));

        Assert.Equal("object-store", artifact.Metadata["storage"]);
        Assert.Equal("true", artifact.Metadata["offloaded"]);
        Assert.Null(artifact.Text);
        Assert.Null(artifact.Content);
        var objects = await host.Objects.ListObjectsAsync(new ObjectListRequest
        {
            Container = host.Options.ArtifactObjectContainer,
            Prefix = "execution-artifacts/"
        });
        var stored = Assert.Single(objects.Items);
        Assert.Equal(LargeArtifactHandler.ArtifactSize, stored.ContentLength);
        var body = await host.Objects.GetObjectAsync(new ObjectReadRequest
        {
            Container = stored.Container,
            Key = stored.Key
        });
        Assert.NotNull(body);
        Assert.Equal(LargeArtifactHandler.ArtifactSize, body!.ContentLength);
    }

    [TemporalContainerFact]
    public async Task TemporalContainer_ExcludesConsumerBodiesFromCoordinatorHistory()
    {
        var host = await CreateHostAsync();
        host.Primary.RegisterHandler(new TemporalHistoryBoundaryHandler());
        var accepted = await host.Primary.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = TemporalHistoryBoundaryHandler.HandlerId,
            IdempotencyKey = $"history-boundary:{Guid.NewGuid():N}",
            Payload = new JsonObject
            {
                ["confidential"] = TemporalHistoryBoundaryHandler.PayloadSentinel
            }
        });
        _ = await WaitForStatusAsync(host.Primary, accepted.Id, ExecutionRunStatuses.Waiting);

        _ = await host.Primary.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = TemporalHistoryBoundaryHandler.EventName,
            Payload = new JsonObject
            {
                ["confidential"] = TemporalHistoryBoundaryHandler.EventSentinel
            }
        });
        var completed = await WaitForStatusAsync(host.Primary, accepted.Id, ExecutionRunStatuses.Succeeded);

        Assert.Equal(
            TemporalHistoryBoundaryHandler.PayloadSentinel,
            completed.Payload!["confidential"]!.GetValue<string>());
        Assert.Equal(
            TemporalHistoryBoundaryHandler.ResultSentinel,
            completed.Result!["confidential"]!.GetValue<string>());
        Assert.Equal(
            TemporalHistoryBoundaryHandler.StatusSentinel,
            completed.StatusDetails!["confidential"]!.GetValue<string>());
        var checkpoint = await host.Primary.GetCheckpointAsync(completed.Id, TemporalHistoryBoundaryHandler.CheckpointKey);
        Assert.Equal(
            TemporalHistoryBoundaryHandler.CheckpointSentinel,
            checkpoint!.Content!["confidential"]!.GetValue<string>());
        var artifact = Assert.Single(await host.Primary.ListArtifactsAsync(completed.Id));
        Assert.Equal(TemporalHistoryBoundaryHandler.ArtifactSentinel, artifact.Text);
        var portableHistory = await host.Primary.GetHistoryAsync(completed.Id, new ExecutionHistoryQuery { Limit = 500 });
        Assert.Contains(
            portableHistory,
            item => item.Message == TemporalHistoryBoundaryHandler.TraceSentinel &&
                item.Details?["confidential"]?.GetValue<string>() == TemporalHistoryBoundaryHandler.TraceSentinel);

        var temporalRunId = await host.GetInitialTemporalRunIdAsync(completed.Id);
        var workflowId = TemporalExecutionIdentity.CreateWorkflowId(host.Options.AdapterNamespace, completed.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var temporalHistory = await InspectTemporalHistoryChainAsync(
            host.Client,
            workflowId,
            temporalRunId,
            timeout.Token);
        foreach (var temporalRun in temporalHistory.Runs)
        {
            foreach (var sentinel in TemporalHistoryBoundaryHandler.ConsumerBodySentinels)
            {
                Assert.False(
                    ContainsUtf8(temporalRun.EncodedHistory, sentinel),
                    "Temporal coordinator history contained a consumer-owned body sentinel.");
            }
        }
    }

    [TemporalContainerFact]
    public async Task TemporalContainer_ContinuesAsNewWithoutChangingThePortableRun()
    {
        var host = await CreateHostAsync();
        host.Primary.RegisterHandler(new ContinueAsNewHandler());
        var accepted = await host.Primary.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = ContinueAsNewHandler.HandlerId,
            IdempotencyKey = $"continue-as-new:{Guid.NewGuid():N}"
        });

        var completed = await WaitForStatusAsync(host.Primary, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(ContinueAsNewHandler.CycleCount + 1, completed.Attempt);
        Assert.Equal(ContinueAsNewHandler.CycleCount, completed.Result!["cycles"]!.GetValue<int>());

        var firstTemporalRunId = await host.GetInitialTemporalRunIdAsync(completed.Id);
        var workflowId = TemporalExecutionIdentity.CreateWorkflowId(host.Options.AdapterNamespace, completed.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var handle = host.Client.GetWorkflowHandle<ITemporalRunCoordinatorWorkflow, TemporalCoordinatorResult>(
            workflowId,
            firstTemporalRunId,
            firstTemporalRunId);
        var coordinatorResult = await handle.GetResultAsync(
            followRuns: true,
            new RpcOptions { CancellationToken = timeout.Token });

        Assert.Equal(completed.Id, coordinatorResult.RunId);
        Assert.Equal(1, coordinatorResult.Generation);
        Assert.Equal(37, coordinatorResult.CoordinationTransitions);
        var latest = await host.Client.WorkflowService.DescribeWorkflowExecutionAsync(
            new Temporalio.Api.WorkflowService.V1.DescribeWorkflowExecutionRequest
            {
                Namespace = host.Options.Namespace,
                Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = workflowId }
            },
            new RpcOptions { CancellationToken = timeout.Token });
        var latestTemporalRunId = latest.WorkflowExecutionInfo.Execution.RunId;
        Assert.False(
            StringComparer.Ordinal.Equals(firstTemporalRunId, latestTemporalRunId),
            "Temporal did not continue the coordinator as a new workflow run.");

        var temporalHistory = await InspectTemporalHistoryChainAsync(
            host.Client,
            workflowId,
            firstTemporalRunId,
            timeout.Token);
        Assert.InRange(temporalHistory.Runs.Count, 2, MaxContinueAsNewChainLength);
        Assert.True(
            StringComparer.Ordinal.Equals(latestTemporalRunId, temporalHistory.LastRunId),
            "Temporal history inspection did not end at the latest workflow run.");
        foreach (var temporalRun in temporalHistory.Runs)
        {
            Assert.InRange(temporalRun.EventCount, 1, MaxHistoryEventsPerTemporalRun);
            Assert.InRange(temporalRun.EncodedBytes, 1, MaxEncodedHistoryBytesPerTemporalRun);
        }

        var history = await host.Primary.GetHistoryAsync(completed.Id, new ExecutionHistoryQuery { Limit = 500 });
        Assert.Equal(
            ContinueAsNewHandler.CycleCount,
            history.Count(item => item.Type == ExecutionEventTypes.WaitRegistered));
        Assert.Equal(
            ContinueAsNewHandler.CycleCount,
            history.Count(item => item.Type == ExecutionEventTypes.WaitResumed));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        List<Exception> failures = [];
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
        if (failures.Count > 0) throw new AggregateException("Temporal container test cleanup failed.", failures);
    }

    protected override async Task<IExecutionRuntimeAdapter> CreateRuntimeAsync() =>
        (await CreateHostAsync()).Primary;

    protected override async Task<(IExecutionRuntimeAdapter First, IExecutionRuntimeAdapter Second)>
        CreateRestartableRuntimePairAsync()
    {
        var host = await CreateHostAsync(createSecondRuntime: true);
        return (host.Primary, host.Secondary!);
    }

    protected override async Task DispatchReadyRunsAsync(IExecutionRuntimeAdapter runtime)
    {
        var host = _hosts.Single(candidate =>
            ReferenceEquals(candidate.Primary, runtime) || ReferenceEquals(candidate.Secondary, runtime));
        _ = await host.Reconciler.ReconcileAsync();
    }

    private async Task<ContainerRuntimeHost> CreateHostAsync(bool createSecondRuntime = false)
    {
        var host = await ContainerRuntimeHost.CreateAsync(createSecondRuntime);
        _hosts.Add(host);
        return host;
    }

    private static async Task<ExecutionRun> WaitForStatusAsync(
        IExecutionRuntime runtime,
        string runId,
        string expectedStatus)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ExecutionRun? current = null;
        while (!timeout.IsCancellationRequested)
        {
            current = await runtime.GetRunAsync(runId, ct: timeout.Token);
            if (current?.Status == expectedStatus) return current;
            if (current is not null && ExecutionRunStatuses.IsTerminal(current.Status))
            {
                throw new InvalidOperationException(
                    $"Run reached terminal status {current.Status} instead of {expectedStatus}; " +
                    $"failure: {current.FailureClass ?? "none"}; error: {current.Error ?? "none"}; " +
                    $"details: {current.StatusDetails?.ToJsonString() ?? "none"}.");
            }
            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException($"Run did not reach {expectedStatus}; last status was {current?.Status ?? "missing"}.");
    }

    private static async Task<TemporalHistoryChainInspection> InspectTemporalHistoryChainAsync(
        ITemporalClient client,
        string workflowId,
        string firstRunId,
        CancellationToken ct)
    {
        // The portable projection can reach a terminal state just before Temporal appends the
        // coordinator's terminal history event. Wait on Temporal itself before inspecting the
        // immutable history chain so qualification never races that final append.
        var handle = client.GetWorkflowHandle<ITemporalRunCoordinatorWorkflow, TemporalCoordinatorResult>(
            workflowId,
            firstRunId,
            firstRunId);
        _ = await handle.GetResultAsync(
            followRuns: true,
            new RpcOptions { CancellationToken = ct });

        List<TemporalHistoryRunMeasurement> measurements = [];
        HashSet<string> visitedRunIds = new(StringComparer.Ordinal);
        var currentRunId = firstRunId;

        for (var chainIndex = 0; chainIndex < MaxContinueAsNewChainLength; chainIndex++)
        {
            if (!visitedRunIds.Add(currentRunId))
            {
                throw new InvalidOperationException("Temporal continue-as-new history contained a run cycle.");
            }

            var events = await FetchTemporalRunHistoryAsync(client, workflowId, currentRunId, ct);
            var encodedHistory = new Temporalio.Api.History.V1.History();
            encodedHistory.Events.AddRange(events);
            var encodedHistoryBytes = encodedHistory.ToByteArray();
            var encodedBytes = encodedHistoryBytes.Length;
            if (encodedBytes > MaxEncodedHistoryBytesPerTemporalRun)
            {
                throw new InvalidOperationException("A Temporal workflow run exceeded the encoded history qualification limit.");
            }
            measurements.Add(new TemporalHistoryRunMeasurement(events.Count, encodedBytes, encodedHistoryBytes));

            var terminalEvent = events[^1];
            var continuationEventCount = events.Count(
                item => item.WorkflowExecutionContinuedAsNewEventAttributes is not null);
            var continuation = terminalEvent.WorkflowExecutionContinuedAsNewEventAttributes;
            if (continuation is null)
            {
                if (continuationEventCount != 0 || terminalEvent.WorkflowExecutionCompletedEventAttributes is null)
                {
                    throw new InvalidOperationException("The final Temporal workflow run did not end as completed.");
                }
                return new TemporalHistoryChainInspection(currentRunId, measurements);
            }

            if (continuationEventCount != 1 ||
                string.IsNullOrWhiteSpace(continuation.NewExecutionRunId))
            {
                throw new InvalidOperationException("A Temporal workflow run had an invalid continue-as-new transition.");
            }
            currentRunId = continuation.NewExecutionRunId;
        }

        throw new InvalidOperationException("Temporal continue-as-new history exceeded the qualification chain limit.");
    }

    private static async Task<List<Temporalio.Api.History.V1.HistoryEvent>> FetchTemporalRunHistoryAsync(
        ITemporalClient client,
        string workflowId,
        string runId,
        CancellationToken ct)
    {
        List<Temporalio.Api.History.V1.HistoryEvent> events = [];
        var nextPageToken = Array.Empty<byte>();

        for (var pageIndex = 0; pageIndex < MaxHistoryPagesPerTemporalRun; pageIndex++)
        {
            var page = await client.OutboundInterceptor.FetchWorkflowHistoryEventPageAsync(
                new FetchWorkflowHistoryEventPageInput(
                    workflowId,
                    runId,
                    MaxHistoryEventsPerTemporalRun + 1,
                    nextPageToken,
                    false,
                    default,
                    true,
                    new RpcOptions { CancellationToken = ct }));
            events.AddRange(page.Events);
            if (events.Count > MaxHistoryEventsPerTemporalRun)
            {
                throw new InvalidOperationException("A Temporal workflow run exceeded the history event qualification limit.");
            }
            var returnedNextPageToken = page.NextPageToken;
            if (returnedNextPageToken is not { Length: > 0 })
            {
                if (events.Count == 0)
                {
                    throw new InvalidOperationException("Temporal returned an empty workflow history.");
                }
                return events;
            }
            nextPageToken = returnedNextPageToken;
        }

        throw new InvalidOperationException("Temporal workflow history pagination exceeded the qualification limit.");
    }

    private static bool ContainsUtf8(ReadOnlySpan<byte> bytes, string value) =>
        bytes.IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;

    private sealed record TemporalHistoryRunMeasurement(
        int EventCount,
        int EncodedBytes,
        byte[] EncodedHistory);

    private sealed record TemporalHistoryChainInspection(
        string LastRunId,
        IReadOnlyList<TemporalHistoryRunMeasurement> Runs);

    private sealed class TimerWaitHandler : IExecutionHandler
    {
        public const string HandlerId = "qualification.temporal.timer";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal qualification timer wait"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var outcome = await context.WaitForTimerAsync(
                "qualification-timer",
                DateTime.UtcNow.AddMilliseconds(300),
                new JsonObject { ["source"] = "container-gate" },
                ct);
            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["outcome"] = outcome.Outcome,
                ["timer"] = outcome.Timer?.Name
            });
        }
    }

    private sealed class DelayedExternalEventWaitHandler : IExecutionHandler
    {
        public const string HandlerId = "qualification.temporal.signal-before-wait";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal qualification signal before wait"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            if (context.Run.Attempt == 1) await Task.Delay(300, ct);
            var outcome = await context.WaitForExternalEventAsync(
                "approval",
                DateTime.UtcNow.AddSeconds(10),
                ct);
            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["outcome"] = outcome.Outcome,
                ["decision"] = outcome.Event?.Payload?["decision"]?.GetValue<string>()
            });
        }
    }

    private sealed class LargeArtifactHandler : IExecutionHandler
    {
        public const string HandlerId = "qualification.temporal.large-artifact";
        public const int ArtifactSize = 131_072;

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal qualification large artifact"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "qualification-large.txt",
                Kind = ExecutionArtifactKinds.Text,
                MediaType = "text/plain",
                Text = new string('q', ArtifactSize)
            }, ct);
            return ExecutionRunResult.Succeeded();
        }
    }

    private sealed class TemporalHistoryBoundaryHandler : IExecutionHandler
    {
        public const string HandlerId = "qualification.temporal.history-boundary";
        public const string EventName = "history-boundary-event";
        public const string CheckpointKey = "history-boundary-checkpoint";
        public const string PayloadSentinel = "vyral-body-payload-65e4b85e";
        public const string EventSentinel = "vyral-body-event-1f0a763c";
        public const string CheckpointSentinel = "vyral-body-checkpoint-c591a0d2";
        public const string ArtifactSentinel = "vyral-body-artifact-b2d85507";
        public const string TraceSentinel = "vyral-body-trace-0a95c2bb";
        public const string StatusSentinel = "vyral-body-status-15b6cf83";
        public const string ResultSentinel = "vyral-body-result-24b3ab67";

        public static IReadOnlyList<string> ConsumerBodySentinels { get; } =
        [
            PayloadSentinel,
            EventSentinel,
            CheckpointSentinel,
            ArtifactSentinel,
            TraceSentinel,
            StatusSentinel,
            ResultSentinel
        ];

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal coordinator history boundary qualification"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var checkpoint = await context.GetCheckpointAsync(CheckpointKey, ct);
            if (checkpoint is null)
            {
                if (context.Run.Payload?["confidential"]?.GetValue<string>() != PayloadSentinel)
                {
                    throw new InvalidOperationException("The history-boundary handler did not receive its projection payload.");
                }
                await context.ReportAsync(new ExecutionRunUpdate
                {
                    Status = ExecutionRunStatuses.Running,
                    Progress = 0.5,
                    CurrentStep = StatusSentinel,
                    StatusDetails = new JsonObject { ["confidential"] = StatusSentinel }
                }, ct);
                await context.RecordEventAsync(
                    ExecutionEventTypes.StepStarted,
                    TraceSentinel,
                    details: new JsonObject { ["confidential"] = TraceSentinel },
                    ct: ct);
                await context.PutArtifactAsync(new ExecutionArtifactWrite
                {
                    Name = "history-boundary.txt",
                    Kind = ExecutionArtifactKinds.Text,
                    MediaType = "text/plain",
                    Text = ArtifactSentinel
                }, ct);
                await context.PutCheckpointAsync(new ExecutionCheckpointWrite
                {
                    Key = CheckpointKey,
                    Content = new JsonObject { ["confidential"] = CheckpointSentinel }
                }, ct);
            }

            var wait = await context.WaitForExternalEventAsync(
                EventName,
                DateTime.UtcNow.AddSeconds(10),
                ct);
            if (wait.Event?.Payload?["confidential"]?.GetValue<string>() != EventSentinel)
            {
                throw new InvalidOperationException("The history-boundary handler did not receive its projection event.");
            }
            return ExecutionRunResult.Succeeded(
                new JsonObject { ["confidential"] = ResultSentinel },
                new JsonObject { ["confidential"] = StatusSentinel });
        }
    }

    private sealed class GenericHostProbeHandler : IExecutionHandler
    {
        public const string HandlerId = "qualification.temporal.generic-host";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal Generic Host qualification"
        };

        public Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default) =>
            Task.FromResult(ExecutionRunResult.Succeeded(
                new JsonObject { ["composition"] = "generic-host" }));
    }

    private sealed class ContinueAsNewHandler : IExecutionHandler
    {
        public const string HandlerId = "qualification.temporal.continue-as-new";
        public const int CycleCount = 18;
        private static readonly TimeSpan CycleDelay = TimeSpan.FromMilliseconds(300);
        private const string CheckpointKey = "continue-as-new-cycles";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal qualification continue-as-new"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var checkpoint = await context.GetCheckpointAsync(CheckpointKey, ct);
            var completedCycles = checkpoint?.Content?["cycles"]?.GetValue<int>() ?? 0;
            while (completedCycles < CycleCount)
            {
                var nextCycle = completedCycles + 1;
                // Keep the timer ahead of a persistence round trip so every cycle exercises
                // the durable suspend/resume path rather than the valid immediate-elapsed path.
                _ = await context.WaitForTimerAsync(
                    $"continue-as-new-{nextCycle}",
                    DateTime.UtcNow.Add(CycleDelay),
                    ct: ct);
                completedCycles = nextCycle;
                await context.PutCheckpointAsync(new ExecutionCheckpointWrite
                {
                    Key = CheckpointKey,
                    Content = new JsonObject { ["cycles"] = completedCycles }
                }, ct);
            }

            return ExecutionRunResult.Succeeded(new JsonObject { ["cycles"] = completedCycles });
        }
    }

    private sealed class ContainerRuntimeHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private readonly TemporalExecutionWorker _worker;
        private readonly Task _workerTask;
        private readonly Task _reconcilerTask;

        private ContainerRuntimeHost(
            string connectionString,
            string schema,
            string objectRoot,
            TemporalExecutionOptions options,
            FileObjectStore objects,
            ITemporalClient client,
            PostgresTemporalExecutionProjectionStore store,
            TemporalExecutionRuntimeAdapter primary,
            TemporalExecutionRuntimeAdapter? secondary,
            TemporalExecutionOutboxReconciler reconciler,
            CancellationTokenSource shutdown,
            TemporalExecutionWorker worker,
            Task workerTask,
            Task reconcilerTask)
        {
            ConnectionString = connectionString;
            Schema = schema;
            ObjectRoot = objectRoot;
            Options = options;
            Objects = objects;
            Client = client;
            Store = store;
            Primary = primary;
            Secondary = secondary;
            Reconciler = reconciler;
            _shutdown = shutdown;
            _worker = worker;
            _workerTask = workerTask;
            _reconcilerTask = reconcilerTask;
        }

        public string ConnectionString { get; }
        public string Schema { get; }
        public string ObjectRoot { get; }
        public TemporalExecutionOptions Options { get; }
        public FileObjectStore Objects { get; }
        public ITemporalClient Client { get; }
        public PostgresTemporalExecutionProjectionStore Store { get; }
        public TemporalExecutionRuntimeAdapter Primary { get; }
        public TemporalExecutionRuntimeAdapter? Secondary { get; }
        public TemporalExecutionOutboxReconciler Reconciler { get; }

        public static async Task<ContainerRuntimeHost> CreateAsync(bool createSecondRuntime)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var connectionString = Required("VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING");
            var targetHost = Optional("VYRAL_EXECUTION_TEMPORAL_TARGET_HOST") ?? "127.0.0.1:37233";
            var temporalNamespace = Optional("VYRAL_EXECUTION_TEMPORAL_NAMESPACE") ?? "vyral-qualification";
            var schema = TemporalGateSettings.SchemaName(
                $"vyral_temporal_gate_{suffix[..20]}",
                "conformance",
                suffix);
            var objectBase = Optional("VYRAL_TEMPORAL_GATE_OBJECT_ROOT") ?? Path.GetTempPath();
            var objectRootName = TemporalGateSettings.PortableName(
                $"vyral-temporal-gate-{suffix}",
                "conformance",
                suffix);
            var objectRoot = Path.GetFullPath(Path.Combine(objectBase, objectRootName));
            var options = new TemporalExecutionOptions
            {
                AdapterId = "temporal-container-gate",
                AdapterNamespace = TemporalGateSettings.PortableName(
                    $"gate-{suffix[..20]}",
                    "conformance",
                    suffix),
                TargetHost = targetHost,
                Namespace = temporalNamespace,
                TaskQueue = TemporalGateSettings.PortableName(
                    $"vyral-temporal-gate-{suffix}",
                    "conformance",
                    suffix),
                ArtifactObjectContainer = "vyral-temporal-gate",
                RequireTls = TemporalGateSettings.TemporalTlsRequired,
                ReconciliationBatchSize = 100,
                Limits = new ExecutionRuntimeLimits
                {
                    MaxArtifactInlineBytes = 65_536
                }
            };
            var projectionOptions = new PostgresTemporalProjectionOptions
            {
                ConnectionString = connectionString,
                DatabaseSchema = schema,
                RequireTls = TemporalGateSettings.PostgresTlsRequired,
                DispatchClaimSeconds = 5,
                DispatchRetrySeconds = 1
            };
            options.Validate();
            projectionOptions.Validate();
            Directory.CreateDirectory(objectRoot);
            var objects = new FileObjectStore(objectRoot);
            var store = new PostgresTemporalExecutionProjectionStore(projectionOptions);
            await store.InitializeAsync();
            var client = await TemporalClient.ConnectAsync(
                TemporalGateSettings.ClientOptions(targetHost, temporalNamespace));
            var coordinator = new TemporalSdkCoordinatorClient(client, options.TaskQueue);
            var primary = new TemporalExecutionRuntimeAdapter(store, coordinator, options);
            var secondary = createSecondRuntime
                ? new TemporalExecutionRuntimeAdapter(store, coordinator, options)
                : null;
            var workerRuntime = secondary ?? primary;
            var worker = new TemporalExecutionWorker(
                client,
                options.TaskQueue,
                workerRuntime,
                store,
                options,
                $"container-gate-{Environment.ProcessId}",
                objects);
            var reconciler = new TemporalExecutionOutboxReconciler(store, coordinator, options);
            var shutdown = new CancellationTokenSource();
            var workerTask = worker.ExecuteAsync(shutdown.Token);
            var reconcilerTask = ReconcileUntilCancelledAsync(reconciler, shutdown.Token);
            await Task.Delay(250);
            return new ContainerRuntimeHost(
                connectionString,
                schema,
                objectRoot,
                options,
                objects,
                client,
                store,
                primary,
                secondary,
                reconciler,
                shutdown,
                worker,
                workerTask,
                reconcilerTask);
        }

        public async Task<string> GetInitialTemporalRunIdAsync(string runId)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT temporal_run_id FROM \"{Schema}\".vyral_temporal_runs WHERE run_id = @run_id;";
            command.Parameters.AddWithValue("run_id", runId);
            return (await command.ExecuteScalarAsync()) as string ??
                throw new InvalidOperationException("Temporal projection did not retain the initial diagnostic run id.");
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            try
            {
                await IgnoreCancellationAsync(_workerTask, _shutdown.Token);
                await IgnoreCancellationAsync(_reconcilerTask, _shutdown.Token);
            }
            finally
            {
                _worker.Dispose();
                _shutdown.Dispose();
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE;";
                await command.ExecuteNonQueryAsync();
                if (Directory.Exists(ObjectRoot)) Directory.Delete(ObjectRoot, recursive: true);
            }
        }

        private static async Task ReconcileUntilCancelledAsync(
            TemporalExecutionOutboxReconciler reconciler,
            CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _ = await reconciler.ReconcileAsync(ct: ct);
                    await Task.Delay(50, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(100, ct);
                }
            }
        }

        private static async Task IgnoreCancellationAsync(Task task, CancellationToken ct)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        private static string Required(string name) => Optional(name) ??
            throw new InvalidOperationException($"Required gate setting '{name}' is missing.");

        private static string? Optional(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class TemporalContainerFactAttribute : FactAttribute
{
    public TemporalContainerFactAttribute()
    {
        if (!TemporalGateSettings.IntegrationGateEnabled)
        {
            Skip = "Run a checked-in Temporal container or live gate to enable integration qualification.";
        }
    }
}
