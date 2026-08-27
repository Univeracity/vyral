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

public class LocalRetrievalEvaluationService : IRetrievalEvaluationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxCases = 200;
    private const int MaxExpectedPerCase = 100;
    private const int MaxHardNegativesPerCase = 200;
    private const int MaxComparisonVariants = 12;
    private const int MaxK = 100;
    private readonly IRetrievalService _retrievalService;
    private readonly IRetrievalEvaluationTargetResolver? _targetResolver;

    public LocalRetrievalEvaluationService(
        IRetrievalService retrievalService,
        IRetrievalEvaluationTargetResolver? targetResolver = null)
    {
        _retrievalService = retrievalService;
        _targetResolver = targetResolver;
    }

    public async Task<RetrievalEvaluationResult> EvaluateAsync(
        RetrievalEvaluationRequest request,
        CancellationToken ct = default,
        IProgress<RetrievalEvaluationProgress>? progress = null)
    {
        return await EvaluateTargetAsync(_retrievalService, request, ct, progress);
    }

    private static async Task<RetrievalEvaluationResult> EvaluateTargetAsync(
        IRetrievalService retrievalService,
        RetrievalEvaluationRequest request,
        CancellationToken ct,
        IProgress<RetrievalEvaluationProgress>? progress = null)
    {
        ValidateRequest(request);
        ct.ThrowIfCancellationRequested();

        var result = new RetrievalEvaluationResult { Requested = request.Cases.Count };
        ReportEvaluationProgress(progress, result);
        for (var i = 0; i < request.Cases.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var testCase = request.Cases[i];
            ReportEvaluationProgress(progress, result, i, testCase.Name);
            var start = DateTime.UtcNow;
            try
            {
                var caseResult = await EvaluateCaseAsync(retrievalService, request, testCase, i, ct);
                caseResult.DurationMs = (DateTime.UtcNow - start).TotalMilliseconds;
                result.Cases.Add(caseResult);
                result.Succeeded++;
                if (caseResult.Hit) result.HitCount++;
            }
            catch (Exception ex) when (IsCaseFailure(ex))
            {
                result.Cases.Add(new RetrievalEvaluationCaseResult
                {
                    Index = i,
                    Name = testCase.Name,
                    Query = testCase.Request?.Query ?? string.Empty,
                    Status = EvaluationCaseStatuses.Failed,
                    Error = ex.Message,
                    DurationMs = (DateTime.UtcNow - start).TotalMilliseconds,
                    K = ResolveK(request, testCase),
                    ExpectedCount = testCase.Expected?.Count ?? 0,
                    HardNegativeCount = testCase.HardNegatives?.Count ?? 0
                });
                result.Failed++;

                if (!request.ContinueOnError)
                {
                    result.StoppedOnError = i + 1 < request.Cases.Count;
                    result.Attempted = result.Cases.Count;
                    ReportEvaluationProgress(progress, result, i, testCase.Name);
                    break;
                }
            }

            result.Attempted = result.Cases.Count;
            ReportEvaluationProgress(progress, result, i, testCase.Name);
        }

        result.Attempted = result.Cases.Count;
        AddAggregateMetrics(result);
        ReportEvaluationProgress(progress, result);
        return result;
    }

    public async Task<RetrievalEvaluationComparisonResult> CompareAsync(
        RetrievalEvaluationComparisonRequest request,
        CancellationToken ct = default,
        IProgress<RetrievalEvaluationComparisonProgress>? progress = null)
    {
        ValidateComparisonRequest(request);
        ct.ThrowIfCancellationRequested();

        var result = new RetrievalEvaluationComparisonResult
        {
            Requested = request.Cases.Count,
            VariantsRequested = request.Variants.Count,
            BaselineVariantId = request.Variants[0].Id
        };
        ReportComparisonProgress(progress, result);

        RetrievalEvaluationMetrics? baseline = null;
        for (var i = 0; i < request.Variants.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var variant = request.Variants[i];
            ReportComparisonProgress(progress, result, variant.Id, i);
            var start = DateTime.UtcNow;
            try
            {
                var resolvedTarget = ResolveTarget(variant);
                var evaluation = await EvaluateTargetAsync(resolvedTarget.Service, new RetrievalEvaluationRequest
                {
                    Cases = ApplyVariant(request.Cases, variant, ct),
                    ContinueOnError = request.ContinueOnError,
                    DefaultK = request.DefaultK,
                    IncludeTopResults = request.IncludeTopResults
                }, ct);
                if (variant.Target is not null && evaluation.Failed != 0)
                {
                    throw new InvalidOperationException(
                        $"Retrieval evaluation target '{variant.Target.Id}' failed {evaluation.Failed} case(s).");
                }
                var metrics = ToMetrics(evaluation);
                if (i == 0)
                {
                    baseline = metrics;
                }

                result.Variants.Add(new RetrievalEvaluationVariantResult
                {
                    Id = variant.Id,
                    Label = variant.Label,
                    Target = variant.Target is null ? null : resolvedTarget.Evidence,
                    Status = EvaluationVariantStatuses.Succeeded,
                    DurationMs = (DateTime.UtcNow - start).TotalMilliseconds,
                    Metrics = metrics,
                    DeltaFromBaseline = i == 0 || baseline is null ? null : CalculateDelta(metrics, baseline),
                    Cases = request.IncludeCaseResults ? evaluation.Cases : new List<RetrievalEvaluationCaseResult>()
                });
                result.VariantsSucceeded++;
                result.VariantsAttempted = result.Variants.Count;
                ReportComparisonProgress(progress, result, variant.Id, i);
            }
            catch (Exception ex) when (IsCaseFailure(ex))
            {
                result.Variants.Add(new RetrievalEvaluationVariantResult
                {
                    Id = variant.Id,
                    Label = variant.Label,
                    Target = null,
                    Status = EvaluationVariantStatuses.Failed,
                    Error = ex.Message,
                    DurationMs = (DateTime.UtcNow - start).TotalMilliseconds,
                    Metrics = new RetrievalEvaluationMetrics { Requested = request.Cases.Count }
                });
                result.VariantsFailed++;
                result.VariantsAttempted = result.Variants.Count;
                ReportComparisonProgress(progress, result, variant.Id, i);

                if (!request.ContinueOnError)
                {
                    result.StoppedOnError = i + 1 < request.Variants.Count;
                    break;
                }
            }
        }

        result.VariantsAttempted = result.Variants.Count;
        ReportComparisonProgress(progress, result);
        return result;
    }

    private RetrievalEvaluationResolvedTarget ResolveTarget(RetrievalEvaluationVariant variant)
    {
        if (variant.Target is null)
        {
            return new RetrievalEvaluationResolvedTarget
            {
                Service = _retrievalService,
                Evidence = new RetrievalEvaluationTargetEvidence { Id = "default" }
            };
        }
        if (_targetResolver is null)
        {
            throw new InvalidOperationException(
                $"Retrieval evaluation target '{variant.Target.Id}' is not configured.");
        }
        return _targetResolver.Resolve(variant.Target);
    }

    private static void ReportComparisonProgress(
        IProgress<RetrievalEvaluationComparisonProgress>? progress,
        RetrievalEvaluationComparisonResult result,
        string? currentVariantId = null,
        int? currentVariantIndex = null)
    {
        progress?.Report(new RetrievalEvaluationComparisonProgress
        {
            CurrentVariantId = currentVariantId,
            CurrentVariantIndex = currentVariantIndex,
            Requested = result.Requested,
            VariantsRequested = result.VariantsRequested,
            VariantsAttempted = result.VariantsAttempted,
            VariantsSucceeded = result.VariantsSucceeded,
            VariantsFailed = result.VariantsFailed,
            Result = CloneComparisonResult(result)
        });
    }

    private static void ReportEvaluationProgress(
        IProgress<RetrievalEvaluationProgress>? progress,
        RetrievalEvaluationResult result,
        int? currentCaseIndex = null,
        string? currentCaseName = null)
    {
        progress?.Report(new RetrievalEvaluationProgress
        {
            CurrentCaseIndex = currentCaseIndex,
            CurrentCaseName = currentCaseName,
            Requested = result.Requested,
            CasesAttempted = result.Attempted,
            CasesSucceeded = result.Succeeded,
            CasesFailed = result.Failed,
            Result = CloneEvaluationResult(result)
        });
    }

    private static RetrievalEvaluationResult CloneEvaluationResult(RetrievalEvaluationResult result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        return JsonSerializer.Deserialize<RetrievalEvaluationResult>(json, JsonOptions)!;
    }

    private static RetrievalEvaluationComparisonResult CloneComparisonResult(RetrievalEvaluationComparisonResult result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        return JsonSerializer.Deserialize<RetrievalEvaluationComparisonResult>(json, JsonOptions)!;
    }

    private static async Task<RetrievalEvaluationCaseResult> EvaluateCaseAsync(
        IRetrievalService retrievalService,
        RetrievalEvaluationRequest request,
        RetrievalEvaluationCase testCase,
        int index,
        CancellationToken ct)
    {
        ValidateCase(testCase);
        ct.ThrowIfCancellationRequested();

        var k = ResolveK(request, testCase);
        var retrievalRequest = CloneRetrievalRequest(testCase.Request, k, request.IncludeTopResults);
        var retrieval = await retrievalService.SearchAsync(retrievalRequest, ct);
        ct.ThrowIfCancellationRequested();
        var topResults = retrieval.Results.OrderBy(match => match.Rank).Take(k).ToList();
        var rerankEnabled = GetTraceBool(retrieval.Trace, "rerankEnabled") ?? (retrievalRequest.Rerank?.Enabled == true);
        var rerankFallbackApplied = GetTraceBool(retrieval.Trace, "rerankFallbackApplied") ?? false;
        var expected = testCase.Expected
            .Select((item, expectedIndex) => new ExpectedReference(item, expectedIndex))
            .ToList();
        var expectedResults = expected
            .Select(item => new RetrievalEvaluationExpectedResult
            {
                Id = item.Id,
                PartitionKey = item.PartitionKey,
                Collection = item.Collection,
                Relevance = item.Relevance
            })
            .ToList();
        var hardNegatives = testCase.HardNegatives
            .Select((item, hardNegativeIndex) => new HardNegativeReference(item, hardNegativeIndex))
            .ToList();
        var hardNegativeResults = hardNegatives
            .Select(item => new RetrievalEvaluationHardNegativeResult
            {
                Id = item.Id,
                PartitionKey = item.PartitionKey,
                Collection = item.Collection,
                Reason = item.Reason
            })
            .ToList();

        var matchedExpectedIndexes = new HashSet<int>();
        var matchedHardNegativeIndexes = new HashSet<int>();
        var firstRelevantRank = default(int?);
        var firstHardNegativeRank = default(int?);
        var topResultSummaries = new List<RetrievalEvaluationTopResult>();
        double dcg = 0;

        foreach (var match in topResults)
        {
            ct.ThrowIfCancellationRequested();
            var expectedMatch = expected.FirstOrDefault(item => MatchesExpected(match, item));
            var matched = expectedMatch is not null;
            var hardNegativeMatch = hardNegatives.FirstOrDefault(item => MatchesHardNegative(match, item));
            var matchedHardNegative = hardNegativeMatch is not null;
            if (matched)
            {
                matchedExpectedIndexes.Add(expectedMatch!.Index);
                firstRelevantRank ??= match.Rank;
                dcg += expectedMatch.Relevance / Math.Log2(match.Rank + 1);

                var expectedResult = expectedResults[expectedMatch.Index];
                expectedResult.Rank ??= match.Rank;
                expectedResult.Score ??= match.Score;
            }

            if (matchedHardNegative)
            {
                matchedHardNegativeIndexes.Add(hardNegativeMatch!.Index);
                firstHardNegativeRank ??= match.Rank;

                var hardNegativeResult = hardNegativeResults[hardNegativeMatch.Index];
                hardNegativeResult.Rank ??= match.Rank;
                hardNegativeResult.Score ??= match.Score;
            }

            if (request.IncludeTopResults)
            {
                topResultSummaries.Add(new RetrievalEvaluationTopResult
                {
                    Rank = match.Rank,
                    Score = match.Score,
                    Collection = match.Collection,
                    Id = match.Record.Id,
                    PartitionKey = match.Record.PartitionKey,
                    Type = string.IsNullOrWhiteSpace(match.Record.Type) ? null : match.Record.Type,
                    MatchedExpected = matched,
                    MatchedHardNegative = matchedHardNegative,
                    RerankFallbackApplied = GetDiagnosticsBool(match.Diagnostics, "rerankFallbackApplied") ?? rerankFallbackApplied,
                    RerankProviderStatus = GetDiagnosticsString(match.Diagnostics, "rerankProviderStatus"),
                    VectorIndexUsed = GetDiagnosticsBool(match.Diagnostics, "vectorIndexUsed")
                        ?? match.Diagnostics?.ReasonCodes.Contains("index.sqlite_vector", StringComparer.Ordinal) == true,
                    VectorIndexProvider = GetDiagnosticsString(match.Diagnostics, "vectorIndexProvider"),
                    VectorIndexFields = GetDiagnosticsStringList(match.Diagnostics, "vectorIndexFields"),
                    Snippet = match.Snippet
                });
            }
        }

        var idealDcg = CalculateIdealDcg(expected, k);
        var matchedCount = matchedExpectedIndexes.Count;
        var hardNegativeMatchedCount = matchedHardNegativeIndexes.Count;
        return new RetrievalEvaluationCaseResult
        {
            Index = index,
            Name = testCase.Name,
            Query = retrievalRequest.Query,
            Status = EvaluationCaseStatuses.Succeeded,
            K = k,
            ExpectedCount = expected.Count,
            RetrievedCount = topResults.Count,
            MatchedCount = matchedCount,
            Hit = matchedCount > 0,
            FirstRelevantRank = firstRelevantRank,
            ReciprocalRank = firstRelevantRank.HasValue ? 1.0 / firstRelevantRank.Value : 0,
            PrecisionAtK = matchedCount / (double)k,
            RecallAtK = expected.Count == 0 ? 0 : matchedCount / (double)expected.Count,
            NdcgAtK = idealDcg == 0 ? 0 : dcg / idealDcg,
            HardNegativeCount = hardNegatives.Count,
            HardNegativeMatchedCount = hardNegativeMatchedCount,
            HardNegativeHit = hardNegativeMatchedCount > 0,
            FirstHardNegativeRank = firstHardNegativeRank,
            HardNegativeRateAtK = hardNegativeMatchedCount / (double)k,
            RerankEnabled = rerankEnabled,
            RerankProvider = GetTraceString(retrieval.Trace, "rerankProvider"),
            RerankTraceId = GetTraceString(retrieval.Trace, "rerankTraceId"),
            RerankFallbackApplied = rerankFallbackApplied,
            RerankFailureClass = GetTraceString(retrieval.Trace, "rerankFailureClass"),
            RerankProviderStatus = GetTraceString(retrieval.Trace, "rerankProviderStatus"),
            RerankInputCandidateCount = GetTraceInt(retrieval.Trace, "rerankInputCandidateCount") ?? 0,
            RerankProviderPayloadBytes = GetTraceInt(retrieval.Trace, "rerankProviderPayloadBytes") ?? 0,
            RerankProviderMaxInputBytes = GetTraceInt(retrieval.Trace, "rerankProviderMaxInputBytes") ?? 0,
            Expected = expectedResults,
            HardNegatives = hardNegativeResults,
            TopResults = topResultSummaries
        };
    }

    private static List<RetrievalEvaluationCase> ApplyVariant(
        IReadOnlyList<RetrievalEvaluationCase> cases,
        RetrievalEvaluationVariant variant,
        CancellationToken ct)
    {
        var applied = new List<RetrievalEvaluationCase>(cases.Count);
        foreach (var testCase in cases)
        {
            ct.ThrowIfCancellationRequested();
            applied.Add(ApplyVariant(testCase, variant));
        }

        return applied;
    }

    private static RetrievalRequest CloneRetrievalRequest(RetrievalRequest source, int k, bool includeTopResultDiagnostics)
    {
        return new RetrievalRequest
        {
            Profile = source.Profile,
            Query = source.Query,
            Collections = source.Collections.ToList(),
            PartitionKeys = source.PartitionKeys?.ToList(),
            Filter = source.Filter,
            Embedding = source.Embedding,
            VectorFields = source.VectorFields?.Select(CloneVectorFieldQuery).ToList(),
            SearchMode = source.SearchMode,
            Lexical = source.Lexical,
            Hybrid = source.Hybrid,
            Rerank = source.Rerank,
            Limit = Math.Max(source.Limit, k),
            MinScore = source.MinScore,
            IncludeTrace = source.IncludeTrace || source.Rerank?.Enabled == true || includeTopResultDiagnostics
        };
    }

    private static RetrievalEvaluationCase ApplyVariant(RetrievalEvaluationCase source, RetrievalEvaluationVariant variant)
    {
        return new RetrievalEvaluationCase
        {
            Name = source.Name,
            Request = source.Request is null ? new RetrievalRequest() : ApplyVariant(source.Request, variant),
            Expected = (source.Expected ?? new List<RetrievalEvaluationExpectedMatch>())
                .Select(item => new RetrievalEvaluationExpectedMatch
                {
                    Id = item.Id,
                    PartitionKey = item.PartitionKey,
                    Collection = item.Collection,
                    Aliases = item.Aliases?.ToList() ?? new List<string>(),
                    SourceIds = item.SourceIds?.ToList() ?? new List<string>(),
                    Sources = item.Sources?.Select(CloneSourceReference).ToList() ?? new List<VyralSourceReference>(),
                    Relevance = item.Relevance
                })
                .ToList(),
            HardNegatives = (source.HardNegatives ?? new List<RetrievalEvaluationHardNegativeMatch>())
                .Select(item => new RetrievalEvaluationHardNegativeMatch
                {
                    Id = item.Id,
                    PartitionKey = item.PartitionKey,
                    Collection = item.Collection,
                    Aliases = item.Aliases?.ToList() ?? new List<string>(),
                    SourceIds = item.SourceIds?.ToList() ?? new List<string>(),
                    Sources = item.Sources?.Select(CloneSourceReference).ToList() ?? new List<VyralSourceReference>(),
                    Reason = item.Reason
                })
                .ToList(),
            K = source.K,
            Metadata = source.Metadata
        };
    }

    private static RetrievalRequest ApplyVariant(RetrievalRequest source, RetrievalEvaluationVariant variant)
    {
        var request = new RetrievalRequest
        {
            Profile = source.Profile,
            Query = source.Query,
            Collections = source.Collections?.ToList() ?? new List<string>(),
            PartitionKeys = source.PartitionKeys?.ToList(),
            Filter = source.Filter,
            Embedding = source.Embedding,
            VectorFields = source.VectorFields?.Select(CloneVectorFieldQuery).ToList(),
            SearchMode = source.SearchMode,
            Lexical = source.Lexical,
            Hybrid = source.Hybrid,
            Rerank = source.Rerank,
            Limit = source.Limit,
            MinScore = source.MinScore,
            IncludeTrace = source.IncludeTrace
        };

        if (variant.Profile is not null) request.Profile = variant.Profile;
        if (variant.Collections is not null) request.Collections = variant.Collections.ToList();
        if (variant.PartitionKeys is not null) request.PartitionKeys = variant.PartitionKeys.ToList();
        if (variant.Filter is not null) request.Filter = variant.Filter;
        if (variant.Embedding is not null) request.Embedding = variant.Embedding;
        if (variant.VectorFields is not null) request.VectorFields = variant.VectorFields.Select(CloneVectorFieldQuery).ToList();
        if (variant.SearchMode is not null) request.SearchMode = variant.SearchMode;
        if (variant.Lexical is not null) request.Lexical = variant.Lexical;
        if (variant.Hybrid is not null) request.Hybrid = variant.Hybrid;
        if (variant.Rerank is not null) request.Rerank = variant.Rerank;
        if (variant.Limit.HasValue) request.Limit = variant.Limit.Value;
        if (variant.MinScore.HasValue) request.MinScore = variant.MinScore.Value;
        if (variant.IncludeTrace.HasValue) request.IncludeTrace = variant.IncludeTrace.Value;

        return request;
    }

    private static RetrievalVectorFieldQuery CloneVectorFieldQuery(RetrievalVectorFieldQuery source)
    {
        return new RetrievalVectorFieldQuery
        {
            Field = source.Field,
            Weight = source.Weight,
            Query = source.Query,
            Embedding = source.Embedding,
            CandidateLimit = source.CandidateLimit,
            MinScore = source.MinScore
        };
    }

    private static VyralSourceReference CloneSourceReference(VyralSourceReference source)
    {
        return new VyralSourceReference
        {
            Id = source.Id,
            Kind = source.Kind,
            Uri = source.Uri,
            Label = source.Label,
            Span = source.Span is null
                ? null
                : new VyralSourceSpan
                {
                    CharStart = source.Span.CharStart,
                    CharEnd = source.Span.CharEnd,
                    Line = source.Span.Line,
                    Column = source.Span.Column,
                    Anchor = source.Span.Anchor
                }
        };
    }

    private static bool MatchesExpected(RetrievalMatch match, ExpectedReference expected)
    {
        return ReferenceScopeMatches(match, expected.Collection, expected.PartitionKey) &&
               RecordMatchesReference(match.Record, expected.ReferenceIds, expected.Sources);
    }

    private static bool MatchesHardNegative(RetrievalMatch match, HardNegativeReference hardNegative)
    {
        return ReferenceScopeMatches(match, hardNegative.Collection, hardNegative.PartitionKey) &&
               RecordMatchesReference(match.Record, hardNegative.ReferenceIds, hardNegative.Sources);
    }

    private static bool ReferenceScopeMatches(RetrievalMatch match, string? collection, string? partitionKey)
    {
        if (!string.IsNullOrWhiteSpace(collection) &&
            !string.Equals(match.Collection, collection, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(partitionKey) ||
               string.Equals(match.Record.PartitionKey, partitionKey, StringComparison.Ordinal);
    }

    private static bool RecordMatchesReference(
        VyralRecord record,
        IReadOnlyCollection<string> referenceIds,
        IReadOnlyCollection<VyralSourceReference> sourceReferences)
    {
        if (referenceIds.Count > 0)
        {
            var recordIds = BuildRecordReferenceIds(record);
            if (referenceIds.Any(id => recordIds.Contains(id)))
            {
                return true;
            }
        }

        return sourceReferences.Count > 0 &&
               record.Sources?.Any(candidate => sourceReferences.Any(expected => SourceReferenceMatches(candidate, expected))) == true;
    }

    private static HashSet<string> BuildRecordReferenceIds(VyralRecord record)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        AddIfPresent(ids, record.Id);
        foreach (var source in record.Sources ?? Enumerable.Empty<VyralSourceReference>())
        {
            AddIfPresent(ids, source.Id);
        }

        AddMetadataStringValues(ids, record.Metadata, "alias", "aliases", "aliasId", "aliasIds",
            "containedId", "containedIds", "containedRecordId", "containedRecordIds",
            "sourceId", "sourceIds", "verseId", "verseIds");
        return ids;
    }

    private static HashSet<string> BuildEvaluationReferenceIds(
        string id,
        IReadOnlyCollection<string>? aliases,
        IReadOnlyCollection<string>? sourceIds,
        IReadOnlyCollection<VyralSourceReference>? sources)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        AddIfPresent(ids, id);
        AddStringValues(ids, aliases);
        AddStringValues(ids, sourceIds);
        foreach (var source in sources ?? Enumerable.Empty<VyralSourceReference>())
        {
            AddIfPresent(ids, source.Id);
        }

        return ids;
    }

    private static void AddMetadataStringValues(HashSet<string> ids, JsonObject? metadata, params string[] keys)
    {
        if (metadata is null)
        {
            return;
        }

        foreach (var key in keys)
        {
            if (!metadata.TryGetPropertyValue(key, out var node) || node is null)
            {
                continue;
            }

            AddJsonStringValues(ids, node);
        }
    }

    private static void AddJsonStringValues(HashSet<string> ids, JsonNode node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        AddJsonStringValues(ids, item);
                    }
                }
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                AddIfPresent(ids, text);
                break;
        }
    }

    private static void AddStringValues(HashSet<string> ids, IEnumerable<string>? values)
    {
        foreach (var value in values ?? Enumerable.Empty<string>())
        {
            AddIfPresent(ids, value);
        }
    }

    private static void AddIfPresent(HashSet<string> ids, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            ids.Add(value);
        }
    }

    private static bool SourceReferenceMatches(VyralSourceReference candidate, VyralSourceReference expected)
    {
        if (!SourceIdentityMatches(candidate, expected))
        {
            return false;
        }

        return SourceSpanContains(candidate.Span, expected.Span);
    }

    private static bool SourceIdentityMatches(VyralSourceReference candidate, VyralSourceReference expected)
    {
        var compared = false;
        if (!string.IsNullOrWhiteSpace(expected.Id))
        {
            compared = true;
            if (!string.Equals(candidate.Id, expected.Id, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(expected.Kind))
        {
            compared = true;
            if (!string.Equals(candidate.Kind, expected.Kind, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(expected.Uri))
        {
            compared = true;
            if (!string.Equals(candidate.Uri, expected.Uri, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return compared;
    }

    private static bool SourceSpanContains(VyralSourceSpan? candidate, VyralSourceSpan? expected)
    {
        if (expected is null)
        {
            return true;
        }

        if (candidate is null)
        {
            return false;
        }

        if (expected.CharStart.HasValue || expected.CharEnd.HasValue)
        {
            if (expected.CharStart.HasValue &&
                (!candidate.CharStart.HasValue || candidate.CharStart.Value > expected.CharStart.Value))
            {
                return false;
            }

            if (expected.CharEnd.HasValue &&
                (!candidate.CharEnd.HasValue || candidate.CharEnd.Value < expected.CharEnd.Value))
            {
                return false;
            }
        }

        if (expected.Line.HasValue && candidate.Line != expected.Line)
        {
            return false;
        }

        if (expected.Column.HasValue && candidate.Column != expected.Column)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(expected.Anchor) ||
               string.Equals(candidate.Anchor, expected.Anchor, StringComparison.Ordinal);
    }

    private static double CalculateIdealDcg(IReadOnlyCollection<ExpectedReference> expected, int k)
    {
        return expected
            .OrderByDescending(item => item.Relevance)
            .Take(k)
            .Select((item, index) => item.Relevance / Math.Log2(index + 2))
            .Sum();
    }

    private static void AddAggregateMetrics(RetrievalEvaluationResult result)
    {
        var succeeded = result.Cases.Where(testCase => testCase.Status == "succeeded").ToList();
        if (succeeded.Count == 0)
        {
            return;
        }

        result.HitRate = result.HitCount / (double)succeeded.Count;
        result.MeanReciprocalRank = succeeded.Average(testCase => testCase.ReciprocalRank);
        result.MeanPrecisionAtK = succeeded.Average(testCase => testCase.PrecisionAtK);
        result.MeanRecallAtK = succeeded.Average(testCase => testCase.RecallAtK);
        result.MeanNdcgAtK = succeeded.Average(testCase => testCase.NdcgAtK);

        var hardNegativeCases = succeeded.Where(testCase => testCase.HardNegativeCount > 0).ToList();
        result.HardNegativeCaseCount = hardNegativeCases.Count;
        if (hardNegativeCases.Count > 0)
        {
            result.HardNegativeHitCount = hardNegativeCases.Count(testCase => testCase.HardNegativeHit);
            result.HardNegativeHitRate = result.HardNegativeHitCount / (double)hardNegativeCases.Count;
            result.MeanHardNegativeRateAtK = hardNegativeCases.Average(testCase => testCase.HardNegativeRateAtK);
        }

        var rerankCases = succeeded.Where(testCase => testCase.RerankEnabled).ToList();
        result.RerankCaseCount = rerankCases.Count;
        if (rerankCases.Count > 0)
        {
            result.RerankFallbackCaseCount = rerankCases.Count(testCase => testCase.RerankFallbackApplied);
            result.RerankFallbackRate = result.RerankFallbackCaseCount / (double)rerankCases.Count;
        }
    }

    private static RetrievalEvaluationMetrics ToMetrics(RetrievalEvaluationResult result)
    {
        return new RetrievalEvaluationMetrics
        {
            Requested = result.Requested,
            Attempted = result.Attempted,
            Succeeded = result.Succeeded,
            Failed = result.Failed,
            StoppedOnError = result.StoppedOnError,
            HitCount = result.HitCount,
            HitRate = result.HitRate,
            MeanReciprocalRank = result.MeanReciprocalRank,
            MeanPrecisionAtK = result.MeanPrecisionAtK,
            MeanRecallAtK = result.MeanRecallAtK,
            MeanNdcgAtK = result.MeanNdcgAtK,
            HardNegativeCaseCount = result.HardNegativeCaseCount,
            HardNegativeHitCount = result.HardNegativeHitCount,
            HardNegativeHitRate = result.HardNegativeHitRate,
            MeanHardNegativeRateAtK = result.MeanHardNegativeRateAtK,
            RerankCaseCount = result.RerankCaseCount,
            RerankFallbackCaseCount = result.RerankFallbackCaseCount,
            RerankFallbackRate = result.RerankFallbackRate
        };
    }

    private static RetrievalEvaluationMetricDeltas CalculateDelta(
        RetrievalEvaluationMetrics metrics,
        RetrievalEvaluationMetrics baseline)
    {
        return new RetrievalEvaluationMetricDeltas
        {
            HitRate = metrics.HitRate - baseline.HitRate,
            MeanReciprocalRank = metrics.MeanReciprocalRank - baseline.MeanReciprocalRank,
            MeanPrecisionAtK = metrics.MeanPrecisionAtK - baseline.MeanPrecisionAtK,
            MeanRecallAtK = metrics.MeanRecallAtK - baseline.MeanRecallAtK,
            MeanNdcgAtK = metrics.MeanNdcgAtK - baseline.MeanNdcgAtK,
            HardNegativeHitRate = metrics.HardNegativeHitRate - baseline.HardNegativeHitRate,
            MeanHardNegativeRateAtK = metrics.MeanHardNegativeRateAtK - baseline.MeanHardNegativeRateAtK,
            RerankFallbackRate = metrics.RerankFallbackRate - baseline.RerankFallbackRate
        };
    }

    private static bool? GetTraceBool(JsonObject? trace, string key)
    {
        var node = trace?[key];
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    private static string? GetTraceString(JsonObject? trace, string key)
    {
        var node = trace?[key];
        if (node is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    }

    private static int? GetTraceInt(JsonObject? trace, string key)
    {
        var node = trace?[key];
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    private static bool? GetDiagnosticsBool(RetrievalDiagnostics? diagnostics, string key)
    {
        var node = diagnostics?.Details.GetValueOrDefault(key);
        if (node is null) return null;
        return node switch
        {
            bool flag => flag,
            System.Text.Json.JsonElement json when json.ValueKind is System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonElement json when json.ValueKind is System.Text.Json.JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? GetDiagnosticsString(RetrievalDiagnostics? diagnostics, string key)
    {
        var node = diagnostics?.Details.GetValueOrDefault(key);
        return node switch
        {
            null => null,
            string text when !string.IsNullOrWhiteSpace(text) => text,
            System.Text.Json.JsonElement json when json.ValueKind == System.Text.Json.JsonValueKind.String => json.GetString(),
            _ => node?.ToString()
        };
    }

    private static List<string> GetDiagnosticsStringList(RetrievalDiagnostics? diagnostics, string key)
    {
        var node = diagnostics?.Details.GetValueOrDefault(key);
        return node switch
        {
            null => new List<string>(),
            IEnumerable<string> strings => strings.Where(item => !string.IsNullOrWhiteSpace(item)).ToList(),
            IEnumerable<object> objects => objects
                .Select(item => item?.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList(),
            System.Text.Json.JsonElement json when json.ValueKind == System.Text.Json.JsonValueKind.Array => json
                .EnumerateArray()
                .Select(item => item.ValueKind == System.Text.Json.JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList(),
            _ => new List<string>()
        };
    }

    private static int ResolveK(RetrievalEvaluationRequest request, RetrievalEvaluationCase testCase)
    {
        var k = testCase.K ?? request.DefaultK ?? testCase.Request?.Limit ?? 10;
        return Math.Clamp(k, 1, MaxK);
    }

    private static void ValidateRequest(RetrievalEvaluationRequest request)
    {
        if (request.Cases is null || request.Cases.Count == 0)
        {
            throw new InvalidOperationException("Retrieval evaluation request must include at least one case.");
        }

        if (request.Cases.Count > MaxCases)
        {
            throw new InvalidOperationException($"Retrieval evaluation request supports at most {MaxCases} cases.");
        }

        if (request.DefaultK is <= 0 or > MaxK)
        {
            throw new InvalidOperationException($"Retrieval evaluation defaultK must be between 1 and {MaxK}.");
        }
    }

    private static void ValidateComparisonRequest(RetrievalEvaluationComparisonRequest request)
    {
        if (request.Cases is null || request.Cases.Count == 0)
        {
            throw new InvalidOperationException("Retrieval evaluation comparison request must include at least one case.");
        }

        if (request.Cases.Count > MaxCases)
        {
            throw new InvalidOperationException($"Retrieval evaluation comparison request supports at most {MaxCases} cases.");
        }

        if (request.Variants is null || request.Variants.Count == 0)
        {
            throw new InvalidOperationException("Retrieval evaluation comparison request must include at least one variant.");
        }

        if (request.Variants.Count > MaxComparisonVariants)
        {
            throw new InvalidOperationException($"Retrieval evaluation comparison request supports at most {MaxComparisonVariants} variants.");
        }

        if (request.DefaultK is <= 0 or > MaxK)
        {
            throw new InvalidOperationException($"Retrieval evaluation comparison defaultK must be between 1 and {MaxK}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in request.Variants)
        {
            if (string.IsNullOrWhiteSpace(variant.Id))
            {
                throw new InvalidOperationException("Retrieval evaluation comparison variant id is required.");
            }

            if (!ids.Add(variant.Id))
            {
                throw new InvalidOperationException($"Retrieval evaluation comparison variant id '{variant.Id}' is duplicated.");
            }

            if (variant.Limit is <= 0)
            {
                throw new InvalidOperationException("Retrieval evaluation comparison variant limit must be greater than zero when provided.");
            }

            ValidateTargetReference(variant.Target);
        }
    }

    private static void ValidateTargetReference(RetrievalEvaluationTargetReference? target)
    {
        if (target is null)
        {
            return;
        }
        ValidateTargetIdentifier(target.Id, "target id");
        if (target.GenerationId is not null)
        {
            ValidateTargetIdentifier(target.GenerationId, "target generationId");
        }
        if (target.ExpectedGenerationDescriptorDigest is { } digest &&
            (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
             digest[7..].Any(character =>
                 !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))))
        {
            throw new InvalidOperationException(
                "Retrieval evaluation target expectedGenerationDescriptorDigest must be a lowercase SHA-256 digest.");
        }
    }

    private static void ValidateTargetIdentifier(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Retrieval evaluation {label} must be a bounded non-whitespace identifier.");
        }
    }

    private static void ValidateCase(RetrievalEvaluationCase testCase)
    {
        if (testCase.Request is null)
        {
            throw new InvalidOperationException("Retrieval evaluation case request is required.");
        }

        if (testCase.Expected is null || testCase.Expected.Count == 0)
        {
            throw new InvalidOperationException("Retrieval evaluation case must include at least one expected match.");
        }

        if (testCase.Expected.Count > MaxExpectedPerCase)
        {
            throw new InvalidOperationException($"Retrieval evaluation case supports at most {MaxExpectedPerCase} expected matches.");
        }

        if (testCase.HardNegatives is null)
        {
            testCase.HardNegatives = new List<RetrievalEvaluationHardNegativeMatch>();
        }

        if (testCase.HardNegatives.Count > MaxHardNegativesPerCase)
        {
            throw new InvalidOperationException($"Retrieval evaluation case supports at most {MaxHardNegativesPerCase} hard-negative matches.");
        }

        if (testCase.K is <= 0 or > MaxK)
        {
            throw new InvalidOperationException($"Retrieval evaluation case k must be between 1 and {MaxK}.");
        }

        foreach (var expected in testCase.Expected)
        {
            if (!HasAnyMatchIdentifier(expected.Id, expected.Aliases, expected.SourceIds, expected.Sources))
            {
                throw new InvalidOperationException("Retrieval evaluation expected match must include id, aliases, sourceIds, or sources.");
            }

            if (expected.Relevance <= 0)
            {
                throw new InvalidOperationException("Retrieval evaluation expected match relevance must be greater than zero.");
            }
        }

        foreach (var hardNegative in testCase.HardNegatives)
        {
            if (!HasAnyMatchIdentifier(hardNegative.Id, hardNegative.Aliases, hardNegative.SourceIds, hardNegative.Sources))
            {
                throw new InvalidOperationException("Retrieval evaluation hard-negative match must include id, aliases, sourceIds, or sources.");
            }

            var overlapsExpected = testCase.Expected.Any(expected => ReferencesOverlap(expected, hardNegative));
            if (overlapsExpected)
            {
                throw new InvalidOperationException($"Retrieval evaluation hard-negative match '{hardNegative.Id}' overlaps an expected match.");
            }
        }
    }

    private static bool HasAnyMatchIdentifier(
        string id,
        IReadOnlyCollection<string>? aliases,
        IReadOnlyCollection<string>? sourceIds,
        IReadOnlyCollection<VyralSourceReference>? sources)
    {
        return !string.IsNullOrWhiteSpace(id) ||
               aliases?.Any(alias => !string.IsNullOrWhiteSpace(alias)) == true ||
               sourceIds?.Any(sourceId => !string.IsNullOrWhiteSpace(sourceId)) == true ||
               sources?.Any(HasSourceIdentity) == true;
    }

    private static bool HasSourceIdentity(VyralSourceReference source)
    {
        return !string.IsNullOrWhiteSpace(source.Id) ||
               !string.IsNullOrWhiteSpace(source.Kind) ||
               !string.IsNullOrWhiteSpace(source.Uri);
    }

    private static bool ReferencesOverlap(RetrievalEvaluationExpectedMatch expected, RetrievalEvaluationHardNegativeMatch hardNegative)
    {
        if (!OptionalReferencesOverlap(expected.Collection, hardNegative.Collection) ||
            !OptionalReferencesOverlap(expected.PartitionKey, hardNegative.PartitionKey))
        {
            return false;
        }

        var expectedIds = BuildEvaluationReferenceIds(expected.Id, expected.Aliases, expected.SourceIds, expected.Sources);
        var hardNegativeIds = BuildEvaluationReferenceIds(hardNegative.Id, hardNegative.Aliases, hardNegative.SourceIds, hardNegative.Sources);
        if (expectedIds.Any(id => hardNegativeIds.Contains(id)))
        {
            return true;
        }

        return expected.Sources?.Any(expectedSource =>
            hardNegative.Sources?.Any(hardNegativeSource =>
                SourceReferencesOverlap(expectedSource, hardNegativeSource)) == true) == true;
    }

    private static bool OptionalReferencesOverlap(string? first, string? second)
    {
        return string.IsNullOrWhiteSpace(first) ||
               string.IsNullOrWhiteSpace(second) ||
               string.Equals(first, second, StringComparison.Ordinal);
    }

    private static bool SourceReferencesOverlap(VyralSourceReference first, VyralSourceReference second)
    {
        if (!SourceIdentitiesOverlap(first, second))
        {
            return false;
        }

        return SourceSpansOverlap(first.Span, second.Span);
    }

    private static bool SourceIdentitiesOverlap(VyralSourceReference first, VyralSourceReference second)
    {
        var compared = false;
        if (!string.IsNullOrWhiteSpace(first.Id) && !string.IsNullOrWhiteSpace(second.Id))
        {
            compared = true;
            if (!string.Equals(first.Id, second.Id, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(first.Kind) && !string.IsNullOrWhiteSpace(second.Kind))
        {
            compared = true;
            if (!string.Equals(first.Kind, second.Kind, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(first.Uri) && !string.IsNullOrWhiteSpace(second.Uri))
        {
            compared = true;
            if (!string.Equals(first.Uri, second.Uri, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return compared;
    }

    private static bool SourceSpansOverlap(VyralSourceSpan? first, VyralSourceSpan? second)
    {
        if (first is null || second is null)
        {
            return true;
        }

        if (first.CharStart.HasValue && first.CharEnd.HasValue &&
            second.CharStart.HasValue && second.CharEnd.HasValue)
        {
            return first.CharStart.Value < second.CharEnd.Value &&
                   second.CharStart.Value < first.CharEnd.Value;
        }

        if (first.Line.HasValue && second.Line.HasValue)
        {
            return first.Line == second.Line;
        }

        return string.IsNullOrWhiteSpace(first.Anchor) ||
               string.IsNullOrWhiteSpace(second.Anchor) ||
               string.Equals(first.Anchor, second.Anchor, StringComparison.Ordinal);
    }

    private static bool IsCaseFailure(Exception exception)
    {
        return exception is ArgumentException or InvalidOperationException or NotSupportedException;
    }

    private sealed class ExpectedReference
    {
        public ExpectedReference(RetrievalEvaluationExpectedMatch expected, int index)
        {
            Index = index;
            Id = expected.Id;
            PartitionKey = expected.PartitionKey;
            Collection = expected.Collection;
            Sources = expected.Sources ?? new List<VyralSourceReference>();
            ReferenceIds = BuildEvaluationReferenceIds(expected.Id, expected.Aliases, expected.SourceIds, Sources);
            Relevance = expected.Relevance;
        }

        public int Index { get; }

        public string Id { get; }

        public string? PartitionKey { get; }

        public string? Collection { get; }

        public IReadOnlyCollection<string> ReferenceIds { get; }

        public IReadOnlyCollection<VyralSourceReference> Sources { get; }

        public double Relevance { get; }
    }

    private sealed class HardNegativeReference
    {
        public HardNegativeReference(RetrievalEvaluationHardNegativeMatch hardNegative, int index)
        {
            Index = index;
            Id = hardNegative.Id;
            PartitionKey = hardNegative.PartitionKey;
            Collection = hardNegative.Collection;
            Sources = hardNegative.Sources ?? new List<VyralSourceReference>();
            ReferenceIds = BuildEvaluationReferenceIds(hardNegative.Id, hardNegative.Aliases, hardNegative.SourceIds, Sources);
            Reason = hardNegative.Reason;
        }

        public int Index { get; }

        public string Id { get; }

        public string? PartitionKey { get; }

        public string? Collection { get; }

        public IReadOnlyCollection<string> ReferenceIds { get; }

        public IReadOnlyCollection<VyralSourceReference> Sources { get; }

        public string? Reason { get; }
    }
}
