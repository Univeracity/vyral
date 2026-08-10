using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public class LocalRagIngestionService : IRagIngestionService
{
    private const int MaxTextLength = 2_000_000;
    private const int MaxChunkChars = 50_000;
    private const int MaxChunkCount = 2_000;
    private const int MaxBatchItemCount = 100;
    private const string ManifestType = "rag.manifest";
    private const string ManifestVersion = "v1";
    private const string IngestionPlanVersion = "rag.ingest.plan.v1";
    private const string VectorReuseScopeRequest = "request";
    private const string VectorReuseScopePartition = "partition";
    private const string VectorReuseScopeCollection = "collection";
    private const string ChunkDedupeScopeRequest = "request";
    private const string ChunkDedupeScopePartition = "partition";
    private const string ChunkDedupeScopeCollection = "collection";
    private readonly IRecordCollectionStore _recordStore;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ITraceStore? _traceStore;
    private readonly EmbeddingProviderOptions? _embeddingOptions;

    public LocalRagIngestionService(
        IRecordCollectionStore recordStore,
        IEmbeddingProvider embeddingProvider,
        ITraceStore? traceStore = null,
        EmbeddingProviderOptions? embeddingOptions = null)
    {
        _recordStore = recordStore;
        _embeddingProvider = embeddingProvider;
        _traceStore = traceStore;
        _embeddingOptions = embeddingOptions;
    }

    public async Task<RagIngestTextBatchResult> IngestTextBatchAsync(string collection, RagIngestTextBatchRequest request, CancellationToken ct = default)
    {
        ValidateBatchRequest(request);

        var policy = await _recordStore.GetCollectionPolicyAsync(collection, ct);
        if (policy is null)
        {
            throw new InvalidOperationException($"Collection '{collection}' does not exist.");
        }

        var result = new RagIngestTextBatchResult
        {
            Collection = collection,
            Requested = request.Items.Count
        };

        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            try
            {
                var itemResult = await IngestTextAsync(collection, item, ct);
                result.Items.Add(new RagIngestTextBatchItemResult
                {
                    Index = i,
                    DocumentId = itemResult.DocumentId,
                    PartitionKey = itemResult.PartitionKey,
                    Status = RagIngestItemStatuses.Succeeded,
                    Result = itemResult
                });
                result.Succeeded++;
                AddBatchCounters(result, itemResult);
            }
            catch (Exception ex) when (IsBatchItemFailure(ex))
            {
                result.Items.Add(new RagIngestTextBatchItemResult
                {
                    Index = i,
                    DocumentId = string.IsNullOrWhiteSpace(item.DocumentId) ? null : item.DocumentId,
                    PartitionKey = string.IsNullOrWhiteSpace(item.PartitionKey) ? null : item.PartitionKey,
                    Status = RagIngestItemStatuses.Failed,
                    Error = ex.Message
                });
                result.Failed++;

                if (!request.ContinueOnError)
                {
                    result.StoppedOnError = i + 1 < request.Items.Count;
                    break;
                }
            }
        }

        result.Attempted = result.Items.Count;
        return result;
    }

    public async Task<RagIngestTextResult> IngestTextAsync(string collection, RagIngestTextRequest request, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        ValidateRequest(request);

        var policy = await _recordStore.GetCollectionPolicyAsync(collection, ct);
        if (policy is null)
        {
            throw new InvalidOperationException($"Collection '{collection}' does not exist.");
        }

        var embeddingField = ResolveEmbeddingField(collection, policy, request.Embedding?.Field);
        var fieldPolicy = policy.VectorPolicies.Single(vector => vector.Name == embeddingField);
        if (fieldPolicy.Dimensions != _embeddingProvider.Dimensions)
        {
            throw new InvalidOperationException($"Embedding provider returns {_embeddingProvider.Dimensions} dimensions, but collection '{collection}' field '{embeddingField}' expects {fieldPolicy.Dimensions}.");
        }

        var opts = request.Options ?? new RagIngestionOptions();
        var textHash = Sha256Hex(request.Text);
        var documentTextHash = $"sha256:{textHash}";
        var documentId = string.IsNullOrWhiteSpace(request.DocumentId)
            ? $"doc-{textHash[..16]}"
            : request.DocumentId.Trim();
        var idPrefix = NormalizeIdSegment(string.IsNullOrWhiteSpace(request.IdPrefix) ? documentId : request.IdPrefix);
        var recordType = string.IsNullOrWhiteSpace(request.Type) ? "rag.chunk" : request.Type;
        var metadataHash = Sha256CanonicalJson(request.Metadata);
        var sourceHash = Sha256CanonicalJson(BuildSourceHashInput(request));
        var chunks = SplitText(request.Text, opts.ChunkChars, opts.ChunkOverlapChars);
        var vectorReuseScope = NormalizeVectorReuseScope(opts.VectorReuseScope);
        var chunkDedupeScope = NormalizeChunkDedupeScope(opts.ChunkDedupeScope);
        var embeddingPurpose = string.IsNullOrWhiteSpace(request.Embedding?.Purpose)
            ? EmbeddingPurposes.Passage
            : EmbeddingTextPreparer.NormalizePurpose(request.Embedding.Purpose);

        if (chunks.Count > MaxChunkCount)
        {
            throw new InvalidOperationException($"RAG text ingestion produced {chunks.Count} chunks, but the limit is {MaxChunkCount}. Increase chunkChars or ingest the source in smaller parts.");
        }

        var result = new RagIngestTextResult
        {
            Collection = collection,
            DocumentId = documentId,
            PartitionKey = request.PartitionKey,
            EmbeddingField = embeddingField,
            EmbeddingProvider = _embeddingProvider.ProviderId,
            EmbeddingModel = _embeddingProvider.ModelId,
            EmbeddingPurpose = embeddingPurpose,
            Dimensions = _embeddingProvider.Dimensions,
            TextLength = request.Text.Length,
            TextHash = documentTextHash,
            ChunkCount = chunks.Count,
            DryRun = opts.DryRun
        };

        var generatedAt = DateTime.UtcNow;
        var currentRecordIds = new HashSet<string>(StringComparer.Ordinal);
        var reusableVectors = new Dictionary<string, ReusableChunkVector>(StringComparer.Ordinal);
        var duplicateChunks = new Dictionary<string, DeduplicatedChunkReference>(StringComparer.Ordinal);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var chunkHash = Sha256Hex(chunk.Text);
            var chunkTextHash = $"sha256:{chunkHash}";
            var preparedChunk = PrepareChunkEmbeddingText(request, chunk.Text, embeddingPurpose);
            var embeddingTextHash = $"sha256:{Sha256Hex(preparedChunk.PreparedText)}";
            var id = BuildChunkRecordId(idPrefix, i, chunkHash);
            var existing = await _recordStore.GetRecordAsync(collection, request.PartitionKey, id, ct);
            if (opts.SkipUnchangedChunks &&
                IsExistingChunkCurrent(existing, recordType, request.SchemaVersion, request.ContentField, embeddingField, chunk, i, chunks.Count, documentTextHash, chunkTextHash, embeddingPurpose, embeddingTextHash, metadataHash, sourceHash))
            {
                currentRecordIds.Add(id);
                CacheReusableChunkVector(reusableVectors, embeddingField, embeddingTextHash, existing, fieldPolicy);
                CacheDeduplicatedChunkReference(duplicateChunks, embeddingField, chunkTextHash, embeddingTextHash, existing);
                result.ReusedCount++;
                result.Chunks.Add(new RagIngestChunkResult
                {
                    Index = i,
                    Id = id,
                    PartitionKey = request.PartitionKey,
                    CharStart = chunk.CharStart,
                    CharEnd = chunk.CharEnd,
                    TextLength = chunk.Text.Length,
                    TextHash = chunkTextHash,
                    EmbeddingTextHash = embeddingTextHash,
                    Action = RagIngestChunkActions.Reused,
                    EmbeddingAction = RagEmbeddingActions.Unchanged,
                    Etag = existing!.Etag,
                    Revision = existing.Revision
                });
                continue;
            }

            var duplicateChunk = opts.DeduplicateExistingChunks
                ? await TryResolveDeduplicatedChunkAsync(collection, request, recordType, chunkDedupeScope, embeddingField, chunkTextHash, embeddingPurpose, embeddingTextHash, id, fieldPolicy, duplicateChunks, ct)
                : null;
            if (duplicateChunk is not null)
            {
                if (string.Equals(duplicateChunk.PartitionKey, request.PartitionKey, StringComparison.Ordinal))
                {
                    currentRecordIds.Add(duplicateChunk.RecordId);
                }

                result.DeduplicatedCount++;
                result.Chunks.Add(new RagIngestChunkResult
                {
                    Index = i,
                    Id = duplicateChunk.RecordId,
                    PartitionKey = duplicateChunk.PartitionKey,
                    CharStart = chunk.CharStart,
                    CharEnd = chunk.CharEnd,
                    TextLength = chunk.Text.Length,
                    TextHash = chunkTextHash,
                    EmbeddingTextHash = embeddingTextHash,
                    Action = RagIngestChunkActions.Deduplicated,
                    EmbeddingAction = RagEmbeddingActions.Deduplicated,
                    DeduplicatedFromId = duplicateChunk.RecordId,
                    DeduplicatedFromPartitionKey = duplicateChunk.PartitionKey,
                    Etag = duplicateChunk.Etag,
                    Revision = duplicateChunk.Revision
                });
                continue;
            }

            var reusableVector = opts.ReuseExistingChunkVectors
                ? await TryResolveReusableChunkVectorAsync(collection, request, vectorReuseScope, embeddingField, embeddingPurpose, embeddingTextHash, fieldPolicy, reusableVectors, ct)
                : null;
            var embeddingAction = reusableVector is null ? RagEmbeddingActions.Generated : RagEmbeddingActions.Reused;
            if (reusableVector is null) result.VectorGeneratedCount++;
            else result.VectorReusedCount++;

            var action = existing is null ? RagIngestChunkActions.Created : RagIngestChunkActions.Updated;
            if (existing is null) result.CreatedCount++;
            else result.UpdatedCount++;

            if (opts.DryRun)
            {
                currentRecordIds.Add(id);
                if (opts.ReuseExistingChunkVectors && reusableVector is null)
                {
                    reusableVectors.TryAdd(
                        BuildReusableVectorCacheKey(embeddingField, embeddingTextHash),
                        new ReusableChunkVector(id, request.PartitionKey, Array.Empty<float>(), generatedAt));
                }

                if (opts.DeduplicateExistingChunks)
                {
                    CacheDeduplicatedChunkReference(duplicateChunks, embeddingField, chunkTextHash, embeddingTextHash, id, request.PartitionKey);
                }

                result.Chunks.Add(new RagIngestChunkResult
                {
                    Index = i,
                    Id = id,
                    PartitionKey = request.PartitionKey,
                    CharStart = chunk.CharStart,
                    CharEnd = chunk.CharEnd,
                    TextLength = chunk.Text.Length,
                    TextHash = chunkTextHash,
                    EmbeddingTextHash = embeddingTextHash,
                    Action = action,
                    EmbeddingAction = embeddingAction,
                    ReusedVectorFromId = reusableVector?.RecordId,
                    ReusedVectorFromPartitionKey = reusableVector?.PartitionKey
                });
                continue;
            }

            var vector = reusableVector is null
                ? await _embeddingProvider.GenerateEmbeddingAsync(preparedChunk.PreparedText, ct)
                : reusableVector.Values.ToArray();

            var record = CreateChunkRecord(
                request,
                documentId,
                documentTextHash,
                recordType,
                metadataHash,
                sourceHash,
                idPrefix,
                embeddingField,
                fieldPolicy,
                chunk,
                chunkHash,
                chunkTextHash,
                embeddingPurpose,
                embeddingTextHash,
                preparedChunk,
                i,
                chunks.Count,
                vector,
                generatedAt,
                reusableVector);

            await _recordStore.UpsertRecordAsync(collection, record, ct);
            currentRecordIds.Add(record.Id);
            CacheReusableChunkVector(reusableVectors, embeddingField, embeddingTextHash, record, fieldPolicy);
            CacheDeduplicatedChunkReference(duplicateChunks, embeddingField, chunkTextHash, embeddingTextHash, record);

            result.Chunks.Add(new RagIngestChunkResult
            {
                Index = i,
                Id = record.Id,
                PartitionKey = record.PartitionKey,
                CharStart = chunk.CharStart,
                CharEnd = chunk.CharEnd,
                TextLength = chunk.Text.Length,
                TextHash = chunkTextHash,
                EmbeddingTextHash = embeddingTextHash,
                Action = action,
                EmbeddingAction = embeddingAction,
                ReusedVectorFromId = reusableVector?.RecordId,
                ReusedVectorFromPartitionKey = reusableVector?.PartitionKey,
                Etag = record.Etag,
                Revision = record.Revision
            });
        }

        if (opts.ReplaceDocumentChunks)
        {
            result.StaleDeletes = await ReconcileStaleDocumentChunksAsync(collection, request.PartitionKey, documentId, recordType, currentRecordIds, opts.DryRun, ct);
            result.DeletedStaleCount = result.StaleDeletes.Count;
        }

        if (opts.PersistManifest)
        {
            await UpsertManifestAsync(
                collection,
                request,
                result,
                documentId,
                documentTextHash,
                recordType,
                metadataHash,
                sourceHash,
                idPrefix,
                embeddingField,
                fieldPolicy,
                chunks,
                ct);
        }

        result.PlanHash = Sha256CanonicalJson(BuildPlanHashInput(
            collection,
            request,
            result,
            documentId,
            documentTextHash,
            recordType,
            metadataHash,
            sourceHash,
            idPrefix,
            embeddingField,
            fieldPolicy,
            vectorReuseScope,
            chunkDedupeScope));
        result.ActionSummary = BuildActionSummary(result);
        result.PlanHashComparison = BuildHashComparison("plan", opts.ExpectedPlanHash, result.PlanHash);
        result.ManifestHashComparison = BuildHashComparison("manifest", opts.ExpectedManifestHash, result.ManifestHash);

        if (opts.IncludeTrace)
        {
            var duration = DateTime.UtcNow - startedAt;
            var trace = new TraceRecord
            {
                Operation = "rag.ingest_text",
                Adapter = _recordStore.GetType().Name,
                StartedAt = startedAt,
                DurationMs = duration.TotalMilliseconds,
                Request = new Dictionary<string, object?>
                {
                    ["collection"] = collection,
                    ["documentId"] = documentId,
                    ["partitionKey"] = request.PartitionKey,
                    ["contentField"] = request.ContentField,
                    ["embeddingField"] = embeddingField,
                    ["chunkChars"] = opts.ChunkChars,
                    ["chunkOverlapChars"] = opts.ChunkOverlapChars,
                    ["dryRun"] = opts.DryRun,
                    ["replaceDocumentChunks"] = opts.ReplaceDocumentChunks,
                    ["skipUnchangedChunks"] = opts.SkipUnchangedChunks,
                    ["reuseExistingChunkVectors"] = opts.ReuseExistingChunkVectors,
                    ["vectorReuseScope"] = vectorReuseScope,
                    ["deduplicateExistingChunks"] = opts.DeduplicateExistingChunks,
                    ["chunkDedupeScope"] = chunkDedupeScope,
                    ["embeddingPurpose"] = embeddingPurpose,
                    ["persistManifest"] = opts.PersistManifest,
                    ["expectedPlanHash"] = string.IsNullOrWhiteSpace(opts.ExpectedPlanHash) ? null : opts.ExpectedPlanHash.Trim(),
                    ["expectedManifestHash"] = string.IsNullOrWhiteSpace(opts.ExpectedManifestHash) ? null : opts.ExpectedManifestHash.Trim(),
                    ["textLength"] = request.Text.Length,
                    ["textHash"] = result.TextHash,
                    ["planHash"] = result.PlanHash
                },
                ResultSummary = new Dictionary<string, object?>
                {
                    ["embeddingProvider"] = _embeddingProvider.ProviderId,
                    ["embeddingModel"] = _embeddingProvider.ModelId,
                    ["embeddingDimensions"] = _embeddingProvider.Dimensions,
                    ["embeddingPurpose"] = embeddingPurpose,
                    ["chunkCount"] = result.ChunkCount,
                    ["dryRun"] = result.DryRun,
                    ["deletedStaleCount"] = result.DeletedStaleCount,
                    ["createdCount"] = result.CreatedCount,
                    ["updatedCount"] = result.UpdatedCount,
                    ["reusedCount"] = result.ReusedCount,
                    ["vectorGeneratedCount"] = result.VectorGeneratedCount,
                    ["vectorReusedCount"] = result.VectorReusedCount,
                    ["deduplicatedCount"] = result.DeduplicatedCount,
                    ["planHash"] = result.PlanHash,
                    ["planHashComparisonStatus"] = result.PlanHashComparison.Status,
                    ["manifestId"] = result.ManifestId,
                    ["manifestHash"] = result.ManifestHash,
                    ["manifestAction"] = result.ManifestAction,
                    ["manifestHashComparisonStatus"] = result.ManifestHashComparison.Status,
                    ["actionSummary"] = result.ActionSummary,
                    ["staleDeleteIds"] = result.StaleDeletes.Select(stale => stale.Id).ToList(),
                    ["recordIds"] = result.Chunks.Select(chunk => chunk.Id).ToList()
                }
            };

            if (_traceStore is not null && !opts.DryRun)
            {
                await _traceStore.WriteTraceAsync(trace, ct);
            }

            result.Trace = JsonSerializer.SerializeToNode(new Dictionary<string, object?>
            {
                ["id"] = trace.Id,
                ["durationMs"] = duration.TotalMilliseconds,
                ["textHash"] = result.TextHash,
                ["planHash"] = result.PlanHash,
                ["planHashComparisonStatus"] = result.PlanHashComparison.Status,
                ["chunkCount"] = result.ChunkCount,
                ["dryRun"] = result.DryRun,
                ["tracePersisted"] = _traceStore is not null && !opts.DryRun,
                ["deletedStaleCount"] = result.DeletedStaleCount,
                ["createdCount"] = result.CreatedCount,
                ["updatedCount"] = result.UpdatedCount,
                ["reusedCount"] = result.ReusedCount,
                ["vectorGeneratedCount"] = result.VectorGeneratedCount,
                ["vectorReusedCount"] = result.VectorReusedCount,
                ["deduplicatedCount"] = result.DeduplicatedCount,
                ["manifestId"] = result.ManifestId ?? string.Empty,
                ["manifestHash"] = result.ManifestHash ?? string.Empty,
                ["manifestAction"] = result.ManifestAction ?? string.Empty,
                ["manifestHashComparisonStatus"] = result.ManifestHashComparison.Status,
                ["actionSummary"] = result.ActionSummary,
                ["staleDeleteIds"] = result.StaleDeletes.Select(stale => stale.Id).ToList(),
                ["embeddingProvider"] = _embeddingProvider.ProviderId,
                ["embeddingModel"] = _embeddingProvider.ModelId,
                ["embeddingDimensions"] = _embeddingProvider.Dimensions,
                ["embeddingPurpose"] = embeddingPurpose
            }) as JsonObject;
        }

        return result;
    }

    private async Task UpsertManifestAsync(
        string collection,
        RagIngestTextRequest request,
        RagIngestTextResult result,
        string documentId,
        string documentTextHash,
        string recordType,
        string metadataHash,
        string sourceHash,
        string idPrefix,
        string embeddingField,
        VectorFieldPolicy fieldPolicy,
        IReadOnlyList<TextChunk> chunks,
        CancellationToken ct)
    {
        var opts = request.Options ?? new RagIngestionOptions();
        var manifestId = string.IsNullOrWhiteSpace(opts.ManifestId)
            ? BuildManifestRecordId(idPrefix)
            : NormalizeIdSegment(opts.ManifestId);
        RecordIdentityValidator.ValidateRecordId(manifestId);

        var manifestInput = BuildManifestHashInput(
            collection,
            request,
            result,
            documentId,
            documentTextHash,
            recordType,
            metadataHash,
            sourceHash,
            embeddingField,
            fieldPolicy);
        var manifestHash = Sha256CanonicalJson(manifestInput);
        var existing = await _recordStore.GetRecordAsync(collection, request.PartitionKey, manifestId, ct);
        if (opts.SkipUnchangedChunks && IsExistingManifestCurrent(existing, manifestHash, documentId, recordType))
        {
            result.ManifestId = manifestId;
            result.ManifestHash = manifestHash;
            result.ManifestAction = RagIngestChunkActions.Reused;
            result.ManifestEtag = existing!.Etag;
            result.ManifestRevision = existing.Revision;
            return;
        }

        if (opts.DryRun)
        {
            result.ManifestId = manifestId;
            result.ManifestHash = manifestHash;
            result.ManifestAction = existing is null ? RagIngestChunkActions.Created : RagIngestChunkActions.Updated;
            return;
        }

        var manifestRecord = CreateManifestRecord(
            request,
            result,
            manifestId,
            manifestHash,
            manifestInput,
            documentId,
            documentTextHash,
            recordType,
            metadataHash,
            sourceHash,
            embeddingField,
            chunks);
        await _recordStore.UpsertRecordAsync(collection, manifestRecord, ct);

        result.ManifestId = manifestRecord.Id;
        result.ManifestHash = manifestHash;
        result.ManifestAction = existing is null ? RagIngestChunkActions.Created : RagIngestChunkActions.Updated;
        result.ManifestEtag = manifestRecord.Etag;
        result.ManifestRevision = manifestRecord.Revision;
    }

    private VyralRecord CreateManifestRecord(
        RagIngestTextRequest request,
        RagIngestTextResult result,
        string manifestId,
        string manifestHash,
        object manifestInput,
        string documentId,
        string documentTextHash,
        string recordType,
        string metadataHash,
        string sourceHash,
        string embeddingField,
        IReadOnlyList<TextChunk> chunks)
    {
        return new VyralRecord
        {
            Id = manifestId,
            PartitionKey = request.PartitionKey,
            Type = ManifestType,
            SchemaVersion = ManifestVersion,
            Metadata = new JsonObject
            {
                ["documentId"] = documentId,
                ["documentTextHash"] = documentTextHash,
                ["manifestHash"] = manifestHash,
                ["manifestVersion"] = ManifestVersion,
                ["chunkRecordType"] = recordType,
                ["chunkCount"] = result.ChunkCount,
                ["textLength"] = result.TextLength,
                ["ingestionMetadataHash"] = metadataHash,
                ["ingestionSourceHash"] = sourceHash,
                ["embeddingField"] = embeddingField,
                ["embeddingProvider"] = _embeddingProvider.ProviderId,
                ["embeddingModel"] = _embeddingProvider.ModelId,
                ["embeddingPurpose"] = result.EmbeddingPurpose,
                ["embeddingDimensions"] = _embeddingProvider.Dimensions
            },
            Content = new JsonObject
            {
                ["manifest"] = JsonSerializer.SerializeToNode(new Dictionary<string, object?>
                {
                    ["version"] = ManifestVersion,
                    ["manifestHash"] = manifestHash,
                    ["rawTextIncluded"] = false,
                    ["sourceTextHash"] = documentTextHash,
                    ["ingestionPlan"] = manifestInput,
                    ["chunkSpans"] = chunks.Select((chunk, index) => new Dictionary<string, object>
                    {
                        ["index"] = index,
                        ["charStart"] = chunk.CharStart,
                        ["charEnd"] = chunk.CharEnd,
                        ["textLength"] = chunk.Text.Length,
                        ["embeddingTextHash"] = result.Chunks.FirstOrDefault(resultChunk => resultChunk.Index == index)?.EmbeddingTextHash ?? string.Empty
                    }).ToList()
                })
            },
            Sources = CreateManifestSources(request, documentId)
        };
    }

    private bool IsExistingManifestCurrent(VyralRecord? existing, string manifestHash, string documentId, string recordType)
    {
        return existing is not null &&
            string.Equals(existing.Type, ManifestType, StringComparison.Ordinal) &&
            string.Equals(existing.SchemaVersion, ManifestVersion, StringComparison.Ordinal) &&
            existing.Metadata is not null &&
            MetadataStringEquals(existing.Metadata, "documentId", documentId) &&
            MetadataStringEquals(existing.Metadata, "manifestHash", manifestHash) &&
            MetadataStringEquals(existing.Metadata, "chunkRecordType", recordType) &&
            MetadataStringEquals(existing.Metadata, "embeddingProvider", _embeddingProvider.ProviderId) &&
            MetadataStringEquals(existing.Metadata, "embeddingModel", _embeddingProvider.ModelId) &&
            MetadataIntEquals(existing.Metadata, "embeddingDimensions", _embeddingProvider.Dimensions);
    }

    private static void AddBatchCounters(RagIngestTextBatchResult batch, RagIngestTextResult item)
    {
        batch.TextLength += item.TextLength;
        batch.ChunkCount += item.ChunkCount;
        batch.DeletedStaleCount += item.DeletedStaleCount;
        batch.CreatedCount += item.CreatedCount;
        batch.UpdatedCount += item.UpdatedCount;
        batch.ReusedCount += item.ReusedCount;
        batch.VectorGeneratedCount += item.VectorGeneratedCount;
        batch.VectorReusedCount += item.VectorReusedCount;
        batch.DeduplicatedCount += item.DeduplicatedCount;
    }

    private static RagIngestActionSummary BuildActionSummary(RagIngestTextResult result)
    {
        var actionCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RagIngestChunkActions.Created] = 0,
            [RagIngestChunkActions.Updated] = 0,
            [RagIngestChunkActions.Reused] = 0,
            [RagIngestChunkActions.Deduplicated] = 0
        };
        var embeddingActionCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RagEmbeddingActions.Generated] = 0,
            [RagEmbeddingActions.Reused] = 0,
            [RagEmbeddingActions.Unchanged] = 0,
            [RagEmbeddingActions.Deduplicated] = 0
        };
        var createdIds = new List<string>();
        var updatedIds = new List<string>();
        var reusedIds = new List<string>();
        var deduplicatedIds = new List<string>();

        foreach (var chunk in result.Chunks.OrderBy(chunk => chunk.Index))
        {
            Increment(actionCounts, chunk.Action);
            Increment(embeddingActionCounts, chunk.EmbeddingAction);

            switch (chunk.Action)
            {
                case RagIngestChunkActions.Created:
                    createdIds.Add(chunk.Id);
                    break;
                case RagIngestChunkActions.Updated:
                    updatedIds.Add(chunk.Id);
                    break;
                case RagIngestChunkActions.Reused:
                    reusedIds.Add(chunk.Id);
                    break;
                case RagIngestChunkActions.Deduplicated:
                    deduplicatedIds.Add(chunk.Id);
                    break;
            }
        }

        return new RagIngestActionSummary
        {
            ActionCounts = actionCounts,
            EmbeddingActionCounts = embeddingActionCounts,
            CreatedIds = createdIds,
            UpdatedIds = updatedIds,
            ReusedIds = reusedIds,
            DeduplicatedIds = deduplicatedIds,
            StaleDeleteIds = result.StaleDeletes
                .OrderBy(stale => stale.PartitionKey, StringComparer.Ordinal)
                .ThenBy(stale => stale.Id, StringComparer.Ordinal)
                .Select(stale => stale.Id)
                .ToList()
        };
    }

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!counts.TryAdd(key, 1))
        {
            counts[key]++;
        }
    }

    private static RagIngestHashComparison BuildHashComparison(string kind, string? expectedHash, string? actualHash)
    {
        var normalizedExpected = string.IsNullOrWhiteSpace(expectedHash) ? null : expectedHash.Trim();
        if (normalizedExpected is null)
        {
            return new RagIngestHashComparison
            {
                Kind = kind,
                ActualHash = actualHash,
                Status = RagIngestHashStatuses.NotProvided
            };
        }

        var matches = !string.IsNullOrWhiteSpace(actualHash) &&
            string.Equals(normalizedExpected, actualHash, StringComparison.Ordinal);
        return new RagIngestHashComparison
        {
            Kind = kind,
            ExpectedHash = normalizedExpected,
            ActualHash = actualHash,
            Compared = true,
            Matches = matches,
            Status = string.IsNullOrWhiteSpace(actualHash)
                ? RagIngestHashStatuses.ActualMissing
                : matches ? RagIngestHashStatuses.Matched : RagIngestHashStatuses.Drifted
        };
    }

    private static void ValidateBatchRequest(RagIngestTextBatchRequest request)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            throw new InvalidOperationException("RAG text batch ingestion request must include at least one item.");
        }

        if (request.Items.Count > MaxBatchItemCount)
        {
            throw new InvalidOperationException($"RAG text batch ingestion request supports at most {MaxBatchItemCount} items.");
        }
    }

    private static bool IsBatchItemFailure(Exception exception)
    {
        return exception is ArgumentException or InvalidOperationException or NotSupportedException;
    }

    private static void ValidateRequest(RagIngestTextRequest request)
    {
        RecordIdentityValidator.ValidatePartitionKey(request.PartitionKey);

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new InvalidOperationException("RAG text ingestion requires non-empty text.");
        }

        if (request.Text.Length > MaxTextLength)
        {
            throw new InvalidOperationException($"RAG text ingestion supports at most {MaxTextLength} characters per request.");
        }

        if (string.IsNullOrWhiteSpace(request.ContentField))
        {
            throw new InvalidOperationException("RAG text ingestion contentField is required.");
        }

        if (request.ContentField.Contains('/') || request.ContentField.Contains('\\') || request.ContentField.Contains('.'))
        {
            throw new InvalidOperationException("RAG text ingestion contentField must be a simple content property name.");
        }

        var opts = request.Options ?? new RagIngestionOptions();

        if (opts.ChunkChars <= 0 || opts.ChunkChars > MaxChunkChars)
        {
            throw new InvalidOperationException($"RAG text ingestion chunkChars must be between 1 and {MaxChunkChars}.");
        }

        if (opts.ChunkOverlapChars < 0)
        {
            throw new InvalidOperationException("RAG text ingestion chunkOverlapChars cannot be negative.");
        }

        if (opts.ChunkOverlapChars >= opts.ChunkChars)
        {
            throw new InvalidOperationException("RAG text ingestion chunkOverlapChars must be smaller than chunkChars.");
        }

        if (!string.IsNullOrWhiteSpace(request.Embedding?.Purpose))
        {
            _ = EmbeddingTextPreparer.NormalizePurpose(request.Embedding.Purpose);
        }

        var stride = opts.ChunkChars - opts.ChunkOverlapChars;
        var estimatedChunkCount = (request.Text.Length + stride - 1) / stride;
        if (estimatedChunkCount > MaxChunkCount)
        {
            throw new InvalidOperationException($"RAG text ingestion would produce approximately {estimatedChunkCount} chunks, but the limit is {MaxChunkCount}. Increase chunkChars or ingest the source in smaller parts.");
        }
    }

    private static string NormalizeVectorReuseScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return VectorReuseScopePartition;
        }

        return scope.Trim().ToLowerInvariant() switch
        {
            VectorReuseScopeRequest => VectorReuseScopeRequest,
            VectorReuseScopePartition => VectorReuseScopePartition,
            VectorReuseScopeCollection => VectorReuseScopeCollection,
            _ => throw new InvalidOperationException("RAG text ingestion vectorReuseScope must be 'request', 'partition', or 'collection'.")
        };
    }

    private static string NormalizeChunkDedupeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return ChunkDedupeScopePartition;
        }

        return scope.Trim().ToLowerInvariant() switch
        {
            ChunkDedupeScopeRequest => ChunkDedupeScopeRequest,
            ChunkDedupeScopePartition => ChunkDedupeScopePartition,
            ChunkDedupeScopeCollection => ChunkDedupeScopeCollection,
            _ => throw new InvalidOperationException("RAG text ingestion chunkDedupeScope must be 'request', 'partition', or 'collection'.")
        };
    }

    private static string ResolveEmbeddingField(string collection, RecordCollectionPolicy policy, string? requestedField)
    {
        if (!string.IsNullOrWhiteSpace(requestedField))
        {
            if (policy.VectorPolicies.Any(vector => vector.Name == requestedField))
            {
                return requestedField;
            }

            throw new InvalidOperationException($"Vector field '{requestedField}' is not defined in policy for collection '{collection}'.");
        }

        if (policy.VectorPolicies.Count == 0)
        {
            throw new InvalidOperationException($"Collection '{collection}' does not define a vector policy for RAG ingestion.");
        }

        return policy.VectorPolicies[0].Name;
    }

    private PreparedEmbeddingText PrepareChunkEmbeddingText(RagIngestTextRequest request, string text, string embeddingPurpose)
    {
        return EmbeddingTextPreparer.Prepare(
            text,
            embeddingPurpose,
            request.Embedding?.QueryPrefix ?? _embeddingOptions?.QueryPrefix,
            request.Embedding?.PassagePrefix ?? _embeddingOptions?.PassagePrefix,
            request.Embedding?.SymmetricPrefix ?? _embeddingOptions?.SymmetricPrefix);
    }

    private VyralRecord CreateChunkRecord(
        RagIngestTextRequest request,
        string documentId,
        string documentTextHash,
        string recordType,
        string metadataHash,
        string sourceHash,
        string idPrefix,
        string embeddingField,
        VectorFieldPolicy fieldPolicy,
        TextChunk chunk,
        string chunkHash,
        string chunkTextHash,
        string embeddingPurpose,
        string embeddingTextHash,
        PreparedEmbeddingText preparedChunk,
        int index,
        int chunkCount,
        float[] vector,
        DateTime generatedAt,
        ReusableChunkVector? reusedVector)
    {
        var id = BuildChunkRecordId(idPrefix, index, chunkHash);
        RecordIdentityValidator.ValidateRecordId(id);
        var vectorGeneratedAt = reusedVector?.GeneratedAt ?? generatedAt;

        var metadata = new JsonObject();
        if (request.Metadata is not null)
        {
            foreach (var (key, value) in request.Metadata)
                metadata[key] = value?.DeepClone();
        }
        metadata["documentId"] = documentId;
        metadata["documentTextHash"] = documentTextHash;
        metadata["ingestionMetadataHash"] = metadataHash;
        metadata["ingestionSourceHash"] = sourceHash;
        metadata["chunkIndex"] = index;
        metadata["chunkCount"] = chunkCount;
        metadata["charStart"] = chunk.CharStart;
        metadata["charEnd"] = chunk.CharEnd;
        metadata["textHash"] = chunkTextHash;
        metadata["embeddingPurpose"] = embeddingPurpose;
        metadata["embeddingTextHash"] = embeddingTextHash;
        metadata["embeddingPrefixApplied"] = preparedChunk.PrefixApplied;
        metadata["embeddingPrefixLength"] = preparedChunk.PrefixLength;
        metadata["embeddingProvider"] = _embeddingProvider.ProviderId;
        metadata["embeddingModel"] = _embeddingProvider.ModelId;
        if (reusedVector is not null)
        {
            metadata["vectorReusedFromId"] = reusedVector.RecordId;
            metadata["vectorReusedFromPartitionKey"] = reusedVector.PartitionKey;
        }

        var embedding = new Dictionary<string, object>
        {
            ["provider"] = _embeddingProvider.ProviderId,
            ["model"] = _embeddingProvider.ModelId,
            ["dimensions"] = _embeddingProvider.Dimensions,
            ["purpose"] = embeddingPurpose,
            ["inputHash"] = embeddingTextHash,
            ["prefixApplied"] = preparedChunk.PrefixApplied,
            ["prefixLength"] = preparedChunk.PrefixLength,
            ["sourceField"] = $"content.{request.ContentField}",
            ["generatedAt"] = vectorGeneratedAt,
            ["action"] = reusedVector is null ? RagEmbeddingActions.Generated : RagEmbeddingActions.Reused
        };
        if (reusedVector is not null)
        {
            embedding["reusedFromId"] = reusedVector.RecordId;
            embedding["reusedFromPartitionKey"] = reusedVector.PartitionKey;
        }

        return new VyralRecord
        {
            Id = id,
            PartitionKey = request.PartitionKey,
            Type = recordType,
            SchemaVersion = request.SchemaVersion,
            Metadata = metadata,
            Content = new JsonObject
            {
                [request.ContentField] = chunk.Text
            },
            Sources = CreateChunkSources(request, documentId, chunk, index),
            Vectors = new Dictionary<string, VyralVector>
            {
                [embeddingField] = new()
                {
                    Values = vector.ToArray(),
                    Dimensions = vector.Length,
                    Datatype = fieldPolicy.Datatype,
                    DistanceFunction = fieldPolicy.DistanceFunction,
                    Model = _embeddingProvider.ModelId,
                    GeneratedAt = vectorGeneratedAt,
                    SourceField = $"content.{request.ContentField}"
                }
            },
        };
    }

    private bool IsExistingChunkCurrent(
        VyralRecord? existing,
        string recordType,
        string? schemaVersion,
        string contentField,
        string embeddingField,
        TextChunk chunk,
        int index,
        int chunkCount,
        string documentTextHash,
        string chunkTextHash,
        string embeddingPurpose,
        string embeddingTextHash,
        string metadataHash,
        string sourceHash)
    {
        if (existing is null)
        {
            return false;
        }

        if (!string.Equals(existing.Type, recordType, StringComparison.Ordinal) ||
            !string.Equals(existing.SchemaVersion, schemaVersion, StringComparison.Ordinal))
        {
            return false;
        }

        if (existing.Content is null ||
            !string.Equals(existing.Content[contentField]?.GetValue<string>(), chunk.Text, StringComparison.Ordinal))
        {
            return false;
        }

        if (existing.Metadata is null ||
            !MetadataStringEquals(existing.Metadata, "documentTextHash", documentTextHash) ||
            !MetadataStringEquals(existing.Metadata, "textHash", chunkTextHash) ||
            !MetadataStringEquals(existing.Metadata, "ingestionMetadataHash", metadataHash) ||
            !MetadataStringEquals(existing.Metadata, "ingestionSourceHash", sourceHash) ||
            !MetadataIntEquals(existing.Metadata, "chunkIndex", index) ||
            !MetadataIntEquals(existing.Metadata, "chunkCount", chunkCount) ||
            !MetadataIntEquals(existing.Metadata, "charStart", chunk.CharStart) ||
            !MetadataIntEquals(existing.Metadata, "charEnd", chunk.CharEnd) ||
            !MetadataStringEquals(existing.Metadata, "embeddingPurpose", embeddingPurpose) ||
            !MetadataStringEquals(existing.Metadata, "embeddingTextHash", embeddingTextHash) ||
            !MetadataStringEquals(existing.Metadata, "embeddingProvider", _embeddingProvider.ProviderId) ||
            !MetadataStringEquals(existing.Metadata, "embeddingModel", _embeddingProvider.ModelId))
        {
            return false;
        }

        return existing.Vectors is not null &&
            existing.Vectors.TryGetValue(embeddingField, out var vector) &&
            vector.Values.Length == _embeddingProvider.Dimensions &&
            string.Equals(vector.Model, _embeddingProvider.ModelId, StringComparison.Ordinal) &&
            string.Equals(vector.SourceField, $"content.{contentField}", StringComparison.Ordinal);
    }

    private static bool MetadataStringEquals(JsonObject metadata, string key, string expected)
    {
        return metadata[key] is JsonValue v &&
               v.TryGetValue<string>(out var actual) &&
               string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static bool MetadataIntEquals(JsonObject metadata, string key, int expected)
    {
        return metadata[key] is JsonValue v &&
               v.TryGetValue<int>(out var actual) &&
               actual == expected;
    }

    private async Task<ReusableChunkVector?> TryResolveReusableChunkVectorAsync(
        string collection,
        RagIngestTextRequest request,
        string vectorReuseScope,
        string embeddingField,
        string embeddingPurpose,
        string embeddingTextHash,
        VectorFieldPolicy fieldPolicy,
        Dictionary<string, ReusableChunkVector> reusableVectors,
        CancellationToken ct)
    {
        var cacheKey = BuildReusableVectorCacheKey(embeddingField, embeddingTextHash);
        if (reusableVectors.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        if (vectorReuseScope == VectorReuseScopeRequest)
        {
            return null;
        }

        var query = new QueryEnvelope
        {
            Limit = 25,
            Filter = new FilterNode
            {
                Combine = "all",
                Children = new List<FilterNode>
                {
                    new() { Path = "/metadata/embeddingPurpose", Op = FilterOps.Eq, Value = embeddingPurpose },
                    new() { Path = "/metadata/embeddingTextHash", Op = FilterOps.Eq, Value = embeddingTextHash },
                    new() { Path = "/metadata/embeddingProvider", Op = FilterOps.Eq, Value = _embeddingProvider.ProviderId },
                    new() { Path = "/metadata/embeddingModel", Op = FilterOps.Eq, Value = _embeddingProvider.ModelId }
                }
            }
        };
        if (vectorReuseScope == VectorReuseScopePartition)
        {
            query.PartitionKeys = new List<string> { request.PartitionKey };
        }

        var candidates = await _recordStore.QueryAllRecordsAsync(collection, query, ct);
        foreach (var candidate in candidates)
        {
            var reusableVector = TryCreateReusableChunkVector(candidate, embeddingField, embeddingPurpose, embeddingTextHash, fieldPolicy);
            if (reusableVector is null)
            {
                continue;
            }

            reusableVectors.TryAdd(cacheKey, reusableVector);
            return reusableVector;
        }

        return null;
    }

    private void CacheReusableChunkVector(
        Dictionary<string, ReusableChunkVector> reusableVectors,
        string embeddingField,
        string embeddingTextHash,
        VyralRecord? record,
        VectorFieldPolicy fieldPolicy)
    {
        var reusableVector = TryCreateReusableChunkVector(record, embeddingField, null, embeddingTextHash, fieldPolicy);
        if (reusableVector is null)
        {
            return;
        }

        reusableVectors.TryAdd(BuildReusableVectorCacheKey(embeddingField, embeddingTextHash), reusableVector);
    }

    private ReusableChunkVector? TryCreateReusableChunkVector(
        VyralRecord? record,
        string embeddingField,
        string? embeddingPurpose,
        string embeddingTextHash,
        VectorFieldPolicy fieldPolicy)
    {
        if (record?.Metadata is null ||
            record.Vectors is null ||
            !record.Vectors.TryGetValue(embeddingField, out var vector))
        {
            return null;
        }

        if (!MetadataStringEquals(record.Metadata, "embeddingTextHash", embeddingTextHash) ||
            (embeddingPurpose is not null && !MetadataStringEquals(record.Metadata, "embeddingPurpose", embeddingPurpose)) ||
            !MetadataStringEquals(record.Metadata, "embeddingProvider", _embeddingProvider.ProviderId) ||
            !MetadataStringEquals(record.Metadata, "embeddingModel", _embeddingProvider.ModelId))
        {
            return null;
        }

        if (vector.Values.Length != _embeddingProvider.Dimensions ||
            vector.Dimensions != _embeddingProvider.Dimensions ||
            !string.Equals(vector.Model, _embeddingProvider.ModelId, StringComparison.Ordinal) ||
            !string.Equals(vector.Datatype, fieldPolicy.Datatype, StringComparison.Ordinal) ||
            !string.Equals(vector.DistanceFunction, fieldPolicy.DistanceFunction, StringComparison.Ordinal))
        {
            return null;
        }

        return new ReusableChunkVector(
            record.Id,
            record.PartitionKey,
            vector.Values.ToArray(),
            vector.GeneratedAt);
    }

    private static string BuildReusableVectorCacheKey(string embeddingField, string embeddingTextHash)
    {
        return $"{embeddingField}\n{embeddingTextHash}";
    }

    private static string BuildDeduplicatedChunkCacheKey(string embeddingField, string chunkTextHash, string embeddingTextHash)
    {
        return $"{embeddingField}\n{chunkTextHash}\n{embeddingTextHash}";
    }

    private async Task<DeduplicatedChunkReference?> TryResolveDeduplicatedChunkAsync(
        string collection,
        RagIngestTextRequest request,
        string recordType,
        string chunkDedupeScope,
        string embeddingField,
        string chunkTextHash,
        string embeddingPurpose,
        string embeddingTextHash,
        string intendedId,
        VectorFieldPolicy fieldPolicy,
        Dictionary<string, DeduplicatedChunkReference> duplicateChunks,
        CancellationToken ct)
    {
        var cacheKey = BuildDeduplicatedChunkCacheKey(embeddingField, chunkTextHash, embeddingTextHash);
        if (duplicateChunks.TryGetValue(cacheKey, out var cached) &&
            !IsIntendedChunkReference(cached, request.PartitionKey, intendedId))
        {
            return cached;
        }

        if (chunkDedupeScope == ChunkDedupeScopeRequest)
        {
            return null;
        }

        var query = new QueryEnvelope
        {
            Limit = 50,
            Filter = new FilterNode
            {
                Combine = "all",
                Children = new List<FilterNode>
                {
                    new() { Path = "/type", Op = FilterOps.Eq, Value = recordType },
                    new() { Path = "/metadata/textHash", Op = FilterOps.Eq, Value = chunkTextHash },
                    new() { Path = "/metadata/embeddingPurpose", Op = FilterOps.Eq, Value = embeddingPurpose },
                    new() { Path = "/metadata/embeddingTextHash", Op = FilterOps.Eq, Value = embeddingTextHash },
                    new() { Path = "/metadata/embeddingProvider", Op = FilterOps.Eq, Value = _embeddingProvider.ProviderId },
                    new() { Path = "/metadata/embeddingModel", Op = FilterOps.Eq, Value = _embeddingProvider.ModelId }
                }
            }
        };
        if (chunkDedupeScope == ChunkDedupeScopePartition)
        {
            query.PartitionKeys = new List<string> { request.PartitionKey };
        }

        var candidates = await _recordStore.QueryAllRecordsAsync(collection, query, ct);
        foreach (var candidate in candidates
                     .OrderBy(candidate => candidate.PartitionKey, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            var duplicate = TryCreateDeduplicatedChunkReference(candidate, request, recordType, intendedId, embeddingField, chunkTextHash, embeddingPurpose, embeddingTextHash, fieldPolicy);
            if (duplicate is null)
            {
                continue;
            }

            duplicateChunks.TryAdd(cacheKey, duplicate);
            return duplicate;
        }

        return null;
    }

    private static bool IsIntendedChunkReference(DeduplicatedChunkReference reference, string partitionKey, string intendedId)
    {
        return string.Equals(reference.PartitionKey, partitionKey, StringComparison.Ordinal) &&
            string.Equals(reference.RecordId, intendedId, StringComparison.Ordinal);
    }

    private DeduplicatedChunkReference? TryCreateDeduplicatedChunkReference(
        VyralRecord? record,
        RagIngestTextRequest request,
        string recordType,
        string intendedId,
        string embeddingField,
        string chunkTextHash,
        string embeddingPurpose,
        string embeddingTextHash,
        VectorFieldPolicy fieldPolicy)
    {
        if (record is null ||
            IsIntendedChunkReference(new DeduplicatedChunkReference(record.Id, record.PartitionKey, record.Etag, record.Revision), request.PartitionKey, intendedId) ||
            !string.Equals(record.Type, recordType, StringComparison.Ordinal) ||
            record.Content is null ||
            record.Metadata is null ||
            !record.Content.ContainsKey(request.ContentField))
        {
            return null;
        }

        if (!MetadataStringEquals(record.Metadata, "textHash", chunkTextHash))
        {
            return null;
        }

        var reusableVector = TryCreateReusableChunkVector(record, embeddingField, embeddingPurpose, embeddingTextHash, fieldPolicy);
        return reusableVector is null
            ? null
            : new DeduplicatedChunkReference(record.Id, record.PartitionKey, record.Etag, record.Revision);
    }

    private void CacheDeduplicatedChunkReference(
        Dictionary<string, DeduplicatedChunkReference> duplicateChunks,
        string embeddingField,
        string chunkTextHash,
        string embeddingTextHash,
        VyralRecord? record)
    {
        if (record is null)
        {
            return;
        }

        CacheDeduplicatedChunkReference(duplicateChunks, embeddingField, chunkTextHash, embeddingTextHash, record.Id, record.PartitionKey, record.Etag, record.Revision);
    }

    private static void CacheDeduplicatedChunkReference(
        Dictionary<string, DeduplicatedChunkReference> duplicateChunks,
        string embeddingField,
        string chunkTextHash,
        string embeddingTextHash,
        string recordId,
        string partitionKey,
        string? etag = null,
        int? revision = null)
    {
        duplicateChunks.TryAdd(
            BuildDeduplicatedChunkCacheKey(embeddingField, chunkTextHash, embeddingTextHash),
            new DeduplicatedChunkReference(recordId, partitionKey, etag, revision));
    }

    private async Task<List<RagIngestStaleDeleteResult>> ReconcileStaleDocumentChunksAsync(
        string collection,
        string partitionKey,
        string documentId,
        string recordType,
        IReadOnlySet<string> currentRecordIds,
        bool dryRun,
        CancellationToken ct)
    {
        var existing = await _recordStore.QueryAllRecordsAsync(collection, new QueryEnvelope
        {
            PartitionKeys = new List<string> { partitionKey },
            Filter = new FilterNode
            {
                Combine = "all",
                Children = new List<FilterNode>
                {
                    new() { Path = "/metadata/documentId", Op = FilterOps.Eq, Value = documentId },
                    new() { Path = "/type", Op = FilterOps.Eq, Value = recordType }
                }
            }
        }, ct);

        var staleDeletes = new List<RagIngestStaleDeleteResult>();
        foreach (var record in existing.OrderBy(record => record.PartitionKey, StringComparer.Ordinal).ThenBy(record => record.Id, StringComparer.Ordinal))
        {
            if (currentRecordIds.Contains(record.Id))
            {
                continue;
            }

            staleDeletes.Add(CreateStaleDeleteResult(record));
            if (!dryRun)
            {
                await _recordStore.DeleteRecordAsync(collection, record.PartitionKey, record.Id, ct);
            }
        }

        return staleDeletes;
    }

    private static RagIngestStaleDeleteResult CreateStaleDeleteResult(VyralRecord record)
    {
        int? chunkIndex = null;
        if (record.Metadata?["chunkIndex"] is JsonValue chunkIndexNode &&
            chunkIndexNode.TryGetValue<int>(out var parsedChunkIndex))
        {
            chunkIndex = parsedChunkIndex;
        }

        string? textHash = null;
        if (record.Metadata?["textHash"] is JsonValue textHashNode &&
            textHashNode.TryGetValue<string>(out var textHashValue))
        {
            textHash = textHashValue;
        }

        return new RagIngestStaleDeleteResult
        {
            Id = record.Id,
            PartitionKey = record.PartitionKey,
            ChunkIndex = chunkIndex,
            TextHash = textHash,
            Etag = record.Etag,
            Revision = record.Revision
        };
    }

    private static List<VyralSourceReference>? CreateChunkSources(RagIngestTextRequest request, string documentId, TextChunk chunk, int index)
    {
        var sources = request.Sources is { Count: > 0 }
            ? request.Sources
            : CreateSourceFromConvenienceFields(request, documentId);
        if (sources is null || sources.Count == 0)
        {
            return null;
        }

        return sources.Select(source =>
        {
            var extensions = source.Span?.Extensions is null
                ? new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
                : new Dictionary<string, System.Text.Json.JsonElement>(source.Span.Extensions, StringComparer.Ordinal);
            extensions["chunkIndex"] = System.Text.Json.JsonSerializer.SerializeToElement(index);

            return new VyralSourceReference
            {
                Id = string.IsNullOrWhiteSpace(source.Id) ? documentId : source.Id,
                Kind = string.IsNullOrWhiteSpace(source.Kind) ? "document" : source.Kind,
                Uri = string.IsNullOrWhiteSpace(source.Uri) ? documentId : source.Uri,
                Label = source.Label,
                Span = new VyralSourceSpan
                {
                    CharStart = chunk.CharStart,
                    CharEnd = chunk.CharEnd,
                    Line = source.Span?.Line,
                    Column = source.Span?.Column,
                    Anchor = source.Span?.Anchor,
                    Extensions = extensions
                }
            };
        }).ToList();
    }

    private static List<VyralSourceReference>? CreateSourceFromConvenienceFields(RagIngestTextRequest request, string documentId)
    {
        if (string.IsNullOrWhiteSpace(request.SourceUri) &&
            string.IsNullOrWhiteSpace(request.SourceId) &&
            string.IsNullOrWhiteSpace(request.SourceLabel))
        {
            return null;
        }

        return new List<VyralSourceReference>
        {
            new()
            {
                Id = string.IsNullOrWhiteSpace(request.SourceId) ? documentId : request.SourceId.Trim(),
                Kind = string.IsNullOrWhiteSpace(request.SourceKind) ? "document" : request.SourceKind.Trim(),
                Uri = string.IsNullOrWhiteSpace(request.SourceUri) ? documentId : request.SourceUri.Trim(),
                Label = string.IsNullOrWhiteSpace(request.SourceLabel) ? null : request.SourceLabel.Trim()
            }
        };
    }

    private static List<VyralSourceReference>? CreateManifestSources(RagIngestTextRequest request, string documentId)
    {
        var sources = request.Sources is { Count: > 0 }
            ? request.Sources
            : CreateSourceFromConvenienceFields(request, documentId);
        if (sources is null || sources.Count == 0)
        {
            return null;
        }

        return sources.Select(source => new VyralSourceReference
        {
            Id = string.IsNullOrWhiteSpace(source.Id) ? documentId : source.Id,
            Kind = string.IsNullOrWhiteSpace(source.Kind) ? "document" : source.Kind,
            Uri = string.IsNullOrWhiteSpace(source.Uri) ? documentId : source.Uri,
            Label = source.Label,
            Span = source.Span
        }).ToList();
    }

    private static List<TextChunk> SplitText(string text, int chunkChars, int overlapChars)
    {
        var chunks = new List<TextChunk>();
        var start = 0;
        while (start < text.Length)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            if (start >= text.Length)
            {
                break;
            }

            var hardEnd = Math.Min(text.Length, start + chunkChars);
            var end = hardEnd == text.Length ? hardEnd : FindChunkBoundary(text, start, hardEnd, chunkChars);
            while (end > start && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }

            if (end <= start)
            {
                end = hardEnd;
            }

            chunks.Add(new TextChunk(start, end, text[start..end]));

            if (end >= text.Length)
            {
                break;
            }

            var nextStart = Math.Max(start + 1, end - overlapChars);
            if (nextStart <= start)
            {
                nextStart = end;
            }

            start = nextStart;
        }

        return chunks;
    }

    private static int FindChunkBoundary(string text, int start, int hardEnd, int chunkChars)
    {
        var minEnd = start + Math.Max(1, chunkChars / 2);
        var searchCount = hardEnd - minEnd;
        if (searchCount <= 0)
        {
            return hardEnd;
        }

        var doubleNewline = text.LastIndexOf("\n\n", hardEnd - 1, searchCount, StringComparison.Ordinal);
        if (doubleNewline >= minEnd)
        {
            return doubleNewline;
        }

        var newline = text.LastIndexOf('\n', hardEnd - 1, searchCount);
        if (newline >= minEnd)
        {
            return newline;
        }

        for (var i = hardEnd - 1; i >= minEnd; i--)
        {
            if ((text[i] == '.' || text[i] == '!' || text[i] == '?') && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
            {
                return i + 1;
            }
        }

        for (var i = hardEnd - 1; i >= minEnd; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return hardEnd;
    }

    private static string NormalizeIdSegment(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value.Trim())
        {
            builder.Append(c is >= 'A' and <= 'Z' ||
                           c is >= 'a' and <= 'z' ||
                           c is >= '0' and <= '9' ||
                           c is '-' or '_' or '.'
                ? c
                : '-');
        }

        var normalized = builder.ToString().Trim('-', '.', '_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "doc";
        }

        normalized = normalized.Length <= 128 ? normalized : normalized[..128].Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(normalized) ? "doc" : normalized;
    }

    private static string BuildChunkRecordId(string idPrefix, int index, string chunkHash)
    {
        return $"{idPrefix}-chunk-{index:D4}-{chunkHash[..12]}";
    }

    private static string BuildManifestRecordId(string idPrefix)
    {
        return $"{idPrefix}-manifest";
    }

    private static string Sha256Hex(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string Sha256CanonicalJson(object? value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value ?? new Dictionary<string, object>()));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(document.RootElement, writer);
        }

        return "sha256:" + Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static object BuildSourceHashInput(RagIngestTextRequest request)
    {
        return new Dictionary<string, object?>
        {
            ["sourceUri"] = string.IsNullOrWhiteSpace(request.SourceUri) ? null : request.SourceUri.Trim(),
            ["sourceKind"] = string.IsNullOrWhiteSpace(request.SourceKind) ? null : request.SourceKind.Trim(),
            ["sourceId"] = string.IsNullOrWhiteSpace(request.SourceId) ? null : request.SourceId.Trim(),
            ["sourceLabel"] = string.IsNullOrWhiteSpace(request.SourceLabel) ? null : request.SourceLabel.Trim(),
            ["sources"] = request.Sources
        };
    }

    private static object BuildManifestHashInput(
        string collection,
        RagIngestTextRequest request,
        RagIngestTextResult result,
        string documentId,
        string documentTextHash,
        string recordType,
        string metadataHash,
        string sourceHash,
        string embeddingField,
        VectorFieldPolicy fieldPolicy)
    {
        var opts = request.Options ?? new RagIngestionOptions();
        return new Dictionary<string, object?>
        {
            ["version"] = ManifestVersion,
            ["collection"] = collection,
            ["documentId"] = documentId,
            ["partitionKey"] = request.PartitionKey,
            ["chunkRecordType"] = recordType,
            ["schemaVersion"] = request.SchemaVersion,
            ["contentField"] = request.ContentField,
            ["embeddingField"] = embeddingField,
            ["embeddingProvider"] = result.EmbeddingProvider,
            ["embeddingModel"] = result.EmbeddingModel,
            ["embeddingPurpose"] = result.EmbeddingPurpose,
            ["embeddingDimensions"] = result.Dimensions,
            ["vectorPath"] = fieldPolicy.Path,
            ["vectorDatatype"] = fieldPolicy.Datatype,
            ["vectorDistanceFunction"] = fieldPolicy.DistanceFunction,
            ["vectorIndexType"] = fieldPolicy.IndexType,
            ["textLength"] = result.TextLength,
            ["documentTextHash"] = documentTextHash,
            ["chunkChars"] = opts.ChunkChars,
            ["chunkOverlapChars"] = opts.ChunkOverlapChars,
            ["deduplicateExistingChunks"] = opts.DeduplicateExistingChunks,
            ["chunkDedupeScope"] = opts.DeduplicateExistingChunks ? NormalizeChunkDedupeScope(opts.ChunkDedupeScope) : null,
            ["metadataHash"] = metadataHash,
            ["sourceHash"] = sourceHash,
            ["chunks"] = result.Chunks
                .OrderBy(chunk => chunk.Index)
                .Select(chunk => new Dictionary<string, object?>
                {
                    ["index"] = chunk.Index,
                    ["id"] = chunk.Id,
                    ["partitionKey"] = chunk.PartitionKey,
                    ["charStart"] = chunk.CharStart,
                    ["charEnd"] = chunk.CharEnd,
                    ["textLength"] = chunk.TextLength,
                    ["textHash"] = chunk.TextHash,
                    ["embeddingTextHash"] = chunk.EmbeddingTextHash
                })
                .ToList()
        };
    }

    private static object BuildPlanHashInput(
        string collection,
        RagIngestTextRequest request,
        RagIngestTextResult result,
        string documentId,
        string documentTextHash,
        string recordType,
        string metadataHash,
        string sourceHash,
        string idPrefix,
        string embeddingField,
        VectorFieldPolicy fieldPolicy,
        string vectorReuseScope,
        string chunkDedupeScope)
    {
        var opts = request.Options ?? new RagIngestionOptions();
        return new Dictionary<string, object?>
        {
            ["version"] = IngestionPlanVersion,
            ["collection"] = collection,
            ["documentId"] = documentId,
            ["partitionKey"] = request.PartitionKey,
            ["idPrefix"] = idPrefix,
            ["chunkRecordType"] = recordType,
            ["schemaVersion"] = request.SchemaVersion,
            ["contentField"] = request.ContentField,
            ["embeddingField"] = embeddingField,
            ["embeddingProvider"] = result.EmbeddingProvider,
            ["embeddingModel"] = result.EmbeddingModel,
            ["embeddingPurpose"] = result.EmbeddingPurpose,
            ["embeddingDimensions"] = result.Dimensions,
            ["vectorPath"] = fieldPolicy.Path,
            ["vectorDatatype"] = fieldPolicy.Datatype,
            ["vectorDistanceFunction"] = fieldPolicy.DistanceFunction,
            ["vectorIndexType"] = fieldPolicy.IndexType,
            ["textLength"] = result.TextLength,
            ["documentTextHash"] = documentTextHash,
            ["chunkChars"] = opts.ChunkChars,
            ["chunkOverlapChars"] = opts.ChunkOverlapChars,
            ["metadataHash"] = metadataHash,
            ["sourceHash"] = sourceHash,
            ["replaceDocumentChunks"] = opts.ReplaceDocumentChunks,
            ["skipUnchangedChunks"] = opts.SkipUnchangedChunks,
            ["reuseExistingChunkVectors"] = opts.ReuseExistingChunkVectors,
            ["vectorReuseScope"] = vectorReuseScope,
            ["deduplicateExistingChunks"] = opts.DeduplicateExistingChunks,
            ["chunkDedupeScope"] = opts.DeduplicateExistingChunks ? chunkDedupeScope : null,
            ["persistManifest"] = opts.PersistManifest,
            ["manifestId"] = result.ManifestId,
            ["manifestHash"] = result.ManifestHash,
            ["manifestAction"] = result.ManifestAction,
            ["chunkCount"] = result.ChunkCount,
            ["deletedStaleCount"] = result.DeletedStaleCount,
            ["createdCount"] = result.CreatedCount,
            ["updatedCount"] = result.UpdatedCount,
            ["reusedCount"] = result.ReusedCount,
            ["vectorGeneratedCount"] = result.VectorGeneratedCount,
            ["vectorReusedCount"] = result.VectorReusedCount,
            ["deduplicatedCount"] = result.DeduplicatedCount,
            ["staleDeletes"] = result.StaleDeletes
                .OrderBy(stale => stale.PartitionKey, StringComparer.Ordinal)
                .ThenBy(stale => stale.Id, StringComparer.Ordinal)
                .Select(stale => new Dictionary<string, object?>
                {
                    ["id"] = stale.Id,
                    ["partitionKey"] = stale.PartitionKey,
                    ["chunkIndex"] = stale.ChunkIndex,
                    ["textHash"] = stale.TextHash
                })
                .ToList(),
            ["chunks"] = result.Chunks
                .OrderBy(chunk => chunk.Index)
                .Select(chunk => new Dictionary<string, object?>
                {
                    ["index"] = chunk.Index,
                    ["id"] = chunk.Id,
                    ["partitionKey"] = chunk.PartitionKey,
                    ["charStart"] = chunk.CharStart,
                    ["charEnd"] = chunk.CharEnd,
                    ["textLength"] = chunk.TextLength,
                    ["textHash"] = chunk.TextHash,
                    ["embeddingTextHash"] = chunk.EmbeddingTextHash,
                    ["action"] = chunk.Action,
                    ["embeddingAction"] = chunk.EmbeddingAction,
                    ["reusedVectorFromId"] = chunk.ReusedVectorFromId,
                    ["reusedVectorFromPartitionKey"] = chunk.ReusedVectorFromPartitionKey,
                    ["deduplicatedFromId"] = chunk.DeduplicatedFromId,
                    ["deduplicatedFromPartitionKey"] = chunk.DeduplicatedFromPartitionKey
                })
                .ToList()
        };
    }

    private static void WriteCanonicalJson(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonicalJson(item, writer);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
        }
    }

    private sealed record ReusableChunkVector(string RecordId, string PartitionKey, float[] Values, DateTime? GeneratedAt);

    private sealed record DeduplicatedChunkReference(string RecordId, string PartitionKey, string? Etag, int? Revision);

    private sealed record TextChunk(int CharStart, int CharEnd, string Text);
}
