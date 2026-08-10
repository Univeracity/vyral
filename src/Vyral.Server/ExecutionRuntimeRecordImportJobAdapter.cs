using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Server;

/// <summary>
/// Runs raw record writes through the durable execution runtime without changing their shape.
/// Retries are intentionally disabled: an import may have already applied a non-idempotent
/// record write when a process fails, so callers must use the normal record-level identities and
/// preconditions to make a later submission safe to repeat.
/// </summary>
public sealed class ExecutionRuntimeRecordImportJobAdapter
{
    public const string PluginId = "vyral.records";
    public const string BatchUpsertHandlerId = "vyral.records.batch-upsert";
    public const string CollectionImportHandlerId = "vyral.records.collection-import";
    public const string StoreWriteConcurrencyKey = "vyral.records.store-write";

    private readonly IExecutionRuntime _runtime;
    private readonly RecordImportJobOptions _options;
    private int _runtimeMaxPayloadBytes;

    public ExecutionRuntimeRecordImportJobAdapter(
        IExecutionRuntime runtime,
        IRecordCollectionStore recordStore,
        RecordImportJobOptions? options = null)
    {
        _runtime = runtime;
        _options = options ?? new RecordImportJobOptions();
        _runtime.RegisterPlugin(new RecordImportExecutionPlugin(recordStore));
    }

    public Task<RecordImportJob> StartBatchUpsertAsync(
        string collection,
        RecordBatchUpsertRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default,
        ExecutionScope? scope = null) =>
        StartRunAsync(CreateBatchUpsertRunRequest(collection, request, idempotencyKey, scope), ct);

    public Task<RecordImportJob> StartCollectionImportAsync(
        string collection,
        CollectionImportRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default,
        ExecutionScope? scope = null) =>
        StartRunAsync(CreateCollectionImportRunRequest(collection, request, idempotencyKey, scope), ct);

    public ExecutionRunRequest CreateBatchUpsertRunRequest(
        string collection,
        RecordBatchUpsertRequest request,
        string? idempotencyKey = null,
        ExecutionScope? scope = null,
        string? admissionOperationId = null) =>
        CreateRunRequest(
            BatchUpsertHandlerId,
            RecordImportJobKinds.BatchUpsert,
            collection,
            new RecordBatchUpsertPayload { Collection = collection, Request = request },
            idempotencyKey,
            scope,
            admissionOperationId);

    public ExecutionRunRequest CreateCollectionImportRunRequest(
        string collection,
        CollectionImportRequest request,
        string? idempotencyKey = null,
        ExecutionScope? scope = null,
        string? admissionOperationId = null) =>
        CreateRunRequest(
            CollectionImportHandlerId,
            RecordImportJobKinds.CollectionImport,
            collection,
            new CollectionImportPayload { Collection = collection, Request = request },
            idempotencyKey,
            scope,
            admissionOperationId);

    public async Task<RecordImportJob> StartRunAsync(ExecutionRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsRecordImportHandler(request.HandlerId))
        {
            throw new InvalidOperationException($"Execution handler '{request.HandlerId}' is not a record import handler.");
        }

