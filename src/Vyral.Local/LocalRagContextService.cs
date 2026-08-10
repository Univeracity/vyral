using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public class LocalRagContextService : IRagContextService
{
    private const string ContextTextFormat = ContextTextFormats.CitationMarkdown;
    private const int MaxEvaluationCases = 100;
    private const int MaxExpectedGraphItemsPerCase = 200;
    private const int MaxGraphSeedDiagnostics = 200;
    private readonly IRetrievalService _retrievalService;
    private readonly IRecordCollectionStore? _recordStore;

    public LocalRagContextService(IRetrievalService retrievalService, IRecordCollectionStore? recordStore = null)
    {
        _retrievalService = retrievalService;
        _recordStore = recordStore;
    }

    public async Task<RagContextEnvelope> BuildContextAsync(RagContextRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);
        var assemblyPlan = BuildAssemblyPlan(request);
        var r = request.Retrieval;

        var retrieval = await _retrievalService.SearchAsync(new RetrievalRequest
        {
            Profile = r.Profile,
            Query = r.Query,
            Collections = r.Collections,
            PartitionKeys = r.PartitionKeys,
            Filter = r.Filter,
            Embedding = r.Embedding == null
                ? null
                : new EmbeddingOptions
                {
                    Field = r.Embedding.Field,
                    Purpose = string.IsNullOrWhiteSpace(r.Embedding.Purpose) ? EmbeddingPurposes.Query : r.Embedding.Purpose,
                    QueryPrefix = r.Embedding.QueryPrefix,
                    PassagePrefix = r.Embedding.PassagePrefix,
                    SymmetricPrefix = r.Embedding.SymmetricPrefix
                },
            VectorFields = r.VectorFields,
            SearchMode = r.SearchMode,
            Lexical = r.Lexical,
            Hybrid = r.Hybrid,
            Rerank = BuildRetrievalRerankOptions(r, request.ContentField),
            Limit = r.Limit,
            MinScore = r.MinScore,
            IncludeTrace = request.IncludeTrace || r.IncludeTrace
        }, ct);
        var effectiveSearchMode = GetTraceString(retrieval.Trace, "searchMode") ?? r.SearchMode ?? string.Empty;

        var chunks = new List<RagContextChunk>();
        var citations = new List<RagContextCitation>();
        var candidates = new List<ContextCandidate>();
        var assembleCitations = request.IncludeCitations || request.IncludeContextText;
        var remainingChars = request.MaxChars;
        var totalChars = 0;
        var skippedEmptyText = 0;
        var skippedForGroupBudget = 0;
        var droppedForBudget = 0;
        var omittedCitationCount = 0;
        var groupStats = new Dictionary<string, ContextGroupStats>(StringComparer.Ordinal);

        foreach (var match in retrieval.Results)
        {
            var text = ExtractText(match.Record, request.ContentField);
            if (string.IsNullOrWhiteSpace(text))
            {
                skippedEmptyText++;
                continue;
            }

            var groupKey = ResolveGroupKey(match, assemblyPlan);
            if (groupKey is not null)
            {
                var stats = GetOrCreateGroupStats(groupStats, groupKey);
                stats.CandidateCount++;
            }

            candidates.Add(new ContextCandidate(match, text, groupKey));
        }

        foreach (var candidate in OrderCandidates(candidates, assemblyPlan, request.MaxCharsPerChunk))
        {
            if (remainingChars <= 0)
            {
                droppedForBudget++;
                break;
            }

            var match = candidate.Match;
            var text = candidate.Text;
            var groupKey = candidate.GroupKey;
            var rule = groupKey is null ? null : assemblyPlan.FindRule(groupKey);

            if (groupKey is not null)
            {
                var stats = GetOrCreateGroupStats(groupStats, groupKey);
                var maxChunks = rule?.MaxChunks ?? assemblyPlan.DefaultMaxChunksPerGroup;
                if (maxChunks.HasValue && stats.ChunkCount >= maxChunks.Value)
                {
                    skippedForGroupBudget++;
                    continue;
                }

                var maxChars = rule?.MaxChars ?? assemblyPlan.DefaultMaxCharsPerGroup;
                if (maxChars.HasValue && stats.CharCount >= maxChars.Value)
                {
                    skippedForGroupBudget++;
                    continue;
                }
            }

            var chunkBudget = Math.Min(request.MaxCharsPerChunk, remainingChars);
            if (groupKey is not null)
            {
                var maxChars = rule?.MaxChars ?? assemblyPlan.DefaultMaxCharsPerGroup;
                if (maxChars.HasValue)
                {
                    chunkBudget = Math.Min(chunkBudget, maxChars.Value - groupStats[groupKey].CharCount);
                }
            }

            if (chunkBudget <= 0)
            {
                skippedForGroupBudget++;
                continue;
            }

            var excerpt = TrimToBudget(text, chunkBudget);
            if (excerpt.Text.Length == 0)
            {
                droppedForBudget++;
                break;
            }

            var chunk = new RagContextChunk
            {
                Rank = chunks.Count + 1,
                Score = match.Score,
                Collection = match.Collection,
                PartitionKey = match.Record.PartitionKey,
                Id = match.Record.Id,
                Text = excerpt.Text,
                ContentField = request.ContentField,
                GroupKey = groupKey,
                CharStart = excerpt.CharStart,
                CharEnd = excerpt.CharEnd,
                OriginalTextLength = text.Length,
                Truncated = excerpt.Truncated,
                ContextExcerptHash = $"sha256:{Sha256Hex(excerpt.Text)}",
                RetrievalDiagnostics = request.IncludeTrace ? match.Diagnostics : null,
                RetrievalMatch = request.IncludeTrace
                    ? new RagContextRetrievalMatch
                    {
                        Rank = match.Rank,
                        Score = match.Score,
                        Collection = match.Collection,
                        SearchMode = effectiveSearchMode,
                        Snippet = match.Snippet
                    }
                    : null,
                Metadata = match.Record.Metadata,
                Sources = match.Record.Sources,
                Record = request.IncludeRecords ? match.Record : null
            };

            if (assembleCitations)
            {
                chunk.CitationIds = AddCitations(citations, chunk, match.Record, request.MaxCitationsPerChunk, out var omitted);
                omittedCitationCount += omitted;
            }

            chunks.Add(chunk);

            totalChars += excerpt.Text.Length;
            remainingChars -= excerpt.Text.Length;
            if (groupKey is not null)
            {
                var stats = groupStats[groupKey];
                stats.ChunkCount++;
                stats.CharCount += excerpt.Text.Length;
            }
        }

        var groupEvaluations = EvaluateGroups(assemblyPlan, groupStats);
        var unsatisfiedRequiredGroups = groupEvaluations
            .Where(pair => pair.Value.Required && !pair.Value.Satisfied)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (assemblyPlan.FailOnUnsatisfiedRequiredGroups && unsatisfiedRequiredGroups.Count > 0)
        {
            throw new InvalidOperationException($"RAG context required groups were not satisfied: {string.Join(", ", unsatisfiedRequiredGroups)}.");
        }

        var graphContext = await BuildGraphContextAsync(request.GraphExpansion, retrieval.Results, ct);
        var envelope = new RagContextEnvelope
        {
            Query = r.Query,
            Chunks = chunks,
            Citations = request.IncludeCitations ? citations : new List<RagContextCitation>(),
            TotalChars = totalChars,
            OmittedCitationCount = omittedCitationCount,
            GraphContext = graphContext
        };
        if (request.IncludeContextText)
        {
            envelope.ContextText = RenderContextText(chunks, citations, graphContext);
            envelope.ContextTextFormat = ContextTextFormat;
            envelope.ContextTextHash = $"sha256:{Sha256Hex(envelope.ContextText)}";
        }
        envelope.GraphExpansion = BuildGraphExpansionSummary(request.GraphExpansion, graphContext, chunks, request.IncludeContextText);

        if (request.IncludeTrace)
        {
            var traceGroupStats = new JsonObject();
            foreach (var pair in groupEvaluations.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                traceGroupStats[pair.Key] = new JsonObject
                {
                    ["candidateCount"] = pair.Value.CandidateCount,
                    ["chunkCount"] = pair.Value.ChunkCount,
                    ["charCount"] = pair.Value.CharCount,
                    ["priority"] = pair.Value.Priority,
                    ["required"] = pair.Value.Required,
                    ["satisfied"] = pair.Value.Satisfied,
                    ["minChunks"] = pair.Value.MinChunks,
                    ["maxChunks"] = pair.Value.MaxChunks,
                    ["minChars"] = pair.Value.MinChars,
                    ["maxChars"] = pair.Value.MaxChars
                };
            }
            envelope.Trace = new JsonObject
            {
                ["retrieval"] = retrieval.Trace?.DeepClone() ?? (JsonNode)new JsonObject(),
                ["chunkCount"] = chunks.Count,
                ["citationCount"] = assembleCitations ? citations.Count : 0,
                ["omittedCitationCount"] = omittedCitationCount,
                ["effectiveSearchMode"] = effectiveSearchMode,
                ["includeContextText"] = request.IncludeContextText,
                ["contextTextFormat"] = request.IncludeContextText ? ContextTextFormat : string.Empty,
                ["contextTextHash"] = envelope.ContextTextHash ?? string.Empty,
                ["totalChars"] = totalChars,
                ["maxChars"] = request.MaxChars,
                ["maxCharsPerChunk"] = request.MaxCharsPerChunk,
                ["skippedEmptyText"] = skippedEmptyText,
                ["skippedForGroupBudget"] = skippedForGroupBudget,
                ["droppedForBudget"] = droppedForBudget,
                ["budgetExhausted"] = remainingChars <= 0 && candidates.Count > chunks.Count,
                ["groupBy"] = assemblyPlan.GroupBy ?? string.Empty,
                ["groupByPath"] = assemblyPlan.GroupByPath ?? string.Empty,
                ["maxChunksPerGroup"] = assemblyPlan.DefaultMaxChunksPerGroup ?? 0,
                ["maxCharsPerGroup"] = assemblyPlan.DefaultMaxCharsPerGroup ?? 0,
                ["groupCount"] = groupEvaluations.Count,
                ["unsatisfiedRequiredGroups"] = JsonSerializer.SerializeToNode(unsatisfiedRequiredGroups),
                ["groupStats"] = traceGroupStats,
                ["graphExpansion"] = BuildGraphExpansionTrace(request.GraphExpansion, graphContext),
                ["graphContribution"] = JsonSerializer.SerializeToNode(envelope.GraphExpansion, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ["contextAssembly"] = new JsonObject
                {
                    ["enabled"] = assemblyPlan.Enabled,
                    ["authorityOrdering"] = assemblyPlan.AuthorityOrdering,
                    ["failOnUnsatisfiedRequiredGroups"] = assemblyPlan.FailOnUnsatisfiedRequiredGroups,
                    ["configuredGroupCount"] = assemblyPlan.Rules.Count
                }
            };
        }

        return envelope;
    }

    public async Task<RagContextEvaluationResult> EvaluateContextAsync(
        RagContextEvaluationRequest request,
        CancellationToken ct = default)
    {
        ValidateEvaluationRequest(request);
        var result = new RagContextEvaluationResult
        {
            Requested = request.Cases.Count
        };

        for (var i = 0; i < request.Cases.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var testCase = request.Cases[i];
            var start = DateTime.UtcNow;
            try
            {
                var context = await BuildContextAsync(testCase.Request, ct);
                var graph = EvaluateGraphContext(context, testCase.ExpectedGraph);
                var caseResult = new RagContextEvaluationCaseResult
                {
                    Index = i,
                    Name = testCase.Name,
                    Status = RagContextEvaluationStatuses.Succeeded,
                    Passed = graph.Passed,
                    DurationMs = (DateTime.UtcNow - start).TotalMilliseconds,
                    QueryId = ResolveEvaluationQueryId(testCase, i),
                    ProfileName = ResolveEvaluationProfileName(testCase.Request),
                    ExpectedAnchorIds = BuildExpectedAnchorIds(testCase.ExpectedGraph),
                    RetrievedRecordIds = context.Chunks.Select(chunk => chunk.Id).Distinct(StringComparer.Ordinal).ToList(),
                    GraphExpandedNodeIds = context.GraphContext?.Projection?.Nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>(),
                    GraphExpandedEdgeIds = context.GraphContext?.Projection?.Edges.Select(edge => edge.Id).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>(),
                    LexicalContributionCount = IsVectorOnly(testCase.Request) ? 0 : context.Chunks.Count,
                    VectorContributionCount = IsVectorOnly(testCase.Request) || IsHybrid(testCase.Request) ? context.Chunks.Count : 0,
                    GraphContributionCount = (context.GraphContext?.NodeCount ?? 0) + (context.GraphContext?.EdgeCount ?? 0),
                    FailureCategories = graph.FailureCategories.ToList(),
                    LimitReasons = BuildLimitReasons(context).ToList(),
                    GraphContribution = context.GraphExpansion,
                    Graph = graph,
                    Context = request.IncludeContext ? context : null
                };
                result.Cases.Add(caseResult);
                result.Succeeded++;
                if (caseResult.Passed)
                {
                    result.PassedCount++;
                }
            }
            catch (Exception ex) when (IsEvaluationCaseFailure(ex))
            {
                result.Cases.Add(new RagContextEvaluationCaseResult
                {
                    Index = i,
                    Name = testCase.Name,
                    Status = RagContextEvaluationStatuses.Failed,
                    Passed = false,
                    DurationMs = (DateTime.UtcNow - start).TotalMilliseconds,
                    QueryId = ResolveEvaluationQueryId(testCase, i),
                    ProfileName = ResolveEvaluationProfileName(testCase.Request),
                    ExpectedAnchorIds = BuildExpectedAnchorIds(testCase.ExpectedGraph),
                    FailureCategories = new List<string> { "case_error" },
                    Error = ex.Message,
                    Graph = new RagContextGraphEvaluationResult
                    {
                        Passed = false,
                        FailureCategories = new List<string> { "case_error" }
                    }
                });
                result.Failed++;
                if (!request.ContinueOnError)
                {
                    result.StoppedOnError = i + 1 < request.Cases.Count;
                    break;
                }
            }

            result.Attempted = result.Cases.Count;
        }

        result.Attempted = result.Cases.Count;
        AddEvaluationMetrics(result);
        return result;
    }

    private static RagContextGraphEvaluationResult EvaluateGraphContext(
        RagContextEnvelope context,
        RagContextExpectedGraph expected)
    {
        expected ??= new RagContextExpectedGraph();
        var graphContext = context.GraphContext;
        var nodeIds = graphContext?.Projection?.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var edgeIds = graphContext?.Projection?.Edges.Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var provenance = graphContext?.Provenance ?? new List<RagContextGraphProvenance>();
        var provenanceIds = provenance.Select(item => item.EntityId).ToHashSet(StringComparer.Ordinal);

        var missingNodeIds = Missing(expected.NodeIds, nodeIds);
        var missingEdgeIds = Missing(expected.EdgeIds, edgeIds);
        var missingProvenanceIds = Missing(expected.ProvenanceEntityIds, provenanceIds);
        var provenanceScope = expected.ProvenanceEntityIds.Count > 0
            ? provenance.Where(item => expected.ProvenanceEntityIds.Contains(item.EntityId, StringComparer.Ordinal)).ToList()
            : provenance;
        var sourceGroundedCount = provenanceScope.Count(item => item.SourceSpans.Count > 0);
        var sourceGroundingSatisfied = !expected.RequireSourceGroundedProvenance ||
            provenanceScope.Count > 0 && sourceGroundedCount == provenanceScope.Count;
        var graphContextTextPresent = !string.IsNullOrWhiteSpace(graphContext?.ContextText);
        var contextTextTruncated = graphContext?.ContextTextTruncated ?? false;
        var budgetTruncated = contextTextTruncated ||
            graphContext?.SourceTruncated == true ||
            graphContext?.OmittedProvenanceCount > 0 ||
            graphContext?.LimitsHit.Count > 0;
        var failureModes = new RagContextGraphEvaluationFailureModes
        {
            RetrievalMiss = context.Chunks.Count == 0,
            SeedMiss = string.Equals(graphContext?.Status, RagContextGraphExpansionStatuses.NoSeeds, StringComparison.Ordinal),
            GraphNotFound = string.Equals(graphContext?.Status, RagContextGraphExpansionStatuses.GraphNotFound, StringComparison.Ordinal),
            TraversalEmpty = graphContext is not null &&
                string.Equals(graphContext.Status, RagContextGraphExpansionStatuses.Succeeded, StringComparison.Ordinal) &&
                (expected.NodeIds.Count > 0 || expected.EdgeIds.Count > 0) &&
                (graphContext.NodeCount + graphContext.EdgeCount == 0),
            ExpectedNodeMissing = missingNodeIds.Count > 0,
            ExpectedEdgeMissing = missingEdgeIds.Count > 0,
            ExpectedProvenanceMissing = missingProvenanceIds.Count > 0,
            SourceGroundingFailed = !sourceGroundingSatisfied,
            GraphContextTextMissing = expected.RequireGraphContextText && !graphContextTextPresent,
            ContextTextTruncated = expected.RequireContextTextNotTruncated && contextTextTruncated,
            BudgetTruncated = budgetTruncated
        };
        var passed = missingNodeIds.Count == 0
            && missingEdgeIds.Count == 0
            && missingProvenanceIds.Count == 0
            && sourceGroundingSatisfied
            && (!expected.RequireGraphContextText || graphContextTextPresent)
            && (!expected.RequireContextTextNotTruncated || !contextTextTruncated);
        var failureCategories = passed ? new List<string>() : BuildFailureCategories(failureModes);

        return new RagContextGraphEvaluationResult
        {
            Status = graphContext?.Status,
            ExpectedNodeCount = expected.NodeIds.Count,
            MatchedNodeCount = expected.NodeIds.Count - missingNodeIds.Count,
            MissingNodeIds = missingNodeIds,
            ExpectedEdgeCount = expected.EdgeIds.Count,
            MatchedEdgeCount = expected.EdgeIds.Count - missingEdgeIds.Count,
            MissingEdgeIds = missingEdgeIds,
            ExpectedProvenanceCount = expected.ProvenanceEntityIds.Count,
            MatchedProvenanceCount = expected.ProvenanceEntityIds.Count - missingProvenanceIds.Count,
            MissingProvenanceEntityIds = missingProvenanceIds,
            SourceGroundedProvenanceCount = sourceGroundedCount,
            SourceGroundingSatisfied = sourceGroundingSatisfied,
            GraphContextTextPresent = graphContextTextPresent,
            ContextTextTruncated = contextTextTruncated,
            BudgetTruncated = budgetTruncated,
            FailureModes = failureModes,
            FailureCategories = failureCategories,
            Passed = passed
        };
    }

    private static List<string> Missing(IEnumerable<string> expected, HashSet<string> actual)
    {
        return expected
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Where(id => !actual.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddEvaluationMetrics(RagContextEvaluationResult result)
    {
        result.PassRate = result.Attempted == 0 ? 0 : result.PassedCount / (double)result.Attempted;
        var graphResults = result.Cases
            .Where(testCase => testCase.Status == RagContextEvaluationStatuses.Succeeded)
            .Select(testCase => testCase.Graph)
            .ToList();
        var expectedNodes = graphResults.Sum(graph => graph.ExpectedNodeCount);
        var expectedEdges = graphResults.Sum(graph => graph.ExpectedEdgeCount);
        var expectedProvenance = graphResults.Sum(graph => graph.ExpectedProvenanceCount);
        result.NodeHitRate = expectedNodes == 0 ? 1.0 : graphResults.Sum(graph => graph.MatchedNodeCount) / (double)expectedNodes;
        result.EdgeHitRate = expectedEdges == 0 ? 1.0 : graphResults.Sum(graph => graph.MatchedEdgeCount) / (double)expectedEdges;
        result.ProvenanceHitRate = expectedProvenance == 0 ? 1.0 : graphResults.Sum(graph => graph.MatchedProvenanceCount) / (double)expectedProvenance;
        foreach (var category in result.Cases.SelectMany(testCase => testCase.FailureCategories))
        {
            Increment(result.FailureCategoryCounts, category);
        }

        foreach (var reason in result.Cases.SelectMany(testCase => testCase.LimitReasons))
        {
            Increment(result.LimitReasonCounts, reason);
        }
    }

    private static bool IsEvaluationCaseFailure(Exception ex)
        => ex is InvalidOperationException or ArgumentException;

    private static string RenderContextText(
        IReadOnlyList<RagContextChunk> chunks,
        IReadOnlyList<RagContextCitation> citations,
        RagContextGraphContext? graphContext)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Context:");
        foreach (var chunk in chunks)
        {
            var marker = chunk.CitationIds.Count == 0
                ? $"[chunk:{chunk.Rank}]"
                : "[" + string.Join(", ", chunk.CitationIds) + "]";
            builder.Append(marker);
            builder.Append(' ');
            builder.AppendLine(NormalizeContextTextLine(chunk.Text));
            builder.AppendLine();
        }

        if (citations.Count > 0)
        {
            builder.AppendLine("Citations:");
            foreach (var citation in citations)
            {
                builder.Append('[');
                builder.Append(citation.Id);
                builder.Append("] ");
                builder.Append(string.IsNullOrWhiteSpace(citation.SourceUri) ? citation.RecordId : citation.SourceUri);
                if (!string.IsNullOrWhiteSpace(citation.SourceLabel))
                {
                    builder.Append(" - ");
                    builder.Append(citation.SourceLabel);
                }

                builder.Append(" (record: ");
                builder.Append(citation.RecordId);
                builder.Append(')');
                builder.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(graphContext?.ContextText))
        {
            builder.AppendLine();
            builder.AppendLine(graphContext.ContextText);
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<RagContextGraphContext?> BuildGraphContextAsync(
        RagContextGraphExpansionOptions? options,
        IReadOnlyList<RetrievalMatch> retrievalMatches,
        CancellationToken ct)
    {
        if (options is null || !options.Enabled)
        {
            return null;
        }

        if (_recordStore is null)
        {
            return HandleGraphExpansionFailure(options, null, "record_store_unavailable", "Graph expansion requires an IRecordCollectionStore.");
        }

        var seedResolution = ResolveGraphSeedNodeIds(options, retrievalMatches);
        var seedNodeIds = seedResolution.SeedNodeIds;
        if (seedNodeIds.Count == 0)
        {
            return new RagContextGraphContext
            {
                Status = RagContextGraphExpansionStatuses.NoSeeds,
                Collection = options.Collection,
                GraphId = options.GraphId,
                SeedCandidateCount = retrievalMatches.Count,
                SeedJsonPointers = options.SeedJsonPointers.ToList(),
                SeedDiagnostics = seedResolution.Diagnostics,
                OmittedSeedDiagnosticCount = seedResolution.OmittedDiagnostics,
                DroppedSeedCount = seedResolution.DroppedSeedCount,
                MaxSeedNodes = options.MaxSeedNodes,
                RequestedMaxRecords = options.MaxRecords
            };
        }

        try
        {
            var traversal = await _recordStore.TraverseGraphAsync(options.Collection, new VyralGraphTraversalRequest
            {
                GraphId = options.GraphId,
                Namespace = options.Namespace,
                TenantId = options.TenantId,
                PartitionKey = options.PartitionKey,
                StartNodeIds = seedNodeIds,
                Profile = options.Profile,
                MaxRecords = options.MaxRecords,
                AllowPartialGraph = options.AllowPartialGraph
            }, ct);
            if (traversal is null)
            {
                return HandleGraphExpansionFailure(options, seedResolution, RagContextGraphExpansionStatuses.GraphNotFound, $"Graph collection '{options.Collection}' was not found.");
            }

            var contextExcerpt = options.IncludeGraphContextText
                ? RenderGraphContextText(traversal.Projection, options.MaxGraphContextChars)
                : null;
            var contextText = contextExcerpt?.Text;
            var provenance = options.IncludeGraphProvenance
                ? BuildGraphProvenance(traversal.Projection, options.MaxGraphProvenanceItems)
                : new GraphProvenanceResult(new List<RagContextGraphProvenance>(), 0);
            return new RagContextGraphContext
            {
                Status = RagContextGraphExpansionStatuses.Succeeded,
                Collection = traversal.Collection,
                GraphId = traversal.GraphId,
                SeedNodeIds = seedNodeIds,
                SeedCandidateCount = retrievalMatches.Count,
                Projection = traversal.Projection,
                NodeCount = traversal.NodeCount,
                EdgeCount = traversal.EdgeCount,
                SourceRecordCount = traversal.SourceRecordCount,
                SourceTruncated = traversal.SourceTruncated,
                SeedJsonPointers = options.SeedJsonPointers.ToList(),
                SeedDiagnostics = seedResolution.Diagnostics,
                OmittedSeedDiagnosticCount = seedResolution.OmittedDiagnostics,
                DroppedSeedCount = seedResolution.DroppedSeedCount,
                MaxSeedNodes = options.MaxSeedNodes,
                RequestedMaxRecords = traversal.RequestedMaxRecords,
                ExportedRecordCount = traversal.ExportedRecordCount,
                EstimatedRequiredRecordCount = traversal.EstimatedRequiredRecordCount,
                SourceContinuationToken = traversal.SourceContinuationToken,
                LimitsHit = BuildGraphLimitsHit(traversal, contextExcerpt, provenance, seedResolution),
                ContextText = contextText,
                ContextTextHash = string.IsNullOrWhiteSpace(contextText) ? null : $"sha256:{Sha256Hex(contextText)}",
                ContextTextChars = contextText?.Length ?? 0,
                ContextTextTruncated = contextExcerpt?.Truncated ?? false,
                Provenance = provenance.Items,
                OmittedProvenanceCount = provenance.OmittedCount
            };
        }
        catch (VyralGraphTraversalTruncatedException ex) when (options.FallbackOnFailure)
        {
            return HandleGraphTraversalTruncation(options, seedResolution, ex);
        }
        catch (InvalidOperationException ex) when (options.FallbackOnFailure)
        {
            return HandleGraphExpansionFailure(options, seedResolution, RagContextGraphExpansionStatuses.Failed, ex.Message);
        }
    }

    private static RagContextGraphContext HandleGraphExpansionFailure(
        RagContextGraphExpansionOptions options,
        GraphSeedResolution? seedResolution,
        string status,
        string reason)
    {
        if (!options.FallbackOnFailure)
        {
            throw new InvalidOperationException($"RAG graph expansion failed: {reason}");
        }

        return new RagContextGraphContext
        {
            Status = status,
            Collection = options.Collection,
            GraphId = options.GraphId,
            SeedNodeIds = seedResolution?.SeedNodeIds ?? new List<string>(),
            SeedCandidateCount = seedResolution?.SeedCandidateCount ?? 0,
            SeedJsonPointers = options.SeedJsonPointers.ToList(),
            SeedDiagnostics = seedResolution?.Diagnostics ?? new List<RagContextGraphSeedDiagnostic>(),
            OmittedSeedDiagnosticCount = seedResolution?.OmittedDiagnostics ?? 0,
            DroppedSeedCount = seedResolution?.DroppedSeedCount ?? 0,
            MaxSeedNodes = options.MaxSeedNodes,
            RequestedMaxRecords = options.MaxRecords,
            FailureReason = reason
        };
    }

    private static RagContextGraphContext HandleGraphTraversalTruncation(
        RagContextGraphExpansionOptions options,
        GraphSeedResolution seedResolution,
        VyralGraphTraversalTruncatedException ex)
    {
        var result = HandleGraphExpansionFailure(
            options,
            seedResolution,
            RagContextGraphExpansionStatuses.BudgetTruncated,
            ex.Message);
        result.SourceRecordCount = ex.ExportedRecordCount;
        result.SourceTruncated = true;
        result.ExportedRecordCount = ex.ExportedRecordCount;
        result.EstimatedRequiredRecordCount = ex.EstimatedRequiredRecordCount;
        result.SourceContinuationToken = ex.ContinuationToken;
        result.LimitsHit.Add("maxRecords");
        return result;
    }

    private static GraphSeedResolution ResolveGraphSeedNodeIds(
        RagContextGraphExpansionOptions options,
        IReadOnlyList<RetrievalMatch> retrievalMatches)
    {
        var seedIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<RagContextGraphSeedDiagnostic>();
        var omittedDiagnostics = 0;
        var droppedSeedCount = 0;
        foreach (var seed in options.SeedNodeIds)
        {
            AddSeed(seedIds, seen, seed, options.MaxSeedNodes, diagnostics, ref omittedDiagnostics, ref droppedSeedCount, new RagContextGraphSeedDiagnostic
            {
                Pointer = "$.seedNodeIds",
                Found = !string.IsNullOrWhiteSpace(seed),
                RawValue = seed
            });
        }

        foreach (var match in retrievalMatches)
        {
            foreach (var path in options.SeedJsonPointers)
            {
                var values = ResolveSeedValues(match.Record, path).ToList();
                if (values.Count == 0)
                {
                    AddSeedDiagnostic(diagnostics, ref omittedDiagnostics, new RagContextGraphSeedDiagnostic
                    {
                        RecordId = match.Record.Id,
                        PartitionKey = match.Record.PartitionKey,
                        Pointer = path,
                        Found = false,
                        SkippedReason = "missing"
                    });
                    continue;
                }

                foreach (var seed in values)
                {
                    AddSeed(seedIds, seen, seed.RawValue, options.MaxSeedNodes, diagnostics, ref omittedDiagnostics, ref droppedSeedCount, new RagContextGraphSeedDiagnostic
                    {
                        RecordId = match.Record.Id,
                        PartitionKey = match.Record.PartitionKey,
                        Pointer = path,
                        Found = true,
                        RawValue = seed.RawValue
                    });
                    if (seedIds.Count >= options.MaxSeedNodes)
                    {
                        return new GraphSeedResolution(seedIds, diagnostics, omittedDiagnostics, droppedSeedCount, retrievalMatches.Count);
                    }
                }
            }
        }

        return new GraphSeedResolution(seedIds, diagnostics, omittedDiagnostics, droppedSeedCount, retrievalMatches.Count);
    }

    private static IEnumerable<SeedValue> ResolveSeedValues(VyralRecord record, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(record));
        if (!TryGetJsonPointerValue(document.RootElement, path, out var value))
        {
            yield break;
        }

        foreach (var seed in JsonElementToSeedValues(value))
        {
            yield return seed;
        }
    }

    private static IEnumerable<SeedValue> JsonElementToSeedValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                foreach (var itemSeed in JsonElementToSeedValues(item))
                {
                    yield return itemSeed;
                }
            }

            yield break;
        }

        var seed = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null
        };
        yield return new SeedValue(seed);
    }

    private static void AddSeed(
        List<string> seedIds,
        HashSet<string> seen,
        string? seed,
        int maxSeedNodes,
        List<RagContextGraphSeedDiagnostic> diagnostics,
        ref int omittedDiagnostics,
        ref int droppedSeedCount,
        RagContextGraphSeedDiagnostic diagnostic)
    {
        diagnostic.NormalizedValue = string.IsNullOrWhiteSpace(seed) ? null : seed.Trim();
        if (string.IsNullOrWhiteSpace(seed))
        {
            diagnostic.Accepted = false;
            diagnostic.SkippedReason = "blank";
            AddSeedDiagnostic(diagnostics, ref omittedDiagnostics, diagnostic);
            return;
        }

        if (seedIds.Count >= maxSeedNodes)
        {
            diagnostic.Accepted = false;
            diagnostic.SkippedReason = "maxSeedNodes";
            droppedSeedCount++;
            AddSeedDiagnostic(diagnostics, ref omittedDiagnostics, diagnostic);
            return;
        }

        var normalized = seed.Trim();
        if (seen.Add(normalized))
        {
            seedIds.Add(normalized);
            diagnostic.Accepted = true;
        }
        else
        {
            diagnostic.Accepted = false;
            diagnostic.SkippedReason = "duplicate";
        }

        AddSeedDiagnostic(diagnostics, ref omittedDiagnostics, diagnostic);
    }

    private static void AddSeedDiagnostic(
        List<RagContextGraphSeedDiagnostic> diagnostics,
        ref int omittedDiagnostics,
        RagContextGraphSeedDiagnostic diagnostic)
    {
        if (diagnostics.Count >= MaxGraphSeedDiagnostics)
        {
            omittedDiagnostics++;
            return;
        }

        diagnostics.Add(diagnostic);
    }

    private static ContextExcerpt? RenderGraphContextText(VyralGraphProjection projection, int maxChars)
    {
        if (maxChars <= 0 || projection.Nodes.Count == 0 && projection.Edges.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Graph context:");
        if (projection.StartNodeIds.Count > 0)
        {
            builder.Append("Seeds: ");
            builder.AppendLine(string.Join(", ", projection.StartNodeIds));
        }

        if (projection.Nodes.Count > 0)
        {
            builder.AppendLine("Nodes:");
            foreach (var node in projection.Nodes)
            {
                builder.Append("- ");
                builder.Append(node.Id);
                if (!string.IsNullOrWhiteSpace(node.Type))
                {
                    builder.Append(" (");
                    builder.Append(node.Type);
                    builder.Append(')');
                }

                if (!string.IsNullOrWhiteSpace(node.Label))
                {
                    builder.Append(": ");
                    builder.Append(node.Label);
                }

                builder.AppendLine();
            }
        }

        if (projection.Edges.Count > 0)
        {
            builder.AppendLine("Edges:");
            foreach (var edge in projection.Edges)
            {
                builder.Append("- ");
                builder.Append(edge.SourceId);
                builder.Append(" --");
                builder.Append(edge.Predicate);
                builder.Append("--> ");
                builder.Append(edge.TargetId);
                if (!string.IsNullOrWhiteSpace(edge.Label))
                {
                    builder.Append(" (");
                    builder.Append(edge.Label);
                    builder.Append(')');
                }

                builder.AppendLine();
            }
        }

        return TrimToBudget(builder.ToString().TrimEnd(), maxChars);
    }

    private static GraphProvenanceResult BuildGraphProvenance(VyralGraphProjection projection, int maxItems)
    {
        var items = new List<RagContextGraphProvenance>();
        var omitted = 0;

        foreach (var node in projection.Nodes)
        {
            AddGraphProvenance(items, ref omitted, maxItems, new RagContextGraphProvenance
            {
                EntityKind = VyralGraphSubjectKinds.Node,
                EntityId = node.Id,
                Label = node.Label,
                NodeType = node.Type,
                SourceSpans = CloneSourceSpans(node.SourceSpans),
                AssertionIds = node.AssertionIds.ToList()
            });
        }

        foreach (var edge in projection.Edges)
        {
            AddGraphProvenance(items, ref omitted, maxItems, new RagContextGraphProvenance
            {
                EntityKind = VyralGraphSubjectKinds.Edge,
                EntityId = edge.Id,
                Label = edge.Label,
                Predicate = edge.Predicate,
                SourceId = edge.SourceId,
                TargetId = edge.TargetId,
                SourceSpans = CloneSourceSpans(edge.SourceSpans),
                AssertionIds = edge.AssertionIds.ToList()
            });
        }

        return new GraphProvenanceResult(items, omitted);
    }

    private static List<string> BuildGraphLimitsHit(
        VyralGraphTraversalResult traversal,
        ContextExcerpt? contextExcerpt,
        GraphProvenanceResult provenance,
        GraphSeedResolution seedResolution)
    {
        var limits = new List<string>();
        if (traversal.SourceTruncated)
        {
            limits.Add("maxRecords");
        }

        if (traversal.Projection.Diagnostics?["edgeTruncated"]?.GetValue<bool>() == true)
        {
            limits.Add("edgeLimit");
        }

        if (traversal.Projection.Diagnostics?["nodeLimitReached"]?.GetValue<bool>() == true)
        {
            limits.Add("nodeLimit");
        }

        if (contextExcerpt?.Truncated == true)
        {
            limits.Add("maxGraphContextChars");
        }

        if (provenance.OmittedCount > 0)
        {
            limits.Add("maxGraphProvenanceItems");
        }

        if (seedResolution.DroppedSeedCount > 0)
        {
            limits.Add("maxSeedNodes");
        }

        return limits.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void AddGraphProvenance(
        List<RagContextGraphProvenance> items,
        ref int omitted,
        int maxItems,
        RagContextGraphProvenance item)
    {
        if (items.Count >= maxItems)
        {
            omitted++;
            return;
        }

        items.Add(item);
    }

    private static List<VyralGraphSourceSpan> CloneSourceSpans(IEnumerable<VyralGraphSourceSpan> spans)
    {
        return spans
            .Select(span => new VyralGraphSourceSpan
            {
                SourceRef = span.SourceRef,
                CharStart = span.CharStart,
                CharEnd = span.CharEnd,
                Unit = span.Unit,
                Locator = span.Locator,
                TextHash = span.TextHash,
                Metadata = span.Metadata?.DeepClone().AsObject()
            })
            .ToList();
    }

    private static JsonObject BuildGraphExpansionTrace(
        RagContextGraphExpansionOptions? options,
        RagContextGraphContext? graphContext)
    {
        if (options is null || !options.Enabled)
        {
            return new JsonObject
            {
                ["enabled"] = false,
                ["status"] = RagContextGraphExpansionStatuses.NotRequested
            };
        }

        return new JsonObject
        {
            ["enabled"] = true,
            ["collection"] = options.Collection,
            ["graphId"] = options.GraphId ?? string.Empty,
            ["namespace"] = options.Namespace ?? string.Empty,
            ["tenantId"] = options.TenantId ?? string.Empty,
            ["partitionKey"] = options.PartitionKey ?? string.Empty,
            ["status"] = graphContext?.Status ?? RagContextGraphExpansionStatuses.NotRequested,
            ["seedJsonPointers"] = JsonSerializer.SerializeToNode(graphContext?.SeedJsonPointers ?? options.SeedJsonPointers),
            ["seedDiagnostics"] = JsonSerializer.SerializeToNode(graphContext?.SeedDiagnostics ?? new List<RagContextGraphSeedDiagnostic>()),
            ["omittedSeedDiagnosticCount"] = graphContext?.OmittedSeedDiagnosticCount ?? 0,
            ["seedCount"] = graphContext?.SeedNodeIds.Count ?? 0,
            ["seedCandidateCount"] = graphContext?.SeedCandidateCount ?? 0,
            ["maxSeedNodes"] = options.MaxSeedNodes,
            ["droppedSeedCount"] = graphContext?.DroppedSeedCount ?? 0,
            ["nodeCount"] = graphContext?.NodeCount ?? 0,
            ["edgeCount"] = graphContext?.EdgeCount ?? 0,
            ["maxDepth"] = options.Profile?.MaxDepth ?? 0,
            ["nodeLimit"] = options.Profile?.Limit ?? 0,
            ["edgeLimit"] = options.Profile?.EdgeLimit ?? 0,
            ["requestedMaxRecords"] = graphContext?.RequestedMaxRecords ?? options.MaxRecords ?? 0,
            ["exportedRecordCount"] = graphContext?.ExportedRecordCount ?? 0,
            ["estimatedRequiredRecordCount"] = graphContext?.EstimatedRequiredRecordCount ?? 0,
            ["sourceRecordCount"] = graphContext?.SourceRecordCount ?? 0,
            ["sourceTruncated"] = graphContext?.SourceTruncated ?? false,
            ["sourceContinuationToken"] = graphContext?.SourceContinuationToken ?? string.Empty,
            ["contextTextHash"] = graphContext?.ContextTextHash ?? string.Empty,
            ["contextTextChars"] = graphContext?.ContextTextChars ?? 0,
            ["contextTextTruncated"] = graphContext?.ContextTextTruncated ?? false,
            ["provenanceCount"] = graphContext?.Provenance.Count ?? 0,
            ["omittedProvenanceCount"] = graphContext?.OmittedProvenanceCount ?? 0,
            ["limitsHit"] = JsonSerializer.SerializeToNode(graphContext?.LimitsHit ?? new List<string>()),
            ["failureReason"] = graphContext?.FailureReason ?? string.Empty
        };
    }

    private static RagContextGraphExpansionSummary? BuildGraphExpansionSummary(
        RagContextGraphExpansionOptions? options,
        RagContextGraphContext? graphContext,
        IReadOnlyList<RagContextChunk> chunks,
        bool includeContextText)
    {
        if (options is null)
        {
            return null;
        }

        var retrievedIds = chunks.Select(chunk => chunk.Id).Distinct(StringComparer.Ordinal).ToList();
        var sourceIdsTouched = graphContext?.Provenance
            .SelectMany(item => new[] { item.EntityId, item.SourceId, item.TargetId })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList() ?? new List<string>();
        var sourceSpans = graphContext?.Provenance.Sum(item => item.SourceSpans.Count) ?? 0;
        return new RagContextGraphExpansionSummary
        {
            ExpansionAttempted = options.Enabled,
            ExpansionEnabled = options.Enabled,
            Collection = options.Collection,
            GraphId = options.GraphId,
            Namespace = options.Namespace,
            TenantId = options.TenantId,
            PartitionKey = options.PartitionKey,
            Status = graphContext?.Status ?? RagContextGraphExpansionStatuses.NotRequested,
            SkippedReason = graphContext?.FailureReason,
            ProfileId = options.Profile?.Id,
            MaxDepth = options.Profile?.MaxDepth ?? 0,
            NodeLimit = options.Profile?.Limit ?? 0,
            EdgeLimit = options.Profile?.EdgeLimit ?? 0,
            MaxRecords = options.MaxRecords,
            MaxGraphContextChars = options.MaxGraphContextChars,
            MaxGraphProvenanceItems = options.MaxGraphProvenanceItems,
            RetrievedRecordIds = retrievedIds,
            SourceRecordIdsTouched = sourceIdsTouched,
            SeedJsonPointers = graphContext?.SeedJsonPointers ?? options.SeedJsonPointers.ToList(),
            SeedCandidateCount = graphContext?.SeedCandidateCount ?? 0,
            SeedCount = graphContext?.SeedNodeIds.Count ?? 0,
            SeedNodeIds = graphContext?.SeedNodeIds ?? new List<string>(),
            DroppedSeedCount = graphContext?.DroppedSeedCount ?? 0,
            NodesAdded = graphContext?.NodeCount ?? 0,
            EdgesAdded = graphContext?.EdgeCount ?? 0,
            RelationshipsAdded = graphContext?.EdgeCount ?? 0,
            SourceRecordCount = graphContext?.SourceRecordCount ?? 0,
            SourceTruncated = graphContext?.SourceTruncated ?? false,
            ExportedRecordCount = graphContext?.ExportedRecordCount ?? 0,
            EstimatedRequiredRecordCount = graphContext?.EstimatedRequiredRecordCount,
            LimitsHit = graphContext?.LimitsHit ?? new List<string>(),
            GroundingStatus = sourceSpans > 0 ? "source_grounded" : graphContext?.Provenance.Count > 0 ? "ungrounded" : "none",
            GraphContextInfluencedContextText = includeContextText && !string.IsNullOrWhiteSpace(graphContext?.ContextText),
            ContextTextTruncated = graphContext?.ContextTextTruncated ?? false,
            OmittedProvenanceCount = graphContext?.OmittedProvenanceCount ?? 0
        };
    }

    private static IEnumerable<string> BuildLimitReasons(RagContextEnvelope context)
    {
        if (context.GraphExpansion is not null)
        {
            foreach (var limit in context.GraphExpansion.LimitsHit)
            {
                yield return limit;
            }
        }

        if (context.GraphContext?.ContextTextTruncated == true)
        {
            yield return "maxGraphContextChars";
        }

        if (context.GraphContext?.SourceTruncated == true)
        {
            yield return "maxRecords";
        }
    }

    private static List<string> BuildFailureCategories(RagContextGraphEvaluationFailureModes modes)
    {
        var categories = new List<string>();
        if (modes.RetrievalMiss) categories.Add("retrieval_miss");
        if (modes.SeedMiss) categories.Add("seed_miss");
        if (modes.GraphNotFound) categories.Add("graph_not_found");
        if (modes.TraversalEmpty) categories.Add("traversal_empty");
        if (modes.ExpectedNodeMissing) categories.Add("expected_node_missing");
        if (modes.ExpectedEdgeMissing) categories.Add("expected_edge_missing");
        if (modes.ExpectedProvenanceMissing) categories.Add("expected_provenance_missing");
        if (modes.SourceGroundingFailed) categories.Add("source_grounding_failed");
        if (modes.GraphContextTextMissing) categories.Add("graph_context_text_missing");
        if (modes.ContextTextTruncated) categories.Add("context_text_truncated");
        if (modes.BudgetTruncated) categories.Add("budget_truncated");
        return categories;
    }

    private static string ResolveEvaluationQueryId(RagContextEvaluationCase testCase, int index)
    {
        if (testCase.Metadata?["queryId"] is JsonValue value &&
            value.TryGetValue<string>(out var queryId) &&
            !string.IsNullOrWhiteSpace(queryId))
        {
            return queryId;
        }

        return string.IsNullOrWhiteSpace(testCase.Name) ? $"case-{index}" : testCase.Name!;
    }

    private static string? ResolveEvaluationProfileName(RagContextRequest request)
        => request.Retrieval.Profile ?? request.GraphExpansion?.Profile?.Id ?? request.Retrieval.SearchMode;

    private static List<string> BuildExpectedAnchorIds(RagContextExpectedGraph expected)
    {
        return expected.NodeIds
            .Concat(expected.EdgeIds)
            .Concat(expected.ProvenanceEntityIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsVectorOnly(RagContextRequest request)
        => string.Equals(request.Retrieval.SearchMode, SearchModes.Vector, StringComparison.Ordinal);

    private static bool IsHybrid(RagContextRequest request)
        => string.Equals(request.Retrieval.SearchMode, SearchModes.Hybrid, StringComparison.Ordinal);

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        counts[key] = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
    }

    private static string NormalizeContextTextLine(string value)
    {
        return string.Join(
            "\n",
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.TrimEnd()));
    }

    private static string? GetTraceString(JsonObject? trace, string key)
    {
        var node = trace?[key];
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
    }

    private static List<string> AddCitations(
        List<RagContextCitation> citations,
        RagContextChunk chunk,
        VyralRecord record,
        int? maxCitationsPerChunk,
        out int omitted)
    {
        omitted = 0;
        var sourceCount = record.Sources?.Count ?? 0;
        if (sourceCount == 0)
        {
            var citationId = $"c{chunk.Rank}";
            citations.Add(CreateCitation(citationId, chunk, source: null));
            return new List<string> { citationId };
        }

        var citationIds = new List<string>();
        var includedSourceCount = maxCitationsPerChunk.HasValue
            ? Math.Min(sourceCount, maxCitationsPerChunk.Value)
            : sourceCount;
        omitted = sourceCount - includedSourceCount;
        for (var i = 0; i < includedSourceCount; i++)
        {
            var citationId = sourceCount == 1
                ? $"c{chunk.Rank}"
                : $"c{chunk.Rank}.{i + 1}";
            citations.Add(CreateCitation(citationId, chunk, record.Sources![i]));
            citationIds.Add(citationId);
        }

        return citationIds;
    }

    private static RagContextCitation CreateCitation(string citationId, RagContextChunk chunk, VyralSourceReference? source)
    {
        var sourceSpan = source?.Span;
        return new RagContextCitation
        {
            Id = citationId,
            ChunkRank = chunk.Rank,
            Collection = chunk.Collection,
            PartitionKey = chunk.PartitionKey,
            RecordId = chunk.Id,
            SourceId = source?.Id,
            SourceKind = source?.Kind,
            SourceUri = source?.Uri,
            SourceLabel = source?.Label,
            SourceSpan = sourceSpan,
            IncludedSourceSpan = BuildIncludedSourceSpan(sourceSpan, chunk.CharStart, chunk.CharEnd),
            ContextCharStart = chunk.CharStart,
            ContextCharEnd = chunk.CharEnd,
            ContextExcerptHash = chunk.ContextExcerptHash
        };
    }

    private static VyralSourceSpan? BuildIncludedSourceSpan(VyralSourceSpan? sourceSpan, int contextCharStart, int contextCharEnd)
    {
        if (sourceSpan is null)
        {
            return null;
        }

        int? includedStart = null;
        int? includedEnd = null;
        if (sourceSpan.CharStart.HasValue)
        {
            includedStart = sourceSpan.CharStart.Value + contextCharStart;
            includedEnd = sourceSpan.CharStart.Value + contextCharEnd;
            if (sourceSpan.CharEnd.HasValue)
            {
                includedEnd = Math.Min(includedEnd.Value, sourceSpan.CharEnd.Value);
            }
        }

        return new VyralSourceSpan
        {
            CharStart = includedStart,
            CharEnd = includedEnd,
            Line = sourceSpan.Line,
            Column = sourceSpan.Column,
            Anchor = sourceSpan.Anchor,
            Extensions = sourceSpan.Extensions
        };
    }

    private static void ValidateRequest(RagContextRequest request)
    {
        if (request.Retrieval is null)
        {
            throw new InvalidOperationException("RAG context retrieval is required.");
        }

        if (request.Retrieval.Collections is null || request.Retrieval.Collections.Count == 0)
        {
            throw new InvalidOperationException("RAG context retrieval.collections must not be empty.");
        }

        if (request.MaxChars <= 0)
        {
            throw new InvalidOperationException("RAG context maxChars must be greater than zero.");
        }

        if (request.MaxCharsPerChunk <= 0)
        {
            throw new InvalidOperationException("RAG context maxCharsPerChunk must be greater than zero.");
        }

        if (request.MaxCitationsPerChunk is <= 0)
        {
            throw new InvalidOperationException("RAG context maxCitationsPerChunk must be greater than zero when provided.");
        }

        if (string.IsNullOrWhiteSpace(request.ContentField))
        {
            throw new InvalidOperationException("RAG context contentField is required.");
        }

        if (request.ContentField.Contains('/') || request.ContentField.Contains('\\') || request.ContentField.Contains('.'))
        {
            throw new InvalidOperationException("RAG context contentField must be a simple content property name.");
        }

        if (!string.IsNullOrWhiteSpace(request.Retrieval.Embedding?.Purpose))
        {
            _ = EmbeddingTextPreparer.NormalizePurpose(request.Retrieval.Embedding.Purpose);
        }

        if (request.ContextAssembly is not null)
        {
            ValidateContextAssembly(request.ContextAssembly);
            var assemblyGroupBy = NormalizeGroupBy(request.ContextAssembly.GroupBy);
            var assemblyGroupPath = request.ContextAssembly.GroupByPath;
            var effectiveGroupBy = assemblyGroupBy;
            if (string.IsNullOrWhiteSpace(effectiveGroupBy) &&
                (!string.IsNullOrWhiteSpace(assemblyGroupPath) ||
                 request.ContextAssembly.Groups.Count > 0 ||
                 request.ContextAssembly.DefaultMaxChunksPerGroup.HasValue ||
                 request.ContextAssembly.DefaultMaxCharsPerGroup.HasValue))
            {
                effectiveGroupBy = "jsonPointer";
            }

            if (effectiveGroupBy is "metadata" or "jsonPointer" && string.IsNullOrWhiteSpace(assemblyGroupPath))
            {
                throw new InvalidOperationException("RAG contextAssembly groupByPath is required when groupBy is metadata or jsonPointer.");
            }

            var normalizedAssemblyGroupPath = NormalizeGroupByPath(effectiveGroupBy, assemblyGroupPath);
            if (effectiveGroupBy == "jsonPointer" &&
                !string.IsNullOrWhiteSpace(normalizedAssemblyGroupPath) &&
                !normalizedAssemblyGroupPath.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("RAG contextAssembly groupByPath must be a JSON pointer when groupBy is jsonPointer.");
            }

            if (string.IsNullOrWhiteSpace(effectiveGroupBy) &&
                string.IsNullOrWhiteSpace(assemblyGroupPath) &&
                (request.ContextAssembly.Groups.Count > 0 ||
                 request.ContextAssembly.DefaultMaxChunksPerGroup.HasValue ||
                 request.ContextAssembly.DefaultMaxCharsPerGroup.HasValue))
            {
                throw new InvalidOperationException("RAG contextAssembly requires groupBy or groupByPath when group budgets are provided.");
            }
        }

        if (request.GraphExpansion is not null)
        {
            ValidateGraphExpansion(request.GraphExpansion);
        }
    }

    private static void ValidateEvaluationRequest(RagContextEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Cases.Count == 0)
        {
            throw new InvalidOperationException("RAG context evaluation requires at least one case.");
        }

        if (request.Cases.Count > MaxEvaluationCases)
        {
            throw new InvalidOperationException($"RAG context evaluation supports at most {MaxEvaluationCases} cases.");
        }

        for (var i = 0; i < request.Cases.Count; i++)
        {
            var testCase = request.Cases[i] ?? throw new InvalidOperationException($"RAG context evaluation case {i} is required.");
            ValidateRequest(testCase.Request);
            testCase.ExpectedGraph ??= new RagContextExpectedGraph();
            ValidateExpectedGraph(testCase.ExpectedGraph, i);
        }
    }

    private static void ValidateExpectedGraph(RagContextExpectedGraph expected, int caseIndex)
    {
        if (expected.NodeIds.Count > MaxExpectedGraphItemsPerCase)
        {
            throw new InvalidOperationException($"RAG context evaluation case {caseIndex} expectedGraph.nodeIds cannot exceed {MaxExpectedGraphItemsPerCase}.");
        }

        if (expected.EdgeIds.Count > MaxExpectedGraphItemsPerCase)
        {
            throw new InvalidOperationException($"RAG context evaluation case {caseIndex} expectedGraph.edgeIds cannot exceed {MaxExpectedGraphItemsPerCase}.");
        }

        if (expected.ProvenanceEntityIds.Count > MaxExpectedGraphItemsPerCase)
        {
            throw new InvalidOperationException($"RAG context evaluation case {caseIndex} expectedGraph.provenanceEntityIds cannot exceed {MaxExpectedGraphItemsPerCase}.");
        }
    }

    private static void ValidateGraphExpansion(RagContextGraphExpansionOptions graph)
    {
        if (!graph.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(graph.Collection))
        {
            throw new InvalidOperationException("RAG graphExpansion collection is required when graph expansion is enabled.");
        }

        if (graph.MaxSeedNodes <= 0)
        {
            throw new InvalidOperationException("RAG graphExpansion maxSeedNodes must be greater than zero.");
        }

        if (graph.MaxGraphContextChars <= 0)
        {
            throw new InvalidOperationException("RAG graphExpansion maxGraphContextChars must be greater than zero.");
        }

        if (graph.MaxGraphProvenanceItems < 0)
        {
            throw new InvalidOperationException("RAG graphExpansion maxGraphProvenanceItems cannot be negative.");
        }

        if (graph.MaxRecords.HasValue && graph.MaxRecords.Value <= 0)
        {
            throw new InvalidOperationException("RAG graphExpansion maxRecords must be greater than zero when provided.");
        }

        foreach (var pointer in graph.SeedJsonPointers)
        {
            if (string.IsNullOrWhiteSpace(pointer) || !pointer.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("RAG graphExpansion seedJsonPointers must be JSON pointers.");
            }
        }
    }

    private static void ValidateContextAssembly(RagContextAssemblyOptions assembly)
    {
        var groupBy = NormalizeGroupBy(assembly.GroupBy);
        if (!string.IsNullOrWhiteSpace(groupBy) && groupBy is not "collection" and not "sourceKind" and not "metadata" and not "jsonPointer")
        {
            throw new InvalidOperationException("RAG contextAssembly groupBy must be one of collection, sourceKind, metadata, or jsonPointer.");
        }

        if (assembly.DefaultMaxChunksPerGroup.HasValue && assembly.DefaultMaxChunksPerGroup.Value <= 0)
        {
            throw new InvalidOperationException("RAG contextAssembly defaultMaxChunksPerGroup must be greater than zero when provided.");
        }

        if (assembly.DefaultMaxCharsPerGroup.HasValue && assembly.DefaultMaxCharsPerGroup.Value <= 0)
        {
            throw new InvalidOperationException("RAG contextAssembly defaultMaxCharsPerGroup must be greater than zero when provided.");
        }

        foreach (var group in assembly.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                throw new InvalidOperationException("RAG contextAssembly group key is required.");
            }

            if (group.MinChunks.HasValue && group.MinChunks.Value <= 0)
            {
                throw new InvalidOperationException("RAG contextAssembly group minChunks must be greater than zero when provided.");
            }

            if (group.MaxChunks.HasValue && group.MaxChunks.Value <= 0)
            {
                throw new InvalidOperationException("RAG contextAssembly group maxChunks must be greater than zero when provided.");
            }

            if (group.MinChars.HasValue && group.MinChars.Value <= 0)
            {
                throw new InvalidOperationException("RAG contextAssembly group minChars must be greater than zero when provided.");
            }

            if (group.MaxChars.HasValue && group.MaxChars.Value <= 0)
            {
                throw new InvalidOperationException("RAG contextAssembly group maxChars must be greater than zero when provided.");
            }

            if (group.MinChunks.HasValue && group.MaxChunks.HasValue && group.MinChunks.Value > group.MaxChunks.Value)
            {
                throw new InvalidOperationException("RAG contextAssembly group minChunks cannot exceed maxChunks.");
            }

            if (group.MinChars.HasValue && group.MaxChars.HasValue && group.MinChars.Value > group.MaxChars.Value)
            {
                throw new InvalidOperationException("RAG contextAssembly group minChars cannot exceed maxChars.");
            }
        }
    }

    private static string ExtractText(VyralRecord record, string contentField)
    {
        var node = record.Content?[contentField];
        if (node is null) return string.Empty;
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
    }

    private static RerankOptions? BuildRetrievalRerankOptions(RetrievalRequest retrieval, string contentField)
    {
        if (retrieval.Rerank is null)
        {
            return null;
        }

        return new RerankOptions
        {
            Enabled = retrieval.Rerank.Enabled,
            Provider = retrieval.Rerank.Provider,
            Mode = retrieval.Rerank.Mode,
            CandidateLimit = retrieval.Rerank.CandidateLimit,
            MaxCandidateChars = retrieval.Rerank.MaxCandidateChars,
            ContentField = string.IsNullOrWhiteSpace(retrieval.Rerank.ContentField)
                ? contentField
                : retrieval.Rerank.ContentField,
            RerankScoreWeight = retrieval.Rerank.RerankScoreWeight,
            OriginalScoreWeight = retrieval.Rerank.OriginalScoreWeight,
            TimeoutSeconds = retrieval.Rerank.TimeoutSeconds,
            MaxOutputBytes = retrieval.Rerank.MaxOutputBytes,
            FallbackOnFailure = retrieval.Rerank.FallbackOnFailure
        };
    }

    private static ContextAssemblyPlan BuildAssemblyPlan(RagContextRequest request)
    {
        if (request.ContextAssembly is null)
        {
            return new ContextAssemblyPlan(
                Enabled: false,
                AuthorityOrdering: false,
                GroupBy: null,
                GroupByPath: null,
                DefaultMaxChunksPerGroup: null,
                DefaultMaxCharsPerGroup: null,
                FailOnUnsatisfiedRequiredGroups: false,
                Rules: new Dictionary<string, ContextGroupRule>(StringComparer.Ordinal));
        }

        var groupBy = NormalizeGroupBy(request.ContextAssembly.GroupBy);
        var groupByPath = request.ContextAssembly.GroupByPath;

        if (string.IsNullOrWhiteSpace(groupBy) &&
            (!string.IsNullOrWhiteSpace(groupByPath) || request.ContextAssembly.Groups.Count > 0 ||
             request.ContextAssembly.DefaultMaxChunksPerGroup.HasValue || request.ContextAssembly.DefaultMaxCharsPerGroup.HasValue))
        {
            groupBy = "jsonPointer";
        }

        var rules = new Dictionary<string, ContextGroupRule>(StringComparer.Ordinal);
        for (var index = 0; index < request.ContextAssembly.Groups.Count; index++)
        {
            var group = request.ContextAssembly.Groups[index];
            rules[group.Key] = new ContextGroupRule(
                Key: group.Key,
                Priority: group.Priority ?? index,
                Required: group.Required,
                MinChunks: group.MinChunks,
                MaxChunks: group.MaxChunks,
                MinChars: group.MinChars,
                MaxChars: group.MaxChars);
        }

        return new ContextAssemblyPlan(
            Enabled: true,
            AuthorityOrdering: rules.Count > 0,
            GroupBy: groupBy,
            GroupByPath: NormalizeGroupByPath(groupBy, groupByPath),
            DefaultMaxChunksPerGroup: request.ContextAssembly.DefaultMaxChunksPerGroup,
            DefaultMaxCharsPerGroup: request.ContextAssembly.DefaultMaxCharsPerGroup,
            FailOnUnsatisfiedRequiredGroups: request.ContextAssembly.FailOnUnsatisfiedRequiredGroups,
            Rules: rules);
    }

    private static IReadOnlyList<ContextCandidate> OrderCandidates(
        IReadOnlyList<ContextCandidate> candidates,
        ContextAssemblyPlan plan,
        int maxCharsPerChunk)
    {
        if (!plan.AuthorityOrdering)
        {
            return candidates;
        }

        var ordered = new List<ContextCandidate>();
        var selected = new HashSet<ContextCandidate>();

        foreach (var rule in plan.Rules.Values.OrderBy(rule => rule.Priority).ThenBy(rule => rule.Key, StringComparer.Ordinal))
        {
            if (!rule.Required && !rule.MinChunks.HasValue && !rule.MinChars.HasValue)
            {
                continue;
            }

            var minimumChunks = rule.MinChunks ?? (rule.Required ? 1 : 0);
            var minimumChars = rule.MinChars ?? 0;
            var candidateChunks = 0;
            var candidateChars = 0;

            foreach (var candidate in candidates.Where(candidate => string.Equals(candidate.GroupKey, rule.Key, StringComparison.Ordinal)))
            {
                if (candidateChunks >= minimumChunks && candidateChars >= minimumChars)
                {
                    break;
                }

                if (selected.Add(candidate))
                {
                    ordered.Add(candidate);
                    candidateChunks++;
                    candidateChars += Math.Min(candidate.Text.Length, maxCharsPerChunk);
                }
            }
        }

        ordered.AddRange(candidates
            .Where(candidate => !selected.Contains(candidate))
            .OrderBy(candidate => plan.PriorityFor(candidate.GroupKey))
            .ThenBy(candidate => candidate.Match.Rank)
            .ThenBy(candidate => candidate.Match.Collection, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Match.Record.PartitionKey, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Match.Record.Id, StringComparer.Ordinal));

        return ordered;
    }

    private static Dictionary<string, ContextGroupEvaluation> EvaluateGroups(
        ContextAssemblyPlan plan,
        IReadOnlyDictionary<string, ContextGroupStats> groupStats)
    {
        var keys = groupStats.Keys
            .Concat(plan.Rules.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var evaluations = new Dictionary<string, ContextGroupEvaluation>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            groupStats.TryGetValue(key, out var stats);
            stats ??= new ContextGroupStats();
            var rule = plan.FindRule(key);
            var required = rule?.Required ?? false;
            var minChunks = rule?.MinChunks ?? (required ? 1 : 0);
            var minChars = rule?.MinChars ?? 0;
            var satisfied = stats.ChunkCount >= minChunks && stats.CharCount >= minChars;

            evaluations[key] = new ContextGroupEvaluation
            {
                CandidateCount = stats.CandidateCount,
                ChunkCount = stats.ChunkCount,
                CharCount = stats.CharCount,
                Priority = rule?.Priority ?? plan.PriorityFor(key),
                Required = required,
                Satisfied = satisfied,
                MinChunks = minChunks,
                MaxChunks = rule?.MaxChunks ?? plan.DefaultMaxChunksPerGroup ?? 0,
                MinChars = minChars,
                MaxChars = rule?.MaxChars ?? plan.DefaultMaxCharsPerGroup ?? 0
            };
        }

        return evaluations;
    }

    private static string? ResolveGroupKey(RetrievalMatch match, ContextAssemblyPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.GroupBy))
        {
            return null;
        }

        return plan.GroupBy switch
        {
            "collection" => string.IsNullOrWhiteSpace(match.Collection) ? "(missing)" : match.Collection,
            "sourceKind" => FirstSourceKind(match.Record) ?? "(missing)",
            "metadata" or "jsonPointer" => ResolveJsonPointerGroupKey(match.Record, plan.GroupByPath),
            _ => "(missing)"
        };
    }

    private static string? ResolveJsonPointerGroupKey(VyralRecord record, string? groupByPath)
    {
        if (string.IsNullOrWhiteSpace(groupByPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(record));
        return TryGetJsonPointerValue(document.RootElement, groupByPath, out var value)
            ? JsonElementToGroupKey(value)
            : "(missing)";
    }

    private static string? FirstSourceKind(VyralRecord record)
    {
        return record.Sources?
            .Select(source => source.Kind)
            .FirstOrDefault(kind => !string.IsNullOrWhiteSpace(kind));
    }

    private static ContextGroupStats GetOrCreateGroupStats(Dictionary<string, ContextGroupStats> groupStats, string groupKey)
    {
        if (!groupStats.TryGetValue(groupKey, out var stats))
        {
            stats = new ContextGroupStats();
            groupStats[groupKey] = stats;
        }

        return stats;
    }

    private static string JsonElementToGroupKey(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            JsonValueKind.Null or JsonValueKind.Undefined => "(null)",
            _ => value.GetRawText()
        };
    }

    private static bool TryGetJsonPointerValue(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var rawSegment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out value))
                {
                    return false;
                }

                continue;
            }

            if (value.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, out var index) &&
                index >= 0 &&
                index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }

            return false;
        }

        return true;
    }

    private static ContextExcerpt TrimToBudget(string text, int budget)
    {
        if (text.Length <= budget)
        {
            return new ContextExcerpt(text, 0, text.Length, false);
        }

        if (budget <= 3)
        {
            return new ContextExcerpt(text[..budget], 0, budget, true);
        }

        var charEnd = budget - 3;
        return new ContextExcerpt(text[..charEnd] + "...", 0, charEnd, true);
    }

    private static string Sha256Hex(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string? NormalizeGroupBy(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
        {
            return null;
        }

        return groupBy.Trim().ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal) switch
        {
            "collection" => "collection",
            "sourcekind" => "sourceKind",
            "metadata" => "metadata",
            "jsonpointer" => "jsonPointer",
            _ => groupBy.Trim()
        };
    }

    private static string? NormalizeGroupByPath(string? groupBy, string? groupByPath)
    {
        if (string.IsNullOrWhiteSpace(groupByPath))
        {
            return null;
        }

        if (groupBy == "metadata" && !groupByPath.StartsWith("/", StringComparison.Ordinal))
        {
            return "/metadata/" + groupByPath
                .Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal);
        }

        return groupByPath;
    }

    private sealed record ContextExcerpt(string Text, int CharStart, int CharEnd, bool Truncated);
    private sealed record GraphProvenanceResult(List<RagContextGraphProvenance> Items, int OmittedCount);
    private sealed record SeedValue(string? RawValue);
    private sealed record GraphSeedResolution(
        List<string> SeedNodeIds,
        List<RagContextGraphSeedDiagnostic> Diagnostics,
        int OmittedDiagnostics,
        int DroppedSeedCount,
        int SeedCandidateCount);

    private sealed record ContextCandidate(RetrievalMatch Match, string Text, string? GroupKey);

    private sealed record ContextAssemblyPlan(
        bool Enabled,
        bool AuthorityOrdering,
        string? GroupBy,
        string? GroupByPath,
        int? DefaultMaxChunksPerGroup,
        int? DefaultMaxCharsPerGroup,
        bool FailOnUnsatisfiedRequiredGroups,
        IReadOnlyDictionary<string, ContextGroupRule> Rules)
    {
        private const int UnconfiguredPriority = 1_000_000;

        public ContextGroupRule? FindRule(string key)
        {
            return Rules.TryGetValue(key, out var rule) ? rule : null;
        }

        public int PriorityFor(string? key)
        {
            if (key is not null && Rules.TryGetValue(key, out var rule))
            {
                return rule.Priority;
            }

            return UnconfiguredPriority;
        }
    }

    private sealed record ContextGroupRule(
        string Key,
        int Priority,
        bool Required,
        int? MinChunks,
        int? MaxChunks,
        int? MinChars,
        int? MaxChars);

    private sealed class ContextGroupStats
    {
        public int CandidateCount { get; set; }
        public int ChunkCount { get; set; }
        public int CharCount { get; set; }
    }

    private sealed class ContextGroupEvaluation
    {
        public int CandidateCount { get; set; }
        public int ChunkCount { get; set; }
        public int CharCount { get; set; }
        public int Priority { get; set; }
        public bool Required { get; set; }
        public bool Satisfied { get; set; }
        public int MinChunks { get; set; }
        public int MaxChunks { get; set; }
        public int MinChars { get; set; }
        public int MaxChars { get; set; }
    }
}
