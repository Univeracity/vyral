using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public static class VyralGraphSchemaVersions
{
    public const string RomanGraphV1 = "roman.graph.v1";
}

public static class VyralGraphProviderShapeIds
{
    public const string LocalSqlite = "local-sqlite";
    public const string VyralCollection = "vyral-collection";
    public const string CosmosGremlin = "cosmos-gremlin";
    public const string Neptune = "neptune";
    public const string SpannerGraph = "spanner-graph";
}

public static class VyralGraphProviderKinds
{
    public const string LocalSqlite = "local_sqlite";
    public const string VyralCollection = "vyral_collection";
    public const string CosmosGremlin = "cosmos_gremlin";
    public const string Neptune = "neptune";
    public const string SpannerGraph = "spanner_graph";
}

public static class VyralGraphSubjectKinds
{
    public const string Node = "node";
    public const string Edge = "edge";
    public const string Assertion = "assertion";
    public const string Projection = "projection";
}

public static class VyralGraphTraversalDirections
{
    public const string Outgoing = "outgoing";
    public const string Incoming = "incoming";
    public const string Both = "both";
}

public static class VyralGraphAssertionStatuses
{
    public const string Proposed = "proposed";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Superseded = "superseded";
}

public static class VyralGraphReviewStatuses
{
    public const string Accepted = "accepted";
    public const string Approved = "approved";
    public const string Verified = "verified";
    public const string Rejected = "rejected";
    public const string Invalid = "invalid";
    public const string Superseded = "superseded";
}

public static class VyralGraphRecordTypes
{
    public const string Envelope = "graph.envelope";
    public const string Node = "graph.node";
    public const string Edge = "graph.edge";
    public const string Assertion = "graph.assertion";
    public const string Review = "graph.review";
    public const string Projection = "graph.projection";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Envelope,
        Node,
        Edge,
        Assertion,
        Review,
        Projection
    };
}

public static class VyralGraphMetadataKeys
{
    public const string GraphKind = "graphKind";
    public const string GraphId = "graphId";
    public const string Namespace = "namespace";
    public const string ScopeCollection = "scopeCollection";
    public const string TenantId = "tenantId";
    public const string GraphPartitionKey = "graphPartitionKey";
    public const string SubjectId = "subjectId";
    public const string SubjectKind = "subjectKind";
    public const string NodeId = "nodeId";
    public const string NodeType = "nodeType";
    public const string EdgeId = "edgeId";
    public const string SourceId = "sourceId";
    public const string TargetId = "targetId";
    public const string Predicate = "predicate";
    public const string AssertionId = "assertionId";
    public const string AssertionStatus = "assertionStatus";
    public const string ReviewId = "reviewId";
    public const string ReviewStatus = "reviewStatus";
    public const string ProjectionId = "projectionId";
}

public static class VyralGraphMetadataPaths
{
    public const string Type = "/type";
    public const string GraphKind = "/metadata/graphKind";
    public const string GraphId = "/metadata/graphId";
    public const string Namespace = "/metadata/namespace";
    public const string ScopeCollection = "/metadata/scopeCollection";
    public const string TenantId = "/metadata/tenantId";
    public const string GraphPartitionKey = "/metadata/graphPartitionKey";
    public const string SubjectId = "/metadata/subjectId";
    public const string SubjectKind = "/metadata/subjectKind";
    public const string NodeId = "/metadata/nodeId";
    public const string NodeType = "/metadata/nodeType";
    public const string EdgeId = "/metadata/edgeId";
    public const string SourceId = "/metadata/sourceId";
    public const string TargetId = "/metadata/targetId";
    public const string Predicate = "/metadata/predicate";
    public const string AssertionId = "/metadata/assertionId";
    public const string AssertionStatus = "/metadata/assertionStatus";
    public const string ReviewId = "/metadata/reviewId";
    public const string ReviewStatus = "/metadata/reviewStatus";
    public const string ProjectionId = "/metadata/projectionId";

    public static readonly IReadOnlyList<string> DefaultIndexed = new[]
    {
        Type,
        GraphKind,
        GraphId,
        Namespace,
        ScopeCollection,
        TenantId,
        GraphPartitionKey,
        SubjectId,
        SubjectKind,
        NodeId,
        NodeType,
        EdgeId,
        SourceId,
        TargetId,
        Predicate,
        AssertionId,
        AssertionStatus,
        ReviewId,
        ReviewStatus,
        ProjectionId
    };
}

public static class VyralGraphCollectionLimits
{
    public const int MaxRecords = CollectionSnapshotLimits.MaxRecords;
}

public static class VyralGraphImportPolicyStatuses
{
    public const string Created = "created";
    public const string ExistingGraphPolicy = "existing_graph_policy";
    public const string ExistingNonGraphPolicyAllowed = "existing_non_graph_policy_allowed";
    public const string Replaced = "replaced";
}

