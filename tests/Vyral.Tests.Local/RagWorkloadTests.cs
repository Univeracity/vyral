using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Local;

public class RagWorkloadTests
{
    private const string EmbeddingField = "contentEmbedding";

    [Fact]
    public async Task RetrievalSearch_ReturnsTenantScopedFilteredMatchesAndPersistsTrace()
    {
        var (store, traces, provider) = await CreateRagStoreAsync();
        var retrieval = new LocalRetrievalService(store, provider, traces);

        var result = await retrieval.SearchAsync(new RetrievalRequest
        {
            Query = "How should active retention policy holds be configured for archived data?",
            Collections = new List<string> { "knowledge" },
            PartitionKeys = new List<string> { "tenant-a" },
            Filter = new FilterNode
            {
                Combine = "all",
                Children = new List<FilterNode>
                {
                    new() { Path = "/metadata/status", Op = "eq", Value = "active" },
                    new() { Path = "/metadata/topic", Op = "eq", Value = "retention" }
                }
            },
            Embedding = new EmbeddingOptions { Field = EmbeddingField },
            Limit = 4,
            IncludeTrace = true
        });

        Assert.Equal("How should active retention policy holds be configured for archived data?", result.Query);
        Assert.Equal(new[] { 1, 2, 3, 4 }, result.Results.Select(match => match.Rank));
        Assert.All(result.Results, match =>
        {
            Assert.Equal("knowledge", match.Collection);
            Assert.Equal("tenant-a", match.Record.PartitionKey);
            Assert.Equal("active", MetadataString(match.Record, "status"));
            Assert.Equal("retention", MetadataString(match.Record, "topic"));
            Assert.NotNull(match.Snippet);
            Assert.True(match.Score > 0.65f, $"Expected a semantic retention match, got {match.Score} for {match.Record.Id}.");
        });
        Assert.DoesNotContain(result.Results, match => match.Record.Id is "tenant-b-retention" or "tenant-a-retention-retired");

        var traceId = result.Trace!["id"]!.GetValue<string>();
        var trace = await traces.GetTraceAsync(traceId);

        Assert.NotNull(trace);
        Assert.Equal("retrieval.search", trace.Operation);
        Assert.Equal(4, result.Trace!["returnedCount"]!.GetValue<int>());
        Assert.Equal(provider.Dimensions, result.Trace!["embeddingDimensions"]!.GetValue<int>());
        Assert.Equal("vector", result.Trace!["searchMode"]!.GetValue<string>());

        var diagnostics = result.Results.First().Diagnostics;
        Assert.NotNull(diagnostics);
        Assert.Equal("vyral.retrieval.diagnostics.v1", diagnostics!.SchemaVersion);
        Assert.NotNull(diagnostics.ResultIdentity);
        Assert.Equal("knowledge", diagnostics.ResultIdentity!.Collection);
        Assert.Equal(result.Results.First().Record.PartitionKey, diagnostics.ResultIdentity.PartitionKey);
        Assert.Equal(result.Results.First().Record.Id, diagnostics.ResultIdentity.Id);
        Assert.Equal(result.Results.First().Record.Etag, diagnostics.ResultIdentity.Etag);
        Assert.True(diagnostics.CandidateCounts["collectionVectorCandidates"] >= result.Results.Count);
        Assert.Equal(result.Results.Count, diagnostics.CandidateCounts["returnedCandidates"]);
        Assert.Contains("candidate.source.vector", diagnostics.ReasonCodes);
        Assert.Contains("rank.final.assigned", diagnostics.ReasonCodes);
        Assert.NotNull(diagnostics.ScoreNormalization);
        Assert.Equal("vector.raw_similarity", diagnostics.ScoreNormalization!.FinalScoreKind);
        Assert.Equal("vector.similarity.cosine", diagnostics.ScoreNormalization.VectorScoreKind);
        Assert.Equal("clamp((score+1)/2,0,1)", diagnostics.ScoreNormalization.VectorNormalization);
        var retrievalTraceRef = Assert.Single(diagnostics.TraceReferences, reference => reference.Kind == "retrieval");
        Assert.Equal("retrieval.search", retrievalTraceRef.Operation);
        Assert.Equal(traceId, retrievalTraceRef.TraceId);
        Assert.True(retrievalTraceRef.Safe);
        Assert.Equal("trace_ref_only", retrievalTraceRef.Redaction);
    }

