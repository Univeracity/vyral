using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vyral.Providers.Abstractions;

namespace Vyral.Tests.Providers;

public sealed class AiMeteringContractTests
{
    [Fact]
    public void Receipt_SignsAndVerifiesWithoutExposingRawEvidence()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var receipt = CreateReceipt();

        AiMeteringCryptography.SignReceipt(receipt, key, "spiffe://example.test/runner", "runner-2026-09");

        var verification = AiMeteringCryptography.VerifyReceipt(
            receipt,
            key,
            expectedIssuer: "spiffe://example.test/runner",
            expectedKeyId: "runner-2026-09");
        Assert.True(verification.Valid, string.Join("; ", verification.Errors));
        Assert.Equal(receipt.Integrity!.PayloadHash, verification.PayloadHash);
        var json = JsonSerializer.Serialize(receipt, ProviderJson.Options);
        Assert.DoesNotContain("raw prompt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw output", json, StringComparison.Ordinal);
        Assert.Contains("sha256:", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_VerificationRejectsTamperingAndUntrustedIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var receipt = CreateReceipt();
        AiMeteringCryptography.SignReceipt(receipt, key, "runner-a", "key-a");

        receipt.Measurements[0].Value++;
        var tampered = AiMeteringCryptography.VerifyReceipt(receipt, key, "runner-a", "key-a");
        Assert.False(tampered.Valid);
        Assert.Contains(tampered.Errors, error => error.Contains("payloadHash", StringComparison.Ordinal));

        receipt.Measurements[0].Value--;
        var wrongIssuer = AiMeteringCryptography.VerifyReceipt(receipt, key, "runner-b", "key-a");
        Assert.False(wrongIssuer.Valid);
        Assert.Contains(wrongIssuer.Errors, error => error.Contains("trusted issuer", StringComparison.Ordinal));

        receipt.Integrity!.Issuer = "substituted-runner";
        var substitutedIssuer = AiMeteringCryptography.VerifyReceipt(receipt, key);
        Assert.False(substitutedIssuer.Valid);
        Assert.Contains(substitutedIssuer.Errors, error => error.Contains("signature", StringComparison.Ordinal));

        receipt.Integrity!.Signature = null!;
        var missingSignature = AiMeteringCryptography.VerifyReceipt(receipt, key);
        Assert.False(missingSignature.Valid);
        Assert.Contains(missingSignature.Errors, error => error.Contains("signature", StringComparison.Ordinal));
    }

    [Fact]
    public void Receipt_RemoteSigningRequestBindsIdentityAndCurrentPayload()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var receipt = CreateReceipt();
        var signingRequest = AiMeteringCryptography.CreateReceiptSigningRequest(receipt, "kms-runner", "kms-key-1");
        var signature = key.SignData(
            signingRequest.Data.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        AiMeteringCryptography.ApplyReceiptSignature(receipt, signingRequest, signature);
        var verified = AiMeteringCryptography.VerifyReceipt(receipt, key, "kms-runner", "kms-key-1");
        Assert.True(verified.Valid, string.Join("; ", verified.Errors));

        receipt.Measurements[0].Value++;
        Assert.Throws<InvalidOperationException>(() =>
            AiMeteringCryptography.ApplyReceiptSignature(receipt, signingRequest, signature));
        Assert.Throws<ArgumentException>(() =>
            AiMeteringCryptography.CreateReceiptSigningRequest(CreateReceipt(), new string('i', 513), "key"));
    }

    [Fact]
    public void Receipt_VerificationRejectsWrongCurveAndWrongKey()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var receipt = CreateReceipt();
        AiMeteringCryptography.SignReceipt(receipt, signingKey, "runner-a", "key-a");

        Assert.True(AiMeteringCryptography.IsP256(signingKey));
        Assert.False(AiMeteringCryptography.IsP256(p384));
        Assert.False(AiMeteringCryptography.VerifyReceipt(receipt, otherKey).Valid);
        var wrongCurve = AiMeteringCryptography.VerifyReceipt(receipt, p384);
        Assert.False(wrongCurve.Valid);
        Assert.Contains(wrongCurve.Errors, error => error.Contains("P-256", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => AiMeteringCryptography.SignReceipt(CreateReceipt(), p384, "runner-a", "key-a"));
    }

