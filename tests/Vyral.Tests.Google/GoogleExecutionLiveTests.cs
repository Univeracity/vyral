using System.Text.Json.Nodes;
using Google.Cloud.Firestore;
using Vyral.Execution;
using Vyral.Google;
using CloudTasksClientBuilder = Google.Cloud.Tasks.V2.CloudTasksClientBuilder;

namespace Vyral.Tests.Google;

/// <summary>
/// Opt-in deployment check for the real Firestore transaction and Cloud Tasks transport. It is
/// deliberately limited to a caller-provided, uniquely prefixed Firestore root and temporary
/// queue/service so it cannot operate on a shared Vyral runtime's state.
/// </summary>
public sealed class GoogleExecutionLiveTests
{
    [GoogleExecutionLiveFact]
    public async Task GoogleExecutionRuntime_UsesFirestoreTransactionsAndCloudTasks()
    {
        var projectId = GoogleLiveSettings.ExecutionProjectId!;
        var rootCollection = GoogleLiveSettings.RequireExecutionFirestoreRoot();
        var queueId = GoogleLiveSettings.ExecutionTasksQueue!;
        var locationId = GoogleLiveSettings.ExecutionTasksLocation!;
        var workerUrl = GoogleLiveSettings.ExecutionWorkerUrl!;
        var serviceAccountEmail = GoogleLiveSettings.ExecutionServiceAccountEmail!;
        var oidcAudience = GoogleLiveSettings.ExecutionOidcAudience!;
        if (!rootCollection.StartsWith("vyral_handoff_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Live execution tests require a uniquely prefixed 'vyral_handoff_' Firestore root.");
        }

        var credential = GoogleLiveSettings.CreateExecutionCredential();
        var firestore = new FirestoreDbBuilder
        {
            ProjectId = projectId,
            DatabaseId = GoogleLiveSettings.ExecutionDatabaseId,
            Credential = credential
        }.Build();
        var tasks = new CloudTasksClientBuilder
        {
            Credential = credential
        }.Build();
        var dispatch = new GoogleCloudExecutionDispatcher(
            new CloudTasksHttpJsonQueue(tasks),
            new GoogleCloudExecutionDispatchOptions
            {
                ProjectId = projectId,
                LocationId = locationId,
                QueueId = queueId,
                WorkerUrl = workerUrl,
                ServiceAccountEmail = serviceAccountEmail,
                OidcAudience = oidcAudience
            });
        var state = new FirestoreExecutionStateStore(firestore, rootCollection);
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "handoff.google.execution.worker",
            PluginId = "handoff.google.execution",
            DisplayName = "Temporary Google execution handoff worker"
        };
        var runtime = new GoogleCloudExecutionRuntimeAdapter(
            state,
            dispatch,
            new GoogleCloudExecutionRuntimeOptions
            {
                MaxActiveRuns = 1,
                MaxListLimit = 10,
                DefaultListLimit = 10,
                MaxHistoryLimit = 10,
                DefaultHistoryLimit = 10,
                WorkerDispatchers =
                [
                    new GoogleCloudExecutionWorkerDispatcher
                    {
                        HandlerId = handler.HandlerId,
                        Dispatcher = dispatch
                    }
                ]
            });
        runtime.RegisterExternalHandler(handler);
        string? delayedTaskName = null;

