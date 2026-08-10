using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Execution.AzureDurable;
using Vyral.Execution.AzureDurable.Functions;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Azure;

public sealed class AzureDurableExecutionHostConformanceTests : ExecutionRuntimeConformanceTests
{
    [Fact]
    public void AzureDurableExecution_MatchesPublishedQualificationProfile()
    {
        var descriptor = AzureDurableExecutionDialect.BuildAdapterDescriptor();
        ExecutionAdapterQualificationAssertions.AssertMatchesPublishedProfile(descriptor);
    }

    protected override Task<IExecutionRuntimeAdapter> CreateRuntimeAsync()
    {
        return Task.FromResult<IExecutionRuntimeAdapter>(CreateRuntime());
    }

    protected override Task<(IExecutionRuntimeAdapter First, IExecutionRuntimeAdapter Second)> CreateRestartableRuntimePairAsync()
    {
        var options = CreateOptions($"azure-conformance-resume-{Guid.NewGuid():N}");
        var store = new AzureDurableInMemoryExecutionStatusStore(options);
        return Task.FromResult<(IExecutionRuntimeAdapter First, IExecutionRuntimeAdapter Second)>((CreateRuntime(options, store), CreateRuntime(options, store)));
    }

    protected override Task DispatchReadyRunsAsync(IExecutionRuntimeAdapter runtime)
    {
        return runtime is HostBackedRuntime hostBacked
            ? hostBacked.DispatchReadyRunsAsync()
            : Task.CompletedTask;
    }

    [Fact]
    public Task AzureDurableExecution_ExposesAdapterCapabilities() =>
        RunExecutionRuntime_ExposesAdapterCapabilities();

    [Fact]
    public Task AzureDurableExecution_RunsPluginRecordsProgressArtifactsHistoryAndIdempotency() =>
        RunExecutionRuntime_RunsPluginRecordsProgressArtifactsHistoryAndIdempotency();

    [Fact]
    public Task AzureDurableExecution_RejectsMissingHandlersAndPluginMismatches() =>
        RunExecutionRuntime_RejectsMissingHandlersAndPluginMismatches();

    [Fact]
    public Task AzureDurableExecution_RejectsIdempotencyKeyReuseForDifferentRequest() =>
        RunExecutionRuntime_RejectsIdempotencyKeyReuseForDifferentRequest();

    [Fact]
    public Task AzureDurableExecution_ExposesConsumerErrorSemantics() =>
        RunExecutionRuntime_ExposesConsumerErrorSemantics();

    [Fact]
    public Task AzureDurableExecution_CancelsRunningPlugin() =>
        RunExecutionRuntime_CancelsRunningPlugin();

    [Fact]
    public Task AzureDurableExecution_CancelAfterTerminalRunIsStable() =>
        RunExecutionRuntime_CancelAfterTerminalRunIsStable();

    [Fact]
    public Task AzureDurableExecution_RetriesFailedRunUntilSuccess() =>
        RunExecutionRuntime_RetriesFailedRunUntilSuccess();

    [Fact]
    public Task AzureDurableExecution_StopsRetryingAfterMaxAttempts() =>
        RunExecutionRuntime_StopsRetryingAfterMaxAttempts();

    [Fact]
    public Task AzureDurableExecution_RetriesTimedOutRunUntilSuccess() =>
        RunExecutionRuntime_RetriesTimedOutRunUntilSuccess();

    [Fact]
    public Task AzureDurableExecution_RunsScheduledWorkWhenDue() =>
        RunExecutionRuntime_RunsScheduledWorkWhenDue();

    [Fact]
    public Task AzureDurableExecution_ResumesPersistedScheduledWorkFromNewAdapter() =>
        RunExecutionRuntime_ResumesPersistedScheduledWorkFromNewAdapter();

    [Fact]
    public Task AzureDurableExecution_PluginCanUseCoordinationPrimitives() =>
        RunExecutionRuntime_PluginCanUseCoordinationPrimitives();

