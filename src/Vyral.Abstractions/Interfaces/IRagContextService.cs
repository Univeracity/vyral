using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public interface IRagContextService
{
    Task<RagContextEnvelope> BuildContextAsync(RagContextRequest request, CancellationToken ct = default);

    Task<RagContextEvaluationResult> EvaluateContextAsync(RagContextEvaluationRequest request, CancellationToken ct = default);
}

public class RagContextRequest
{
    [JsonPropertyName("retrieval")]
    public RetrievalRequest Retrieval { get; set; } = new();

    [JsonPropertyName("contentField")]
    public string ContentField { get; set; } = "text";

    [JsonPropertyName("maxChars")]
    public int MaxChars { get; set; } = 8000;

    /// <summary>
    /// Maximum characters to include from any single retrieved chunk before assembly.
    /// Applied per-chunk before MaxChars enforcement. Setting MaxCharsPerChunk = 1200 and
    /// MaxChars = 8000 yields up to ~6–7 chunks at default sizes. Raising MaxCharsPerChunk
    /// allows longer individual excerpts without changing the total budget.
    /// </summary>
    [JsonPropertyName("maxCharsPerChunk")]
    public int MaxCharsPerChunk { get; set; } = 1200;

    /// <summary>
    /// Optional cap on source citations emitted for each returned chunk. When omitted, all
    /// source references are cited. Use this to keep prompt-ready context compact when
    /// records carry many source references.
    /// </summary>
    [JsonPropertyName("maxCitationsPerChunk")]
    public int? MaxCitationsPerChunk { get; set; }

    [JsonPropertyName("contextAssembly")]
    public RagContextAssemblyOptions? ContextAssembly { get; set; }

    [JsonPropertyName("graphExpansion")]
    public RagContextGraphExpansionOptions? GraphExpansion { get; set; }

    [JsonPropertyName("includeRecords")]
    public bool IncludeRecords { get; set; }

    [JsonPropertyName("includeCitations")]
    public bool IncludeCitations { get; set; } = true;

    [JsonPropertyName("includeContextText")]
    public bool IncludeContextText { get; set; }

    /// <summary>
    /// When true, includes debug trace in RagContextEnvelope.Trace and propagates to the
    /// retrieval layer (equivalent to also setting Retrieval.IncludeTrace = true). Setting
    /// only Retrieval.IncludeTrace has the same effect via OR-propagation in the service.
    /// </summary>
    [JsonPropertyName("includeTrace")]
    public bool IncludeTrace { get; set; }

}

public class RagContextEvaluationRequest
{
    [JsonPropertyName("cases")]
    public List<RagContextEvaluationCase> Cases { get; set; } = new();

    [JsonPropertyName("continueOnError")]
    public bool ContinueOnError { get; set; } = true;

    [JsonPropertyName("includeContext")]
    public bool IncludeContext { get; set; }
}

public class RagContextEvaluationCase
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("request")]
    public RagContextRequest Request { get; set; } = new();

    [JsonPropertyName("expectedGraph")]
    public RagContextExpectedGraph ExpectedGraph { get; set; } = new();

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }
}

public class RagContextExpectedGraph
{
    [JsonPropertyName("nodeIds")]
    public List<string> NodeIds { get; set; } = new();

    [JsonPropertyName("edgeIds")]
    public List<string> EdgeIds { get; set; } = new();

    [JsonPropertyName("provenanceEntityIds")]
    public List<string> ProvenanceEntityIds { get; set; } = new();

    [JsonPropertyName("requireSourceGroundedProvenance")]
    public bool RequireSourceGroundedProvenance { get; set; }

    [JsonPropertyName("requireGraphContextText")]
    public bool RequireGraphContextText { get; set; }

    [JsonPropertyName("requireContextTextNotTruncated")]
    public bool RequireContextTextNotTruncated { get; set; }
}

public class RagContextEvaluationResult
{
    [JsonPropertyName("requested")]
    public int Requested { get; set; }

    [JsonPropertyName("attempted")]
    public int Attempted { get; set; }

    [JsonPropertyName("succeeded")]
    public int Succeeded { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("stoppedOnError")]
    public bool StoppedOnError { get; set; }

