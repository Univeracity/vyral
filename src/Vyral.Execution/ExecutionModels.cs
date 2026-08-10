using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vyral.Primitives;

namespace Vyral.Execution;

public sealed class ExecutionPluginDescriptor
{
    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("handlers")]
    public List<ExecutionHandlerDescriptor> Handlers { get; set; } = new();
}

public sealed class ExecutionHandlerDescriptor
{
    [JsonPropertyName("handlerId")]
    public string HandlerId { get; set; } = string.Empty;

    [JsonPropertyName("pluginId")]
    public string? PluginId { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("maxAttempts")]
    public int MaxAttempts { get; set; } = 1;

    [JsonPropertyName("concurrencyKey")]
    public string? ConcurrencyKey { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ExecutionRuntimeAdapterDescriptor
{
    [JsonPropertyName("adapterId")]
    public string AdapterId { get; set; } = string.Empty;

    [JsonPropertyName("runtimeKind")]
    public string RuntimeKind { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Provider-neutral host inputs for a configured runtime factory. Keys are configuration paths;
/// providers own the keys they consume and must reject incomplete or invalid settings.
/// </summary>
public sealed class ExecutionRuntimeAdapterFactoryContext
{
    public required string AdapterId { get; init; }
    public required IReadOnlyDictionary<string, string?> Settings { get; init; }
}

public sealed class ExecutionRuntimeAdapterStatus
{
    [JsonPropertyName("adapter")]
    public ExecutionRuntimeAdapterDescriptor Adapter { get; set; } = new();

    [JsonPropertyName("available")]
    public bool Available { get; set; } = true;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    [JsonPropertyName("checkedAtUtc")]
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("activeRuns")]
    public int? ActiveRuns { get; set; }

    [JsonPropertyName("operationalPolicy")]
    public ExecutionOperationalPolicy? OperationalPolicy { get; set; }

    [JsonPropertyName("resumePolicy")]
    public ExecutionResumePolicy? ResumePolicy { get; set; }

    [JsonPropertyName("details")]
    public JsonObject? Details { get; set; }
}

/// <summary>
/// A provider-neutral request to make a durable run eligible for worker execution. The durable
/// run remains the source of truth; dispatch payloads must not carry mutable run state.
/// </summary>
public sealed class ExecutionDispatchRequest
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = ExecutionDispatchReasons.RunReady;

    [JsonPropertyName("scheduledAtUtc")]
    public DateTime? ScheduledAtUtc { get; set; }
}

public sealed class ExecutionOperationalPolicy
{
    [JsonPropertyName("maxActiveRuns")]
    public int MaxActiveRuns { get; set; }

    [JsonPropertyName("maxRetainedTerminalRuns")]
    public int? MaxRetainedTerminalRuns { get; set; }

    [JsonPropertyName("defaultListLimit")]
    public int DefaultListLimit { get; set; }

    [JsonPropertyName("maxListLimit")]
    public int MaxListLimit { get; set; }

    [JsonPropertyName("defaultHistoryLimit")]
    public int DefaultHistoryLimit { get; set; }

    [JsonPropertyName("maxHistoryLimit")]
    public int MaxHistoryLimit { get; set; }

    [JsonPropertyName("maxPayloadBytes")]
    public int MaxPayloadBytes { get; set; }

    [JsonPropertyName("maxResultBytes")]
    public int MaxResultBytes { get; set; }

    [JsonPropertyName("maxStatusDetailsBytes")]
    public int MaxStatusDetailsBytes { get; set; }

    [JsonPropertyName("maxArtifactBytes")]
    public int MaxArtifactBytes { get; set; }

    [JsonPropertyName("maxArtifactInlineBytes")]
    public int MaxArtifactInlineBytes { get; set; }

    [JsonPropertyName("maxTraceMessageChars")]
    public int MaxTraceMessageChars { get; set; }

    [JsonPropertyName("maxTraceDetailsBytes")]
    public int MaxTraceDetailsBytes { get; set; }

    [JsonPropertyName("maxRetryAttempts")]
    public int MaxRetryAttempts { get; set; }

    [JsonPropertyName("maxRetryDelaySeconds")]
    public double MaxRetryDelaySeconds { get; set; }

    [JsonPropertyName("maxLeaseTtlSeconds")]
    public double MaxLeaseTtlSeconds { get; set; }

    [JsonPropertyName("concurrencyKeyPolicy")]
    public string ConcurrencyKeyPolicy { get; set; } = "adapter_defined";

    [JsonPropertyName("concurrencyRetryDelayMs")]
    public int? ConcurrencyRetryDelayMs { get; set; }

    [JsonPropertyName("defaultTraceSeverity")]
    public string DefaultTraceSeverity { get; set; } = "info";

    [JsonPropertyName("retentionScope")]
    public string RetentionScope { get; set; } = "run_owned";
}

public sealed class ExecutionResumePolicy
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = ExecutionResumePolicyModes.RestartRecovery;

    [JsonPropertyName("interruptedRunningBehavior")]
    public string InterruptedRunningBehavior { get; set; } = ExecutionResumePolicyBehaviors.MayReexecuteHandler;

    [JsonPropertyName("scheduledWaitingBehavior")]
    public string ScheduledWaitingBehavior { get; set; } = ExecutionResumePolicyBehaviors.DispatchWhenDue;

    [JsonPropertyName("terminalBehavior")]
    public string TerminalBehavior { get; set; } = ExecutionResumePolicyBehaviors.NeverResume;

    [JsonPropertyName("pluginCheckpointBehavior")]
    public string PluginCheckpointBehavior { get; set; } = ExecutionResumePolicyBehaviors.PluginOwned;

    [JsonPropertyName("idempotencyScope")]
    public string IdempotencyScope { get; set; } = "handler_plugin_payload";

    [JsonPropertyName("createsLinkedFollowUpRuns")]
    public bool CreatesLinkedFollowUpRuns { get; set; }
}

public sealed class ExecutionRetryPolicy
{
    [JsonPropertyName("maxAttempts")]
    public int MaxAttempts { get; set; } = 1;

