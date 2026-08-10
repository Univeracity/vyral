using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Server;

public sealed class ExecutionRuntimeGraphJobAdapter
{
    public const string PluginId = "vyral.graph";
    public const string ImportHandlerId = "vyral.graph.import";
    public const string InspectionHandlerId = "vyral.graph.inspect";
    public const string DoctorHandlerId = "vyral.graph.doctor";

    private readonly IExecutionRuntime _runtime;
    private readonly GraphJobOptions _options;

    public ExecutionRuntimeGraphJobAdapter(
        IExecutionRuntime runtime,
        IRecordCollectionStore recordStore,
        GraphJobOptions? options = null)
    {
        _runtime = runtime;
        _options = options ?? new GraphJobOptions();
        _runtime.RegisterPlugin(new GraphExecutionPlugin(recordStore));
    }

    public Task<GraphJob> StartImportAsync(
        string collection,
        VyralGraphCollectionImportRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default,
        string? admissionOperationId = null)
    {
        var payload = new GraphImportPayload
        {
            Collection = collection,
            Request = request
        };
        return StartAsync(
            ImportHandlerId,
            GraphJobKinds.Import,
            collection,
            payload,
            idempotencyKey,
            ct,
            admissionOperationId);
    }

    public Task<GraphJob> StartInspectionAsync(
        string collection,
        VyralGraphCollectionInspectionRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var payload = new GraphInspectionPayload
        {
            Collection = collection,
            Request = request
        };
        return StartAsync(
            InspectionHandlerId,
            GraphJobKinds.Inspect,
            collection,
            payload,
            idempotencyKey,
            ct);
    }

    public Task<GraphJob> StartDoctorAsync(
        string collection,
        VyralGraphDoctorRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var payload = new GraphDoctorPayload
        {
            Collection = collection,
            Request = request
        };
        return StartAsync(
            DoctorHandlerId,
            GraphJobKinds.Doctor,
            collection,
            payload,
            idempotencyKey,
            ct);
    }

    public async Task<GraphJob?> GetAsync(string id, bool includeResult = true, CancellationToken ct = default)
    {
        var run = await _runtime.GetRunAsync(id, includeResult, ct);
        return IsGraphRun(run) ? MapRun(run!) : null;
    }