    [JsonPropertyName("passedCount")]
    public int PassedCount { get; set; }

    [JsonPropertyName("passRate")]
    public double PassRate { get; set; }

    [JsonPropertyName("nodeHitRate")]
    public double NodeHitRate { get; set; }

    [JsonPropertyName("edgeHitRate")]
    public double EdgeHitRate { get; set; }

    [JsonPropertyName("provenanceHitRate")]
    public double ProvenanceHitRate { get; set; }

    [JsonPropertyName("failureCategoryCounts")]
    public Dictionary<string, int> FailureCategoryCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("limitReasonCounts")]
    public Dictionary<string, int> LimitReasonCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("cases")]
    public List<RagContextEvaluationCaseResult> Cases { get; set; } = new();
}

public class RagContextEvaluationCaseResult
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = RagContextEvaluationStatuses.Succeeded;

    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    [JsonPropertyName("durationMs")]
    public double DurationMs { get; set; }

    [JsonPropertyName("queryId")]
    public string? QueryId { get; set; }

    [JsonPropertyName("profileName")]
    public string? ProfileName { get; set; }

    [JsonPropertyName("expectedAnchorIds")]
    public List<string> ExpectedAnchorIds { get; set; } = new();

    [JsonPropertyName("retrievedRecordIds")]
    public List<string> RetrievedRecordIds { get; set; } = new();

    [JsonPropertyName("graphExpandedNodeIds")]
    public List<string> GraphExpandedNodeIds { get; set; } = new();

    [JsonPropertyName("graphExpandedEdgeIds")]
    public List<string> GraphExpandedEdgeIds { get; set; } = new();

    [JsonPropertyName("lexicalContributionCount")]
    public int LexicalContributionCount { get; set; }

    [JsonPropertyName("vectorContributionCount")]
    public int VectorContributionCount { get; set; }

    [JsonPropertyName("graphContributionCount")]
    public int GraphContributionCount { get; set; }

    [JsonPropertyName("failureCategories")]
    public List<string> FailureCategories { get; set; } = new();

    [JsonPropertyName("limitReasons")]
    public List<string> LimitReasons { get; set; } = new();

    [JsonPropertyName("graphContribution")]
    public RagContextGraphExpansionSummary? GraphContribution { get; set; }

    [JsonPropertyName("graph")]
    public RagContextGraphEvaluationResult Graph { get; set; } = new();

    [JsonPropertyName("context")]
    public RagContextEnvelope? Context { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class RagContextGraphEvaluationResult
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("expectedNodeCount")]
    public int ExpectedNodeCount { get; set; }

    [JsonPropertyName("matchedNodeCount")]
    public int MatchedNodeCount { get; set; }

    [JsonPropertyName("missingNodeIds")]
    public List<string> MissingNodeIds { get; set; } = new();

    [JsonPropertyName("expectedEdgeCount")]
    public int ExpectedEdgeCount { get; set; }

    [JsonPropertyName("matchedEdgeCount")]
    public int MatchedEdgeCount { get; set; }

    [JsonPropertyName("missingEdgeIds")]
    public List<string> MissingEdgeIds { get; set; } = new();

    [JsonPropertyName("expectedProvenanceCount")]
    public int ExpectedProvenanceCount { get; set; }

    [JsonPropertyName("matchedProvenanceCount")]
    public int MatchedProvenanceCount { get; set; }

    [JsonPropertyName("missingProvenanceEntityIds")]
    public List<string> MissingProvenanceEntityIds { get; set; } = new();

    [JsonPropertyName("sourceGroundedProvenanceCount")]
    public int SourceGroundedProvenanceCount { get; set; }

    [JsonPropertyName("sourceGroundingSatisfied")]
    public bool SourceGroundingSatisfied { get; set; } = true;

    [JsonPropertyName("graphContextTextPresent")]
    public bool GraphContextTextPresent { get; set; }

    [JsonPropertyName("contextTextTruncated")]
    public bool ContextTextTruncated { get; set; }

    [JsonPropertyName("budgetTruncated")]
    public bool BudgetTruncated { get; set; }

    [JsonPropertyName("failureModes")]
    public RagContextGraphEvaluationFailureModes FailureModes { get; set; } = new();

    [JsonPropertyName("failureCategories")]
    public List<string> FailureCategories { get; set; } = new();

    [JsonPropertyName("passed")]
    public bool Passed { get; set; }
}

