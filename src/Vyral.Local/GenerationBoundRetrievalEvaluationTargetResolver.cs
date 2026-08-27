using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

/// <summary>
/// Host-owned registration for one exact generation-bound evaluation target. Registrations carry
/// code references directly; target ids are dictionary keys and are never interpreted as dynamic
/// endpoints, assemblies, or provider types.
/// </summary>
public sealed class GenerationBoundRetrievalEvaluationTargetRegistration
{
    public required string Id { get; init; }
    public required IGenerationBoundRecordSearchProjection Projection { get; init; }
    public required IRecordCollectionStore CanonicalStore { get; init; }
    public required RecordCollectionPolicy Policy { get; init; }
    public required string GenerationId { get; init; }
    public required string GenerationDescriptorDigest { get; init; }
}

/// <summary>
/// Resolves explicitly registered candidate-only projection generations for the existing retrieval
/// evaluation metrics. This keeps target selection out of query profiles and makes descriptor
/// substitution an evaluation failure rather than a silent comparison change.
/// </summary>
public sealed class GenerationBoundRetrievalEvaluationTargetResolver : IRetrievalEvaluationTargetResolver
{
    private readonly IReadOnlyDictionary<string, GenerationBoundRetrievalEvaluationTargetRegistration> _targets;

    public GenerationBoundRetrievalEvaluationTargetResolver(
        IEnumerable<GenerationBoundRetrievalEvaluationTargetRegistration> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var registrations = targets.ToList();
        if (registrations.Count == 0)
        {
            throw new InvalidOperationException("At least one generation-bound evaluation target is required.");
        }
        var duplicate = registrations
            .GroupBy(target => target.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Generation-bound evaluation target id '{duplicate.Key}' is duplicated.");
        }
        foreach (var registration in registrations)
        {
            ValidateRegistration(registration);
        }
        _targets = registrations.ToDictionary(target => target.Id, StringComparer.Ordinal);
    }

    public RetrievalEvaluationResolvedTarget Resolve(RetrievalEvaluationTargetReference target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_targets.TryGetValue(target.Id, out var registration))
        {
            throw new InvalidOperationException(
                $"Retrieval evaluation target '{target.Id}' is not registered.");
        }
        if (target.GenerationId is not null &&
            !string.Equals(target.GenerationId, registration.GenerationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Retrieval evaluation target '{target.Id}' does not match the requested generation.");
        }
        if (target.ExpectedGenerationDescriptorDigest is not null &&
            !string.Equals(
                target.ExpectedGenerationDescriptorDigest,
                registration.GenerationDescriptorDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Retrieval evaluation target '{target.Id}' does not match the expected generation descriptor.");
        }
        return new RetrievalEvaluationResolvedTarget
        {
            Service = new GenerationBoundRetrievalServiceAdapter(registration),
            Evidence = new RetrievalEvaluationTargetEvidence
            {
                Id = registration.Id,
                GenerationId = registration.GenerationId,
                GenerationDescriptorDigest = registration.GenerationDescriptorDigest
            }
        };
    }

    private static void ValidateRegistration(
        GenerationBoundRetrievalEvaluationTargetRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.Projection);
        ArgumentNullException.ThrowIfNull(registration.CanonicalStore);
        ArgumentNullException.ThrowIfNull(registration.Policy);
        if (string.IsNullOrWhiteSpace(registration.Id) || registration.Id.Length > 200 ||
            !string.Equals(registration.Id, registration.Id.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A generation-bound evaluation target requires a bounded canonical id.");
        }
        RecordSearchProjectionGenerationContract.ValidateRequest(
            new GenerationBoundRecordSearchProjectionRequest
            {
                GenerationId = registration.GenerationId,
                ExpectedDescriptorDigest = registration.GenerationDescriptorDigest,
                Query = new QueryEnvelope()
            });
        if (string.IsNullOrWhiteSpace(registration.Policy.Name))
        {
            throw new InvalidOperationException(
                "A generation-bound evaluation target requires a collection policy.");
        }
    }
}

/// <summary>
/// Projects an exact candidate-only generation into the existing evaluation service's retrieval
/// input shape. Canonical hydration remains mandatory; stale candidate revisions fail the target
/// rather than silently changing its measured result set.
/// </summary>
public sealed class GenerationBoundRetrievalServiceAdapter : IRetrievalService
{
    private readonly GenerationBoundRetrievalEvaluationTargetRegistration _registration;

    public GenerationBoundRetrievalServiceAdapter(
        GenerationBoundRetrievalEvaluationTargetRegistration registration)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
    }

