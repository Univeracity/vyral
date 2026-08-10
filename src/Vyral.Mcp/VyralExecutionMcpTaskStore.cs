using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Mcp;

/// <summary>
/// Durable, instance-independent MCP task state. The task envelope lives in the configured
/// object store while the authoritative work state remains an execution-runtime run.
/// </summary>
public sealed class VyralExecutionMcpTaskStore : IMcpTaskStore
{
    private const string Container = "vyral-mcp";
    private const int MaxUpdateAttempts = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IObjectStore _objects;
    private readonly IExecutionRuntime _runtime;
    private readonly IVyralMcpExecutionAuthorizer _authorizer;
    private readonly VyralMcpRequestContextAccessor _requestContextAccessor;
    private readonly TimeSpan _timeToLive;
    private readonly long _pollIntervalMs;
    private long _createCount;

    public VyralExecutionMcpTaskStore(
        IObjectStore objects,
        IExecutionRuntime runtime,
        IVyralMcpExecutionAuthorizer authorizer,
        VyralMcpRequestContextAccessor requestContextAccessor,
        VyralMcpOptions options)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _requestContextAccessor = requestContextAccessor ?? throw new ArgumentNullException(nameof(requestContextAccessor));
        ArgumentNullException.ThrowIfNull(options);
        _timeToLive = options.TaskTimeToLive;
        _pollIntervalMs = options.TaskPollIntervalMs;
    }

    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;

    public async Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _createCount) % 32 == 1)
            await TryPruneExpiredTasksAsync(cancellationToken);

        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            var state = new StoredTask
            {
                TaskId = Guid.NewGuid().ToString("N"),
                Status = StoredTaskStatuses.Working,
                CreatedAt = now,
                LastUpdatedAt = now,
                StatusMessage = "Execution is being scheduled."
            };
            try
            {
                await SaveAsync(state, etag: null, createOnly: true, cancellationToken);
                return ToTaskInfo(state);
            }
            catch (InvalidOperationException) when (attempt + 1 < MaxUpdateAttempts)
            {
                // A UUID collision or concurrent object creation is extraordinarily unlikely,
                // but retrying keeps the durable-create contract explicit.
            }
        }

        throw new InvalidOperationException("Unable to allocate a durable MCP task id.");
    }

    public async Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(taskId, cancellationToken);
        if (stored is null) return null;
        if (IsExpired(stored.State))
        {
            await TryDeleteAsync(stored.State.TaskId, stored.Etag, cancellationToken);
            return null;
        }

        if (string.IsNullOrWhiteSpace(stored.State.RunId))
            return ToTaskInfo(stored.State);

        var run = await _runtime.GetRunAsync(stored.State.RunId, includeResult: true, cancellationToken);
        await AuthorizeAsync(run, VyralMcpExecutionOperations.ReadRun, cancellationToken);
        return run is null ? ToTaskInfo(stored.State) : ToTaskInfo(stored.State, run);
    }

    public async Task SetCompletedAsync(
        string taskId,
        JsonElement result,
        CancellationToken cancellationToken = default)
    {
        var runId = TryGetRunId(result);
        var admission = TryGetAdmission(result);
        var cancelAssociatedRun = false;
        await UpdateAsync(taskId, state =>
        {
            if (state.Status == StoredTaskStatuses.Cancelled && !string.IsNullOrWhiteSpace(runId))
            {
                // A cancellation can land on another instance during the short interval between
                // task creation and run association. Persist the late association and cancel the
                // durable run below so the background execution cannot become an orphan.
                state.RunId = runId;
                state.Admission = admission;
                cancelAssociatedRun = true;
                return true;
            }
            if (IsTerminal(state.Status)) return false;
            if (!string.IsNullOrWhiteSpace(runId))
            {
                state.RunId = runId;
                state.Admission = admission;
                state.Status = StoredTaskStatuses.Working;
                state.StatusMessage = "Execution run accepted.";
                state.Result = null;
            }
            else
            {
                state.Status = StoredTaskStatuses.Completed;
                state.StatusMessage = "Task completed.";
                state.Result = result.Clone();
            }
            return true;
        }, cancellationToken);

        if (!cancelAssociatedRun || string.IsNullOrWhiteSpace(runId)) return;
        var run = await _runtime.GetRunAsync(runId, includeResult: false, cancellationToken);
        await AuthorizeAsync(run, VyralMcpExecutionOperations.CancelRun, cancellationToken);
        if (run is not null && !ExecutionRunStatuses.IsTerminal(run.Status))
            _ = await _runtime.CancelRunAsync(run.Id, cancellationToken);
    }

    public Task SetFailedAsync(
        string taskId,
        JsonElement error,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(taskId, state =>
        {
            if (IsTerminal(state.Status)) return false;
            state.Status = StoredTaskStatuses.Failed;
            state.StatusMessage = "The MCP request failed.";
            state.Error = error.Clone();
            return true;
        }, cancellationToken);

    public async Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(taskId, cancellationToken);
        if (stored is null) throw UnknownTask(taskId);
        if (IsTerminal(stored.State.Status)) return false;

        if (!string.IsNullOrWhiteSpace(stored.State.RunId))
        {
            var run = await _runtime.GetRunAsync(stored.State.RunId, includeResult: false, cancellationToken);
            await AuthorizeAsync(run, VyralMcpExecutionOperations.CancelRun, cancellationToken);
            if (run is null || ExecutionRunStatuses.IsTerminal(run.Status)) return false;
            _ = await _runtime.CancelRunAsync(run.Id, cancellationToken);
            return true;
        }

        await UpdateAsync(taskId, state =>
        {
            state.Status = StoredTaskStatuses.Cancelled;
            state.StatusMessage = "Task cancelled before execution was associated.";
            return true;
        }, cancellationToken);
        return true;
    }

    public async Task SetInputRequestsAsync(
        string taskId,
        IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken = default)
    {
        await UpdateAsync(taskId, state =>
        {
            if (IsTerminal(state.Status)) return false;
            foreach (var pair in inputRequests) state.InputRequests[pair.Key] = pair.Value;
            state.Status = StoredTaskStatuses.InputRequired;
            state.StatusMessage = "Task requires client input.";
            return true;
        }, cancellationToken);

        // The SDK waits on this process's InputResponseReceived event. A tasks/update request may
        // land on a different instance, so poll the durable response mailbox and relay it locally.
        _ = PollForInputResponsesAsync(taskId, inputRequests.Keys.ToHashSet(StringComparer.Ordinal), cancellationToken);
    }

    public async Task ResolveInputRequestsAsync(
        string taskId,
        IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken = default)
    {
        var resolvedCount = 0;
        await UpdateAsync(taskId, state =>
        {
            if (IsTerminal(state.Status)) return false;
            resolvedCount = 0;
            foreach (var pair in inputResponses)
            {
                if (!state.InputRequests.Remove(pair.Key)) continue;
                state.ResolvedInputResponses[pair.Key] = pair.Value;
                resolvedCount++;
            }
            state.Status = state.InputRequests.Count == 0
                ? StoredTaskStatuses.Working
                : StoredTaskStatuses.InputRequired;
            state.StatusMessage = state.InputRequests.Count == 0
                ? "Execution resumed."
                : "Task requires client input.";
            return resolvedCount > 0;
        }, cancellationToken);
    }

    private async Task PollForInputResponsesAsync(
        string taskId,
        IReadOnlySet<string> requestIds,
        CancellationToken cancellationToken)
    {
        var remaining = requestIds.ToHashSet(StringComparer.Ordinal);
        var deadline = DateTimeOffset.UtcNow + _timeToLive;
        try
        {
            while (remaining.Count > 0 && DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var delivered = new List<KeyValuePair<string, InputResponse>>();
                var terminal = false;
                try
                {
                    await UpdateAsync(taskId, state =>
                    {
                        delivered.Clear();
                        foreach (var requestId in remaining)
                        {
                            if (!state.ResolvedInputResponses.Remove(requestId, out var response)) continue;
                            delivered.Add(new KeyValuePair<string, InputResponse>(requestId, response));
                        }
                        terminal = IsTerminal(state.Status);
                        return delivered.Count > 0;
                    }, cancellationToken);
                }
                catch (McpProtocolException)
                {
                    // The task expired or was removed while the originating invocation waited.
                    return;
                }
                catch (InvalidOperationException)
                {
                    // A transient object-store or compare-and-swap failure is safe to retry until
                    // the task TTL. The durable mailbox remains authoritative between attempts.
                    await Task.Delay(TimeSpan.FromMilliseconds(_pollIntervalMs), cancellationToken);
                    continue;
                }

                foreach (var pair in delivered)
                {
                    remaining.Remove(pair.Key);
                    InputResponseReceived?.Invoke(new InputResponseReceivedEventArgs
                    {
                        TaskId = taskId,
                        RequestId = pair.Key,
                        Response = pair.Value
                    });
                }

                if (remaining.Count == 0 || terminal) return;
                await Task.Delay(TimeSpan.FromMilliseconds(_pollIntervalMs), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The task invocation was cancelled or completed while this response relay waited.
        }
    }

    private async Task UpdateAsync(
        string taskId,
        Func<StoredTask, bool> update,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var stored = await LoadAsync(taskId, cancellationToken)
                ?? throw UnknownTask(taskId);
            if (!update(stored.State)) return;
            stored.State.LastUpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                await SaveAsync(stored.State, stored.Etag, createOnly: false, cancellationToken);
                return;
            }
            catch (InvalidOperationException) when (attempt + 1 < MaxUpdateAttempts)
            {
                // Optimistic concurrency lost to another stateless request; reload and retry.
            }
        }

        throw new InvalidOperationException($"Unable to update MCP task '{taskId}' after concurrent writes.");
    }

    private async Task<StoredTaskEnvelope?> LoadAsync(string taskId, CancellationToken cancellationToken)
    {
        if (!IsValidTaskId(taskId)) return null;
        var result = await _objects.GetObjectAsync(new ObjectReadRequest
        {
            Container = Container,
            Key = GetKey(taskId)
        }, cancellationToken);
        if (result is null) return null;
        using (result.Content)
        {
            var state = await JsonSerializer.DeserializeAsync<StoredTask>(result.Content, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException($"MCP task '{taskId}' contains an invalid durable envelope.");
            if (!string.Equals(state.TaskId, taskId, StringComparison.Ordinal))
                throw new InvalidOperationException("MCP task envelope identity does not match its object key.");
            return new StoredTaskEnvelope(state, result.Etag);
        }
    }

    private async Task SaveAsync(
        StoredTask state,
        string? etag,
        bool createOnly,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        using var content = new MemoryStream(bytes, writable: false);
        await _objects.PutObjectAsync(new ObjectWriteRequest
        {
            Container = Container,
            Key = GetKey(state.TaskId),
            ContentType = "application/json",
            Content = content,
            IfMatch = createOnly ? null : etag,
            IfNoneMatch = createOnly ? "*" : null,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mcp_task"] = "true"
            }
        }, cancellationToken);
    }

    private async Task TryDeleteAsync(string taskId, string etag, CancellationToken cancellationToken)
    {
        try
        {
            await _objects.DeleteObjectAsync(new ObjectDeleteRequest
            {
                Container = Container,
                Key = GetKey(taskId),
                IfMatch = etag
            }, cancellationToken);
        }
        catch (Exception)
        {
            // Another instance refreshed the envelope while this request observed expiry.
        }
    }

    private async Task TryPruneExpiredTasksAsync(CancellationToken cancellationToken)
    {
        try
        {
            var page = await _objects.ListObjectsAsync(new ObjectListRequest
            {
                Container = Container,
                Prefix = "tasks/",
                Limit = 100
            }, cancellationToken);
            var cutoff = DateTime.UtcNow - _timeToLive;
            foreach (var item in page.Items.Where(item => item.UpdatedAt <= cutoff))
            {
                await _objects.DeleteObjectAsync(new ObjectDeleteRequest
                {
                    Container = Container,
                    Key = item.Key,
                    IfMatch = item.Etag
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // Cleanup is opportunistic. Conditional-delete races and temporarily unavailable
            // list support must not prevent a new durable task from being created.
        }
    }

    private async Task AuthorizeAsync(
        ExecutionRun? run,
        string operation,
        CancellationToken cancellationToken)
    {
        var context = _requestContextAccessor.Current;
        if (context is null)
            throw new InvalidOperationException("MCP task access requires an active request context.");
        await _authorizer.AuthorizeRunAsync(context, run, operation, cancellationToken);
    }

    private McpTaskInfo ToTaskInfo(StoredTask state, ExecutionRun? run = null)
    {
        var status = ToMcpStatus(state.Status);
        var updatedAt = state.LastUpdatedAt;
        var statusMessage = state.StatusMessage;
        JsonElement? result = state.Result;
        JsonElement? error = state.Error;

        if (run is not null && state.Status != StoredTaskStatuses.Cancelled)
        {
            if (state.Admission is not null)
            {
                run.Admission = state.Admission;
                run.IdempotencyKey = null;
            }
            else
            {
                // Compatibility for task envelopes created before admission receipts were stored.
                ExecutionAdmission.Attach(
                    run,
                    "startExecutionRun",
                    $"/execution/runs/{run.Id}");
            }
            updatedAt = new DateTimeOffset(DateTime.SpecifyKind(run.UpdatedAtUtc, DateTimeKind.Utc));
            statusMessage = $"Execution run is {run.Status}.";
            switch (run.Status)
            {
                case ExecutionRunStatuses.Succeeded:
                    status = McpTaskStatus.Completed;
                    result = CreateToolResult(run, isError: false);
                    break;
                case ExecutionRunStatuses.Cancelled:
                    status = McpTaskStatus.Cancelled;
                    result = null;
                    break;
                case ExecutionRunStatuses.Failed:
                case ExecutionRunStatuses.Rejected:
                case ExecutionRunStatuses.TimedOut:
                    // Tool-level/domain failures are successful JSON-RPC executions with isError=true.
                    status = McpTaskStatus.Completed;
                    result = CreateToolResult(run, isError: true);
                    break;
                default:
                    status = McpTaskStatus.Working;
                    result = null;
                    break;
            }
        }

        return new McpTaskInfo(
            state.TaskId,
            status,
            state.CreatedAt,
            updatedAt,
            _timeToLive,
            _pollIntervalMs,
            statusMessage,
            result,
            error,
            state.InputRequests);
    }

    private static JsonElement CreateToolResult(ExecutionRun run, bool isError)
    {
        var text = isError
            ? run.Error ?? $"Execution run ended with status '{run.Status}'."
            : $"Execution run '{run.Id}' completed successfully.";
        return JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text } },
            structuredContent = run,
            isError
        }, JsonOptions);
    }

    private static string? TryGetRunId(JsonElement result)
    {
        if (TryGetString(result, "id", out var id)) return id;
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("structuredContent", out var structured) &&
            TryGetString(structured, "id", out id)) return id;
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("result", out var nested)) return TryGetRunId(nested);
        return null;
    }

    private static AdmissionReceipt? TryGetAdmission(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("admission", out var admission))
        {
            return admission.Deserialize<AdmissionReceipt>(JsonOptions);
        }
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("structuredContent", out var structured))
        {
            var value = TryGetAdmission(structured);
            if (value is not null) return value;
        }
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("result", out var nested))
        {
            return TryGetAdmission(nested);
        }
        return null;
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private bool IsExpired(StoredTask state) => DateTimeOffset.UtcNow - state.CreatedAt >= _timeToLive;

    private static bool IsTerminal(string status) =>
        status is StoredTaskStatuses.Completed or StoredTaskStatuses.Cancelled or StoredTaskStatuses.Failed;

    private static McpTaskStatus ToMcpStatus(string status) => status switch
    {
        StoredTaskStatuses.Working => McpTaskStatus.Working,
        StoredTaskStatuses.InputRequired => McpTaskStatus.InputRequired,
        StoredTaskStatuses.Completed => McpTaskStatus.Completed,
        StoredTaskStatuses.Cancelled => McpTaskStatus.Cancelled,
        StoredTaskStatuses.Failed => McpTaskStatus.Failed,
        _ => throw new InvalidOperationException($"Unknown stored MCP task status '{status}'.")
    };

    private static string GetKey(string taskId) => $"tasks/{taskId}.json";

    private static bool IsValidTaskId(string taskId) =>
        taskId.Length == 32 && taskId.All(Uri.IsHexDigit);

    private static McpProtocolException UnknownTask(string taskId) =>
        new($"MCP task '{taskId}' does not exist.", McpErrorCode.InvalidParams);

    private sealed class StoredTask
    {
        public string TaskId { get; set; } = string.Empty;
        public string? RunId { get; set; }
        public AdmissionReceipt? Admission { get; set; }
        public string Status { get; set; } = StoredTaskStatuses.Working;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastUpdatedAt { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public JsonElement? Result { get; set; }
        public JsonElement? Error { get; set; }
        public Dictionary<string, InputRequest> InputRequests { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, InputResponse> ResolvedInputResponses { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record StoredTaskEnvelope(StoredTask State, string Etag);

    private static class StoredTaskStatuses
    {
        public const string Working = "working";
        public const string InputRequired = "input_required";
        public const string Completed = "completed";
        public const string Cancelled = "cancelled";
        public const string Failed = "failed";
    }
}