    [JsonPropertyName("initialDelaySeconds")]
    public double InitialDelaySeconds { get; set; } = 1;

    [JsonPropertyName("maxDelaySeconds")]
    public double MaxDelaySeconds { get; set; } = 60;

    [JsonPropertyName("backoffMultiplier")]
    public double BackoffMultiplier { get; set; } = 2;
}

public sealed class ExecutionRunRequest
{
    [JsonPropertyName("handlerId")]
    public string HandlerId { get; set; } = string.Empty;

    [JsonPropertyName("pluginId")]
    public string? PluginId { get; set; }

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("scope")]
    public ExecutionScope? Scope { get; set; }

    [JsonPropertyName("scheduledAtUtc")]
    public DateTime? ScheduledAtUtc { get; set; }

    [JsonPropertyName("retryPolicy")]
    public ExecutionRetryPolicy RetryPolicy { get; set; } = new();

    [JsonPropertyName("tags")]
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ExecutionRun
{
    [JsonPropertyName("admission")]
    public AdmissionReceipt Admission { get; set; } = new();

    [JsonIgnore]
    public bool AdmissionReplayed { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("handlerId")]
    public string HandlerId { get; set; } = string.Empty;

    [JsonPropertyName("pluginId")]
    public string? PluginId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = ExecutionRunStatuses.Queued;

    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    [JsonPropertyName("maxAttempts")]
    public int MaxAttempts { get; set; } = 1;

    [JsonPropertyName("retryPolicy")]
    public ExecutionRetryPolicy RetryPolicy { get; set; } = new();

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public ExecutionScope? Scope { get; set; }

    [JsonPropertyName("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("scheduledAtUtc")]
    public DateTime? ScheduledAtUtc { get; set; }

    [JsonPropertyName("startedAtUtc")]
    public DateTime? StartedAtUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("completedAtUtc")]
    public DateTime? CompletedAtUtc { get; set; }

    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; set; }

    [JsonPropertyName("cancellationRequested")]
    public bool CancellationRequested { get; set; }

    [JsonPropertyName("requested")]
    public int? Requested { get; set; }

    [JsonPropertyName("attempted")]
    public int? Attempted { get; set; }

    [JsonPropertyName("succeeded")]
    public int? Succeeded { get; set; }

    [JsonPropertyName("failed")]
    public int? Failed { get; set; }

    [JsonPropertyName("progress")]
    public double? Progress { get; set; }

    [JsonPropertyName("currentStep")]
    public string? CurrentStep { get; set; }

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("result")]
    public JsonNode? Result { get; set; }

