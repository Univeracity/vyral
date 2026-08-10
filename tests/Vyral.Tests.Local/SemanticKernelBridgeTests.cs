using Microsoft.Extensions.VectorData;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Models;
using Vyral.Bridge.SemanticKernel;
using Vyral.Local;

namespace Vyral.Tests.Local;

public class SemanticKernelBridgeTests
{
    [Fact]
    public void AlignmentProfile_DocumentsCurrentBridgeScope()
    {
        var profile = VyralSemanticKernelAlignment.Current;

        Assert.Equal("Vyral RecordCollectionStore", profile.CoreContract);
        Assert.Equal("Microsoft.Extensions.VectorData.VectorStore", profile.TargetContract);
        Assert.Contains(profile.Mappings, mapping =>
            mapping.VyralConcept == "record collection" &&
            mapping.SemanticKernelConcept == "VectorStoreCollection<TKey, TRecord>" &&
            mapping.Status == SemanticKernelAlignmentStatuses.Aligned);
        Assert.Contains(profile.Mappings, mapping =>
            mapping.VyralConcept == "record id and partition key" &&
            mapping.Status == SemanticKernelAlignmentStatuses.CallerMapped);
        Assert.Contains(profile.Mappings, mapping =>
            mapping.VyralConcept == "RecordCollectionPolicy.VectorPolicies" &&
            mapping.Status == SemanticKernelAlignmentStatuses.Aligned);
        Assert.Contains(profile.Mappings, mapping =>
            mapping.VyralConcept == "RAG chunks and manifests" &&
            mapping.Status == SemanticKernelAlignmentStatuses.CallerMapped);
        Assert.Contains("typed vector search", profile.SupportedFeatures);
        Assert.Contains("dynamic Semantic Kernel collections", profile.DeferredFeatures);
        Assert.Contains(profile.ConformanceTargets, target =>
            target.Contains("RAG context assembly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MappedCollection_UpsertsGetsSearchesAndDeletesRecords()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-sk-{Guid.NewGuid():N}.sqlite");
        var recordStore = new SqliteRecordCollectionStore(dbPath);
        await recordStore.InitializeAsync();

        var vectorStore = new VyralVectorStore(recordStore);
        var collection = vectorStore.GetMappedCollection<string, SkChunk>("chunks", CreateOptions("chunks"));
        await collection.EnsureCollectionExistsAsync();

        await collection.UpsertAsync(new SkChunk
        {
            Id = "near",
            PartitionKey = "tenant-a",
            Text = "near text",
            Embedding = new float[] { 1, 0 }
        });
        await collection.UpsertAsync(new SkChunk
        {
            Id = "far",
            PartitionKey = "tenant-a",
            Text = "far text",
            Embedding = new float[] { 0, 1 }
        });

        var retrieved = await collection.GetAsync("tenant-a|near");
        Assert.NotNull(retrieved);
        Assert.Equal("near text", retrieved.Text);

        var matches = new List<VectorSearchResult<SkChunk>>();
        await foreach (var match in collection.SearchAsync(new float[] { 1, 0 }, top: 1))
        {
            matches.Add(match);
        }

        var best = Assert.Single(matches);
        Assert.Equal("near", best.Record.Id);

        await collection.DeleteAsync("tenant-a|near");
        Assert.Null(await collection.GetAsync("tenant-a|near"));
    }

    [Fact]
    public async Task MappedCollection_TranslatesPredicateRetrievalAndVectorSearchFilters()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-sk-{Guid.NewGuid():N}.sqlite");
        var recordStore = new SqliteRecordCollectionStore(dbPath);
        await recordStore.InitializeAsync();

        var collection = new VyralVectorStore(recordStore).GetMappedCollection<string, SkChunk>("chunks", CreateOptions("chunks"));
        await collection.EnsureCollectionExistsAsync();

        await collection.UpsertAsync(new SkChunk
        {
            Id = "near",
            PartitionKey = "tenant-a",
            Text = "near retrieval text",
            Embedding = new float[] { 1, 0 }
        });
        await collection.UpsertAsync(new SkChunk
        {
            Id = "far",
            PartitionKey = "tenant-a",
            Text = "far retrieval text",
            Embedding = new float[] { 1, 0 }
        });
        await collection.UpsertAsync(new SkChunk
        {
            Id = "other",
            PartitionKey = "tenant-b",
            Text = "near retrieval text",
            Embedding = new float[] { 1, 0 }
        });

        var filtered = new List<SkChunk>();
        await foreach (var chunk in collection.GetAsync(
            chunk => chunk.PartitionKey == "tenant-a" && chunk.Text.Contains("near"),
            limit: 5))
        {
            filtered.Add(chunk);
        }

        Assert.Equal(new[] { "near" }, filtered.Select(chunk => chunk.Id));

        var searchMatches = new List<VectorSearchResult<SkChunk>>();
        await foreach (var match in collection.SearchAsync(
            new float[] { 1, 0 },
            top: 5,
            options: new Microsoft.Extensions.VectorData.VectorSearchOptions<SkChunk>
            {
                Filter = chunk => chunk.PartitionKey == "tenant-a" && chunk.Text.StartsWith("near")
            }))
        {
            searchMatches.Add(match);
        }

        Assert.Equal(new[] { "near" }, searchMatches.Select(match => match.Record.Id));
    }

