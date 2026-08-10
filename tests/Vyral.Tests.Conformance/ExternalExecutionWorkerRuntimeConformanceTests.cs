using System.Text.Json.Nodes;
using Vyral.Execution;

namespace Vyral.Tests.Conformance;

/// <summary>
/// Reusable behavioral contract for adapters that advertise <c>external.workers</c>. It avoids
/// assumptions about queues, HTTP hosts, or storage engines: an adapter only has to provide the
/// portable runtime and worker interfaces.
/// </summary>
public abstract class ExternalExecutionWorkerRuntimeConformanceTests
{
    protected abstract Task<ExternalExecutionWorkerRuntimeFixture> CreateExternalWorkerRuntimeAsync();

    protected async Task RunExternalWorker_AdvertisesExternalExecutionModel()
    {
        var fixture = await CreateExternalWorkerRuntimeAsync();
        Assert.Contains(ExecutionCapabilityIds.ExternalWorkers, fixture.Adapter.Adapter.Capabilities);
        Assert.True(ExecutionCapabilityCatalog.HasExecutionModelCapability(fixture.Adapter.Adapter.Capabilities));

        // An external-worker adapter is still a full execution adapter. These policies are the
        // portable statement of its operational and recovery semantics; a provider-specific
        // queue must not become the only place those constraints are knowable.
        var status = await fixture.Adapter.GetAdapterStatusAsync();
        Assert.NotNull(status.OperationalPolicy);
        Assert.True(status.OperationalPolicy!.MaxActiveRuns > 0);
        Assert.True(status.OperationalPolicy.DefaultListLimit > 0);
        Assert.True(status.OperationalPolicy.MaxListLimit >= status.OperationalPolicy.DefaultListLimit);
        Assert.True(status.OperationalPolicy.DefaultHistoryLimit > 0);
        Assert.True(status.OperationalPolicy.MaxHistoryLimit >= status.OperationalPolicy.DefaultHistoryLimit);
        Assert.NotNull(status.ResumePolicy);
        Assert.Equal(ExecutionResumePolicyModes.RestartRecovery, status.ResumePolicy!.Mode);
        Assert.Equal(ExecutionResumePolicyBehaviors.MayReexecuteHandler, status.ResumePolicy.InterruptedRunningBehavior);
        Assert.Equal(ExecutionResumePolicyBehaviors.DispatchWhenDue, status.ResumePolicy.ScheduledWaitingBehavior);
        Assert.Equal(ExecutionResumePolicyBehaviors.NeverResume, status.ResumePolicy.TerminalBehavior);
        Assert.Equal(ExecutionResumePolicyBehaviors.PluginOwned, status.ResumePolicy.PluginCheckpointBehavior);
    }

