namespace Vyral.Providers.Abstractions;

/// <summary>Bounded structural validation for portable metering evidence.</summary>
public static class AiMeteringValidator
{
    public const int MaxMeasurements = 256;
    public const int MaxEvidenceReferences = 64;
    public const int MaxReceiptsPerRun = 64;
    public const int MaxReviewReceipts = 4096;
    public const int MaxReviewFindings = 256;
    public const int MaxAggregateMeasurements = 1024;
    public const long MaxPortableInteger = 9_007_199_254_740_991;

    private static readonly HashSet<string> ReceiptKinds =
        new([AiMeteringReceiptKinds.Observation, AiMeteringReceiptKinds.Summary], StringComparer.Ordinal);
    private static readonly HashSet<string> Outcomes =
        new([AiMeteringOutcomes.Succeeded, AiMeteringOutcomes.Failed, AiMeteringOutcomes.TimedOut, AiMeteringOutcomes.Rejected, AiMeteringOutcomes.Unsupported, AiMeteringOutcomes.NotConfigured, AiMeteringOutcomes.Cancelled], StringComparer.Ordinal);
    private static readonly HashSet<string> CompletenessValues =
        new([AiMeteringCompleteness.Complete, AiMeteringCompleteness.Partial, AiMeteringCompleteness.Sampled, AiMeteringCompleteness.Unknown], StringComparer.Ordinal);
    private static readonly HashSet<string> AttestationLevels =
        new([AiMeteringAttestationLevels.SelfReported, AiMeteringAttestationLevels.ObserverSigned, AiMeteringAttestationLevels.ProviderAttested, AiMeteringAttestationLevels.Reconciled, AiMeteringAttestationLevels.HardwareAttested], StringComparer.Ordinal);
    private static readonly HashSet<string> Sources =
        new([AiMeteringSources.ProviderResponse, AiMeteringSources.ProviderEventStream, AiMeteringSources.RunnerObserver, AiMeteringSources.GatewayObserver, AiMeteringSources.ConsumerInference], StringComparer.Ordinal);
    private static readonly HashSet<string> Qualities =
        new([AiMeteringQualities.Reported, AiMeteringQualities.Observed, AiMeteringQualities.Estimated, AiMeteringQualities.Reconciled, AiMeteringQualities.Unknown], StringComparer.Ordinal);
    private static readonly HashSet<string> ReviewVerdicts =
        new([AiMeteringReviewVerdicts.Verified, AiMeteringReviewVerdicts.VerifiedWithGaps, AiMeteringReviewVerdicts.Rejected], StringComparer.Ordinal);
    private static readonly HashSet<string> FindingSeverities =
        new([AiMeteringFindingSeverities.Info, AiMeteringFindingSeverities.Warning, AiMeteringFindingSeverities.Error], StringComparer.Ordinal);
    private static readonly HashSet<string> ScopeKinds =
        new([AiMeteringScopeKinds.ProviderThread, AiMeteringScopeKinds.RunnerSession], StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> KnownMeasurementUnits = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AiMeteringMeasurementNames.InputTokens] = AiMeteringUnits.Tokens,
        [AiMeteringMeasurementNames.CachedInputTokens] = AiMeteringUnits.Tokens,
        [AiMeteringMeasurementNames.CacheWriteInputTokens] = AiMeteringUnits.Tokens,
        [AiMeteringMeasurementNames.OutputTokens] = AiMeteringUnits.Tokens,
        [AiMeteringMeasurementNames.ReasoningOutputTokens] = AiMeteringUnits.Tokens,
        [AiMeteringMeasurementNames.TotalTokens] = AiMeteringUnits.Tokens,
        [AiMeteringMeasurementNames.ProviderCalls] = AiMeteringUnits.Count,
        [AiMeteringMeasurementNames.ModelCalls] = AiMeteringUnits.Count,
        [AiMeteringMeasurementNames.Turns] = AiMeteringUnits.Count,
        [AiMeteringMeasurementNames.ToolCalls] = AiMeteringUnits.Count,
        [AiMeteringMeasurementNames.Retries] = AiMeteringUnits.Count,
        [AiMeteringMeasurementNames.BytesIn] = AiMeteringUnits.Bytes,
        [AiMeteringMeasurementNames.BytesOut] = AiMeteringUnits.Bytes,
        [AiMeteringMeasurementNames.PayloadBytesIn] = AiMeteringUnits.Bytes,
        [AiMeteringMeasurementNames.PayloadBytesOut] = AiMeteringUnits.Bytes,
        [AiMeteringMeasurementNames.MessagesIn] = AiMeteringUnits.Count,
        [AiMeteringMeasurementNames.MessagesOut] = AiMeteringUnits.Count,
        [AiMeteringMeasurementNames.Artifacts] = AiMeteringUnits.Count,
        [AiMeteringMeasurementNames.ContextReferences] = AiMeteringUnits.Count
    };

    public static AiMeteringVerificationResult ValidateReceipt(
        AiMeteringReceipt receipt,
        bool requireIntegrity = false,
        bool allowPendingIntegrity = false)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var errors = new List<string>();
        RequireExact(receipt.Schema, AiMeteringSchemas.ReceiptV1, "schema", errors);
        RequireText(receipt.ReceiptId, "receiptId", errors);
        RequireMember(receipt.Kind, ReceiptKinds, "kind", errors);
        RequireText(receipt.Provider, "provider", errors);
        RequireText(receipt.Capability, "capability", errors);
        RequireText(receipt.Operation, "operation", errors);
        RequireMember(receipt.Outcome, Outcomes, "outcome", errors);
        RequireMember(receipt.Completeness, CompletenessValues, "completeness", errors);
        RequireMember(receipt.AttestationLevel, AttestationLevels, "attestationLevel", errors);
        ValidateTimestamp(receipt.IssuedAt, "issuedAt", errors, out var issuedAt);
        ValidateOptionalText(receipt.ModelId, "modelId", errors);
        ValidateOptionalText(receipt.AdapterId, "adapterId", errors);

        if (receipt.Subject is null)
        {
            errors.Add("subject is required");
        }
        else
        {
            var identifiers = new[]
            {
                (receipt.Subject.ProviderRunId, "subject.providerRunId"),
                (receipt.Subject.ExecutionRunId, "subject.executionRunId"),
                (receipt.Subject.ProviderThreadId, "subject.providerThreadId"),
                (receipt.Subject.RunnerSessionId, "subject.runnerSessionId"),
                (receipt.Subject.TurnId, "subject.turnId"),
                (receipt.Subject.CorrelationId, "subject.correlationId")
            };
            if (identifiers.All(identifier => string.IsNullOrWhiteSpace(identifier.Item1)))
            {
                errors.Add("subject must contain at least one correlation identifier");
            }
            foreach (var (identifier, path) in identifiers)
            {
                ValidateOptionalText(identifier, path, errors);
            }
        }

        ValidatePeriod(receipt.Period, errors, out var completedAt);
        if (issuedAt.HasValue && completedAt.HasValue && issuedAt.Value < completedAt.Value)
        {
            errors.Add("issuedAt cannot precede period.observedCompletedAt");
        }
        if (receipt.Sequence < 0)
        {
            errors.Add("sequence must be non-negative");
        }
        if (receipt.Sequence is null && receipt.PreviousReceiptHash is not null)
        {
            errors.Add("sequence is required when previousReceiptHash is supplied");
        }
        else if (receipt.Sequence == 0 && receipt.PreviousReceiptHash is not null)
        {
            errors.Add("previousReceiptHash must be absent when sequence is zero");
        }
        if (receipt.Sequence > 0 && !IsSha256(receipt.PreviousReceiptHash))
        {
            errors.Add("previousReceiptHash must be a canonical sha256 digest when sequence is greater than zero");
        }

        if (receipt.Measurements is null)
        {
            errors.Add("measurements is required");
        }
        else if (receipt.Measurements.Count > MaxMeasurements)
        {
            errors.Add($"measurements cannot exceed {MaxMeasurements} items");
        }
        else
        {
            for (var index = 0; index < receipt.Measurements.Count; index++)
            {
                ValidateMeasurement(receipt.Measurements[index], index, errors);
            }
            foreach (var duplicate in receipt.Measurements
                .Where(measurement => measurement is not null)
                .GroupBy(measurement => (measurement.Name, measurement.Unit, measurement.Source, measurement.Quality, measurement.SourceId, measurement.Method, measurement.TokenizerId))
                .Where(group => group.Count() > 1))
            {
                errors.Add($"measurements contains a duplicate observation for '{duplicate.Key.Name}' and source '{duplicate.Key.Source}'");
            }
        }

        if (receipt.Evidence is null)
        {
            errors.Add("evidence is required");
        }
        else if (receipt.Evidence.Count > MaxEvidenceReferences)
        {
            errors.Add($"evidence cannot exceed {MaxEvidenceReferences} items");
        }
        else
        {
            for (var index = 0; index < receipt.Evidence.Count; index++)
            {
                var evidence = receipt.Evidence[index];
                if (evidence is null)
                {
                    errors.Add($"evidence[{index}] is required");
                    continue;
                }
                RequireText(evidence.Kind, $"evidence[{index}].kind", errors);
                if (!IsSha256(evidence.Digest))
                {
                    errors.Add($"evidence[{index}].digest must be a canonical sha256 digest");
                }
                ValidateOptionalText(evidence.Uri, $"evidence[{index}].uri", errors, 2048);
                ValidateOptionalText(evidence.MediaType, $"evidence[{index}].mediaType", errors);
            }
        }

        if (!allowPendingIntegrity &&
            !string.Equals(receipt.AttestationLevel, AiMeteringAttestationLevels.SelfReported, StringComparison.Ordinal) &&
            receipt.Integrity is null)
        {
            errors.Add("integrity is required when attestationLevel is stronger than self_reported");
        }

        ValidateIntegrity(receipt.Integrity, requireIntegrity, errors);
        return new AiMeteringVerificationResult(AiMeteringCryptography.ComputeReceiptPayloadHash(receipt), errors);
    }

    public static AiMeteringVerificationResult ValidateReview(AiMeteringReview review, bool requireIntegrity = false)
    {
        ArgumentNullException.ThrowIfNull(review);
        var errors = new List<string>();
        RequireExact(review.Schema, AiMeteringSchemas.ReviewV1, "schema", errors);
        RequireText(review.ReviewId, "reviewId", errors);
        RequireText(review.RulesetId, "rulesetId", errors);
        ValidateScope(review.Scope, errors);
        RequireMember(review.Verdict, ReviewVerdicts, "verdict", errors);
        ValidateTimestamp(review.ReviewedAt, "reviewedAt", errors, out _);

        if (review.ReceiptHashes is null || review.ReceiptHashes.Count == 0)
        {
            errors.Add("receiptHashes must contain at least one receipt envelope hash");
        }
        else if (review.ReceiptHashes.Count > MaxReviewReceipts)
        {
            errors.Add($"receiptHashes cannot exceed {MaxReviewReceipts} items");
        }
        else
        {
            for (var index = 0; index < review.ReceiptHashes.Count; index++)
            {
                if (!IsSha256(review.ReceiptHashes[index]))
                {
                    errors.Add($"receiptHashes[{index}] must be a canonical sha256 digest");
                }
            }
            if (review.ReceiptHashes.Distinct(StringComparer.Ordinal).Count() != review.ReceiptHashes.Count)
            {
                errors.Add("receiptHashes cannot contain duplicates");
            }
        }

        if (review.Aggregate is null)
        {
            if (!string.Equals(review.Verdict, AiMeteringReviewVerdicts.Rejected, StringComparison.Ordinal))
            {
                errors.Add("aggregate is required for a non-rejected review");
            }
        }
        else
        {
            if (string.Equals(review.Verdict, AiMeteringReviewVerdicts.Rejected, StringComparison.Ordinal))
            {
                errors.Add("aggregate must be absent from a rejected review");
            }
            ValidateAggregate(review.Aggregate, review.ReceiptHashes?.Count ?? 0, errors);
        }

        if (review.Findings is null)
        {
            errors.Add("findings is required");
        }
        else if (review.Findings.Count > MaxReviewFindings)
        {
            errors.Add($"findings cannot exceed {MaxReviewFindings} items");
        }
        else
        {
            for (var index = 0; index < review.Findings.Count; index++)
            {
                var finding = review.Findings[index];
                if (finding is null)
                {
                    errors.Add($"findings[{index}] is required");
                    continue;
                }
                RequireText(finding.Code, $"findings[{index}].code", errors);
                RequireMember(finding.Severity, FindingSeverities, $"findings[{index}].severity", errors);
                RequireText(finding.Message, $"findings[{index}].message", errors);
                if (finding.ReceiptIds is null || finding.ReceiptIds.Count > MaxReviewReceipts)
                {
                    errors.Add($"findings[{index}].receiptIds must contain at most {MaxReviewReceipts} items");
                }
                else
                {
                    for (var receiptIndex = 0; receiptIndex < finding.ReceiptIds.Count; receiptIndex++)
                    {
                        RequireText(finding.ReceiptIds[receiptIndex], $"findings[{index}].receiptIds[{receiptIndex}]", errors);
                    }
                }
                if (finding.MeasurementNames is null || finding.MeasurementNames.Count > MaxMeasurements)
                {
                    errors.Add($"findings[{index}].measurementNames must contain at most {MaxMeasurements} items");
                }
                else
                {
                    for (var measurementIndex = 0; measurementIndex < finding.MeasurementNames.Count; measurementIndex++)
                    {
                        RequireMetricName(finding.MeasurementNames[measurementIndex], $"findings[{index}].measurementNames[{measurementIndex}]", errors);
                    }
                }
            }
        }

        ValidateIntegrity(review.Integrity, requireIntegrity, errors);
        return new AiMeteringVerificationResult(AiMeteringCryptography.ComputeReviewPayloadHash(review), errors);
    }

    public static AiMeteringVerificationResult ValidateChain(IReadOnlyList<AiMeteringReceipt> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        var errors = new List<string>();
        if (receipts.Count == 0)
        {
            errors.Add("receipt chain must contain at least one receipt");
            return new AiMeteringVerificationResult(ProviderHash.Sha256(string.Empty), errors);
        }
        if (receipts.Any(receipt => receipt is null))
        {
            errors.Add("receipt chain cannot contain a null receipt");
            return new AiMeteringVerificationResult(ProviderHash.Sha256(string.Empty), errors);
        }

        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            var validation = ValidateReceipt(receipt);
            errors.AddRange(validation.Errors.Select(error => $"receipt[{index}]: {error}"));
            if (receipt.Sequence is null)
            {
                errors.Add($"receipt[{index}].sequence is required in a producer-managed chain");
            }
            if (receipt.Sequence != index)
            {
                errors.Add($"receipt[{index}].sequence must be {index}");
            }
            if (index > 0)
            {
                var expected = AiMeteringCryptography.ComputeReceiptEnvelopeHash(receipts[index - 1]);
                if (!string.Equals(receipt.PreviousReceiptHash, expected, StringComparison.Ordinal))
                {
                    errors.Add($"receipt[{index}].previousReceiptHash does not match receipt[{index - 1}]");
                }
            }
        }

        var chainHash = AiMeteringCryptography.ComputeReceiptEnvelopeHash(receipts[^1]);
        return new AiMeteringVerificationResult(chainHash, errors);
    }

    public static IReadOnlyList<string> ValidateContext(AiMeteringContext? context)
    {
        if (context is null)
        {
            return Array.Empty<string>();
        }
        var errors = new List<string>();
        if (context.Sequence < 0)
        {
            errors.Add("meteringContext.sequence must be non-negative");
        }
        if (context.Sequence is null && context.PreviousReceiptHash is not null)
        {
            errors.Add("meteringContext.sequence is required when previousReceiptHash is supplied");
        }
        else if (context.Sequence == 0 && context.PreviousReceiptHash is not null)
        {
            errors.Add("meteringContext.previousReceiptHash must be absent when sequence is zero");
        }
        if (context.Sequence > 0 && !IsSha256(context.PreviousReceiptHash))
        {
            errors.Add("meteringContext.previousReceiptHash must be a canonical sha256 digest when sequence is greater than zero");
        }
        ValidateOptionalText(context.ProviderThreadId, "meteringContext.providerThreadId", errors);
        ValidateOptionalText(context.RunnerSessionId, "meteringContext.runnerSessionId", errors);
        ValidateOptionalText(context.TurnId, "meteringContext.turnId", errors);
        return errors;
    }

    public static IReadOnlyList<string> ValidateScope(AiMeteringScope? scope)
    {
        var errors = new List<string>();
        ValidateScope(scope, errors);
        return errors;
    }

    private static void ValidateScope(AiMeteringScope? scope, List<string> errors)
    {
        if (scope is null)
        {
            errors.Add("scope is required");
            return;
        }
        RequireMember(scope.Kind, ScopeKinds, "scope.kind", errors);
        RequireText(scope.Id, "scope.id", errors);
    }

    private static void ValidateAggregate(AiMeteringAggregate aggregate, int receiptHashCount, List<string> errors)
    {
        if (aggregate.ReceiptCount <= 0 || aggregate.ReceiptCount != receiptHashCount)
        {
            errors.Add("aggregate.receiptCount must equal the number of reviewed receipt hashes");
        }
        if (aggregate.SummaryReceiptCount <= 0 || aggregate.ObservationReceiptCount < 0 ||
            aggregate.SummaryReceiptCount > MaxReviewReceipts || aggregate.ObservationReceiptCount > MaxReviewReceipts ||
            aggregate.SummaryReceiptCount + aggregate.ObservationReceiptCount != aggregate.ReceiptCount)
        {
            errors.Add("aggregate summary and observation receipt counts must be bounded and sum to receiptCount");
        }
        if (aggregate.ProviderRunCount <= 0 || aggregate.ProviderRunCount > aggregate.ReceiptCount)
        {
            errors.Add("aggregate.providerRunCount must be positive and cannot exceed receiptCount");
        }
        if (aggregate.ProviderRunCount != aggregate.SummaryReceiptCount)
        {
            errors.Add("aggregate.providerRunCount must equal summaryReceiptCount under the basic ruleset");
        }
        if (aggregate.Period is null)
        {
            errors.Add("aggregate.period is required");
        }
        else
        {
            ValidateTimestamp(aggregate.Period.ObservedStartedAt, "aggregate.period.observedStartedAt", errors, out var startedAt);
            ValidateTimestamp(aggregate.Period.ObservedCompletedAt, "aggregate.period.observedCompletedAt", errors, out var completedAt);
            if (startedAt.HasValue && completedAt.HasValue && completedAt.Value < startedAt.Value)
            {
                errors.Add("aggregate.period.observedCompletedAt cannot precede observedStartedAt");
            }
            ValidateNonNegative(aggregate.Period.WallSpanDurationMs, "aggregate.period.wallSpanDurationMs", errors);
            ValidateNonNegative(aggregate.Period.SummedElapsedDurationMs, "aggregate.period.summedElapsedDurationMs", errors);
            ValidateNonNegative(aggregate.Period.SummedQueueDurationMs, "aggregate.period.summedQueueDurationMs", errors);
            ValidateNonNegative(aggregate.Period.SummedActiveDurationMs, "aggregate.period.summedActiveDurationMs", errors);
            ValidateNonNegative(aggregate.Period.SummedIdleDurationMs, "aggregate.period.summedIdleDurationMs", errors);
            ValidateNonNegative(aggregate.Period.SummedProviderDurationMs, "aggregate.period.summedProviderDurationMs", errors);
            if (startedAt.HasValue && completedAt.HasValue)
            {
                var actualWallSpanMs = (long)Math.Round((completedAt.Value - startedAt.Value).TotalMilliseconds);
                if (aggregate.Period.WallSpanDurationMs != actualWallSpanMs)
                {
                    errors.Add("aggregate.period.wallSpanDurationMs must match its observed timestamps");
                }
            }
        }
        if (aggregate.Measurements is null)
        {
            errors.Add("aggregate.measurements is required");
        }
        else if (aggregate.Measurements.Count > MaxAggregateMeasurements)
        {
            errors.Add($"aggregate.measurements cannot exceed {MaxAggregateMeasurements} items");
        }
        else
        {
            for (var index = 0; index < aggregate.Measurements.Count; index++)
            {
                var measurement = aggregate.Measurements[index];
                if (measurement is null)
                {
                    errors.Add($"aggregate.measurements[{index}] is required");
                    continue;
                }
                RequireMetricName(measurement.Name, $"aggregate.measurements[{index}].name", errors);
                RequireMember(measurement.ReceiptKind, ReceiptKinds, $"aggregate.measurements[{index}].receiptKind", errors);
                RequireText(measurement.EvidenceIssuer, $"aggregate.measurements[{index}].evidenceIssuer", errors);
                RequireText(measurement.Unit, $"aggregate.measurements[{index}].unit", errors);
                RequireMember(measurement.Source, Sources, $"aggregate.measurements[{index}].source", errors);
                RequireMember(measurement.Quality, Qualities, $"aggregate.measurements[{index}].quality", errors);
                RequireText(measurement.Provider, $"aggregate.measurements[{index}].provider", errors);
                ValidateNonNegative(measurement.Value, $"aggregate.measurements[{index}].value", errors);
                if (measurement.ReceiptCount <= 0 || measurement.ReceiptCount > aggregate.ReceiptCount)
                {
                    errors.Add($"aggregate.measurements[{index}].receiptCount must be within the reviewed receipt count");
                }
                if (measurement.Quality == AiMeteringQualities.Estimated && string.IsNullOrWhiteSpace(measurement.Method))
                {
                    errors.Add($"aggregate.measurements[{index}].method is required for estimated values");
                }
                ValidateKnownMeasurementUnit(measurement.Name, measurement.Unit, $"aggregate.measurements[{index}]", errors);
                ValidateOptionalText(measurement.ModelId, $"aggregate.measurements[{index}].modelId", errors);
                ValidateOptionalText(measurement.Method, $"aggregate.measurements[{index}].method", errors);
                ValidateOptionalText(measurement.TokenizerId, $"aggregate.measurements[{index}].tokenizerId", errors);
            }
            foreach (var duplicate in aggregate.Measurements
                .Where(measurement => measurement is not null)
                .GroupBy(measurement => (measurement.ReceiptKind, measurement.EvidenceIssuer, measurement.Provider, measurement.ModelId, measurement.Name, measurement.Unit, measurement.Source, measurement.Quality, measurement.Method, measurement.TokenizerId))
                .Where(group => group.Count() > 1))
            {
                errors.Add($"aggregate.measurements contains a duplicate total for '{duplicate.Key.Name}' and provider '{duplicate.Key.Provider}'");
            }
        }
    }

    private static void ValidatePeriod(AiMeteringPeriod? period, List<string> errors, out DateTimeOffset? completedAt)
    {
        completedAt = null;
        if (period is null)
        {
            errors.Add("period is required");
            return;
        }

        ValidateTimestamp(period.ObservedStartedAt, "period.observedStartedAt", errors, out var startedAt);
        ValidateTimestamp(period.ObservedCompletedAt, "period.observedCompletedAt", errors, out completedAt);
        if (startedAt.HasValue && completedAt.HasValue && completedAt.Value < startedAt.Value)
        {
            errors.Add("period.observedCompletedAt cannot precede period.observedStartedAt");
        }
        ValidateNonNegative(period.ElapsedDurationMs, "period.elapsedDurationMs", errors);
        ValidateNonNegative(period.QueueDurationMs, "period.queueDurationMs", errors);
        ValidateNonNegative(period.ActiveDurationMs, "period.activeDurationMs", errors);
        ValidateNonNegative(period.IdleDurationMs, "period.idleDurationMs", errors);
        ValidateNonNegative(period.ProviderDurationMs, "period.providerDurationMs", errors);
        if (period.QueueDurationMs > period.ElapsedDurationMs)
        {
            errors.Add("period.queueDurationMs cannot exceed elapsedDurationMs");
        }
        if (period.ActiveDurationMs > period.ElapsedDurationMs)
        {
            errors.Add("period.activeDurationMs cannot exceed elapsedDurationMs");
        }
        if (period.IdleDurationMs > period.ElapsedDurationMs)
        {
            errors.Add("period.idleDurationMs cannot exceed elapsedDurationMs");
        }
        if (period.ActiveDurationMs.HasValue && period.IdleDurationMs.HasValue &&
            period.ActiveDurationMs.Value > period.ElapsedDurationMs - Math.Min(period.ElapsedDurationMs, period.IdleDurationMs.Value))
        {
            errors.Add("period active and idle durations cannot exceed elapsedDurationMs");
        }
        if (period.QueueDurationMs.HasValue && period.ActiveDurationMs.HasValue &&
            period.QueueDurationMs.Value > period.ElapsedDurationMs - Math.Min(period.ElapsedDurationMs, period.ActiveDurationMs.Value))
        {
            errors.Add("period queue and active durations cannot exceed elapsedDurationMs");
        }
    }

    private static void ValidateMeasurement(AiMeteringMeasurement? measurement, int index, List<string> errors)
    {
        if (measurement is null)
        {
            errors.Add($"measurements[{index}] is required");
            return;
        }
        RequireMetricName(measurement.Name, $"measurements[{index}].name", errors);
        RequireText(measurement.Unit, $"measurements[{index}].unit", errors);
        RequireMember(measurement.Source, Sources, $"measurements[{index}].source", errors);
        RequireMember(measurement.Quality, Qualities, $"measurements[{index}].quality", errors);
        ValidateNonNegative(measurement.Value, $"measurements[{index}].value", errors);
        ValidateKnownMeasurementUnit(measurement.Name, measurement.Unit, $"measurements[{index}]", errors);
        if (measurement.Quality == AiMeteringQualities.Estimated && string.IsNullOrWhiteSpace(measurement.Method))
        {
            errors.Add($"measurements[{index}].method is required for estimated values");
        }
        ValidateOptionalText(measurement.SourceId, $"measurements[{index}].sourceId", errors);
        ValidateOptionalText(measurement.Method, $"measurements[{index}].method", errors);
        ValidateOptionalText(measurement.TokenizerId, $"measurements[{index}].tokenizerId", errors);
    }

    private static void ValidateIntegrity(AiMeteringIntegrity? integrity, bool required, List<string> errors)
    {
        if (integrity is null)
        {
            if (required)
            {
                errors.Add("integrity is required");
            }
            return;
        }
        RequireExact(integrity.Algorithm, AiMeteringCryptography.Es256, "integrity.algorithm", errors);
        RequireText(integrity.Issuer, "integrity.issuer", errors);
        RequireText(integrity.KeyId, "integrity.keyId", errors);
        if (!IsSha256(integrity.PayloadHash))
        {
            errors.Add("integrity.payloadHash must be a canonical sha256 digest");
        }
        RequireText(integrity.Signature, "integrity.signature", errors);
        if (!string.IsNullOrEmpty(integrity.Signature) &&
            (integrity.Signature.Length != 86 || integrity.Signature.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))))
        {
            errors.Add("integrity.signature must be an unpadded 64-byte base64url value");
        }
    }

    private static void ValidateTimestamp(string? value, string path, List<string> errors, out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (!AiMeteringTimestamp.TryParse(value, out var parsed))
        {
            errors.Add($"{path} must be a UTC RFC 3339 timestamp with millisecond precision");
            return;
        }
        timestamp = parsed;
    }

    private static void ValidateNonNegative(long? value, string path, List<string> errors)
    {
        if (value < 0)
        {
            errors.Add($"{path} must be non-negative");
        }
        else if (value > MaxPortableInteger)
        {
            errors.Add($"{path} cannot exceed the portable JSON integer limit {MaxPortableInteger}");
        }
    }

    private static void RequireText(string? value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{path} is required");
        }
        else if (value.Length > 512)
        {
            errors.Add($"{path} cannot exceed 512 characters");
        }
    }

    private static void ValidateOptionalText(string? value, string path, List<string> errors, int maxLength = 512)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > maxLength))
        {
            errors.Add($"{path} must be non-empty and cannot exceed {maxLength} characters when supplied");
        }
    }

    private static void RequireMetricName(string? value, string path, List<string> errors)
    {
        RequireText(value, path, errors);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        if (value.Length > 128 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            errors.Add($"{path} must be a portable metric name containing only ASCII letters, digits, '.', '_', or '-'");
        }
        else if (!KnownMeasurementUnits.ContainsKey(value) && !value.Contains('.'))
        {
            errors.Add($"{path} must use a namespaced name when it is not a Vyral portable measurement");
        }
    }

    private static void ValidateKnownMeasurementUnit(string? name, string? unit, string path, List<string> errors)
    {
        if (name is not null && KnownMeasurementUnits.TryGetValue(name, out var expectedUnit) &&
            !string.Equals(unit, expectedUnit, StringComparison.Ordinal))
        {
            errors.Add($"{path}.unit must be '{expectedUnit}' for '{name}'");
        }
    }

    private static void RequireExact(string? value, string expected, string path, List<string> errors)
    {
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            errors.Add($"{path} must be '{expected}'");
        }
    }

    private static void RequireMember(string? value, HashSet<string> allowed, string path, List<string> errors)
    {
        if (value is null || !allowed.Contains(value))
        {
            errors.Add($"{path} is not a supported value");
        }
    }

    private static bool IsSha256(string? value) =>
        value is not null && value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
