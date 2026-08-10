using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Tasks.V2;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using CloudTask = Google.Cloud.Tasks.V2.Task;
using CloudTaskHttpMethod = Google.Cloud.Tasks.V2.HttpMethod;

namespace Vyral.Google;

public sealed class CloudTasksHttpJsonQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CloudTasksClient _client;

    public CloudTasksHttpJsonQueue(CloudTasksClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<CloudTasksHttpJsonEnqueueResult> EnqueueAsync(
        CloudTasksHttpJsonEnqueueRequest request,
        CancellationToken ct = default)
    {
        Validate(request);

        var parent = QueueName.FromProjectLocationQueue(request.ProjectId, request.LocationId, request.QueueId);
        var json = JsonSerializer.Serialize(request.Payload, JsonOptions);
        var task = new CloudTask
        {
            HttpRequest = new HttpRequest
            {
                HttpMethod = CloudTaskHttpMethod.Post,
                Url = request.Url,
                Body = ByteString.CopyFromUtf8(json)
            }
        };
        task.HttpRequest.Headers["Content-Type"] = "application/json";
        task.HttpRequest.Headers["Accept"] = "application/json";
        foreach (var (key, value) in request.Headers)
        {
            task.HttpRequest.Headers[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(request.ServiceAccountEmail))
        {
            var oidcToken = new OidcToken
            {
                ServiceAccountEmail = request.ServiceAccountEmail
            };
            if (!string.IsNullOrWhiteSpace(request.OidcAudience))
            {
                oidcToken.Audience = request.OidcAudience;
            }

            task.HttpRequest.OidcToken = oidcToken;
        }

        if (!string.IsNullOrWhiteSpace(request.TaskId))
        {
            task.Name = TaskName.FromProjectLocationQueueTask(
                request.ProjectId,
                request.LocationId,
                request.QueueId,
                request.TaskId).ToString();
        }

        if (request.ScheduleDelay.HasValue && request.ScheduleDelay.Value > TimeSpan.Zero)
        {
            task.ScheduleTime = Timestamp.FromDateTime(DateTime.UtcNow.Add(request.ScheduleDelay.Value));
        }

        try
        {
            var created = await _client.CreateTaskAsync(parent, task, ct);
            return new CloudTasksHttpJsonEnqueueResult
            {
                Name = created.Name,
                ScheduleTimeUtc = created.ScheduleTime?.ToDateTime(),
                Url = request.Url
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists && !string.IsNullOrWhiteSpace(request.TaskId))
        {
            return new CloudTasksHttpJsonEnqueueResult
            {
                Name = TaskName.FromProjectLocationQueueTask(
                    request.ProjectId,
                    request.LocationId,
                    request.QueueId,
                    request.TaskId).ToString(),
                Url = request.Url
            };
        }
    }

    private static void Validate(CloudTasksHttpJsonEnqueueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectId)) throw new InvalidOperationException("Cloud Tasks project id is required.");
        if (string.IsNullOrWhiteSpace(request.LocationId)) throw new InvalidOperationException("Cloud Tasks location id is required.");
        if (string.IsNullOrWhiteSpace(request.QueueId)) throw new InvalidOperationException("Cloud Tasks queue id is required.");
        if (string.IsNullOrWhiteSpace(request.Url) || !Uri.TryCreate(request.Url, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Cloud Tasks target URL must be absolute.");
        }

        if (request.Payload == null)
        {
            throw new InvalidOperationException("Cloud Tasks payload is required.");
        }
    }
}

public sealed class CloudTasksHttpJsonEnqueueRequest
{
    public string ProjectId { get; init; } = string.Empty;
    public string LocationId { get; init; } = string.Empty;
    public string QueueId { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public object? Payload { get; init; }
    public string? TaskId { get; init; }
    public string? ServiceAccountEmail { get; init; }
    public string? OidcAudience { get; init; }
    public TimeSpan? ScheduleDelay { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.Ordinal);
}

public sealed class CloudTasksHttpJsonEnqueueResult
{
    public string Name { get; init; } = string.Empty;
    public DateTime? ScheduleTimeUtc { get; init; }
    public string Url { get; init; } = string.Empty;
}
