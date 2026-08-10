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

    public ExecutionRuntimeProviderRunJobAdapter(
        IExecutionRuntime runtime,
        ProviderTargetRegistry registry,
        ITraceStore traces,
        ProviderRunGuard guard,
        ProviderRunJobStoreOptions? options = null)
    {
        _runtime = runtime;
        Options = options ?? new ProviderRunJobStoreOptions();
        PersistenceKind = DescribePersistence(runtime);
        _runtime.RegisterPlugin(new ProviderRunExecutionPlugin(registry, traces, guard));
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
            Status = ToJobStatus(run.Status, result?.Status),
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
            Result = result
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

        if (providerStatus.HasValue)
        {
            return providerStatus.Value switch
            {
                ProviderRunStatus.Succeeded => ProviderJobStatus.Succeeded,
                ProviderRunStatus.Failed => ProviderJobStatus.Failed,
                ProviderRunStatus.TimedOut => ProviderJobStatus.TimedOut,
                ProviderRunStatus.Rejected => ProviderJobStatus.Rejected,
                ProviderRunStatus.Unsupported => ProviderJobStatus.Unsupported,
                ProviderRunStatus.NotConfigured => ProviderJobStatus.NotConfigured,
                ProviderRunStatus.Cancelled => ProviderJobStatus.Cancelled,
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
            ["traceId"] = result?.Trace?.TraceId
        };

        return details;
    }

    private sealed class ProviderRunExecutionHandler : IExecutionHandler
    {
        private readonly ProviderTargetRegistry _registry;
        private readonly ITraceStore _traces;
        private readonly ProviderRunGuard _guard;

        public ProviderRunExecutionHandler(
            ProviderTargetRegistry registry,
            ITraceStore traces,
            ProviderRunGuard guard)
        {
            _registry = registry;
            _traces = traces;
            _guard = guard;
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

            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(request)
            }, ct);

            var target = _registry.GetTarget(payload.Provider);
            ProviderRunResult result;
            if (target is null)
            {
                result = CreateTerminalResult(
                    context.Run,
                    request,
                    ProviderRunStatus.NotConfigured,
                    ProviderFailureClasses.Configuration,
                    "provider_not_found",
                    $"Provider '{payload.Provider}' is not registered.");
            }
            else
            {
                result = await RunProviderWithGuardAsync(target, request, context.Run, _guard, ct);
            }

            result = await PersistCompletionAsync(context.Run, request, result, _traces, ct);
            var statusDetails = BuildStatusDetails(request, result);
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Attempted = 1,
                Succeeded = result.Status == ProviderRunStatus.Succeeded ? 1 : 0,
                Failed = result.Status == ProviderRunStatus.Succeeded ? 0 : 1,
                Progress = 1,
                FailureClass = result.FailureClass,
                Error = result.Error,
                Result = JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                StatusDetails = statusDetails
            }, ct);

            return new ExecutionRunResult
            {
                Status = ToRunStatus(result.Status),
                Result = JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
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
            CancellationToken ct)
        {
            await using var admission = await guard.TryEnterAsync(target.Profile.Id, request, ct);
            if (!admission.Accepted)
            {
                return admission.RejectionResult!;
            }

            try
            {
                return await target.RunAsync(request, admission.CancellationToken);
            }
            catch (OperationCanceledException) when (admission.TimedOut && !ct.IsCancellationRequested)
            {
                return guard.CreateTimeoutResult(target.Profile.Id, request);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return CreateTerminalResult(run, request, ProviderRunStatus.Cancelled, ProviderFailureClasses.Cancelled, "cancelled");
            }
            catch (Exception ex)
            {
                return CreateTerminalResult(run, request, ProviderRunStatus.Failed, ProviderFailureClasses.Unknown, "job_unhandled_exception", ex.Message);
            }
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
            catch (Exception ex)
            {
                return CreateTerminalResult(run, request, ProviderRunStatus.Failed, ProviderFailureClasses.Unknown, "job_trace_persist_failed", ex.Message);
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
                    ["artifactRefs"] = traceEvent.ArtifactRefs
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
            ProviderRunGuard guard)
        {
            Handlers = new[] { new ProviderRunExecutionHandler(registry, traces, guard) };
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