public class VyralGraphTraversalTruncatedException : InvalidOperationException
{
    public VyralGraphTraversalTruncatedException(
        string message,
        string collection,
        string? graphId,
        string? @namespace,
        string? tenantId,
        string? partitionKey,
        int requestedMaxRecords,
        int exportedRecordCount,
        string? continuationToken)
        : base(message)
    {
        Collection = collection;
        GraphId = graphId;
        Namespace = @namespace;
        TenantId = tenantId;
        PartitionKey = partitionKey;
        RequestedMaxRecords = requestedMaxRecords;
        ExportedRecordCount = exportedRecordCount;
        ContinuationToken = continuationToken;
    }

    public string Collection { get; }

    public string? GraphId { get; }

    public string? Namespace { get; }

    public string? TenantId { get; }

    public string? PartitionKey { get; }

    public int RequestedMaxRecords { get; }

    public int ExportedRecordCount { get; }

    public string? ContinuationToken { get; }

    public int EstimatedRequiredRecordCount => ExportedRecordCount + 1;
}

public class VyralGraphCollectionImportRequest
{
    [JsonPropertyName("envelope")]
    public VyralGraphEnvelope Envelope { get; set; } = new();

    [JsonPropertyName("createCollectionIfMissing")]
    public bool CreateCollectionIfMissing { get; set; } = true;

    [JsonPropertyName("replaceExisting")]
    public bool ReplaceExisting { get; set; }

    [JsonPropertyName("continueOnError")]
    public bool ContinueOnError { get; set; }

    [JsonPropertyName("allowNonGraphPolicy")]
    public bool AllowNonGraphPolicy { get; set; }
}

public class VyralGraphCollectionImportResult
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("graphId")]
    public string GraphId { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("policyStatus")]
    public string PolicyStatus { get; set; } = string.Empty;

    [JsonPropertyName("nodeCount")]
    public int NodeCount { get; set; }

    [JsonPropertyName("edgeCount")]
    public int EdgeCount { get; set; }

    [JsonPropertyName("assertionCount")]
    public int AssertionCount { get; set; }

    [JsonPropertyName("reviewCount")]
    public int ReviewCount { get; set; }

    [JsonPropertyName("projectionCount")]
    public int ProjectionCount { get; set; }

    [JsonPropertyName("recordCount")]
    public int RecordCount { get; set; }

    [JsonPropertyName("records")]
    public RecordBatchUpsertResult Records { get; set; } = new();
}

public class VyralGraphCollectionImportPreflightResult
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("readyToImport")]
    public bool ReadyToImport { get; set; }

    [JsonPropertyName("graphId")]
    public string GraphId { get; set; } = string.Empty;

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("collectionExists")]
    public bool CollectionExists { get; set; }

    [JsonPropertyName("collectionPolicyStatus")]
    public string CollectionPolicyStatus { get; set; } = string.Empty;

    [JsonPropertyName("wouldCreateCollection")]
    public bool WouldCreateCollection { get; set; }

    [JsonPropertyName("wouldReplaceCollection")]
    public bool WouldReplaceCollection { get; set; }

    [JsonPropertyName("wouldAllowNonGraphPolicy")]
    public bool WouldAllowNonGraphPolicy { get; set; }

    [JsonPropertyName("createCollectionIfMissing")]
    public bool CreateCollectionIfMissing { get; set; }

    [JsonPropertyName("replaceExisting")]
    public bool ReplaceExisting { get; set; }

    [JsonPropertyName("allowNonGraphPolicy")]
    public bool AllowNonGraphPolicy { get; set; }

    [JsonPropertyName("nodeCount")]
    public int NodeCount { get; set; }

    [JsonPropertyName("edgeCount")]
    public int EdgeCount { get; set; }

    [JsonPropertyName("assertionCount")]
    public int AssertionCount { get; set; }

    [JsonPropertyName("reviewCount")]
    public int ReviewCount { get; set; }

    [JsonPropertyName("projectionCount")]
    public int ProjectionCount { get; set; }

    [JsonPropertyName("recordCount")]
    public int RecordCount { get; set; }

    [JsonPropertyName("maxRecords")]
    public int MaxRecords { get; set; } = VyralGraphCollectionLimits.MaxRecords;

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("errorCount")]
    public int ErrorCount { get; set; }

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();
}

