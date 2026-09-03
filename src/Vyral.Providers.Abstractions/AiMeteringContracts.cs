using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

/// <summary>Versioned schemas for portable AI metering evidence.</summary>
public static class AiMeteringSchemas
{
    public const string ReceiptV1 = "vyral.ai-metering.v1";
    public const string ReviewV1 = "vyral.ai-metering-review.v1";
}

public static class AiMeteringReceiptKinds
{
    public const string Observation = "observation";
    public const string Summary = "summary";
}

public static class AiMeteringOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
    public const string Rejected = "rejected";
    public const string Unsupported = "unsupported";
    public const string NotConfigured = "not_configured";
    public const string Cancelled = "cancelled";
}

public static class AiMeteringCompleteness
{
    public const string Complete = "complete";
    public const string Partial = "partial";
    public const string Sampled = "sampled";
    public const string Unknown = "unknown";
}

/// <summary>
/// Describes the strongest evidence supporting a receipt. A signature authenticates an assertion;
/// it does not by itself promote the assertion to provider- or hardware-attested evidence.
/// </summary>
public static class AiMeteringAttestationLevels
{
    public const string SelfReported = "self_reported";
    public const string ObserverSigned = "observer_signed";
    public const string ProviderAttested = "provider_attested";
    public const string Reconciled = "reconciled";
    public const string HardwareAttested = "hardware_attested";
}

public static class AiMeteringSources
{
    public const string ProviderResponse = "provider_response";
    public const string ProviderEventStream = "provider_event_stream";
    public const string RunnerObserver = "runner_observer";
    public const string GatewayObserver = "gateway_observer";
    public const string ConsumerInference = "consumer_inference";
}

public static class AiMeteringQualities
{
    public const string Reported = "reported";
    public const string Observed = "observed";
    public const string Estimated = "estimated";
    public const string Reconciled = "reconciled";
    public const string Unknown = "unknown";
}

public static class AiMeteringMeasurementNames
{
    public const string InputTokens = "tokens.input";
    public const string CachedInputTokens = "tokens.input.cached";
    public const string CacheWriteInputTokens = "tokens.input.cache_write";
    public const string OutputTokens = "tokens.output";
    public const string ReasoningOutputTokens = "tokens.output.reasoning";
    public const string TotalTokens = "tokens.total";
    public const string ProviderCalls = "provider.calls";
    public const string ModelCalls = "model.calls";
    public const string Turns = "turns";
    public const string ToolCalls = "tool.calls";
    public const string Retries = "retries";
    public const string BytesIn = "transport.bytes.in";
    public const string BytesOut = "transport.bytes.out";
    public const string PayloadBytesIn = "payload.bytes.in";
    public const string PayloadBytesOut = "payload.bytes.out";
    public const string MessagesIn = "transport.messages.in";
    public const string MessagesOut = "transport.messages.out";
    public const string Artifacts = "artifacts";
    public const string ContextReferences = "context.references";
}

public static class AiMeteringUnits
{
    public const string Tokens = "tokens";
    public const string Count = "count";
    public const string Bytes = "bytes";
}

public static class AiMeteringReviewVerdicts
{
    public const string Verified = "verified";
    public const string VerifiedWithGaps = "verified_with_gaps";
    public const string Rejected = "rejected";
}

public static class AiMeteringRulesets
{
    public const string BasicV1 = "vyral.metering.basic.v1";
}

public static class AiMeteringFindingSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
}

public static class AiMeteringScopeKinds
{
    public const string ProviderThread = "provider_thread";
    public const string RunnerSession = "runner_session";
}

public sealed class AiMeteringReceipt
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = AiMeteringSchemas.ReceiptV1;

    [JsonPropertyName("receiptId")]
    public string ReceiptId { get; set; } = "amr_" + Guid.NewGuid().ToString("N");

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = AiMeteringReceiptKinds.Summary;

    [JsonPropertyName("subject")]
    public AiMeteringSubject Subject { get; set; } = new();

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = string.Empty;

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = AiMeteringOutcomes.Failed;

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("adapterId")]
    public string? AdapterId { get; set; }

    [JsonPropertyName("period")]
    public AiMeteringPeriod Period { get; set; } = new();

    [JsonPropertyName("measurements")]
    public List<AiMeteringMeasurement> Measurements { get; set; } = new();

    [JsonPropertyName("evidence")]
    public List<AiMeteringEvidenceReference> Evidence { get; set; } = new();

    [JsonPropertyName("completeness")]
    public string Completeness { get; set; } = AiMeteringCompleteness.Unknown;

    [JsonPropertyName("attestationLevel")]
    public string AttestationLevel { get; set; } = AiMeteringAttestationLevels.SelfReported;

    /// <summary>
    /// Optional position in a caller-managed receipt chain. Standalone and concurrently emitted
    /// receipts leave both this value and previousReceiptHash unset; an independent review binds
    /// its ordered receipt set even when no producer-side chain exists.
    /// </summary>
    [JsonPropertyName("sequence")]
    public int? Sequence { get; set; }

    /// <summary>SHA-256 envelope hash of the immediately preceding receipt in this chain.</summary>
    [JsonPropertyName("previousReceiptHash")]
    public string? PreviousReceiptHash { get; set; }

    /// <summary>UTC RFC 3339 timestamp with exactly millisecond precision.</summary>
    [JsonPropertyName("issuedAt")]
    public string IssuedAt { get; set; } = AiMeteringTimestamp.Format(DateTimeOffset.UtcNow);

    [JsonPropertyName("integrity")]
    public AiMeteringIntegrity? Integrity { get; set; }
}

