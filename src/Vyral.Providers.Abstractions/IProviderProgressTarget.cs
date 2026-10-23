namespace Vyral.Providers.Abstractions;

/// <summary>Optional, awaited observations. Observation failures must never cause replay.</summary>
public interface IProviderProgressTarget : IProviderTarget
{
    Task<ProviderRunResult> RunWithProgressAsync(
        ProviderRunRequest request,
        Func<ProviderProgressObservation, CancellationToken, ValueTask>? observe,
        DateTimeOffset? deadlineUtc = null,
        CancellationToken ct = default);
}

public enum ProviderProgressPhase
{
    RuntimeBinding,
    PromptPreparation,
    PromptPrepared,
    CompletionPending,
    CompletionValidation
}

/// <summary>
/// Closed, non-content observations from one invocation. Token fit is an observation,
/// not usage accounting. CompletionPending denotes dispatch intent, not proof of execution.
/// The execution lifecycle remains authoritative for terminal outcomes.
/// </summary>
public sealed record ProviderProgressObservation
{
    public required ProviderProgressPhase Phase { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset DeadlineUtc { get; init; }
    public int? RuntimeContextTokens { get; init; }
    public int? PromptTokens { get; init; }
    public int? ReservedOutputTokens { get; init; }
    public bool? PromptFitsContext { get; init; }
}