public class VyralGraphCollectionExportRequest
{
    [JsonPropertyName("graphId")]
    public string? GraphId { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    [JsonPropertyName("includeProjections")]
    public bool IncludeProjections { get; set; } = true;

    [JsonPropertyName("maxRecords")]
    public int? MaxRecords { get; set; }

    [JsonPropertyName("failOnLimitExceeded")]
    public bool FailOnLimitExceeded { get; set; } = true;
}

public class VyralGraphCollectionExportResult
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("envelope")]
    public VyralGraphEnvelope Envelope { get; set; } = new();

    [JsonPropertyName("recordCount")]
    public int RecordCount { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }

    [JsonPropertyName("exportedAt")]
    public DateTime? ExportedAt { get; set; }
}

public class VyralGraphTraversalRequest
{
    [JsonPropertyName("graphId")]
    public string? GraphId { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    [JsonPropertyName("startNodeIds")]
    public List<string> StartNodeIds { get; set; } = new();

    [JsonPropertyName("profile")]
    public VyralGraphTraversalProfile Profile { get; set; } = new();

    [JsonPropertyName("maxRecords")]
    public int? MaxRecords { get; set; }

    [JsonPropertyName("allowPartialGraph")]
    public bool AllowPartialGraph { get; set; }
}

public class VyralGraphTraversalResult
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("graphId")]
    public string GraphId { get; set; } = string.Empty;

    [JsonPropertyName("projection")]
    public VyralGraphProjection Projection { get; set; } = new();

    [JsonPropertyName("nodeCount")]
    public int NodeCount { get; set; }

    [JsonPropertyName("edgeCount")]
    public int EdgeCount { get; set; }

    [JsonPropertyName("sourceRecordCount")]
    public int SourceRecordCount { get; set; }

    [JsonPropertyName("sourceTruncated")]
    public bool SourceTruncated { get; set; }

    [JsonPropertyName("requestedMaxRecords")]
    public int RequestedMaxRecords { get; set; }

    [JsonPropertyName("exportedRecordCount")]
    public int ExportedRecordCount { get; set; }

    [JsonPropertyName("estimatedRequiredRecordCount")]
    public int? EstimatedRequiredRecordCount { get; set; }

    [JsonPropertyName("sourceContinuationToken")]
    public string? SourceContinuationToken { get; set; }
}

public class VyralGraphCollectionInspectionRequest
{
    [JsonPropertyName("graphId")]
    public string? GraphId { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    [JsonPropertyName("maxRecords")]
    public int? MaxRecords { get; set; }

    [JsonPropertyName("allowPartialGraph")]
    public bool AllowPartialGraph { get; set; }

    [JsonPropertyName("includeAnomalies")]
    public bool IncludeAnomalies { get; set; } = true;

    [JsonPropertyName("anomalyLimit")]
    public int AnomalyLimit { get; set; } = 50;
}

public class VyralGraphCollectionInspectionResult
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("graphId")]
    public string GraphId { get; set; } = string.Empty;

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("recordCount")]
    public int RecordCount { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }

    [JsonPropertyName("traversalReady")]
    public bool TraversalReady { get; set; }

    [JsonPropertyName("nodeCount")]
    public int NodeCount { get; set; }

    [JsonPropertyName("edgeCount")]
    public int EdgeCount { get; set; }

    [JsonPropertyName("assertionCount")]
    public int AssertionCount { get; set; }

    [JsonPropertyName("reviewCount")]
    public int ReviewCount { get; set; }

    [JsonPropertyName("projectionCount")]
    public int ProjectionCount { get; set; }

    [JsonPropertyName("recordTypeCounts")]
    public Dictionary<string, int> RecordTypeCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("graphIdCounts")]
    public Dictionary<string, int> GraphIdCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("namespaceCounts")]
    public Dictionary<string, int> NamespaceCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("tenantIdCounts")]
    public Dictionary<string, int> TenantIdCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("partitionKeyCounts")]
    public Dictionary<string, int> PartitionKeyCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("nodeTypeCounts")]
    public Dictionary<string, int> NodeTypeCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("predicateCounts")]
    public Dictionary<string, int> PredicateCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("assertionStatusCounts")]
    public Dictionary<string, int> AssertionStatusCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("reviewStatusCounts")]
    public Dictionary<string, int> ReviewStatusCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("sourceGrounding")]
    public VyralGraphSourceGroundingInspection SourceGrounding { get; set; } = new();

    [JsonPropertyName("danglingEdgeCount")]
    public int DanglingEdgeCount { get; set; }

    [JsonPropertyName("orphanAssertionCount")]
    public int OrphanAssertionCount { get; set; }

    [JsonPropertyName("orphanReviewCount")]
    public int OrphanReviewCount { get; set; }

    [JsonPropertyName("danglingAssertionReferenceCount")]
    public int DanglingAssertionReferenceCount { get; set; }

    [JsonPropertyName("danglingProjectionStartNodeCount")]
    public int DanglingProjectionStartNodeCount { get; set; }

    [JsonPropertyName("duplicateNodeIdCount")]
    public int DuplicateNodeIdCount { get; set; }

    [JsonPropertyName("duplicateEdgeIdCount")]
    public int DuplicateEdgeIdCount { get; set; }

    [JsonPropertyName("duplicateAssertionIdCount")]
    public int DuplicateAssertionIdCount { get; set; }

    [JsonPropertyName("duplicateReviewIdCount")]
    public int DuplicateReviewIdCount { get; set; }

    [JsonPropertyName("duplicateProjectionIdCount")]
    public int DuplicateProjectionIdCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("anomalyCount")]
    public int AnomalyCount { get; set; }

    [JsonPropertyName("returnedAnomalyCount")]
    public int ReturnedAnomalyCount { get; set; }

    [JsonPropertyName("anomalies")]
    public List<VyralGraphInspectionAnomaly> Anomalies { get; set; } = new();
}

