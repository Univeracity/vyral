using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;

namespace Vyral.Server;

/// <summary>
/// Experimental management-plane composition for durable projection generation. Search adapters
/// do not depend on this class and no per-query network hop is introduced.
/// </summary>
public sealed class ExecutionRuntimeProjectionGenerationAdapter
{
    public const string PluginId = "vyral.retrieval.projection-generation";
    public const string BuildHandlerId = "vyral.retrieval.projection-generation.build";
    private readonly IExecutionRuntime _runtime;
    private readonly IRecordSearchProjectionGenerationBuilder _builder;

    public ExecutionRuntimeProjectionGenerationAdapter(
        IExecutionRuntime runtime,
        IRecordSearchProjectionGenerationBuilder builder,
        IObjectStore artifactStore)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        ArgumentNullException.ThrowIfNull(artifactStore);
        if (string.IsNullOrWhiteSpace(builder.BuilderId))
        {
            throw new InvalidOperationException("A projection generation builder requires a stable builder ID.");
        }
        _runtime.RegisterPlugin(new ProjectionGenerationPlugin(builder, artifactStore));
    }

    public Task<ExecutionRun> StartBuildAsync(
        RecordSearchProjectionGenerationBuildRequest request,
        string idempotencyKey,
        ExecutionScope? scope = null,
        CancellationToken ct = default)
    {
        RecordSearchProjectionGenerationContract.ValidateBuildRequest(request);
        if (!string.Equals(request.BuilderId, _builder.BuilderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The build request does not target the configured builder.");
        }
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new InvalidOperationException("A durable projection generation build requires an idempotency key.");
        }
        return _runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = BuildHandlerId,
            PluginId = PluginId,
            Payload = JsonSerializer.SerializeToNode(request, ExecutionJson.Options),
            IdempotencyKey = idempotencyKey,
            Scope = scope,
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = 3,
                InitialDelaySeconds = 1,
                MaxDelaySeconds = 30,
                BackoffMultiplier = 2
            },
            Tags =
            {
                ["vyral.job"] = "retrieval-projection-generation",
                ["vyral.retrieval.builder"] = request.BuilderId,
                ["vyral.retrieval.generation"] = request.GenerationId,
                ["vyral.retrieval.profile"] = request.ProfileId
            }
        }, ct);
    }

    public async Task<ExecutionRun?> GetAsync(
        string runId,
        bool includeResult = true,
        CancellationToken ct = default)
    {
        var run = await _runtime.GetRunAsync(runId, includeResult, ct);
        return IsProjectionGenerationRun(run) ? run : null;
    }

    public async Task<ExecutionRun?> CancelAsync(string runId, CancellationToken ct = default)
    {
        var existing = await _runtime.GetRunAsync(runId, includeResult: false, ct);
        return IsProjectionGenerationRun(existing)
            ? await _runtime.CancelRunAsync(runId, ct)
            : null;
    }

    private static bool IsProjectionGenerationRun(ExecutionRun? run) =>
        run is not null &&
        string.Equals(run.HandlerId, BuildHandlerId, StringComparison.Ordinal) &&
        string.Equals(run.PluginId, PluginId, StringComparison.Ordinal);

    private sealed class ProjectionGenerationPlugin : IExecutionPlugin
    {
        private readonly IReadOnlyList<IExecutionHandler> _handlers;

        public ProjectionGenerationPlugin(
            IRecordSearchProjectionGenerationBuilder builder,
            IObjectStore artifactStore)
        {
            _handlers = [new BuildHandler(builder, artifactStore)];
            Descriptor = new ExecutionPluginDescriptor
            {
                PluginId = PluginId,
                Name = "Vyral retrieval projection generation",
                Version = "0.1.0",
                Handlers = [BuildHandler.DescriptorValue]
            };
        }

        public ExecutionPluginDescriptor Descriptor { get; }
        public IReadOnlyList<IExecutionHandler> Handlers => _handlers;
    }

    private sealed class BuildHandler : IExecutionHandler
    {
        public static readonly ExecutionHandlerDescriptor DescriptorValue = new()
        {
            HandlerId = BuildHandlerId,
            PluginId = PluginId,
            DisplayName = "Build and verify an immutable retrieval projection generation",
            Description = "Builds provider-native artifacts and returns a compact generation-bound verification receipt.",
            MaxAttempts = 3,
            Tags =
            {
                ["vyral.job"] = "retrieval-projection-generation"
            }
        };

        private readonly IRecordSearchProjectionGenerationBuilder _builder;
        private readonly IObjectStore _artifactStore;

        public BuildHandler(IRecordSearchProjectionGenerationBuilder builder, IObjectStore artifactStore)
        {
            _builder = builder;
            _artifactStore = artifactStore;
        }

        public ExecutionHandlerDescriptor Descriptor => DescriptorValue;

        public async Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default)
        {
            var request = context.Run.Payload?.Deserialize<RecordSearchProjectionGenerationBuildRequest>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("A projection generation build payload is required.");
            RecordSearchProjectionGenerationContract.ValidateBuildRequest(request);
            if (!string.Equals(request.BuilderId, _builder.BuilderId, StringComparison.Ordinal))
            {
                return ExecutionRunResult.Failed(
                    ExecutionFailureClasses.Validation,
                    "The durable build targets a different projection generation builder.");
            }
            if (request.DeadlineUtc is { } deadline && deadline <= DateTime.UtcNow)
            {
                return ExecutionRunResult.Failed(
                    ExecutionFailureClasses.Timeout,
                    "The projection generation build deadline elapsed before execution began.");
            }

            var buildDeadline = request.DeadlineUtc;
            using var deadlineSource = buildDeadline.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (deadlineSource is not null)
            {
                var remaining = buildDeadline!.Value - DateTime.UtcNow;
                deadlineSource.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }

            RecordSearchProjectionGenerationBuildReceipt receipt;
            try
            {
                var buildCancellation = deadlineSource?.Token ?? ct;
                receipt = await _builder.BuildAndVerifyAsync(
                    request,
                    _artifactStore,
                    (update, _) => ReportProgressAsync(context, update, buildCancellation),
                    buildCancellation);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadlineSource?.IsCancellationRequested == true)
            {
                return ExecutionRunResult.Failed(
                    ExecutionFailureClasses.Timeout,
                    "The projection generation build deadline elapsed during execution.");
            }
            if (request.DeadlineUtc is { } completedDeadline && completedDeadline <= DateTime.UtcNow)
            {
                return ExecutionRunResult.Failed(
                    ExecutionFailureClasses.Timeout,
                    "The projection generation build deadline elapsed before verification completed.");
            }
            try
            {
                RecordSearchProjectionGenerationContract.ValidateBuildReceipt(request, receipt);
            }
            catch (InvalidOperationException exception)
            {
                return ExecutionRunResult.Failed(
                    ExecutionFailureClasses.Validation,
                    "Projection generation verification receipt was rejected: " + exception.Message);
            }

            var result = JsonSerializer.SerializeToNode(receipt, ExecutionJson.Options);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "projection-generation-receipt",
                Kind = ExecutionArtifactKinds.Json,
                MediaType = "application/json",
                Content = result?.DeepClone(),
                Metadata =
                {
                    ["generationId"] = receipt.Descriptor.GenerationId,
                    ["descriptorDigest"] = receipt.Descriptor.DescriptorDigest,
                    ["builderId"] = receipt.BuilderId
                }
            }, ct);
            var status = new JsonObject
            {
                ["stage"] = "verified",
                ["generationId"] = receipt.Descriptor.GenerationId,
                ["descriptorDigest"] = receipt.Descriptor.DescriptorDigest,
                ["artifactCount"] = receipt.Descriptor.Artifacts.Count
            };
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = checked((int)Math.Min(receipt.Descriptor.ExpectedItemCount, int.MaxValue)),
                Attempted = checked((int)Math.Min(receipt.Descriptor.ExpectedItemCount, int.MaxValue)),
                Succeeded = checked((int)Math.Min(receipt.Descriptor.ExpectedItemCount, int.MaxValue)),
                Failed = 0,
                Progress = 1,
                CurrentStep = "verified",
                Result = result?.DeepClone(),
                StatusDetails = status
            }, ct);
            return ExecutionRunResult.Succeeded(result, status);
        }

        private static async Task ReportProgressAsync(
            IExecutionRunContext context,
            RecordSearchProjectionGenerationBuildProgress progress,
            CancellationToken ct)
        {
            RecordSearchProjectionGenerationContract.ValidateBuildProgress(progress);
            var ratio = progress.Total is > 0
                ? Math.Clamp(progress.Completed / (double)progress.Total.Value, 0, 1)
                : 0;
            var checkpoint = new JsonObject
            {
                ["stage"] = progress.Stage,
                ["completed"] = progress.Completed,
                ["total"] = progress.Total
            };
            if (progress.Checkpoint is not null)
            {
                checkpoint["builder"] = progress.Checkpoint.DeepClone();
            }
            await context.PutCheckpointAsync(new ExecutionCheckpointWrite
            {
                Key = "projection-generation-progress",
                Content = checkpoint.DeepClone()
            }, ct);
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Progress = ratio,
                CurrentStep = progress.Stage,
                StatusDetails = checkpoint
            }, ct);
        }
    }

}
