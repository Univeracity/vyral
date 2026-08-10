using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class ProviderRunRejectionDiagnostic
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = ProviderRejectionSources.Unknown;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "unknown";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("contentUsable")]
    public bool ContentUsable { get; set; }

    [JsonPropertyName("parsedOutputPresent")]
    public bool ParsedOutputPresent { get; set; }

    [JsonPropertyName("structuredOutputAccepted")]
    public bool StructuredOutputAccepted { get; set; }

    [JsonPropertyName("structuredOutputValidationStatus")]
    public string? StructuredOutputValidationStatus { get; set; }

    [JsonPropertyName("parsedOutputDisposition")]
    public string ParsedOutputDisposition { get; set; } = ProviderParsedOutputDispositions.None;

    [JsonPropertyName("decisionAuthority")]
    public string DecisionAuthority { get; set; } = ProviderRejectionDecisionAuthorities.Unknown;

    [JsonPropertyName("processOutcome")]
    public string ProcessOutcome { get; set; } = ProviderProcessOutcomes.Unknown;

    [JsonPropertyName("retryable")]
    public bool Retryable { get; set; }

    [JsonPropertyName("retryRecommendation")]
    public string RetryRecommendation { get; set; } = ProviderRetryRecommendations.DoNotRetry;

    [JsonPropertyName("operatorReviewRecommended")]
    public bool OperatorReviewRecommended { get; set; }
}

public static class ProviderRejectionSources
{
    public const string VyralPolicy = "vyral_policy";
    public const string VyralGuardrail = "vyral_guardrail";
    public const string VyralClassification = "vyral_classification";
    public const string ProviderPolicy = "provider_policy";
    public const string ProviderRuntime = "provider_runtime";
    public const string Configuration = "configuration";
    public const string Unsupported = "unsupported";
    public const string Unknown = "unknown";
}

public static class ProviderParsedOutputDispositions
{
    public const string None = "none";
    public const string NotCapabilityOutput = "not_capability_output";
    public const string QuarantineForOperatorReview = "quarantine_for_operator_review";
    public const string DebugOnly = "debug_only";
}

public static class ProviderRejectionDecisionAuthorities
{
    public const string VyralPreflight = "vyral_preflight";
    public const string VyralPolicy = "vyral_policy";
    public const string VyralGuardrail = "vyral_guardrail";
    public const string VyralStructuredOutputValidation = "vyral_structured_output_validation";
    public const string ProviderProcessExit = "provider_process_exit";
    public const string ProviderResponseStatus = "provider_response_status";
    public const string ServerGuardrail = "server_guardrail";
    public const string Unknown = "unknown";
}

public static class ProviderProcessOutcomes
{
    public const string NotStarted = "not_started";
    public const string ExitZero = "exit_zero";
    public const string ExitNonZero = "exit_nonzero";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
    public const string OutputTruncated = "output_truncated";
    public const string HttpSuccess = "http_success";
    public const string HttpFailure = "http_failure";
    public const string Unknown = "unknown";
}

public static class ProviderRetryRecommendations
{
    public const string DoNotRetry = "do_not_retry";
    public const string RetryAfterBackoff = "retry_after_backoff";
    public const string RetryWithSmallerInput = "retry_with_smaller_input";
    public const string RetryWithRedactedInput = "retry_with_redacted_input";
    public const string RetryWithStricterStructuredOutputInstructions = "retry_with_stricter_structured_output_instructions";
    public const string FixConfiguration = "fix_configuration";
    public const string UseSupportedCapability = "use_supported_capability";
    public const string OperatorReview = "operator_review";
}

