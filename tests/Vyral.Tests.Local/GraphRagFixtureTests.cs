using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Local;

public class GraphRagFixtureTests
{
    private const string Collection = "consumer-fixture";
    private const string GraphCollection = "consumer-fixture-graph";
    private const string PartitionKey = "tenant:fixture";
    private const string GraphId = "consumer-fixture";
    private const string EmbeddingField = "contentEmbedding";

    [Fact]
    public async Task ConsumerGraphRagFixture_CoversImportInspectTraverseContextAndEvaluation()
    {
        var store = await CreateStoreAsync();
        var provider = new FixtureEmbeddingProvider();
        var ingestion = new LocalRagIngestionService(store, provider);

        await IngestFixtureCorpusAsync(ingestion);
        var import = await store.ImportGraphEnvelopeAsync(GraphCollection, new VyralGraphCollectionImportRequest
        {
            Envelope = CreateGraphEnvelope(),
            ReplaceExisting = true
        });

        Assert.Equal(VyralGraphImportPolicyStatuses.Created, import.PolicyStatus);
        Assert.Equal(3, import.NodeCount);
        Assert.Equal(2, import.EdgeCount);

        var inspection = await store.InspectGraphAsync(GraphCollection, new VyralGraphCollectionInspectionRequest
        {
            GraphId = GraphId,
            PartitionKey = PartitionKey
        });

        Assert.NotNull(inspection);
        Assert.True(inspection!.TraversalReady);
        Assert.Equal(3, inspection.NodeCount);
        Assert.Equal(2, inspection.EdgeCount);
        Assert.Empty(inspection.Warnings);
        Assert.Empty(inspection.Anomalies);

        var traversal = await store.TraverseGraphAsync(GraphCollection, new VyralGraphTraversalRequest
        {
            GraphId = GraphId,
            PartitionKey = PartitionKey,
            StartNodeIds = new List<string> { "chunk:retention" },
            Profile = CreateTraversalProfile()
        });

        Assert.NotNull(traversal);
        Assert.Equal(new[] { "chunk:retention", "concept:protected-record", "concept:retention-hold" }, traversal!.Projection.Nodes.Select(node => node.Id));
        Assert.Equal(new[] { "edge:retention-hold", "edge:retention-record" }, traversal.Projection.Edges.Select(edge => edge.Id));
        Assert.Equal("filtered_graph_export", traversal.Projection.Diagnostics?["sourceScanMode"]?.GetValue<string>());
        Assert.True(traversal.Projection.Diagnostics?["partitionFilterApplied"]?.GetValue<bool>());

        var retrieval = new LocalRetrievalService(store, provider);
        var rag = new LocalRagContextService(retrieval, store);
        var contextRequest = CreateContextRequest();
        var context = await rag.BuildContextAsync(contextRequest);

        Assert.Equal(RagContextGraphExpansionStatuses.Succeeded, context.GraphContext?.Status);
        Assert.Contains("chunk:retention", context.GraphContext!.SeedNodeIds);
        Assert.Contains("edge:retention-hold", context.GraphContext.Provenance.Select(item => item.EntityId));
        Assert.Contains("Graph context:", context.ContextText);
        Assert.Equal(RagContextGraphExpansionStatuses.Succeeded, context.Trace!["graphExpansion"]!["status"]!.GetValue<string>());
        Assert.True(context.GraphContext.ContextTextChars > 0);
        Assert.False(context.GraphContext.ContextTextTruncated);

        var evaluation = await rag.EvaluateContextAsync(new RagContextEvaluationRequest
        {
            Cases = new List<RagContextEvaluationCase>
            {
                new()
                {
                    Name = "consumer-fixture-retention",
                    Request = contextRequest,
                    ExpectedGraph = new RagContextExpectedGraph
                    {
                        NodeIds = new List<string> { "chunk:retention", "concept:retention-hold" },
                        EdgeIds = new List<string> { "edge:retention-hold" },
                        ProvenanceEntityIds = new List<string> { "edge:retention-hold" },
                        RequireSourceGroundedProvenance = true,
                        RequireGraphContextText = true,
                        RequireContextTextNotTruncated = true
                    }
                }
            }
        });

        Assert.Equal(1, evaluation.Requested);
        Assert.Equal(1, evaluation.PassedCount);
        Assert.Equal(1.0, evaluation.PassRate);
        Assert.Equal(1.0, evaluation.NodeHitRate);
        Assert.Equal(1.0, evaluation.EdgeHitRate);
        Assert.Equal(1.0, evaluation.ProvenanceHitRate);
        var evaluationCase = Assert.Single(evaluation.Cases);
        Assert.True(evaluationCase.Passed);
        Assert.Empty(evaluationCase.Graph.MissingNodeIds);
        Assert.Empty(evaluationCase.Graph.MissingEdgeIds);
        Assert.True(evaluationCase.Graph.SourceGroundingSatisfied);
    }

