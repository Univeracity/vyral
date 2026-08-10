using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Execution.Local;
using Vyral.Execution.WorkerClient;

namespace Vyral.Tests.ExecutionWorkerClient;

public sealed class ExecutionPluginWorkerTests
{
    [Fact]
    public async Task Worker_SuspendsAndReplaysPortablePluginDurableWait()
    {
        var plugin = CreateWaitPlugin();
        var handler = Assert.Single(plugin.Handlers);
        var runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"vyral-plugin-worker-{Guid.NewGuid():N}.sqlite")
        });
        runtime.RegisterExternalHandler(handler.Descriptor);
        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = handler.Descriptor.HandlerId,
            PluginId = plugin.Descriptor.PluginId,
            IdempotencyKey = "wait-plugin:one"
        });
        var worker = new ExecutionPluginWorker(
            new InProcessExecutionWorkerTransport(runtime, "worker-a", [handler.Descriptor.HandlerId]),
            [plugin],
            new ExecutionPluginWorkerOptions { HeartbeatInterval = Timeout.InfiniteTimeSpan });

        var waiting = Assert.IsType<ExecutionRun>(await worker.RunOnceAsync(accepted.Id));
        Assert.Equal(ExecutionRunStatuses.Waiting, waiting.Status);
        Assert.NotNull(await runtime.GetCheckpointAsync(accepted.Id, "before-wait"));

        await runtime.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = "approval",
            Payload = new JsonObject { ["approved"] = true }
        });
        var completed = Assert.IsType<ExecutionRun>(await worker.RunOnceAsync(accepted.Id));

        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
        Assert.True(completed.Result!["approved"]!.GetValue<bool>());
        Assert.Single(await runtime.ListArtifactsAsync(accepted.Id), artifact => artifact.Name == "approval-summary");
        var history = await runtime.GetHistoryAsync(accepted.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.CheckpointWritten);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitRegistered);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunCompleted);
    }

    [Fact]
    public async Task Worker_HeartbeatsAndCompletesCancellationWithoutExposingLeaseToken()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = CreatePlugin(
            "worker.heartbeat.plugin",
            "worker.heartbeat.handler",
            async (_, ct) =>
            {
                handlerStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return ExecutionRunResult.Succeeded();
            });
        var transport = new CancellingHeartbeatTransport(Assert.Single(plugin.Handlers).Descriptor);
        var worker = new ExecutionPluginWorker(
            transport,
            [plugin],
            new ExecutionPluginWorkerOptions
            {
                LeaseTtlSeconds = 1,
                HeartbeatInterval = TimeSpan.FromMilliseconds(10)
            });

        var completed = Assert.IsType<ExecutionRun>(await worker.RunOnceAsync("run-heartbeat"));

        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(transport.HeartbeatCount > 0);
        Assert.Equal(ExecutionRunStatuses.Cancelled, completed.Status);
        Assert.Equal(ExecutionFailureClasses.Cancelled, transport.CompletedResult!.FailureClass);
        Assert.DoesNotContain("lease-secret", completed.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_BoundsUnexpectedHandlerFailureToFixedPortableDiagnostic()
    {
        var plugin = CreatePlugin(
            "worker.failure.plugin",
            "worker.failure.handler",
            (_, _) => throw new InvalidOperationException("dependency-secret-must-not-escape"));
        var transport = new CancellingHeartbeatTransport(
            Assert.Single(plugin.Handlers).Descriptor,
            cancelOnHeartbeat: false);
        var worker = new ExecutionPluginWorker(
            transport,
            [plugin],
            new ExecutionPluginWorkerOptions { HeartbeatInterval = Timeout.InfiniteTimeSpan });

        var failed = Assert.IsType<ExecutionRun>(await worker.RunOnceAsync("run-failure"));

        Assert.Equal(ExecutionRunStatuses.Failed, failed.Status);
        Assert.Equal(ExecutionFailureClasses.Unknown, failed.FailureClass);
        Assert.Equal("External plugin handler failed.", failed.Error);
        Assert.DoesNotContain("secret", failed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Worker_DoesNotReinterpretOrRetryTransportCompletionFailure()
    {
        var plugin = CreatePlugin(
            "worker.transport.plugin",
            "worker.transport.handler",
            (_, _) => Task.FromResult(ExecutionRunResult.Succeeded()));
        var transport = new CancellingHeartbeatTransport(
            Assert.Single(plugin.Handlers).Descriptor,
            cancelOnHeartbeat: false,
            throwOnComplete: true);
        var worker = new ExecutionPluginWorker(
            transport,
            [plugin],
            new ExecutionPluginWorkerOptions { HeartbeatInterval = Timeout.InfiniteTimeSpan });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunOnceAsync("run-transport"));

        Assert.Equal("transport completion failed", error.Message);
        Assert.Equal(1, transport.CompletionCount);
    }

    private static IExecutionPlugin CreateWaitPlugin() =>
        CreatePlugin(
            "worker.wait.plugin",
            "worker.wait.handler",
            async (context, ct) =>
            {
                if (await context.GetCheckpointAsync("before-wait", ct) is null)
                {
                    await context.PutCheckpointAsync(new ExecutionCheckpointWrite
                    {
                        Key = "before-wait",
                        Content = new JsonObject { ["ready"] = true }
                    }, ct);
                }

                var outcome = await context.WaitForExternalEventAsync(
                    "approval",
                    DateTime.UtcNow.AddMinutes(1),
                    ct);
                var approved = outcome.Event?.Payload?["approved"]?.GetValue<bool>() == true;
                await context.PutArtifactAsync(new ExecutionArtifactWrite
                {
                    Name = "approval-summary",
                    Kind = ExecutionArtifactKinds.Json,
                    Content = new JsonObject { ["approved"] = approved }
                }, ct);
                return ExecutionRunResult.Succeeded(new JsonObject { ["approved"] = approved });
            });

    private static IExecutionPlugin CreatePlugin(
        string pluginId,
        string handlerId,
        Func<IExecutionRunContext, CancellationToken, Task<ExecutionRunResult>> execute)
    {
        var descriptor = ExecutionDescriptors.Handler(
            handlerId,
            handlerId,
            builder => builder.WithPluginId(pluginId));
        var handler = new DelegateExecutionHandler(descriptor, execute);
        return new StaticExecutionPlugin(
            ExecutionDescriptors.Plugin(pluginId, pluginId, "1.0.0", builder => builder.AddHandler(descriptor)),
            [handler]);
    }

    private sealed class CancellingHeartbeatTransport : IExecutionWorkerTransport
    {
        private readonly bool _cancelOnHeartbeat;
        private readonly bool _throwOnComplete;
        private ExecutionExternalWorkerLease? _lease;

        public CancellingHeartbeatTransport(
            ExecutionHandlerDescriptor handler,
            bool cancelOnHeartbeat = true,
            bool throwOnComplete = false)
        {
            _cancelOnHeartbeat = cancelOnHeartbeat;
            _throwOnComplete = throwOnComplete;
            _lease = new ExecutionExternalWorkerLease
            {
                LeaseKey = "lease-a",
                LeaseToken = "lease-secret",
                WorkerId = "worker-a",
                Run = new ExecutionRun
                {
                    Id = handler.HandlerId.Contains("failure", StringComparison.Ordinal)
                        ? "run-failure"
                        : handler.HandlerId.Contains("transport", StringComparison.Ordinal)
                            ? "run-transport"
                            : "run-heartbeat",
                    HandlerId = handler.HandlerId,
                    PluginId = handler.PluginId,
                    Status = ExecutionRunStatuses.Running,
                    Attempt = 1
                }
            };
        }

        public int HeartbeatCount { get; private set; }
        public int CompletionCount { get; private set; }
        public ExecutionRunResult? CompletedResult { get; private set; }

        public Task<ExecutionExternalWorkerLease?> LeaseNextAsync(string? runId = null, double ttlSeconds = 60, CancellationToken ct = default)
        {
            var lease = _lease;
            _lease = null;
            return Task.FromResult(lease);
        }

        public Task<ExecutionExternalWorkerLease> HeartbeatAsync(ExecutionExternalWorkerLease lease, double ttlSeconds = 60, CancellationToken ct = default)
        {
            HeartbeatCount++;
            lease.Run.CancellationRequested = _cancelOnHeartbeat;
            return Task.FromResult(lease);
        }

        public Task<ExecutionRun> CompleteAsync(ExecutionExternalWorkerLease lease, ExecutionRunResult result, CancellationToken ct = default)
        {
            CompletionCount++;
            if (_throwOnComplete)
            {
                throw new InvalidOperationException("transport completion failed");
            }

            CompletedResult = result;
            lease.Run.Status = result.Status;
            lease.Run.Result = result.Result;
            lease.Run.FailureClass = result.FailureClass;
            lease.Run.Error = result.Error;
            return Task.FromResult(lease.Run);
        }

        public Task<ExecutionCheckpoint> CheckpointAsync(ExecutionExternalWorkerLease lease, ExecutionCheckpointWrite checkpoint, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExecutionCheckpoint?> GetCheckpointAsync(ExecutionExternalWorkerLease lease, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExecutionRun> ReportAsync(ExecutionExternalWorkerLease lease, ExecutionRunUpdate update, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecordEventAsync(ExecutionExternalWorkerLease lease, ExecutionExternalWorkerEventRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExecutionArtifact> PutArtifactAsync(ExecutionExternalWorkerLease lease, ExecutionArtifactWrite artifact, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExecutionExternalWorkerWaitResponse> WaitAsync(ExecutionExternalWorkerLease lease, ExecutionExternalWorkerWaitRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
