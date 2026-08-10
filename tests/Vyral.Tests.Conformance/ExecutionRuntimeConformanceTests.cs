using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Tests.Conformance;

public abstract class ExecutionRuntimeConformanceTests
{
    protected abstract Task<IExecutionRuntimeAdapter> CreateRuntimeAsync();

    protected virtual Task<(IExecutionRuntimeAdapter First, IExecutionRuntimeAdapter Second)> CreateRestartableRuntimePairAsync()
    {
        throw new NotSupportedException("This execution adapter conformance fixture does not provide a restartable runtime pair.");
    }

    protected virtual Task DispatchReadyRunsAsync(IExecutionRuntimeAdapter runtime)
    {
        return Task.CompletedTask;
    }

    protected async Task RunExecutionRuntime_ExposesAdapterCapabilities()
    {
        var runtime = await CreateRuntimeAsync();
        var status = await runtime.GetAdapterStatusAsync();

        Assert.True(status.Available);
        Assert.Equal("ok", status.Status);
        Assert.False(string.IsNullOrWhiteSpace(status.Adapter.AdapterId));
        Assert.False(string.IsNullOrWhiteSpace(status.Adapter.RuntimeKind));
        ExecutionContractValidator.ValidateAdapterDescriptor(status.Adapter);
        foreach (var required in ExecutionCapabilityCatalog.RequiredAdapterCapabilities)
        {
            Assert.Contains(required, status.Adapter.Capabilities);
        }

        Assert.True(ExecutionCapabilityCatalog.HasDispatchCapability(status.Adapter.Capabilities));
        Assert.True(ExecutionCapabilityCatalog.HasExecutionModelCapability(status.Adapter.Capabilities));
        Assert.NotNull(status.OperationalPolicy);
        Assert.True(status.OperationalPolicy!.MaxActiveRuns > 0);
        Assert.True(status.OperationalPolicy.DefaultListLimit > 0);
        Assert.True(status.OperationalPolicy.MaxListLimit >= status.OperationalPolicy.DefaultListLimit);
        Assert.True(status.OperationalPolicy.DefaultHistoryLimit > 0);
        Assert.True(status.OperationalPolicy.MaxHistoryLimit >= status.OperationalPolicy.DefaultHistoryLimit);
        Assert.True(status.OperationalPolicy.MaxPayloadBytes > 0);
        Assert.True(status.OperationalPolicy.MaxResultBytes > 0);
        Assert.True(status.OperationalPolicy.MaxArtifactBytes > 0);
        Assert.True(status.OperationalPolicy.MaxTraceMessageChars > 0);
        Assert.False(string.IsNullOrWhiteSpace(status.OperationalPolicy.ConcurrencyKeyPolicy));
        Assert.False(string.IsNullOrWhiteSpace(status.OperationalPolicy.DefaultTraceSeverity));
        Assert.False(string.IsNullOrWhiteSpace(status.OperationalPolicy.RetentionScope));
        Assert.NotNull(status.ResumePolicy);
        Assert.Equal(ExecutionResumePolicyModes.RestartRecovery, status.ResumePolicy!.Mode);
        Assert.Equal(ExecutionResumePolicyBehaviors.MayReexecuteHandler, status.ResumePolicy.InterruptedRunningBehavior);
        Assert.Equal(ExecutionResumePolicyBehaviors.DispatchWhenDue, status.ResumePolicy.ScheduledWaitingBehavior);
        Assert.Equal(ExecutionResumePolicyBehaviors.NeverResume, status.ResumePolicy.TerminalBehavior);
        Assert.Equal(ExecutionResumePolicyBehaviors.PluginOwned, status.ResumePolicy.PluginCheckpointBehavior);
        Assert.Equal("handler_plugin_payload", status.ResumePolicy.IdempotencyScope);
        Assert.False(status.ResumePolicy.CreatesLinkedFollowUpRuns);
    }

