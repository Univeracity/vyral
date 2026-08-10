using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Azure.Cosmos;
using Vyral.Abstractions.Models;

namespace Vyral.Azure;

public static class CosmosVectorPolicyMapper
{
    private const string SupportedPartitionKeyPath = "/partitionKey";

    public static CosmosPolicyCompatibilityReport AnalyzeCompatibility(RecordCollectionPolicy policy)
    {
        var report = new CosmosPolicyCompatibilityReport
        {
            Collection = policy.Name,
            PartitionKeyPath = policy.PartitionKeyPath
        };

        try
        {
            ValidatePolicy(policy);
        }
        catch (InvalidOperationException ex)
        {
            report.Errors.Add(ex.Message);
            return report;
        }

        if (policy.VectorPolicies.Count == 0)
        {
            report.Warnings.Add("Collection has no vector policies; Cosmos vector-search migration rehearsal cannot validate vector paths, dimensions, datatypes, distance functions, or index types.");
        }

        if (policy.IndexedMetadata.Count > 0)
        {
            report.Warnings.Add("Indexed metadata paths are local query projections; the Cosmos adapter currently relies on Cosmos container indexing defaults instead of mapping these paths into a custom indexing policy.");
        }

        foreach (var vectorPolicy in policy.VectorPolicies)
        {
            report.VectorFields.Add(new CosmosVectorFieldCompatibility
            {
                Name = vectorPolicy.Name,
                Path = vectorPolicy.Path,
                Dimensions = vectorPolicy.Dimensions,
                Datatype = vectorPolicy.Datatype,
                DistanceFunction = vectorPolicy.DistanceFunction,
                IndexType = vectorPolicy.IndexType
            });
        }

        return report;
    }

    public static ContainerProperties ToContainerProperties(RecordCollectionPolicy policy)
    {
        ValidatePolicy(policy);

        var properties = new ContainerProperties(policy.Name, policy.PartitionKeyPath);
        if (policy.VectorPolicies.Count == 0)
        {
            return properties;
        }

        properties.VectorEmbeddingPolicy = new VectorEmbeddingPolicy(
            new Collection<Embedding>(
                policy.VectorPolicies.Select(vectorPolicy => new Embedding
                {
                    Path = vectorPolicy.Path,
                    DataType = ToCosmosDataType(vectorPolicy.Datatype),
                    Dimensions = vectorPolicy.Dimensions,
                    DistanceFunction = ToCosmosDistanceFunction(vectorPolicy.DistanceFunction)
                }).ToList()));

        foreach (var vectorPolicy in policy.VectorPolicies)
        {
            properties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
            {
                Path = vectorPolicy.Path,
                Type = ToCosmosIndexType(vectorPolicy.IndexType)
            });
        }