    [Fact]
    public void StrictParsing_RejectsUnknownDuplicateAndOversizedEvidence()
    {
        var receipt = CreateReceipt();
        var json = JsonSerializer.Serialize(receipt, ProviderJson.Options);
        var parsed = AiMeteringCryptography.DeserializeReceipt(Encoding.UTF8.GetBytes(json));
        Assert.Equal(receipt.ReceiptId, parsed.ReceiptId);

        var unknown = json.Insert(json.Length - 1, ",\"unexpectedAuthority\":true");
        Assert.Throws<JsonException>(() => AiMeteringCryptography.DeserializeReceipt(Encoding.UTF8.GetBytes(unknown)));
        var duplicate = json.Replace(
            "\"schema\":\"vyral.ai-metering.v1\"",
            "\"schema\":\"vyral.ai-metering.v1\",\"schema\":\"vyral.ai-metering.v1\"",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => AiMeteringCryptography.DeserializeReceipt(Encoding.UTF8.GetBytes(duplicate)));
        Assert.Throws<JsonException>(() => AiMeteringCryptography.DeserializeReceipt(
            new byte[AiMeteringCryptography.MaxEvidenceJsonBytes + 1]));
    }

    [Fact]
    public void ReceiptChain_DetectsDeletionReorderingAndPriorEnvelopeMutation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = CreateReceipt(sequence: 0);
        AiMeteringCryptography.SignReceipt(first, key, "runner-a", "key-a");
        var second = CreateReceipt(sequence: 1, previous: AiMeteringCryptography.ComputeReceiptEnvelopeHash(first));
        second.Subject.TurnId = "turn-2";
        AiMeteringCryptography.SignReceipt(second, key, "runner-a", "key-a");

        var valid = AiMeteringValidator.ValidateChain(new[] { first, second });
        Assert.True(valid.Valid, string.Join("; ", valid.Errors));

        Assert.False(AiMeteringValidator.ValidateChain(new[] { second, first }).Valid);
        Assert.False(AiMeteringValidator.ValidateChain(new[] { second }).Valid);

