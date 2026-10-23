using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

/// <summary>Versioned schemas for application-owned decisions made from <c>ai.judge</c> evidence.</summary>
public static class AiDecisionSchemas
{
    public const string PolicyV1 = "vyral.ai-decision-policy.v1";
    public const string ReceiptV1 = "vyral.ai-decision-receipt.v1";
}

public static class AiDecisionAnswerKinds
{
    public const string Choice = "choice";
    public const string Probability = "probability";
    public const string Score = "score";
}

public static class AiDecisionOperators
{
    public const string In = "in";
    public const string AtLeast = "at_least";
    public const string AtMost = "at_most";
}

public static class AiDecisionOutcomes
{
    public const string Accept = "accept";
    public const string Reject = "reject";
    public const string Escalate = "escalate";
}

public static class AiDecisionReasons
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string NoRuleMatch = "no_rule_match";
    public const string ProviderNotSucceeded = "provider_not_succeeded";
    public const string InvalidProviderEvidence = "invalid_provider_evidence";
    public const string InvalidProviderOutput = "invalid_provider_output";
    public const string AnswerMissing = "answer_missing";
    public const string AnswerAmbiguous = "answer_ambiguous";
    public const string AnswerShapeMismatch = "answer_shape_mismatch";
    public const string Uncalibrated = "uncalibrated";
    public const string ConfidenceUnavailable = "confidence_unavailable";
    public const string ConfidenceBelowMinimum = "confidence_below_minimum";
    public const string LabelMassCoverageUnavailable = "label_mass_coverage_unavailable";
    public const string LabelMassCoverageBelowMinimum = "label_mass_coverage_below_minimum";
    public const string RotationAgreementUnavailable = "rotation_agreement_unavailable";
    public const string RotationAgreementBelowMinimum = "rotation_agreement_below_minimum";
    public const string ValidationStatusNotAllowed = "validation_status_not_allowed";
}

/// <summary>
/// One explicit branch predicate. Choice predicates use <c>in</c> with <see cref="Values"/>;
/// probability and score predicates use <c>at_least</c> or <c>at_most</c> with
/// <see cref="Threshold"/>.
/// </summary>
public sealed class AiDecisionPredicate
{
    [JsonRequired]
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = string.Empty;

    [JsonPropertyName("values")]
    public List<string> Values { get; set; } = new();

