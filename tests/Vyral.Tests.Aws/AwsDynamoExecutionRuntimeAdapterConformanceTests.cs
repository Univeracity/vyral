using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Execution.Aws;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Aws;

/// <summary>
/// Runs the provider-neutral external-worker contract against the AWS adapter with a
/// deterministic state/dispatch fixture. DynamoDB and SQS are separately validated by
/// opt-in integration deployment checks; this suite prevents their mechanics from becoming the
/// only source of correctness for the adapter's portable behavior.
/// </summary>
public sealed class AwsDynamoExecutionRuntimeAdapterConformanceTests : ExternalExecutionWorkerRuntimeConformanceTests
{
    [Fact]
    public async Task AwsExecutionRuntime_MatchesPublishedQualificationProfile()
    {
        var fixture = await CreateExternalWorkerRuntimeAsync();
        ExecutionAdapterQualificationAssertions.AssertMatchesPublishedProfile(fixture.Adapter.Adapter);
    }

    protected override Task<ExternalExecutionWorkerRuntimeFixture> CreateExternalWorkerRuntimeAsync()
    {
        var fixture = CreateExternalWorkerRuntimeFixture();
        return Task.FromResult(fixture.Runtime);
    }

    private static (ExternalExecutionWorkerRuntimeFixture Runtime, InMemoryAwsDynamoExecutionStateStore State) CreateExternalWorkerRuntimeFixture()
    {
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "conformance.aws.external.worker",
            PluginId = "conformance.aws.external",
            DisplayName = "AWS external worker conformance handler"
        };
        var dispatcher = new CapturingExecutionRunDispatcher();
        var state = new InMemoryAwsDynamoExecutionStateStore();
        var runtime = new AwsDynamoExecutionRuntimeAdapter(
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
        return (
            new ExternalExecutionWorkerRuntimeFixture
            {
                Adapter = runtime,
                Worker = runtime,
                Handler = handler
            },
            state);
    }

    [Fact]
    public Task AwsExternalWorker_AdvertisesExternalExecutionModel() =>
        RunExternalWorker_AdvertisesExternalExecutionModel();

    [Fact]
    public Task AwsExternalWorker_ReportsArtifactsEventsAndCheckpointsUnderLease() =>
        RunExternalWorker_ReportsArtifactsEventsAndCheckpointsUnderLease();

    [Fact]
    public Task AwsExternalWorker_DeduplicatesClaimsAndResumesDurableEvents() =>
        RunExternalWorker_DeduplicatesClaimsAndResumesDurableEvents();

    [Fact]
    public Task AwsExternalWorker_CompletionIsReplaySafeForTheSameLeaseToken() =>
        RunExternalWorker_CompletionIsReplaySafeForTheSameLeaseToken();

    [Fact]
    public Task AwsExternalWorker_RejectsStaleLeaseEvents() =>
        RunExternalWorker_RejectsStaleLeaseEvents();