    public async Task<RetrievalResultEnvelope> SearchAsync(
        RetrievalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Collections is null || request.Collections.Count != 1 ||
            !string.Equals(request.Collections[0], _registration.Policy.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A generation-bound evaluation target requires its exact registered collection.");
        }
        if (request.Limit <= 0)
        {
            throw new InvalidOperationException(
                "A generation-bound evaluation target requires a positive result limit.");
        }
        if (!string.IsNullOrWhiteSpace(request.Profile) || request.MinScore.HasValue ||
            request.Embedding is not null || request.VectorFields is { Count: > 0 } ||
            request.Hybrid is not null || request.Rerank?.Enabled == true ||
            !string.IsNullOrWhiteSpace(request.SearchMode) &&
            !string.Equals(request.SearchMode, SearchModes.Lexical, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "This generation-bound evaluation target supports unprofiled lexical candidate comparison without a score threshold or reranking.");
        }

        var lexical = CloneLexical(request.Lexical);
        if (string.IsNullOrWhiteSpace(lexical.Query))
        {
            lexical.Query = request.Query;
        }
        lexical.Top = request.Limit;
        var generationRequest = new GenerationBoundRecordSearchProjectionRequest
        {
            GenerationId = _registration.GenerationId,
            ExpectedDescriptorDigest = _registration.GenerationDescriptorDigest,
            Query = new QueryEnvelope
            {
                PartitionKeys = request.PartitionKeys?.ToList(),
                Filter = request.Filter,
                Lexical = lexical,
                Limit = request.Limit
            }
        };
        var hydrated = await _registration.Projection.SearchGenerationAndHydrateAsync(
            _registration.CanonicalStore,
            _registration.Policy,
            generationRequest,
            ct);
        var projection = hydrated.Projection;
        if (projection.Status != RecordSearchProjectionResultStatuses.Succeeded)
        {
            throw new InvalidOperationException(
                $"Generation-bound evaluation target failed with '{projection.Failure?.Code ?? "unknown"}'.");
        }
        if (hydrated.StaleCandidatesDiscarded != 0)
        {
            throw new InvalidOperationException(
                "Generation-bound evaluation target encountered stale canonical candidate revisions.");
        }

        var results = hydrated.Items
            .Select((item, index) => new RetrievalMatch
            {
                Rank = index + 1,
                Score = item.Score,
                Collection = _registration.Policy.Name,
                Record = item.Record,
                Diagnostics = new RetrievalDiagnostics
                {
                    CandidateSources = ["projection.generation-bound"],
                    CandidateCounts = new Dictionary<string, int>
                    {
                        ["projection"] = projection.Diagnostics.CandidateCount is { } count
                            ? checked((int)Math.Min(count, int.MaxValue))
                            : projection.Items.Count
                    },
                    ReasonCodes = ["coverage.complete", "generation.pinned"],
                    Details = new Dictionary<string, object?>
                    {
                        ["projectionGenerationId"] = projection.GenerationId,
                        ["projectionGenerationDescriptorDigest"] = projection.GenerationDescriptorDigest,
                        ["projectionCoverageStatus"] = projection.Coverage.Status
                    }
                }
            })
            .ToList();
        return new RetrievalResultEnvelope
        {
            Query = request.Query,
            Results = results,
            Trace = new JsonObject
            {
                ["projectionGenerationId"] = projection.GenerationId,
                ["projectionGenerationDescriptorDigest"] = projection.GenerationDescriptorDigest,
                ["projectionCoverageStatus"] = projection.Coverage.Status,
                ["projectionCandidateCount"] = projection.Diagnostics.CandidateCount,
                ["projectionReturnedCount"] = projection.Diagnostics.ReturnedCount,
                ["projectionStaleCandidatesDiscarded"] = hydrated.StaleCandidatesDiscarded
            }
        };
    }

    private static LexicalSearchOptions CloneLexical(LexicalSearchOptions? source)
    {
        source ??= LexicalSearchOptions.Default;
        return new LexicalSearchOptions
        {
            Query = source.Query,
            Fields = source.Fields?.ToList(),
            Top = source.Top,
            ScanLimit = source.ScanLimit,
            MinScore = source.MinScore,
            Scoring = source.Scoring,
            MatchMode = source.MatchMode,
            FieldBoosts = source.FieldBoosts is null
                ? null
                : new Dictionary<string, float>(source.FieldBoosts, StringComparer.Ordinal),
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
}
