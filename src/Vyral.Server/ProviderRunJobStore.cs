using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Vyral.Providers.Abstractions;

namespace Vyral.Server;

public sealed class ProviderRunJobStore
{
    private readonly ConcurrentDictionary<string, ProviderRunJobState> _jobs = new(StringComparer.Ordinal);
    private readonly IProviderRunJobPersistence? _persistence;

    public ProviderRunJobStore(ProviderRunJobStoreOptions? options = null, IProviderRunJobPersistence? persistence = null)
    {
        Options = options ?? new ProviderRunJobStoreOptions();
        _persistence = persistence;
        LoadPersistedJobs();
    }

    public ProviderRunJobStoreOptions Options { get; }
    public string PersistenceKind => _persistence?.Kind ?? "memory";

    public ProviderRunJob Start(
        string provider,
        ProviderRunRequest request,
        Func<ProviderRunJob, CancellationToken, Task<ProviderRunResult>> executeAsync,
        Func<ProviderRunJob, ProviderRunResult, Task>? onCompletedAsync = null)
    {
        var job = new ProviderRunJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Provider = provider,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? Guid.NewGuid().ToString("N") : request.CorrelationId,
            RequestHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)),
            CreatedAt = DateTime.UtcNow
        };

        request.CorrelationId = job.CorrelationId;
        var state = new ProviderRunJobState(this, job);
        _jobs[job.Id] = state;
        PersistSnapshot(state.Snapshot());
        if (ActiveJobCount() > Options.MaxActiveJobs)
        {
            var rejection = CreateTerminalResult(job, request, ProviderRunStatus.Rejected, ProviderFailureClasses.RateLimit, "job_queue_full");
            var completed = state.BuildCompletedSnapshot(rejection);
            PersistSnapshot(completed);
            state.ApplySnapshot(completed);
            _ = PersistCompletionAsync(state, rejection, onCompletedAsync);
            PruneTerminalJobs();
            return state.Snapshot();
        }

        var initial = state.Snapshot();

        _ = Task.Run(() => RunAsync(state, request, executeAsync, onCompletedAsync));
        return initial;
    }

    public ProviderRunJob? Get(string id)
    {
        return _jobs.TryGetValue(id, out var state) ? state.Snapshot() : null;
    }

    public IReadOnlyList<ProviderRunJob> List(string? provider = null, int? limit = null, bool includeResult = false)
    {
        var effectiveLimit = ValidateListLimit(limit);
        return _jobs.Values
            .Select(state => state.Snapshot(includeResult))
            .Where(job => string.IsNullOrWhiteSpace(provider) || string.Equals(job.Provider, provider, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(job => job.CreatedAt)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .ToList();
    }

    public ProviderRunJob? Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var state))
        {
            return null;
        }

        state.RequestCancel();
        PersistSnapshot(state.Snapshot());
        return state.Snapshot();
    }

    private static async Task RunAsync(
        ProviderRunJobState state,
        ProviderRunRequest request,
        Func<ProviderRunJob, CancellationToken, Task<ProviderRunResult>> executeAsync,
        Func<ProviderRunJob, ProviderRunResult, Task>? onCompletedAsync)
    {
        state.MarkRunning();
        state.Owner.PersistSnapshot(state.Snapshot());
        ProviderRunResult result;
        try
        {
            state.ThrowIfCancellationRequested();
            result = await executeAsync(state.Snapshot(), state.CancellationToken);
            if (state.CancellationRequested && result.Status != ProviderRunStatus.TimedOut)
            {
                result = CreateTerminalResult(state.Snapshot(), request, ProviderRunStatus.Cancelled, ProviderFailureClasses.Cancelled, "cancelled");
            }
        }
        catch (OperationCanceledException) when (state.CancellationRequested)
        {
            result = CreateTerminalResult(state.Snapshot(), request, ProviderRunStatus.Cancelled, ProviderFailureClasses.Cancelled, "cancelled");
        }
        catch (Exception ex)
        {
            result = CreateTerminalResult(state.Snapshot(), request, ProviderRunStatus.Failed, ProviderFailureClasses.Unknown, "job_unhandled_exception", ex.Message);
        }

        result = await PersistCompletionAsync(state, result, onCompletedAsync);
        var completed = state.BuildCompletedSnapshot(result);
        state.Owner.PersistSnapshot(completed);
        state.ApplySnapshot(completed);
        state.Owner.PruneTerminalJobs();
    }

    private static async Task<ProviderRunResult> PersistCompletionAsync(
        ProviderRunJobState state,
        ProviderRunResult result,
        Func<ProviderRunJob, ProviderRunResult, Task>? onCompletedAsync)
    {
        if (onCompletedAsync is null)
        {
            return result;
        }

        try
        {
            await onCompletedAsync(state.Snapshot(), result);
            return result;
        }
        catch (Exception ex)
        {
            return CreateTerminalResult(state.Snapshot(), new ProviderRunRequest
            {
                Capability = state.Snapshot().Capability,
                Operation = state.Snapshot().Operation,
                Mode = state.Snapshot().Mode
            }, ProviderRunStatus.Failed, ProviderFailureClasses.Unknown, "job_trace_persist_failed", ex.Message);
        }
    }

    private static ProviderRunResult CreateTerminalResult(
        ProviderRunJob job,
        ProviderRunRequest request,
        ProviderRunStatus status,
        string failureClass,
        string providerStatus,
        string? textOutput = null)
    {
        var now = DateTime.UtcNow;
        var trace = new ProviderTraceEvent
        {
            TraceId = Guid.NewGuid().ToString("N"),
            Timestamp = now,
            Provider = job.Provider,
            Capability = job.Capability,
            Operation = job.Operation,
            Mode = job.Mode,
            AdapterId = "server-provider-job",
            ModelId = request.ModelId,
            InputHash = job.RequestHash,
            FailureClass = failureClass,
            DurationMs = job.StartedAt.HasValue ? (now - job.StartedAt.Value).TotalMilliseconds : 0
        };

        var obj = new JsonObject();
        if (textOutput != null) obj["text"] = textOutput;
        return new ProviderRunResult
        {
            Status = status,
            Provider = job.Provider,
            Capability = job.Capability,
            Operation = job.Operation,
            Mode = job.Mode,
            FailureClass = failureClass,
            ProviderStatus = providerStatus,
            Trace = trace,
            Output = obj
        };
    }

    private int ActiveJobCount()
    {
        return _jobs.Values.Count(state => !ProviderRunJobState.IsTerminal(state.Snapshot().Status));
    }

    private int ValidateListLimit(int? limit)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new InvalidOperationException("Provider job list limit must be greater than zero.");
        }

        var effectiveLimit = limit ?? Options.DefaultListLimit;
        if (effectiveLimit > Options.MaxListLimit)
        {
            throw new InvalidOperationException($"Provider job list limit cannot exceed {Options.MaxListLimit}.");
        }

        return effectiveLimit;
    }

    private void PruneTerminalJobs()
    {
        var terminalJobs = _jobs.Values
            .Select(state => state.Snapshot())
            .Where(job => ProviderRunJobState.IsTerminal(job.Status))
            .OrderByDescending(job => job.CreatedAt)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var job in terminalJobs.Skip(Options.MaxRetainedTerminalJobs))
        {
            _jobs.TryRemove(job.Id, out _);
            _persistence?.Delete(job.Id);
        }

        _persistence?.PruneTerminal(Options.MaxRetainedTerminalJobs);
    }

    private void LoadPersistedJobs()
    {
        if (_persistence is null)
        {
            return;
        }

        foreach (var job in _persistence.LoadLatest(Options.MaxRetainedTerminalJobs))
        {
            var state = new ProviderRunJobState(this, job);
            if (!ProviderRunJobState.IsTerminal(job.Status))
            {
                var interrupted = CreateTerminalResult(
                    job,
                    new ProviderRunRequest
                    {
                        Capability = job.Capability,
                        Operation = job.Operation,
                        Mode = job.Mode,
                        CorrelationId = job.CorrelationId
                    },
                    ProviderRunStatus.Failed,
                    ProviderFailureClasses.Unknown,
                    "job_interrupted",
                    "Provider job was interrupted by server restart before reaching a terminal state.");
                var completed = state.BuildCompletedSnapshot(interrupted);
                PersistSnapshot(completed);
                state.ApplySnapshot(completed);
            }

            _jobs[job.Id] = state;
        }

        PruneTerminalJobs();
    }

    private void PersistSnapshot(ProviderRunJob job)
    {
        _persistence?.Upsert(job);
    }

    private sealed class ProviderRunJobState
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ProviderRunJobStore _owner;
        private readonly ProviderRunJob _job;

        public ProviderRunJobState(ProviderRunJobStore owner, ProviderRunJob job)
        {
            _owner = owner;
            _job = job;
        }

        public ProviderRunJobStore Owner => _owner;
        public CancellationToken CancellationToken => _cancellation.Token;
        public bool CancellationRequested => _job.CancellationRequested;

        public ProviderRunJob Snapshot(bool includeResult = true)
        {
            lock (_sync)
            {
                return new ProviderRunJob
                {
                    Id = _job.Id,
                    Status = _job.Status,
                    Provider = _job.Provider,
                    Capability = _job.Capability,
                    Operation = _job.Operation,
                    Mode = _job.Mode,
                    CorrelationId = _job.CorrelationId,
                    RequestHash = _job.RequestHash,
                    CreatedAt = _job.CreatedAt,
                    StartedAt = _job.StartedAt,
                    CompletedAt = _job.CompletedAt,
                    DurationMs = _job.DurationMs,
                    CancellationRequested = _job.CancellationRequested,
                    TraceId = _job.TraceId,
                    FailureClass = _job.FailureClass,
                    ProviderStatus = _job.ProviderStatus,
                    Result = includeResult ? _job.Result : null
                };
            }
        }

        public void MarkRunning()
        {
            lock (_sync)
            {
                if (_job.CancellationRequested)
                {
                    return;
                }

                _job.Status = ProviderJobStatus.Running;
                _job.StartedAt = DateTime.UtcNow;
            }
        }

        public ProviderRunJob BuildCompletedSnapshot(ProviderRunResult result)
        {
            lock (_sync)
            {
                var completedAt = DateTime.UtcNow;
                var startedAt = _job.StartedAt ?? completedAt;
                return new ProviderRunJob
                {
                    Id = _job.Id,
                    Status = ToJobStatus(result.Status),
                    Provider = _job.Provider,
                    Capability = _job.Capability,
                    Operation = _job.Operation,
                    Mode = _job.Mode,
                    CorrelationId = _job.CorrelationId,
                    RequestHash = _job.RequestHash,
                    CreatedAt = _job.CreatedAt,
                    StartedAt = startedAt,
                    CompletedAt = completedAt,
                    DurationMs = (completedAt - startedAt).TotalMilliseconds,
                    CancellationRequested = _job.CancellationRequested,
                    TraceId = result.Trace?.TraceId,
                    FailureClass = result.FailureClass,
                    ProviderStatus = result.ProviderStatus,
                    Result = result
                };
            }
        }

        public void ApplySnapshot(ProviderRunJob job)
        {
            lock (_sync)
            {
                _job.Status = job.Status;
                _job.StartedAt = job.StartedAt;
                _job.CompletedAt = job.CompletedAt;
                _job.DurationMs = job.DurationMs;
                _job.CancellationRequested = job.CancellationRequested;
                _job.TraceId = job.TraceId;
                _job.FailureClass = job.FailureClass;
                _job.ProviderStatus = job.ProviderStatus;
                _job.Result = job.Result;
            }
        }

        public void RequestCancel()
        {
            lock (_sync)
            {
                _job.CancellationRequested = true;
                if (IsTerminal(_job.Status))
                {
                    return;
                }
            }

            _cancellation.Cancel();
        }

        public void ThrowIfCancellationRequested()
        {
            _cancellation.Token.ThrowIfCancellationRequested();
        }

        public static bool IsTerminal(ProviderJobStatus status)
        {
            return status is not (ProviderJobStatus.Queued or ProviderJobStatus.Running);
        }

        private static ProviderJobStatus ToJobStatus(ProviderRunStatus status)
        {
            return status switch
            {
                ProviderRunStatus.Succeeded => ProviderJobStatus.Succeeded,
                ProviderRunStatus.Failed => ProviderJobStatus.Failed,
                ProviderRunStatus.TimedOut => ProviderJobStatus.TimedOut,
                ProviderRunStatus.Rejected => ProviderJobStatus.Rejected,
                ProviderRunStatus.Unsupported => ProviderJobStatus.Unsupported,
                ProviderRunStatus.NotConfigured => ProviderJobStatus.NotConfigured,
                ProviderRunStatus.Cancelled => ProviderJobStatus.Cancelled,
                _ => ProviderJobStatus.Failed
            };
        }
    }

}