    public async Task<IReadOnlyList<GraphJob>> ListAsync(int? limit = null, bool includeResult = false, CancellationToken ct = default)
    {
        var effectiveLimit = ValidateListLimit(limit);
        var importRuns = await ListHandlerRunsAsync(ImportHandlerId, effectiveLimit, includeResult, ct);
        var inspectionRuns = await ListHandlerRunsAsync(InspectionHandlerId, effectiveLimit, includeResult, ct);
        var doctorRuns = await ListHandlerRunsAsync(DoctorHandlerId, effectiveLimit, includeResult, ct);

        return importRuns
            .Concat(inspectionRuns)
            .Concat(doctorRuns)
            .OrderByDescending(run => run.CreatedAtUtc)
            .ThenBy(run => run.Id, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .Select(MapRun)
            .ToList();
    }

    public async Task<GraphJob?> CancelAsync(string id, CancellationToken ct = default)
    {
        var existing = await _runtime.GetRunAsync(id, includeResult: false, ct);
        if (!IsGraphRun(existing))
        {
            return null;
        }

        var run = await _runtime.CancelRunAsync(id, ct);
        return run is null ? null : MapRun(run);
    }

    private async Task<GraphJob> StartAsync(
        string handlerId,
        string kind,
        string collection,
        object payload,
        string? idempotencyKey,
        CancellationToken ct,
        string? admissionOperationId = null)
    {
        var runRequest = new ExecutionRunRequest
        {
            HandlerId = handlerId,
            PluginId = PluginId,
            Payload = JsonSerializer.SerializeToNode(payload, ExecutionJson.Options),
            IdempotencyKey = idempotencyKey,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 1 },
            Tags =
            {
                ["vyral.job"] = "graph",
                ["vyral.graph.kind"] = kind,
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

    private Task<IReadOnlyList<ExecutionRun>> ListHandlerRunsAsync(
        string handlerId,
        int limit,
        bool includeResult,
        CancellationToken ct)
    {
        return _runtime.ListRunsAsync(new ExecutionRunQuery
        {
            HandlerId = handlerId,
            IncludeResult = includeResult,
            Limit = limit
        }, ct);
    }

    private int ValidateListLimit(int? limit)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new InvalidOperationException("Graph job list limit must be greater than zero.");
        }

        var effectiveLimit = limit ?? _options.DefaultListLimit;
        if (effectiveLimit > _options.MaxListLimit)
        {
            throw new InvalidOperationException($"Graph job list limit cannot exceed {_options.MaxListLimit}.");
        }

        return effectiveLimit;
    }

    private static bool IsGraphRun(ExecutionRun? run)
    {
        return run is not null &&
            (string.Equals(run.HandlerId, ImportHandlerId, StringComparison.Ordinal) ||
             string.Equals(run.HandlerId, InspectionHandlerId, StringComparison.Ordinal) ||
             string.Equals(run.HandlerId, DoctorHandlerId, StringComparison.Ordinal));
    }

    private static GraphJob MapRun(ExecutionRun run)
    {
        var kind = GetKind(run);
        var importPayload = kind == GraphJobKinds.Import
            ? run.Payload?.Deserialize<GraphImportPayload>(ExecutionJson.Options)
            : null;
        var inspectionPayload = kind == GraphJobKinds.Inspect
            ? run.Payload?.Deserialize<GraphInspectionPayload>(ExecutionJson.Options)
            : null;
        var doctorPayload = kind == GraphJobKinds.Doctor
            ? run.Payload?.Deserialize<GraphDoctorPayload>(ExecutionJson.Options)
            : null;
        var importResult = kind == GraphJobKinds.Import
            ? run.Result?.Deserialize<VyralGraphCollectionImportResult>(ExecutionJson.Options)
            : null;
        var inspectionResult = kind == GraphJobKinds.Inspect
            ? run.Result?.Deserialize<VyralGraphCollectionInspectionResult>(ExecutionJson.Options)
            : null;
        var doctorResult = kind == GraphJobKinds.Doctor
            ? run.Result?.Deserialize<VyralGraphDoctorResult>(ExecutionJson.Options)
            : null;
        var collection = importPayload?.Collection ??
            inspectionPayload?.Collection ??
            doctorPayload?.Collection ??
            importResult?.Collection ??
            inspectionResult?.Collection ??
            doctorResult?.Collection ??
            GetString(run.StatusDetails, "collection") ??
            string.Empty;
        var requested = importResult?.RecordCount ??
            run.Requested ??
            GetInt(run.StatusDetails, "recordCount") ??
            CountGraphRecords(importPayload?.Request?.Envelope) ??
            1;

        return new GraphJob
        {
            Admission = ExecutionAdmission.Create(
                run,
                VyralAdmissionOperations.ResolveOperationId(run, kind switch
                {
                    GraphJobKinds.Import => VyralAdmissionOperations.StartGraphImportJob,
                    GraphJobKinds.Inspect => VyralAdmissionOperations.StartGraphInspectionJob,
                    _ => VyralAdmissionOperations.StartGraphDoctorJob
                }),
                $"/graph/jobs/{run.Id}"),
            Id = run.Id,
            Kind = kind,
            Status = ToJobStatus(run.Status),
            Collection = collection,
            RequestHash = run.PayloadHash,
            CreatedAt = run.CreatedAtUtc,
            StartedAt = run.StartedAtUtc,
            CompletedAt = run.CompletedAtUtc,
            DurationMs = run.DurationMs,
            CancellationRequested = run.CancellationRequested,
            Requested = requested,
            Attempted = importResult?.Records.Attempted ?? run.Attempted ?? GetInt(run.StatusDetails, "attempted") ?? (run.Status == ExecutionRunStatuses.Succeeded ? requested : 0),
            Succeeded = importResult?.Records.Succeeded ?? run.Succeeded ?? GetInt(run.StatusDetails, "succeeded") ?? (run.Status == ExecutionRunStatuses.Succeeded ? requested : 0),
            Failed = importResult?.Records.Failed ?? run.Failed ?? GetInt(run.StatusDetails, "failed") ?? 0,
            Progress = run.Progress ?? (run.Status == ExecutionRunStatuses.Succeeded ? 1 : 0),
            GraphId = importResult?.GraphId ??
                inspectionResult?.GraphId ??
                doctorResult?.Inspection?.GraphId ??
                importPayload?.Request?.Envelope?.Scope?.GraphId ??
                inspectionPayload?.Request?.GraphId ??
                doctorPayload?.Request?.GraphId ??
                GetString(run.StatusDetails, "graphId"),
            PartitionKey = importResult?.PartitionKey ??
                inspectionResult?.PartitionKey ??
                doctorResult?.Inspection?.PartitionKey ??
                importPayload?.Request?.Envelope?.Scope?.PartitionKey ??
                inspectionPayload?.Request?.PartitionKey ??
                doctorPayload?.Request?.PartitionKey ??
                GetString(run.StatusDetails, "partitionKey"),
            RecordCount = importResult?.RecordCount ?? inspectionResult?.RecordCount ?? doctorResult?.GraphRecordCount ?? GetInt(run.StatusDetails, "recordCount") ?? 0,
            NodeCount = importResult?.NodeCount ?? inspectionResult?.NodeCount ?? doctorResult?.GraphNodeCount ?? GetInt(run.StatusDetails, "nodeCount") ?? 0,
            EdgeCount = importResult?.EdgeCount ?? inspectionResult?.EdgeCount ?? doctorResult?.GraphEdgeCount ?? GetInt(run.StatusDetails, "edgeCount") ?? 0,
            AssertionCount = importResult?.AssertionCount ?? inspectionResult?.AssertionCount ?? GetInt(run.StatusDetails, "assertionCount") ?? 0,
            ReviewCount = importResult?.ReviewCount ?? inspectionResult?.ReviewCount ?? GetInt(run.StatusDetails, "reviewCount") ?? 0,
            ProjectionCount = importResult?.ProjectionCount ?? inspectionResult?.ProjectionCount ?? GetInt(run.StatusDetails, "projectionCount") ?? 0,
            WarningCount = inspectionResult?.WarningCount ?? doctorResult?.Inspection?.WarningCount ?? GetInt(run.StatusDetails, "warningCount") ?? 0,
            ErrorCount = GetInt(run.StatusDetails, "errorCount") ?? 0,
            AnomalyCount = inspectionResult?.AnomalyCount ?? doctorResult?.Inspection?.AnomalyCount ?? GetInt(run.StatusDetails, "anomalyCount") ?? 0,
            FailureClass = run.FailureClass,
            Error = run.Error,
            ImportResult = importResult,
            InspectionResult = inspectionResult,
            DoctorResult = doctorResult
        };
    }

    private static string GetKind(ExecutionRun run)
    {
        if (run.Tags.TryGetValue("vyral.graph.kind", out var kind) && !string.IsNullOrWhiteSpace(kind))
        {
            return kind;
        }

        var detailKind = GetString(run.StatusDetails, "kind");
        if (!string.IsNullOrWhiteSpace(detailKind))
        {
            return detailKind;
        }

        return run.HandlerId switch
        {
            ImportHandlerId => GraphJobKinds.Import,
            InspectionHandlerId => GraphJobKinds.Inspect,
            DoctorHandlerId => GraphJobKinds.Doctor,
            _ => GraphJobKinds.Import
        };
    }

    private static string ToJobStatus(string status)
    {
        return status switch
        {
            ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting => GraphJobStatuses.Queued,
            ExecutionRunStatuses.Running => GraphJobStatuses.Running,
            ExecutionRunStatuses.Succeeded => GraphJobStatuses.Succeeded,
            ExecutionRunStatuses.Cancelled => GraphJobStatuses.Cancelled,
            ExecutionRunStatuses.Rejected => GraphJobStatuses.Rejected,
            _ => GraphJobStatuses.Failed
        };
    }

    private static JsonObject BuildStatusDetails(
        string kind,
        string collection,
        string? graphId,
        string? partitionKey,
        int requested,
        int attempted,
        int succeeded,
        int failed,
        int recordCount = 0,
        int nodeCount = 0,
        int edgeCount = 0,
        int assertionCount = 0,
        int reviewCount = 0,
        int projectionCount = 0,
        int warningCount = 0,
        int errorCount = 0,
        int anomalyCount = 0)
    {
        var details = new JsonObject
        {
            ["kind"] = kind,
            ["collection"] = collection,
            ["requested"] = requested,
            ["attempted"] = attempted,
            ["succeeded"] = succeeded,
            ["failed"] = failed,
            ["recordCount"] = recordCount,
            ["nodeCount"] = nodeCount,
            ["edgeCount"] = edgeCount,
            ["assertionCount"] = assertionCount,
            ["reviewCount"] = reviewCount,
            ["projectionCount"] = projectionCount,
            ["warningCount"] = warningCount,
            ["errorCount"] = errorCount,
            ["anomalyCount"] = anomalyCount
        };
        if (!string.IsNullOrWhiteSpace(graphId))
        {
            details["graphId"] = graphId;
        }

        if (!string.IsNullOrWhiteSpace(partitionKey))
        {
            details["partitionKey"] = partitionKey;
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

    private static int? CountGraphRecords(VyralGraphEnvelope? envelope)
    {
        return envelope is null
            ? null
            : 1 + envelope.Nodes.Count + envelope.Edges.Count + envelope.Assertions.Count + envelope.Reviews.Count + envelope.Projections.Count;
    }

    private static double CalculateProgress(int attempted, int requested)
    {
        return requested <= 0 ? 0 : Math.Clamp(attempted / (double)requested, 0, 1);
    }

    private sealed class ImportExecutionHandler : IExecutionHandler
    {
        private readonly IRecordCollectionStore _recordStore;

        public ImportExecutionHandler(IRecordCollectionStore recordStore)
        {
            _recordStore = recordStore;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
            ImportHandlerId,
            "Vyral graph import",
            handler => handler
                .WithPluginId(PluginId)
                .WithDescription("Imports a graph envelope into collection records.")
                .WithMaxAttempts(1)
                .WithTag("vyral.job", "graph")
                .WithTag("vyral.graph.kind", GraphJobKinds.Import));

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var payload = context.Run.Payload?.Deserialize<GraphImportPayload>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("Graph import payload is required.");
            var request = payload.Request ?? throw new InvalidOperationException("Graph import request is required.");
            var requested = CountGraphRecords(request.Envelope) ?? 0;
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = requested,
                Attempted = 0,
                Succeeded = 0,
                Failed = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(
                    GraphJobKinds.Import,
                    payload.Collection,
                    request.Envelope?.Scope?.GraphId,
                    request.Envelope?.Scope?.PartitionKey,
                    requested,
                    0,
                    0,
                    0,
                    recordCount: requested,
                    nodeCount: request.Envelope?.Nodes.Count ?? 0,
                    edgeCount: request.Envelope?.Edges.Count ?? 0,
                    assertionCount: request.Envelope?.Assertions.Count ?? 0,
                    reviewCount: request.Envelope?.Reviews.Count ?? 0,
                    projectionCount: request.Envelope?.Projections.Count ?? 0)
            }, ct);

            var result = await _recordStore.ImportGraphEnvelopeAsync(payload.Collection, request, ct);
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = result.RecordCount,
                Attempted = result.Records.Attempted,
                Succeeded = result.Records.Succeeded,
                Failed = result.Records.Failed,
                Progress = CalculateProgress(result.Records.Attempted, result.RecordCount),
                Result = JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                StatusDetails = BuildStatusDetails(
                    GraphJobKinds.Import,
                    payload.Collection,
                    result.GraphId,
                    result.PartitionKey,
                    result.RecordCount,
                    result.Records.Attempted,
                    result.Records.Succeeded,
                    result.Records.Failed,
                    recordCount: result.RecordCount,
                    nodeCount: result.NodeCount,
                    edgeCount: result.EdgeCount,
                    assertionCount: result.AssertionCount,
                    reviewCount: result.ReviewCount,
                    projectionCount: result.ProjectionCount)
            }, ct);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "graph-import-result",
                Kind = ExecutionArtifactKinds.Json,
                Content = JsonSerializer.SerializeToNode(result, ExecutionJson.Options)
            }, ct);

            return ExecutionRunResult.Succeeded(
                JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                BuildStatusDetails(
                    GraphJobKinds.Import,
                    payload.Collection,
                    result.GraphId,
                    result.PartitionKey,
                    result.RecordCount,
                    result.Records.Attempted,
                    result.Records.Succeeded,
                    result.Records.Failed,
                    recordCount: result.RecordCount,
                    nodeCount: result.NodeCount,
                    edgeCount: result.EdgeCount,
                    assertionCount: result.AssertionCount,
                    reviewCount: result.ReviewCount,
                    projectionCount: result.ProjectionCount));
        }
    }

    private sealed class InspectionExecutionHandler : IExecutionHandler
    {
        private readonly IRecordCollectionStore _recordStore;

        public InspectionExecutionHandler(IRecordCollectionStore recordStore)
        {
            _recordStore = recordStore;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
            InspectionHandlerId,
            "Vyral graph inspection",
            handler => handler
                .WithPluginId(PluginId)
                .WithDescription("Inspects graph collection records.")
                .WithMaxAttempts(1)
                .WithTag("vyral.job", "graph")
                .WithTag("vyral.graph.kind", GraphJobKinds.Inspect));

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var payload = context.Run.Payload?.Deserialize<GraphInspectionPayload>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("Graph inspection payload is required.");
            var request = payload.Request ?? throw new InvalidOperationException("Graph inspection request is required.");
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 0,
                Succeeded = 0,
                Failed = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(
                    GraphJobKinds.Inspect,
                    payload.Collection,
                    request.GraphId,
                    request.PartitionKey,
                    1,
                    0,
                    0,
                    0)
            }, ct);

            var result = await _recordStore.InspectGraphAsync(payload.Collection, request, ct);
            if (result is null)
            {
                return ExecutionRunResult.Failed("not_found", $"Graph collection '{payload.Collection}' was not found.");
            }

            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 1,
                Succeeded = 1,
                Failed = 0,
                Progress = 1,
                Result = JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                StatusDetails = BuildStatusDetails(
                    GraphJobKinds.Inspect,
                    payload.Collection,
                    result.GraphId,
                    result.PartitionKey,
                    1,
                    1,
                    1,
                    0,
                    recordCount: result.RecordCount,
                    nodeCount: result.NodeCount,
                    edgeCount: result.EdgeCount,
                    assertionCount: result.AssertionCount,
                    reviewCount: result.ReviewCount,
                    projectionCount: result.ProjectionCount,
                    warningCount: result.WarningCount,
                    anomalyCount: result.AnomalyCount)
            }, ct);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "graph-inspection-result",
                Kind = ExecutionArtifactKinds.Json,
                Content = JsonSerializer.SerializeToNode(result, ExecutionJson.Options)
            }, ct);

            return ExecutionRunResult.Succeeded(
                JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                BuildStatusDetails(
                    GraphJobKinds.Inspect,
                    payload.Collection,
                    result.GraphId,
                    result.PartitionKey,
                    1,
                    1,
                    1,
                    0,
                    recordCount: result.RecordCount,
                    nodeCount: result.NodeCount,
                    edgeCount: result.EdgeCount,
                    assertionCount: result.AssertionCount,
                    reviewCount: result.ReviewCount,
                    projectionCount: result.ProjectionCount,
                    warningCount: result.WarningCount,
                    anomalyCount: result.AnomalyCount));
        }
    }

    private sealed class DoctorExecutionHandler : IExecutionHandler
    {
        private readonly IRecordCollectionStore _recordStore;

        public DoctorExecutionHandler(IRecordCollectionStore recordStore)
        {
            _recordStore = recordStore;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
            DoctorHandlerId,
            "Vyral graph doctor",
            handler => handler
                .WithPluginId(PluginId)
                .WithDescription("Evaluates graph readiness diagnostics.")
                .WithMaxAttempts(1)
                .WithTag("vyral.job", "graph")
                .WithTag("vyral.graph.kind", GraphJobKinds.Doctor));

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var payload = context.Run.Payload?.Deserialize<GraphDoctorPayload>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("Graph doctor payload is required.");
            var request = payload.Request ?? throw new InvalidOperationException("Graph doctor request is required.");
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 0,
                Succeeded = 0,
                Failed = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(
                    GraphJobKinds.Doctor,
                    payload.Collection,
                    request.GraphId,
                    request.PartitionKey,
                    1,
                    0,
                    0,
                    0)
            }, ct);

            var result = await _recordStore.DoctorGraphAsync(payload.Collection, request, ct);
            if (result is null)
            {
                return ExecutionRunResult.Failed("not_found", $"Graph collection '{payload.Collection}' was not found.");
            }

            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 1,
                Succeeded = 1,
                Failed = 0,
                Progress = 1,
                Result = JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                StatusDetails = BuildStatusDetails(
                    GraphJobKinds.Doctor,
                    payload.Collection,
                    result.Inspection?.GraphId,
                    result.Inspection?.PartitionKey,
                    1,
                    1,
                    1,
                    0,
                    recordCount: result.GraphRecordCount,
                    nodeCount: result.GraphNodeCount,
                    edgeCount: result.GraphEdgeCount,
                    assertionCount: result.Inspection?.AssertionCount ?? 0,
                    reviewCount: result.Inspection?.ReviewCount ?? 0,
                    projectionCount: result.Inspection?.ProjectionCount ?? 0,
                    warningCount: result.Inspection?.WarningCount ?? 0,
                    anomalyCount: result.Inspection?.AnomalyCount ?? 0)
            }, ct);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "graph-doctor-result",
                Kind = ExecutionArtifactKinds.Json,
                Content = JsonSerializer.SerializeToNode(result, ExecutionJson.Options)
            }, ct);

            return ExecutionRunResult.Succeeded(
                JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                BuildStatusDetails(
                    GraphJobKinds.Doctor,
                    payload.Collection,
                    result.Inspection?.GraphId,
                    result.Inspection?.PartitionKey,
                    1,
                    1,
                    1,
                    0,
                    recordCount: result.GraphRecordCount,
                    nodeCount: result.GraphNodeCount,
                    edgeCount: result.GraphEdgeCount,
                    assertionCount: result.Inspection?.AssertionCount ?? 0,
                    reviewCount: result.Inspection?.ReviewCount ?? 0,
                    projectionCount: result.Inspection?.ProjectionCount ?? 0,
                    warningCount: result.Inspection?.WarningCount ?? 0,
                    anomalyCount: result.Inspection?.AnomalyCount ?? 0));
        }
    }

    private sealed class GraphExecutionPlugin : IExecutionPlugin
    {
        public GraphExecutionPlugin(IRecordCollectionStore recordStore)
        {
            Handlers =
            [
                new ImportExecutionHandler(recordStore),
                new InspectionExecutionHandler(recordStore),
                new DoctorExecutionHandler(recordStore)
            ];
            Descriptor = ExecutionDescriptors.Plugin(
                PluginId,
                "Vyral graph jobs",
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

    private sealed class GraphImportPayload
    {
        public string Collection { get; set; } = string.Empty;
        public VyralGraphCollectionImportRequest? Request { get; set; }
    }

    private sealed class GraphInspectionPayload
    {
        public string Collection { get; set; } = string.Empty;
        public VyralGraphCollectionInspectionRequest? Request { get; set; }
    }

    private sealed class GraphDoctorPayload
    {
        public string Collection { get; set; } = string.Empty;
        public VyralGraphDoctorRequest? Request { get; set; }
    }
}

public static class GraphJobKinds
{
    public const string Import = "import";
    public const string Inspect = "inspect";
    public const string Doctor = "doctor";
}

public static class GraphJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
}

public sealed class GraphJob
{
    public AdmissionReceipt Admission { get; set; } = new();
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = GraphJobKinds.Import;
    public string Status { get; set; } = GraphJobStatuses.Queued;
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
    public double Progress { get; set; }
    public string? GraphId { get; set; }
    public string? PartitionKey { get; set; }
    public int RecordCount { get; set; }
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public int AssertionCount { get; set; }
    public int ReviewCount { get; set; }
    public int ProjectionCount { get; set; }
    public int WarningCount { get; set; }
    public int ErrorCount { get; set; }
    public int AnomalyCount { get; set; }
    public string? FailureClass { get; set; }
    public string? Error { get; set; }
    public VyralGraphCollectionImportResult? ImportResult { get; set; }
    public VyralGraphCollectionInspectionResult? InspectionResult { get; set; }
    public VyralGraphDoctorResult? DoctorResult { get; set; }
}

public sealed class GraphJobOptions
{
    public int DefaultListLimit { get; init; } = 50;
    public int MaxListLimit { get; init; } = 200;
}
