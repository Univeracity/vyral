using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;

namespace Vyral.Mcp;

[McpServerToolType]
public sealed class VyralMcpTools
{
    [McpServerTool(Name = "vyral_list_graph_provider_shapes_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists the provider-neutral graph storage shapes understood by Vyral.")]
    public static IReadOnlyList<VyralGraphProviderShape> ListGraphProviderShapes() =>
        VyralGraphProviderShapeCatalog.All;

    [McpServerTool(Name = "vyral_get_graph_provider_shape_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Gets one provider-neutral graph storage shape by provider id.")]
    public static VyralGraphProviderShape GetGraphProviderShape(
        [Description("Stable graph provider id.")] string providerId) =>
        VyralGraphProviderShapeCatalog.TryGet(providerId, out var shape)
            ? shape
            : throw new McpException($"Graph provider shape '{providerId}' was not found.");

    [McpServerTool(Name = "vyral_list_collections_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists record collections visible to the authenticated caller.")]
    public static async Task<IReadOnlyList<string>> ListCollections(
        IRecordCollectionStore records,
        CancellationToken cancellationToken) =>
        (await records.GetCollectionsAsync(cancellationToken)).Order(StringComparer.Ordinal).ToList();

    [McpServerTool(Name = "vyral_get_collection_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Gets a record collection policy.")]
    public static async Task<RecordCollectionPolicy> GetCollection(
        [Description("Collection name.")] string collection,
        IRecordCollectionStore records,
        CancellationToken cancellationToken) =>
        await records.GetCollectionPolicyAsync(collection, cancellationToken)
            ?? throw new McpException($"Collection '{collection}' was not found.");

    [McpServerTool(Name = "vyral_inspect_collection_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Inspects a collection and returns bounded policy and anomaly diagnostics.")]
    public static Task<CollectionInspectionResult> InspectCollection(
        [Description("Collection name.")] string collection,
        [Description("Include bounded anomaly details.")] bool includeAnomalies,
        [Description("Maximum number of anomalies to return (1-500).")] int anomalyLimit,
        ICollectionInspectionService inspections,
        CancellationToken cancellationToken)
    {
        if (anomalyLimit is < 1 or > 500)
            throw new McpException("anomalyLimit must be between 1 and 500.");
        return inspections.InspectAsync(collection, new CollectionInspectionRequest
        {
            IncludeAnomalies = includeAnomalies,
            AnomalyLimit = anomalyLimit
        }, cancellationToken);
    }

    [McpServerTool(Name = "vyral_get_record_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Gets one record by collection, partition key, and id.")]
    public static async Task<VyralRecord> GetRecord(
        [Description("Collection name.")] string collection,
        [Description("Record partition key.")] string partitionKey,
        [Description("Record id.")] string id,
        IRecordCollectionStore records,
        CancellationToken cancellationToken) =>
        await records.GetRecordAsync(collection, partitionKey, id, cancellationToken)
            ?? throw new McpException("Record was not found.");

    [McpServerTool(Name = "vyral_query_records_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Runs a bounded structured query against one record collection.")]
    public static Task<RecordQueryResult> QueryRecords(
        [Description("Collection name.")] string collection,
        [Description("Bounded query envelope.")] QueryEnvelope query,
        IRecordCollectionStore records,
        CancellationToken cancellationToken) =>
        records.QueryRecordsPageAsync(collection, query, cancellationToken);

    [McpServerTool(Name = "vyral_search_records_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Runs a bounded lexical/vector search against one record collection.")]
    public static Task<RecordSearchResult> SearchRecords(
        [Description("Collection name.")] string collection,
        [Description("Bounded search envelope.")] QueryEnvelope query,
        IRecordCollectionStore records,
        CancellationToken cancellationToken) =>
        records.SearchRecordsPageAsync(collection, query, cancellationToken);

    [McpServerTool(Name = "vyral_list_retrieval_profiles_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists the built-in retrieval profiles and their defaults.")]
    public static IReadOnlyList<RetrievalProfileDescriptor> ListRetrievalProfiles() =>
        RetrievalProfileCatalog.GetProfiles();

    [McpServerTool(Name = "vyral_retrieve_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Runs bounded provider-neutral retrieval across one or more collections.")]
    public static Task<RetrievalResultEnvelope> Retrieve(
        [Description("Retrieval request.")] RetrievalRequest request,
        IRetrievalService retrieval,
        CancellationToken cancellationToken) =>
        retrieval.SearchAsync(request, cancellationToken);