/// <summary>
/// Correlation scopes carried together because provider threads, runner sessions, turns, and
/// Vyral runs do not have a portable one-to-one relationship.
/// </summary>
public sealed class AiMeteringSubject
{
    [JsonPropertyName("providerRunId")]
    public string? ProviderRunId { get; set; }

    [JsonPropertyName("executionRunId")]
    public string? ExecutionRunId { get; set; }

    [JsonPropertyName("providerThreadId")]
    public string? ProviderThreadId { get; set; }

    [JsonPropertyName("runnerSessionId")]
    public string? RunnerSessionId { get; set; }

    [JsonPropertyName("turnId")]
    public string? TurnId { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Optional caller correlation for a provider run. Vyral assigns providerRunId and
/// executionRunId at admission; callers may identify a containing runner session or turn.
/// </summary>
public sealed class AiMeteringContext
{
    [JsonPropertyName("providerThreadId")]
    public string? ProviderThreadId { get; set; }

    [JsonPropertyName("runnerSessionId")]
    public string? RunnerSessionId { get; set; }

    [JsonPropertyName("turnId")]
    public string? TurnId { get; set; }

    [JsonPropertyName("sequence")]
    public int? Sequence { get; set; }

    [JsonPropertyName("previousReceiptHash")]
    public string? PreviousReceiptHash { get; set; }
}

public sealed class AiMeteringPeriod
{
    [JsonPropertyName("observedStartedAt")]
    public string ObservedStartedAt { get; set; } = string.Empty;

    [JsonPropertyName("observedCompletedAt")]
    public string ObservedCompletedAt { get; set; } = string.Empty;

    /// <summary>Observer wall duration measured with a monotonic clock where available.</summary>
    [JsonPropertyName("elapsedDurationMs")]
    public long ElapsedDurationMs { get; set; }

    [JsonPropertyName("queueDurationMs")]
    public long? QueueDurationMs { get; set; }

    [JsonPropertyName("activeDurationMs")]
    public long? ActiveDurationMs { get; set; }

    [JsonPropertyName("idleDurationMs")]
    public long? IdleDurationMs { get; set; }

    [JsonPropertyName("providerDurationMs")]
    public long? ProviderDurationMs { get; set; }
}

/// <summary>A non-negative integer observation with explicit origin and evidentiary quality.</summary>
public sealed class AiMeteringMeasurement
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public long Value { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = AiMeteringUnits.Count;

    [JsonPropertyName("source")]
    public string Source { get; set; } = AiMeteringSources.RunnerObserver;

    [JsonPropertyName("quality")]
    public string Quality { get; set; } = AiMeteringQualities.Observed;

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("tokenizerId")]
    public string? TokenizerId { get; set; }
}

public sealed class AiMeteringEvidenceReference
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("digest")]
    public string Digest { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    /// <summary>Whether raw sensitive content was omitted or replaced by a digest.</summary>
    [JsonPropertyName("redacted")]
    public bool Redacted { get; set; } = true;
}

public sealed class AiMeteringIntegrity
{
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = AiMeteringCryptography.Es256;

    [JsonPropertyName("issuer")]
    public string Issuer { get; set; } = string.Empty;

    [JsonPropertyName("keyId")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>Base64url-encoded IEEE P1363 ES256 signature.</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}

/// <summary>The provider-thread or runner-session boundary assessed by a review.</summary>
public sealed class AiMeteringScope
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = AiMeteringScopeKinds.RunnerSession;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

/// <summary>
/// Time aggregation over reviewed terminal summaries. Wall span includes gaps; summed durations
/// can include overlapping work and therefore deliberately remain separate.
/// </summary>
public sealed class AiMeteringAggregatePeriod
{
    [JsonPropertyName("observedStartedAt")]
    public string ObservedStartedAt { get; set; } = string.Empty;

