using System.Text;
using System.Text.Json.Nodes;

namespace Vyral.Execution;

public static class ExecutionContractValidator
{
    public static void ValidateAdapterDescriptor(ExecutionRuntimeAdapterDescriptor descriptor, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateId(descriptor.AdapterId, "Adapter id", limits);
        ValidateId(descriptor.RuntimeKind, "Runtime kind", limits);
        ValidateBoundedText(descriptor.DisplayName, "Adapter display name", limits.MaxDisplayNameChars, required: true);
        ValidateBoundedText(descriptor.Version, "Adapter version", limits.MaxIdChars, required: true);
        ValidateCapabilities(descriptor.Capabilities, limits);
        ValidateMetadata(descriptor.Metadata, limits);

        var missing = ExecutionCapabilityCatalog.MissingRequiredCapabilities(descriptor.Capabilities);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Adapter '{descriptor.AdapterId}' is missing required execution capabilities: {string.Join(", ", missing)}.");
        }

        if (!ExecutionCapabilityCatalog.HasDispatchCapability(descriptor.Capabilities))
        {
            throw new InvalidOperationException($"Adapter '{descriptor.AdapterId}' must advertise at least one dispatch capability: {string.Join(", ", ExecutionCapabilityCatalog.DispatchCapabilities)}.");
        }