    [McpServerTool(Name = "vyral_build_rag_context_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Builds a bounded, citation-aware RAG context from Vyral retrieval.")]
    public static Task<RagContextEnvelope> BuildRagContext(
        [Description("RAG context request.")] RagContextRequest request,
        IRagContextService contexts,
        CancellationToken cancellationToken) =>
        contexts.BuildContextAsync(request, cancellationToken);

    [McpServerTool(Name = "vyral_build_rag_prompt_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Builds a bounded prompt from a citation-aware Vyral RAG context.")]
    public static Task<RagPromptEnvelope> BuildRagPrompt(
        [Description("RAG prompt request.")] RagPromptRequest request,
        IRagPromptService prompts,
        CancellationToken cancellationToken) =>
        prompts.BuildPromptAsync(request, cancellationToken);

    [McpServerTool(Name = "vyral_traverse_graph_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Traverses a bounded Vyral graph collection from explicit start nodes.")]
    public static async Task<VyralGraphTraversalResult> TraverseGraph(
        [Description("Collection name.")] string collection,
        [Description("Bounded graph traversal request.")] VyralGraphTraversalRequest request,
        IRecordCollectionStore records,
        CancellationToken cancellationToken) =>
        await records.TraverseGraphAsync(collection, request, cancellationToken)
            ?? throw new McpException($"Collection '{collection}' was not found.");

    [McpServerTool(Name = "vyral_inspect_graph_collection_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Inspects graph integrity and statistics for a bounded collection slice.")]
    public static async Task<VyralGraphCollectionInspectionResult> InspectGraphCollection(
        [Description("Collection name.")] string collection,
        [Description("Bounded graph inspection request.")] VyralGraphCollectionInspectionRequest request,
        IRecordCollectionStore records,
        CancellationToken cancellationToken) =>
        await records.InspectGraphAsync(collection, request, cancellationToken)
            ?? throw new McpException($"Collection '{collection}' was not found.");

    [McpServerTool(Name = "vyral_doctor_graph_collection_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Produces bounded graph repair guidance without mutating the collection.")]
    public static async Task<VyralGraphDoctorResult> DoctorGraphCollection(
        [Description("Collection name.")] string collection,
        [Description("Bounded graph doctor request.")] VyralGraphDoctorRequest request,
        IRecordCollectionStore records,
        CancellationToken cancellationToken) =>
        await records.DoctorGraphAsync(collection, request, cancellationToken)
            ?? throw new McpException($"Collection '{collection}' was not found.");

    [McpServerTool(Name = "vyral_get_execution_run_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Gets one durable execution run after applying the same run policy as REST.")]
    public static async Task<ExecutionRun> GetExecutionRun(
        [Description("Durable execution run id.")] string runId,
        [Description("Include the run result when present.")] bool includeResult,
        IExecutionRuntime runtime,
        IVyralMcpExecutionAuthorizer authorizer,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        var run = await runtime.GetRunAsync(runId, includeResult, cancellationToken);
        await authorizer.AuthorizeRunAsync(GetHttpContext(contextAccessor), run, VyralMcpExecutionOperations.ReadRun, cancellationToken);
        return run ?? throw new McpException($"Execution run '{runId}' was not found.");
    }

    [McpServerTool(Name = "vyral_get_execution_run_history_v1", ReadOnly = true, UseStructuredContent = true)]
    [Description("Gets bounded durable history for one execution run after applying the same run policy as REST.")]
    public static async Task<IReadOnlyList<ExecutionTraceEvent>> GetExecutionRunHistory(
        [Description("Durable execution run id.")] string runId,
        [Description("Maximum history entries to return.")] int? limit,
        IExecutionRuntime runtime,
        IVyralMcpExecutionAuthorizer authorizer,
        VyralMcpRequestContextAccessor contextAccessor,
        CancellationToken cancellationToken)
    {
        var run = await runtime.GetRunAsync(runId, false, cancellationToken);
        await authorizer.AuthorizeRunAsync(GetHttpContext(contextAccessor), run, VyralMcpExecutionOperations.ReadRun, cancellationToken);
        if (run is null) throw new McpException($"Execution run '{runId}' was not found.");
        return await runtime.GetHistoryAsync(runId, new ExecutionHistoryQuery { Limit = limit }, cancellationToken);
    }

    private static Microsoft.AspNetCore.Http.HttpContext GetHttpContext(VyralMcpRequestContextAccessor accessor) =>
        accessor.Current ?? throw new McpException("The MCP request context is unavailable.");
}