    [Fact]
    public async Task AwsExternalWorker_RunsPortableConsumerPluginUnchanged()
    {
        var handler = Assert.Single(ExternalExecutionConsumerSample.CreatePlugin().Handlers).Descriptor;
        var dispatcher = new CapturingExecutionRunDispatcher();
        var runtime = new AwsDynamoExecutionRuntimeAdapter(
            new InMemoryAwsDynamoExecutionStateStore(),
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
        var completed = await ExternalExecutionConsumerSample.RunExternalAsync(
            runtime,
            runtime,
            "portable-consumer:aws-external");

        await ExternalExecutionConsumerSample.AssertRunShapeAsync(runtime, completed);
    }

    [Fact]
    public async Task AwsExternalWorker_EnforcesSharedProductPolicyAndRedactsTraceDetails()
    {
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "conformance.aws.policy.worker",
            PluginId = "conformance.aws.policy",
            DisplayName = "AWS product policy handler"
        };
        var dispatcher = new CapturingExecutionRunDispatcher();
        var runtime = new AwsDynamoExecutionRuntimeAdapter(
            new InMemoryAwsDynamoExecutionStateStore(),
            dispatcher,
            new AwsDynamoExecutionRuntimeOptions
            {
                ProductPolicies =
                [
                    new ExecutionProductPolicy
                    {
                        ProductId = "product-a",
                        AllowedHandlerIds = new HashSet<string>(StringComparer.Ordinal) { handler.HandlerId },
                        AllowedTenantIds = new HashSet<string>(StringComparer.Ordinal) { "tenant-a" },
                        AllowedServiceIdentities = new HashSet<string>(StringComparer.Ordinal) { "conformance-worker" },
                        ArtifactPrefix = "product-a/",
                        RedactedJsonPropertyNames = new HashSet<string>(StringComparer.Ordinal) { "secret" }
                    }
                ],
                WorkerDispatchers =
                [
                    new AwsDynamoExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatcher }
                ]
            });
        runtime.RegisterExternalHandler(handler);
        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = handler.HandlerId,
            Scope = new ExecutionScope { ProductId = "product-a", TenantId = "tenant-a" }
        });
        var lease = (await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "conformance-worker",
            HandlerIds = { handler.HandlerId },
            RunId = accepted.Id
        }))!;

        await runtime.RecordExternalLeaseEventAsync(new ExecutionExternalWorkerEventRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Type = ExecutionEventTypes.Log,
            Details = new JsonObject { ["secret"] = "do-not-persist", ["safe"] = "visible" }
        });
        var history = await runtime.GetHistoryAsync(accepted.Id);
        var logged = Assert.Single(history, item => item.Type == ExecutionEventTypes.Log);
        Assert.Equal("[redacted]", logged.Details!["secret"]!.GetValue<string>());
        Assert.Equal("product-a", logged.Context["productId"]);
        Assert.Equal("tenant-a", logged.Context["tenantId"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.PutExternalLeaseArtifactAsync(new ExecutionExternalWorkerArtifactRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Artifact = new ExecutionArtifactWrite { Name = "wrong-prefix", Content = new JsonObject() }
        }));
        var artifact = await runtime.PutExternalLeaseArtifactAsync(new ExecutionExternalWorkerArtifactRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Artifact = new ExecutionArtifactWrite { Name = "product-a/summary", Content = new JsonObject() }
        });
        Assert.Equal("product-a/summary", artifact.Name);
    }

    [Fact]
    public async Task AwsRuntime_ReconcilesRunnableWorkThroughPortableDispatchSeam()
    {
        var fixture = await CreateExternalWorkerRuntimeAsync();
        fixture.Worker.RegisterExternalHandler(fixture.Handler);
        var accepted = await fixture.Adapter.StartRunAsync(new ExecutionRunRequest { HandlerId = fixture.Handler.HandlerId });
        var maintenance = Assert.IsAssignableFrom<IExecutionRuntimeMaintenance>(fixture.Adapter);

        var dryRun = await maintenance.ReconcileDispatchAsync(new ExecutionMaintenanceDispatchReconcileRequest
        {
            DryRun = true,
            Limit = 10
        });
        Assert.Contains(accepted.Id, dryRun.CandidateRunIds);
        Assert.Equal(0, dryRun.Dispatched);

        var reconciled = await maintenance.ReconcileDispatchAsync(new ExecutionMaintenanceDispatchReconcileRequest
        {
            DryRun = false,
            Limit = 10
        });
        Assert.Contains(accepted.Id, reconciled.CandidateRunIds);
        Assert.Equal(1, reconciled.Dispatched);
        Assert.Empty(reconciled.Failures);
    }

    [Fact]
    public async Task AwsRuntime_EnforcesPublishedOperationalBounds()
    {
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "conformance.aws.operational.worker",
            PluginId = "conformance.aws.operational",
            DisplayName = "AWS operational-policy worker"
        };
        var dispatcher = new CapturingExecutionRunDispatcher();
        var runtime = new AwsDynamoExecutionRuntimeAdapter(
            new InMemoryAwsDynamoExecutionStateStore(),
            dispatcher,
            new AwsDynamoExecutionRuntimeOptions
            {
                MaxActiveRuns = 1,
                DefaultListLimit = 1,
                MaxListLimit = 1,
                DefaultHistoryLimit = 1,
                MaxHistoryLimit = 1,
                MaintenanceScanLimit = 1,
                WorkerDispatchers =
                [
                    new AwsDynamoExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatcher }
                ]
            });
        runtime.RegisterExternalHandler(handler);

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = handler.HandlerId });
        Assert.Equal(ExecutionRunStatuses.Queued, accepted.Status);
        var rejected = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = handler.HandlerId });
        Assert.Equal(ExecutionRunStatuses.Rejected, rejected.Status);
        Assert.Equal(ExecutionFailureClasses.QueueFull, rejected.FailureClass);

        var status = await runtime.GetAdapterStatusAsync();
        Assert.Equal(1, status.ActiveRuns);
        Assert.Equal(1, status.OperationalPolicy!.MaxActiveRuns);
        Assert.Equal(1, status.OperationalPolicy.DefaultListLimit);
        Assert.Equal(1, status.OperationalPolicy.MaxHistoryLimit);
        Assert.Equal(ExecutionResumePolicyModes.RestartRecovery, status.ResumePolicy!.Mode);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ListRunsAsync(new ExecutionRunQuery { Limit = 2 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.GetHistoryAsync(accepted.Id, new ExecutionHistoryQuery { Limit = 2 }));
    }

    [Fact]
    public async Task AwsRuntime_AtomicallyReservesIdempotencyKeysAndDispatchesOnce()
    {
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "conformance.aws.idempotency.worker",
            PluginId = "conformance.aws.idempotency",
            DisplayName = "AWS idempotency worker"
        };
        var dispatcher = new CapturingExecutionRunDispatcher();
        var state = new InMemoryAwsDynamoExecutionStateStore();
        var runtime = new AwsDynamoExecutionRuntimeAdapter(
            state,
            dispatcher,
            new AwsDynamoExecutionRuntimeOptions
            {
                WorkerDispatchers = [new AwsDynamoExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatcher }]
            });
        runtime.RegisterExternalHandler(handler);

        var requests = Enumerable.Range(0, 16).Select(_ => runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = handler.HandlerId,
            IdempotencyKey = "same-request",
            Payload = new JsonObject { ["work"] = "once" }
        })).ToArray();
        var runs = await Task.WhenAll(requests);

        Assert.Single(runs.Select(run => run.Id).Distinct(StringComparer.Ordinal));
        Assert.Single(await runtime.ListRunsAsync(new ExecutionRunQuery { IdempotencyKey = "same-request", Limit = 10 }));
        Assert.Single(dispatcher.Requests, request => request.RunId == runs[0].Id && request.Reason == ExecutionDispatchReasons.RunReady);
    }

    [Fact]
    public async Task AwsRuntime_OffloadsLargeArtifactsAndRejectsOversizeInlineArtifactsBeforeStateStorage()
    {
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "conformance.aws.storage.worker",
            PluginId = "conformance.aws.storage",
            DisplayName = "AWS storage worker"
        };
        var dispatcher = new CapturingExecutionRunDispatcher();
        var state = new InMemoryAwsDynamoExecutionStateStore();
        var objects = new InMemoryObjectStore();
        var runtime = new AwsDynamoExecutionRuntimeAdapter(
            state,
            dispatcher,
            new AwsDynamoExecutionRuntimeOptions
            {
                Limits = AwsDynamoExecutionLimits.WithArtifactOffload,
                ArtifactObjectContainer = "execution-artifacts",
                WorkerDispatchers = [new AwsDynamoExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatcher }]
            },
            objects);
        runtime.RegisterExternalHandler(handler);
        var run = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = handler.HandlerId });
        var lease = (await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "storage-worker",
            HandlerIds = { handler.HandlerId },
            RunId = run.Id
        }))!;

        var artifact = await runtime.PutExternalLeaseArtifactAsync(new ExecutionExternalWorkerArtifactRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Artifact = new ExecutionArtifactWrite
            {
                Name = "large-result",
                Kind = ExecutionArtifactKinds.Text,
                Text = new string('x', AwsDynamoExecutionLimits.MaxInlineDocumentValueBytes + 1)
            }
        });

        Assert.Null(artifact.Text);
        Assert.Null(artifact.Content);
        Assert.StartsWith("vyral-object://execution-artifacts/execution-artifacts/", artifact.Uri, StringComparison.Ordinal);
        Assert.Equal("true", artifact.Metadata["offloaded"]);
        Assert.Single(objects.Items);

        var inlineOnly = new AwsDynamoExecutionRuntimeAdapter(
            new InMemoryAwsDynamoExecutionStateStore(),
            dispatcher,
            new AwsDynamoExecutionRuntimeOptions
            {
                WorkerDispatchers = [new AwsDynamoExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatcher }]
            });
        inlineOnly.RegisterExternalHandler(handler);
        var inlineRun = await inlineOnly.StartRunAsync(new ExecutionRunRequest { HandlerId = handler.HandlerId });
        var inlineLease = (await inlineOnly.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "storage-worker",
            HandlerIds = { handler.HandlerId },
            RunId = inlineRun.Id
        }))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inlineOnly.PutExternalLeaseArtifactAsync(new ExecutionExternalWorkerArtifactRequest
        {
            LeaseKey = inlineLease.LeaseKey,
            LeaseToken = inlineLease.LeaseToken,
            WorkerId = inlineLease.WorkerId,
            Artifact = new ExecutionArtifactWrite
            {
                Name = "too-large-inline",
                Text = new string('x', AwsDynamoExecutionLimits.MaxInlineDocumentValueBytes + 1)
            }
        }));
    }

    [Fact]
    public async Task AwsRuntime_EnforcesDynamoSafePayloadResultAndCheckpointLimitsBeforePersistence()
    {
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "conformance.aws.inline-limits.worker",
            PluginId = "conformance.aws.inline-limits",
            DisplayName = "AWS inline limits worker"
        };
        var dispatcher = new CapturingExecutionRunDispatcher();
        var runtime = new AwsDynamoExecutionRuntimeAdapter(
            new InMemoryAwsDynamoExecutionStateStore(),
            dispatcher,
            new AwsDynamoExecutionRuntimeOptions
            {
                WorkerDispatchers = [new AwsDynamoExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatcher }]
            });
        runtime.RegisterExternalHandler(handler);
        var justUnder = new string('x', AwsDynamoExecutionLimits.MaxInlineRunValueBytes - 2);
        var run = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = handler.HandlerId,
            Payload = JsonValue.Create(justUnder)
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = handler.HandlerId,
            Payload = JsonValue.Create(new string('x', AwsDynamoExecutionLimits.MaxInlineRunValueBytes + 1))
        }));

        var lease = (await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "inline-limits-worker",
            HandlerIds = { handler.HandlerId },
            RunId = run.Id
        }))!;
        var checkpoint = await runtime.CheckpointExternalLeaseAsync(new ExecutionExternalWorkerCheckpointRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Checkpoint = new ExecutionCheckpointWrite { Key = "safe", Content = JsonValue.Create(justUnder) }
        });
        Assert.Equal("safe", checkpoint.Key);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CheckpointExternalLeaseAsync(new ExecutionExternalWorkerCheckpointRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Checkpoint = new ExecutionCheckpointWrite { Key = "too-large", Content = JsonValue.Create(new string('x', AwsDynamoExecutionLimits.MaxInlineDocumentValueBytes + 1)) }
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Result = ExecutionRunResult.Succeeded(JsonValue.Create(new string('x', AwsDynamoExecutionLimits.MaxInlineRunValueBytes + 1)))
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Result = ExecutionRunResult.Succeeded(statusDetails: new JsonObject { ["detail"] = new string('x', AwsDynamoExecutionLimits.MaxInlineRunStatusDetailsBytes + 1) })
        }));
        var completed = await runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Result = ExecutionRunResult.Succeeded(JsonValue.Create(justUnder))
        });
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
    }

    [Fact]
    public async Task AwsRuntime_DefersLongSqsDelaysToDurableMaintenance()
    {
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "conformance.aws.long-delay.worker",
            PluginId = "conformance.aws.long-delay",
            DisplayName = "AWS long-delay worker"
        };
        var queue = new CapturingSqsQueue();
        var dispatcher = new AwsSqsExecutionDispatcher(
            queue,
            new AwsSqsExecutionDispatchOptions { QueueUrl = "https://sqs.us-east-1.amazonaws.com/123456789012/vyral-execution" });
        var runtime = new AwsDynamoExecutionRuntimeAdapter(
            new InMemoryAwsDynamoExecutionStateStore(),
            dispatcher,
            new AwsDynamoExecutionRuntimeOptions
            {
                WorkerDispatchers = [new AwsDynamoExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatcher }]
            });
        runtime.RegisterExternalHandler(handler);

        var run = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = handler.HandlerId,
            ScheduledAtUtc = DateTime.UtcNow.AddSeconds(AwsSqsExecutionDispatchOptions.MaximumSupportedDelaySeconds + 30)
        });

        Assert.Equal(ExecutionRunStatuses.Waiting, run.Status);
        Assert.Empty(queue.Requests);
        Assert.Equal("maintenance-for-delays-over-900-seconds", runtime.Adapter.Metadata["timerDispatch"]);
    }

    [Fact]
    public async Task AwsRuntime_CancellationFencePreventsAStaleWorkerCompletion()
    {
        var fixture = await CreateExternalWorkerRuntimeAsync();
        fixture.Worker.RegisterExternalHandler(fixture.Handler);
        var accepted = await fixture.Adapter.StartRunAsync(new ExecutionRunRequest { HandlerId = fixture.Handler.HandlerId });
        var lease = Assert.IsType<ExecutionExternalWorkerLease>(await fixture.Worker.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = fixture.WorkerId,
            HandlerIds = { fixture.Handler.HandlerId },
            RunId = accepted.Id
        }));

        var cancellation = await fixture.Adapter.CancelRunAsync(accepted.Id);
        Assert.NotNull(cancellation);
        Assert.True(cancellation!.CancellationRequested);
        Assert.Equal(ExecutionRunStatuses.Running, cancellation.Status);

        var completed = await fixture.Worker.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Result = ExecutionRunResult.Succeeded()
        });

        Assert.Equal(ExecutionRunStatuses.Cancelled, completed.Status);
        var stored = await fixture.Adapter.GetRunAsync(accepted.Id);
        Assert.NotNull(stored);
        Assert.Equal(ExecutionRunStatuses.Cancelled, stored!.Status);
        Assert.True(stored.CancellationRequested);
    }

    [Fact]
    public async Task AwsRuntime_EventAndTimeoutRaceConsumesExactlyOneDurableWait()
    {
        var created = CreateExternalWorkerRuntimeFixture();
        var fixture = created.Runtime;
        fixture.Worker.RegisterExternalHandler(fixture.Handler);
        var accepted = await fixture.Adapter.StartRunAsync(new ExecutionRunRequest { HandlerId = fixture.Handler.HandlerId });
        var firstLease = Assert.IsType<ExecutionExternalWorkerLease>(await fixture.Worker.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = fixture.WorkerId,
            HandlerIds = { fixture.Handler.HandlerId },
            RunId = accepted.Id
        }));
        var suspended = await fixture.Worker.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = firstLease.LeaseKey,
            LeaseToken = firstLease.LeaseToken,
            WorkerId = firstLease.WorkerId,
            Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
            Name = "approval",
            TimeoutAtUtc = DateTime.UtcNow.AddHours(1)
        });
        Assert.True(suspended.Suspended);

        created.State.MakeWaitDue(accepted.Id);
        var eventTask = fixture.Adapter.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = "approval",
            Payload = new JsonObject { ["approved"] = true }
        });
        var leaseTask = fixture.Worker.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = fixture.WorkerId,
            HandlerIds = { fixture.Handler.HandlerId },
            RunId = accepted.Id
        });
        await Task.WhenAll(eventTask, leaseTask);

        var resumedLease = await leaseTask;
        resumedLease ??= Assert.IsType<ExecutionExternalWorkerLease>(await fixture.Worker.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = fixture.WorkerId,
            HandlerIds = { fixture.Handler.HandlerId },
            RunId = accepted.Id
        }));
        var outcome = await fixture.Worker.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = resumedLease.LeaseKey,
            LeaseToken = resumedLease.LeaseToken,
            WorkerId = resumedLease.WorkerId,
            Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
            Name = "approval",
            TimeoutAtUtc = DateTime.UtcNow.AddMinutes(1)
        });

        Assert.False(outcome.Suspended);
        Assert.NotNull(outcome.Outcome);
        Assert.Contains(outcome.Outcome!.Outcome, new[] { ExecutionWaitOutcomes.TimedOut, ExecutionWaitOutcomes.ExternalEvent });
        var stored = await fixture.Adapter.GetRunAsync(accepted.Id);
        Assert.NotNull(stored);
        Assert.Equal(ExecutionRunStatuses.Running, stored!.Status);
    }

    private sealed class CapturingExecutionRunDispatcher : IExecutionRunDispatcher
    {
        public List<ExecutionDispatchRequest> Requests { get; } = new();

        public Task DispatchAsync(ExecutionDispatchRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            lock (Requests)
            {
                Requests.Add(Clone(request));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CapturingSqsQueue : IAwsSqsExecutionQueue
    {
        public List<AwsSqsExecutionEnqueueRequest> Requests { get; } = [];

        public Task<AwsSqsExecutionEnqueueResult> EnqueueAsync(AwsSqsExecutionEnqueueRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AwsSqsExecutionEnqueueResult { MessageId = "test", QueueUrl = request.QueueUrl });
        }
    }

    private sealed class InMemoryObjectStore : IObjectStore
    {
        public Dictionary<string, byte[]> Items { get; } = new(StringComparer.Ordinal);

        public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
        {
            await using var stream = new MemoryStream();
            await request.Content.CopyToAsync(stream, ct);
            Items[$"{request.Container}/{request.Key}"] = stream.ToArray();
            return new ObjectInfo
            {
                Container = request.Container,
                Key = request.Key,
                ContentLength = stream.Length,
                ContentHash = "sha256:test",
                Etag = "test"
            };
        }

        public Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default) =>
            Task.FromResult<ObjectResult?>(null);

        public Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ObjectListResult());
    }

    private sealed class InMemoryAwsDynamoExecutionStateStore : IAwsDynamoExecutionStateStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ExecutionRun> _runs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _idempotencyReservations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ExecutionTraceEvent>> _history = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, ExecutionCheckpoint>> _checkpoints = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ExecutionArtifact>> _artifacts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutionLease> _leases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ExecutionTimer>> _timers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ExecutionExternalEvent>> _events = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AwsDynamoExecutionWait> _waits = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutionWaitResult> _waitOutcomes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _consumedEvents = new(StringComparer.Ordinal);

        public void MakeWaitDue(string runId)
        {
            lock (_gate)
            {
                if (!_waits.TryGetValue(runId, out var wait))
                    throw new InvalidOperationException($"Execution wait for run '{runId}' was not found.");
                wait.FireAtUtc = DateTime.UtcNow.AddSeconds(-1);
            }
        }

        public Task CreateRunAsync(ExecutionRun run, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_runs.TryAdd(run.Id, Clone(run))) throw new InvalidOperationException($"Execution run '{run.Id}' already exists.");
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryCreateRunWithActiveCapacityAsync(ExecutionRun run, int maxActiveRuns, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_runs.Values.Count(existing => ExecutionRunLifecycle.IsActive(existing.Status)) >= maxActiveRuns)
                    return Task.FromResult(false);
                if (!_runs.TryAdd(run.Id, Clone(run))) throw new InvalidOperationException($"Execution run '{run.Id}' already exists.");
                return Task.FromResult(true);
            }
        }

        public Task<AwsDynamoExecutionRunCreation> CreateRunAtomicallyAsync(
            ExecutionRun run,
            ExecutionRun? capacityRejectedRun,
            int maxActiveRuns,
            string? idempotencyScopeKey,
            CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(idempotencyScopeKey) && _idempotencyReservations.TryGetValue(idempotencyScopeKey, out var existingRunId))
                {
                    var existing = _runs[existingRunId];
                    return Task.FromResult(new AwsDynamoExecutionRunCreation
                    {
                        Created = false,
                        RunId = existing.Id,
                        HandlerId = existing.HandlerId,
                        PayloadHash = existing.PayloadHash
                    });
                }

                var persisted = run;
                if (ExecutionRunLifecycle.IsActive(run.Status) && _runs.Values.Count(existing => ExecutionRunLifecycle.IsActive(existing.Status)) >= maxActiveRuns)
                {
                    persisted = capacityRejectedRun ?? throw new InvalidOperationException("An active execution run requires a capacity rejection shape.");
                }

                if (!_runs.TryAdd(persisted.Id, Clone(persisted))) throw new InvalidOperationException($"Execution run '{persisted.Id}' already exists.");
                if (!string.IsNullOrWhiteSpace(idempotencyScopeKey)) _idempotencyReservations[idempotencyScopeKey] = persisted.Id;
                return Task.FromResult(new AwsDynamoExecutionRunCreation
                {
                    Created = true,
                    RunId = persisted.Id,
                    HandlerId = persisted.HandlerId,
                    PayloadHash = persisted.PayloadHash,
                    CreatedRun = Clone(persisted)
                });
            }
        }

        public Task UpsertRunAsync(ExecutionRun run, CancellationToken ct = default)
        {
            lock (_gate) _runs[run.Id] = Clone(run);
            return Task.CompletedTask;
        }

        public Task<ExecutionRun?> CancelRunAtomicallyAsync(string runId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_runs.TryGetValue(runId, out var run)) return Task.FromResult<ExecutionRun?>(null);
                if (ExecutionRunStatuses.IsTerminal(run.Status)) return Task.FromResult<ExecutionRun?>(Clone(run));

                var now = DateTime.UtcNow;
                var terminal = run.Status is ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting;
                run.CancellationRequested = true;
                run.UpdatedAtUtc = now;
                if (terminal)
                {
                    ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Cancelled);
                    run.Status = ExecutionRunStatuses.Cancelled;
                    run.FailureClass = ExecutionFailureClasses.Cancelled;
                    run.Error = "Execution run was cancelled.";
                    run.CompletedAtUtc = now;
                    run.DurationMs = (now - (run.StartedAtUtc ?? now)).TotalMilliseconds;
                    run.CurrentStep = null;
                    _waits.Remove(run.Id);
                }

                return Task.FromResult<ExecutionRun?>(Clone(run));
            }
        }

        public Task<ExecutionRun?> TryResumeWaitAsync(
            string runId,
            string expectedKind,
            string expectedName,
            ExecutionWaitResult outcome,
            string? consumedEventId = null,
            CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_runs.TryGetValue(runId, out var run) || !_waits.TryGetValue(runId, out var wait) ||
                    run.Status != ExecutionRunStatuses.Waiting || wait.Kind != expectedKind || wait.Name != expectedName)
                    return Task.FromResult<ExecutionRun?>(null);
                if (!string.IsNullOrWhiteSpace(consumedEventId) && !_consumedEvents.Add(consumedEventId))
                    return Task.FromResult<ExecutionRun?>(null);

                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Queued);
                run.Status = ExecutionRunStatuses.Queued;
                run.ScheduledAtUtc = null;
                run.CurrentStep = null;
                run.UpdatedAtUtc = DateTime.UtcNow;
                _waits.Remove(runId);
                _waitOutcomes[WaitOutcomeKey(runId, expectedKind, expectedName)] = Clone(outcome);
                return Task.FromResult<ExecutionRun?>(Clone(run));
            }
        }

        public Task<int> GetActiveRunCountAsync(CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult(_runs.Values.Count(run => ExecutionRunLifecycle.IsActive(run.Status)));
        }

        public Task<IReadOnlyList<string>> ListDueExternalRunIdsAsync(IEnumerable<string> handlerIds, int limit, CancellationToken ct = default)
        {
            var handlers = new HashSet<string>(handlerIds, StringComparer.Ordinal);
            lock (_gate)
            {
                IReadOnlyList<string> ids = _runs.Values
                    .Where(run => handlers.Contains(run.HandlerId))
                    .Where(IsDue)
                    .OrderBy(run => run.ScheduledAtUtc ?? run.UpdatedAtUtc)
                    .ThenBy(run => run.Id, StringComparer.Ordinal)
                    .Take(limit)
                    .Select(run => run.Id)
                    .ToList();
                return Task.FromResult(ids);
            }
        }

        public Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_runs.TryGetValue(runId, out var run)) return Task.FromResult<ExecutionRun?>(null);
                var clone = Clone(run);
                if (!includeResult) clone.Result = null;
                return Task.FromResult<ExecutionRun?>(clone);
            }
        }

        public Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default)
        {
            query ??= new ExecutionRunQuery();
            lock (_gate)
            {
                var limit = query.Limit ?? 100;
                IReadOnlyList<ExecutionRun> results = _runs.Values
                    .Where(run => Matches(run, query))
                    .OrderByDescending(run => run.CreatedAtUtc)
                    .ThenBy(run => run.Id, StringComparer.Ordinal)
                    .Take(limit)
                    .Select(Clone)
                    .Select(run =>
                    {
                        if (!query.IncludeResult) run.Result = null;
                        return run;
                    })
                    .ToList();
                return Task.FromResult(results);
            }
        }

        public Task AppendHistoryAsync(ExecutionTraceEvent item, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_history.TryGetValue(item.RunId, out var items))
                {
                    items = new List<ExecutionTraceEvent>();
                    _history[item.RunId] = items;
                }

                items.Add(Clone(item));
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, int limit = 100, CancellationToken ct = default)
        {
            lock (_gate)
            {
                IReadOnlyList<ExecutionTraceEvent> result = _history.GetValueOrDefault(runId, [])
                    .OrderBy(item => item.TimestampUtc)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Take(limit)
                    .Select(Clone)
                    .ToList();
                return Task.FromResult(result);
            }
        }

        public Task PutCheckpointAsync(ExecutionCheckpoint checkpoint, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_checkpoints.TryGetValue(checkpoint.RunId, out var values))
                {
                    values = new Dictionary<string, ExecutionCheckpoint>(StringComparer.Ordinal);
                    _checkpoints[checkpoint.RunId] = values;
                }

                values[checkpoint.Key] = Clone(checkpoint);
            }

            return Task.CompletedTask;
        }

        public Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_checkpoints.TryGetValue(runId, out var values) && values.TryGetValue(key, out var checkpoint)
                    ? Clone(checkpoint)
                    : null);
            }
        }

        public Task PutArtifactAsync(ExecutionArtifact artifact, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_artifacts.TryGetValue(artifact.RunId, out var values))
                {
                    values = new List<ExecutionArtifact>();
                    _artifacts[artifact.RunId] = values;
                }

                values.RemoveAll(item => item.Id == artifact.Id);
                values.Add(Clone(artifact));
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                IReadOnlyList<ExecutionArtifact> result = _artifacts.GetValueOrDefault(runId, [])
                    .OrderBy(item => item.CreatedAtUtc)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Select(Clone)
                    .ToList();
                return Task.FromResult(result);
            }
        }

        public async Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default)
        {
            var artifacts = await ListArtifactsAsync(runId, ct);
            return artifacts.FirstOrDefault(item => item.Id == artifactRef || item.Name == artifactRef);
        }

        public Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_leases.TryGetValue(request.LeaseKey, out var existing) && existing.ExpiresAtUtc > DateTime.UtcNow && existing.OwnerId != request.OwnerId)
                    return Task.FromResult<ExecutionLease?>(null);
                var now = DateTime.UtcNow;
                var lease = new ExecutionLease
                {
                    LeaseKey = request.LeaseKey,
                    OwnerId = request.OwnerId,
                    RunId = request.RunId,
                    AcquiredAtUtc = now,
                    ExpiresAtUtc = now.AddSeconds(request.TtlSeconds),
                    Metadata = Clone(request.Metadata)
                };
                _leases[lease.LeaseKey] = lease;
                return Task.FromResult<ExecutionLease?>(Clone(lease));
            }
        }

        public Task<AwsDynamoExecutionLeaseClaim?> TryClaimExternalRunAsync(string runId, ExecutionLeaseRequest request, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_runs.TryGetValue(runId, out var run)) return Task.FromResult<AwsDynamoExecutionLeaseClaim?>(null);
                var now = DateTime.UtcNow;
                _leases.TryGetValue(request.LeaseKey, out var existingLease);
                if (existingLease is not null && existingLease.ExpiresAtUtc > now) return Task.FromResult<AwsDynamoExecutionLeaseClaim?>(null);

                var recovered = false;
                if (run.Status == ExecutionRunStatuses.Running)
                {
                    ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Queued, ExecutionTransitionKind.Recovery);
                    run.Status = ExecutionRunStatuses.Queued;
                    run.CurrentStep = null;
                    recovered = true;
                }
                else if (!IsDue(run))
                {
                    return Task.FromResult<AwsDynamoExecutionLeaseClaim?>(null);
                }

                if (run.CancellationRequested)
                {
                    ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Cancelled);
                    run.Status = ExecutionRunStatuses.Cancelled;
                    run.FailureClass = ExecutionFailureClasses.Cancelled;
                    run.Error = "Execution run was cancelled.";
                    run.CompletedAtUtc = now;
                    run.UpdatedAtUtc = now;
                    return Task.FromResult<AwsDynamoExecutionLeaseClaim?>(null);
                }

                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Running);
                run.Status = ExecutionRunStatuses.Running;
                run.Attempt++;
                run.StartedAtUtc ??= now;
                run.UpdatedAtUtc = now;
                var lease = new ExecutionLease
                {
                    LeaseKey = request.LeaseKey,
                    OwnerId = request.OwnerId,
                    RunId = run.Id,
                    AcquiredAtUtc = now,
                    ExpiresAtUtc = now.AddSeconds(request.TtlSeconds),
                    Metadata = Clone(request.Metadata)
                };
                _leases[lease.LeaseKey] = lease;
                return Task.FromResult<AwsDynamoExecutionLeaseClaim?>(new AwsDynamoExecutionLeaseClaim
                {
                    Run = Clone(run),
                    Lease = Clone(lease),
                    Recovered = recovered
                });
            }
        }

        public Task<ExecutionLease?> RenewLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_leases.TryGetValue(request.LeaseKey, out var lease) || lease.OwnerId != request.OwnerId || lease.ExpiresAtUtc <= DateTime.UtcNow)
                    return Task.FromResult<ExecutionLease?>(null);
                lease.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(request.TtlSeconds);
                lease.Metadata = request.Metadata is null ? lease.Metadata : Clone(request.Metadata);
                return Task.FromResult<ExecutionLease?>(Clone(lease));
            }
        }

        public Task<ExecutionRun> UpdateExternalRunUnderLeaseAsync(string leaseKey, string ownerId, ExecutionRunUpdate update, CancellationToken ct = default)
        {
            lock (_gate)
            {
                RequireLease(leaseKey, ownerId, null);
                var run = _runs[_leases[leaseKey].RunId!];
                if (run.Status != ExecutionRunStatuses.Running) throw new InvalidOperationException("External worker run is not running.");
                ApplyUpdate(run, update);
                run.UpdatedAtUtc = DateTime.UtcNow;
                return Task.FromResult(Clone(run));
            }
        }

        public Task AppendHistoryUnderLeaseAsync(string leaseKey, string ownerId, ExecutionTraceEvent item, CancellationToken ct = default)
        {
            lock (_gate)
            {
                RequireLease(leaseKey, ownerId, item.RunId);
                if (!_history.TryGetValue(item.RunId, out var items)) _history[item.RunId] = items = new List<ExecutionTraceEvent>();
                items.Add(Clone(item));
                return Task.CompletedTask;
            }
        }

        public Task PutArtifactUnderLeaseAsync(string leaseKey, string ownerId, ExecutionArtifact artifact, CancellationToken ct = default)
        {
            lock (_gate)
            {
                RequireLease(leaseKey, ownerId, artifact.RunId);
                if (!_artifacts.TryGetValue(artifact.RunId, out var items)) _artifacts[artifact.RunId] = items = new List<ExecutionArtifact>();
                items.RemoveAll(item => item.Id == artifact.Id);
                items.Add(Clone(artifact));
                return Task.CompletedTask;
            }
        }

        public Task PutCheckpointUnderLeaseAsync(string leaseKey, string ownerId, ExecutionCheckpoint checkpoint, CancellationToken ct = default)
        {
            lock (_gate)
            {
                RequireLease(leaseKey, ownerId, checkpoint.RunId);
            }

            return PutCheckpointAsync(checkpoint, ct);
        }

        public Task<ExecutionCheckpoint?> GetCheckpointUnderLeaseAsync(string leaseKey, string ownerId, string key, CancellationToken ct = default)
        {
            lock (_gate)
            {
                RequireLease(leaseKey, ownerId, null);
                var runId = _leases[leaseKey].RunId!;
                return Task.FromResult(_checkpoints.TryGetValue(runId, out var values) && values.TryGetValue(key, out var checkpoint)
                    ? Clone(checkpoint)
                    : null);
            }
        }

        public Task<ExecutionWaitResult?> TakeWaitOutcomeUnderLeaseAsync(string leaseKey, string ownerId, string runId, string kind, string name, CancellationToken ct = default)
        {
            lock (_gate)
            {
                RequireLease(leaseKey, ownerId, runId);
                var outcomeKey = WaitOutcomeKey(runId, kind, name);
                return Task.FromResult(_waitOutcomes.Remove(outcomeKey, out var outcome) ? Clone(outcome) : null);
            }
        }

        public Task<ExecutionExternalEvent?> TakeExternalEventUnderLeaseAsync(string leaseKey, string ownerId, string runId, string name, CancellationToken ct = default)
        {
            lock (_gate)
            {
                RequireLease(leaseKey, ownerId, runId);
                var externalEvent = _events.GetValueOrDefault(runId, [])
                    .Where(item => item.Name == name && !_consumedEvents.Contains(item.Id))
                    .OrderBy(item => item.RaisedAtUtc)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (externalEvent is null) return Task.FromResult<ExecutionExternalEvent?>(null);
                _consumedEvents.Add(externalEvent.Id);
                return Task.FromResult<ExecutionExternalEvent?>(Clone(externalEvent));
            }
        }

        public Task<ExecutionRun> SuspendExternalRunUnderLeaseAsync(string leaseKey, string ownerId, AwsDynamoExecutionWait wait, CancellationToken ct = default)
        {
            lock (_gate)
            {
                RequireLease(leaseKey, ownerId, wait.RunId);
                var run = _runs[wait.RunId];
                if (run.Status != ExecutionRunStatuses.Running) throw new InvalidOperationException("External worker run is not running.");
                if (wait.Timer is not null)
                {
                    if (!_timers.TryGetValue(run.Id, out var timers)) _timers[run.Id] = timers = new List<ExecutionTimer>();
                    timers.RemoveAll(timer => timer.Id == wait.Timer.Id);
                    timers.Add(Clone(wait.Timer));
                }

                _waits[run.Id] = Clone(wait);
                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.DurableWait);
                run.Status = ExecutionRunStatuses.Waiting;
                run.ScheduledAtUtc = wait.FireAtUtc;
                run.CurrentStep = $"waiting:{wait.Kind}:{wait.Name}";
                run.UpdatedAtUtc = DateTime.UtcNow;
                _leases.Remove(leaseKey);
                return Task.FromResult(Clone(run));
            }
        }

        public Task<AwsDynamoExecutionExternalCompletion> CompleteExternalRunUnderLeaseAsync(string leaseKey, string ownerId, ExecutionRunResult result, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_leases.TryGetValue(leaseKey, out var lease) || lease.OwnerId != ownerId || lease.RunId is null)
                    throw new InvalidOperationException("External worker lease is no longer active.");
                var run = _runs[lease.RunId];
                if (IsCompletedLease(lease) && (ExecutionRunStatuses.IsTerminal(run.Status) || run.Status == ExecutionRunStatuses.Waiting))
                    return Task.FromResult(new AwsDynamoExecutionExternalCompletion { Run = Clone(run), AlreadyCompleted = true });
                RequireLease(leaseKey, ownerId, run.Id);
                if (run.Status != ExecutionRunStatuses.Running) throw new InvalidOperationException("External worker run is not running.");

                var now = DateTime.UtcNow;
                var terminal = run.CancellationRequested && result.Status != ExecutionRunStatuses.TimedOut
                    ? ExecutionRunStatuses.Cancelled
                    : result.Status;
                ExecutionRunLifecycle.EnsureTransition(run.Status, terminal);
                run.Status = terminal;
                run.Result = Clone(result.Result);
                run.StatusDetails = Clone(result.StatusDetails);
                run.FailureClass = terminal == ExecutionRunStatuses.Cancelled ? ExecutionFailureClasses.Cancelled : result.FailureClass;
                run.Error = terminal == ExecutionRunStatuses.Cancelled ? "Execution run was cancelled." : result.Error;
                var retryScheduled = !run.CancellationRequested && (terminal is ExecutionRunStatuses.Failed or ExecutionRunStatuses.TimedOut) && run.Attempt < Math.Max(1, run.MaxAttempts);
                if (retryScheduled)
                {
                    ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.Retry);
                    run.Status = ExecutionRunStatuses.Waiting;
                    run.ScheduledAtUtc = now.Add(RetryDelay(run));
                    run.UpdatedAtUtc = now;
                    run.CurrentStep = null;
                }
                else
                {
                    run.CompletedAtUtc = now;
                    run.UpdatedAtUtc = now;
                    run.DurationMs = (now - (run.StartedAtUtc ?? now)).TotalMilliseconds;
                    if (run.Status == ExecutionRunStatuses.Succeeded) run.Progress = 1;
                }

                lease.ExpiresAtUtc = now;
                lease.Metadata ??= new JsonObject();
                lease.Metadata["state"] = "completed";
                return Task.FromResult(new AwsDynamoExecutionExternalCompletion { Run = Clone(run), RetryScheduled = retryScheduled });
            }
        }

        public Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_leases.TryGetValue(leaseKey, out var lease) || lease.OwnerId != ownerId) return Task.FromResult(false);
                _leases.Remove(leaseKey);
                return Task.FromResult(true);
            }
        }

        public Task<ExecutionLease?> GetLeaseAsync(string leaseKey, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult(_leases.TryGetValue(leaseKey, out var lease) ? Clone(lease) : null);
        }

        public Task PutTimerAsync(ExecutionTimer timer, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var runId = timer.RunId ?? throw new InvalidOperationException("Timers must be run-owned.");
                if (!_timers.TryGetValue(runId, out var values))
                {
                    values = new List<ExecutionTimer>();
                    _timers[runId] = values;
                }

                values.RemoveAll(item => item.Id == timer.Id);
                values.Add(Clone(timer));
            }

            return Task.CompletedTask;
        }

        public Task PutExternalEventAsync(ExecutionExternalEvent externalEvent, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var runId = externalEvent.RunId ?? throw new InvalidOperationException("Events must be run-owned.");
                if (!_events.TryGetValue(runId, out var values))
                {
                    values = new List<ExecutionExternalEvent>();
                    _events[runId] = values;
                }

                values.Add(Clone(externalEvent));
            }

            return Task.CompletedTask;
        }

        public Task PutWaitAsync(AwsDynamoExecutionWait wait, CancellationToken ct = default)
        {
            lock (_gate) _waits[wait.RunId] = Clone(wait);
            return Task.CompletedTask;
        }

        public Task<AwsDynamoExecutionWait?> GetWaitAsync(string runId, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult(_waits.TryGetValue(runId, out var wait) ? Clone(wait) : null);
        }

        public Task DeleteWaitAsync(string runId, CancellationToken ct = default)
        {
            lock (_gate) _waits.Remove(runId);
            return Task.CompletedTask;
        }

        public Task PutWaitOutcomeAsync(string runId, AwsDynamoExecutionWait wait, ExecutionWaitResult outcome, CancellationToken ct = default)
        {
            lock (_gate) _waitOutcomes[WaitOutcomeKey(runId, wait.Kind, wait.Name)] = Clone(outcome);
            return Task.CompletedTask;
        }

        public Task<ExecutionWaitResult?> TakeWaitOutcomeAsync(string runId, string kind, string name, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var key = WaitOutcomeKey(runId, kind, name);
                if (!_waitOutcomes.Remove(key, out var outcome)) return Task.FromResult<ExecutionWaitResult?>(null);
                return Task.FromResult<ExecutionWaitResult?>(Clone(outcome));
            }
        }

        public Task<ExecutionExternalEvent?> TakeExternalEventAsync(string runId, string name, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var externalEvent = _events.GetValueOrDefault(runId, [])
                    .Where(item => item.Name == name && !_consumedEvents.Contains(item.Id))
                    .OrderBy(item => item.RaisedAtUtc)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (externalEvent is null) return Task.FromResult<ExecutionExternalEvent?>(null);
                _consumedEvents.Add(externalEvent.Id);
                return Task.FromResult<ExecutionExternalEvent?>(Clone(externalEvent));
            }
        }

        public Task<AwsDynamoExecutionRunDeletion> DeleteRunAsync(ExecutionRun run, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _runs.Remove(run.Id);
                if (!string.IsNullOrWhiteSpace(run.IdempotencyKey))
                {
                    var scopeKey = string.Join("\n", run.Scope?.ProductId ?? string.Empty, run.Scope?.TenantId ?? string.Empty, run.HandlerId, run.IdempotencyKey);
                    _idempotencyReservations.Remove(scopeKey);
                }
                var result = new AwsDynamoExecutionRunDeletion
                {
                    RunId = run.Id,
                    Runs = 1,
                    History = _history.Remove(run.Id, out var history) ? history.Count : 0,
                    Artifacts = _artifacts.Remove(run.Id, out var artifacts) ? artifacts.Count : 0,
                    Checkpoints = _checkpoints.Remove(run.Id, out var checkpoints) ? checkpoints.Count : 0,
                    Timers = _timers.Remove(run.Id, out var timers) ? timers.Count : 0,
                    ExternalEvents = _events.Remove(run.Id, out var events) ? events.Count : 0
                };
                _waits.Remove(run.Id);
                foreach (var key in _waitOutcomes.Keys.Where(key => key.StartsWith(run.Id + "\n", StringComparison.Ordinal)).ToList()) _waitOutcomes.Remove(key);
                foreach (var key in _leases.Where(item => item.Value.RunId == run.Id).Select(item => item.Key).ToList()) _leases.Remove(key);
                return Task.FromResult(result);
            }
        }

        private void RequireLease(string leaseKey, string ownerId, string? runId)
        {
            if (!_leases.TryGetValue(leaseKey, out var lease) || lease.OwnerId != ownerId ||
                (runId is not null && lease.RunId != runId) || lease.ExpiresAtUtc <= DateTime.UtcNow)
                throw new InvalidOperationException("External worker lease is no longer active.");
        }

        private static bool IsCompletedLease(ExecutionLease lease) =>
            string.Equals(lease.Metadata?["state"]?.GetValue<string>(), "completed", StringComparison.Ordinal);

        private static void ApplyUpdate(ExecutionRun run, ExecutionRunUpdate update)
        {
            run.Requested = update.Requested ?? run.Requested;
            run.Attempted = update.Attempted ?? run.Attempted;
            run.Succeeded = update.Succeeded ?? run.Succeeded;
            run.Failed = update.Failed ?? run.Failed;
            run.Progress = update.Progress.HasValue ? Math.Clamp(update.Progress.Value, 0, 1) : run.Progress;
            run.CurrentStep = update.CurrentStep ?? run.CurrentStep;
            run.FailureClass = update.FailureClass ?? run.FailureClass;
            run.Error = update.Error ?? run.Error;
            run.Result = update.Result is null ? run.Result : Clone(update.Result);
            run.StatusDetails = update.StatusDetails is null ? run.StatusDetails : Clone(update.StatusDetails);
        }

        private static TimeSpan RetryDelay(ExecutionRun run)
        {
            var policy = run.RetryPolicy ?? new ExecutionRetryPolicy();
            var seconds = Math.Min(
                Math.Max(policy.InitialDelaySeconds, 0) * Math.Pow(Math.Max(policy.BackoffMultiplier, 1), Math.Max(0, run.Attempt - 1)),
                Math.Max(policy.InitialDelaySeconds, policy.MaxDelaySeconds));
            return TimeSpan.FromSeconds(seconds);
        }

        private static bool IsDue(ExecutionRun run) =>
            run.Status == ExecutionRunStatuses.Queued ||
            (run.Status == ExecutionRunStatuses.Waiting && run.ScheduledAtUtc.HasValue && run.ScheduledAtUtc <= DateTime.UtcNow);

        private static bool Matches(ExecutionRun run, ExecutionRunQuery query) =>
            (string.IsNullOrWhiteSpace(query.HandlerId) || run.HandlerId == query.HandlerId) &&
            (string.IsNullOrWhiteSpace(query.PluginId) || run.PluginId == query.PluginId) &&
            (string.IsNullOrWhiteSpace(query.Status) || run.Status == query.Status) &&
            (string.IsNullOrWhiteSpace(query.CorrelationId) || run.CorrelationId == query.CorrelationId) &&
            (string.IsNullOrWhiteSpace(query.IdempotencyKey) || run.IdempotencyKey == query.IdempotencyKey) &&
            (!query.CreatedAfterUtc.HasValue || run.CreatedAtUtc >= query.CreatedAfterUtc) &&
            (!query.CreatedBeforeUtc.HasValue || run.CreatedAtUtc <= query.CreatedBeforeUtc) &&
            (!query.UpdatedAfterUtc.HasValue || run.UpdatedAtUtc >= query.UpdatedAfterUtc) &&
            (!query.UpdatedBeforeUtc.HasValue || run.UpdatedAtUtc <= query.UpdatedBeforeUtc) &&
            query.Tags.All(filter => run.Tags.TryGetValue(filter.Key, out var value) && value == filter.Value);

        private static string WaitOutcomeKey(string runId, string kind, string name) => runId + "\n" + kind + "\n" + name;
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ExecutionJson.Options), ExecutionJson.Options)!;
}