public class RagContextGraphEvaluationFailureModes
{
    [JsonPropertyName("retrievalMiss")]
    public bool RetrievalMiss { get; set; }

    [JsonPropertyName("seedMiss")]
    public bool SeedMiss { get; set; }

    [JsonPropertyName("graphNotFound")]
    public bool GraphNotFound { get; set; }

    [JsonPropertyName("traversalEmpty")]
    public bool TraversalEmpty { get; set; }

    [JsonPropertyName("expectedNodeMissing")]
    public bool ExpectedNodeMissing { get; set; }

    [JsonPropertyName("expectedEdgeMissing")]
    public bool ExpectedEdgeMissing { get; set; }

    [JsonPropertyName("expectedProvenanceMissing")]
    public bool ExpectedProvenanceMissing { get; set; }

    [JsonPropertyName("sourceGroundingFailed")]
    public bool SourceGroundingFailed { get; set; }

    [JsonPropertyName("graphContextTextMissing")]
    public bool GraphContextTextMissing { get; set; }

    [JsonPropertyName("contextTextTruncated")]
    public bool ContextTextTruncated { get; set; }

    [JsonPropertyName("budgetTruncated")]
    public bool BudgetTruncated { get; set; }
}

public static class RagContextEvaluationStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public class RagContextGraphExpansionOptions
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("graphId")]
    public string? GraphId { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    [JsonPropertyName("seedNodeIds")]
    public List<string> SeedNodeIds { get; set; } = new();

    [JsonPropertyName("seedJsonPointers")]
    public List<string> SeedJsonPointers { get; set; } = new()
    {
        "/metadata/graphNodeId",
        "/metadata/nodeId",
        "/metadata/graphNodeIds",
        "/id"
    };

    [JsonPropertyName("maxSeedNodes")]
    public int MaxSeedNodes { get; set; } = 16;

    [JsonPropertyName("profile")]
    public VyralGraphTraversalProfile Profile { get; set; } = new();

    [JsonPropertyName("maxRecords")]
    public int? MaxRecords { get; set; }

    [JsonPropertyName("allowPartialGraph")]
    public bool AllowPartialGraph { get; set; }

    [JsonPropertyName("includeGraphContextText")]
    public bool IncludeGraphContextText { get; set; } = true;

    [JsonPropertyName("maxGraphContextChars")]
    public int MaxGraphContextChars { get; set; } = 1200;

    [JsonPropertyName("includeGraphProvenance")]
    public bool IncludeGraphProvenance { get; set; } = true;

    [JsonPropertyName("maxGraphProvenanceItems")]
    public int MaxGraphProvenanceItems { get; set; } = 64;

    [JsonPropertyName("fallbackOnFailure")]
    public bool FallbackOnFailure { get; set; } = true;
}

public class RagContextAssemblyOptions
{
    /// <summary>
    /// How to group retrieved chunks. Valid values are defined in <see cref="ContextGroupByModes"/>.
    /// When null and GroupByPath is set, the service infers <see cref="ContextGroupByModes.JsonPointer"/> automatically.
    /// </summary>
    [JsonPropertyName("groupBy")]
    public string? GroupBy { get; set; }

    /// <summary>
    /// JSON pointer (e.g. "/metadata/source") or metadata key (e.g. "source" when GroupBy is "metadata")
    /// identifying the field whose value becomes each chunk's group key.
    /// When this is set and GroupBy is null, the service infers <see cref="ContextGroupByModes.JsonPointer"/> automatically.
    /// Valid GroupBy values are defined in <see cref="ContextGroupByModes"/>.
    /// </summary>
    [JsonPropertyName("groupByPath")]
    public string? GroupByPath { get; set; }

    [JsonPropertyName("defaultMaxChunksPerGroup")]
    public int? DefaultMaxChunksPerGroup { get; set; }

    [JsonPropertyName("defaultMaxCharsPerGroup")]
    public int? DefaultMaxCharsPerGroup { get; set; }