    [JsonPropertyName("threshold")]
    public double? Threshold { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// Minimum evidence needed before a model-backed answer may select an application branch.
/// Defaults fail closed on uncalibrated answers. Other thresholds remain application-owned.
/// </summary>
public sealed class AiDecisionEvidenceRequirements
{
    [JsonPropertyName("requireCalibrated")]
    public bool RequireCalibrated { get; set; } = true;

    [JsonPropertyName("minimumConfidence")]
    public double? MinimumConfidence { get; set; }

    [JsonPropertyName("minimumLabelMassCoverage")]
    public double? MinimumLabelMassCoverage { get; set; }

    [JsonPropertyName("minimumRotationAgreement")]
    public double? MinimumRotationAgreement { get; set; }

    /// <summary>An empty list accepts any reported validation status while preserving it in the receipt.</summary>
    [JsonPropertyName("allowedValidationStatuses")]
    public List<string> AllowedValidationStatuses { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// Application-owned policy that turns exactly one bounded <c>ai.judge</c> answer into an
/// accept, reject, or escalation outcome. It never names a provider and never invokes the
/// escalation route; routing authority remains with the caller.
/// </summary>
public sealed class AiDecisionPolicy
{
    [JsonRequired]
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = AiDecisionSchemas.PolicyV1;

    [JsonRequired]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("questionId")]
    public string QuestionId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("answerKind")]
    public string AnswerKind { get; set; } = AiDecisionAnswerKinds.Choice;

    [JsonRequired]
    [JsonPropertyName("accept")]
    public AiDecisionPredicate Accept { get; set; } = new();

    [JsonRequired]
    [JsonPropertyName("reject")]
    public AiDecisionPredicate Reject { get; set; } = new();

    [JsonRequired]
    [JsonPropertyName("evidence")]
    public AiDecisionEvidenceRequirements Evidence { get; set; } = new();

    /// <summary>Opaque caller-owned route label returned when the evaluator abstains.</summary>
    [JsonRequired]
    [JsonPropertyName("escalationRoute")]
    public string EscalationRoute { get; set; } = "human";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>Privacy-minimized provider evidence retained with an application decision.</summary>
public sealed class AiDecisionProviderEvidence
{
    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("adapterId")]
    public string? AdapterId { get; set; }

    [JsonPropertyName("configHash")]
    public string? ConfigHash { get; set; }

    [JsonPropertyName("inputHash")]
    public string? InputHash { get; set; }

    [JsonPropertyName("outputHash")]
    public string? OutputHash { get; set; }

    [JsonPropertyName("answerHash")]
    public string? AnswerHash { get; set; }
}

/// <summary>
/// Portable record of a branch decision. It records evidence and policy identity; it is not a
/// truth claim, provider attestation, or authorization to execute the selected branch.
/// </summary>
public sealed class AiDecisionReceipt
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = AiDecisionSchemas.ReceiptV1;

    [JsonPropertyName("policyId")]
    public string PolicyId { get; set; } = string.Empty;

    [JsonPropertyName("policyVersion")]
    public string PolicyVersion { get; set; } = string.Empty;

    [JsonPropertyName("policyHash")]
    public string PolicyHash { get; set; } = string.Empty;

    [JsonPropertyName("questionId")]
    public string QuestionId { get; set; } = string.Empty;

    [JsonPropertyName("answerKind")]
    public string AnswerKind { get; set; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = AiDecisionOutcomes.Escalate;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("escalationRoute")]
    public string? EscalationRoute { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("providerRunStatus")]
    public ProviderRunStatus ProviderRunStatus { get; set; }

    [JsonPropertyName("providerStatus")]
    public string? ProviderStatus { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("validationStatus")]
    public string? ValidationStatus { get; set; }

    [JsonPropertyName("choice")]
    public string? Choice { get; set; }

    [JsonPropertyName("value")]
    public double? Value { get; set; }

    [JsonPropertyName("calibrated")]
    public bool? Calibrated { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("labelMassCoverage")]
    public double? LabelMassCoverage { get; set; }

    [JsonPropertyName("rotationAgreement")]
    public double? RotationAgreement { get; set; }

    [JsonPropertyName("evidence")]
    public AiDecisionProviderEvidence Evidence { get; set; } = new();

    [JsonPropertyName("evaluatedAtUtc")]
    public DateTimeOffset EvaluatedAtUtc { get; set; }
}

/// <summary>Validates and deterministically evaluates application-owned decision policies.</summary>
public static class AiDecisionPolicyEvaluator
{
    private const int MaxIdentifierLength = 256;
    private const int MaxChoiceValues = 128;
    public static IReadOnlyList<string> Validate(AiDecisionPolicy? policy, string? capability = ProviderCapabilityIds.AiJudge)
    {
        var errors = new List<string>();
        if (policy is null)
        {
            errors.Add("policy is required");
            return errors;
        }
        if (!string.Equals(capability, ProviderCapabilityIds.AiJudge, StringComparison.OrdinalIgnoreCase))
            errors.Add("decision policies require capability ai.judge");
        RequireText(policy.Schema, "schema", errors);
        if (!string.Equals(policy.Schema, AiDecisionSchemas.PolicyV1, StringComparison.Ordinal))
            errors.Add($"schema must be {AiDecisionSchemas.PolicyV1}");
        RequireText(policy.Id, "id", errors);
        RequireText(policy.Version, "version", errors);
        RequireText(policy.QuestionId, "questionId", errors);
        RequireText(policy.EscalationRoute, "escalationRoute", errors);
        if (policy.Accept is null) errors.Add("accept is required");
        if (policy.Reject is null) errors.Add("reject is required");
        if (policy.Evidence is null) errors.Add("evidence is required");
        if (policy.AdditionalProperties?.Count > 0) errors.Add("policy contains unknown properties");
        if (errors.Count != 0) return errors;

        var kind = policy.AnswerKind;
        if (kind is not (AiDecisionAnswerKinds.Choice or AiDecisionAnswerKinds.Probability or AiDecisionAnswerKinds.Score))
            errors.Add("answerKind must be choice, probability, or score");
        var accept = policy.Accept!;
        var reject = policy.Reject!;
        if (accept.AdditionalProperties?.Count > 0) errors.Add("accept contains unknown properties");
        if (reject.AdditionalProperties?.Count > 0) errors.Add("reject contains unknown properties");
        ValidatePredicate(accept, "accept", kind, errors);
        ValidatePredicate(reject, "reject", kind, errors);

        if (kind == AiDecisionAnswerKinds.Choice && accept.Values is not null && reject.Values is not null)
        {
            var overlap = accept.Values.Intersect(reject.Values, StringComparer.Ordinal).FirstOrDefault();
            if (overlap is not null) errors.Add("accept and reject choice values must be disjoint");
        }
        else if (accept.Threshold.HasValue && reject.Threshold.HasValue)
        {
            if (accept.Operator == reject.Operator)
                errors.Add("numeric accept and reject predicates must use opposing operators");
            else
            {
                var lower = accept.Operator == AiDecisionOperators.AtLeast ? accept.Threshold.Value : reject.Threshold.Value;
                var upper = accept.Operator == AiDecisionOperators.AtMost ? accept.Threshold.Value : reject.Threshold.Value;
                if (lower <= upper) errors.Add("numeric accept and reject ranges must not overlap");
            }
        }

        ValidateUnit(policy.Evidence!.MinimumConfidence, "evidence.minimumConfidence", errors);
        ValidateUnit(policy.Evidence.MinimumLabelMassCoverage, "evidence.minimumLabelMassCoverage", errors);
        ValidateUnit(policy.Evidence.MinimumRotationAgreement, "evidence.minimumRotationAgreement", errors);
        if (policy.Evidence.AllowedValidationStatuses is null)
            errors.Add("evidence.allowedValidationStatuses must be an array");
        else if (policy.Evidence.AllowedValidationStatuses.Count > MaxChoiceValues)
            errors.Add($"evidence.allowedValidationStatuses cannot exceed {MaxChoiceValues} values");
        else if (policy.Evidence.AllowedValidationStatuses.Any(status => !IsBoundedText(status)) ||
            policy.Evidence.AllowedValidationStatuses.Distinct(StringComparer.Ordinal).Count() != policy.Evidence.AllowedValidationStatuses.Count)
            errors.Add("evidence.allowedValidationStatuses must contain unique non-empty values");
        if (policy.Evidence.AdditionalProperties?.Count > 0) errors.Add("evidence contains unknown properties");
        return errors;
    }

