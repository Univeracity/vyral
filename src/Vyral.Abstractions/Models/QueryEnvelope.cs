using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public static class QueryWorkLimits
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 5_000;
    public const int DefaultVectorScanLimit = 10_000;
    public const int DefaultLexicalScanLimit = 5_000;
    public const int MaxCandidateLimit = 100_000;
    public const int MaxSearchTop = 10_000;
    public const int MaxPartitionKeys = 1_000;
    public const int MaxOrderExpressions = 8;
    public const int MaxFilterDepth = 16;
    public const int MaxFilterNodes = 100;
    public const int MaxFilterInValues = 1_000;
    public const int MaxContinuationTokenChars = 8_192;
    public const int MaxSearchTextChars = 32_768;

    public static int ResolvePageSize(int? limit, string description = "Query page size") =>
        ResolvePositiveLimit(limit, DefaultPageSize, MaxPageSize, description);

    public static int ResolveSearchTop(int top, string description) =>
        ResolvePositiveLimit(top, top, MaxSearchTop, description);

    public static int ResolveCandidateLimit(int? limit, int defaultLimit, string description) =>
        ResolvePositiveLimit(limit, defaultLimit, MaxCandidateLimit, description);

    public static void Validate(QueryEnvelope query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _ = ResolvePageSize(query.Limit);
        if (query.PartitionKeys is { Count: > MaxPartitionKeys })
        {
            throw new InvalidOperationException($"Query partitionKeys supports at most {MaxPartitionKeys} values.");
        }
        if (query.OrderBy is { Count: > MaxOrderExpressions })
        {
            throw new InvalidOperationException($"Query orderBy supports at most {MaxOrderExpressions} expressions.");
        }
        if (query.ContinuationToken is { Length: > MaxContinuationTokenChars })
        {
            throw new InvalidOperationException($"Query continuationToken cannot exceed {MaxContinuationTokenChars} characters.");
        }

        ValidateFilter(query.Filter);
        if (query.Vector is not null)
        {
            _ = ResolveSearchTop(query.Vector.Top, "Vector search top");
            _ = ResolveCandidateLimit(query.Vector.ScanLimit, DefaultVectorScanLimit, "Vector scan limit");
        }
        if (query.Lexical is not null)
        {
            _ = ResolveSearchTop(query.Lexical.Top, "Lexical top");
            _ = ResolveCandidateLimit(query.Lexical.ScanLimit, DefaultLexicalScanLimit, "Lexical scan limit");
            if (query.Lexical.Query.Length > MaxSearchTextChars)
            {
                throw new InvalidOperationException($"Lexical query cannot exceed {MaxSearchTextChars} characters.");
            }
        }
    }

    private static int ResolvePositiveLimit(int? limit, int defaultLimit, int maximum, string description)
    {
        if (limit is <= 0)
        {
            throw new InvalidOperationException($"{description} must be greater than zero.");
        }
        var selected = limit ?? defaultLimit;
        if (selected > maximum)
        {
            throw new InvalidOperationException($"{description} cannot exceed {maximum}.");
        }
        return selected;
    }

    private static void ValidateFilter(FilterNode? root)
    {
        if (root is null) return;
        var pending = new Stack<(FilterNode Node, int Depth)>();
        var visited = new HashSet<FilterNode>(ReferenceEqualityComparer.Instance);
        pending.Push((root, 1));
        var count = 0;
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (!visited.Add(node))
            {
                throw new InvalidOperationException("Query filter cannot contain cycles or reuse the same node instance.");
            }
            count++;
            if (count > MaxFilterNodes)
            {
                throw new InvalidOperationException($"Query filter supports at most {MaxFilterNodes} nodes.");
            }
            if (depth > MaxFilterDepth)
            {
                throw new InvalidOperationException($"Query filter supports at most {MaxFilterDepth} levels.");
            }
            if (string.Equals(node.Op, FilterOps.In, StringComparison.OrdinalIgnoreCase) &&
                FilterValueCount(node.Value) is > MaxFilterInValues)
            {
                throw new InvalidOperationException($"Query filter 'in' supports at most {MaxFilterInValues} values.");
            }
            if (node.Children is null) continue;
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push((node.Children[index], depth + 1));
            }
        }
    }

    private static int? FilterValueCount(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Array } element => element.GetArrayLength(),
        System.Collections.ICollection collection => collection.Count,
        _ => null
    };
}

public class QueryEnvelope
{
    [JsonPropertyName("partitionKeys")]
    public List<string>? PartitionKeys { get; set; }

    [JsonPropertyName("filter")]
    public FilterNode? Filter { get; set; }

    [JsonPropertyName("vector")]
    public VectorSearchOptions? Vector { get; set; }

