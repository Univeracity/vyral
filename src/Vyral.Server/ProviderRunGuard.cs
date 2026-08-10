using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Vyral.Providers.Abstractions;

namespace Vyral.Server;

public sealed class ProviderRunGuard
{
    private readonly ConcurrentDictionary<string, ProviderRunLimiter> _limiters = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRunGuard(ProviderRunGuardOptions options)
    {
        Options = options;
    }

    public ProviderRunGuardOptions Options { get; }

    public async Task<ProviderRunAdmission> TryEnterAsync(string provider, ProviderRunRequest request, CancellationToken ct)
    {
        var timeoutStatus = ValidateTimeout(request);
        if (timeoutStatus is not null)
        {
            return ProviderRunAdmission.Rejected(CreateResult(provider, request, ProviderRunStatus.Rejected, ProviderFailureClasses.Policy, timeoutStatus));
        }

        var outputStatus = ValidateOutputLimit(request);
        if (outputStatus is not null)
        {
            return ProviderRunAdmission.Rejected(CreateResult(provider, request, ProviderRunStatus.Rejected, ProviderFailureClasses.Policy, outputStatus));
        }

        request.TimeoutSeconds ??= Options.DefaultTimeoutSeconds;
        request.MaxOutputBytes ??= Options.MaxOutputBytes;

        var limiter = _limiters.GetOrAdd(provider, _ => new ProviderRunLimiter(Options));
        if (!await limiter.TryEnterAsync(ct))
        {
            return ProviderRunAdmission.Rejected(CreateResult(provider, request, ProviderRunStatus.Rejected, ProviderFailureClasses.RateLimit, "concurrency_queue_timeout"));
        }

        if (!limiter.TryRecord(DateTime.UtcNow))
        {
            limiter.Release();
            return ProviderRunAdmission.Rejected(CreateResult(provider, request, ProviderRunStatus.Rejected, ProviderFailureClasses.RateLimit, "rate_limited"));
        }

        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds.Value));
        return ProviderRunAdmission.Accept(limiter, timeoutCts);
    }

    public ProviderRunResult CreateTimeoutResult(string provider, ProviderRunRequest request)
    {
        return CreateResult(provider, request, ProviderRunStatus.TimedOut, ProviderFailureClasses.Timeout, "server_timeout");
    }

    public Dictionary<string, object?> ToOperationalLimits(bool authRequired)
    {
        return new Dictionary<string, object?>
        {
            ["authRequired"] = authRequired,
            ["maxConcurrentRuns"] = Options.MaxConcurrentRuns,
            ["queueTimeoutSeconds"] = Options.QueueTimeoutSeconds,
            ["maxRunsPerWindow"] = Options.MaxRunsPerWindow,
            ["rateLimitWindowSeconds"] = Options.RateLimitWindowSeconds,
            ["defaultTimeoutSeconds"] = Options.DefaultTimeoutSeconds,
            ["maxTimeoutSeconds"] = Options.MaxTimeoutSeconds,
            ["maxOutputBytes"] = Options.MaxOutputBytes,
            ["longJobPolicy"] = "sync_reject_timeout_above_max",
            ["asyncJobPolicy"] = "in_memory_cancelable"
        };
    }

    private string? ValidateTimeout(ProviderRunRequest request)
    {
        if (request.TimeoutSeconds.HasValue && request.TimeoutSeconds.Value <= 0)
        {
            return "invalid_timeout";
        }

        if (request.TimeoutSeconds.HasValue && request.TimeoutSeconds.Value > Options.MaxTimeoutSeconds)
        {
            return "timeout_exceeds_policy";
        }

        return null;
    }

    private string? ValidateOutputLimit(ProviderRunRequest request)
    {
        if (request.MaxOutputBytes.HasValue && request.MaxOutputBytes.Value <= 0)
        {
            return "invalid_output_limit";
        }

        if (request.MaxOutputBytes.HasValue && request.MaxOutputBytes.Value > Options.MaxOutputBytes)
        {
            return "output_limit_exceeds_policy";
        }

        return null;
    }

    private static ProviderRunResult CreateResult(string provider, ProviderRunRequest request, ProviderRunStatus status, string failureClass, string providerStatus)
    {
        var trace = new ProviderTraceEvent
        {
            Provider = provider,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            AdapterId = "server-guardrail",
            ModelId = request.ModelId,
            InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)),
            FailureClass = failureClass,
            DurationMs = 0
        };

        return new ProviderRunResult
        {
            Status = status,
            Provider = provider,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            FailureClass = failureClass,
            ProviderStatus = providerStatus,
            Rejection = ProviderRunRejectionDiagnostics.Create(
                status,
                failureClass,
                providerStatus,
                request.Capability,
                decisionAuthority: ProviderRejectionDecisionAuthorities.ServerGuardrail,
                processOutcome: ProviderProcessOutcomes.NotStarted),
            Trace = trace
        };
    }
}