public class VyralGraphDoctorRequest
{
    [JsonPropertyName("graphId")]
    public string? GraphId { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    [JsonPropertyName("targetCollection")]
    public string? TargetCollection { get; set; }

    [JsonPropertyName("targetPartitionKeys")]
    public List<string> TargetPartitionKeys { get; set; } = new();

    [JsonPropertyName("seedJsonPointers")]
    public List<string> SeedJsonPointers { get; set; } = new()
    {
        "/metadata/graphNodeId",
        "/metadata/nodeId",
        "/metadata/graphNodeIds",
        "/id"
    };

    [JsonPropertyName("maxGraphRecords")]
    public int? MaxGraphRecords { get; set; }

    [JsonPropertyName("maxTargetRecords")]
    public int MaxTargetRecords { get; set; } = 1000;

    [JsonPropertyName("allowPartialGraph")]
    public bool AllowPartialGraph { get; set; }

    [JsonPropertyName("includeAnomalies")]
    public bool IncludeAnomalies { get; set; } = true;

    [JsonPropertyName("anomalyLimit")]
    public int AnomalyLimit { get; set; } = 50;
}

public class VyralGraphDoctorResult
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("failureMode")]
    public string? FailureMode { get; set; }

    [JsonPropertyName("graphReady")]
    public bool GraphReady { get; set; }

    [JsonPropertyName("graphRecordCount")]
    public int GraphRecordCount { get; set; }

    [JsonPropertyName("graphNodeCount")]
    public int GraphNodeCount { get; set; }

    [JsonPropertyName("graphEdgeCount")]
    public int GraphEdgeCount { get; set; }

    [JsonPropertyName("graphTruncated")]
    public bool GraphTruncated { get; set; }

    [JsonPropertyName("inspection")]
    public VyralGraphCollectionInspectionResult? Inspection { get; set; }

    [JsonPropertyName("seedCoverage")]
    public VyralGraphSeedCoverage? SeedCoverage { get; set; }

    [JsonPropertyName("recommendedActions")]
    public List<string> RecommendedActions { get; set; } = new();
}

public class VyralGraphSeedCoverage
{
    [JsonPropertyName("targetCollection")]
    public string TargetCollection { get; set; } = string.Empty;

    [JsonPropertyName("targetRecordCount")]
    public int TargetRecordCount { get; set; }

    [JsonPropertyName("targetTruncated")]
    public bool TargetTruncated { get; set; }

    [JsonPropertyName("seedJsonPointers")]
    public List<string> SeedJsonPointers { get; set; } = new();

    [JsonPropertyName("recordsWithSeedMetadataCount")]
    public int RecordsWithSeedMetadataCount { get; set; }

    [JsonPropertyName("seedValueCount")]
    public int SeedValueCount { get; set; }

    [JsonPropertyName("uniqueSeedValueCount")]
    public int UniqueSeedValueCount { get; set; }

    [JsonPropertyName("resolvedSeedNodeCount")]
    public int ResolvedSeedNodeCount { get; set; }

    [JsonPropertyName("unresolvedSeedNodeCount")]
    public int UnresolvedSeedNodeCount { get; set; }

    [JsonPropertyName("seedCoverage")]
    public double SeedCoverage { get; set; }

    [JsonPropertyName("resolvedSeedCoverage")]
    public double ResolvedSeedCoverage { get; set; }

    [JsonPropertyName("resolvedSeedNodeIds")]
    public List<string> ResolvedSeedNodeIds { get; set; } = new();

    [JsonPropertyName("unresolvedSeedNodeIds")]
    public List<string> UnresolvedSeedNodeIds { get; set; } = new();
}

public class VyralGraphSourceGroundingInspection
{
    [JsonPropertyName("nodeGroundedCount")]
    public int NodeGroundedCount { get; set; }

    [JsonPropertyName("nodeUngroundedCount")]
    public int NodeUngroundedCount { get; set; }

    [JsonPropertyName("nodeCoverage")]
    public double NodeCoverage { get; set; }

