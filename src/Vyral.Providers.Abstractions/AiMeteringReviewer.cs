using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vyral.Providers.Abstractions;

/// <summary>
/// Builds a bounded review statement and verified scope totals from signed terminal receipts.
/// Cryptographic verification remains caller-supplied so the reviewer, rather than Vyral, owns
/// its trust roots and issuer policy.
/// </summary>
public static class AiMeteringReviewer
{
    private static readonly JsonSerializerOptions ComparisonJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reviews an ordered provider-thread or runner-session receipt set. Standalone receipts are
    /// valid: the signed review binds their order even when concurrent producers could not safely
    /// maintain one producer-side hash chain.
    /// </summary>
    public static AiMeteringReview ReviewReceipts(
        IReadOnlyList<AiMeteringReceipt> receipts,
        AiMeteringScope scope,
        string rulesetId,
        Func<AiMeteringReceipt, AiMeteringVerificationResult> verifyReceipt,
        DateTimeOffset? reviewedAt = null)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(verifyReceipt);
        if (string.IsNullOrWhiteSpace(rulesetId))
        {
            throw new ArgumentException("A review ruleset id is required.", nameof(rulesetId));
        }
        if (!string.Equals(rulesetId, AiMeteringRulesets.BasicV1, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"AI metering ruleset '{rulesetId}' is not implemented by this reviewer.");
        }
        if (receipts.Count == 0 || receipts.Count > AiMeteringValidator.MaxReviewReceipts)
        {
            throw new ArgumentException(
                $"The review must contain between 1 and {AiMeteringValidator.MaxReviewReceipts} receipts.",
                nameof(receipts));
        }
        if (receipts.Any(receipt => receipt is null))
        {
            throw new ArgumentException("The review cannot contain a null receipt.", nameof(receipts));
        }
        var scopeErrors = AiMeteringValidator.ValidateScope(scope);
        if (scopeErrors.Count > 0)
        {
            throw new ArgumentException("Invalid AI metering scope: " + string.Join("; ", scopeErrors), nameof(scope));
        }

        var review = new AiMeteringReview
        {
            RulesetId = rulesetId,
            Scope = new AiMeteringScope { Kind = scope.Kind, Id = scope.Id },
            ReviewedAt = AiMeteringTimestamp.Format(reviewedAt ?? DateTimeOffset.UtcNow),
            ReceiptHashes = receipts.Select(AiMeteringCryptography.ComputeReceiptEnvelopeHash).ToList()
        };

        ReviewDeclaredChain(receipts, review);
        ReviewIdentifiers(receipts, review);

        foreach (var receipt in receipts)
        {
            ReviewReceipt(receipt, scope, verifyReceipt, review);
        }

        if (!HasErrors(review))
        {
            review.Aggregate = BuildAggregate(receipts, review);
        }