    [JsonPropertyName("statusDetails")]
    public JsonObject? StatusDetails { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Product and tenant identity carried with a run. Adapters enforce this against their configured
/// execution-boundary policy; callers must not treat it as an authenticated principal by itself.
/// </summary>
public sealed class ExecutionScope
{
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("serviceIdentity")]
    public string? ServiceIdentity { get; set; }
}

/// <summary>
/// Provider-neutral boundary policy for one execution product. Empty allowlists impose no
/// additional restriction; when a host configures any policy, every new run must carry a
/// recognized product and tenant scope. Authentication remains a host concern: adapters trust
/// only the service identity that the host has already verified and bound to the run scope.
/// </summary>
public class ExecutionProductPolicy
{
    [JsonPropertyName("productId")]
    public required string ProductId { get; init; }

    [JsonPropertyName("allowedHandlerIds")]
    public IReadOnlySet<string> AllowedHandlerIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    [JsonPropertyName("allowedTenantIds")]
    public IReadOnlySet<string> AllowedTenantIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    [JsonPropertyName("allowedServiceIdentities")]
    public IReadOnlySet<string> AllowedServiceIdentities { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    [JsonPropertyName("maxPayloadBytes")]
    public int? MaxPayloadBytes { get; init; }

    [JsonPropertyName("artifactPrefix")]
    public string? ArtifactPrefix { get; init; }

    [JsonPropertyName("redactedJsonPropertyNames")]
    public IReadOnlySet<string> RedactedJsonPropertyNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed class ExecutionRunUpdate
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("requested")]
    public int? Requested { get; set; }

    [JsonPropertyName("attempted")]
    public int? Attempted { get; set; }

    [JsonPropertyName("succeeded")]
    public int? Succeeded { get; set; }

    [JsonPropertyName("failed")]
    public int? Failed { get; set; }

    [JsonPropertyName("progress")]
    public double? Progress { get; set; }

    [JsonPropertyName("currentStep")]
    public string? CurrentStep { get; set; }

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("result")]
    public JsonNode? Result { get; set; }

    [JsonPropertyName("statusDetails")]
    public JsonObject? StatusDetails { get; set; }
}

public sealed class ExecutionRunResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = ExecutionRunStatuses.Succeeded;

    [JsonPropertyName("result")]
    public JsonNode? Result { get; set; }

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("statusDetails")]
    public JsonObject? StatusDetails { get; set; }

    public static ExecutionRunResult Succeeded(JsonNode? result = null, JsonObject? statusDetails = null)
    {
        return new ExecutionRunResult
        {
            Status = ExecutionRunStatuses.Succeeded,
            Result = result,
            StatusDetails = statusDetails
        };
    }

    public static ExecutionRunResult Failed(string failureClass, string error, JsonNode? result = null)
    {
        return new ExecutionRunResult
        {
            Status = ExecutionRunStatuses.Failed,
            FailureClass = failureClass,
            Error = error,
            Result = result
        };
    }

    public static ExecutionRunResult Cancelled(JsonNode? result = null)
    {
        return new ExecutionRunResult
        {
            Status = ExecutionRunStatuses.Cancelled,
            FailureClass = ExecutionFailureClasses.Cancelled,
            Error = "Execution run was cancelled.",
            Result = result
        };
    }
}

public sealed class ExecutionTraceEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = OrderedId.CreateString();

    [JsonPropertyName("sequenceId")]
    public string SequenceId { get; set; } = OrderedId.CreateString();

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = ExecutionEventTypes.RunStatus;

    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    [JsonPropertyName("stepId")]
    public string? StepId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("details")]
    public JsonObject? Details { get; set; }

    [JsonPropertyName("context")]
    public Dictionary<string, string> Context { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ExecutionArtifactWrite
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ExecutionArtifactKinds.Json;

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("content")]
    public JsonNode? Content { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ExecutionArtifact
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = OrderedId.CreateString();

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ExecutionArtifactKinds.Json;

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("content")]
    public JsonNode? Content { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ExecutionCheckpointWrite
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public JsonNode? Content { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ExecutionCheckpoint
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("content")]
    public JsonNode? Content { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ExecutionRunQuery
{
    [JsonPropertyName("handlerId")]
    public string? HandlerId { get; set; }

    [JsonPropertyName("pluginId")]
    public string? PluginId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    [JsonPropertyName("createdAfterUtc")]
    public DateTime? CreatedAfterUtc { get; set; }

    [JsonPropertyName("createdBeforeUtc")]
    public DateTime? CreatedBeforeUtc { get; set; }

    [JsonPropertyName("updatedAfterUtc")]
    public DateTime? UpdatedAfterUtc { get; set; }

    [JsonPropertyName("updatedBeforeUtc")]
    public DateTime? UpdatedBeforeUtc { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("includeResult")]
    public bool IncludeResult { get; set; } = true;

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

public sealed class ExecutionHistoryQuery
{
    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

public sealed class ExecutionLeaseRequest
{
    [JsonPropertyName("leaseKey")]
    public string LeaseKey { get; set; } = string.Empty;

    [JsonPropertyName("ownerId")]
    public string OwnerId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("ttlSeconds")]
    public double TtlSeconds { get; set; } = 60;

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }
}

public sealed class ExecutionLease
{
    [JsonPropertyName("leaseKey")]
    public string LeaseKey { get; set; } = string.Empty;

    [JsonPropertyName("ownerId")]
    public string OwnerId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("acquiredAtUtc")]
    public DateTime AcquiredAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("expiresAtUtc")]
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddMinutes(1);

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }
}

