namespace Vyral.Abstractions.Interfaces;

/// <summary>
/// Resolves an evaluation target from host-owned registrations. Implementations must not interpret
/// the target id as a URL, assembly name, provider type, or other caller-selected code location.
/// </summary>
public interface IRetrievalEvaluationTargetResolver
{
    RetrievalEvaluationResolvedTarget Resolve(RetrievalEvaluationTargetReference target);
}

/// <summary>One resolved retrieval service and the immutable identity it will exercise.</summary>
public sealed class RetrievalEvaluationResolvedTarget
{
    public required IRetrievalService Service { get; init; }
    public required RetrievalEvaluationTargetEvidence Evidence { get; init; }
}