    [JsonPropertyName("edgeGroundedCount")]
    public int EdgeGroundedCount { get; set; }

    [JsonPropertyName("edgeUngroundedCount")]
    public int EdgeUngroundedCount { get; set; }

    [JsonPropertyName("edgeCoverage")]
    public double EdgeCoverage { get; set; }

    [JsonPropertyName("assertionGroundedCount")]
    public int AssertionGroundedCount { get; set; }

    [JsonPropertyName("assertionUngroundedCount")]
    public int AssertionUngroundedCount { get; set; }

    [JsonPropertyName("assertionCoverage")]
    public double AssertionCoverage { get; set; }
}

public class VyralGraphInspectionAnomaly
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("subjectId")]
    public string? SubjectId { get; set; }

    [JsonPropertyName("subjectKind")]
    public string? SubjectKind { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public Dictionary<string, object?> Details { get; set; } = new(StringComparer.Ordinal);
}

public class VyralGraphScope : IJsonOnDeserialized
{
    [JsonPropertyName("graphId")]
    public string GraphId { get; set; } = "default";

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = "default";

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = "default";

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public void OnDeserialized()
    {
        GraphId = VyralGraphJson.GetString(AdditionalProperties, "graph_id", GraphId);
        TenantId = VyralGraphJson.GetString(AdditionalProperties, "tenant_id", TenantId);
        PartitionKey = VyralGraphJson.GetString(AdditionalProperties, "partition_key", PartitionKey);
        VyralGraphJson.Remove(AdditionalProperties, "graph_id", "tenant_id", "partition_key");
    }
}

public class VyralGraphSourceSpan : IJsonOnDeserialized
{
    [JsonPropertyName("sourceRef")]
    public string SourceRef { get; set; } = string.Empty;

    [JsonPropertyName("charStart")]
    public int? CharStart { get; set; }

    [JsonPropertyName("charEnd")]
    public int? CharEnd { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "utf16";

    [JsonPropertyName("locator")]
    public string? Locator { get; set; }

    [JsonPropertyName("textHash")]
    public string? TextHash { get; set; }

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public void OnDeserialized()
    {
        SourceRef = VyralGraphJson.GetString(AdditionalProperties, "source_ref", SourceRef);
        CharStart ??= VyralGraphJson.GetInt(AdditionalProperties, "start");
        CharEnd ??= VyralGraphJson.GetInt(AdditionalProperties, "end");
        TextHash ??= VyralGraphJson.GetStringOrNull(AdditionalProperties, "text_hash");
        Unit = string.IsNullOrWhiteSpace(Unit) ? "utf16" : Unit;
        VyralGraphJson.Remove(AdditionalProperties, "source_ref", "start", "end", "text_hash");
    }
}

public class VyralGraphAssertion : IJsonOnDeserialized
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("subjectId")]
    public string SubjectId { get; set; } = string.Empty;

    [JsonPropertyName("subjectKind")]
    public string SubjectKind { get; set; } = VyralGraphSubjectKinds.Node;

    [JsonPropertyName("status")]
    public string Status { get; set; } = VyralGraphAssertionStatuses.Proposed;

    [JsonPropertyName("method")]
    public string Method { get; set; } = "unspecified";

    [JsonPropertyName("actor")]
    public string Actor { get; set; } = "system";

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("sourceSpans")]
    public List<VyralGraphSourceSpan> SourceSpans { get; set; } = new();

    [JsonPropertyName("properties")]
    public JsonObject? Properties { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public void OnDeserialized()
    {
        Id = VyralGraphJson.GetString(AdditionalProperties, "assertion_id", Id);
        SubjectId = VyralGraphJson.GetString(AdditionalProperties, "subject_id", SubjectId);
        SubjectKind = VyralGraphJson.GetString(AdditionalProperties, "subject_kind", SubjectKind);
        CreatedAt ??= VyralGraphJson.GetDateTime(AdditionalProperties, "created_at");
        SourceSpans = VyralGraphJson.GetList(AdditionalProperties, "source_spans", SourceSpans);
        VyralGraphJson.Remove(AdditionalProperties, "assertion_id", "subject_id", "subject_kind", "created_at", "source_spans");
    }
}