        return properties;
    }

    public static RecordCollectionPolicy FromContainerProperties(ContainerProperties properties)
    {
        var vectorIndexes = properties.IndexingPolicy.VectorIndexes
            .GroupBy(index => index.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var vectorPolicies = new List<VectorFieldPolicy>();
        if (properties.VectorEmbeddingPolicy?.Embeddings != null)
        {
            foreach (var embedding in properties.VectorEmbeddingPolicy.Embeddings)
            {
                vectorIndexes.TryGetValue(embedding.Path, out var vectorIndex);
                vectorPolicies.Add(new VectorFieldPolicy
                {
                    Name = InferFieldName(embedding.Path),
                    Path = embedding.Path,
                    Dimensions = embedding.Dimensions,
                    Datatype = FromCosmosDataType(embedding.DataType),
                    DistanceFunction = FromCosmosDistanceFunction(embedding.DistanceFunction),
                    IndexType = vectorIndex == null ? IndexTypes.Flat : FromCosmosIndexType(vectorIndex.Type)
                });
            }
        }

        return new RecordCollectionPolicy
        {
            Name = properties.Id,
            PartitionKeyPath = properties.PartitionKeyPath,
            VectorPolicies = vectorPolicies
        };
    }

    /// <summary>
    /// Converts Cosmos <c>VectorDistance</c> output to Vyral's portable similarity convention
    /// (higher is a better match). Cosmos returns cosine and dot-product similarity directly;
    /// Euclidean remains a distance and is normalized to a bounded similarity.
    /// </summary>
    public static float DistanceToScore(string distanceFunction, float distance)
    {
        return distanceFunction.ToLowerInvariant() switch
        {
            "cosine" => distance,
            "dotproduct" => distance,
            "euclidean" => 1.0f / (1.0f + distance),
            _ => 1.0f / (1.0f + distance)
        };
    }

    private static void ValidatePolicy(RecordCollectionPolicy policy)
    {
        RecordIdentityValidator.ValidateCollectionName(policy.Name);

        if (!string.Equals(policy.PartitionKeyPath, SupportedPartitionKeyPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Collection partition key path must be '{SupportedPartitionKeyPath}'.");
        }

        if (policy.VectorPolicies.GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new InvalidOperationException("Vector policy names must be unique within a collection.");
        }

        if (policy.VectorPolicies.GroupBy(p => p.Path, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new InvalidOperationException("Vector policy paths must be unique within a collection.");
        }

        foreach (var vectorPolicy in policy.VectorPolicies)
        {
            if (string.IsNullOrWhiteSpace(vectorPolicy.Name)) throw new InvalidOperationException("Vector policy name is required.");
            if (!IsSupportedPathSegment(vectorPolicy.Name)) throw new InvalidOperationException($"Vector policy name '{vectorPolicy.Name}' contains unsupported characters.");
            if (!string.Equals(vectorPolicy.Path, BuildExpectedVectorPath(vectorPolicy.Name), StringComparison.Ordinal))
                throw new InvalidOperationException($"Vector policy '{vectorPolicy.Name}' path must be '{BuildExpectedVectorPath(vectorPolicy.Name)}'.");
            if (vectorPolicy.Dimensions <= 0) throw new InvalidOperationException($"Vector policy '{vectorPolicy.Name}' dimensions must be greater than zero.");

            ToCosmosDataType(vectorPolicy.Datatype);
            ToCosmosDistanceFunction(vectorPolicy.DistanceFunction);
            ToCosmosIndexType(vectorPolicy.IndexType);
        }
    }

    private static VectorDataType ToCosmosDataType(string datatype)
    {
        return Normalize(datatype) switch
        {
            "float32" => VectorDataType.Float32,
            "float16" => VectorDataType.Float16,
            "uint8" => VectorDataType.Uint8,
            "int8" => VectorDataType.Int8,
            _ => throw new InvalidOperationException($"Vector datatype '{datatype}' is not supported by the Cosmos adapter.")
        };
    }

    private static string FromCosmosDataType(VectorDataType datatype)
    {
        return datatype switch
        {
            VectorDataType.Float32 => VectorDatatypes.Float32,
            VectorDataType.Float16 => "float16",
            VectorDataType.Uint8 => "uint8",
            VectorDataType.Int8 => "int8",
            _ => datatype.ToString()
        };
    }

    private static DistanceFunction ToCosmosDistanceFunction(string distanceFunction)
    {
        return Normalize(distanceFunction) switch
        {
            "cosine" => DistanceFunction.Cosine,
            "dotproduct" => DistanceFunction.DotProduct,
            "euclidean" => DistanceFunction.Euclidean,
            _ => throw new InvalidOperationException($"Vector distance function '{distanceFunction}' is not supported by the Cosmos adapter.")
        };
    }

    private static string FromCosmosDistanceFunction(DistanceFunction distanceFunction)
    {
        return distanceFunction switch
        {
            DistanceFunction.Cosine => DistanceFunctions.Cosine,
            DistanceFunction.DotProduct => DistanceFunctions.DotProduct,
            DistanceFunction.Euclidean => DistanceFunctions.Euclidean,
            _ => distanceFunction.ToString()
        };
    }

    private static VectorIndexType ToCosmosIndexType(string indexType)
    {
        return Normalize(indexType) switch
        {
            "flat" => VectorIndexType.Flat,
            "quantizedflat" => VectorIndexType.QuantizedFlat,
            "diskann" => VectorIndexType.DiskANN,
            _ => throw new InvalidOperationException($"Vector index type '{indexType}' is not supported by the Cosmos adapter.")
        };
    }

    private static string FromCosmosIndexType(VectorIndexType indexType)
    {
        return indexType switch
        {
            VectorIndexType.Flat => IndexTypes.Flat,
            VectorIndexType.QuantizedFlat => IndexTypes.QuantizedFlat,
            VectorIndexType.DiskANN => IndexTypes.DiskAnn,
            _ => indexType.ToString()
        };
    }

    private static string InferFieldName(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 &&
            string.Equals(segments[0], "vectors", StringComparison.Ordinal) &&
            string.Equals(segments[^1], "values", StringComparison.Ordinal))
        {
            return segments[^2];
        }

        return segments.LastOrDefault() ?? path.Trim('/');
    }

    private static bool IsSupportedPathSegment(string segment)
    {
        return segment.Length > 0 && segment.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
    }

    private static string BuildExpectedVectorPath(string name) => $"/vectors/{name}/values";

    private static string Normalize(string value)
    {
        return value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}

public sealed class CosmosPolicyCompatibilityReport
{
    public string Collection { get; set; } = string.Empty;

    public string PartitionKeyPath { get; set; } = string.Empty;

    public bool Compatible => Errors.Count == 0;

    public List<string> Errors { get; } = new();

    public List<string> Warnings { get; } = new();

    public List<CosmosVectorFieldCompatibility> VectorFields { get; } = new();
}

public sealed class CosmosVectorFieldCompatibility
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public int Dimensions { get; set; }

    public string Datatype { get; set; } = string.Empty;

    public string DistanceFunction { get; set; } = string.Empty;

    public string IndexType { get; set; } = string.Empty;
}