    public static string ComputePolicyHash(AiDecisionPolicy policy)
    {
        var errors = Validate(policy);
        if (errors.Count != 0) throw new ArgumentException(string.Join("; ", errors), nameof(policy));
        return CanonicalHash(policy);
    }

    public static AiDecisionReceipt Evaluate(
        AiDecisionPolicy policy,
        ProviderRunResult result,
        DateTimeOffset? evaluatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var errors = Validate(policy, result.Capability);
        if (errors.Count != 0) throw new ArgumentException(string.Join("; ", errors), nameof(policy));

        var invalidProviderEvidence = result.Trace is not null &&
            (!IsOptionalHash(result.Trace.ConfigHash) ||
             !IsOptionalHash(result.Trace.InputHash) ||
             !IsOptionalHash(result.Trace.OutputHash));
        var receipt = new AiDecisionReceipt
        {
            PolicyId = policy.Id,
            PolicyVersion = policy.Version,
            PolicyHash = CanonicalHash(policy),
            QuestionId = policy.QuestionId,
            AnswerKind = policy.AnswerKind,
            Provider = result.Provider,
            ProviderRunStatus = result.Status,
            ProviderStatus = result.ProviderStatus,
            ModelId = result.Trace?.ModelId,
            EvaluatedAtUtc = evaluatedAtUtc ?? DateTimeOffset.UtcNow,
            Evidence = new AiDecisionProviderEvidence
            {
                TraceId = result.Trace?.TraceId,
                AdapterId = result.Trace?.AdapterId,
                ConfigHash = IsOptionalHash(result.Trace?.ConfigHash) ? result.Trace?.ConfigHash : null,
                InputHash = IsOptionalHash(result.Trace?.InputHash) ? result.Trace?.InputHash : null,
                OutputHash = IsOptionalHash(result.Trace?.OutputHash) ? result.Trace?.OutputHash : null
            }
        };

        if (invalidProviderEvidence)
            return Escalate(receipt, policy, AiDecisionReasons.InvalidProviderEvidence);
        if (result.Status != ProviderRunStatus.Succeeded)
            return Escalate(receipt, policy, AiDecisionReasons.ProviderNotSucceeded);

        AiJudgeResult? judge;
        if (result.Output is null)
            return Escalate(receipt, policy, AiDecisionReasons.InvalidProviderOutput);
        try
        {
            judge = result.Output.Deserialize<AiJudgeResult>(ProviderJson.Options);
        }
        catch (JsonException)
        {
            return Escalate(receipt, policy, AiDecisionReasons.InvalidProviderOutput);
        }
        if (judge is null || !HasValidOutputShape(judge))
            return Escalate(receipt, policy, AiDecisionReasons.InvalidProviderOutput);

        receipt.ModelId ??= judge.ModelId;
        receipt.ValidationStatus = judge.ValidationStatus;
        var matches = judge.Answers.Where(answer => string.Equals(answer.QuestionId, policy.QuestionId, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0) return Escalate(receipt, policy, AiDecisionReasons.AnswerMissing);
        if (matches.Count != 1) return Escalate(receipt, policy, AiDecisionReasons.AnswerAmbiguous);

        var answer = matches[0];
        receipt.Evidence.AnswerHash = CanonicalHash(answer);
        receipt.Calibrated = answer.Calibrated;
        receipt.Confidence = answer.Confidence;
        receipt.LabelMassCoverage = answer.LabelMassCoverage;
        receipt.RotationAgreement = answer.RotationAgreement;
        if (policy.AnswerKind == AiDecisionAnswerKinds.Choice) receipt.Choice = answer.Choice;
        if (policy.AnswerKind == AiDecisionAnswerKinds.Probability) receipt.Value = answer.Probability;
        if (policy.AnswerKind == AiDecisionAnswerKinds.Score) receipt.Value = answer.Score;

        if (!HasExpectedShape(policy.AnswerKind, answer))
            return Escalate(receipt, policy, AiDecisionReasons.AnswerShapeMismatch);
        var evidenceReason = EvidenceFailure(policy.Evidence, judge, answer);
        if (evidenceReason is not null) return Escalate(receipt, policy, evidenceReason);

        if (Matches(policy.Accept, policy.AnswerKind, answer))
            return Decide(receipt, AiDecisionOutcomes.Accept, AiDecisionReasons.Accepted);
        if (Matches(policy.Reject, policy.AnswerKind, answer))
            return Decide(receipt, AiDecisionOutcomes.Reject, AiDecisionReasons.Rejected);
        return Escalate(receipt, policy, AiDecisionReasons.NoRuleMatch);
    }

    private static string? EvidenceFailure(AiDecisionEvidenceRequirements requirements, AiJudgeResult result, AiJudgeAnswer answer)
    {
        if (requirements.AllowedValidationStatuses.Count != 0 &&
            !requirements.AllowedValidationStatuses.Contains(result.ValidationStatus, StringComparer.Ordinal))
            return AiDecisionReasons.ValidationStatusNotAllowed;
        if (requirements.RequireCalibrated && !answer.Calibrated)
            return AiDecisionReasons.Uncalibrated;
        if (requirements.MinimumConfidence.HasValue && !answer.Confidence.HasValue)
            return AiDecisionReasons.ConfidenceUnavailable;
        if (requirements.MinimumConfidence.HasValue && answer.Confidence < requirements.MinimumConfidence)
            return AiDecisionReasons.ConfidenceBelowMinimum;
        if (requirements.MinimumLabelMassCoverage.HasValue && !answer.LabelMassCoverage.HasValue)
            return AiDecisionReasons.LabelMassCoverageUnavailable;
        if (requirements.MinimumLabelMassCoverage.HasValue && answer.LabelMassCoverage < requirements.MinimumLabelMassCoverage)
            return AiDecisionReasons.LabelMassCoverageBelowMinimum;
        if (requirements.MinimumRotationAgreement.HasValue && !answer.RotationAgreement.HasValue)
            return AiDecisionReasons.RotationAgreementUnavailable;
        if (requirements.MinimumRotationAgreement.HasValue && answer.RotationAgreement < requirements.MinimumRotationAgreement)
            return AiDecisionReasons.RotationAgreementBelowMinimum;
        return null;
    }

    private static bool HasExpectedShape(string kind, AiJudgeAnswer answer) => kind switch
    {
        AiDecisionAnswerKinds.Choice => !string.IsNullOrWhiteSpace(answer.Choice),
        AiDecisionAnswerKinds.Probability => answer.Probability is >= 0 and <= 1,
        AiDecisionAnswerKinds.Score => answer.Score.HasValue && double.IsFinite(answer.Score.Value) && answer.Score.Value >= 0,
        _ => false
    };

    private static bool HasValidOutputShape(AiJudgeResult result)
    {
        if (result.Answers is null || result.Answers.Any(answer => answer is null) || result.ValidationStatus is null) return false;
        return result.Answers.All(answer =>
            IsBoundedText(answer.QuestionId) &&
            IsNullableFinite(answer.Probability) &&
            IsNullableFinite(answer.Score) &&
            IsNullableUnit(answer.Confidence) &&
            IsNullableUnit(answer.LabelMassCoverage) &&
            IsNullableUnit(answer.RotationAgreement) &&
            IsProbabilityMap(answer.Probabilities) &&
            IsProbabilityMap(answer.RawClassProbabilities) &&
            IsProbabilityMap(answer.RawLabelProbabilities));
    }

    private static bool IsNullableFinite(double? value) => !value.HasValue || double.IsFinite(value.Value);

    private static bool IsNullableUnit(double? value) =>
        !value.HasValue || (double.IsFinite(value.Value) && value.Value is >= 0 and <= 1);

    private static bool IsProbabilityMap(IReadOnlyDictionary<string, double>? values) =>
        values is null || values.All(pair =>
            IsBoundedText(pair.Key) && double.IsFinite(pair.Value) && pair.Value is >= 0 and <= 1);

    private static bool IsOptionalHash(string? value) => value is null ||
        (value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
         value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0);

    private static bool Matches(AiDecisionPredicate predicate, string kind, AiJudgeAnswer answer)
    {
        if (kind == AiDecisionAnswerKinds.Choice)
            return answer.Choice is not null && predicate.Values.Contains(answer.Choice, StringComparer.Ordinal);
        var value = kind == AiDecisionAnswerKinds.Probability ? answer.Probability!.Value : answer.Score!.Value;
        return predicate.Operator == AiDecisionOperators.AtLeast
            ? value >= predicate.Threshold!.Value
            : value <= predicate.Threshold!.Value;
    }

    private static AiDecisionReceipt Decide(AiDecisionReceipt receipt, string outcome, string reason)
    {
        receipt.Outcome = outcome;
        receipt.Reason = reason;
        receipt.EscalationRoute = null;
        return receipt;
    }

    private static AiDecisionReceipt Escalate(AiDecisionReceipt receipt, AiDecisionPolicy policy, string reason)
    {
        receipt.Outcome = AiDecisionOutcomes.Escalate;
        receipt.Reason = reason;
        receipt.EscalationRoute = policy.EscalationRoute;
        return receipt;
    }

    private static void ValidatePredicate(AiDecisionPredicate predicate, string path, string kind, List<string> errors)
    {
        if (kind == AiDecisionAnswerKinds.Choice)
        {
            if (predicate.Operator != AiDecisionOperators.In) errors.Add($"{path}.operator must be in for choice answers");
            if (predicate.Values is null || predicate.Values.Count == 0 || predicate.Values.Count > MaxChoiceValues)
                errors.Add($"{path}.values must contain 1 through {MaxChoiceValues} choices");
            else if (predicate.Values.Any(value => !IsBoundedText(value)) ||
                predicate.Values.Distinct(StringComparer.Ordinal).Count() != predicate.Values.Count)
                errors.Add($"{path}.values must contain unique non-empty bounded choices");
            if (predicate.Threshold.HasValue) errors.Add($"{path}.threshold must be null for choice answers");
            return;
        }
        if (predicate.Operator is not (AiDecisionOperators.AtLeast or AiDecisionOperators.AtMost))
            errors.Add($"{path}.operator must be at_least or at_most for numeric answers");
        if (predicate.Values is not null && predicate.Values.Count != 0) errors.Add($"{path}.values must be empty for numeric answers");
        if (!predicate.Threshold.HasValue || !double.IsFinite(predicate.Threshold.Value))
            errors.Add($"{path}.threshold must be a finite number");
        else if (kind == AiDecisionAnswerKinds.Probability && predicate.Threshold is < 0 or > 1)
            errors.Add($"{path}.threshold must be in [0, 1] for probability answers");
        else if (kind == AiDecisionAnswerKinds.Score && predicate.Threshold < 0)
            errors.Add($"{path}.threshold must be non-negative for score answers");
    }

    private static void ValidateUnit(double? value, string path, List<string> errors)
    {
        if (value.HasValue && (!double.IsFinite(value.Value) || value.Value is < 0 or > 1))
            errors.Add($"{path} must be in [0, 1]");
    }

    private static void RequireText(string? value, string path, List<string> errors)
    {
        if (!IsBoundedText(value))
            errors.Add($"{path} must be non-empty and no longer than {MaxIdentifierLength} characters");
    }

    private static bool IsBoundedText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.EnumerateRunes().Count() <= MaxIdentifierLength;

    private static string CanonicalHash<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, ProviderJson.Options)
            ?? throw new InvalidOperationException("Decision evidence cannot be serialized.");
        var canonical = new StringBuilder();
        AppendCanonical(node, canonical);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static void AppendCanonical(JsonNode? node, StringBuilder destination)
    {
        switch (node)
        {
            case null:
                destination.Append("null");
                break;
            case JsonObject obj:
                destination.Append('{');
                var firstProperty = true;
                foreach (var property in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!firstProperty) destination.Append(',');
                    firstProperty = false;
                    AppendCanonicalString(property.Key, destination);
                    destination.Append(':');
                    AppendCanonical(property.Value, destination);
                }
                destination.Append('}');
                break;
            case JsonArray array:
                destination.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index != 0) destination.Append(',');
                    AppendCanonical(array[index], destination);
                }
                destination.Append(']');
                break;
            case JsonValue scalar when scalar.TryGetValue<double>(out var number):
                if (!double.IsFinite(number)) throw new JsonException("Decision evidence numbers must be finite.");
                destination.Append(FormatCanonicalNumber(number));
                break;
            case JsonValue scalar when scalar.TryGetValue<bool>(out var boolean):
                destination.Append(boolean ? "true" : "false");
                break;
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                AppendCanonicalString(text, destination);
                break;
            default:
                throw new JsonException("Unsupported decision evidence JSON node.");
        }
    }

    private static void AppendCanonicalString(string value, StringBuilder destination)
    {
        destination.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': destination.Append("\\\""); break;
                case '\\': destination.Append("\\\\"); break;
                case '\b': destination.Append("\\b"); break;
                case '\f': destination.Append("\\f"); break;
                case '\n': destination.Append("\\n"); break;
                case '\r': destination.Append("\\r"); break;
                case '\t': destination.Append("\\t"); break;
                default:
                    if (character is < ' ' or > '~')
                    {
                        destination.Append("\\u");
                        destination.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        destination.Append(character);
                    }
                    break;
            }
        }
        destination.Append('"');
    }

    private static string FormatCanonicalNumber(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = text.IndexOf('E');
        if (exponentAt < 0) return text;
        var exponent = int.Parse(text.AsSpan(exponentAt + 1), NumberStyles.Integer, CultureInfo.InvariantCulture);
        return string.Concat(text.AsSpan(0, exponentAt), "e", exponent.ToString(CultureInfo.InvariantCulture));
    }
}