public static class ProviderRunRejectionDiagnostics
{
    private static readonly HashSet<string> LocalPolicyStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown_mode",
        "input_limit",
        "output_limit",
        "invalid_output_limit",
        "output_limit_exceeds_policy",
        "invalid_timeout",
        "timeout_exceeds_policy",
        "job_queue_full",
        "concurrency_queue_timeout",
        "rate_limited"
    };

    public static ProviderRunRejectionDiagnostic? Create(
        ProviderRunStatus status,
        string? failureClass,
        string? providerStatus,
        string capability,
        bool parsedOutputPresent = false,
        bool structuredOutputAccepted = false,
        string? structuredOutputValidationStatus = null,
        string? decisionAuthority = null,
        string? processOutcome = null)
    {
        if (status == ProviderRunStatus.Succeeded)
        {
            return null;
        }

        var normalizedFailure = failureClass?.Trim().ToLowerInvariant();
        var normalizedStatus = providerStatus?.Trim();
        var category = ResolveCategory(status, normalizedFailure, normalizedStatus);
        var source = ResolveSource(status, normalizedFailure, normalizedStatus);
        var parsedDisposition = ResolveParsedOutputDisposition(source, category, parsedOutputPresent);
        var retryRecommendation = ResolveRetryRecommendation(status, normalizedFailure, normalizedStatus, category);
        var resolvedDecisionAuthority = string.IsNullOrWhiteSpace(decisionAuthority)
            ? ResolveDecisionAuthority(status, source, category, normalizedFailure, normalizedStatus)
            : decisionAuthority;
        var resolvedProcessOutcome = string.IsNullOrWhiteSpace(processOutcome)
            ? ResolveProcessOutcome(status, normalizedStatus)
            : processOutcome;
        var retryable = retryRecommendation is ProviderRetryRecommendations.RetryAfterBackoff
            or ProviderRetryRecommendations.RetryWithSmallerInput
            or ProviderRetryRecommendations.RetryWithRedactedInput
            or ProviderRetryRecommendations.RetryWithStricterStructuredOutputInstructions
            or ProviderRetryRecommendations.FixConfiguration
            or ProviderRetryRecommendations.UseSupportedCapability;

        return new ProviderRunRejectionDiagnostic
        {
            Source = source,
            Category = category,
            Message = BuildMessage(source, category, capability, parsedOutputPresent),
            ContentUsable = false,
            ParsedOutputPresent = parsedOutputPresent,
            StructuredOutputAccepted = structuredOutputAccepted,
            StructuredOutputValidationStatus = structuredOutputValidationStatus,
            ParsedOutputDisposition = parsedDisposition,
            DecisionAuthority = resolvedDecisionAuthority,
            ProcessOutcome = resolvedProcessOutcome,
            Retryable = retryable,
            RetryRecommendation = retryRecommendation,
            OperatorReviewRecommended = parsedDisposition == ProviderParsedOutputDispositions.QuarantineForOperatorReview ||
                retryRecommendation == ProviderRetryRecommendations.OperatorReview
        };
    }

    private static string ResolveCategory(ProviderRunStatus status, string? failureClass, string? providerStatus)
    {
        if (!string.IsNullOrWhiteSpace(providerStatus))
        {
            if (string.Equals(failureClass, ProviderFailureClasses.Policy, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(providerStatus, out _))
            {
                return "provider_policy";
            }

            return providerStatus.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(failureClass))
        {
            return failureClass.Trim().ToLowerInvariant();
        }

        return status.ToString().ToLowerInvariant();
    }

    private static string ResolveSource(ProviderRunStatus status, string? failureClass, string? providerStatus)
    {
        if (status == ProviderRunStatus.Unsupported)
        {
            return ProviderRejectionSources.Unsupported;
        }

        if (status == ProviderRunStatus.NotConfigured ||
            string.Equals(failureClass, ProviderFailureClasses.Configuration, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderRejectionSources.Configuration;
        }

        if (string.Equals(providerStatus, "tool_plan_leakage", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderRejectionSources.VyralGuardrail;
        }

        if (string.Equals(providerStatus, "invalid_provider_json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(failureClass, ProviderFailureClasses.Schema, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderRejectionSources.VyralClassification;
        }

        if (!string.IsNullOrWhiteSpace(providerStatus) && LocalPolicyStatuses.Contains(providerStatus))
        {
            return ProviderRejectionSources.VyralPolicy;
        }

        if (string.Equals(failureClass, ProviderFailureClasses.Policy, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderRejectionSources.ProviderPolicy;
        }

        if (IsAnyFailureClass(
            failureClass,
            ProviderFailureClasses.Auth,
            ProviderFailureClasses.Quota,
            ProviderFailureClasses.RateLimit,
            ProviderFailureClasses.Timeout,
            ProviderFailureClasses.Cancelled,
            ProviderFailureClasses.Network,
            ProviderFailureClasses.ProviderUnavailable))
        {
            return ProviderRejectionSources.ProviderRuntime;
        }

        return ProviderRejectionSources.Unknown;
    }

    private static string ResolveParsedOutputDisposition(string source, string category, bool parsedOutputPresent)
    {
        if (!parsedOutputPresent)
        {
            return ProviderParsedOutputDispositions.None;
        }

        if (source is ProviderRejectionSources.ProviderPolicy or ProviderRejectionSources.VyralGuardrail ||
            category.Contains("policy", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderParsedOutputDispositions.QuarantineForOperatorReview;
        }

        if (source == ProviderRejectionSources.VyralClassification)
        {
            return ProviderParsedOutputDispositions.DebugOnly;
        }

        return ProviderParsedOutputDispositions.NotCapabilityOutput;
    }

    private static string ResolveRetryRecommendation(ProviderRunStatus status, string? failureClass, string? providerStatus, string category)
    {
        if (status == ProviderRunStatus.Unsupported)
        {
            return ProviderRetryRecommendations.UseSupportedCapability;
        }

        if (status == ProviderRunStatus.NotConfigured ||
            string.Equals(failureClass, ProviderFailureClasses.Configuration, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderRetryRecommendations.FixConfiguration;
        }

        if (category.Contains("input_limit", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("output_limit", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("timeout_exceeds_policy", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderRetryRecommendations.RetryWithSmallerInput;
        }

        if (string.Equals(providerStatus, "tool_plan_leakage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(failureClass, ProviderFailureClasses.Trust, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderRetryRecommendations.OperatorReview;
        }

        if (string.Equals(providerStatus, "invalid_provider_json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(failureClass, ProviderFailureClasses.Schema, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderRetryRecommendations.RetryWithStricterStructuredOutputInstructions;
        }

        if (IsAnyFailureClass(
            failureClass,
            ProviderFailureClasses.RateLimit,
            ProviderFailureClasses.Timeout,
            ProviderFailureClasses.Network,
            ProviderFailureClasses.ProviderUnavailable,
            ProviderFailureClasses.Quota))
        {
            return ProviderRetryRecommendations.RetryAfterBackoff;
        }

        if (string.Equals(failureClass, ProviderFailureClasses.Policy, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderRetryRecommendations.RetryWithRedactedInput;
        }

        return ProviderRetryRecommendations.DoNotRetry;
    }

    private static string ResolveDecisionAuthority(
        ProviderRunStatus status,
        string source,
        string category,
        string? failureClass,
        string? providerStatus)
    {
        if (source == ProviderRejectionSources.VyralGuardrail)
        {
            return ProviderRejectionDecisionAuthorities.VyralGuardrail;
        }

        if (source == ProviderRejectionSources.VyralClassification)
        {
            return ProviderRejectionDecisionAuthorities.VyralStructuredOutputValidation;
        }

        if (source == ProviderRejectionSources.VyralPolicy)
        {
            return ProviderRejectionDecisionAuthorities.VyralPolicy;
        }

        if (source == ProviderRejectionSources.ProviderPolicy ||
            (string.Equals(failureClass, ProviderFailureClasses.Policy, StringComparison.OrdinalIgnoreCase) &&
             int.TryParse(providerStatus, out _)))
        {
            return ProviderRejectionDecisionAuthorities.ProviderProcessExit;
        }

        if (source == ProviderRejectionSources.ProviderRuntime && int.TryParse(providerStatus, out _))
        {
            return ProviderRejectionDecisionAuthorities.ProviderProcessExit;
        }

        if (status is ProviderRunStatus.Unsupported or ProviderRunStatus.NotConfigured ||
            category is "unknown_mode" or "invalid_request")
        {
            return ProviderRejectionDecisionAuthorities.VyralPreflight;
        }

        return ProviderRejectionDecisionAuthorities.Unknown;
    }

    private static string ResolveProcessOutcome(ProviderRunStatus status, string? providerStatus)
    {
        if (string.IsNullOrWhiteSpace(providerStatus))
        {
            return ProviderProcessOutcomes.Unknown;
        }

        if (string.Equals(providerStatus, "cancelled", StringComparison.OrdinalIgnoreCase) ||
            status == ProviderRunStatus.Cancelled)
        {
            return ProviderProcessOutcomes.Cancelled;
        }

        if (status == ProviderRunStatus.TimedOut ||
            string.Equals(providerStatus, "timeout", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerStatus, "server_timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderProcessOutcomes.TimedOut;
        }

        if (string.Equals(providerStatus, "output_limit", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderProcessOutcomes.OutputTruncated;
        }

        if (int.TryParse(providerStatus, out var exitCode))
        {
            return exitCode == 0 ? ProviderProcessOutcomes.ExitZero : ProviderProcessOutcomes.ExitNonZero;
        }

        return ProviderProcessOutcomes.NotStarted;
    }

    private static bool IsAnyFailureClass(string? failureClass, params string[] values)
    {
        return values.Any(value => string.Equals(failureClass, value, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildMessage(string source, string category, string capability, bool parsedOutputPresent)
    {
        var parsedClause = parsedOutputPresent
            ? " Parsed provider output was present, but it is not usable as capability output unless an operator explicitly adopts it outside the provider contract."
            : string.Empty;

        return source switch
        {
            ProviderRejectionSources.VyralGuardrail =>
                $"Vyral rejected the {capability} result because provider output crossed a tool/workspace boundary.{parsedClause}",
            ProviderRejectionSources.VyralClassification =>
                $"Vyral rejected the {capability} result because output did not satisfy the required structured-output contract.{parsedClause}",
            ProviderRejectionSources.VyralPolicy =>
                $"Vyral rejected the {capability} request or result under local mode, input, output, rate, or timeout policy.{parsedClause}",
            ProviderRejectionSources.ProviderPolicy =>
                $"The provider rejected or classified the {capability} run under provider policy.{parsedClause}",
            ProviderRejectionSources.ProviderRuntime =>
                $"The provider did not complete the {capability} run successfully because of runtime, quota, network, cancellation, or availability conditions.{parsedClause}",
            ProviderRejectionSources.Configuration =>
                $"The {capability} run could not execute because provider configuration is incomplete or invalid.{parsedClause}",
            ProviderRejectionSources.Unsupported =>
                $"The provider does not support the requested {capability} operation.{parsedClause}",
            _ =>
                $"The provider run did not succeed. Category: {category}.{parsedClause}"
        };
    }
}
