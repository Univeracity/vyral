using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Mcp;
using Vyral.Primitives;
using Vyral.Providers.Abstractions;

namespace Vyral.Server;

/// <summary>
/// Opt-in MCP write tools. Each tool starts work through the same durable adapters as REST and
/// returns the underlying execution run so the MCP task store can follow it across instances.
/// </summary>
[McpServerToolType]
public sealed class VyralMcpTaskTools
{
    [McpServerTool(Name = "vyral_start_execution_run_v1", Destructive = true, UseStructuredContent = true)]
    [Description("Starts an explicitly configured durable Vyral execution handler.")]
    public static async Task<ExecutionRun> StartExecutionRun(
        [Description("Durable execution request.")] ExecutionRunRequest request,
        IExecutionRuntime runtime,
        IVyralMcpExecutionAuthorizer authorizer,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        BindIdempotencyKey(contextAccessor, request);
        await authorizer.BindStartRunAsync(GetHttpContext(contextAccessor), request, cancellationToken);
        var run = await runtime.StartRunAsync(request, cancellationToken);
        ExecutionAdmission.Attach(
            run,
            VyralAdmissionOperations.StartExecutionRun,
            $"/execution/runs/{run.Id}");
        return run;
    }

    [McpServerTool(Name = "vyral_start_embedding_job_v1", Destructive = false, UseStructuredContent = true)]
    [Description("Starts a durable embedding job.")]
    public static async Task<ExecutionRun> StartEmbeddingJob(
        [Description("Embedding request.")] EmbeddingRequest request,
        ExecutionRuntimeEmbeddingJobAdapter jobs,
        IExecutionRuntime runtime,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        var job = await jobs.StartAsync(request, GetIdempotencyKey(contextAccessor), cancellationToken);
        return await GetRunAsync(runtime, job.Admission, cancellationToken);
    }

    [McpServerTool(Name = "vyral_start_provider_run_job_v1", Destructive = false, UseStructuredContent = true)]
    [Description("Starts a durable guarded provider run job.")]
    public static async Task<ExecutionRun> StartProviderRunJob(
        [Description("Provider id.")] string provider,
        [Description("Provider run request.")] ProviderRunRequest request,
        ProviderTargetRegistry registry,
        IConfiguration configuration,
        ExecutionRuntimeProviderRunJobAdapter jobs,
        IExecutionRuntime runtime,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        if (registry.GetTarget(provider) is null)
            throw new McpException($"Provider '{provider}' was not found.");
        request.Provider = provider;
        request.ArtifactDirectory = EmptyToNull(configuration["Providers:ArtifactDirectory"]);
        var job = await jobs.StartAsync(
            provider,
            request,
            request.ArtifactDirectory,
            GetIdempotencyKey(contextAccessor),
            VyralAdmissionOperations.StartProviderRunJob,
            cancellationToken);
        return await GetRunAsync(runtime, job.Admission, cancellationToken);
    }

