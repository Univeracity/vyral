using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public interface IRetrievalService
{
    Task<RetrievalResultEnvelope> SearchAsync(RetrievalRequest request, CancellationToken ct = default);
}

/// <summary>
/// For vector-capable retrieval, VectorFields takes precedence over Embedding.
/// Set Embedding for single-field vector search; set VectorFields for multi-field weighted search.
/// </summary>
public class RetrievalRequest
{
    /// <summary>
    /// Optional retrieval profile id. Built-in values are defined in <see cref="RetrievalProfileIds"/>.
    /// Profiles apply local R&D defaults for search mode, lexical options, hybrid options, rerank options,
    /// limit, and trace inclusion when those fields are not otherwise supplied.
    /// </summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// Collections to search. Must be non-empty; the service rejects an empty list.
    /// </summary>
    [JsonPropertyName("collections")]
    public List<string> Collections { get; set; } = new();

    [JsonPropertyName("partitionKeys")]
    public List<string>? PartitionKeys { get; set; }

    [JsonPropertyName("filter")]
    public FilterNode? Filter { get; set; }

    [JsonPropertyName("embedding")]
    public EmbeddingOptions? Embedding { get; set; }

    [JsonPropertyName("vectorFields")]
    public List<RetrievalVectorFieldQuery>? VectorFields { get; set; }

    /// <summary>
    /// Search mode. When null or blank, the service infers: <see cref="SearchModes.Vector"/> if
    /// <see cref="Embedding"/> or <see cref="VectorFields"/> is set; otherwise <see cref="SearchModes.Lexical"/>.
    /// Set explicitly to <see cref="SearchModes.Hybrid"/> for lexical/vector fusion, or to override inference.
    /// Valid values are defined in <see cref="SearchModes"/>.
    /// </summary>
    [JsonPropertyName("searchMode")]
    public string? SearchMode { get; set; }

    [JsonPropertyName("lexical")]
    public LexicalSearchOptions? Lexical { get; set; }

    [JsonPropertyName("hybrid")]
    public HybridSearchOptions? Hybrid { get; set; }

    [JsonPropertyName("rerank")]
    public RerankOptions? Rerank { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 10;

    [JsonPropertyName("minScore")]
    public float? MinScore { get; set; }

    [JsonPropertyName("includeTrace")]
    public bool IncludeTrace { get; set; }
}

public class RetrievalVectorFieldQuery
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public float Weight { get; set; } = 1.0f;

    [JsonPropertyName("query")]
    public string? Query { get; set; }

    [JsonPropertyName("embedding")]
    public EmbeddingOptions? Embedding { get; set; }

    [JsonPropertyName("candidateLimit")]
    public int? CandidateLimit { get; set; }

    [JsonPropertyName("minScore")]
    public float? MinScore { get; set; }
}

public class RetrievalResultEnvelope
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("results")]
    public List<RetrievalMatch> Results { get; set; } = new();

    [JsonPropertyName("trace")]
    public JsonObject? Trace { get; set; }
}

public class RetrievalMatch
{
    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("score")]
    public float Score { get; set; }

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("snippet")]
    public string? Snippet { get; set; }

    [JsonPropertyName("record")]
    public VyralRecord Record { get; set; } = null!;

    [JsonPropertyName("diagnostics")]
    public RetrievalDiagnostics? Diagnostics { get; set; }
}