public class VyralGraphNode : IJsonOnDeserialized
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("properties")]
    public JsonObject? Properties { get; set; }

    [JsonPropertyName("sourceSpans")]
    public List<VyralGraphSourceSpan> SourceSpans { get; set; } = new();

    [JsonPropertyName("assertionIds")]
    public List<string> AssertionIds { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public void OnDeserialized()
    {
        Id = VyralGraphJson.GetString(AdditionalProperties, "node_id", Id);
        Type = VyralGraphJson.GetString(AdditionalProperties, "node_type", Type);
        CreatedAt ??= VyralGraphJson.GetDateTime(AdditionalProperties, "created_at");
        UpdatedAt ??= VyralGraphJson.GetDateTime(AdditionalProperties, "updated_at");
        SourceSpans = VyralGraphJson.GetList(AdditionalProperties, "source_spans", SourceSpans);
        AssertionIds = VyralGraphJson.GetStringList(AdditionalProperties, "assertion_ids", AssertionIds);
        VyralGraphJson.Remove(AdditionalProperties, "node_id", "node_type", "created_at", "updated_at", "source_spans", "assertion_ids");
    }
}

public class VyralGraphEdge : IJsonOnDeserialized
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = string.Empty;

    [JsonPropertyName("predicate")]
    public string Predicate { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("properties")]
    public JsonObject? Properties { get; set; }

    [JsonPropertyName("sourceSpans")]
    public List<VyralGraphSourceSpan> SourceSpans { get; set; } = new();

    [JsonPropertyName("assertionIds")]
    public List<string> AssertionIds { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public void OnDeserialized()
    {
        Id = VyralGraphJson.GetString(AdditionalProperties, "edge_id", Id);
        SourceId = VyralGraphJson.GetString(AdditionalProperties, "source_id", SourceId);
        TargetId = VyralGraphJson.GetString(AdditionalProperties, "target_id", TargetId);
        CreatedAt ??= VyralGraphJson.GetDateTime(AdditionalProperties, "created_at");
        UpdatedAt ??= VyralGraphJson.GetDateTime(AdditionalProperties, "updated_at");
        SourceSpans = VyralGraphJson.GetList(AdditionalProperties, "source_spans", SourceSpans);
        AssertionIds = VyralGraphJson.GetStringList(AdditionalProperties, "assertion_ids", AssertionIds);
        VyralGraphJson.Remove(AdditionalProperties, "edge_id", "source_id", "target_id", "created_at", "updated_at", "source_spans", "assertion_ids");
    }
}

public class VyralGraphReviewEvent : IJsonOnDeserialized
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("subjectId")]
    public string SubjectId { get; set; } = string.Empty;

    [JsonPropertyName("subjectKind")]
    public string SubjectKind { get; set; } = VyralGraphSubjectKinds.Assertion;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("reviewer")]
    public string Reviewer { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("properties")]
    public JsonObject? Properties { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public void OnDeserialized()
    {
        Id = VyralGraphJson.GetString(AdditionalProperties, "review_id", Id);
        SubjectId = VyralGraphJson.GetString(AdditionalProperties, "subject_id", SubjectId);
        SubjectKind = VyralGraphJson.GetString(AdditionalProperties, "subject_kind", SubjectKind);
        CreatedAt ??= VyralGraphJson.GetDateTime(AdditionalProperties, "created_at");
        VyralGraphJson.Remove(AdditionalProperties, "review_id", "subject_id", "subject_kind", "created_at");
    }
}

public class VyralGraphTraversalProfile : IJsonOnDeserialized
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "default";

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = VyralGraphTraversalDirections.Both;

    [JsonPropertyName("maxDepth")]
    public int MaxDepth { get; set; } = 1;

    [JsonPropertyName("predicates")]
    public List<string> Predicates { get; set; } = new();

    [JsonPropertyName("nodeTypes")]
    public List<string> NodeTypes { get; set; } = new();

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 100;

    [JsonPropertyName("edgeLimit")]
    public int EdgeLimit { get; set; } = 100;

    [JsonPropertyName("includeStart")]
    public bool IncludeStart { get; set; } = true;

    [JsonPropertyName("reviewStatuses")]
    public List<string> ReviewStatuses { get; set; } = new();

    [JsonPropertyName("assertionStatuses")]
    public List<string> AssertionStatuses { get; set; } = new();

    [JsonPropertyName("requireSourceGrounding")]
    public bool RequireSourceGrounding { get; set; }

    [JsonPropertyName("minScore")]
    public double? MinScore { get; set; }

    [JsonPropertyName("includePathExplanations")]
    public bool IncludePathExplanations { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public void OnDeserialized()
    {
        MaxDepth = VyralGraphJson.GetInt(AdditionalProperties, "max_depth") ?? MaxDepth;
        EdgeLimit = VyralGraphJson.GetInt(AdditionalProperties, "edge_limit") ?? EdgeLimit;
        IncludeStart = VyralGraphJson.GetBool(AdditionalProperties, "include_start") ?? IncludeStart;
        RequireSourceGrounding = VyralGraphJson.GetBool(AdditionalProperties, "require_source_grounding") ?? RequireSourceGrounding;
        MinScore ??= VyralGraphJson.GetDouble(AdditionalProperties, "min_score");
        IncludePathExplanations = VyralGraphJson.GetBool(AdditionalProperties, "include_path_explanations") ?? IncludePathExplanations;
        NodeTypes = VyralGraphJson.GetStringList(AdditionalProperties, "node_types", NodeTypes);
        ReviewStatuses = VyralGraphJson.GetStringList(AdditionalProperties, "review_statuses", ReviewStatuses);
        AssertionStatuses = VyralGraphJson.GetStringList(AdditionalProperties, "assertion_statuses", AssertionStatuses);
        VyralGraphJson.Remove(
            AdditionalProperties,
            "max_depth",
            "edge_limit",
            "include_start",
            "require_source_grounding",
            "min_score",
            "include_path_explanations",
            "node_types",
            "review_statuses",
            "assertion_statuses");
    }
}