public sealed class ProviderRunGuardOptions
{
    public int MaxConcurrentRuns { get; init; } = 1;
    public int QueueTimeoutSeconds { get; init; } = 5;
    public int MaxRunsPerWindow { get; init; } = 60;
    public int RateLimitWindowSeconds { get; init; } = 60;
    public int DefaultTimeoutSeconds { get; init; } = 120;
    public int MaxTimeoutSeconds { get; init; } = 300;
    public int MaxOutputBytes { get; init; } = 128 * 1024;

    public static ProviderRunGuardOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new ProviderRunGuardOptions
        {
            MaxConcurrentRuns = ParsePositiveInt(configuration["Providers:MaxConcurrentRuns"], "Providers:MaxConcurrentRuns") ?? 1,
            QueueTimeoutSeconds = ParsePositiveInt(configuration["Providers:QueueTimeoutSeconds"], "Providers:QueueTimeoutSeconds") ?? 5,
            MaxRunsPerWindow = ParsePositiveInt(configuration["Providers:MaxRunsPerWindow"], "Providers:MaxRunsPerWindow") ?? 60,
            RateLimitWindowSeconds = ParsePositiveInt(configuration["Providers:RateLimitWindowSeconds"], "Providers:RateLimitWindowSeconds") ?? 60,
            DefaultTimeoutSeconds = ParsePositiveInt(configuration["Providers:DefaultRunTimeoutSeconds"], "Providers:DefaultRunTimeoutSeconds") ?? 120,
            MaxTimeoutSeconds = ParsePositiveInt(configuration["Providers:MaxRunTimeoutSeconds"], "Providers:MaxRunTimeoutSeconds") ?? 300,
            MaxOutputBytes = ParsePositiveInt(configuration["Providers:MaxOutputBytes"], "Providers:MaxOutputBytes") ?? 128 * 1024
        };

        if (options.DefaultTimeoutSeconds > options.MaxTimeoutSeconds)
        {
            throw new InvalidOperationException("Providers:DefaultRunTimeoutSeconds cannot exceed Providers:MaxRunTimeoutSeconds.");
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

public sealed class ProviderRunAdmission : IAsyncDisposable
{
    private readonly ProviderRunLimiter? _limiter;
    private readonly CancellationTokenSource? _timeoutCts;

    private ProviderRunAdmission(bool accepted, ProviderRunResult? rejectionResult, ProviderRunLimiter? limiter, CancellationTokenSource? timeoutCts)
    {
        Accepted = accepted;
        RejectionResult = rejectionResult;
        _limiter = limiter;
        _timeoutCts = timeoutCts;
    }

    public bool Accepted { get; }
    public ProviderRunResult? RejectionResult { get; }
    public CancellationToken CancellationToken => _timeoutCts?.Token ?? CancellationToken.None;
    public bool TimedOut => _timeoutCts?.IsCancellationRequested == true;

    public static ProviderRunAdmission Accept(ProviderRunLimiter limiter, CancellationTokenSource timeoutCts)
    {
        return new ProviderRunAdmission(true, null, limiter, timeoutCts);
    }

    public static ProviderRunAdmission Rejected(ProviderRunResult result)
    {
        return new ProviderRunAdmission(false, result, null, null);
    }

    public ValueTask DisposeAsync()
    {
        _timeoutCts?.Dispose();
        _limiter?.Release();
        return ValueTask.CompletedTask;
    }
}

public sealed class ProviderRunLimiter
{
    private readonly ProviderRunGuardOptions _options;
    private readonly SemaphoreSlim _semaphore;
    private readonly Queue<DateTime> _acceptedRuns = new();
    private readonly object _lock = new();

    public ProviderRunLimiter(ProviderRunGuardOptions options)
    {
        _options = options;
        _semaphore = new SemaphoreSlim(options.MaxConcurrentRuns, options.MaxConcurrentRuns);
    }

    public bool TryRecord(DateTime now)
    {
        lock (_lock)
        {
            var cutoff = now - TimeSpan.FromSeconds(_options.RateLimitWindowSeconds);
            while (_acceptedRuns.Count > 0 && _acceptedRuns.Peek() < cutoff)
            {
                _acceptedRuns.Dequeue();
            }

            if (_acceptedRuns.Count >= _options.MaxRunsPerWindow)
            {
                return false;
            }

            _acceptedRuns.Enqueue(now);
            return true;
        }
    }

    public Task<bool> TryEnterAsync(CancellationToken ct)
    {
        return _semaphore.WaitAsync(TimeSpan.FromSeconds(_options.QueueTimeoutSeconds), ct);
    }

    public void Release()
    {
        _semaphore.Release();
    }
}
