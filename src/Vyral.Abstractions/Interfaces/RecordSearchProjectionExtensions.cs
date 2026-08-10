using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public static class RecordSearchProjectionExtensions
{
    /// <summary>
    /// Hydrates eventual candidates from the canonical store. A missing record
    /// or revision mismatch is intentionally omitted rather than exposing a
    /// stale projection document to the caller.
    /// </summary>
    public static async Task<HydratedRecordSearchProjectionResult> SearchAndHydrateAsync(
        this IRecordSearchProjection projection,
        IRecordCollectionStore canonicalStore,
        RecordCollectionPolicy policy,
        QueryEnvelope query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(canonicalStore);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(query);

        var candidates = await projection.SearchAsync(policy, query, ct);
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

        return new HydratedRecordSearchProjectionResult
        {
            Items = items,
            ContinuationToken = candidates.ContinuationToken,
            Consistency = candidates.Consistency,
            StaleCandidatesDiscarded = stale
        };
    }
}