    protected async Task RunExternalWorker_ReportsArtifactsEventsAndCheckpointsUnderLease()
    {
        var fixture = await CreateExternalWorkerRuntimeAsync();
        fixture.Worker.RegisterExternalHandler(fixture.Handler);
        var accepted = await fixture.Adapter.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = fixture.Handler.HandlerId,
            PluginId = fixture.Handler.PluginId,
            Payload = new JsonObject { ["item"] = "alpha" }
        });
        var lease = await LeaseAsync(fixture, accepted.Id);

        var reported = await fixture.Worker.ReportExternalLeaseAsync(new ExecutionExternalWorkerReportRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Update = new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 1,
                Progress = 0.5,
                CurrentStep = "processing"
            }
        });
        Assert.Equal(ExecutionRunStatuses.Running, reported.Status);
        Assert.Equal(0.5, reported.Progress);

        await fixture.Worker.RecordExternalLeaseEventAsync(new ExecutionExternalWorkerEventRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Type = ExecutionEventTypes.Log,
            Message = "External worker started.",
            Details = new JsonObject { ["worker"] = lease.WorkerId }
        });
        var artifact = await fixture.Worker.PutExternalLeaseArtifactAsync(new ExecutionExternalWorkerArtifactRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Artifact = new ExecutionArtifactWrite
            {
                Name = "external-summary",
                Kind = ExecutionArtifactKinds.Json,
                Content = new JsonObject { ["item"] = "alpha" }
            }
        });
        var checkpoint = await fixture.Worker.CheckpointExternalLeaseAsync(new ExecutionExternalWorkerCheckpointRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Checkpoint = new ExecutionCheckpointWrite
            {
                Key = "external-progress",
                Content = new JsonObject { ["position"] = 1 }
            }
        });
        var readCheckpoint = await fixture.Worker.GetExternalLeaseCheckpointAsync(new ExecutionExternalWorkerCheckpointReadRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Key = checkpoint.Key
        });

        Assert.Equal(accepted.Id, artifact.RunId);
        Assert.Equal(accepted.Id, checkpoint.RunId);
        Assert.NotNull(readCheckpoint);
        Assert.Equal(1, readCheckpoint!.Content!["position"]!.GetValue<int>());
        Assert.Contains(await fixture.Adapter.GetHistoryAsync(accepted.Id), item => item.Type == ExecutionEventTypes.Log);
        Assert.Single(await fixture.Adapter.ListArtifactsAsync(accepted.Id));

        var completed = await fixture.Worker.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Result = ExecutionRunResult.Succeeded(new JsonObject { ["item"] = "alpha" })
        });
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
    }

    protected async Task RunExternalWorker_DeduplicatesClaimsAndResumesDurableEvents()
    {
        var fixture = await CreateExternalWorkerRuntimeAsync();
        fixture.Worker.RegisterExternalHandler(fixture.Handler);
        var accepted = await fixture.Adapter.StartRunAsync(new ExecutionRunRequest { HandlerId = fixture.Handler.HandlerId });
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            fixture.Worker.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
            {
                WorkerId = fixture.WorkerId,
                HandlerIds = { fixture.Handler.HandlerId },
                RunId = accepted.Id,
                TtlSeconds = 30
            })));
        var lease = Assert.Single(claims, claim => claim is not null)!;

        var suspended = await fixture.Worker.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
            Name = "approval",
            TimeoutAtUtc = DateTime.UtcNow.AddMinutes(1)
        });
        Assert.True(suspended.Suspended);

        await fixture.Adapter.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = "approval",
            Payload = new JsonObject { ["approved"] = true }
        });
        var resumed = await LeaseAsync(fixture, accepted.Id);
        var outcome = await fixture.Worker.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = resumed.LeaseKey,
            LeaseToken = resumed.LeaseToken,
            WorkerId = resumed.WorkerId,
            Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
            Name = "approval",
            TimeoutAtUtc = DateTime.UtcNow.AddMinutes(1)
        });
        Assert.False(outcome.Suspended);
        Assert.True(outcome.Outcome!.Event!.Payload!["approved"]!.GetValue<bool>());

        var completed = await fixture.Worker.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = resumed.LeaseKey,
            LeaseToken = resumed.LeaseToken,
            WorkerId = resumed.WorkerId,
            Result = ExecutionRunResult.Succeeded()
        });
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
    }

    protected async Task RunExternalWorker_CompletionIsReplaySafeForTheSameLeaseToken()
    {
        var fixture = await CreateExternalWorkerRuntimeAsync();
        fixture.Worker.RegisterExternalHandler(fixture.Handler);
        var accepted = await fixture.Adapter.StartRunAsync(new ExecutionRunRequest { HandlerId = fixture.Handler.HandlerId });
        var lease = await LeaseAsync(fixture, accepted.Id);
        var request = new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Result = ExecutionRunResult.Succeeded(new JsonObject { ["completed"] = true })
        };

        var completed = await fixture.Worker.CompleteExternalLeaseAsync(request);
        var replayed = await fixture.Worker.CompleteExternalLeaseAsync(request);
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
        Assert.Equal(completed.Id, replayed.Id);
        Assert.Equal(completed.Status, replayed.Status);
    }

    protected async Task RunExternalWorker_RejectsStaleLeaseEvents()
    {
        var fixture = await CreateExternalWorkerRuntimeAsync();
        fixture.Worker.RegisterExternalHandler(fixture.Handler);
        var accepted = await fixture.Adapter.StartRunAsync(new ExecutionRunRequest { HandlerId = fixture.Handler.HandlerId });
        var lease = await LeaseAsync(fixture, accepted.Id);
        var checkpoint = await fixture.Worker.CheckpointExternalLeaseAsync(new ExecutionExternalWorkerCheckpointRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Checkpoint = new ExecutionCheckpointWrite { Key = "before-completion", Content = new JsonObject() }
        });
        await fixture.Worker.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = lease.WorkerId,
            Result = ExecutionRunResult.Succeeded()
        });
        var historyBefore = await fixture.Adapter.GetHistoryAsync(accepted.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Worker.RecordExternalLeaseEventAsync(
            new ExecutionExternalWorkerEventRequest
            {
                LeaseKey = lease.LeaseKey,
                LeaseToken = lease.LeaseToken,
                WorkerId = lease.WorkerId,
                Type = ExecutionEventTypes.Log,
                Message = "This stale event must not be persisted."
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Worker.GetExternalLeaseCheckpointAsync(
            new ExecutionExternalWorkerCheckpointReadRequest
            {
                LeaseKey = lease.LeaseKey,
                LeaseToken = lease.LeaseToken,
                WorkerId = lease.WorkerId,
                Key = checkpoint.Key
            }));

        Assert.Equal(historyBefore.Count, (await fixture.Adapter.GetHistoryAsync(accepted.Id)).Count);
    }

    private static async Task<ExecutionExternalWorkerLease> LeaseAsync(ExternalExecutionWorkerRuntimeFixture fixture, string runId)
    {
        var lease = await fixture.Worker.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = fixture.WorkerId,
            HandlerIds = { fixture.Handler.HandlerId },
            RunId = runId,
            TtlSeconds = 30
        });
        return Assert.IsType<ExecutionExternalWorkerLease>(lease);
    }
}

public sealed class ExternalExecutionWorkerRuntimeFixture
{
    public required IExecutionRuntimeAdapter Adapter { get; init; }
    public required IExternalExecutionWorkerRuntime Worker { get; init; }
    public required ExecutionHandlerDescriptor Handler { get; init; }
    public string WorkerId { get; init; } = "conformance-worker";
}
