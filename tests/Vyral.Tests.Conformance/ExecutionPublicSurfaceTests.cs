using Vyral.Execution;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vyral.Tests.Conformance;

public sealed class ExecutionPublicSurfaceTests
{
    [Fact]
    public void ExecutionPublicSurface_ContainsOnlyReviewedConsumerContractTypes()
    {
        var exportedTypes = typeof(IExecutionRuntime).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "Vyral.Execution")
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var expected = new[]
        {
            "DelegateExecutionHandler",
            "ExecutionArtifact",
            "ExecutionArtifactKinds",
            "ExecutionArtifactWrite",
            "ExecutionAdmission",
            "ExecutionCapabilityCatalog",
            "ExecutionCapabilityIds",
            "ExecutionCheckpoint",
            "ExecutionCheckpointWrite",
            "ExecutionContractValidator",
            "ExecutionDescriptors",
            "ExecutionDispatchReasons",
            "ExecutionDispatchRequest",
            "ExecutionEventTypes",
            "ExecutionExternalEvent",
            "ExecutionExternalEventRequest",
            "ExecutionExternalWorkerCheckpointRequest",
            "ExecutionExternalWorkerCheckpointReadRequest",
            "ExecutionExternalWorkerCompletionRequest",
            "ExecutionExternalWorkerArtifactRequest",
            "ExecutionExternalWorkerEventRequest",
            "ExecutionExternalWorkerHeartbeatRequest",
            "ExecutionExternalWorkerLease",
            "ExecutionExternalWorkerLeaseRequest",
            "ExecutionExternalWorkerReportRequest",
            "ExecutionExternalWorkerWaitKinds",
            "ExecutionExternalWorkerWaitRequest",
            "ExecutionExternalWorkerWaitResponse",
            "ExecutionFailureClasses",
            "ExecutionHandlerDescriptor",
            "ExecutionHandlerDescriptorBuilder",
            "ExecutionHistoryQuery",
            "ExecutionJson",
            "ExecutionLease",
            "ExecutionLeaseRequest",
            "ExecutionLogRecord",
            "ExecutionMaintenancePruneRequest",
            "ExecutionMaintenancePruneResult",
            "ExecutionMaintenanceDispatchReconcileRequest",
            "ExecutionMaintenanceDispatchReconcileResult",
            "ExecutionMaintenanceStatus",
            "ExecutionOperationalPolicy",
            "ExecutionPluginDescriptor",
            "ExecutionPluginDescriptorBuilder",
            "ExecutionProductPolicy",
            "ExecutionRunContextLoggingExtensions",
            "ExecutionResumePolicy",
            "ExecutionResumePolicyBehaviors",
            "ExecutionResumePolicyModes",
            "ExecutionRetryPolicy",
            "ExecutionRun",
            "ExecutionRunLifecycle",
            "ExecutionRunQuery",
            "ExecutionRunRequest",
            "ExecutionRunResult",
            "ExecutionRunStatuses",
            "ExecutionRunUpdate",
            "ExecutionScope",
            "ExecutionRuntimeAdapterDescriptor",
            "ExecutionRuntimeAdapterFactoryContext",
            "ExecutionRuntimeAdapterStatus",
            "ExecutionRuntimeLimits",
            "ExecutionTimer",
            "ExecutionTimerRequest",
            "ExecutionTraceEvent",
            "ExecutionTransitionKind",
            "ExecutionWaitOutcomes",
            "ExecutionWaitResult",
            "IExecutionHandler",
            "IExternalExecutionWorkerRuntime",
            "IExecutionPlugin",
            "IExecutionRunDispatcher",
            "IExecutionRunContext",
            "IExecutionRuntime",
            "IExecutionRuntimeAdapter",
            "IExecutionRuntimeAdapterFactory",
            "IExecutionRuntimeMaintenance",
            "StaticExecutionPlugin"
        }.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, exportedTypes);
        Assert.DoesNotContain(exportedTypes, name => name.Contains("Azure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(exportedTypes, name => name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(exportedTypes, name => name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExecutionCore_HasNoCloudProviderAssemblyDependencies()
    {
        var references = typeof(IExecutionRuntime).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name => name.Contains("Google", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.Contains("Azure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.Contains("Aws", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.Contains("Amazon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExecutionPublicSurface_PinsRuntimeInterfaceSignatures()
    {
        var signatures = new[]
            {
                typeof(IExecutionPlugin),
                typeof(IExecutionHandler),
                typeof(IExecutionRunContext),
                typeof(IExecutionRuntime),
                typeof(IExecutionRuntimeAdapter),
                typeof(IExecutionRuntimeAdapterFactory),
                typeof(IExecutionRuntimeMaintenance),
                typeof(IExecutionRunDispatcher),
                typeof(IExternalExecutionWorkerRuntime)
            }
            .SelectMany(GetInterfaceSignatures)
            .ToArray();

        var expected = new[]
        {
            "IExecutionPlugin.Descriptor: ExecutionPluginDescriptor get",
            "IExecutionPlugin.Handlers: IReadOnlyList<IExecutionHandler> get",
            "IExecutionHandler.Descriptor: ExecutionHandlerDescriptor get",
            "IExecutionHandler.ExecuteAsync(IExecutionRunContext context, CancellationToken ct): Task<ExecutionRunResult>",
            "IExecutionRunContext.Run: ExecutionRun get",
            "IExecutionRunContext.CancellationToken: CancellationToken get",
            "IExecutionRunContext.ReportAsync(ExecutionRunUpdate update, CancellationToken ct): Task<ExecutionRun>",
            "IExecutionRunContext.RecordEventAsync(String type, String message, String severity, JsonObject details, CancellationToken ct): Task",
            "IExecutionRunContext.PutArtifactAsync(ExecutionArtifactWrite artifact, CancellationToken ct): Task<ExecutionArtifact>",
            "IExecutionRunContext.PutCheckpointAsync(ExecutionCheckpointWrite checkpoint, CancellationToken ct): Task<ExecutionCheckpoint>",
            "IExecutionRunContext.GetCheckpointAsync(String key, CancellationToken ct): Task<ExecutionCheckpoint>",
            "IExecutionRunContext.TryAcquireLeaseAsync(String leaseKey, Double ttlSeconds, JsonObject metadata, CancellationToken ct): Task<ExecutionLease>",
            "IExecutionRunContext.ReleaseLeaseAsync(String leaseKey, CancellationToken ct): Task<Boolean>",
            "IExecutionRunContext.ScheduleTimerAsync(String name, DateTime fireAtUtc, JsonNode payload, CancellationToken ct): Task<ExecutionTimer>",
            "IExecutionRunContext.RaiseEventAsync(String name, JsonNode payload, CancellationToken ct): Task<ExecutionExternalEvent>",
            "IExecutionRunContext.WaitForExternalEventAsync(String name, Nullable<DateTime> timeoutAtUtc, CancellationToken ct): Task<ExecutionWaitResult>",
            "IExecutionRunContext.WaitForTimerAsync(String name, DateTime fireAtUtc, JsonNode payload, CancellationToken ct): Task<ExecutionWaitResult>",
            "IExecutionRuntime.RegisterHandler(IExecutionHandler handler): Void",
            "IExecutionRuntime.RegisterPlugin(IExecutionPlugin plugin): Void",
            "IExecutionRuntime.ListPlugins(): IReadOnlyList<ExecutionPluginDescriptor>",
            "IExecutionRuntime.ListHandlers(): IReadOnlyList<ExecutionHandlerDescriptor>",
            "IExecutionRuntime.StartRunAsync(ExecutionRunRequest request, CancellationToken ct): Task<ExecutionRun>",
            "IExecutionRuntime.GetRunAsync(String runId, Boolean includeResult, CancellationToken ct): Task<ExecutionRun>",
            "IExecutionRuntime.ListRunsAsync(ExecutionRunQuery query, CancellationToken ct): Task<IReadOnlyList<ExecutionRun>>",
            "IExecutionRuntime.CancelRunAsync(String runId, CancellationToken ct): Task<ExecutionRun>",
            "IExecutionRuntime.GetHistoryAsync(String runId, ExecutionHistoryQuery query, CancellationToken ct): Task<IReadOnlyList<ExecutionTraceEvent>>",
            "IExecutionRuntime.ListArtifactsAsync(String runId, CancellationToken ct): Task<IReadOnlyList<ExecutionArtifact>>",
            "IExecutionRuntime.GetArtifactAsync(String runId, String artifactRef, CancellationToken ct): Task<ExecutionArtifact>",
            "IExecutionRuntime.GetCheckpointAsync(String runId, String key, CancellationToken ct): Task<ExecutionCheckpoint>",
            "IExecutionRuntime.TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct): Task<ExecutionLease>",
            "IExecutionRuntime.ReleaseLeaseAsync(String leaseKey, String ownerId, CancellationToken ct): Task<Boolean>",
            "IExecutionRuntime.ScheduleTimerAsync(ExecutionTimerRequest request, CancellationToken ct): Task<ExecutionTimer>",
            "IExecutionRuntime.RaiseEventAsync(ExecutionExternalEventRequest request, CancellationToken ct): Task<ExecutionExternalEvent>",
            "IExecutionRuntimeAdapter.Adapter: ExecutionRuntimeAdapterDescriptor get",
            "IExecutionRuntimeAdapter.GetAdapterStatusAsync(CancellationToken ct): Task<ExecutionRuntimeAdapterStatus>",
            "IExecutionRuntimeAdapterFactory.Create(ExecutionRuntimeAdapterFactoryContext context): IExecutionRuntimeAdapter",
            "IExecutionRuntimeMaintenance.GetMaintenanceStatusAsync(CancellationToken ct): Task<ExecutionMaintenanceStatus>",
            "IExecutionRuntimeMaintenance.PruneAsync(ExecutionMaintenancePruneRequest request, CancellationToken ct): Task<ExecutionMaintenancePruneResult>",
            "IExecutionRuntimeMaintenance.ReconcileDispatchAsync(ExecutionMaintenanceDispatchReconcileRequest request, CancellationToken ct): Task<ExecutionMaintenanceDispatchReconcileResult>",
            "IExecutionRunDispatcher.DispatchAsync(ExecutionDispatchRequest request, CancellationToken ct): Task",
            "IExternalExecutionWorkerRuntime.RegisterExternalHandler(ExecutionHandlerDescriptor handler): Void",
            "IExternalExecutionWorkerRuntime.LeaseNextRunAsync(ExecutionExternalWorkerLeaseRequest request, CancellationToken ct): Task<ExecutionExternalWorkerLease>",
            "IExternalExecutionWorkerRuntime.HeartbeatExternalLeaseAsync(ExecutionExternalWorkerHeartbeatRequest request, CancellationToken ct): Task<ExecutionExternalWorkerLease>",
            "IExternalExecutionWorkerRuntime.ReportExternalLeaseAsync(ExecutionExternalWorkerReportRequest request, CancellationToken ct): Task<ExecutionRun>",
            "IExternalExecutionWorkerRuntime.RecordExternalLeaseEventAsync(ExecutionExternalWorkerEventRequest request, CancellationToken ct): Task",
            "IExternalExecutionWorkerRuntime.PutExternalLeaseArtifactAsync(ExecutionExternalWorkerArtifactRequest request, CancellationToken ct): Task<ExecutionArtifact>",
            "IExternalExecutionWorkerRuntime.CheckpointExternalLeaseAsync(ExecutionExternalWorkerCheckpointRequest request, CancellationToken ct): Task<ExecutionCheckpoint>",
            "IExternalExecutionWorkerRuntime.GetExternalLeaseCheckpointAsync(ExecutionExternalWorkerCheckpointReadRequest request, CancellationToken ct): Task<ExecutionCheckpoint>",
            "IExternalExecutionWorkerRuntime.WaitExternalLeaseAsync(ExecutionExternalWorkerWaitRequest request, CancellationToken ct): Task<ExecutionExternalWorkerWaitResponse>",
            "IExternalExecutionWorkerRuntime.CompleteExternalLeaseAsync(ExecutionExternalWorkerCompletionRequest request, CancellationToken ct): Task<ExecutionRun>"
        };

        Assert.Equal(expected, signatures);
    }

    [Fact]
    public void ExecutionPublicSurface_PinsDtoJsonPropertyNames()
    {
        var expected = new Dictionary<Type, string[]>
        {
            [typeof(ExecutionPluginDescriptor)] = new[] { "pluginId", "name", "version", "handlers" },
            [typeof(ExecutionHandlerDescriptor)] = new[] { "handlerId", "pluginId", "displayName", "description", "maxAttempts", "concurrencyKey", "tags" },
            [typeof(ExecutionRuntimeAdapterDescriptor)] = new[] { "adapterId", "runtimeKind", "displayName", "version", "capabilities", "metadata" },
            [typeof(ExecutionRuntimeAdapterStatus)] = new[] { "adapter", "available", "status", "checkedAtUtc", "activeRuns", "operationalPolicy", "resumePolicy", "details" },
            [typeof(ExecutionDispatchRequest)] = new[] { "runId", "reason", "scheduledAtUtc" },
            [typeof(ExecutionOperationalPolicy)] = new[] { "maxActiveRuns", "maxRetainedTerminalRuns", "defaultListLimit", "maxListLimit", "defaultHistoryLimit", "maxHistoryLimit", "maxPayloadBytes", "maxResultBytes", "maxStatusDetailsBytes", "maxArtifactBytes", "maxArtifactInlineBytes", "maxTraceMessageChars", "maxTraceDetailsBytes", "maxRetryAttempts", "maxRetryDelaySeconds", "maxLeaseTtlSeconds", "concurrencyKeyPolicy", "concurrencyRetryDelayMs", "defaultTraceSeverity", "retentionScope" },
            [typeof(ExecutionResumePolicy)] = new[] { "mode", "interruptedRunningBehavior", "scheduledWaitingBehavior", "terminalBehavior", "pluginCheckpointBehavior", "idempotencyScope", "createsLinkedFollowUpRuns" },
            [typeof(ExecutionRetryPolicy)] = new[] { "maxAttempts", "initialDelaySeconds", "maxDelaySeconds", "backoffMultiplier" },
            [typeof(ExecutionRunRequest)] = new[] { "handlerId", "pluginId", "payload", "idempotencyKey", "correlationId", "scope", "scheduledAtUtc", "retryPolicy", "tags" },
            [typeof(ExecutionRun)] = new[] { "admission", "id", "handlerId", "pluginId", "status", "attempt", "maxAttempts", "retryPolicy", "idempotencyKey", "correlationId", "scope", "payloadHash", "payload", "createdAtUtc", "scheduledAtUtc", "startedAtUtc", "updatedAtUtc", "completedAtUtc", "durationMs", "cancellationRequested", "requested", "attempted", "succeeded", "failed", "progress", "currentStep", "failureClass", "error", "result", "statusDetails", "tags" },
            [typeof(ExecutionScope)] = new[] { "productId", "tenantId", "serviceIdentity" },
            [typeof(ExecutionProductPolicy)] = new[] { "productId", "allowedHandlerIds", "allowedTenantIds", "allowedServiceIdentities", "maxPayloadBytes", "artifactPrefix", "redactedJsonPropertyNames" },
            [typeof(ExecutionRunUpdate)] = new[] { "status", "requested", "attempted", "succeeded", "failed", "progress", "currentStep", "failureClass", "error", "result", "statusDetails" },
            [typeof(ExecutionRunResult)] = new[] { "status", "result", "failureClass", "error", "statusDetails" },
            [typeof(ExecutionTraceEvent)] = new[] { "id", "sequenceId", "runId", "type", "timestampUtc", "attempt", "stepId", "status", "severity", "message", "details", "context" },
            [typeof(ExecutionArtifactWrite)] = new[] { "name", "kind", "mediaType", "text", "content", "uri", "metadata" },
            [typeof(ExecutionArtifact)] = new[] { "id", "runId", "name", "kind", "mediaType", "contentHash", "sizeBytes", "text", "content", "uri", "createdAtUtc", "metadata" },
            [typeof(ExecutionCheckpointWrite)] = new[] { "key", "content", "metadata" },
            [typeof(ExecutionCheckpoint)] = new[] { "runId", "key", "contentHash", "updatedAtUtc", "content", "metadata" },
            [typeof(ExecutionRunQuery)] = new[] { "handlerId", "pluginId", "status", "correlationId", "idempotencyKey", "createdAfterUtc", "createdBeforeUtc", "updatedAfterUtc", "updatedBeforeUtc", "tags", "includeResult", "limit" },
            [typeof(ExecutionHistoryQuery)] = new[] { "limit" },
            [typeof(ExecutionLeaseRequest)] = new[] { "leaseKey", "ownerId", "runId", "ttlSeconds", "metadata" },
            [typeof(ExecutionLease)] = new[] { "leaseKey", "ownerId", "runId", "acquiredAtUtc", "expiresAtUtc", "metadata" },
            [typeof(ExecutionTimerRequest)] = new[] { "name", "runId", "fireAtUtc", "payload" },
            [typeof(ExecutionTimer)] = new[] { "id", "name", "runId", "fireAtUtc", "createdAtUtc", "payload" },
            [typeof(ExecutionExternalEventRequest)] = new[] { "name", "runId", "payload" },
            [typeof(ExecutionExternalEvent)] = new[] { "id", "name", "runId", "raisedAtUtc", "payload" },
            [typeof(ExecutionExternalWorkerLeaseRequest)] = new[] { "workerId", "handlerIds", "runId", "ttlSeconds" },
            [typeof(ExecutionExternalWorkerLease)] = new[] { "leaseKey", "leaseToken", "workerId", "run", "acquiredAtUtc", "expiresAtUtc" },
            [typeof(ExecutionExternalWorkerHeartbeatRequest)] = new[] { "leaseKey", "leaseToken", "workerId", "ttlSeconds" },
            [typeof(ExecutionExternalWorkerReportRequest)] = new[] { "leaseKey", "leaseToken", "workerId", "update" },
            [typeof(ExecutionExternalWorkerEventRequest)] = new[] { "leaseKey", "leaseToken", "workerId", "type", "message", "severity", "details" },
            [typeof(ExecutionExternalWorkerArtifactRequest)] = new[] { "leaseKey", "leaseToken", "workerId", "artifact" },
            [typeof(ExecutionExternalWorkerCheckpointRequest)] = new[] { "leaseKey", "leaseToken", "workerId", "checkpoint" },
            [typeof(ExecutionExternalWorkerCheckpointReadRequest)] = new[] { "leaseKey", "leaseToken", "workerId", "key" },
            [typeof(ExecutionExternalWorkerCompletionRequest)] = new[] { "leaseKey", "leaseToken", "workerId", "result" },
            [typeof(ExecutionExternalWorkerWaitRequest)] = new[] { "leaseKey", "leaseToken", "workerId", "kind", "name", "timeoutAtUtc", "fireAtUtc", "payload" },
            [typeof(ExecutionExternalWorkerWaitResponse)] = new[] { "run", "suspended", "outcome" },
            [typeof(ExecutionWaitResult)] = new[] { "name", "outcome", "event", "timer" },
            [typeof(ExecutionMaintenanceStatus)] = new[] { "adapterId", "runtimeKind", "checkedAtUtc", "retentionScope", "maxRetainedTerminalRuns", "runCounts", "rowCounts", "artifactDirectory", "artifactDirectoryCount", "artifactFileCount", "artifactBytes" },
            [typeof(ExecutionMaintenancePruneRequest)] = new[] { "dryRun", "retainTerminalRuns" },
            [typeof(ExecutionMaintenancePruneResult)] = new[] { "dryRun", "retainTerminalRuns", "prunedAtUtc", "runIds", "runs", "events", "artifacts", "checkpoints", "timers", "externalEvents", "leases", "artifactDirectories" },
            [typeof(ExecutionMaintenanceDispatchReconcileRequest)] = new[] { "dryRun", "limit" },
            [typeof(ExecutionMaintenanceDispatchReconcileResult)] = new[] { "dryRun", "limit", "reconciledAtUtc", "candidateRunIds", "dispatched", "failures" }
        };

        foreach (var (type, propertyNames) in expected)
        {
            Assert.Equal(propertyNames, GetJsonPropertyNames(type));
        }
    }

    private static IEnumerable<string> GetInterfaceSignatures(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .OrderBy(property => property.MetadataToken))
        {
            var accessors = string.Join(
                " ",
                new[] { property.GetMethod is null ? null : "get", property.SetMethod is null ? null : "set" }
                    .Where(accessor => accessor is not null));
            yield return $"{type.Name}.{property.Name}: {FormatType(property.PropertyType)} {accessors}";
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(method => !method.IsSpecialName)
                     .OrderBy(method => method.MetadataToken))
        {
            var parameters = string.Join(", ", method.GetParameters().Select(FormatParameter));
            yield return $"{type.Name}.{method.Name}({parameters}): {FormatType(method.ReturnType)}";
        }
    }

    private static string[] GetJsonPropertyNames(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .OrderBy(property => property.MetadataToken)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
            .ToArray();
    }

    private static string FormatParameter(ParameterInfo parameter)
    {
        return $"{FormatType(parameter.ParameterType)} {parameter.Name}";
    }

    private static string FormatType(Type type)
    {
        if (type == typeof(void))
        {
            return "Void";
        }

        if (type.IsGenericType)
        {
            var genericName = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
            return $"{genericName}<{string.Join(", ", type.GetGenericArguments().Select(FormatType))}>";
        }

        if (type.IsArray)
        {
            return $"{FormatType(type.GetElementType()!)}[]";
        }

        return type == typeof(JsonObject) || type == typeof(JsonNode)
            ? type.Name
            : type.Name;
    }
}
