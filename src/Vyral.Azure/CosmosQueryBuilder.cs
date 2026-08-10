using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Azure.Cosmos;
using Vyral.Abstractions.Models;

namespace Vyral.Azure;

public class CosmosQueryPlan
{
    public string Sql { get; set; } = string.Empty;
    public Dictionary<string, object?> Parameters { get; set; } = new();

    public QueryDefinition ToQueryDefinition()
    {
        var definition = new QueryDefinition(Sql);
        foreach (var (name, value) in Parameters)
        {
            definition.WithParameter(name, value);
        }

        return definition;
    }
}

public class CosmosQueryBuilder
{
    public CosmosQueryPlan BuildRecordQuery(QueryEnvelope query)
    {
        ValidateRecordQueryEnvelope(query);

        var parameters = new Dictionary<string, object?>();
        var sql = new StringBuilder("SELECT * FROM c");
        AppendWhere(sql, parameters, query);
        AppendOrderBy(sql, query);
        return new CosmosQueryPlan { Sql = sql.ToString(), Parameters = parameters };
    }

    public CosmosQueryPlan BuildVectorSearchQuery(QueryEnvelope query)
    {
        ValidateVectorQueryEnvelope(query);

        if (query.Vector == null) throw new ArgumentException("Vector query options are required.", nameof(query));
        if (query.Vector.Top <= 0) throw new InvalidOperationException("Vector search top must be greater than zero.");
        if (string.IsNullOrWhiteSpace(query.Vector.Field)) throw new InvalidOperationException("Vector field is required.");

        var parameters = new Dictionary<string, object?>
        {
            ["@top"] = query.Vector.Top,
            ["@vector"] = query.Vector.Value
        };
        var vectorExpr = BuildVectorExpression(query.Vector.Field);
        var distanceExpr = $"VectorDistance({vectorExpr}, @vector)";

        var sql = new StringBuilder($"SELECT TOP @top c, {distanceExpr} AS SimilarityScore FROM c");
        AppendWhere(sql, parameters, query);
        sql.Append(" ORDER BY ");
        sql.Append(distanceExpr);

        return new CosmosQueryPlan { Sql = sql.ToString(), Parameters = parameters };
    }

    private static void ValidateRecordQueryEnvelope(QueryEnvelope query)
    {
        if (query.Vector != null)
        {
            throw new NotSupportedException("Cosmos record queries do not accept vector options; use vector search instead.");
        }

        if (query.Lexical != null)
        {
            throw new NotSupportedException("Cosmos record queries do not support Vyral lexical search options yet.");
        }
    }

    private static void ValidateVectorQueryEnvelope(QueryEnvelope query)
    {
        if (query.Lexical != null)
        {
            throw new NotSupportedException("Cosmos vector search does not support Vyral lexical search options yet.");
        }

        if (query.OrderBy?.Any() == true)
        {
            throw new NotSupportedException("Cosmos vector search ordering is fixed to VectorDistance.");
        }

    }

    private static void AppendWhere(StringBuilder sql, Dictionary<string, object?> parameters, QueryEnvelope query)
    {
        var clauses = new List<string>();
        if (query.PartitionKeys?.Any() == true)
        {
            if (query.PartitionKeys.Count == 1)
            {
                parameters["@partitionKey0"] = query.PartitionKeys[0];
                clauses.Add($"{BuildPathExpression("/partitionKey")} = @partitionKey0");
            }
            else
            {
                parameters["@partitionKeys"] = query.PartitionKeys;
                clauses.Add($"ARRAY_CONTAINS(@partitionKeys, {BuildPathExpression("/partitionKey")})");
            }
        }

        if (query.Filter != null)
        {
            var filter = BuildFilterClause(query.Filter, parameters);
            if (!string.IsNullOrWhiteSpace(filter)) clauses.Add(filter);
        }

        if (clauses.Count > 0)
        {
            sql.Append(" WHERE ");
            sql.Append(string.Join(" AND ", clauses));
        }
    }

