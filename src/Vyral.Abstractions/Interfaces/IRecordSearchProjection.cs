using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

/// <summary>
/// An optional, eventually-consistent search projection over canonical records.
/// Implementations must never be used as the source of record truth: callers
/// hydrate returned identities from <see cref="IRecordCollectionStore"/> before
/// returning records to an application.
/// </summary>
public interface IRecordSearchProjection
{
    /// <summary>
    /// Applies an idempotent canonical-record change. Implementations must use
    /// <see cref="RecordSearchProjectionChange.Revision"/> to reject an older
    /// delivery after a newer delivery has already been observed.
    /// </summary>
    Task ProjectAsync(RecordSearchProjectionChange change, CancellationToken ct = default);

    /// <summary>
    /// Finds eventual-consistency candidates. Results contain identities and
    /// scores only; their canonical records must be read before use.
    /// </summary>
    Task<RecordSearchProjectionResult> SearchAsync(
        RecordCollectionPolicy policy,
        QueryEnvelope query,
        CancellationToken ct = default);
}

/// <summary>
/// Optional lifecycle operations for a search projection. A projection owns
/// only its derived index, never the canonical collection.
/// </summary>
public interface IRecordSearchProjectionProvisioner
{
    Task EnsureCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default);
    Task DeleteCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default);
}
