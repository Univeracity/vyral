using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;
using Vyral.Abstractions.Models;
using Vyral.Azure;

namespace Vyral.Tests.Azure;

public class AzureCosmosRecordCollectionStoreLiveTests
{
    [AzureCosmosLiveFact]
    public async Task CosmosRecordCollectionStore_NormalizesSupportedVectorFunctions()
    {
        var settings = AzureLiveSettings.Cosmos();
        using var client = new CosmosClient(settings.ConnectionString);
        await client.CreateDatabaseIfNotExistsAsync(settings.DatabaseId);

        var collection = AzureLiveSettings.UniqueContainerName(settings.ContainerPrefix);
        var store = new CosmosRecordCollectionStore(client, settings.DatabaseId);
        var fields = new[]
        {
            (Name: "cosineEmbedding", Distance: DistanceFunctions.Cosine),
            (Name: "dotEmbedding", Distance: DistanceFunctions.DotProduct),
            (Name: "euclideanEmbedding", Distance: DistanceFunctions.Euclidean)
        };

        try
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = collection,
                VectorPolicies = fields.Select(field => new VectorFieldPolicy
                {
                    Name = field.Name,
                    Path = $"/vectors/{field.Name}/values",
                    Dimensions = 2,
                    DistanceFunction = field.Distance,
                    IndexType = IndexTypes.Flat
                }).ToList()
            });

            foreach (var (id, values) in new[]
            {
                ("exact", new float[] { 1, 0 }),
                ("near", new float[] { 0.8f, 0.2f })
            })
            {
                await store.UpsertRecordAsync(collection, new VyralRecord
                {
                    Id = id,
                    PartitionKey = "tenant-a",
                    Vectors = fields.ToDictionary(
                        field => field.Name,
                        field => new VyralVector
                        {
                            Values = values,
                            Dimensions = 2,
                            DistanceFunction = field.Distance
                        })
                });
            }

            foreach (var field in fields)
            {
                var search = await store.SearchRecordsPageAsync(collection, new QueryEnvelope
                {
                    Vector = new VectorSearchOptions { Field = field.Name, Value = new float[] { 1, 0 }, Top = 2 }
                });
                Assert.Equal(new[] { "exact", "near" }, search.Items.Select(match => match.Record.Id));
                Assert.InRange(search.Items[0].Score, 0.99f, 1.01f);
                Assert.True(search.Items[0].Score > search.Items[1].Score);
            }
        }
        finally
        {
            try
            {
                await client.GetDatabase(settings.DatabaseId).GetContainer(collection).DeleteContainerAsync();
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }
        }
    }

    [AzureCosmosLiveFact]
    public async Task CosmosRecordCollectionStore_CreatesVectorPolicyAndRoundTripsRecord()
    {
        var settings = AzureLiveSettings.Cosmos();
        using var client = new CosmosClient(settings.ConnectionString);
        await client.CreateDatabaseIfNotExistsAsync(settings.DatabaseId);

        var collection = AzureLiveSettings.UniqueContainerName(settings.ContainerPrefix);
        var store = new CosmosRecordCollectionStore(client, settings.DatabaseId);

        try
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = collection,
                PartitionKeyPath = "/partitionKey",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new()
                    {
                        Name = "contentEmbedding",
                        Path = "/vectors/contentEmbedding/values",
                        Dimensions = 3,
                        Datatype = "float32",
                        DistanceFunction = "cosine",
                        IndexType = "flat"
                    }
                }
            });

            var policy = await store.GetCollectionPolicyAsync(collection);
            Assert.NotNull(policy);
            Assert.Equal("/partitionKey", policy.PartitionKeyPath);
            var vectorPolicy = Assert.Single(policy.VectorPolicies);
            Assert.Equal("contentEmbedding", vectorPolicy.Name);
            Assert.Equal(3, vectorPolicy.Dimensions);
            Assert.Equal("cosine", vectorPolicy.DistanceFunction);

            await store.UpsertRecordAsync(collection, new VyralRecord
            {
                Id = "record-1",
                PartitionKey = "tenant-a",
                Type = "chunk",
                Metadata = new JsonObject { ["status"] = "active" },
                Content = new JsonObject { ["text"] = "live cosmos test" },
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["contentEmbedding"] = new() { Values = new float[] { 1, 0, 0 }, Dimensions = 3 }
                }
            });

            var record = await store.GetRecordAsync(collection, "tenant-a", "record-1");
            Assert.NotNull(record);
            Assert.Equal("record-1", record.Id);
            Assert.Equal("tenant-a", record.PartitionKey);
            Assert.Equal(1, record.Revision);
            Assert.Equal("rev:1", record.Etag);

            await store.UpsertRecordAsync(collection, new VyralRecord
            {
                Id = "record-1",
                PartitionKey = "tenant-a",
                Type = "chunk",
                Metadata = new JsonObject { ["status"] = "active" },
                Content = new JsonObject { ["text"] = "live cosmos test updated" },
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["contentEmbedding"] = new() { Values = new float[] { 1, 0, 0 }, Dimensions = 3 }
                }
            });

            var updated = await store.GetRecordAsync(collection, "tenant-a", "record-1");
            Assert.NotNull(updated);
            Assert.Equal(record.CreatedAt, updated.CreatedAt);
            Assert.Equal(2, updated.Revision);
            Assert.Equal("rev:2", updated.Etag);

            var page = await store.QueryRecordsPageAsync(collection, new QueryEnvelope
            {
                PartitionKeys = new List<string> { "tenant-a" },
                Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
                Limit = 10
            });
            Assert.Contains(page.Items, item => item.Id == "record-1");

            var search = await store.SearchRecordsPageAsync(collection, new QueryEnvelope
            {
                Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
                Vector = new VectorSearchOptions
                {
                    Field = "contentEmbedding",
                    Value = new float[] { 1, 0, 0 },
                    Top = 1
                }
            });
            var match = Assert.Single(search.Items);
            Assert.Equal("record-1", match.Record.Id);
            Assert.InRange(match.Score, 0.99f, 1.01f);
        }
        finally
        {
            try
            {
                await client.GetDatabase(settings.DatabaseId).GetContainer(collection).DeleteContainerAsync();
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }
        }
    }
}
