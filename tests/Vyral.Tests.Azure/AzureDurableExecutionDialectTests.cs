using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Execution.AzureDurable;

namespace Vyral.Tests.Azure;

public sealed class AzureDurableExecutionDialectTests
{
    [Fact]
    public void DurableDialect_DescriptorAdvertisesRemoteCapabilities()
    {
        var descriptor = AzureDurableExecutionDialect.BuildAdapterDescriptor(new AzureDurableExecutionOptions
        {
            AdapterId = "azure-prod",
            TaskHubName = "prod-hub"
        });

        Assert.Equal("azure-prod", descriptor.AdapterId);
        Assert.Equal(AzureDurableExecutionRuntimeKindIds.DurableFunctions, descriptor.RuntimeKind);
        ExecutionContractValidator.ValidateAdapterDescriptor(descriptor);
        Assert.Contains(ExecutionCapabilityIds.RemoteOrchestration, descriptor.Capabilities);
        Assert.Contains(ExecutionCapabilityIds.DurableTimers, descriptor.Capabilities);
        Assert.Contains(ExecutionCapabilityIds.ExternalEvents, descriptor.Capabilities);
        Assert.Contains(ExecutionCapabilityIds.DurableWaits, descriptor.Capabilities);
        Assert.Contains(ExecutionCapabilityIds.Idempotency, descriptor.Capabilities);
        Assert.DoesNotContain(ExecutionCapabilityIds.LocalDispatch, descriptor.Capabilities);
        Assert.Equal("prod-hub", descriptor.Metadata["taskHubName"]);
    }

    [Fact]
    public void DurableDialect_BuildsStableInstanceIdForIdempotencyKey()
    {
        var request = new ExecutionRunRequest
        {
            HandlerId = "example.handler",
            IdempotencyKey = "same-input"
        };

        var first = AzureDurableExecutionDialect.BuildInstanceId(request);
        var second = AzureDurableExecutionDialect.BuildInstanceId(request);
        var different = AzureDurableExecutionDialect.BuildInstanceId(new ExecutionRunRequest
        {
            HandlerId = "example.handler",
            IdempotencyKey = "different-input"
        });

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
        Assert.StartsWith("azure-durable-", first);
    }

    [Fact]
    public void DurableAdapter_ListsRegisteredPluginDescriptors()
    {
        var adapter = new AzureDurableExecutionRuntimeAdapter(new RecordingAzureDurableExecutionClient());
        adapter.RegisterPlugin(new NoOpExecutionPlugin());

        var plugin = Assert.Single(adapter.ListPlugins());
        Assert.Equal(NoOpExecutionPlugin.PluginId, plugin.PluginId);
        var handler = Assert.Single(plugin.Handlers);
        Assert.Equal("example.handler", handler.HandlerId);
        Assert.Contains(adapter.ListHandlers(), item => item.HandlerId == "example.handler");
    }

