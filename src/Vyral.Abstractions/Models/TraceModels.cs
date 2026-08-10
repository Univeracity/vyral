using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Vyral.Primitives;

namespace Vyral.Abstractions.Models;

public class TraceRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = CreateId();

    public static string CreateId()
    {
        return OrderedId.CreateString();
    }

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("adapter")]
    public string? Adapter { get; set; }

    [JsonPropertyName("request")]
    public Dictionary<string, object?> Request { get; set; } = new();

    [JsonPropertyName("resultSummary")]
    public Dictionary<string, object?> ResultSummary { get; set; } = new();

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    [JsonPropertyName("durationMs")]
    public double DurationMs { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TracePruneRequest
{
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("olderThan")]
    public DateTime? OlderThan { get; set; }

    [JsonPropertyName("keepLatest")]
    public int? KeepLatest { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }
}

public class TraceExportRequest
{
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("failOnUnsafeContent")]
    public bool FailOnUnsafeContent { get; set; }
}

public class TraceSummary
{
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("statusCounts")]
    public Dictionary<string, int> StatusCounts { get; set; } = new();

    [JsonPropertyName("failureClassCounts")]
    public Dictionary<string, int> FailureClassCounts { get; set; } = new();

    [JsonPropertyName("providerStatusCounts")]
    public Dictionary<string, int> ProviderStatusCounts { get; set; } = new();

    [JsonPropertyName("providerCounts")]
    public Dictionary<string, int> ProviderCounts { get; set; } = new();

    [JsonPropertyName("capabilityCounts")]
    public Dictionary<string, int> CapabilityCounts { get; set; } = new();

    [JsonPropertyName("operations")]
    public List<TraceOperationSummary> Operations { get; set; } = new();
}

public class TraceOperationSummary
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("firstCreatedAt")]
    public DateTime FirstCreatedAt { get; set; }

    [JsonPropertyName("latestCreatedAt")]
    public DateTime LatestCreatedAt { get; set; }

    [JsonPropertyName("adapters")]
    public List<string> Adapters { get; set; } = new();

    [JsonPropertyName("statusCounts")]
    public Dictionary<string, int> StatusCounts { get; set; } = new();

    [JsonPropertyName("failureClassCounts")]
    public Dictionary<string, int> FailureClassCounts { get; set; } = new();

    [JsonPropertyName("providerStatusCounts")]
    public Dictionary<string, int> ProviderStatusCounts { get; set; } = new();

    [JsonPropertyName("providerCounts")]
    public Dictionary<string, int> ProviderCounts { get; set; } = new();

    [JsonPropertyName("capabilityCounts")]
    public Dictionary<string, int> CapabilityCounts { get; set; } = new();
}

public class TraceExportBundle
{
    [JsonPropertyName("formatVersion")]
    public string FormatVersion { get; set; } = "vyral.trace-export.v1";

    [JsonPropertyName("exportedAt")]
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("traceCount")]
    public int TraceCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("warnings")]
    public List<TraceExportWarning> Warnings { get; set; } = new();

    [JsonPropertyName("traces")]
    public List<TraceRecord> Traces { get; set; } = new();
}

public class TraceExportWarning
{
    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public class TracePruneResult
{
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("olderThan")]
    public DateTime? OlderThan { get; set; }

    [JsonPropertyName("keepLatest")]
    public int? KeepLatest { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("matchedCount")]
    public int MatchedCount { get; set; }

    [JsonPropertyName("deletedCount")]
    public int DeletedCount { get; set; }

    [JsonPropertyName("matchedIds")]
    public List<string> MatchedIds { get; set; } = new();

    [JsonPropertyName("deletedIds")]
    public List<string> DeletedIds { get; set; } = new();
}