        await ValidatePayloadSizeAsync(request.Payload, ct);
        return MapRun(await _runtime.StartRunAsync(request, ct));
    }

    public async Task<RecordImportJob?> GetAsync(string id, bool includeResult = true, CancellationToken ct = default)
    {
        var run = await GetRunAsync(id, includeResult, ct);
        return run is null ? null : MapRun(run);
    }

    public async Task<ExecutionRun?> GetRunAsync(string id, bool includeResult = true, CancellationToken ct = default)
    {
        var run = await _runtime.GetRunAsync(id, includeResult, ct);
        return IsRecordImportRun(run) ? run : null;
    }

    public async Task<IReadOnlyList<RecordImportJob>> ListAsync(int? limit = null, bool includeResult = false, CancellationToken ct = default) =>
        MapRuns(await ListRunsAsync(limit, includeResult, ct));

    public async Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(int? limit = null, bool includeResult = false, CancellationToken ct = default)
    {
        var effectiveLimit = ValidateListLimit(limit);
        var batchRuns = await ListHandlerRunsAsync(BatchUpsertHandlerId, effectiveLimit, includeResult, ct);
        var importRuns = await ListHandlerRunsAsync(CollectionImportHandlerId, effectiveLimit, includeResult, ct);

        return batchRuns
            .Concat(importRuns)
            .OrderByDescending(run => run.CreatedAtUtc)
            .ThenBy(run => run.Id, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .ToList();
    }

    public IReadOnlyList<RecordImportJob> MapRuns(IEnumerable<ExecutionRun> runs) =>
        runs.Select(MapRun).ToList();

    public RecordImportJob MapRun(ExecutionRun run) => MapRunCore(run);

    public async Task<RecordImportJob?> CancelAsync(string id, CancellationToken ct = default)
    {
        var existing = await GetRunAsync(id, includeResult: false, ct);
        if (existing is null)
        {
            return null;
        }

        var run = await _runtime.CancelRunAsync(id, ct);
        return run is null ? null : MapRun(run);
    }

    private static ExecutionRunRequest CreateRunRequest(
        string handlerId,
        string kind,
        string collection,
        object payload,
        string? idempotencyKey,
        ExecutionScope? scope,
        string? admissionOperationId = null)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        var runRequest = new ExecutionRunRequest
        {
            HandlerId = handlerId,
            PluginId = PluginId,
            Payload = JsonSerializer.SerializeToNode(payload, ExecutionJson.Options),
            IdempotencyKey = BuildScopedIdempotencyKey(idempotencyKey, scope, handlerId),
            Scope = scope,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 1 },
            Tags =
            {
                ["vyral.job"] = "record-import",
                ["vyral.record-import.kind"] = kind,
                ["vyral.collection"] = collection
            }
        };
        if (!string.IsNullOrWhiteSpace(admissionOperationId))
        {
            runRequest.Tags["vyral.admission.operation-id"] = admissionOperationId;
        }

        return runRequest;
    }

    // Local and some external execution adapters persist an idempotency key directly. Namespace
    // record-import submissions by the verified product/tenant scope before that durable boundary
    // so a shared-plane retry cannot replay or disclose another tenant's run.
    private static string? BuildScopedIdempotencyKey(string? idempotencyKey, ExecutionScope? scope, string handlerId)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || scope is null)
        {
            return idempotencyKey;
        }

        var material = string.Join("\n", scope.ProductId, scope.TenantId, handlerId, idempotencyKey);
        return "scope:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private async Task ValidatePayloadSizeAsync(JsonNode? payload, CancellationToken ct)
    {
        if (payload is null)
        {
            return;
        }

        var maxPayloadBytes = await GetRuntimeMaxPayloadBytesAsync(ct);
        var payloadBytes = Encoding.UTF8.GetByteCount(payload.ToJsonString(ExecutionJson.Options));
        if (payloadBytes > maxPayloadBytes)
        {
            throw new InvalidOperationException(
                $"Durable record import payload cannot exceed {maxPayloadBytes} bytes; this request serializes to {payloadBytes} bytes. Split the batch or externalize record content before submitting the job.");
        }
    }

    private async Task<int> GetRuntimeMaxPayloadBytesAsync(CancellationToken ct)
    {
        var cached = Volatile.Read(ref _runtimeMaxPayloadBytes);
        if (cached > 0)
        {
            return cached;
        }

        var maxPayloadBytes = ExecutionRuntimeLimits.Default.MaxPayloadBytes;
        if (_runtime is IExecutionRuntimeAdapter adapter)
        {
            var status = await adapter.GetAdapterStatusAsync(ct);
            if (status.OperationalPolicy?.MaxPayloadBytes > 0)
            {
                maxPayloadBytes = status.OperationalPolicy.MaxPayloadBytes;
            }
        }

        Interlocked.CompareExchange(ref _runtimeMaxPayloadBytes, maxPayloadBytes, 0);
        return Volatile.Read(ref _runtimeMaxPayloadBytes);
    }

    private Task<IReadOnlyList<ExecutionRun>> ListHandlerRunsAsync(
        string handlerId,
        int limit,
        bool includeResult,
        CancellationToken ct) =>
        _runtime.ListRunsAsync(new ExecutionRunQuery
        {
            HandlerId = handlerId,
            IncludeResult = includeResult,
            Limit = limit
        }, ct);

    private int ValidateListLimit(int? limit)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new InvalidOperationException("Record import job list limit must be greater than zero.");
        }

        var effectiveLimit = limit ?? _options.DefaultListLimit;
        if (effectiveLimit > _options.MaxListLimit)
        {
            throw new InvalidOperationException($"Record import job list limit cannot exceed {_options.MaxListLimit}.");
        }

        return effectiveLimit;
    }

    private static bool IsRecordImportRun(ExecutionRun? run) =>
        run is not null &&
        IsRecordImportHandler(run.HandlerId);

    private static bool IsRecordImportHandler(string? handlerId) =>
        string.Equals(handlerId, BatchUpsertHandlerId, StringComparison.Ordinal) ||
        string.Equals(handlerId, CollectionImportHandlerId, StringComparison.Ordinal);

    private static RecordImportJob MapRunCore(ExecutionRun run)
    {
        var kind = GetKind(run);
        var batchPayload = kind == RecordImportJobKinds.BatchUpsert
            ? run.Payload?.Deserialize<RecordBatchUpsertPayload>(ExecutionJson.Options)
            : null;
        var importPayload = kind == RecordImportJobKinds.CollectionImport
            ? run.Payload?.Deserialize<CollectionImportPayload>(ExecutionJson.Options)
            : null;
        var batchResult = kind == RecordImportJobKinds.BatchUpsert
            ? run.Result?.Deserialize<RecordBatchUpsertResult>(ExecutionJson.Options)
            : null;
        var importResult = kind == RecordImportJobKinds.CollectionImport
            ? run.Result?.Deserialize<CollectionImportResult>(ExecutionJson.Options)
            : null;
        var result = batchResult ?? importResult?.Records;
        var requested = result?.Requested ?? run.Requested ??
            (kind == RecordImportJobKinds.BatchUpsert
                ? batchPayload?.Request?.Records.Count
                : importPayload?.Request?.Snapshot?.Records.Count) ??
            GetInt(run.StatusDetails, "requested") ?? 0;

        return new RecordImportJob
        {
            Admission = ExecutionAdmission.Create(
                run,
                VyralAdmissionOperations.ResolveOperationId(run,
                    kind == RecordImportJobKinds.CollectionImport
                    ? VyralAdmissionOperations.StartCollectionImportJob
                    : VyralAdmissionOperations.StartRecordBatchUpsertJob),
                $"/record-import/jobs/{run.Id}"),
            Id = run.Id,
            Kind = kind,
            Status = ToJobStatus(run.Status),
            Collection = batchPayload?.Collection ?? importPayload?.Collection ?? batchResult?.Collection ?? importResult?.Collection ?? GetString(run.StatusDetails, "collection") ?? string.Empty,
            SourceCollection = importPayload?.Request?.Snapshot?.Collection ?? importResult?.SourceCollection,
            RequestHash = run.PayloadHash,
            CreatedAt = run.CreatedAtUtc,
            StartedAt = run.StartedAtUtc,
            CompletedAt = run.CompletedAtUtc,
            DurationMs = run.DurationMs,
            CancellationRequested = run.CancellationRequested,
            Requested = requested,
            Attempted = result?.Attempted ?? run.Attempted ?? GetInt(run.StatusDetails, "attempted") ?? (run.Status == ExecutionRunStatuses.Succeeded ? requested : 0),
            Succeeded = result?.Succeeded ?? run.Succeeded ?? GetInt(run.StatusDetails, "succeeded") ?? (run.Status == ExecutionRunStatuses.Succeeded ? requested : 0),
            Failed = result?.Failed ?? run.Failed ?? GetInt(run.StatusDetails, "failed") ?? 0,
            Progress = run.Progress ?? (run.Status == ExecutionRunStatuses.Succeeded ? 1 : 0),
            StoppedOnError = result?.StoppedOnError ?? false,
            FailureClass = run.FailureClass,
            Error = run.Error,
            BatchResult = batchResult,
            ImportResult = importResult
        };
    }

    private static string GetKind(ExecutionRun run)
    {
        if (run.Tags.TryGetValue("vyral.record-import.kind", out var kind) && !string.IsNullOrWhiteSpace(kind))
        {
            return kind;
        }

        return run.HandlerId == CollectionImportHandlerId
            ? RecordImportJobKinds.CollectionImport
            : RecordImportJobKinds.BatchUpsert;
    }

    private static string ToJobStatus(string status) => status switch
    {
        ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting => RecordImportJobStatuses.Queued,
        ExecutionRunStatuses.Running => RecordImportJobStatuses.Running,
        ExecutionRunStatuses.Succeeded => RecordImportJobStatuses.Succeeded,
        ExecutionRunStatuses.Cancelled => RecordImportJobStatuses.Cancelled,
        ExecutionRunStatuses.Rejected => RecordImportJobStatuses.Rejected,
        _ => RecordImportJobStatuses.Failed
    };

    private static JsonObject BuildStatusDetails(
        string kind,
        string collection,
        int requested,
        int attempted,
        int succeeded,
        int failed,
        bool stoppedOnError = false,
        string? sourceCollection = null)
    {
        var details = new JsonObject
        {
            ["kind"] = kind,
            ["collection"] = collection,
            ["requested"] = requested,
            ["attempted"] = attempted,
            ["succeeded"] = succeeded,
            ["failed"] = failed,
            ["stoppedOnError"] = stoppedOnError
        };
        if (!string.IsNullOrWhiteSpace(sourceCollection))
        {
            details["sourceCollection"] = sourceCollection;
        }

        return details;
    }

    private static string? GetString(JsonObject? details, string key) =>
        details is not null && details.TryGetPropertyValue(key, out var node)
            ? node?.GetValue<string>()
            : null;

    private static int? GetInt(JsonObject? details, string key) =>
        details is not null && details.TryGetPropertyValue(key, out var node) &&
        node is JsonValue valueNode && valueNode.TryGetValue<int>(out var value)
            ? value
            : null;

    private static double CalculateProgress(int attempted, int requested) =>
        requested <= 0 ? 0 : Math.Clamp(attempted / (double)requested, 0, 1);

    private sealed class BatchUpsertExecutionHandler : IExecutionHandler
    {
        private readonly IRecordCollectionStore _recordStore;

        public BatchUpsertExecutionHandler(IRecordCollectionStore recordStore) => _recordStore = recordStore;

        public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
            BatchUpsertHandlerId,
            "Vyral record batch upsert",
            handler => handler
                .WithPluginId(PluginId)
                .WithDescription("Upserts caller-shaped records without RAG reshaping.")
                .WithMaxAttempts(1)
                .WithConcurrencyKey(StoreWriteConcurrencyKey)
                .WithTag("vyral.job", "record-import")
                .WithTag("vyral.record-import.kind", RecordImportJobKinds.BatchUpsert));

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var payload = context.Run.Payload?.Deserialize<RecordBatchUpsertPayload>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("Record batch upsert payload is required.");
            var request = payload.Request ?? throw new InvalidOperationException("Record batch upsert request is required.");
            var requested = request.Records.Count;
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = requested,
                Attempted = 0,
                Succeeded = 0,
                Failed = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(RecordImportJobKinds.BatchUpsert, payload.Collection, requested, 0, 0, 0)
            }, ct);

            var result = await _recordStore.UpsertRecordsAsync(payload.Collection, request, ct);
            var resultNode = JsonSerializer.SerializeToNode(result, ExecutionJson.Options);
            var details = BuildStatusDetails(
                RecordImportJobKinds.BatchUpsert,
                payload.Collection,
                result.Requested,
                result.Attempted,
                result.Succeeded,
                result.Failed,
                result.StoppedOnError);
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = result.Requested,
                Attempted = result.Attempted,
                Succeeded = result.Succeeded,
                Failed = result.Failed,
                Progress = CalculateProgress(result.Attempted, result.Requested),
                Result = resultNode,
                StatusDetails = details
            }, ct);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "record-batch-upsert-result",
                Kind = ExecutionArtifactKinds.Json,
                Content = resultNode
            }, ct);
            return ExecutionRunResult.Succeeded(resultNode, details);
        }
    }

    private sealed class CollectionImportExecutionHandler : IExecutionHandler
    {
        private readonly IRecordCollectionStore _recordStore;

        public CollectionImportExecutionHandler(IRecordCollectionStore recordStore) => _recordStore = recordStore;

        public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
            CollectionImportHandlerId,
            "Vyral collection import",
            handler => handler
                .WithPluginId(PluginId)
                .WithDescription("Imports a validated collection snapshot through the durable runtime.")
                .WithMaxAttempts(1)
                .WithConcurrencyKey(StoreWriteConcurrencyKey)
                .WithTag("vyral.job", "record-import")
                .WithTag("vyral.record-import.kind", RecordImportJobKinds.CollectionImport));

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var payload = context.Run.Payload?.Deserialize<CollectionImportPayload>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("Collection import payload is required.");
            var request = payload.Request ?? throw new InvalidOperationException("Collection import request is required.");
            var requested = request.Snapshot?.Records.Count ?? 0;
            var sourceCollection = request.Snapshot?.Collection;
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = requested,
                Attempted = 0,
                Succeeded = 0,
                Failed = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(RecordImportJobKinds.CollectionImport, payload.Collection, requested, 0, 0, 0, sourceCollection: sourceCollection)
            }, ct);

            var result = await _recordStore.ImportCollectionAsync(payload.Collection, request, ct);
            var resultNode = JsonSerializer.SerializeToNode(result, ExecutionJson.Options);
            var details = BuildStatusDetails(
                RecordImportJobKinds.CollectionImport,
                payload.Collection,
                result.Records.Requested,
                result.Records.Attempted,
                result.Records.Succeeded,
                result.Records.Failed,
                result.Records.StoppedOnError,
                result.SourceCollection);
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = result.Records.Requested,
                Attempted = result.Records.Attempted,
                Succeeded = result.Records.Succeeded,
                Failed = result.Records.Failed,
                Progress = CalculateProgress(result.Records.Attempted, result.Records.Requested),
                Result = resultNode,
                StatusDetails = details
            }, ct);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "collection-import-result",
                Kind = ExecutionArtifactKinds.Json,
                Content = resultNode
            }, ct);
            return ExecutionRunResult.Succeeded(resultNode, details);
        }
    }

    private sealed class RecordImportExecutionPlugin : IExecutionPlugin
    {
        public RecordImportExecutionPlugin(IRecordCollectionStore recordStore)
        {
            Handlers =
            [
                new BatchUpsertExecutionHandler(recordStore),
                new CollectionImportExecutionHandler(recordStore)
            ];
            Descriptor = ExecutionDescriptors.Plugin(
                PluginId,
                "Vyral record import jobs",
                "1.0.0",
                plugin =>
                {
                    foreach (var handler in Handlers)
                    {
                        plugin.AddHandler(handler.Descriptor);
                    }
                });
        }

        public ExecutionPluginDescriptor Descriptor { get; }
        public IReadOnlyList<IExecutionHandler> Handlers { get; }
    }

    private sealed class RecordBatchUpsertPayload
    {
        public string Collection { get; set; } = string.Empty;
        public RecordBatchUpsertRequest? Request { get; set; }
    }

    private sealed class CollectionImportPayload
    {
        public string Collection { get; set; } = string.Empty;
        public CollectionImportRequest? Request { get; set; }
    }
}

public static class RecordImportJobKinds
{
    public const string BatchUpsert = "batch_upsert";
    public const string CollectionImport = "collection_import";
}

public static class RecordImportJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
}

public sealed class RecordImportJob
{
    public AdmissionReceipt Admission { get; set; } = new();
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = RecordImportJobKinds.BatchUpsert;
    public string Status { get; set; } = RecordImportJobStatuses.Queued;
    public string Collection { get; set; } = string.Empty;
    public string? SourceCollection { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double? DurationMs { get; set; }
    public bool CancellationRequested { get; set; }
    public int Requested { get; set; }
    public int Attempted { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public double Progress { get; set; }
    public bool StoppedOnError { get; set; }
    public string? FailureClass { get; set; }
    public string? Error { get; set; }
    public RecordBatchUpsertResult? BatchResult { get; set; }
    public CollectionImportResult? ImportResult { get; set; }
}

public sealed class RecordImportJobOptions
{
    public int DefaultListLimit { get; init; } = 50;
    public int MaxListLimit { get; init; } = 200;
}