    [Fact]
    public Task AzureDurableExecution_EnforcesLeaseOwnershipReleaseAndExpiry() =>
        RunExecutionRuntime_EnforcesLeaseOwnershipReleaseAndExpiry();

    [Fact]
    public Task AzureDurableExecution_RecordsTimerAndExternalEventShape() =>
        RunExecutionRuntime_RecordsTimerAndExternalEventShape();

    [Fact]
    public Task AzureDurableExecution_BoundsRunListingAndHistoryQueries() =>
        RunExecutionRuntime_BoundsRunListingAndHistoryQueries();

    [Fact]
    public Task AzureDurableExecution_RunsWindowedBatchPluginFixture() =>
        RunExecutionRuntime_RunsWindowedBatchPluginFixture();

    [Fact]
    public Task AzureDurableExecution_RunsPressureAwarePipelineFixture() =>
        RunExecutionRuntime_RunsPressureAwarePipelineFixture();

    [Fact]
    public async Task AzureDurableExecution_StatusStorePreventsStaleActivityWritesFromUndoingCancellationOrTerminalState()
    {
        var store = new AzureDurableInMemoryExecutionStatusStore();

        ExecutionRun CreateRun(string id, string status, bool cancellationRequested = false) => new()
        {
            Id = id,
            HandlerId = "test.write-fence",
            Status = status,
            PayloadHash = "sha256:write-fence",
            CancellationRequested = cancellationRequested
        };

        await store.UpsertRunAsync(CreateRun("terminal-wins", ExecutionRunStatuses.Cancelled, cancellationRequested: true));
        var staleRunning = await store.UpsertRunAsync(CreateRun("terminal-wins", ExecutionRunStatuses.Running));
        Assert.Equal(ExecutionRunStatuses.Cancelled, staleRunning.Status);

        await store.UpsertRunAsync(CreateRun("cancellation-wins", ExecutionRunStatuses.Running, cancellationRequested: true));
        var lateSuccess = await store.UpsertRunAsync(CreateRun("cancellation-wins", ExecutionRunStatuses.Succeeded));
        Assert.Equal(ExecutionRunStatuses.Running, lateSuccess.Status);
        Assert.True(lateSuccess.CancellationRequested);
    }

    [Fact]
    public async Task AzureDurableExecution_StatusStoreMakesWakeReplayIdempotentAfterHandlerConsumesOutcome()
    {
        var store = new AzureDurableInMemoryExecutionStatusStore();
        var run = new ExecutionRun
        {
            Id = "wake-replay",
            HandlerId = "test.wake-replay",
            Status = ExecutionRunStatuses.Running,
            PayloadHash = "sha256:wake-replay"
        };
        await store.CreateRunIfAbsentAsync(run);
        await store.RegisterDurableWaitAsync(new AzureDurableWait
        {
            Kind = AzureDurableWaitKinds.ExternalEvent,
            Name = "approval"
        }, run.Id);
        var outcome = new ExecutionWaitResult
        {
            Name = "approval",
            Outcome = ExecutionWaitOutcomes.ExternalEvent,
            Event = new ExecutionExternalEvent
            {
                Id = "approval-event",
                RunId = run.Id,
                Name = "approval",
                RaisedAtUtc = DateTime.UtcNow
            }
        };

        await store.ResumeDurableWaitAsync(run.Id, outcome);
        Assert.NotNull(await store.TakeDurableWaitOutcomeAsync(run.Id, AzureDurableWaitKinds.ExternalEvent, "approval"));

        var replay = await store.ResumeDurableWaitAsync(run.Id, outcome);

        Assert.Equal(ExecutionRunStatuses.Queued, replay.Status);
    }