    [JsonPropertyName("lexical")]
    public LexicalSearchOptions? Lexical { get; set; }

    [JsonPropertyName("orderBy")]
    public List<OrderExpression>? OrderBy { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}

/// <summary>
/// Recursive filter tree. Leaf nodes set Path/Op/Value. Compound nodes set
/// Combine ("all"|"any") and Children.
/// Use the static factory methods rather than constructing nodes directly.
/// </summary>
public class FilterNode
{
    // Leaf fields
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("op")]
    public string? Op { get; set; }

    /// <summary>
    /// Supported scalar values: string, bool, built-in numeric types, and null.
    /// For <see cref="FilterOps.In"/>, pass an IEnumerable&lt;object?&gt; of supported scalars.
    /// For <see cref="FilterOps.Exists"/>, pass bool; omitted/null means true.
    /// Object values and non-In arrays are rejected by provider adapters.
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }

    // Compound fields
    [JsonPropertyName("combine")]
    public string? Combine { get; set; }

    [JsonPropertyName("children")]
    public List<FilterNode>? Children { get; set; }

    // -----------------------------------------------------------------------
    // Factories
    // -----------------------------------------------------------------------

    public static FilterNode Leaf(string path, string op, object? value)
        => new() { Path = path, Op = op, Value = value };

    public static FilterNode All(params FilterNode[] children)
        => new() { Combine = FilterCombineModes.All, Children = new List<FilterNode>(children) };

    public static FilterNode Any(params FilterNode[] children)
        => new() { Combine = FilterCombineModes.Any, Children = new List<FilterNode>(children) };

    public static FilterNode Eq(string path, object? value)        => Leaf(path, FilterOps.Eq, value);
    public static FilterNode Neq(string path, object? value)       => Leaf(path, FilterOps.Neq, value);
    public static FilterNode Gt(string path, object? value)        => Leaf(path, FilterOps.Gt, value);
    public static FilterNode Gte(string path, object? value)       => Leaf(path, FilterOps.Gte, value);
    public static FilterNode Lt(string path, object? value)        => Leaf(path, FilterOps.Lt, value);
    public static FilterNode Lte(string path, object? value)       => Leaf(path, FilterOps.Lte, value);
    public static FilterNode In(string path, IEnumerable<object?> values) => Leaf(path, FilterOps.In, new List<object?>(values));
    public static FilterNode Contains(string path, object? value)  => Leaf(path, FilterOps.Contains, value);
    public static FilterNode StartsWith(string path, object? value) => Leaf(path, FilterOps.StartsWith, value);
    public static FilterNode Exists(string path)                   => Leaf(path, FilterOps.Exists, true);
}

public class EmbeddingOptions
{
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    /// <summary>
    /// Semantic purpose of the embedding. Valid values are defined in <see cref="EmbeddingPurposes"/>:
    /// Query, Passage, Document, Symmetric. When null, the service selects the purpose appropriate
    /// to its context: retrieval uses EmbeddingPurposes.Query; ingestion uses EmbeddingPurposes.Passage.
    /// </summary>
    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }

    [JsonPropertyName("queryPrefix")]
    public string? QueryPrefix { get; set; }

    [JsonPropertyName("passagePrefix")]
    public string? PassagePrefix { get; set; }

    [JsonPropertyName("symmetricPrefix")]
    public string? SymmetricPrefix { get; set; }
}

public class VectorSearchOptions
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public float[] Value { get; set; } = System.Array.Empty<float>();

    [JsonPropertyName("top")]
    public int Top { get; set; } = 10;

    [JsonPropertyName("scanLimit")]
    public int? ScanLimit { get; set; } = QueryWorkLimits.DefaultVectorScanLimit;

    [JsonPropertyName("minScore")]
    public float? MinScore { get; set; }
}

public class LexicalSearchOptions
{
    /// <summary>Default lexical search options — all BM25 parameters at their recommended values.</summary>
    public static LexicalSearchOptions Default => new();

    /// <summary>Lexical search restricted to the specified content/metadata fields.</summary>
    public static LexicalSearchOptions ForFields(params string[] fields)
        => new() { Fields = new List<string>(fields) };

    /// <summary>
    /// Optional query text override for lexical scoring. When set on RetrievalRequest.Lexical,
    /// this overrides the top-level RetrievalRequest.Query for lexical search only (useful for
    /// running a different keyword query alongside a semantic query). When empty, the service
    /// uses RetrievalRequest.Query. This field is also used internally by the store layer.
    /// </summary>
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public List<string>? Fields { get; set; }

    [JsonPropertyName("top")]
    public int Top { get; set; } = 50;