    private static void AppendOrderBy(StringBuilder sql, QueryEnvelope query)
    {
        if (query.OrderBy?.Any() != true) return;

        sql.Append(" ORDER BY ");
        sql.Append(string.Join(", ", query.OrderBy.Select(order =>
        {
            var direction = order.Direction.ToLowerInvariant() switch
            {
                "asc" => "ASC",
                "desc" => "DESC",
                _ => throw new NotSupportedException($"Order direction '{order.Direction}' is not supported.")
            };
            return $"{BuildPathExpression(order.Path)} {direction}";
        })));
    }

    private static string BuildFilterClause(FilterNode node, Dictionary<string, object?> parameters)
    {
        if (node.Children != null && !string.IsNullOrEmpty(node.Combine))
        {
            var separator = string.Equals(node.Combine, "any", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ";
            var clauses = node.Children.Select(c => BuildFilterClause(c, parameters)).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            return clauses.Count == 0 ? string.Empty : "(" + string.Join(separator, clauses) + ")";
        }

        if (!string.IsNullOrWhiteSpace(node.Path))
        {
            return BuildLeafCondition(node.Path, node.Op ?? FilterOps.Eq, node.Value, parameters);
        }

        return string.Empty;
    }

    private static string BuildLeafCondition(string path, string op, object? rawValue, Dictionary<string, object?> parameters)
    {
        var pathExpr = BuildPathExpression(path);
        var normalizedOp = FilterValueNormalizer.NormalizeOperator(op);

        if (normalizedOp == "exists")
        {
            var shouldExist = FilterValueNormalizer.NormalizeExistsValue(rawValue);
            return shouldExist ? $"IS_DEFINED({pathExpr})" : $"NOT IS_DEFINED({pathExpr})";
        }

        if (normalizedOp == "in")
        {
            var parameterName = NextParameterName(parameters);
            parameters[parameterName] = FilterValueNormalizer.NormalizeScalarList(rawValue);
            return $"ARRAY_CONTAINS({parameterName}, {pathExpr})";
        }

        var value = FilterValueNormalizer.NormalizeScalar(rawValue);
        if (value == null)
        {
            return normalizedOp switch
            {
                "eq" => $"IS_NULL({pathExpr})",
                "neq" => $"(IS_DEFINED({pathExpr}) AND NOT IS_NULL({pathExpr}))",
                _ => throw new NotSupportedException($"Operator '{op}' cannot be used with null values.")
            };
        }

        if (normalizedOp is FilterOps.Contains or FilterOps.StartsWith)
        {
            if (value is not string)
                throw new NotSupportedException($"Filter operator '{op}' requires a string value.");

            var textParameter = NextParameterName(parameters);
            parameters[textParameter] = value;
            return normalizedOp == FilterOps.Contains
                ? $"CONTAINS({pathExpr}, {textParameter})"
                : $"STARTSWITH({pathExpr}, {textParameter})";
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

        var valueParameter = NextParameterName(parameters);
        parameters[valueParameter] = value;
        return $"{pathExpr} {sqlOp} {valueParameter}";
    }

    private static string BuildVectorExpression(string field)
    {
        ValidatePathSegment(field);
        return $"c[\"vectors\"][\"{field}\"][\"values\"]";
    }

    private static string BuildPathExpression(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Query path '{path}' must be a JSON pointer such as '/metadata/status'.");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Replace("~1", "/").Replace("~0", "~"))
            .ToList();

        if (segments.Count == 0) throw new NotSupportedException("Root JSON pointer is not supported in Cosmos query translation.");
        foreach (var segment in segments) ValidatePathSegment(segment);

        return "c" + string.Concat(segments.Select(segment => $"[\"{segment}\"]"));
    }

    private static void ValidatePathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || !segment.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
        {
            throw new NotSupportedException($"Path segment '{segment}' is not supported in Cosmos query translation.");
        }
    }

    private static string NextParameterName(Dictionary<string, object?> parameters)
    {
        return "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
    }

}
