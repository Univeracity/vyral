using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

/// <summary>
/// Span within normalized source text. CharStart/CharEnd are zero-based UTF-16
/// character offsets, matching .NET/JavaScript string indexing. Store byte
/// offsets in Extensions when a source requires byte-addressed citations.
/// </summary>
public class VyralSourceSpan
{
    [JsonPropertyName("charStart")]
    public int? CharStart { get; set; }

    [JsonPropertyName("charEnd")]
    public int? CharEnd { get; set; }

    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("column")]
    public int? Column { get; set; }

    [JsonPropertyName("anchor")]
    public string? Anchor { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>
/// Content holds text and structured content fields used for embedding and
/// retrieval (e.g. Content["text"]). Metadata holds attributes indexed for
/// filtering via RecordCollectionPolicy.IndexedMetadata. Fields stored only
/// in Content are NOT filterable via FilterNode — add them to Metadata too
/// if you need to filter on them.
/// </summary>
public class VyralRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }

    [JsonPropertyName("content")]
    public JsonObject? Content { get; set; }

    [JsonPropertyName("sources")]
    public List<VyralSourceReference>? Sources { get; set; }

    [JsonPropertyName("vectors")]
    public Dictionary<string, VyralVector>? Vectors { get; set; }

    [JsonPropertyName("etag")]
    public string? Etag { get; set; }

    [JsonPropertyName("revision")]
    public int? Revision { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public class VyralSourceReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("span")]
    public VyralSourceSpan? Span { get; set; }
}

public class VyralVector
{
    [JsonPropertyName("values")]
    public float[] Values { get; set; } = Array.Empty<float>();

    [JsonPropertyName("dimensions")]
    public int Dimensions { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("datatype")]
    public string Datatype { get; set; } = VectorDatatypes.Float32;

    [JsonPropertyName("distanceFunction")]
    public string DistanceFunction { get; set; } = DistanceFunctions.Cosine;

    [JsonPropertyName("generatedAt")]
    public DateTime? GeneratedAt { get; set; }

    [JsonPropertyName("sourceField")]
    public string? SourceField { get; set; }
}
