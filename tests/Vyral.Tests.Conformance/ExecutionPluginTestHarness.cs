using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Tests.Conformance;

public sealed class ExecutionPluginTestHarness
{
    private readonly List<ExecutionRunUpdate> _reports = new();
    private readonly List<ExecutionTraceEvent> _events = new();
    private readonly List<ExecutionArtifactWrite> _artifacts = new();
    private readonly Dictionary<string, ExecutionCheckpoint> _checkpoints = new(StringComparer.Ordinal);
    private readonly List<ExecutionLease> _leases = new();
    private readonly List<ExecutionTimer> _timers = new();
    private readonly List<ExecutionExternalEvent> _externalEvents = new();

    public IReadOnlyList<ExecutionRunUpdate> Reports => _reports;
    public IReadOnlyList<ExecutionTraceEvent> Events => _events;
    public IReadOnlyList<ExecutionArtifactWrite> Artifacts => _artifacts;
    public IReadOnlyCollection<ExecutionCheckpoint> Checkpoints => _checkpoints.Values;
    public IReadOnlyList<ExecutionLease> Leases => _leases;
    public IReadOnlyList<ExecutionTimer> Timers => _timers;
    public IReadOnlyList<ExecutionExternalEvent> ExternalEvents => _externalEvents;

    public async Task<ExecutionRunResult> ExecuteAsync(
        IExecutionHandler handler,
        JsonNode? payload = null,
        string? runId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ExecutionContractValidator.ValidateHandlerDescriptor(handler.Descriptor);
        var now = DateTime.UtcNow;
        var run = new ExecutionRun
        {
            Id = string.IsNullOrWhiteSpace(runId) ? OrderedId.CreateString() : runId,
            HandlerId = handler.Descriptor.HandlerId,
            PluginId = handler.Descriptor.PluginId,
            Status = ExecutionRunStatuses.Running,
            Attempt = 1,
            MaxAttempts = Math.Max(1, handler.Descriptor.MaxAttempts),
            CorrelationId = OrderedId.CreateString(),
            Payload = CloneNode(payload),
            PayloadHash = "sha256:test-harness",
            CreatedAtUtc = now,
            StartedAtUtc = now,
            UpdatedAtUtc = now
        };

        var result = await handler.ExecuteAsync(new HarnessRunContext(this, run, ct), ct);
        ExecutionContractValidator.ValidateRunResult(result);
        return result;
    }

    private static JsonNode? CloneNode(JsonNode? value)
    {
        return value is null ? null : JsonNode.Parse(value.ToJsonString(ExecutionJson.Options));
    }

    private sealed class HarnessRunContext : IExecutionRunContext
    {
        private readonly ExecutionPluginTestHarness _harness;

        public HarnessRunContext(ExecutionPluginTestHarness harness, ExecutionRun run, CancellationToken cancellationToken)
        {
            _harness = harness;
            Run = run;
            CancellationToken = cancellationToken;
        }

        public ExecutionRun Run { get; private set; }
        public CancellationToken CancellationToken { get; }

        public Task<ExecutionRun> ReportAsync(ExecutionRunUpdate update, CancellationToken ct = default)
        {
            ExecutionContractValidator.ValidateRunUpdate(update);
            _harness._reports.Add(update);
            ApplyUpdate(Run, update);
            Run.UpdatedAtUtc = DateTime.UtcNow;
            return Task.FromResult(Run);
        }

        public Task RecordEventAsync(string type, string? message = null, string severity = "info", JsonObject? details = null, CancellationToken ct = default)
        {
            var traceEvent = new ExecutionTraceEvent
            {
                RunId = Run.Id,
                Type = type,
                Attempt = Run.Attempt,
                Status = Run.Status,
                Message = message,
                Severity = string.IsNullOrWhiteSpace(severity) ? "info" : severity,
                Details = details,
                Context =
                {
                    ["runId"] = Run.Id,
                    ["handlerId"] = Run.HandlerId,
                    ["adapterId"] = "test-harness",
                    ["runtimeKind"] = "test.harness",
                    ["workerId"] = "test-harness"
                }
            };
            if (!string.IsNullOrWhiteSpace(Run.PluginId))
            {
                traceEvent.Context["pluginId"] = Run.PluginId!;
            }

            ExecutionContractValidator.ValidateTraceEvent(traceEvent);
            _harness._events.Add(traceEvent);
            return Task.CompletedTask;
        }

        public Task<ExecutionArtifact> PutArtifactAsync(ExecutionArtifactWrite artifact, CancellationToken ct = default)
        {
            ExecutionContractValidator.ValidateArtifactWrite(artifact);
            _harness._artifacts.Add(artifact);
            return Task.FromResult(new ExecutionArtifact
            {
                RunId = Run.Id,
                Name = artifact.Name,
                Kind = artifact.Kind,
                MediaType = artifact.MediaType,
                Text = artifact.Text,
                Content = artifact.Content,
                Uri = artifact.Uri,
                ContentHash = "sha256:test-harness",
                SizeBytes = 0,
                Metadata = new Dictionary<string, string>(artifact.Metadata, StringComparer.Ordinal)
            });
        }