    [Fact]
    public async Task AzureDurableExecution_RetriesIdempotentSchedulingAfterInitialSchedulerFailure()
    {
        var options = CreateOptions($"azure-schedule-retry-{Guid.NewGuid():N}");
        var host = new AzureDurableExecutionHost(options);
        const string handlerId = "test.schedule-once";
        const string pluginId = "test.schedule-once-plugin";
        host.RegisterHandler(new DelegateExecutionHandler(
            new ExecutionHandlerDescriptor
            {
                HandlerId = handlerId,
                PluginId = pluginId,
                DisplayName = "Schedule once"
            },
            (_, _) => Task.FromResult(ExecutionRunResult.Succeeded())));
        var command = AzureDurableExecutionDialect.BuildStartCommand(new ExecutionRunRequest
        {
            HandlerId = handlerId,
            PluginId = pluginId,
            IdempotencyKey = "schedule-once",
            Payload = new JsonObject { ["items"] = new JsonArray("a") }
        }, host.ListHandlers(), options);
        var scheduler = new FailOnceScheduler();
        var client = new AzureDurableExecutionClient(host, scheduler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartRunAsync(command));
        var reserved = await host.GetRunAsync(command.InstanceId);
        Assert.NotNull(reserved);
        Assert.Equal(ExecutionRunStatuses.Queued, reserved!.Status);

        var replay = await client.StartRunAsync(command);

        Assert.Equal(command.InstanceId, replay.Id);
        Assert.Equal(2, scheduler.EnsureCalls);
        Assert.Equal(command.InstanceId, Assert.Single(scheduler.ScheduledInstanceIds));
    }

    [Fact]
    public async Task AzureDurableExecution_AtomicallyPersistsRejectedRunAndSingleAuditEvent()
    {
        var options = CreateOptions($"azure-rejected-reservation-{Guid.NewGuid():N}");
        var host = new AzureDurableExecutionHost(options);
        const string runId = "missing-handler-run";

        AzureDurableStartCommand CreateCommand() => new()
        {
            InstanceId = runId,
            Request = new ExecutionRunRequest
            {
                HandlerId = "test.missing-handler",
                PluginId = "test.missing-plugin",
                IdempotencyKey = "missing-handler-request",
                Payload = new JsonObject { ["request"] = "same" }
            },
            Handler = new ExecutionHandlerDescriptor
            {
                HandlerId = "test.missing-handler",
                PluginId = "test.missing-plugin",
                DisplayName = "Missing handler"
            }
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => host.StartRunWithReservationAsync(CreateCommand())));

