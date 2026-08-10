using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Execution.Local;

namespace Vyral.Tests.Conformance;

public sealed class PortableNativeExecutionLifecycleFixtureTests
{
    private const string ScenarioId = "execution.native-lifecycle.v1";
    private const string ManifestResource =
        "Vyral.Tests.Conformance.runtime-v1-manifest.json";
    private const string ScenarioResource =
        "Vyral.Tests.Conformance.runtime-v1-native-execution-lifecycle.json";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task LocalExecutionRuntimeMatchesPortableLifecycleFixture()
    {
        var manifestBytes = ReadResource(ManifestResource);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var descriptor = manifest.RootElement
            .GetProperty("scenarios")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("id").GetString() == ScenarioId);
        Assert.Equal(
            "vyral.runtime.execution.local.v1",
            descriptor.GetProperty("profile").GetString());

        var scenarioBytes = ReadResource(ScenarioResource);
        var digest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(scenarioBytes));
        Assert.Equal(
            descriptor.GetProperty("sha256").GetString(),
            digest);

        var path = Path.Combine(
            Path.GetTempPath(),
            $"vyral-portable-native-execution-{Guid.NewGuid():N}.sqlite");
        var artifacts = path + "-artifacts";
        var runner = new FixtureRunner(path, artifacts);
        try
        {
            using var scenario = JsonDocument.Parse(scenarioBytes);
            foreach (
                var step in scenario.RootElement
                    .GetProperty("steps")
                    .EnumerateArray())
            {
                var actual = await runner.ExecuteAsync(
                    step.GetProperty("operation").GetString()!,
                    step.GetProperty("arguments"));
                var expected = JsonNode.Parse(
                    step.GetProperty("expect")
                        .GetProperty("value")
                        .GetRawText());
                Assert.True(
                    JsonNode.DeepEquals(expected, actual),
                    $"Native execution step " +
                    $"'{step.GetProperty("id").GetString()}' produced " +
                    $"{actual.ToJsonString()}, expected " +
                    $"{expected?.ToJsonString() ?? "null"}.");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            if (File.Exists(path + "-wal"))
            {
                File.Delete(path + "-wal");
            }
            if (File.Exists(path + "-shm"))
            {
                File.Delete(path + "-shm");
            }
            if (Directory.Exists(artifacts))
            {
                Directory.Delete(artifacts, recursive: true);
            }
        }
    }

    private sealed class FixtureRunner
    {
        private readonly string _path;
        private readonly string _artifacts;
        private LocalExecutionRuntime _runtime;
        private int _retryAttempts;
        private int _failureAttempts;

        public FixtureRunner(string path, string artifacts)
        {
            _path = path;
            _artifacts = artifacts;
            _runtime = CreateRuntime();
        }

        public Task<JsonObject> ExecuteAsync(
            string operation,
            JsonElement arguments) =>
            operation switch
            {
                "execution.native-success" =>
                    SuccessAsync(arguments),
                "execution.native-rejections" =>
                    RejectionsAsync(arguments),
                "execution.native-retry" =>
                    RetryAsync(arguments),
                "execution.native-failure" =>
                    FailureAsync(arguments),
                "execution.native-cancel" =>
                    CancelAsync(arguments),
                "execution.native-wait-restart" =>
                    WaitRestartAsync(arguments),
                "execution.native-coordination" =>
                    CoordinationAsync(arguments),
                _ => throw new InvalidOperationException(
                    $"Unsupported native execution fixture " +
                    $"operation '{operation}'.")
            };

        private LocalExecutionRuntime CreateRuntime()
        {
            var runtime = new LocalExecutionRuntime(
                new LocalExecutionRuntimeOptions
                {
                    DatabasePath = _path,
                    ArtifactDirectory = _artifacts,
                    MaxActiveRuns = 10,
                    MaxRetainedTerminalRuns = 50,
                    DefaultListLimit = 20,
                    MaxListLimit = 100
                });
            runtime.RegisterHandler(
                new DelegateExecutionHandler(
                    Descriptor(
                        "portable.success", "Portable success"),
                    SuccessHandlerAsync));
            runtime.RegisterHandler(
                new DelegateExecutionHandler(
                    Descriptor(
                        "portable.retry", "Portable retry"),
                    RetryHandlerAsync));
            runtime.RegisterHandler(
                new DelegateExecutionHandler(
                    Descriptor(
                        "portable.failure", "Portable failure"),
                    FailureHandlerAsync));
            runtime.RegisterHandler(
                new DelegateExecutionHandler(
                    Descriptor(
                        "portable.wait", "Portable wait"),
                    WaitHandlerAsync));
            return runtime;
        }

        private static ExecutionHandlerDescriptor Descriptor(
            string handlerId,
            string displayName) =>
            new()
            {
                HandlerId = handlerId,
                PluginId = "portable",
                DisplayName = displayName,
                MaxAttempts = 2
            };

        private static async Task<ExecutionRunResult>
            SuccessHandlerAsync(
                IExecutionRunContext context,
                CancellationToken ct)
        {
            var items = context.Run.Payload?["items"]?.AsArray()
                ?? throw new InvalidOperationException(
                    "Portable items are required.");
            var total = items.Sum(
                item => item?.GetValue<int>() ?? 0);
            await context.ReportAsync(
                new ExecutionRunUpdate
                {
                    Requested = items.Count,
                    Attempted = items.Count,
                    Succeeded = items.Count,
                    Failed = 0,
                    Progress = 0.5,
                    CurrentStep = "persist"
                },
                ct);
            await context.PutCheckpointAsync(
                new ExecutionCheckpointWrite
                {
                    Key = "progress",
                    Content = new JsonObject
                    {
                        ["total"] = total
                    }
                },
                ct);
            await context.PutArtifactAsync(
                new ExecutionArtifactWrite
                {
                    Name = "summary",
                    Content = new JsonObject
                    {
                        ["total"] = total
                    }
                },
                ct);
            return ExecutionRunResult.Succeeded(
                new JsonObject
                {
                    ["total"] = total
                });
        }

        private Task<ExecutionRunResult> RetryHandlerAsync(
            IExecutionRunContext _,
            CancellationToken __)
        {
            _retryAttempts++;
            return Task.FromResult(
                _retryAttempts == 1
                    ? ExecutionRunResult.Failed(
                        ExecutionFailureClasses.Transient,
                        "portable retry")
                    : ExecutionRunResult.Succeeded(
                        new JsonObject
                        {
                            ["attempts"] = _retryAttempts
                        }));
        }

        private Task<ExecutionRunResult> FailureHandlerAsync(
            IExecutionRunContext _,
            CancellationToken __)
        {
            _failureAttempts++;
            return Task.FromResult(
                ExecutionRunResult.Failed(
                    ExecutionFailureClasses.Validation,
                    "portable validation failure"));
        }

        private static async Task<ExecutionRunResult>
            WaitHandlerAsync(
                IExecutionRunContext context,
                CancellationToken ct)
        {
            var checkpoint = await context.GetCheckpointAsync(
                "calls", ct);
            var calls =
                checkpoint?.Content?["calls"]?.GetValue<int>() ?? 0;
            calls++;
            await context.PutCheckpointAsync(
                new ExecutionCheckpointWrite
                {
                    Key = "calls",
                    Content = new JsonObject
                    {
                        ["calls"] = calls
                    }
                },
                ct);
            var outcome = await context.WaitForExternalEventAsync(
                "approval", null, ct);
            return ExecutionRunResult.Succeeded(
                new JsonObject
                {
                    ["calls"] = calls,
                    ["outcome"] = outcome.Outcome,
                    ["approved"] =
                        outcome.Event?.Payload?["approved"]
                            ?.GetValue<bool>() == true
                });
        }

        private async Task<JsonObject> SuccessAsync(
            JsonElement arguments)
        {
            var request = Deserialize<ExecutionRunRequest>(
                arguments.GetProperty("request"));
            var first = await _runtime.StartRunAsync(request);
            var replay = await _runtime.StartRunAsync(request);
            await _runtime.DispatchReadyRunsAsync();
            var final = await WaitForStatusAsync(
                _runtime,
                first.Id,
                ExecutionRunStatuses.Succeeded);
            var artifacts = await _runtime.ListArtifactsAsync(
                first.Id);
            var checkpoint = await _runtime.GetCheckpointAsync(
                first.Id, "progress");
            var history = await _runtime.GetHistoryAsync(first.Id);
            var required = new[]
            {
                ExecutionEventTypes.RunCreated,
                ExecutionEventTypes.RunStarted,
                ExecutionEventTypes.RunStatus,
                ExecutionEventTypes.ArtifactWritten,
                ExecutionEventTypes.CheckpointWritten,
                ExecutionEventTypes.RunCompleted
            };
            var firstAdmission = ExecutionAdmission.Create(
                first,
                "startExecutionRun",
                $"/execution/runs/{first.Id}");
            var replayAdmission = ExecutionAdmission.Create(
                replay,
                "startExecutionRun",
                $"/execution/runs/{replay.Id}");
            return new JsonObject
            {
                ["createdStatus"] = first.Status,
                ["replaySame"] = replay.Id == first.Id,
                ["admissionVersion"] = firstAdmission.Version,
                ["admissionStatus"] = firstAdmission.Status,
                ["admissionIdStable"] =
                    firstAdmission.AdmissionId ==
                    replayAdmission.AdmissionId,
                ["replayMarked"] = replayAdmission.Replayed,
                ["idempotencyKeyHash"] =
                    firstAdmission.IdempotencyKeyHash,
                ["admissionContainsRawKey"] =
                    JsonSerializer.Serialize(firstAdmission)
                        .Contains(
                            request.IdempotencyKey!,
                            StringComparison.Ordinal),
                ["finalStatus"] = final.Status,
                ["attempt"] = final.Attempt,
                ["progress"] = final.Progress,
                ["resultTotal"] =
                    final.Result?["total"]?.GetValue<int>(),
                ["artifactCount"] = artifacts.Count,
                ["checkpointTotal"] =
                    checkpoint?.Content?["total"]?.GetValue<int>(),
                ["requiredHistoryPassed"] = required.All(
                    type => history.Any(item => item.Type == type))
            };
        }

        private async Task<JsonObject> RejectionsAsync(
            JsonElement arguments)
        {
            var missing = await _runtime.StartRunAsync(
                new ExecutionRunRequest
                {
                    HandlerId = "portable.missing"
                });
            var mismatch = await _runtime.StartRunAsync(
                new ExecutionRunRequest
                {
                    HandlerId = "portable.success",
                    PluginId = "other"
                });
            var conflict = false;
            try
            {
                await _runtime.StartRunAsync(
                    Deserialize<ExecutionRunRequest>(
                        arguments.GetProperty(
                            "conflictingRequest")));
            }
            catch (InvalidOperationException)
            {
                conflict = true;
            }
            return new JsonObject
            {
                ["missingStatus"] = missing.Status,
                ["missingFailureClass"] = missing.FailureClass,
                ["pluginStatus"] = mismatch.Status,
                ["pluginFailureClass"] = mismatch.FailureClass,
                ["idempotencyConflictRejected"] = conflict
            };
        }

        private async Task<JsonObject> RetryAsync(
            JsonElement arguments)
        {
            var run = await _runtime.StartRunAsync(
                Deserialize<ExecutionRunRequest>(
                    arguments.GetProperty("request")));
            await _runtime.DispatchReadyRunsAsync();
            var final = await WaitForStatusAsync(
                _runtime,
                run.Id,
                ExecutionRunStatuses.Succeeded);
            var history = await _runtime.GetHistoryAsync(run.Id);
            return new JsonObject
            {
                ["finalStatus"] = final.Status,
                ["attempt"] = final.Attempt,
                ["handlerAttempts"] = _retryAttempts,
                ["retryScheduled"] = history.Any(
                    item =>
                        item.Type ==
                        ExecutionEventTypes.RetryScheduled)
            };
        }

        private static ExecutionRunRequest WithSchedule(
            ExecutionRunRequest request,
            int delaySeconds)
        {
            if (delaySeconds <= 0)
            {
                throw new InvalidOperationException(
                    "Fixture schedule delay must be positive.");
            }
            request.ScheduledAtUtc = DateTime.UtcNow.AddSeconds(
                delaySeconds);
            return request;
        }

        private async Task<JsonObject> WaitRestartAsync(
            JsonElement arguments)
        {
            var run = await _runtime.StartRunAsync(
                Deserialize<ExecutionRunRequest>(
                    arguments.GetProperty("request")));
            await _runtime.DispatchReadyRunsAsync();
            var suspended = await WaitForStatusAsync(
                _runtime,
                run.Id,
                ExecutionRunStatuses.Waiting);

            _runtime = CreateRuntime();
            var rawEvent = arguments.GetProperty("event");
            await _runtime.RaiseEventAsync(
                new ExecutionExternalEventRequest
                {
                    Name = rawEvent.GetProperty("name").GetString()!,
                    RunId = run.Id,
                    Payload = JsonNode.Parse(
                        rawEvent.GetProperty("payload").GetRawText())
                });
            await _runtime.DispatchReadyRunsAsync(
                recoverInterruptedRuns: true);
            var final = await WaitForStatusAsync(
                _runtime,
                run.Id,
                ExecutionRunStatuses.Succeeded);
            var checkpoint = await _runtime.GetCheckpointAsync(
                run.Id, "calls");
            var history = await _runtime.GetHistoryAsync(run.Id);
            return new JsonObject
            {
                ["suspendedStatus"] = suspended.Status,
                ["finalStatus"] = final.Status,
                ["attempt"] = final.Attempt,
                ["checkpointCalls"] =
                    checkpoint?.Content?["calls"]?.GetValue<int>(),
                ["outcome"] =
                    final.Result?["outcome"]?.GetValue<string>(),
                ["approved"] =
                    final.Result?["approved"]?.GetValue<bool>(),
                ["waitRegistered"] = history.Any(
                    item =>
                        item.Type ==
                        ExecutionEventTypes.WaitRegistered),
                ["waitResumed"] = history.Any(
                    item =>
                        item.Type ==
                        ExecutionEventTypes.WaitResumed)
            };
        }

        private async Task<JsonObject> FailureAsync(
            JsonElement arguments)
        {
            var run = await _runtime.StartRunAsync(
                Deserialize<ExecutionRunRequest>(
                    arguments.GetProperty("request")));
            await _runtime.DispatchReadyRunsAsync();
            var final = await WaitForStatusAsync(
                _runtime,
                run.Id,
                ExecutionRunStatuses.Failed);
            var history = await _runtime.GetHistoryAsync(run.Id);
            return new JsonObject
            {
                ["finalStatus"] = final.Status,
                ["failureClass"] = final.FailureClass,
                ["attempt"] = final.Attempt,
                ["handlerAttempts"] = _failureAttempts,
                ["retryScheduled"] = history.Any(
                    item =>
                        item.Type ==
                        ExecutionEventTypes.RetryScheduled)
            };
        }

        private async Task<JsonObject> CancelAsync(
            JsonElement arguments)
        {
            var run = await _runtime.StartRunAsync(
                WithSchedule(
                    Deserialize<ExecutionRunRequest>(
                        arguments.GetProperty("request")),
                    arguments.GetProperty(
                        "scheduleDelaySeconds").GetInt32()));
            var cancelled = await _runtime.CancelRunAsync(run.Id)
                ?? throw new InvalidOperationException(
                    "Portable cancellation run disappeared.");
            var stable = await _runtime.CancelRunAsync(run.Id)
                ?? throw new InvalidOperationException(
                    "Portable cancellation replay disappeared.");
            var history = await _runtime.GetHistoryAsync(run.Id);
            return new JsonObject
            {
                ["createdStatus"] = run.Status,
                ["finalStatus"] = cancelled.Status,
                ["failureClass"] = cancelled.FailureClass,
                ["cancellationRequested"] =
                    cancelled.CancellationRequested,
                ["terminalCancelStable"] =
                    stable.Status == ExecutionRunStatuses.Cancelled,
                ["cancellationRecorded"] = history.Any(
                    item =>
                        item.Type ==
                        ExecutionEventTypes.RunCancellationRequested)
            };
        }

        private async Task<JsonObject> CoordinationAsync(
            JsonElement arguments)
        {
            var leaseKey = arguments.GetProperty(
                "leaseKey").GetString()!;
            var first = await _runtime.TryAcquireLeaseAsync(
                new ExecutionLeaseRequest
                {
                    LeaseKey = leaseKey,
                    OwnerId = "owner-a"
                });
            var conflict = await _runtime.TryAcquireLeaseAsync(
                new ExecutionLeaseRequest
                {
                    LeaseKey = leaseKey,
                    OwnerId = "owner-b"
                });
            var wrong = await _runtime.ReleaseLeaseAsync(
                leaseKey, "owner-b");
            var released = await _runtime.ReleaseLeaseAsync(
                leaseKey, "owner-a");
            var timer = await _runtime.ScheduleTimerAsync(
                new ExecutionTimerRequest
                {
                    Name = arguments.GetProperty(
                        "timerName").GetString()!,
                    FireAtUtc = DateTime.UtcNow.AddMinutes(1)
                });
            var externalEvent = await _runtime.RaiseEventAsync(
                new ExecutionExternalEventRequest
                {
                    Name = arguments.GetProperty(
                        "eventName").GetString()!
                });
            var maintenance =
                (IExecutionRuntimeMaintenance)_runtime;
            var status =
                await maintenance.GetMaintenanceStatusAsync();
            var reconcile =
                await maintenance.ReconcileDispatchAsync(
                    new ExecutionMaintenanceDispatchReconcileRequest
                    {
                        DryRun = true
                    });
            return new JsonObject
            {
                ["firstLeaseAcquired"] = first is not null,
                ["conflictingLeaseRejected"] = conflict is null,
                ["wrongOwnerRelease"] = wrong,
                ["ownerRelease"] = released,
                ["timerName"] = timer.Name,
                ["eventName"] = externalEvent.Name,
                ["maintenanceHealthy"] =
                    status.RowCounts.GetValueOrDefault("runs") >= 1
                    && status.RetentionScope == "run_owned",
                ["reconcileDryRun"] = reconcile.DryRun
            };
        }

        private static async Task<ExecutionRun> WaitForStatusAsync(
            IExecutionRuntime runtime,
            string runId,
            string status)
        {
            for (var attempt = 0; attempt < 400; attempt++)
            {
                var run = await runtime.GetRunAsync(runId);
                if (run?.Status == status)
                {
                    return run;
                }
                if (
                    run is not null
                    && ExecutionRunStatuses.IsTerminal(run.Status)
                    && run.Status != status)
                {
                    throw new InvalidOperationException(
                        $"Run {runId} became {run.Status}; " +
                        $"expected {status}.");
                }
                await Task.Delay(10);
            }
            throw new TimeoutException(
                $"Run {runId} did not become {status}.");
        }
    }

    private static T Deserialize<T>(JsonElement value)
    {
        return JsonSerializer.Deserialize<T>(
            value.GetRawText(), JsonOptions)
            ?? throw new InvalidOperationException(
                $"Fixture value did not deserialize as "
                + typeof(T).Name + ".");
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(
            PortableNativeExecutionLifecycleFixtureTests)
            .Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{name}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
