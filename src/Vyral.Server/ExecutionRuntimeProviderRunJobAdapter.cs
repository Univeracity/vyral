using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Providers.Abstractions;

namespace Vyral.Server;

public sealed class ExecutionRuntimeProviderRunJobAdapter
{
    public const string PluginId = "vyral.providers";
    public const string HandlerId = "vyral.provider.run";

    private readonly IExecutionRuntime _runtime;
    private readonly ProviderTargetRegistry _registry;

    public ExecutionRuntimeProviderRunJobAdapter(
        IExecutionRuntime runtime,
        ProviderTargetRegistry registry,
        ITraceStore traces,
        ProviderRunGuard guard,
        ProviderRunJobStoreOptions? options = null,
        IAiMeteringReceiptSigner? meteringSigner = null)
    {
        _runtime = runtime;
        _registry = registry;
        Options = options ?? new ProviderRunJobStoreOptions();
        PersistenceKind = DescribePersistence(runtime);
        _runtime.RegisterPlugin(new ProviderRunExecutionPlugin(registry, traces, guard, meteringSigner));
    }

    public ProviderRunJobStoreOptions Options { get; }
    public string PersistenceKind { get; }

    private static string DescribePersistence(IExecutionRuntime runtime)
    {
        if (runtime is not IExecutionRuntimeAdapter adapter)
        {
            return "execution-runtime";
        }

        if (ExecutionCapabilityCatalog.Supports(adapter.Adapter.Capabilities, ExecutionCapabilityIds.LocalDispatch))
        {
            return "local";
        }

        if (ExecutionCapabilityCatalog.Supports(adapter.Adapter.Capabilities, ExecutionCapabilityIds.RemoteOrchestration))
        {
            return "remote";
        }

        return ExecutionCapabilityCatalog.Supports(adapter.Adapter.Capabilities, ExecutionCapabilityIds.DurableRuns)
            ? "durable"
            : "execution-runtime";
    }

    public async Task<ProviderRunJob> StartAsync(
        string provider,
        ProviderRunRequest request,
        string? artifactDirectory = null,
        string? idempotencyKey = null,
        string? admissionOperationId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new InvalidOperationException("Provider id is required.");
        }

        var meteringContextErrors = AiMeteringValidator.ValidateContext(request.MeteringContext);
        if (meteringContextErrors.Count > 0)
        {
            throw new InvalidOperationException("Invalid provider metering context: " + string.Join("; ", meteringContextErrors));
        }

        request.Provider = provider;
        request.ArtifactDirectory = artifactDirectory;
        if (string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            request.CorrelationId = Guid.NewGuid().ToString("N");
        }

