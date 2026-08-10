using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public interface IRagIngestionService
{
    Task<RagIngestTextResult> IngestTextAsync(string collection, RagIngestTextRequest request, CancellationToken ct = default);

    Task<RagIngestTextBatchResult> IngestTextBatchAsync(string collection, RagIngestTextBatchRequest request, CancellationToken ct = default);
}

public class RagIngestTextBatchRequest
{
    [JsonPropertyName("items")]
    public List<RagIngestTextRequest> Items { get; set; } = new();

    [JsonPropertyName("continueOnError")]
    public bool ContinueOnError { get; set; }
}

public class RagIngestTextBatchResult
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

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

    [JsonPropertyName("textLength")]
    public int TextLength { get; set; }

    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; set; }

    [JsonPropertyName("deletedStaleCount")]
    public int DeletedStaleCount { get; set; }

    [JsonPropertyName("createdCount")]
    public int CreatedCount { get; set; }

    [JsonPropertyName("updatedCount")]
    public int UpdatedCount { get; set; }

    [JsonPropertyName("reusedCount")]
    public int ReusedCount { get; set; }

    [JsonPropertyName("vectorGeneratedCount")]
    public int VectorGeneratedCount { get; set; }

    [JsonPropertyName("vectorReusedCount")]
    public int VectorReusedCount { get; set; }

    [JsonPropertyName("deduplicatedCount")]
    public int DeduplicatedCount { get; set; }

    [JsonPropertyName("items")]
    public List<RagIngestTextBatchItemResult> Items { get; set; } = new();
}

public class RagIngestTextBatchItemResult
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("documentId")]
    public string? DocumentId { get; set; }

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    /// <summary>Outcome of this item's ingestion. Valid values are defined in <see cref="RagIngestItemStatuses"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public RagIngestTextResult? Result { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class RagIngestTextRequest
{
    [JsonPropertyName("documentId")]
    public string? DocumentId { get; set; }

    [JsonPropertyName("idPrefix")]
    public string? IdPrefix { get; set; }

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = VyralRecordTypes.RagChunk;

    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("contentField")]
    public string ContentField { get; set; } = "text";

    [JsonPropertyName("embedding")]
    public EmbeddingOptions? Embedding { get; set; }

    /// <summary>
    /// Caller-supplied key/value attributes attached to every chunk record produced from this request.
    /// Shorthand source fields (SourceUri, SourceKind, SourceId, SourceLabel) are ignored when
    /// Sources is non-empty; otherwise they are coalesced into a single VyralSourceReference entry.
    /// Use Sources directly for multi-source documents.
    /// </summary>
    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }

    [JsonPropertyName("sourceUri")]
    public string? SourceUri { get; set; }

    [JsonPropertyName("sourceKind")]
    public string? SourceKind { get; set; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("sourceLabel")]
    public string? SourceLabel { get; set; }

    [JsonPropertyName("sources")]
    public List<VyralSourceReference>? Sources { get; set; }

    [JsonPropertyName("options")]
    public RagIngestionOptions? Options { get; set; }
}

public class RagIngestionOptions
{
    [JsonPropertyName("chunkChars")]
    public int ChunkChars { get; set; } = 1200;

    [JsonPropertyName("chunkOverlapChars")]
    public int ChunkOverlapChars { get; set; } = 150;

    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("replaceDocumentChunks")]
    public bool ReplaceDocumentChunks { get; set; }

    [JsonPropertyName("skipUnchangedChunks")]
    public bool SkipUnchangedChunks { get; set; }

    [JsonPropertyName("reuseExistingChunkVectors")]
    public bool ReuseExistingChunkVectors { get; set; }

    /// <summary>
    /// Scope for vector reuse lookup. Only consulted when ReuseExistingChunkVectors = true.
    /// Valid values are defined in <see cref="VectorReuseScopes"/>.
    /// </summary>
    [JsonPropertyName("vectorReuseScope")]
    public string VectorReuseScope { get; set; } = VectorReuseScopes.Partition;

    [JsonPropertyName("deduplicateExistingChunks")]
    public bool DeduplicateExistingChunks { get; set; }

    /// <summary>
    /// Scope for chunk deduplication. Only consulted when DeduplicateExistingChunks = true.
    /// Valid values are defined in <see cref="ChunkDedupeScopes"/>.
    /// </summary>
    [JsonPropertyName("chunkDedupeScope")]
    public string ChunkDedupeScope { get; set; } = ChunkDedupeScopes.Partition;

    [JsonPropertyName("persistManifest")]
    public bool PersistManifest { get; set; }

    [JsonPropertyName("manifestId")]
    public string? ManifestId { get; set; }

    [JsonPropertyName("expectedPlanHash")]
    public string? ExpectedPlanHash { get; set; }

    [JsonPropertyName("expectedManifestHash")]
    public string? ExpectedManifestHash { get; set; }

    [JsonPropertyName("includeTrace")]
    public bool IncludeTrace { get; set; }
}

