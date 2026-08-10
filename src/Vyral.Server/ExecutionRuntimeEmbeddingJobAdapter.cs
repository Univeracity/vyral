using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;

namespace Vyral.Server;

public sealed class ExecutionRuntimeEmbeddingJobAdapter
{
    public const string PluginId = "vyral.embeddings";
    public const string HandlerId = "vyral.embedding.batch";

    private readonly IExecutionRuntime _runtime;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly EmbeddingProviderOptions _embeddingOptions;

    public ExecutionRuntimeEmbeddingJobAdapter(
        IExecutionRuntime runtime,
        IEmbeddingProvider embeddingProvider,
        EmbeddingProviderOptions embeddingOptions)
    {
        _runtime = runtime;
        _embeddingProvider = embeddingProvider;
        _embeddingOptions = embeddingOptions;
        _runtime.RegisterPlugin(new EmbeddingExecutionPlugin(embeddingProvider, embeddingOptions));
    }

    public async Task<EmbeddingJob> StartAsync(EmbeddingRequest request, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var run = await _runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = HandlerId,
            PluginId = PluginId,
            Payload = JsonSerializer.SerializeToNode(request, ExecutionJson.Options),
            IdempotencyKey = idempotencyKey,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 1 },
            Tags =
            {
                ["vyral.job"] = "embedding"
            }
        }, ct);

        return MapRun(run);
    }

    public async Task<EmbeddingJob?> GetAsync(string id, bool includeResult = true, CancellationToken ct = default)
    {
        var run = await _runtime.GetRunAsync(id, includeResult, ct);
        return run is null ? null : MapRun(run);
    }

    public async Task<IReadOnlyList<EmbeddingJob>> ListAsync(int? limit = null, bool includeResult = false, CancellationToken ct = default)
    {
        var runs = await _runtime.ListRunsAsync(new ExecutionRunQuery
        {
            HandlerId = HandlerId,
            IncludeResult = includeResult,
            Limit = limit
        }, ct);
        return runs.Select(MapRun).ToList();
    }

    public async Task<EmbeddingJob?> CancelAsync(string id, CancellationToken ct = default)
    {
        var run = await _runtime.CancelRunAsync(id, ct);
        return run is null ? null : MapRun(run);
    }

    private EmbeddingJob MapRun(ExecutionRun run)
    {
        var result = run.Result?.Deserialize<EmbeddingResponse>(ExecutionJson.Options);
        var request = run.Payload?.Deserialize<EmbeddingRequest>(ExecutionJson.Options);
        var requested = CountRequestedTexts(request);
        var requestedPurpose = request is null ? null : EmbeddingTextPreparer.NormalizePurpose(request.Purpose);
        return new EmbeddingJob
        {
            Admission = ExecutionAdmission.Create(
                run,
                VyralAdmissionOperations.StartEmbeddingJob,
                $"/embeddings/jobs/{run.Id}"),
            Id = run.Id,
            Status = ToEmbeddingStatus(run.Status),
            Provider = result?.Provider ?? GetString(run.StatusDetails, "provider") ?? _embeddingProvider.ProviderId,
            ModelId = result?.ModelId ?? GetString(run.StatusDetails, "modelId") ?? _embeddingProvider.ModelId,
            Dimensions = result?.Dimensions ?? GetInt(run.StatusDetails, "dimensions") ?? _embeddingProvider.Dimensions,
            Purpose = result?.Purpose ?? GetString(run.StatusDetails, "purpose") ?? requestedPurpose ?? EmbeddingPurposes.Symmetric,
            RequestHash = run.PayloadHash,
            CreatedAt = run.CreatedAtUtc,
            StartedAt = run.StartedAtUtc,
            CompletedAt = run.CompletedAtUtc,
            DurationMs = run.DurationMs,
            CancellationRequested = run.CancellationRequested,
            Requested = run.Requested ?? requested ?? 0,
            Attempted = run.Attempted ?? result?.Items.Count ?? 0,
            Succeeded = run.Succeeded ?? result?.Items.Count ?? 0,
            Failed = run.Failed ?? 0,
            CurrentIndex = GetInt(run.StatusDetails, "currentIndex"),
            Progress = run.Progress ?? 0,
            FailureClass = run.FailureClass,
            Error = run.Error,
            Result = result
        };
    }

    private static string ToEmbeddingStatus(string status)
    {
        return status switch
        {
            ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting => EmbeddingJobStatuses.Queued,
            ExecutionRunStatuses.Running => EmbeddingJobStatuses.Running,
            ExecutionRunStatuses.Succeeded => EmbeddingJobStatuses.Succeeded,
            ExecutionRunStatuses.Cancelled => EmbeddingJobStatuses.Cancelled,
            ExecutionRunStatuses.Rejected => EmbeddingJobStatuses.Rejected,
            _ => EmbeddingJobStatuses.Failed
        };
    }

    private static int? CountRequestedTexts(EmbeddingRequest? request)
    {
        if (request is null)
        {
            return null;
        }

        var count = 0;
        if (request.Text is not null)
        {
            count++;
        }

        if (request.Texts is not null)
        {
            count += request.Texts.Count;
        }

        return count;
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

    private sealed class EmbeddingBatchExecutionHandler : IExecutionHandler
    {
        private readonly IEmbeddingProvider _embeddingProvider;
        private readonly EmbeddingProviderOptions _embeddingOptions;

        public EmbeddingBatchExecutionHandler(IEmbeddingProvider embeddingProvider, EmbeddingProviderOptions embeddingOptions)
        {
            _embeddingProvider = embeddingProvider;
            _embeddingOptions = embeddingOptions;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            PluginId = PluginId,
            DisplayName = "Vyral embedding batch",
            Description = "Generates embeddings for a bounded batch of text.",
            MaxAttempts = 1,
            Tags =
            {
                ["vyral.job"] = "embedding"
            }
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var request = context.Run.Payload?.Deserialize<EmbeddingRequest>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("Embedding execution payload is required.");
            var texts = GetEmbeddingTexts(request);
            var purpose = EmbeddingTextPreparer.NormalizePurpose(request.Purpose);
            var response = new EmbeddingResponse
            {
                Provider = _embeddingProvider.ProviderId,
                ModelId = _embeddingProvider.ModelId,
                Dimensions = _embeddingProvider.Dimensions,
                Purpose = purpose
            };

            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = texts.Count,
                Attempted = 0,
                Succeeded = 0,
                Failed = 0,
                Progress = 0,
                StatusDetails = BuildStatusDetails(purpose, null)
            }, ct);

            for (var i = 0; i < texts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await context.ReportAsync(new ExecutionRunUpdate
                {
                    CurrentStep = "embedding.item",
                    StatusDetails = BuildStatusDetails(purpose, i)
                }, ct);

                var item = await GenerateEmbeddingResultAsync(request, texts[i], i, purpose, _embeddingProvider, _embeddingOptions, ct);
                response.Items.Add(item);
                await context.ReportAsync(new ExecutionRunUpdate
                {
                    Requested = texts.Count,
                    Attempted = response.Items.Count,
                    Succeeded = response.Items.Count,
                    Failed = 0,
                    Progress = CalculateProgress(response.Items.Count, texts.Count),
                    Result = JsonSerializer.SerializeToNode(response, ExecutionJson.Options),
                    StatusDetails = BuildStatusDetails(purpose, i)
                }, ct);
            }

            return ExecutionRunResult.Succeeded(
                JsonSerializer.SerializeToNode(response, ExecutionJson.Options),
                BuildStatusDetails(purpose, null));
        }

        private JsonObject BuildStatusDetails(string purpose, int? currentIndex)
        {
            var details = new JsonObject
            {
                ["provider"] = _embeddingProvider.ProviderId,
                ["modelId"] = _embeddingProvider.ModelId,
                ["dimensions"] = _embeddingProvider.Dimensions,
                ["purpose"] = purpose
            };
            if (currentIndex.HasValue)
            {
                details["currentIndex"] = currentIndex.Value;
            }

            return details;
        }

        private static IReadOnlyList<string> GetEmbeddingTexts(EmbeddingRequest request)
        {
            const int maxBatchSize = 128;
            const int maxTextLength = 100_000;
            var texts = new List<string>();

            if (request.Text is not null)
            {
                texts.Add(request.Text);
            }

            if (request.Texts is not null)
            {
                texts.AddRange(request.Texts);
            }

            if (texts.Count == 0)
            {
                throw new InvalidOperationException("Embedding request must include text or texts.");
            }

            if (texts.Count > maxBatchSize)
            {
                throw new InvalidOperationException($"Embedding request supports at most {maxBatchSize} texts.");
            }

            foreach (var text in texts)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("Embedding request text values cannot be empty.");
                }

                if (text.Length > maxTextLength)
                {
                    throw new InvalidOperationException($"Embedding request text values cannot exceed {maxTextLength} characters.");
                }
            }

            return texts;
        }

        private static async Task<EmbeddingResult> GenerateEmbeddingResultAsync(
            EmbeddingRequest request,
            string text,
            int index,
            string purpose,
            IEmbeddingProvider embeddingProvider,
            EmbeddingProviderOptions embeddingOptions,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var prepared = EmbeddingTextPreparer.Prepare(
                text,
                purpose,
                request.QueryPrefix ?? embeddingOptions.QueryPrefix,
                request.PassagePrefix ?? embeddingOptions.PassagePrefix,
                request.SymmetricPrefix ?? embeddingOptions.SymmetricPrefix);
            var values = await embeddingProvider.GenerateEmbeddingAsync(prepared.PreparedText, ct);
            ct.ThrowIfCancellationRequested();
            return new EmbeddingResult
            {
                Index = index,
                TextLength = text.Length,
                PreparedTextLength = prepared.PreparedText.Length,
                PrefixApplied = prepared.PrefixApplied,
                PrefixLength = prepared.PrefixLength,
                Values = values
            };
        }

        private static double CalculateProgress(int attempted, int requested)
        {
            if (requested <= 0)
            {
                return 0;
            }

            return Math.Clamp(attempted / (double)requested, 0, 1);
        }
    }

    private sealed class EmbeddingExecutionPlugin : IExecutionPlugin
    {
        public EmbeddingExecutionPlugin(IEmbeddingProvider embeddingProvider, EmbeddingProviderOptions embeddingOptions)
        {
            Handlers = new[] { new EmbeddingBatchExecutionHandler(embeddingProvider, embeddingOptions) };
        }

        public ExecutionPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = PluginId,
            Name = "Vyral embeddings",
            Version = "1.0.0",
            Handlers =
            {
                new ExecutionHandlerDescriptor
                {
                    HandlerId = HandlerId,
                    PluginId = PluginId,
                    DisplayName = "Vyral embedding batch",
                    Description = "Generates embeddings for a bounded batch of text.",
                    MaxAttempts = 1,
                    Tags =
                    {
                        ["vyral.job"] = "embedding"
                    }
                }
            }
        };

        public IReadOnlyList<IExecutionHandler> Handlers { get; }
    }
}