    [JsonPropertyName("observedCompletedAt")]
    public string ObservedCompletedAt { get; set; } = string.Empty;

    [JsonPropertyName("wallSpanDurationMs")]
    public long WallSpanDurationMs { get; set; }

    [JsonPropertyName("summedElapsedDurationMs")]
    public long SummedElapsedDurationMs { get; set; }

    [JsonPropertyName("summedQueueDurationMs")]
    public long? SummedQueueDurationMs { get; set; }

    [JsonPropertyName("summedActiveDurationMs")]
    public long? SummedActiveDurationMs { get; set; }

    [JsonPropertyName("summedIdleDurationMs")]
    public long? SummedIdleDurationMs { get; set; }

    [JsonPropertyName("summedProviderDurationMs")]
    public long? SummedProviderDurationMs { get; set; }

    [JsonPropertyName("concurrentIntervalsDetected")]
    public bool ConcurrentIntervalsDetected { get; set; }
}

/// <summary>
/// One session-level total. Provider and model remain explicit so unlike measurements are not
/// silently collapsed into an apparently uniform quantity.
/// </summary>
public sealed class AiMeteringAggregateMeasurement
{
    [JsonPropertyName("receiptKind")]
    public string ReceiptKind { get; set; } = AiMeteringReceiptKinds.Summary;

    [JsonPropertyName("evidenceIssuer")]
    public string EvidenceIssuer { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public long Value { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = AiMeteringUnits.Count;

    [JsonPropertyName("source")]
    public string Source { get; set; } = AiMeteringSources.RunnerObserver;

    [JsonPropertyName("quality")]
    public string Quality { get; set; } = AiMeteringQualities.Observed;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("tokenizerId")]
    public string? TokenizerId { get; set; }

    [JsonPropertyName("receiptCount")]
    public int ReceiptCount { get; set; }
}

/// <summary>Verified totals derived from one receipt per provider run in a single scope.</summary>
public sealed class AiMeteringAggregate
{
    [JsonPropertyName("receiptCount")]
    public int ReceiptCount { get; set; }

    [JsonPropertyName("summaryReceiptCount")]
    public int SummaryReceiptCount { get; set; }

    [JsonPropertyName("observationReceiptCount")]
    public int ObservationReceiptCount { get; set; }

    [JsonPropertyName("providerRunCount")]
    public int ProviderRunCount { get; set; }

    [JsonPropertyName("period")]
    public AiMeteringAggregatePeriod Period { get; set; } = new();

    [JsonPropertyName("measurements")]
    public List<AiMeteringAggregateMeasurement> Measurements { get; set; } = new();
}

/// <summary>A separately signed assessment over one ordered receipt set.</summary>
public sealed class AiMeteringReview
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = AiMeteringSchemas.ReviewV1;

    [JsonPropertyName("reviewId")]
    public string ReviewId { get; set; } = "amv_" + Guid.NewGuid().ToString("N");

    [JsonPropertyName("rulesetId")]
    public string RulesetId { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public AiMeteringScope Scope { get; set; } = new();

    /// <summary>Ordered full-envelope hashes of the reviewed receipts.</summary>
    [JsonPropertyName("receiptHashes")]
    public List<string> ReceiptHashes { get; set; } = new();

    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = AiMeteringReviewVerdicts.VerifiedWithGaps;

    [JsonPropertyName("findings")]
    public List<AiMeteringReviewFinding> Findings { get; set; } = new();

    /// <summary>
    /// Present only when every receipt passed structural, signature, scope, and uniqueness checks.
    /// A rejected review never publishes totals that could be mistaken for verified metering.
    /// </summary>
    [JsonPropertyName("aggregate")]
    public AiMeteringAggregate? Aggregate { get; set; }

    [JsonPropertyName("reviewedAt")]
    public string ReviewedAt { get; set; } = AiMeteringTimestamp.Format(DateTimeOffset.UtcNow);

    [JsonPropertyName("integrity")]
    public AiMeteringIntegrity? Integrity { get; set; }
}

public sealed class AiMeteringReviewFinding
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = AiMeteringFindingSeverities.Warning;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("receiptIds")]
    public List<string> ReceiptIds { get; set; } = new();

    [JsonPropertyName("measurementNames")]
    public List<string> MeasurementNames { get; set; } = new();
}

public static class AiMeteringTimestamp
{
    public const string FormatString = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(FormatString, System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(string? value, out DateTimeOffset timestamp)
    {
        var parsed = DateTimeOffset.TryParseExact(
            value,
            FormatString,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out timestamp);
        return parsed;
    }
}
