using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;
using Vyral.Azure;
using Vyral.Execution;
using Vyral.Execution.AzureDurable;

namespace Vyral.Tests.Azure;

public sealed class AzureCosmosExecutionStatusStoreLiveTests
{
    [AzureCosmosLiveFact]
    public async Task CosmosExecutionStatusStore_PersistsRunStateAndCoordinatesLeases()
    {
        var settings = AzureLiveSettings.Cosmos();
        using var client = new CosmosClient(settings.ConnectionString);
        await client.CreateDatabaseIfNotExistsAsync(settings.DatabaseId);
        var containerId = AzureLiveSettings.UniqueContainerName(settings.ContainerPrefix);
        var options = new AzureDurableExecutionOptions
        {
            StatusStoreName = containerId,
            DefaultListLimit = 20,
            MaxListLimit = 100
        };
        var first = new AzureCosmosExecutionStatusStore(client, settings.DatabaseId, containerId, options);
        var run = new ExecutionRun
        {
            Id = "run-1",
            HandlerId = "live-handler",
            PluginId = "live-plugin",
            Status = ExecutionRunStatuses.Queued,
            IdempotencyKey = "live-key",
            CorrelationId = "live-correlation",
            PayloadHash = "sha256:live",
            Payload = new JsonObject { ["kind"] = "live" },
            Tags = new Dictionary<string, string> { ["environment"] = "test" }
        };

        try
        {
            var created = await first.CreateRunIfAbsentAsync(run);
            Assert.True(created.Created);
            Assert.Equal(run.Id, created.Run.Id);

            var replay = await first.CreateRunIfAbsentAsync(run);
            Assert.False(replay.Created);
            Assert.Equal(run.Id, replay.Run.Id);

            var filtered = await first.ListRunsAsync(new ExecutionRunQuery
            {
                HandlerId = run.HandlerId,
                CorrelationId = run.CorrelationId,
                Tags = new Dictionary<string, string> { ["environment"] = "test" },
                IncludeResult = false,
                Limit = 10
            });
            Assert.Single(filtered);
            Assert.Equal(run.Id, filtered[0].Id);
            Assert.Null(filtered[0].Result);

            var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => first.CreateRunIfAbsentAsync(new ExecutionRun
            {
                Id = "run-concurrent",
                HandlerId = "live-handler",
                PluginId = "live-plugin",
                Status = ExecutionRunStatuses.Queued,
                IdempotencyKey = "live-concurrent-key",
                PayloadHash = "sha256:live-concurrent"
            })));
            Assert.Equal(1, concurrent.Count(result => result.Created));
            Assert.All(concurrent, result => Assert.Equal("run-concurrent", result.Run.Id));
            Assert.Equal(2, await first.CountActiveRunsAsync());

            var cancellationPending = new ExecutionRun
            {
                Id = "run-cancellation-fence",
                HandlerId = "live-handler",
                Status = ExecutionRunStatuses.Running,
                PayloadHash = "sha256:cancellation-fence",
                CancellationRequested = true
            };
            await first.UpsertRunAsync(cancellationPending);
            var lateSuccess = await first.UpsertRunAsync(new ExecutionRun
            {
                Id = cancellationPending.Id,
                HandlerId = cancellationPending.HandlerId,
                Status = ExecutionRunStatuses.Succeeded,
                PayloadHash = cancellationPending.PayloadHash
            });
            Assert.Equal(ExecutionRunStatuses.Running, lateSuccess.Status);
            Assert.True(lateSuccess.CancellationRequested);

            var cancelled = await first.UpsertRunAsync(new ExecutionRun
            {
                Id = cancellationPending.Id,
                HandlerId = cancellationPending.HandlerId,
                Status = ExecutionRunStatuses.Cancelled,
                PayloadHash = cancellationPending.PayloadHash,
                CancellationRequested = true
            });
            var staleRunning = await first.UpsertRunAsync(new ExecutionRun
            {
                Id = cancellationPending.Id,
                HandlerId = cancellationPending.HandlerId,
                Status = ExecutionRunStatuses.Running,
                PayloadHash = cancellationPending.PayloadHash
            });
            Assert.Equal(ExecutionRunStatuses.Cancelled, cancelled.Status);
            Assert.Equal(ExecutionRunStatuses.Cancelled, staleRunning.Status);

            var waitingRun = new ExecutionRun
            {
                Id = "run-durable-wait",
                HandlerId = "live-handler",
                Status = ExecutionRunStatuses.Running,
                PayloadHash = "sha256:durable-wait"
            };
            await first.CreateRunIfAbsentAsync(waitingRun);
            var registeredWait = await first.RegisterDurableWaitAsync(new AzureDurableWait
            {
                Kind = AzureDurableWaitKinds.ExternalEvent,
                Name = "approval",
                FireAtUtc = DateTime.UtcNow.AddMinutes(1)
            }, waitingRun.Id);
            Assert.Equal(AzureDurableWaitKinds.ExternalEvent, registeredWait.Kind);
            Assert.Equal(ExecutionRunStatuses.Waiting, (await first.GetRunAsync(waitingRun.Id))!.Status);
            Assert.Equal("approval", (await first.GetDurableWaitAsync(waitingRun.Id))!.Name);