public class VyralGraphProjection : IJsonOnDeserialized
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("profile")]
    public VyralGraphTraversalProfile Profile { get; set; } = new();

    [JsonPropertyName("startNodeIds")]
    public List<string> StartNodeIds { get; set; } = new();

    [JsonPropertyName("nodes")]
    public List<VyralGraphNode> Nodes { get; set; } = new();

    [JsonPropertyName("edges")]
    public List<VyralGraphEdge> Edges { get; set; } = new();

    [JsonPropertyName("diagnostics")]
    public JsonObject? Diagnostics { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public void OnDeserialized()
    {
        Id = VyralGraphJson.GetString(AdditionalProperties, "projection_id", Id);
        CreatedAt ??= VyralGraphJson.GetDateTime(AdditionalProperties, "created_at");
        StartNodeIds = VyralGraphJson.GetStringList(AdditionalProperties, "start_node_ids", StartNodeIds);
        VyralGraphJson.Remove(AdditionalProperties, "projection_id", "created_at", "start_node_ids");
    }
}

public class VyralGraphEnvelope
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = VyralGraphSchemaVersions.RomanGraphV1;

    [JsonPropertyName("scope")]
    public VyralGraphScope Scope { get; set; } = new();

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }

    [JsonPropertyName("nodes")]
    public List<VyralGraphNode> Nodes { get; set; } = new();

    [JsonPropertyName("edges")]
    public List<VyralGraphEdge> Edges { get; set; } = new();

    [JsonPropertyName("assertions")]
    public List<VyralGraphAssertion> Assertions { get; set; } = new();

    [JsonPropertyName("reviews")]
    public List<VyralGraphReviewEvent> Reviews { get; set; } = new();

    [JsonPropertyName("projections")]
    public List<VyralGraphProjection> Projections { get; set; } = new();
}

public class VyralGraphProviderShape : IJsonOnDeserialized
{
    [JsonPropertyName("id")]
    public string Id
    {
        get => ProviderId;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                ProviderId = value;
            }
        }
    }

    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("graphIdField")]
    public string GraphIdField { get; set; } = "graphId";

    [JsonPropertyName("nodeIdField")]
    public string NodeIdField { get; set; } = "id";

    [JsonPropertyName("edgeIdField")]
    public string EdgeIdField { get; set; } = "id";

    [JsonPropertyName("sourceField")]
    public string SourceField { get; set; } = "sourceId";

    [JsonPropertyName("targetField")]
    public string TargetField { get; set; } = "targetId";

    [JsonPropertyName("partitionField")]
    public string PartitionField { get; set; } = "partitionKey";

    [JsonPropertyName("tenantField")]
    public string TenantField { get; set; } = "tenantId";

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();

    [JsonPropertyName("limitations")]
    public List<string> Limitations { get; set; } = new();

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public void OnDeserialized()
    {
        ProviderId = VyralGraphJson.GetString(AdditionalProperties, "provider_id", ProviderId);
        GraphIdField = VyralGraphJson.GetString(AdditionalProperties, "graph_id_field", GraphIdField);
        NodeIdField = VyralGraphJson.GetString(AdditionalProperties, "node_id_field", NodeIdField);
        EdgeIdField = VyralGraphJson.GetString(AdditionalProperties, "edge_id_field", EdgeIdField);
        SourceField = VyralGraphJson.GetString(AdditionalProperties, "source_field", SourceField);
        TargetField = VyralGraphJson.GetString(AdditionalProperties, "target_field", TargetField);
        PartitionField = VyralGraphJson.GetString(AdditionalProperties, "partition_field", PartitionField);
        TenantField = VyralGraphJson.GetString(AdditionalProperties, "tenant_field", TenantField);
        VyralGraphJson.Remove(
            AdditionalProperties,
            "provider_id",
            "graph_id_field",
            "node_id_field",
            "edge_id_field",
            "source_field",
            "target_field",
            "partition_field",
            "tenant_field");
    }
}

