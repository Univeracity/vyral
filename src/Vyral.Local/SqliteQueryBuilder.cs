using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public class SqliteQueryBuilder
{
    public (string Sql, List<SqliteParameter> Parameters) BuildQuery(string collection, QueryEnvelope query, IEnumerable<string>? indexedMetadata = null)
    {
        var sql = new StringBuilder();
        var parameters = new List<SqliteParameter>();
        var indexedPaths = ToIndexedPathSet(indexedMetadata);

        sql.Append("SELECT r.content_json FROM vyral_records r");
        sql.Append(BuildWhereClause(collection, query, parameters, indexedPaths, "r"));

        if (query.OrderBy?.Any() == true)
        {
            sql.Append(" ORDER BY ");
            sql.Append(string.Join(", ", query.OrderBy.Select(o => 
            {
                var column = MapOrderPath(o.Path, parameters, indexedPaths, "r");
                var direction = NormalizeDirection(o.Direction);
                return $"{column} {direction}";
            })));
        }
        else
        {
            sql.Append(" ORDER BY r.partitionKey, r.id");
        }

        if (query.Limit.HasValue)
        {
            sql.Append(" LIMIT $limit");
            parameters.Add(new SqliteParameter("$limit", query.Limit.Value));
        }
        else if (!string.IsNullOrWhiteSpace(query.ContinuationToken))
        {
            sql.Append(" LIMIT -1");
        }

        var offset = DecodeContinuationToken(query.ContinuationToken);
        if (offset > 0)
        {
            sql.Append(" OFFSET $offset");
            parameters.Add(new SqliteParameter("$offset", offset));
        }

        return (sql.ToString(), parameters);
    }

    public (string Sql, List<SqliteParameter> Parameters) BuildVectorCandidateQuery(string collection, QueryEnvelope query, IEnumerable<string>? indexedMetadata = null)
    {
        if (query.Vector == null)
        {
            throw new ArgumentException("Vector search options are required.", nameof(query));
        }

        var parameters = new List<SqliteParameter>();
        var indexedPaths = ToIndexedPathSet(indexedMetadata);
        var sql = new StringBuilder();
        sql.Append("SELECT r.content_json, v.vector_data, v.dimensions FROM vyral_records r ");
        sql.Append("JOIN vyral_record_vectors v ON r.collection = v.collection AND r.partitionKey = v.partitionKey AND r.id = v.record_id");
        sql.Append(BuildWhereClause(collection, query, parameters, indexedPaths, "r"));
        sql.Append(" AND v.vector_name = $vectorName");
        parameters.Add(new SqliteParameter("$vectorName", query.Vector.Field));
        sql.Append(" ORDER BY r.partitionKey, r.id");
        return (sql.ToString(), parameters);
    }

    public (string Sql, List<SqliteParameter> Parameters) BuildLexicalFtsCandidateQuery(
        string collection,
        QueryEnvelope query,
        string matchExpression,
        IEnumerable<string>? indexedMetadata = null)
    {
        if (string.IsNullOrWhiteSpace(matchExpression))
        {
            throw new ArgumentException("FTS match expression is required.", nameof(matchExpression));
        }

        var parameters = new List<SqliteParameter>
        {
            new("$fts", matchExpression)
        };
        var indexedPaths = ToIndexedPathSet(indexedMetadata);
        var sql = new StringBuilder();
        sql.Append("SELECT r.content_json FROM vyral_records r ");
        sql.Append("JOIN vyral_record_fts ON r.collection = vyral_record_fts.collection ");
        sql.Append("AND r.partitionKey = vyral_record_fts.partitionKey ");
        sql.Append("AND r.id = vyral_record_fts.record_id ");
        sql.Append("WHERE vyral_record_fts MATCH $fts");
        sql.Append(BuildWhereClause(collection, query, parameters, indexedPaths, "r", " AND "));
        sql.Append(" ORDER BY bm25(vyral_record_fts), r.partitionKey, r.id");

        if (query.Limit.HasValue)
        {
            sql.Append(" LIMIT $limit");
            parameters.Add(new SqliteParameter("$limit", query.Limit.Value));
        }

        return (sql.ToString(), parameters);
    }

    private string BuildWhereClause(
        string collection,
        QueryEnvelope query,
        List<SqliteParameter> parameters,
        IReadOnlySet<string> indexedPaths,
        string? alias = null,
        string clausePrefix = " WHERE ")
    {
        var sql = new StringBuilder();
        var prefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";

        sql.Append($"{clausePrefix}{prefix}collection = $collection");
        parameters.Add(new SqliteParameter("$collection", collection));

        if (query.PartitionKeys?.Any() == true)
        {
            var partitionParams = new List<string>();
            for (int i = 0; i < query.PartitionKeys.Count; i++)
            {
                var parameterName = "$pk" + i.ToString(CultureInfo.InvariantCulture);
                partitionParams.Add(parameterName);
                parameters.Add(new SqliteParameter(parameterName, query.PartitionKeys[i]));
            }
            sql.Append($" AND {prefix}partitionKey IN ({string.Join(", ", partitionParams)})");
        }

        if (query.Filter != null)
        {
            var filterResult = BuildFilterClause(query.Filter, parameters, indexedPaths, alias);
            if (!string.IsNullOrWhiteSpace(filterResult))
            {
                sql.Append(" AND " + filterResult);
            }
        }

        return sql.ToString();
    }

    private string BuildFilterClause(FilterNode node, List<SqliteParameter> parameters, IReadOnlySet<string> indexedPaths, string? alias)
    {
        if (node.Children != null && !string.IsNullOrEmpty(node.Combine))
        {
            var separator = string.Equals(node.Combine, "any", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ";
            var clauses = node.Children.Select(c => BuildFilterClause(c, parameters, indexedPaths, alias)).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            if (clauses.Count == 0) return string.Empty;
            return "(" + string.Join(separator, clauses) + ")";
        }

        if (!string.IsNullOrEmpty(node.Path))
        {
            return BuildLeafCondition(node.Path, node.Op ?? FilterOps.Eq, node.Value, parameters, indexedPaths, alias);
        }

        return string.Empty;
    }

    private string BuildLeafCondition(string path, string op, object? rawValue, List<SqliteParameter> parameters, IReadOnlySet<string> indexedPaths, string? alias)
    {
        if (indexedPaths.Contains(path))
        {
            return BuildIndexedCondition(path, op, rawValue, parameters, alias);
        }

        var normalizedOp = FilterValueNormalizer.NormalizeOperator(op);
        var column = MapPathToColumn(path, alias);

        if (normalizedOp == "exists")
        {
            var shouldExist = FilterValueNormalizer.NormalizeExistsValue(rawValue);
            var existsExpression = MapPathToExistsExpression(path, alias);
            return shouldExist ? $"{existsExpression} IS NOT NULL" : $"{existsExpression} IS NULL";
        }

        if (normalizedOp == "in")
        {
            var values = FilterValueNormalizer.NormalizeScalarList(rawValue);
            if (values.Count == 0) return "0 = 1";

            var parameterNames = new List<string>();
            foreach (var item in values)
            {
                var parameterName = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                parameterNames.Add(parameterName);
                parameters.Add(new SqliteParameter(parameterName, item ?? DBNull.Value));
            }

            return $"{column} IN ({string.Join(", ", parameterNames)})";
        }

        var value = FilterValueNormalizer.NormalizeScalar(rawValue);
        if (value == null)
        {
            return normalizedOp switch
            {
                "eq" => BuildNullCondition(path, alias, isNull: true),
                "neq" => BuildNullCondition(path, alias, isNull: false),
                _ => throw new NotSupportedException($"Operator '{op}' cannot be used with null values.")
            };
        }

        if (normalizedOp is FilterOps.Contains or FilterOps.StartsWith)
        {
            if (value is not string text)
                throw new NotSupportedException($"Filter operator '{op}' requires a string value.");

            var textParameter = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new SqliteParameter(textParameter, text));
            return normalizedOp == FilterOps.Contains
                ? $"instr({column}, {textParameter}) > 0"
                : $"substr({column}, 1, length({textParameter})) = {textParameter}";
        }

        var paramName = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters.Add(new SqliteParameter(paramName, value));

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

        return $"{column} {sqlOp} {paramName}";
    }

    private string BuildIndexedCondition(string path, string op, object? rawValue, List<SqliteParameter> parameters, string? alias)
    {
        var normalizedOp = FilterValueNormalizer.NormalizeOperator(op);
        var pathParameter = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters.Add(new SqliteParameter(pathParameter, path));

        var outerPrefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";
        var correlation = "mi.collection = " + outerPrefix + "collection " +
            "AND mi.partitionKey = " + outerPrefix + "partitionKey " +
            "AND mi.record_id = " + outerPrefix + "id " +
            "AND mi.path = " + pathParameter;

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
            if (values.Count == 0) return "0 = 1";

            var parameterNames = new List<string>();
            foreach (var item in values)
            {
                var parameterName = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                parameterNames.Add(parameterName);
                parameters.Add(new SqliteParameter(parameterName, SerializeIndexValue(item)));
            }

            return $"EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation} AND mi.value_json IN ({string.Join(", ", parameterNames)}))";
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

            var textParameter = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new SqliteParameter(textParameter, text));
            return normalizedOp == FilterOps.Contains
                ? $"EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation} AND instr(mi.value_text, {textParameter}) > 0)"
                : $"EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation} AND substr(mi.value_text, 1, length({textParameter})) = {textParameter})";
        }

        var valueParameter = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        var (column, parameterValue) = GetIndexedComparisonTarget(value);
        parameters.Add(new SqliteParameter(valueParameter, parameterValue));

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

        if (column != "mi.value_number" && normalizedOp is "gt" or "gte" or "lt" or "lte")
            throw new NotSupportedException($"Indexed range filter '{path}' requires a numeric value.");

        return $"EXISTS (SELECT 1 FROM vyral_record_metadata_index mi WHERE {correlation} AND {column} {sqlOp} {valueParameter})";
    }

    private string MapPathToColumn(string path, string? alias = null)
    {
        var prefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";

        return TryMapDirectColumn(path, alias, out var column)
            ? column
            : $"json_extract({prefix}content_json, '{ToSqliteJsonPath(path)}')";
    }

    private string MapOrderPath(string path, List<SqliteParameter> parameters, IReadOnlySet<string> indexedPaths, string? alias = null)
    {
        if (!indexedPaths.Contains(path))
        {
            return MapPathToColumn(path, alias);
        }

        var pathParameter = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters.Add(new SqliteParameter(pathParameter, path));
        var outerPrefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";

        return "(SELECT COALESCE(mi.value_number, mi.value_text, mi.value_bool, mi.value_json) " +
            "FROM vyral_record_metadata_index mi " +
            "WHERE mi.collection = " + outerPrefix + "collection " +
            "AND mi.partitionKey = " + outerPrefix + "partitionKey " +
            "AND mi.record_id = " + outerPrefix + "id " +
            "AND mi.path = " + pathParameter + " LIMIT 1)";
    }

    private string MapPathToExistsExpression(string path, string? alias = null)
    {
        if (TryMapDirectColumn(path, alias, out var column))
        {
            return column;
        }

        var prefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";
        return $"json_type({prefix}content_json, '{ToSqliteJsonPath(path)}')";
    }

    private string BuildNullCondition(string path, string? alias, bool isNull)
    {
        if (TryMapDirectColumn(path, alias, out var column))
        {
            return isNull ? $"{column} IS NULL" : $"{column} IS NOT NULL";
        }

        var jsonType = MapPathToExistsExpression(path, alias);
        return isNull
            ? $"{jsonType} = 'null'"
            : $"({jsonType} IS NOT NULL AND {jsonType} != 'null')";
    }

    private static bool TryMapDirectColumn(string path, string? alias, out string column)
    {
        var prefix = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias + ".";

        column = path.ToLowerInvariant() switch
        {
            "/id" => $"{prefix}id",
            "/partitionkey" => $"{prefix}partitionKey",
            "/updatedat" => $"{prefix}updated_at",
            "/revision" => $"{prefix}revision",
            _ => string.Empty
        };

        return column.Length > 0;
    }

    private static string NormalizeDirection(string direction)
    {
        return direction.ToLowerInvariant() switch
        {
            "asc" => "ASC",
            "desc" => "DESC",
            _ => throw new NotSupportedException($"Order direction '{direction}' is not supported.")
        };
    }

    private static string ToSqliteJsonPath(string path)
    {
        if (path.StartsWith("$.", StringComparison.Ordinal))
        {
            if (!path.All(c => char.IsLetterOrDigit(c) || c is '$' or '.' or '_' or '-'))
            {
                throw new NotSupportedException($"JSON path '{path}' is not supported.");
            }
            return path;
        }

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Query path '{path}' must be a JSON pointer such as '/metadata/status'.");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Replace("~1", "/").Replace("~0", "~"))
            .ToList();

        if (segments.Any(segment => segment.Length == 0 || !segment.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')))
        {
            throw new NotSupportedException($"JSON pointer '{path}' contains unsupported segment characters.");
        }

        return "$." + string.Join(".", segments);
    }

    private static IReadOnlySet<string> ToIndexedPathSet(IEnumerable<string>? indexedMetadata)
    {
        return indexedMetadata == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(indexedMetadata, StringComparer.Ordinal);
    }

    private static string SerializeIndexValue(object? value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static (string Column, object Value) GetIndexedComparisonTarget(object value)
    {
        return value switch
        {
            string text => ("mi.value_text", text),
            bool boolean => ("mi.value_bool", boolean ? 1 : 0),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => ("mi.value_number", Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            _ => ("mi.value_json", SerializeIndexValue(value))
        };
    }

    private static int DecodeContinuationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var offset = int.Parse(decoded, CultureInfo.InvariantCulture);
            if (offset < 0) throw new NotSupportedException("Continuation token offset must be non-negative.");
            return offset;
        }
        catch (FormatException ex)
        {
            throw new NotSupportedException("Continuation token is not valid for the local SQLite adapter.", ex);
        }
    }
}
