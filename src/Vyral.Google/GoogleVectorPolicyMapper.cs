using Vyral.Abstractions.Models;
using Vyral.Pgvector;

namespace Vyral.Google;

/// <summary>
/// Maps Vyral vector field policies to AlloyDB/pgvector index configuration.
/// Inherits from PgvectorVectorPolicyMapper and adds AlloyDB-specific guidance.
///
/// AlloyDB HNSW indexes are the recommended choice for production vector
/// workloads on GCP — AlloyDB's columnar engine accelerates HNSW scans beyond
/// standard Postgres pgvector performance. The "diskANN" Vyral index type
/// maps to HNSW here for that reason.
/// </summary>
public static class GoogleVectorPolicyMapper
{
    public static string BuildCreateIndexSql(string collection, string vectorName, VectorFieldPolicy policy)
    {
        // Delegate to pgvector mapper; AlloyDB uses the same DDL syntax.
        // AlloyDB-specific acceleration (columnar store, ScaNN) can be layered
        // here once the base pgvector implementation is stable.
        return PgvectorVectorPolicyMapper.BuildCreateIndexSql(collection, vectorName, policy);
    }
}
