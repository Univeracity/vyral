using System;
using Vyral.Abstractions.Models;

namespace Vyral.Pgvector;

/// <summary>
/// Maps Vyral vector field policies to pgvector index DDL.
/// ivfflat is the default for most workloads; hnsw gives better recall at the
/// cost of build time and index size. Both support cosine, L2, and inner product
/// operators. The index type can be surfaced through the collection policy
/// IndexType field: "flat" → no index (exact scan), "quantizedFlat" → ivfflat,
/// "diskANN" → hnsw.
/// </summary>
public static class PgvectorVectorPolicyMapper
{
    public static string BuildCreateIndexSql(string collection, string vectorName, VectorFieldPolicy policy)
    {
        var tableName = "vyral_record_vectors";
        var indexName = $"idx_vec_{Sanitize(collection)}_{Sanitize(vectorName)}";
        var opClass = MapDistanceOpClass(policy.DistanceFunction);
        var indexType = MapIndexType(policy.IndexType);

        return indexType switch
        {
            PgvectorIndexType.Exact =>
                string.Empty,
            PgvectorIndexType.IvfFlat =>
                $"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName} " +
                $"USING ivfflat (vector_data {opClass}) WITH (lists = 100) " +
                $"WHERE collection = '{Escape(collection)}' AND vector_name = '{Escape(vectorName)}';",
            PgvectorIndexType.Hnsw =>
                $"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName} " +
                $"USING hnsw (vector_data {opClass}) WITH (m = 16, ef_construction = 64) " +
                $"WHERE collection = '{Escape(collection)}' AND vector_name = '{Escape(vectorName)}';",
            _ => string.Empty
        };
    }

    public static string MapDistanceOpClass(string distanceFunction)
    {
        return distanceFunction.ToLowerInvariant() switch
        {
            "cosine" => "vector_cosine_ops",
            "dotproduct" => "vector_ip_ops",
            "euclidean" => "vector_l2_ops",
            _ => throw new NotSupportedException($"Distance function '{distanceFunction}' is not supported by the pgvector adapter.")
        };
    }

    public static string MapDistanceOperator(string distanceFunction)
    {
        return distanceFunction.ToLowerInvariant() switch
        {
            "cosine" => "<=>",
            "dotproduct" => "<#>",
            "euclidean" => "<->",
            _ => throw new NotSupportedException($"Distance function '{distanceFunction}' is not supported by the pgvector adapter.")
        };
    }

    public static PgvectorIndexType MapIndexType(string indexType)
    {
        return indexType?.ToLowerInvariant() switch
        {
            "flat" or null => PgvectorIndexType.Exact,
            "quantizedflat" => PgvectorIndexType.IvfFlat,
            "diskann" => PgvectorIndexType.Hnsw,
            _ => PgvectorIndexType.Exact
        };
    }

    private static string Sanitize(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "[^a-zA-Z0-9_]", "_");

    /// <summary>
    /// Converts a pgvector distance value (lower = more similar) to the Vyral
    /// similarity score convention (higher = more similar) used by the retrieval
    /// service and conformance tests.
    /// </summary>
    public static float DistanceToScore(string vyralDistance, float distance)
    {
        return vyralDistance.ToLowerInvariant() switch
        {
            // cosine distance = 1 - cosine_similarity; invert to get raw similarity [-1, 1]
            "cosine" => 1.0f - distance,
            // inner product distance = -dot_product; invert to get dot product
            "dotproduct" => -distance,
            // L2 distance: apply same transform as local flat scan
            "euclidean" => 1.0f / (1.0f + distance),
            _ => 1.0f / (1.0f + distance)
        };
    }

    private static string Escape(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}

public enum PgvectorIndexType
{
    Exact,
    IvfFlat,
    Hnsw
}
