using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

/// <summary>
/// Typed judgment over a fixed option set (<see cref="ProviderCapabilityIds.AiJudge"/>). Unlike the
/// other AI capabilities, no text is generated and no action is proposed: the output is a
/// choice/probability distribution meant to be consumed programmatically. Providers of this shape
/// (a remote scoring API, a local ONNX classifier, or a deterministic mechanics stub) all implement
/// the same request/result contract so callers do not branch on provider family.
/// Calibration is provider-specific; inspect <see cref="AiJudgeAnswer.Calibrated"/>.
/// </summary>
public static class AiJudgeQuestionTypes
{
    /// <summary>Choose exactly one of a fixed set of labeled options; answer carries a probability per option.</summary>
    public const string Choice = "choice";
    /// <summary>A single yes/no-shaped question; answer carries one probability in [0, 1].</summary>
    public const string Noul = "noul";
    /// <summary>
    /// Rate a degree along an ordered scale; answer carries a probability-weighted position
    /// (<see cref="AiJudgeAnswer.Score"/>) plus the full per-level distribution and a
    /// <see cref="AiJudgeAnswer.Legend"/> explaining each level.
    /// </summary>
    public const string Score = "score";
}

public sealed class AiJudgeOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

public sealed class AiJudgeQuestion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = AiJudgeQuestionTypes.Choice;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// Required for <see cref="AiJudgeQuestionTypes.Choice"/> (unordered option set) and
    /// <see cref="AiJudgeQuestionTypes.Score"/> (ordered levels, low to high — list order is the
    /// scale order and determines each level's position for <see cref="AiJudgeAnswer.Score"/>);
    /// ignored for <see cref="AiJudgeQuestionTypes.Noul"/>.
    /// </summary>
    [JsonPropertyName("options")]
    public List<AiJudgeOption> Options { get; set; } = new();
}

/// <summary>
/// A batch of questions against one shared context, answered in a single run. Batching is a
/// first-class part of the contract (not a provider-specific extension) because it is the
/// dominant cost/latency lever for a remote judgment API and a real throughput lever for a
/// local batched-inference provider, even though the two save different resources.
/// </summary>
public sealed class AiJudgeRequest
{
    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("questions")]
    public List<AiJudgeQuestion> Questions { get; set; } = new();

    [JsonPropertyName("references")]
    public List<AiReference> References { get; set; } = new();
}

public sealed class AiJudgeAnswer
{
    [JsonPropertyName("questionId")]
    public string QuestionId { get; set; } = string.Empty;

    /// <summary>Set for <see cref="AiJudgeQuestionTypes.Choice"/>: the id of the chosen option.</summary>
    [JsonPropertyName("choice")]
    public string? Choice { get; set; }

    /// <summary>
    /// Set for <see cref="AiJudgeQuestionTypes.Choice"/> and <see cref="AiJudgeQuestionTypes.Score"/>:
    /// option/level id (from the question's <c>Options</c>) to probability, summing to ~1.
    /// </summary>
    [JsonPropertyName("probabilities")]
    public Dictionary<string, double>? Probabilities { get; set; }

    /// <summary>Set for <see cref="AiJudgeQuestionTypes.Noul"/>: a single probability in [0, 1].</summary>
    [JsonPropertyName("probability")]
    public double? Probability { get; set; }

    /// <summary>
    /// Set for <see cref="AiJudgeQuestionTypes.Score"/>: the probability-weighted position on the
    /// ordered scale, as a 0-based expected level index (0 = the question's first/lowest option,
    /// Options.Count - 1 = the last/highest). Not a normalized [0,1] value.
    /// </summary>
    [JsonPropertyName("score")]
    public double? Score { get; set; }

    /// <summary>
    /// Set for <see cref="AiJudgeQuestionTypes.Score"/>: option/level id to its label, so a caller can
    /// interpret <see cref="Score"/> or <see cref="Probabilities"/> without re-reading the question.
    /// </summary>
    [JsonPropertyName("legend")]
    public Dictionary<string, string>? Legend { get; set; }

    /// <summary>
    /// Confidence in the winning answer, in [0, 1]. Definition is provider-specific (e.g. the winning
    /// class's probability); compare within one provider, not across providers.
    /// </summary>
    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    /// <summary>
    /// Optional, provider-specific enrichment for <see cref="AiJudgeQuestionTypes.Noul"/>: the full
    /// raw class distribution, keyed by the provider's own class names, for an implementer whose
    /// underlying model natively produces more than a single yes/no split. <see cref="Probability"/>
    /// remains the portable, primary field (conventionally the "yes"/entailment class's mass) that
    /// every implementer populates; this is additive detail only a genuine multi-class classifier can
    /// honestly report. A single-claim entailment/neutral/contradiction checkpoint is the motivating
    /// case — that native 3-way split answers a claim-verification question ("does this passage
    /// support, contradict, or simply not address this claim") directly, without needing to be routed
    /// through <see cref="AiJudgeQuestionTypes.Choice"/>'s multi-hypothesis comparison, which is
    /// shaped for topic selection, not single-claim verification, and collapses onto whichever
    /// candidate hypothesis reads least like the passage's topic when misused for this. Null (not a
    /// zero-filled map) for any implementer without a native class breakdown to report.
    /// </summary>
    [JsonPropertyName("rawClassProbabilities")]
    public Dictionary<string, double>? RawClassProbabilities { get; set; }

    /// <summary>
    /// True only when the underlying probability model has been fit against labeled outcomes
    /// (e.g. a temperature-scaling calibration). False means the probability is a heuristic proxy
    /// (an uncalibrated model score, or a deterministic mechanics stub) and must not be treated as
    /// a validated likelihood by callers doing downstream thresholding or risk-weighting.
    /// </summary>
    [JsonPropertyName("calibrated")]
    public bool Calibrated { get; set; }
}

public sealed class AiJudgeResult
{
    [JsonPropertyName("answers")]
    public List<AiJudgeAnswer> Answers { get; set; } = new();

    [JsonPropertyName("validationStatus")]
    public string ValidationStatus { get; set; } = AiValidationStatuses.NotValidated;

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }
}