    [Fact]
    public async Task RagIngestion_ReusesExistingChunkVectorsAcrossDocuments()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-rag-vector-reuse-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "knowledge",
            IndexedMetadata = new List<string>
            {
                "/metadata/documentId",
                "/metadata/textHash",
                "/metadata/embeddingProvider",
                "/metadata/embeddingModel"
            },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = EmbeddingField,
                    Path = $"/vectors/{EmbeddingField}/values",
                    Dimensions = CountingEmbeddingProvider.VectorDimensions,
                    DistanceFunction = "cosine",
                    IndexType = "flat"
                }
            }
        });

        var provider = new CountingEmbeddingProvider();
        var ingestion = new LocalRagIngestionService(store, provider);
        const string text = "Reusable retention policy guidance for immutable archive review windows.";

        var first = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "doc-a",
            PartitionKey = "tenant-a",
            Text = text,
            ContentField = "body",
            Options = new RagIngestionOptions { ChunkChars = 500, ChunkOverlapChars = 0, ReuseExistingChunkVectors = true }
        });

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, first.VectorGeneratedCount);
        Assert.Equal(0, first.VectorReusedCount);
        Assert.Equal("generated", first.Chunks[0].EmbeddingAction);

        var second = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "doc-b",
            PartitionKey = "tenant-a",
            Text = text,
            ContentField = "body",
            Options = new RagIngestionOptions { ChunkChars = 500, ChunkOverlapChars = 0, ReuseExistingChunkVectors = true }
        });

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, second.VectorGeneratedCount);
        Assert.Equal(1, second.VectorReusedCount);
        Assert.Equal("reused", second.Chunks[0].EmbeddingAction);
        Assert.Equal(first.Chunks[0].Id, second.Chunks[0].ReusedVectorFromId);
        Assert.Equal("tenant-a", second.Chunks[0].ReusedVectorFromPartitionKey);
        Assert.Equal(1, second.CreatedCount);

        var firstRecord = await store.GetRecordAsync("knowledge", "tenant-a", first.Chunks[0].Id);
        var secondRecord = await store.GetRecordAsync("knowledge", "tenant-a", second.Chunks[0].Id);

        Assert.NotNull(firstRecord);
        Assert.NotNull(secondRecord);
        Assert.Equal(firstRecord!.Vectors![EmbeddingField].Values, secondRecord!.Vectors![EmbeddingField].Values);
        Assert.Equal(firstRecord.Vectors[EmbeddingField].GeneratedAt, secondRecord.Vectors[EmbeddingField].GeneratedAt);
        Assert.Equal(first.Chunks[0].Id, MetadataString(secondRecord.Metadata, "vectorReusedFromId"));

        var crossPartitionDefault = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "doc-c",
            PartitionKey = "tenant-b",
            Text = text,
            ContentField = "body",
            Options = new RagIngestionOptions { ChunkChars = 500, ChunkOverlapChars = 0, ReuseExistingChunkVectors = true }
        });

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(1, crossPartitionDefault.VectorGeneratedCount);
        Assert.Equal(0, crossPartitionDefault.VectorReusedCount);
        Assert.Equal("generated", crossPartitionDefault.Chunks[0].EmbeddingAction);

        var crossPartitionOptIn = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "doc-d",
            PartitionKey = "tenant-c",
            Text = text,
            ContentField = "body",
            Options = new RagIngestionOptions { ChunkChars = 500, ChunkOverlapChars = 0, ReuseExistingChunkVectors = true, VectorReuseScope = "collection" }
        });

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(0, crossPartitionOptIn.VectorGeneratedCount);
        Assert.Equal(1, crossPartitionOptIn.VectorReusedCount);
        Assert.Equal("reused", crossPartitionOptIn.Chunks[0].EmbeddingAction);
        Assert.Equal(first.Chunks[0].Id, crossPartitionOptIn.Chunks[0].ReusedVectorFromId);
        Assert.Equal("tenant-a", crossPartitionOptIn.Chunks[0].ReusedVectorFromPartitionKey);
    }

    [Fact]
    public async Task RagIngestion_TracksPreparedEmbeddingInputForPurposePrefixes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-rag-prefix-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "knowledge",
            IndexedMetadata = new List<string>
            {
                "/metadata/textHash",
                "/metadata/embeddingTextHash",
                "/metadata/embeddingPurpose",
                "/metadata/embeddingProvider",
                "/metadata/embeddingModel",
                "/type"
            },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = EmbeddingField,
                    Path = $"/vectors/{EmbeddingField}/values",
                    Dimensions = CountingEmbeddingProvider.VectorDimensions,
                    DistanceFunction = "cosine",
                    IndexType = "flat"
                }
            }
        });

        var provider = new CountingEmbeddingProvider();
        var ingestion = new LocalRagIngestionService(store, provider);
        const string text = "Retention clause for archive review.";

        var prefixed = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "doc-prefix",
            PartitionKey = "tenant-a",
            Text = text,
            ContentField = "body",
            Embedding = new EmbeddingOptions { Purpose = "passage", PassagePrefix = "passage: " },
            Options = new RagIngestionOptions { ChunkChars = 500, ChunkOverlapChars = 0 }
        });

        Assert.Equal(new[] { "passage: " + text }, provider.Inputs);
        Assert.Equal("passage", prefixed.EmbeddingPurpose);
        Assert.Single(prefixed.Chunks);
        Assert.NotEqual(prefixed.Chunks[0].TextHash, prefixed.Chunks[0].EmbeddingTextHash);

        var record = await store.GetRecordAsync("knowledge", "tenant-a", prefixed.Chunks[0].Id);
        Assert.NotNull(record);
        Assert.Equal("passage", MetadataString(record!, "embeddingPurpose"));
        Assert.Equal(prefixed.Chunks[0].EmbeddingTextHash, MetadataString(record!, "embeddingTextHash"));

        var unchanged = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "doc-prefix",
            PartitionKey = "tenant-a",
            Text = text,
            ContentField = "body",
            Embedding = new EmbeddingOptions { Purpose = "passage", PassagePrefix = "passage: " },
            Options = new RagIngestionOptions { ChunkChars = 500, ChunkOverlapChars = 0, SkipUnchangedChunks = true }
        });

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, unchanged.ReusedCount);
        Assert.Equal(prefixed.Chunks[0].EmbeddingTextHash, unchanged.Chunks[0].EmbeddingTextHash);
    }

    [Fact]
    public async Task RagIngestion_CanDeduplicateExistingChunksWhenExplicitlyEnabled()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-rag-chunk-dedupe-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "knowledge",
            IndexedMetadata = new List<string>
            {
                "/metadata/textHash",
                "/metadata/embeddingProvider",
                "/metadata/embeddingModel",
                "/type"
            },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = EmbeddingField,
                    Path = $"/vectors/{EmbeddingField}/values",
                    Dimensions = CountingEmbeddingProvider.VectorDimensions,
                    DistanceFunction = "cosine",
                    IndexType = "flat"
                }
            }
        });

        var provider = new CountingEmbeddingProvider();
        var ingestion = new LocalRagIngestionService(store, provider);
        const string text = "Canonical retention policy chunk for deduplicated corpus backfills.";

        var first = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "doc-a",
            PartitionKey = "tenant-a",
            Text = text,
            ContentField = "body",
            Options = new RagIngestionOptions { ChunkChars = 500, ChunkOverlapChars = 0, DeduplicateExistingChunks = true }
        });

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, first.CreatedCount);
        Assert.Equal(0, first.DeduplicatedCount);
        Assert.Equal("created", first.Chunks[0].Action);

        var samePartition = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "doc-b",
            PartitionKey = "tenant-a",
            Text = text,
            ContentField = "body",
            Options = new RagIngestionOptions { ChunkChars = 500, ChunkOverlapChars = 0, DeduplicateExistingChunks = true }
        });

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, samePartition.CreatedCount);
        Assert.Equal(0, samePartition.UpdatedCount);
        Assert.Equal(0, samePartition.VectorGeneratedCount);
        Assert.Equal(1, samePartition.DeduplicatedCount);
        Assert.Equal(first.Chunks[0].Id, samePartition.Chunks[0].Id);
        Assert.Equal(first.Chunks[0].Id, samePartition.Chunks[0].DeduplicatedFromId);
        Assert.Equal("tenant-a", samePartition.Chunks[0].DeduplicatedFromPartitionKey);
        Assert.Equal("deduplicated", samePartition.Chunks[0].Action);
        Assert.Equal("deduplicated", samePartition.Chunks[0].EmbeddingAction);

        var chunkRecords = await store.QueryRecordsAsync("knowledge", new QueryEnvelope
        {
            Filter = new FilterNode { Path = "/type", Op = "eq", Value = "rag.chunk" }
        });
        Assert.Single(chunkRecords);

        var crossPartitionDefault = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "doc-c",
            PartitionKey = "tenant-b",
            Text = text,
            ContentField = "body",
            Options = new RagIngestionOptions { ChunkChars = 500, ChunkOverlapChars = 0, DeduplicateExistingChunks = true }
        });

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(1, crossPartitionDefault.CreatedCount);
        Assert.Equal(0, crossPartitionDefault.DeduplicatedCount);
        Assert.NotEqual(first.Chunks[0].Id, crossPartitionDefault.Chunks[0].Id);

        var crossPartitionOptIn = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "doc-d",
            PartitionKey = "tenant-c",
            Text = text,
            ContentField = "body",
            Options = new RagIngestionOptions { ChunkChars = 500, ChunkOverlapChars = 0, DeduplicateExistingChunks = true, ChunkDedupeScope = "collection" }
        });

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(0, crossPartitionOptIn.CreatedCount);
        Assert.Equal(1, crossPartitionOptIn.DeduplicatedCount);
        Assert.Equal(first.Chunks[0].Id, crossPartitionOptIn.Chunks[0].DeduplicatedFromId);
        Assert.Equal("tenant-a", crossPartitionOptIn.Chunks[0].DeduplicatedFromPartitionKey);

        var callsBeforeRequestScope = provider.CallCount;
        var requestScope = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "doc-e",
            PartitionKey = "tenant-d",
            Text = "Repeated request-local clause.\n\nRepeated request-local clause.",
            ContentField = "body",
            Options = new RagIngestionOptions { ChunkChars = 35, ChunkOverlapChars = 0, DeduplicateExistingChunks = true, ChunkDedupeScope = "request" }
        });

        Assert.Equal(callsBeforeRequestScope + 1, provider.CallCount);
        Assert.Equal(2, requestScope.ChunkCount);
        Assert.Equal(1, requestScope.CreatedCount);
        Assert.Equal(1, requestScope.DeduplicatedCount);
        Assert.Equal(requestScope.Chunks[0].Id, requestScope.Chunks[1].DeduplicatedFromId);
        Assert.Equal("tenant-d", requestScope.Chunks[1].DeduplicatedFromPartitionKey);
    }

    [Fact]
    public async Task RagIngestion_DryRunPlansWritesWithoutMutatingStore()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-rag-dryrun-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "knowledge",
            IndexedMetadata = new List<string>
            {
                "/metadata/documentId",
                "/type"
            },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = EmbeddingField,
                    Path = $"/vectors/{EmbeddingField}/values",
                    Dimensions = CountingEmbeddingProvider.VectorDimensions,
                    DistanceFunction = "cosine",
                    IndexType = "flat"
                }
            }
        });

        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();
        var provider = new CountingEmbeddingProvider();
        var ingestion = new LocalRagIngestionService(store, provider, traces);
        var originalText = string.Join(" ", Enumerable.Repeat("Original retention archive review paragraph.", 5));
        var original = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "policy-doc",
            PartitionKey = "tenant-a",
            Text = originalText,
            Options = new RagIngestionOptions { ChunkChars = 55, ChunkOverlapChars = 0, PersistManifest = true }
        });

        var callsAfterOriginal = provider.CallCount;
        Assert.True(original.ChunkCount > 1);
        Assert.Equal(original.ChunkCount, original.CreatedCount);
        Assert.Equal("created", original.ManifestAction);

        var plan = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "policy-doc",
            PartitionKey = "tenant-a",
            Text = "Replacement retention summary.",
            Options = new RagIngestionOptions { ChunkChars = 120, ChunkOverlapChars = 0, ReplaceDocumentChunks = true, PersistManifest = true, DryRun = true, IncludeTrace = true }
        });

        Assert.True(plan.DryRun);
        Assert.StartsWith("sha256:", plan.PlanHash);
        Assert.Equal(callsAfterOriginal, provider.CallCount);
        Assert.Single(plan.Chunks);
        Assert.Equal(1, plan.CreatedCount);
        Assert.Equal(1, plan.VectorGeneratedCount);
        Assert.Equal(original.ChunkCount, plan.DeletedStaleCount);
        Assert.Equal(original.Chunks.Select(chunk => chunk.Id), plan.StaleDeletes.Select(stale => stale.Id));
        Assert.All(plan.StaleDeletes, stale =>
        {
            Assert.Equal("tenant-a", stale.PartitionKey);
            Assert.StartsWith("sha256:", stale.TextHash);
            Assert.NotNull(stale.Etag);
            Assert.NotNull(stale.Revision);
        });
        Assert.Equal("updated", plan.ManifestAction);
        Assert.Null(plan.ManifestEtag);
        Assert.Null(plan.ManifestRevision);
        Assert.Equal("not_provided", plan.PlanHashComparison.Status);
        Assert.Equal("not_provided", plan.ManifestHashComparison.Status);
        Assert.Equal(1, plan.ActionSummary.ActionCounts["created"]);
        Assert.Equal(0, plan.ActionSummary.ActionCounts["updated"]);
        Assert.Equal(1, plan.ActionSummary.EmbeddingActionCounts["generated"]);
        Assert.Equal(original.ChunkCount, plan.ActionSummary.StaleDeleteIds.Count);
        Assert.Equal(plan.StaleDeletes.Select(stale => stale.Id).OrderBy(id => id), plan.ActionSummary.StaleDeleteIds.OrderBy(id => id));
        Assert.NotNull(plan.Trace);
        Assert.True(plan.Trace!["dryRun"]!.GetValue<bool>());
        Assert.Equal(plan.PlanHash, plan.Trace!["planHash"]!.GetValue<string>());
        Assert.Equal(plan.StaleDeletes.Select(stale => stale.Id), plan.Trace!["staleDeleteIds"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.False(plan.Trace!["tracePersisted"]!.GetValue<bool>());
        Assert.Null(await traces.GetTraceAsync(plan.Trace!["id"]!.GetValue<string>()));

        var driftPlan = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "policy-doc",
            PartitionKey = "tenant-a",
            Text = "Replacement retention summary.",
            Options = new RagIngestionOptions { ChunkChars = 120, ChunkOverlapChars = 0, ReplaceDocumentChunks = true, PersistManifest = true, DryRun = true, ExpectedPlanHash = "sha256:not-the-current-plan", ExpectedManifestHash = "sha256:not-the-current-manifest" }
        });

        Assert.Equal("drifted", driftPlan.PlanHashComparison.Status);
        Assert.False(driftPlan.PlanHashComparison.Matches);
        Assert.Equal("sha256:not-the-current-plan", driftPlan.PlanHashComparison.ExpectedHash);
        Assert.Equal(plan.PlanHash, driftPlan.PlanHashComparison.ActualHash);
        Assert.Equal("drifted", driftPlan.ManifestHashComparison.Status);

        var originalQuery = await store.QueryRecordsAsync("knowledge", new QueryEnvelope
        {
            PartitionKeys = new List<string> { "tenant-a" },
            Filter = new FilterNode
            {
                Combine = "all",
                Children = new List<FilterNode>
                {
                    new() { Path = "/metadata/documentId", Op = "eq", Value = "policy-doc" },
                    new() { Path = "/type", Op = "eq", Value = "rag.chunk" }
                }
            }
        });
        Assert.Equal(original.ChunkCount, originalQuery.Count());
        Assert.Null(await store.GetRecordAsync("knowledge", "tenant-a", plan.Chunks[0].Id));

        var manifest = await store.GetRecordAsync("knowledge", "tenant-a", original.ManifestId!);
        Assert.NotNull(manifest);
        Assert.Equal(original.ManifestHash, MetadataString(manifest!.Metadata, "manifestHash"));

        var committed = await ingestion.IngestTextAsync("knowledge", new RagIngestTextRequest
        {
            DocumentId = "policy-doc",
            PartitionKey = "tenant-a",
            Text = "Replacement retention summary.",
            Options = new RagIngestionOptions { ChunkChars = 120, ChunkOverlapChars = 0, ReplaceDocumentChunks = true, PersistManifest = true, ExpectedPlanHash = plan.PlanHash, ExpectedManifestHash = plan.ManifestHash, IncludeTrace = true }
        });

        Assert.False(committed.DryRun);
        Assert.Equal(plan.PlanHash, committed.PlanHash);
        Assert.Equal(plan.ManifestHash, committed.ManifestHash);
        Assert.True(committed.PlanHashComparison.Matches);
        Assert.Equal("matched", committed.PlanHashComparison.Status);
        Assert.True(committed.ManifestHashComparison.Matches);
        Assert.Equal("matched", committed.ManifestHashComparison.Status);
        Assert.Equal(plan.ActionSummary.ActionCounts["created"], committed.ActionSummary.ActionCounts["created"]);
        Assert.Equal(plan.ActionSummary.EmbeddingActionCounts["generated"], committed.ActionSummary.EmbeddingActionCounts["generated"]);
        Assert.Equal(1, committed.CreatedCount);
        Assert.Equal(1, committed.VectorGeneratedCount);
        Assert.Equal(original.ChunkCount, committed.DeletedStaleCount);
        Assert.Equal(plan.StaleDeletes.Select(stale => stale.Id), committed.StaleDeletes.Select(stale => stale.Id));
        Assert.NotNull(committed.Trace);
        Assert.Equal(committed.PlanHash, committed.Trace!["planHash"]!.GetValue<string>());
        Assert.Equal(committed.StaleDeletes.Select(stale => stale.Id), committed.Trace!["staleDeleteIds"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.True(committed.Trace!["tracePersisted"]!.GetValue<bool>());
        Assert.NotNull(await traces.GetTraceAsync(committed.Trace!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task RagIngestion_PersistedManifestUsesStablePolicyAwareRawTextFreeShape()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-rag-manifest-shape-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "knowledge",
            IndexedMetadata = new List<string>
            {
                "/metadata/documentId",
                "/metadata/manifestHash",
                "/metadata/embeddingProvider",
                "/metadata/embeddingModel",
                "/type"
            },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = EmbeddingField,
                    Path = $"/vectors/{EmbeddingField}/values",
                    Dimensions = CountingEmbeddingProvider.VectorDimensions,
                    Datatype = "float32",
                    DistanceFunction = "cosine",
                    IndexType = "quantizedFlat"
                }
            }
        });

        var provider = new CountingEmbeddingProvider();
        var ingestion = new LocalRagIngestionService(store, provider);
        var sourceText = string.Join(" ", new[]
        {
            "Confidential sentinel alpha retention source text belongs only in chunk records.",
            "Manifest conformance should preserve spans hashes sources and vector policy shape.",
            "Repeated retention archive guidance gives the splitter enough text for multiple chunks.",
            "Final retention note keeps immutable archive review windows auditable."
        });
        var request = new RagIngestTextRequest
        {
            DocumentId = "policy-manifest",
            PartitionKey = "tenant-a",
            Text = sourceText,
            ContentField = "body",
            Metadata = new JsonObject
            {
                ["status"] = "active",
                ["documentKind"] = "policy"
            },
            SourceUri = "file:///docs/policy-manifest.md",
            SourceKind = "markdown",
            SourceId = "src-policy-manifest",
            SourceLabel = "Policy manifest source",
            Options = new RagIngestionOptions { ChunkChars = 95, ChunkOverlapChars = 10, ReplaceDocumentChunks = true, SkipUnchangedChunks = true, PersistManifest = true }
        };

        var first = await ingestion.IngestTextAsync("knowledge", request);

        Assert.True(first.ChunkCount > 1);
        Assert.Equal(first.ChunkCount, first.CreatedCount);
        Assert.Equal(first.ChunkCount, first.VectorGeneratedCount);
        Assert.Equal("created", first.ManifestAction);
        Assert.StartsWith("sha256:", first.ManifestHash, StringComparison.Ordinal);

        var manifest = await store.GetRecordAsync("knowledge", "tenant-a", first.ManifestId!);
        Assert.NotNull(manifest);
        Assert.Equal("rag.manifest", manifest!.Type);
        Assert.Equal("v1", manifest.SchemaVersion);
        Assert.Null(manifest.Vectors);
        Assert.NotNull(manifest.Sources);
        var manifestSource = Assert.Single(manifest.Sources!);
        Assert.Equal("src-policy-manifest", manifestSource.Id);
        Assert.Equal("markdown", manifestSource.Kind);
        Assert.Equal("file:///docs/policy-manifest.md", manifestSource.Uri);

        Assert.Equal("policy-manifest", MetadataString(manifest, "documentId"));
        Assert.Equal(first.TextHash, MetadataString(manifest, "documentTextHash"));
        Assert.Equal(first.ManifestHash, MetadataString(manifest, "manifestHash"));
        Assert.Equal("v1", MetadataString(manifest, "manifestVersion"));
        Assert.Equal("rag.chunk", MetadataString(manifest, "chunkRecordType"));
        Assert.Equal(first.ChunkCount, MetadataInt(manifest.Metadata, "chunkCount"));
        Assert.Equal(sourceText.Length, MetadataInt(manifest.Metadata, "textLength"));
        Assert.Equal(EmbeddingField, MetadataString(manifest, "embeddingField"));
        Assert.Equal(provider.ProviderId, MetadataString(manifest, "embeddingProvider"));
        Assert.Equal(provider.ModelId, MetadataString(manifest, "embeddingModel"));
        Assert.Equal(CountingEmbeddingProvider.VectorDimensions, MetadataInt(manifest.Metadata, "embeddingDimensions"));

        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("Confidential sentinel alpha", manifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"values\"", manifestJson, StringComparison.Ordinal);
        Assert.Contains(first.Chunks[0].Id, manifestJson, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(manifestJson);
        var manifestElement = document.RootElement
            .GetProperty("content")
            .GetProperty("manifest");
        Assert.Equal("v1", manifestElement.GetProperty("version").GetString());
        Assert.Equal(first.ManifestHash, manifestElement.GetProperty("manifestHash").GetString());
        Assert.False(manifestElement.GetProperty("rawTextIncluded").GetBoolean());
        Assert.Equal(first.TextHash, manifestElement.GetProperty("sourceTextHash").GetString());

        var ingestionPlan = manifestElement.GetProperty("ingestionPlan");
        Assert.Equal("knowledge", ingestionPlan.GetProperty("collection").GetString());
        Assert.Equal("tenant-a", ingestionPlan.GetProperty("partitionKey").GetString());
        Assert.Equal("policy-manifest", ingestionPlan.GetProperty("documentId").GetString());
        Assert.Equal("body", ingestionPlan.GetProperty("contentField").GetString());
        Assert.Equal(EmbeddingField, ingestionPlan.GetProperty("embeddingField").GetString());
        Assert.Equal(provider.ProviderId, ingestionPlan.GetProperty("embeddingProvider").GetString());
        Assert.Equal(provider.ModelId, ingestionPlan.GetProperty("embeddingModel").GetString());
        Assert.Equal(CountingEmbeddingProvider.VectorDimensions, ingestionPlan.GetProperty("embeddingDimensions").GetInt32());
        Assert.Equal($"/vectors/{EmbeddingField}/values", ingestionPlan.GetProperty("vectorPath").GetString());
        Assert.Equal("float32", ingestionPlan.GetProperty("vectorDatatype").GetString());
        Assert.Equal("cosine", ingestionPlan.GetProperty("vectorDistanceFunction").GetString());
        Assert.Equal("quantizedFlat", ingestionPlan.GetProperty("vectorIndexType").GetString());

        var planChunks = ingestionPlan.GetProperty("chunks").EnumerateArray().ToList();
        var chunkSpans = manifestElement.GetProperty("chunkSpans").EnumerateArray().ToList();
        Assert.Equal(first.ChunkCount, planChunks.Count);
        Assert.Equal(first.ChunkCount, chunkSpans.Count);
        for (var i = 0; i < first.Chunks.Count; i++)
        {
            var resultChunk = first.Chunks[i];
            var planChunk = planChunks[i];
            var span = chunkSpans[i];

            Assert.Equal(resultChunk.Index, planChunk.GetProperty("index").GetInt32());
            Assert.Equal(resultChunk.Id, planChunk.GetProperty("id").GetString());
            Assert.Equal(resultChunk.PartitionKey, planChunk.GetProperty("partitionKey").GetString());
            Assert.Equal(resultChunk.TextHash, planChunk.GetProperty("textHash").GetString());
            Assert.Equal(resultChunk.CharStart, planChunk.GetProperty("charStart").GetInt32());
            Assert.Equal(resultChunk.CharEnd, planChunk.GetProperty("charEnd").GetInt32());
            Assert.Equal(resultChunk.TextLength, planChunk.GetProperty("textLength").GetInt32());
            Assert.Equal(resultChunk.Index, span.GetProperty("index").GetInt32());
            Assert.Equal(resultChunk.CharStart, span.GetProperty("charStart").GetInt32());
            Assert.Equal(resultChunk.CharEnd, span.GetProperty("charEnd").GetInt32());
            Assert.False(planChunk.TryGetProperty("text", out _));
            Assert.False(planChunk.TryGetProperty("content", out _));
        }

        var callsAfterFirst = provider.CallCount;
        var second = await ingestion.IngestTextAsync("knowledge", request);

        Assert.Equal(callsAfterFirst, provider.CallCount);
        Assert.Equal(first.ChunkCount, second.ReusedCount);
        Assert.Equal(0, second.CreatedCount);
        Assert.Equal(0, second.UpdatedCount);
        Assert.Equal(0, second.VectorGeneratedCount);
        Assert.Equal("reused", second.ManifestAction);
        Assert.Equal(first.ManifestHash, second.ManifestHash);
        Assert.Equal(first.ManifestEtag, second.ManifestEtag);
        Assert.Equal(first.ManifestRevision, second.ManifestRevision);
    }

    [Fact]
    public async Task VectorSearch_PaginatesStableTopKAcrossRagCorpus()
    {
        var (store, _, provider) = await CreateRagStoreAsync();
        var queryVector = await provider.GenerateEmbeddingAsync("active retention policy archive deletion records");
        var query = new QueryEnvelope
        {
            PartitionKeys = new List<string> { "tenant-a" },
            Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
            Vector = new VectorSearchOptions { Field = EmbeddingField, Value = queryVector, Top = 7 },
            Limit = 3
        };

        var pagedIds = new List<string>();
        string? continuation = null;
        do
        {
            query.ContinuationToken = continuation;
            var page = await store.SearchRecordsPageAsync("knowledge", query);
            pagedIds.AddRange(page.Items.Select(match => match.Record.Id));
            continuation = page.ContinuationToken;

            Assert.True(page.Items.Count <= 3);
        }
        while (continuation != null);

        var allAtOnce = await store.SearchRecordsPageAsync("knowledge", new QueryEnvelope
        {
            PartitionKeys = new List<string> { "tenant-a" },
            Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
            Vector = new VectorSearchOptions { Field = EmbeddingField, Value = queryVector, Top = 7 },
            Limit = 7
        });

        Assert.Equal(allAtOnce.Items.Select(match => match.Record.Id), pagedIds);
        Assert.Equal(pagedIds.Count, pagedIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("tenant-a-retention-archive", pagedIds);
        Assert.Contains("tenant-a-retention-holds", pagedIds);
    }

    [Fact]
    public async Task RetrievalSearch_AppliesMinScoreToSuppressLowSimilarityContext()
    {
        var (store, _, provider) = await CreateRagStoreAsync();
        var retrieval = new LocalRetrievalService(store, provider);

        var result = await retrieval.SearchAsync(new RetrievalRequest
        {
            Query = "retention archive deletion policy",
            Collections = new List<string> { "knowledge" },
            PartitionKeys = new List<string> { "tenant-a" },
            Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
            SearchMode = "vector",
            Embedding = new EmbeddingOptions { Field = EmbeddingField },
            Limit = 10,
            MinScore = 0.65f
        });

        Assert.NotEmpty(result.Results);
        Assert.All(result.Results, match =>
        {
            Assert.True(match.Score >= 0.65f, $"Expected score >= 0.65, got {match.Score} for {match.Record.Id}.");
            Assert.Equal("retention", MetadataString(match.Record, "topic"));
        });
    }

    [Fact]
    public async Task RetrievalSearch_DefaultsToLexicalForVectorlessRecords()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-record-lexical-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "record-pages",
            IndexedMetadata = new List<string> { "/metadata/referenceId", "/metadata/status" }
        });
        await store.UpsertRecordAsync("record-pages", new VyralRecord
        {
            Id = "page-001",
            PartitionKey = "source-a",
            Type = "page",
            Metadata = new JsonObject
            {
                ["referenceId"] = "RECORD-000123",
                ["status"] = "active"
            },
            Content = new JsonObject
            {
                ["text"] = "RECORD-000123 contains the exact update deadline and retention notice."
            }
        });

        var retrieval = new LocalRetrievalService(store, new KeywordEmbeddingProvider());
        var result = await retrieval.SearchAsync(new RetrievalRequest
        {
            Query = "RECORD-000123 update deadline",
            Collections = new List<string> { "record-pages" },
            PartitionKeys = new List<string> { "source-a" },
            Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
            Limit = 3,
            IncludeTrace = true,
            Lexical = new LexicalSearchOptions
            {
                Fields = new List<string> { "/content/text", "/metadata/referenceId" }
            }
        });

        var match = Assert.Single(result.Results);
        Assert.Equal("page-001", match.Record.Id);
        Assert.NotNull(match.Diagnostics);
        Assert.Contains("lexical", match.Diagnostics!.CandidateSources);
        Assert.Contains("/metadata/referenceId", match.Diagnostics.MatchedFields);
        Assert.Contains("record", match.Diagnostics.MatchedTerms);
        Assert.Equal("lexical", result.Trace!["searchMode"]!.GetValue<string>());
    }

    [Fact]
    public async Task RetrievalSearch_HybridBoostsExactRecordPhrasesAndExplainsScores()
    {
        var (store, _, provider) = await CreateRagStoreAsync();
        var unrelatedVector = await provider.GenerateEmbeddingAsync("billing invoice payment adjustment");
        await store.UpsertRecordAsync("knowledge", new VyralRecord
        {
            Id = "record-page-000123",
            PartitionKey = "tenant-a",
            Type = "page",
            Metadata = new JsonObject
            {
                ["topic"] = "records",
                ["status"] = "active",
                ["source"] = "source-record",
                ["referenceId"] = "RECORD-000123"
            },
            Content = new JsonObject
            {
                ["text"] = "RECORD-000123 states the update deadline and retention instruction."
            },
            Vectors = new Dictionary<string, VyralVector>
            {
                [EmbeddingField] = new()
                {
                    Values = unrelatedVector,
                    Dimensions = unrelatedVector.Length,
                    Model = provider.ModelId,
                    DistanceFunction = "cosine",
                    SourceField = "content.text",
                    GeneratedAt = DateTime.UtcNow
                }
            }
        });

        var retrieval = new LocalRetrievalService(store, provider);
        var result = await retrieval.SearchAsync(new RetrievalRequest
        {
            Query = "RECORD-000123 update deadline",
            Collections = new List<string> { "knowledge" },
            PartitionKeys = new List<string> { "tenant-a" },
            Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
            SearchMode = "hybrid",
            Limit = 5,
            IncludeTrace = true,
            Lexical = new LexicalSearchOptions
            {
                Fields = new List<string> { "/content/text", "/metadata/referenceId", "/id" }
            },
            Hybrid = new HybridSearchOptions
            {
                Fusion = "rrf",
                VectorWeight = 0.2f,
                LexicalWeight = 0.8f,
                CandidateMultiplier = 6,
                RrfK = 60
            }
        });

        Assert.NotEmpty(result.Results);
        var first = result.Results.First();
        Assert.Equal("record-page-000123", first.Record.Id);
        Assert.NotNull(first.Diagnostics);
        Assert.Contains("vector", first.Diagnostics!.CandidateSources);
        Assert.Contains("lexical", first.Diagnostics.CandidateSources);
        Assert.True(first.Diagnostics.ScoreComponents["lexical"] > 0.75f);
        Assert.True(first.Diagnostics.ScoreComponents["vectorWeight"] > 0);
        Assert.True(first.Diagnostics.ScoreComponents["lexicalWeight"] > 0);
        Assert.True(first.Diagnostics.ScoreComponents["rrf"] > 0);
        Assert.Contains("/metadata/referenceId", first.Diagnostics.MatchedFields);
        Assert.Equal("hybrid", first.Diagnostics.Details["searchMode"]);
        Assert.Equal("rrf", first.Diagnostics.Details["hybridFusion"]);
        Assert.Equal("hybrid", result.Trace!["searchMode"]!.GetValue<string>());
        Assert.True(result.Trace!["candidateCount"]!.GetValue<int>() >= result.Results.Count);
    }

    [Fact]
    public async Task RetrievalSearch_CanRerankCandidatePoolBeforeReturningResults()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-rerank-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "notes",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = EmbeddingField, Path = $"/vectors/{EmbeddingField}/values", Dimensions = 2 }
            }
        });

        foreach (var record in new[]
        {
            ("a-general", "travel reimbursement policy and meal expense rules"),
            ("z-retention", "active retention policy hold details")
        })
        {
            await store.UpsertRecordAsync("notes", new VyralRecord
            {
                Id = record.Item1,
                PartitionKey = "tenant-a",
                Content = new JsonObject { ["text"] = record.Item2 },
                Vectors = new Dictionary<string, VyralVector>
                {
                    [EmbeddingField] = new() { Values = new float[] { 1, 0 }, Dimensions = 2 }
                }
            });
        }

        var retrieval = new LocalRetrievalService(
            store,
            new FixedEmbeddingProvider(),
            reranker: new LocalTokenOverlapRerankingService());
        var result = await retrieval.SearchAsync(new RetrievalRequest
        {
            Query = "retention policy",
            Collections = new List<string> { "notes" },
            SearchMode = "vector",
            Embedding = new EmbeddingOptions { Field = EmbeddingField },
            Limit = 1,
            IncludeTrace = true,
            Rerank = new RerankOptions
            {
                Enabled = true,
                CandidateLimit = 2,
                ContentField = "text"
            }
        });

        var match = Assert.Single(result.Results);
        Assert.Equal("z-retention", match.Record.Id);
        Assert.NotNull(match.Diagnostics);
        Assert.Contains("rerank", match.Diagnostics!.CandidateSources);
        Assert.Equal("local-token-overlap-reranker", match.Diagnostics.Details["rerankProvider"]);
        Assert.Equal(2, Convert.ToInt32(match.Diagnostics.Details["preRerankRank"]));
        Assert.Equal(1, Convert.ToInt32(match.Diagnostics.Details["rerankRank"]));
        Assert.Equal(2, match.Diagnostics.CandidateCounts["rerankInputCandidates"]);
        Assert.Contains("rerank.applied", match.Diagnostics.ReasonCodes);
        Assert.Contains("score.rerank.blended", match.Diagnostics.ReasonCodes);
        Assert.Equal("rerank.weighted_blend", match.Diagnostics.ScoreNormalization!.FinalScoreKind);
        Assert.True(result.Trace!["rerankEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RetrievalSearch_Bm25RarityAndFieldBoostsBreakRecordLexicalTies()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-record-bm25-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "record-pages",
            IndexedMetadata = new List<string> { "/metadata/event", "/metadata/status" }
        });

        await store.UpsertRecordAsync("record-pages", new VyralRecord
        {
            Id = "target-authorization",
            PartitionKey = "source-a",
            Type = "page",
            Metadata = new JsonObject
            {
                ["event"] = "authorization",
                ["status"] = "active",
                ["referenceId"] = "RECORD-000777"
            },
            Content = new JsonObject
            {
                ["text"] = "The comprehensive business plan was accepted only after explicit authorization by the hearing officer."
            }
        });

        for (var index = 0; index < 8; index++)
        {
            await store.UpsertRecordAsync("record-pages", new VyralRecord
            {
                Id = $"boilerplate-{index:00}",
                PartitionKey = "source-a",
                Type = "page",
                Metadata = new JsonObject
                {
                    ["event"] = "planning",
                    ["status"] = "active"
                },
                Content = new JsonObject
                {
                    ["text"] = "Business plan comprehensive business plan comprehensive business plan overview."
                }
            });
        }

        var retrieval = new LocalRetrievalService(store, new KeywordEmbeddingProvider());
        var result = await retrieval.SearchAsync(new RetrievalRequest
        {
            Query = "business plan comprehensive authorization",
            Collections = new List<string> { "record-pages" },
            PartitionKeys = new List<string> { "source-a" },
            SearchMode = "lexical",
            Limit = 5,
            IncludeTrace = true,
            Lexical = new LexicalSearchOptions
            {
                Fields = new List<string> { "/content/text", "/metadata/event", "/metadata/referenceId" },
                FieldBoosts = new Dictionary<string, float>
                {
                    ["/metadata/event"] = 2.5f,
                    ["/metadata/referenceId"] = 1.5f
                }
            }
        });

        Assert.NotEmpty(result.Results);
        var first = result.Results.First();
        Assert.Equal("target-authorization", first.Record.Id);
        Assert.NotNull(first.Diagnostics);
        Assert.Equal("bm25", first.Diagnostics!.Details["lexicalScoring"]);
        Assert.Equal("score desc, collection asc, partitionKey asc, id asc", first.Diagnostics.Details["tieBreakOrder"]);

        var termIdf = Assert.IsType<Dictionary<string, float>>(first.Diagnostics.Details["termIdf"]);
        Assert.True(termIdf["authorization"] > termIdf["business"]);
        var termScores = Assert.IsType<Dictionary<string, float>>(first.Diagnostics.Details["termScores"]);
        Assert.True(termScores["authorization"] > termScores["business"]);
        var fieldBoosts = Assert.IsType<Dictionary<string, float>>(first.Diagnostics.Details["fieldBoosts"]);
        Assert.Equal(2.5f, fieldBoosts["/metadata/event"]);
    }

    [Fact]
    public async Task RagContext_BuildsBudgetedChunksWithSourcesAndTrace()
    {
        var (store, _, provider) = await CreateRagStoreAsync();
        var retrieval = new LocalRetrievalService(store, provider);
        var rag = new LocalRagContextService(retrieval);

        var context = await rag.BuildContextAsync(new RagContextRequest
        {
            Retrieval = new RetrievalRequest
            {
                Query = "retention archive deletion policy",
                Collections = new List<string> { "knowledge" },
                PartitionKeys = new List<string> { "tenant-a" },
                Filter = new FilterNode
                {
                    Combine = "all",
                    Children = new List<FilterNode>
                    {
                        new() { Path = "/metadata/status", Op = "eq", Value = "active" },
                        new() { Path = "/metadata/topic", Op = "eq", Value = "retention" }
                    }
                },
                SearchMode = "vector",
                Embedding = new EmbeddingOptions { Field = EmbeddingField },
                Limit = 5,
                MinScore = 0.65f
            },
            MaxChars = 180,
            MaxCharsPerChunk = 90,
            IncludeContextText = true,
            IncludeTrace = true
        });

        Assert.Equal("retention archive deletion policy", context.Query);
        Assert.NotEmpty(context.Chunks);
        Assert.NotEmpty(context.Citations);
        Assert.True(context.TotalChars <= 180);
        Assert.All(context.Chunks, chunk =>
        {
            Assert.True(chunk.Text.Length <= 90);
            Assert.Equal("text", chunk.ContentField);
            Assert.Equal(0, chunk.CharStart);
            Assert.True(chunk.CharEnd <= chunk.OriginalTextLength);
            Assert.Equal(chunk.Text.EndsWith("...", StringComparison.Ordinal), chunk.Truncated);
            Assert.StartsWith("sha256:", chunk.ContextExcerptHash, StringComparison.Ordinal);
            Assert.NotNull(chunk.RetrievalDiagnostics);
            Assert.NotNull(chunk.RetrievalMatch);
            Assert.Equal("vector", chunk.RetrievalMatch!.SearchMode);
            Assert.Equal(chunk.Score, chunk.RetrievalMatch.Score);
            Assert.Equal("tenant-a", chunk.PartitionKey);
            Assert.Equal("active", MetadataString(chunk.Metadata, "status"));
            Assert.Equal("retention", MetadataString(chunk.Metadata, "topic"));
            Assert.NotNull(chunk.Sources);
            Assert.NotEmpty(chunk.Sources);
            Assert.NotEmpty(chunk.CitationIds);
            Assert.All(chunk.CitationIds, citationId =>
            {
                var citation = Assert.Single(context.Citations, item => item.Id == citationId);
                Assert.Equal(chunk.Rank, citation.ChunkRank);
                Assert.Equal(chunk.Collection, citation.Collection);
                Assert.Equal(chunk.PartitionKey, citation.PartitionKey);
                Assert.Equal(chunk.Id, citation.RecordId);
                Assert.Equal(chunk.ContextExcerptHash, citation.ContextExcerptHash);
                Assert.Equal(chunk.CharStart, citation.ContextCharStart);
                Assert.Equal(chunk.CharEnd, citation.ContextCharEnd);
                Assert.NotNull(citation.SourceUri);
                Assert.NotNull(citation.SourceSpan);
                Assert.NotNull(citation.IncludedSourceSpan);
                Assert.True((citation.IncludedSourceSpan!.CharEnd ?? 0) <= (citation.SourceSpan!.CharEnd ?? 0));
            });
            Assert.Null(chunk.Record);
        });
        Assert.Equal(new[] { 1, 2 }, context.Chunks.Take(2).Select(chunk => chunk.Rank));
        Assert.NotNull(context.Trace);
        Assert.Equal(context.Chunks.Count, context.Trace!["chunkCount"]!.GetValue<int>());
        Assert.Equal(context.Citations.Count, context.Trace!["citationCount"]!.GetValue<int>());
        Assert.Equal("citation-markdown", context.ContextTextFormat);
        Assert.StartsWith("sha256:", context.ContextTextHash, StringComparison.Ordinal);
        Assert.Contains("Context:", context.ContextText);
        Assert.Contains("Citations:", context.ContextText);
        Assert.Contains("[" + context.Chunks[0].CitationIds[0] + "]", context.ContextText);
        Assert.Contains(context.Citations[0].RecordId, context.ContextText);
        Assert.Equal(context.ContextTextHash, context.Trace!["contextTextHash"]!.GetValue<string>());

        var withoutCitations = await rag.BuildContextAsync(new RagContextRequest
        {
            Retrieval = new RetrievalRequest
            {
                Query = "retention archive deletion policy",
                Collections = new List<string> { "knowledge" },
                PartitionKeys = new List<string> { "tenant-a" },
                Filter = new FilterNode { Path = "/metadata/topic", Op = "eq", Value = "retention" },
                Embedding = new EmbeddingOptions { Field = EmbeddingField },
                Limit = 1
            },
            MaxChars = 120,
            MaxCharsPerChunk = 120,
            IncludeCitations = false
        });

        Assert.Empty(withoutCitations.Citations);
        Assert.Empty(Assert.Single(withoutCitations.Chunks).CitationIds);
    }

    [Fact]
    public async Task RagContext_AppliesProfilesAndCitationCaps()
    {
        var (store, _, provider) = await CreateRagStoreAsync();
        var multiSource = await CreateChunkAsync(
            provider,
            id: "tenant-a-retention-multi-source",
            partitionKey: "tenant-a",
            topic: "retention",
            status: "active",
            source: "manual",
            text: "Multi source retention citation packet with archive deletion evidence and policy review notes.");
        multiSource.Sources!.Add(new VyralSourceReference
        {
            Id = "faq:tenant-a-retention-multi-source",
            Kind = "faq",
            Uri = "file:///faq/tenant-a-retention-multi-source.md",
            Label = "faq",
            Span = new VyralSourceSpan { CharStart = 0, CharEnd = 92 }
        });
        multiSource.Sources!.Add(new VyralSourceReference
        {
            Id = "runbook:tenant-a-retention-multi-source",
            Kind = "runbook",
            Uri = "file:///runbook/tenant-a-retention-multi-source.md",
            Label = "runbook",
            Span = new VyralSourceSpan { CharStart = 0, CharEnd = 92 }
        });
        await store.UpsertRecordAsync("knowledge", multiSource);

        var retrieval = new LocalRetrievalService(store, provider);
        var rag = new LocalRagContextService(retrieval);

        var context = await rag.BuildContextAsync(new RagContextRequest
        {
            Retrieval = new RetrievalRequest
            {
                Profile = RetrievalProfileIds.RagBaseline,
                Query = "multi source retention citation packet",
                Collections = new List<string> { "knowledge" },
                PartitionKeys = new List<string> { "tenant-a" },
                Limit = 1
            },
            MaxChars = 500,
            MaxCharsPerChunk = 250,
            MaxCitationsPerChunk = 1,
            IncludeContextText = true,
            IncludeTrace = true
        });

        var chunk = Assert.Single(context.Chunks);
        Assert.Equal("tenant-a-retention-multi-source", chunk.Id);
        Assert.Single(chunk.CitationIds);
        Assert.Single(context.Citations);
        Assert.Equal(2, context.OmittedCitationCount);
        Assert.NotNull(chunk.RetrievalMatch);
        Assert.Equal(SearchModes.Lexical, chunk.RetrievalMatch!.SearchMode);
        Assert.NotNull(context.Trace);
        Assert.Equal(2, context.Trace!["omittedCitationCount"]!.GetValue<int>());
        Assert.Equal(SearchModes.Lexical, context.Trace!["effectiveSearchMode"]!.GetValue<string>());
    }

    [Fact]
    public async Task RagContext_GraphExpansionUsesRetrievedRecordsAsTraversalSeeds()
    {
        var (store, _, provider) = await CreateRagStoreAsync();
        await store.ImportGraphEnvelopeAsync("graphs", new VyralGraphCollectionImportRequest
        {
            Envelope = new VyralGraphEnvelope
            {
                Scope = new VyralGraphScope
                {
                    GraphId = "retention-controls",
                    Namespace = "rag",
                    Collection = "knowledge",
                    TenantId = "tenant-a",
                    PartitionKey = "tenant-a"
                },
                Nodes = new List<VyralGraphNode>
                {
                    new() { Id = "tenant-a-retention-archive", Type = "chunk", Label = "Archive retention guidance" },
                    new() { Id = "control:release-review", Type = "control", Label = "Release review hold" },
                    new() { Id = "control:audit-log", Type = "control", Label = "Audit log evidence" }
                },
                Edges = new List<VyralGraphEdge>
                {
                    new()
                    {
                        Id = "edge:archive-review",
                        SourceId = "tenant-a-retention-archive",
                        TargetId = "control:release-review",
                        Predicate = "requires",
                        SourceSpans = new List<VyralGraphSourceSpan>
                        {
                            new() { SourceRef = "handbook:retention", CharStart = 0, CharEnd = 20 }
                        }
                    },
                    new()
                    {
                        Id = "edge:archive-audit",
                        SourceId = "tenant-a-retention-archive",
                        TargetId = "control:audit-log",
                        Predicate = "requires",
                        SourceSpans = new List<VyralGraphSourceSpan>
                        {
                            new() { SourceRef = "handbook:retention", CharStart = 21, CharEnd = 40 }
                        }
                    }
                }
            }
        });

        var retrieval = new LocalRetrievalService(store, provider);
        var rag = new LocalRagContextService(retrieval, store);

        var contextRequest = new RagContextRequest
        {
            Retrieval = new RetrievalRequest
            {
                Query = "archive retention release review audit",
                Collections = new List<string> { "knowledge" },
                PartitionKeys = new List<string> { "tenant-a" },
                SearchMode = SearchModes.Lexical,
                Lexical = new LexicalSearchOptions
                {
                    Fields = new List<string> { "/content/text", "/metadata/source", "/metadata/graphNodeId" },
                    Top = 4
                },
                Limit = 2
            },
            MaxChars = 260,
            MaxCharsPerChunk = 160,
            IncludeContextText = true,
            IncludeTrace = true,
            GraphExpansion = new RagContextGraphExpansionOptions
            {
                Collection = "graphs",
                GraphId = "retention-controls",
                MaxGraphProvenanceItems = 4,
                Profile = new VyralGraphTraversalProfile
                {
                    Id = "chunk-controls",
                    Direction = VyralGraphTraversalDirections.Outgoing,
                    MaxDepth = 1,
                    Predicates = new List<string> { "requires" },
                    EdgeLimit = 8,
                    Limit = 8
                }
            }
        };
        var context = await rag.BuildContextAsync(contextRequest);

        Assert.NotNull(context.GraphContext);
        Assert.Equal(RagContextGraphExpansionStatuses.Succeeded, context.GraphContext!.Status);
        Assert.Contains("tenant-a-retention-archive", context.GraphContext.SeedNodeIds);
        Assert.Equal(3, context.GraphContext.NodeCount);
        Assert.Equal(2, context.GraphContext.EdgeCount);
        Assert.Contains("control:release-review", context.GraphContext.ContextText);
        Assert.Equal(4, context.GraphContext.Provenance.Count);
        Assert.Equal(1, context.GraphContext.OmittedProvenanceCount);
        var edgeProvenance = Assert.Single(context.GraphContext.Provenance, item => item.EntityId == "edge:archive-audit");
        Assert.Equal(VyralGraphSubjectKinds.Edge, edgeProvenance.EntityKind);
        Assert.Equal("requires", edgeProvenance.Predicate);
        Assert.Equal("handbook:retention", Assert.Single(edgeProvenance.SourceSpans).SourceRef);
        Assert.True(context.GraphContext.ContextTextChars > 0);
        Assert.False(context.GraphContext.ContextTextTruncated);
        Assert.Contains("Graph context:", context.ContextText);
        Assert.Contains("tenant-a-retention-archive --requires--> control:audit-log", context.ContextText);
        Assert.Equal(RagContextGraphExpansionStatuses.Succeeded, context.Trace!["graphExpansion"]!["status"]!.GetValue<string>());
        Assert.Equal(2, context.Trace["graphExpansion"]!["edgeCount"]!.GetValue<int>());
        Assert.Equal(4, context.Trace["graphExpansion"]!["provenanceCount"]!.GetValue<int>());
        Assert.Equal(1, context.Trace["graphExpansion"]!["omittedProvenanceCount"]!.GetValue<int>());
        Assert.NotNull(context.GraphExpansion);
        Assert.Equal(RagContextGraphExpansionStatuses.Succeeded, context.GraphExpansion!.Status);
        Assert.Equal(3, context.GraphExpansion.NodesAdded);
        Assert.Equal(2, context.GraphExpansion.EdgesAdded);
        Assert.Equal(1, context.GraphExpansion.OmittedProvenanceCount);
        Assert.Contains("tenant-a-retention-archive", context.GraphExpansion.SeedNodeIds);
        Assert.Contains(context.GraphContext.SeedDiagnostics, diagnostic => diagnostic.Accepted && diagnostic.NormalizedValue == "tenant-a-retention-archive");
        Assert.Equal(0, context.GraphContext.DroppedSeedCount);
        Assert.Equal(2, context.Trace["graphContribution"]!["edgesAdded"]!.GetValue<int>());
        Assert.NotNull(context.Trace["graphExpansion"]!["seedDiagnostics"]);

        var evaluation = await rag.EvaluateContextAsync(new RagContextEvaluationRequest
        {
            Cases = new List<RagContextEvaluationCase>
            {
                new()
                {
                    Name = "retention-controls",
                    Request = contextRequest,
                    ExpectedGraph = new RagContextExpectedGraph
                    {
                        NodeIds = new List<string> { "tenant-a-retention-archive", "control:audit-log" },
                        EdgeIds = new List<string> { "edge:archive-audit" },
                        ProvenanceEntityIds = new List<string> { "edge:archive-audit" },
                        RequireSourceGroundedProvenance = true,
                        RequireGraphContextText = true,
                        RequireContextTextNotTruncated = true
                    }
                }
            }
        });

        Assert.Equal(1, evaluation.PassedCount);
        Assert.Equal(1.0, evaluation.PassRate);
        Assert.Equal(1.0, evaluation.ProvenanceHitRate);
        var evaluationCase = Assert.Single(evaluation.Cases);
        Assert.True(evaluationCase.Passed);
        Assert.True(evaluationCase.Graph.SourceGroundingSatisfied);
        Assert.Empty(evaluationCase.Graph.MissingNodeIds);
        Assert.Empty(evaluationCase.Graph.MissingEdgeIds);
        Assert.Empty(evaluationCase.Graph.FailureCategories);
        Assert.Empty(evaluation.FailureCategoryCounts);
        Assert.Equal("retention-controls", evaluationCase.QueryId);
        Assert.Equal("chunk-controls", evaluationCase.ProfileName);
        Assert.Contains("edge:archive-audit", evaluationCase.GraphExpandedEdgeIds);
        Assert.Equal(5, evaluationCase.GraphContributionCount);

        contextRequest.GraphExpansion!.MaxRecords = 1;
        var truncatedContext = await rag.BuildContextAsync(contextRequest);
        Assert.Equal(RagContextGraphExpansionStatuses.BudgetTruncated, truncatedContext.GraphContext?.Status);
        Assert.True(truncatedContext.GraphContext!.SourceTruncated);
        Assert.Equal(1, truncatedContext.GraphContext.ExportedRecordCount);
        Assert.Equal(2, truncatedContext.GraphContext.EstimatedRequiredRecordCount);
        Assert.Contains("maxRecords", truncatedContext.GraphContext.LimitsHit);
        Assert.Equal(1, truncatedContext.GraphExpansion!.ExportedRecordCount);
        Assert.Contains("maxRecords", truncatedContext.GraphExpansion.LimitsHit);
    }

    [Fact]
    public async Task RagPrompt_BuildsDeterministicPromptWithContextAndMessages()
    {
        var (store, _, provider) = await CreateRagStoreAsync();
        var retrieval = new LocalRetrievalService(store, provider);
        var context = new LocalRagContextService(retrieval);
        var prompts = new LocalRagPromptService(context);

        var prompt = await prompts.BuildPromptAsync(new RagPromptRequest
        {
            Context = new RagContextRequest
            {
                Retrieval = new RetrievalRequest
                {
                    Query = "retention archive deletion policy",
                    Collections = new List<string> { "knowledge" },
                    PartitionKeys = new List<string> { "tenant-a" },
                    Filter = new FilterNode { Path = "/metadata/topic", Op = "eq", Value = "retention" },
                    Embedding = new EmbeddingOptions { Field = EmbeddingField },
                    Limit = 2
                },
                MaxChars = 300,
                MaxCharsPerChunk = 140,
                IncludeTrace = true
            },
            Template = new RagPromptTemplateOptions
            {
                UserInstruction = "Explain the active retention rule.",
                FailOnEmptyContext = true
            }
        });

        Assert.Equal("retention archive deletion policy", prompt.Query);
        Assert.Equal("chat-markdown", prompt.Format);
        Assert.StartsWith("sha256:", prompt.PromptHash, StringComparison.Ordinal);
        Assert.Equal(new[] { "system", "user" }, prompt.Messages.Select(message => message.Role));
        Assert.Contains("Explain the active retention rule.", prompt.Messages[1].Content);
        Assert.Contains("Citation rule:", prompt.Messages[1].Content);
        Assert.Contains("Context:", prompt.Messages[1].Content);
        Assert.NotEmpty(prompt.Context.Chunks);
        Assert.NotEmpty(prompt.Context.Citations);
        Assert.NotNull(prompt.Context.ContextText);
        Assert.NotNull(prompt.Trace);
        Assert.Equal(prompt.PromptHash, prompt.Trace!["promptHash"]!.GetValue<string>());

        var tooSmall = await Assert.ThrowsAsync<InvalidOperationException>(() => prompts.BuildPromptAsync(new RagPromptRequest
        {
            Context = new RagContextRequest
            {
                Retrieval = new RetrievalRequest
                {
                    Query = "retention archive deletion policy",
                    Collections = new List<string> { "knowledge" },
                    PartitionKeys = new List<string> { "tenant-a" },
                    Embedding = new EmbeddingOptions { Field = EmbeddingField },
                    Limit = 1
                }
            },
            Template = new RagPromptTemplateOptions { MaxPromptChars = 10 }
        }));
        Assert.Contains("exceeds maxPromptChars", tooSmall.Message);
    }

    [Fact]
    public async Task RagContext_AppliesGroupBudgetsByJsonPointer()
    {
        var (store, _, provider) = await CreateRagStoreAsync();
        await store.UpsertRecordAsync("knowledge", await CreateChunkAsync(
            provider,
            id: "tenant-a-retention-handbook-addendum",
            partitionKey: "tenant-a",
            topic: "retention",
            status: "active",
            source: "handbook",
            text: "Retention policy archive deletion audit records handbook addendum repeats retention policy details."));

        var retrieval = new LocalRetrievalService(store, provider);
        var rag = new LocalRagContextService(retrieval);

        var context = await rag.BuildContextAsync(new RagContextRequest
        {
            Retrieval = new RetrievalRequest
            {
                Query = "retention policy archive deletion audit records",
                Collections = new List<string> { "knowledge" },
                PartitionKeys = new List<string> { "tenant-a" },
                Filter = new FilterNode
                {
                    Combine = "all",
                    Children = new List<FilterNode>
                    {
                        new() { Path = "/metadata/status", Op = "eq", Value = "active" },
                        new() { Path = "/metadata/topic", Op = "eq", Value = "retention" }
                    }
                },
                SearchMode = "lexical",
                Lexical = new LexicalSearchOptions
                {
                    Fields = new List<string> { "/content/text", "/metadata/source" },
                    Top = 10
                },
                Limit = 10
            },
            MaxChars = 1000,
            MaxCharsPerChunk = 250,
            ContextAssembly = new RagContextAssemblyOptions
            {
                GroupByPath = "/metadata/source",
                DefaultMaxChunksPerGroup = 1
            },
            IncludeTrace = true
        });

        Assert.NotEmpty(context.Chunks);
        Assert.Equal(context.Chunks.Count, context.Chunks.Select(chunk => chunk.GroupKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("handbook", context.Chunks.Select(chunk => chunk.GroupKey));
        Assert.All(context.Chunks, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk.GroupKey)));
        Assert.NotNull(context.Trace);
        Assert.Equal("/metadata/source", context.Trace!["groupByPath"]!.GetValue<string>());
        Assert.True(context.Trace!["skippedForGroupBudget"]!.GetValue<int>() >= 1);
        Assert.True(context.Trace!["groupCount"]!.GetValue<int>() >= context.Chunks.Count);
    }

    [Fact]
    public async Task RagContext_AppliesAuthorityAssemblyPriorityAndRequiredGroups()
    {
        var (store, _, provider) = await CreateRagStoreAsync();
        await store.UpsertRecordAsync("knowledge", await CreateChunkAsync(
            provider,
            id: "tenant-a-retention-handbook-addendum",
            partitionKey: "tenant-a",
            topic: "retention",
            status: "active",
            source: "handbook",
            text: "Retention policy archive deletion audit records handbook addendum repeats retention policy details."));

        var retrieval = new LocalRetrievalService(store, provider);
        var rag = new LocalRagContextService(retrieval);

        var context = await rag.BuildContextAsync(new RagContextRequest
        {
            Retrieval = new RetrievalRequest
            {
                Query = "retention policy archive deletion audit records",
                Collections = new List<string> { "knowledge" },
                PartitionKeys = new List<string> { "tenant-a" },
                Filter = new FilterNode
                {
                    Combine = "all",
                    Children = new List<FilterNode>
                    {
                        new() { Path = "/metadata/status", Op = "eq", Value = "active" },
                        new() { Path = "/metadata/topic", Op = "eq", Value = "retention" }
                    }
                },
                SearchMode = "lexical",
                Lexical = new LexicalSearchOptions
                {
                    Fields = new List<string> { "/content/text", "/metadata/source" },
                    Top = 10
                },
                Limit = 10
            },
            MaxChars = 1000,
            MaxCharsPerChunk = 250,
            IncludeTrace = true,
            ContextAssembly = new RagContextAssemblyOptions
            {
                GroupBy = "metadata",
                GroupByPath = "source",
                DefaultMaxChunksPerGroup = 1,
                Groups = new List<RagContextGroupBudget>
                {
                    new() { Key = "runbook", Priority = 0, Required = true, MinChunks = 1 },
                    new() { Key = "handbook", Priority = 1 },
                    new() { Key = "faq", Priority = 2 }
                }
            }
        });

        Assert.NotEmpty(context.Chunks);
        Assert.Equal("runbook", context.Chunks[0].GroupKey);
        Assert.Equal(context.Chunks.Count, context.Chunks.Select(chunk => chunk.GroupKey).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(context.Trace);
        Assert.Equal("metadata", context.Trace!["groupBy"]!.GetValue<string>());
        Assert.Equal("/metadata/source", context.Trace!["groupByPath"]!.GetValue<string>());
        Assert.Empty(context.Trace!["unsatisfiedRequiredGroups"]!.AsArray());

        var assemblyTrace = context.Trace!["contextAssembly"]!.AsObject();
        Assert.True(assemblyTrace["enabled"]!.GetValue<bool>());
        Assert.True(assemblyTrace["authorityOrdering"]!.GetValue<bool>());

        var groupStats = context.Trace!["groupStats"]!.AsObject();
        Assert.True(groupStats["runbook"]!.AsObject()["required"]!.GetValue<bool>());
        Assert.True(groupStats["runbook"]!.AsObject()["satisfied"]!.GetValue<bool>());
        Assert.Equal(1, groupStats["runbook"]!.AsObject()["minChunks"]!.GetValue<int>());
        Assert.Equal(1, groupStats["runbook"]!.AsObject()["chunkCount"]!.GetValue<int>());

        var missingRequired = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rag.BuildContextAsync(new RagContextRequest
            {
                Retrieval = new RetrievalRequest
                {
                    Query = "retention policy archive deletion audit records",
                    Collections = new List<string> { "knowledge" },
                    PartitionKeys = new List<string> { "tenant-a" },
                    SearchMode = "lexical",
                    Lexical = new LexicalSearchOptions { Fields = new List<string> { "/content/text", "/metadata/source" } },
                    Limit = 10
                },
                ContextAssembly = new RagContextAssemblyOptions
                {
                    GroupBy = "metadata",
                    GroupByPath = "source",
                    FailOnUnsatisfiedRequiredGroups = true,
                    Groups = new List<RagContextGroupBudget>
                    {
                        new() { Key = "source", Required = true, MinChunks = 1 }
                    }
                }
            }));

        Assert.Contains("required groups were not satisfied", missingRequired.Message);
    }

    [Fact]
    public async Task RetrievalSearch_BoundsLongSnippetsAndIgnoresVectorlessRecords()
    {
        var (store, _, provider) = await CreateRagStoreAsync();
        var longText = string.Join(" ", Enumerable.Repeat(
            "Retention policy archive guidance keeps retention holds, deletion windows, and restore checks aligned for operators.",
            8));

        await store.UpsertRecordAsync("knowledge", await CreateChunkAsync(
            provider,
            id: "tenant-a-retention-long",
            partitionKey: "tenant-a",
            topic: "retention",
            status: "active",
            source: "runbook",
            text: longText));

        await store.UpsertRecordAsync("knowledge", new VyralRecord
        {
            Id = "tenant-a-retention-vectorless",
            PartitionKey = "tenant-a",
            Type = "chunk",
            Metadata = new JsonObject
            {
                ["topic"] = "retention",
                ["status"] = "active",
                ["source"] = "draft"
            },
            Content = new JsonObject
            {
                ["text"] = "Retention policy archive guidance without an embedding should not appear in vector search."
            }
        });

        var retrieval = new LocalRetrievalService(store, provider);
        var result = await retrieval.SearchAsync(new RetrievalRequest
        {
            Query = "retention policy archive guidance",
            Collections = new List<string> { "knowledge" },
            PartitionKeys = new List<string> { "tenant-a" },
            Filter = new FilterNode { Path = "/metadata/topic", Op = "eq", Value = "retention" },
            SearchMode = "vector",
            Embedding = new EmbeddingOptions { Field = EmbeddingField },
            Limit = 8
        });

        var longMatch = Assert.Single(result.Results, match => match.Record.Id == "tenant-a-retention-long");

        Assert.Equal(200, longMatch.Snippet?.Length);
        Assert.EndsWith("...", longMatch.Snippet);
        Assert.DoesNotContain(result.Results, match => match.Record.Id == "tenant-a-retention-vectorless");
    }

    [Fact]
    public async Task RetrievalSearch_RejectsInvalidConfigurationInsteadOfReturningSilentZeroResults()
    {
        var (store, _, provider) = await CreateRagStoreAsync();
        var retrieval = new LocalRetrievalService(store, provider);

        var emptyQuery = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retrieval.SearchAsync(new RetrievalRequest
            {
                Query = " ",
                Collections = new List<string> { "knowledge" },
                Limit = 1
            }));
        var emptyCollections = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retrieval.SearchAsync(new RetrievalRequest
            {
                Query = "retention",
                Collections = new List<string>(),
                Limit = 1
            }));
        var zeroLimit = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retrieval.SearchAsync(new RetrievalRequest
            {
                Query = "retention",
                Collections = new List<string> { "knowledge" },
                Limit = 0
            }));
        var missingCollection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retrieval.SearchAsync(new RetrievalRequest
            {
                Query = "retention",
                Collections = new List<string> { "missing" },
                Limit = 1
            }));
        var missingField = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retrieval.SearchAsync(new RetrievalRequest
            {
                Query = "retention",
                Collections = new List<string> { "knowledge" },
                SearchMode = "vector",
                Embedding = new EmbeddingOptions { Field = "titleEmbedding" },
                Limit = 1
            }));

        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "notes" });
        var noVectorPolicy = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retrieval.SearchAsync(new RetrievalRequest
            {
                Query = "retention",
                Collections = new List<string> { "notes" },
                SearchMode = "vector",
                Limit = 1
            }));

        Assert.Contains("query is required", emptyQuery.Message);
        Assert.Contains("At least one retrieval collection", emptyCollections.Message);
        Assert.Contains("limit must be greater than zero", zeroLimit.Message);
        Assert.Contains("does not exist", missingCollection.Message);
        Assert.Contains("not defined in policy", missingField.Message);
        Assert.Contains("does not define a vector policy", noVectorPolicy.Message);
    }

    private static async Task<(SqliteRecordCollectionStore Store, SqliteTraceStore Traces, KeywordEmbeddingProvider Provider)> CreateRagStoreAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-rag-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();

        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();

        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "knowledge",
            IndexedMetadata = new List<string>
            {
                "/metadata/status",
                "/metadata/topic",
                "/metadata/source",
                "/metadata/graphNodeId",
                "/type"
            },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = EmbeddingField,
                    Path = $"/vectors/{EmbeddingField}/values",
                    Dimensions = KeywordEmbeddingProvider.VectorDimensions,
                    DistanceFunction = "cosine",
                    IndexType = "flat"
                }
            }
        });

        var provider = new KeywordEmbeddingProvider();
        foreach (var chunk in RagCorpus)
        {
            await store.UpsertRecordAsync("knowledge", await CreateChunkAsync(
                provider,
                chunk.Id,
                chunk.PartitionKey,
                chunk.Topic,
                chunk.Status,
                chunk.Source,
                chunk.Text));
        }

        return (store, traces, provider);
    }

    private static async Task<VyralRecord> CreateChunkAsync(
        KeywordEmbeddingProvider provider,
        string id,
        string partitionKey,
        string topic,
        string status,
        string source,
        string text)
    {
        var vector = await provider.GenerateEmbeddingAsync(text);

        return new VyralRecord
        {
            Id = id,
            PartitionKey = partitionKey,
            Type = "chunk",
            Metadata = new JsonObject
            {
                ["topic"] = topic,
                ["status"] = status,
                ["source"] = source,
                ["graphNodeId"] = id
            },
            Content = new JsonObject { ["text"] = text },
            Sources = new List<VyralSourceReference>
            {
                new()
                {
                    Id = source + ":" + id,
                    Kind = "document",
                    Uri = $"file:///{source}/{id}.md",
                    Label = source,
                    Span = new VyralSourceSpan
                    {
                        CharStart = 0,
                        CharEnd = text.Length
                    }
                }
            },
            Vectors = new Dictionary<string, VyralVector>
            {
                [EmbeddingField] = new()
                {
                    Values = vector,
                    Dimensions = vector.Length,
                    Model = provider.ModelId,
                    DistanceFunction = "cosine",
                    SourceField = "content.text",
                    GeneratedAt = DateTime.UtcNow
                }
            }
        };
    }

    private static readonly IReadOnlyList<RagChunk> RagCorpus = new List<RagChunk>
    {
        new(
            "tenant-a-retention-archive",
            "tenant-a",
            "retention",
            "active",
            "handbook",
            "Active retention policy guidance for archived data requires immutable storage, deletion review windows, and audit records."),
        new(
            "tenant-a-retention-holds",
            "tenant-a",
            "retention",
            "active",
            "runbook",
            "Retention holds block deletion while release review is active and keep archive restore checks visible to support teams."),
        new(
            "tenant-a-retention-export",
            "tenant-a",
            "retention",
            "active",
            "faq",
            "Retention export jobs keep data archive manifests, audit logs, and deletion approvals together for compliance evidence."),
        new(
            "tenant-a-retention-restore",
            "tenant-a",
            "retention",
            "active",
            "playbook",
            "Archive restore drills validate retention policy data recovery before a deletion schedule is approved."),
        new(
            "tenant-a-retention-retired",
            "tenant-a",
            "retention",
            "retired",
            "handbook",
            "Retired retention notes describe a previous deletion workflow that should no longer be used."),
        new(
            "tenant-b-retention",
            "tenant-b",
            "retention",
            "active",
            "handbook",
            "Tenant B retention policy keeps archive and deletion controls isolated from tenant A workloads."),
        new(
            "tenant-a-security-mfa",
            "tenant-a",
            "security",
            "active",
            "handbook",
            "Security guidance requires MFA, role review, and access approval before privileged support operations."),
        new(
            "tenant-a-security-keys",
            "tenant-a",
            "security",
            "active",
            "runbook",
            "Key rotation and access token review protect support systems from expired credentials."),
        new(
            "tenant-a-billing-invoices",
            "tenant-a",
            "billing",
            "active",
            "faq",
            "Billing invoice disputes require payment references, account owner approval, and credit memo review."),
        new(
            "tenant-a-billing-usage",
            "tenant-a",
            "billing",
            "active",
            "guide",
            "Usage reconciliation compares billing meter totals with monthly payment adjustments."),
        new(
            "tenant-a-onboarding-setup",
            "tenant-a",
            "onboarding",
            "active",
            "guide",
            "Onboarding setup provisions welcome tasks, project templates, and owner notification messages."),
        new(
            "tenant-a-support-escalation",
            "tenant-a",
            "support",
            "active",
            "runbook",
            "Support escalation requires a ticket owner, severity level, response target, and customer update cadence.")
    };

    private sealed record RagChunk(string Id, string PartitionKey, string Topic, string Status, string Source, string Text);

    private static string? MetadataString(VyralRecord record, string key)
    {
        return MetadataString(record.Metadata, key);
    }

    private static string? MetadataString(JsonObject? metadata, string key)
    {
        var node = metadata?[key];
        if (node is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) ? s : null;
    }

    private static int MetadataInt(JsonObject? metadata, string key)
    {
        var node = metadata?[key];
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        return 0;
    }

    private sealed class KeywordEmbeddingProvider : IEmbeddingProvider
    {
        public const int VectorDimensions = 8;

        private static readonly IReadOnlyDictionary<int, string[]> KeywordsByDimension = new Dictionary<int, string[]>
        {
            [0] = new[] { "retention", "archive", "archived", "deletion", "deleted", "hold", "holds", "immutable", "compliance", "audit", "restore" },
            [1] = new[] { "security", "mfa", "role", "access", "token", "key", "credentials", "privileged" },
            [2] = new[] { "billing", "invoice", "payment", "credit", "usage", "meter", "account" },
            [3] = new[] { "onboarding", "setup", "welcome", "template", "provisions", "project" },
            [4] = new[] { "support", "ticket", "escalation", "severity", "response", "customer" },
            [5] = new[] { "release", "review", "approval", "approved", "owner" },
            [6] = new[] { "manifest", "source", "document", "evidence", "logs" },
            [7] = new[] { "local", "development", "query", "search", "vector" }
        };

        public int Dimensions => VectorDimensions;
        public string ProviderId => "test-keyword";
        public string ModelId => "test-keyword-embedding-v1";

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

    private sealed class FixedEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 2;
        public string ProviderId => "test-fixed";
        public string ModelId => "test-fixed-embedding-v1";

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        {
            return Task.FromResult(new float[] { 1, 0 });
        }
    }

    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        public const int VectorDimensions = 4;

        public int CallCount { get; private set; }
        public List<string> Inputs { get; } = new();
        public int Dimensions => VectorDimensions;
        public string ProviderId => "test-counting";
        public string ModelId => "test-counting-embedding-v1";

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        {
            CallCount++;
            Inputs.Add(text);
            return Task.FromResult(new[]
            {
                (float)CallCount,
                text.Length,
                text.Count(c => char.IsWhiteSpace(c)),
                text.Count(c => char.IsLetter(c))
            });
        }
    }
}
