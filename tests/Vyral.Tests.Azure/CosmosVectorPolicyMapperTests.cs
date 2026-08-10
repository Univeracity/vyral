using System.Collections.ObjectModel;
using Microsoft.Azure.Cosmos;
using Vyral.Abstractions.Models;
using Vyral.Azure;

namespace Vyral.Tests.Azure;

public class CosmosVectorPolicyMapperTests
{
    [Theory]
    [InlineData(DistanceFunctions.Cosine, 1f, 1f)]
    [InlineData(DistanceFunctions.Cosine, 0.75f, 0.75f)]
    [InlineData(DistanceFunctions.DotProduct, 0.8f, 0.8f)]
    [InlineData(DistanceFunctions.Euclidean, 1f, 0.5f)]
    public void DistanceToScore_UsesPortableHigherIsBetterSimilarity(string distanceFunction, float distance, float expected)
    {
        Assert.Equal(expected, CosmosVectorPolicyMapper.DistanceToScore(distanceFunction, distance), precision: 5);
    }

    [Fact]
    public void ApplyPortableVersion_SetsTimestampsRevisionAndEtag()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var updated = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var existing = new VyralRecord
        {
            Id = "record-1",
            PartitionKey = "tenant-a",
            CreatedAt = created,
            Revision = 3,
            Etag = "rev:3"
        };
        var next = new VyralRecord { Id = "record-1", PartitionKey = "tenant-a" };

        CosmosRecordCollectionStore.ApplyPortableVersion(next, existing, updated);

        Assert.Equal(created, next.CreatedAt);
        Assert.Equal(updated, next.UpdatedAt);
        Assert.Equal(4, next.Revision);
        Assert.Equal("rev:4", next.Etag);
    }

    [Fact]
    public void ToContainerProperties_MapsVectorPolicyToCosmosEmbeddingAndIndex()
    {
        var properties = CosmosVectorPolicyMapper.ToContainerProperties(new RecordCollectionPolicy
        {
            Name = "records",
            PartitionKeyPath = "/partitionKey",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = "contentEmbedding",
                    Path = "/vectors/contentEmbedding/values",
                    Dimensions = 1536,
                    Datatype = "float32",
                    DistanceFunction = "cosine",
                    IndexType = "diskANN"
                }
            }
        });

        Assert.Equal("records", properties.Id);
        Assert.Equal("/partitionKey", properties.PartitionKeyPath);

        var embedding = Assert.Single(properties.VectorEmbeddingPolicy!.Embeddings);
        Assert.Equal("/vectors/contentEmbedding/values", embedding.Path);
        Assert.Equal(1536, embedding.Dimensions);
        Assert.Equal(VectorDataType.Float32, embedding.DataType);
        Assert.Equal(DistanceFunction.Cosine, embedding.DistanceFunction);

        var vectorIndex = Assert.Single(properties.IndexingPolicy.VectorIndexes);
        Assert.Equal("/vectors/contentEmbedding/values", vectorIndex.Path);
        Assert.Equal(VectorIndexType.DiskANN, vectorIndex.Type);
    }

    [Fact]
    public void FromContainerProperties_MapsCosmosEmbeddingAndIndexToVyralPolicy()
    {
        var properties = new ContainerProperties("records", "/tenantId")
        {
            VectorEmbeddingPolicy = new VectorEmbeddingPolicy(
                new Collection<Embedding>
                {
                    new()
                    {
                        Path = "/vectors/contentEmbedding/values",
                        Dimensions = 768,
                        DataType = VectorDataType.Float16,
                        DistanceFunction = DistanceFunction.DotProduct
                    }
                })
        };
        properties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
        {
            Path = "/vectors/contentEmbedding/values",
            Type = VectorIndexType.QuantizedFlat
        });

        var policy = CosmosVectorPolicyMapper.FromContainerProperties(properties);

        Assert.Equal("records", policy.Name);
        Assert.Equal("/tenantId", policy.PartitionKeyPath);

        var vectorPolicy = Assert.Single(policy.VectorPolicies);
        Assert.Equal("contentEmbedding", vectorPolicy.Name);
        Assert.Equal("/vectors/contentEmbedding/values", vectorPolicy.Path);
        Assert.Equal(768, vectorPolicy.Dimensions);
        Assert.Equal("float16", vectorPolicy.Datatype);
        Assert.Equal("dotProduct", vectorPolicy.DistanceFunction);
        Assert.Equal("quantizedFlat", vectorPolicy.IndexType);
    }

    [Fact]
    public void ToContainerProperties_RejectsUnsupportedVectorOption()
    {
        var policy = new RecordCollectionPolicy
        {
            Name = "records",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = "contentEmbedding",
                    Path = "/vectors/contentEmbedding/values",
                    Dimensions = 3,
                    IndexType = "unknown"
                }
            }
        };

        Assert.Throws<InvalidOperationException>(() => CosmosVectorPolicyMapper.ToContainerProperties(policy));
    }

    [Fact]
    public void ToContainerProperties_RejectsNonPortablePolicyShape()
    {
        Assert.Throws<InvalidOperationException>(() => CosmosVectorPolicyMapper.ToContainerProperties(new RecordCollectionPolicy
        {
            Name = "records",
            PartitionKeyPath = "/tenantId"
        }));

        Assert.Throws<InvalidOperationException>(() => CosmosVectorPolicyMapper.ToContainerProperties(new RecordCollectionPolicy
        {
            Name = "records",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/embeddings/content", Dimensions = 3 }
            }
        }));

        Assert.Throws<InvalidOperationException>(() => CosmosVectorPolicyMapper.ToContainerProperties(new RecordCollectionPolicy
        {
            Name = "records",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "content/embedding", Path = "/vectors/content/embedding/values", Dimensions = 3 }
            }
        }));
    }

    [Fact]
    public void AnalyzeCompatibility_ReturnsMigrationWarningsWithoutThrowing()
    {
        var report = CosmosVectorPolicyMapper.AnalyzeCompatibility(new RecordCollectionPolicy
        {
            Name = "records",
            PartitionKeyPath = "/partitionKey",
            IndexedMetadata = new List<string> { "/metadata/status" },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = "contentEmbedding",
                    Path = "/vectors/contentEmbedding/values",
                    Dimensions = 384,
                    Datatype = "float32",
                    DistanceFunction = "cosine",
                    IndexType = "quantizedFlat"
                }
            }
        });

        Assert.True(report.Compatible);
        Assert.Empty(report.Errors);
        Assert.Contains(report.Warnings, warning => warning.Contains("Indexed metadata paths", StringComparison.Ordinal));
        var vectorField = Assert.Single(report.VectorFields);
        Assert.Equal("contentEmbedding", vectorField.Name);
        Assert.Equal("quantizedFlat", vectorField.IndexType);
    }

    [Fact]
    public void AnalyzeCompatibility_ReturnsErrorsForNonPortablePolicies()
    {
        var report = CosmosVectorPolicyMapper.AnalyzeCompatibility(new RecordCollectionPolicy
        {
            Name = "records",
            PartitionKeyPath = "/tenantId"
        });

        Assert.False(report.Compatible);
        Assert.Contains(report.Errors, error => error.Contains("/partitionKey", StringComparison.Ordinal));
        Assert.Empty(report.VectorFields);
    }
}