        if (!ExecutionCapabilityCatalog.HasExecutionModelCapability(descriptor.Capabilities))
        {
            throw new InvalidOperationException($"Adapter '{descriptor.AdapterId}' must advertise at least one execution-model capability: {string.Join(", ", ExecutionCapabilityCatalog.ExecutionModelCapabilities)}.");
        }
    }

    public static void ValidatePluginDescriptor(ExecutionPluginDescriptor descriptor, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateId(descriptor.PluginId, "Plugin id", limits);
        ValidateBoundedText(descriptor.Name, "Plugin name", limits.MaxDisplayNameChars, required: true);
        ValidateBoundedText(descriptor.Version, "Plugin version", limits.MaxIdChars, required: true);
        foreach (var handler in descriptor.Handlers)
        {
            ValidateHandlerDescriptor(handler, limits);
            if (string.IsNullOrWhiteSpace(handler.PluginId))
            {
                continue;
            }

            if (!string.Equals(handler.PluginId.Trim(), descriptor.PluginId.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Handler '{handler.HandlerId}' plugin id does not match plugin '{descriptor.PluginId}'.");
            }
        }
    }

    public static void ValidateHandlerDescriptor(ExecutionHandlerDescriptor descriptor, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateId(descriptor.HandlerId, "Handler id", limits);
        ValidateOptionalId(descriptor.PluginId, "Handler plugin id", limits);
        ValidateBoundedText(descriptor.DisplayName, "Handler display name", limits.MaxDisplayNameChars, required: true);
        ValidateBoundedText(descriptor.Description, "Handler description", limits.MaxDescriptionChars, required: false);
        if (descriptor.MaxAttempts < 0 || descriptor.MaxAttempts > limits.MaxRetryAttempts)
        {
            throw new InvalidOperationException($"Handler max attempts must be between 0 and {limits.MaxRetryAttempts}.");
        }

        ValidateOptionalId(descriptor.ConcurrencyKey, "Handler concurrency key", limits);
        ValidateTags(descriptor.Tags, limits);
    }

    public static void ValidateRunRequest(ExecutionRunRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateId(request.HandlerId, "Handler id", limits);
        ValidateOptionalId(request.PluginId, "Plugin id", limits);
        ValidateOptionalId(request.IdempotencyKey, "Idempotency key", limits);
        ValidateOptionalId(request.CorrelationId, "Correlation id", limits);
        ValidateScope(request.Scope, limits);
        ValidateJsonBytes(request.Payload, "Run payload", limits.MaxPayloadBytes);
        ValidateRetryPolicy(request.RetryPolicy, limits);
        ValidateTags(request.Tags, limits);
    }

    public static void ValidateRunUpdate(ExecutionRunUpdate update, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateOptionalStatus(update.Status, "Run update status");
        ValidateCount(update.Requested, "requested");
        ValidateCount(update.Attempted, "attempted");
        ValidateCount(update.Succeeded, "succeeded");
        ValidateCount(update.Failed, "failed");
        if (update.Progress is < 0 or > 1)
        {
            throw new InvalidOperationException("Run progress must be between 0 and 1.");
        }

        ValidateBoundedText(update.CurrentStep, "Current step", limits.MaxDisplayNameChars, required: false);
        ValidateOptionalId(update.FailureClass, "Failure class", limits);
        ValidateBoundedText(update.Error, "Run error", limits.MaxTraceMessageChars, required: false);
        ValidateJsonBytes(update.Result, "Run result", limits.MaxResultBytes);
        ValidateJsonBytes(update.StatusDetails, "Status details", limits.MaxStatusDetailsBytes);
    }

    public static void ValidateRunResult(ExecutionRunResult result, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateStatus(result.Status, "Run result status");
        ValidateOptionalId(result.FailureClass, "Failure class", limits);
        ValidateBoundedText(result.Error, "Run result error", limits.MaxTraceMessageChars, required: false);
        ValidateJsonBytes(result.Result, "Run result", limits.MaxResultBytes);
        ValidateJsonBytes(result.StatusDetails, "Status details", limits.MaxStatusDetailsBytes);
    }

    public static void ValidateArtifactWrite(ExecutionArtifactWrite artifact, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateBoundedText(artifact.Name, "Artifact name", limits.MaxArtifactNameChars, required: true);
        ValidateOptionalId(artifact.Kind, "Artifact kind", limits);
        ValidateBoundedText(artifact.MediaType, "Artifact media type", limits.MaxDisplayNameChars, required: false);
        ValidateBoundedText(artifact.Uri, "Artifact uri", limits.MaxTraceMessageChars, required: false);
        ValidateTags(artifact.Metadata, limits);

        if (artifact.Text is not null)
        {
            ValidateBytes(Encoding.UTF8.GetByteCount(artifact.Text), "Artifact text", limits.MaxArtifactBytes);
        }

        ValidateJsonBytes(artifact.Content, "Artifact content", limits.MaxArtifactBytes);

        if (artifact.Text is null && artifact.Content is null && string.IsNullOrWhiteSpace(artifact.Uri))
        {
            throw new InvalidOperationException("Artifact requires text, content, or uri.");
        }
    }

    public static void ValidateCheckpointWrite(ExecutionCheckpointWrite checkpoint, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateBoundedText(checkpoint.Key, "Checkpoint key", limits.MaxCheckpointKeyChars, required: true);
        ValidateJsonBytes(checkpoint.Content, "Checkpoint content", limits.MaxCheckpointBytes);
        ValidateTags(checkpoint.Metadata, limits);
    }

    public static void ValidateTraceEvent(ExecutionTraceEvent traceEvent, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(traceEvent);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateId(traceEvent.Id, "Trace id", limits);
        ValidateId(traceEvent.RunId, "Trace run id", limits);
        ValidateId(traceEvent.SequenceId, "Trace sequence id", limits);
        ValidateId(traceEvent.Type, "Trace event type", limits);
        ValidateOptionalStatus(traceEvent.Status, "Trace status");
        ValidateBoundedText(traceEvent.StepId, "Trace step id", limits.MaxIdChars, required: false);
        ValidateBoundedText(traceEvent.Severity, "Trace severity", limits.MaxIdChars, required: true);
        ValidateBoundedText(traceEvent.Message, "Trace message", limits.MaxTraceMessageChars, required: false);
        ValidateJsonBytes(traceEvent.Details, "Trace details", limits.MaxTraceDetailsBytes);
        ValidateTags(traceEvent.Context, limits);
    }

    public static void ValidateLeaseRequest(ExecutionLeaseRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateId(request.LeaseKey, "Lease key", limits);
        ValidateId(request.OwnerId, "Lease owner id", limits);
        ValidateOptionalId(request.RunId, "Lease run id", limits);
        if (request.TtlSeconds <= 0 || request.TtlSeconds > limits.MaxLeaseTtlSeconds)
        {
            throw new InvalidOperationException($"Lease ttl seconds must be between 0 and {limits.MaxLeaseTtlSeconds}.");
        }

        ValidateJsonBytes(request.Metadata, "Lease metadata", limits.MaxStatusDetailsBytes);
    }

    public static void ValidateTimerRequest(ExecutionTimerRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateId(request.Name, "Timer name", limits);
        ValidateOptionalId(request.RunId, "Timer run id", limits);
        ValidateJsonBytes(request.Payload, "Timer payload", limits.MaxPayloadBytes);
    }

    public static void ValidateExternalEventRequest(ExecutionExternalEventRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;

        ValidateId(request.Name, "External event name", limits);
        ValidateOptionalId(request.RunId, "External event run id", limits);
        ValidateJsonBytes(request.Payload, "External event payload", limits.MaxPayloadBytes);
    }

    public static void ValidateExternalWorkerLeaseRequest(ExecutionExternalWorkerLeaseRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;
        ValidateId(request.WorkerId, "External worker id", limits);
        ValidateOptionalId(request.RunId, "Execution run id", limits);
        ValidateWorkerHandlers(request.HandlerIds, limits);
        ValidateLeaseTtl(request.TtlSeconds, limits);
    }

    public static void ValidateExternalWorkerHeartbeatRequest(ExecutionExternalWorkerHeartbeatRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;
        ValidateExternalWorkerLeaseIdentity(request.LeaseKey, request.LeaseToken, request.WorkerId, limits);
        ValidateLeaseTtl(request.TtlSeconds, limits);
    }

    public static void ValidateExternalWorkerReportRequest(ExecutionExternalWorkerReportRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;
        ValidateExternalWorkerLeaseIdentity(request.LeaseKey, request.LeaseToken, request.WorkerId, limits);
        ValidateRunUpdate(request.Update, limits);
        if (!string.IsNullOrWhiteSpace(request.Update.Status) && request.Update.Status != ExecutionRunStatuses.Running)
        {
            throw new InvalidOperationException("External worker progress updates may only report the running status; use the wait or completion operation for lifecycle transitions.");
        }
    }

    public static void ValidateExternalWorkerEventRequest(ExecutionExternalWorkerEventRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;
        ValidateExternalWorkerLeaseIdentity(request.LeaseKey, request.LeaseToken, request.WorkerId, limits);
        ValidateId(request.Type, "External worker event type", limits);
        ValidateBoundedText(request.Message, "External worker event message", limits.MaxTraceMessageChars, required: false);
        ValidateBoundedText(request.Severity, "External worker event severity", limits.MaxIdChars, required: true);
        ValidateJsonBytes(request.Details, "External worker event details", limits.MaxTraceDetailsBytes);
    }

    public static void ValidateExternalWorkerArtifactRequest(ExecutionExternalWorkerArtifactRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;
        ValidateExternalWorkerLeaseIdentity(request.LeaseKey, request.LeaseToken, request.WorkerId, limits);
        ValidateArtifactWrite(request.Artifact, limits);
    }

    public static void ValidateExternalWorkerCheckpointRequest(ExecutionExternalWorkerCheckpointRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;
        ValidateExternalWorkerLeaseIdentity(request.LeaseKey, request.LeaseToken, request.WorkerId, limits);
        ValidateCheckpointWrite(request.Checkpoint, limits);
    }

    public static void ValidateExternalWorkerCheckpointReadRequest(ExecutionExternalWorkerCheckpointReadRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;
        ValidateExternalWorkerLeaseIdentity(request.LeaseKey, request.LeaseToken, request.WorkerId, limits);
        ValidateBoundedText(request.Key, "External worker checkpoint key", limits.MaxCheckpointKeyChars, required: true);
    }

    public static void ValidateExternalWorkerCompletionRequest(ExecutionExternalWorkerCompletionRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;
        ValidateExternalWorkerLeaseIdentity(request.LeaseKey, request.LeaseToken, request.WorkerId, limits);
        ValidateRunResult(request.Result, limits);
        if (!ExecutionRunStatuses.IsTerminal(request.Result.Status))
        {
            throw new InvalidOperationException("External worker completion result must have a terminal status.");
        }
    }

    public static void ValidateExternalWorkerWaitRequest(ExecutionExternalWorkerWaitRequest request, ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ExecutionRuntimeLimits.Default;
        ValidateExternalWorkerLeaseIdentity(request.LeaseKey, request.LeaseToken, request.WorkerId, limits);
        ValidateId(request.Name, "External worker wait name", limits);
        if (request.Kind is not (ExecutionExternalWorkerWaitKinds.ExternalEvent or ExecutionExternalWorkerWaitKinds.Timer))
        {
            throw new InvalidOperationException($"External worker wait kind '{request.Kind}' is not supported.");
        }

        if (request.Kind == ExecutionExternalWorkerWaitKinds.Timer && !request.FireAtUtc.HasValue)
        {
            throw new InvalidOperationException("External worker timer wait requires fireAtUtc.");
        }

        if (request.Kind == ExecutionExternalWorkerWaitKinds.ExternalEvent && request.FireAtUtc.HasValue)
        {
            throw new InvalidOperationException("External worker external-event wait does not accept fireAtUtc.");
        }

        ValidateJsonBytes(request.Payload, "External worker wait payload", limits.MaxPayloadBytes);
    }

    public static void ValidateScope(ExecutionScope? scope, ExecutionRuntimeLimits? limits = null)
    {
        if (scope is null)
        {
            return;
        }

        limits ??= ExecutionRuntimeLimits.Default;
        ValidateId(scope.ProductId, "Execution scope product id", limits);
        ValidateId(scope.TenantId, "Execution scope tenant id", limits);
        ValidateOptionalId(scope.ServiceIdentity, "Execution scope service identity", limits);
    }

    public static ExecutionRetryPolicy NormalizeRetryPolicy(ExecutionRetryPolicy? retryPolicy, ExecutionRuntimeLimits? limits = null)
    {
        limits ??= ExecutionRuntimeLimits.Default;
        retryPolicy ??= new ExecutionRetryPolicy();
        ValidateRetryPolicy(retryPolicy, limits);

        var initial = Math.Max(0, retryPolicy.InitialDelaySeconds);
        var max = Math.Max(initial, retryPolicy.MaxDelaySeconds);
        return new ExecutionRetryPolicy
        {
            MaxAttempts = Math.Max(1, retryPolicy.MaxAttempts),
            InitialDelaySeconds = initial,
            MaxDelaySeconds = Math.Min(limits.MaxRetryDelaySeconds, max),
            BackoffMultiplier = retryPolicy.BackoffMultiplier <= 0 ? 1 : retryPolicy.BackoffMultiplier
        };
    }

    public static string? BoundText(string? value, int maxChars)
    {
        if (value is null || value.Length <= maxChars)
        {
            return value;
        }

        const string suffix = "...[truncated]";
        if (maxChars <= suffix.Length)
        {
            return value[..Math.Max(0, maxChars)];
        }

        return value[..(maxChars - suffix.Length)] + suffix;
    }

    private static void ValidateRetryPolicy(ExecutionRetryPolicy? retryPolicy, ExecutionRuntimeLimits limits)
    {
        if (retryPolicy is null)
        {
            return;
        }

        if (retryPolicy.MaxAttempts < 0 || retryPolicy.MaxAttempts > limits.MaxRetryAttempts)
        {
            throw new InvalidOperationException($"Retry max attempts must be between 0 and {limits.MaxRetryAttempts}.");
        }

        if (retryPolicy.InitialDelaySeconds < 0 || retryPolicy.InitialDelaySeconds > limits.MaxRetryDelaySeconds)
        {
            throw new InvalidOperationException($"Retry initial delay seconds must be between 0 and {limits.MaxRetryDelaySeconds}.");
        }

        if (retryPolicy.MaxDelaySeconds < 0 || retryPolicy.MaxDelaySeconds > limits.MaxRetryDelaySeconds)
        {
            throw new InvalidOperationException($"Retry max delay seconds must be between 0 and {limits.MaxRetryDelaySeconds}.");
        }

        if (retryPolicy.BackoffMultiplier < 0)
        {
            throw new InvalidOperationException("Retry backoff multiplier cannot be negative.");
        }
    }

    private static void ValidateExternalWorkerLeaseIdentity(
        string leaseKey,
        string leaseToken,
        string workerId,
        ExecutionRuntimeLimits limits)
    {
        ValidateId(leaseKey, "External worker lease key", limits);
        ValidateBoundedText(leaseToken, "External worker lease token", limits.MaxDescriptionChars, required: true);
        ValidateId(workerId, "External worker id", limits);
    }

    private static void ValidateWorkerHandlers(IReadOnlyList<string>? handlerIds, ExecutionRuntimeLimits limits)
    {
        if (handlerIds is null || handlerIds.Count == 0)
        {
            throw new InvalidOperationException("External worker handler ids are required.");
        }

        if (handlerIds.Count > limits.MaxTagCount)
        {
            throw new InvalidOperationException($"External worker handler ids cannot contain more than {limits.MaxTagCount} entries.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handlerId in handlerIds)
        {
            ValidateId(handlerId, "External worker handler id", limits);
            if (!seen.Add(handlerId.Trim()))
            {
                throw new InvalidOperationException($"External worker handler id '{handlerId}' is duplicated.");
            }
        }
    }

    private static void ValidateLeaseTtl(double ttlSeconds, ExecutionRuntimeLimits limits)
    {
        if (ttlSeconds <= 0 || ttlSeconds > limits.MaxLeaseTtlSeconds)
        {
            throw new InvalidOperationException($"External worker lease ttl seconds must be between 0 and {limits.MaxLeaseTtlSeconds}.");
        }
    }

    private static void ValidateStatus(string value, string name)
    {
        if (!ExecutionRunStatuses.IsKnown(value))
        {
            throw new InvalidOperationException($"{name} '{value}' is not supported.");
        }
    }

    private static void ValidateOptionalStatus(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        ValidateStatus(value.Trim(), name);
    }

    private static void ValidateCount(int? value, string name)
    {
        if (value < 0)
        {
            throw new InvalidOperationException($"Run {name} count cannot be negative.");
        }
    }

    private static void ValidateId(string value, string name, ExecutionRuntimeLimits limits)
    {
        ValidateBoundedText(value, name, limits.MaxIdChars, required: true);
    }

    private static void ValidateOptionalId(string? value, string name, ExecutionRuntimeLimits limits)
    {
        ValidateBoundedText(value, name, limits.MaxIdChars, required: false);
    }

    private static void ValidateBoundedText(string? value, string name, int maxChars, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                throw new InvalidOperationException($"{name} is required.");
            }

            return;
        }

        if (value.Length > maxChars)
        {
            throw new InvalidOperationException($"{name} cannot exceed {maxChars} characters.");
        }

        if (value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{name} cannot contain control characters.");
        }
    }

    private static void ValidateTags(IReadOnlyDictionary<string, string>? tags, ExecutionRuntimeLimits limits)
    {
        if (tags is null)
        {
            return;
        }

        if (tags.Count > limits.MaxTagCount)
        {
            throw new InvalidOperationException($"Tags cannot contain more than {limits.MaxTagCount} entries.");
        }

        foreach (var (key, value) in tags)
        {
            ValidateBoundedText(key, "Tag key", limits.MaxTagKeyChars, required: true);
            ValidateBoundedText(value, "Tag value", limits.MaxTagValueChars, required: false);
        }
    }

    private static void ValidateCapabilities(IReadOnlyList<string>? capabilities, ExecutionRuntimeLimits limits)
    {
        if (capabilities is null || capabilities.Count == 0)
        {
            throw new InvalidOperationException("Adapter capabilities are required.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            ValidateId(capability, "Adapter capability", limits);
            var normalized = capability.Trim();
            if (!string.Equals(capability, normalized, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Adapter capability '{capability}' must not contain leading or trailing whitespace.");
            }

            if (!seen.Add(normalized))
            {
                throw new InvalidOperationException($"Adapter capability '{capability}' is duplicated.");
            }
        }
    }

    private static void ValidateMetadata(IReadOnlyDictionary<string, string>? metadata, ExecutionRuntimeLimits limits)
    {
        if (metadata is null)
        {
            return;
        }

        if (metadata.Count > limits.MaxTagCount)
        {
            throw new InvalidOperationException($"Adapter metadata cannot contain more than {limits.MaxTagCount} entries.");
        }

        foreach (var (key, value) in metadata)
        {
            ValidateBoundedText(key, "Adapter metadata key", limits.MaxTagKeyChars, required: true);
            ValidateBoundedText(value, "Adapter metadata value", limits.MaxDescriptionChars, required: false);
        }
    }

    private static void ValidateJsonBytes(JsonNode? node, string name, int maxBytes)
    {
        if (node is null)
        {
            return;
        }

        ValidateBytes(Encoding.UTF8.GetByteCount(node.ToJsonString(ExecutionJson.Options)), name, maxBytes);
    }

    private static void ValidateBytes(int bytes, string name, int maxBytes)
    {
        if (bytes > maxBytes)
        {
            throw new InvalidOperationException($"{name} cannot exceed {maxBytes} bytes.");
        }
    }
}
