using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.TaskQueue.V1;
using TemporalDeploymentOptions = Temporalio.Api.Deployment.V1.WorkerDeploymentOptions;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Execution.Temporal;
using Vyral.Execution.Temporal.Hosting;

namespace Vyral.Tests.Temporal;

public sealed class TemporalExecutionRuntimeAdapterTests
{
    [Fact]
    public async Task Runtime_StartsCanonicalIdempotentRunsAndPersistsRejectionsWithoutWorkflowStarts()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var coordinator = new RecordingCoordinatorClient(store);
        var runtime = CreateRuntime(store, coordinator);
        runtime.RegisterHandler(new TestHandler());

        var first = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = TestHandler.HandlerId,
            IdempotencyKey = "same-request",
            Payload = new JsonObject { ["b"] = 2, ["a"] = 1 }
        });
        var replay = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = TestHandler.HandlerId,
            IdempotencyKey = "same-request",
            Payload = new JsonObject { ["a"] = 1, ["b"] = 2 }
        });

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(ExecutionRunStatuses.Queued, first.Status);
        Assert.Single(coordinator.Starts);
        Assert.DoesNotContain("same-request", coordinator.Starts[0].WorkflowId, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", JsonSerializer.Serialize(coordinator.Starts), StringComparison.OrdinalIgnoreCase);

        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = TestHandler.HandlerId,
            IdempotencyKey = "same-request",
            Payload = new JsonObject { ["a"] = "different" }
        }));
        Assert.Contains("idempotency key", conflict.Message, StringComparison.OrdinalIgnoreCase);

        var missing = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = "missing.handler" });
        Assert.Equal(ExecutionRunStatuses.Rejected, missing.Status);
        Assert.Equal(ExecutionFailureClasses.HandlerMissing, missing.FailureClass);
        Assert.Single(coordinator.Starts);
        Assert.Equal(missing.Id, (await runtime.GetRunAsync(missing.Id))!.Id);
    }

    [Fact]
    public async Task Runtime_PersistsSignalsAndCancellationBeforeCoordinatorDelivery()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var coordinator = new RecordingCoordinatorClient(store);
        var runtime = CreateRuntime(store, coordinator);
        runtime.RegisterHandler(new TestHandler());
        var run = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = TestHandler.HandlerId });

        var externalEvent = await runtime.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = run.Id,
            Name = "approved",
            Payload = new JsonObject { ["secret"] = "body" }
        });

        var signal = Assert.Single(coordinator.Signals);
        Assert.Equal(externalEvent.Id, signal.EventId);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(signal), StringComparison.OrdinalIgnoreCase);

        var cancelled = await runtime.CancelRunAsync(run.Id);
        Assert.NotNull(cancelled);
        Assert.True(cancelled!.CancellationRequested);
        Assert.Equal(run.Id, Assert.Single(coordinator.Cancellations));
        Assert.True(coordinator.CancellationObservedDurableFlag);
    }

    [Fact]
    public async Task Runtime_RedrivesDurableCancellationAfterCoordinatorOutage()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var coordinator = new RecordingCoordinatorClient(store) { FailCancellations = true };
        var runtime = CreateRuntime(store, coordinator);
        runtime.RegisterHandler(new TestHandler());
        var run = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = TestHandler.HandlerId });

        var requested = await runtime.CancelRunAsync(run.Id);

        Assert.True(requested!.CancellationRequested);
        Assert.Equal(1, (await store.GetRuntimeStatusAsync()).PendingCancellationDispatches);
        Assert.Empty(coordinator.Cancellations);

        coordinator.FailCancellations = false;
        var replay = await runtime.CancelRunAsync(run.Id);

        Assert.True(replay!.CancellationRequested);
        Assert.Equal(run.Id, Assert.Single(coordinator.Cancellations));
        Assert.Equal(0, (await store.GetRuntimeStatusAsync()).PendingCancellationDispatches);
    }

    [Fact]
    public async Task AggregateReconciler_DrainsEveryDurableDeliveryPlane()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var coordinator = new RecordingCoordinatorClient(store)
        {
            FailStarts = true,
            FailSignals = true,
            FailCancellations = true
        };
        var runtime = CreateRuntime(store, coordinator);
        runtime.RegisterHandler(new TestHandler());

        var run = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = TestHandler.HandlerId });
        _ = await runtime.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = run.Id,
            Name = "approved"
        });
        _ = await runtime.CancelRunAsync(run.Id);
        var pending = await store.GetRuntimeStatusAsync();
        Assert.Equal(1, pending.PendingStartDispatches);
        Assert.Equal(1, pending.PendingSignalDispatches);
        Assert.Equal(1, pending.PendingCancellationDispatches);

        coordinator.FailStarts = false;
        coordinator.FailSignals = false;
        coordinator.FailCancellations = false;
        var result = await new TemporalExecutionOutboxReconciler(
            store,
            coordinator,
            runtime.Options).ReconcileAsync();

        Assert.Equal(3, result.Examined);
        Assert.Equal(3, result.Delivered);
        Assert.Equal(0, result.Failed);
        Assert.Equal(1, result.Starts.Delivered);
        Assert.Equal(1, result.Signals.Delivered);
        Assert.Equal(1, result.Cancellations.Delivered);
        var drained = await store.GetRuntimeStatusAsync();
        Assert.Equal(0, drained.PendingStartDispatches);
        Assert.Equal(0, drained.PendingSignalDispatches);
        Assert.Equal(0, drained.PendingCancellationDispatches);
    }

    [Fact]
    public async Task Preflight_ReportsRedactedChecksAndCleansItsObjectProbe()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var objects = new InMemoryObjectStore();
        var options = new TemporalExecutionOptions
        {
            TargetHost = "temporal.example.invalid:7233",
            Namespace = "private-namespace",
            TaskQueue = "private-workers",
            WorkerDeploymentName = "private-deployment",
            WorkerBuildId = "private-build",
            AdapterNamespace = "preflight",
            ArtifactObjectContainer = "vyral-preflight"
        };
        var preflight = new TemporalExecutionPreflight(
            _ => Task.FromResult(true),
            store,
            objects,
            options,
            _ => Task.FromResult<TemporalWorkerPollerStatus?>(new(1, 2)));

        var result = await preflight.RunAsync();

        Assert.True(result.Ready);
        Assert.Equal(0, result.BlockerCount);
        Assert.Equal(0, result.WarningCount);
        Assert.Contains(result.Checks, item => item.Code == "temporal.namespace_reachable");
        Assert.Contains(result.Checks, item => item.Code == "worker.pollers_active");
        Assert.Contains(result.Checks, item => item.Code == "worker.current_build_pollers_active");
        Assert.Contains(result.Checks, item => item.Code == "worker.versioning_mode_consistent");
        Assert.Contains(result.Checks, item => item.Code == "projection.schema_supported");
        Assert.Contains(result.Checks, item => item.Code == "projection.coordinators_consistent");
        Assert.Contains(result.Checks, item => item.Code == "projection.coordinator_check_complete");
        Assert.Contains(result.Checks, item => item.Code == "object_store.cleanup_passed");
        Assert.Equal("1", result.Details["workflowPollers"]);
        Assert.Equal("2", result.Details["activityPollers"]);
        Assert.Equal("1", result.Details["workflowCurrentBuildPollers"]);
        Assert.Equal("2", result.Details["activityCurrentBuildPollers"]);
        Assert.Equal("unversioned", result.Details["workerVersioningMode"]);
        Assert.Equal("replay_compatible", result.Details["workerCompatibilityPolicy"]);
        Assert.Equal("Vyral.RunCoordinator.v1", result.Details["coordinatorWorkflowType"]);
        Assert.Equal("0", result.Details["activeCoordinators"]);
        Assert.Equal("0", result.Details["staleRuns"]);
        Assert.Empty(objects.Objects);
        Assert.Equal("prototype_unqualified", result.Qualification);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(options.TargetHost, json, StringComparison.Ordinal);
        Assert.DoesNotContain(options.Namespace, json, StringComparison.Ordinal);
        Assert.DoesNotContain(options.TaskQueue, json, StringComparison.Ordinal);
        Assert.DoesNotContain(options.WorkerDeploymentName, json, StringComparison.Ordinal);
        Assert.DoesNotContain(options.WorkerBuildId, json, StringComparison.Ordinal);

        var unavailable = await new TemporalExecutionPreflight(
            _ => Task.FromResult(false),
            store,
            objects,
            options).RunAsync();
        Assert.False(unavailable.Ready);
        Assert.Contains(unavailable.Checks, item => item.Code == "temporal.workflow_service_unhealthy");
        Assert.Empty(objects.Objects);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task Preflight_WarnsWhenARequiredWorkerPollerIsMissing(
        int workflowPollers,
        int activityPollers)
    {
        var options = new TemporalExecutionOptions
        {
            TargetHost = "temporal.example.invalid:7233",
            Namespace = "private-namespace",
            TaskQueue = "private-workers",
            AdapterNamespace = "preflight",
            ArtifactObjectContainer = "vyral-preflight"
        };
        var result = await new TemporalExecutionPreflight(
            _ => Task.FromResult(true),
            new InMemoryTemporalRuntimeStore(),
            new InMemoryObjectStore(),
            options,
            _ => Task.FromResult<TemporalWorkerPollerStatus?>(
                new(workflowPollers, activityPollers))).RunAsync();

        Assert.True(result.Ready);
        Assert.Equal(2, result.WarningCount);
        Assert.Contains(result.Checks, item => item.Code == "worker.pollers_missing");
        Assert.Contains(result.Checks, item => item.Code == "worker.current_build_pollers_missing");
        Assert.Equal(workflowPollers.ToString(), result.Details["workflowPollers"]);
        Assert.Equal(activityPollers.ToString(), result.Details["activityPollers"]);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(options.Namespace, json, StringComparison.Ordinal);
        Assert.DoesNotContain(options.TaskQueue, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Preflight_CountsOnlyFreshWorkerPollers()
    {
        var nowUtc = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var pollers = new[]
        {
            new PollerInfo { LastAccessTime = Timestamp.FromDateTime(nowUtc.AddSeconds(-30)) },
            new PollerInfo { LastAccessTime = Timestamp.FromDateTime(nowUtc.AddMinutes(-3)) },
            new PollerInfo()
        };

        Assert.Equal(1, TemporalExecutionPreflight.CountFreshPollers(pollers, nowUtc));
    }

    [Fact]
    public void Preflight_ClassifiesFreshWorkerBuildsWithoutReturningTheirIdentities()
    {
        var nowUtc = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var expected = new TemporalWorkerDeploymentDescriptor("vyral-workers", "current-build");
        var pollers = new[]
        {
            Poller("vyral-workers", "current-build", WorkerVersioningMode.Unversioned, nowUtc),
            Poller("vyral-workers", "older-build", WorkerVersioningMode.Unversioned, nowUtc),
            Poller("other-workers", "routed-build", WorkerVersioningMode.Versioned, nowUtc),
            new PollerInfo { LastAccessTime = Timestamp.FromDateTime(nowUtc) },
            Poller("vyral-workers", "stale-build", WorkerVersioningMode.Unversioned,
                nowUtc.AddMinutes(-3))
        };

        var status = TemporalWorkerCompatibility.Summarize(
            pollers,
            expected,
            nowUtc,
            TimeSpan.FromMinutes(2));

        Assert.True(status.CompatibilityProbed);
        Assert.Equal(4, status.FreshPollers);
        Assert.Equal(1, status.CurrentBuildPollers);
        Assert.Equal(1, status.OtherBuildPollers);
        Assert.Equal(1, status.VersionedPollers);
        Assert.Equal(1, status.UnattributedPollers);
        Assert.Equal(3, status.DistinctBuilds);
        Assert.DoesNotContain("current-build", JsonSerializer.Serialize(status), StringComparison.Ordinal);
        Assert.DoesNotContain("older-build", JsonSerializer.Serialize(status), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_ReportsMixedAndUnattributedWorkerCompatibilityAsWarnings()
    {
        var options = new TemporalExecutionOptions
        {
            TargetHost = "temporal.example.invalid:7233",
            Namespace = "private-namespace",
            TaskQueue = "private-workers",
            WorkerDeploymentName = "private-deployment",
            WorkerBuildId = "private-build",
            AdapterNamespace = "preflight",
            ArtifactObjectContainer = "vyral-preflight"
        };
        var workflow = new TemporalWorkerPollerQueueStatus(
            FreshPollers: 4,
            CurrentBuildPollers: 1,
            OtherBuildPollers: 1,
            UnattributedPollers: 1,
            VersionedPollers: 1,
            DistinctBuilds: 3,
            CompatibilityProbed: true);
        var activity = new TemporalWorkerPollerQueueStatus(
            FreshPollers: 2,
            CurrentBuildPollers: 1,
            OtherBuildPollers: 1,
            DistinctBuilds: 2,
            CompatibilityProbed: true);

        var result = await new TemporalExecutionPreflight(
            _ => Task.FromResult(true),
            new InMemoryTemporalRuntimeStore(),
            new InMemoryObjectStore(),
            options,
            _ => Task.FromResult<TemporalWorkerPollerStatus?>(new(workflow, activity))).RunAsync();

        Assert.True(result.Ready);
        Assert.Equal(3, result.WarningCount);
        Assert.Contains(result.Checks, item => item.Code == "worker.mixed_builds_observed");
        Assert.Contains(result.Checks, item => item.Code == "worker.compatibility_metadata_missing");
        Assert.Contains(result.Checks, item => item.Code == "worker.unexpected_versioned_pollers");
        Assert.Contains(result.Checks, item => item.Code == "worker.current_build_pollers_active");
        Assert.Equal("3", result.Details["workflowDistinctBuilds"]);
        Assert.Equal("1", result.Details["workflowVersionedPollers"]);
        Assert.Equal("1", result.Details["workflowUnattributedPollers"]);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(options.WorkerDeploymentName, json, StringComparison.Ordinal);
        Assert.DoesNotContain(options.WorkerBuildId, json, StringComparison.Ordinal);
    }

    private static PollerInfo Poller(
        string deploymentName,
        string buildId,
        WorkerVersioningMode mode,
        DateTime lastAccessUtc) => new()
    {
        LastAccessTime = Timestamp.FromDateTime(lastAccessUtc),
        DeploymentOptions = new TemporalDeploymentOptions
        {
            DeploymentName = deploymentName,
            BuildId = buildId,
            WorkerVersioningMode = mode
        }
    };

    [Fact]
    public async Task Preflight_WarnsWhenProjectionHasAClosedOrMissingCoordinator()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var runtime = CreateRuntime(store, new RecordingCoordinatorClient(store));
        runtime.RegisterHandler(new TestHandler());
        var run = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = TestHandler.HandlerId
        });
        IReadOnlyList<TemporalActiveCoordinator>? examined = null;
        var result = await new TemporalExecutionPreflight(
            _ => Task.FromResult(true),
            store,
            new InMemoryObjectStore(),
            runtime.Options,
            _ => Task.FromResult<TemporalWorkerPollerStatus?>(new(1, 1)),
            (coordinators, _) =>
            {
                examined = coordinators;
                return Task.FromResult<IReadOnlyList<TemporalActiveCoordinator>>(coordinators);
            }).RunAsync();

        var coordinator = Assert.Single(examined!);
        Assert.Equal(TemporalExecutionIdentity.CreateWorkflowId(runtime.Options.AdapterNamespace, run.Id),
            coordinator.WorkflowId);
        Assert.True(result.Ready);
        Assert.Equal(1, result.WarningCount);
        Assert.Contains(result.Checks, item => item.Code == "projection.stale_coordinators_detected");
        Assert.Contains(result.Checks, item => item.Code == "projection.coordinator_check_complete");
        Assert.Equal("1", result.Details["activeCoordinators"]);
        Assert.Equal("1", result.Details["coordinatorsExamined"]);
        Assert.Equal("1", result.Details["staleRunsDetected"]);
        Assert.Equal("1", result.Details["staleRuns"]);
        Assert.DoesNotContain(coordinator.WorkflowId, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_DeclaresPartialCoordinatorCoverageWithoutInventingAStaleRunTotal()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var runtime = CreateRuntime(store, new RecordingCoordinatorClient(store));
        runtime.RegisterHandler(new TestHandler());
        for (var index = 0; index < 26; index++)
        {
            _ = await runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = TestHandler.HandlerId,
                IdempotencyKey = $"preflight-coverage-{index}"
            });
        }
        var result = await new TemporalExecutionPreflight(
            _ => Task.FromResult(true),
            store,
            new InMemoryObjectStore(),
            runtime.Options,
            _ => Task.FromResult<TemporalWorkerPollerStatus?>(new(1, 1)),
            (coordinators, _) =>
            {
                Assert.Equal(25, coordinators.Count);
                return Task.FromResult<IReadOnlyList<TemporalActiveCoordinator>>([]);
            }).RunAsync();

        Assert.True(result.Ready);
        Assert.Equal(1, result.WarningCount);
        Assert.Contains(result.Checks, item => item.Code == "projection.coordinator_check_partial");
        Assert.Equal("26", result.Details["activeCoordinators"]);
        Assert.Equal("25", result.Details["coordinatorsExamined"]);
        Assert.Equal("0", result.Details["staleRunsDetected"]);
        Assert.False(result.Details.ContainsKey("staleRuns"));
    }

    [Fact]
    public async Task Preflight_DoesNotReportAStaleRunThatCompletedDuringTheProbe()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var runtime = CreateRuntime(store, new RecordingCoordinatorClient(store));
        runtime.RegisterHandler(new TestHandler());
        var run = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = TestHandler.HandlerId
        });
        var result = await new TemporalExecutionPreflight(
            _ => Task.FromResult(true),
            store,
            new InMemoryObjectStore(),
            runtime.Options,
            _ => Task.FromResult<TemporalWorkerPollerStatus?>(new(1, 1)),
            async (coordinators, ct) =>
            {
                await store.ProjectCancellationAsync(new TemporalExecutionCancellation
                {
                    RunId = run.Id,
                    Generation = 1
                }, ct);
                return coordinators;
            }).RunAsync();

        Assert.True(result.Ready);
        Assert.Equal(0, result.WarningCount);
        Assert.Contains(result.Checks, item => item.Code == "projection.coordinators_consistent");
        Assert.Equal("0", result.Details["staleRunsDetected"]);
        Assert.Equal("0", result.Details["staleRuns"]);
    }

    [Fact]
    public async Task Runtime_StatusDisclosesPrototypeQualificationWithoutProviderSecrets()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var coordinator = new RecordingCoordinatorClient(store);
        var runtime = CreateRuntime(store, coordinator);

        var status = await runtime.GetAdapterStatusAsync();

        Assert.True(status.Available);
        Assert.Equal("prototype_unqualified", status.Details!["qualification"]!.GetValue<string>());
        Assert.Equal(0, status.Details["activeCoordinators"]!.GetValue<int>());
        ExecutionContractValidator.ValidateAdapterDescriptor(status.Adapter);
        Assert.Equal(ExecutionResumePolicyBehaviors.MayReexecuteHandler, status.ResumePolicy!.InterruptedRunningBehavior);
        var json = JsonSerializer.Serialize(status);
        Assert.DoesNotContain("127.0.0.1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vyral-workers", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandardAttemptHandler_ExecutesPortableContextAndPersistsEvidence()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var coordinator = new RecordingCoordinatorClient(store);
        var runtime = CreateRuntime(store, coordinator);
        runtime.RegisterHandler(new ContextUsingHandler());
        var run = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = ContextUsingHandler.HandlerId,
            Payload = new JsonObject { ["work"] = 3 }
        });
        var handler = new TemporalExecutionAttemptHandler(
            runtime,
            store,
            runtime.Options,
            "temporal-test-worker");

        var outcome = await handler.ExecuteAttemptAsync(new TemporalExecutionAttemptRequest
        {
            RunId = run.Id,
            Generation = 1,
            Attempt = 1
        });

        Assert.Equal(TemporalAttemptDispositions.Completed, outcome.Disposition);
        Assert.Equal(ExecutionRunStatuses.Succeeded, outcome.TerminalStatus);
        var completed = await runtime.GetRunAsync(run.Id);
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed!.Status);
        Assert.Equal(0.5, completed.Progress);
        Assert.Equal("portable-context", completed.CurrentStep);
        Assert.Equal("ok", completed.Result!["result"]!.GetValue<string>());

        var artifact = Assert.Single(await runtime.ListArtifactsAsync(run.Id));
        Assert.Equal("summary.json", artifact.Name);
        Assert.StartsWith("sha256:", artifact.ContentHash, StringComparison.Ordinal);
        Assert.NotNull(await runtime.GetCheckpointAsync(run.Id, "cursor"));

        var history = await runtime.GetHistoryAsync(run.Id, new ExecutionHistoryQuery { Limit = 100 });
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunStarted);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunStatus);
        Assert.Contains(history, item => item.Type == "handler.observed");
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.ArtifactWritten);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.CheckpointWritten);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunCompleted);
        Assert.All(history, item =>
        {
            Assert.Equal(run.Id, item.Context["runId"]);
            Assert.Equal(ContextUsingHandler.HandlerId, item.Context["handlerId"]);
            Assert.Equal("temporal-test-worker", item.Context["workerId"]);
            Assert.Equal(TemporalExecutionRuntimeKindIds.Temporal, item.Context["runtimeKind"]);
        });
    }

    [Fact]
    public async Task StandardAttemptHandler_ProjectsPortableRetryBeforeSecondAttempt()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var coordinator = new RecordingCoordinatorClient(store);
        var runtime = CreateRuntime(store, coordinator);
        runtime.RegisterHandler(new RetryThenSucceedHandler());
        var run = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = RetryThenSucceedHandler.HandlerId,
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = 2,
                InitialDelaySeconds = 0.01,
                MaxDelaySeconds = 0.01,
                BackoffMultiplier = 1
            }
        });
        var handler = new TemporalExecutionAttemptHandler(
            runtime,
            store,
            runtime.Options,
            "temporal-test-worker");

        var first = await handler.ExecuteAttemptAsync(new TemporalExecutionAttemptRequest
        {
            RunId = run.Id,
            Generation = 1,
            Attempt = 1
        });
        var transportReplay = await handler.ExecuteAttemptAsync(new TemporalExecutionAttemptRequest
        {
            RunId = run.Id,
            Generation = 1,
            Attempt = 1
        });
        var second = await handler.ExecuteAttemptAsync(new TemporalExecutionAttemptRequest
        {
            RunId = run.Id,
            Generation = 1,
            Attempt = 2
        });

        Assert.Equal(TemporalAttemptDispositions.Retryable, first.Disposition);
        Assert.Equal(TemporalAttemptDispositions.Retryable, transportReplay.Disposition);
        Assert.Equal(2, ((RetryThenSucceedHandler)runtime.ResolveHandler(RetryThenSucceedHandler.HandlerId)!).Calls);
        Assert.InRange(first.RetryDelayMilliseconds!.Value, 1, 100);
        Assert.Equal(TemporalAttemptDispositions.Completed, second.Disposition);
        var completed = await runtime.GetRunAsync(run.Id);
        Assert.Equal(2, completed!.Attempt);
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
        Assert.Contains(
            await runtime.GetHistoryAsync(run.Id, new ExecutionHistoryQuery { Limit = 100 }),
            item => item.Type == ExecutionEventTypes.RetryScheduled);
    }

    [Fact]
    public async Task StandardAttemptHandler_SuspendsAndConsumesDurableTimerOutcome()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var coordinator = new RecordingCoordinatorClient(store);
        var runtime = CreateRuntime(store, coordinator);
        runtime.RegisterHandler(new TimerWaitingHandler());
        var run = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = TimerWaitingHandler.HandlerId
        });
        var handler = new TemporalExecutionAttemptHandler(
            runtime,
            store,
            runtime.Options,
            "temporal-test-worker");

        var first = await handler.ExecuteAttemptAsync(new TemporalExecutionAttemptRequest
        {
            RunId = run.Id,
            Generation = 1,
            Attempt = 1
        });
        Assert.Equal(TemporalAttemptDispositions.Suspended, first.Disposition);
        Assert.Equal(TemporalWaitKinds.Timer, first.WaitKind);
        Assert.NotNull(first.ResumeAtUtc);
        var suspendedReplay = await handler.ExecuteAttemptAsync(new TemporalExecutionAttemptRequest
        {
            RunId = run.Id,
            Generation = 1,
            Attempt = 1
        });
        Assert.Equal(first.WaitId, suspendedReplay.WaitId);
        Assert.Equal(1, ((TimerWaitingHandler)runtime.ResolveHandler(TimerWaitingHandler.HandlerId)!).Calls);

        var projected = await store.ProjectWaitResolutionAsync(new TemporalExecutionWaitResolution
        {
            RunId = run.Id,
            Generation = 1,
            WaitId = first.WaitId!,
            Resolution = TemporalWaitResolutions.Timer
        });
        Assert.True(projected.Accepted);
        var claimed = await store.ConsumeWaitResultAsync(
            run.Id,
            1,
            2,
            TemporalWaitKinds.Timer,
            "settle");
        var replayedClaim = await store.ConsumeWaitResultAsync(
            run.Id,
            1,
            2,
            TemporalWaitKinds.Timer,
            "settle");
        Assert.Equal(ExecutionWaitOutcomes.Timer, claimed!.Outcome);
        Assert.Equal(claimed.Outcome, replayedClaim!.Outcome);

        var second = await handler.ExecuteAttemptAsync(new TemporalExecutionAttemptRequest
        {
            RunId = run.Id,
            Generation = 1,
            Attempt = 2
        });

        Assert.Equal(TemporalAttemptDispositions.Completed, second.Disposition);
        var completed = await runtime.GetRunAsync(run.Id);
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed!.Status);
        Assert.Equal(ExecutionWaitOutcomes.Timer, completed.Result!["outcome"]!.GetValue<string>());
        Assert.Null(await store.ConsumeWaitResultAsync(
            run.Id,
            1,
            2,
            TemporalWaitKinds.Timer,
            "settle"));
        var history = await runtime.GetHistoryAsync(run.Id, new ExecutionHistoryQuery { Limit = 100 });
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitRegistered);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitResumed);
    }

    [Fact]
    public async Task StandardAttemptHandler_OffloadsLargeArtifactBodyAndProjectsOnlyReference()
    {
        var store = new InMemoryTemporalRuntimeStore();
        var coordinator = new RecordingCoordinatorClient(store);
        var options = new TemporalExecutionOptions
        {
            TargetHost = "127.0.0.1:7233",
            TaskQueue = "vyral-workers",
            AdapterNamespace = "tests",
            ArtifactObjectContainer = "vyral-temporal-tests",
            RequireTls = false,
            Limits = new ExecutionRuntimeLimits
            {
                MaxArtifactBytes = 1_024,
                MaxArtifactInlineBytes = 16
            }
        };
        var runtime = new TemporalExecutionRuntimeAdapter(store, coordinator, options);
        runtime.RegisterHandler(new LargeArtifactHandler());
        var run = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = LargeArtifactHandler.HandlerId
        });
        var objects = new InMemoryObjectStore();
        var handler = new TemporalExecutionAttemptHandler(
            runtime,
            store,
            options,
            "temporal-test-worker",
            objects);

        var outcome = await handler.ExecuteAttemptAsync(new TemporalExecutionAttemptRequest
        {
            RunId = run.Id,
            Generation = 1,
            Attempt = 1
        });

        Assert.Equal(TemporalAttemptDispositions.Completed, outcome.Disposition);
        var artifact = Assert.Single(await runtime.ListArtifactsAsync(run.Id));
        Assert.Null(artifact.Text);
        Assert.Null(artifact.Content);
        Assert.StartsWith("vyral-object://vyral-temporal-tests/execution-artifacts/", artifact.Uri, StringComparison.Ordinal);
        Assert.Equal("object-store", artifact.Metadata["storage"]);
        Assert.Equal("true", artifact.Metadata["offloaded"]);
        var stored = Assert.Single(objects.Objects);
        Assert.Equal(64, stored.Value.Content.Length);
        Assert.DoesNotContain(new string('x', 64), JsonSerializer.Serialize(artifact), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandardAttemptHandler_ReplaysArtifactAfterCompletionWriteFailureWithoutDuplicateObject()
    {
        var store = new InMemoryTemporalRuntimeStore { FailNextCompletion = true };
        var coordinator = new RecordingCoordinatorClient(store);
        var options = new TemporalExecutionOptions
        {
            TargetHost = "127.0.0.1:7233",
            TaskQueue = "vyral-workers",
            AdapterNamespace = "tests",
            ArtifactObjectContainer = "vyral-temporal-tests",
            RequireTls = false,
            Limits = new ExecutionRuntimeLimits
            {
                MaxArtifactBytes = 1_024,
                MaxArtifactInlineBytes = 16
            }
        };
        var runtime = new TemporalExecutionRuntimeAdapter(store, coordinator, options);
        runtime.RegisterHandler(new LargeArtifactHandler());
        var run = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = LargeArtifactHandler.HandlerId
        });
        var objects = new InMemoryObjectStore();
        var handler = new TemporalExecutionAttemptHandler(
            runtime,
            store,
            options,
            "temporal-test-worker",
            objects);
        var request = new TemporalExecutionAttemptRequest
        {
            RunId = run.Id,
            Generation = 1,
            Attempt = 1
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAttemptAsync(request));
        Assert.Single(objects.Objects);
        Assert.Single(await runtime.ListArtifactsAsync(run.Id));

        var replay = await handler.ExecuteAttemptAsync(request);

        Assert.Equal(TemporalAttemptDispositions.Completed, replay.Disposition);
        Assert.Single(objects.Objects);
        Assert.Single(await runtime.ListArtifactsAsync(run.Id));
    }

    private static TemporalExecutionRuntimeAdapter CreateRuntime(
        InMemoryTemporalRuntimeStore store,
        RecordingCoordinatorClient coordinator) => new(
        store,
        coordinator,
        new TemporalExecutionOptions
        {
            TargetHost = "127.0.0.1:7233",
            TaskQueue = "vyral-workers",
            AdapterNamespace = "tests",
            RequireTls = false
        });

    private sealed class TestHandler : IExecutionHandler
    {
        public const string HandlerId = "test.temporal";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal test handler"
        };

        public Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default) =>
            Task.FromResult(ExecutionRunResult.Succeeded());
    }

    private sealed class ContextUsingHandler : IExecutionHandler
    {
        public const string HandlerId = "test.temporal.context";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal context test handler"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default)
        {
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Status = ExecutionRunStatuses.Running,
                Progress = 0.5,
                CurrentStep = "portable-context"
            }, ct);
            await context.RecordEventAsync(
                "handler.observed",
                "Handler observed its portable context.",
                details: new JsonObject { ["portable"] = true },
                ct: ct);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "summary.json",
                Content = new JsonObject { ["value"] = 3 }
            }, ct);
            await context.PutCheckpointAsync(new ExecutionCheckpointWrite
            {
                Key = "cursor",
                Content = new JsonObject { ["offset"] = 3 }
            }, ct);
            return ExecutionRunResult.Succeeded(new JsonObject { ["result"] = "ok" });
        }
    }

    private sealed class RetryThenSucceedHandler : IExecutionHandler
    {
        public const string HandlerId = "test.temporal.retry";
        private int _calls;

        public int Calls => _calls;

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal retry test handler",
            MaxAttempts = 2
        };

        public Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default) => Task.FromResult(
                Interlocked.Increment(ref _calls) == 1
                    ? ExecutionRunResult.Failed(ExecutionFailureClasses.Transient, "retry me")
                    : ExecutionRunResult.Succeeded());
    }

    private sealed class TimerWaitingHandler : IExecutionHandler
    {
        public const string HandlerId = "test.temporal.timer";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal timer test handler"
        };

        public int Calls { get; private set; }

        public async Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default)
        {
            Calls++;
            var outcome = await context.WaitForTimerAsync(
                "settle",
                DateTime.UtcNow.AddMinutes(1),
                new JsonObject { ["reason"] = "test" },
                ct);
            return ExecutionRunResult.Succeeded(new JsonObject { ["outcome"] = outcome.Outcome });
        }
    }

    private sealed class LargeArtifactHandler : IExecutionHandler
    {
        public const string HandlerId = "test.temporal.large-artifact";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal large artifact test handler"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default)
        {
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "large.txt",
                Kind = ExecutionArtifactKinds.Text,
                MediaType = "text/plain",
                Text = new string('x', 64)
            }, ct);
            return ExecutionRunResult.Succeeded();
        }
    }

    private sealed class InMemoryObjectStore : IObjectStore
    {
        public Dictionary<(string Container, string Key), (byte[] Content, ObjectInfo Info)> Objects { get; } = [];

        public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
        {
            await using var buffer = new MemoryStream();
            await request.Content.CopyToAsync(buffer, ct);
            var content = buffer.ToArray();
            var info = new ObjectInfo
            {
                Container = request.Container,
                Key = request.Key,
                ContentType = request.ContentType,
                ContentLength = content.LongLength,
                Etag = $"etag-{content.Length}",
                ContentHash = "sha256:test",
                Metadata = request.Metadata is null
                    ? []
                    : new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal),
                UpdatedAt = DateTime.UtcNow
            };
            if (request.IfNoneMatch == "*" && Objects.ContainsKey((request.Container, request.Key)))
                throw new InvalidOperationException("Object already exists.");
            Objects[(request.Container, request.Key)] = (content, info);
            return info;
        }

        public Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default)
        {
            if (!Objects.TryGetValue((request.Container, request.Key), out var stored))
                return Task.FromResult<ObjectResult?>(null);
            return Task.FromResult<ObjectResult?>(new ObjectResult
            {
                Container = stored.Info.Container,
                Key = stored.Info.Key,
                ContentType = stored.Info.ContentType,
                ContentLength = stored.Info.ContentLength,
                Etag = stored.Info.Etag,
                ContentHash = stored.Info.ContentHash,
                Metadata = new Dictionary<string, string>(stored.Info.Metadata, StringComparer.Ordinal),
                UpdatedAt = stored.Info.UpdatedAt,
                Content = new MemoryStream(stored.Content, writable: false)
            });
        }

        public Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default)
        {
            Objects.Remove((request.Container, request.Key));
            return Task.CompletedTask;
        }

        public Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ObjectListResult
            {
                Items = Objects
                    .Where(item => item.Key.Container == request.Container)
                    .Select(item => item.Value.Info)
                    .ToList()
            });
    }

    private sealed class RecordingCoordinatorClient(InMemoryTemporalRuntimeStore store) : ITemporalCoordinatorClient
    {
        public List<TemporalStartDispatch> Starts { get; } = [];
        public List<TemporalSignalDispatch> Signals { get; } = [];
        public List<string> Cancellations { get; } = [];
        public bool CancellationObservedDurableFlag { get; private set; }
        public bool FailStarts { get; set; }
        public bool FailSignals { get; set; }
        public bool FailCancellations { get; set; }

        public Task<TemporalCoordinationReference> StartAsync(
            TemporalStartDispatch dispatch,
            CancellationToken ct = default)
        {
            if (FailStarts)
                throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.Unavailable);
            Starts.Add(dispatch);
            return Task.FromResult(new TemporalCoordinationReference
            {
                WorkflowId = dispatch.WorkflowId,
                TemporalRunId = $"temporal-{dispatch.RunId}",
                Generation = dispatch.Generation
            });
        }

        public Task<TemporalWorkflowIdentity?> GetIdentityAsync(string workflowId, CancellationToken ct = default) =>
            Task.FromResult<TemporalWorkflowIdentity?>(null);

        public Task SignalExternalEventAsync(TemporalSignalDispatch dispatch, CancellationToken ct = default)
        {
            if (FailSignals)
                throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.Unavailable);
            Signals.Add(dispatch);
            return Task.CompletedTask;
        }

        public async Task RequestCancellationAsync(string workflowId, CancellationToken ct = default)
        {
            if (FailCancellations)
                throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.Unavailable);
            var runId = workflowId.Split(':')[^1];
            Cancellations.Add(runId);
            CancellationObservedDurableFlag = (await store.GetRunAsync(runId, ct: ct))?.CancellationRequested == true;
        }
    }

    private sealed class InMemoryTemporalRuntimeStore : ITemporalExecutionRuntimeStore
    {
        private readonly Dictionary<string, ExecutionRun> _runs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Hash, string RunId)> _idempotency = new(StringComparer.Ordinal);
        private readonly List<TemporalStartDispatch> _starts = [];
        private readonly List<TemporalSignalDispatch> _signals = [];
        private readonly List<TemporalCancellationDispatch> _cancellations = [];
        private readonly Dictionary<string, TemporalActiveCoordinator> _coordinators = new(StringComparer.Ordinal);
        private readonly List<ExecutionTraceEvent> _history = [];
        private readonly List<ExecutionArtifact> _artifacts = [];
        private readonly Dictionary<(string RunId, string Key), ExecutionCheckpoint> _checkpoints = [];
        private readonly Dictionary<string, TemporalProjectionWaitRegistration> _waits = new(StringComparer.Ordinal);
        private readonly Dictionary<(string RunId, string Kind, string Name), ExecutionWaitResult> _waitResults = [];
        private readonly Dictionary<(string RunId, string Kind, string Name), int> _waitResultClaims = [];
        private readonly List<ExecutionTimer> _timers = [];

        public bool FailNextCompletion { get; set; }

        public Task<TemporalProjectionRunStartResult> CreateRunWithPendingStartAsync(
            TemporalProjectionRunStart start,
            CancellationToken ct = default)
        {
            var replay = Reserve(start.Run, start.RequestHash);
            if (!replay)
            {
                _starts.Add(new TemporalStartDispatch
                {
                    DispatchId = start.DispatchId,
                    RunId = start.Run.Id,
                    WorkflowId = start.WorkflowId,
                    ProjectionRevision = start.ProjectionRevision,
                    Generation = start.Generation,
                    AttemptCount = 0
                });
            }
            var run = Resolve(start.Run, start.RequestHash);
            return Task.FromResult(new TemporalProjectionRunStartResult
            {
                Run = Clone(run),
                DispatchId = replay
                    ? _starts.FirstOrDefault(item => item.RunId == run.Id)?.DispatchId ?? start.DispatchId
                    : start.DispatchId,
                Replayed = replay
            });
        }

        public Task<TemporalProjectionRunCreationResult> CreateRunWithoutPendingStartAsync(
            TemporalProjectionRunCreation creation,
            CancellationToken ct = default)
        {
            var replay = Reserve(creation.Run, creation.RequestHash);
            return Task.FromResult(new TemporalProjectionRunCreationResult
            {
                Run = Clone(Resolve(creation.Run, creation.RequestHash)),
                Replayed = replay
            });
        }

        public Task<ExecutionRun> BeginAttemptAsync(
            TemporalExecutionAttemptRequest request,
            ExecutionTraceEvent startedEvent,
            CancellationToken ct = default)
        {
            var run = _runs[request.RunId];
            run.Status = ExecutionRunStatuses.Running;
            run.Attempt = request.Attempt;
            run.StartedAtUtc ??= DateTime.UtcNow;
            run.UpdatedAtUtc = DateTime.UtcNow;
            _history.Add(Clone(startedEvent));
            return Task.FromResult(Clone(run));
        }

        public Task<TemporalExecutionAttemptOutcome?> GetAttemptOutcomeAsync(
            TemporalExecutionAttemptRequest request,
            CancellationToken ct = default)
        {
            if (!_runs.TryGetValue(request.RunId, out var run) || run.Attempt != request.Attempt)
                return Task.FromResult<TemporalExecutionAttemptOutcome?>(null);
            if (ExecutionRunStatuses.IsTerminal(run.Status))
            {
                return Task.FromResult<TemporalExecutionAttemptOutcome?>(new TemporalExecutionAttemptOutcome
                {
                    Disposition = run.Status == ExecutionRunStatuses.Succeeded
                        ? TemporalAttemptDispositions.Completed
                        : TemporalAttemptDispositions.Terminal,
                    TerminalStatus = run.Status
                });
            }
            if (run.Status == ExecutionRunStatuses.Waiting &&
                _waits.Values.FirstOrDefault(wait => wait.RunId == run.Id) is { } wait)
            {
                return Task.FromResult<TemporalExecutionAttemptOutcome?>(new TemporalExecutionAttemptOutcome
                {
                    Disposition = TemporalAttemptDispositions.Suspended,
                    WaitId = wait.WaitId,
                    WaitKind = wait.Kind,
                    ResumeAtUtc = wait.ResumeAtUtc
                });
            }
            if (run.Status == ExecutionRunStatuses.Waiting && run.ScheduledAtUtc.HasValue)
            {
                return Task.FromResult<TemporalExecutionAttemptOutcome?>(new TemporalExecutionAttemptOutcome
                {
                    Disposition = TemporalAttemptDispositions.Retryable,
                    RetryDelayMilliseconds = Math.Clamp(
                        (int)Math.Ceiling((run.ScheduledAtUtc.Value - DateTime.UtcNow).TotalMilliseconds),
                        1,
                        86_400_000)
                });
            }
            return Task.FromResult<TemporalExecutionAttemptOutcome?>(null);
        }

        public Task<ExecutionRun> ReportRunAsync(
            string runId,
            int generation,
            ExecutionRunUpdate update,
            ExecutionTraceEvent statusEvent,
            CancellationToken ct = default)
        {
            var run = _runs[runId];
            run.Requested = update.Requested ?? run.Requested;
            run.Attempted = update.Attempted ?? run.Attempted;
            run.Succeeded = update.Succeeded ?? run.Succeeded;
            run.Failed = update.Failed ?? run.Failed;
            run.Progress = update.Progress ?? run.Progress;
            run.CurrentStep = update.CurrentStep ?? run.CurrentStep;
            run.Result = update.Result?.DeepClone() ?? run.Result;
            run.StatusDetails = update.StatusDetails?.DeepClone() as JsonObject ?? run.StatusDetails;
            run.UpdatedAtUtc = DateTime.UtcNow;
            _history.Add(Clone(statusEvent));
            return Task.FromResult(Clone(run));
        }

        public Task RecordHistoryAsync(ExecutionTraceEvent traceEvent, CancellationToken ct = default)
        {
            _history.Add(Clone(traceEvent));
            return Task.CompletedTask;
        }

        public Task<ExecutionArtifact> PutArtifactMetadataAsync(
            string runId,
            int generation,
            ExecutionArtifact artifact,
            ExecutionTraceEvent writtenEvent,
            CancellationToken ct = default)
        {
            var existing = _artifacts.FirstOrDefault(item =>
                item.RunId == runId && item.Name == artifact.Name);
            if (existing is not null)
            {
                if (existing.ContentHash != artifact.ContentHash || existing.Kind != artifact.Kind ||
                    existing.MediaType != artifact.MediaType)
                {
                    throw new InvalidOperationException("Temporal artifact name conflicts with existing metadata.");
                }
                return Task.FromResult(Clone(existing));
            }
            _artifacts.Add(Clone(artifact));
            _history.Add(Clone(writtenEvent));
            return Task.FromResult(Clone(artifact));
        }

        public Task<ExecutionCheckpoint> PutCheckpointAsync(
            string runId,
            int generation,
            ExecutionCheckpoint checkpoint,
            ExecutionTraceEvent writtenEvent,
            CancellationToken ct = default)
        {
            _checkpoints[(runId, checkpoint.Key)] = Clone(checkpoint);
            _history.Add(Clone(writtenEvent));
            return Task.FromResult(Clone(checkpoint));
        }

        public Task<ExecutionWaitResult?> ConsumeWaitResultAsync(
            string runId,
            int generation,
            int attempt,
            string kind,
            string name,
            CancellationToken ct = default)
        {
            var key = (runId, kind, name);
            if (!_waitResults.TryGetValue(key, out var outcome) ||
                _waitResultClaims.TryGetValue(key, out var claimedAttempt) && claimedAttempt != attempt)
            {
                return Task.FromResult<ExecutionWaitResult?>(null);
            }
            _waitResultClaims[key] = attempt;
            return Task.FromResult<ExecutionWaitResult?>(Clone(outcome));
        }

        public Task<TemporalProjectionAttemptCompletion> CompleteAttemptAsync(
            string runId,
            int generation,
            ExecutionRunResult result,
            ExecutionTraceEvent retryEvent,
            ExecutionTraceEvent terminalEvent,
            CancellationToken ct = default)
        {
            if (FailNextCompletion)
            {
                FailNextCompletion = false;
                throw new InvalidOperationException("Injected completion projection failure.");
            }
            var run = _runs[runId];
            ConsumeClaimedWaitResults(runId, run.Attempt);
            if (result.Status is ExecutionRunStatuses.Failed or ExecutionRunStatuses.TimedOut &&
                run.Attempt < run.MaxAttempts)
            {
                var delay = Math.Clamp(
                    (int)Math.Ceiling(run.RetryPolicy.InitialDelaySeconds * 1_000),
                    1,
                    86_400_000);
                run.Status = ExecutionRunStatuses.Waiting;
                run.ScheduledAtUtc = DateTime.UtcNow.AddMilliseconds(delay);
                run.UpdatedAtUtc = DateTime.UtcNow;
                _history.Add(Clone(retryEvent));
                return Task.FromResult(new TemporalProjectionAttemptCompletion
                {
                    Run = Clone(run),
                    RetryDelayMilliseconds = delay
                });
            }
            run.Status = result.Status;
            run.Result = result.Result?.DeepClone();
            run.FailureClass = result.FailureClass;
            run.Error = result.Error;
            run.StatusDetails = result.StatusDetails?.DeepClone() as JsonObject;
            run.CompletedAtUtc = DateTime.UtcNow;
            run.UpdatedAtUtc = run.CompletedAtUtc.Value;
            _history.Add(Clone(terminalEvent));
            return Task.FromResult(new TemporalProjectionAttemptCompletion { Run = Clone(run) });
        }

        public Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default)
        {
            if (!_runs.TryGetValue(runId, out var run)) return Task.FromResult<ExecutionRun?>(null);
            var clone = Clone(run);
            if (!includeResult) clone.Result = null;
            return Task.FromResult<ExecutionRun?>(clone);
        }

        public Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExecutionRun>>(_runs.Values.Select(Clone).Take(query?.Limit ?? 100).ToList());

        public Task<TemporalActiveCoordinatorSnapshot> GetActiveCoordinatorSnapshotAsync(
            int limit,
            CancellationToken ct = default)
        {
            var active = _coordinators
                .Where(item => _runs.TryGetValue(item.Key, out var run) &&
                    !ExecutionRunStatuses.IsTerminal(run.Status))
                .Select(item => item.Value)
                .ToList();
            return Task.FromResult(new TemporalActiveCoordinatorSnapshot
            {
                TotalCount = active.Count,
                Coordinators = active.Take(limit).ToList()
            });
        }

        public Task<bool> IsActiveCoordinatorAsync(
            string workflowId,
            int generation,
            CancellationToken ct = default) =>
            Task.FromResult(_coordinators.Any(item =>
                string.Equals(item.Value.WorkflowId, workflowId, StringComparison.Ordinal) &&
                item.Value.Generation == generation &&
                _runs.TryGetValue(item.Key, out var run) &&
                !ExecutionRunStatuses.IsTerminal(run.Status)));

        public Task<TemporalProjectionCancellationRequestResult> RequestCancellationAsync(
            string runId,
            CancellationToken ct = default)
        {
            if (!_runs.TryGetValue(runId, out var run))
                return Task.FromResult(new TemporalProjectionCancellationRequestResult { NewlyRequested = false });
            var newlyRequested = !run.CancellationRequested && !ExecutionRunStatuses.IsTerminal(run.Status);
            if (newlyRequested)
            {
                run.CancellationRequested = true;
                _cancellations.Add(new TemporalCancellationDispatch
                {
                    DispatchId = $"cancel-{run.Id}",
                    RunId = run.Id,
                    WorkflowId = TemporalExecutionIdentity.CreateWorkflowId("tests", run.Id),
                    Generation = 1,
                    AttemptCount = 0
                });
            }
            return Task.FromResult(new TemporalProjectionCancellationRequestResult
            {
                Run = Clone(run),
                WorkflowId = TemporalExecutionIdentity.CreateWorkflowId("tests", run.Id),
                Generation = 1,
                NewlyRequested = newlyRequested
            });
        }

        public Task<TemporalProjectionExternalEventDispatch> CreateExternalEventWithPendingSignalAsync(
            ExecutionExternalEvent externalEvent,
            string dispatchId,
            CancellationToken ct = default)
        {
            var dispatch = new TemporalSignalDispatch
            {
                DispatchId = dispatchId,
                RunId = externalEvent.RunId!,
                WorkflowId = TemporalExecutionIdentity.CreateWorkflowId("tests", externalEvent.RunId!),
                Generation = 1,
                EventId = externalEvent.Id,
                EventRevision = 1,
                AttemptCount = 0
            };
            _signals.Add(dispatch);
            return Task.FromResult(new TemporalProjectionExternalEventDispatch
            {
                Event = Clone(externalEvent),
                EventRevision = dispatch.EventRevision,
                WorkflowId = dispatch.WorkflowId,
                Generation = dispatch.Generation,
                DispatchId = dispatch.DispatchId
            });
        }

        public Task<IReadOnlyList<TemporalStartDispatch>> ListPendingStartsAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TemporalStartDispatch>>(_starts.Take(limit).ToList());

        public Task MarkStartDeliveredAsync(
            string dispatchId,
            TemporalCoordinationReference reference,
            CancellationToken ct = default)
        {
            var dispatch = _starts.Single(item => item.DispatchId == dispatchId);
            _coordinators[dispatch.RunId] = new TemporalActiveCoordinator
            {
                WorkflowId = reference.WorkflowId,
                Generation = reference.Generation
            };
            _starts.RemoveAll(item => item.DispatchId == dispatchId);
            return Task.CompletedTask;
        }

        public Task RecordStartFailureAsync(string dispatchId, string failureClass, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<TemporalSignalDispatch>> ListPendingSignalsAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TemporalSignalDispatch>>(_signals.Take(limit).ToList());

        public Task MarkSignalDeliveredAsync(string dispatchId, CancellationToken ct = default)
        {
            _signals.RemoveAll(item => item.DispatchId == dispatchId);
            return Task.CompletedTask;
        }

        public Task RecordSignalFailureAsync(string dispatchId, string failureClass, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<TemporalCancellationDispatch>> ListPendingCancellationsAsync(
            int limit,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TemporalCancellationDispatch>>(_cancellations.Take(limit).ToList());

        public Task MarkCancellationDeliveredAsync(
            string dispatchId,
            CancellationToken ct = default)
        {
            _cancellations.RemoveAll(item => item.DispatchId == dispatchId);
            return Task.CompletedTask;
        }

        public Task RecordCancellationFailureAsync(
            string dispatchId,
            string failureClass,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterWaitAsync(
            TemporalProjectionWaitRegistration registration,
            ExecutionTraceEvent registeredEvent,
            CancellationToken ct = default)
        {
            _waits[registration.WaitId] = registration;
            var run = _runs[registration.RunId];
            ConsumeClaimedWaitResults(run.Id, run.Attempt);
            run.Status = ExecutionRunStatuses.Waiting;
            run.ScheduledAtUtc = registration.ResumeAtUtc;
            run.CurrentStep = registration.Name;
            _history.Add(Clone(registeredEvent));
            return Task.CompletedTask;
        }

        private void ConsumeClaimedWaitResults(string runId, int attempt)
        {
            var keys = _waitResultClaims
                .Where(item => item.Key.RunId == runId && item.Value == attempt)
                .Select(item => item.Key)
                .ToList();
            foreach (var key in keys)
            {
                _waitResultClaims.Remove(key);
                _waitResults.Remove(key);
            }
        }

        public Task PersistExternalEventWithPendingSignalAsync(
            TemporalProjectionExternalEventWrite write,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<TemporalExecutionWaitProjectionResult> ProjectWaitResolutionAsync(
            TemporalExecutionWaitResolution resolution,
            CancellationToken ct = default)
        {
            if (!_waits.Remove(resolution.WaitId, out var wait) ||
                wait.RunId != resolution.RunId || wait.Generation != resolution.Generation)
            {
                return Task.FromResult(new TemporalExecutionWaitProjectionResult { Accepted = false });
            }
            var timer = wait.Kind == TemporalWaitKinds.Timer
                ? _timers.Last(item => item.RunId == wait.RunId && item.Name == wait.Name)
                : null;
            _waitResults[(wait.RunId, wait.Kind, wait.Name)] = new ExecutionWaitResult
            {
                Name = wait.Name,
                Outcome = resolution.Resolution == TemporalWaitResolutions.Timeout
                    ? ExecutionWaitOutcomes.TimedOut
                    : resolution.Resolution,
                Timer = timer is null ? null : Clone(timer)
            };
            var run = _runs[wait.RunId];
            run.Status = ExecutionRunStatuses.Queued;
            run.ScheduledAtUtc = null;
            run.CurrentStep = null;
            return Task.FromResult(new TemporalExecutionWaitProjectionResult { Accepted = true });
        }

        public Task ProjectCancellationAsync(TemporalExecutionCancellation cancellation, CancellationToken ct = default) =>
            Cancel(cancellation.RunId);

        public Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(
            string runId,
            int limit,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExecutionTraceEvent>>(
                _history.Where(item => item.RunId == runId).Take(limit).Select(Clone).ToList());

        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExecutionArtifact>>(
                _artifacts.Where(item => item.RunId == runId).Select(Clone).ToList());

        public Task<ExecutionArtifact?> GetArtifactAsync(
            string runId,
            string artifactRef,
            CancellationToken ct = default) => Task.FromResult<ExecutionArtifact?>(
                _artifacts.FirstOrDefault(item => item.RunId == runId &&
                    (item.Id == artifactRef || item.Name == artifactRef)) is { } artifact
                    ? Clone(artifact)
                    : null);

        public Task<ExecutionCheckpoint?> GetCheckpointAsync(
            string runId,
            string key,
            CancellationToken ct = default) => Task.FromResult<ExecutionCheckpoint?>(
                _checkpoints.TryGetValue((runId, key), out var checkpoint) ? Clone(checkpoint) : null);

        public Task<ExecutionLease?> TryAcquireLeaseAsync(
            ExecutionLeaseRequest request,
            CancellationToken ct = default) => Task.FromResult<ExecutionLease?>(null);

        public Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimerRequest request, CancellationToken ct = default)
        {
            var timer = new ExecutionTimer
            {
                Name = request.Name,
                RunId = request.RunId,
                FireAtUtc = request.FireAtUtc,
                Payload = request.Payload?.DeepClone()
            };
            _timers.Add(timer);
            return Task.FromResult(Clone(timer));
        }

        public Task<TemporalExecutionProjectionStatus> GetRuntimeStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(new TemporalExecutionProjectionStatus
            {
                SchemaVersion = 2,
                PendingStartDispatches = _starts.Count,
                PendingSignalDispatches = _signals.Count,
                PendingCancellationDispatches = _cancellations.Count,
                ActiveRuns = _runs.Values.Count(run => !ExecutionRunStatuses.IsTerminal(run.Status)),
                ActiveCoordinators = _coordinators.Count(item =>
                    _runs.TryGetValue(item.Key, out var run) && !ExecutionRunStatuses.IsTerminal(run.Status))
            });

        private Task Cancel(string runId)
        {
            if (_runs.TryGetValue(runId, out var run))
            {
                run.CancellationRequested = true;
                run.Status = ExecutionRunStatuses.Cancelled;
                run.FailureClass = ExecutionFailureClasses.Cancelled;
                run.Error = "Execution run was cancelled.";
                run.CompletedAtUtc = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        private bool Reserve(ExecutionRun candidate, string hash)
        {
            if (!string.IsNullOrWhiteSpace(candidate.IdempotencyKey) &&
                _idempotency.TryGetValue(candidate.IdempotencyKey, out var receipt))
            {
                if (!string.Equals(receipt.Hash, hash, StringComparison.Ordinal))
                    throw new InvalidOperationException("Temporal projection idempotency key belongs to a different run request.");
                return true;
            }
            _runs.Add(candidate.Id, Clone(candidate));
            if (!string.IsNullOrWhiteSpace(candidate.IdempotencyKey))
                _idempotency.Add(candidate.IdempotencyKey, (hash, candidate.Id));
            return false;
        }

        private ExecutionRun Resolve(ExecutionRun candidate, string hash)
        {
            if (!string.IsNullOrWhiteSpace(candidate.IdempotencyKey) &&
                _idempotency.TryGetValue(candidate.IdempotencyKey, out var receipt))
            {
                if (!string.Equals(receipt.Hash, hash, StringComparison.Ordinal))
                    throw new InvalidOperationException("Temporal projection idempotency key belongs to a different run request.");
                return _runs[receipt.RunId];
            }
            return _runs[candidate.Id];
        }

        private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    }
}