    [Fact]
    public async Task MappedCollection_OrdersAndSkipsFilteredRetrievalForChunkSequences()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-sk-{Guid.NewGuid():N}.sqlite");
        var recordStore = new SqliteRecordCollectionStore(dbPath);
        await recordStore.InitializeAsync();

        var collection = new VyralVectorStore(recordStore).GetMappedCollection<string, SkChunk>("chunks", CreateOptions("chunks"));
        await collection.EnsureCollectionExistsAsync();

        await collection.UpsertAsync(new SkChunk
        {
            Id = "chunk-003",
            PartitionKey = "tenant-a",
            DocumentId = "doc-1",
            ChunkIndex = 3,
            Text = "third chunk",
            Embedding = new float[] { 0.8f, 0.2f }
        });
        await collection.UpsertAsync(new SkChunk
        {
            Id = "chunk-001",
            PartitionKey = "tenant-a",
            DocumentId = "doc-1",
            ChunkIndex = 1,
            Text = "first chunk",
            Embedding = new float[] { 1, 0 }
        });
        await collection.UpsertAsync(new SkChunk
        {
            Id = "chunk-002",
            PartitionKey = "tenant-a",
            DocumentId = "doc-1",
            ChunkIndex = 2,
            Text = "second chunk",
            Embedding = new float[] { 0.9f, 0.1f }
        });
        await collection.UpsertAsync(new SkChunk
        {
            Id = "other-doc",
            PartitionKey = "tenant-a",
            DocumentId = "doc-2",
            ChunkIndex = 1,
            Text = "other document",
            Embedding = new float[] { 1, 0 }
        });

        var ordered = new List<SkChunk>();
        await foreach (var chunk in collection.GetAsync(
            chunk => chunk.DocumentId == "doc-1",
            limit: 2,
            options: new FilteredRecordRetrievalOptions<SkChunk>
            {
                Skip = 1,
                OrderBy = order => order.Ascending(chunk => chunk.ChunkIndex)
            }))
        {
            ordered.Add(chunk);
        }

