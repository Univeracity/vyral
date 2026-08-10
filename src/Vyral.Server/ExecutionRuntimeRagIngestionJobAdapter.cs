using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Server;

public sealed class ExecutionRuntimeRagIngestionJobAdapter
{
    public const string PluginId = "vyral.rag.ingestion";
    public const string TextHandlerId = "vyral.rag.ingestion.text";
    public const string BatchHandlerId = "vyral.rag.ingestion.batch";

    private readonly IExecutionRuntime _runtime;
    private readonly RagIngestionJobOptions _options;

    public ExecutionRuntimeRagIngestionJobAdapter(
        IExecutionRuntime runtime,
        IRagIngestionService ingestionService,
        RagIngestionJobOptions? options = null)
    {
        _runtime = runtime;
        _options = options ?? new RagIngestionJobOptions();
        _runtime.RegisterPlugin(new RagIngestionExecutionPlugin(ingestionService));
    }

    public async Task<RagIngestionJob> StartTextAsync(
        string collection,
        RagIngestTextRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default,
        string? admissionOperationId = null)
    {
        var payload = new RagIngestionTextPayload
        {
            Collection = collection,
            Request = request
        };
        var runRequest = new ExecutionRunRequest
        {
            HandlerId = TextHandlerId,
            PluginId = PluginId,
            Payload = JsonSerializer.SerializeToNode(payload, ExecutionJson.Options),
            IdempotencyKey = idempotencyKey,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 1 },
            Tags =
            {
                ["vyral.job"] = "rag-ingestion",
                ["vyral.rag.kind"] = RagIngestionJobKinds.Text,
                ["vyral.collection"] = collection
            }
        };
        if (!string.IsNullOrWhiteSpace(admissionOperationId))
        {
            runRequest.Tags["vyral.admission.operation-id"] = admissionOperationId;
        }
        var run = await _runtime.StartRunAsync(runRequest, ct);