public static class VyralGraphProviderShapeCatalog
{
    private static readonly IReadOnlyDictionary<string, VyralGraphProviderShape> ShapesById =
        new Dictionary<string, VyralGraphProviderShape>(StringComparer.Ordinal)
        {
            [VyralGraphProviderShapeIds.LocalSqlite] = new()
            {
                ProviderId = VyralGraphProviderShapeIds.LocalSqlite,
                Kind = VyralGraphProviderKinds.LocalSqlite,
                Capabilities = new List<string> { "append_only_reviews", "bounded_traversal", "local_import_export" },
                Limitations = new List<string> { "single_process_sqlite_writer", "no_native_distributed_partitioning" }
            },
            [VyralGraphProviderShapeIds.VyralCollection] = new()
            {
                ProviderId = VyralGraphProviderShapeIds.VyralCollection,
                Kind = VyralGraphProviderKinds.VyralCollection,
                Capabilities = new List<string> { "collection_scoped_objects", "hybrid_retrieval_join", "provider_trace_join" },
                Limitations = new List<string> { "graph_traversal_may_be_adapter_level" }
            },
            [VyralGraphProviderShapeIds.CosmosGremlin] = new()
            {
                ProviderId = VyralGraphProviderShapeIds.CosmosGremlin,
                Kind = VyralGraphProviderKinds.CosmosGremlin,
                Capabilities = new List<string> { "partitioned_graph", "remote_traversal" },
                Limitations = new List<string> { "provider_specific_query_language", "requires_partition_strategy" }
            },
            [VyralGraphProviderShapeIds.Neptune] = new()
            {
                ProviderId = VyralGraphProviderShapeIds.Neptune,
                Kind = VyralGraphProviderKinds.Neptune,
                Capabilities = new List<string> { "managed_graph", "remote_traversal" },
                Limitations = new List<string> { "provider_specific_operations", "external_service_dependency" }
            },
            [VyralGraphProviderShapeIds.SpannerGraph] = new()
            {
                ProviderId = VyralGraphProviderShapeIds.SpannerGraph,
                Kind = VyralGraphProviderKinds.SpannerGraph,
                Capabilities = new List<string> { "relational_graph_mapping", "distributed_sql_substrate" },
                Limitations = new List<string> { "requires_schema_mapping", "external_service_dependency" }
            }
        };

    public static IReadOnlyList<VyralGraphProviderShape> All => ShapesById.Values.Select(Clone).ToList();

    public static bool TryGet(string providerId, out VyralGraphProviderShape shape)
    {
        if (ShapesById.TryGetValue(providerId, out var existing))
        {
            shape = Clone(existing);
            return true;
        }

        shape = null!;
        return false;
    }

    public static VyralGraphProviderShape Get(string providerId)
    {
        return TryGet(providerId, out var shape)
            ? Clone(shape)
            : throw new InvalidOperationException($"Unknown graph provider shape: {providerId}.");
    }

    private static VyralGraphProviderShape Clone(VyralGraphProviderShape shape)
    {
        return new VyralGraphProviderShape
        {
            ProviderId = shape.ProviderId,
            Kind = shape.Kind,
            GraphIdField = shape.GraphIdField,
            NodeIdField = shape.NodeIdField,
            EdgeIdField = shape.EdgeIdField,
            SourceField = shape.SourceField,
            TargetField = shape.TargetField,
            PartitionField = shape.PartitionField,
            TenantField = shape.TenantField,
            Capabilities = shape.Capabilities.ToList(),
            Limitations = shape.Limitations.ToList(),
            Metadata = shape.Metadata?.DeepClone().AsObject()
        };
    }
}

internal static class VyralGraphJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string GetString(Dictionary<string, JsonElement>? values, string key, string current)
    {
        var value = GetStringOrNull(values, key);
        return string.IsNullOrWhiteSpace(value) ? current : value;
    }

    public static string? GetStringOrNull(Dictionary<string, JsonElement>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    public static int? GetInt(Dictionary<string, JsonElement>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;
    }

    public static bool? GetBool(Dictionary<string, JsonElement>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    public static double? GetDouble(Dictionary<string, JsonElement>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out number) ? number : null;
    }

    public static DateTime? GetDateTime(Dictionary<string, JsonElement>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public static List<string> GetStringList(Dictionary<string, JsonElement>? values, string key, List<string> current)
    {
        if (values is null || !values.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return current;
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();
    }

    public static List<T> GetList<T>(Dictionary<string, JsonElement>? values, string key, List<T> current)
    {
        if (values is null || !values.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return current;
        }

        return value.Deserialize<List<T>>(Options) ?? current;
    }

    public static void Remove(Dictionary<string, JsonElement>? values, params string[] keys)
    {
        if (values is null)
        {
            return;
        }

        foreach (var key in keys)
        {
            values.Remove(key);
        }
    }
}