            var resumedWait = await first.ResumeDurableWaitAsync(waitingRun.Id, new ExecutionWaitResult
            {
                Name = "approval",
                Outcome = ExecutionWaitOutcomes.ExternalEvent,
                Event = new ExecutionExternalEvent
                {
                    Id = "durable-wait-event",
                    RunId = waitingRun.Id,
                    Name = "approval",
                    RaisedAtUtc = DateTime.UtcNow,
                    Payload = new JsonObject { ["approved"] = true }
                }
            });
            Assert.Equal(ExecutionRunStatuses.Queued, resumedWait.Status);
            var waitOutcome = await first.TakeDurableWaitOutcomeAsync(
                waitingRun.Id,
                AzureDurableWaitKinds.ExternalEvent,
                "approval");
            Assert.Equal(ExecutionWaitOutcomes.ExternalEvent, waitOutcome!.Outcome);
            Assert.True(waitOutcome.Event!.Payload!["approved"]!.GetValue<bool>());
            Assert.Null(await first.GetDurableWaitAsync(waitingRun.Id));
            Assert.Equal(
                ExecutionRunStatuses.Queued,
                (await first.ResumeDurableWaitAsync(waitingRun.Id, new ExecutionWaitResult
                {
                    Name = "approval",
                    Outcome = ExecutionWaitOutcomes.ExternalEvent,
                    Event = new ExecutionExternalEvent
                    {
                        Id = "durable-wait-event",
                        RunId = waitingRun.Id,
                        Name = "approval",
                        RaisedAtUtc = DateTime.UtcNow
                    }
                })).Status);

            await first.AppendEventAsync(new ExecutionTraceEvent
            {
                Id = "event-1",
                SequenceId = "0001",
                RunId = run.Id,
                Type = ExecutionEventTypes.RunCreated,
                TimestampUtc = DateTime.UtcNow,
                Message = "Run created."
            });
            await first.PutArtifactAsync(new ExecutionArtifact
            {
                Id = "artifact-1",
                RunId = run.Id,
                Name = "receipt",
                Text = "live",
                ContentHash = "sha256:live"
            });
            await first.PutCheckpointAsync(new ExecutionCheckpoint
            {
                RunId = run.Id,
                Key = "cursor",
                Content = new JsonObject { ["offset"] = 3 }
            });
            await first.ScheduleTimerAsync(new ExecutionTimer
            {
                Id = "timer-1",
                RunId = run.Id,
                Name = "wake",
                FireAtUtc = DateTime.UtcNow.AddMinutes(1)
            });
            await first.RaiseEventAsync(new ExecutionExternalEvent
            {
                Id = "external-1",
                RunId = run.Id,
                Name = "approval",
                Payload = new JsonObject { ["approved"] = true }
            });

            var second = new AzureCosmosExecutionStatusStore(client, settings.DatabaseId, containerId, options);
            var restored = await second.GetRunAsync(run.Id, includeResult: false);
            Assert.NotNull(restored);
            Assert.Null(restored!.Result);
            Assert.Equal(run.PayloadHash, restored.PayloadHash);
            Assert.Equal("test", restored.Tags["environment"]);
            Assert.Equal(run.Id, (await second.FindRunByIdempotencyKeyAsync(run.IdempotencyKey!))!.Id);
            Assert.Single(await second.GetHistoryAsync(run.Id));
            Assert.Equal("receipt", (await second.GetArtifactAsync(run.Id, "receipt"))!.Name);
            Assert.Equal("artifact-1", (await second.GetArtifactAsync(run.Id, "artifact-1"))!.Id);
            Assert.Equal(3, (await second.GetCheckpointAsync(run.Id, "cursor"))!.Content!["offset"]!.GetValue<int>());

            var lease = await second.TryAcquireLeaseAsync(new ExecutionLease
            {
                LeaseKey = "live-lease",
                OwnerId = "worker-a",
                RunId = run.Id,
                AcquiredAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
            });
            Assert.NotNull(lease);
            Assert.Equal("worker-a", (await second.GetLeaseAsync("live-lease"))!.OwnerId);
            Assert.Null(await second.TryAcquireLeaseAsync(new ExecutionLease
            {
                LeaseKey = "live-lease",
                OwnerId = "worker-b",
                RunId = run.Id,
                AcquiredAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
            }));
            Assert.False(await second.ReleaseLeaseAsync("live-lease", "worker-b"));
            Assert.True(await second.ReleaseLeaseAsync("live-lease", "worker-a"));
            Assert.Null(await second.GetLeaseAsync("live-lease"));
        }
        finally
        {
            try
            {
                await client.GetDatabase(settings.DatabaseId).GetContainer(containerId).DeleteContainerAsync();
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }
        }
    }
}
