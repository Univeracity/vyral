using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vyral.Execution;

namespace Vyral.Google;

/// <summary>
/// Cloud Tasks transport for a Cloud Run execution worker. The message deliberately contains only
/// a run id and reason; workers must obtain all mutable state by leasing the durable run.
/// </summary>
public sealed class GoogleCloudExecutionDispatcher : IExecutionRunDispatcher
{
    private readonly CloudTasksHttpJsonQueue _queue;

    public GoogleCloudExecutionDispatcher(CloudTasksHttpJsonQueue queue, GoogleCloudExecutionDispatchOptions options)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
    }

    public GoogleCloudExecutionDispatchOptions Options { get; }

    public async Task<CloudTasksHttpJsonEnqueueResult> DispatchAsync(
        string runId,
        string reason = GoogleCloudExecutionDispatchReasons.RunReady,
        DateTime? scheduleAtUtc = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId)) throw new InvalidOperationException("Execution run id is required.");
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Execution dispatch reason is required.");
        var normalizedRunId = runId.Trim();
        var normalizedReason = reason.Trim();
        var scheduled = scheduleAtUtc?.ToUniversalTime();
        var delay = scheduled.HasValue ? scheduled.Value - DateTime.UtcNow : (TimeSpan?)null;
        if (delay is { } value && value <= TimeSpan.Zero)
        {
            delay = null;
        }

        return await _queue.EnqueueAsync(new CloudTasksHttpJsonEnqueueRequest
        {
            ProjectId = Options.ProjectId,
            LocationId = Options.LocationId,
            QueueId = Options.QueueId,
            Url = Options.WorkerUrl,
            ServiceAccountEmail = Options.ServiceAccountEmail,
            OidcAudience = Options.OidcAudience,
            TaskId = BuildTaskId(normalizedRunId, normalizedReason, scheduled),
            ScheduleDelay = delay,
            Payload = new GoogleCloudExecutionDispatchMessage
            {
                RunId = normalizedRunId,
                Reason = normalizedReason,
                ScheduledAtUtc = scheduled
            },
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Vyral-Execution-Dispatch"] = "1"
            }
        }, ct);
    }

    Task IExecutionRunDispatcher.DispatchAsync(ExecutionDispatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchCoreAsync(request, ct);
    }

    private async Task DispatchCoreAsync(ExecutionDispatchRequest request, CancellationToken ct)
    {
        await DispatchAsync(request.RunId, request.Reason, request.ScheduledAtUtc, ct);
    }

    private static string BuildTaskId(string runId, string reason, DateTime? scheduledAtUtc)
    {
        var material = $"{runId}\n{reason}\n{scheduledAtUtc?.Ticks.ToString() ?? "now"}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"vyral-exec-{hash[..48]}";
    }
}

public sealed class GoogleCloudExecutionDispatchOptions
{
    public string ProjectId { get; init; } = string.Empty;
    public string LocationId { get; init; } = string.Empty;
    public string QueueId { get; init; } = string.Empty;
    public string WorkerUrl { get; init; } = string.Empty;
    public string? ServiceAccountEmail { get; init; }
    public string? OidcAudience { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectId)) throw new InvalidOperationException("Google execution project id is required.");
        if (string.IsNullOrWhiteSpace(LocationId)) throw new InvalidOperationException("Google execution location id is required.");
        if (string.IsNullOrWhiteSpace(QueueId)) throw new InvalidOperationException("Google execution queue id is required.");
        if (string.IsNullOrWhiteSpace(WorkerUrl) || !Uri.TryCreate(WorkerUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Google execution worker url must be absolute.");
        }
    }
}

public static class GoogleCloudExecutionDispatchReasons
{
    public const string RunReady = ExecutionDispatchReasons.RunReady;
    public const string TimerDue = ExecutionDispatchReasons.TimerDue;
    public const string ExternalEvent = ExecutionDispatchReasons.ExternalEvent;
    public const string LeaseExpired = ExecutionDispatchReasons.LeaseExpired;
}

public sealed class GoogleCloudExecutionDispatchMessage
{
    public string RunId { get; init; } = string.Empty;
    public string Reason { get; init; } = GoogleCloudExecutionDispatchReasons.RunReady;
    public DateTime? ScheduledAtUtc { get; init; }
}
