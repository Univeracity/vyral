using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Vyral.Abstractions.Models;

namespace Vyral.Pgvector;

/// <summary>
/// Translates portable Vyral QueryEnvelopes into parameterized PostgreSQL SQL.
/// Parallel to CosmosQueryBuilder (Azure) and SqliteQueryBuilder (Local).
///
/// JSONB path notation: Postgres uses #>> for text extraction from a path array
/// and -> / ->> for single-key access. Vyral JSON Pointer paths (/metadata/status)
/// are converted to Postgres JSONB operator chains.
/// </summary>
public class PgvectorQueryBuilder
{
    public (string Sql, List<NpgsqlParameter> Parameters) BuildQuery(
        string collection,
        QueryEnvelope query,
        IEnumerable<string>? indexedMetadata = null)
    {
        var sql = new StringBuilder();
        var parameters = new List<NpgsqlParameter>();
        var indexed = ToIndexedSet(indexedMetadata);
        var p = new ParamCounter();

        sql.Append("SELECT content_json FROM vyral_records r");
        sql.Append(BuildWhereClause(collection, query, parameters, indexed, p, "r"));

        if (query.OrderBy?.Count > 0)
        {
            sql.Append(" ORDER BY ");
            var orderParts = new List<string>();
            foreach (var order in query.OrderBy)
            {
                orderParts.AddRange(MapOrderExpressions(order.Path, order.Direction, parameters, indexed, p, "r"));
            }
            sql.Append(string.Join(", ", orderParts));
        }
        else
        {
            sql.Append(" ORDER BY r.partition_key, r.id");
        }

        var offset = DecodeContinuationToken(query.ContinuationToken);
        if (query.Limit.HasValue)
        {
            var pn = p.Next();
            sql.Append($" LIMIT @p{pn}");
            parameters.Add(new NpgsqlParameter($"p{pn}", query.Limit.Value));
        }
        if (offset > 0)
        {
            var pn = p.Next();
            sql.Append($" OFFSET @p{pn}");
            parameters.Add(new NpgsqlParameter($"p{pn}", offset));
        }

        return (sql.ToString(), parameters);
    }

    public (string Sql, List<NpgsqlParameter> Parameters) BuildVectorSearchQuery(
        string collection,
        QueryEnvelope query,
        VectorFieldPolicy fieldPolicy,
        IEnumerable<string>? indexedMetadata = null)
    {
        if (query.Vector == null) throw new ArgumentException("Vector search options are required.", nameof(query));

        var parameters = new List<NpgsqlParameter>();
        var p = new ParamCounter();
        var indexed = ToIndexedSet(indexedMetadata);
        var op = PgvectorVectorPolicyMapper.MapDistanceOperator(fieldPolicy.DistanceFunction);
        var topN = p.Next();
        var collP = p.Next();
        var vecP = p.Next();
        var nameP = p.Next();

        // Build partition key filter fragment
        var pkFilter = new StringBuilder();
        if (query.PartitionKeys?.Count > 0)
        {
            var pkParams = new List<string>();
            foreach (var pk in query.PartitionKeys)
            {
                var pn = p.Next();
                pkParams.Add($"@p{pn}");
                parameters.Add(new NpgsqlParameter($"p{pn}", pk));
            }
            pkFilter.Append($" AND v.partition_key IN ({string.Join(", ", pkParams)})");
        }

        // Build record-level filter fragment (references r alias)
        var recordFilter = new StringBuilder();
        if (query.Filter != null)
        {
            var filterSql = BuildFilterClause(query.Filter, parameters, indexed, p, "r");
            if (!string.IsNullOrWhiteSpace(filterSql))
                recordFilter.Append($" AND {filterSql}");
        }

        parameters.Add(new NpgsqlParameter($"p{collP}", collection));
        var vector = query.Vector.Value;
        parameters.Add(new NpgsqlParameter($"p{vecP}", NpgsqlDbType.Unknown) { Value = $"[{string.Join(",", vector)}]" });
        parameters.Add(new NpgsqlParameter($"p{nameP}", query.Vector.Field));
        parameters.Add(new NpgsqlParameter($"p{topN}", query.Vector.Top));

        var sql = $@"SELECT r.content_json, (v.vector_data {op} @p{vecP}::vector) AS distance
FROM vyral_record_vectors v
JOIN vyral_records r ON r.collection = v.collection AND r.partition_key = v.partition_key AND r.id = v.record_id
WHERE v.collection = @p{collP}{pkFilter} AND v.vector_name = @p{nameP}{recordFilter}
ORDER BY distance
LIMIT @p{topN}";

        return (sql, parameters);
    }

