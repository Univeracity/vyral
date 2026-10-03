using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Local;

/// <summary>A batch-scoped local scoring session. Host leasing and transport stay behind this interface.</summary>
public interface ILocalLogprobSessionFactory
{
    string BindingId { get; }
    string ModelId { get; }
    bool RequiresNetwork { get; }
    string Auth => "session-managed";
    ValueTask<ILocalLogprobSession> OpenAsync(CancellationToken ct);
}

public interface ILocalLogprobSession : IAsyncDisposable
{
    int ContextTokens { get; }
    ValueTask<LocalDecisionToken> ResolveLabelAsync(string label, CancellationToken ct);
    /// <summary>Count the complete model-templated prompt, with no truncation or inference.</summary>
    ValueTask<LocalScoringPrompt> PrepareAsync(string prompt, CancellationToken ct);
    /// <summary>Return original full-vocabulary log probabilities at exactly the first output position.
    /// Missing labels must remain missing. No partial-response retries or renormalization.</summary>
    ValueTask<LocalDecisionScore> ScoreAsync(LocalScoringPrompt prompt,
        IReadOnlyList<LocalDecisionToken> labels, CancellationToken ct);
}

public sealed record LocalDecisionToken(int Id, byte[] Bytes);
public sealed record LocalScoringPrompt(int[] TokenIds);
public sealed record LocalLabelLogprob(int Id, byte[] Bytes, double LogProbability);
public sealed record LocalDecisionScore(IReadOnlyList<LocalLabelLogprob> Labels,
    int OutputPositions = 1, bool FullVocabulary = true);

/// <summary>Typed safe refusal; never embed credentials or raw backend errors in Message.</summary>
public sealed class LocalScoringException(string reason, string failureClass) : Exception(reason)
{
    public string Reason { get; } = reason;
    public string FailureClass { get; } = failureClass;
}

public sealed class LocalLogprobJudgeOptions
{
    public int Rotations { get; init; } = 3;
    public int MaxQuestions { get; init; } = 32;
    public int MaxOptions { get; init; } = 26;
    public int MaxScoringCalls { get; init; } = 96;
    public int MaxInputBytes { get; init; } = 65536;
    public int MaxOutputBytes { get; init; } = 131072;
    public int TimeoutSeconds { get; init; } = 120;
}
