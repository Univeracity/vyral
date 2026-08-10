using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public class CollectionInspectionRequest
{
    [JsonPropertyName("includeAnomalies")]
    public bool IncludeAnomalies { get; set; } = true;

    [JsonPropertyName("anomalyLimit")]
    public int AnomalyLimit { get; set; } = 50;
}

public class CollectionInspectionResult
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("policy")]
    public RecordCollectionPolicy Policy { get; set; } = new();

    [JsonPropertyName("recordCount")]
    public int RecordCount { get; set; }

    [JsonPropertyName("partitionCount")]
    public int PartitionCount { get; set; }

    [JsonPropertyName("typeCounts")]
    public Dictionary<string, int> TypeCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("embeddingProviderCounts")]
    public Dictionary<string, int> EmbeddingProviderCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("embeddingModelCounts")]
    public Dictionary<string, int> EmbeddingModelCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("rag")]
    public RagCollectionInspection Rag { get; set; } = new();

    [JsonPropertyName("vectors")]
    public List<VectorFieldInspection> Vectors { get; set; } = new();

    [JsonPropertyName("extraVectorFieldCounts")]
    public Dictionary<string, int> ExtraVectorFieldCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("anomalyCount")]
    public int AnomalyCount { get; set; }

    [JsonPropertyName("returnedAnomalyCount")]
    public int ReturnedAnomalyCount { get; set; }

    [JsonPropertyName("anomalies")]
    public List<CollectionInspectionAnomaly> Anomalies { get; set; } = new();
}

public class RagCollectionInspection
{
    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; set; }

    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; set; }

    [JsonPropertyName("manifestCount")]
    public int ManifestCount { get; set; }

    [JsonPropertyName("chunkRecordsWithDocumentIdCount")]
    public int ChunkRecordsWithDocumentIdCount { get; set; }

    [JsonPropertyName("chunkRecordsWithVectorCount")]
    public int ChunkRecordsWithVectorCount { get; set; }

    [JsonPropertyName("chunkRecordsWithoutVectorCount")]
    public int ChunkRecordsWithoutVectorCount { get; set; }
}

public class VectorFieldInspection
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("policyDimensions")]
    public int PolicyDimensions { get; set; }

    [JsonPropertyName("datatype")]
    public string Datatype { get; set; } = VectorDatatypes.Float32;

    [JsonPropertyName("distanceFunction")]
    public string DistanceFunction { get; set; } = DistanceFunctions.Cosine;

    [JsonPropertyName("indexType")]
    public string IndexType { get; set; } = IndexTypes.Flat;

    [JsonPropertyName("recordCount")]
    public int RecordCount { get; set; }

    [JsonPropertyName("presentCount")]
    public int PresentCount { get; set; }

    [JsonPropertyName("missingCount")]
    public int MissingCount { get; set; }

    [JsonPropertyName("notApplicableCount")]
    public int NotApplicableCount { get; set; }

    [JsonPropertyName("emptyCount")]
    public int EmptyCount { get; set; }

    [JsonPropertyName("dimensionMismatchCount")]
    public int DimensionMismatchCount { get; set; }

    [JsonPropertyName("policyCoverage")]
    public double PolicyCoverage { get; set; }

    [JsonPropertyName("modelCounts")]
    public Dictionary<string, int> ModelCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("sourceFieldCounts")]
    public Dictionary<string, int> SourceFieldCounts { get; set; } = new(StringComparer.Ordinal);
}

public class CollectionInspectionAnomaly
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public Dictionary<string, object?> Details { get; set; } = new(StringComparer.Ordinal);
}
