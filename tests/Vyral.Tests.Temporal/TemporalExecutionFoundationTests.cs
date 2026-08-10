using System.Reflection;
using System.Text.Json;
using Vyral.Execution;
using Vyral.Execution.Temporal;
using Vyral.Execution.Temporal.Hosting;

namespace Vyral.Tests.Temporal;

public sealed class TemporalExecutionFoundationTests
{
    [Fact]
    public void TemporalOptions_ValidateSecureTopologyAndEmitOnlyHashedProviderNames()
    {
        var options = new TemporalExecutionOptions
        {
            TargetHost = "temporal.example.invalid:7233",
            Namespace = "vyral-production",
            TaskQueue = "vyral-workers",
            WorkerDeploymentName = "vyral-production-workers",
            WorkerBuildId = "release-2026-07-28",
            AdapterNamespace = "production"
        };

        options.Validate();
        var metadata = options.ToDiagnosticMetadata();

        Assert.Equal("production", metadata["adapterNamespace"]);
        Assert.Equal(16, metadata["targetHostHash"].Length);
        Assert.Equal(16, metadata["namespaceHash"].Length);
        Assert.Equal(16, metadata["taskQueueHash"].Length);
        Assert.Equal(16, metadata["workerDeploymentNameHash"].Length);
        Assert.Equal(16, metadata["artifactObjectContainerHash"].Length);
        Assert.DoesNotContain(options.TargetHost, string.Join('|', metadata.Values), StringComparison.Ordinal);
        Assert.DoesNotContain(options.Namespace, string.Join('|', metadata.Values), StringComparison.Ordinal);
        Assert.DoesNotContain(options.TaskQueue, string.Join('|', metadata.Values), StringComparison.Ordinal);
        Assert.DoesNotContain(options.WorkerDeploymentName, string.Join('|', metadata.Values), StringComparison.Ordinal);
        Assert.DoesNotContain(options.WorkerBuildId, string.Join('|', metadata.Values), StringComparison.Ordinal);
        Assert.DoesNotContain(options.ArtifactObjectContainer, string.Join('|', metadata.Values), StringComparison.Ordinal);
    }

