using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vyral.Execution;

namespace Vyral.Execution.AzureDurable;

public static class AzureDurableExecutionDialect
{
    public static ExecutionRuntimeAdapterDescriptor BuildAdapterDescriptor(AzureDurableExecutionOptions? options = null)
    {
        options ??= new AzureDurableExecutionOptions();
        var descriptor = new ExecutionRuntimeAdapterDescriptor
        {
            AdapterId = NormalizeAdapterId(options.AdapterId),
            RuntimeKind = AzureDurableExecutionRuntimeKindIds.DurableFunctions,
            DisplayName = "Azure Durable Functions execution runtime",
            Version = "0.2.0",
            Capabilities =
            {
                ExecutionCapabilityIds.RemoteOrchestration,
                ExecutionCapabilityIds.InProcessHandlers,
                ExecutionCapabilityIds.DurableRuns,
                ExecutionCapabilityIds.DurableTimers,
                ExecutionCapabilityIds.ExternalEvents,
                ExecutionCapabilityIds.DurableWaits,
                ExecutionCapabilityIds.Cancellation,
                ExecutionCapabilityIds.Retries,
                ExecutionCapabilityIds.RestartResume,
                ExecutionCapabilityIds.Leases,
                ExecutionCapabilityIds.Artifacts,
                ExecutionCapabilityIds.TraceHistory,
                ExecutionCapabilityIds.Idempotency
            },
            Metadata =
            {
                ["taskHubName"] = options.TaskHubName,
                ["orchestratorName"] = options.OrchestratorName,
                ["activityName"] = options.ActivityName,
                ["startActivityName"] = options.StartActivityName,
                ["stepActivityName"] = options.StepActivityName,
                ["statusStoreName"] = options.StatusStoreName,
                ["artifactContainerName"] = options.ArtifactContainerName,
                ["maxActiveRuns"] = options.MaxActiveRuns.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["defaultListLimit"] = options.DefaultListLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxListLimit"] = options.MaxListLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxPayloadBytes"] = options.Limits.MaxPayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxArtifactBytes"] = options.Limits.MaxArtifactBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxArtifactInlineBytes"] = options.Limits.MaxArtifactInlineBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxTraceMessageChars"] = options.Limits.MaxTraceMessageChars.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["concurrencyKeyPolicy"] = "orchestrator_serialization_required"
            }
        };
        ExecutionContractValidator.ValidateAdapterDescriptor(descriptor, options.Limits);
        return descriptor;
    }

    public static ExecutionOperationalPolicy BuildOperationalPolicy(AzureDurableExecutionOptions? options = null)
    {
        options ??= new AzureDurableExecutionOptions();
        return new ExecutionOperationalPolicy
        {
            MaxActiveRuns = options.MaxActiveRuns,
            MaxRetainedTerminalRuns = null,
            DefaultListLimit = options.DefaultListLimit,
            MaxListLimit = options.MaxListLimit,
            DefaultHistoryLimit = options.DefaultListLimit,
            MaxHistoryLimit = options.MaxListLimit,
            MaxPayloadBytes = options.Limits.MaxPayloadBytes,
            MaxResultBytes = options.Limits.MaxResultBytes,
            MaxStatusDetailsBytes = options.Limits.MaxStatusDetailsBytes,
            MaxArtifactBytes = options.Limits.MaxArtifactBytes,
            MaxArtifactInlineBytes = options.Limits.MaxArtifactInlineBytes,
            MaxTraceMessageChars = options.Limits.MaxTraceMessageChars,
            MaxTraceDetailsBytes = options.Limits.MaxTraceDetailsBytes,
            MaxRetryAttempts = options.Limits.MaxRetryAttempts,
            MaxRetryDelaySeconds = options.Limits.MaxRetryDelaySeconds,
            MaxLeaseTtlSeconds = options.Limits.MaxLeaseTtlSeconds,
            ConcurrencyKeyPolicy = "orchestrator_serialization_required",
            ConcurrencyRetryDelayMs = null,
            DefaultTraceSeverity = "info",
            RetentionScope = "status_store_defined"
        };
    }

    public static ExecutionResumePolicy BuildResumePolicy()
    {
        return new ExecutionResumePolicy
        {
            Mode = ExecutionResumePolicyModes.RestartRecovery,
            InterruptedRunningBehavior = ExecutionResumePolicyBehaviors.MayReexecuteHandler,
            ScheduledWaitingBehavior = ExecutionResumePolicyBehaviors.DispatchWhenDue,
            TerminalBehavior = ExecutionResumePolicyBehaviors.NeverResume,
            PluginCheckpointBehavior = ExecutionResumePolicyBehaviors.PluginOwned,
            IdempotencyScope = "handler_plugin_payload",
            CreatesLinkedFollowUpRuns = false
        };
    }

    public static AzureDurableStartCommand BuildStartCommand(
        ExecutionRunRequest request,
        IReadOnlyList<ExecutionHandlerDescriptor> handlers,
        AzureDurableExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handlers);
        options ??= new AzureDurableExecutionOptions();
        ExecutionContractValidator.ValidateRunRequest(request, options.Limits);
        var retryPolicy = ExecutionContractValidator.NormalizeRetryPolicy(request.RetryPolicy, options.Limits);

        var handler = handlers.FirstOrDefault(candidate =>
            string.Equals(candidate.HandlerId, request.HandlerId, StringComparison.Ordinal));
        if (handler is null)
        {
            throw new InvalidOperationException($"Execution handler '{request.HandlerId}' is not registered.");
        }

        var effectiveRequest = CloneRequest(request, retryPolicy);
        effectiveRequest.PluginId = ResolvePluginId(effectiveRequest.HandlerId, effectiveRequest.PluginId, handler.PluginId);

        return new AzureDurableStartCommand
        {
            InstanceId = BuildInstanceId(request, options),
            OrchestratorName = options.OrchestratorName,
            ActivityName = options.ActivityName,
            StartActivityName = options.StartActivityName,
            StepActivityName = options.StepActivityName,
            ExternalEventName = options.ExternalEventName,
            Request = effectiveRequest,
            Handler = Clone(handler, options.Limits),
            RetryOptions = ToRetryOptions(retryPolicy, options.Limits),
            Metadata =
            {
                ["adapterId"] = NormalizeAdapterId(options.AdapterId),
                ["runtimeKind"] = AzureDurableExecutionRuntimeKindIds.DurableFunctions,
                ["taskHubName"] = options.TaskHubName,
                ["workerId"] = string.IsNullOrWhiteSpace(options.WorkerId) ? Environment.MachineName : options.WorkerId
            }
        };
    }

    public static string BuildInstanceId(ExecutionRunRequest request, AzureDurableExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new AzureDurableExecutionOptions();
        ExecutionContractValidator.ValidateRunRequest(request, options.Limits);

        var prefix = NormalizeInstanceSegment(options.AdapterId);
        var source = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : $"{request.HandlerId}\n{request.IdempotencyKey}";
        var suffix = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? source
            : Hash(source);
        return $"{prefix}-{suffix}";
    }

    public static AzureDurableRetryOptions ToRetryOptions(ExecutionRetryPolicy? retryPolicy, ExecutionRuntimeLimits? limits = null)
    {
        retryPolicy = ExecutionContractValidator.NormalizeRetryPolicy(retryPolicy, limits);
        return new AzureDurableRetryOptions
        {
            MaxAttempts = retryPolicy.MaxAttempts,
            InitialDelaySeconds = retryPolicy.InitialDelaySeconds,
            MaxDelaySeconds = retryPolicy.MaxDelaySeconds,
            BackoffMultiplier = retryPolicy.BackoffMultiplier
        };
    }

    public static ExecutionRun CreateQueuedRun(AzureDurableStartCommand command, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ExecutionContractValidator.ValidateRunRequest(command.Request);
        ExecutionContractValidator.ValidateHandlerDescriptor(command.Handler);
        var retryPolicy = ExecutionContractValidator.NormalizeRetryPolicy(command.Request.RetryPolicy);
        var now = nowUtc ?? DateTime.UtcNow;
        var runId = string.IsNullOrWhiteSpace(command.InstanceId) ? BuildInstanceId(command.Request) : command.InstanceId;

        var status = command.Request.ScheduledAtUtc.HasValue && command.Request.ScheduledAtUtc.Value > now
            ? ExecutionRunStatuses.Waiting
            : ExecutionRunStatuses.Queued;
        ExecutionRunLifecycle.EnsureCreationStatus(status);

        return new ExecutionRun
        {
            Id = runId,
            HandlerId = command.Request.HandlerId,
            PluginId = string.IsNullOrWhiteSpace(command.Request.PluginId) ? null : command.Request.PluginId,
            Status = status,
            Attempt = 0,
            MaxAttempts = retryPolicy.MaxAttempts,
            RetryPolicy = Clone(retryPolicy),
            IdempotencyKey = string.IsNullOrWhiteSpace(command.Request.IdempotencyKey) ? null : command.Request.IdempotencyKey,
            CorrelationId = string.IsNullOrWhiteSpace(command.Request.CorrelationId) ? runId : command.Request.CorrelationId,
            PayloadHash = HashPayload(command.Request.Payload),
            Payload = CloneNode(command.Request.Payload),
            CreatedAtUtc = now,
            ScheduledAtUtc = command.Request.ScheduledAtUtc,
            UpdatedAtUtc = now,
            Tags = new Dictionary<string, string>(command.Request.Tags, StringComparer.Ordinal)
        };
    }

    public static ExecutionRun CreateRejectedRun(
        ExecutionRunRequest request,
        string failureClass,
        string error,
        AzureDurableExecutionOptions? options = null,
        DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new AzureDurableExecutionOptions();
        ExecutionContractValidator.ValidateRunRequest(request, options.Limits);
        ExecutionRunLifecycle.EnsureCreationStatus(ExecutionRunStatuses.Rejected);
        var retryPolicy = ExecutionContractValidator.NormalizeRetryPolicy(request.RetryPolicy, options.Limits);
        var now = nowUtc ?? DateTime.UtcNow;

        var runId = BuildInstanceId(request, options);
        return new ExecutionRun
        {
            Id = runId,
            HandlerId = request.HandlerId.Trim(),
            PluginId = NormalizeOptional(request.PluginId),
            Status = ExecutionRunStatuses.Rejected,
            Attempt = 0,
            MaxAttempts = retryPolicy.MaxAttempts,
            RetryPolicy = Clone(retryPolicy),
            IdempotencyKey = NormalizeOptional(request.IdempotencyKey),
            CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? runId : request.CorrelationId.Trim(),
            PayloadHash = HashPayload(request.Payload),
            Payload = CloneNode(request.Payload),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CompletedAtUtc = now,
            DurationMs = 0,
            FailureClass = failureClass,
            Error = ExecutionContractValidator.BoundText(error, options.Limits.MaxTraceMessageChars),
            Tags = new Dictionary<string, string>(request.Tags, StringComparer.Ordinal)
        };
    }

    public static ExecutionRun StartActivityAttempt(ExecutionRun run, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        var now = nowUtc ?? DateTime.UtcNow;
        var next = Clone(run);
        ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Running);
        next.Status = ExecutionRunStatuses.Running;
        next.Attempt = Math.Max(0, run.Attempt) + 1;
        next.StartedAtUtc ??= now;
        next.UpdatedAtUtc = now;
        return next;
    }

    public static AzureDurableActivityCommand BuildActivityCommand(ExecutionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new AzureDurableActivityCommand
        {
            RunId = run.Id,
            HandlerId = run.HandlerId,
            PluginId = run.PluginId,
            Attempt = Math.Max(1, run.Attempt),
            Payload = CloneNode(run.Payload),
            CorrelationId = run.CorrelationId
        };
    }

    public static ExecutionRun ApplyActivityResult(
        ExecutionRun run,
        AzureDurableActivityResult activityResult,
        DateTime? nowUtc = null,
        ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(activityResult);
        if (!string.Equals(run.Id, activityResult.RunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Activity result run id '{activityResult.RunId}' does not match run '{run.Id}'.");
        }

        ExecutionContractValidator.ValidateRunResult(activityResult.Result, limits);
        var next = Clone(run);
        var terminalStatus = NormalizeTerminalStatus(activityResult.Result.Status);
        ExecutionRunLifecycle.EnsureTransition(run.Status, terminalStatus);
        next.Status = terminalStatus;
        next.Result = CloneNode(activityResult.Result.Result ?? next.Result);
        next.StatusDetails = CloneObject(activityResult.Result.StatusDetails ?? next.StatusDetails);
        if (next.Status == ExecutionRunStatuses.Succeeded)
        {
            next.FailureClass = null;
            next.Error = null;
        }
        else
        {
            next.FailureClass = activityResult.Result.FailureClass ?? next.FailureClass;
            next.Error = activityResult.Result.Error ?? next.Error;
        }

        next.CancellationRequested = next.CancellationRequested || next.Status == ExecutionRunStatuses.Cancelled;
        CompleteTiming(next, nowUtc ?? DateTime.UtcNow);
        return next;
    }

    public static bool ShouldRetry(ExecutionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.CancellationRequested || run.Status is not (ExecutionRunStatuses.Failed or ExecutionRunStatuses.TimedOut))
        {
            return false;
        }

        return run.Attempt < Math.Max(1, run.MaxAttempts);
    }

    public static TimeSpan CalculateRetryDelay(ExecutionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var policy = run.RetryPolicy ?? new ExecutionRetryPolicy();
        var initial = Math.Max(0, policy.InitialDelaySeconds);
        var max = Math.Max(initial, policy.MaxDelaySeconds);
        var multiplier = policy.BackoffMultiplier <= 0 ? 1 : policy.BackoffMultiplier;
        var exponent = Math.Max(0, run.Attempt - 1);
        var seconds = initial * Math.Pow(multiplier, exponent);
        return TimeSpan.FromSeconds(Math.Min(max, seconds));
    }

    public static ExecutionRun ScheduleRetry(ExecutionRun run, DateTime scheduledAtUtc)
    {
        ArgumentNullException.ThrowIfNull(run);
        var next = Clone(run);
        ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.Retry);
        next.Status = ExecutionRunStatuses.Waiting;
        next.ScheduledAtUtc = scheduledAtUtc;
        next.UpdatedAtUtc = DateTime.UtcNow;
        next.CurrentStep = null;
        return next;
    }

    public static AzureDurableStatusSnapshot ToStatusSnapshot(ExecutionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new AzureDurableStatusSnapshot
        {
            RunId = run.Id,
            HandlerId = run.HandlerId,
            PluginId = run.PluginId,
            Status = run.Status,
            Attempt = run.Attempt,
            Progress = run.Progress,
            CurrentStep = run.CurrentStep,
            FailureClass = run.FailureClass,
            Error = run.Error,
            UpdatedAtUtc = run.UpdatedAtUtc,
            Details = run.StatusDetails
        };
    }

    private static ExecutionRun Clone(ExecutionRun run)
    {
        return new ExecutionRun
        {
            Id = run.Id,
            HandlerId = run.HandlerId,
            PluginId = run.PluginId,
            Status = run.Status,
            Attempt = run.Attempt,
            MaxAttempts = run.MaxAttempts,
            RetryPolicy = Clone(run.RetryPolicy),
            IdempotencyKey = run.IdempotencyKey,
            CorrelationId = run.CorrelationId,
            PayloadHash = run.PayloadHash,
            Payload = CloneNode(run.Payload),
            CreatedAtUtc = run.CreatedAtUtc,
            ScheduledAtUtc = run.ScheduledAtUtc,
            StartedAtUtc = run.StartedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            DurationMs = run.DurationMs,
            CancellationRequested = run.CancellationRequested,
            Requested = run.Requested,
            Attempted = run.Attempted,
            Succeeded = run.Succeeded,
            Failed = run.Failed,
            Progress = run.Progress,
            CurrentStep = run.CurrentStep,
            FailureClass = run.FailureClass,
            Error = run.Error,
            Result = CloneNode(run.Result),
            StatusDetails = CloneObject(run.StatusDetails),
            Tags = new Dictionary<string, string>(run.Tags, StringComparer.Ordinal)
        };
    }

    private static ExecutionHandlerDescriptor Clone(ExecutionHandlerDescriptor descriptor, ExecutionRuntimeLimits? limits = null)
    {
        ExecutionContractValidator.ValidateHandlerDescriptor(descriptor, limits);
        return new ExecutionHandlerDescriptor
        {
            HandlerId = descriptor.HandlerId,
            PluginId = descriptor.PluginId,
            DisplayName = descriptor.DisplayName,
            Description = descriptor.Description,
            MaxAttempts = descriptor.MaxAttempts,
            ConcurrencyKey = descriptor.ConcurrencyKey,
            Tags = new Dictionary<string, string>(descriptor.Tags, StringComparer.Ordinal)
        };
    }

    private static ExecutionRetryPolicy Clone(ExecutionRetryPolicy retryPolicy)
    {
        return new ExecutionRetryPolicy
        {
            MaxAttempts = retryPolicy.MaxAttempts,
            InitialDelaySeconds = retryPolicy.InitialDelaySeconds,
            MaxDelaySeconds = retryPolicy.MaxDelaySeconds,
            BackoffMultiplier = retryPolicy.BackoffMultiplier
        };
    }

    private static ExecutionRunRequest CloneRequest(ExecutionRunRequest request, ExecutionRetryPolicy retryPolicy)
    {
        return new ExecutionRunRequest
        {
            HandlerId = request.HandlerId,
            PluginId = request.PluginId,
            Payload = CloneNode(request.Payload),
            IdempotencyKey = request.IdempotencyKey,
            CorrelationId = request.CorrelationId,
            ScheduledAtUtc = request.ScheduledAtUtc,
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = retryPolicy.MaxAttempts,
                InitialDelaySeconds = retryPolicy.InitialDelaySeconds,
                MaxDelaySeconds = retryPolicy.MaxDelaySeconds,
                BackoffMultiplier = retryPolicy.BackoffMultiplier
            },
            Tags = new Dictionary<string, string>(request.Tags, StringComparer.Ordinal)
        };
    }

    private static System.Text.Json.Nodes.JsonNode? CloneNode(System.Text.Json.Nodes.JsonNode? value)
    {
        return value is null ? null : System.Text.Json.Nodes.JsonNode.Parse(value.ToJsonString(ExecutionJson.Options));
    }

    private static System.Text.Json.Nodes.JsonObject? CloneObject(System.Text.Json.Nodes.JsonObject? value)
    {
        return value is null ? null : System.Text.Json.Nodes.JsonNode.Parse(value.ToJsonString(ExecutionJson.Options))!.AsObject();
    }

    private static void CompleteTiming(ExecutionRun run, DateTime completedAtUtc)
    {
        var startedAt = run.StartedAtUtc ?? completedAtUtc;
        run.StartedAtUtc = startedAt;
        run.CompletedAtUtc = completedAtUtc;
        run.UpdatedAtUtc = completedAtUtc;
        run.DurationMs = (completedAtUtc - startedAt).TotalMilliseconds;
        run.CurrentStep = null;
        if (run.Status == ExecutionRunStatuses.Succeeded)
        {
            run.Progress = 1;
        }
    }

    private static string NormalizeTerminalStatus(string status)
    {
        return ExecutionRunStatuses.IsTerminal(status) ? status : ExecutionRunStatuses.Failed;
    }

    private static string NormalizeAdapterId(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "azure-durable" : value.Trim();
    }

    private static string? ResolvePluginId(string handlerId, string? requestedPluginId, string? handlerPluginId)
    {
        requestedPluginId = NormalizeOptional(requestedPluginId);
        handlerPluginId = NormalizeOptional(handlerPluginId);
        if (requestedPluginId is not null &&
            handlerPluginId is not null &&
            !string.Equals(requestedPluginId, handlerPluginId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Execution handler '{handlerId}' belongs to plugin '{handlerPluginId}', not '{requestedPluginId}'.");
        }

        return requestedPluginId ?? handlerPluginId;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeInstanceSegment(string value)
    {
        var normalized = Regex.Replace(NormalizeAdapterId(value).ToLowerInvariant(), "[^a-z0-9-]", "-");
        normalized = Regex.Replace(normalized, "-{2,}", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "azure-durable" : normalized;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static string HashPayload(System.Text.Json.Nodes.JsonNode? value)
    {
        var text = value?.ToJsonString(ExecutionJson.Options) ?? string.Empty;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