    public (string Sql, List<NpgsqlParameter> Parameters) BuildLexicalSearchQuery(
        string collection,
        QueryEnvelope query)
    {
        if (query.Lexical == null) throw new ArgumentException("Lexical search options are required.", nameof(query));

        var parameters = new List<NpgsqlParameter>();
        var p = new ParamCounter();

        var collP = p.Next();
        var queryP = p.Next();
        var topP = p.Next();

        parameters.Add(new NpgsqlParameter($"p{collP}", collection));
        parameters.Add(new NpgsqlParameter($"p{queryP}", query.Lexical.Query));
        parameters.Add(new NpgsqlParameter($"p{topP}", query.Lexical.Top > 0 ? query.Lexical.Top : 50));

        // Use pg_trgm for similarity or tsvector/tsquery for FTS depending on the query.
        // Default to tsvector/tsquery (plainto_tsquery) for standard BM25-style retrieval;
        // pg_trgm similarity is better suited for prefix and partial-token matching.
        var sql = $@"SELECT r.content_json,
    ts_rank_cd(to_tsvector('english', r.content_json::text), plainto_tsquery('english', @p{queryP})) AS score
FROM vyral_records r
WHERE r.collection = @p{collP}
  AND to_tsvector('english', r.content_json::text) @@ plainto_tsquery('english', @p{queryP})
ORDER BY score DESC, r.partition_key, r.id
LIMIT @p{topP}";

        return (sql, parameters);
    }

    private string BuildWhereClause(
        string collection,
        QueryEnvelope query,
        List<NpgsqlParameter> parameters,
        HashSet<string> indexed,
        ParamCounter p,
        string alias)
    {
        var sql = new StringBuilder();
        var prefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";
        var collP = p.Next();
        parameters.Add(new NpgsqlParameter($"p{collP}", collection));
        sql.Append($" WHERE {prefix}collection = @p{collP}");

        if (query.PartitionKeys?.Count > 0)
        {
            var pkParams = new List<string>();
            foreach (var pk in query.PartitionKeys)
            {
                var pn = p.Next();
                pkParams.Add($"@p{pn}");
                parameters.Add(new NpgsqlParameter($"p{pn}", pk));
            }
            sql.Append($" AND {prefix}partition_key IN ({string.Join(", ", pkParams)})");
        }

        if (query.Filter != null)
        {
            var filterSql = BuildFilterClause(query.Filter, parameters, indexed, p, alias);
            if (!string.IsNullOrWhiteSpace(filterSql))
            {
                sql.Append($" AND {filterSql}");
            }
        }

        return sql.ToString();
    }

