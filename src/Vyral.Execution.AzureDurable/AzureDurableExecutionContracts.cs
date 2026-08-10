using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vyral.Execution;

namespace Vyral.Execution.AzureDurable;

public sealed class AzureDurableStartCommand
{
    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = string.Empty;

    [JsonPropertyName("orchestratorName")]
    public string OrchestratorName { get; set; } = AzureDurableExecutionNames.Orchestrator;

    [JsonPropertyName("activityName")]
    public string ActivityName { get; set; } = AzureDurableExecutionNames.Activity;

    [JsonPropertyName("startActivityName")]
    public string StartActivityName { get; set; } = AzureDurableExecutionNames.StartActivity;

    [JsonPropertyName("stepActivityName")]
    public string StepActivityName { get; set; } = AzureDurableExecutionNames.StepActivity;

    [JsonPropertyName("externalEventName")]
    public string ExternalEventName { get; set; } = AzureDurableExecutionNames.ExternalEvent;

    [JsonPropertyName("request")]
    public ExecutionRunRequest Request { get; set; } = new();

    [JsonPropertyName("handler")]
    public ExecutionHandlerDescriptor Handler { get; set; } = new();

    [JsonPropertyName("retryOptions")]
    public AzureDurableRetryOptions RetryOptions { get; set; } = new();

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AzureDurableActivityCommand
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("handlerId")]
    public string HandlerId { get; set; } = string.Empty;

    [JsonPropertyName("pluginId")]
    public string? PluginId { get; set; }

    [JsonPropertyName("attempt")]
    public int Attempt { get; set; } = 1;

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// A durable wait outcome supplied by the replayed orchestration after it has received the
    /// provider timer or external event. It is persisted by the step activity before the handler
    /// is invoked again.
    /// </summary>
    [JsonPropertyName("waitOutcome")]
    public ExecutionWaitResult? WaitOutcome { get; set; }
}

public sealed class AzureDurableActivityResult
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public ExecutionRunResult Result { get; set; } = ExecutionRunResult.Succeeded();

    [JsonPropertyName("events")]
    public List<ExecutionTraceEvent> Events { get; set; } = new();

    [JsonPropertyName("artifacts")]
    public List<ExecutionArtifact> Artifacts { get; set; } = new();

    /// <summary>
    /// A handler registered a portable durable wait. This is not a terminal handler result: the
    /// orchestration must wait with the provider and replay the step with the resulting outcome.
    /// </summary>
    [JsonPropertyName("wait")]
    public AzureDurableWait? Wait { get; set; }
}

/// <summary>
/// Result of one replay-safe orchestration step. In a real Durable Functions host the step runs
/// as an activity, so all status-store I/O and handler code stay outside the replayed orchestrator.
/// </summary>
public sealed class AzureDurableOrchestrationStepResult
{
    [JsonPropertyName("run")]
    public ExecutionRun Run { get; set; } = new();

    [JsonPropertyName("wait")]
    public AzureDurableWait? Wait { get; set; }
}

/// <summary>
/// Provider-neutral data the Azure bridge needs to wait deterministically. The portable handler
/// only observes <see cref="ExecutionWaitResult"/> after it is replayed in a later activity.
/// </summary>
public sealed class AzureDurableWait
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = AzureDurableWaitKinds.ExternalEvent;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("fireAtUtc")]
    public DateTime? FireAtUtc { get; set; }

    [JsonPropertyName("timer")]
    public ExecutionTimer? Timer { get; set; }
}

public static class AzureDurableWaitKinds
{
    public const string ExternalEvent = "external_event";
    public const string Timer = "timer";
}

public sealed class AzureDurableRetryOptions
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

public sealed class AzureDurableStatusSnapshot
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("handlerId")]
    public string HandlerId { get; set; } = string.Empty;

    [JsonPropertyName("pluginId")]
    public string? PluginId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = ExecutionRunStatuses.Queued;

    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    [JsonPropertyName("progress")]
    public double? Progress { get; set; }

    [JsonPropertyName("currentStep")]
    public string? CurrentStep { get; set; }

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("details")]
    public JsonObject? Details { get; set; }
}