public sealed class ExecutionTimerRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("fireAtUtc")]
    public DateTime FireAtUtc { get; set; }

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }
}

public sealed class ExecutionTimer
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = OrderedId.CreateString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("fireAtUtc")]
    public DateTime FireAtUtc { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }
}

public sealed class ExecutionExternalEventRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }
}

public sealed class ExecutionExternalEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = OrderedId.CreateString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("raisedAtUtc")]
    public DateTime RaisedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }
}

/// <summary>
/// Durable result returned by a handler-side external-event or timer wait after its replay.
/// Exactly one of Event or Timer is set for a successful wake; timed_out has neither.
/// </summary>
public sealed class ExecutionWaitResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = ExecutionWaitOutcomes.ExternalEvent;

    [JsonPropertyName("event")]
    public ExecutionExternalEvent? Event { get; set; }

    [JsonPropertyName("timer")]
    public ExecutionTimer? Timer { get; set; }
}

/// <summary>
/// A request from an out-of-process worker to atomically claim one due run. Supplying RunId is
/// useful for a Cloud Tasks delivery; omitting it lets a worker poll from its allowed handlers.
/// </summary>
public sealed class ExecutionExternalWorkerLeaseRequest
{
    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("handlerIds")]
    public List<string> HandlerIds { get; set; } = new();

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("ttlSeconds")]
    public double TtlSeconds { get; set; } = 60;
}

/// <summary>
/// An opaque, bearer-style lease returned only to the worker that won the atomic claim. Hosts
/// must not log LeaseToken.
/// </summary>
public sealed class ExecutionExternalWorkerLease
{
    [JsonPropertyName("leaseKey")]
    public string LeaseKey { get; set; } = string.Empty;

    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("run")]
    public ExecutionRun Run { get; set; } = new();

    [JsonPropertyName("acquiredAtUtc")]
    public DateTime AcquiredAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("expiresAtUtc")]
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddMinutes(1);
}

public sealed class ExecutionExternalWorkerHeartbeatRequest
{
    [JsonPropertyName("leaseKey")]
    public string LeaseKey { get; set; } = string.Empty;

    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("ttlSeconds")]
    public double TtlSeconds { get; set; } = 60;
}

/// <summary>Durable progress/status update issued by an external worker holding a lease.</summary>
public sealed class ExecutionExternalWorkerReportRequest
{
    [JsonPropertyName("leaseKey")]
    public string LeaseKey { get; set; } = string.Empty;

    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("update")]
    public ExecutionRunUpdate Update { get; set; } = new();
}

/// <summary>Durable trace event issued by an external worker holding a lease.</summary>
public sealed class ExecutionExternalWorkerEventRequest
{
    [JsonPropertyName("leaseKey")]
    public string LeaseKey { get; set; } = string.Empty;

    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = ExecutionEventTypes.Log;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";

    [JsonPropertyName("details")]
    public JsonObject? Details { get; set; }
}

/// <summary>Artifact write issued by an external worker holding a lease.</summary>
public sealed class ExecutionExternalWorkerArtifactRequest
{
    [JsonPropertyName("leaseKey")]
    public string LeaseKey { get; set; } = string.Empty;

    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("artifact")]
    public ExecutionArtifactWrite Artifact { get; set; } = new();
}

public sealed class ExecutionExternalWorkerCheckpointRequest
{
    [JsonPropertyName("leaseKey")]
    public string LeaseKey { get; set; } = string.Empty;

    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("checkpoint")]
    public ExecutionCheckpointWrite Checkpoint { get; set; } = new();
}

/// <summary>Reads a durable checkpoint while an external worker holds a lease.</summary>
public sealed class ExecutionExternalWorkerCheckpointReadRequest
{
    [JsonPropertyName("leaseKey")]
    public string LeaseKey { get; set; } = string.Empty;

    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;
}