    [JsonPropertyName("failOnUnsatisfiedRequiredGroups")]
    public bool FailOnUnsatisfiedRequiredGroups { get; set; }

    [JsonPropertyName("groups")]
    public List<RagContextGroupBudget> Groups { get; set; } = new();
}

public class RagContextGroupBudget
{
    /// <summary>
    /// Identity of the group this budget applies to. Interpretation depends on GroupBy:
    /// "collection" → collection name; "sourceKind" → source kind value;
    /// "metadata" → the metadata field value; "jsonPointer" → the resolved pointer value.
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public int? Priority { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("minChunks")]
    public int? MinChunks { get; set; }

    [JsonPropertyName("maxChunks")]
    public int? MaxChunks { get; set; }

    [JsonPropertyName("minChars")]
    public int? MinChars { get; set; }

    [JsonPropertyName("maxChars")]
    public int? MaxChars { get; set; }
}

public class RagContextEnvelope
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("chunks")]
    public List<RagContextChunk> Chunks { get; set; } = new();

    [JsonPropertyName("citations")]
    public List<RagContextCitation> Citations { get; set; } = new();

    [JsonPropertyName("totalChars")]
    public int TotalChars { get; set; }

    [JsonPropertyName("omittedCitationCount")]
    public int OmittedCitationCount { get; set; }

    [JsonPropertyName("contextText")]
    public string? ContextText { get; set; }

    /// <summary>Format of ContextText. Valid values are defined in <see cref="ContextTextFormats"/>. Null when IncludeContextText = false.</summary>
    [JsonPropertyName("contextTextFormat")]
    public string? ContextTextFormat { get; set; }

    [JsonPropertyName("contextTextHash")]
    public string? ContextTextHash { get; set; }

    [JsonPropertyName("graphContext")]
    public RagContextGraphContext? GraphContext { get; set; }

    [JsonPropertyName("graphExpansion")]
    public RagContextGraphExpansionSummary? GraphExpansion { get; set; }

    [JsonPropertyName("trace")]
    public JsonObject? Trace { get; set; }
}

public class RagContextGraphExpansionSummary
{
    [JsonPropertyName("expansionAttempted")]
    public bool ExpansionAttempted { get; set; }

    [JsonPropertyName("expansionEnabled")]
    public bool ExpansionEnabled { get; set; }

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("graphId")]
    public string? GraphId { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = RagContextGraphExpansionStatuses.NotRequested;

    [JsonPropertyName("skippedReason")]
    public string? SkippedReason { get; set; }

    [JsonPropertyName("profileId")]
    public string? ProfileId { get; set; }

    [JsonPropertyName("maxDepth")]
    public int MaxDepth { get; set; }

    [JsonPropertyName("nodeLimit")]
    public int NodeLimit { get; set; }

    [JsonPropertyName("edgeLimit")]
    public int EdgeLimit { get; set; }

    [JsonPropertyName("maxRecords")]
    public int? MaxRecords { get; set; }

    [JsonPropertyName("maxGraphContextChars")]
    public int MaxGraphContextChars { get; set; }

    [JsonPropertyName("maxGraphProvenanceItems")]
    public int MaxGraphProvenanceItems { get; set; }

    [JsonPropertyName("retrievedRecordIds")]
    public List<string> RetrievedRecordIds { get; set; } = new();

    [JsonPropertyName("sourceRecordIdsTouched")]
    public List<string> SourceRecordIdsTouched { get; set; } = new();

    [JsonPropertyName("seedJsonPointers")]
    public List<string> SeedJsonPointers { get; set; } = new();

    [JsonPropertyName("seedCandidateCount")]
    public int SeedCandidateCount { get; set; }

    [JsonPropertyName("seedCount")]
    public int SeedCount { get; set; }

    [JsonPropertyName("seedNodeIds")]
    public List<string> SeedNodeIds { get; set; } = new();

    [JsonPropertyName("droppedSeedCount")]
    public int DroppedSeedCount { get; set; }

    [JsonPropertyName("nodesAdded")]
    public int NodesAdded { get; set; }

    [JsonPropertyName("edgesAdded")]
    public int EdgesAdded { get; set; }

    [JsonPropertyName("relationshipsAdded")]
    public int RelationshipsAdded { get; set; }

    [JsonPropertyName("sourceRecordCount")]
    public int SourceRecordCount { get; set; }

    [JsonPropertyName("sourceTruncated")]
    public bool SourceTruncated { get; set; }

    [JsonPropertyName("exportedRecordCount")]
    public int ExportedRecordCount { get; set; }

    [JsonPropertyName("estimatedRequiredRecordCount")]
    public int? EstimatedRequiredRecordCount { get; set; }

    [JsonPropertyName("limitsHit")]
    public List<string> LimitsHit { get; set; } = new();

    [JsonPropertyName("groundingStatus")]
    public string GroundingStatus { get; set; } = "unknown";

    [JsonPropertyName("graphContextInfluencedContextText")]
    public bool GraphContextInfluencedContextText { get; set; }

    [JsonPropertyName("contextTextTruncated")]
    public bool ContextTextTruncated { get; set; }

    [JsonPropertyName("omittedProvenanceCount")]
    public int OmittedProvenanceCount { get; set; }
}

