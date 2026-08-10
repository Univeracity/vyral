using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Vyral.Execution;

namespace Vyral.Execution.Aws;

/// <summary>
/// SQS transport for an external execution worker. Messages contain only an immutable dispatch
/// envelope; the worker must lease the run from the durable state store before observing or
/// changing any execution state. SQS is at-least-once, so duplicate deliveries are expected and
/// are made safe by the lease protocol rather than by the message transport.
/// </summary>
public sealed class AwsSqsExecutionDispatcher : IExecutionRunDispatcher
{
    private readonly IAwsSqsExecutionQueue _queue;

    public AwsSqsExecutionDispatcher(IAwsSqsExecutionQueue queue, AwsSqsExecutionDispatchOptions options)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
    }

    public AwsSqsExecutionDispatchOptions Options { get; }

    public async Task<AwsSqsExecutionEnqueueResult> DispatchAsync(
        string runId,
        string reason = AwsSqsExecutionDispatchReasons.RunReady,
        DateTime? scheduleAtUtc = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId)) throw new InvalidOperationException("Execution run id is required.");
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Execution dispatch reason is required.");

        var normalizedRunId = runId.Trim();
        var normalizedReason = reason.Trim();
        var scheduled = scheduleAtUtc?.ToUniversalTime();
        var delay = NormalizeDelay(scheduled);
        var messageId = BuildDispatchId(normalizedRunId, normalizedReason, scheduled);

        return await _queue.EnqueueAsync(new AwsSqsExecutionEnqueueRequest
        {
            QueueUrl = Options.QueueUrl,
            DelaySeconds = delay,
            MessageGroupId = Options.Fifo ? Options.MessageGroupId : null,
            MessageDeduplicationId = Options.Fifo ? messageId : null,
            Payload = new AwsSqsExecutionDispatchMessage
            {
                RunId = normalizedRunId,
                Reason = normalizedReason,
                ScheduledAtUtc = scheduled,
                DispatchId = messageId
            },
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vyral_execution_dispatch"] = "1"
            }
        }, ct);
    }

    Task IExecutionRunDispatcher.DispatchAsync(ExecutionDispatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchAsync(request.RunId, request.Reason, request.ScheduledAtUtc, ct);
    }

    private int? NormalizeDelay(DateTime? scheduledAtUtc)
    {
        if (!scheduledAtUtc.HasValue) return null;

        var delay = scheduledAtUtc.Value - DateTime.UtcNow;
        if (delay <= TimeSpan.Zero) return null;
        var roundedUpSeconds = checked((int)Math.Ceiling(delay.TotalSeconds));
        if (roundedUpSeconds > Options.MaximumDelaySeconds)
        {
            throw new InvalidOperationException(
                $"SQS dispatch can delay work by at most {Options.MaximumDelaySeconds} seconds. " +
                "Persist longer timers in the execution state store and dispatch them from maintenance when due.");
        }

        return roundedUpSeconds;
    }

    private static string BuildDispatchId(string runId, string reason, DateTime? scheduledAtUtc)
    {
        var material = $"{runId}\n{reason}\n{scheduledAtUtc?.Ticks.ToString() ?? "now"}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"vyral-exec-{hash[..48]}";
    }
}

/// <summary>
/// Provider-owned SQS boundary, kept narrow so dispatcher behavior can be tested without an AWS
/// account while the production implementation still uses the official AWS SDK.
/// </summary>
public interface IAwsSqsExecutionQueue
{
    Task<AwsSqsExecutionEnqueueResult> EnqueueAsync(AwsSqsExecutionEnqueueRequest request, CancellationToken ct = default);
}