public class RagIngestTextResult
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("documentId")]
    public string DocumentId { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("embeddingField")]
    public string EmbeddingField { get; set; } = string.Empty;

    [JsonPropertyName("embeddingProvider")]
    public string EmbeddingProvider { get; set; } = string.Empty;

    [JsonPropertyName("embeddingModel")]
    public string EmbeddingModel { get; set; } = string.Empty;

    [JsonPropertyName("embeddingPurpose")]
    public string EmbeddingPurpose { get; set; } = string.Empty;

    [JsonPropertyName("dimensions")]
    public int Dimensions { get; set; }

    [JsonPropertyName("textLength")]
    public int TextLength { get; set; }

    [JsonPropertyName("textHash")]
    public string TextHash { get; set; } = string.Empty;

    [JsonPropertyName("planHash")]
    public string PlanHash { get; set; } = string.Empty;

    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; set; }

    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("deletedStaleCount")]
    public int DeletedStaleCount { get; set; }

    [JsonPropertyName("createdCount")]
    public int CreatedCount { get; set; }

    [JsonPropertyName("updatedCount")]
    public int UpdatedCount { get; set; }

    [JsonPropertyName("reusedCount")]
    public int ReusedCount { get; set; }

    [JsonPropertyName("vectorGeneratedCount")]
    public int VectorGeneratedCount { get; set; }

    [JsonPropertyName("vectorReusedCount")]
    public int VectorReusedCount { get; set; }

    [JsonPropertyName("deduplicatedCount")]
    public int DeduplicatedCount { get; set; }

    [JsonPropertyName("manifestId")]
    public string? ManifestId { get; set; }

    [JsonPropertyName("manifestHash")]
    public string? ManifestHash { get; set; }

    /// <summary>What happened to the manifest record. Valid values are defined in <see cref="RagIngestChunkActions"/>. Null when PersistManifest = false.</summary>
    [JsonPropertyName("manifestAction")]
    public string? ManifestAction { get; set; }

    [JsonPropertyName("manifestEtag")]
    public string? ManifestEtag { get; set; }

    [JsonPropertyName("manifestRevision")]
    public int? ManifestRevision { get; set; }

    [JsonPropertyName("actionSummary")]
    public RagIngestActionSummary ActionSummary { get; set; } = new();

    [JsonPropertyName("planHashComparison")]
    public RagIngestHashComparison PlanHashComparison { get; set; } = new();

    [JsonPropertyName("manifestHashComparison")]
    public RagIngestHashComparison ManifestHashComparison { get; set; } = new();

    [JsonPropertyName("staleDeletes")]
    public List<RagIngestStaleDeleteResult> StaleDeletes { get; set; } = new();

    [JsonPropertyName("chunks")]
    public List<RagIngestChunkResult> Chunks { get; set; } = new();

    [JsonPropertyName("trace")]
    public JsonObject? Trace { get; set; }
}

public class RagIngestActionSummary
{
    [JsonPropertyName("actionCounts")]
    public Dictionary<string, int> ActionCounts { get; set; } = new();

    [JsonPropertyName("embeddingActionCounts")]
    public Dictionary<string, int> EmbeddingActionCounts { get; set; } = new();

    [JsonPropertyName("createdIds")]
    public List<string> CreatedIds { get; set; } = new();

    [JsonPropertyName("updatedIds")]
    public List<string> UpdatedIds { get; set; } = new();

    [JsonPropertyName("reusedIds")]
    public List<string> ReusedIds { get; set; } = new();

    [JsonPropertyName("deduplicatedIds")]
    public List<string> DeduplicatedIds { get; set; } = new();

    [JsonPropertyName("staleDeleteIds")]
    public List<string> StaleDeleteIds { get; set; } = new();
}

