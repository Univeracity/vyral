using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

/// <summary>
/// Optional candidate-only search projection for providers that can prove immutable generation
/// identity and complete requested-partition coverage. It does not replace
/// <see cref="IRecordSearchProjection"/> for simple eventual projections and does not grant access,
/// select application policy, or return canonical records.
/// </summary>
public interface IGenerationBoundRecordSearchProjection
{
    /// <summary>
    /// Inspects immutable evidence plus current eligibility observations. A null generation ID
    /// selects the active generation. Mutable health never substitutes for descriptor completeness.
    /// </summary>
    Task<RecordSearchProjectionGenerationInspection?> InspectGenerationAsync(
        RecordCollectionPolicy policy,
        string? generationId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns candidates only after every requested partition is covered by one retained immutable
    /// generation. Incomplete and unavailable outcomes fail closed with no candidates.
    /// </summary>
    Task<GenerationBoundRecordSearchProjectionResult> SearchGenerationAsync(
        RecordCollectionPolicy policy,
        GenerationBoundRecordSearchProjectionRequest request,
        CancellationToken ct = default);
}