        review.Verdict = HasErrors(review)
            ? AiMeteringReviewVerdicts.Rejected
            : review.Findings.Count == 0
                ? AiMeteringReviewVerdicts.Verified
                : AiMeteringReviewVerdicts.VerifiedWithGaps;
        AiMeteringValidator.ValidateReview(review).ThrowIfInvalid();
        return review;
    }

    /// <summary>
    /// Compatibility helper for a fully linked producer-managed chain. Prefer ReviewReceipts for
    /// independently reviewing concurrent session work.
    /// </summary>
    public static AiMeteringReview ReviewChain(
        IReadOnlyList<AiMeteringReceipt> receipts,
        string rulesetId,
        Func<AiMeteringReceipt, AiMeteringVerificationResult> verifyReceipt,
        DateTimeOffset? reviewedAt = null)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        var scope = InferScope(receipts);
        var review = ReviewReceipts(receipts, scope, rulesetId, verifyReceipt, reviewedAt);
        var chain = AiMeteringValidator.ValidateChain(receipts);
        if (!chain.Valid && !review.Findings.Any(finding =>
                string.Equals(finding.Code, "receipt_chain_invalid", StringComparison.Ordinal)))
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "receipt_chain_invalid",
                Severity = AiMeteringFindingSeverities.Error,
                Message = "The producer-managed receipt chain is incomplete, reordered, or invalid.",
                ReceiptIds = receipts.Select(receipt => receipt.ReceiptId).ToList()
            });
            review.Aggregate = null;
            review.Verdict = AiMeteringReviewVerdicts.Rejected;
        }
        AiMeteringValidator.ValidateReview(review).ThrowIfInvalid();
        return review;
    }

    /// <summary>
    /// Verifies a signed review and deterministically replays its ruleset against the supplied
    /// ordered receipts. This checks both trust policies, receipt-envelope bindings, findings,
    /// verdict, and aggregate values; signature verification alone cannot establish that a
    /// reviewer applied the named ruleset correctly.
    /// </summary>
    public static AiMeteringVerificationResult VerifyReviewBundle(
        AiMeteringReview review,
        IReadOnlyList<AiMeteringReceipt> receipts,
        Func<AiMeteringReview, AiMeteringVerificationResult> verifyReview,
        Func<AiMeteringReceipt, AiMeteringVerificationResult> verifyReceipt)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(verifyReview);
        ArgumentNullException.ThrowIfNull(verifyReceipt);

        var structural = AiMeteringValidator.ValidateReview(review, requireIntegrity: true);
        var errors = structural.Errors.ToList();
        TryVerifyReviewSignature(review, verifyReview, errors);

        if (receipts.Count == 0 || receipts.Count > AiMeteringValidator.MaxReviewReceipts)
        {
            errors.Add($"receipt bundle must contain between 1 and {AiMeteringValidator.MaxReviewReceipts} receipts");
            return new AiMeteringVerificationResult(structural.PayloadHash, errors);
        }
        if (receipts.Any(receipt => receipt is null))
        {
            errors.Add("receipt bundle cannot contain a null receipt");
            return new AiMeteringVerificationResult(structural.PayloadHash, errors);
        }

        List<string> receiptHashes;
        try
        {
            receiptHashes = receipts.Select(AiMeteringCryptography.ComputeReceiptEnvelopeHash).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or NotSupportedException)
        {
            errors.Add("receipt bundle could not be canonically hashed");
            return new AiMeteringVerificationResult(structural.PayloadHash, errors);
        }

        if (review.ReceiptHashes is null || !review.ReceiptHashes.SequenceEqual(receiptHashes, StringComparer.Ordinal))
        {
            errors.Add("review receiptHashes do not bind the supplied ordered receipt envelopes");
        }

        if (!AiMeteringTimestamp.TryParse(review.ReviewedAt, out var reviewedAt))
        {
            return new AiMeteringVerificationResult(structural.PayloadHash, errors);
        }

        try
        {
            var replayed = ReviewReceipts(
                receipts,
                review.Scope,
                review.RulesetId,
                verifyReceipt,
                reviewedAt);
            if (!string.Equals(review.Verdict, replayed.Verdict, StringComparison.Ordinal) ||
                !JsonEquivalent(review.Findings, replayed.Findings) ||
                !JsonEquivalent(review.Aggregate, replayed.Aggregate))
            {
                errors.Add("review verdict, findings, or aggregate do not match a deterministic ruleset replay");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            errors.Add("review ruleset could not be replayed against the supplied receipt bundle");
        }

        return new AiMeteringVerificationResult(structural.PayloadHash, errors);
    }

    private static AiMeteringScope InferScope(IReadOnlyList<AiMeteringReceipt> receipts)
    {
        if (receipts.Count == 0)
        {
            throw new ArgumentException("At least one metering receipt is required.", nameof(receipts));
        }
        var sessions = receipts
            .Select(receipt => receipt.Subject?.RunnerSessionId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sessions.Count == 1 && receipts.All(receipt => string.Equals(receipt.Subject?.RunnerSessionId, sessions[0], StringComparison.Ordinal)))
        {
            return new AiMeteringScope { Kind = AiMeteringScopeKinds.RunnerSession, Id = sessions[0]! };
        }
        var threads = receipts
            .Select(receipt => receipt.Subject?.ProviderThreadId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (threads.Count == 1 && receipts.All(receipt => string.Equals(receipt.Subject?.ProviderThreadId, threads[0], StringComparison.Ordinal)))
        {
            return new AiMeteringScope { Kind = AiMeteringScopeKinds.ProviderThread, Id = threads[0]! };
        }
        throw new ArgumentException("A receipt chain must identify one common runner session or provider thread.", nameof(receipts));
    }

    private static void TryVerifyReviewSignature(
        AiMeteringReview review,
        Func<AiMeteringReview, AiMeteringVerificationResult> verifyReview,
        List<string> errors)
    {
        try
        {
            if (!verifyReview(review).Valid)
            {
                errors.Add("review signature failed the verifier's cryptographic issuer/key policy");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"review signature verification did not complete: {ex.GetType().Name}");
        }
    }

    private static bool JsonEquivalent<T>(T first, T second) => JsonNode.DeepEquals(
        JsonSerializer.SerializeToNode(first, ComparisonJsonOptions),
        JsonSerializer.SerializeToNode(second, ComparisonJsonOptions));

    private static void ReviewDeclaredChain(IReadOnlyList<AiMeteringReceipt> receipts, AiMeteringReview review)
    {
        var declaresChain = receipts.Any(receipt => receipt.Sequence.HasValue || receipt.PreviousReceiptHash is not null);
        if (!declaresChain)
        {
            return;
        }
        var chain = AiMeteringValidator.ValidateChain(receipts);
        if (!chain.Valid)
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "receipt_chain_invalid",
                Severity = AiMeteringFindingSeverities.Error,
                Message = "The declared producer-managed receipt chain is incomplete, reordered, or invalid.",
                ReceiptIds = receipts.Select(receipt => receipt.ReceiptId).ToList()
            });
        }
    }

    private static void ReviewIdentifiers(IReadOnlyList<AiMeteringReceipt> receipts, AiMeteringReview review)
    {
        var duplicateReceiptIds = receipts
            .GroupBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateReceiptIds.Count > 0)
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "receipt_id_duplicated",
                Severity = AiMeteringFindingSeverities.Error,
                Message = "The ordered receipt set contains duplicate receipt identifiers.",
                ReceiptIds = duplicateReceiptIds
            });
        }

        var missingRunIds = receipts.Where(receipt => string.IsNullOrWhiteSpace(receipt.Subject?.ProviderRunId)).ToList();
        if (missingRunIds.Count > 0)
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "provider_run_id_missing",
                Severity = AiMeteringFindingSeverities.Error,
                Message = "Session aggregation requires a providerRunId on every reviewed receipt.",
                ReceiptIds = missingRunIds.Select(receipt => receipt.ReceiptId).ToList()
            });
        }

        var runGroups = receipts
            .Where(receipt => !string.IsNullOrWhiteSpace(receipt.Subject?.ProviderRunId))
            .GroupBy(receipt => receipt.Subject.ProviderRunId!, StringComparer.Ordinal)
            .ToList();
        var duplicateRunReceipts = runGroups
            .Where(group => group.Count(receipt => string.Equals(receipt.Kind, AiMeteringReceiptKinds.Summary, StringComparison.Ordinal)) > 1)
            .SelectMany(group => group
                .Where(receipt => string.Equals(receipt.Kind, AiMeteringReceiptKinds.Summary, StringComparison.Ordinal))
                .Select(receipt => receipt.ReceiptId))
            .ToList();
        if (duplicateRunReceipts.Count > 0)
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "provider_run_summary_duplicated",
                Severity = AiMeteringFindingSeverities.Error,
                Message = "Session aggregation permits exactly one terminal summary per provider run.",
                ReceiptIds = duplicateRunReceipts
            });
        }

        var runsWithoutSummary = runGroups
            .Where(group => !group.Any(receipt => string.Equals(receipt.Kind, AiMeteringReceiptKinds.Summary, StringComparison.Ordinal)))
            .SelectMany(group => group.Select(receipt => receipt.ReceiptId))
            .ToList();
        if (runsWithoutSummary.Count > 0)
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "provider_run_summary_missing",
                Severity = AiMeteringFindingSeverities.Error,
                Message = "Every observed provider run requires one terminal summary before it can be aggregated.",
                ReceiptIds = runsWithoutSummary
            });
        }

        var duplicateObserverReceipts = runGroups
            .SelectMany(group => group
                .Where(receipt => string.Equals(receipt.Kind, AiMeteringReceiptKinds.Observation, StringComparison.Ordinal))
                .GroupBy(receipt => receipt.Integrity?.Issuer ?? string.Empty, StringComparer.Ordinal)
                .Where(observer => observer.Count() > 1)
                .SelectMany(observer => observer.Select(receipt => receipt.ReceiptId)))
            .ToList();
        if (duplicateObserverReceipts.Count > 0)
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "provider_run_observer_duplicated",
                Severity = AiMeteringFindingSeverities.Error,
                Message = "The basic ruleset permits one terminal observation per observer and provider run.",
                ReceiptIds = duplicateObserverReceipts
            });
        }
    }

    private static void ReviewReceipt(
        AiMeteringReceipt receipt,
        AiMeteringScope scope,
        Func<AiMeteringReceipt, AiMeteringVerificationResult> verifyReceipt,
        AiMeteringReview review)
    {
        if (!MatchesScope(receipt, scope))
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "receipt_scope_mismatch",
                Severity = AiMeteringFindingSeverities.Error,
                Message = "A receipt does not identify the scope named by the review.",
                ReceiptIds = new List<string> { receipt.ReceiptId }
            });
        }

        var structural = AiMeteringValidator.ValidateReceipt(receipt, requireIntegrity: true);
        if (!structural.Valid)
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "receipt_structure_invalid",
                Severity = AiMeteringFindingSeverities.Error,
                Message = "A receipt failed bounded structural or required-integrity validation.",
                ReceiptIds = new List<string> { receipt.ReceiptId }
            });
            // Later consistency checks intentionally assume a structurally valid receipt. A
            // malformed evidence object must produce a rejected review, not crash the reviewer.
            return;
        }

        try
        {
            var verification = verifyReceipt(receipt);
            if (!verification.Valid)
            {
                AddFinding(review, new AiMeteringReviewFinding
                {
                    Code = "receipt_signature_invalid",
                    Severity = AiMeteringFindingSeverities.Error,
                    Message = "A receipt failed the reviewer's cryptographic issuer/key policy.",
                    ReceiptIds = new List<string> { receipt.ReceiptId }
                });
            }
        }
        catch (Exception ex)
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "receipt_verification_failed",
                Severity = AiMeteringFindingSeverities.Error,
                Message = $"Receipt verification did not complete: {ex.GetType().Name}.",
                ReceiptIds = new List<string> { receipt.ReceiptId }
            });
        }

        if (!string.Equals(receipt.Completeness, AiMeteringCompleteness.Complete, StringComparison.Ordinal))
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "receipt_coverage_incomplete",
                Severity = AiMeteringFindingSeverities.Warning,
                Message = "One or more receipts do not claim complete coverage.",
                ReceiptIds = new List<string> { receipt.ReceiptId }
            });
        }
        if (string.Equals(receipt.AttestationLevel, AiMeteringAttestationLevels.SelfReported, StringComparison.Ordinal))
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "receipt_self_reported",
                Severity = AiMeteringFindingSeverities.Warning,
                Message = "One or more receipts do not claim independently observed, provider-attested, reconciled, or hardware-attested evidence.",
                ReceiptIds = new List<string> { receipt.ReceiptId }
            });
        }
        if (AiMeteringTimestamp.TryParse(receipt.IssuedAt, out var issuedAt) &&
            AiMeteringTimestamp.TryParse(review.ReviewedAt, out var reviewedAt) &&
            issuedAt > reviewedAt)
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "review_precedes_receipt",
                Severity = AiMeteringFindingSeverities.Error,
                Message = "The review timestamp precedes one or more receipt issue timestamps.",
                ReceiptIds = new List<string> { receipt.ReceiptId }
            });
        }
        AddClockConsistencyFinding(receipt, review);
        AddProviderDurationFinding(receipt, review);
        AddTokenConsistencyFindings(receipt, review);
    }

    private static bool MatchesScope(AiMeteringReceipt receipt, AiMeteringScope scope) => scope.Kind switch
    {
        AiMeteringScopeKinds.ProviderThread => string.Equals(receipt.Subject?.ProviderThreadId, scope.Id, StringComparison.Ordinal),
        AiMeteringScopeKinds.RunnerSession => string.Equals(receipt.Subject?.RunnerSessionId, scope.Id, StringComparison.Ordinal),
        _ => false
    };

    private static AiMeteringAggregate? BuildAggregate(IReadOnlyList<AiMeteringReceipt> receipts, AiMeteringReview review)
    {
        try
        {
            var summaries = receipts
                .Where(receipt => string.Equals(receipt.Kind, AiMeteringReceiptKinds.Summary, StringComparison.Ordinal))
                .ToList();
            var periods = summaries.Select(receipt => receipt.Period).ToList();
            var starts = periods.Select(period => Parse(period.ObservedStartedAt)).ToList();
            var completions = periods.Select(period => Parse(period.ObservedCompletedAt)).ToList();
            var earliest = starts.Min();
            var latest = completions.Max();
            var aggregate = new AiMeteringAggregate
            {
                ReceiptCount = receipts.Count,
                SummaryReceiptCount = summaries.Count,
                ObservationReceiptCount = receipts.Count - summaries.Count,
                ProviderRunCount = summaries.Select(receipt => receipt.Subject.ProviderRunId).Distinct(StringComparer.Ordinal).Count(),
                Period = new AiMeteringAggregatePeriod
                {
                    ObservedStartedAt = AiMeteringTimestamp.Format(earliest),
                    ObservedCompletedAt = AiMeteringTimestamp.Format(latest),
                    WallSpanDurationMs = checked((long)Math.Round((latest - earliest).TotalMilliseconds)),
                    SummedElapsedDurationMs = Sum(periods.Select(period => period.ElapsedDurationMs)),
                    SummedQueueDurationMs = SumOptional(periods.Select(period => period.QueueDurationMs)),
                    SummedActiveDurationMs = SumOptional(periods.Select(period => period.ActiveDurationMs)),
                    SummedIdleDurationMs = SumOptional(periods.Select(period => period.IdleDurationMs)),
                    SummedProviderDurationMs = SumOptional(periods.Select(period => period.ProviderDurationMs)),
                    ConcurrentIntervalsDetected = HasOverlappingIntervals(starts.Zip(completions))
                },
                Measurements = AggregateMeasurements(receipts)
            };
            if (aggregate.Measurements.Count > AiMeteringValidator.MaxAggregateMeasurements)
            {
                throw new InvalidOperationException("The aggregate measurement cardinality exceeds the contract bound.");
            }
            return aggregate;
        }
        catch (Exception ex) when (ex is OverflowException or InvalidOperationException or FormatException)
        {
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "aggregate_failed",
                Severity = AiMeteringFindingSeverities.Error,
                Message = "Verified totals could not be produced within the bounded aggregation rules.",
                ReceiptIds = receipts.Select(receipt => receipt.ReceiptId).ToList()
            });
            return null;
        }
    }

    private static List<AiMeteringAggregateMeasurement> AggregateMeasurements(IReadOnlyList<AiMeteringReceipt> receipts)
    {
        return receipts
            .SelectMany(receipt => receipt.Measurements.Select(measurement => new { receipt, measurement }))
            .GroupBy(item => new
            {
                item.receipt.Provider,
                item.receipt.ModelId,
                item.receipt.Kind,
                EvidenceIssuer = item.receipt.Integrity?.Issuer,
                item.measurement.Name,
                item.measurement.Unit,
                item.measurement.Source,
                item.measurement.Quality,
                item.measurement.Method,
                item.measurement.TokenizerId
            })
            .Select(group => new AiMeteringAggregateMeasurement
            {
                Provider = group.Key.Provider,
                ModelId = group.Key.ModelId,
                ReceiptKind = group.Key.Kind,
                EvidenceIssuer = group.Key.EvidenceIssuer!,
                Name = group.Key.Name,
                Unit = group.Key.Unit,
                Source = group.Key.Source,
                Quality = group.Key.Quality,
                Method = group.Key.Method,
                TokenizerId = group.Key.TokenizerId,
                Value = Sum(group.Select(item => item.measurement.Value)),
                ReceiptCount = group.Select(item => item.receipt.ReceiptId).Distinct(StringComparer.Ordinal).Count()
            })
            .OrderBy(measurement => measurement.Provider, StringComparer.Ordinal)
            .ThenBy(measurement => measurement.ModelId, StringComparer.Ordinal)
            .ThenBy(measurement => measurement.ReceiptKind, StringComparer.Ordinal)
            .ThenBy(measurement => measurement.EvidenceIssuer, StringComparer.Ordinal)
            .ThenBy(measurement => measurement.Name, StringComparer.Ordinal)
            .ThenBy(measurement => measurement.Unit, StringComparer.Ordinal)
            .ThenBy(measurement => measurement.Source, StringComparer.Ordinal)
            .ThenBy(measurement => measurement.Quality, StringComparer.Ordinal)
            .ThenBy(measurement => measurement.TokenizerId, StringComparer.Ordinal)
            .ThenBy(measurement => measurement.Method, StringComparer.Ordinal)
            .ToList();
    }

    private static DateTimeOffset Parse(string value) =>
        AiMeteringTimestamp.TryParse(value, out var parsed)
            ? parsed
            : throw new FormatException("Invalid metering timestamp.");

    private static long Sum(IEnumerable<long> values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            total = checked(total + value);
            if (total > AiMeteringValidator.MaxPortableInteger)
            {
                throw new OverflowException("Aggregate exceeds the portable JSON integer limit.");
            }
        }
        return total;
    }

    private static long? SumOptional(IEnumerable<long?> values)
    {
        var buffered = values.ToList();
        return buffered.Any(value => !value.HasValue) ? null : Sum(buffered.Select(value => value!.Value));
    }

    private static bool HasOverlappingIntervals(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        DateTimeOffset? latestEnd = null;
        foreach (var interval in intervals.OrderBy(interval => interval.Start).ThenBy(interval => interval.End))
        {
            if (latestEnd.HasValue && interval.Start < latestEnd.Value)
            {
                return true;
            }
            if (!latestEnd.HasValue || interval.End > latestEnd.Value)
            {
                latestEnd = interval.End;
            }
        }
        return false;
    }

    private static void AddClockConsistencyFinding(AiMeteringReceipt receipt, AiMeteringReview review)
    {
        if (!AiMeteringTimestamp.TryParse(receipt.Period.ObservedStartedAt, out var startedAt) ||
            !AiMeteringTimestamp.TryParse(receipt.Period.ObservedCompletedAt, out var completedAt))
        {
            return;
        }
        var wallDurationMs = Math.Max(0, (long)Math.Round((completedAt - startedAt).TotalMilliseconds));
        var toleranceMs = Math.Max(1000, receipt.Period.ElapsedDurationMs / 10);
        if (Math.Abs(wallDurationMs - receipt.Period.ElapsedDurationMs) <= toleranceMs)
        {
            return;
        }
        AddFinding(review, new AiMeteringReviewFinding
        {
            Code = "clock_elapsed_mismatch",
            Severity = AiMeteringFindingSeverities.Warning,
            Message = "Wall-clock timestamps and monotonic elapsed duration differ beyond the basic ruleset tolerance.",
            ReceiptIds = new List<string> { receipt.ReceiptId }
        });
    }

    private static void AddTokenConsistencyFindings(AiMeteringReceipt receipt, AiMeteringReview review)
    {
        foreach (var source in receipt.Measurements.GroupBy(measurement => (measurement.Source, measurement.SourceId)))
        {
            var values = source
                .GroupBy(measurement => measurement.Name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Value, StringComparer.Ordinal);
            if (!values.TryGetValue(AiMeteringMeasurementNames.InputTokens, out var input) ||
                !values.TryGetValue(AiMeteringMeasurementNames.OutputTokens, out var output) ||
                !values.TryGetValue(AiMeteringMeasurementNames.TotalTokens, out var total) ||
                input > long.MaxValue - output || total == input + output)
            {
                continue;
            }
            AddFinding(review, new AiMeteringReviewFinding
            {
                Code = "token_total_mismatch",
                Severity = AiMeteringFindingSeverities.Warning,
                Message = "A source-reported total token count does not equal its input and output token counts.",
                ReceiptIds = new List<string> { receipt.ReceiptId },
                MeasurementNames = new List<string>
                {
                    AiMeteringMeasurementNames.InputTokens,
                    AiMeteringMeasurementNames.OutputTokens,
                    AiMeteringMeasurementNames.TotalTokens
                }
            });
        }
    }

    private static void AddProviderDurationFinding(AiMeteringReceipt receipt, AiMeteringReview review)
    {
        if (!receipt.Period.ProviderDurationMs.HasValue)
        {
            return;
        }
        var toleranceMs = Math.Max(1000, receipt.Period.ElapsedDurationMs / 10);
        if (receipt.Period.ProviderDurationMs.Value <= receipt.Period.ElapsedDurationMs ||
            receipt.Period.ProviderDurationMs.Value - receipt.Period.ElapsedDurationMs <= toleranceMs)
        {
            return;
        }
        AddFinding(review, new AiMeteringReviewFinding
        {
            Code = "provider_duration_exceeds_observation",
            Severity = AiMeteringFindingSeverities.Warning,
            Message = "A provider-reported duration exceeds the observer's elapsed duration beyond the basic ruleset tolerance.",
            ReceiptIds = new List<string> { receipt.ReceiptId }
        });
    }

    private static bool HasErrors(AiMeteringReview review) =>
        review.Findings.Any(finding => string.Equals(finding.Severity, AiMeteringFindingSeverities.Error, StringComparison.Ordinal));

    private static void AddFinding(AiMeteringReview review, AiMeteringReviewFinding finding)
    {
        var existing = review.Findings.FirstOrDefault(candidate =>
            string.Equals(candidate.Code, finding.Code, StringComparison.Ordinal) &&
            string.Equals(candidate.Severity, finding.Severity, StringComparison.Ordinal) &&
            string.Equals(candidate.Message, finding.Message, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.ReceiptIds = existing.ReceiptIds
                .Concat(finding.ReceiptIds)
                .Distinct(StringComparer.Ordinal)
                .Take(AiMeteringValidator.MaxReviewReceipts)
                .ToList();
            existing.MeasurementNames = existing.MeasurementNames
                .Concat(finding.MeasurementNames)
                .Distinct(StringComparer.Ordinal)
                .Take(AiMeteringValidator.MaxMeasurements)
                .ToList();
            return;
        }
        if (review.Findings.Count >= AiMeteringValidator.MaxReviewFindings)
        {
            throw new InvalidOperationException("AI metering review finding cardinality exceeds the contract bound.");
        }
        review.Findings.Add(finding);
    }
}