    [McpServerTool(Name = "vyral_start_collection_import_job_v1", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Starts a durable collection snapshot import job.")]
    public static async Task<ExecutionRun> StartCollectionImportJob(
        [Description("Destination collection name.")] string collection,
        [Description("Collection import request.")] CollectionImportRequest request,
        ExecutionRuntimeRecordImportJobAdapter jobs,
        IExecutionRuntime runtime,
        IVyralMcpExecutionAuthorizer authorizer,
        VyralMcpRequestContextAccessor contextAccessor,
        [Description("Execution product id; provide together with tenantId on a shared execution plane.")] string? productId = null,
        [Description("Execution tenant id; provide together with productId on a shared execution plane.")] string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCollectionImportRequest(request);
        var runRequest = jobs.CreateCollectionImportRunRequest(
            collection,
            request,
            GetIdempotencyKey(contextAccessor),
            BuildExecutionScope(productId, tenantId));
        await authorizer.BindStartRunAsync(GetHttpContext(contextAccessor), runRequest, cancellationToken);
        var job = await jobs.StartRunAsync(runRequest, cancellationToken);
        return await GetRunAsync(runtime, job.Admission, cancellationToken);
    }

    [McpServerTool(Name = "vyral_start_record_batch_upsert_job_v1", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Starts a durable batch record upsert job.")]
    public static async Task<ExecutionRun> StartRecordBatchUpsertJob(
        [Description("Collection name.")] string collection,
        [Description("Batch upsert request.")] RecordBatchUpsertRequest request,
        ExecutionRuntimeRecordImportJobAdapter jobs,
        IExecutionRuntime runtime,
        IVyralMcpExecutionAuthorizer authorizer,
        VyralMcpRequestContextAccessor contextAccessor,
        [Description("Execution product id; provide together with tenantId on a shared execution plane.")] string? productId = null,
        [Description("Execution tenant id; provide together with productId on a shared execution plane.")] string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBatchUpsertRequest(request);
        var runRequest = jobs.CreateBatchUpsertRunRequest(
            collection,
            request,
            GetIdempotencyKey(contextAccessor),
            BuildExecutionScope(productId, tenantId));
        await authorizer.BindStartRunAsync(GetHttpContext(contextAccessor), runRequest, cancellationToken);
        var job = await jobs.StartRunAsync(runRequest, cancellationToken);
        return await GetRunAsync(runtime, job.Admission, cancellationToken);
    }

    [McpServerTool(Name = "vyral_start_rag_ingestion_text_job_v1", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Starts durable ingestion of one text into a RAG collection.")]
    public static async Task<ExecutionRun> StartRagIngestionTextJob(
        [Description("Collection name.")] string collection,
        [Description("RAG text ingestion request.")] RagIngestTextRequest request,
        ExecutionRuntimeRagIngestionJobAdapter jobs,
        IExecutionRuntime runtime,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        var job = await jobs.StartTextAsync(collection, request, GetIdempotencyKey(contextAccessor), cancellationToken);
        return await GetRunAsync(runtime, job.Admission, cancellationToken);
    }

    [McpServerTool(Name = "vyral_start_rag_ingestion_batch_job_v1", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Starts durable ingestion of a bounded text batch into a RAG collection.")]
    public static async Task<ExecutionRun> StartRagIngestionBatchJob(
        [Description("Collection name.")] string collection,
        [Description("RAG text batch ingestion request.")] RagIngestTextBatchRequest request,
        ExecutionRuntimeRagIngestionJobAdapter jobs,
        IExecutionRuntime runtime,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        var job = await jobs.StartBatchAsync(collection, request, GetIdempotencyKey(contextAccessor), cancellationToken);
        return await GetRunAsync(runtime, job.Admission, cancellationToken);
    }

    [McpServerTool(Name = "vyral_start_retrieval_evaluation_comparison_job_v1", Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Starts a durable retrieval evaluation comparison job.")]
    public static async Task<ExecutionRun> StartRetrievalEvaluationComparisonJob(
        [Description("Retrieval comparison request.")] RetrievalEvaluationComparisonRequest request,
        ExecutionRuntimeRetrievalEvaluationJobAdapter jobs,
        IExecutionRuntime runtime,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        var job = await jobs.StartComparisonAsync(request, GetIdempotencyKey(contextAccessor), cancellationToken);
        return await GetRunAsync(runtime, job.Admission, cancellationToken);
    }

    [McpServerTool(Name = "vyral_start_retrieval_evaluation_job_v1", Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Starts a durable retrieval evaluation job.")]
    public static async Task<ExecutionRun> StartRetrievalEvaluationJob(
        [Description("Retrieval evaluation request.")] RetrievalEvaluationRequest request,
        ExecutionRuntimeRetrievalEvaluationJobAdapter jobs,
        IExecutionRuntime runtime,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        var job = await jobs.StartEvaluationAsync(request, GetIdempotencyKey(contextAccessor), cancellationToken);
        return await GetRunAsync(runtime, job.Admission, cancellationToken);
    }

    [McpServerTool(Name = "vyral_start_graph_import_job_v1", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Starts a durable graph import job.")]
    public static async Task<ExecutionRun> StartGraphImportJob(
        [Description("Collection name.")] string collection,
        [Description("Graph import request.")] VyralGraphCollectionImportRequest request,
        ExecutionRuntimeGraphJobAdapter jobs,
        IExecutionRuntime runtime,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        ValidateGraphImportRequest(request);
        var job = await jobs.StartImportAsync(collection, request, GetIdempotencyKey(contextAccessor), cancellationToken);
        return await GetRunAsync(runtime, job.Admission, cancellationToken);
    }

    [McpServerTool(Name = "vyral_start_graph_inspection_job_v1", Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Starts a durable graph inspection job.")]
    public static async Task<ExecutionRun> StartGraphInspectionJob(
        [Description("Collection name.")] string collection,
        [Description("Graph inspection request.")] VyralGraphCollectionInspectionRequest request,
        ExecutionRuntimeGraphJobAdapter jobs,
        IExecutionRuntime runtime,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        var job = await jobs.StartInspectionAsync(collection, request, GetIdempotencyKey(contextAccessor), cancellationToken);
        return await GetRunAsync(runtime, job.Admission, cancellationToken);
    }

    [McpServerTool(Name = "vyral_start_graph_doctor_job_v1", Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Starts a durable graph doctor job.")]
    public static async Task<ExecutionRun> StartGraphDoctorJob(
        [Description("Collection name.")] string collection,
        [Description("Graph doctor request.")] VyralGraphDoctorRequest request,
        ExecutionRuntimeGraphJobAdapter jobs,
        IExecutionRuntime runtime,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        var job = await jobs.StartDoctorAsync(collection, request, GetIdempotencyKey(contextAccessor), cancellationToken);
        return await GetRunAsync(runtime, job.Admission, cancellationToken);
    }

    private static async Task<ExecutionRun> GetRunAsync(
        IExecutionRuntime runtime,
        AdmissionReceipt admission,
        CancellationToken cancellationToken)
    {
        var run = await runtime.GetRunAsync(admission.ResourceId, includeResult: true, cancellationToken)
            ?? throw new McpException($"Execution run '{admission.ResourceId}' was not found after it was admitted.");
        run.AdmissionReplayed = admission.Replayed;
        ExecutionAdmission.Attach(run, admission.OperationId, admission.StatusUri, admission.ResultUri);
        return run;
    }

    private static string? GetIdempotencyKey(VyralMcpRequestContextAccessor accessor)
    {
        var request = GetHttpContext(accessor).Request;
        var value = request.Headers["Idempotency-Key"].FirstOrDefault()
            ?? request.Headers["X-Idempotency-Key"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void BindIdempotencyKey(
        VyralMcpRequestContextAccessor accessor,
        ExecutionRunRequest request)
    {
        var headerKey = GetIdempotencyKey(accessor);
        var bodyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? null
            : request.IdempotencyKey.Trim();
        if (headerKey is not null && bodyKey is not null &&
            !string.Equals(headerKey, bodyKey, StringComparison.Ordinal))
        {
            throw new McpException(
                "Idempotency-Key must match request.idempotencyKey when both are supplied.");
        }

        request.IdempotencyKey = headerKey ?? bodyKey;
    }

    private static HttpContext GetHttpContext(VyralMcpRequestContextAccessor accessor) =>
        accessor.Current ?? throw new McpException("The MCP request context is unavailable.");

    private static ExecutionScope? BuildExecutionScope(string? productId, string? tenantId)
    {
        var normalizedProductId = EmptyToNull(productId);
        var normalizedTenantId = EmptyToNull(tenantId);
        if (normalizedProductId is null && normalizedTenantId is null) return null;
        if (normalizedProductId is null || normalizedTenantId is null)
            throw new McpException("Record import execution scope requires both productId and tenantId.");
        return new ExecutionScope { ProductId = normalizedProductId, TenantId = normalizedTenantId };
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateBatchUpsertRequest(RecordBatchUpsertRequest request)
    {
        const int maxBatchSize = 1000;
        if (request.Records.Count == 0)
            throw new McpException("Batch upsert request must include at least one record.");
        if (request.Records.Count > maxBatchSize)
            throw new McpException($"Batch upsert request supports at most {maxBatchSize} records.");
        request.ValidatePreconditionAlignment();
    }

    private static void ValidateCollectionImportRequest(CollectionImportRequest request)
    {
        if (request.Snapshot is null)
            throw new McpException("Collection import request must include a snapshot.");
        if (request.Snapshot.Records.Count > CollectionSnapshotLimits.MaxRecords)
            throw new McpException($"Collection import snapshot supports at most {CollectionSnapshotLimits.MaxRecords} records.");
    }

    private static void ValidateGraphImportRequest(VyralGraphCollectionImportRequest request)
    {
        if (request.Envelope is null)
            throw new McpException("Graph import request must include an envelope.");
        var recordCount = 1 +
            (request.Envelope.Nodes?.Count ?? 0) +
            (request.Envelope.Edges?.Count ?? 0) +
            (request.Envelope.Assertions?.Count ?? 0) +
            (request.Envelope.Reviews?.Count ?? 0) +
            (request.Envelope.Projections?.Count ?? 0);
        if (recordCount > VyralGraphCollectionLimits.MaxRecords)
            throw new McpException($"Graph import supports at most {VyralGraphCollectionLimits.MaxRecords} collection records.");
    }
}
