using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Execution.Temporal;

internal sealed class TemporalExecutionRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, IExecutionHandler> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExecutionPluginDescriptor> _plugins = new(StringComparer.Ordinal);
    private readonly ExecutionRuntimeLimits _limits;

    public TemporalExecutionRegistry(ExecutionRuntimeLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public void RegisterHandler(IExecutionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ExecutionContractValidator.ValidateHandlerDescriptor(handler.Descriptor, _limits);
        var handlerId = handler.Descriptor.HandlerId.Trim();
        if (!_handlers.TryAdd(handlerId, handler))
            throw new InvalidOperationException($"Execution handler '{handlerId}' is already registered.");
    }

    public void RegisterPlugin(IExecutionPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ExecutionContractValidator.ValidatePluginDescriptor(plugin.Descriptor, _limits);
        var pluginId = plugin.Descriptor.PluginId.Trim();
        if (_plugins.ContainsKey(pluginId))
            throw new InvalidOperationException($"Execution plugin '{pluginId}' is already registered.");
        foreach (var handler in plugin.Handlers) RegisterHandler(handler);
        if (!_plugins.TryAdd(pluginId, Clone(plugin.Descriptor)))
            throw new InvalidOperationException($"Execution plugin '{pluginId}' is already registered.");
    }

    public IReadOnlyList<ExecutionPluginDescriptor> ListPlugins() =>
        _plugins.Values.Select(Clone).OrderBy(item => item.PluginId, StringComparer.Ordinal).ToList();

    public IReadOnlyList<ExecutionHandlerDescriptor> ListHandlers() =>
        _handlers.Values.Select(item => Clone(item.Descriptor)).OrderBy(item => item.HandlerId, StringComparer.Ordinal).ToList();

    public IExecutionHandler? FindHandler(string handlerId) =>
        _handlers.GetValueOrDefault(handlerId.Trim());

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)
        ?? throw new InvalidOperationException($"Temporal execution registry could not clone {typeof(T).Name}.");
}

internal static class TemporalExecutionDialect
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ExecutionRuntimeAdapterDescriptor BuildAdapterDescriptor(TemporalExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var descriptor = new ExecutionRuntimeAdapterDescriptor
        {
            AdapterId = options.AdapterId,
            RuntimeKind = TemporalExecutionRuntimeKindIds.Temporal,
            DisplayName = "Temporal execution runtime",
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
            Metadata = new Dictionary<string, string>(options.ToDiagnosticMetadata(), StringComparer.Ordinal)
            {
                ["coordinatorWorkflow"] = "Vyral.RunCoordinator.v1",
                ["continueAsNewTransitionThreshold"] = "32",
                ["projectionAuthority"] = "provider_store",
                ["resumeBehavior"] = ExecutionResumePolicyBehaviors.MayReexecuteHandler,
                ["qualification"] = "prototype_unqualified"
            }
        };
        ExecutionContractValidator.ValidateAdapterDescriptor(descriptor, options.Limits);
        return descriptor;
    }

    public static ExecutionOperationalPolicy BuildOperationalPolicy(TemporalExecutionOptions options) => new()
    {
        MaxActiveRuns = int.MaxValue,
        MaxRetainedTerminalRuns = null,
        DefaultListLimit = options.DefaultListLimit,
        MaxListLimit = options.MaxListLimit,
        DefaultHistoryLimit = options.DefaultHistoryLimit,
        MaxHistoryLimit = options.MaxHistoryLimit,
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
        ConcurrencyKeyPolicy = "projection_fenced_lease",
        DefaultTraceSeverity = "info",
        RetentionScope = "projection_store_defined"
    };

    public static ExecutionResumePolicy BuildResumePolicy() => new()
    {
        Mode = ExecutionResumePolicyModes.RestartRecovery,
        InterruptedRunningBehavior = ExecutionResumePolicyBehaviors.MayReexecuteHandler,
        ScheduledWaitingBehavior = ExecutionResumePolicyBehaviors.DispatchWhenDue,
        TerminalBehavior = ExecutionResumePolicyBehaviors.NeverResume,
        PluginCheckpointBehavior = ExecutionResumePolicyBehaviors.PluginOwned,
        IdempotencyScope = "handler_plugin_payload",
        CreatesLinkedFollowUpRuns = false
    };

    public static ExecutionRun CreateRun(
        ExecutionRunRequest request,
        ExecutionHandlerDescriptor? handler,
        TemporalExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTime.UtcNow;
        var retry = ExecutionContractValidator.NormalizeRetryPolicy(request.RetryPolicy, options.Limits);
        var handlerId = request.HandlerId.Trim();
        var requestedPlugin = NormalizeOptional(request.PluginId);
        var handlerPlugin = NormalizeOptional(handler?.PluginId);
        var run = new ExecutionRun
        {
            Id = OrderedId.CreateString(),
            HandlerId = handlerId,
            PluginId = handlerPlugin ?? requestedPlugin,
            Status = request.ScheduledAtUtc is { } scheduled && scheduled > now
                ? ExecutionRunStatuses.Waiting
                : ExecutionRunStatuses.Queued,
            MaxAttempts = retry.MaxAttempts,
            RetryPolicy = retry,
            IdempotencyKey = NormalizeOptional(request.IdempotencyKey),
            CorrelationId = NormalizeOptional(request.CorrelationId) ?? OrderedId.CreateString(),
            Scope = Clone(request.Scope),
            PayloadHash = HashCanonical(request.Payload ?? new JsonObject()),
            Payload = request.Payload?.DeepClone(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ScheduledAtUtc = request.ScheduledAtUtc?.ToUniversalTime(),
            Tags = new Dictionary<string, string>(request.Tags, StringComparer.Ordinal)
        };
        if (handler is null)
        {
            Reject(run, ExecutionFailureClasses.HandlerMissing, $"Execution handler '{handlerId}' is not registered.", now);
        }
        else if (requestedPlugin is not null && handlerPlugin is not null &&
            !string.Equals(requestedPlugin, handlerPlugin, StringComparison.Ordinal))
        {
            Reject(
                run,
                ExecutionFailureClasses.PluginMismatch,
                $"Execution handler '{handlerId}' belongs to plugin '{handlerPlugin}', not '{requestedPlugin}'.",
                now);
        }
        return run;
    }

    public static string CreateRequestHash(ExecutionRun run)
    {
        var material = new JsonObject
        {
            ["handlerId"] = run.HandlerId,
            ["pluginId"] = run.PluginId,
            ["payload"] = run.Payload?.DeepClone()
        };
        return HashCanonical(material);
    }

    private static void Reject(ExecutionRun run, string failureClass, string error, DateTime now)
    {
        run.Status = ExecutionRunStatuses.Rejected;
        run.FailureClass = failureClass;
        run.Error = error;
        run.CompletedAtUtc = now;
        run.DurationMs = 0;
    }

    private static string HashCanonical(JsonNode value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString(JsonOptions));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(document.RootElement, writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static T? Clone<T>(T? value) => value is null
        ? default
        : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions);
}
