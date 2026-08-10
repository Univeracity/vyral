using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Server;

public sealed class EmbeddingJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ConcurrentDictionary<string, EmbeddingJobState> _jobs = new(StringComparer.Ordinal);

    public EmbeddingJobStore(EmbeddingJobStoreOptions? options = null)
    {
        Options = options ?? new EmbeddingJobStoreOptions();
    }

    public EmbeddingJobStoreOptions Options { get; }

    public EmbeddingJob Start(
        EmbeddingRequest request,
        IEmbeddingProvider embeddingProvider,
        EmbeddingProviderOptions embeddingOptions)
    {
        var texts = GetEmbeddingTexts(request);
        var purpose = EmbeddingTextPreparer.NormalizePurpose(request.Purpose);
        var job = new EmbeddingJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = EmbeddingJobStatuses.Queued,
            Provider = embeddingProvider.ProviderId,
            ModelId = embeddingProvider.ModelId,
            Dimensions = embeddingProvider.Dimensions,
            Purpose = purpose,
            RequestHash = Sha256(JsonSerializer.Serialize(request, JsonOptions)),
            CreatedAt = DateTime.UtcNow,
            Requested = texts.Count,
            Progress = 0
        };

        var state = new EmbeddingJobState(this, job);
        _jobs[job.Id] = state;
        if (ActiveJobCount() > Options.MaxActiveJobs)
        {
            state.Reject("job_queue_full", $"Embedding job queue is full. Max active jobs: {Options.MaxActiveJobs}.");
            PruneTerminalJobs();
            return state.Snapshot();
        }

        var initial = state.Snapshot();
        _ = Task.Run(() => RunAsync(state, request, texts, purpose, embeddingProvider, embeddingOptions));
        return initial;
    }

    public EmbeddingJob? Get(string id)
    {
        return _jobs.TryGetValue(id, out var state) ? state.Snapshot() : null;
    }

    public IReadOnlyList<EmbeddingJob> List(int? limit = null, bool includeResult = false)
    {
        var effectiveLimit = ValidateListLimit(limit);
        return _jobs.Values
            .Select(state => state.Snapshot(includeResult))
            .OrderByDescending(job => job.CreatedAt)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .ToList();
    }

    public EmbeddingJob? Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var state))
        {
            return null;
        }

        state.RequestCancel();
        return state.Snapshot();
    }

    private static async Task RunAsync(
        EmbeddingJobState state,
        EmbeddingRequest request,
        IReadOnlyList<string> texts,
        string purpose,
        IEmbeddingProvider embeddingProvider,
        EmbeddingProviderOptions embeddingOptions)
    {
        state.MarkRunning();
        var response = new EmbeddingResponse
        {
            Provider = embeddingProvider.ProviderId,
            ModelId = embeddingProvider.ModelId,
            Dimensions = embeddingProvider.Dimensions,
            Purpose = purpose
        };

        try
        {
            for (var i = 0; i < texts.Count; i++)
            {
                state.ThrowIfCancellationRequested();
                state.MarkCurrent(i);
                var item = await GenerateEmbeddingResultAsync(
                    request,
                    texts[i],
                    i,
                    purpose,
                    embeddingProvider,
                    embeddingOptions,
                    state.CancellationToken);
                response.Items.Add(item);
                state.RecordItem(response);
            }

            state.Succeed(response);
        }
        catch (OperationCanceledException) when (state.CancellationRequested)
        {
            state.Cancel("cancelled", response);
        }
        catch (Exception ex)
        {
            state.Fail("unknown", ex.Message, response);
        }

        state.Owner.PruneTerminalJobs();
    }

    private int ActiveJobCount()
    {
        return _jobs.Values.Count(state => !EmbeddingJobState.IsTerminal(state.Snapshot().Status));
    }

    private int ValidateListLimit(int? limit)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new InvalidOperationException("Embedding job list limit must be greater than zero.");
        }

        var effectiveLimit = limit ?? Options.DefaultListLimit;
        if (effectiveLimit > Options.MaxListLimit)
        {
            throw new InvalidOperationException($"Embedding job list limit cannot exceed {Options.MaxListLimit}.");
        }

        return effectiveLimit;
    }

    private void PruneTerminalJobs()
    {
        var terminalJobs = _jobs.Values
            .Select(state => state.Snapshot(includeResult: false))
            .Where(job => EmbeddingJobState.IsTerminal(job.Status))
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

    private static IReadOnlyList<string> GetEmbeddingTexts(EmbeddingRequest request)
    {
        const int maxBatchSize = 128;
        const int maxTextLength = 100_000;
        var texts = new List<string>();

        if (request.Text is not null)
        {
            texts.Add(request.Text);
        }

        if (request.Texts is not null)
        {
            texts.AddRange(request.Texts);
        }

        if (texts.Count == 0)
        {
            throw new InvalidOperationException("Embedding request must include text or texts.");
        }

        if (texts.Count > maxBatchSize)
        {
            throw new InvalidOperationException($"Embedding request supports at most {maxBatchSize} texts.");
        }

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Embedding request text values cannot be empty.");
            }

            if (text.Length > maxTextLength)
            {
                throw new InvalidOperationException($"Embedding request text values cannot exceed {maxTextLength} characters.");
            }
        }

        return texts;
    }

    private static async Task<EmbeddingResult> GenerateEmbeddingResultAsync(
        EmbeddingRequest request,
        string text,
        int index,
        string purpose,
        IEmbeddingProvider embeddingProvider,
        EmbeddingProviderOptions embeddingOptions,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var prepared = EmbeddingTextPreparer.Prepare(
            text,
            purpose,
            request.QueryPrefix ?? embeddingOptions.QueryPrefix,
            request.PassagePrefix ?? embeddingOptions.PassagePrefix,
            request.SymmetricPrefix ?? embeddingOptions.SymmetricPrefix);
        var values = await embeddingProvider.GenerateEmbeddingAsync(prepared.PreparedText, ct);
        ct.ThrowIfCancellationRequested();
        return new EmbeddingResult
        {
            Index = index,
            TextLength = text.Length,
            PreparedTextLength = prepared.PreparedText.Length,
            PrefixApplied = prepared.PrefixApplied,
            PrefixLength = prepared.PrefixLength,
            Values = values
        };
    }

    private sealed class EmbeddingJobState
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly EmbeddingJob _job;

        public EmbeddingJobState(EmbeddingJobStore owner, EmbeddingJob job)
        {
            Owner = owner;
            _job = job;
        }

        public EmbeddingJobStore Owner { get; }
        public CancellationToken CancellationToken => _cancellation.Token;
        public bool CancellationRequested => _job.CancellationRequested;

        public EmbeddingJob Snapshot(bool includeResult = true)
        {
            lock (_sync)
            {
                return new EmbeddingJob
                {
                    Id = _job.Id,
                    Status = _job.Status,
                    Provider = _job.Provider,
                    ModelId = _job.ModelId,
                    Dimensions = _job.Dimensions,
                    Purpose = _job.Purpose,
                    RequestHash = _job.RequestHash,
                    CreatedAt = _job.CreatedAt,
                    StartedAt = _job.StartedAt,
                    CompletedAt = _job.CompletedAt,
                    DurationMs = _job.DurationMs,
                    CancellationRequested = _job.CancellationRequested,
                    Requested = _job.Requested,
                    Attempted = _job.Attempted,
                    Succeeded = _job.Succeeded,
                    Failed = _job.Failed,
                    CurrentIndex = _job.CurrentIndex,
                    Progress = _job.Progress,
                    FailureClass = _job.FailureClass,
                    Error = _job.Error,
                    Result = includeResult ? CloneResponse(_job.Result) : null
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

                _job.Status = EmbeddingJobStatuses.Running;
                _job.StartedAt = DateTime.UtcNow;
            }
        }

        public void MarkCurrent(int index)
        {
            lock (_sync)
            {
                if (!IsTerminal(_job.Status))
                {
                    _job.CurrentIndex = index;
                }
            }
        }

        public void RecordItem(EmbeddingResponse response)
        {
            lock (_sync)
            {
                _job.Attempted = response.Items.Count;
                _job.Succeeded = response.Items.Count;
                _job.Progress = CalculateProgress(_job.Attempted, _job.Requested);
                _job.Result = CloneResponse(response);
            }
        }

        public void Succeed(EmbeddingResponse response)
        {
            Complete(EmbeddingJobStatuses.Succeeded, null, null, response);
        }

        public void Cancel(string failureClass, EmbeddingResponse response)
        {
            Complete(EmbeddingJobStatuses.Cancelled, failureClass, "Embedding job was cancelled.", response);
        }

        public void Fail(string failureClass, string error, EmbeddingResponse response)
        {
            Complete(EmbeddingJobStatuses.Failed, failureClass, error, response);
        }

        public void Reject(string failureClass, string error)
        {
            Complete(EmbeddingJobStatuses.Rejected, failureClass, error, null);
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
            return status is not (EmbeddingJobStatuses.Queued or EmbeddingJobStatuses.Running);
        }

        private void Complete(string status, string? failureClass, string? error, EmbeddingResponse? response)
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
                _job.Result = CloneResponse(response ?? _job.Result);
                _job.Attempted = _job.Result?.Items.Count ?? _job.Attempted;
                _job.Succeeded = status == EmbeddingJobStatuses.Failed ? _job.Succeeded : _job.Attempted;
                _job.Failed = status == EmbeddingJobStatuses.Failed ? Math.Max(1, _job.Requested - _job.Succeeded) : 0;
                _job.CurrentIndex = null;
                _job.Progress = status == EmbeddingJobStatuses.Succeeded
                    ? 1
                    : CalculateProgress(_job.Attempted, _job.Requested);
            }
        }

        private static double CalculateProgress(int attempted, int requested)
        {
            if (requested <= 0)
            {
                return 0;
            }

            return Math.Clamp(attempted / (double)requested, 0, 1);
        }

        private static EmbeddingResponse? CloneResponse(EmbeddingResponse? response)
        {
            if (response is null)
            {
                return null;
            }

            var json = JsonSerializer.Serialize(response, JsonOptions);
            return JsonSerializer.Deserialize<EmbeddingResponse>(json, JsonOptions);
        }
    }
}