public sealed class ProviderRunJobStoreOptions
{
    public int MaxActiveJobs { get; init; } = 100;
    public int MaxRetainedTerminalJobs { get; init; } = 200;
    public int DefaultListLimit { get; init; } = 50;
    public int MaxListLimit { get; init; } = 200;

    public static ProviderRunJobStoreOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new ProviderRunJobStoreOptions
        {
            MaxActiveJobs = ParsePositiveInt(configuration["Providers:Jobs:MaxActiveJobs"], "Providers:Jobs:MaxActiveJobs") ?? 100,
            MaxRetainedTerminalJobs = ParsePositiveInt(configuration["Providers:Jobs:MaxRetainedTerminalJobs"], "Providers:Jobs:MaxRetainedTerminalJobs") ?? 200,
            DefaultListLimit = ParsePositiveInt(configuration["Providers:Jobs:DefaultListLimit"], "Providers:Jobs:DefaultListLimit") ?? 50,
            MaxListLimit = ParsePositiveInt(configuration["Providers:Jobs:MaxListLimit"], "Providers:Jobs:MaxListLimit") ?? 200
        };

        if (options.DefaultListLimit > options.MaxListLimit)
        {
            throw new InvalidOperationException("Providers:Jobs:DefaultListLimit cannot exceed Providers:Jobs:MaxListLimit.");
        }

        return options;
    }

    private static int? ParsePositiveInt(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new InvalidOperationException($"{key} must be a positive integer.");
        }

        return parsed;
    }
}
