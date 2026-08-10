using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Execution.AzureDurable;
using Vyral.Execution.Local;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Azure;

public sealed class AzureDurableExecutionConsumerSwitchingTests
{
    [Fact]
    public async Task AzureDurableExecution_ExternalConsumerSampleRunsUnchangedOnLocalAndAzure()
    {
        var runtimes = new IExecutionRuntimeAdapter[]
        {
            CreateLocalRuntime(),
            CreateAzureRuntime()
        };

        var results = new List<ExecutionRun>();
        foreach (var runtime in runtimes)
        {
            var completed = await ExternalExecutionConsumerSample.RunAsync(
                runtime,
                $"external-sample:{runtime.Adapter.AdapterId}");
            await ExternalExecutionConsumerSample.AssertRunShapeAsync(runtime, completed);
            results.Add(completed);
        }

        Assert.All(results, run => Assert.Equal(ExternalExecutionConsumerSample.HandlerId, run.HandlerId));
        Assert.All(results, run => Assert.Equal(ExternalExecutionConsumerSample.PluginId, run.PluginId));
        Assert.Equal(
            results[0].Result!["digest"]!.GetValue<string>(),
            results[1].Result!["digest"]!.GetValue<string>());
    }

    private static LocalExecutionRuntime CreateLocalRuntime()
    {
        return new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            AdapterId = "local-consumer-sample",
            DatabasePath = Path.Combine(Path.GetTempPath(), $"vyral-execution-consumer-sample-{Guid.NewGuid():N}.sqlite"),
            MaxActiveRuns = 8,
            MaxRetainedTerminalRuns = 20,
            DefaultListLimit = 20,
            MaxListLimit = 100
        });
    }

    private static IExecutionRuntimeAdapter CreateAzureRuntime()
    {
        var options = new AzureDurableExecutionOptions
        {
            AdapterId = "azure-consumer-sample",
            TaskHubName = "test-hub",
            WorkerId = "test-worker",
            MaxActiveRuns = 16,
            DefaultListLimit = 20,
            MaxListLimit = 100
        };
        var registry = new AzureDurableExecutionRegistry(options.Limits);
        var host = new AzureDurableExecutionHost(options, registry);
        var scheduler = new InlineAzureDurableScheduler(host, options);
        var client = new AzureDurableExecutionClient(host, scheduler);
        return new HostBackedRuntime(new AzureDurableExecutionRuntimeAdapter(client, options, registry), scheduler);
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
    }

    private sealed class InlineAzureDurableScheduler : IAzureDurableExecutionOrchestrationScheduler
    {
        private readonly AzureDurableExecutionHost _host;
        private readonly AzureDurableExecutionOptions _options;
        private readonly ConcurrentDictionary<string, Task> _instances = new(StringComparer.Ordinal);

        public InlineAzureDurableScheduler(AzureDurableExecutionHost host, AzureDurableExecutionOptions options)
        {
            _host = host;
            _options = options;
        }

        public Task ScheduleNewAsync(AzureDurableStartCommand command, CancellationToken ct = default)
        {
            _instances.GetOrAdd(command.InstanceId, _ =>
                Task.Run(() => _host.OrchestrateAsync(Clone(command), new InlineAzureDurableDriver(_host), CancellationToken.None)));
            return Task.CompletedTask;
        }

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