    private static async Task<SqliteRecordCollectionStore> CreateStoreAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-consumer-graphrag-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = Collection,
            IndexedMetadata = new List<string>
            {
                "/metadata/documentId",
                "/metadata/topic",
                "/metadata/status",
                "/metadata/graphNodeId",
                "/type"
            },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = EmbeddingField,
                    Path = $"/vectors/{EmbeddingField}/values",
                    Dimensions = FixtureEmbeddingProvider.VectorDimensions,
                    DistanceFunction = "cosine",
                    IndexType = "flat"
                }
            }
        });

        return store;
    }

    private static async Task IngestFixtureCorpusAsync(LocalRagIngestionService ingestion)
    {
        var documents = new[]
        {
            new
            {
                Id = "retention",
                Topic = "records",
                GraphNodeId = "chunk:retention",
                Text = "Retention holds keep protected records from deletion until the hold is released."
            },
            new
            {
                Id = "travel",
                Topic = "finance",
                GraphNodeId = "chunk:travel",
                Text = "Travel reimbursement requires receipts for hotels, flights, and approved meals."
            },
            new
            {
                Id = "security",
                Topic = "security",
                GraphNodeId = "chunk:security",
                Text = "Compromised credentials should be rotated and reviewed through incident response."
            }
        };

        foreach (var document in documents)
        {
            await ingestion.IngestTextAsync(Collection, new RagIngestTextRequest
            {
                DocumentId = document.Id,
                PartitionKey = PartitionKey,
                Text = document.Text,
                Embedding = new EmbeddingOptions { Field = EmbeddingField },
                SourceUri = $"memory://consumer-fixture/{document.Id}",
                SourceKind = "fixture",
                Metadata = new JsonObject
                {
                    ["status"] = "active",
                    ["topic"] = document.Topic,
                    ["graphNodeId"] = document.GraphNodeId
                },
                Options = new RagIngestionOptions
                {
                    ChunkChars = 500,
                    ChunkOverlapChars = 0,
                    IncludeTrace = true
                }
            });
        }
    }

    private static RagContextRequest CreateContextRequest()
    {
        return new RagContextRequest
        {
            Retrieval = new RetrievalRequest
            {
                Query = "retention protected records deletion hold",
                Collections = new List<string> { Collection },
                PartitionKeys = new List<string> { PartitionKey },
                SearchMode = SearchModes.Lexical,
                Lexical = new LexicalSearchOptions
                {
                    Fields = new List<string> { "/content/text", "/metadata/topic", "/id" },
                    ScanLimit = 1000
                },
                Limit = 3,
                IncludeTrace = true
            },
            MaxChars = 2000,
            MaxCharsPerChunk = 800,
            IncludeContextText = true,
            IncludeTrace = true,
            GraphExpansion = new RagContextGraphExpansionOptions
            {
                Collection = GraphCollection,
                GraphId = GraphId,
                PartitionKey = PartitionKey,
                SeedJsonPointers = new List<string> { "/metadata/graphNodeId" },
                MaxGraphContextChars = 1000,
                MaxGraphProvenanceItems = 16,
                Profile = CreateTraversalProfile()
            }
        };
    }

    private static VyralGraphTraversalProfile CreateTraversalProfile()
    {
        return new VyralGraphTraversalProfile
        {
            Id = "grounded-support",
            Direction = VyralGraphTraversalDirections.Outgoing,
            MaxDepth = 2,
            Predicates = new List<string> { "supports", "mentions" },
            RequireSourceGrounding = true,
            EdgeLimit = 8,
            Limit = 8
        };
    }

    private static VyralGraphEnvelope CreateGraphEnvelope()
    {
        static List<VyralGraphSourceSpan> Source(string id, int end = 20) => new()
        {
            new()
            {
                SourceRef = id,
                CharStart = 0,
                CharEnd = end,
                Unit = "utf16"
            }
        };

        return new VyralGraphEnvelope
        {
            Scope = new VyralGraphScope
            {
                GraphId = GraphId,
                Namespace = "fixtures",
                Collection = Collection,
                TenantId = PartitionKey,
                PartitionKey = PartitionKey
            },
            Nodes = new List<VyralGraphNode>
            {
                new() { Id = "chunk:retention", Type = "chunk", Label = "Retention chunk", SourceSpans = Source("memory://consumer-fixture/retention") },
                new() { Id = "concept:retention-hold", Type = "concept", Label = "Retention hold", SourceSpans = Source("memory://consumer-fixture/retention") },
                new() { Id = "concept:protected-record", Type = "concept", Label = "Protected record", SourceSpans = Source("memory://consumer-fixture/retention") }
            },
            Edges = new List<VyralGraphEdge>
            {
                new()
                {
                    Id = "edge:retention-hold",
                    SourceId = "chunk:retention",
                    TargetId = "concept:retention-hold",
                    Predicate = "supports",
                    SourceSpans = Source("memory://consumer-fixture/retention")
                },
                new()
                {
                    Id = "edge:retention-record",
                    SourceId = "concept:retention-hold",
                    TargetId = "concept:protected-record",
                    Predicate = "mentions",
                    SourceSpans = Source("memory://consumer-fixture/retention")
                }
            }
        };
    }

    private sealed class FixtureEmbeddingProvider : IEmbeddingProvider
    {
        public const int VectorDimensions = 8;

        private static readonly IReadOnlyDictionary<int, string[]> KeywordsByDimension = new Dictionary<int, string[]>
        {
            [0] = new[] { "retention", "hold", "holds", "protected", "records", "deletion" },
            [1] = new[] { "travel", "reimbursement", "receipts", "hotels", "flights", "meals" },
            [2] = new[] { "security", "credentials", "rotated", "incident", "response" },
            [3] = new[] { "product", "listing", "mug", "capacity", "lid" },
            [4] = new[] { "graph", "node", "edge", "provenance" },
            [5] = new[] { "source", "evidence", "grounded", "fixture" },
            [6] = new[] { "local", "development", "consumer" },
            [7] = new[] { "context", "retrieval", "rag" }
        };

        public int Dimensions => VectorDimensions;
        public string ProviderId => "fixture-keyword";
        public string ModelId => "fixture-keyword-embedding-v1";

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        {
            var vector = new float[VectorDimensions];
            var tokens = text
                .ToLowerInvariant()
                .Split(new[] { ' ', '.', ',', ';', ':', '?', '!', '-', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (dimension, keywords) in KeywordsByDimension)
            {
                vector[dimension] = keywords.Count(tokens.Contains);
            }

            return Task.FromResult(vector);
        }
    }
}
