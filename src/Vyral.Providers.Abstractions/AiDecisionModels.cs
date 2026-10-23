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
    public const string LedgerEntryV1 = "vyral.ai-decision-ledger-entry.v1";
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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// Portable record of a branch decision. It records evidence and policy identity; it is not a
/// truth claim, provider attestation, or authorization to execute the selected branch.
/// </summary>
public sealed class AiDecisionReceipt
{
    [JsonRequired]
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = AiDecisionSchemas.ReceiptV1;

    [JsonRequired]
    [JsonPropertyName("policyId")]
    public string PolicyId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("policyVersion")]
    public string PolicyVersion { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("policyHash")]
    public string PolicyHash { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("questionId")]
    public string QuestionId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("answerKind")]
    public string AnswerKind { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = AiDecisionOutcomes.Escalate;

    [JsonRequired]
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("escalationRoute")]
    public string? EscalationRoute { get; set; }

    [JsonRequired]
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonRequired]
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

    [JsonRequired]
    [JsonPropertyName("evidence")]
    public AiDecisionProviderEvidence Evidence { get; set; } = new();

    [JsonRequired]
    [JsonPropertyName("evaluatedAtUtc")]
    public DateTimeOffset EvaluatedAtUtc { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// One privacy-minimized, hash-linked decision receipt. The link is tamper-evident only relative
/// to a trusted head checkpoint; it is not a signature, independent attestation, or authorization.
/// </summary>
public sealed class AiDecisionLedgerEntry
{
    [JsonRequired]
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = AiDecisionSchemas.LedgerEntryV1;

    [JsonRequired]
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    /// <summary>Canonical UTC RFC 3339 timestamp with exactly microsecond precision.</summary>
    [JsonRequired]
    [JsonPropertyName("recordedAtUtc")]
    public string RecordedAtUtc { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("receipt")]
    public AiDecisionReceipt Receipt { get; set; } = new();

    [JsonRequired]
    [JsonPropertyName("receiptHash")]
    public string ReceiptHash { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("previousEntryHash")]
    public string? PreviousEntryHash { get; set; }

    [JsonRequired]
    [JsonPropertyName("entryHash")]
    public string EntryHash { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>Validates, hashes, creates, and verifies portable decision-ledger entries.</summary>
public static class AiDecisionLedger
{
    public const long MaxSafeSequence = 9_007_199_254_740_991;
    private const int MaxIdentifierLength = 256;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        AiDecisionReasons.Accepted,
        AiDecisionReasons.Rejected,
        AiDecisionReasons.NoRuleMatch,
        AiDecisionReasons.ProviderNotSucceeded,
        AiDecisionReasons.InvalidProviderEvidence,
        AiDecisionReasons.InvalidProviderOutput,
        AiDecisionReasons.AnswerMissing,
        AiDecisionReasons.AnswerAmbiguous,
        AiDecisionReasons.AnswerShapeMismatch,
        AiDecisionReasons.Uncalibrated,
        AiDecisionReasons.ConfidenceUnavailable,
        AiDecisionReasons.ConfidenceBelowMinimum,
        AiDecisionReasons.LabelMassCoverageUnavailable,
        AiDecisionReasons.LabelMassCoverageBelowMinimum,
        AiDecisionReasons.RotationAgreementUnavailable,
        AiDecisionReasons.RotationAgreementBelowMinimum,
        AiDecisionReasons.ValidationStatusNotAllowed
    };

    public static IReadOnlyList<string> ValidateReceipt(AiDecisionReceipt? receipt)
    {
        var errors = new List<string>();
        if (receipt is null)
        {
            errors.Add("receipt is required");
            return errors;
        }
        if (!string.Equals(receipt.Schema, AiDecisionSchemas.ReceiptV1, StringComparison.Ordinal))
            errors.Add($"schema must be {AiDecisionSchemas.ReceiptV1}");
        RequireText(receipt.PolicyId, "policyId", errors);
        RequireText(receipt.PolicyVersion, "policyVersion", errors);
        RequireText(receipt.QuestionId, "questionId", errors);
        if (receipt.Provider is null || receipt.Provider.EnumerateRunes().Count() > MaxIdentifierLength)
            errors.Add($"provider must be a string no longer than {MaxIdentifierLength} characters");
        if (!IsRequiredHash(receipt.PolicyHash))
            errors.Add("policyHash must be a canonical sha256 digest");
        if (receipt.AnswerKind is not (AiDecisionAnswerKinds.Choice or AiDecisionAnswerKinds.Probability or AiDecisionAnswerKinds.Score))
            errors.Add("answerKind must be choice, probability, or score");
        if (receipt.Outcome is not (AiDecisionOutcomes.Accept or AiDecisionOutcomes.Reject or AiDecisionOutcomes.Escalate))
            errors.Add("outcome must be accept, reject, or escalate");
        if (!Reasons.Contains(receipt.Reason))
            errors.Add("reason is not recognized");
        if (!Enum.IsDefined(receipt.ProviderRunStatus))
            errors.Add("providerRunStatus is invalid");
        ValidateOptionalText(receipt.EscalationRoute, "escalationRoute", errors);
        ValidateOptionalText(receipt.ProviderStatus, "providerStatus", errors);
        ValidateOptionalText(receipt.ModelId, "modelId", errors);
        ValidateOptionalText(receipt.ValidationStatus, "validationStatus", errors);
        ValidateOptionalText(receipt.Choice, "choice", errors);

        if (receipt.Outcome == AiDecisionOutcomes.Accept)
        {
            if (receipt.Reason != AiDecisionReasons.Accepted)
                errors.Add("accept outcome requires reason accepted");
            if (receipt.EscalationRoute is not null)
                errors.Add("accept outcome cannot include escalationRoute");
        }
        else if (receipt.Outcome == AiDecisionOutcomes.Reject)
        {
            if (receipt.Reason != AiDecisionReasons.Rejected)
                errors.Add("reject outcome requires reason rejected");
            if (receipt.EscalationRoute is not null)
                errors.Add("reject outcome cannot include escalationRoute");
        }
        else if (receipt.Outcome == AiDecisionOutcomes.Escalate)
        {
            if (receipt.Reason is AiDecisionReasons.Accepted or AiDecisionReasons.Rejected)
                errors.Add("escalate outcome cannot use a terminal branch reason");
            if (!IsBoundedText(receipt.EscalationRoute))
                errors.Add("escalate outcome requires escalationRoute");
        }

        if (receipt.AnswerKind == AiDecisionAnswerKinds.Choice)
        {
            if (receipt.Value.HasValue) errors.Add("choice receipt value must be null");
            if (receipt.Outcome is AiDecisionOutcomes.Accept or AiDecisionOutcomes.Reject && !IsBoundedText(receipt.Choice))
                errors.Add("terminal choice receipt requires choice");
        }
        else if (receipt.AnswerKind is AiDecisionAnswerKinds.Probability or AiDecisionAnswerKinds.Score)
        {
            if (receipt.Choice is not null) errors.Add("numeric receipt choice must be null");
            if (receipt.Value.HasValue && !double.IsFinite(receipt.Value.Value))
                errors.Add("numeric receipt value must be null or finite");
            else if (receipt.AnswerKind == AiDecisionAnswerKinds.Probability && receipt.Value is < 0 or > 1)
                errors.Add("probability receipt value must be in [0, 1]");
            else if (receipt.AnswerKind == AiDecisionAnswerKinds.Score && receipt.Value < 0)
                errors.Add("score receipt value must be non-negative");
            if (receipt.Outcome is AiDecisionOutcomes.Accept or AiDecisionOutcomes.Reject && !receipt.Value.HasValue)
                errors.Add("terminal numeric receipt requires value");
        }

        ValidateUnit(receipt.Confidence, "confidence", errors);
        ValidateUnit(receipt.LabelMassCoverage, "labelMassCoverage", errors);
        ValidateUnit(receipt.RotationAgreement, "rotationAgreement", errors);
        if (receipt.Evidence is null)
            errors.Add("evidence is required");
        else
        {
            ValidateOptionalText(receipt.Evidence.TraceId, "evidence.traceId", errors);
            ValidateOptionalText(receipt.Evidence.AdapterId, "evidence.adapterId", errors);
            ValidateOptionalHash(receipt.Evidence.ConfigHash, "evidence.configHash", errors);
            ValidateOptionalHash(receipt.Evidence.InputHash, "evidence.inputHash", errors);
            ValidateOptionalHash(receipt.Evidence.OutputHash, "evidence.outputHash", errors);
            ValidateOptionalHash(receipt.Evidence.AnswerHash, "evidence.answerHash", errors);
            if (receipt.Evidence.AdditionalProperties?.Count > 0)
                errors.Add("evidence contains unknown properties");
        }
        if (receipt.EvaluatedAtUtc == default || receipt.EvaluatedAtUtc.Offset != TimeSpan.Zero)
            errors.Add("evaluatedAtUtc must be a UTC timestamp");
        else if (receipt.EvaluatedAtUtc.Ticks % 10 != 0)
            errors.Add("evaluatedAtUtc cannot be more precise than one microsecond");
        if (receipt.AdditionalProperties?.Count > 0)
            errors.Add("receipt contains unknown properties");
        return errors;
    }

    public static string ComputeReceiptHash(AiDecisionReceipt receipt)
    {
        var errors = ValidateReceipt(receipt).ToList();
        if (errors.Count != 0) throw new ArgumentException(string.Join("; ", errors), nameof(receipt));
        return AiDecisionCanonicalJson.ComputeHash(NormalizeReceipt(receipt));
    }

    public static AiDecisionLedgerEntry CreateEntry(
        AiDecisionReceipt receipt,
        long sequence,
        string? previousEntryHash,
        DateTimeOffset? recordedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var errors = ValidateReceipt(receipt).ToList();
        ValidateSequenceAndPrevious(sequence, previousEntryHash, errors);
        if (errors.Count != 0) throw new ArgumentException(string.Join("; ", errors), nameof(receipt));
        var recorded = TruncateToMicrosecond((recordedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());
        var entry = new AiDecisionLedgerEntry
        {
            Sequence = sequence,
            RecordedAtUtc = FormatTimestamp(recorded),
            Receipt = receipt,
            ReceiptHash = ComputeReceiptHash(receipt),
            PreviousEntryHash = previousEntryHash
        };
        entry.EntryHash = AiDecisionCanonicalJson.ComputeHash(CreateEntryMaterial(entry));
        return entry;
    }

    public static IReadOnlyList<string> ValidateEntry(AiDecisionLedgerEntry? entry)
    {
        var errors = new List<string>();
        if (entry is null)
        {
            errors.Add("entry is required");
            return errors;
        }
        if (!string.Equals(entry.Schema, AiDecisionSchemas.LedgerEntryV1, StringComparison.Ordinal))
            errors.Add($"schema must be {AiDecisionSchemas.LedgerEntryV1}");
        ValidateSequenceAndPrevious(entry.Sequence, entry.PreviousEntryHash, errors);
        if (!TryParseCanonicalTimestamp(entry.RecordedAtUtc, out _))
            errors.Add("recordedAtUtc must use canonical UTC form");
        errors.AddRange(ValidateReceipt(entry.Receipt).Select(error => "receipt." + error));
        if (!IsRequiredHash(entry.ReceiptHash))
            errors.Add("receiptHash must be a canonical sha256 digest");
        if (!IsRequiredHash(entry.EntryHash))
            errors.Add("entryHash must be a canonical sha256 digest");
        if (entry.AdditionalProperties?.Count > 0)
            errors.Add("entry contains unknown properties");
        if (errors.Count != 0) return errors;
        if (!string.Equals(entry.ReceiptHash, ComputeReceiptHash(entry.Receipt), StringComparison.Ordinal))
            errors.Add("receiptHash does not match receipt");
        var expected = AiDecisionCanonicalJson.ComputeHash(CreateEntryMaterial(entry));
        if (!string.Equals(entry.EntryHash, expected, StringComparison.Ordinal))
            errors.Add("entryHash does not match entry");
        return errors;
    }

    public static IReadOnlyList<string> VerifyChain(IReadOnlyList<AiDecisionLedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var errors = new List<string>();
        string? previous = null;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null)
            {
                errors.Add($"entry[{index}] is required");
                previous = null;
                continue;
            }
            errors.AddRange(ValidateEntry(entry).Select(error => $"entry[{index}].{error}"));
            if (entry.Sequence != index) errors.Add($"entry[{index}].sequence is not contiguous");
            if (!string.Equals(entry.PreviousEntryHash, previous, StringComparison.Ordinal))
                errors.Add($"entry[{index}].previousEntryHash does not match");
            previous = entry.EntryHash;
        }
        return errors;
    }

    internal static DateTimeOffset TruncateToMicrosecond(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, value.Offset);

    private static JsonObject NormalizeReceipt(AiDecisionReceipt receipt)
    {
        var node = JsonSerializer.SerializeToNode(receipt, ProviderJson.Options) as JsonObject
            ?? throw new InvalidOperationException("Decision receipt cannot be serialized.");
        node["evaluatedAtUtc"] = FormatTimestamp(receipt.EvaluatedAtUtc);
        return node;
    }

    private static JsonObject CreateEntryMaterial(AiDecisionLedgerEntry entry) => new()
    {
        ["schema"] = AiDecisionSchemas.LedgerEntryV1,
        ["sequence"] = entry.Sequence,
        ["recordedAtUtc"] = entry.RecordedAtUtc,
        ["receipt"] = NormalizeReceipt(entry.Receipt),
        ["receiptHash"] = entry.ReceiptHash,
        ["previousEntryHash"] = entry.PreviousEntryHash
    };

    private static void ValidateSequenceAndPrevious(long sequence, string? previous, List<string> errors)
    {
        if (sequence < 0 || sequence > MaxSafeSequence)
            errors.Add($"sequence must be an integer in [0, {MaxSafeSequence}]");
        if (sequence == 0 && previous is not null)
            errors.Add("genesis entry previousEntryHash must be null");
        else if (sequence > 0 && !IsRequiredHash(previous))
            errors.Add("non-genesis entry previousEntryHash must be a canonical sha256 digest");
    }

    private static bool TryParseCanonicalTimestamp(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(value, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp) &&
        timestamp.Offset == TimeSpan.Zero;

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static void RequireText(string? value, string path, List<string> errors)
    {
        if (!IsBoundedText(value))
            errors.Add($"{path} must be non-empty and no longer than {MaxIdentifierLength} characters");
    }

    private static void ValidateOptionalText(string? value, string path, List<string> errors)
    {
        if (value is not null && !IsBoundedText(value))
            errors.Add($"{path} must be null or a non-empty string no longer than {MaxIdentifierLength} characters");
    }

    private static bool IsBoundedText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.EnumerateRunes().Count() <= MaxIdentifierLength;

    private static void ValidateUnit(double? value, string path, List<string> errors)
    {
        if (value.HasValue && (!double.IsFinite(value.Value) || value.Value is < 0 or > 1))
            errors.Add($"{path} must be null or in [0, 1]");
    }

    private static void ValidateOptionalHash(string? value, string path, List<string> errors)
    {
        if (value is not null && !IsRequiredHash(value))
            errors.Add($"{path} must be null or a canonical sha256 digest");
    }

    private static bool IsRequiredHash(string? value) => value is not null &&
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
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
        return AiDecisionCanonicalJson.ComputeHash(policy);
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
            PolicyHash = AiDecisionCanonicalJson.ComputeHash(policy),
            QuestionId = policy.QuestionId,
            AnswerKind = policy.AnswerKind,
            Provider = result.Provider,
            ProviderRunStatus = result.Status,
            ProviderStatus = result.ProviderStatus,
            ModelId = result.Trace?.ModelId,
            EvaluatedAtUtc = AiDecisionLedger.TruncateToMicrosecond(
                (evaluatedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime()),
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
        receipt.Evidence.AnswerHash = AiDecisionCanonicalJson.ComputeHash(answer);
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

}

internal static class AiDecisionCanonicalJson
{
    internal static string ComputeHash<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, ProviderJson.Options)
            ?? throw new InvalidOperationException("Decision evidence cannot be serialized.");
        return ComputeHash(node);
    }

    internal static string ComputeHash(JsonNode node)
    {
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