        var payload = new ProviderRunJobPayload
        {
            Provider = provider,
            Request = request
        };
        var run = await _runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = HandlerId,
            PluginId = PluginId,
            Payload = JsonSerializer.SerializeToNode(payload, ExecutionJson.Options),
            IdempotencyKey = idempotencyKey,
            CorrelationId = request.CorrelationId,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 1 },
            Tags =
            {
                ["vyral.job"] = "provider-run",
                ["vyral.provider"] = provider,
                ["vyral.admission.operation-id"] = admissionOperationId ?? VyralAdmissionOperations.StartProviderRunJob
            }
        }, ct);

        return MapRun(run);
    }

    public async Task<ProviderRunJob?> GetAsync(string id, bool includeResult = true, CancellationToken ct = default)
    {
        var run = await _runtime.GetRunAsync(id, includeResult, ct);
        return IsProviderRun(run) ? MapRun(run!) : null;
    }

    public async Task<JsonObject?> GetProgressAsync(string id, CancellationToken ct = default)
    {
        var run = await _runtime.GetRunAsync(id, includeResult: false, ct);
        if (!IsProviderRun(run)) return null;
        var job = MapRun(run!);
        return ProviderProgressSnapshot.Project(run!, job,
            _registry.GetTarget(job.Provider)?.Profile.ConfigHash, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<ProviderRunJob>> ListAsync(
        string? provider = null,
        int? limit = null,
        bool includeResult = false,
        CancellationToken ct = default)
    {
        var effectiveLimit = ValidateListLimit(limit);
        var runs = await _runtime.ListRunsAsync(new ExecutionRunQuery
        {
            HandlerId = HandlerId,
            IncludeResult = includeResult,
            Limit = Options.MaxListLimit
        }, ct);

        return runs
            .Select(run => MapRun(run))
            .Where(job => string.IsNullOrWhiteSpace(provider) || string.Equals(job.Provider, provider, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(job => job.CreatedAt)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .ToList();
    }

    public async Task<ProviderRunJob?> CancelAsync(string id, CancellationToken ct = default)
    {
        var existing = await _runtime.GetRunAsync(id, includeResult: true, ct);
        if (!IsProviderRun(existing))
        {
            return null;
        }

        if (ExecutionRunStatuses.IsTerminal(existing!.Status))
        {
            return MapRun(existing, forceCancellationRequested: true);
        }

        var run = await _runtime.CancelRunAsync(id, ct);
        return run is null ? null : MapRun(run);
    }

    private int ValidateListLimit(int? limit)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new InvalidOperationException("Provider job list limit must be greater than zero.");
        }

        var effectiveLimit = limit ?? Options.DefaultListLimit;
        if (effectiveLimit > Options.MaxListLimit)
        {
            throw new InvalidOperationException($"Provider job list limit cannot exceed {Options.MaxListLimit}.");
        }

        return effectiveLimit;
    }

    private static bool IsProviderRun(ExecutionRun? run)
    {
        return run is not null && string.Equals(run.HandlerId, HandlerId, StringComparison.Ordinal);
    }

    private static ProviderRunJob MapRun(ExecutionRun run, bool forceCancellationRequested = false)
    {
        var payload = run.Payload?.Deserialize<ProviderRunJobPayload>(ExecutionJson.Options);
        var request = payload?.Request;
        var result = run.Result?.Deserialize<ProviderRunResult>(ExecutionJson.Options);
        var provider = payload?.Provider ?? request?.Provider ?? GetString(run.StatusDetails, "provider") ?? string.Empty;
        return new ProviderRunJob
        {
            Admission = ExecutionAdmission.Create(
                run,
                VyralAdmissionOperations.ResolveOperationId(run, VyralAdmissionOperations.StartProviderRunJob),
                $"/provider-jobs/{run.Id}"),
            Id = run.Id,
            Status = ToJobStatus(run.Status, result?.Status ?? ReadResultStatus(run.StatusDetails, run.Status)),
            Provider = provider,
            Capability = result?.Capability ?? request?.Capability ?? string.Empty,
            Operation = result?.Operation ?? request?.Operation ?? string.Empty,
            Mode = result?.Mode ?? request?.Mode ?? string.Empty,
            CorrelationId = request?.CorrelationId ?? run.CorrelationId,
            RequestHash = ProviderHash.Sha256(request?.Payload.ToJsonString(ProviderJson.Options)),
            CreatedAt = run.CreatedAtUtc,
            StartedAt = run.StartedAtUtc,
            CompletedAt = run.CompletedAtUtc,
            DurationMs = run.DurationMs,
            CancellationRequested = forceCancellationRequested || run.CancellationRequested,
            TraceId = result?.Trace?.TraceId ?? GetString(run.StatusDetails, "traceId"),
            FailureClass = result?.FailureClass ?? run.FailureClass,
            ProviderStatus = result?.ProviderStatus ?? GetString(run.StatusDetails, "providerStatus"),
            Result = result,
            Metering = result?.Metering ?? new List<AiMeteringReceipt>()
        };
    }

    private static ProviderJobStatus ToJobStatus(string runStatus, ProviderRunStatus? providerStatus)
    {
        if (!ExecutionRunStatuses.IsTerminal(runStatus))
        {
            return runStatus switch
            {
                ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting => ProviderJobStatus.Queued,
                ExecutionRunStatuses.Running => ProviderJobStatus.Running,
                _ => ProviderJobStatus.Failed
            };
        }

        // Persisting a provider response can precede a crash or cancellation.
        // Only refine a failed lifecycle with the two provider-specific failures;
        // a loaded result has no greater lifecycle authority than an observation.
        if (runStatus == ExecutionRunStatuses.Failed && providerStatus.HasValue)
        {
            return providerStatus.Value switch
            {
                ProviderRunStatus.Unsupported => ProviderJobStatus.Unsupported,
                ProviderRunStatus.NotConfigured => ProviderJobStatus.NotConfigured,
                _ => ProviderJobStatus.Failed
            };
        }

        return runStatus switch
        {
            ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting => ProviderJobStatus.Queued,
            ExecutionRunStatuses.Running => ProviderJobStatus.Running,
            ExecutionRunStatuses.Succeeded => ProviderJobStatus.Succeeded,
            ExecutionRunStatuses.Cancelled => ProviderJobStatus.Cancelled,
            ExecutionRunStatuses.Rejected => ProviderJobStatus.Rejected,
            ExecutionRunStatuses.TimedOut => ProviderJobStatus.TimedOut,
            _ => ProviderJobStatus.Failed
        };
    }

    private static string ToRunStatus(ProviderRunStatus status)
    {
        return status switch
        {
            ProviderRunStatus.Succeeded => ExecutionRunStatuses.Succeeded,
            ProviderRunStatus.Cancelled => ExecutionRunStatuses.Cancelled,
            ProviderRunStatus.TimedOut => ExecutionRunStatuses.TimedOut,
            ProviderRunStatus.Rejected => ExecutionRunStatuses.Rejected,
            _ => ExecutionRunStatuses.Failed
        };
    }

    private static ProviderRunStatus? ReadResultStatus(JsonObject? details, string runStatus)
    {
        // A persisted observation may precede a crash. It may refine a failure,
        // but must never promote the authoritative execution lifecycle to success.
        if (runStatus != ExecutionRunStatuses.Failed) return null;
        var value = GetString(details, "resultStatus");
        return Enum.TryParse<ProviderRunStatus>(value, out var status)
            && status is ProviderRunStatus.Unsupported or ProviderRunStatus.NotConfigured ? status : null;
    }

    private static string? GetString(JsonObject? details, string key)
    {
        return details is not null && details.TryGetPropertyValue(key, out var node)
            ? node?.GetValue<string>()
            : null;
    }

    private static JsonObject BuildStatusDetails(ProviderRunRequest request, ProviderRunResult? result = null)
    {
        var details = new JsonObject
        {
            ["provider"] = request.Provider,
            ["capability"] = request.Capability,
            ["operation"] = request.Operation,
            ["mode"] = request.Mode,
            ["providerStatus"] = result?.ProviderStatus,
            ["resultStatus"] = result?.Status.ToString(),
            ["traceId"] = result?.Trace?.TraceId
        };

        if (result?.Metering.Count > 0)
        {
            details["meteringReceiptHashes"] = new JsonArray(result.Metering
                .Select(receipt => (JsonNode?)AiMeteringCryptography.ComputeReceiptEnvelopeHash(receipt))
                .ToArray());
        }

        return details;
    }

    private sealed class ProviderRunExecutionHandler : IExecutionHandler
    {
        private readonly ProviderTargetRegistry _registry;
        private readonly ITraceStore _traces;
        private readonly ProviderRunGuard _guard;
        private readonly IAiMeteringReceiptSigner? _meteringSigner;

        public ProviderRunExecutionHandler(
            ProviderTargetRegistry registry,
            ITraceStore traces,
            ProviderRunGuard guard,
            IAiMeteringReceiptSigner? meteringSigner)
        {
            _registry = registry;
            _traces = traces;
            _guard = guard;
            _meteringSigner = meteringSigner;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            PluginId = PluginId,
            DisplayName = "Vyral provider run",
            Description = "Runs a provider target request.",
            MaxAttempts = 1,
            Tags =
            {
                ["vyral.job"] = "provider-run"
            }
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var payload = context.Run.Payload?.Deserialize<ProviderRunJobPayload>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("Provider run payload is required.");
            var request = payload.Request ?? throw new InvalidOperationException("Provider run request is required.");
            request.Provider = payload.Provider;
            if (string.IsNullOrWhiteSpace(request.CorrelationId))
            {
                request.CorrelationId = context.Run.CorrelationId;
            }

            // Restart recovery is a new handler execution, not authorization for
            // a second provider effect. Inspect retained evidence before overwriting it.
            if (context.Run.Attempt > 1 || context.Run.StatusDetails?.ContainsKey("providerInvocation") == true)
            {
                var retained = ProviderInvocationJournal.Read(context.Run, payload.Provider);
                var completion = retained?.RetainedCompletion(context.Run, request);
                var details = context.Run.StatusDetails?.DeepClone().AsObject() ?? new JsonObject();
                if (completion is not null)
                {
                    details["providerRecovery"] = "retained_completion";
                    return new ExecutionRunResult { Status = ToRunStatus(completion.Status),
                        Result = context.Run.Result!.DeepClone(), FailureClass = completion.FailureClass,
                        Error = completion.Error, StatusDetails = details };
                }
                var held = CreateTerminalResult(context.Run, request, ProviderRunStatus.Failed,
                    ProviderFailureClasses.Unknown, "provider_effect_unresolved",
                    "Interrupted provider invocation requires reconciliation; no provider was invoked during recovery.");
                held.Rejection = new ProviderRunRejectionDiagnostic
                {
                    Source = ProviderRejectionSources.ProviderRuntime, Category = "recovery_requires_reconciliation",
                    Message = "Original provider effects remain unknown. Explicit authority is required for a fresh attempt.",
                    Retryable = false, RetryRecommendation = ProviderRetryRecommendations.DoNotRetry,
                    OperatorReviewRecommended = true, StructuredOutputAccepted = false,
                    ParsedOutputDisposition = ProviderParsedOutputDispositions.None,
                    DecisionAuthority = ProviderRejectionDecisionAuthorities.ProviderResponseStatus,
                    ProcessOutcome = ProviderProcessOutcomes.Unknown
                };
                held = await PersistCompletionAsync(context.Run, request, held, _traces, ct);
                details["providerRecovery"] = "reconciliation_required";
                details["resultStatus"] = held.Status.ToString();
                details["providerStatus"] = held.ProviderStatus;
                return new ExecutionRunResult { Status = ExecutionRunStatuses.Failed,
                    Result = JsonSerializer.SerializeToNode(held, ExecutionJson.Options),
                    FailureClass = held.FailureClass, Error = held.Error, StatusDetails = details };
            }

            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(request)
            }, ct);

            var target = _registry.GetTarget(payload.Provider);
            ProviderProgressSnapshot? progress = null;
            ProviderInvocationJournal? invocation = null;
            async ValueTask RecordIntentAsync(DateTimeOffset deadlineUtc, CancellationToken token)
            {
                var intent = new ProviderInvocationJournal(ProviderInvocationJournal.Version,
                    context.Run.Id, context.Run.Attempt, context.Run.PayloadHash, payload.Provider,
                    target!.Profile.ConfigHash, DateTimeOffset.UtcNow, deadlineUtc, "intent");
                var details = BuildStatusDetails(request);
                details["providerInvocation"] = JsonSerializer.SerializeToNode(intent, ExecutionJson.Options);
                await context.ReportAsync(new ExecutionRunUpdate { StatusDetails = details }, token);
                invocation = intent;
            }
            async ValueTask ObserveAsync(ProviderProgressObservation item, CancellationToken token)
            {
                var configHash = target!.Profile.ConfigHash;
                if (!ProviderProgressSnapshot.Valid(item, DateTimeOffset.UtcNow)
                    || string.IsNullOrWhiteSpace(configHash)
                    || item.Sequence != (progress?.Observation.Sequence ?? 0) + 1
                    || (progress is not null && (item.StartedAtUtc != progress.Observation.StartedAtUtc
                        || item.DeadlineUtc != progress.Observation.DeadlineUtc
                        || configHash != progress.ProviderConfigHash)))
                    throw new InvalidOperationException("Invalid provider progress observation.");
                var next = new ProviderProgressSnapshot(context.Run.Id, context.Run.Attempt,
                    context.Run.PayloadHash, configHash, item);
                var details = BuildStatusDetails(request);
                if (invocation is not null)
                    details["providerInvocation"] = JsonSerializer.SerializeToNode(invocation, ExecutionJson.Options);
                details["providerProgress"] = JsonSerializer.SerializeToNode(next, ExecutionJson.Options);
                await context.ReportAsync(new ExecutionRunUpdate { StatusDetails = details }, token);
                await context.RecordEventAsync("provider.progress", "Observed provider pipeline boundary.", details: new JsonObject
                {
                    ["phase"] = ProviderProgressSnapshot.PhaseName(item.Phase), ["sequence"] = item.Sequence,
                    ["observedAt"] = item.ObservedAtUtc, ["startedAt"] = item.StartedAtUtc,
                    ["deadlineUtc"] = item.DeadlineUtc, ["runtimeContextTokens"] = item.RuntimeContextTokens,
                    ["promptTokens"] = item.PromptTokens, ["reservedOutputTokens"] = item.ReservedOutputTokens,
                    ["promptFitsContext"] = item.PromptFitsContext
                }, ct: token);
                progress = next;
            }
            ProviderRunResult result;
            if (target is null)
            {
                var observedAt = DateTimeOffset.UtcNow;
                result = CreateTerminalResult(
                    context.Run,
                    request,
                    ProviderRunStatus.NotConfigured,
                    ProviderFailureClasses.Configuration,
                    "provider_not_found",
                    $"Provider '{payload.Provider}' is not registered.");
                await ProviderMetering.AttachAsync(result, AiMeteringReceiptFactory.CreateProviderRunSummary(
                    request,
                    result,
                    context.Run.Id,
                    context.Run.Id,
                    observedAt,
                    observedAt,
                    elapsedDurationMs: 0,
                    queueDurationMs: 0,
                    activeDurationMs: 0,
                    providerInvoked: false), _meteringSigner, ct);
            }
            else
            {
                result = await RunProviderWithGuardAsync(target, request, context.Run, _guard, _meteringSigner,
                    RecordIntentAsync, ObserveAsync, ct);
            }

            result = await PersistCompletionAsync(context.Run, request, result, _traces, ct);
            var statusDetails = BuildStatusDetails(request, result);
            var resultNode = JsonSerializer.SerializeToNode(result, ExecutionJson.Options);
            if (invocation is not null)
                statusDetails["providerInvocation"] = JsonSerializer.SerializeToNode(invocation with
                { State = "completed", ResultHash = ProviderHash.Sha256(resultNode!.ToJsonString(ExecutionJson.Options)),
                    EffectsUnresolved = result.ProviderStatus is "native_effect_unresolved" or "provider_effect_unresolved"
                        or "server_timeout" or "cancelled" or "job_unhandled_exception" or "job_trace_persist_failed" }, ExecutionJson.Options);
            if (progress is not null)
                statusDetails["providerProgress"] = JsonSerializer.SerializeToNode(progress, ExecutionJson.Options);
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Attempted = 1,
                Succeeded = result.Status == ProviderRunStatus.Succeeded ? 1 : 0,
                Failed = result.Status == ProviderRunStatus.Succeeded ? 0 : 1,
                Progress = 1,
                FailureClass = result.FailureClass,
                Error = result.Error,
                Result = resultNode,
                StatusDetails = statusDetails
            }, ct);

            return new ExecutionRunResult
            {
                Status = ToRunStatus(result.Status),
                Result = resultNode,
                FailureClass = result.FailureClass,
                Error = result.Error,
                StatusDetails = statusDetails
            };
        }

        private static async Task<ProviderRunResult> RunProviderWithGuardAsync(
            IProviderTarget target,
            ProviderRunRequest request,
            ExecutionRun run,
            ProviderRunGuard guard,
            IAiMeteringReceiptSigner? meteringSigner,
            Func<DateTimeOffset, CancellationToken, ValueTask> recordIntent,
            Func<ProviderProgressObservation, CancellationToken, ValueTask> observe,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.CorrelationId))
            {
                request.CorrelationId = run.CorrelationId;
            }
            var observedStartedAt = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.StartNew();
            var queue = Stopwatch.StartNew();
            await using var admission = await guard.TryEnterAsync(target.Profile.Id, request, ct);
            queue.Stop();
            ProviderRunResult result;
            var providerInvoked = false;
            var activeDurationMs = 0L;
            if (!admission.Accepted)
            {
                result = admission.RejectionResult!;
            }
            else
            {
                var active = Stopwatch.StartNew();
                try
                {
                    await recordIntent(admission.DeadlineUtc!.Value, admission.CancellationToken);
                    admission.CancellationToken.ThrowIfCancellationRequested();
                    if (DateTimeOffset.UtcNow >= admission.DeadlineUtc.Value)
                        throw new OperationCanceledException(admission.CancellationToken);
                    providerInvoked = true;
                    ValueTask ObserveBoundAsync(ProviderProgressObservation item, CancellationToken token)
                    {
                        if (admission.DeadlineUtc is { } bound && item.DeadlineUtc > bound)
                            throw new InvalidOperationException("Provider progress widened the execution deadline.");
                        return observe(item, token);
                    }
                    result = target is IProviderProgressTarget observedTarget
                        ? await observedTarget.RunWithProgressAsync(request, ObserveBoundAsync, admission.DeadlineUtc, admission.CancellationToken)
                        : await target.RunAsync(request, admission.CancellationToken);
                    // A cooperative provider may return cancellation instead of throwing.
                    // The guard knows whether its own timer, rather than the caller, expired.
                    if (result.Status == ProviderRunStatus.Cancelled && admission.TimedOut && !ct.IsCancellationRequested)
                    {
                        result.Status = ProviderRunStatus.TimedOut;
                        result.FailureClass = ProviderFailureClasses.Timeout;
                        if (result.Trace is not null) result.Trace.FailureClass = ProviderFailureClasses.Timeout;
                    }
                }
                catch (OperationCanceledException) when (admission.TimedOut && !ct.IsCancellationRequested)
                {
                    result = guard.CreateTimeoutResult(target.Profile.Id, request);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    result = CreateTerminalResult(run, request, ProviderRunStatus.Cancelled, ProviderFailureClasses.Cancelled, "cancelled");
                }
                catch (Exception)
                {
                    result = CreateTerminalResult(run, request, ProviderRunStatus.Failed, ProviderFailureClasses.Unknown, "job_unhandled_exception", "Provider execution failed unexpectedly.");
                }
                finally
                {
                    active.Stop();
                    activeDurationMs = active.ElapsedMilliseconds;
                }
            }

            elapsed.Stop();
            var observedCompletedAt = DateTimeOffset.UtcNow;
            await ProviderMetering.AttachAsync(result, AiMeteringReceiptFactory.CreateProviderRunSummary(
                request,
                result,
                run.Id,
                run.Id,
                observedStartedAt,
                observedCompletedAt,
                elapsed.ElapsedMilliseconds,
                queue.ElapsedMilliseconds,
                activeDurationMs,
                providerInvoked), meteringSigner, ct);
            return result;
        }

        private static async Task<ProviderRunResult> PersistCompletionAsync(
            ExecutionRun run,
            ProviderRunRequest request,
            ProviderRunResult result,
            ITraceStore traces,
            CancellationToken ct)
        {
            try
            {
                await PersistProviderTraceAsync(run, request, result, traces, ct);
                return result;
            }
            catch (Exception)
            {
                var failure = CreateTerminalResult(run, request, ProviderRunStatus.Failed, ProviderFailureClasses.Unknown, "job_trace_persist_failed", "Provider trace persistence failed.");
                failure.Metering = result.Metering;
                failure.Trace!.MeteringReceiptHashes = result.Metering
                    .Select(AiMeteringCryptography.ComputeReceiptEnvelopeHash)
                    .ToList();
                return failure;
            }
        }

        private static async Task PersistProviderTraceAsync(
            ExecutionRun run,
            ProviderRunRequest request,
            ProviderRunResult result,
            ITraceStore traces,
            CancellationToken ct)
        {
            var traceEvent = result.Trace ??= new ProviderTraceEvent
            {
                Provider = result.Provider,
                Capability = result.Capability,
                Operation = result.Operation,
                Mode = result.Mode,
                ModelId = request.ModelId,
                InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)),
                FailureClass = result.FailureClass
            };

            if (string.IsNullOrWhiteSpace(traceEvent.TraceId))
            {
                traceEvent.TraceId = Guid.NewGuid().ToString("N");
            }

            traceEvent.MeteringReceiptHashes = result.Metering
                .Select(AiMeteringCryptography.ComputeReceiptEnvelopeHash)
                .ToList();

            var trace = new TraceRecord
            {
                Id = traceEvent.TraceId,
                Operation = "provider.run",
                Adapter = result.Provider,
                StartedAt = traceEvent.Timestamp == default ? DateTime.UtcNow : traceEvent.Timestamp,
                DurationMs = traceEvent.DurationMs,
                Request = new Dictionary<string, object?>
                {
                    ["provider"] = result.Provider,
                    ["capability"] = request.Capability,
                    ["operation"] = request.Operation,
                    ["mode"] = request.Mode,
                    ["modelId"] = request.ModelId,
                    ["correlationId"] = request.CorrelationId,
                    ["jobId"] = run.Id,
                    ["contextRefs"] = request.ContextRefs,
                    ["timeoutSeconds"] = request.TimeoutSeconds,
                    ["maxOutputBytes"] = request.MaxOutputBytes,
                    ["payloadHash"] = traceEvent.InputHash ?? ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options))
                },
                ResultSummary = new Dictionary<string, object?>
                {
                    ["status"] = result.Status.ToString(),
                    ["failureClass"] = result.FailureClass,
                    ["providerStatus"] = result.ProviderStatus,
                    ["inputHash"] = traceEvent.InputHash,
                    ["outputHash"] = traceEvent.OutputHash,
                    ["modelId"] = traceEvent.ModelId,
                    ["adapterId"] = traceEvent.AdapterId,
                    ["configHash"] = traceEvent.ConfigHash,
                    ["authorityBoundary"] = traceEvent.AuthorityBoundary,
                    ["artifactRefs"] = traceEvent.ArtifactRefs,
                    ["meteringReceiptHashes"] = traceEvent.MeteringReceiptHashes,
                    ["metering"] = result.Metering
                }
            };

            await traces.WriteTraceAsync(trace, ct);
        }

        private static ProviderRunResult CreateTerminalResult(
            ExecutionRun run,
            ProviderRunRequest request,
            ProviderRunStatus status,
            string failureClass,
            string providerStatus,
            string? textOutput = null)
        {
            var now = DateTime.UtcNow;
            var trace = new ProviderTraceEvent
            {
                TraceId = Guid.NewGuid().ToString("N"),
                Timestamp = now,
                Provider = request.Provider ?? string.Empty,
                Capability = request.Capability,
                Operation = request.Operation,
                Mode = request.Mode,
                AdapterId = "server-provider-job",
                ModelId = request.ModelId,
                InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)),
                FailureClass = failureClass,
                DurationMs = run.StartedAtUtc.HasValue ? (now - run.StartedAtUtc.Value).TotalMilliseconds : 0
            };

            var output = new JsonObject();
            if (textOutput is not null)
            {
                output["text"] = textOutput;
            }

            return new ProviderRunResult
            {
                Status = status,
                Provider = request.Provider ?? string.Empty,
                Capability = request.Capability,
                Operation = request.Operation,
                Mode = request.Mode,
                FailureClass = failureClass,
                ProviderStatus = providerStatus,
                Error = textOutput,
                Trace = trace,
                Output = output
            };
        }
    }

    private sealed class ProviderRunJobPayload
    {
        public string Provider { get; set; } = string.Empty;
        public ProviderRunRequest? Request { get; set; }
    }

    private sealed class ProviderRunExecutionPlugin : IExecutionPlugin
    {
        public ProviderRunExecutionPlugin(
            ProviderTargetRegistry registry,
            ITraceStore traces,
            ProviderRunGuard guard,
            IAiMeteringReceiptSigner? meteringSigner)
        {
            Handlers = new[] { new ProviderRunExecutionHandler(registry, traces, guard, meteringSigner) };
        }

        public ExecutionPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = PluginId,
            Name = "Vyral providers",
            Version = "1.0.0",
            Handlers =
            {
                new ExecutionHandlerDescriptor
                {
                    HandlerId = HandlerId,
                    PluginId = PluginId,
                    DisplayName = "Vyral provider run",
                    Description = "Runs a provider target request.",
                    MaxAttempts = 1,
                    Tags =
                    {
                        ["vyral.job"] = "provider-run"
                    }
                }
            }
        };

        public IReadOnlyList<IExecutionHandler> Handlers { get; }
    }
}