    protected async Task RunExecutionRuntime_RunsPluginRecordsProgressArtifactsHistoryAndIdempotency()
    {
        var runtime = await CreateRuntimeAsync();
        runtime.RegisterPlugin(new CountingPlugin());
        var plugins = runtime.ListPlugins();
        Assert.Contains(plugins, plugin =>
            plugin.PluginId == CountingPlugin.PluginId &&
            plugin.Handlers.Any(handler => handler.HandlerId == CountingHandler.HandlerId));

        var first = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            PluginId = CountingPlugin.PluginId,
            IdempotencyKey = "count:alpha",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a", "b", "c")
            }
        });
        var second = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            PluginId = CountingPlugin.PluginId,
            IdempotencyKey = "count:alpha",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a", "b", "c")
            }
        });

        Assert.Equal(first.Id, second.Id);
        Assert.False(first.AdmissionReplayed);
        Assert.True(second.AdmissionReplayed);

        var completed = await WaitForRunAsync(runtime, first.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(3, completed.Requested);
        Assert.Equal(3, completed.Attempted);
        Assert.Equal(1, completed.Progress);
        Assert.Equal(6, completed.Result!["total"]!.GetValue<int>());

        var history = await runtime.GetHistoryAsync(first.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunStarted);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunStatus);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.ArtifactWritten);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.CheckpointWritten);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunCompleted);
        Assert.All(history, item =>
        {
            Assert.True(OrderedId.TryParse(item.Id, out _));
            Assert.True(OrderedId.TryParse(item.SequenceId, out _));
            Assert.Equal(first.Id, item.Context["runId"]);
            Assert.Equal(first.CorrelationId, item.Context["correlationId"]);
            Assert.Equal(CountingHandler.HandlerId, item.Context["handlerId"]);
            Assert.Equal(CountingPlugin.PluginId, item.Context["pluginId"]);
            Assert.False(string.IsNullOrWhiteSpace(item.Context["adapterId"]));
            Assert.False(string.IsNullOrWhiteSpace(item.Context["runtimeKind"]));
            Assert.False(string.IsNullOrWhiteSpace(item.Context["workerId"]));
        });
        Assert.Equal(
            history.Select(item => item.SequenceId).ToList(),
            history.Select(item => item.SequenceId).OrderBy(id => id, StringComparer.Ordinal).ToList());

        var artifacts = await runtime.ListArtifactsAsync(first.Id);
        var summary = Assert.Single(artifacts);
        Assert.Equal("summary", summary.Name);
        Assert.Equal(ExecutionArtifactKinds.Json, summary.Kind);
        Assert.StartsWith("sha256:", summary.ContentHash);
        Assert.True(OrderedId.TryParse(summary.Id, out _));
        Assert.Equal(3, summary.Content!["count"]!.GetValue<int>());

        var artifactByName = await runtime.GetArtifactAsync(first.Id, "summary");
        Assert.NotNull(artifactByName);
        Assert.Equal(summary.Id, artifactByName!.Id);
        Assert.Equal(3, artifactByName.Content!["count"]!.GetValue<int>());

        var artifactById = await runtime.GetArtifactAsync(first.Id, summary.Id);
        Assert.NotNull(artifactById);
        Assert.Equal(summary.Name, artifactById!.Name);
        Assert.Equal(summary.ContentHash, artifactById.ContentHash);
        Assert.Null(await runtime.GetArtifactAsync(first.Id, "missing-artifact"));

        var checkpoint = await runtime.GetCheckpointAsync(first.Id, "progress");
        Assert.NotNull(checkpoint);
        Assert.Equal(first.Id, checkpoint!.RunId);
        Assert.Equal(3, checkpoint.Content!["attempted"]!.GetValue<int>());
        Assert.Equal(6, checkpoint.Content["total"]!.GetValue<int>());
        Assert.StartsWith("sha256:", checkpoint.ContentHash);
    }

    protected async Task RunExecutionRuntime_RejectsMissingHandlersAndPluginMismatches()
    {
        var runtime = await CreateRuntimeAsync();
        runtime.RegisterPlugin(new CountingPlugin());

        var missing = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = "test.missing-handler"
        });

        Assert.Equal(ExecutionRunStatuses.Rejected, missing.Status);
        Assert.Equal(ExecutionFailureClasses.HandlerMissing, missing.FailureClass);
        Assert.True(ExecutionRunStatuses.IsTerminal(missing.Status));
        Assert.NotNull(missing.CompletedAtUtc);

        var mismatch = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            PluginId = "test.other-plugin",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a")
            }
        });

        Assert.Equal(ExecutionRunStatuses.Rejected, mismatch.Status);
        Assert.Equal(ExecutionFailureClasses.PluginMismatch, mismatch.FailureClass);
        Assert.True(ExecutionRunStatuses.IsTerminal(mismatch.Status));
        Assert.Contains(CountingPlugin.PluginId, mismatch.Error);

        var history = await runtime.GetHistoryAsync(mismatch.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunRejected);
    }

    protected async Task RunExecutionRuntime_RejectsIdempotencyKeyReuseForDifferentRequest()
    {
        var runtime = await CreateRuntimeAsync();
        runtime.RegisterPlugin(new CountingPlugin());

        var first = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            PluginId = CountingPlugin.PluginId,
            IdempotencyKey = "count:conflict",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a")
            }
        });

        var duplicate = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            PluginId = CountingPlugin.PluginId,
            IdempotencyKey = "count:conflict",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a")
            }
        });
        Assert.Equal(first.Id, duplicate.Id);
        Assert.True(duplicate.AdmissionReplayed);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = CountingHandler.HandlerId,
                PluginId = CountingPlugin.PluginId,
                IdempotencyKey = "count:conflict",
                Payload = new JsonObject
                {
                    ["items"] = new JsonArray("different")
                }
            }));

        Assert.Contains("idempotency key", error.Message);
    }

    protected async Task RunExecutionRuntime_ExposesConsumerErrorSemantics()
    {
        var runtime = await CreateRuntimeAsync();
        runtime.RegisterPlugin(new CountingPlugin());

        var validationError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = " "
            }));
        Assert.Contains("Handler id", validationError.Message, StringComparison.OrdinalIgnoreCase);

        var missing = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = "test.consumer.missing"
        });
        Assert.Equal(ExecutionRunStatuses.Rejected, missing.Status);
        Assert.Equal(ExecutionFailureClasses.HandlerMissing, missing.FailureClass);
        Assert.True(ExecutionRunStatuses.IsTerminal(missing.Status));

        var mismatch = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            PluginId = "test.consumer.other-plugin",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a")
            }
        });
        Assert.Equal(ExecutionRunStatuses.Rejected, mismatch.Status);
        Assert.Equal(ExecutionFailureClasses.PluginMismatch, mismatch.FailureClass);
        Assert.Contains(CountingPlugin.PluginId, mismatch.Error);

        Assert.Null(await runtime.CancelRunAsync("missing-run"));

        runtime.RegisterHandler(new ThrowingHandler());
        var thrown = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = ThrowingHandler.HandlerId,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 1 }
        });
        var failed = await WaitForRunAsync(runtime, thrown.Id, ExecutionRunStatuses.Failed);
        Assert.Equal(ExecutionFailureClasses.Unknown, failed.FailureClass);
        Assert.Contains("consumer-visible failure", failed.Error);
        var failedHistory = await runtime.GetHistoryAsync(failed.Id);
        Assert.Contains(failedHistory, item => item.Type == ExecutionEventTypes.RunFailed);

        runtime.RegisterHandler(new AlwaysFailHandler());
        var retrying = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = AlwaysFailHandler.HandlerId,
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = 2,
                InitialDelaySeconds = 0.01,
                MaxDelaySeconds = 0.01,
                BackoffMultiplier = 1
            }
        });
        var exhausted = await WaitForRunAsync(runtime, retrying.Id, ExecutionRunStatuses.Failed);
        Assert.Equal(2, exhausted.Attempt);
        Assert.Equal(ExecutionFailureClasses.Transient, exhausted.FailureClass);
        var exhaustedHistory = await runtime.GetHistoryAsync(exhausted.Id);
        Assert.Single(exhaustedHistory, item => item.Type == ExecutionEventTypes.RetryScheduled);
        Assert.Contains(exhaustedHistory, item => item.Type == ExecutionEventTypes.RunFailed);

        runtime.RegisterHandler(new TimeoutOnceHandler());
        var timedOut = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = TimeoutOnceHandler.HandlerId,
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = 1
            }
        });
        var terminalTimeout = await WaitForRunAsync(runtime, timedOut.Id, ExecutionRunStatuses.TimedOut);
        Assert.Equal(ExecutionFailureClasses.Timeout, terminalTimeout.FailureClass);
        Assert.Contains("timed out", terminalTimeout.Error);
        Assert.True(ExecutionRunStatuses.IsTerminal(terminalTimeout.Status));
    }

    protected async Task RunExecutionRuntime_CancelsRunningPlugin()
    {
        var runtime = await CreateRuntimeAsync();
        var handler = new BlockingHandler();
        runtime.RegisterHandler(handler);

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = BlockingHandler.HandlerId,
            Payload = new JsonObject { ["name"] = "cancel-me" }
        });

        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await runtime.GetRunAsync(accepted.Id);
        Assert.Equal(ExecutionRunStatuses.Running, running!.Status);

        var requested = await runtime.CancelRunAsync(accepted.Id);
        Assert.NotNull(requested);
        Assert.True(requested!.CancellationRequested);

        var cancelled = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Cancelled);
        Assert.True(cancelled.CancellationRequested);
        Assert.Equal(ExecutionFailureClasses.Cancelled, cancelled.FailureClass);
    }

    protected async Task RunExecutionRuntime_CancelAfterTerminalRunIsStable()
    {
        var runtime = await CreateRuntimeAsync();
        runtime.RegisterHandler(new CountingHandler());

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a")
            }
        });

        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        var cancelled = await runtime.CancelRunAsync(completed.Id);

        Assert.NotNull(cancelled);
        Assert.Equal(ExecutionRunStatuses.Succeeded, cancelled!.Status);
        Assert.False(cancelled.CancellationRequested);

        var history = await runtime.GetHistoryAsync(completed.Id);
        Assert.DoesNotContain(history, item => item.Type == ExecutionEventTypes.RunCancellationRequested);
    }

    protected async Task RunExecutionRuntime_RetriesFailedRunUntilSuccess()
    {
        var runtime = await CreateRuntimeAsync();
        var handler = new RetryOnceHandler();
        runtime.RegisterHandler(handler);

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = RetryOnceHandler.HandlerId,
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = 2,
                InitialDelaySeconds = 0.01,
                MaxDelaySeconds = 0.01,
                BackoffMultiplier = 1
            }
        });

        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        AssertRunAttempt(completed, 2);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal(2, completed.Result!["attempt"]!.GetValue<int>());

        var history = await runtime.GetHistoryAsync(accepted.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RetryScheduled);
    }

    protected async Task RunExecutionRuntime_StopsRetryingAfterMaxAttempts()
    {
        var runtime = await CreateRuntimeAsync();
        var handler = new AlwaysFailHandler();
        runtime.RegisterHandler(handler);

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = AlwaysFailHandler.HandlerId,
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = 2,
                InitialDelaySeconds = 0.01,
                MaxDelaySeconds = 0.01,
                BackoffMultiplier = 1
            }
        });

        var failed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Failed);
        AssertRunAttempt(failed, 2);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal(ExecutionFailureClasses.Transient, failed.FailureClass);

        var history = await runtime.GetHistoryAsync(failed.Id);
        Assert.Single(history, item => item.Type == ExecutionEventTypes.RetryScheduled);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunFailed);
    }

    protected async Task RunExecutionRuntime_RetriesTimedOutRunUntilSuccess()
    {
        var runtime = await CreateRuntimeAsync();
        var handler = new TimeoutOnceHandler();
        runtime.RegisterHandler(handler);

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = TimeoutOnceHandler.HandlerId,
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = 2,
                InitialDelaySeconds = 0.01,
                MaxDelaySeconds = 0.01,
                BackoffMultiplier = 1
            }
        });

        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        AssertRunAttempt(completed, 2);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal("recovered", completed.Result!["status"]!.GetValue<string>());

        var history = await runtime.GetHistoryAsync(completed.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RetryScheduled);
    }

    protected async Task RunExecutionRuntime_RunsScheduledWorkWhenDue()
    {
        var runtime = await CreateRuntimeAsync();
        runtime.RegisterHandler(new CountingHandler());

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            ScheduledAtUtc = DateTime.UtcNow.AddMilliseconds(120),
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a")
            }
        });

        Assert.Equal(ExecutionRunStatuses.Waiting, accepted.Status);
        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(1, completed.Attempted);
    }

    protected async Task RunExecutionRuntime_ResumesPersistedScheduledWorkFromNewAdapter()
    {
        var (first, second) = await CreateRestartableRuntimePairAsync();
        var executions = new SharedExecutionCounter();
        first.RegisterHandler(new SharedCountingHandler(executions));
        second.RegisterHandler(new SharedCountingHandler(executions));

        var accepted = await first.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = SharedCountingHandler.HandlerId,
            ScheduledAtUtc = DateTime.UtcNow.AddMilliseconds(150),
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a", "b")
            }
        });

        Assert.Equal(ExecutionRunStatuses.Waiting, accepted.Status);
        await Task.Delay(220);
        await DispatchReadyRunsAsync(second);

        var completed = await WaitForRunAsync(second, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(2, completed.Attempted);
        Assert.Equal(1, executions.Count);
    }

    protected async Task RunExecutionRuntime_PluginCanUseCoordinationPrimitives()
    {
        var runtime = await CreateRuntimeAsync();
        runtime.RegisterHandler(new CoordinationHandler());

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CoordinationHandler.HandlerId
        });

        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(completed.Id, completed.Result!["leaseOwner"]!.GetValue<string>());
        Assert.Equal(completed.Id, completed.Result!["leaseRunId"]!.GetValue<string>());
        Assert.Equal(completed.Id, completed.Result!["timerRunId"]!.GetValue<string>());
        Assert.Equal(completed.Id, completed.Result!["eventRunId"]!.GetValue<string>());
        Assert.True(completed.Result!["released"]!.GetValue<bool>());

        var history = await runtime.GetHistoryAsync(completed.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.LeaseAcquired);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.LeaseReleased);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.TimerScheduled);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.ExternalEventRaised);
    }

    protected async Task RunExecutionRuntime_EnforcesLeaseOwnershipReleaseAndExpiry()
    {
        var runtime = await CreateRuntimeAsync();

        var lease = await runtime.TryAcquireLeaseAsync(new ExecutionLeaseRequest
        {
            LeaseKey = "conformance.lock",
            OwnerId = "owner-a",
            TtlSeconds = 1,
            Metadata = new JsonObject { ["scope"] = "conformance" }
        });
        Assert.NotNull(lease);
        Assert.Equal("owner-a", lease!.OwnerId);
        Assert.Equal("conformance", lease.Metadata!["scope"]!.GetValue<string>());

        var conflict = await runtime.TryAcquireLeaseAsync(new ExecutionLeaseRequest
        {
            LeaseKey = "conformance.lock",
            OwnerId = "owner-b",
            TtlSeconds = 1
        });
        Assert.Null(conflict);

        Assert.False(await runtime.ReleaseLeaseAsync("conformance.lock", "owner-b"));
        Assert.True(await runtime.ReleaseLeaseAsync("conformance.lock", "owner-a"));

        var reacquired = await runtime.TryAcquireLeaseAsync(new ExecutionLeaseRequest
        {
            LeaseKey = "conformance.lock",
            OwnerId = "owner-b",
            TtlSeconds = 1
        });
        Assert.NotNull(reacquired);
        Assert.Equal("owner-b", reacquired!.OwnerId);

        var expiring = await runtime.TryAcquireLeaseAsync(new ExecutionLeaseRequest
        {
            LeaseKey = "conformance.expiring-lock",
            OwnerId = "owner-a",
            TtlSeconds = 1
        });
        Assert.NotNull(expiring);
        await Task.Delay(1100);

        var afterExpiry = await runtime.TryAcquireLeaseAsync(new ExecutionLeaseRequest
        {
            LeaseKey = "conformance.expiring-lock",
            OwnerId = "owner-b",
            TtlSeconds = 1
        });
        Assert.NotNull(afterExpiry);
        Assert.Equal("owner-b", afterExpiry!.OwnerId);
    }

    protected async Task RunExecutionRuntime_RecordsTimerAndExternalEventShape()
    {
        var runtime = await CreateRuntimeAsync();
        runtime.RegisterHandler(new CountingHandler());

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a")
            }
        });
        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);

        var fireAt = DateTime.UtcNow.AddMinutes(5);
        var timer = await runtime.ScheduleTimerAsync(new ExecutionTimerRequest
        {
            RunId = completed.Id,
            Name = "conformance.checkpoint",
            FireAtUtc = fireAt,
            Payload = new JsonObject { ["phase"] = "checkpoint" }
        });
        var externalEvent = await runtime.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = completed.Id,
            Name = "conformance.ready",
            Payload = new JsonObject { ["timerId"] = timer.Id }
        });

        Assert.Equal(completed.Id, timer.RunId);
        Assert.Equal("conformance.checkpoint", timer.Name);
        Assert.Equal("checkpoint", timer.Payload!["phase"]!.GetValue<string>());
        Assert.Equal(completed.Id, externalEvent.RunId);
        Assert.Equal("conformance.ready", externalEvent.Name);
        Assert.Equal(timer.Id, externalEvent.Payload!["timerId"]!.GetValue<string>());

        var history = await runtime.GetHistoryAsync(completed.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.TimerScheduled);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.ExternalEventRaised);
    }

    protected async Task RunExecutionRuntime_BoundsRunListingAndHistoryQueries()
    {
        var runtime = await CreateRuntimeAsync();
        runtime.RegisterHandler(new CountingHandler());
        runtime.RegisterHandler(new VerboseHistoryHandler());

        for (var i = 0; i < 3; i++)
        {
            var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = CountingHandler.HandlerId,
                PluginId = CountingPlugin.PluginId,
                CorrelationId = $"corr-{i}",
                IdempotencyKey = $"counting:{i}:{Guid.NewGuid():N}",
                Payload = new JsonObject
                {
                    ["items"] = new JsonArray($"item-{i}")
                },
                Tags =
                {
                    ["projectId"] = i == 1 ? "project-a" : "other",
                    ["pipelineType"] = "import"
                }
            });
            await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        }

        var listed = await runtime.ListRunsAsync(new ExecutionRunQuery
        {
            HandlerId = CountingHandler.HandlerId,
            Status = ExecutionRunStatuses.Succeeded,
            IncludeResult = false,
            Limit = 2
        });
        Assert.Equal(2, listed.Count);
        Assert.All(listed, run => Assert.Null(run.Result));

        var filtered = await runtime.ListRunsAsync(new ExecutionRunQuery
        {
            HandlerId = CountingHandler.HandlerId,
            PluginId = CountingPlugin.PluginId,
            Status = ExecutionRunStatuses.Succeeded,
            CorrelationId = "corr-1",
            Tags =
            {
                ["projectId"] = "project-a",
                ["pipelineType"] = "import"
            },
            CreatedAfterUtc = DateTime.UtcNow.AddMinutes(-5),
            CreatedBeforeUtc = DateTime.UtcNow.AddMinutes(5),
            UpdatedAfterUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedBeforeUtc = DateTime.UtcNow.AddMinutes(5),
            IncludeResult = false,
            Limit = 5
        });
        var filteredRun = Assert.Single(filtered);
        Assert.Equal("corr-1", filteredRun.CorrelationId);
        Assert.Null(filteredRun.Result);

        var byIdempotency = await runtime.ListRunsAsync(new ExecutionRunQuery
        {
            IdempotencyKey = filteredRun.IdempotencyKey,
            Limit = 5
        });
        Assert.Single(byIdempotency);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.ListRunsAsync(new ExecutionRunQuery { Limit = 0 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.ListRunsAsync(new ExecutionRunQuery { Limit = int.MaxValue }));

        var verbose = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = VerboseHistoryHandler.HandlerId
        });
        var completed = await WaitForRunAsync(runtime, verbose.Id, ExecutionRunStatuses.Succeeded);
        var boundedHistory = await runtime.GetHistoryAsync(completed.Id, new ExecutionHistoryQuery
        {
            Limit = 3
        });

        Assert.Equal(3, boundedHistory.Count);
        Assert.True(boundedHistory.SequenceEqual(boundedHistory.OrderBy(item => item.TimestampUtc).ThenBy(item => item.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.GetHistoryAsync(completed.Id, new ExecutionHistoryQuery { Limit = 0 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.GetHistoryAsync(completed.Id, new ExecutionHistoryQuery { Limit = int.MaxValue }));
    }

    protected async Task RunExecutionRuntime_RunsWindowedBatchPluginFixture()
    {
        var runtime = await CreateRuntimeAsync();
        runtime.RegisterPlugin(new WindowedBatchPlugin());

        var first = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = WindowedBatchHandler.HandlerId,
            PluginId = WindowedBatchPlugin.PluginId,
            IdempotencyKey = "windowed-batch:alpha",
            Payload = new JsonObject
            {
                ["maxParallelWindows"] = 2,
                ["windows"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "recent",
                        ["items"] = new JsonArray("a", "b", "b")
                    },
                    new JsonObject
                    {
                        ["name"] = "backfill",
                        ["items"] = new JsonArray("c")
                    },
                    new JsonObject
                    {
                        ["name"] = "audit",
                        ["items"] = new JsonArray("d", "e")
                    }
                }
            }
        });
        var duplicate = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = WindowedBatchHandler.HandlerId,
            PluginId = WindowedBatchPlugin.PluginId,
            IdempotencyKey = "windowed-batch:alpha",
            Payload = new JsonObject
            {
                ["maxParallelWindows"] = 2,
                ["windows"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "recent",
                        ["items"] = new JsonArray("a", "b", "b")
                    },
                    new JsonObject
                    {
                        ["name"] = "backfill",
                        ["items"] = new JsonArray("c")
                    },
                    new JsonObject
                    {
                        ["name"] = "audit",
                        ["items"] = new JsonArray("d", "e")
                    }
                }
            }
        });

        Assert.Equal(first.Id, duplicate.Id);

        var completed = await WaitForRunAsync(runtime, first.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(6, completed.Requested);
        Assert.Equal(6, completed.Attempted);
        Assert.Equal(5, completed.Succeeded);
        Assert.Equal(0, completed.Failed);
        Assert.Equal(1, completed.Progress);
        Assert.Equal(3, completed.Result!["windowCount"]!.GetValue<int>());
        Assert.Equal(2, completed.Result!["batchCount"]!.GetValue<int>());
        Assert.Equal(5, completed.Result!["recordCount"]!.GetValue<int>());
        Assert.Equal(1, completed.Result!["duplicateSkipped"]!.GetValue<int>());

        var history = await runtime.GetHistoryAsync(completed.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.StepStarted);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.StepCompleted);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.ArtifactWritten);

        var artifacts = await runtime.ListArtifactsAsync(completed.Id);
        var summary = Assert.Single(artifacts, item => item.Name == "windowed-batch-summary");
        Assert.Equal(ExecutionArtifactKinds.Json, summary.Kind);
        Assert.Equal(3, summary.Content!["windows"]!.AsArray().Count);
        Assert.Equal(2, summary.Content!["maxParallelWindows"]!.GetValue<int>());
    }

    protected async Task RunExecutionRuntime_RunsPressureAwarePipelineFixture()
    {
        var runtime = await CreateRuntimeAsync();
        runtime.RegisterPlugin(new PressureAwarePipelinePlugin());

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = PressureAwarePipelineHandler.HandlerId,
            PluginId = PressureAwarePipelinePlugin.PluginId,
            IdempotencyKey = "pressure-aware-pipeline:alpha",
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = 2,
                InitialDelaySeconds = 0.01,
                MaxDelaySeconds = 0.01,
                BackoffMultiplier = 1
            },
            Payload = new JsonObject
            {
                ["failAfterCheckpoint"] = true,
                ["warnAfterWindows"] = 1,
                ["windows"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "daily",
                        ["items"] = new JsonArray("a", "b")
                    },
                    new JsonObject
                    {
                        ["name"] = "backfill",
                        ["items"] = new JsonArray("b", "c")
                    },
                    new JsonObject
                    {
                        ["name"] = "audit",
                        ["items"] = new JsonArray("d")
                    }
                }
            }
        });

        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        AssertRunAttempt(completed, 2);
        Assert.Equal(5, completed.Requested);
        Assert.Equal(5, completed.Attempted);
        Assert.Equal(4, completed.Succeeded);
        Assert.Equal(0, completed.Failed);
        Assert.Equal(1, completed.Progress);
        Assert.Equal(3, completed.Result!["windowCount"]!.GetValue<int>());
        Assert.Equal(4, completed.Result!["recordCount"]!.GetValue<int>());
        Assert.Equal(3, completed.Result!["checkpointCount"]!.GetValue<int>());
        Assert.True(completed.Result!["runtimeWarning"]!.GetValue<bool>());
        Assert.True(completed.Result!["recoveredFromCheckpoint"]!.GetValue<bool>());
        Assert.Equal(1, completed.Result!["duplicateSkipped"]!.GetValue<int>());
        Assert.NotNull(completed.StatusDetails);
        Assert.True(completed.StatusDetails!["runtimeWarning"]!.GetValue<bool>());
        var heartbeatEntries = completed.StatusDetails["heartbeat"]!["byPipeline"]!["pressure-aware"]!.AsArray();
        Assert.Equal(3, heartbeatEntries.Count);
        Assert.All(heartbeatEntries, item => Assert.Equal("succeeded", item!["status"]!.GetValue<string>()));

        var history = await runtime.GetHistoryAsync(completed.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RetryScheduled);
        Assert.Contains(history, item =>
        {
            var completedWindows = item.Details?["checkpoint"]?["completedWindows"] as JsonArray;
            return item.Type == ExecutionEventTypes.RunStatus && completedWindows is { Count: >= 1 };
        });
        Assert.Contains(history, item =>
            item.Type == ExecutionEventTypes.StepStarted &&
            item.Details?["pressure"]?["level"]?.GetValue<string>() == "warning");

        var artifacts = await runtime.ListArtifactsAsync(completed.Id);
        var advisory = Assert.Single(artifacts, item => item.Name == "pipeline-advisory");
        Assert.Equal(ExecutionArtifactKinds.Json, advisory.Kind);
        Assert.True(advisory.Content!["runtimeWarning"]!.GetValue<bool>());
        Assert.Equal("shrink_window", advisory.Content["adaptiveHints"]!["nextAction"]!.GetValue<string>());
        Assert.Equal(3, advisory.Content["heartbeat"]!["byPipeline"]!["pressure-aware"]!.AsArray().Count);
    }

    protected static async Task<ExecutionRun> WaitForRunAsync(IExecutionRuntime runtime, string id, string status)
    {
        ExecutionRun? run = null;
        for (var i = 0; i < 400; i++)
        {
            run = await runtime.GetRunAsync(id);
            if (run?.Status == status)
            {
                return run;
            }

            await Task.Delay(50);
        }

        var history = await runtime.GetHistoryAsync(id);
        var recent = string.Join(
            " | ",
            history.TakeLast(5).Select(item =>
                $"{item.Type}:{item.Status ?? "none"}:{item.Message ?? "none"}"));
        throw new InvalidOperationException(
            $"Run {id} did not reach {status}. Last status: {run?.Status ?? "(missing)"}; " +
            $"failure: {run?.FailureClass ?? "none"}; error: {run?.Error ?? "none"}; " +
            $"recent history: {recent}");
    }

    private static void AssertRunAttempt(ExecutionRun run, int expected)
    {
        Assert.True(
            run.Attempt == expected,
            $"Expected portable attempt {expected}, got {run.Attempt}; status: {run.Status}; " +
            $"failure: {run.FailureClass ?? "none"}; error: {run.Error ?? "none"}; " +
            $"result: {run.Result?.ToJsonString() ?? "none"}");
    }

    private sealed class CountingPlugin : IExecutionPlugin
    {
        public const string PluginId = "test.counting";

        public ExecutionPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = PluginId,
            Name = "Counting plugin",
            Version = "1.0.0",
            Handlers =
            {
                new ExecutionHandlerDescriptor
                {
                    HandlerId = CountingHandler.HandlerId,
                    PluginId = PluginId,
                    DisplayName = "Count items"
                }
            }
        };

        public IReadOnlyList<IExecutionHandler> Handlers { get; } = new[] { new CountingHandler() };
    }

    private sealed class CountingHandler : IExecutionHandler
    {
        public const string HandlerId = "test.counting.count";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            PluginId = CountingPlugin.PluginId,
            DisplayName = "Count items"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var items = context.Run.Payload?["items"]?.AsArray() ?? new JsonArray();
            var total = 0;
            for (var i = 0; i < items.Count; i++)
            {
                total += i + 1;
                await context.PutCheckpointAsync(new ExecutionCheckpointWrite
                {
                    Key = "progress",
                    Content = new JsonObject
                    {
                        ["attempted"] = i + 1,
                        ["total"] = total
                    },
                    Metadata =
                    {
                        ["handler"] = HandlerId
                    }
                }, ct);
                var checkpoint = await context.GetCheckpointAsync("progress", ct);
                if (checkpoint?.Content?["attempted"]?.GetValue<int>() != i + 1)
                {
                    return ExecutionRunResult.Failed("checkpoint_unavailable", "Checkpoint was not readable after write.");
                }

                await context.ReportAsync(new ExecutionRunUpdate
                {
                    Requested = items.Count,
                    Attempted = i + 1,
                    Succeeded = i + 1,
                    Failed = 0,
                    Progress = (i + 1) / (double)items.Count,
                    CurrentStep = $"item:{i}"
                }, ct);
            }

            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "summary",
                Kind = ExecutionArtifactKinds.Json,
                Content = new JsonObject
                {
                    ["count"] = items.Count,
                    ["total"] = total
                }
            }, ct);

            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["count"] = items.Count,
                ["total"] = total
            });
        }
    }

    private sealed class BlockingHandler : IExecutionHandler
    {
        public const string HandlerId = "test.blocking";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Blocking handler"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 0,
                Progress = 0,
                CurrentStep = "blocked"
            }, ct);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ExecutionRunResult.Succeeded();
        }
    }

    private sealed class RetryOnceHandler : IExecutionHandler
    {
        public const string HandlerId = "test.retry.once";
        private int _attempts;

        public int Attempts => _attempts;

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Retry once"
        };

        public Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt == 1)
            {
                return Task.FromResult(ExecutionRunResult.Failed(ExecutionFailureClasses.Transient, "first attempt fails"));
            }

            return Task.FromResult(ExecutionRunResult.Succeeded(new JsonObject
            {
                ["attempt"] = attempt
            }));
        }
    }

    private sealed class AlwaysFailHandler : IExecutionHandler
    {
        public const string HandlerId = "test.retry.always-fails";
        private int _attempts;

        public int Attempts => _attempts;

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Always fails"
        };

        public Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromResult(ExecutionRunResult.Failed(ExecutionFailureClasses.Transient, "attempt failed"));
        }
    }

    private sealed class TimeoutOnceHandler : IExecutionHandler
    {
        public const string HandlerId = "test.retry.timeout-once";
        private int _attempts;

        public int Attempts => _attempts;

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Timeout once"
        };

        public Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt == 1)
            {
                return Task.FromResult(new ExecutionRunResult
                {
                    Status = ExecutionRunStatuses.TimedOut,
                    FailureClass = ExecutionFailureClasses.Timeout,
                    Error = "attempt timed out"
                });
            }

            return Task.FromResult(ExecutionRunResult.Succeeded(new JsonObject
            {
                ["status"] = "recovered"
            }));
        }
    }

    private sealed class ThrowingHandler : IExecutionHandler
    {
        public const string HandlerId = "test.consumer.throwing";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Throwing handler"
        };

        public Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            throw new InvalidOperationException("consumer-visible failure");
        }
    }

    private sealed class SharedExecutionCounter
    {
        private int _count;

        public int Count => _count;

        public void Increment()
        {
            Interlocked.Increment(ref _count);
        }
    }

    private sealed class SharedCountingHandler : IExecutionHandler
    {
        public const string HandlerId = "test.restart.shared-count";
        private readonly SharedExecutionCounter _executions;

        public SharedCountingHandler(SharedExecutionCounter executions)
        {
            _executions = executions;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Shared count items"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            _executions.Increment();
            var items = context.Run.Payload?["items"]?.AsArray() ?? new JsonArray();
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = items.Count,
                Attempted = items.Count,
                Succeeded = items.Count,
                Failed = 0,
                Progress = 1
            }, ct);

            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["count"] = items.Count
            });
        }
    }

    private sealed class VerboseHistoryHandler : IExecutionHandler
    {
        public const string HandlerId = "test.history.verbose";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Verbose history"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            for (var i = 0; i < 6; i++)
            {
                await context.RecordEventAsync(
                    ExecutionEventTypes.StepStarted,
                    $"history event {i}",
                    details: new JsonObject { ["index"] = i },
                    ct: ct);
            }

            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["events"] = 6
            });
        }
    }

    private sealed class WindowedBatchPlugin : IExecutionPlugin
    {
        public const string PluginId = "test.windowed-batch";

        public ExecutionPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = PluginId,
            Name = "Windowed batch plugin",
            Version = "1.0.0",
            Handlers =
            {
                new ExecutionHandlerDescriptor
                {
                    HandlerId = WindowedBatchHandler.HandlerId,
                    PluginId = PluginId,
                    DisplayName = "Windowed batch"
                }
            }
        };

        public IReadOnlyList<IExecutionHandler> Handlers { get; } = new[] { new WindowedBatchHandler() };
    }

    private sealed class WindowedBatchHandler : IExecutionHandler
    {
        public const string HandlerId = "test.windowed-batch.run";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            PluginId = WindowedBatchPlugin.PluginId,
            DisplayName = "Windowed batch"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var windows = ReadWindows(context.Run.Payload?["windows"]?.AsArray());
            var maxParallel = Math.Max(1, context.Run.Payload?["maxParallelWindows"]?.GetValue<int>() ?? 1);
            var requested = windows.Sum(window => window.Items.Count);
            var attempted = 0;
            var succeeded = 0;
            var skipped = 0;
            var batchCount = 0;
            var processed = new HashSet<string>(StringComparer.Ordinal);
            var summaries = new JsonArray();

            for (var offset = 0; offset < windows.Count; offset += maxParallel)
            {
                var batch = windows.Skip(offset).Take(maxParallel).ToList();
                batchCount++;
                await context.RecordEventAsync(
                    ExecutionEventTypes.StepStarted,
                    $"Window batch {batchCount} started.",
                    details: new JsonObject
                    {
                        ["batch"] = batchCount,
                        ["windowCount"] = batch.Count,
                        ["maxParallelWindows"] = maxParallel
                    },
                    ct: ct);

                foreach (var window in batch)
                {
                    var result = ProcessWindow(window, processed);
                    attempted += result.Attempted;
                    succeeded += result.Succeeded;
                    skipped += result.Skipped;
                    summaries.Add(new JsonObject
                    {
                        ["name"] = window.Name,
                        ["attempted"] = result.Attempted,
                        ["succeeded"] = result.Succeeded,
                        ["duplicateSkipped"] = result.Skipped
                    });

                    await context.ReportAsync(new ExecutionRunUpdate
                    {
                        Requested = requested,
                        Attempted = attempted,
                        Succeeded = succeeded,
                        Failed = 0,
                        Progress = requested == 0 ? 1 : attempted / (double)requested,
                        CurrentStep = $"window:{window.Name}",
                        StatusDetails = new JsonObject
                        {
                            ["currentWindow"] = window.Name,
                            ["batch"] = batchCount,
                            ["duplicateSkipped"] = skipped
                        }
                    }, ct);
                }

                await context.RecordEventAsync(
                    ExecutionEventTypes.StepCompleted,
                    $"Window batch {batchCount} completed.",
                    details: new JsonObject
                    {
                        ["batch"] = batchCount,
                        ["attempted"] = attempted,
                        ["succeeded"] = succeeded,
                        ["duplicateSkipped"] = skipped
                    },
                    ct: ct);
            }

            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "windowed-batch-summary",
                Kind = ExecutionArtifactKinds.Json,
                Content = new JsonObject
                {
                    ["windowCount"] = windows.Count,
                    ["maxParallelWindows"] = maxParallel,
                    ["batchCount"] = batchCount,
                    ["recordCount"] = succeeded,
                    ["duplicateSkipped"] = skipped,
                    ["windows"] = summaries
                }
            }, ct);

            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["windowCount"] = windows.Count,
                ["batchCount"] = batchCount,
                ["recordCount"] = succeeded,
                ["duplicateSkipped"] = skipped
            });
        }

        public static IReadOnlyList<WindowSpec> ReadWindows(JsonArray? source)
        {
            if (source is null)
            {
                return Array.Empty<WindowSpec>();
            }

            var windows = new List<WindowSpec>();
            for (var i = 0; i < source.Count; i++)
            {
                if (source[i] is not JsonObject item)
                {
                    continue;
                }

                var name = item["name"]?.GetValue<string>() ?? $"window-{i + 1}";
                var values = new List<string>();
                if (item["items"] is JsonArray items)
                {
                    foreach (var value in items)
                    {
                        if (value is not null)
                        {
                            values.Add(value.GetValue<string>());
                        }
                    }
                }

                windows.Add(new WindowSpec(name, values));
            }

            return windows;
        }

        private static WindowResult ProcessWindow(WindowSpec window, ISet<string> processed)
        {
            var succeeded = 0;
            var skipped = 0;
            foreach (var item in window.Items)
            {
                if (processed.Add(item))
                {
                    succeeded++;
                }
                else
                {
                    skipped++;
                }
            }

            return new WindowResult(window.Items.Count, succeeded, skipped);
        }
    }

    private sealed record WindowSpec(string Name, IReadOnlyList<string> Items);

    private sealed record WindowResult(int Attempted, int Succeeded, int Skipped);

    private sealed class PressureAwarePipelinePlugin : IExecutionPlugin
    {
        public const string PluginId = "test.pressure-aware-pipeline";

        public ExecutionPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = PluginId,
            Name = "Pressure aware pipeline plugin",
            Version = "1.0.0",
            Handlers =
            {
                new ExecutionHandlerDescriptor
                {
                    HandlerId = PressureAwarePipelineHandler.HandlerId,
                    PluginId = PluginId,
                    DisplayName = "Pressure aware pipeline",
                    ConcurrencyKey = "test.pressure-aware-pipeline"
                }
            }
        };

        public IReadOnlyList<IExecutionHandler> Handlers { get; } = new[] { new PressureAwarePipelineHandler() };
    }

    private sealed class PressureAwarePipelineHandler : IExecutionHandler
    {
        public const string HandlerId = "test.pressure-aware-pipeline.run";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            PluginId = PressureAwarePipelinePlugin.PluginId,
            DisplayName = "Pressure aware pipeline",
            ConcurrencyKey = "test.pressure-aware-pipeline"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var windows = WindowedBatchHandler.ReadWindows(context.Run.Payload?["windows"]?.AsArray());
            var requested = windows.Sum(window => window.Items.Count);
            var warnAfterWindows = Math.Max(1, context.Run.Payload?["warnAfterWindows"]?.GetValue<int>() ?? 1);
            var failAfterCheckpoint = context.Run.Payload?["failAfterCheckpoint"]?.GetValue<bool>() == true;
            var completedWindows = ReadStringSet(context.Run.StatusDetails?["checkpoint"]?["completedWindows"]?.AsArray());
            var processedItems = ReadStringSet(context.Run.StatusDetails?["checkpoint"]?["processedItems"]?.AsArray());
            var recoveredFromCheckpoint = processedItems.Count > 0 || completedWindows.Count > 0;
            var attempted = processedItems.Count;
            var succeeded = processedItems.Count;
            var skipped = 0;
            var windowSummaries = new JsonArray();
            var runtimeWarning = false;

            await context.RecordEventAsync(
                ExecutionEventTypes.StepStarted,
                "Pressure-aware pipeline started.",
                details: new JsonObject
                {
                    ["pressure"] = new JsonObject
                    {
                        ["level"] = "warning",
                        ["reason"] = "fixture"
                    },
                    ["checkpointedItems"] = processedItems.Count
                },
                ct: ct);

            foreach (var window in windows)
            {
                if (completedWindows.Contains(window.Name))
                {
                    windowSummaries.Add(BuildWindowSummary(window.Name, 0, 0, 0, recovered: true));
                    continue;
                }

                var windowAttempted = 0;
                var windowSucceeded = 0;
                var windowSkipped = 0;
                foreach (var item in window.Items)
                {
                    attempted++;
                    windowAttempted++;
                    if (processedItems.Add(item))
                    {
                        succeeded++;
                        windowSucceeded++;
                    }
                    else
                    {
                        skipped++;
                        windowSkipped++;
                    }
                }

                completedWindows.Add(window.Name);
                runtimeWarning = runtimeWarning || completedWindows.Count >= warnAfterWindows;
                windowSummaries.Add(BuildWindowSummary(window.Name, windowAttempted, windowSucceeded, windowSkipped, recovered: false));
                var statusDetails = BuildStatusDetails(
                    completedWindows,
                    processedItems,
                    runtimeWarning,
                    outcome: "running",
                    skipped,
                    recoveredFromCheckpoint);

                await context.ReportAsync(new ExecutionRunUpdate
                {
                    Requested = requested,
                    Attempted = attempted,
                    Succeeded = succeeded,
                    Failed = 0,
                    Progress = requested == 0 ? 1 : Math.Min(1, attempted / (double)requested),
                    CurrentStep = $"window:{window.Name}",
                    StatusDetails = statusDetails
                }, ct);

                if (failAfterCheckpoint && context.Run.Attempt == 1 && completedWindows.Count == 1)
                {
                    return ExecutionRunResult.Failed(
                        "fixture_transient_pressure",
                        "Fixture transient pressure after checkpoint.",
                        new JsonObject
                        {
                            ["checkpointedWindows"] = completedWindows.Count,
                            ["checkpointedItems"] = processedItems.Count
                        });
                }
            }

            runtimeWarning = runtimeWarning || completedWindows.Count >= warnAfterWindows;
            var heartbeat = BuildHeartbeat(completedWindows, runtimeWarning, outcome: "succeeded");
            var adaptiveHints = BuildAdaptiveHints(runtimeWarning, skipped);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "pipeline-advisory",
                Kind = ExecutionArtifactKinds.Json,
                Content = new JsonObject
                {
                    ["runtimeWarning"] = runtimeWarning,
                    ["recoveredFromCheckpoint"] = recoveredFromCheckpoint,
                    ["heartbeat"] = heartbeat.DeepClone(),
                    ["adaptiveHints"] = adaptiveHints.DeepClone(),
                    ["windows"] = windowSummaries
                }
            }, ct);

            return ExecutionRunResult.Succeeded(
                new JsonObject
                {
                    ["windowCount"] = windows.Count,
                    ["recordCount"] = processedItems.Count,
                    ["checkpointCount"] = completedWindows.Count,
                    ["duplicateSkipped"] = skipped,
                    ["runtimeWarning"] = runtimeWarning,
                    ["recoveredFromCheckpoint"] = recoveredFromCheckpoint
                },
                BuildStatusDetails(
                    completedWindows,
                    processedItems,
                    runtimeWarning,
                    outcome: "succeeded",
                    skipped,
                    recoveredFromCheckpoint));
        }

        private static JsonObject BuildStatusDetails(
            ISet<string> completedWindows,
            ISet<string> processedItems,
            bool runtimeWarning,
            string outcome,
            int duplicateSkipped,
            bool recoveredFromCheckpoint)
        {
            return new JsonObject
            {
                ["runtimeWarning"] = runtimeWarning,
                ["recoveredFromCheckpoint"] = recoveredFromCheckpoint,
                ["checkpoint"] = new JsonObject
                {
                    ["completedWindows"] = ToJsonArray(completedWindows),
                    ["processedItems"] = ToJsonArray(processedItems)
                },
                ["heartbeat"] = BuildHeartbeat(completedWindows, runtimeWarning, outcome),
                ["adaptiveHints"] = BuildAdaptiveHints(runtimeWarning, duplicateSkipped)
            };
        }

        private static JsonObject BuildHeartbeat(ISet<string> completedWindows, bool runtimeWarning, string outcome)
        {
            var entries = new JsonArray();
            var index = 0;
            foreach (var window in completedWindows.OrderBy(value => value, StringComparer.Ordinal))
            {
                entries.Add(new JsonObject
                {
                    ["pipeline"] = "pressure-aware",
                    ["window"] = window,
                    ["status"] = outcome,
                    ["runtimeWarning"] = runtimeWarning,
                    ["sequence"] = ++index
                });
            }

            return new JsonObject
            {
                ["recent"] = entries.DeepClone(),
                ["byPipeline"] = new JsonObject
                {
                    ["pressure-aware"] = entries
                }
            };
        }

        private static JsonObject BuildAdaptiveHints(bool runtimeWarning, int duplicateSkipped)
        {
            return new JsonObject
            {
                ["nextAction"] = runtimeWarning ? "shrink_window" : "hold",
                ["duplicateSkipped"] = duplicateSkipped,
                ["reason"] = runtimeWarning ? "runtime_warning" : "within_budget"
            };
        }

        private static JsonObject BuildWindowSummary(string name, int attempted, int succeeded, int skipped, bool recovered)
        {
            return new JsonObject
            {
                ["name"] = name,
                ["attempted"] = attempted,
                ["succeeded"] = succeeded,
                ["skipped"] = skipped,
                ["recovered"] = recovered
            };
        }

        private static HashSet<string> ReadStringSet(JsonArray? source)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);
            if (source is null)
            {
                return values;
            }

            foreach (var item in source)
            {
                if (item is not null)
                {
                    values.Add(item.GetValue<string>());
                }
            }

            return values;
        }

        private static JsonArray ToJsonArray(IEnumerable<string> values)
        {
            var array = new JsonArray();
            foreach (var value in values.OrderBy(value => value, StringComparer.Ordinal))
            {
                array.Add(value);
            }

            return array;
        }
    }

    private sealed class CoordinationHandler : IExecutionHandler
    {
        public const string HandlerId = "test.coordination";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Coordination handler"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var lease = await context.TryAcquireLeaseAsync(
                "coordination-lock",
                ttlSeconds: 30,
                metadata: new JsonObject { ["scope"] = "conformance" },
                ct);
            if (lease is null)
            {
                return ExecutionRunResult.Failed("lease_unavailable", "Coordination lease was not acquired.");
            }

            var timer = await context.ScheduleTimerAsync(
                "coordination-checkpoint",
                DateTime.UtcNow.AddMilliseconds(10),
                new JsonObject { ["phase"] = "checkpoint" },
                ct);
            var externalEvent = await context.RaiseEventAsync(
                "coordination.ready",
                new JsonObject { ["timerId"] = timer.Id },
                ct);
            var released = await context.ReleaseLeaseAsync("coordination-lock", ct);

            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["leaseOwner"] = lease.OwnerId,
                ["leaseRunId"] = lease.RunId,
                ["timerRunId"] = timer.RunId,
                ["eventRunId"] = externalEvent.RunId,
                ["released"] = released
            });
        }
    }
}
