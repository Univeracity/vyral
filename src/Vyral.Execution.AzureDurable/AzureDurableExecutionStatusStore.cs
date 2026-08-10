using System.Text.Json;
using Vyral.Execution;

namespace Vyral.Execution.AzureDurable;

public interface IAzureDurableExecutionStatusStore
{
    Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default);
    Task<ExecutionRun?> FindRunByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default);
    Task<int> CountActiveRunsAsync(CancellationToken ct = default);
    Task<AzureDurableRunCreation> CreateRunIfAbsentAsync(ExecutionRun run, CancellationToken ct = default);
    /// <summary>
    /// Persists a run transition and returns the authoritative stored run. Stores must preserve a
    /// terminal run (and a pending cancellation) when a stale activity attempts to write over it.
    /// </summary>
    Task<ExecutionRun> UpsertRunAsync(ExecutionRun run, CancellationToken ct = default);
    Task AppendEventAsync(ExecutionTraceEvent traceEvent, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default);
    Task PutArtifactAsync(ExecutionArtifact artifact, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default);
    Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default);
    Task PutCheckpointAsync(ExecutionCheckpoint checkpoint, CancellationToken ct = default);
    Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default);
    Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLease lease, CancellationToken ct = default);
    Task<ExecutionLease?> GetLeaseAsync(string leaseKey, CancellationToken ct = default);
    Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default);
    Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimer timer, CancellationToken ct = default);
    Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEvent externalEvent, CancellationToken ct = default);
    Task<AzureDurableWait?> GetDurableWaitAsync(string runId, CancellationToken ct = default);
    Task<AzureDurableWait> RegisterDurableWaitAsync(AzureDurableWait wait, string runId, CancellationToken ct = default);
    Task<ExecutionRun> ResumeDurableWaitAsync(string runId, ExecutionWaitResult outcome, CancellationToken ct = default);
    Task<ExecutionWaitResult?> TakeDurableWaitOutcomeAsync(string runId, string kind, string name, CancellationToken ct = default);
}

/// <summary>
/// Result of atomically reserving a Vyral run id. Adapters use the durable run id as the
/// idempotency reservation, so concurrent equivalent starts resolve to one run.
/// </summary>
public sealed class AzureDurableRunCreation
{
    public bool Created { get; init; }
    public ExecutionRun Run { get; init; } = new();
}

public sealed class AzureDurableInMemoryExecutionStatusStore : IAzureDurableExecutionStatusStore
{
    private readonly Dictionary<string, ExecutionRun> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionLease> _leases = new(StringComparer.Ordinal);
    private readonly List<ExecutionTraceEvent> _events = new();
    private readonly List<ExecutionArtifact> _artifacts = new();
    private readonly Dictionary<string, ExecutionCheckpoint> _checkpoints = new(StringComparer.Ordinal);
    private readonly List<ExecutionTimer> _timers = new();
    private readonly List<ExecutionExternalEvent> _externalEvents = new();
    private readonly Dictionary<string, AzureDurableWait> _waits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionWaitResult> _waitOutcomes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly AzureDurableExecutionOptions _options;

    public AzureDurableInMemoryExecutionStatusStore(AzureDurableExecutionOptions? options = null)
    {
        _options = options ?? new AzureDurableExecutionOptions();
    }

    public async Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            if (!_runs.TryGetValue(runId, out var run))
            {
                return null;
            }

            var clone = Clone(run);
            if (!includeResult)
            {
                clone.Result = null;
            }

