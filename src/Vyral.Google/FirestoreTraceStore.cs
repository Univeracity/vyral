using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Google;

/// <summary>
/// ITraceStore backed by Google Cloud Firestore.
/// </summary>
public class FirestoreTraceStore : ITraceStore
{
    private const string TraceCollectionSuffix = "_traces";
    private const int DefaultTraceListLimit = 100;
    private const int MaxTraceListLimit = 5000;
    private const int DefaultTraceExportLimit = 100;
    private const int MaxTraceExportLimit = 5000;
    private const int DefaultTracePruneLimit = 100;
    private const int MaxTracePruneLimit = 5000;
    private const int MaxTraceSummaryDistinctValues = 64;
    private const int MaxTraceSummaryValueLength = 128;
    private const string TraceSummaryOtherKey = "_other";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FirestoreDb _db;
    private readonly string _rootCollection;

    public FirestoreTraceStore(FirestoreDb db, string rootCollection = "vyral")
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _rootCollection = NormalizeRootCollection(rootCollection);
    }

    public async Task WriteTraceAsync(TraceRecord trace, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trace.Id)) trace.Id = TraceRecord.CreateId();
        if (string.IsNullOrWhiteSpace(trace.Operation)) throw new InvalidOperationException("Trace operation is required.");
        if (trace.CreatedAt == default) trace.CreatedAt = DateTime.UtcNow;

        await Traces.Document(trace.Id).SetAsync(new Dictionary<string, object?>
        {
            ["id"] = trace.Id,
            ["operation"] = trace.Operation,
            ["adapter"] = trace.Adapter,
            ["requestJson"] = JsonSerializer.Serialize(trace.Request, JsonOptions),
            ["resultSummaryJson"] = JsonSerializer.Serialize(trace.ResultSummary, JsonOptions),
            ["startedAt"] = Timestamp.FromDateTime(DateTime.SpecifyKind(trace.StartedAt.ToUniversalTime(), DateTimeKind.Utc)),
            ["durationMs"] = trace.DurationMs,
            ["createdAt"] = Timestamp.FromDateTime(DateTime.SpecifyKind(trace.CreatedAt.ToUniversalTime(), DateTimeKind.Utc))
        }, cancellationToken: ct);
    }

    public async Task<TraceRecord?> GetTraceAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Trace id is required.", nameof(id));

        var snapshot = await Traces.Document(id).GetSnapshotAsync(ct);
        return snapshot.Exists ? ReadTrace(snapshot) : null;
    }

    public async Task<IEnumerable<TraceRecord>> ListTracesAsync(string? operation = null, int? limit = null, CancellationToken ct = default)
    {
        var effectiveLimit = ValidateLimit(limit, DefaultTraceListLimit, MaxTraceListLimit, "Trace list limit");
        operation = string.IsNullOrWhiteSpace(operation) ? null : operation.Trim();

        Query query = Traces;
        if (operation != null)
        {
            query = query.WhereEqualTo("operation", operation);
        }

        var snapshot = await query.GetSnapshotAsync(ct);
        return snapshot.Documents
            .Select(ReadTrace)
            .Where(trace => trace != null)
            .Select(trace => trace!)
            .OrderByDescending(trace => trace.CreatedAt)
            .ThenBy(trace => trace.Id, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .ToList();
    }

    public async Task<TraceSummary> SummarizeTracesAsync(string? operation = null, CancellationToken ct = default)
    {
        operation = string.IsNullOrWhiteSpace(operation) ? null : operation.Trim();

        Query query = Traces;
        if (operation != null)
        {
            query = query.WhereEqualTo("operation", operation);
        }

        var snapshot = await query.GetSnapshotAsync(ct);
        var summary = new TraceSummary
        {
            Operation = operation
        };
        var operationsByName = new Dictionary<string, TraceOperationSummary>(StringComparer.Ordinal);
        var adaptersByOperation = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var trace in snapshot.Documents.Select(ReadTrace).Where(trace => trace != null).Select(trace => trace!))
        {
            if (!operationsByName.TryGetValue(trace.Operation, out var item))
            {
                item = new TraceOperationSummary
                {
                    Operation = trace.Operation
                };
                operationsByName[trace.Operation] = item;
                adaptersByOperation[trace.Operation] = new SortedSet<string>(StringComparer.Ordinal);
            }

            if (!string.IsNullOrWhiteSpace(trace.Adapter))
            {
                adaptersByOperation[trace.Operation].Add(trace.Adapter);
            }

            item.Count++;
            summary.TotalCount++;
            if (item.Count == 1 || trace.CreatedAt < item.FirstCreatedAt)
            {
                item.FirstCreatedAt = trace.CreatedAt;
            }

            if (item.Count == 1 || trace.CreatedAt > item.LatestCreatedAt)
            {
                item.LatestCreatedAt = trace.CreatedAt;
            }

            AddTraceDiagnosticCounts(summary, item, trace.Request, trace.ResultSummary);
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
        var traces = (await ListTracesAsync(operation, MaxTraceListLimit, ct))
            .Where(trace => !olderThan.HasValue || trace.CreatedAt < olderThan.Value)
            .OrderByDescending(trace => trace.CreatedAt)
            .ThenBy(trace => trace.Id, StringComparer.Ordinal)
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
            MatchedCount = traces.Count,
            DeletedCount = request.DryRun ? 0 : traces.Count,
            MatchedIds = traces.Select(trace => trace.Id).ToList(),
            DeletedIds = request.DryRun ? new List<string>() : traces.Select(trace => trace.Id).ToList()
        };

        if (request.DryRun || traces.Count == 0)
        {
            return result;
        }

        var batch = _db.StartBatch();
        foreach (var trace in traces)
        {
            batch.Delete(Traces.Document(trace.Id));
        }

        await batch.CommitAsync(ct);
        return result;
    }

    private CollectionReference Traces => _db.Collection(_rootCollection + TraceCollectionSuffix);

    private static TraceRecord? ReadTrace(DocumentSnapshot snapshot)
    {
        if (!snapshot.TryGetValue<string>("id", out var id) ||
            !snapshot.TryGetValue<string>("operation", out var operation))
        {
            return null;
        }

        var startedAt = snapshot.TryGetValue<Timestamp>("startedAt", out var startedAtValue)
            ? startedAtValue.ToDateTime()
            : DateTime.MinValue;
        var createdAt = snapshot.TryGetValue<Timestamp>("createdAt", out var createdAtValue)
            ? createdAtValue.ToDateTime()
            : DateTime.MinValue;

        return new TraceRecord
        {
            Id = id,
            Operation = operation,
            Adapter = snapshot.TryGetValue<string>("adapter", out var adapter) ? adapter : null,
            Request = snapshot.TryGetValue<string>("requestJson", out var requestJson)
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(requestJson, JsonOptions) ?? new()
                : new Dictionary<string, object?>(),
            ResultSummary = snapshot.TryGetValue<string>("resultSummaryJson", out var resultSummaryJson)
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(resultSummaryJson, JsonOptions) ?? new()
                : new Dictionary<string, object?>(),
            StartedAt = startedAt,
            DurationMs = snapshot.TryGetValue<double>("durationMs", out var duration) ? duration : 0,
            CreatedAt = createdAt
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

    private static void ValidatePruneRequest(TracePruneRequest request)
    {
        _ = ValidateLimit(request.Limit, DefaultTracePruneLimit, MaxTracePruneLimit, "Trace prune limit");
        if (request.KeepLatest < 0)
        {
            throw new InvalidOperationException("Trace prune keepLatest must be non-negative.");
        }
    }

    private static int ValidateLimit(int? limit, int defaultLimit, int maxLimit, string description)
    {
        if (limit <= 0)
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
        var json = JsonSerializer.Serialize(hashPayload, JsonOptions);
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
            or "bearertoken"
            or "token"
            or "secret"
            or "password"
            or "clientsecret";
    }

    private static bool LooksLikeBearerToken(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRootCollection(string rootCollection)
    {
        var value = string.IsNullOrWhiteSpace(rootCollection) ? "vyral" : rootCollection.Trim();
        if (value.Contains('/'))
        {
            throw new InvalidOperationException("Firestore root collection prefix must not contain '/'.");
        }

        if (value is "." or "..")
        {
            throw new InvalidOperationException("Firestore root collection prefix cannot be '.' or '..'.");
        }

        return value;
    }
}
