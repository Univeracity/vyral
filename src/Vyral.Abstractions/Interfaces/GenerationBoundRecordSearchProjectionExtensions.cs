using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public static class GenerationBoundRecordSearchProjectionExtensions
{
    /// <summary>
    /// Hydrates successful generation-bound candidates from canonical storage. Projection evidence
    /// is preserved verbatim; a missing or differing canonical revision is omitted and counted rather
    /// than allowing the derived index to become authoritative.
    /// </summary>
    public static async Task<HydratedGenerationBoundRecordSearchProjectionResult> SearchGenerationAndHydrateAsync(
        this IGenerationBoundRecordSearchProjection projection,
        IRecordCollectionStore canonicalStore,
        RecordCollectionPolicy policy,
        GenerationBoundRecordSearchProjectionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(canonicalStore);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(request);

        var candidates = await projection.SearchGenerationAsync(policy, request, ct);
        RecordSearchProjectionGenerationContract.ValidateResult(candidates);
        if (candidates.Status != RecordSearchProjectionResultStatuses.Succeeded)
        {
            return new HydratedGenerationBoundRecordSearchProjectionResult { Projection = candidates };
        }

        var items = new List<VyralRecordMatch>(candidates.Items.Count);
        var stale = 0;
        foreach (var candidate in candidates.Items)
        {
            var record = await canonicalStore.GetRecordAsync(
                policy.Name,
                candidate.PartitionKey,
                candidate.Id,
                ct);
            if (record is null || record.Revision != candidate.Revision)
            {
                stale++;
                continue;
            }
            items.Add(new VyralRecordMatch { Record = record, Score = candidate.Score });
        }

        return new HydratedGenerationBoundRecordSearchProjectionResult
        {
            Projection = candidates,
            Items = items,
            StaleCandidatesDiscarded = stale
        };
    }
}