public sealed class ExecutionExternalWorkerCompletionRequest
{
    [JsonPropertyName("leaseKey")]
    public string LeaseKey { get; set; } = string.Empty;

    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public ExecutionRunResult Result { get; set; } = new();
}

public static class ExecutionExternalWorkerWaitKinds
{
    public const string ExternalEvent = "external_event";
    public const string Timer = "timer";
}

/// <summary>
/// Registers or resumes a durable wait while an external worker holds a lease. A replaying
/// worker repeats the same request: if a durable outcome is available, the response is not
/// suspended and includes it; otherwise the lease is released into a waiting run.
/// </summary>
public sealed class ExecutionExternalWorkerWaitRequest
{
    [JsonPropertyName("leaseKey")]
    public string LeaseKey { get; set; } = string.Empty;

    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;

    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ExecutionExternalWorkerWaitKinds.ExternalEvent;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("timeoutAtUtc")]
    public DateTime? TimeoutAtUtc { get; set; }

    [JsonPropertyName("fireAtUtc")]
    public DateTime? FireAtUtc { get; set; }

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }
}

public sealed class ExecutionExternalWorkerWaitResponse
{
    [JsonPropertyName("run")]
    public ExecutionRun Run { get; set; } = new();

    [JsonPropertyName("suspended")]
    public bool Suspended { get; set; }

    [JsonPropertyName("outcome")]
    public ExecutionWaitResult? Outcome { get; set; }
}

public sealed class ExecutionMaintenanceStatus
{
    [JsonPropertyName("adapterId")]
    public string AdapterId { get; set; } = string.Empty;

    [JsonPropertyName("runtimeKind")]
    public string RuntimeKind { get; set; } = string.Empty;

    [JsonPropertyName("checkedAtUtc")]
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("retentionScope")]
    public string RetentionScope { get; set; } = "run_owned";

    [JsonPropertyName("maxRetainedTerminalRuns")]
    public int? MaxRetainedTerminalRuns { get; set; }

    [JsonPropertyName("runCounts")]
    public Dictionary<string, int> RunCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("rowCounts")]
    public Dictionary<string, int> RowCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("artifactDirectory")]
    public string? ArtifactDirectory { get; set; }

    [JsonPropertyName("artifactDirectoryCount")]
    public int? ArtifactDirectoryCount { get; set; }

    [JsonPropertyName("artifactFileCount")]
    public int? ArtifactFileCount { get; set; }

    [JsonPropertyName("artifactBytes")]
    public long? ArtifactBytes { get; set; }
}

public sealed class ExecutionMaintenancePruneRequest
{
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; } = true;

    [JsonPropertyName("retainTerminalRuns")]
    public int? RetainTerminalRuns { get; set; }
}

public sealed class ExecutionMaintenancePruneResult
{
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("retainTerminalRuns")]
    public int RetainTerminalRuns { get; set; }

    [JsonPropertyName("prunedAtUtc")]
    public DateTime PrunedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("runIds")]
    public List<string> RunIds { get; set; } = new();

    [JsonPropertyName("runs")]
    public int Runs { get; set; }

    [JsonPropertyName("events")]
    public int Events { get; set; }

    [JsonPropertyName("artifacts")]
    public int Artifacts { get; set; }

    [JsonPropertyName("checkpoints")]
    public int Checkpoints { get; set; }

    [JsonPropertyName("timers")]
    public int Timers { get; set; }

    [JsonPropertyName("externalEvents")]
    public int ExternalEvents { get; set; }

    [JsonPropertyName("leases")]
    public int Leases { get; set; }

    [JsonPropertyName("artifactDirectories")]
    public int ArtifactDirectories { get; set; }
}

/// <summary>
/// Requests idempotent redelivery of durable work that remains runnable after a dispatch
/// interruption. Remote adapters should persist runnable state before enqueueing and implement
/// this operation through their durable runnable-work index or transactional outbox.
/// </summary>
public sealed class ExecutionMaintenanceDispatchReconcileRequest
{
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

public sealed class ExecutionMaintenanceDispatchReconcileResult
{
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("reconciledAtUtc")]
    public DateTime ReconciledAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("candidateRunIds")]
    public List<string> CandidateRunIds { get; set; } = new();

    [JsonPropertyName("dispatched")]
    public int Dispatched { get; set; }

    [JsonPropertyName("failures")]
    public Dictionary<string, string> Failures { get; set; } = new(StringComparer.Ordinal);
}