        public Task<ExecutionCheckpoint> PutCheckpointAsync(ExecutionCheckpointWrite checkpoint, CancellationToken ct = default)
        {
            ExecutionContractValidator.ValidateCheckpointWrite(checkpoint);
            var saved = new ExecutionCheckpoint
            {
                RunId = Run.Id,
                Key = checkpoint.Key,
                Content = CloneNode(checkpoint.Content),
                ContentHash = "sha256:test-harness",
                UpdatedAtUtc = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>(checkpoint.Metadata, StringComparer.Ordinal)
            };
            _harness._checkpoints[checkpoint.Key] = saved;
            return Task.FromResult(saved);
        }

        public Task<ExecutionCheckpoint?> GetCheckpointAsync(string key, CancellationToken ct = default)
        {
            return Task.FromResult(_harness._checkpoints.TryGetValue(key, out var checkpoint)
                ? new ExecutionCheckpoint
                {
                    RunId = checkpoint.RunId,
                    Key = checkpoint.Key,
                    Content = CloneNode(checkpoint.Content),
                    ContentHash = checkpoint.ContentHash,
                    UpdatedAtUtc = checkpoint.UpdatedAtUtc,
                    Metadata = new Dictionary<string, string>(checkpoint.Metadata, StringComparer.Ordinal)
                }
                : null);
        }

        public Task<ExecutionLease?> TryAcquireLeaseAsync(string leaseKey, double ttlSeconds = 60, JsonObject? metadata = null, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var lease = new ExecutionLease
            {
                LeaseKey = leaseKey,
                OwnerId = Run.Id,
                RunId = Run.Id,
                AcquiredAtUtc = now,
                ExpiresAtUtc = now.AddSeconds(Math.Max(1, ttlSeconds)),
                Metadata = metadata
            };
            _harness._leases.Add(lease);
            return Task.FromResult<ExecutionLease?>(lease);
        }

        public Task<bool> ReleaseLeaseAsync(string leaseKey, CancellationToken ct = default)
        {
            return Task.FromResult(_harness._leases.Any(lease =>
                string.Equals(lease.LeaseKey, leaseKey, StringComparison.Ordinal) &&
                string.Equals(lease.OwnerId, Run.Id, StringComparison.Ordinal)));
        }

        public Task<ExecutionTimer> ScheduleTimerAsync(string name, DateTime fireAtUtc, JsonNode? payload = null, CancellationToken ct = default)
        {
            var timer = new ExecutionTimer
            {
                Name = name,
                RunId = Run.Id,
                FireAtUtc = fireAtUtc,
                Payload = payload
            };
            _harness._timers.Add(timer);
            return Task.FromResult(timer);
        }

        public Task<ExecutionExternalEvent> RaiseEventAsync(string name, JsonNode? payload = null, CancellationToken ct = default)
        {
            var externalEvent = new ExecutionExternalEvent
            {
                Name = name,
                RunId = Run.Id,
                Payload = payload
            };
            _harness._externalEvents.Add(externalEvent);
            return Task.FromResult(externalEvent);
        }

        public Task<ExecutionWaitResult> WaitForExternalEventAsync(string name, DateTime? timeoutAtUtc = null, CancellationToken ct = default)
        {
            throw new NotSupportedException("The backend-free execution plugin test harness does not implement durable waits.");
        }

        public Task<ExecutionWaitResult> WaitForTimerAsync(string name, DateTime fireAtUtc, JsonNode? payload = null, CancellationToken ct = default)
        {
            throw new NotSupportedException("The backend-free execution plugin test harness does not implement durable waits.");
        }

        private static void ApplyUpdate(ExecutionRun run, ExecutionRunUpdate update)
        {
            if (!string.IsNullOrWhiteSpace(update.Status))
            {
                ExecutionRunLifecycle.EnsureTransition(run.Status, update.Status!);
                run.Status = update.Status!;
            }

            run.Requested = update.Requested ?? run.Requested;
            run.Attempted = update.Attempted ?? run.Attempted;
            run.Succeeded = update.Succeeded ?? run.Succeeded;
            run.Failed = update.Failed ?? run.Failed;
            run.Progress = update.Progress.HasValue ? Math.Clamp(update.Progress.Value, 0, 1) : run.Progress;
            run.CurrentStep = update.CurrentStep ?? run.CurrentStep;
            run.FailureClass = update.FailureClass ?? run.FailureClass;
            run.Error = update.Error ?? run.Error;
            run.Result = update.Result ?? run.Result;
            run.StatusDetails = update.StatusDetails ?? run.StatusDetails;
        }
    }
}