    [JsonPropertyName("scanLimit")]
    public int? ScanLimit { get; set; } = 5000;

    [JsonPropertyName("minScore")]
    public float? MinScore { get; set; }

    [JsonPropertyName("scoring")]
    public string Scoring { get; set; } = LexicalScorings.Bm25;

    [JsonPropertyName("matchMode")]
    public string MatchMode { get; set; } = LexicalMatchModes.Any;

    [JsonPropertyName("fieldBoosts")]
    public Dictionary<string, float>? FieldBoosts { get; set; }

    [JsonPropertyName("bm25K1")]
    public float Bm25K1 { get; set; } = 1.2f;

    [JsonPropertyName("bm25B")]
    public float Bm25B { get; set; } = 0.75f;

    [JsonPropertyName("phraseBoost")]
    public float PhraseBoost { get; set; } = 0.15f;

    [JsonPropertyName("exactBoost")]
    public float ExactBoost { get; set; } = 0.25f;

    [JsonPropertyName("metadataBoost")]
    public float MetadataBoost { get; set; } = 0.10f;

    [JsonPropertyName("prefixMatching")]
    public bool PrefixMatching { get; set; }

    [JsonPropertyName("prefixMinChars")]
    public int PrefixMinChars { get; set; } = 3;

    /// <summary>
    /// Optional conjunction of phrase disjunctions used as a lexical candidate
    /// constraint. Every group must match at least one phrase, and a phrase
    /// matches when it occurs contiguously within one scalar value of one of
    /// <see cref="Fields"/> (for example, within one JSON array element).
    /// For example, [["browser network diagnostics"], ["loaded latency",
    /// "access latency"]] requires the concept phrase and either aspect
    /// phrase. This is structured data rather than provider-specific FTS
    /// syntax, so adapters can either implement the same semantics or reject
    /// the request explicitly.
    /// </summary>
    [JsonPropertyName("requiredPhraseGroups")]
    public List<List<string>>? RequiredPhraseGroups { get; set; }
}

public class HybridSearchOptions
{
    [JsonPropertyName("vectorWeight")]
    public float VectorWeight { get; set; } = 0.55f;

    [JsonPropertyName("lexicalWeight")]
    public float LexicalWeight { get; set; } = 0.45f;

    [JsonPropertyName("candidateMultiplier")]
    public int CandidateMultiplier { get; set; } = 8;

    [JsonPropertyName("vectorCandidateLimit")]
    public int? VectorCandidateLimit { get; set; }

    [JsonPropertyName("lexicalCandidateLimit")]
    public int? LexicalCandidateLimit { get; set; }

    [JsonPropertyName("fusion")]
    public string Fusion { get; set; } = FusionModes.Weighted;

    [JsonPropertyName("rrfK")]
    public int RrfK { get; set; } = 60;
}

public class OrderExpression
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = SortDirections.Asc;
}

// ---------------------------------------------------------------------------
// String constant classes for discriminator fields
// ---------------------------------------------------------------------------

public static class FilterOps
{
    public const string Eq = "eq";
    public const string Neq = "neq";
    public const string Gt = "gt";
    public const string Gte = "gte";
    public const string Lt = "lt";
    public const string Lte = "lte";
    public const string In = "in";
    public const string Contains = "contains";
    public const string StartsWith = "startsWith";
    public const string Exists = "exists";
}

public static class DistanceFunctions
{
    public const string Cosine = "cosine";
    public const string DotProduct = "dotProduct";
    public const string Dot = DotProduct;
    public const string Euclidean = "euclidean";
}

public static class VectorDatatypes
{
    public const string Float32 = "float32";
}

public static class IndexTypes
{
    public const string Flat = "flat";
    public const string QuantizedFlat = "quantizedFlat";
    public const string DiskAnn = "diskANN";
    [Obsolete("Use IndexTypes.DiskAnn. The supported approximate index is DiskANN, not HNSW.")]
    public const string Hnsw = DiskAnn;
}

public static class SortDirections
{
    public const string Asc = "asc";
    public const string Desc = "desc";
}

public static class SearchModes
{
    public const string Vector = "vector";
    public const string Lexical = "lexical";
    public const string Hybrid = "hybrid";
}

public static class FusionModes
{
    public const string Weighted = "weighted";
    public const string Rrf = "rrf";
}

public static class LexicalScorings
{
    public const string Bm25 = "bm25";
}

public static class LexicalMatchModes
{
    public const string Any = "any";
    public const string All = "all";
}

public static class FilterCombineModes
{
    /// <summary>All child conditions must be true (logical AND).</summary>
    public const string All = "all";
    /// <summary>At least one child condition must be true (logical OR).</summary>
    public const string Any = "any";
}
