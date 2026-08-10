using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public class CollectionExportEnvelope
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("policy")]
    public RecordCollectionPolicy Policy { get; set; } = new();

    [JsonPropertyName("records")]
    public List<VyralRecord> Records { get; set; } = new();

    [JsonPropertyName("query")]
    public QueryEnvelope? Query { get; set; }

    [JsonPropertyName("maxRecords")]
    public int? MaxRecords { get; set; }

    [JsonPropertyName("recordCount")]
    public int? RecordCount { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }

    [JsonPropertyName("exportedAt")]
    public DateTime? ExportedAt { get; set; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; set; }
}

public class CollectionExportRequest
{
    [JsonPropertyName("query")]
    public QueryEnvelope? Query { get; set; }

    [JsonPropertyName("maxRecords")]
    public int? MaxRecords { get; set; }

    [JsonPropertyName("failOnLimitExceeded")]
    public bool FailOnLimitExceeded { get; set; } = true;
}

public class CollectionImportRequest
{
    [JsonPropertyName("snapshot")]
    public CollectionExportEnvelope Snapshot { get; set; } = new();

    [JsonPropertyName("expectedContentHash")]
    public string? ExpectedContentHash { get; set; }

    [JsonPropertyName("replaceExisting")]
    public bool ReplaceExisting { get; set; }

    [JsonPropertyName("continueOnError")]
    public bool ContinueOnError { get; set; }

    [JsonPropertyName("allowCollectionRename")]
    public bool AllowCollectionRename { get; set; }

    [JsonPropertyName("allowPartialSnapshot")]
    public bool AllowPartialSnapshot { get; set; }
}

public class CollectionImportResult
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("sourceCollection")]
    public string SourceCollection { get; set; } = string.Empty;

    [JsonPropertyName("policyStatus")]
    public string PolicyStatus { get; set; } = string.Empty;

    [JsonPropertyName("recordCount")]
    public int RecordCount { get; set; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("contentHashComparison")]
    public CollectionSnapshotHashComparison ContentHashComparison { get; set; } = new();

    [JsonPropertyName("records")]
    public RecordBatchUpsertResult Records { get; set; } = new();
}

public class CollectionSnapshotHashComparison
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "collectionSnapshot";

    [JsonPropertyName("expectedHash")]
    public string? ExpectedHash { get; set; }

    [JsonPropertyName("actualHash")]
    public string? ActualHash { get; set; }

    [JsonPropertyName("compared")]
    public bool Compared { get; set; }

    [JsonPropertyName("matches")]
    public bool Matches { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = CollectionSnapshotHashStatuses.NotProvided;
}

public static class CollectionImportPolicyStatuses
{
    public const string Created = "created";
    public const string ExistingEquivalent = "existing_equivalent";
    public const string Replaced = "replaced";
}

public static class CollectionSnapshotHashStatuses
{
    public const string NotProvided = "not_provided";
    public const string ActualMissing = "actual_missing";
    public const string Matched = "matched";
    public const string Drifted = "drifted";
}

public static class CollectionSnapshotLimits
{
    public const int MaxRecords = 10000;
}
