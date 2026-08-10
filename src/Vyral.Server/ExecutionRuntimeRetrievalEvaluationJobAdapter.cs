using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Execution;

namespace Vyral.Server;

public sealed class ExecutionRuntimeRetrievalEvaluationJobAdapter
{
    public const string PluginId = "vyral.retrieval.evaluation";
    public const string EvaluationHandlerId = "vyral.retrieval.evaluation.run";
    public const string ComparisonHandlerId = "vyral.retrieval.evaluation.compare";

    private readonly IExecutionRuntime _runtime;
    private readonly RetrievalEvaluationJobStoreOptions _options;

    public ExecutionRuntimeRetrievalEvaluationJobAdapter(
        IExecutionRuntime runtime,
        IRetrievalEvaluationService evaluationService,
        RetrievalEvaluationJobStoreOptions? options = null)
    {
        _runtime = runtime;
        _options = options ?? new RetrievalEvaluationJobStoreOptions();
        _runtime.RegisterPlugin(new RetrievalEvaluationExecutionPlugin(evaluationService));
    }

    public async Task<RetrievalEvaluationJob> StartEvaluationAsync(
        RetrievalEvaluationRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var run = await _runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = EvaluationHandlerId,
            PluginId = PluginId,
            Payload = JsonSerializer.SerializeToNode(request, ExecutionJson.Options),
            IdempotencyKey = idempotencyKey,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 1 },
            Tags =
            {
                ["vyral.job"] = "retrieval-evaluation",
                ["vyral.retrieval.kind"] = RetrievalEvaluationJobKinds.Evaluation
            }
        }, ct);

        return MapRun(run);
    }

    public async Task<RetrievalEvaluationJob> StartComparisonAsync(
        RetrievalEvaluationComparisonRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var run = await _runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = ComparisonHandlerId,
            PluginId = PluginId,
            Payload = JsonSerializer.SerializeToNode(request, ExecutionJson.Options),
            IdempotencyKey = idempotencyKey,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 1 },
            Tags =
            {
                ["vyral.job"] = "retrieval-evaluation",
                ["vyral.retrieval.kind"] = RetrievalEvaluationJobKinds.Comparison
            }
        }, ct);

        return MapRun(run);
    }

    public async Task<RetrievalEvaluationJob?> GetAsync(string id, bool includeResult = true, CancellationToken ct = default)
    {
        var run = await _runtime.GetRunAsync(id, includeResult, ct);
        return IsRetrievalEvaluationRun(run) ? MapRun(run!) : null;
    }

    public async Task<IReadOnlyList<RetrievalEvaluationJob>> ListAsync(int? limit = null, bool includeResult = false, CancellationToken ct = default)
    {
        var effectiveLimit = ValidateListLimit(limit);
        var evaluationRuns = await _runtime.ListRunsAsync(new ExecutionRunQuery
        {
            HandlerId = EvaluationHandlerId,
            IncludeResult = includeResult,
            Limit = effectiveLimit
        }, ct);
        var comparisonRuns = await _runtime.ListRunsAsync(new ExecutionRunQuery
        {
            HandlerId = ComparisonHandlerId,
            IncludeResult = includeResult,
            Limit = effectiveLimit
        }, ct);

        return evaluationRuns
            .Concat(comparisonRuns)
            .OrderByDescending(run => run.CreatedAtUtc)
            .ThenBy(run => run.Id, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .Select(MapRun)
            .ToList();
    }

    public async Task<RetrievalEvaluationJob?> CancelAsync(string id, CancellationToken ct = default)
    {
        var existing = await _runtime.GetRunAsync(id, includeResult: false, ct);
        if (!IsRetrievalEvaluationRun(existing))
        {
            return null;
        }

        var run = await _runtime.CancelRunAsync(id, ct);
        return run is null ? null : MapRun(run);
    }

    private RetrievalEvaluationJob MapRun(ExecutionRun run)
    {
        var kind = GetKind(run);
        var evaluationRequest = kind == RetrievalEvaluationJobKinds.Evaluation
            ? run.Payload?.Deserialize<RetrievalEvaluationRequest>(ExecutionJson.Options)
            : null;
        var comparisonRequest = kind == RetrievalEvaluationJobKinds.Comparison
            ? run.Payload?.Deserialize<RetrievalEvaluationComparisonRequest>(ExecutionJson.Options)
            : null;
        var evaluationResult = kind == RetrievalEvaluationJobKinds.Evaluation
            ? run.Result?.Deserialize<RetrievalEvaluationResult>(ExecutionJson.Options)
            : null;
        var comparisonResult = kind == RetrievalEvaluationJobKinds.Comparison
            ? run.Result?.Deserialize<RetrievalEvaluationComparisonResult>(ExecutionJson.Options)
            : null;

        return new RetrievalEvaluationJob
        {
            Admission = ExecutionAdmission.Create(
                run,
                kind == RetrievalEvaluationJobKinds.Comparison
                    ? VyralAdmissionOperations.StartRetrievalEvaluationComparisonJob
                    : VyralAdmissionOperations.StartRetrievalEvaluationJob,
                $"/retrieval/evaluate/jobs/{run.Id}"),
            Id = run.Id,
            Kind = kind,
            Status = ToJobStatus(run.Status),
            RequestHash = run.PayloadHash,
            CreatedAt = run.CreatedAtUtc,
            StartedAt = run.StartedAtUtc,
            CompletedAt = run.CompletedAtUtc,
            DurationMs = run.DurationMs,
            CancellationRequested = run.CancellationRequested,
            Requested = evaluationResult?.Requested ??
                comparisonResult?.Requested ??
                run.Requested ??
                GetInt(run.StatusDetails, "requested") ??
                evaluationRequest?.Cases.Count ??
                comparisonRequest?.Cases.Count ??
                0,
            CasesAttempted = kind == RetrievalEvaluationJobKinds.Evaluation
                ? evaluationResult?.Attempted ?? run.Attempted ?? GetInt(run.StatusDetails, "casesAttempted") ?? 0
                : 0,
            CasesSucceeded = kind == RetrievalEvaluationJobKinds.Evaluation
                ? evaluationResult?.Succeeded ?? run.Succeeded ?? GetInt(run.StatusDetails, "casesSucceeded") ?? 0
                : 0,
            CasesFailed = kind == RetrievalEvaluationJobKinds.Evaluation
                ? evaluationResult?.Failed ?? run.Failed ?? GetInt(run.StatusDetails, "casesFailed") ?? 0
                : 0,
            CurrentCaseIndex = GetInt(run.StatusDetails, "currentCaseIndex"),
            CurrentCaseName = GetString(run.StatusDetails, "currentCaseName"),
            VariantsRequested = comparisonResult?.VariantsRequested ??
                GetInt(run.StatusDetails, "variantsRequested") ??
                comparisonRequest?.Variants.Count ??
                0,
            VariantsAttempted = comparisonResult?.VariantsAttempted ?? GetInt(run.StatusDetails, "variantsAttempted") ?? 0,
            VariantsSucceeded = comparisonResult?.VariantsSucceeded ?? GetInt(run.StatusDetails, "variantsSucceeded") ?? 0,
            VariantsFailed = comparisonResult?.VariantsFailed ?? GetInt(run.StatusDetails, "variantsFailed") ?? 0,
            CurrentVariantId = GetString(run.StatusDetails, "currentVariantId"),
            CurrentVariantIndex = GetInt(run.StatusDetails, "currentVariantIndex"),
            Progress = run.Progress ?? (run.Status == ExecutionRunStatuses.Succeeded ? 1 : 0),
            FailureClass = run.FailureClass,
            Error = GetJobError(run, kind),
            EvaluationResult = evaluationResult,
            Result = comparisonResult
        };
    }

    private int ValidateListLimit(int? limit)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new InvalidOperationException("Retrieval evaluation job list limit must be greater than zero.");
        }

        var effectiveLimit = limit ?? _options.DefaultListLimit;
        if (effectiveLimit > _options.MaxListLimit)
        {
            throw new InvalidOperationException($"Retrieval evaluation job list limit cannot exceed {_options.MaxListLimit}.");
        }

        return effectiveLimit;
    }

    private static bool IsRetrievalEvaluationRun(ExecutionRun? run)
    {
        return run is not null &&
            (string.Equals(run.HandlerId, EvaluationHandlerId, StringComparison.Ordinal) ||
             string.Equals(run.HandlerId, ComparisonHandlerId, StringComparison.Ordinal));
    }

    private static string GetKind(ExecutionRun run)
    {
        if (run.Tags.TryGetValue("vyral.retrieval.kind", out var kind) && !string.IsNullOrWhiteSpace(kind))
        {
            return kind;
        }

        var detailKind = GetString(run.StatusDetails, "kind");
        if (!string.IsNullOrWhiteSpace(detailKind))
        {
            return detailKind;
        }

        return string.Equals(run.HandlerId, EvaluationHandlerId, StringComparison.Ordinal)
            ? RetrievalEvaluationJobKinds.Evaluation
            : RetrievalEvaluationJobKinds.Comparison;
    }

    private static string ToJobStatus(string status)
    {
        return status switch
        {
            ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting => RetrievalEvaluationJobStatuses.Queued,
            ExecutionRunStatuses.Running => RetrievalEvaluationJobStatuses.Running,
            ExecutionRunStatuses.Succeeded => RetrievalEvaluationJobStatuses.Succeeded,
            ExecutionRunStatuses.Cancelled => RetrievalEvaluationJobStatuses.Cancelled,
            ExecutionRunStatuses.Rejected => RetrievalEvaluationJobStatuses.Rejected,
            _ => RetrievalEvaluationJobStatuses.Failed
        };
    }

    private static string? GetJobError(ExecutionRun run, string kind)
    {
        if (run.Status != ExecutionRunStatuses.Cancelled)
        {
            return run.Error;
        }

        return kind == RetrievalEvaluationJobKinds.Evaluation
            ? "Retrieval evaluation job was cancelled."
            : "Retrieval evaluation comparison job was cancelled.";
    }

    private static string? GetString(JsonObject? details, string key)
    {
        return details is not null && details.TryGetPropertyValue(key, out var node)
            ? node?.GetValue<string>()
            : null;
    }

    private static int? GetInt(JsonObject? details, string key)
    {
        return details is not null &&
               details.TryGetPropertyValue(key, out var node) &&
               node is JsonValue valueNode &&
               valueNode.TryGetValue<int>(out var value)
            ? value
            : null;
    }

    private static JsonObject BuildStatusDetails(
        string kind,
        int requested,
        int casesAttempted,
        int casesSucceeded,
        int casesFailed,
        int? currentCaseIndex = null,
        string? currentCaseName = null,
        int? variantsRequested = null,
        int? variantsAttempted = null,
        int? variantsSucceeded = null,
        int? variantsFailed = null,
        string? currentVariantId = null,
        int? currentVariantIndex = null)
    {
        var details = new JsonObject
        {
            ["kind"] = kind,
            ["requested"] = requested,
            ["casesAttempted"] = casesAttempted,
            ["casesSucceeded"] = casesSucceeded,
            ["casesFailed"] = casesFailed
        };
        if (currentCaseIndex.HasValue)
        {
            details["currentCaseIndex"] = currentCaseIndex.Value;
        }

        if (!string.IsNullOrWhiteSpace(currentCaseName))
        {
            details["currentCaseName"] = currentCaseName;
        }

        if (variantsRequested.HasValue)
        {
            details["variantsRequested"] = variantsRequested.Value;
            details["variantsAttempted"] = variantsAttempted ?? 0;
            details["variantsSucceeded"] = variantsSucceeded ?? 0;
            details["variantsFailed"] = variantsFailed ?? 0;
        }

        if (!string.IsNullOrWhiteSpace(currentVariantId))
        {
            details["currentVariantId"] = currentVariantId;
        }

        if (currentVariantIndex.HasValue)
        {
            details["currentVariantIndex"] = currentVariantIndex.Value;
        }

        return details;
    }

    private static double CalculateProgress(int attempted, int requested)
    {
        return requested <= 0 ? 0 : Math.Clamp(attempted / (double)requested, 0, 1);
    }

    private sealed class EvaluationExecutionHandler : IExecutionHandler
    {
        private readonly IRetrievalEvaluationService _evaluationService;

        public EvaluationExecutionHandler(IRetrievalEvaluationService evaluationService)
        {
            _evaluationService = evaluationService;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = EvaluationHandlerId,
            PluginId = PluginId,
            DisplayName = "Vyral retrieval evaluation",
            Description = "Runs retrieval evaluation cases.",
            MaxAttempts = 1,
            Tags =
            {
                ["vyral.job"] = "retrieval-evaluation",
                ["vyral.retrieval.kind"] = RetrievalEvaluationJobKinds.Evaluation
            }
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var request = context.Run.Payload?.Deserialize<RetrievalEvaluationRequest>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("Retrieval evaluation payload is required.");
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = request.Cases.Count,
                Attempted = 0,
                Succeeded = 0,
                Failed = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(RetrievalEvaluationJobKinds.Evaluation, request.Cases.Count, 0, 0, 0)
            }, ct);

            var progress = new InlineProgress<RetrievalEvaluationProgress>(update =>
            {
                ReportEvaluationProgress(context, update, ct).GetAwaiter().GetResult();
            });
            var result = await _evaluationService.EvaluateAsync(request, ct, progress);
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = result.Requested,
                Attempted = result.Attempted,
                Succeeded = result.Succeeded,
                Failed = result.Failed,
                Progress = 1,
                Result = JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                StatusDetails = BuildStatusDetails(
                    RetrievalEvaluationJobKinds.Evaluation,
                    result.Requested,
                    result.Attempted,
                    result.Succeeded,
                    result.Failed)
            }, ct);

            return ExecutionRunResult.Succeeded(
                JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                BuildStatusDetails(
                    RetrievalEvaluationJobKinds.Evaluation,
                    result.Requested,
                    result.Attempted,
                    result.Succeeded,
                    result.Failed));
        }

        private static Task ReportEvaluationProgress(IExecutionRunContext context, RetrievalEvaluationProgress progress, CancellationToken ct)
        {
            return context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = progress.Requested,
                Attempted = progress.CasesAttempted,
                Succeeded = progress.CasesSucceeded,
                Failed = progress.CasesFailed,
                Progress = CalculateProgress(progress.CasesAttempted, progress.Requested),
                CurrentStep = progress.CurrentCaseName,
                Result = progress.Result is null ? null : JsonSerializer.SerializeToNode(progress.Result, ExecutionJson.Options),
                StatusDetails = BuildStatusDetails(
                    RetrievalEvaluationJobKinds.Evaluation,
                    progress.Requested,
                    progress.CasesAttempted,
                    progress.CasesSucceeded,
                    progress.CasesFailed,
                    progress.CurrentCaseIndex,
                    progress.CurrentCaseName)
            }, ct);
        }
    }

    private sealed class ComparisonExecutionHandler : IExecutionHandler
    {
        private readonly IRetrievalEvaluationService _evaluationService;

        public ComparisonExecutionHandler(IRetrievalEvaluationService evaluationService)
        {
            _evaluationService = evaluationService;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = ComparisonHandlerId,
            PluginId = PluginId,
            DisplayName = "Vyral retrieval evaluation comparison",
            Description = "Runs retrieval evaluation variants.",
            MaxAttempts = 1,
            Tags =
            {
                ["vyral.job"] = "retrieval-evaluation",
                ["vyral.retrieval.kind"] = RetrievalEvaluationJobKinds.Comparison
            }
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var request = context.Run.Payload?.Deserialize<RetrievalEvaluationComparisonRequest>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("Retrieval evaluation comparison payload is required.");
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = request.Cases.Count,
                Attempted = 0,
                Succeeded = 0,
                Failed = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(
                    RetrievalEvaluationJobKinds.Comparison,
                    request.Cases.Count,
                    0,
                    0,
                    0,
                    variantsRequested: request.Variants.Count,
                    variantsAttempted: 0,
                    variantsSucceeded: 0,
                    variantsFailed: 0)
            }, ct);

            var progress = new InlineProgress<RetrievalEvaluationComparisonProgress>(update =>
            {
                ReportComparisonProgress(context, update, ct).GetAwaiter().GetResult();
            });
            var result = await _evaluationService.CompareAsync(request, ct, progress);
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = result.Requested,
                Attempted = result.VariantsAttempted,
                Succeeded = result.VariantsSucceeded,
                Failed = result.VariantsFailed,
                Progress = 1,
                Result = JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                StatusDetails = BuildStatusDetails(
                    RetrievalEvaluationJobKinds.Comparison,
                    result.Requested,
                    0,
                    0,
                    0,
                    variantsRequested: result.VariantsRequested,
                    variantsAttempted: result.VariantsAttempted,
                    variantsSucceeded: result.VariantsSucceeded,
                    variantsFailed: result.VariantsFailed)
            }, ct);

            return ExecutionRunResult.Succeeded(
                JsonSerializer.SerializeToNode(result, ExecutionJson.Options),
                BuildStatusDetails(
                    RetrievalEvaluationJobKinds.Comparison,
                    result.Requested,
                    0,
                    0,
                    0,
                    variantsRequested: result.VariantsRequested,
                    variantsAttempted: result.VariantsAttempted,
                    variantsSucceeded: result.VariantsSucceeded,
                    variantsFailed: result.VariantsFailed));
        }

        private static Task ReportComparisonProgress(IExecutionRunContext context, RetrievalEvaluationComparisonProgress progress, CancellationToken ct)
        {
            return context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = progress.Requested,
                Attempted = progress.VariantsAttempted,
                Succeeded = progress.VariantsSucceeded,
                Failed = progress.VariantsFailed,
                Progress = CalculateProgress(progress.VariantsAttempted, progress.VariantsRequested),
                CurrentStep = progress.CurrentVariantId,
                Result = progress.Result is null ? null : JsonSerializer.SerializeToNode(progress.Result, ExecutionJson.Options),
                StatusDetails = BuildStatusDetails(
                    RetrievalEvaluationJobKinds.Comparison,
                    progress.Requested,
                    0,
                    0,
                    0,
                    variantsRequested: progress.VariantsRequested,
                    variantsAttempted: progress.VariantsAttempted,
                    variantsSucceeded: progress.VariantsSucceeded,
                    variantsFailed: progress.VariantsFailed,
                    currentVariantId: progress.CurrentVariantId,
                    currentVariantIndex: progress.CurrentVariantIndex)
            }, ct);
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }

    private sealed class RetrievalEvaluationExecutionPlugin : IExecutionPlugin
    {
        public RetrievalEvaluationExecutionPlugin(IRetrievalEvaluationService evaluationService)
        {
            Handlers = new IExecutionHandler[]
            {
                new EvaluationExecutionHandler(evaluationService),
                new ComparisonExecutionHandler(evaluationService)
            };
        }

        public ExecutionPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = PluginId,
            Name = "Vyral retrieval evaluation",
            Version = "1.0.0",
            Handlers =
            {
                new ExecutionHandlerDescriptor
                {
                    HandlerId = EvaluationHandlerId,
                    PluginId = PluginId,
                    DisplayName = "Vyral retrieval evaluation",
                    Description = "Runs a retrieval evaluation job.",
                    MaxAttempts = 1,
                    Tags =
                    {
                        ["vyral.job"] = "retrieval-evaluation",
                        ["vyral.retrieval.kind"] = RetrievalEvaluationJobKinds.Evaluation
                    }
                },
                new ExecutionHandlerDescriptor
                {
                    HandlerId = ComparisonHandlerId,
                    PluginId = PluginId,
                    DisplayName = "Vyral retrieval evaluation comparison",
                    Description = "Runs a retrieval evaluation comparison job.",
                    MaxAttempts = 1,
                    Tags =
                    {
                        ["vyral.job"] = "retrieval-evaluation",
                        ["vyral.retrieval.kind"] = RetrievalEvaluationJobKinds.Comparison
                    }
                }
            }
        };

        public IReadOnlyList<IExecutionHandler> Handlers { get; }
    }
}