        Assert.Equal(new[] { "chunk-002", "chunk-003" }, ordered.Select(chunk => chunk.Id));
    }

    [Fact]
    public async Task MappedCollection_SkipsVectorSearchResultsWithoutProviderContinuation()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-sk-{Guid.NewGuid():N}.sqlite");
        var recordStore = new SqliteRecordCollectionStore(dbPath);
        await recordStore.InitializeAsync();

        var collection = new VyralVectorStore(recordStore).GetMappedCollection<string, SkChunk>("chunks", CreateOptions("chunks"));
        await collection.EnsureCollectionExistsAsync();

        await collection.UpsertAsync(new SkChunk
        {
            Id = "near",
            PartitionKey = "tenant-a",
            Text = "near text",
            Embedding = new float[] { 1, 0 }
        });
        await collection.UpsertAsync(new SkChunk
        {
            Id = "middle",
            PartitionKey = "tenant-a",
            Text = "middle text",
            Embedding = new float[] { 0.9f, 0.1f }
        });
        await collection.UpsertAsync(new SkChunk
        {
            Id = "far",
            PartitionKey = "tenant-a",
            Text = "far text",
            Embedding = new float[] { 0, 1 }
        });

        var matches = new List<VectorSearchResult<SkChunk>>();
        await foreach (var match in collection.SearchAsync(
            new float[] { 1, 0 },
            top: 1,
            options: new Microsoft.Extensions.VectorData.VectorSearchOptions<SkChunk>
            {
                Skip = 1
            }))
        {
            matches.Add(match);
        }

        Assert.Equal(new[] { "middle" }, matches.Select(match => match.Record.Id));
    }

    [Fact]
    public async Task MappedCollection_DeletesCollectionThroughBridge()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-sk-{Guid.NewGuid():N}.sqlite");
        var recordStore = new SqliteRecordCollectionStore(dbPath);
        await recordStore.InitializeAsync();

        var vectorStore = new VyralVectorStore(recordStore);
        var collection = vectorStore.GetMappedCollection<string, SkChunk>("chunks", CreateOptions("chunks"));
        await collection.EnsureCollectionExistsAsync();
        await collection.UpsertAsync(new SkChunk
        {
            Id = "near",
            PartitionKey = "tenant-a",
            Text = "near text",
            Embedding = new float[] { 1, 0 }
        });

        await collection.EnsureCollectionDeletedAsync();
        await vectorStore.EnsureCollectionDeletedAsync("chunks");

        Assert.False(await collection.CollectionExistsAsync());
        Assert.False(await vectorStore.CollectionExistsAsync("chunks"));
        Assert.Null(await recordStore.GetCollectionPolicyAsync("chunks"));
    }

    [Fact]
    public async Task DynamicCollection_RemainsDeferred()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-sk-{Guid.NewGuid():N}.sqlite");
        var recordStore = new SqliteRecordCollectionStore(dbPath);
        await recordStore.InitializeAsync();

        var vectorStore = new VyralVectorStore(recordStore);

        var error = Assert.Throws<NotSupportedException>(() => vectorStore.GetDynamicCollection("chunks"));
        Assert.Contains("Dynamic Semantic Kernel collections", error.Message);
    }

    [Fact]
    public async Task UnmappedCollection_FailsWithConfigurationError()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-sk-{Guid.NewGuid():N}.sqlite");
        var recordStore = new SqliteRecordCollectionStore(dbPath);
        await recordStore.InitializeAsync();

        var collection = new VyralVectorStore(recordStore).GetCollection<string, SkChunk>("chunks");

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => collection.EnsureCollectionExistsAsync());
        Assert.Contains("VyralVectorStoreCollectionOptions", error.Message);
    }

    private static VyralVectorStoreCollectionOptions<string, SkChunk> CreateOptions(string collectionName)
    {
        return new VyralVectorStoreCollectionOptions<string, SkChunk>
        {
            GetKey = chunk => $"{chunk.PartitionKey}|{chunk.Id}",
            GetRecordId = key => key.Split('|')[1],
            GetPartitionKey = key => key.Split('|')[0],
            VectorField = "contentEmbedding",
            FilterPropertyPaths = new Dictionary<string, string>
            {
                ["Text"] = "/content/text",
                ["DocumentId"] = "/metadata/documentId",
                ["ChunkIndex"] = "/metadata/chunkIndex"
            },
            CollectionPolicy = new RecordCollectionPolicy
            {
                Name = collectionName,
                IndexedMetadata = new List<string> { "/content/text", "/metadata/documentId", "/metadata/chunkIndex" },
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
                }
            },
            ToVyralRecord = chunk => new VyralRecord
            {
                Id = chunk.Id,
                PartitionKey = chunk.PartitionKey,
                Type = "chunk",
                Metadata = new JsonObject
                {
                    ["documentId"] = chunk.DocumentId,
                    ["chunkIndex"] = chunk.ChunkIndex
                },
                Content = new JsonObject { ["text"] = chunk.Text },
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["contentEmbedding"] = new() { Values = chunk.Embedding, Dimensions = chunk.Embedding.Length }
                }
            },
            FromVyralRecord = record => new SkChunk
            {
                Id = record.Id,
                PartitionKey = record.PartitionKey,
                DocumentId = ReadMetadataString(record, "documentId"),
                ChunkIndex = ReadMetadataInt(record, "chunkIndex"),
                Text = record.Content?["text"]?.ToString() ?? string.Empty,
                Embedding = record.Vectors?["contentEmbedding"].Values ?? Array.Empty<float>()
            }
        };
    }

    private static string ReadMetadataString(VyralRecord record, string key)
    {
        var node = record.Metadata?[key];
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
    }

    private static int ReadMetadataInt(VyralRecord record, string key)
    {
        var node = record.Metadata?[key];
        if (node is not JsonValue v) return 0;
        return v.TryGetValue<int>(out var i) ? i : 0;
    }

    private sealed class SkChunk
    {
        public string Id { get; set; } = string.Empty;

        public string PartitionKey { get; set; } = string.Empty;

        public string DocumentId { get; set; } = string.Empty;

        public int ChunkIndex { get; set; }

        public string Text { get; set; } = string.Empty;

        public float[] Embedding { get; set; } = Array.Empty<float>();
    }
}
