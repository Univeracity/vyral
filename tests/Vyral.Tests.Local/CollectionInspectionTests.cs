using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Local;

public class CollectionInspectionTests
{
    [Fact]
    public async Task LocalCollectionInspectionService_FindsVectorAnomaliesFromStoreRecords()
    {
        var policy = new RecordCollectionPolicy
        {
            Name = "chunks",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = "contentEmbedding",
                    Path = "/vectors/contentEmbedding/values",
                    Dimensions = 3
                }
            }
        };
        var records = new List<VyralRecord>
        {
            new()
            {
                Id = "good",
                PartitionKey = "tenant-a",
                Type = "rag.chunk",
                Metadata = new JsonObject
                {
                    ["documentId"] = "doc-a",
                    ["embeddingProvider"] = "onnx",
                    ["embeddingModel"] = "multi-qa-minilm"
                },
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["contentEmbedding"] = new() { Values = new float[] { 1, 0, 0 }, Dimensions = 3, Model = "multi-qa-minilm" }
                }
            },
            new()
            {
                Id = "empty",
                PartitionKey = "tenant-a",
                Type = "rag.chunk",
                Metadata = new JsonObject { ["documentId"] = "doc-a" },
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["contentEmbedding"] = new() { Values = Array.Empty<float>(), Dimensions = 0 }
                }
            },
            new()
            {
                Id = "mismatch",
                PartitionKey = "tenant-a",
                Type = "rag.chunk",
                Metadata = new JsonObject { ["documentId"] = "doc-b" },
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["contentEmbedding"] = new() { Values = new float[] { 0, 1 }, Dimensions = 2, Model = "wrong-dims" }
                }
            },
            new()
            {
                Id = "extra-only",
                PartitionKey = "tenant-b",
                Type = "rag.chunk",
                Metadata = new JsonObject { ["documentId"] = "doc-c" },
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["titleEmbedding"] = new() { Values = new float[] { 1, 0, 0 }, Dimensions = 3, Model = "multi-qa-minilm" }
                }
            },
            new()
            {
                Id = "manifest",
                PartitionKey = "tenant-a",
                Type = "rag.manifest",
                Metadata = new JsonObject { ["documentId"] = "doc-a" }
            }
        };
        var service = new LocalCollectionInspectionService(new StubRecordCollectionStore(policy, records));

        var result = await service.InspectAsync("chunks", new CollectionInspectionRequest
        {
            IncludeAnomalies = true,
            AnomalyLimit = 3
        });

        Assert.Equal(5, result.RecordCount);
        Assert.Equal(3, result.Rag.DocumentCount);
        Assert.Equal(4, result.Rag.ChunkCount);
        Assert.Equal(1, result.Rag.ManifestCount);
        Assert.Equal(3, result.Rag.ChunkRecordsWithVectorCount);
        Assert.Equal(1, result.Rag.ChunkRecordsWithoutVectorCount);
        Assert.Equal(1, result.ExtraVectorFieldCounts["titleEmbedding"]);

        var vector = Assert.Single(result.Vectors);
        Assert.Equal(3, vector.PresentCount);
        Assert.Equal(1, vector.MissingCount);
        Assert.Equal(1, vector.NotApplicableCount);
        Assert.Equal(1, vector.EmptyCount);
        Assert.Equal(2, vector.DimensionMismatchCount);
        Assert.Equal(0.75, vector.PolicyCoverage, 3);

        Assert.Equal(5, result.AnomalyCount);
        Assert.Equal(3, result.ReturnedAnomalyCount);
        Assert.Contains(result.Anomalies, anomaly => anomaly.Kind == "emptyVector" && anomaly.Id == "empty");
        Assert.Contains(result.Anomalies, anomaly => anomaly.Kind == "dimensionMismatch" && anomaly.Id == "empty");
        Assert.Contains(result.Anomalies, anomaly => anomaly.Kind == "dimensionMismatch" && anomaly.Id == "mismatch");
    }

    private sealed class StubRecordCollectionStore : IRecordCollectionStore
    {
        private readonly RecordCollectionPolicy _policy;
        private readonly List<VyralRecord> _records;

        public StubRecordCollectionStore(RecordCollectionPolicy policy, List<VyralRecord> records)
        {
            _policy = policy;
            _records = records;
        }

        public Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<string>>(new[] { _policy.Name });
        public Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default) => Task.FromResult<RecordCollectionPolicy?>(_policy);
        public Task DeleteCollectionAsync(string collection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) =>
            Task.FromResult(new RecordQueryResult { Items = _records });
        public Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