public class RagContextGraphContext
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = RagContextGraphExpansionStatuses.NotRequested;

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("graphId")]
    public string? GraphId { get; set; }

    [JsonPropertyName("seedNodeIds")]
    public List<string> SeedNodeIds { get; set; } = new();

    [JsonPropertyName("seedCandidateCount")]
    public int SeedCandidateCount { get; set; }

    [JsonPropertyName("seedJsonPointers")]
    public List<string> SeedJsonPointers { get; set; } = new();

    [JsonPropertyName("seedDiagnostics")]
    public List<RagContextGraphSeedDiagnostic> SeedDiagnostics { get; set; } = new();

    [JsonPropertyName("omittedSeedDiagnosticCount")]
    public int OmittedSeedDiagnosticCount { get; set; }

    [JsonPropertyName("droppedSeedCount")]
    public int DroppedSeedCount { get; set; }

    [JsonPropertyName("maxSeedNodes")]
    public int MaxSeedNodes { get; set; }

    [JsonPropertyName("requestedMaxRecords")]
    public int? RequestedMaxRecords { get; set; }

    [JsonPropertyName("exportedRecordCount")]
    public int ExportedRecordCount { get; set; }

    [JsonPropertyName("estimatedRequiredRecordCount")]
    public int? EstimatedRequiredRecordCount { get; set; }

    [JsonPropertyName("sourceContinuationToken")]
    public string? SourceContinuationToken { get; set; }

    [JsonPropertyName("limitsHit")]
    public List<string> LimitsHit { get; set; } = new();

    [JsonPropertyName("projection")]
    public VyralGraphProjection? Projection { get; set; }

    [JsonPropertyName("nodeCount")]
    public int NodeCount { get; set; }

    [JsonPropertyName("edgeCount")]
    public int EdgeCount { get; set; }

    [JsonPropertyName("sourceRecordCount")]
    public int SourceRecordCount { get; set; }

    [JsonPropertyName("sourceTruncated")]
    public bool SourceTruncated { get; set; }

    [JsonPropertyName("contextText")]
    public string? ContextText { get; set; }

    [JsonPropertyName("contextTextHash")]
    public string? ContextTextHash { get; set; }

    [JsonPropertyName("contextTextChars")]
    public int ContextTextChars { get; set; }

    [JsonPropertyName("contextTextTruncated")]
    public bool ContextTextTruncated { get; set; }

    [JsonPropertyName("provenance")]
    public List<RagContextGraphProvenance> Provenance { get; set; } = new();

    [JsonPropertyName("omittedProvenanceCount")]
    public int OmittedProvenanceCount { get; set; }

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }
}

public class RagContextGraphSeedDiagnostic
{
    [JsonPropertyName("recordId")]
    public string? RecordId { get; set; }

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    [JsonPropertyName("pointer")]
    public string Pointer { get; set; } = string.Empty;

    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("rawValue")]
    public string? RawValue { get; set; }

    [JsonPropertyName("normalizedValue")]
    public string? NormalizedValue { get; set; }

    [JsonPropertyName("skippedReason")]
    public string? SkippedReason { get; set; }

    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }
}