    [Fact]
    public async Task DurableAdapter_DerivesPluginIdAndRejectsPluginMismatch()
    {
        var client = new RecordingAzureDurableExecutionClient();
        var adapter = new AzureDurableExecutionRuntimeAdapter(client);
        adapter.RegisterPlugin(new NoOpExecutionPlugin());

        await adapter.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = "example.handler"
        });

        Assert.NotNull(client.LastStartCommand);
        Assert.Equal(NoOpExecutionPlugin.PluginId, client.LastStartCommand!.Request.PluginId);
        Assert.Equal(1, client.StartCount);

        var rejected = await adapter.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = "example.handler",
            PluginId = "example.other"
        });
        Assert.Equal(ExecutionRunStatuses.Rejected, rejected.Status);
        Assert.Equal(ExecutionFailureClasses.PluginMismatch, rejected.FailureClass);
        Assert.Contains(NoOpExecutionPlugin.PluginId, rejected.Error);
        Assert.Equal(1, client.StartCount);
    }

    [Fact]
    public async Task DurableAdapter_ReturnsRejectedRunForMissingHandlerWithoutCallingHostClient()
    {
        var client = new RecordingAzureDurableExecutionClient();
        var adapter = new AzureDurableExecutionRuntimeAdapter(client);

        var rejected = await adapter.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = "example.missing",
            Payload = new JsonObject { ["value"] = 42 }
        });

        Assert.Equal(ExecutionRunStatuses.Rejected, rejected.Status);
        Assert.Equal(ExecutionFailureClasses.HandlerMissing, rejected.FailureClass);
        Assert.Equal("example.missing", rejected.HandlerId);
        Assert.Equal("sha256:", rejected.PayloadHash[..7]);
        Assert.NotNull(rejected.CompletedAtUtc);
        Assert.Equal(0, rejected.DurationMs);
        Assert.Equal(0, client.StartCount);
    }

    [Fact]
    public void DurableDialect_MapsRetryPolicyToSerializableDurableOptions()
    {
        var retry = AzureDurableExecutionDialect.ToRetryOptions(new ExecutionRetryPolicy
        {
            MaxAttempts = 4,
            InitialDelaySeconds = 2.5,
            MaxDelaySeconds = 30,
            BackoffMultiplier = 1.5
        });

        Assert.Equal(4, retry.MaxAttempts);
        Assert.Equal(2.5, retry.InitialDelaySeconds);
        Assert.Equal(30, retry.MaxDelaySeconds);
        Assert.Equal(1.5, retry.BackoffMultiplier);
    }

    [Fact]
    public void DurableDialect_CreatesRunAndActivityCommandForHostOrchestration()
    {
        var now = new DateTime(2026, 06, 24, 12, 00, 00, DateTimeKind.Utc);
        var command = AzureDurableExecutionDialect.BuildStartCommand(
            new ExecutionRunRequest
            {
                HandlerId = "example.handler",
                PluginId = "example.plugin",
                CorrelationId = "corr-1",
                ScheduledAtUtc = now.AddMinutes(5),
                Payload = new JsonObject { ["value"] = 42 },
                Tags = { ["tenant"] = "alpha" }
            },
            new[] { new NoOpExecutionHandler("example.handler").Descriptor },
            new AzureDurableExecutionOptions { AdapterId = "azure-test" });

        var waiting = AzureDurableExecutionDialect.CreateQueuedRun(command, now);
        Assert.Equal(command.InstanceId, waiting.Id);
        Assert.Equal(ExecutionRunStatuses.Waiting, waiting.Status);
        Assert.Equal("corr-1", waiting.CorrelationId);
        Assert.Equal("sha256:", waiting.PayloadHash[..7]);
        Assert.Equal("alpha", waiting.Tags["tenant"]);

        var running = AzureDurableExecutionDialect.StartActivityAttempt(waiting, now.AddMinutes(5));
        var activity = AzureDurableExecutionDialect.BuildActivityCommand(running);

        Assert.Equal(ExecutionRunStatuses.Running, running.Status);
        Assert.Equal(1, running.Attempt);
        Assert.Equal(running.Id, activity.RunId);
        Assert.Equal("example.handler", activity.HandlerId);
        Assert.Equal("example.plugin", activity.PluginId);
        Assert.Equal(1, activity.Attempt);
        Assert.Equal(42, activity.Payload!["value"]!.GetValue<int>());
    }

    [Fact]
    public void DurableDialect_AppliesActivityResultAndSchedulesRetry()
    {
        var now = new DateTime(2026, 06, 24, 12, 00, 00, DateTimeKind.Utc);
        var command = AzureDurableExecutionDialect.BuildStartCommand(
            new ExecutionRunRequest
            {
                HandlerId = "example.handler",
                RetryPolicy = new ExecutionRetryPolicy
                {
                    MaxAttempts = 3,
                    InitialDelaySeconds = 2,
                    MaxDelaySeconds = 30,
                    BackoffMultiplier = 2
                }
            },
            new[] { new NoOpExecutionHandler("example.handler").Descriptor });
        var running = AzureDurableExecutionDialect.StartActivityAttempt(
            AzureDurableExecutionDialect.CreateQueuedRun(command, now),
            now.AddSeconds(1));

        var failed = AzureDurableExecutionDialect.ApplyActivityResult(
            running,
            new AzureDurableActivityResult
            {
                RunId = running.Id,
                Result = ExecutionRunResult.Failed("transient", "temporary failure")
            },
            now.AddSeconds(3));

        Assert.Equal(ExecutionRunStatuses.Failed, failed.Status);
        Assert.Equal("transient", failed.FailureClass);
        Assert.Equal("temporary failure", failed.Error);
        Assert.Equal(2000, failed.DurationMs);
        Assert.True(AzureDurableExecutionDialect.ShouldRetry(failed));
        Assert.Equal(TimeSpan.FromSeconds(2), AzureDurableExecutionDialect.CalculateRetryDelay(failed));

        var retryAt = now.AddSeconds(5);
        var waiting = AzureDurableExecutionDialect.ScheduleRetry(failed, retryAt);
        Assert.Equal(ExecutionRunStatuses.Waiting, waiting.Status);
        Assert.Equal(retryAt, waiting.ScheduledAtUtc);
        Assert.Null(waiting.CurrentStep);
    }

    [Fact]
    public async Task DurableAdapter_ForwardsStartCommandThroughInjectedClient()
    {
        var client = new RecordingAzureDurableExecutionClient();
        var adapter = new AzureDurableExecutionRuntimeAdapter(client, new AzureDurableExecutionOptions
        {
            AdapterId = "azure-test",
            TaskHubName = "test-hub",
            WorkerId = "test-worker"
        });
        adapter.RegisterHandler(new NoOpExecutionHandler("example.handler"));

        var run = await adapter.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = "example.handler",
            PluginId = "example.plugin",
            IdempotencyKey = "run-once",
            Payload = new JsonObject { ["value"] = 42 },
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 3 }
        });

        Assert.NotNull(client.LastStartCommand);
        Assert.Equal(client.LastStartCommand!.InstanceId, run.Id);
        Assert.Equal("example.handler", client.LastStartCommand.Request.HandlerId);
        Assert.Equal("example.handler", client.LastStartCommand.Handler.HandlerId);
        Assert.Equal("test-hub", client.LastStartCommand.Metadata["taskHubName"]);
        Assert.Equal("test-worker", client.LastStartCommand.Metadata["workerId"]);
        Assert.Equal(3, client.LastStartCommand.RetryOptions.MaxAttempts);
        Assert.Equal(ExecutionRunStatuses.Queued, run.Status);
    }

    [Fact]
    public async Task DurableAdapter_RejectsOversizedPayloadBeforeCallingHostClient()
    {
        var client = new RecordingAzureDurableExecutionClient();
        var adapter = new AzureDurableExecutionRuntimeAdapter(client, new AzureDurableExecutionOptions
        {
            Limits = new ExecutionRuntimeLimits { MaxPayloadBytes = 24 }
        });
        adapter.RegisterHandler(new NoOpExecutionHandler("example.handler"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = "example.handler",
                Payload = new JsonObject
                {
                    ["value"] = new string('x', 64)
                }
            }));

        Assert.Contains("Run payload", error.Message);
        Assert.Null(client.LastStartCommand);
    }

    [Fact]
    public void DurableDialect_RejectsInvalidLifecycleTransitions()
    {
        var now = new DateTime(2026, 06, 24, 12, 00, 00, DateTimeKind.Utc);
        var command = AzureDurableExecutionDialect.BuildStartCommand(
            new ExecutionRunRequest
            {
                HandlerId = "example.handler"
            },
            new[] { new NoOpExecutionHandler("example.handler").Descriptor });
        var queued = AzureDurableExecutionDialect.CreateQueuedRun(command, now);

        var resultError = Assert.Throws<InvalidOperationException>(() =>
            AzureDurableExecutionDialect.ApplyActivityResult(
                queued,
                new AzureDurableActivityResult
                {
                    RunId = queued.Id,
                    Result = ExecutionRunResult.Succeeded()
                },
                now.AddSeconds(1)));
        Assert.Contains("cannot transition", resultError.Message);

        var succeeded = AzureDurableExecutionDialect.ApplyActivityResult(
            AzureDurableExecutionDialect.StartActivityAttempt(queued, now.AddSeconds(1)),
            new AzureDurableActivityResult
            {
                RunId = queued.Id,
                Result = ExecutionRunResult.Succeeded()
            },
            now.AddSeconds(2));
        var startError = Assert.Throws<InvalidOperationException>(() =>
            AzureDurableExecutionDialect.StartActivityAttempt(succeeded, now.AddSeconds(3)));
        Assert.Contains("cannot transition", startError.Message);

        var retryError = Assert.Throws<InvalidOperationException>(() =>
            AzureDurableExecutionDialect.ScheduleRetry(succeeded, now.AddSeconds(4)));
        Assert.Contains("cannot transition", retryError.Message);
    }

    private sealed class NoOpExecutionHandler : IExecutionHandler
    {
        public NoOpExecutionHandler(string handlerId)
        {
            Descriptor = new ExecutionHandlerDescriptor
            {
                HandlerId = handlerId,
                PluginId = "example.plugin",
                DisplayName = "Example handler"
            };
        }

        public ExecutionHandlerDescriptor Descriptor { get; }

        public Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            return Task.FromResult(ExecutionRunResult.Succeeded());
        }
    }

    private sealed class NoOpExecutionPlugin : IExecutionPlugin
    {
        public const string PluginId = "example.plugin";

        public ExecutionPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = PluginId,
            Name = "Example plugin",
            Version = "1.0.0",
            Handlers =
            {
                new ExecutionHandlerDescriptor
                {
                    HandlerId = "example.handler",
                    PluginId = PluginId,
                    DisplayName = "Example handler"
                }
            }
        };

        public IReadOnlyList<IExecutionHandler> Handlers { get; } = new[] { new NoOpExecutionHandler("example.handler") };
    }

    private sealed class RecordingAzureDurableExecutionClient : IAzureDurableExecutionClient
    {
        public AzureDurableStartCommand? LastStartCommand { get; private set; }
        public int StartCount { get; private set; }

        public Task<ExecutionRun> StartRunAsync(AzureDurableStartCommand command, CancellationToken ct = default)
        {
            LastStartCommand = command;
            StartCount++;
            var now = DateTime.UtcNow;
            return Task.FromResult(new ExecutionRun
            {
                Id = command.InstanceId,
                HandlerId = command.Request.HandlerId,
                PluginId = command.Request.PluginId,
                Status = ExecutionRunStatuses.Queued,
                MaxAttempts = command.Request.RetryPolicy.MaxAttempts,
                RetryPolicy = command.Request.RetryPolicy,
                IdempotencyKey = command.Request.IdempotencyKey,
                CorrelationId = command.Request.CorrelationId ?? command.InstanceId,
                PayloadHash = "sha256:test",
                Payload = command.Request.Payload,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        public Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default)
        {
            return Task.FromResult<ExecutionRun?>(null);
        }

        public Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ExecutionRun>>(Array.Empty<ExecutionRun>());
        }

        public Task<ExecutionRun?> CancelRunAsync(string runId, CancellationToken ct = default)
        {
            return Task.FromResult<ExecutionRun?>(null);
        }

        public Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ExecutionTraceEvent>>(Array.Empty<ExecutionTraceEvent>());
        }

        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ExecutionArtifact>>(Array.Empty<ExecutionArtifact>());
        }

        public Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default)
        {
            return Task.FromResult<ExecutionArtifact?>(null);
        }

        public Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default)
        {
            return Task.FromResult<ExecutionCheckpoint?>(null);
        }

        public Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default)
        {
            return Task.FromResult<ExecutionLease?>(null);
        }

        public Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default)
        {
            return Task.FromResult(false);
        }

        public Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimerRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new ExecutionTimer
            {
                Id = Guid.NewGuid().ToString("N"),
                RunId = request.RunId,
                Name = request.Name,
                FireAtUtc = request.FireAtUtc
            });
        }

        public Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEventRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new ExecutionExternalEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                RunId = request.RunId,
                Name = request.Name,
                Payload = request.Payload
            });
        }

        public Task<ExecutionRuntimeAdapterStatus> GetAdapterStatusAsync(ExecutionRuntimeAdapterDescriptor adapter, CancellationToken ct = default)
        {
            return Task.FromResult(new ExecutionRuntimeAdapterStatus
            {
                Adapter = adapter,
                Available = true,
                Status = "ok"
            });
        }
    }
}