        try
        {
            var delayedDispatch = await dispatch.DispatchAsync(
                "handoff-live-dispatch",
                GoogleCloudExecutionDispatchReasons.RunReady,
                DateTime.UtcNow.AddMinutes(2));
            delayedTaskName = delayedDispatch.Name;
            var scheduledTask = await tasks.GetTaskAsync(delayedDispatch.Name);
            Assert.Equal(
                new Uri(workerUrl).GetLeftPart(UriPartial.Path).TrimEnd('/'),
                new Uri(scheduledTask.HttpRequest.Url).GetLeftPart(UriPartial.Path).TrimEnd('/'));
            Assert.Equal("1", scheduledTask.HttpRequest.Headers["X-Vyral-Execution-Dispatch"]);
            Assert.NotNull(scheduledTask.HttpRequest.OidcToken);
            Assert.Equal(serviceAccountEmail, scheduledTask.HttpRequest.OidcToken.ServiceAccountEmail);
            Assert.Equal(oidcAudience, scheduledTask.HttpRequest.OidcToken.Audience);
            await tasks.DeleteTaskAsync(delayedDispatch.Name);
            delayedTaskName = null;

            var created = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = handler.HandlerId,
                Payload = new JsonObject { ["source"] = "live-handoff", ["request"] = index }
            })));
            var accepted = Assert.Single(created, run => run.Status == ExecutionRunStatuses.Queued);
            var rejected = created.Where(run => run.Status == ExecutionRunStatuses.Rejected).ToList();
            Assert.Equal(ExecutionRunStatuses.Queued, accepted.Status);
            Assert.Equal(3, rejected.Count);
            Assert.All(rejected, run => Assert.Equal(ExecutionFailureClasses.QueueFull, run.FailureClass));
            Assert.Equal(1, (await runtime.GetAdapterStatusAsync()).ActiveRuns);

            var claimed = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
            {
                WorkerId = "handoff-live-worker",
                HandlerIds = { handler.HandlerId },
                RunId = accepted.Id,
                TtlSeconds = 60
            })));
            var lease = Assert.IsType<ExecutionExternalWorkerLease>(Assert.Single(claimed, item => item is not null));
            await runtime.RecordExternalLeaseEventAsync(new ExecutionExternalWorkerEventRequest
            {
                LeaseKey = lease.LeaseKey,
                LeaseToken = lease.LeaseToken,
                WorkerId = lease.WorkerId,
                Type = ExecutionEventTypes.Log,
                Message = "Live Firestore lease mutation."
            });
            var checkpoint = await runtime.CheckpointExternalLeaseAsync(new ExecutionExternalWorkerCheckpointRequest
            {
                LeaseKey = lease.LeaseKey,
                LeaseToken = lease.LeaseToken,
                WorkerId = lease.WorkerId,
                Checkpoint = new ExecutionCheckpointWrite
                {
                    Key = "live-progress",
                    Content = new JsonObject { ["position"] = 1 }
                }
            });
            var checkpointRead = await runtime.GetExternalLeaseCheckpointAsync(new ExecutionExternalWorkerCheckpointReadRequest
            {
                LeaseKey = lease.LeaseKey,
                LeaseToken = lease.LeaseToken,
                WorkerId = lease.WorkerId,
                Key = checkpoint.Key
            });
            Assert.Equal(1, checkpointRead!.Content!["position"]!.GetValue<int>());

            var suspended = await runtime.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
            {
                LeaseKey = lease.LeaseKey,
                LeaseToken = lease.LeaseToken,
                WorkerId = lease.WorkerId,
                Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
                Name = "approval",
                TimeoutAtUtc = DateTime.UtcNow.AddMinutes(1)
            });
            Assert.True(suspended.Suspended);
            await runtime.RaiseEventAsync(new ExecutionExternalEventRequest
            {
                RunId = accepted.Id,
                Name = "approval",
                Payload = new JsonObject { ["approved"] = true }
            });
            var resumed = Assert.IsType<ExecutionExternalWorkerLease>(await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
            {
                WorkerId = "handoff-live-worker",
                HandlerIds = { handler.HandlerId },
                RunId = accepted.Id,
                TtlSeconds = 60
            }));
            var outcome = await runtime.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
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

            var completed = await runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
            {
                LeaseKey = resumed.LeaseKey,
                LeaseToken = resumed.LeaseToken,
                WorkerId = resumed.WorkerId,
                Result = ExecutionRunResult.Succeeded()
            });
            Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RecordExternalLeaseEventAsync(new ExecutionExternalWorkerEventRequest
            {
                LeaseKey = resumed.LeaseKey,
                LeaseToken = resumed.LeaseToken,
                WorkerId = resumed.WorkerId,
                Type = ExecutionEventTypes.Log,
                Message = "Stale live lease event."
            }));
            Assert.Equal(0, (await runtime.GetAdapterStatusAsync()).ActiveRuns);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(delayedTaskName))
            {
                await tasks.DeleteTaskAsync(delayedTaskName);
            }

            var runs = await state.ListRunsAsync(new ExecutionRunQuery { IncludeResult = false, Limit = 10 });
            foreach (var run in runs)
            {
                await state.DeleteRunAsync(run);
            }

            await firestore.Collection(rootCollection).Document("state").Collection("metadata").Document("active-run-count").DeleteAsync();
            await firestore.Collection(rootCollection).Document("state").DeleteAsync();
        }
    }
}