public sealed class EmbeddingJobStoreOptions
{
    public int MaxActiveJobs { get; init; } = 4;
    public int MaxRetainedTerminalJobs { get; init; } = 50;
    public int DefaultListLimit { get; init; } = 20;
    public int MaxListLimit { get; init; } = 100;

    public static EmbeddingJobStoreOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new EmbeddingJobStoreOptions
        {
            MaxActiveJobs = ParsePositiveInt(configuration["Embeddings:Jobs:MaxActiveJobs"], "Embeddings:Jobs:MaxActiveJobs") ?? 4,
            MaxRetainedTerminalJobs = ParsePositiveInt(configuration["Embeddings:Jobs:MaxRetainedTerminalJobs"], "Embeddings:Jobs:MaxRetainedTerminalJobs") ?? 50,
            DefaultListLimit = ParsePositiveInt(configuration["Embeddings:Jobs:DefaultListLimit"], "Embeddings:Jobs:DefaultListLimit") ?? 20,
            MaxListLimit = ParsePositiveInt(configuration["Embeddings:Jobs:MaxListLimit"], "Embeddings:Jobs:MaxListLimit") ?? 100
        };

        if (options.DefaultListLimit > options.MaxListLimit)
        {
            throw new InvalidOperationException("Embeddings:Jobs:DefaultListLimit cannot exceed Embeddings:Jobs:MaxListLimit.");
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