public class RagIngestHashComparison
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("expectedHash")]
    public string? ExpectedHash { get; set; }

    [JsonPropertyName("actualHash")]
    public string? ActualHash { get; set; }

    [JsonPropertyName("compared")]
    public bool Compared { get; set; }

    [JsonPropertyName("matches")]
    public bool Matches { get; set; }

    /// <summary>Hash comparison outcome. Valid values are defined in <see cref="RagIngestHashStatuses"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = RagIngestHashStatuses.NotProvided;
}

public class RagIngestChunkResult
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("charStart")]
    public int CharStart { get; set; }

    [JsonPropertyName("charEnd")]
    public int CharEnd { get; set; }

    [JsonPropertyName("textLength")]
    public int TextLength { get; set; }

    [JsonPropertyName("textHash")]
    public string TextHash { get; set; } = string.Empty;

    [JsonPropertyName("embeddingTextHash")]
    public string EmbeddingTextHash { get; set; } = string.Empty;

    /// <summary>What happened to this chunk record. Valid values are defined in <see cref="RagIngestChunkActions"/>.</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>What happened to this chunk's embedding. Valid values are defined in <see cref="RagEmbeddingActions"/>.</summary>
    [JsonPropertyName("embeddingAction")]
    public string EmbeddingAction { get; set; } = string.Empty;

    [JsonPropertyName("reusedVectorFromId")]
    public string? ReusedVectorFromId { get; set; }

    [JsonPropertyName("reusedVectorFromPartitionKey")]
    public string? ReusedVectorFromPartitionKey { get; set; }

    [JsonPropertyName("deduplicatedFromId")]
    public string? DeduplicatedFromId { get; set; }

    [JsonPropertyName("deduplicatedFromPartitionKey")]
    public string? DeduplicatedFromPartitionKey { get; set; }

    [JsonPropertyName("etag")]
    public string? Etag { get; set; }

    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

public class RagIngestStaleDeleteResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("chunkIndex")]
    public int? ChunkIndex { get; set; }

    [JsonPropertyName("textHash")]
    public string? TextHash { get; set; }

    [JsonPropertyName("etag")]
    public string? Etag { get; set; }

    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

public static class VyralRecordTypes
{
    /// <summary>Standard RAG text chunk produced by RagIngestionService.</summary>
    public const string RagChunk = "rag.chunk";
    /// <summary>Manifest record written when PersistManifest = true; tracks all chunk IDs for a document.</summary>
    public const string RagManifest = "rag.manifest";
}

public static class RagIngestItemStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

public static class VectorReuseScopes
{
    /// <summary>Reuse vectors from records in the same partition.</summary>
    public const string Partition = "partition";
    /// <summary>Reuse vectors from records across all partitions in the collection.</summary>
    public const string Collection = "collection";
}

public static class ChunkDedupeScopes
{
    /// <summary>Deduplicate chunks against records in the same partition.</summary>
    public const string Partition = "partition";
    /// <summary>Deduplicate chunks against records across all partitions in the collection.</summary>
    public const string Collection = "collection";
}

public static class RagIngestHashStatuses
{
    /// <summary>No expected hash was supplied; comparison was skipped.</summary>
    public const string NotProvided = "not_provided";
    /// <summary>Expected and actual hashes matched.</summary>
    public const string Matched = "matched";
    /// <summary>Expected hash was supplied but the actual hash differed — content has drifted.</summary>
    public const string Drifted = "drifted";
    /// <summary>Expected hash was supplied but the actual hash was missing or empty.</summary>
    public const string ActualMissing = "actual_missing";
}

public static class RagIngestChunkActions
{
    /// <summary>Chunk record was newly created.</summary>
    public const string Created = "created";
    /// <summary>Chunk record already existed and was updated.</summary>
    public const string Updated = "updated";
    /// <summary>Chunk record was reused without modification (SkipUnchangedChunks = true).</summary>
    public const string Reused = "reused";
    /// <summary>Chunk was a duplicate of an existing record and was skipped.</summary>
    public const string Deduplicated = "deduplicated";
}

public static class RagEmbeddingActions
{
    /// <summary>A new embedding was generated for this chunk.</summary>
    public const string Generated = "generated";
    /// <summary>An existing embedding was reused (ReuseExistingChunkVectors = true).</summary>
    public const string Reused = "reused";
    /// <summary>The chunk text was unchanged so the existing embedding was kept as-is.</summary>
    public const string Unchanged = "unchanged";
    /// <summary>Chunk was deduplicated; embedding action mirrors the chunk action.</summary>
    public const string Deduplicated = "deduplicated";
}
