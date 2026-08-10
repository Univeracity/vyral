using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public class SqliteTraceStore : ITraceStore
{
    private const int DefaultTraceListLimit = 100;
    private const int MaxTraceListLimit = 5000;
    private const int DefaultTraceExportLimit = 100;
    private const int MaxTraceExportLimit = 5000;
    private const int DefaultTracePruneLimit = 100;
    private const int MaxTracePruneLimit = 5000;
    private const int MaxTraceSummaryDistinctValues = 64;
    private const int MaxTraceSummaryValueLength = 128;
    private const string TraceSummaryOtherKey = "_other";

    private readonly string _connectionString;

    public SqliteTraceStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var migrationManager = new SqliteMigrationManager(_connectionString);
        await migrationManager.MigrateAsync(ct);
    }

    public async Task WriteTraceAsync(TraceRecord trace, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trace.Id)) trace.Id = TraceRecord.CreateId();
        if (string.IsNullOrWhiteSpace(trace.Operation)) throw new InvalidOperationException("Trace operation is required.");
        if (trace.CreatedAt == default) trace.CreatedAt = DateTime.UtcNow;

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO vyral_traces (
                id,
                operation,
                adapter,
                request_json,
                result_summary_json,
                started_at,
                duration_ms,
                created_at
            )
            VALUES (
                $id,
                $operation,
                $adapter,
                $request_json,
                $result_summary_json,
                $started_at,
                $duration_ms,
                $created_at
            );";
        command.Parameters.AddWithValue("$id", trace.Id);
        command.Parameters.AddWithValue("$operation", trace.Operation);
        command.Parameters.AddWithValue("$adapter", (object?)trace.Adapter ?? DBNull.Value);
        command.Parameters.AddWithValue("$request_json", JsonSerializer.Serialize(trace.Request));
        command.Parameters.AddWithValue("$result_summary_json", JsonSerializer.Serialize(trace.ResultSummary));
        command.Parameters.AddWithValue("$started_at", trace.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$duration_ms", trace.DurationMs);
        command.Parameters.AddWithValue("$created_at", trace.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<TraceRecord?> GetTraceAsync(string id, CancellationToken ct = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, operation, adapter, request_json, result_summary_json, started_at, duration_ms, created_at
            FROM vyral_traces
            WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return ReadTrace(reader);
    }

    public async Task<IEnumerable<TraceRecord>> ListTracesAsync(string? operation = null, int? limit = null, CancellationToken ct = default)
    {
        var effectiveLimit = ValidateLimit(limit, DefaultTraceListLimit, MaxTraceListLimit, "Trace list limit");
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, operation, adapter, request_json, result_summary_json, started_at, duration_ms, created_at
            FROM vyral_traces
            WHERE ($operation IS NULL OR operation = $operation)
            ORDER BY created_at DESC, id ASC
            LIMIT $limit;";
        command.Parameters.AddWithValue("$operation", (object?)operation ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", effectiveLimit);

        var traces = new List<TraceRecord>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            traces.Add(ReadTrace(reader));
        }

        return traces;
    }

    public async Task<TraceSummary> SummarizeTracesAsync(string? operation = null, CancellationToken ct = default)
    {
        operation = string.IsNullOrWhiteSpace(operation) ? null : operation.Trim();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT operation, adapter, request_json, result_summary_json, created_at
            FROM vyral_traces
            WHERE ($operation IS NULL OR operation = $operation)
            ORDER BY operation ASC, created_at ASC, id ASC;";
        command.Parameters.AddWithValue("$operation", (object?)operation ?? DBNull.Value);

        var summary = new TraceSummary
        {
            Operation = operation
        };
        var operationsByName = new Dictionary<string, TraceOperationSummary>(StringComparer.Ordinal);
        var adaptersByOperation = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var operationName = reader.GetString(0);
            if (!operationsByName.TryGetValue(operationName, out var item))
            {
                item = new TraceOperationSummary
                {
                    Operation = operationName
                };
                operationsByName[operationName] = item;
                adaptersByOperation[operationName] = new SortedSet<string>(StringComparer.Ordinal);
            }

            var adapter = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(adapter))
            {
                adaptersByOperation[operationName].Add(adapter);
            }

            var request = JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(2)) ?? new();
            var resultSummary = JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(3)) ?? new();
            var createdAt = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind);

            item.Count++;
            summary.TotalCount++;
            if (item.Count == 1 || createdAt < item.FirstCreatedAt)
            {
                item.FirstCreatedAt = createdAt;
            }

            if (item.Count == 1 || createdAt > item.LatestCreatedAt)
            {
                item.LatestCreatedAt = createdAt;
            }

            AddTraceDiagnosticCounts(summary, item, request, resultSummary);
        }

        summary.Operations = operationsByName.Values
            .OrderBy(item => item.Operation, StringComparer.Ordinal)
            .ToList();
        SortCounts(summary.StatusCounts);
        SortCounts(summary.FailureClassCounts);
        SortCounts(summary.ProviderStatusCounts);
        SortCounts(summary.ProviderCounts);
        SortCounts(summary.CapabilityCounts);

        foreach (var item in summary.Operations)
        {
            item.Adapters = adaptersByOperation.TryGetValue(item.Operation, out var adapters)
                ? adapters.ToList()
                : new List<string>();
            SortCounts(item.StatusCounts);
            SortCounts(item.FailureClassCounts);
            SortCounts(item.ProviderStatusCounts);
            SortCounts(item.ProviderCounts);
            SortCounts(item.CapabilityCounts);
        }

        return summary;
    }

    public async Task<TraceExportBundle> ExportTracesAsync(TraceExportRequest request, CancellationToken ct = default)
    {
        ValidateExportRequest(request);

        var operation = string.IsNullOrWhiteSpace(request.Operation) ? null : request.Operation.Trim();
        var limit = request.Limit ?? DefaultTraceExportLimit;
        var traces = (await ListTracesAsync(operation, limit, ct)).ToList();
        var warnings = new List<TraceExportWarning>();
        foreach (var trace in traces)
        {
            AddUnsafeContentWarnings(trace, warnings);
        }

        if (request.FailOnUnsafeContent && warnings.Count > 0)
        {
            throw new InvalidOperationException($"Trace export detected {warnings.Count} potentially unsafe trace field(s). Review warnings by exporting without failOnUnsafeContent before sharing the bundle.");
        }

        var bundle = new TraceExportBundle
        {
            ExportedAt = DateTime.UtcNow,
            Operation = operation,
            Limit = limit,
            TraceCount = traces.Count,
            WarningCount = warnings.Count,
            Warnings = warnings,
            Traces = traces
        };
        bundle.ContentHash = ComputeBundleHash(bundle);
        return bundle;
    }

    public async Task<TracePruneResult> PruneTracesAsync(TracePruneRequest request, CancellationToken ct = default)
    {
        ValidatePruneRequest(request);

        var operation = string.IsNullOrWhiteSpace(request.Operation) ? null : request.Operation.Trim();
        var olderThan = request.OlderThan?.ToUniversalTime();
        var keepLatest = request.KeepLatest ?? 0;
        var limit = request.Limit ?? DefaultTracePruneLimit;

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var candidateIds = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = @"
                SELECT id
                FROM vyral_traces
                WHERE ($operation IS NULL OR operation = $operation)
                    AND ($older_than IS NULL OR created_at < $older_than)
                ORDER BY created_at DESC, id ASC;";
            select.Parameters.AddWithValue("$operation", (object?)operation ?? DBNull.Value);
            select.Parameters.AddWithValue("$older_than", olderThan.HasValue ? olderThan.Value.ToString("O") : DBNull.Value);

            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                candidateIds.Add(reader.GetString(0));
            }
        }

        var idsToDelete = candidateIds
            .Skip(keepLatest)
            .Take(limit)
            .ToList();

        var result = new TracePruneResult
        {
            Operation = operation,
            OlderThan = olderThan,
            KeepLatest = request.KeepLatest,
            Limit = limit,
            DryRun = request.DryRun,
            MatchedCount = idsToDelete.Count,
            DeletedCount = request.DryRun ? 0 : idsToDelete.Count,
            MatchedIds = idsToDelete,
            DeletedIds = request.DryRun ? new List<string>() : idsToDelete
        };

        if (request.DryRun || idsToDelete.Count == 0)
        {
            return result;
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var id in idsToDelete)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM vyral_traces WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                await delete.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static TraceRecord ReadTrace(SqliteDataReader reader)
    {
        return new TraceRecord
        {
            Id = reader.GetString(0),
            Operation = reader.GetString(1),
            Adapter = reader.IsDBNull(2) ? null : reader.GetString(2),
            Request = JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(3)) ?? new(),
            ResultSummary = JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(4)) ?? new(),
            StartedAt = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DurationMs = reader.GetDouble(6),
            CreatedAt = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    private static void AddTraceDiagnosticCounts(
        TraceSummary summary,
        TraceOperationSummary item,
        IReadOnlyDictionary<string, object?> request,
        IReadOnlyDictionary<string, object?> resultSummary)
    {
        Increment(summary.StatusCounts, ExtractScalarString(resultSummary, "status"));
        Increment(item.StatusCounts, ExtractScalarString(resultSummary, "status"));
        Increment(summary.FailureClassCounts, ExtractScalarString(resultSummary, "failureClass"));
        Increment(item.FailureClassCounts, ExtractScalarString(resultSummary, "failureClass"));
        Increment(summary.ProviderStatusCounts, ExtractScalarString(resultSummary, "providerStatus"));
        Increment(item.ProviderStatusCounts, ExtractScalarString(resultSummary, "providerStatus"));

        var provider = ExtractScalarString(request, "provider") ?? ExtractScalarString(resultSummary, "provider");
        Increment(summary.ProviderCounts, provider);
        Increment(item.ProviderCounts, provider);

        var capability = ExtractScalarString(request, "capability") ?? ExtractScalarString(resultSummary, "capability");
        Increment(summary.CapabilityCounts, capability);
        Increment(item.CapabilityCounts, capability);
    }

    private static string? ExtractScalarString(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => NormalizeSummaryValue(text),
            JsonElement json => ExtractScalarString(json),
            bool boolean => boolean ? "true" : "false",
            Enum enumValue => enumValue.ToString(),
            _ => NormalizeSummaryValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))
        };
    }

    private static string? ExtractScalarString(JsonElement json)
    {
        return json.ValueKind switch
        {
            JsonValueKind.String => NormalizeSummaryValue(json.GetString()),
            JsonValueKind.Number => NormalizeSummaryValue(json.GetRawText()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? NormalizeSummaryValue(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxTraceSummaryValueLength
            ? trimmed
            : trimmed[..MaxTraceSummaryValueLength];
    }

    private static void Increment(Dictionary<string, int> counts, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!counts.ContainsKey(value) && counts.Count >= MaxTraceSummaryDistinctValues)
        {
            value = TraceSummaryOtherKey;
        }

        counts[value] = counts.TryGetValue(value, out var count) ? count + 1 : 1;
    }

    private static void SortCounts(Dictionary<string, int> counts)
    {
        var ordered = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();
        counts.Clear();
        foreach (var (key, value) in ordered)
        {
            counts[key] = value;
        }
    }

    private static void ValidateExportRequest(TraceExportRequest request)
    {
        _ = ValidateLimit(request.Limit, DefaultTraceExportLimit, MaxTraceExportLimit, "Trace export limit");
    }

    private static string ComputeBundleHash(TraceExportBundle bundle)
    {
        var hashPayload = new
        {
            bundle.FormatVersion,
            bundle.Operation,
            bundle.Limit,
            bundle.TraceCount,
            bundle.WarningCount,
            bundle.Warnings,
            bundle.Traces
        };
        var json = JsonSerializer.Serialize(hashPayload);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static void AddUnsafeContentWarnings(TraceRecord trace, List<TraceExportWarning> warnings)
    {
        AddUnsafeContentWarnings(trace.Id, "request", trace.Request, warnings);
        AddUnsafeContentWarnings(trace.Id, "resultSummary", trace.ResultSummary, warnings);
    }

    private static void AddUnsafeContentWarnings(string traceId, string location, IReadOnlyDictionary<string, object?> values, List<TraceExportWarning> warnings)
    {
        foreach (var (key, value) in values)
        {
            var childLocation = $"{location}.{key}";
            if (IsSensitiveFieldName(key))
            {
                warnings.Add(new TraceExportWarning
                {
                    TraceId = traceId,
                    Location = childLocation,
                    Reason = "sensitive_field_name"
                });
            }

            AddUnsafeContentWarnings(traceId, childLocation, value, warnings);
        }
    }

    private static void AddUnsafeContentWarnings(string traceId, string location, object? value, List<TraceExportWarning> warnings)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                if (LooksLikeBearerToken(text))
                {
                    warnings.Add(new TraceExportWarning
                    {
                        TraceId = traceId,
                        Location = location,
                        Reason = "bearer_token_value"
                    });
                }

                return;
            case JsonElement json:
                AddUnsafeJsonWarnings(traceId, location, json, warnings);
                return;
            case IReadOnlyDictionary<string, object?> dictionary:
                AddUnsafeContentWarnings(traceId, location, dictionary, warnings);
                return;
            default:
                return;
        }
    }

    private static void AddUnsafeJsonWarnings(string traceId, string location, JsonElement json, List<TraceExportWarning> warnings)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in json.EnumerateObject())
                {
                    var childLocation = $"{location}.{property.Name}";
                    if (IsSensitiveFieldName(property.Name))
                    {
                        warnings.Add(new TraceExportWarning
                        {
                            TraceId = traceId,
                            Location = childLocation,
                            Reason = "sensitive_field_name"
                        });
                    }

                    AddUnsafeJsonWarnings(traceId, childLocation, property.Value, warnings);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in json.EnumerateArray())
                {
                    AddUnsafeJsonWarnings(traceId, $"{location}[{index}]", item, warnings);
                    index++;
                }

                break;
            case JsonValueKind.String:
                var text = json.GetString();
                if (!string.IsNullOrWhiteSpace(text) && LooksLikeBearerToken(text))
                {
                    warnings.Add(new TraceExportWarning
                    {
                        TraceId = traceId,
                        Location = location,
                        Reason = "bearer_token_value"
                    });
                }

                break;
        }
    }

    private static bool IsSensitiveFieldName(string key)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized is "apikey"
            or "xapikey"
            or "authorization"
            or "password"
            or "passwd"
            or "secret"
            or "clientsecret"
            or "accesstoken"
            or "refreshtoken"
            or "privatekey"
            or "credential"
            or "credentials"
            or "token"
            or "authtoken";
    }

    private static bool LooksLikeBearerToken(string value)
    {
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidatePruneRequest(TracePruneRequest request)
    {
        if (request.KeepLatest.HasValue && request.KeepLatest.Value < 0)
        {
            throw new InvalidOperationException("Trace prune keepLatest must be non-negative.");
        }

        if (request.Limit.HasValue && request.Limit.Value <= 0)
        {
            throw new InvalidOperationException("Trace prune limit must be greater than zero.");
        }

        if (request.Limit.HasValue && request.Limit.Value > MaxTracePruneLimit)
        {
            throw new InvalidOperationException($"Trace prune limit cannot exceed {MaxTracePruneLimit}.");
        }

        if (string.IsNullOrWhiteSpace(request.Operation) &&
            request.OlderThan is null &&
            request.KeepLatest is null)
        {
            throw new InvalidOperationException("Trace prune requires at least one constraint: operation, olderThan, or keepLatest.");
        }
    }

    private static int ValidateLimit(int? limit, int defaultLimit, int maxLimit, string description)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new InvalidOperationException($"{description} must be greater than zero.");
        }

        var effectiveLimit = limit ?? defaultLimit;
        if (effectiveLimit > maxLimit)
        {
            throw new InvalidOperationException($"{description} cannot exceed {maxLimit}.");
        }

        return effectiveLimit;
    }
}