        first.Integrity!.KeyId = "substituted-key";
        var mutatedPriorEnvelope = AiMeteringValidator.ValidateChain(new[] { first, second });
        Assert.False(mutatedPriorEnvelope.Valid);
        Assert.Contains(mutatedPriorEnvelope.Errors, error => error.Contains("previousReceiptHash", StringComparison.Ordinal));
    }

    [Fact]
    public void IndependentReview_BindsOrderedReceiptEnvelopesAndHasItsOwnSignature()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reviewerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var receipt = CreateReceipt();
        receipt.Completeness = AiMeteringCompleteness.Complete;
        receipt.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
        AiMeteringCryptography.SignReceipt(receipt, runnerKey, "runner-a", "runner-key");

        var review = AiMeteringReviewer.ReviewChain(
            new[] { receipt },
            "vyral.metering.basic.v1",
            item => AiMeteringCryptography.VerifyReceipt(item, runnerKey, "runner-a", "runner-key"),
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"));
        Assert.Equal(AiMeteringReviewVerdicts.Verified, review.Verdict);
        Assert.Empty(review.Findings);
        Assert.NotNull(review.Aggregate);
        Assert.Equal(1, review.Aggregate!.ReceiptCount);
        Assert.Equal(1250, review.Aggregate.Period.SummedElapsedDurationMs);

        AiMeteringCryptography.SignReview(review, reviewerKey, "reviewer-a", "review-key");
        var verified = AiMeteringCryptography.VerifyReview(review, reviewerKey, "reviewer-a", "review-key");
        Assert.True(verified.Valid, string.Join("; ", verified.Errors));
        var verifiedBundle = AiMeteringReviewer.VerifyReviewBundle(
            review,
            new[] { receipt },
            item => AiMeteringCryptography.VerifyReview(item, reviewerKey, "reviewer-a", "review-key"),
            item => AiMeteringCryptography.VerifyReceipt(item, runnerKey, "runner-a", "runner-key"));
        Assert.True(verifiedBundle.Valid, string.Join("; ", verifiedBundle.Errors));
        Assert.NotEqual(receipt.Integrity!.Issuer, review.Integrity!.Issuer);

        review.RulesetId = "altered";
        Assert.False(AiMeteringCryptography.VerifyReview(review, reviewerKey).Valid);
    }

    [Fact]
    public void ReviewBundleVerification_RejectsSubstitutionAndIncorrectSignedTotals()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reviewerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var receipt = CreateReceipt();
        receipt.Completeness = AiMeteringCompleteness.Complete;
        receipt.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
        AiMeteringCryptography.SignReceipt(receipt, runnerKey, "runner-a", "runner-key");
        var review = AiMeteringReviewer.ReviewChain(
            new[] { receipt },
            AiMeteringRulesets.BasicV1,
            item => AiMeteringCryptography.VerifyReceipt(item, runnerKey, "runner-a", "runner-key"),
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"));

        review.Aggregate!.Measurements[0].Value++;
        AiMeteringCryptography.SignReview(review, reviewerKey, "reviewer-a", "review-key");
        Assert.True(AiMeteringCryptography.VerifyReview(review, reviewerKey, "reviewer-a", "review-key").Valid);
        var wrongTotal = AiMeteringReviewer.VerifyReviewBundle(
            review,
            new[] { receipt },
            item => AiMeteringCryptography.VerifyReview(item, reviewerKey, "reviewer-a", "review-key"),
            item => AiMeteringCryptography.VerifyReceipt(item, runnerKey, "runner-a", "runner-key"));
        Assert.False(wrongTotal.Valid);
        Assert.Contains(wrongTotal.Errors, error => error.Contains("ruleset replay", StringComparison.Ordinal));

        var substitute = CreateReceipt();
        substitute.Completeness = AiMeteringCompleteness.Complete;
        substitute.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
        substitute.Measurements[0].Value = 99;
        AiMeteringCryptography.SignReceipt(substitute, runnerKey, "runner-a", "runner-key");
        var substituted = AiMeteringReviewer.VerifyReviewBundle(
            review,
            new[] { substitute },
            item => AiMeteringCryptography.VerifyReview(item, reviewerKey, "reviewer-a", "review-key"),
            item => AiMeteringCryptography.VerifyReceipt(item, runnerKey, "runner-a", "runner-key"));
        Assert.False(substituted.Valid);
        Assert.Contains(substituted.Errors, error => error.Contains("receiptHashes", StringComparison.Ordinal));
    }

    [Fact]
    public void Review_ReportsPartialCoverageWithoutCallingItVerified()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var receipt = CreateReceipt();
        AiMeteringCryptography.SignReceipt(receipt, runnerKey, "runner-a", "runner-key");

        var review = AiMeteringReviewer.ReviewChain(
            new[] { receipt },
            "vyral.metering.basic.v1",
            item => AiMeteringCryptography.VerifyReceipt(item, runnerKey),
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"));

        Assert.Equal(AiMeteringReviewVerdicts.VerifiedWithGaps, review.Verdict);
        Assert.Contains(review.Findings, finding => finding.Code == "receipt_coverage_incomplete");
        Assert.Contains(review.Findings, finding => finding.Code == "receipt_self_reported");
    }

    [Fact]
    public void Review_RejectsMalformedReceiptWithoutRunningUnsafeConsistencyChecks()
    {
        var receipt = CreateReceipt(sequence: null);
        receipt.Period = null!;
        receipt.Measurements = null!;
        var verifierCalled = false;

        var review = AiMeteringReviewer.ReviewReceipts(
            new[] { receipt },
            new AiMeteringScope { Kind = AiMeteringScopeKinds.RunnerSession, Id = "runner-session-1" },
            AiMeteringRulesets.BasicV1,
            _ =>
            {
                verifierCalled = true;
                throw new InvalidOperationException("Malformed evidence must not reach the verifier.");
            },
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"));

        Assert.False(verifierCalled);
        Assert.Equal(AiMeteringReviewVerdicts.Rejected, review.Verdict);
        Assert.Null(review.Aggregate);
        Assert.Contains(review.Findings, finding => finding.Code == "receipt_structure_invalid");
        Assert.True(AiMeteringValidator.ValidateReview(review).Valid);
    }

    [Fact]
    public void Review_FlagsClockAndProviderTokenInconsistency()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var receipt = CreateReceipt();
        receipt.Completeness = AiMeteringCompleteness.Complete;
        receipt.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
        receipt.Period.ObservedCompletedAt = "2026-09-03T12:00:10.000Z";
        receipt.IssuedAt = "2026-09-03T12:00:10.000Z";
        receipt.Measurements.AddRange(new[]
        {
            new AiMeteringMeasurement
            {
                Name = AiMeteringMeasurementNames.OutputTokens,
                Value = 8,
                Unit = AiMeteringUnits.Tokens,
                Source = AiMeteringSources.ProviderResponse,
                Quality = AiMeteringQualities.Reported,
                SourceId = "provider-response-1"
            },
            new AiMeteringMeasurement
            {
                Name = AiMeteringMeasurementNames.TotalTokens,
                Value = 100,
                Unit = AiMeteringUnits.Tokens,
                Source = AiMeteringSources.ProviderResponse,
                Quality = AiMeteringQualities.Reported,
                SourceId = "provider-response-1"
            }
        });
        AiMeteringCryptography.SignReceipt(receipt, runnerKey, "runner-a", "runner-key");

        var review = AiMeteringReviewer.ReviewChain(
            new[] { receipt },
            "vyral.metering.basic.v1",
            item => AiMeteringCryptography.VerifyReceipt(item, runnerKey),
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"));

        Assert.Equal(AiMeteringReviewVerdicts.VerifiedWithGaps, review.Verdict);
        Assert.Contains(review.Findings, finding => finding.Code == "clock_elapsed_mismatch");
        Assert.Contains(review.Findings, finding => finding.Code == "token_total_mismatch");
    }

    [Fact]
    public void ReviewReceipts_AggregatesConcurrentStandaloneRunsWithoutDoubleCountingSources()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = CreateReceipt(sequence: null);
        first.ReceiptId = "amr_first";
        first.Completeness = AiMeteringCompleteness.Complete;
        first.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
        var second = CreateReceipt(sequence: null);
        second.ReceiptId = "amr_second";
        second.Subject.ProviderRunId = "provider-run-2";
        second.Subject.TurnId = "turn-2";
        second.Period.ObservedStartedAt = "2026-09-03T12:00:00.500Z";
        second.Period.ObservedCompletedAt = "2026-09-03T12:00:02.000Z";
        second.Period.ElapsedDurationMs = 1500;
        second.IssuedAt = "2026-09-03T12:00:02.000Z";
        second.Completeness = AiMeteringCompleteness.Complete;
        second.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
        AiMeteringCryptography.SignReceipt(first, runnerKey, "runner-a", "runner-key");
        AiMeteringCryptography.SignReceipt(second, runnerKey, "runner-a", "runner-key");

        var review = AiMeteringReviewer.ReviewReceipts(
            new[] { first, second },
            new AiMeteringScope { Kind = AiMeteringScopeKinds.RunnerSession, Id = "runner-session-1" },
            "vyral.metering.basic.v1",
            item => AiMeteringCryptography.VerifyReceipt(item, runnerKey, "runner-a", "runner-key"),
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"));

        Assert.Equal(AiMeteringReviewVerdicts.Verified, review.Verdict);
        var aggregate = Assert.IsType<AiMeteringAggregate>(review.Aggregate);
        Assert.Equal(2, aggregate.ReceiptCount);
        Assert.Equal(2, aggregate.ProviderRunCount);
        Assert.Equal(2000, aggregate.Period.WallSpanDurationMs);
        Assert.Equal(2750, aggregate.Period.SummedElapsedDurationMs);
        Assert.Equal(2000, aggregate.Period.SummedActiveDurationMs);
        Assert.True(aggregate.Period.ConcurrentIntervalsDetected);
        var tokens = Assert.Single(aggregate.Measurements, measurement => measurement.Name == AiMeteringMeasurementNames.InputTokens);
        Assert.Equal(84, tokens.Value);
        Assert.Equal(2, tokens.ReceiptCount);
        Assert.Equal("provider-a", tokens.Provider);
    }

    [Fact]
    public void ReviewReceipts_RejectsScopeMismatchAndDuplicateProviderRun()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = CreateReceipt(sequence: null);
        first.ReceiptId = "amr_first";
        first.Completeness = AiMeteringCompleteness.Complete;
        first.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
        var second = CreateReceipt(sequence: null);
        second.ReceiptId = "amr_second";
        second.Subject.RunnerSessionId = "different-session";
        second.Completeness = AiMeteringCompleteness.Complete;
        second.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
        AiMeteringCryptography.SignReceipt(first, runnerKey, "runner-a", "runner-key");
        AiMeteringCryptography.SignReceipt(second, runnerKey, "runner-a", "runner-key");

        var review = AiMeteringReviewer.ReviewReceipts(
            new[] { first, second },
            new AiMeteringScope { Kind = AiMeteringScopeKinds.RunnerSession, Id = "runner-session-1" },
            "vyral.metering.basic.v1",
            item => AiMeteringCryptography.VerifyReceipt(item, runnerKey),
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"));

        Assert.Equal(AiMeteringReviewVerdicts.Rejected, review.Verdict);
        Assert.Null(review.Aggregate);
        Assert.Contains(review.Findings, finding => finding.Code == "receipt_scope_mismatch");
        Assert.Contains(review.Findings, finding => finding.Code == "provider_run_summary_duplicated");
    }

    [Fact]
    public void ReviewReceipts_KeepsIndependentObservationsSeparateFromRunnerSummaries()
    {
        using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var observerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var summary = CreateReceipt(sequence: null);
        summary.ReceiptId = "amr_summary";
        summary.Completeness = AiMeteringCompleteness.Complete;
        summary.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
        var observation = CreateReceipt(sequence: null);
        observation.ReceiptId = "amr_observation";
        observation.Kind = AiMeteringReceiptKinds.Observation;
        observation.Measurements[0].Source = AiMeteringSources.ProviderEventStream;
        observation.Measurements[0].SourceId = "provider-event-1";
        observation.Completeness = AiMeteringCompleteness.Complete;
        observation.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
        AiMeteringCryptography.SignReceipt(summary, runnerKey, "runner-a", "runner-key");
        AiMeteringCryptography.SignReceipt(observation, observerKey, "observer-a", "observer-key");

        var review = AiMeteringReviewer.ReviewReceipts(
            new[] { summary, observation },
            new AiMeteringScope { Kind = AiMeteringScopeKinds.RunnerSession, Id = "runner-session-1" },
            AiMeteringRulesets.BasicV1,
            item => item.Integrity?.Issuer == "runner-a"
                ? AiMeteringCryptography.VerifyReceipt(item, runnerKey, "runner-a", "runner-key")
                : AiMeteringCryptography.VerifyReceipt(item, observerKey, "observer-a", "observer-key"),
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"));

        Assert.Equal(AiMeteringReviewVerdicts.Verified, review.Verdict);
        var aggregate = Assert.IsType<AiMeteringAggregate>(review.Aggregate);
        Assert.Equal(2, aggregate.ReceiptCount);
        Assert.Equal(1, aggregate.SummaryReceiptCount);
        Assert.Equal(1, aggregate.ObservationReceiptCount);
        Assert.Equal(1, aggregate.ProviderRunCount);
        var tokenStreams = aggregate.Measurements
            .Where(measurement => measurement.Name == AiMeteringMeasurementNames.InputTokens)
            .ToList();
        Assert.Equal(2, tokenStreams.Count);
        Assert.Contains(tokenStreams, measurement => measurement.ReceiptKind == AiMeteringReceiptKinds.Summary && measurement.Value == 42);
        Assert.Contains(tokenStreams, measurement => measurement.ReceiptKind == AiMeteringReceiptKinds.Observation && measurement.Value == 42);
        Assert.Equal(1250, aggregate.Period.SummedElapsedDurationMs);
    }

    [Fact]
    public void Factory_BindsVyralAndProviderSessionScopesWithObservedWork()
    {
        var request = new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            CorrelationId = "correlation-1",
            Payload = new() { ["prompt"] = "raw prompt" },
            MeteringContext = new AiMeteringContext
            {
                ProviderThreadId = "thread-1",
                RunnerSessionId = "session-1",
                TurnId = "turn-1"
            }
        };
        var result = new ProviderRunResult
        {
            Status = ProviderRunStatus.Succeeded,
            Provider = "provider-a",
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "advisory",
            Output = new() { ["text"] = "raw output" },
            Trace = new ProviderTraceEvent
            {
                InputHash = ProviderHash.Sha256("input"),
                OutputHash = ProviderHash.Sha256("output"),
                ConfigHash = ProviderHash.Sha256("config"),
                AdapterId = "adapter-a",
                ModelId = "model-a",
                DurationMs = 115
            }
        };
        result.MeteringMeasurements.Add(new AiMeteringMeasurement
        {
            Name = AiMeteringMeasurementNames.OutputTokens,
            Value = 9,
            Unit = AiMeteringUnits.Tokens,
            Source = AiMeteringSources.ProviderResponse,
            Quality = AiMeteringQualities.Reported,
            SourceId = "provider-response-1"
        });

        var receipt = AiMeteringReceiptFactory.CreateProviderRunSummary(
            request,
            result,
            "provider-run-1",
            "execution-run-1",
            DateTimeOffset.Parse("2026-09-03T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-03T12:00:00.125Z"),
            elapsedDurationMs: 125,
            queueDurationMs: 10,
            activeDurationMs: 115,
            providerInvoked: true);

        Assert.Equal("provider-run-1", receipt.Subject.ProviderRunId);
        Assert.Equal("execution-run-1", receipt.Subject.ExecutionRunId);
        Assert.Equal("thread-1", receipt.Subject.ProviderThreadId);
        Assert.Equal("session-1", receipt.Subject.RunnerSessionId);
        Assert.Equal(125, receipt.Period.ElapsedDurationMs);
        Assert.Equal(115, receipt.Period.ProviderDurationMs);
        Assert.Equal(1, receipt.Measurements.Single(item => item.Name == AiMeteringMeasurementNames.ProviderCalls).Value);
        Assert.Equal(9, receipt.Measurements.Single(item => item.Name == AiMeteringMeasurementNames.OutputTokens).Value);
        Assert.True(receipt.Measurements.Single(item => item.Name == AiMeteringMeasurementNames.PayloadBytesIn).Value > 0);
        Assert.True(receipt.Measurements.Single(item => item.Name == AiMeteringMeasurementNames.PayloadBytesOut).Value > 0);
        Assert.Equal(3, receipt.Evidence.Count);
        Assert.True(AiMeteringValidator.ValidateReceipt(receipt).Valid);
        var json = Encoding.UTF8.GetString(AiMeteringCryptography.CanonicalReceiptPayload(receipt));
        Assert.DoesNotContain("raw prompt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw output", json, StringComparison.Ordinal);
        Assert.DoesNotContain("meteringMeasurements", JsonSerializer.Serialize(result, ProviderJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_RequiresMethodForEstimatesAndConsistentTimeBounds()
    {
        var receipt = CreateReceipt();
        receipt.Measurements[0].Quality = AiMeteringQualities.Estimated;
        receipt.Measurements[0].Method = null;
        receipt.Measurements[0].Unit = AiMeteringUnits.Bytes;
        receipt.Measurements[0].Value = AiMeteringValidator.MaxPortableInteger + 1;
        receipt.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
        receipt.Period.ActiveDurationMs = receipt.Period.ElapsedDurationMs + 1;
        receipt.Period.IdleDurationMs = 1;
        receipt.Measurements.Add(new AiMeteringMeasurement
        {
            Name = receipt.Measurements[0].Name,
            Value = receipt.Measurements[0].Value,
            Unit = receipt.Measurements[0].Unit,
            Source = receipt.Measurements[0].Source,
            Quality = receipt.Measurements[0].Quality,
            SourceId = receipt.Measurements[0].SourceId
        });

        var validation = AiMeteringValidator.ValidateReceipt(receipt);
        Assert.False(validation.Valid);
        Assert.Contains(validation.Errors, error => error.Contains("method is required", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("active and idle", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("unit must be 'tokens'", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("duplicate observation", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("integrity is required", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("portable JSON integer limit", StringComparison.Ordinal));
    }

    [Fact]
    public void UsageNormalizer_PreservesProviderProvenanceAndLabelsDerivedTotals()
    {
        var usage = new System.Text.Json.Nodes.JsonObject
        {
            ["inputTokens"] = 100,
            ["outputTokens"] = 25,
            ["modelCalls"] = 2,
            ["outputTokensDetails"] = new System.Text.Json.Nodes.JsonObject
            {
                ["reasoningTokens"] = 7
            },
            ["costUsdTicks"] = 1234,
            ["numTurns"] = AiMeteringValidator.MaxPortableInteger + 1,
            ["negative"] = -1
        };

        var measurements = AiMeteringUsageNormalizer.Normalize(usage, sourceId: "provider-event-1");

        Assert.Equal(100, measurements.Single(item => item.Name == AiMeteringMeasurementNames.InputTokens).Value);
        Assert.Equal(25, measurements.Single(item => item.Name == AiMeteringMeasurementNames.OutputTokens).Value);
        Assert.Equal(7, measurements.Single(item => item.Name == AiMeteringMeasurementNames.ReasoningOutputTokens).Value);
        Assert.Equal(2, measurements.Single(item => item.Name == AiMeteringMeasurementNames.ModelCalls).Value);
        var total = measurements.Single(item => item.Name == AiMeteringMeasurementNames.TotalTokens);
        Assert.Equal(125, total.Value);
        Assert.Equal(AiMeteringSources.ConsumerInference, total.Source);
        Assert.Equal(AiMeteringQualities.Estimated, total.Quality);
        Assert.NotNull(total.Method);
        Assert.DoesNotContain(measurements, item => item.Name.Contains("cost", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(measurements, item => item.Name == AiMeteringMeasurementNames.Turns);

        var unrepresentableTotal = AiMeteringUsageNormalizer.Normalize(new System.Text.Json.Nodes.JsonObject
        {
            ["inputTokens"] = AiMeteringValidator.MaxPortableInteger,
            ["outputTokens"] = 1
        });
        Assert.DoesNotContain(unrepresentableTotal, item => item.Name == AiMeteringMeasurementNames.TotalTokens);
    }

    private static AiMeteringReceipt CreateReceipt(int? sequence = 0, string? previous = null)
    {
        return new AiMeteringReceipt
        {
            ReceiptId = $"amr_test_{sequence}",
            Subject = new AiMeteringSubject
            {
                ProviderRunId = "provider-run-1",
                RunnerSessionId = "runner-session-1",
                TurnId = "turn-1"
            },
            Provider = "provider-a",
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            ModelId = "model-a",
            AdapterId = "adapter-a",
            Period = new AiMeteringPeriod
            {
                ObservedStartedAt = "2026-09-03T12:00:00.000Z",
                ObservedCompletedAt = "2026-09-03T12:00:01.250Z",
                ElapsedDurationMs = 1250,
                ActiveDurationMs = 1000,
                IdleDurationMs = 250,
                ProviderDurationMs = 950
            },
            Measurements =
            {
                new AiMeteringMeasurement
                {
                    Name = AiMeteringMeasurementNames.InputTokens,
                    Value = 42,
                    Unit = AiMeteringUnits.Tokens,
                    Source = AiMeteringSources.ProviderResponse,
                    Quality = AiMeteringQualities.Reported,
                    SourceId = "provider-response-1"
                }
            },
            Evidence =
            {
                new AiMeteringEvidenceReference
                {
                    Kind = "request",
                    Digest = ProviderHash.Sha256("raw prompt"),
                    Redacted = true
                }
            },
            Completeness = AiMeteringCompleteness.Partial,
            Sequence = sequence,
            PreviousReceiptHash = previous,
            IssuedAt = "2026-09-03T12:00:01.250Z"
        };
    }
}
