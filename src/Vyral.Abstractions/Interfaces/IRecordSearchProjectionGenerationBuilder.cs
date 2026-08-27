using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

/// <summary>
/// Experimental host-composition seam for constructing and verifying immutable projection
/// generations. It deliberately excludes activation, rollback, retirement, authorization, and
/// application adoption. Implementations store provider-native bytes through the supplied portable
/// object store and return only a compact verified descriptor receipt.
/// </summary>
public interface IRecordSearchProjectionGenerationBuilder
{
    string BuilderId { get; }

    Task<RecordSearchProjectionGenerationBuildReceipt> BuildAndVerifyAsync(
        RecordSearchProjectionGenerationBuildRequest request,
        IObjectStore artifactStore,
        Func<RecordSearchProjectionGenerationBuildProgress, CancellationToken, Task>? reportProgress = null,
        CancellationToken ct = default);
}