public class RagContextGraphProvenance
{
    [JsonPropertyName("entityKind")]
    public string EntityKind { get; set; } = string.Empty;

    [JsonPropertyName("entityId")]
    public string EntityId { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("nodeType")]
    public string? NodeType { get; set; }

    [JsonPropertyName("predicate")]
    public string? Predicate { get; set; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    [JsonPropertyName("sourceSpans")]
    public List<VyralGraphSourceSpan> SourceSpans { get; set; } = new();

    [JsonPropertyName("assertionIds")]
    public List<string> AssertionIds { get; set; } = new();
}

public static class RagContextGraphExpansionStatuses
{
    public const string NotRequested = "not_requested";
    public const string Succeeded = "succeeded";
    public const string NoSeeds = "no_seeds";
    public const string GraphNotFound = "graph_not_found";
    public const string BudgetTruncated = "budget_truncated";
    public const string Failed = "failed";
}

public class RagContextChunk
{
    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("score")]
    public float Score { get; set; }

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("contentField")]
    public string ContentField { get; set; } = string.Empty;

    [JsonPropertyName("groupKey")]
    public string? GroupKey { get; set; }

    [JsonPropertyName("charStart")]
    public int CharStart { get; set; }

    [JsonPropertyName("charEnd")]
    public int CharEnd { get; set; }

    [JsonPropertyName("originalTextLength")]
    public int OriginalTextLength { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("contextExcerptHash")]
    public string ContextExcerptHash { get; set; } = string.Empty;

    [JsonPropertyName("citationIds")]
    public List<string> CitationIds { get; set; } = new();

    [JsonPropertyName("retrievalDiagnostics")]
    public RetrievalDiagnostics? RetrievalDiagnostics { get; set; }

    [JsonPropertyName("retrievalMatch")]
    public RagContextRetrievalMatch? RetrievalMatch { get; set; }

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }

    [JsonPropertyName("sources")]
    public List<VyralSourceReference>? Sources { get; set; }

    [JsonPropertyName("record")]
    public VyralRecord? Record { get; set; }
}

public static class ContextTextFormats
{
    /// <summary>
    /// Markdown with inline citation markers ([1], [2], …) referencing the Citations list.
    /// Each chunk's text is included verbatim; citations provide source attribution.
    /// </summary>
    public const string CitationMarkdown = "citation-markdown";
}

public static class ContextGroupByModes
{
    /// <summary>Group by the collection the chunk came from.</summary>
    public const string Collection = "collection";
    /// <summary>Group by the source kind of the chunk's first source reference.</summary>
    public const string SourceKind = "sourceKind";
    /// <summary>Group by a metadata field value; use GroupByPath to name the field.</summary>
    public const string Metadata = "metadata";
    /// <summary>Group by the value at an arbitrary JSON pointer; use GroupByPath to supply the pointer.</summary>
    public const string JsonPointer = "jsonPointer";
}

public class RagContextCitation
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("chunkRank")]
    public int ChunkRank { get; set; }

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("recordId")]
    public string RecordId { get; set; } = string.Empty;

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("sourceKind")]
    public string? SourceKind { get; set; }

    [JsonPropertyName("sourceUri")]
    public string? SourceUri { get; set; }

    [JsonPropertyName("sourceLabel")]
    public string? SourceLabel { get; set; }

    [JsonPropertyName("sourceSpan")]
    public VyralSourceSpan? SourceSpan { get; set; }

    [JsonPropertyName("includedSourceSpan")]
    public VyralSourceSpan? IncludedSourceSpan { get; set; }

    [JsonPropertyName("contextCharStart")]
    public int ContextCharStart { get; set; }

    [JsonPropertyName("contextCharEnd")]
    public int ContextCharEnd { get; set; }

    [JsonPropertyName("contextExcerptHash")]
    public string ContextExcerptHash { get; set; } = string.Empty;
}

public class RagContextRetrievalMatch
{
    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("score")]
    public float Score { get; set; }

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    /// <summary>Search mode that produced this match. Valid values are defined in <see cref="SearchModes"/>.</summary>
    [JsonPropertyName("searchMode")]
    public string SearchMode { get; set; } = string.Empty;

    [JsonPropertyName("snippet")]
    public string? Snippet { get; set; }
}