    [Fact]
    public void TemporalOptions_AllowInsecureLoopbackOnlyAndRejectCredentialBearingTargets()
    {
        new TemporalExecutionOptions { TargetHost = "127.0.0.1:7233", RequireTls = false }.Validate();

        Assert.Throws<InvalidOperationException>(() =>
            new TemporalExecutionOptions { TargetHost = "temporal.example.invalid:7233", RequireTls = false }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new TemporalExecutionOptions { TargetHost = "https://temporal.example.invalid:7233" }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new TemporalExecutionOptions { TargetHost = "user:secret@temporal.example.invalid:7233" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new TemporalExecutionOptions
        {
            TargetHost = "127.0.0.1:7233",
            RequireTls = false,
            ArtifactObjectContainer = "Invalid_Container"
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new TemporalExecutionOptions
        {
            TargetHost = "127.0.0.1:7233",
            RequireTls = false,
            WorkerDeploymentName = "invalid.deployment"
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new TemporalExecutionOptions
        {
            TargetHost = "127.0.0.1:7233",
            RequireTls = false,
            WorkerBuildId = "\nsecret"
        }.Validate());
    }

    [Fact]
    public void TemporalIdentity_IsDeterministicAndContainsNoPortableRequestContent()
    {
        var first = TemporalExecutionIdentity.CreateWorkflowId("preview", "00970374978807572400");
        var second = TemporalExecutionIdentity.CreateWorkflowId("preview", "00970374978807572400");

        Assert.Equal("vyral:preview:00970374978807572400", first);
        Assert.Equal(first, second);
        Assert.Throws<InvalidOperationException>(() => TemporalExecutionIdentity.CreateWorkflowId("preview", "tenant-a:run-1"));
    }

    [Fact]
    public void CoordinatorEnvelopes_SerializeOnlyCoordinationReferences()
    {
        var signal = new TemporalCoordinatorSignal { EventId = "event-1", EventRevision = 3 };
        var input = new TemporalCoordinatorInput
        {
            RunId = "run-1",
            ProjectionRevision = 2,
            Generation = 1,
            BufferedSignals = [signal]
        };
        var json = JsonSerializer.Serialize(new { input, signal });

        Assert.Equal(
            "{\"input\":{\"runId\":\"run-1\",\"projectionRevision\":2,\"generation\":1," +
            "\"attemptOffset\":0,\"coordinationTransitions\":0,\"bufferedSignals\":[{" +
            "\"eventId\":\"event-1\",\"eventRevision\":3}]},\"signal\":{" +
            "\"eventId\":\"event-1\",\"eventRevision\":3}}",
            json);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("artifact", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoordinatorStateMachine_RejectsAmbiguousOrUnboundedOutcomes()
    {
        TemporalRunCoordinatorWorkflow.ValidateOutcome(new TemporalExecutionAttemptOutcome
        {
            Disposition = TemporalAttemptDispositions.Suspended,
            WaitId = "wait-1",
            WaitKind = TemporalWaitKinds.ExternalEvent
        });
        TemporalRunCoordinatorWorkflow.ValidateOutcome(new TemporalExecutionAttemptOutcome
        {
            Disposition = TemporalAttemptDispositions.Retryable,
            RetryDelayMilliseconds = 500
        });

        Assert.Throws<InvalidOperationException>(() => TemporalRunCoordinatorWorkflow.ValidateOutcome(new TemporalExecutionAttemptOutcome
        {
            Disposition = TemporalAttemptDispositions.Suspended,
            WaitKind = TemporalWaitKinds.Timer
        }));
        Assert.Throws<InvalidOperationException>(() => TemporalRunCoordinatorWorkflow.ValidateOutcome(new TemporalExecutionAttemptOutcome
        {
            Disposition = TemporalAttemptDispositions.Retryable,
            RetryDelayMilliseconds = 0
        }));
    }

    [Fact]
    public void CoordinatorActivities_HeartbeatAndRetryTransportWithoutChangingPortableAttempts()
    {
        var options = TemporalRunCoordinatorWorkflow.CreateActivityOptions(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(10), options.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatTimeout);
        Assert.Equal(100, options.RetryPolicy!.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RetryPolicy.MaximumInterval);
    }

    [Fact]
    public void CoordinatorContinueAsNew_PreservesPortableOffsetsAndBufferedSignalIdentities()
    {
        Assert.Equal(
            "vyral-run-coordinator-continue-as-new-v1",
            TemporalRunCoordinatorWorkflow.ContinueAsNewPatchId);
        Assert.False(TemporalRunCoordinatorWorkflow.ShouldContinueAsNew(0, serverSuggested: true));
        Assert.False(TemporalRunCoordinatorWorkflow.ShouldContinueAsNew(
            TemporalRunCoordinatorWorkflow.ContinueAsNewTransitionThreshold - 1,
            serverSuggested: false));
        Assert.True(TemporalRunCoordinatorWorkflow.ShouldContinueAsNew(
            TemporalRunCoordinatorWorkflow.ContinueAsNewTransitionThreshold,
            serverSuggested: false));
        Assert.True(TemporalRunCoordinatorWorkflow.ShouldContinueAsNew(1, serverSuggested: true));

        var input = new TemporalCoordinatorInput
        {
            RunId = "run-1",
            ProjectionRevision = 7,
            Generation = 2
        };
        var continuation = TemporalRunCoordinatorWorkflow.CreateContinuationInput(
            input,
            attemptOffset: 16,
            coordinationTransitions: 32,
            [new TemporalCoordinatorSignal { EventId = "event-1", EventRevision = 3 }]);

        Assert.Equal(input.RunId, continuation.RunId);
        Assert.Equal(input.ProjectionRevision, continuation.ProjectionRevision);
        Assert.Equal(input.Generation, continuation.Generation);
        Assert.Equal(16, continuation.AttemptOffset);
        Assert.Equal(32, continuation.CoordinationTransitions);
        var signal = Assert.Single(continuation.BufferedSignals);
        Assert.Equal("event-1", signal.EventId);
        Assert.Equal(3, signal.EventRevision);
    }

    [Fact]
    public async Task ProjectionActivityExecutor_SeparatesHandlerExecutionFromProjectionWrites()
    {
        var attempt = new TemporalExecutionAttemptRequest { RunId = "run-1", Generation = 1, Attempt = 2 };
        var outcome = new TemporalExecutionAttemptOutcome
        {
            Disposition = TemporalAttemptDispositions.Completed,
            TerminalStatus = ExecutionRunStatuses.Succeeded
        };
        var handler = new RecordingAttemptHandler(outcome);
        var projection = new RecordingProjectionStore();
        var executor = new TemporalProjectionActivityExecutor(handler, projection);
        var resolution = new TemporalExecutionWaitResolution
        {
            RunId = "run-1",
            Generation = 1,
            WaitId = "wait-1",
            Resolution = TemporalWaitResolutions.Timer
        };
        var cancellation = new TemporalExecutionCancellation { RunId = "run-1", Generation = 1 };

        Assert.Same(outcome, await executor.ExecuteAttemptAsync(attempt));
        Assert.True((await executor.ProjectWaitResolutionAsync(resolution)).Accepted);
        await executor.ProjectCancellationAsync(cancellation);

        Assert.Same(attempt, handler.Request);
        Assert.Same(resolution, projection.Resolution);
        Assert.Same(cancellation, projection.Cancellation);
    }

    [Fact]
    public async Task DispatchReconciler_TreatsMatchingAlreadyStartedWorkflowAsDelivered()
    {
        var dispatch = CreateDispatch();
        var outbox = new RecordingOutbox(dispatch);
        var client = new StubCoordinatorClient
        {
            Start = _ => throw new TemporalWorkflowAlreadyStartedException(),
            Identity = new TemporalWorkflowIdentity { RunId = dispatch.RunId, Generation = dispatch.Generation }
        };
        var reconciler = new TemporalExecutionDispatchReconciler(outbox, client, "preview");

        var result = await reconciler.ReconcileAsync();

        Assert.Equal(1, result.Delivered);
        Assert.Equal(0, result.Failed);
        Assert.True(Assert.Single(result.Items).ReplayedExistingWorkflow);
        Assert.Single(outbox.Delivered);
        Assert.Empty(outbox.Failures);
    }

    [Fact]
    public async Task DispatchReconciler_RejectsAmbiguousWorkflowIdentityWithoutSecondRun()
    {
        var dispatch = CreateDispatch();
        var outbox = new RecordingOutbox(dispatch);
        var client = new StubCoordinatorClient
        {
            Start = _ => throw new TemporalWorkflowAlreadyStartedException(),
            Identity = new TemporalWorkflowIdentity { RunId = "different-run", Generation = dispatch.Generation }
        };
        var reconciler = new TemporalExecutionDispatchReconciler(outbox, client, "preview");

        var result = await reconciler.ReconcileAsync();

        Assert.Equal(0, result.Delivered);
        Assert.Equal(1, result.Failed);
        Assert.Equal(TemporalDispatchFailureClasses.IdentityConflict, Assert.Single(result.Items).FailureClass);
        Assert.Empty(outbox.Delivered);
        Assert.Equal(TemporalDispatchFailureClasses.IdentityConflict, Assert.Single(outbox.Failures).FailureClass);
        Assert.Equal(1, client.StartCalls);
    }

    [Fact]
    public async Task DispatchReconciler_MapsDescribeFailureWithoutLeakingProviderException()
    {
        var dispatch = CreateDispatch();
        var outbox = new RecordingOutbox(dispatch);
        var client = new StubCoordinatorClient
        {
            Start = _ => throw new TemporalWorkflowAlreadyStartedException(),
            GetIdentity = _ => throw new InvalidOperationException("endpoint and credential detail")
        };
        var reconciler = new TemporalExecutionDispatchReconciler(outbox, client, "preview");

        var result = await reconciler.ReconcileAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal(TemporalDispatchFailureClasses.Unknown, item.FailureClass);
        Assert.DoesNotContain("endpoint", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, client.StartCalls);
    }

    [Fact]
    public async Task SignalReconciler_DeliversOnlyDurableEventReferences()
    {
        var dispatch = new TemporalSignalDispatch
        {
            DispatchId = "signal-1",
            RunId = "run-1",
            WorkflowId = TemporalExecutionIdentity.CreateWorkflowId("preview", "run-1"),
            Generation = 1,
            EventId = "event-1",
            EventRevision = 2,
            AttemptCount = 0
        };
        var outbox = new RecordingSignalOutbox(dispatch);
        var client = new StubCoordinatorClient { Start = _ => throw new NotSupportedException() };
        var reconciler = new TemporalExecutionSignalReconciler(outbox, client, "preview");

        var result = await reconciler.ReconcileAsync();

        Assert.Equal(1, result.Delivered);
        Assert.Equal(dispatch, Assert.Single(client.Signals));
        Assert.Equal(dispatch.DispatchId, Assert.Single(outbox.Delivered));
        Assert.Empty(outbox.Failures);
        var json = JsonSerializer.Serialize(client.Signals);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationReconciler_RetriesOnlyDurableCoordinatorIdentity()
    {
        var dispatch = new TemporalCancellationDispatch
        {
            DispatchId = "cancellation-1",
            RunId = "run-1",
            WorkflowId = TemporalExecutionIdentity.CreateWorkflowId("preview", "run-1"),
            Generation = 1,
            AttemptCount = 0
        };
        var outbox = new RecordingCancellationOutbox(dispatch);
        var client = new StubCoordinatorClient { Start = _ => throw new NotSupportedException() };
        var reconciler = new TemporalExecutionCancellationReconciler(outbox, client, "preview");

        var result = await reconciler.ReconcileAsync();

        Assert.Equal(1, result.Delivered);
        Assert.Equal(dispatch.WorkflowId, Assert.Single(client.Cancellations));
        Assert.Equal(dispatch.DispatchId, Assert.Single(outbox.Delivered));
        Assert.Empty(outbox.Failures);
        Assert.DoesNotContain("payload", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PortableExecutionAssembly_HasNoTemporalSdkReferenceOrExportedType()
    {
        var assembly = typeof(IExecutionRuntime).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.StartsWith("Temporalio", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(assembly.ExportedTypes.SelectMany(ReferencedTypes), type =>
            type.Namespace?.StartsWith("Temporalio", StringComparison.Ordinal) == true);
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        yield return type;
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            switch (member)
            {
                case PropertyInfo property:
                    yield return property.PropertyType;
                    break;
                case MethodInfo method:
                    yield return method.ReturnType;
                    foreach (var parameter in method.GetParameters()) yield return parameter.ParameterType;
                    break;
                case FieldInfo field:
                    yield return field.FieldType;
                    break;
            }
        }
    }

    private static TemporalStartDispatch CreateDispatch() => new()
    {
        DispatchId = "dispatch-1",
        RunId = "run-1",
        WorkflowId = TemporalExecutionIdentity.CreateWorkflowId("preview", "run-1"),
        ProjectionRevision = 1,
        Generation = 1,
        AttemptCount = 0
    };

    private sealed class RecordingOutbox(params TemporalStartDispatch[] pending) : ITemporalExecutionDispatchOutbox
    {
        public List<(string DispatchId, TemporalCoordinationReference Reference)> Delivered { get; } = [];
        public List<(string DispatchId, string FailureClass)> Failures { get; } = [];

        public Task<IReadOnlyList<TemporalStartDispatch>> ListPendingStartsAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TemporalStartDispatch>>(pending.Take(limit).ToList());

        public Task MarkStartDeliveredAsync(string dispatchId, TemporalCoordinationReference reference, CancellationToken ct = default)
        {
            Delivered.Add((dispatchId, reference));
            return Task.CompletedTask;
        }

        public Task RecordStartFailureAsync(string dispatchId, string failureClass, CancellationToken ct = default)
        {
            Failures.Add((dispatchId, failureClass));
            return Task.CompletedTask;
        }
    }

    private sealed class StubCoordinatorClient : ITemporalCoordinatorClient
    {
        public required Func<TemporalStartDispatch, TemporalCoordinationReference> Start { get; init; }
        public TemporalWorkflowIdentity? Identity { get; init; }
        public Func<string, TemporalWorkflowIdentity?>? GetIdentity { get; init; }
        public int StartCalls { get; private set; }
        public List<TemporalSignalDispatch> Signals { get; } = [];
        public List<string> Cancellations { get; } = [];

        public Task<TemporalCoordinationReference> StartAsync(TemporalStartDispatch dispatch, CancellationToken ct = default)
        {
            StartCalls++;
            return Task.FromResult(Start(dispatch));
        }

        public Task<TemporalWorkflowIdentity?> GetIdentityAsync(string workflowId, CancellationToken ct = default) =>
            Task.FromResult(GetIdentity is null ? Identity : GetIdentity(workflowId));

        public Task SignalExternalEventAsync(TemporalSignalDispatch dispatch, CancellationToken ct = default)
        {
            Signals.Add(dispatch);
            return Task.CompletedTask;
        }

        public Task RequestCancellationAsync(string workflowId, CancellationToken ct = default)
        {
            Cancellations.Add(workflowId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSignalOutbox(params TemporalSignalDispatch[] pending) : ITemporalExecutionSignalOutbox
    {
        public List<string> Delivered { get; } = [];
        public List<(string DispatchId, string FailureClass)> Failures { get; } = [];

        public Task<IReadOnlyList<TemporalSignalDispatch>> ListPendingSignalsAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TemporalSignalDispatch>>(pending.Take(limit).ToList());

        public Task MarkSignalDeliveredAsync(string dispatchId, CancellationToken ct = default)
        {
            Delivered.Add(dispatchId);
            return Task.CompletedTask;
        }

        public Task RecordSignalFailureAsync(string dispatchId, string failureClass, CancellationToken ct = default)
        {
            Failures.Add((dispatchId, failureClass));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCancellationOutbox(
        params TemporalCancellationDispatch[] pending) : ITemporalExecutionCancellationOutbox
    {
        public List<string> Delivered { get; } = [];
        public List<(string DispatchId, string FailureClass)> Failures { get; } = [];

        public Task<IReadOnlyList<TemporalCancellationDispatch>> ListPendingCancellationsAsync(
            int limit,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TemporalCancellationDispatch>>(pending.Take(limit).ToList());

        public Task MarkCancellationDeliveredAsync(
            string dispatchId,
            CancellationToken ct = default)
        {
            Delivered.Add(dispatchId);
            return Task.CompletedTask;
        }

        public Task RecordCancellationFailureAsync(
            string dispatchId,
            string failureClass,
            CancellationToken ct = default)
        {
            Failures.Add((dispatchId, failureClass));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAttemptHandler(TemporalExecutionAttemptOutcome outcome) : ITemporalExecutionAttemptHandler
    {
        public TemporalExecutionAttemptRequest? Request { get; private set; }

        public Task<TemporalExecutionAttemptOutcome> ExecuteAttemptAsync(
            TemporalExecutionAttemptRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(outcome);
        }
    }

    private sealed class RecordingProjectionStore : ITemporalExecutionProjectionStore
    {
        public TemporalExecutionWaitResolution? Resolution { get; private set; }
        public TemporalExecutionCancellation? Cancellation { get; private set; }

        public Task<TemporalExecutionWaitProjectionResult> ProjectWaitResolutionAsync(
            TemporalExecutionWaitResolution resolution,
            CancellationToken ct = default)
        {
            Resolution = resolution;
            return Task.FromResult(new TemporalExecutionWaitProjectionResult { Accepted = true });
        }

        public Task ProjectCancellationAsync(
            TemporalExecutionCancellation cancellation,
            CancellationToken ct = default)
        {
            Cancellation = cancellation;
            return Task.CompletedTask;
        }

        public Task<TemporalProjectionRunStartResult> CreateRunWithPendingStartAsync(
            TemporalProjectionRunStart start,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task RegisterWaitAsync(
            TemporalProjectionWaitRegistration registration,
            ExecutionTraceEvent registeredEvent,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task PersistExternalEventWithPendingSignalAsync(
            TemporalProjectionExternalEventWrite write,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TemporalStartDispatch>> ListPendingStartsAsync(
            int limit,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task MarkStartDeliveredAsync(
            string dispatchId,
            TemporalCoordinationReference reference,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task RecordStartFailureAsync(
            string dispatchId,
            string failureClass,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TemporalSignalDispatch>> ListPendingSignalsAsync(
            int limit,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task MarkSignalDeliveredAsync(
            string dispatchId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task RecordSignalFailureAsync(
            string dispatchId,
            string failureClass,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