            return clone;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ExecutionRun?> FindRunByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var run = _runs.Values
                .Where(candidate => string.Equals(candidate.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                .OrderBy(candidate => candidate.CreatedAtUtc)
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            return run is null ? null : Clone(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default)
    {
        query ??= new ExecutionRunQuery();
        var limit = ValidateLimit(query.Limit);
        await _mutex.WaitAsync(ct);
        try
        {
            var runs = _runs.Values.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(query.HandlerId))
            {
                runs = runs.Where(run => string.Equals(run.HandlerId, query.HandlerId, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(query.PluginId))
            {
                runs = runs.Where(run => string.Equals(run.PluginId, query.PluginId, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(query.Status))
            {
                runs = runs.Where(run => string.Equals(run.Status, query.Status, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(query.CorrelationId))
            {
                runs = runs.Where(run => string.Equals(run.CorrelationId, query.CorrelationId, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(query.IdempotencyKey))
            {
                runs = runs.Where(run => string.Equals(run.IdempotencyKey, query.IdempotencyKey, StringComparison.Ordinal));
            }

            if (query.CreatedAfterUtc.HasValue)
            {
                var createdAfter = query.CreatedAfterUtc.Value.ToUniversalTime();
                runs = runs.Where(run => run.CreatedAtUtc >= createdAfter);
            }

            if (query.CreatedBeforeUtc.HasValue)
            {
                var createdBefore = query.CreatedBeforeUtc.Value.ToUniversalTime();
                runs = runs.Where(run => run.CreatedAtUtc <= createdBefore);
            }

            if (query.UpdatedAfterUtc.HasValue)
            {
                var updatedAfter = query.UpdatedAfterUtc.Value.ToUniversalTime();
                runs = runs.Where(run => run.UpdatedAtUtc >= updatedAfter);
            }

            if (query.UpdatedBeforeUtc.HasValue)
            {
                var updatedBefore = query.UpdatedBeforeUtc.Value.ToUniversalTime();
                runs = runs.Where(run => run.UpdatedAtUtc <= updatedBefore);
            }

            if (query.Tags.Count > 0)
            {
                runs = runs.Where(run => MatchesTagFilters(run, query.Tags));
            }

            return runs
                .OrderByDescending(run => run.CreatedAtUtc)
                .ThenBy(run => run.Id, StringComparer.Ordinal)
                .Take(limit)
                .Select(run =>
                {
                    var clone = Clone(run);
                    if (!query.IncludeResult)
                    {
                        clone.Result = null;
                    }

                    return clone;
                })
                .ToList();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static bool MatchesTagFilters(ExecutionRun run, IReadOnlyDictionary<string, string> filters)
    {
        foreach (var (key, value) in filters)
        {
            if (!run.Tags.TryGetValue(key, out var actual) ||
                !string.Equals(actual, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<int> CountActiveRunsAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            return _runs.Values.Count(run => ExecutionRunLifecycle.IsActive(run.Status));
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<AzureDurableRunCreation> CreateRunIfAbsentAsync(ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await _mutex.WaitAsync(ct);
        try
        {
            if (_runs.TryGetValue(run.Id, out var existing))
            {
                return new AzureDurableRunCreation { Created = false, Run = Clone(existing) };
            }

            var stored = Clone(run);
            _runs[stored.Id] = stored;
            return new AzureDurableRunCreation { Created = true, Run = Clone(stored) };
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ExecutionRun> UpsertRunAsync(ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await _mutex.WaitAsync(ct);
        try
        {
            if (_runs.TryGetValue(run.Id, out var existing) &&
                (ExecutionRunStatuses.IsTerminal(existing.Status) ||
                 (existing.CancellationRequested && run.Status != ExecutionRunStatuses.Cancelled)))
            {
                return Clone(existing);
            }

            _runs[run.Id] = Clone(run);
            return Clone(_runs[run.Id]);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task AppendEventAsync(ExecutionTraceEvent traceEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(traceEvent);
        await _mutex.WaitAsync(ct);
        try
        {
            _events.Add(Clone(traceEvent));
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default)
    {
        query ??= new ExecutionHistoryQuery();
        var limit = ValidateLimit(query.Limit);
        await _mutex.WaitAsync(ct);
        try
        {
            return _events
                .Where(item => string.Equals(item.RunId, runId, StringComparison.Ordinal))
                .OrderBy(item => item.TimestampUtc)
                .ThenBy(item => item.SequenceId, StringComparer.Ordinal)
                .Take(limit)
                .Select(Clone)
                .ToList();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task PutArtifactAsync(ExecutionArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        await _mutex.WaitAsync(ct);
        try
        {
            _artifacts.Add(Clone(artifact));
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            return _artifacts
                .Where(item => string.Equals(item.RunId, runId, StringComparison.Ordinal))
                .OrderBy(item => item.CreatedAtUtc)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Select(Clone)
                .ToList();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var artifact = _artifacts
                .Where(item => string.Equals(item.RunId, runId, StringComparison.Ordinal) &&
                    (string.Equals(item.Id, artifactRef, StringComparison.Ordinal) ||
                     string.Equals(item.Name, artifactRef, StringComparison.Ordinal)))
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            return artifact is null ? null : Clone(artifact);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task PutCheckpointAsync(ExecutionCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await _mutex.WaitAsync(ct);
        try
        {
            _checkpoints[CheckpointId(checkpoint.RunId, checkpoint.Key)] = Clone(checkpoint);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            return _checkpoints.TryGetValue(CheckpointId(runId, key), out var checkpoint)
                ? Clone(checkpoint)
                : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await _mutex.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (_leases.TryGetValue(lease.LeaseKey, out var existing) &&
                existing.ExpiresAtUtc > now &&
                !string.Equals(existing.OwnerId, lease.OwnerId, StringComparison.Ordinal))
            {
                return null;
            }

            _leases[lease.LeaseKey] = Clone(lease);
            return Clone(lease);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ExecutionLease?> GetLeaseAsync(string leaseKey, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            return _leases.TryGetValue(leaseKey, out var lease) ? Clone(lease) : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            if (!_leases.TryGetValue(leaseKey, out var existing) ||
                !string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal))
            {
                return false;
            }

            _leases.Remove(leaseKey);
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimer timer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(timer);
        await _mutex.WaitAsync(ct);
        try
        {
            _timers.Add(Clone(timer));
            return Clone(timer);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEvent externalEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(externalEvent);
        await _mutex.WaitAsync(ct);
        try
        {
            _externalEvents.Add(Clone(externalEvent));
            return Clone(externalEvent);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<AzureDurableWait> RegisterDurableWaitAsync(AzureDurableWait wait, string runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wait);
        await _mutex.WaitAsync(ct);
        try
        {
            var normalizedRunId = RequireId(runId, "Run id");
            var normalized = NormalizeWait(wait);
            if (!_runs.TryGetValue(normalizedRunId, out var run))
            {
                throw new InvalidOperationException($"Execution run '{normalizedRunId}' was not found.");
            }

            if (_waits.TryGetValue(normalizedRunId, out var existing))
            {
                if (WaitsMatch(existing, normalized)) return Clone(existing);
                throw new InvalidOperationException($"Execution run '{normalizedRunId}' already has a different durable wait.");
            }

            if (ExecutionRunStatuses.IsTerminal(run.Status) || run.CancellationRequested)
            {
                throw new OperationCanceledException($"Execution run '{normalizedRunId}' was cancelled before its durable wait could be registered.");
            }

            if (run.Status != ExecutionRunStatuses.Running)
            {
                throw new InvalidOperationException($"Execution run '{normalizedRunId}' is not running and cannot register a durable wait.");
            }

            var waiting = Clone(run);
            ExecutionRunLifecycle.EnsureTransition(waiting.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.DurableWait);
            waiting.Status = ExecutionRunStatuses.Waiting;
            waiting.CurrentStep = $"waiting:{normalized.Kind}:{normalized.Name}";
            waiting.ScheduledAtUtc = normalized.FireAtUtc;
            waiting.UpdatedAtUtc = DateTime.UtcNow;
            _runs[normalizedRunId] = waiting;
            _waits[normalizedRunId] = Clone(normalized);
            if (normalized.Timer is not null)
            {
                _timers.RemoveAll(timer => string.Equals(timer.Id, normalized.Timer.Id, StringComparison.Ordinal));
                _timers.Add(Clone(normalized.Timer));
            }

            return Clone(normalized);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<AzureDurableWait?> GetDurableWaitAsync(string runId, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            return _waits.TryGetValue(RequireId(runId, "Run id"), out var wait) ? Clone(wait) : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ExecutionRun> ResumeDurableWaitAsync(string runId, ExecutionWaitResult outcome, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await _mutex.WaitAsync(ct);
        try
        {
            var normalizedRunId = RequireId(runId, "Run id");
            if (!_runs.TryGetValue(normalizedRunId, out var run))
            {
                throw new InvalidOperationException($"Execution run '{normalizedRunId}' was not found.");
            }

            if (ExecutionRunStatuses.IsTerminal(run.Status)) return Clone(run);
            if (!_waits.TryGetValue(normalizedRunId, out var wait))
            {
                if (_waitOutcomes.Keys.Any(key => key.StartsWith(normalizedRunId + "\n", StringComparison.Ordinal)))
                {
                    return Clone(run);
                }

                // A provider can replay the step activity after its original wake transition and
                // handler replay have completed. The outcome may already have been consumed by
                // that handler, so treat a non-waiting run as the idempotent wake result.
                if (run.Status != ExecutionRunStatuses.Waiting)
                {
                    return Clone(run);
                }

                throw new InvalidOperationException($"Execution run '{normalizedRunId}' has no durable wait to resume.");
            }

            var normalizedOutcome = NormalizeWaitOutcome(outcome, wait);
            var waiting = Clone(run);
            if (waiting.Status != ExecutionRunStatuses.Waiting)
            {
                throw new InvalidOperationException($"Execution run '{normalizedRunId}' is not waiting and cannot resume a durable wait.");
            }

            ExecutionRunLifecycle.EnsureTransition(waiting.Status, ExecutionRunStatuses.Queued);
            waiting.Status = ExecutionRunStatuses.Queued;
            waiting.CurrentStep = null;
            waiting.ScheduledAtUtc = null;
            waiting.UpdatedAtUtc = DateTime.UtcNow;
            _runs[normalizedRunId] = waiting;
            _waits.Remove(normalizedRunId);
            _waitOutcomes[WaitOutcomeId(normalizedRunId, wait.Kind, wait.Name)] = Clone(normalizedOutcome);
            return Clone(waiting);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ExecutionWaitResult?> TakeDurableWaitOutcomeAsync(string runId, string kind, string name, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var key = WaitOutcomeId(RequireId(runId, "Run id"), NormalizeWaitKind(kind), RequireId(name, "Wait name"));
            if (!_waitOutcomes.Remove(key, out var outcome)) return null;
            return Clone(outcome);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private int ValidateLimit(int? limit)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new InvalidOperationException("Execution list limit must be greater than zero.");
        }

        var effective = limit ?? _options.DefaultListLimit;
        if (effective > _options.MaxListLimit)
        {
            throw new InvalidOperationException($"Execution list limit cannot exceed {_options.MaxListLimit}.");
        }

        return effective;
    }

    private static string CheckpointId(string runId, string key)
    {
        return $"{runId}\n{key}";
    }

    private static T Clone<T>(T value)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ExecutionJson.Options), ExecutionJson.Options)!;
    }

    private static AzureDurableWait NormalizeWait(AzureDurableWait wait)
    {
        var normalized = Clone(wait);
        normalized.Kind = NormalizeWaitKind(normalized.Kind);
        normalized.Name = RequireId(normalized.Name, "Wait name");
        normalized.FireAtUtc = normalized.FireAtUtc?.ToUniversalTime();
        if (normalized.Kind == AzureDurableWaitKinds.Timer)
        {
            if (normalized.Timer is null || !normalized.FireAtUtc.HasValue)
            {
                throw new InvalidOperationException("A durable timer wait requires a timer and fire time.");
            }
        }

        return normalized;
    }

    private static ExecutionWaitResult NormalizeWaitOutcome(ExecutionWaitResult outcome, AzureDurableWait wait)
    {
        var normalized = Clone(outcome);
        if (!string.Equals(normalized.Name, wait.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Durable wait outcome '{normalized.Name}' does not match wait '{wait.Name}'.");
        }

        var expectedOutcome = wait.Kind == AzureDurableWaitKinds.Timer
            ? ExecutionWaitOutcomes.Timer
            : normalized.Outcome == ExecutionWaitOutcomes.TimedOut
                ? ExecutionWaitOutcomes.TimedOut
                : ExecutionWaitOutcomes.ExternalEvent;
        if (!string.Equals(normalized.Outcome, expectedOutcome, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Durable wait outcome '{normalized.Outcome}' is not valid for '{wait.Kind}'.");
        }

        if (expectedOutcome == ExecutionWaitOutcomes.Timer)
        {
            normalized.Timer = Clone(wait.Timer!);
            normalized.Event = null;
        }
        else if (expectedOutcome == ExecutionWaitOutcomes.TimedOut)
        {
            normalized.Event = null;
            normalized.Timer = null;
        }
        else if (normalized.Event is null || !string.Equals(normalized.Event.Name, wait.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An external-event durable wait outcome requires the matching event.");
        }

        return normalized;
    }

    private static bool WaitsMatch(AzureDurableWait first, AzureDurableWait second) =>
        string.Equals(first.Kind, second.Kind, StringComparison.Ordinal) &&
        string.Equals(first.Name, second.Name, StringComparison.Ordinal) &&
        first.FireAtUtc == second.FireAtUtc;

    private static string NormalizeWaitKind(string? kind) => kind switch
    {
        AzureDurableWaitKinds.ExternalEvent => AzureDurableWaitKinds.ExternalEvent,
        AzureDurableWaitKinds.Timer => AzureDurableWaitKinds.Timer,
        _ => throw new InvalidOperationException($"Durable wait kind '{kind ?? "(null)"}' is not supported.")
    };

    private static string WaitOutcomeId(string runId, string kind, string name) => $"{runId}\n{kind}\n{name}";

    private static string RequireId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
        return value.Trim();
    }
}
