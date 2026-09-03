namespace Vyral.Providers.Abstractions;

/// <summary>Creates privacy-minimized runner observations around provider runs.</summary>
public static class AiMeteringReceiptFactory
{
    public static AiMeteringReceipt CreateProviderRunSummary(
        ProviderRunRequest request,
        ProviderRunResult result,
        string providerRunId,
        string? executionRunId,
        DateTimeOffset observedStartedAt,
        DateTimeOffset observedCompletedAt,
        long elapsedDurationMs,
        long queueDurationMs,
        long activeDurationMs,
        bool providerInvoked)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRunId);
        var contextErrors = AiMeteringValidator.ValidateContext(request.MeteringContext);
        if (contextErrors.Count > 0)
        {
            throw new ArgumentException("Invalid AI metering context: " + string.Join("; ", contextErrors), nameof(request));
        }
        var context = request.MeteringContext;
        // Monotonic elapsed time remains authoritative if the wall clock steps backwards.
        // Clamp the display timestamp rather than failing an otherwise valid provider run.
        var normalizedCompletedAt = observedCompletedAt < observedStartedAt
            ? observedStartedAt
            : observedCompletedAt;
        var receipt = new AiMeteringReceipt
        {
            Kind = AiMeteringReceiptKinds.Summary,
            Subject = new AiMeteringSubject
            {
                ProviderRunId = providerRunId,
                ExecutionRunId = executionRunId,
                ProviderThreadId = context?.ProviderThreadId,
                RunnerSessionId = context?.RunnerSessionId,
                TurnId = context?.TurnId,
                CorrelationId = request.CorrelationId
            },
            Provider = result.Provider,
            Capability = result.Capability,
            Operation = result.Operation,
            Outcome = result.Status switch
            {
                ProviderRunStatus.Succeeded => AiMeteringOutcomes.Succeeded,
                ProviderRunStatus.TimedOut => AiMeteringOutcomes.TimedOut,
                ProviderRunStatus.Rejected => AiMeteringOutcomes.Rejected,
                ProviderRunStatus.Unsupported => AiMeteringOutcomes.Unsupported,
                ProviderRunStatus.NotConfigured => AiMeteringOutcomes.NotConfigured,
                ProviderRunStatus.Cancelled => AiMeteringOutcomes.Cancelled,
                _ => AiMeteringOutcomes.Failed
            },
            ModelId = result.Trace?.ModelId ?? request.ModelId,
            AdapterId = result.Trace?.AdapterId,
            Period = new AiMeteringPeriod
            {
                ObservedStartedAt = AiMeteringTimestamp.Format(observedStartedAt),
                ObservedCompletedAt = AiMeteringTimestamp.Format(normalizedCompletedAt),
                ElapsedDurationMs = Math.Max(0, elapsedDurationMs),
                QueueDurationMs = Math.Max(0, queueDurationMs),
                ActiveDurationMs = Math.Max(0, activeDurationMs),
                ProviderDurationMs = result.Trace is null ? null : Math.Max(0, (long)Math.Round(result.Trace.DurationMs))
            },
            Completeness = AiMeteringCompleteness.Partial,
            AttestationLevel = AiMeteringAttestationLevels.SelfReported,
            Sequence = context?.Sequence,
            PreviousReceiptHash = context?.PreviousReceiptHash,
            IssuedAt = AiMeteringTimestamp.Format(normalizedCompletedAt),
            Measurements =
            {
                new AiMeteringMeasurement
                {
                    Name = AiMeteringMeasurementNames.ProviderCalls,
                    Value = providerInvoked ? 1 : 0,
                    Unit = AiMeteringUnits.Count,
                    Source = AiMeteringSources.RunnerObserver,
                    Quality = AiMeteringQualities.Observed,
                    SourceId = "vyral.provider-runner",
                    Method = "provider invocation boundary"
                },
                new AiMeteringMeasurement
                {
                    Name = AiMeteringMeasurementNames.PayloadBytesIn,
                    Value = System.Text.Encoding.UTF8.GetByteCount(request.Payload.ToJsonString(ProviderJson.Options)),
                    Unit = AiMeteringUnits.Bytes,
                    Source = AiMeteringSources.RunnerObserver,
                    Quality = AiMeteringQualities.Observed,
                    SourceId = "vyral.provider-runner",
                    Method = "serialized provider payload byte count"
                },
                new AiMeteringMeasurement
                {
                    Name = AiMeteringMeasurementNames.PayloadBytesOut,
                    Value = System.Text.Encoding.UTF8.GetByteCount(result.Output.ToJsonString(ProviderJson.Options)),
                    Unit = AiMeteringUnits.Bytes,
                    Source = AiMeteringSources.RunnerObserver,
                    Quality = AiMeteringQualities.Observed,
                    SourceId = "vyral.provider-runner",
                    Method = "serialized provider output byte count"
                },
                new AiMeteringMeasurement
                {
                    Name = AiMeteringMeasurementNames.ContextReferences,
                    Value = request.ContextRefs.Count,
                    Unit = AiMeteringUnits.Count,
                    Source = AiMeteringSources.RunnerObserver,
                    Quality = AiMeteringQualities.Observed,
                    SourceId = "vyral.provider-runner",
                    Method = "provider request context reference count"
                }
            }
        };

        // Trace refs and normalized output artifacts are normally two projections of the same
        // outputs. Use the larger observed count rather than silently double-counting them.
        var artifactCount = Math.Max(
            result.Trace?.ArtifactRefs.Count ?? 0,
            result.Output["artifacts"] is System.Text.Json.Nodes.JsonArray artifacts ? artifacts.Count : 0);
        if (artifactCount > 0)
        {
            receipt.Measurements.Add(new AiMeteringMeasurement
            {
                Name = AiMeteringMeasurementNames.Artifacts,
                Value = artifactCount,
                Unit = AiMeteringUnits.Count,
                Source = AiMeteringSources.RunnerObserver,
                Quality = AiMeteringQualities.Observed,
                SourceId = "vyral.provider-runner",
                Method = "maximum of trace references and normalized output artifacts"
            });
        }

        receipt.Measurements.AddRange(result.MeteringMeasurements.Select(CloneMeasurement));

        AddHashEvidence(receipt, "request", result.Trace?.InputHash ?? ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)));
        AddHashEvidence(receipt, "output", result.Trace?.OutputHash);
        AddHashEvidence(receipt, "configuration", result.Trace?.ConfigHash);
        AiMeteringValidator.ValidateReceipt(receipt).ThrowIfInvalid();
        return receipt;
    }

    private static void AddHashEvidence(AiMeteringReceipt receipt, string kind, string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return;
        }
        receipt.Evidence.Add(new AiMeteringEvidenceReference
        {
            Kind = kind,
            Digest = digest,
            Redacted = true
        });
    }

    private static AiMeteringMeasurement CloneMeasurement(AiMeteringMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        return new AiMeteringMeasurement
        {
            Name = measurement.Name,
            Value = measurement.Value,
            Unit = measurement.Unit,
            Source = measurement.Source,
            Quality = measurement.Quality,
            SourceId = measurement.SourceId,
            Method = measurement.Method,
            TokenizerId = measurement.TokenizerId
        };
    }
}