        return MapRun(run);
    }

    public async Task<RagIngestionJob> StartBatchAsync(
        string collection,
        RagIngestTextBatchRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default,
        string? admissionOperationId = null)
    {
        var payload = new RagIngestionBatchPayload
        {
            Collection = collection,
            Request = request
        };
        var runRequest = new ExecutionRunRequest
        {
            HandlerId = BatchHandlerId,
            PluginId = PluginId,
            Payload = JsonSerializer.SerializeToNode(payload, ExecutionJson.Options),
            IdempotencyKey = idempotencyKey,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 1 },
            Tags =
            {
                ["vyral.job"] = "rag-ingestion",
                ["vyral.rag.kind"] = RagIngestionJobKinds.Batch,
                ["vyral.collection"] = collection
            }
        };
        if (!string.IsNullOrWhiteSpace(admissionOperationId))
        {
            runRequest.Tags["vyral.admission.operation-id"] = admissionOperationId;
        }
        var run = await _runtime.StartRunAsync(runRequest, ct);

        return MapRun(run);
    }

    public async Task<RagIngestionJob?> GetAsync(string id, bool includeResult = true, CancellationToken ct = default)
    {
        var run = await _runtime.GetRunAsync(id, includeResult, ct);
        return IsRagIngestionRun(run) ? MapRun(run!) : null;
    }

    public async Task<IReadOnlyList<RagIngestionJob>> ListAsync(int? limit = null, bool includeResult = false, CancellationToken ct = default)
    {
        var effectiveLimit = ValidateListLimit(limit);
        var textRuns = await _runtime.ListRunsAsync(new ExecutionRunQuery
        {
            HandlerId = TextHandlerId,
            IncludeResult = includeResult,
            Limit = effectiveLimit
        }, ct);
        var batchRuns = await _runtime.ListRunsAsync(new ExecutionRunQuery
        {
            HandlerId = BatchHandlerId,
            IncludeResult = includeResult,
            Limit = effectiveLimit
        }, ct);

        return textRuns
            .Concat(batchRuns)
            .OrderByDescending(run => run.CreatedAtUtc)
            .ThenBy(run => run.Id, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .Select(MapRun)
            .ToList();
    }

    public async Task<RagIngestionJob?> CancelAsync(string id, CancellationToken ct = default)
    {
        var existing = await _runtime.GetRunAsync(id, includeResult: false, ct);
        if (!IsRagIngestionRun(existing))
        {
            return null;
        }

        var run = await _runtime.CancelRunAsync(id, ct);
        return run is null ? null : MapRun(run);
    }

    private int ValidateListLimit(int? limit)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new InvalidOperationException("RAG ingestion job list limit must be greater than zero.");
        }

        var effectiveLimit = limit ?? _options.DefaultListLimit;
        if (effectiveLimit > _options.MaxListLimit)
        {
            throw new InvalidOperationException($"RAG ingestion job list limit cannot exceed {_options.MaxListLimit}.");
        }

        return effectiveLimit;
    }

    private static bool IsRagIngestionRun(ExecutionRun? run)
    {
        return run is not null &&
            (string.Equals(run.HandlerId, TextHandlerId, StringComparison.Ordinal) ||
             string.Equals(run.HandlerId, BatchHandlerId, StringComparison.Ordinal));
    }

    private static RagIngestionJob MapRun(ExecutionRun run)
    {
        var kind = GetKind(run);
        var textPayload = kind == RagIngestionJobKinds.Text
            ? run.Payload?.Deserialize<RagIngestionTextPayload>(ExecutionJson.Options)
            : null;
        var batchPayload = kind == RagIngestionJobKinds.Batch
            ? run.Payload?.Deserialize<RagIngestionBatchPayload>(ExecutionJson.Options)
            : null;
        var textResult = kind == RagIngestionJobKinds.Text
            ? run.Result?.Deserialize<RagIngestTextResult>(ExecutionJson.Options)
            : null;
        var batchResult = kind == RagIngestionJobKinds.Batch
            ? run.Result?.Deserialize<RagIngestTextBatchResult>(ExecutionJson.Options)
            : null;
        var requested = kind == RagIngestionJobKinds.Batch
            ? batchResult?.Requested ?? run.Requested ?? batchPayload?.Request?.Items.Count ?? 0
            : run.Requested ?? 1;

        return new RagIngestionJob
        {
            Admission = ExecutionAdmission.Create(
                run,
                VyralAdmissionOperations.ResolveOperationId(run,
                    kind == RagIngestionJobKinds.Text
                    ? VyralAdmissionOperations.StartRagIngestionTextJob
                    : VyralAdmissionOperations.StartRagIngestionBatchJob),
                $"/rag/ingestion/jobs/{run.Id}"),
            Id = run.Id,
            Kind = kind,
            Status = ToJobStatus(run.Status),
            Collection = textPayload?.Collection ?? batchPayload?.Collection ?? GetString(run.StatusDetails, "collection") ?? string.Empty,
            RequestHash = run.PayloadHash,
            CreatedAt = run.CreatedAtUtc,
            StartedAt = run.StartedAtUtc,
            CompletedAt = run.CompletedAtUtc,
            DurationMs = run.DurationMs,
            CancellationRequested = run.CancellationRequested,
            Requested = requested,
            Attempted = batchResult?.Attempted ?? run.Attempted ?? (textResult is null ? 0 : 1),
            Succeeded = batchResult?.Succeeded ?? run.Succeeded ?? (textResult is null ? 0 : 1),
            Failed = batchResult?.Failed ?? run.Failed ?? 0,
            CurrentIndex = GetInt(run.StatusDetails, "currentIndex"),
            CurrentDocumentId = GetString(run.StatusDetails, "currentDocumentId") ?? textResult?.DocumentId,
            CurrentPartitionKey = GetString(run.StatusDetails, "currentPartitionKey") ?? textResult?.PartitionKey,
            Progress = run.Progress ?? (run.Status == ExecutionRunStatuses.Succeeded ? 1 : 0),
            TextLength = textResult?.TextLength ?? batchResult?.TextLength ?? GetInt(run.StatusDetails, "textLength") ?? 0,
            ChunkCount = textResult?.ChunkCount ?? batchResult?.ChunkCount ?? GetInt(run.StatusDetails, "chunkCount") ?? 0,
            DeletedStaleCount = textResult?.DeletedStaleCount ?? batchResult?.DeletedStaleCount ?? 0,
            CreatedCount = textResult?.CreatedCount ?? batchResult?.CreatedCount ?? 0,
            UpdatedCount = textResult?.UpdatedCount ?? batchResult?.UpdatedCount ?? 0,
            ReusedCount = textResult?.ReusedCount ?? batchResult?.ReusedCount ?? 0,
            VectorGeneratedCount = textResult?.VectorGeneratedCount ?? batchResult?.VectorGeneratedCount ?? 0,
            VectorReusedCount = textResult?.VectorReusedCount ?? batchResult?.VectorReusedCount ?? 0,
            DeduplicatedCount = textResult?.DeduplicatedCount ?? batchResult?.DeduplicatedCount ?? 0,
            FailureClass = run.FailureClass,
            Error = run.Error,
            TextResult = textResult,
            BatchResult = batchResult
        };
    }

    private static string GetKind(ExecutionRun run)
    {
        if (run.Tags.TryGetValue("vyral.rag.kind", out var kind) && !string.IsNullOrWhiteSpace(kind))
        {
            return kind;
        }

        return string.Equals(run.HandlerId, TextHandlerId, StringComparison.Ordinal)
            ? RagIngestionJobKinds.Text
            : RagIngestionJobKinds.Batch;
    }

    private static string ToJobStatus(string status)
    {
        return status switch
        {
            ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting => RagIngestionJobStatuses.Queued,
            ExecutionRunStatuses.Running => RagIngestionJobStatuses.Running,
            ExecutionRunStatuses.Succeeded => RagIngestionJobStatuses.Succeeded,
            ExecutionRunStatuses.Cancelled => RagIngestionJobStatuses.Cancelled,
            ExecutionRunStatuses.Rejected => RagIngestionJobStatuses.Rejected,
            _ => RagIngestionJobStatuses.Failed
        };
    }

    private static JsonObject BuildStatusDetails(
        string kind,
        string collection,
        int requested,
        int attempted,
        int succeeded,
        int failed,
        int? currentIndex = null,
        string? currentDocumentId = null,
        string? currentPartitionKey = null,
        int? textLength = null,
        int? chunkCount = null)
    {
        var details = new JsonObject
        {
            ["kind"] = kind,
            ["collection"] = collection,
            ["requested"] = requested,
            ["attempted"] = attempted,
            ["succeeded"] = succeeded,
            ["failed"] = failed
        };
        if (currentIndex.HasValue)
        {
            details["currentIndex"] = currentIndex.Value;
        }

        if (!string.IsNullOrWhiteSpace(currentDocumentId))
        {
            details["currentDocumentId"] = currentDocumentId;
        }

        if (!string.IsNullOrWhiteSpace(currentPartitionKey))
        {
            details["currentPartitionKey"] = currentPartitionKey;
        }

        if (textLength.HasValue)
        {
            details["textLength"] = textLength.Value;
        }

        if (chunkCount.HasValue)
        {
            details["chunkCount"] = chunkCount.Value;
        }

        return details;
    }

    private static string? GetString(JsonObject? details, string key)
    {
        return details is not null && details.TryGetPropertyValue(key, out var node)
            ? node?.GetValue<string>()
            : null;
    }

    private static int? GetInt(JsonObject? details, string key)
    {
        return details is not null &&
               details.TryGetPropertyValue(key, out var node) &&
               node is JsonValue valueNode &&
               valueNode.TryGetValue<int>(out var value)
            ? value
            : null;
    }

    private static double CalculateProgress(int attempted, int requested)
    {
        return requested <= 0 ? 0 : Math.Clamp(attempted / (double)requested, 0, 1);
    }

    private sealed class TextExecutionHandler : IExecutionHandler
    {
        private readonly IRagIngestionService _ingestionService;

        public TextExecutionHandler(IRagIngestionService ingestionService)
        {
            _ingestionService = ingestionService;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
            TextHandlerId,
            "Vyral RAG text ingestion",
            handler => handler
                .WithPluginId(PluginId)
                .WithDescription("Ingests one RAG text document.")
                .WithMaxAttempts(1)
                .WithTag("vyral.job", "rag-ingestion")
                .WithTag("vyral.rag.kind", RagIngestionJobKinds.Text));

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var payload = context.Run.Payload?.Deserialize<RagIngestionTextPayload>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("RAG ingestion text payload is required.");
            var request = payload.Request ?? throw new InvalidOperationException("RAG ingestion text request is required.");
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 0,
                Succeeded = 0,
                Failed = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(RagIngestionJobKinds.Text, payload.Collection, 1, 0, 0, 0)
            }, ct);

            var result = await _ingestionService.IngestTextAsync(payload.Collection, request, ct);
            var resultNode = JsonSerializer.SerializeToNode(result, ExecutionJson.Options);
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 1,
                Succeeded = 1,
                Failed = 0,
                Progress = 1,
                Result = resultNode,
                StatusDetails = BuildStatusDetails(
                    RagIngestionJobKinds.Text,
                    payload.Collection,
                    1,
                    1,
                    1,
                    0,
                    currentDocumentId: result.DocumentId,
                    currentPartitionKey: result.PartitionKey,
                    textLength: result.TextLength,
                    chunkCount: result.ChunkCount)
            }, ct);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "rag-ingestion-result",
                Kind = ExecutionArtifactKinds.Json,
                Content = JsonSerializer.SerializeToNode(result, ExecutionJson.Options)
            }, ct);

            return ExecutionRunResult.Succeeded(
                JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                BuildStatusDetails(
                    RagIngestionJobKinds.Text,
                    payload.Collection,
                    1,
                    1,
                    1,
                    0,
                    currentDocumentId: result.DocumentId,
                    currentPartitionKey: result.PartitionKey,
                    textLength: result.TextLength,
                    chunkCount: result.ChunkCount));
        }
    }

    private sealed class BatchExecutionHandler : IExecutionHandler
    {
        private readonly IRagIngestionService _ingestionService;

        public BatchExecutionHandler(IRagIngestionService ingestionService)
        {
            _ingestionService = ingestionService;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
            BatchHandlerId,
            "Vyral RAG text batch ingestion",
            handler => handler
                .WithPluginId(PluginId)
                .WithDescription("Ingests a batch of RAG text documents.")
                .WithMaxAttempts(1)
                .WithTag("vyral.job", "rag-ingestion")
                .WithTag("vyral.rag.kind", RagIngestionJobKinds.Batch));

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var payload = context.Run.Payload?.Deserialize<RagIngestionBatchPayload>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("RAG ingestion batch payload is required.");
            var request = payload.Request ?? throw new InvalidOperationException("RAG ingestion batch request is required.");
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = request.Items.Count,
                Attempted = 0,
                Succeeded = 0,
                Failed = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(RagIngestionJobKinds.Batch, payload.Collection, request.Items.Count, 0, 0, 0)
            }, ct);

            var result = await _ingestionService.IngestTextBatchAsync(payload.Collection, request, ct);
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = result.Requested,
                Attempted = result.Attempted,
                Succeeded = result.Succeeded,
                Failed = result.Failed,
                Progress = CalculateProgress(result.Attempted, result.Requested),
                Result = JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                StatusDetails = BuildStatusDetails(
                    RagIngestionJobKinds.Batch,
                    payload.Collection,
                    result.Requested,
                    result.Attempted,
                    result.Succeeded,
                    result.Failed,
                    textLength: result.TextLength,
                    chunkCount: result.ChunkCount)
            }, ct);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "rag-ingestion-batch-result",
                Kind = ExecutionArtifactKinds.Json,
                Content = JsonSerializer.SerializeToNode(result, ExecutionJson.Options)
            }, ct);

            return ExecutionRunResult.Succeeded(
                JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                BuildStatusDetails(
                    RagIngestionJobKinds.Batch,
                    payload.Collection,
                    result.Requested,
                    result.Attempted,
                    result.Succeeded,
                    result.Failed,
                    textLength: result.TextLength,
                    chunkCount: result.ChunkCount));
        }
    }

    private sealed class RagIngestionExecutionPlugin : IExecutionPlugin
    {
        public RagIngestionExecutionPlugin(IRagIngestionService ingestionService)
        {
            Handlers =
            [
                new TextExecutionHandler(ingestionService),
                new BatchExecutionHandler(ingestionService)
            ];
            Descriptor = ExecutionDescriptors.Plugin(
                PluginId,
                "Vyral RAG ingestion",
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

    private sealed class RagIngestionTextPayload
    {
        public string Collection { get; set; } = string.Empty;
        public RagIngestTextRequest? Request { get; set; }
    }

    private sealed class RagIngestionBatchPayload
    {
        public string Collection { get; set; } = string.Empty;
        public RagIngestTextBatchRequest? Request { get; set; }
    }
}

public static class RagIngestionJobKinds
{
    public const string Text = "text";
    public const string Batch = "batch";
}

public static class RagIngestionJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
}

public sealed class RagIngestionJob
{
    public AdmissionReceipt Admission { get; set; } = new();
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = RagIngestionJobKinds.Text;
    public string Status { get; set; } = RagIngestionJobStatuses.Queued;
    public string Collection { get; set; } = string.Empty;
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
    public int? CurrentIndex { get; set; }
    public string? CurrentDocumentId { get; set; }
    public string? CurrentPartitionKey { get; set; }
    public double Progress { get; set; }
    public int TextLength { get; set; }
    public int ChunkCount { get; set; }
    public int DeletedStaleCount { get; set; }
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int ReusedCount { get; set; }
    public int VectorGeneratedCount { get; set; }
    public int VectorReusedCount { get; set; }
    public int DeduplicatedCount { get; set; }
    public string? FailureClass { get; set; }
    public string? Error { get; set; }
    public RagIngestTextResult? TextResult { get; set; }
    public RagIngestTextBatchResult? BatchResult { get; set; }
}

public sealed class RagIngestionJobOptions
{
    public int DefaultListLimit { get; init; } = 50;
    public int MaxListLimit { get; init; } = 200;
}