/// <summary>Official AWS SDK implementation of the SQS dispatch boundary.</summary>
public sealed class AwsSqsExecutionQueue : IAwsSqsExecutionQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IAmazonSQS _client;

    public AwsSqsExecutionQueue(IAmazonSQS client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<AwsSqsExecutionEnqueueResult> EnqueueAsync(
        AwsSqsExecutionEnqueueRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var message = new SendMessageRequest
        {
            QueueUrl = request.QueueUrl,
            MessageBody = JsonSerializer.Serialize(request.Payload, JsonOptions),
            DelaySeconds = request.DelaySeconds ?? 0,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>(StringComparer.Ordinal)
        };
        foreach (var (name, value) in request.Attributes)
        {
            message.MessageAttributes[name] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = value
            };
        }

        if (!string.IsNullOrWhiteSpace(request.MessageGroupId))
        {
            message.MessageGroupId = request.MessageGroupId;
            message.MessageDeduplicationId = request.MessageDeduplicationId;
        }

        var result = await _client.SendMessageAsync(message, ct);
        return new AwsSqsExecutionEnqueueResult
        {
            MessageId = result.MessageId,
            SequenceNumber = result.SequenceNumber,
            DelaySeconds = request.DelaySeconds,
            QueueUrl = request.QueueUrl
        };
    }

    private static void Validate(AwsSqsExecutionEnqueueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.QueueUrl) || !Uri.TryCreate(request.QueueUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("SQS execution queue URL must be absolute.");
        }

        if (request.Payload is null) throw new InvalidOperationException("SQS execution dispatch payload is required.");
        if (request.DelaySeconds is < 0 or > AwsSqsExecutionDispatchOptions.MaximumSupportedDelaySeconds)
        {
            throw new InvalidOperationException($"SQS execution delay must be between 0 and {AwsSqsExecutionDispatchOptions.MaximumSupportedDelaySeconds} seconds.");
        }

        if (!string.IsNullOrWhiteSpace(request.MessageGroupId) && string.IsNullOrWhiteSpace(request.MessageDeduplicationId))
        {
            throw new InvalidOperationException("FIFO SQS execution dispatch requires a message deduplication id.");
        }
    }
}

public sealed class AwsSqsExecutionDispatchOptions
{
    public const int MaximumSupportedDelaySeconds = 900;

    public string QueueUrl { get; init; } = string.Empty;
    public bool Fifo { get; init; }
    public string MessageGroupId { get; init; } = "vyral-execution";
    public int MaximumDelaySeconds { get; init; } = MaximumSupportedDelaySeconds;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(QueueUrl) || !Uri.TryCreate(QueueUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("AWS execution queue URL must be absolute.");
        }

        if (MaximumDelaySeconds is < 0 or > MaximumSupportedDelaySeconds)
        {
            throw new InvalidOperationException($"AWS execution maximum SQS delay must be between 0 and {MaximumSupportedDelaySeconds} seconds.");
        }

        if (Fifo && string.IsNullOrWhiteSpace(MessageGroupId))
        {
            throw new InvalidOperationException("AWS FIFO execution dispatch requires a message group id.");
        }
    }
}

public static class AwsSqsExecutionDispatchReasons
{
    public const string RunReady = ExecutionDispatchReasons.RunReady;
    public const string TimerDue = ExecutionDispatchReasons.TimerDue;
    public const string ExternalEvent = ExecutionDispatchReasons.ExternalEvent;
    public const string LeaseExpired = ExecutionDispatchReasons.LeaseExpired;
}

public sealed class AwsSqsExecutionDispatchMessage
{
    public string RunId { get; init; } = string.Empty;
    public string Reason { get; init; } = AwsSqsExecutionDispatchReasons.RunReady;
    public DateTime? ScheduledAtUtc { get; init; }
    public string DispatchId { get; init; } = string.Empty;
}

public sealed class AwsSqsExecutionEnqueueRequest
{
    public string QueueUrl { get; init; } = string.Empty;
    public object? Payload { get; init; }
    public int? DelaySeconds { get; init; }
    public string? MessageGroupId { get; init; }
    public string? MessageDeduplicationId { get; init; }
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class AwsSqsExecutionEnqueueResult
{
    public string MessageId { get; init; } = string.Empty;
    public string? SequenceNumber { get; init; }
    public int? DelaySeconds { get; init; }
    public string QueueUrl { get; init; } = string.Empty;
}
