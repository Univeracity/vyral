using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;

namespace Vyral.Tests.Conformance;

public sealed class ExecutionContractSnapshotTests
{
    [Fact]
    public void ExecutionContractSnapshots_PinRequestDescriptorAndRunShapes()
    {
        var request = new ExecutionRunRequest
        {
            HandlerId = "sample.external.work.audit",
            PluginId = "sample.external.work",
            IdempotencyKey = "sample:alpha",
            CorrelationId = "corr-1",
            ScheduledAtUtc = new DateTime(2026, 06, 24, 12, 30, 00, DateTimeKind.Utc),
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("alpha", "beta")
            },
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = 2,
                InitialDelaySeconds = 1.5,
                MaxDelaySeconds = 10,
                BackoffMultiplier = 2
            },
            Tags =
            {
                ["tenant"] = "demo"
            }
        };

        var plugin = new ExecutionPluginDescriptor
        {
            PluginId = "sample.external.work",
            Name = "External work sample",
            Version = "1.0.0",
            Handlers =
            {
                new ExecutionHandlerDescriptor
                {
                    HandlerId = "sample.external.work.audit",
                    PluginId = "sample.external.work",
                    DisplayName = "Audit external work items",
                    Description = "Consumer-owned handler",
                    MaxAttempts = 1,
                    ConcurrencyKey = "sample.external.work",
                    Tags =
                    {
                        ["sample"] = "external"
                    }
                }
            }
        };

        var run = new ExecutionRun
        {
            Id = "000000000000000000000000000000000001",
            HandlerId = "sample.external.work.audit",
            PluginId = "sample.external.work",
            Status = ExecutionRunStatuses.Succeeded,
            Attempt = 1,
            MaxAttempts = 2,
            RetryPolicy = request.RetryPolicy,
            IdempotencyKey = "sample:alpha",
            CorrelationId = "corr-1",
            PayloadHash = "sha256:abc",
            Payload = request.Payload!.DeepClone(),
            CreatedAtUtc = new DateTime(2026, 06, 24, 12, 00, 00, DateTimeKind.Utc),
            ScheduledAtUtc = new DateTime(2026, 06, 24, 12, 30, 00, DateTimeKind.Utc),
            StartedAtUtc = new DateTime(2026, 06, 24, 12, 30, 01, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 06, 24, 12, 30, 03, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 06, 24, 12, 30, 03, DateTimeKind.Utc),
            DurationMs = 2000,
            Requested = 2,
            Attempted = 2,
            Succeeded = 2,
            Failed = 0,
            Progress = 1,
            CurrentStep = "complete",
            Result = new JsonObject
            {
                ["itemCount"] = 2
            },
            StatusDetails = new JsonObject
            {
                ["phase"] = "completed"
            },
            Tags =
            {
                ["tenant"] = "demo"
            }
        };
        ExecutionAdmission.Attach(
            run,
            "startExecutionRun",
            "/execution/runs/000000000000000000000000000000000001");

        AssertJsonSnapshot(
            request,
            """
            {"handlerId":"sample.external.work.audit","pluginId":"sample.external.work","payload":{"items":["alpha","beta"]},"idempotencyKey":"sample:alpha","correlationId":"corr-1","scheduledAtUtc":"2026-06-24T12:30:00Z","retryPolicy":{"maxAttempts":2,"initialDelaySeconds":1.5,"maxDelaySeconds":10,"backoffMultiplier":2},"tags":{"tenant":"demo"}}
            """);
        AssertJsonSnapshot(
            plugin,
            """
            {"pluginId":"sample.external.work","name":"External work sample","version":"1.0.0","handlers":[{"handlerId":"sample.external.work.audit","pluginId":"sample.external.work","displayName":"Audit external work items","description":"Consumer-owned handler","maxAttempts":1,"concurrencyKey":"sample.external.work","tags":{"sample":"external"}}]}
            """);
        AssertJsonSnapshot(
            run,
            """
            {"admission":{"version":"vyral.admission.v1","admissionId":"adm_6731679ba0bb0fb7677789c0d0293f08d90c4631d80c0e869c9d904f7f34926d","operationId":"startExecutionRun","status":"accepted","resourceId":"000000000000000000000000000000000001","requestHash":"sha256:abc","idempotencyKeyHash":"e28d47366bafe0d71c8ab4f554fae9a908fb9e0ab9c018ea43eb4a27d03e0f7b","replayed":false,"admittedAtUtc":"2026-06-24T12:00:00Z","statusUri":"/execution/runs/000000000000000000000000000000000001"},"id":"000000000000000000000000000000000001","handlerId":"sample.external.work.audit","pluginId":"sample.external.work","status":"succeeded","attempt":1,"maxAttempts":2,"retryPolicy":{"maxAttempts":2,"initialDelaySeconds":1.5,"maxDelaySeconds":10,"backoffMultiplier":2},"correlationId":"corr-1","payloadHash":"sha256:abc","payload":{"items":["alpha","beta"]},"createdAtUtc":"2026-06-24T12:00:00Z","scheduledAtUtc":"2026-06-24T12:30:00Z","startedAtUtc":"2026-06-24T12:30:01Z","updatedAtUtc":"2026-06-24T12:30:03Z","completedAtUtc":"2026-06-24T12:30:03Z","durationMs":2000,"cancellationRequested":false,"requested":2,"attempted":2,"succeeded":2,"failed":0,"progress":1,"currentStep":"complete","result":{"itemCount":2},"statusDetails":{"phase":"completed"},"tags":{"tenant":"demo"}}
            """);
    }

    [Fact]
    public void ExecutionContractSnapshots_PinStatusHistoryArtifactAndPolicyShapes()
    {
        var adapterStatus = new ExecutionRuntimeAdapterStatus
        {
            Adapter = new ExecutionRuntimeAdapterDescriptor
            {
                AdapterId = "local-sqlite",
                RuntimeKind = "local.sqlite",
                DisplayName = "Local SQLite execution runtime",
                Version = "0.2.0",
                Capabilities =
                {
                    ExecutionCapabilityIds.LocalDispatch,
                    ExecutionCapabilityIds.InProcessHandlers,
                    ExecutionCapabilityIds.DurableRuns,
                    ExecutionCapabilityIds.Cancellation,
                    ExecutionCapabilityIds.Retries,
                    ExecutionCapabilityIds.Artifacts,
                    ExecutionCapabilityIds.TraceHistory,
                    ExecutionCapabilityIds.Idempotency
                },
                Metadata =
                {
                    ["concurrencyKeyPolicy"] = "serialize_running_runs"
                }
            },
            Available = true,
            Status = "ok",
            CheckedAtUtc = new DateTime(2026, 06, 24, 12, 00, 00, DateTimeKind.Utc),
            ActiveRuns = 1,
            OperationalPolicy = new ExecutionOperationalPolicy
            {
                MaxActiveRuns = 8,
                MaxRetainedTerminalRuns = 20,
                DefaultListLimit = 20,
                MaxListLimit = 100,
                DefaultHistoryLimit = 20,
                MaxHistoryLimit = 100,
                MaxPayloadBytes = 1024,
                MaxResultBytes = 2048,
                MaxStatusDetailsBytes = 1024,
                MaxArtifactBytes = 4096,
                MaxArtifactInlineBytes = 1024,
                MaxTraceMessageChars = 512,
                MaxTraceDetailsBytes = 1024,
                MaxRetryAttempts = 3,
                MaxRetryDelaySeconds = 60,
                MaxLeaseTtlSeconds = 300,
                ConcurrencyKeyPolicy = "serialize_running_runs",
                ConcurrencyRetryDelayMs = 100,
                DefaultTraceSeverity = "info",
                RetentionScope = "run_owned"
            },
            ResumePolicy = new ExecutionResumePolicy
            {
                Mode = ExecutionResumePolicyModes.RestartRecovery,
                InterruptedRunningBehavior = ExecutionResumePolicyBehaviors.MayReexecuteHandler,
                ScheduledWaitingBehavior = ExecutionResumePolicyBehaviors.DispatchWhenDue,
                TerminalBehavior = ExecutionResumePolicyBehaviors.NeverResume,
                PluginCheckpointBehavior = ExecutionResumePolicyBehaviors.PluginOwned,
                IdempotencyScope = "handler_plugin_payload",
                CreatesLinkedFollowUpRuns = false
            },
            Details = new JsonObject
            {
                ["registeredHandlers"] = 1
            }
        };

        var trace = new ExecutionTraceEvent
        {
            Id = "000000000000000000000000000000000010",
            SequenceId = "000000000000000000000000000000000011",
            RunId = "run-1",
            Type = ExecutionEventTypes.RunStatus,
            TimestampUtc = new DateTime(2026, 06, 24, 12, 00, 01, DateTimeKind.Utc),
            Attempt = 1,
            StepId = "step-1",
            Status = ExecutionRunStatuses.Running,
            Severity = "info",
            Message = "Processing",
            Details = new JsonObject
            {
                ["progress"] = 0.5
            },
            Context =
            {
                ["adapterId"] = "local-sqlite",
                ["handlerId"] = "sample.external.work.audit"
            }
        };

        var artifact = new ExecutionArtifact
        {
            Id = "000000000000000000000000000000000012",
            RunId = "run-1",
            Name = "summary",
            Kind = ExecutionArtifactKinds.Json,
            MediaType = "application/json",
            ContentHash = "sha256:def",
            SizeBytes = 32,
            Content = new JsonObject
            {
                ["ok"] = true
            },
            CreatedAtUtc = new DateTime(2026, 06, 24, 12, 00, 02, DateTimeKind.Utc),
            Metadata =
            {
                ["sample"] = "external"
            }
        };

        AssertJsonSnapshot(
            adapterStatus,
            """
            {"adapter":{"adapterId":"local-sqlite","runtimeKind":"local.sqlite","displayName":"Local SQLite execution runtime","version":"0.2.0","capabilities":["local.dispatch","in_process.handlers","durable.runs","cancellation","retries","artifacts","trace.history","idempotency"],"metadata":{"concurrencyKeyPolicy":"serialize_running_runs"}},"available":true,"status":"ok","checkedAtUtc":"2026-06-24T12:00:00Z","activeRuns":1,"operationalPolicy":{"maxActiveRuns":8,"maxRetainedTerminalRuns":20,"defaultListLimit":20,"maxListLimit":100,"defaultHistoryLimit":20,"maxHistoryLimit":100,"maxPayloadBytes":1024,"maxResultBytes":2048,"maxStatusDetailsBytes":1024,"maxArtifactBytes":4096,"maxArtifactInlineBytes":1024,"maxTraceMessageChars":512,"maxTraceDetailsBytes":1024,"maxRetryAttempts":3,"maxRetryDelaySeconds":60,"maxLeaseTtlSeconds":300,"concurrencyKeyPolicy":"serialize_running_runs","concurrencyRetryDelayMs":100,"defaultTraceSeverity":"info","retentionScope":"run_owned"},"resumePolicy":{"mode":"restart_recovery","interruptedRunningBehavior":"may_reexecute_handler","scheduledWaitingBehavior":"dispatch_when_due","terminalBehavior":"never_resume","pluginCheckpointBehavior":"plugin_owned","idempotencyScope":"handler_plugin_payload","createsLinkedFollowUpRuns":false},"details":{"registeredHandlers":1}}
            """);
        AssertJsonSnapshot(
            trace,
            """
            {"id":"000000000000000000000000000000000010","sequenceId":"000000000000000000000000000000000011","runId":"run-1","type":"run.status","timestampUtc":"2026-06-24T12:00:01Z","attempt":1,"stepId":"step-1","status":"running","severity":"info","message":"Processing","details":{"progress":0.5},"context":{"adapterId":"local-sqlite","handlerId":"sample.external.work.audit"}}
            """);
        AssertJsonSnapshot(
            artifact,
            """
            {"id":"000000000000000000000000000000000012","runId":"run-1","name":"summary","kind":"json","mediaType":"application/json","contentHash":"sha256:def","sizeBytes":32,"content":{"ok":true},"createdAtUtc":"2026-06-24T12:00:02Z","metadata":{"sample":"external"}}
            """);
    }

    [Fact]
    public void ExecutionOpenApiContract_ExposesRuntimeSurfaceAndStabilityMarker()
    {
        var contractPath = FindRepoFile("src/Vyral.Server/contracts/vyral.openapi.json");
        var document = JsonNode.Parse(File.ReadAllText(contractPath))!.AsObject();
        var paths = document["paths"]!.AsObject();
        var schemas = document["components"]!["schemas"]!.AsObject();

        Assert.NotNull(document["x-vyral-executionRuntimeStability"]);
        AssertOperation(paths, "/execution/runtime", "get", "getExecutionRuntime");
        AssertOperation(paths, "/execution/runtime/maintenance", "get", "getExecutionRuntimeMaintenance");
        AssertOperation(paths, "/execution/runtime/maintenance/prune", "post", "pruneExecutionRuntimeMaintenance");
        AssertOperation(paths, "/execution/runtime/maintenance/reconcile", "post", "reconcileExecutionRuntimeDispatch");
        AssertOperation(paths, "/execution/workers/leases", "post", "leaseExternalExecutionRun");
        AssertOperation(paths, "/execution/workers/leases/heartbeat", "post", "heartbeatExternalExecutionLease");
        AssertOperation(paths, "/execution/workers/leases/reports", "post", "reportExternalExecutionLease");
        AssertOperation(paths, "/execution/workers/leases/events", "post", "recordExternalExecutionLeaseEvent");
        AssertOperation(paths, "/execution/workers/leases/artifacts", "post", "putExternalExecutionLeaseArtifact");
        AssertOperation(paths, "/execution/workers/leases/checkpoints", "post", "putExternalExecutionLeaseCheckpoint");
        AssertOperation(paths, "/execution/workers/leases/checkpoints/read", "post", "getExternalExecutionLeaseCheckpoint");
        AssertOperation(paths, "/execution/workers/leases/wait", "post", "waitExternalExecutionLease");
        AssertOperation(paths, "/execution/workers/leases/complete", "post", "completeExternalExecutionLease");
        AssertOperation(paths, "/execution/runs", "get", "listExecutionRuns");
        AssertOperation(paths, "/execution/runs", "post", "startExecutionRun");
        AssertOperation(paths, "/execution/runs/{runId}", "get", "getExecutionRun");
        AssertOperation(paths, "/execution/runs/{runId}", "delete", "cancelExecutionRun");
        AssertOperation(paths, "/execution/runs/{runId}/history", "get", "getExecutionRunHistory");
        AssertOperation(paths, "/execution/runs/{runId}/artifacts", "get", "listExecutionRunArtifacts");
        AssertOperation(paths, "/execution/runs/{runId}/artifacts/{artifactRef}", "get", "getExecutionRunArtifact");
        AssertOperation(paths, "/execution/runs/{runId}/checkpoints/{key}", "get", "getExecutionRunCheckpoint");

        foreach (var schemaName in new[]
        {
            "ExecutionRuntimeSurface",
            "ExecutionRuntimeAdapterStatus",
            "ExecutionRuntimeAdapterDescriptor",
            "ExecutionMaintenanceStatus",
            "ExecutionMaintenancePruneRequest",
            "ExecutionMaintenancePruneResult",
            "ExecutionMaintenanceDispatchReconcileRequest",
            "ExecutionMaintenanceDispatchReconcileResult",
            "ExecutionOperationalPolicy",
            "ExecutionResumePolicy",
            "ExecutionPluginDescriptor",
            "ExecutionHandlerDescriptor",
            "ExecutionRetryPolicy",
            "ExecutionRunRequest",
            "ExecutionRun",
            "ExecutionTraceEvent",
            "ExecutionArtifact",
            "ExecutionCheckpoint",
            "ExecutionExternalWorkerLeaseRequest",
            "ExecutionExternalWorkerLease",
            "ExecutionExternalWorkerHeartbeatRequest",
            "ExecutionExternalWorkerReportRequest",
            "ExecutionExternalWorkerEventRequest",
            "ExecutionExternalWorkerArtifactRequest",
            "ExecutionExternalWorkerCheckpointRequest",
            "ExecutionExternalWorkerCheckpointReadRequest",
            "ExecutionExternalWorkerWaitRequest",
            "ExecutionExternalWorkerWaitResponse",
            "ExecutionExternalWorkerCompletionRequest"
        })
        {
            Assert.True(schemas.ContainsKey(schemaName), $"Missing OpenAPI schema '{schemaName}'.");
        }
    }

    private static void AssertJsonSnapshot<T>(T value, string expected)
    {
        Assert.Equal(NormalizeJson(expected), JsonSerializer.Serialize(value, ExecutionJson.Options));
    }

    private static string NormalizeJson(string json)
    {
        return JsonNode.Parse(json)!.ToJsonString(ExecutionJson.Options);
    }

    private static void AssertOperation(JsonObject paths, string path, string method, string operationId)
    {
        Assert.True(paths.TryGetPropertyValue(path, out var pathNode), $"Missing OpenAPI path '{path}'.");
        var pathObject = pathNode!.AsObject();
        Assert.True(pathObject.TryGetPropertyValue(method, out var operationNode), $"Missing OpenAPI operation '{method} {path}'.");
        Assert.Equal(operationId, operationNode!["operationId"]!.GetValue<string>());
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }
}
