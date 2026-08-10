using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public class LocalRetrievalService : IRetrievalService
{
    private const string VectorMode = "vector";
    private const string LexicalMode = "lexical";
    private const string HybridMode = "hybrid";
    private const int MaxVectorFields = 8;

    private readonly IRecordCollectionStore _recordStore;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ITraceStore? _traceStore;
    private readonly IRerankingService? _reranker;
    private readonly EmbeddingProviderOptions? _embeddingOptions;

    public LocalRetrievalService(
        IRecordCollectionStore recordStore,
        IEmbeddingProvider embeddingProvider,
        ITraceStore? traceStore = null,
        IRerankingService? reranker = null,
        EmbeddingProviderOptions? embeddingOptions = null)
    {
        _recordStore = recordStore;
        _embeddingProvider = embeddingProvider;
        _traceStore = traceStore;
        _reranker = reranker;
        _embeddingOptions = embeddingOptions;
    }

    public async Task<RetrievalResultEnvelope> SearchAsync(RetrievalRequest request, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        ct.ThrowIfCancellationRequested();
        request = RetrievalProfileCatalog.Apply(request);
        ValidateRequest(request);
        ct.ThrowIfCancellationRequested();

        var searchMode = NormalizeSearchMode(request);
        var rerankOptions = NormalizeRerankOptions(request.Rerank);
        var rerankEnabled = rerankOptions?.Enabled == true;
        var retrievalCandidateLimit = rerankEnabled ? ComputeRerankCandidateLimit(request.Limit, rerankOptions!) : request.Limit;
        var needsVector = searchMode is VectorMode or HybridMode;
        var needsLexical = searchMode is LexicalMode or HybridMode;
        var requestedVectorFields = needsVector ? NormalizeVectorFieldQueries(request) : new List<RetrievalVectorFieldQuery>();
        var preparedEmbeddings = new Dictionary<string, PreparedQueryEmbedding>(StringComparer.Ordinal);
        var allResults = new List<RetrievalMatch>();
        var candidatePools = new List<Dictionary<string, object?>>();

        if (needsVector)
        {
            await PrecomputeEmbeddingsAsync(request, requestedVectorFields, preparedEmbeddings, ct);
            ct.ThrowIfCancellationRequested();
        }

        var collectionTasks = request.Collections.Select(collection =>
            SearchCollectionAsync(
                collection,
                request,
                searchMode,
                needsVector,
                needsLexical,
                requestedVectorFields,
                preparedEmbeddings,
                retrievalCandidateLimit,
                ct));

        var collectionOutcomes = await Task.WhenAll(collectionTasks);
        ct.ThrowIfCancellationRequested();

        foreach (var outcome in collectionOutcomes)
        {
            ct.ThrowIfCancellationRequested();
            allResults.AddRange(outcome.Matches);
            candidatePools.Add(outcome.Pool);
        }

        ct.ThrowIfCancellationRequested();
        var baseRankedResults = SortMatches(allResults)
            .Take(retrievalCandidateLimit)
            .ToList();
        StampPreRerankDiagnostics(baseRankedResults);

        List<RetrievalMatch> rankedResults;
        RerankResult? rerankResult = null;
        if (rerankEnabled)
        {
            var reranked = await ApplyRerankingAsync(request, rerankOptions!, baseRankedResults, ct);
            ct.ThrowIfCancellationRequested();
            rankedResults = reranked.Results;
            rerankResult = reranked.Result;
        }
        else
        {
            rankedResults = baseRankedResults.Take(request.Limit).ToList();
        }

        rankedResults = rankedResults
            .Select((match, index) =>
            {
                ct.ThrowIfCancellationRequested();
                match.Rank = index + 1;
                AddTieBreakDiagnostics(match);
                return match;
            })
            .ToList();
        StampReturnedDiagnostics(rankedResults);

        var duration = DateTime.UtcNow - startTime;

        var envelope = new RetrievalResultEnvelope
        {
            Query = request.Query,
            Results = rankedResults
        };

        if (request.IncludeTrace)
        {
            var preparedEmbeddingSummaries = preparedEmbeddings.Values
                .OrderBy(item => item.Field, StringComparer.Ordinal)
                .Select(item => new Dictionary<string, object?>
                {
                    ["field"] = item.Field,
                    ["query"] = item.Query,
                    ["purpose"] = item.Prepared.Purpose,
                    ["prefixApplied"] = item.Prepared.PrefixApplied,
                    ["prefixLength"] = item.Prepared.PrefixLength,
                    ["preparedTextLength"] = item.Prepared.PreparedText.Length
                })
                .ToList();
            var firstPreparedEmbedding = preparedEmbeddings.Values.FirstOrDefault();
            var trace = new TraceRecord
            {
                Operation = "retrieval.search",
                Adapter = _recordStore.GetType().Name,
                StartedAt = startTime,
                DurationMs = duration.TotalMilliseconds,
                Request = new Dictionary<string, object?>
                {
                    ["profile"] = request.Profile,
                    ["query"] = request.Query,
                    ["collections"] = request.Collections,
                    ["partitionKeys"] = request.PartitionKeys,
                    ["filter"] = request.Filter,
                    ["embeddingField"] = request.Embedding?.Field,
                    ["vectorFields"] = request.VectorFields,
                    ["embeddingPurpose"] = firstPreparedEmbedding?.Prepared.Purpose,
                    ["embeddingPrefixApplied"] = firstPreparedEmbedding?.Prepared.PrefixApplied,
                    ["embeddingPrefixLength"] = firstPreparedEmbedding?.Prepared.PrefixLength,
                    ["preparedQueryLength"] = firstPreparedEmbedding?.Prepared.PreparedText.Length,
                    ["preparedVectorQueries"] = preparedEmbeddingSummaries,
                    ["searchMode"] = searchMode,
                    ["lexical"] = request.Lexical,
                    ["hybrid"] = request.Hybrid,
                    ["rerank"] = request.Rerank,
                    ["limit"] = request.Limit,
                    ["minScore"] = request.MinScore
                },
                ResultSummary = new Dictionary<string, object?>
                {
                    ["profile"] = request.Profile,
                    ["embeddingModel"] = needsVector ? _embeddingProvider.ModelId : null,
                    ["embeddingDimensions"] = needsVector ? _embeddingProvider.Dimensions : null,
                    ["embeddingPurpose"] = firstPreparedEmbedding?.Prepared.Purpose,
                    ["embeddingPrefixApplied"] = firstPreparedEmbedding?.Prepared.PrefixApplied,
                    ["embeddingPrefixLength"] = firstPreparedEmbedding?.Prepared.PrefixLength,
                    ["preparedVectorQueries"] = preparedEmbeddingSummaries,
                    ["candidateCount"] = allResults.Count,
                    ["returnedCount"] = rankedResults.Count,
                    ["searchMode"] = searchMode,
                    ["candidatePools"] = candidatePools,
                    ["rerankEnabled"] = rerankEnabled,
                    ["rerankProvider"] = rerankResult?.Provider,
                    ["rerankTraceId"] = rerankResult?.TraceId,
                    ["rerankFallbackApplied"] = rerankResult?.FallbackApplied,
                    ["rerankFailureClass"] = rerankResult?.FailureClass,
                    ["rerankProviderStatus"] = rerankResult?.ProviderStatus,
                    ["rerankInputCandidateCount"] = rerankResult?.InputCandidateCount,
                    ["rerankProviderPayloadBytes"] = rerankResult?.ProviderPayloadBytes,
                    ["rerankProviderMaxInputBytes"] = rerankResult?.ProviderMaxInputBytes
                }
            };

            if (_traceStore != null)
            {
                await _traceStore.WriteTraceAsync(trace, ct);
            }

            StampRetrievalTraceReferences(rankedResults, trace.Id);

            envelope.Trace = JsonSerializer.SerializeToNode(new Dictionary<string, object?>
            {
                ["id"] = trace.Id,
                ["profile"] = request.Profile ?? string.Empty,
                ["durationMs"] = duration.TotalMilliseconds,
                ["embeddingModel"] = needsVector ? _embeddingProvider.ModelId : string.Empty,
                ["embeddingDimensions"] = needsVector ? _embeddingProvider.Dimensions : 0,
                ["embeddingPurpose"] = firstPreparedEmbedding?.Prepared.Purpose ?? string.Empty,
                ["embeddingPrefixApplied"] = firstPreparedEmbedding?.Prepared.PrefixApplied ?? false,
                ["embeddingPrefixLength"] = firstPreparedEmbedding?.Prepared.PrefixLength ?? 0,
                ["preparedVectorQueries"] = preparedEmbeddingSummaries,
                ["candidateCount"] = allResults.Count,
                ["returnedCount"] = rankedResults.Count,
                ["searchMode"] = searchMode,
                ["candidatePools"] = candidatePools,
                ["rerankEnabled"] = rerankEnabled,
                ["rerankProvider"] = rerankResult?.Provider ?? string.Empty,
                ["rerankTraceId"] = rerankResult?.TraceId ?? string.Empty,
                ["rerankFallbackApplied"] = rerankResult?.FallbackApplied ?? false,
                ["rerankFailureClass"] = rerankResult?.FailureClass ?? string.Empty,
                ["rerankProviderStatus"] = rerankResult?.ProviderStatus ?? string.Empty,
                ["rerankInputCandidateCount"] = rerankResult?.InputCandidateCount ?? 0,
                ["rerankProviderPayloadBytes"] = rerankResult?.ProviderPayloadBytes ?? 0,
                ["rerankProviderMaxInputBytes"] = rerankResult?.ProviderMaxInputBytes ?? 0
            }) as JsonObject;
        }

        return envelope;
    }

    private async Task PrecomputeEmbeddingsAsync(
        RetrievalRequest request,
        IReadOnlyList<RetrievalVectorFieldQuery> requestedVectorFields,
        Dictionary<string, PreparedQueryEmbedding> preparedEmbeddings,
        CancellationToken ct)
    {
        foreach (var vectorField in requestedVectorFields)
        {
            ct.ThrowIfCancellationRequested();
            var prepared = PrepareQueryEmbeddingText(request, new ResolvedVectorFieldQuery
            {
                Field = vectorField.Field ?? string.Empty,
                Policy = null!,
                Weight = vectorField.Weight,
                Query = vectorField.Query,
                EmbeddingPurpose = vectorField.Embedding?.Purpose,
                QueryPrefix = vectorField.Embedding?.QueryPrefix,
                PassagePrefix = vectorField.Embedding?.PassagePrefix,
                SymmetricPrefix = vectorField.Embedding?.SymmetricPrefix
            });
            if (!preparedEmbeddings.ContainsKey(prepared.PreparedText))
            {
                preparedEmbeddings[prepared.PreparedText] = new PreparedQueryEmbedding
                {
                    Field = vectorField.Field ?? string.Empty,
                    Query = string.IsNullOrWhiteSpace(vectorField.Query) ? request.Query : vectorField.Query!,
                    Prepared = prepared,
                    Vector = await _embeddingProvider.GenerateEmbeddingAsync(prepared.PreparedText, ct)
                };
            }
        }
    }

    private async Task<CollectionSearchOutcome> SearchCollectionAsync(
        string collection,
        RetrievalRequest request,
        string searchMode,
        bool needsVector,
        bool needsLexical,
        IReadOnlyList<RetrievalVectorFieldQuery> requestedVectorFields,
        IReadOnlyDictionary<string, PreparedQueryEmbedding> preparedEmbeddings,
        int retrievalCandidateLimit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var policy = await _recordStore.GetCollectionPolicyAsync(collection, ct);
        if (policy == null)
        {
            throw new InvalidOperationException($"Collection '{collection}' does not exist.");
        }

        var candidates = new Dictionary<string, RetrievalCandidate>(StringComparer.Ordinal);
        var vectorCandidateCount = 0;
        var vectorCandidateCountsByField = new Dictionary<string, int>(StringComparer.Ordinal);
        var lexicalCandidateCount = 0;
        var resolvedVectorFields = needsVector
            ? ResolveVectorFields(collection, policy, request, requestedVectorFields, searchMode, retrievalCandidateLimit)
            : new List<ResolvedVectorFieldQuery>();

        if (needsVector)
        {
            foreach (var vectorField in resolvedVectorFields)
            {
                ct.ThrowIfCancellationRequested();
                ValidateEmbeddingDimensions(collection, vectorField.Field, vectorField.Policy);
                var prepared = PrepareQueryEmbeddingText(request, vectorField);
                PreparedQueryEmbedding embedding;
                if (preparedEmbeddings.TryGetValue(prepared.PreparedText, out var cached))
                {
                    embedding = cached;
                }
                else
                {
                    embedding = new PreparedQueryEmbedding
                    {
                        Field = vectorField.Field,
                        Query = string.IsNullOrWhiteSpace(vectorField.Query) ? request.Query : vectorField.Query!,
                        Prepared = prepared,
                        Vector = await _embeddingProvider.GenerateEmbeddingAsync(prepared.PreparedText, ct)
                    };
                }

                var vectorMatches = await _recordStore.SearchAllRecordsAsync(collection, new QueryEnvelope
                {
                    PartitionKeys = request.PartitionKeys,
                    Filter = request.Filter,
                    Vector = new VectorSearchOptions
                    {
                        Field = vectorField.Field,
                        Value = embedding.Vector,
                        Top = vectorField.CandidateLimit,
                        MinScore = vectorField.MinScore
                    },
                    Limit = vectorField.CandidateLimit
                }, ct);

                var fieldCandidateCount = 0;
                var vectorRank = 1;
                foreach (var match in vectorMatches)
                {
                    ct.ThrowIfCancellationRequested();
                    var candidate = GetCandidate(candidates, collection, match.Record);
                    candidate.AddVectorHit(
                        vectorField.Field,
                        match.Score,
                        NormalizeVectorScore(vectorField.Policy.DistanceFunction, match.Score),
                        vectorRank++,
                        vectorField.Policy.DistanceFunction,
                        vectorField.Weight,
                        match.Diagnostics);
                    candidate.CandidateSources.Add(VectorMode);
                    fieldCandidateCount++;
                }

                vectorCandidateCount += fieldCandidateCount;
                vectorCandidateCountsByField[vectorField.Field] = fieldCandidateCount;
            }
        }

        if (needsLexical)
        {
            var lexical = BuildLexicalOptions(request, searchMode, retrievalCandidateLimit);
            var lexicalMatches = await _recordStore.SearchAllRecordsAsync(collection, new QueryEnvelope
            {
                PartitionKeys = request.PartitionKeys,
                Filter = request.Filter,
                Lexical = lexical,
                Limit = lexical.Top
            }, ct);

            var lexicalRank = 1;
            foreach (var match in lexicalMatches)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = GetCandidate(candidates, collection, match.Record);
                candidate.LexicalScore = match.Score;
                candidate.LexicalRank = lexicalRank++;
                candidate.LexicalDiagnostics = match.Diagnostics;
                candidate.CandidateSources.Add(LexicalMode);
                lexicalCandidateCount++;
            }
        }

        foreach (var candidate in candidates.Values)
        {
            ct.ThrowIfCancellationRequested();
            candidate.FinalizeVectorScore(resolvedVectorFields);
        }

        var matches = new List<RetrievalMatch>();
        foreach (var candidate in candidates.Values)
        {
            ct.ThrowIfCancellationRequested();
            candidate.Score = CalculateFinalScore(candidate, searchMode, request.Hybrid);
            if (request.MinScore.HasValue && candidate.Score < request.MinScore.Value)
            {
                continue;
            }

            matches.Add(new RetrievalMatch
            {
                Score = candidate.Score,
                Collection = collection,
                Snippet = BuildSnippet(candidate.Record),
                Record = candidate.Record,
                Diagnostics = request.IncludeTrace
                    ? BuildDiagnostics(
                        candidate,
                        searchMode,
                        request.Hybrid,
                        vectorCandidateCount,
                        vectorCandidateCountsByField,
                        lexicalCandidateCount,
                        candidates.Count,
                        retrievalCandidateLimit)
                    : null
            });
        }

        var pool = new Dictionary<string, object?>
        {
            ["collection"] = collection,
            ["vectorCandidates"] = vectorCandidateCount,
            ["vectorCandidatesByField"] = vectorCandidateCountsByField,
            ["lexicalCandidates"] = lexicalCandidateCount,
            ["mergedCandidates"] = candidates.Count,
            ["searchMode"] = searchMode
        };

        return new CollectionSearchOutcome(matches, pool);
    }

    private readonly record struct CollectionSearchOutcome(
        List<RetrievalMatch> Matches,
        Dictionary<string, object?> Pool);

    private static void ValidateRequest(RetrievalRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new InvalidOperationException("Retrieval query is required.");
        }

        if (request.Collections == null || request.Collections.Count == 0)
        {
            throw new InvalidOperationException("At least one retrieval collection is required.");
        }

        if (request.Limit <= 0)
        {
            throw new InvalidOperationException("Retrieval limit must be greater than zero.");
        }

        _ = NormalizeSearchMode(request);
        if (!string.IsNullOrWhiteSpace(request.Embedding?.Purpose))
        {
            _ = EmbeddingTextPreparer.NormalizePurpose(request.Embedding.Purpose);
        }

        if (request.VectorFields is { Count: > MaxVectorFields })
        {
            throw new InvalidOperationException($"Retrieval supports at most {MaxVectorFields} vectorFields entries.");
        }

        if (request.VectorFields is { Count: > 0 })
        {
            var totalWeight = 0.0f;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var vectorField in request.VectorFields)
            {
                if (string.IsNullOrWhiteSpace(vectorField.Field))
                {
                    throw new InvalidOperationException("Retrieval vectorFields field is required.");
                }

                if (!fields.Add(vectorField.Field.Trim()))
                {
                    throw new InvalidOperationException($"Retrieval vectorFields contains duplicate field '{vectorField.Field}'.");
                }

                if (vectorField.Weight < 0)
                {
                    throw new InvalidOperationException("Retrieval vectorFields weight must be non-negative.");
                }

                totalWeight += vectorField.Weight;

                if (vectorField.CandidateLimit is <= 0)
                {
                    throw new InvalidOperationException("Retrieval vectorFields candidateLimit must be greater than zero when provided.");
                }

                if (!string.IsNullOrWhiteSpace(vectorField.Embedding?.Purpose))
                {
                    _ = EmbeddingTextPreparer.NormalizePurpose(vectorField.Embedding.Purpose);
                }
            }

            if (totalWeight <= 0)
            {
                throw new InvalidOperationException("Retrieval vectorFields requires at least one positive weight.");
            }
        }

        if (request.Hybrid is not null)
        {
            if (request.Hybrid.VectorWeight < 0 || request.Hybrid.LexicalWeight < 0)
            {
                throw new InvalidOperationException("Hybrid search weights must be non-negative.");
            }

            if (request.Hybrid.VectorWeight + request.Hybrid.LexicalWeight <= 0)
            {
                throw new InvalidOperationException("Hybrid search requires at least one positive weight.");
            }

            if (request.Hybrid.CandidateMultiplier <= 0)
            {
                throw new InvalidOperationException("Hybrid candidateMultiplier must be greater than zero.");
            }

            if (request.Hybrid.RrfK <= 0)
            {
                throw new InvalidOperationException("Hybrid rrfK must be greater than zero.");
            }

            _ = NormalizeHybridFusion(request.Hybrid.Fusion);
        }
    }

    private static List<RetrievalVectorFieldQuery> NormalizeVectorFieldQueries(RetrievalRequest request)
    {
        if (request.VectorFields is { Count: > 0 })
        {
            return request.VectorFields
                .Select(item => new RetrievalVectorFieldQuery
                {
                    Field = item.Field.Trim(),
                    Weight = item.Weight,
                    Query = item.Query,
                    Embedding = item.Embedding,
                    CandidateLimit = item.CandidateLimit,
                    MinScore = item.MinScore
                })
                .ToList();
        }

        return new List<RetrievalVectorFieldQuery>
        {
            new()
            {
                Field = request.Embedding?.Field ?? string.Empty,
                Weight = 1.0f,
                Embedding = request.Embedding
            }
        };
    }

    private static List<ResolvedVectorFieldQuery> ResolveVectorFields(
        string collection,
        RecordCollectionPolicy policy,
        RetrievalRequest request,
        IReadOnlyList<RetrievalVectorFieldQuery> requestedVectorFields,
        string searchMode,
        int retrievalCandidateLimit)
    {
        var defaultVectorTop = searchMode == HybridMode
            ? Math.Max(retrievalCandidateLimit, ComputeCandidateLimit(retrievalCandidateLimit, request.Hybrid, request.Hybrid?.VectorCandidateLimit))
            : retrievalCandidateLimit;
        return requestedVectorFields
            .Select(requested =>
            {
                var requestedField = string.IsNullOrWhiteSpace(requested.Field) ? request.Embedding?.Field : requested.Field;
                var (field, fieldPolicy) = ResolveVectorPolicy(collection, policy, requestedField);
                return new ResolvedVectorFieldQuery
                {
                    Field = field,
                    Policy = fieldPolicy,
                    Weight = requested.Weight,
                    Query = requested.Query,
                    EmbeddingPurpose = requested.Embedding?.Purpose,
                    QueryPrefix = requested.Embedding?.QueryPrefix,
                    PassagePrefix = requested.Embedding?.PassagePrefix,
                    SymmetricPrefix = requested.Embedding?.SymmetricPrefix,
                    CandidateLimit = Math.Max(retrievalCandidateLimit, requested.CandidateLimit ?? defaultVectorTop),
                    MinScore = requested.MinScore ?? (searchMode == VectorMode ? request.MinScore : null)
                };
            })
            .ToList();
    }


    private PreparedEmbeddingText PrepareQueryEmbeddingText(RetrievalRequest request, ResolvedVectorFieldQuery vectorField)
    {
        return EmbeddingTextPreparer.Prepare(
            string.IsNullOrWhiteSpace(vectorField.Query) ? request.Query : vectorField.Query!,
            string.IsNullOrWhiteSpace(vectorField.EmbeddingPurpose)
                ? string.IsNullOrWhiteSpace(request.Embedding?.Purpose) ? EmbeddingPurposes.Query : request.Embedding.Purpose
                : vectorField.EmbeddingPurpose,
            vectorField.QueryPrefix ?? request.Embedding?.QueryPrefix ?? _embeddingOptions?.QueryPrefix,
            vectorField.PassagePrefix ?? request.Embedding?.PassagePrefix ?? _embeddingOptions?.PassagePrefix,
            vectorField.SymmetricPrefix ?? request.Embedding?.SymmetricPrefix ?? _embeddingOptions?.SymmetricPrefix);
    }

    private static string NormalizeSearchMode(RetrievalRequest request)
    {
        var searchMode = request.SearchMode;
        if (string.IsNullOrWhiteSpace(searchMode))
        {
            return HasVectorIntent(request) ? VectorMode : LexicalMode;
        }

        return searchMode.Trim().ToLowerInvariant() switch
        {
            VectorMode => VectorMode,
            LexicalMode => LexicalMode,
            HybridMode => HybridMode,
            _ => throw new InvalidOperationException($"Retrieval searchMode '{searchMode}' is not supported.")
        };
    }

    private static bool HasVectorIntent(RetrievalRequest request)
    {
        return request.VectorFields is { Count: > 0 } || request.Embedding is not null;
    }

    private static (string SearchField, VectorFieldPolicy FieldPolicy) ResolveVectorPolicy(string collection, RecordCollectionPolicy policy, string? requestedField)
    {
        var searchField = requestedField;
        if (string.IsNullOrEmpty(searchField) && policy.VectorPolicies.Any())
        {
            searchField = policy.VectorPolicies[0].Name;
        }

        if (string.IsNullOrEmpty(searchField))
        {
            throw new InvalidOperationException($"Collection '{collection}' does not define a vector policy for retrieval.");
        }

        var fieldPolicy = policy.VectorPolicies.FirstOrDefault(p => p.Name == searchField);
        if (fieldPolicy == null)
        {
            throw new InvalidOperationException($"Vector field '{searchField}' is not defined in policy for collection '{collection}'.");
        }

        return (searchField, fieldPolicy);
    }

    private void ValidateEmbeddingDimensions(string collection, string searchField, VectorFieldPolicy fieldPolicy)
    {
        if (fieldPolicy.Dimensions != _embeddingProvider.Dimensions)
        {
            throw new InvalidOperationException($"Embedding provider returns {_embeddingProvider.Dimensions} dimensions, but collection '{collection}' field '{searchField}' expects {fieldPolicy.Dimensions}.");
        }
    }

    private static LexicalSearchOptions BuildLexicalOptions(RetrievalRequest request, string searchMode, int retrievalCandidateLimit)
    {
        var source = request.Lexical ?? new LexicalSearchOptions();
        var top = source.Top > 0 ? source.Top : 50;
        if (searchMode == HybridMode)
        {
            top = Math.Max(top, ComputeCandidateLimit(retrievalCandidateLimit, request.Hybrid, request.Hybrid?.LexicalCandidateLimit));
        }
        else
        {
            top = Math.Max(retrievalCandidateLimit, top);
        }

        return new LexicalSearchOptions
        {
            Query = string.IsNullOrWhiteSpace(source.Query) ? request.Query : source.Query,
            Fields = source.Fields == null ? null : new List<string>(source.Fields),
            Top = top,
            ScanLimit = source.ScanLimit,
            MinScore = searchMode == LexicalMode ? request.MinScore ?? source.MinScore : source.MinScore,
            Scoring = source.Scoring,
            MatchMode = source.MatchMode,
            FieldBoosts = source.FieldBoosts == null ? null : new Dictionary<string, float>(source.FieldBoosts, StringComparer.Ordinal),
            Bm25K1 = source.Bm25K1,
            Bm25B = source.Bm25B,
            PhraseBoost = source.PhraseBoost,
            ExactBoost = source.ExactBoost,
            MetadataBoost = source.MetadataBoost,
            PrefixMatching = source.PrefixMatching,
            PrefixMinChars = source.PrefixMinChars,
            RequiredPhraseGroups = source.RequiredPhraseGroups?
                .Select(group => group.ToList())
                .ToList()
        };
    }

    private static RerankOptions? NormalizeRerankOptions(RerankOptions? options)
    {
        if (options?.Enabled != true)
        {
            return options;
        }

        if (options.CandidateLimit.HasValue && options.CandidateLimit.Value <= 0)
        {
            throw new InvalidOperationException("Rerank candidateLimit must be greater than zero when provided.");
        }

        if (options.MaxCandidateChars <= 0)
        {
            throw new InvalidOperationException("Rerank maxCandidateChars must be greater than zero.");
        }

        if (options.RerankScoreWeight < 0 || options.OriginalScoreWeight < 0)
        {
            throw new InvalidOperationException("Rerank score weights must be non-negative.");
        }

        if (options.RerankScoreWeight + options.OriginalScoreWeight <= 0)
        {
            throw new InvalidOperationException("Rerank requires at least one positive score weight.");
        }

        if (options.TimeoutSeconds.HasValue && options.TimeoutSeconds.Value <= 0)
        {
            throw new InvalidOperationException("Rerank timeoutSeconds must be greater than zero when provided.");
        }

        if (options.MaxOutputBytes.HasValue && options.MaxOutputBytes.Value <= 0)
        {
            throw new InvalidOperationException("Rerank maxOutputBytes must be greater than zero when provided.");
        }

        if (!string.IsNullOrWhiteSpace(options.ContentField) &&
            (options.ContentField.Contains('/') || options.ContentField.Contains('\\') || options.ContentField.Contains('.')))
        {
            throw new InvalidOperationException("Rerank contentField must be a simple content property name.");
        }

        return options;
    }

    private static int ComputeRerankCandidateLimit(int resultLimit, RerankOptions options)
    {
        if (options.CandidateLimit.HasValue)
        {
            return Math.Max(resultLimit, options.CandidateLimit.Value);
        }

        return Math.Max(resultLimit * 4, resultLimit);
    }

    private static int ComputeCandidateLimit(int resultLimit, HybridSearchOptions? hybrid, int? explicitLimit)
    {
        if (explicitLimit.HasValue)
        {
            return Math.Max(resultLimit, explicitLimit.Value);
        }

        var multiplier = hybrid?.CandidateMultiplier > 0 ? hybrid.CandidateMultiplier : 8;
        return Math.Max(Math.Max(resultLimit * multiplier, resultLimit), 50);
    }

    private static RetrievalCandidate GetCandidate(Dictionary<string, RetrievalCandidate> candidates, string collection, VyralRecord record)
    {
        var key = $"{collection}\u001f{record.PartitionKey}\u001f{record.Id}";
        if (candidates.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var candidate = new RetrievalCandidate
        {
            Collection = collection,
            Record = record
        };
        candidates[key] = candidate;
        return candidate;
    }

    private static float CalculateFinalScore(RetrievalCandidate candidate, string searchMode, HybridSearchOptions? hybrid)
    {
        return searchMode switch
        {
            VectorMode => candidate.VectorScore ?? 0,
            LexicalMode => candidate.LexicalScore ?? 0,
            HybridMode => CalculateHybridScore(candidate, hybrid),
            _ => 0
        };
    }

    private static float CalculateHybridScore(RetrievalCandidate candidate, HybridSearchOptions? hybrid)
    {
        var fusion = NormalizeHybridFusion(hybrid?.Fusion);
        if (fusion == "rrf")
        {
            return CalculateRrfScore(candidate, hybrid);
        }

        var vectorWeight = hybrid?.VectorWeight ?? 0.55f;
        var lexicalWeight = hybrid?.LexicalWeight ?? 0.45f;
        var totalWeight = vectorWeight + lexicalWeight;
        if (totalWeight <= 0)
        {
            return 0;
        }

        var vectorScore = candidate.VectorNormalizedScore ?? 0;
        var lexicalScore = candidate.LexicalScore ?? 0;
        return ((vectorWeight * vectorScore) + (lexicalWeight * lexicalScore)) / totalWeight;
    }

    private static float CalculateRrfScore(RetrievalCandidate candidate, HybridSearchOptions? hybrid)
    {
        var vectorWeight = hybrid?.VectorWeight ?? 0.55f;
        var lexicalWeight = hybrid?.LexicalWeight ?? 0.45f;
        var rrfK = hybrid?.RrfK > 0 ? hybrid.RrfK : 60;
        var score = 0.0f;

        if (candidate.VectorRank.HasValue)
        {
            score += vectorWeight / (rrfK + candidate.VectorRank.Value);
        }

        if (candidate.LexicalRank.HasValue)
        {
            score += lexicalWeight / (rrfK + candidate.LexicalRank.Value);
        }

        return score;
    }

    private static float NormalizeVectorScore(string distanceFunction, float score)
    {
        return distanceFunction.ToLowerInvariant() switch
        {
            "cosine" => Math.Clamp((score + 1.0f) / 2.0f, 0, 1),
            "euclidean" => Math.Clamp(score, 0, 1),
            "dotproduct" => score <= 0 ? 0 : score / (1.0f + score),
            _ => Math.Clamp(score, 0, 1)
        };
    }

    private RetrievalDiagnostics BuildDiagnostics(
        RetrievalCandidate candidate,
        string searchMode,
        HybridSearchOptions? hybrid,
        int vectorCandidateCount,
        IReadOnlyDictionary<string, int> vectorCandidateCountsByField,
        int lexicalCandidateCount,
        int mergedCandidateCount,
        int retrievalCandidateLimit)
    {
        var diagnostics = new RetrievalDiagnostics
        {
            ResultIdentity = BuildResultIdentity(candidate),
            CandidateSources = candidate.CandidateSources.OrderBy(source => source, StringComparer.Ordinal).ToList(),
            CandidateCounts = new Dictionary<string, int>
            {
                ["collectionVectorCandidates"] = vectorCandidateCount,
                ["collectionLexicalCandidates"] = lexicalCandidateCount,
                ["collectionMergedCandidates"] = mergedCandidateCount,
                ["retrievalCandidateLimit"] = retrievalCandidateLimit,
                ["collectionVectorCandidateFields"] = vectorCandidateCountsByField.Count
            },
            ReasonCodes = BuildReasonCodes(candidate, searchMode, hybrid),
            MatchedFields = candidate.LexicalDiagnostics?.MatchedFields ?? new List<string>(),
            MatchedTerms = candidate.LexicalDiagnostics?.MatchedTerms ?? new List<string>(),
            ScoreNormalization = BuildScoreNormalization(candidate, searchMode, hybrid),
            ScoreComponents = new Dictionary<string, float>
            {
                ["final"] = candidate.Score
            },
            Details = new Dictionary<string, object?>
            {
                ["searchMode"] = searchMode,
                ["collection"] = candidate.Collection,
                ["embeddingProvider"] = _embeddingProvider.ProviderId,
                ["embeddingModel"] = candidate.VectorScore.HasValue ? _embeddingProvider.ModelId : null,
                ["embeddingDimensions"] = candidate.VectorScore.HasValue ? _embeddingProvider.Dimensions : null,
                ["embeddingField"] = candidate.EmbeddingField,
                ["vectorFieldCount"] = candidate.VectorFusionFieldCount,
                ["vectorFields"] = candidate.VectorHits.Values
                    .OrderBy(hit => hit.Field, StringComparer.Ordinal)
                    .Select(hit => new Dictionary<string, object?>
                    {
                        ["field"] = hit.Field,
                        ["rawScore"] = hit.RawScore,
                        ["normalizedScore"] = hit.NormalizedScore,
                        ["rank"] = hit.Rank,
                        ["weight"] = hit.Weight,
                        ["distanceFunction"] = hit.DistanceFunction,
                        ["indexProvider"] = hit.VectorIndexProvider,
                        ["indexUsed"] = hit.VectorIndexUsed,
                        ["indexTable"] = hit.VectorIndexTable,
                        ["indexQuantized"] = hit.VectorIndexQuantized,
                        ["indexReason"] = hit.VectorIndexReason
                    })
                    .ToList(),
                ["vectorDistanceFunction"] = candidate.VectorDistanceFunction,
                ["vectorRank"] = candidate.VectorRank,
                ["vectorIndexUsed"] = candidate.VectorHits.Values.Any(hit => hit.VectorIndexUsed == true),
                ["vectorIndexProvider"] = BuildVectorIndexProviderSummary(candidate.VectorHits.Values),
                ["vectorIndexProviders"] = candidate.VectorHits.Values
                    .Where(hit => !string.IsNullOrWhiteSpace(hit.VectorIndexProvider))
                    .GroupBy(hit => hit.Field, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First().VectorIndexProvider, StringComparer.Ordinal),
                ["vectorIndexFields"] = candidate.VectorHits.Values
                    .Where(hit => hit.VectorIndexUsed == true)
                    .Select(hit => hit.Field)
                    .OrderBy(field => field, StringComparer.Ordinal)
                    .ToList(),
                ["vectorIndexReasons"] = candidate.VectorHits.Values
                    .Where(hit => !string.IsNullOrWhiteSpace(hit.VectorIndexReason))
                    .GroupBy(hit => hit.Field, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First().VectorIndexReason, StringComparer.Ordinal),
                ["lexicalRank"] = candidate.LexicalRank
            }
        };

        foreach (var (field, count) in vectorCandidateCountsByField)
        {
            diagnostics.CandidateCounts[$"collectionVectorCandidates.{field}"] = count;
        }

        if (candidate.VectorScore.HasValue)
        {
            diagnostics.ScoreComponents["vector"] = candidate.VectorScore.Value;
            diagnostics.ScoreComponents["vectorNormalized"] = candidate.VectorNormalizedScore ?? 0;
            if (candidate.UsesMultiVectorFusion)
            {
                diagnostics.ScoreComponents["vectorAggregate"] = candidate.VectorNormalizedScore ?? 0;
            }

            foreach (var hit in candidate.VectorHits.Values)
            {
                diagnostics.ScoreComponents[$"vector.{hit.Field}.raw"] = hit.RawScore;
                diagnostics.ScoreComponents[$"vector.{hit.Field}.normalized"] = hit.NormalizedScore;
                diagnostics.ScoreComponents[$"vector.{hit.Field}.weight"] = hit.Weight;
            }
        }

        if (candidate.LexicalDiagnostics is not null)
        {
            foreach (var (key, value) in candidate.LexicalDiagnostics.ScoreComponents)
            {
                diagnostics.ScoreComponents[key] = value;
            }

            foreach (var (key, value) in candidate.LexicalDiagnostics.Details)
            {
                diagnostics.Details[key] = value;
            }
        }

        if (searchMode == HybridMode)
        {
            diagnostics.Details["hybridFusion"] = NormalizeHybridFusion(hybrid?.Fusion);
            diagnostics.Details["rrfK"] = hybrid?.RrfK ?? 60;
            diagnostics.ScoreComponents["vectorWeight"] = hybrid?.VectorWeight ?? 0.55f;
            diagnostics.ScoreComponents["lexicalWeight"] = hybrid?.LexicalWeight ?? 0.45f;
            if (NormalizeHybridFusion(hybrid?.Fusion) == "rrf")
            {
                diagnostics.ScoreComponents["rrf"] = candidate.Score;
            }
        }

        return diagnostics;
    }

    private static string? BuildVectorIndexProviderSummary(IEnumerable<VectorFieldHit> hits)
    {
        var providers = hits
            .Select(hit => hit.VectorIndexProvider)
            .Where(provider => !string.IsNullOrWhiteSpace(provider))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return providers.Count switch
        {
            0 => null,
            1 => providers[0],
            _ => "mixed"
        };
    }

    private static RetrievalResultIdentity BuildResultIdentity(RetrievalCandidate candidate)
    {
        return new RetrievalResultIdentity
        {
            Collection = candidate.Collection,
            PartitionKey = candidate.Record.PartitionKey,
            Id = candidate.Record.Id,
            Type = candidate.Record.Type,
            Etag = candidate.Record.Etag,
            Revision = candidate.Record.Revision
        };
    }

    private static List<string> BuildReasonCodes(RetrievalCandidate candidate, string searchMode, HybridSearchOptions? hybrid)
    {
        var reasonCodes = new List<string>
        {
            "result.identity.record",
            $"mode.{searchMode}"
        };

        if (candidate.VectorScore.HasValue)
        {
            reasonCodes.Add("candidate.source.vector");
            reasonCodes.Add("score.vector.raw_similarity");
            reasonCodes.Add("score.vector.normalized");
            if (candidate.VectorHits.Values.Any(hit => hit.VectorIndexUsed == true))
            {
                reasonCodes.Add("index.sqlite_vector");
            }

            if (candidate.UsesMultiVectorFusion)
            {
                reasonCodes.Add("fusion.multi_vector");
                reasonCodes.Add("score.vector.weighted_field_fusion");
            }
        }

        if (candidate.LexicalScore.HasValue)
        {
            reasonCodes.Add("candidate.source.lexical");
            reasonCodes.Add("score.lexical");
        }

        if (searchMode == HybridMode)
        {
            reasonCodes.Add(NormalizeHybridFusion(hybrid?.Fusion) == "rrf"
                ? "fusion.rrf"
                : "fusion.weighted");
        }

        return reasonCodes.Distinct(StringComparer.Ordinal).ToList();
    }

    private static RetrievalScoreNormalization BuildScoreNormalization(RetrievalCandidate candidate, string searchMode, HybridSearchOptions? hybrid)
    {
        var fusion = searchMode == HybridMode ? NormalizeHybridFusion(hybrid?.Fusion) : null;
        var normalization = new RetrievalScoreNormalization
        {
            FinalScoreKind = searchMode switch
            {
                VectorMode when candidate.UsesMultiVectorFusion => "vector.multi_field_weighted_normalized",
                VectorMode => "vector.raw_similarity",
                LexicalMode => "lexical.score",
                HybridMode when fusion == "rrf" => "hybrid.rrf",
                HybridMode => "hybrid.weighted_normalized",
                _ => "unknown"
            },
            HybridFusion = fusion,
            VectorDistanceFunction = candidate.VectorDistanceFunction,
            VectorNormalization = candidate.VectorScore.HasValue
                ? candidate.UsesMultiVectorFusion
                    ? "weighted normalized vector field scores; missing fields contribute 0"
                    : DescribeVectorNormalization(candidate.VectorDistanceFunction)
                : null
        };

        if (candidate.VectorScore.HasValue)
        {
            normalization.VectorScoreKind = candidate.UsesMultiVectorFusion
                ? "vector.multi_field_weighted_normalized"
                : $"vector.similarity.{NormalizeScoreKindToken(candidate.VectorDistanceFunction)}";
            normalization.Parameters["vectorRank"] = candidate.VectorRank;
            foreach (var hit in candidate.VectorHits.Values)
            {
                normalization.Weights[$"vectorField.{hit.Field}"] = hit.Weight;
            }
        }

        if (candidate.LexicalScore.HasValue)
        {
            var lexicalKind = "score";
            if (candidate.LexicalDiagnostics?.Details.TryGetValue("lexicalScoring", out var scoring) == true)
            {
                lexicalKind = NormalizeScoreKindToken(scoring?.ToString() ?? string.Empty);
            }

            normalization.LexicalScoreKind = $"lexical.{lexicalKind}";
            normalization.Parameters["lexicalRank"] = candidate.LexicalRank;
        }

        if (searchMode == HybridMode)
        {
            normalization.Weights["vector"] = hybrid?.VectorWeight ?? 0.55f;
            normalization.Weights["lexical"] = hybrid?.LexicalWeight ?? 0.45f;
            normalization.Parameters["rrfK"] = hybrid?.RrfK ?? 60;
        }

        return normalization;
    }

    private static string? DescribeVectorNormalization(string? distanceFunction)
    {
        return NormalizeScoreKindToken(distanceFunction) switch
        {
            "cosine" => "clamp((score+1)/2,0,1)",
            "euclidean" => "clamp(local_similarity,0,1)",
            "dotproduct" => "score<=0?0:score/(1+score)",
            "" => null,
            _ => "clamp(score,0,1)"
        };
    }

    private static string NormalizeScoreKindToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var token = value.Trim().ToLowerInvariant();
        return token switch
        {
            "dot product" => "dotproduct",
            "dot_product" => "dotproduct",
            "dot-product" => "dotproduct",
            _ => token.Replace(" ", "_", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal)
        };
    }

    private async Task<(List<RetrievalMatch> Results, RerankResult Result)> ApplyRerankingAsync(
        RetrievalRequest request,
        RerankOptions options,
        IReadOnlyList<RetrievalMatch> baseRankedResults,
        CancellationToken ct)
    {
        if (_reranker is null)
        {
            if (options.FallbackOnFailure)
            {
                var missing = new InvalidOperationException("Reranking was requested, but no reranking service is configured.");
                return BuildRerankFallback(request, options, baseRankedResults, Array.Empty<RerankCandidate>(), missing);
            }

            throw new InvalidOperationException("Reranking was requested, but no reranking service is configured.");
        }

        if (baseRankedResults.Count == 0)
        {
            return (new List<RetrievalMatch>(), new RerankResult());
        }

        var candidates = baseRankedResults
            .Select((match, index) => BuildRerankCandidate(match, index + 1, options))
            .ToList();

        RerankResult rerankResult;
        try
        {
            rerankResult = await _reranker.RerankAsync(new RerankRequest
            {
                Query = request.Query,
                Limit = request.Limit,
                Options = options,
                Candidates = candidates
            }, ct);

            ValidateRerankResult(rerankResult, candidates);
        }
        catch (Exception ex) when (options.FallbackOnFailure && ex is not OperationCanceledException)
        {
            return BuildRerankFallback(request, options, baseRankedResults, candidates, ex);
        }

        var candidatesById = candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var matchesById = baseRankedResults.ToDictionary(BuildMatchKey, StringComparer.Ordinal);
        var rerankRanks = rerankResult.Items.ToDictionary(item => item.Id, item => item.Rank, StringComparer.Ordinal);
        var reranked = new List<RetrievalMatch>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in rerankResult.Items.OrderBy(item => item.Rank).ThenByDescending(item => item.Score))
        {
            if (!matchesById.TryGetValue(item.Id, out var match) || !candidatesById.TryGetValue(item.Id, out var candidate))
            {
                continue;
            }

            seen.Add(item.Id);
            ApplyRerankScore(match, candidate, item, options, rerankResult, candidates.Count);
            reranked.Add(match);
        }

        foreach (var match in baseRankedResults)
        {
            if (reranked.Count >= request.Limit)
            {
                break;
            }

            var key = BuildMatchKey(match);
            if (seen.Contains(key))
            {
                continue;
            }

            reranked.Add(match);
        }

        var final = reranked
            .OrderByDescending(match => match.Score)
            .ThenBy(match => rerankRanks.TryGetValue(BuildMatchKey(match), out var rank) ? rank : int.MaxValue)
            .ThenBy(match => candidatesById.TryGetValue(BuildMatchKey(match), out var candidate) ? candidate.OriginalRank : int.MaxValue)
            .ThenBy(match => match.Collection)
            .ThenBy(match => match.Record.PartitionKey)
            .ThenBy(match => match.Record.Id)
            .Take(request.Limit)
            .ToList();

        return (final, rerankResult);
    }

    private static (List<RetrievalMatch> Results, RerankResult Result) BuildRerankFallback(
        RetrievalRequest request,
        RerankOptions options,
        IReadOnlyList<RetrievalMatch> baseRankedResults,
        IReadOnlyList<RerankCandidate> candidates,
        Exception exception)
    {
        var providerException = exception as RerankProviderException;
        var provider = providerException?.Provider ?? options.Provider ?? string.Empty;
        var final = baseRankedResults.Take(request.Limit).ToList();
        var result = new RerankResult
        {
            Provider = provider,
            TraceId = providerException?.TraceId,
            InputCandidateCount = providerException?.CandidateCount ?? candidates.Count,
            ProviderPayloadBytes = providerException?.ProviderPayloadBytes,
            ProviderMaxInputBytes = providerException?.ProviderMaxInputBytes,
            FallbackApplied = true,
            FailureClass = providerException?.FailureClass ?? "rerank_failure",
            ProviderStatus = providerException?.ProviderStatus ?? providerException?.Status ?? "exception",
            Error = exception.Message
        };

        StampRerankFallbackDiagnostics(final, result);
        return (final, result);
    }

    private static void ValidateRerankResult(RerankResult result, IReadOnlyList<RerankCandidate> candidates)
    {
        if (result.Items.Count == 0)
        {
            throw new InvalidOperationException("Rerank provider returned no items.");
        }

        if (result.Items.Any(item => item.Rank <= 0))
        {
            throw new InvalidOperationException("Rerank provider returned a non-positive rank.");
        }

        var candidateIds = candidates.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
        var duplicateIds = result.Items
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateIds.Count > 0)
        {
            throw new InvalidOperationException($"Rerank provider returned duplicate candidate ids: {string.Join(", ", duplicateIds)}.");
        }

        var unknownIds = result.Items
            .Where(item => !candidateIds.Contains(item.Id))
            .Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknownIds.Count > 0)
        {
            throw new InvalidOperationException($"Rerank provider returned unknown candidate ids: {string.Join(", ", unknownIds)}.");
        }
    }

    private static RerankCandidate BuildRerankCandidate(RetrievalMatch match, int originalRank, RerankOptions options)
    {
        return new RerankCandidate
        {
            Id = BuildMatchKey(match),
            Text = TrimRerankText(ExtractContentText(match.Record, options.ContentField), options.MaxCandidateChars),
            Collection = match.Collection,
            PartitionKey = match.Record.PartitionKey,
            RecordId = match.Record.Id,
            OriginalRank = originalRank,
            OriginalScore = match.Score,
            Metadata = new JsonObject
            {
                ["collection"] = match.Collection,
                ["partitionKey"] = match.Record.PartitionKey,
                ["recordId"] = match.Record.Id,
                ["type"] = match.Record.Type,
                ["metadata"] = match.Record.Metadata?.DeepClone(),
                ["sources"] = JsonSerializer.SerializeToNode(match.Record.Sources)
            }
        };
    }

    private static void ApplyRerankScore(
        RetrievalMatch match,
        RerankCandidate candidate,
        RerankResultItem item,
        RerankOptions options,
        RerankResult result,
        int rerankInputCount)
    {
        var originalScore = match.Score;
        var totalWeight = options.RerankScoreWeight + options.OriginalScoreWeight;
        match.Score = totalWeight <= 0
            ? item.Score
            : ((options.RerankScoreWeight * item.Score) + (options.OriginalScoreWeight * originalScore)) / totalWeight;

        if (match.Diagnostics is null)
        {
            return;
        }

        if (!match.Diagnostics.CandidateSources.Contains("rerank", StringComparer.Ordinal))
        {
            match.Diagnostics.CandidateSources.Add("rerank");
        }

        match.Diagnostics.ScoreComponents["preRerank"] = originalScore;
        match.Diagnostics.ScoreComponents["rerank"] = item.Score;
        match.Diagnostics.ScoreComponents["rerankScoreWeight"] = options.RerankScoreWeight;
        match.Diagnostics.ScoreComponents["originalScoreWeight"] = options.OriginalScoreWeight;
        match.Diagnostics.ScoreComponents["final"] = match.Score;
        match.Diagnostics.CandidateCounts["rerankInputCandidates"] = rerankInputCount;
        match.Diagnostics.Details["preRerankRank"] = candidate.OriginalRank;
        match.Diagnostics.Details["rerankRank"] = item.Rank;
        match.Diagnostics.Details["rerankProvider"] = result.Provider;
        match.Diagnostics.Details["rerankModel"] = result.ModelId;
        match.Diagnostics.Details["rerankTraceId"] = result.TraceId;
        match.Diagnostics.Details["rerankProviderPayloadBytes"] = result.ProviderPayloadBytes;
        match.Diagnostics.Details["rerankProviderMaxInputBytes"] = result.ProviderMaxInputBytes;

        AddReasonCode(match.Diagnostics, "rerank.applied");
        AddReasonCode(match.Diagnostics, "score.rerank.blended");
        if (!string.IsNullOrWhiteSpace(result.TraceId))
        {
            AddTraceReference(match.Diagnostics, "rerank", "provider.run", result.TraceId, result.Provider, result.ModelId);
        }

        match.Diagnostics.ScoreNormalization ??= new RetrievalScoreNormalization();
        match.Diagnostics.ScoreNormalization.FinalScoreKind = "rerank.weighted_blend";
        match.Diagnostics.ScoreNormalization.Weights["rerank"] = options.RerankScoreWeight;
        match.Diagnostics.ScoreNormalization.Weights["original"] = options.OriginalScoreWeight;
        match.Diagnostics.ScoreNormalization.Parameters["rerankProvider"] = result.Provider;
        match.Diagnostics.ScoreNormalization.Parameters["rerankModel"] = result.ModelId;
        match.Diagnostics.ScoreNormalization.Parameters["rerankRank"] = item.Rank;
    }

    private static void StampRerankFallbackDiagnostics(IReadOnlyList<RetrievalMatch> matches, RerankResult result)
    {
        foreach (var match in matches)
        {
            var diagnostics = match.Diagnostics;
            if (diagnostics is null)
            {
                continue;
            }

            if (!diagnostics.CandidateSources.Contains("rerank", StringComparer.Ordinal))
            {
                diagnostics.CandidateSources.Add("rerank");
            }

            diagnostics.CandidateCounts["rerankInputCandidates"] = result.InputCandidateCount;
            diagnostics.Details["rerankProvider"] = result.Provider;
            diagnostics.Details["rerankTraceId"] = result.TraceId;
            diagnostics.Details["rerankFallbackApplied"] = true;
            diagnostics.Details["rerankFailureClass"] = result.FailureClass;
            diagnostics.Details["rerankProviderStatus"] = result.ProviderStatus;
            diagnostics.Details["rerankProviderPayloadBytes"] = result.ProviderPayloadBytes;
            diagnostics.Details["rerankProviderMaxInputBytes"] = result.ProviderMaxInputBytes;
            diagnostics.Details["rerankError"] = result.Error;
            AddReasonCode(diagnostics, "rerank.fallback");
            AddReasonCode(diagnostics, "rank.pre_rerank_retained");
            if (!string.IsNullOrWhiteSpace(result.TraceId))
            {
                AddTraceReference(diagnostics, "rerank", "provider.run", result.TraceId, result.Provider, result.ModelId);
            }
        }
    }

    private static IEnumerable<RetrievalMatch> SortMatches(IEnumerable<RetrievalMatch> matches)
    {
        return matches
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Collection)
            .ThenBy(r => r.Record.PartitionKey)
            .ThenBy(r => r.Record.Id);
    }

    private static void StampPreRerankDiagnostics(IReadOnlyList<RetrievalMatch> matches)
    {
        for (var index = 0; index < matches.Count; index++)
        {
            var diagnostics = matches[index].Diagnostics;
            if (diagnostics is null)
            {
                continue;
            }

            diagnostics.Details["preRerankRank"] = index + 1;
            diagnostics.ScoreComponents["preRerank"] = matches[index].Score;
            diagnostics.CandidateCounts["preRerankCandidates"] = matches.Count;
            AddReasonCode(diagnostics, "rank.pre_rerank.assigned");
        }
    }

    private static string BuildMatchKey(RetrievalMatch match)
    {
        return $"{match.Collection}\u001f{match.Record.PartitionKey}\u001f{match.Record.Id}";
    }

    private static string ExtractContentText(VyralRecord record, string? preferredContentField)
    {
        if (!string.IsNullOrWhiteSpace(preferredContentField))
        {
            var preferred = NodeToText(record.Content?[preferredContentField]);
            if (!string.IsNullOrEmpty(preferred)) return preferred;
        }

        var text = NodeToText(record.Content?["text"]);
        if (!string.IsNullOrEmpty(text)) return text;

        if (record.Content == null) return string.Empty;

        foreach (var kvp in record.Content)
        {
            var candidate = NodeToText(kvp.Value);
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        }

        return string.Empty;
    }

    private static string NodeToText(JsonNode? node)
    {
        if (node is null) return string.Empty;
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
    }

    private static string TrimRerankText(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars];
    }

    private static string NormalizeHybridFusion(string? fusion)
    {
        if (string.IsNullOrWhiteSpace(fusion))
        {
            return "weighted";
        }

        return fusion.Trim().ToLowerInvariant() switch
        {
            "weighted" => "weighted",
            "rrf" => "rrf",
            "reciprocalrankfusion" => "rrf",
            "reciprocal_rank_fusion" => "rrf",
            "reciprocal-rank-fusion" => "rrf",
            _ => throw new InvalidOperationException($"Hybrid fusion '{fusion}' is not supported.")
        };
    }

    private static void AddTieBreakDiagnostics(RetrievalMatch match)
    {
        if (match.Diagnostics is null)
        {
            return;
        }

        match.Diagnostics.Details["rank"] = match.Rank;
        var reranked = match.Diagnostics.CandidateSources.Contains("rerank", StringComparer.Ordinal);
        match.Diagnostics.Details["tieBreakOrder"] = reranked
            ? "score desc, rerankRank asc, preRerankRank asc, collection asc, partitionKey asc, id asc"
            : "score desc, collection asc, partitionKey asc, id asc";
        match.Diagnostics.Details["tieBreakKey"] = new Dictionary<string, object?>
        {
            ["score"] = match.Score,
            ["collection"] = match.Collection,
            ["partitionKey"] = match.Record.PartitionKey,
            ["id"] = match.Record.Id
        };
        AddReasonCode(match.Diagnostics, "rank.tie_break.applied");
    }

    private static void StampReturnedDiagnostics(IReadOnlyList<RetrievalMatch> matches)
    {
        foreach (var match in matches)
        {
            if (match.Diagnostics is null)
            {
                continue;
            }

            match.Diagnostics.CandidateCounts["returnedCandidates"] = matches.Count;
            match.Diagnostics.ScoreComponents["final"] = match.Score;
            match.Diagnostics.ScoreNormalization ??= new RetrievalScoreNormalization();
            match.Diagnostics.ScoreNormalization.Parameters["rank"] = match.Rank;
            AddReasonCode(match.Diagnostics, "rank.final.assigned");
        }
    }

    private static void StampRetrievalTraceReferences(IEnumerable<RetrievalMatch> matches, string traceId)
    {
        foreach (var match in matches)
        {
            if (match.Diagnostics is null)
            {
                continue;
            }

            AddTraceReference(match.Diagnostics, "retrieval", "retrieval.search", traceId);
        }
    }

    private static void AddReasonCode(RetrievalDiagnostics diagnostics, string reasonCode)
    {
        if (!diagnostics.ReasonCodes.Contains(reasonCode, StringComparer.Ordinal))
        {
            diagnostics.ReasonCodes.Add(reasonCode);
        }
    }

    private static void AddTraceReference(
        RetrievalDiagnostics diagnostics,
        string kind,
        string operation,
        string traceId,
        string? provider = null,
        string? modelId = null)
    {
        if (diagnostics.TraceReferences.Any(reference =>
                string.Equals(reference.Kind, kind, StringComparison.Ordinal) &&
                string.Equals(reference.Operation, operation, StringComparison.Ordinal) &&
                string.Equals(reference.TraceId, traceId, StringComparison.Ordinal)))
        {
            return;
        }

        diagnostics.TraceReferences.Add(new RetrievalTraceReference
        {
            Kind = kind,
            Operation = operation,
            TraceId = traceId,
            Provider = provider,
            ModelId = modelId
        });
    }

    private static string BuildSnippet(VyralRecord record)
    {
        var text = NodeToText(record.Content?["text"]);
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length > 200 ? text[..197] + "..." : text;
    }

    private sealed class RetrievalCandidate
    {
        public string Collection { get; init; } = string.Empty;
        public VyralRecord Record { get; init; } = null!;
        public float Score { get; set; }
        public float? VectorScore { get; set; }
        public float? VectorNormalizedScore { get; set; }
        public int? VectorRank { get; set; }
        public float? LexicalScore { get; set; }
        public int? LexicalRank { get; set; }
        public RetrievalDiagnostics? LexicalDiagnostics { get; set; }
        public string? EmbeddingField { get; set; }
        public string? VectorDistanceFunction { get; set; }
        public int VectorFusionFieldCount { get; set; }
        public bool UsesMultiVectorFusion => VectorFusionFieldCount > 1;
        public Dictionary<string, VectorFieldHit> VectorHits { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CandidateSources { get; } = new(StringComparer.Ordinal);

        public void AddVectorHit(
            string field,
            float rawScore,
            float normalizedScore,
            int rank,
            string distanceFunction,
            float weight,
            RetrievalDiagnostics? diagnostics)
        {
            VectorHits[field] = new VectorFieldHit
            {
                Field = field,
                RawScore = rawScore,
                NormalizedScore = normalizedScore,
                Rank = rank,
                DistanceFunction = distanceFunction,
                Weight = weight,
                VectorIndexProvider = GetDiagnosticsString(diagnostics, "vectorIndexProvider"),
                VectorIndexUsed = GetDiagnosticsBool(diagnostics, "vectorIndexUsed")
                    ?? diagnostics?.ReasonCodes.Contains("index.sqlite_vector", StringComparer.Ordinal),
                VectorIndexTable = GetDiagnosticsString(diagnostics, "vectorIndexTable")
                    ?? GetDiagnosticsString(diagnostics, "vectorIndex"),
                VectorIndexQuantized = GetDiagnosticsBool(diagnostics, "vectorIndexQuantized"),
                VectorIndexReason = GetDiagnosticsString(diagnostics, "vectorIndexReason")
            };
        }

        public void FinalizeVectorScore(IReadOnlyList<ResolvedVectorFieldQuery> resolvedFields)
        {
            if (VectorHits.Count == 0)
            {
                return;
            }

            VectorFusionFieldCount = resolvedFields.Count;
            if (resolvedFields.Count <= 1)
            {
                var hit = VectorHits.Values.First();
                VectorScore = hit.RawScore;
                VectorNormalizedScore = hit.NormalizedScore;
                VectorRank = hit.Rank;
                EmbeddingField = hit.Field;
                VectorDistanceFunction = hit.DistanceFunction;
                return;
            }

            var totalWeight = resolvedFields.Sum(field => field.Weight);
            if (totalWeight <= 0)
            {
                return;
            }

            var weightedScore = 0.0f;
            foreach (var field in resolvedFields)
            {
                if (VectorHits.TryGetValue(field.Field, out var hit))
                {
                    weightedScore += hit.NormalizedScore * field.Weight;
                }
            }

            VectorScore = weightedScore / totalWeight;
            VectorNormalizedScore = VectorScore;
            VectorRank = VectorHits.Values.Min(hit => hit.Rank);
            EmbeddingField = string.Join(",", VectorHits.Keys.OrderBy(field => field, StringComparer.Ordinal));
            VectorDistanceFunction = "multi";
        }

        private static bool? GetDiagnosticsBool(RetrievalDiagnostics? diagnostics, string key)
        {
            if (diagnostics?.Details.TryGetValue(key, out var value) != true || value is null)
            {
                return null;
            }

            return value switch
            {
                bool flag => flag,
                JsonElement json when json.ValueKind == JsonValueKind.True => true,
                JsonElement json when json.ValueKind == JsonValueKind.False => false,
                string text when bool.TryParse(text, out var parsed) => parsed,
                _ => null
            };
        }

        private static string? GetDiagnosticsString(RetrievalDiagnostics? diagnostics, string key)
        {
            if (diagnostics?.Details.TryGetValue(key, out var value) != true || value is null)
            {
                return null;
            }

            return value switch
            {
                string text when !string.IsNullOrWhiteSpace(text) => text,
                JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
                _ => value.ToString()
            };
        }
    }

    private sealed class VectorFieldHit
    {
        public string Field { get; init; } = string.Empty;
        public float RawScore { get; init; }
        public float NormalizedScore { get; init; }
        public int Rank { get; init; }
        public string DistanceFunction { get; init; } = string.Empty;
        public float Weight { get; init; }
        public string? VectorIndexProvider { get; init; }
        public bool? VectorIndexUsed { get; init; }
        public string? VectorIndexTable { get; init; }
        public bool? VectorIndexQuantized { get; init; }
        public string? VectorIndexReason { get; init; }
    }

    private sealed class ResolvedVectorFieldQuery
    {
        public string Field { get; init; } = string.Empty;
        public VectorFieldPolicy Policy { get; init; } = null!;
        public float Weight { get; init; } = 1.0f;
        public string? Query { get; init; }
        public string? EmbeddingPurpose { get; init; }
        public string? QueryPrefix { get; init; }
        public string? PassagePrefix { get; init; }
        public string? SymmetricPrefix { get; init; }
        public int CandidateLimit { get; init; }
        public float? MinScore { get; init; }
    }

    private sealed class PreparedQueryEmbedding
    {
        public string Field { get; init; } = string.Empty;
        public string Query { get; init; } = string.Empty;
        public PreparedEmbeddingText Prepared { get; init; } = null!;
        public float[] Vector { get; init; } = Array.Empty<float>();
    }
}