    private string BuildFilterClause(FilterNode node, List<NpgsqlParameter> parameters, HashSet<string> indexed, ParamCounter p, string? alias)
    {
        if (node.Children != null && !string.IsNullOrEmpty(node.Combine))
        {
            var separator = string.Equals(node.Combine, "any", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ";
            var clauses = new List<string>();
            foreach (var c in node.Children)
            {
                var cl = BuildFilterClause(c, parameters, indexed, p, alias);
                if (!string.IsNullOrWhiteSpace(cl)) clauses.Add(cl);
            }
            return clauses.Count == 0 ? string.Empty : "(" + string.Join(separator, clauses) + ")";
        }

        if (!string.IsNullOrEmpty(node.Path))
        {
            return BuildLeafCondition(node.Path, node.Op ?? FilterOps.Eq, node.Value, parameters, indexed, p, alias);
        }

        return string.Empty;
    }

    private string BuildLeafCondition(string path, string op, object? rawValue, List<NpgsqlParameter> parameters, HashSet<string> indexed, ParamCounter p, string? alias)
    {
        if (indexed.Contains(path))
        {
            return BuildIndexedCondition(path, op, rawValue, parameters, p, alias);
        }

        var normalizedOp = FilterValueNormalizer.NormalizeOperator(op);
        var col = MapPathToJsonbExpr(path, alias);

        if (normalizedOp == "exists")
        {
            var shouldExist = FilterValueNormalizer.NormalizeExistsValue(rawValue);
            var existsExpr = MapPathToExistsExpr(path, alias);
            return shouldExist ? $"({existsExpr}) IS NOT NULL" : $"({existsExpr}) IS NULL";
        }

        if (normalizedOp == "in")
        {
            var values = FilterValueNormalizer.NormalizeScalarList(rawValue);
            if (values.Count == 0) return "false";
            var paramNames = new List<string>();
            foreach (var v in values)
            {
                var pn = p.Next();
                paramNames.Add($"@p{pn}");
                parameters.Add(new NpgsqlParameter($"p{pn}", v ?? (object)DBNull.Value));
            }
            return $"{col} IN ({string.Join(", ", paramNames)})";
        }

        var value = FilterValueNormalizer.NormalizeScalar(rawValue);
        if (value == null)
        {
            return normalizedOp switch
            {
                "eq" => $"{col} IS NULL",
                "neq" => $"{col} IS NOT NULL",
                _ => throw new NotSupportedException($"Operator '{op}' cannot be used with null values.")
            };
        }

        if (normalizedOp is FilterOps.Contains or FilterOps.StartsWith)
        {
            if (value is not string text)
                throw new NotSupportedException($"Filter operator '{op}' requires a string value.");
            var pn = p.Next();
            parameters.Add(new NpgsqlParameter($"p{pn}", text));
            return normalizedOp == FilterOps.Contains
                ? $"({col})::text ILIKE '%' || @p{pn} || '%'"
                : $"({col})::text LIKE @p{pn} || '%'";
        }

        var sqlOp = normalizedOp switch
        {
            "eq" => "=",
            "neq" => "!=",
            "gt" => ">",
            "gte" => ">=",
            "lt" => "<",
            "lte" => "<=",
            _ => throw new NotSupportedException($"Filter operator '{op}' is not supported.")
        };
        var vpn = p.Next();
        parameters.Add(new NpgsqlParameter($"p{vpn}", value));
        var cast = value is int or long or float or double or decimal ? "::numeric"
                 : value is bool ? "::boolean"
                 : "::text";
        return $"({col}){cast} {sqlOp} (@p{vpn}){cast}";
    }

    private string BuildIndexedCondition(string path, string op, object? rawValue, List<NpgsqlParameter> parameters, ParamCounter p, string? alias)
    {
        var normalizedOp = FilterValueNormalizer.NormalizeOperator(op);
        var pathP = p.Next();
        parameters.Add(new NpgsqlParameter($"p{pathP}", path));
        var outerAlias = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";
        var correlation = $"mi.collection = {outerAlias}collection AND mi.partition_key = {outerAlias}partition_key AND mi.record_id = {outerAlias}id AND mi.path = @p{pathP}";

        if (normalizedOp == "exists")
        {
            var shouldExist = FilterValueNormalizer.NormalizeExistsValue(rawValue);
            return shouldExist
                ? $"EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation})"
                : $"NOT EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation})";
        }

        if (normalizedOp == "in")
        {
            var values = FilterValueNormalizer.NormalizeScalarList(rawValue);
            if (values.Count == 0) return "false";
            var paramNames = new List<string>();
            foreach (var v in values)
            {
                var pn = p.Next();
                paramNames.Add($"@p{pn}");
                parameters.Add(new NpgsqlParameter($"p{pn}", JsonSerializer.Serialize(v)));
            }
            return $"EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation} AND mi.value_json IN ({string.Join(", ", paramNames)}))";
        }

        var value = FilterValueNormalizer.NormalizeScalar(rawValue);
        if (value == null)
        {
            return normalizedOp switch
            {
                "eq" => $"EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation} AND mi.value_json = 'null')",
                "neq" => $"EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation} AND mi.value_json != 'null')",
                _ => throw new NotSupportedException($"Operator '{op}' cannot be used with null values.")
            };
        }

        if (normalizedOp is FilterOps.Contains or FilterOps.StartsWith)
        {
            if (value is not string text)
                throw new NotSupportedException($"Filter operator '{op}' requires a string value.");
            var pn = p.Next();
            parameters.Add(new NpgsqlParameter($"p{pn}", text));
            return normalizedOp == FilterOps.Contains
                ? $"EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation} AND mi.value_text ILIKE '%' || @p{pn} || '%')"
                : $"EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation} AND mi.value_text LIKE @p{pn} || '%')";
        }

        var sqlOp = normalizedOp switch
        {
            "eq" => "=",
            "neq" => "!=",
            "gt" => ">",
            "gte" => ">=",
            "lt" => "<",
            "lte" => "<=",
            _ => throw new NotSupportedException($"Filter operator '{op}' is not supported.")
        };

        var (col, paramValue) = GetIndexedComparisonTarget(value);
        var vpn = p.Next();
        parameters.Add(new NpgsqlParameter($"p{vpn}", paramValue));

        if (col != "mi.value_number" && normalizedOp is "gt" or "gte" or "lt" or "lte")
            throw new NotSupportedException($"Indexed range filter '{path}' requires a numeric value.");

        return $"EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation} AND {col} {sqlOp} @p{vpn})";
    }

    // Returns one or two ORDER BY expressions (numeric first, then text fallback for indexed paths).
    private List<string> MapOrderExpressions(string path, string direction, List<NpgsqlParameter> parameters, HashSet<string> indexed, ParamCounter p, string? alias)
    {
        var dir = NormalizeDirection(direction);
        if (indexed.Contains(path))
        {
            var outerAlias = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";
            var pnNum = p.Next();
            var pnTxt = p.Next();
            parameters.Add(new NpgsqlParameter($"p{pnNum}", path));
            parameters.Add(new NpgsqlParameter($"p{pnTxt}", path));
            var numExpr = $"(SELECT mi.value_number FROM vyral_record_metadata_index mi WHERE mi.collection = {outerAlias}collection AND mi.partition_key = {outerAlias}partition_key AND mi.record_id = {outerAlias}id AND mi.path = @p{pnNum} LIMIT 1)";
            var txtExpr = $"(SELECT mi.value_text FROM vyral_record_metadata_index mi WHERE mi.collection = {outerAlias}collection AND mi.partition_key = {outerAlias}partition_key AND mi.record_id = {outerAlias}id AND mi.path = @p{pnTxt} LIMIT 1)";
            return new List<string>
            {
                $"{numExpr} {dir} NULLS LAST",
                $"{txtExpr} {dir} NULLS LAST"
            };
        }
        return new List<string> { $"{MapPathToJsonbExpr(path, alias)} {dir}" };
    }

    private string MapOrderPath(string path, List<NpgsqlParameter> parameters, HashSet<string> indexed, ParamCounter p, string? alias) =>
        MapOrderExpressions(path, "ASC", parameters, indexed, p, alias)[0];

    private static string MapPathToJsonbExpr(string path, string? alias)
    {
        var prefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";
        if (TryMapDirectColumn(path, alias, out var col)) return col;
        var segments = PointerToSegments(path);
        if (segments.Length == 0) throw new NotSupportedException($"Path '{path}' could not be mapped to a JSONB expression.");
        return $"({prefix}content_json#>>ARRAY[{string.Join(", ", System.Array.ConvertAll(segments, s => $"'{EscapeString(s)}'"))}])";
    }

    private static string MapPathToExistsExpr(string path, string? alias)
    {
        var prefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";
        if (TryMapDirectColumn(path, alias, out var col)) return col;
        var segments = PointerToSegments(path);
        if (segments.Length == 0) throw new NotSupportedException($"Path '{path}' could not be mapped.");
        return $"({prefix}content_json#>ARRAY[{string.Join(", ", System.Array.ConvertAll(segments, s => $"'{EscapeString(s)}'"))}])";
    }

    private static bool TryMapDirectColumn(string path, string? alias, out string column)
    {
        var prefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";
        column = path.ToLowerInvariant() switch
        {
            "/id" => $"{prefix}id",
            "/partitionkey" => $"{prefix}partition_key",
            "/updatedat" => $"{prefix}updated_at",
            "/revision" => $"{prefix}revision",
            _ => string.Empty
        };
        return column.Length > 0;
    }

    private static string[] PointerToSegments(string path)
    {
        if (path.StartsWith("$.", StringComparison.Ordinal))
        {
            return path[2..].Split('.');
        }
        if (!path.StartsWith("/", StringComparison.Ordinal))
            throw new NotSupportedException($"Query path '{path}' must be a JSON pointer (/x/y) or JSONPath ($.x.y).");
        return path.TrimStart('/').Split('/');
    }

    private static string NormalizeDirection(string direction) =>
        direction.ToLowerInvariant() switch
        {
            "asc" => "ASC",
            "desc" => "DESC",
            _ => throw new NotSupportedException($"Order direction '{direction}' is not supported.")
        };

    private static HashSet<string> ToIndexedSet(IEnumerable<string>? paths) =>
        paths == null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(paths, StringComparer.Ordinal);

    private static (string Column, object Value) GetIndexedComparisonTarget(object value) =>
        value switch
        {
            string s => ("mi.value_text", s),
            bool b => ("mi.value_bool", b),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                ("mi.value_number", Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            _ => ("mi.value_json", JsonSerializer.Serialize(value))
        };

    private static string EscapeString(string s) => s.Replace("'", "''", StringComparison.Ordinal);

    public static string EncodeContinuationToken(int offset) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    public static int DecodeContinuationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var offset = int.Parse(decoded, CultureInfo.InvariantCulture);
            if (offset < 0) throw new InvalidOperationException("Continuation token offset must be non-negative.");
            return offset;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Continuation token is not valid for the pgvector adapter.", ex);
        }
    }

    private sealed class ParamCounter
    {
        private int _n;
        public int Next() => ++_n;
    }
}
