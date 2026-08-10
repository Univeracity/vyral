using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Vyral.Abstractions.Interfaces;

namespace Vyral.Server;

public sealed class RetrievalEvaluationJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ConcurrentDictionary<string, RetrievalEvaluationJobState> _jobs = new(StringComparer.Ordinal);

    public RetrievalEvaluationJobStore(RetrievalEvaluationJobStoreOptions? options = null)
    {
        Options = options ?? new RetrievalEvaluationJobStoreOptions();
    }

    public RetrievalEvaluationJobStoreOptions Options { get; }

    public RetrievalEvaluationJob StartEvaluation(
        RetrievalEvaluationRequest request,
        IRetrievalEvaluationService evaluationService)
    {
        var job = new RetrievalEvaluationJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = RetrievalEvaluationJobKinds.Evaluation,
            RequestHash = Sha256(JsonSerializer.Serialize(request, JsonOptions)),
            CreatedAt = DateTime.UtcNow,
            Requested = request.Cases.Count,
            Progress = 0
        };

        var state = new RetrievalEvaluationJobState(this, job);
        _jobs[job.Id] = state;
        if (ActiveJobCount() > Options.MaxActiveJobs)
        {
            state.Reject("job_queue_full", $"Retrieval evaluation job queue is full. Max active jobs: {Options.MaxActiveJobs}.");
            PruneTerminalJobs();
            return state.Snapshot();
        }

        var initial = state.Snapshot();
        _ = Task.Run(() => RunEvaluationAsync(state, request, evaluationService));
        return initial;
    }

    public RetrievalEvaluationJob StartComparison(
        RetrievalEvaluationComparisonRequest request,
        IRetrievalEvaluationService evaluationService)
    {
        var job = new RetrievalEvaluationJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = RetrievalEvaluationJobKinds.Comparison,
            RequestHash = Sha256(JsonSerializer.Serialize(request, JsonOptions)),
            CreatedAt = DateTime.UtcNow,
            Requested = request.Cases.Count,
            VariantsRequested = request.Variants.Count,
            Progress = 0
        };

        var state = new RetrievalEvaluationJobState(this, job);
        _jobs[job.Id] = state;
        if (ActiveJobCount() > Options.MaxActiveJobs)
        {
            state.Reject("job_queue_full", $"Retrieval evaluation job queue is full. Max active jobs: {Options.MaxActiveJobs}.");
            PruneTerminalJobs();
            return state.Snapshot();
        }

        var initial = state.Snapshot();
        _ = Task.Run(() => RunComparisonAsync(state, request, evaluationService));
        return initial;
    }

    public RetrievalEvaluationJob? Get(string id)
    {
        return _jobs.TryGetValue(id, out var state) ? state.Snapshot() : null;
    }

    public IReadOnlyList<RetrievalEvaluationJob> List(int? limit = null, bool includeResult = false)
    {
        var effectiveLimit = ValidateListLimit(limit);
        return _jobs.Values
            .Select(state => state.Snapshot(includeResult))
            .OrderByDescending(job => job.CreatedAt)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .ToList();
    }

    public RetrievalEvaluationJob? Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var state))
        {
            return null;
        }

        state.RequestCancel();
        return state.Snapshot();
    }

    private static async Task RunEvaluationAsync(
        RetrievalEvaluationJobState state,
        RetrievalEvaluationRequest request,
        IRetrievalEvaluationService evaluationService)
    {
        state.MarkRunning();
        try
        {
            state.ThrowIfCancellationRequested();
            var progress = new InlineProgress<RetrievalEvaluationProgress>(state.ApplyProgress);
            var result = await evaluationService.EvaluateAsync(request, state.CancellationToken, progress);
            if (state.CancellationRequested)
            {
                state.Cancel("cancelled", result);
            }
            else
            {
                state.Succeed(result);
            }
        }
        catch (OperationCanceledException) when (state.CancellationRequested)
        {
            state.Cancel("cancelled");
        }
        catch (Exception ex)
        {
            state.Fail("unknown", ex.Message);
        }

        state.Owner.PruneTerminalJobs();
    }

    private static async Task RunComparisonAsync(
        RetrievalEvaluationJobState state,
        RetrievalEvaluationComparisonRequest request,
        IRetrievalEvaluationService evaluationService)
    {
        state.MarkRunning();
        try
        {
            state.ThrowIfCancellationRequested();
            var progress = new InlineProgress<RetrievalEvaluationComparisonProgress>(state.ApplyProgress);
            var result = await evaluationService.CompareAsync(request, state.CancellationToken, progress);
            if (state.CancellationRequested)
            {
                state.Cancel("cancelled", result);
            }
            else
            {
                state.Succeed(result);
            }
        }
        catch (OperationCanceledException) when (state.CancellationRequested)
        {
            state.Cancel("cancelled");
        }
        catch (Exception ex)
        {
            state.Fail("unknown", ex.Message);
        }

        state.Owner.PruneTerminalJobs();
    }

    private int ActiveJobCount()
    {
        return _jobs.Values.Count(state => !RetrievalEvaluationJobState.IsTerminal(state.Snapshot().Status));
    }

    private int ValidateListLimit(int? limit)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new InvalidOperationException("Retrieval evaluation job list limit must be greater than zero.");
        }

        var effectiveLimit = limit ?? Options.DefaultListLimit;
        if (effectiveLimit > Options.MaxListLimit)
        {
            throw new InvalidOperationException($"Retrieval evaluation job list limit cannot exceed {Options.MaxListLimit}.");
        }

        return effectiveLimit;
    }

    private void PruneTerminalJobs()
    {
        var terminalJobs = _jobs.Values
            .Select(state => state.Snapshot(includeResult: false))
            .Where(job => RetrievalEvaluationJobState.IsTerminal(job.Status))
            .OrderByDescending(job => job.CreatedAt)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var job in terminalJobs.Skip(Options.MaxRetainedTerminalJobs))
        {
            _jobs.TryRemove(job.Id, out _);
        }
    }

    private static string Sha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private sealed class RetrievalEvaluationJobState
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly RetrievalEvaluationJob _job;

        public RetrievalEvaluationJobState(RetrievalEvaluationJobStore owner, RetrievalEvaluationJob job)
        {
            Owner = owner;
            _job = job;
        }

        public RetrievalEvaluationJobStore Owner { get; }
        public CancellationToken CancellationToken => _cancellation.Token;
        public bool CancellationRequested => _job.CancellationRequested;

        public RetrievalEvaluationJob Snapshot(bool includeResult = true)
        {
            lock (_sync)
            {
                return new RetrievalEvaluationJob
                {
                    Id = _job.Id,
                    Kind = _job.Kind,
                    Status = _job.Status,
                    RequestHash = _job.RequestHash,
                    CreatedAt = _job.CreatedAt,
                    StartedAt = _job.StartedAt,
                    CompletedAt = _job.CompletedAt,
                    DurationMs = _job.DurationMs,
                    CancellationRequested = _job.CancellationRequested,
                    Requested = _job.Requested,
                    CasesAttempted = _job.CasesAttempted,
                    CasesSucceeded = _job.CasesSucceeded,
                    CasesFailed = _job.CasesFailed,
                    CurrentCaseIndex = _job.CurrentCaseIndex,
                    CurrentCaseName = _job.CurrentCaseName,
                    VariantsRequested = _job.VariantsRequested,
                    VariantsAttempted = _job.VariantsAttempted,
                    VariantsSucceeded = _job.VariantsSucceeded,
                    VariantsFailed = _job.VariantsFailed,
                    CurrentVariantId = _job.CurrentVariantId,
                    CurrentVariantIndex = _job.CurrentVariantIndex,
                    Progress = _job.Progress,
                    FailureClass = _job.FailureClass,
                    Error = _job.Error,
                    EvaluationResult = includeResult ? CloneEvaluationResult(_job.EvaluationResult) : null,
                    Result = includeResult ? CloneResult(_job.Result) : null
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

                _job.Status = RetrievalEvaluationJobStatuses.Running;
                _job.StartedAt = DateTime.UtcNow;
            }
        }

        public void ApplyProgress(RetrievalEvaluationProgress progress)
        {
            lock (_sync)
            {
                if (IsTerminal(_job.Status))
                {
                    return;
                }

                _job.CurrentCaseIndex = progress.CurrentCaseIndex;
                _job.CurrentCaseName = progress.CurrentCaseName;
                _job.Requested = progress.Requested;
                _job.CasesAttempted = progress.CasesAttempted;
                _job.CasesSucceeded = progress.CasesSucceeded;
                _job.CasesFailed = progress.CasesFailed;
                _job.Progress = CalculateProgress(progress.CasesAttempted, progress.Requested);
                _job.EvaluationResult = CloneEvaluationResult(progress.Result);
            }
        }

        public void ApplyProgress(RetrievalEvaluationComparisonProgress progress)
        {
            lock (_sync)
            {
                if (IsTerminal(_job.Status))
                {
                    return;
                }

                _job.CurrentVariantId = progress.CurrentVariantId;
                _job.CurrentVariantIndex = progress.CurrentVariantIndex;
                _job.Requested = progress.Requested;
                _job.VariantsRequested = progress.VariantsRequested;
                _job.VariantsAttempted = progress.VariantsAttempted;
                _job.VariantsSucceeded = progress.VariantsSucceeded;
                _job.VariantsFailed = progress.VariantsFailed;
                _job.Progress = CalculateProgress(progress.VariantsAttempted, progress.VariantsRequested);
                _job.Result = CloneResult(progress.Result);
            }
        }

        public void Succeed(RetrievalEvaluationResult result)
        {
            Complete(RetrievalEvaluationJobStatuses.Succeeded, null, null, result, null);
        }

        public void Succeed(RetrievalEvaluationComparisonResult result)
        {
            Complete(RetrievalEvaluationJobStatuses.Succeeded, null, null, null, result);
        }

        public void Cancel(string failureClass)
        {
            if (string.Equals(_job.Kind, RetrievalEvaluationJobKinds.Evaluation, StringComparison.Ordinal))
            {
                Complete(RetrievalEvaluationJobStatuses.Cancelled, failureClass, "Retrieval evaluation job was cancelled.", _job.EvaluationResult, null);
                return;
            }

            Complete(RetrievalEvaluationJobStatuses.Cancelled, failureClass, "Retrieval evaluation comparison job was cancelled.", null, _job.Result);
        }

        public void Cancel(string failureClass, RetrievalEvaluationResult result)
        {
            Complete(RetrievalEvaluationJobStatuses.Cancelled, failureClass, "Retrieval evaluation job was cancelled.", result, null);
        }

        public void Cancel(string failureClass, RetrievalEvaluationComparisonResult result)
        {
            Complete(RetrievalEvaluationJobStatuses.Cancelled, failureClass, "Retrieval evaluation comparison job was cancelled.", null, result);
        }

        public void Fail(string failureClass, string error)
        {
            Complete(RetrievalEvaluationJobStatuses.Failed, failureClass, error, _job.EvaluationResult, _job.Result);
        }

        public void Reject(string failureClass, string error)
        {
            Complete(RetrievalEvaluationJobStatuses.Rejected, failureClass, error, null, null);
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

        public static bool IsTerminal(string status)
        {
            return status is not (RetrievalEvaluationJobStatuses.Queued or RetrievalEvaluationJobStatuses.Running);
        }

        private void Complete(
            string status,
            string? failureClass,
            string? error,
            RetrievalEvaluationResult? evaluationResult,
            RetrievalEvaluationComparisonResult? result)
        {
            lock (_sync)
            {
                var completedAt = DateTime.UtcNow;
                var startedAt = _job.StartedAt ?? completedAt;
                _job.Status = status;
                _job.StartedAt = startedAt;
                _job.CompletedAt = completedAt;
                _job.DurationMs = (completedAt - startedAt).TotalMilliseconds;
                _job.FailureClass = failureClass;
                _job.Error = error;
                var effectiveEvaluationResult = evaluationResult ?? _job.EvaluationResult;
                _job.EvaluationResult = CloneEvaluationResult(effectiveEvaluationResult);
                if (effectiveEvaluationResult is not null)
                {
                    _job.Requested = effectiveEvaluationResult.Requested;
                    _job.CasesAttempted = effectiveEvaluationResult.Attempted;
                    _job.CasesSucceeded = effectiveEvaluationResult.Succeeded;
                    _job.CasesFailed = effectiveEvaluationResult.Failed;
                }

                var effectiveResult = result ?? _job.Result;
                _job.Result = CloneResult(effectiveResult);
                if (effectiveResult is not null)
                {
                    _job.Requested = effectiveResult.Requested;
                    _job.VariantsRequested = effectiveResult.VariantsRequested;
                    _job.VariantsAttempted = effectiveResult.VariantsAttempted;
                    _job.VariantsSucceeded = effectiveResult.VariantsSucceeded;
                    _job.VariantsFailed = effectiveResult.VariantsFailed;
                }

                _job.CurrentCaseIndex = null;
                _job.CurrentCaseName = null;
                _job.CurrentVariantId = null;
                _job.CurrentVariantIndex = null;
                _job.Progress = status == RetrievalEvaluationJobStatuses.Succeeded
                    ? 1
                    : CalculateJobProgress();
            }
        }

        private double CalculateJobProgress()
        {
            return string.Equals(_job.Kind, RetrievalEvaluationJobKinds.Evaluation, StringComparison.Ordinal)
                ? CalculateProgress(_job.CasesAttempted, _job.Requested)
                : CalculateProgress(_job.VariantsAttempted, _job.VariantsRequested);
        }

        private static double CalculateProgress(int attempted, int requested)
        {
            if (requested <= 0)
            {
                return 0;
            }

            return Math.Clamp(attempted / (double)requested, 0, 1);
        }

        private static RetrievalEvaluationComparisonResult? CloneResult(RetrievalEvaluationComparisonResult? result)
        {
            if (result is null)
            {
                return null;
            }

            var json = JsonSerializer.Serialize(result, JsonOptions);
            return JsonSerializer.Deserialize<RetrievalEvaluationComparisonResult>(json, JsonOptions);
        }

        private static RetrievalEvaluationResult? CloneEvaluationResult(RetrievalEvaluationResult? result)
        {
            if (result is null)
            {
                return null;
            }

            var json = JsonSerializer.Serialize(result, JsonOptions);
            return JsonSerializer.Deserialize<RetrievalEvaluationResult>(json, JsonOptions);
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }
}

public sealed class RetrievalEvaluationJobStoreOptions
{
    public int MaxActiveJobs { get; init; } = 8;
    public int MaxRetainedTerminalJobs { get; init; } = 50;
    public int DefaultListLimit { get; init; } = 20;
    public int MaxListLimit { get; init; } = 100;

    public static RetrievalEvaluationJobStoreOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new RetrievalEvaluationJobStoreOptions
        {
            MaxActiveJobs = ParsePositiveInt(configuration["Retrieval:EvaluationJobs:MaxActiveJobs"], "Retrieval:EvaluationJobs:MaxActiveJobs") ?? 8,
            MaxRetainedTerminalJobs = ParsePositiveInt(configuration["Retrieval:EvaluationJobs:MaxRetainedTerminalJobs"], "Retrieval:EvaluationJobs:MaxRetainedTerminalJobs") ?? 50,
            DefaultListLimit = ParsePositiveInt(configuration["Retrieval:EvaluationJobs:DefaultListLimit"], "Retrieval:EvaluationJobs:DefaultListLimit") ?? 20,
            MaxListLimit = ParsePositiveInt(configuration["Retrieval:EvaluationJobs:MaxListLimit"], "Retrieval:EvaluationJobs:MaxListLimit") ?? 100
        };

        if (options.DefaultListLimit > options.MaxListLimit)
        {
            throw new InvalidOperationException("Retrieval:EvaluationJobs:DefaultListLimit cannot exceed Retrieval:EvaluationJobs:MaxListLimit.");
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
