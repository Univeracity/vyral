using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public class RecordCollectionPolicy
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("partitionKeyPath")]
    public string PartitionKeyPath { get; set; } = "/partitionKey";

    [JsonPropertyName("vectorPolicies")]
    public List<VectorFieldPolicy> VectorPolicies { get; set; } = new();

    [JsonPropertyName("indexedMetadata")]
    public List<string> IndexedMetadata { get; set; } = new();
}

public class VectorFieldPolicy
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("dimensions")]
    public int Dimensions { get; set; }

    [JsonPropertyName("datatype")]
    public string Datatype { get; set; } = VectorDatatypes.Float32;

    [JsonPropertyName("distanceFunction")]
    public string DistanceFunction { get; set; } = DistanceFunctions.Cosine;

    [JsonPropertyName("indexType")]
    public string IndexType { get; set; } = IndexTypes.Flat;
}