        Assert.Equal(1, results.Count(result => result.Created));
        Assert.All(results, result => Assert.Equal(ExecutionRunStatuses.Rejected, result.Run.Status));
        var history = await host.GetHistoryAsync(runId);
        Assert.Single(history);
        Assert.Equal(ExecutionEventTypes.RunRejected, history[0].Type);
    }

    [Fact]
    public async Task AzureDurableExecution_ReplaySafeOrchestratorDelegatesAllStoreWorkToStateActivities()
    {
        var options = CreateOptions($"azure-state-activity-{Guid.NewGuid():N}");
        var activityHost = new AzureDurableExecutionHost(options);
        var handler = new DelegateExecutionHandler(
            new ExecutionHandlerDescriptor
            {
                HandlerId = "test.state-activity",
                PluginId = "test.state-activity-plugin",
                DisplayName = "State activity handler"
            },
            (_, _) => Task.FromResult(ExecutionRunResult.Succeeded(new JsonObject { ["ok"] = true })));
        activityHost.RegisterHandler(handler);

        // This host represents the replayed orchestrator. Its independent store must stay empty;
        // the driver below executes all persisted transitions through activityHost instead.
        var orchestratorHost = new AzureDurableExecutionHost(options);
        var command = AzureDurableExecutionDialect.BuildStartCommand(
            new ExecutionRunRequest
            {
                HandlerId = handler.Descriptor.HandlerId,
                PluginId = handler.Descriptor.PluginId,
                IdempotencyKey = "state-activity"
            },
            activityHost.ListHandlers(),
            options);
        var driver = new StateActivityDriver(activityHost);

        var completed = await orchestratorHost.OrchestrateAsync(command, driver);

        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
        Assert.Equal(1, driver.StartCalls);
        Assert.Equal(1, driver.StepCalls);
        Assert.Null(await orchestratorHost.GetRunAsync(completed.Id));
        Assert.Equal(ExecutionRunStatuses.Succeeded, (await activityHost.GetRunAsync(completed.Id))!.Status);
        Assert.NotEmpty(driver.Statuses);
    }

    [Fact]
    public async Task AzureDurableExecution_ReplaysHandlerAfterPortableTimerWait()
    {
        var options = CreateOptions($"azure-durable-timer-wait-{Guid.NewGuid():N}");
        var activityHost = new AzureDurableExecutionHost(options);
        var invocations = 0;
        var handler = new DelegateExecutionHandler(
            new ExecutionHandlerDescriptor
            {
                HandlerId = "test.durable-timer-wait",
                PluginId = "test.durable-wait-plugin",
                DisplayName = "Durable timer wait"
            },
            async (context, ct) =>
            {
                Interlocked.Increment(ref invocations);
                await context.PutCheckpointAsync(new ExecutionCheckpointWrite
                {
                    Key = "before-timer-wait",
                    Content = new JsonObject { ["prepared"] = true }
                }, ct);
                var outcome = await context.WaitForTimerAsync(
                    "continue",
                    DateTime.UtcNow.AddMinutes(1),
                    new JsonObject { ["source"] = "test" },
                    ct);
                return ExecutionRunResult.Succeeded(new JsonObject
                {
                    ["outcome"] = outcome.Outcome,
                    ["timer"] = outcome.Timer?.Name
                });
            });
        activityHost.RegisterHandler(handler);
        var command = AzureDurableExecutionDialect.BuildStartCommand(
            new ExecutionRunRequest
            {
                HandlerId = handler.Descriptor.HandlerId,
                PluginId = handler.Descriptor.PluginId,
                IdempotencyKey = "durable-timer-wait"
            },
            activityHost.ListHandlers(),
            options);

        var completed = await new AzureDurableExecutionHost(options).OrchestrateAsync(command, new StateActivityDriver(activityHost));

        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
        Assert.Equal(ExecutionWaitOutcomes.Timer, completed.Result!["outcome"]!.GetValue<string>());
        Assert.Equal("continue", completed.Result!["timer"]!.GetValue<string>());
        Assert.Equal(2, Volatile.Read(ref invocations));
        var history = await activityHost.GetHistoryAsync(completed.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitRegistered);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitResumed);
    }

    [Fact]
    public async Task AzureDurableExecution_ReplaysHandlerAfterPortableExternalEventWait()
    {
        var options = CreateOptions($"azure-durable-event-wait-{Guid.NewGuid():N}");
        var activityHost = new AzureDurableExecutionHost(options);
        var handler = new DelegateExecutionHandler(
            new ExecutionHandlerDescriptor
            {
                HandlerId = "test.durable-event-wait",
                PluginId = "test.durable-wait-plugin",
                DisplayName = "Durable external-event wait"
            },
            async (context, ct) =>
            {
                await context.PutCheckpointAsync(new ExecutionCheckpointWrite
                {
                    Key = "before-event-wait",
                    Content = new JsonObject { ["prepared"] = true }
                }, ct);
                var outcome = await context.WaitForExternalEventAsync("approval", DateTime.UtcNow.AddMinutes(1), ct);
                return ExecutionRunResult.Succeeded(new JsonObject
                {
                    ["outcome"] = outcome.Outcome,
                    ["event"] = outcome.Event?.Name
                });
            });
        activityHost.RegisterHandler(handler);
        var command = AzureDurableExecutionDialect.BuildStartCommand(
            new ExecutionRunRequest
            {
                HandlerId = handler.Descriptor.HandlerId,
                PluginId = handler.Descriptor.PluginId,
                IdempotencyKey = "durable-event-wait"
            },
            activityHost.ListHandlers(),
            options);

        var completed = await new AzureDurableExecutionHost(options).OrchestrateAsync(command, new StateActivityDriver(activityHost));

        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
        Assert.Equal(ExecutionWaitOutcomes.ExternalEvent, completed.Result!["outcome"]!.GetValue<string>());
        Assert.Equal("approval", completed.Result!["event"]!.GetValue<string>());
        var history = await activityHost.GetHistoryAsync(completed.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitRegistered);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitResumed);
    }

    [Fact]
    public void AzureDurableFunctionsBridge_InitializesItsSingleAttemptActivityOptions()
    {
        // Constructing with a null context exercises the driver's static Durable Task options
        // without needing to fake a provider orchestration context. In particular, a single
        // attempt still needs Durable Task's strictly-positive retry interval.
        var exception = Assert.Throws<ArgumentNullException>(() => new AzureDurableFunctionsOrchestrationDriver(null!));
        Assert.Equal("context", exception.ParamName);
    }

    private static HostBackedRuntime CreateRuntime()
    {
        return CreateRuntime(CreateOptions($"azure-conformance-{Guid.NewGuid():N}"), null);
    }

    private static HostBackedRuntime CreateRuntime(AzureDurableExecutionOptions options, IAzureDurableExecutionStatusStore? store)
    {
        var registry = new AzureDurableExecutionRegistry(options.Limits);
        var host = new AzureDurableExecutionHost(options, registry, store);
        var scheduler = new InlineAzureDurableScheduler(host, options);
        var client = new AzureDurableExecutionClient(host, scheduler);
        var adapter = new AzureDurableExecutionRuntimeAdapter(client, options, registry);
        return new HostBackedRuntime(adapter, scheduler);
    }

    private static AzureDurableExecutionOptions CreateOptions(string adapterId)
    {
        return new AzureDurableExecutionOptions
        {
            AdapterId = adapterId,
            TaskHubName = "test-hub",
            WorkerId = "test-worker",
            MaxActiveRuns = 16,
            DefaultListLimit = 20,
            MaxListLimit = 100
        };
    }

    private sealed class HostBackedRuntime : IExecutionRuntimeAdapter
    {
        private readonly AzureDurableExecutionRuntimeAdapter _adapter;
        private readonly InlineAzureDurableScheduler _scheduler;

        public HostBackedRuntime(AzureDurableExecutionRuntimeAdapter adapter, InlineAzureDurableScheduler scheduler)
        {
            _adapter = adapter;
            _scheduler = scheduler;
        }

        public ExecutionRuntimeAdapterDescriptor Adapter => _adapter.Adapter;

        public void RegisterHandler(IExecutionHandler handler) => _adapter.RegisterHandler(handler);

        public void RegisterPlugin(IExecutionPlugin plugin) => _adapter.RegisterPlugin(plugin);

        public IReadOnlyList<ExecutionPluginDescriptor> ListPlugins() => _adapter.ListPlugins();

        public IReadOnlyList<ExecutionHandlerDescriptor> ListHandlers() => _adapter.ListHandlers();

        public Task<ExecutionRun> StartRunAsync(ExecutionRunRequest request, CancellationToken ct = default) =>
            _adapter.StartRunAsync(request, ct);

        public Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default) =>
            _adapter.GetRunAsync(runId, includeResult, ct);

        public Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default) =>
            _adapter.ListRunsAsync(query, ct);

        public Task<ExecutionRun?> CancelRunAsync(string runId, CancellationToken ct = default) =>
            _adapter.CancelRunAsync(runId, ct);

        public Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default) =>
            _adapter.GetHistoryAsync(runId, query, ct);

        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default) =>
            _adapter.ListArtifactsAsync(runId, ct);

        public Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default) =>
            _adapter.GetArtifactAsync(runId, artifactRef, ct);

        public Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default) =>
            _adapter.GetCheckpointAsync(runId, key, ct);

        public Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default) =>
            _adapter.TryAcquireLeaseAsync(request, ct);

        public Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default) =>
            _adapter.ReleaseLeaseAsync(leaseKey, ownerId, ct);

        public Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimerRequest request, CancellationToken ct = default) =>
            _adapter.ScheduleTimerAsync(request, ct);

        public Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEventRequest request, CancellationToken ct = default) =>
            _adapter.RaiseEventAsync(request, ct);

        public Task<ExecutionRuntimeAdapterStatus> GetAdapterStatusAsync(CancellationToken ct = default) =>
            _adapter.GetAdapterStatusAsync(ct);

        public Task DispatchReadyRunsAsync() => _scheduler.DispatchReadyRunsAsync();

        public int ScheduleCallCount => _scheduler.ScheduleCallCount;
    }

    private sealed class InlineAzureDurableScheduler : IAzureDurableExecutionOrchestrationScheduler
    {
        private readonly AzureDurableExecutionHost _host;
        private readonly AzureDurableExecutionOptions _options;
        private readonly ConcurrentDictionary<string, Task> _instances = new(StringComparer.Ordinal);
        private int _scheduleCallCount;

        public InlineAzureDurableScheduler(AzureDurableExecutionHost host, AzureDurableExecutionOptions options)
        {
            _host = host;
            _options = options;
        }

        public Task ScheduleNewAsync(AzureDurableStartCommand command, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _scheduleCallCount);
            _instances.GetOrAdd(command.InstanceId, _ =>
                Task.Run(() => _host.OrchestrateAsync(Clone(command), new InlineAzureDurableDriver(_host), CancellationToken.None)));
            return Task.CompletedTask;
        }

        public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);

        public Task TerminateAsync(string instanceId, string reason, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task RaiseEventAsync(string instanceId, string eventName, JsonNode? payload, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public async Task DispatchReadyRunsAsync()
        {
            var handlers = _host.ListHandlers();
            var activeRuns = await _host.ListRunsAsync(new ExecutionRunQuery
            {
                IncludeResult = true,
                Limit = _options.MaxListLimit
            });

            foreach (var run in activeRuns.Where(run => ExecutionRunLifecycle.IsActive(run.Status)))
            {
                if (_instances.ContainsKey(run.Id))
                {
                    continue;
                }

                var handler = handlers.FirstOrDefault(candidate =>
                    string.Equals(candidate.HandlerId, run.HandlerId, StringComparison.Ordinal));
                if (handler is null)
                {
                    continue;
                }

                var command = AzureDurableExecutionDialect.BuildStartCommand(
                    new ExecutionRunRequest
                    {
                        HandlerId = run.HandlerId,
                        PluginId = run.PluginId,
                        Payload = CloneNode(run.Payload),
                        IdempotencyKey = run.IdempotencyKey,
                        CorrelationId = run.CorrelationId,
                        ScheduledAtUtc = run.ScheduledAtUtc,
                        RetryPolicy = Clone(run.RetryPolicy),
                        Tags = new Dictionary<string, string>(run.Tags, StringComparer.Ordinal)
                    },
                    handlers,
                    _options);
                command.InstanceId = run.Id;
                await ScheduleNewAsync(command);
            }
        }
    }

    private sealed class FailOnceScheduler : IAzureDurableExecutionOrchestrationScheduler
    {
        private int _ensureCalls;

        public int EnsureCalls => Volatile.Read(ref _ensureCalls);
        public List<string> ScheduledInstanceIds { get; } = new();

        public Task ScheduleNewAsync(AzureDurableStartCommand command, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _ensureCalls) == 1)
            {
                throw new InvalidOperationException("Transient provider scheduling failure.");
            }

            ScheduledInstanceIds.Add(command.InstanceId);
            return Task.CompletedTask;
        }

        public Task TerminateAsync(string instanceId, string reason, CancellationToken ct = default) => Task.CompletedTask;

        public Task RaiseEventAsync(string instanceId, string eventName, JsonNode? payload, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StateActivityDriver :
        IAzureDurableExecutionOrchestrationDriver,
        IAzureDurableExecutionOrchestrationStateDriver
    {
        private readonly AzureDurableExecutionHost _activityHost;

        public StateActivityDriver(AzureDurableExecutionHost activityHost)
        {
            _activityHost = activityHost;
        }

        public int StartCalls { get; private set; }
        public int StepCalls { get; private set; }
        public List<AzureDurableStatusSnapshot> Statuses { get; } = new();
        public DateTime CurrentUtc => DateTime.UtcNow;

        public Task CreateTimerAsync(DateTime fireAtUtc, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AzureDurableActivityResult> CallActivityAsync(
            string activityName,
            AzureDurableActivityCommand command,
            AzureDurableRetryOptions retryOptions,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("The replay-safe orchestration path must use RunStepAsync.");

        public Task SetCustomStatusAsync(AzureDurableStatusSnapshot snapshot, CancellationToken ct = default)
        {
            Statuses.Add(snapshot);
            return Task.CompletedTask;
        }

        public async Task<AzureDurableRunCreation> StartRunAsync(AzureDurableStartCommand command, CancellationToken ct = default)
        {
            StartCalls++;
            return await _activityHost.StartRunWithReservationAsync(command, ct);
        }

        public async Task<AzureDurableOrchestrationStepResult> RunStepAsync(
            AzureDurableStartCommand command,
            ExecutionRun run,
            ExecutionWaitResult? waitOutcome = null,
            CancellationToken ct = default)
        {
            StepCalls++;
            var activityCommand = AzureDurableExecutionDialect.BuildActivityCommand(run);
            activityCommand.WaitOutcome = waitOutcome;
            return await _activityHost.ExecuteOrchestrationStepAsync(
                activityCommand,
                ct);
        }

        public Task<ExecutionWaitResult> WaitForDurableWaitAsync(AzureDurableWait wait, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(wait);
            return Task.FromResult(wait.Kind == AzureDurableWaitKinds.Timer
                ? new ExecutionWaitResult
                {
                    Name = wait.Name,
                    Outcome = ExecutionWaitOutcomes.Timer,
                    Timer = Clone(wait.Timer!)
                }
                : new ExecutionWaitResult
                {
                    Name = wait.Name,
                    Outcome = ExecutionWaitOutcomes.ExternalEvent,
                    Event = new ExecutionExternalEvent
                    {
                        Id = "test-event-" + wait.Name,
                        Name = wait.Name,
                        RunId = "test-run",
                        RaisedAtUtc = DateTime.UtcNow
                    }
                });
        }
    }

    private sealed class InlineAzureDurableDriver : IAzureDurableExecutionOrchestrationDriver
    {
        private readonly AzureDurableExecutionHost _host;

        public InlineAzureDurableDriver(AzureDurableExecutionHost host)
        {
            _host = host;
        }

        public DateTime CurrentUtc => DateTime.UtcNow;

        public async Task CreateTimerAsync(DateTime fireAtUtc, CancellationToken ct = default)
        {
            var delay = fireAtUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
        }

        public Task<AzureDurableActivityResult> CallActivityAsync(
            string activityName,
            AzureDurableActivityCommand command,
            AzureDurableRetryOptions retryOptions,
            CancellationToken ct = default)
        {
            Assert.Equal(AzureDurableExecutionNames.Activity, activityName);
            return _host.DispatchActivityAsync(command, ct);
        }

        public Task SetCustomStatusAsync(AzureDurableStatusSnapshot snapshot, CancellationToken ct = default)
        {
            Assert.False(string.IsNullOrWhiteSpace(snapshot.RunId));
            return Task.CompletedTask;
        }
    }

    private static T Clone<T>(T value)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ExecutionJson.Options), ExecutionJson.Options)!;
    }

    private static JsonNode? CloneNode(JsonNode? value)
    {
        return value is null ? null : JsonNode.Parse(value.ToJsonString(ExecutionJson.Options));
    }
}
