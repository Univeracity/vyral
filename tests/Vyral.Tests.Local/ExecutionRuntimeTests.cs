using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Execution.Local;
using Vyral.Local;
using Vyral.Primitives;
using Vyral.Server;

namespace Vyral.Tests.Local;

public class ExecutionRuntimeConformanceTests
{
    [Fact]
    public async Task LocalRuntime_RunsPluginRecordsProgressArtifactsHistoryAndIdempotency()
    {
        var runtime = CreateRuntime();
        ExecutionContractValidator.ValidateAdapterDescriptor(runtime.Adapter);
        Assert.Contains(ExecutionCapabilityIds.LocalDispatch, runtime.Adapter.Capabilities);
        foreach (var required in ExecutionCapabilityCatalog.RequiredAdapterCapabilities)
        {
            Assert.Contains(required, runtime.Adapter.Capabilities);
        }

        runtime.RegisterPlugin(new CountingPlugin());

        var first = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            PluginId = CountingPlugin.PluginId,
            IdempotencyKey = "count:alpha",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a", "b", "c")
            }
        });
        var second = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            PluginId = CountingPlugin.PluginId,
            IdempotencyKey = "count:alpha",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a", "b", "c")
            }
        });

        Assert.Equal(first.Id, second.Id);
        Assert.False(first.AdmissionReplayed);
        Assert.True(second.AdmissionReplayed);

        var completed = await WaitForRunAsync(runtime, first.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(3, completed.Requested);
        Assert.Equal(3, completed.Attempted);
        Assert.Equal(1, completed.Progress);
        Assert.Equal(6, completed.Result!["total"]!.GetValue<int>());

        var history = await runtime.GetHistoryAsync(first.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunStarted);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunStatus);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.ArtifactWritten);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunCompleted);

        var artifacts = await runtime.ListArtifactsAsync(first.Id);
        var summary = Assert.Single(artifacts);
        Assert.Equal("summary", summary.Name);
        Assert.Equal(ExecutionArtifactKinds.Json, summary.Kind);
        Assert.StartsWith("sha256:", summary.ContentHash);
        Assert.Equal(3, summary.Content!["count"]!.GetValue<int>());

        var listed = await runtime.ListRunsAsync(new ExecutionRunQuery
        {
            HandlerId = CountingHandler.HandlerId,
            IncludeResult = false,
            Limit = 10
        });
        Assert.Contains(listed, run => run.Id == first.Id && run.Result is null);
    }

    [Fact]
    public async Task LocalRuntime_DerivesPluginIdAndRejectsPluginMismatch()
    {
        var runtime = CreateRuntime();
        runtime.RegisterPlugin(new CountingPlugin());

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a")
            }
        });
        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(CountingPlugin.PluginId, completed.PluginId);

        var rejected = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            PluginId = "test.other-plugin",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a")
            }
        });

        Assert.Equal(ExecutionRunStatuses.Rejected, rejected.Status);
        Assert.Equal(ExecutionFailureClasses.PluginMismatch, rejected.FailureClass);
        Assert.Contains(CountingPlugin.PluginId, rejected.Error);
    }

    [Fact]
    public async Task LocalRuntime_CancelsRunningPlugin()
    {
        var runtime = CreateRuntime();
        var handler = new BlockingHandler();
        runtime.RegisterHandler(handler);

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = BlockingHandler.HandlerId,
            Payload = new JsonObject { ["name"] = "cancel-me" }
        });

        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await runtime.GetRunAsync(accepted.Id);
        Assert.Equal(ExecutionRunStatuses.Running, running!.Status);

        var requested = await runtime.CancelRunAsync(accepted.Id);
        Assert.NotNull(requested);
        Assert.True(requested!.CancellationRequested);

        var cancelled = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Cancelled);
        Assert.True(cancelled.CancellationRequested);
        Assert.Equal(ExecutionFailureClasses.Cancelled, cancelled.FailureClass);
    }

    [Fact]
    public async Task LocalRuntime_CancelsQueuedRecordImportBeforeAnyRecordAttempt()
    {
        var runtime = CreateRuntime();
        var blocker = new RecordImportConcurrencyBlocker();
        var recordStore = new CountingRecordImportStore();
        runtime.RegisterHandler(blocker);
        var jobs = new ExecutionRuntimeRecordImportJobAdapter(runtime, recordStore);

        var blockingRun = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = RecordImportConcurrencyBlocker.HandlerId
        });
        await blocker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var accepted = await jobs.StartBatchUpsertAsync("queued-import", new RecordBatchUpsertRequest
        {
            Records =
            [
                new VyralRecord { Id = "record-1", PartitionKey = "tenant-a" }
            ]
        });

        var queued = await runtime.GetRunAsync(accepted.Id);
        Assert.Equal(ExecutionRunStatuses.Queued, queued!.Status);
        Assert.Equal(0, recordStore.BatchUpsertCalls);

        var cancellation = await jobs.CancelAsync(accepted.Id);

        Assert.NotNull(cancellation);
        Assert.Equal(RecordImportJobStatuses.Cancelled, cancellation!.Status);
        Assert.True(cancellation.CancellationRequested);
        Assert.Equal(0, cancellation.Attempted);
        Assert.Equal(0, recordStore.BatchUpsertCalls);

        blocker.Release();
        await WaitForRunAsync(runtime, blockingRun.Id, ExecutionRunStatuses.Succeeded);
        await Task.Delay(100);
        Assert.Equal(0, recordStore.BatchUpsertCalls);
    }

    [Fact]
    public async Task LocalRuntime_RejectsRunWhenActiveRunLimitIsReached()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-execution-active-limit-{Guid.NewGuid():N}.sqlite");
        var runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = path,
            MaxActiveRuns = 1,
            MaxRetainedTerminalRuns = 50,
            DefaultListLimit = 20,
            MaxListLimit = 100
        });
        var handler = new BlockingHandler();
        runtime.RegisterHandler(handler);

        var first = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = BlockingHandler.HandlerId
        });
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = BlockingHandler.HandlerId
        });

        Assert.Equal(ExecutionRunStatuses.Rejected, second.Status);
        Assert.Equal(ExecutionFailureClasses.QueueFull, second.FailureClass);

        await runtime.CancelRunAsync(first.Id);
        await WaitForRunAsync(runtime, first.Id, ExecutionRunStatuses.Cancelled);
    }

    [Fact]
    public async Task LocalRuntime_SerializesRunsWithSameConcurrencyKey()
    {
        var runtime = CreateRuntime();
        var handler = new SerialBlockingHandler();
        runtime.RegisterHandler(handler);

        var first = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = SerialBlockingHandler.HandlerId
        });
        await handler.FirstStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = SerialBlockingHandler.HandlerId
        });
        await Task.Delay(175);

        var pending = await runtime.GetRunAsync(second.Id);
        Assert.NotNull(pending);
        Assert.NotEqual(ExecutionRunStatuses.Running, pending!.Status);
        Assert.Equal(1, handler.StartedCount);

        handler.Release();
        await WaitForRunAsync(runtime, first.Id, ExecutionRunStatuses.Succeeded);
        var completed = await WaitForRunAsync(runtime, second.Id, ExecutionRunStatuses.Succeeded);

        Assert.Equal(2, handler.StartedCount);
        Assert.Equal(2, completed.Result!["started"]!.GetValue<int>());
    }

    [Fact]
    public async Task EmbeddingAdapter_ProjectsRuntimeRunToExistingEmbeddingJobShape()
    {
        var runtime = CreateRuntime();
        var provider = new DeterministicEmbeddingProvider();
        var adapter = new ExecutionRuntimeEmbeddingJobAdapter(runtime, provider, new EmbeddingProviderOptions());

        var accepted = await adapter.StartAsync(new EmbeddingRequest
        {
            Texts = new List<string> { "alpha", "beta" },
            Purpose = EmbeddingPurposes.Symmetric
        }, idempotencyKey: "embedding:alpha-beta");

        var completed = await WaitForEmbeddingJobAsync(adapter, accepted.Id, EmbeddingJobStatuses.Succeeded);
        Assert.Equal(EmbeddingJobStatuses.Succeeded, completed.Status);
        Assert.Equal(provider.ProviderId, completed.Provider);
        Assert.Equal(provider.ModelId, completed.ModelId);
        Assert.Equal(provider.Dimensions, completed.Dimensions);
        Assert.Equal(2, completed.Requested);
        Assert.Equal(2, completed.Attempted);
        Assert.Equal(2, completed.Succeeded);
        Assert.Equal(1, completed.Progress);
        Assert.NotNull(completed.Result);
        Assert.Equal(new[] { 0, 1 }, completed.Result!.Items.Select(item => item.Index));

        var duplicate = await adapter.StartAsync(new EmbeddingRequest
        {
            Texts = new List<string> { "alpha", "beta" },
            Purpose = EmbeddingPurposes.Symmetric
        }, idempotencyKey: "embedding:alpha-beta");
        Assert.Equal(accepted.Id, duplicate.Id);
    }

    [Fact]
    public async Task LocalRuntime_RejectsIdempotencyKeyReuseForDifferentRequestShape()
    {
        var runtime = CreateRuntime();
        runtime.RegisterPlugin(new CountingPlugin());

        await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            PluginId = CountingPlugin.PluginId,
            IdempotencyKey = "count:conflict",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a")
            }
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = CountingHandler.HandlerId,
                PluginId = CountingPlugin.PluginId,
                IdempotencyKey = "count:conflict",
                Payload = new JsonObject
                {
                    ["items"] = new JsonArray("different")
                }
            }));

        Assert.Contains("idempotency key", error.Message);
    }

    [Fact]
    public async Task LocalRuntime_FailsHandlerThatReportsInvalidStatusTransition()
    {
        var runtime = CreateRuntime();
        runtime.RegisterHandler(new InvalidTransitionHandler());

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = InvalidTransitionHandler.HandlerId
        });

        var failed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Failed);

        Assert.Equal(ExecutionFailureClasses.Unknown, failed.FailureClass);
        Assert.Contains("cannot transition", failed.Error);
    }

    [Fact]
    public async Task LocalRuntime_RetriesFailedRunUntilSuccess()
    {
        var runtime = CreateRuntime();
        var handler = new RetryOnceHandler();
        runtime.RegisterHandler(handler);

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = RetryOnceHandler.HandlerId,
            RetryPolicy = new ExecutionRetryPolicy
            {
                MaxAttempts = 2,
                InitialDelaySeconds = 0.01,
                MaxDelaySeconds = 0.01,
                BackoffMultiplier = 1
            }
        });

        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(2, completed.Attempt);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal(2, completed.Result!["attempt"]!.GetValue<int>());

        var history = await runtime.GetHistoryAsync(accepted.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RetryScheduled);
    }

    [Fact]
    public async Task LocalRuntime_RunsScheduledWorkWhenDue()
    {
        var runtime = CreateRuntime();
        runtime.RegisterHandler(new CountingHandler());

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = CountingHandler.HandlerId,
            ScheduledAtUtc = DateTime.UtcNow.AddMilliseconds(120),
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a")
            }
        });

        Assert.Equal(ExecutionRunStatuses.Waiting, accepted.Status);
        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(1, completed.Attempted);
    }

    [Fact]
    public async Task LocalRuntime_RejectsOversizedPayloadBeforePersisting()
    {
        var runtime = CreateRuntime(new ExecutionRuntimeLimits { MaxPayloadBytes = 24 });
        runtime.RegisterHandler(new CountingHandler());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = CountingHandler.HandlerId,
                Payload = new JsonObject
                {
                    ["value"] = new string('x', 64)
                }
            }));

        Assert.Contains("Run payload", error.Message);
        var runs = await runtime.ListRunsAsync(new ExecutionRunQuery { HandlerId = CountingHandler.HandlerId });
        Assert.Empty(runs);
    }

    [Fact]
    public async Task LocalRuntime_DispatchesPersistedWaitingRunFromNewInstance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-execution-resume-{Guid.NewGuid():N}.sqlite");
        var options = new LocalExecutionRuntimeOptions
        {
            DatabasePath = path,
            MaxActiveRuns = 8,
            MaxRetainedTerminalRuns = 50,
            DefaultListLimit = 20,
            MaxListLimit = 100
        };
        var firstHandler = new DelayedCountingHandler();
        var first = new LocalExecutionRuntime(options);
        first.RegisterHandler(firstHandler);

        var accepted = await first.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = DelayedCountingHandler.HandlerId,
            ScheduledAtUtc = DateTime.UtcNow.AddMilliseconds(200),
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a", "b")
            }
        });

        var secondHandler = new DelayedCountingHandler();
        var second = new LocalExecutionRuntime(options);
        second.RegisterHandler(secondHandler);
        await Task.Delay(240);
        await second.DispatchReadyRunsAsync();

        var completed = await WaitForRunAsync(second, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(2, completed.Attempted);
        Assert.Equal(1, firstHandler.Executions + secondHandler.Executions);
    }

    [Fact]
    public async Task LocalRuntime_RecoversInterruptedRunningRunByReexecutingHandler()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-execution-interrupted-{Guid.NewGuid():N}.sqlite");
        var options = new LocalExecutionRuntimeOptions
        {
            DatabasePath = path,
            MaxActiveRuns = 8,
            MaxRetainedTerminalRuns = 50,
            DefaultListLimit = 20,
            MaxListLimit = 100
        };
        _ = new LocalExecutionRuntime(options);
        var runId = OrderedId.CreateString();
        var now = DateTime.UtcNow.AddSeconds(-10);
        var interrupted = new ExecutionRun
        {
            Id = runId,
            HandlerId = CountingHandler.HandlerId,
            Status = ExecutionRunStatuses.Running,
            Attempt = 1,
            MaxAttempts = 3,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 3 },
            CorrelationId = runId,
            PayloadHash = "manual-test-payload",
            Payload = new JsonObject
            {
                ["items"] = new JsonArray("a", "b")
            },
            CreatedAtUtc = now,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            CurrentStep = "interrupted"
        };
        await InsertRunAsync(path, interrupted);

        var recovered = new LocalExecutionRuntime(options);
        recovered.RegisterHandler(new CountingHandler());

        var dispatched = await recovered.DispatchReadyRunsAsync(recoverInterruptedRuns: true);
        Assert.Equal(1, dispatched);

        var completed = await WaitForRunAsync(recovered, runId, ExecutionRunStatuses.Succeeded);
        Assert.Equal(2, completed.Attempt);
        Assert.Equal(2, completed.Attempted);
        Assert.NotEqual("interrupted", completed.CurrentStep);

        var history = await recovered.GetHistoryAsync(runId);
        Assert.Contains(history, item =>
            item.Type == ExecutionEventTypes.RunStatus &&
            item.Message == "Interrupted execution run requeued.");
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.RunStarted);
    }

    [Fact]
    public async Task LocalRuntime_PrunesRunOwnedRowsWhenTerminalRetentionIsExceeded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-execution-prune-{Guid.NewGuid():N}.sqlite");
        var runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = path,
            MaxActiveRuns = 8,
            MaxRetainedTerminalRuns = 1,
            DefaultListLimit = 20,
            MaxListLimit = 100
        });
        runtime.RegisterHandler(new RunOwnedStateHandler());

        var first = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = RunOwnedStateHandler.HandlerId,
            Payload = new JsonObject { ["name"] = "first" }
        });
        await WaitForRunAsync(runtime, first.Id, ExecutionRunStatuses.Succeeded);

        var second = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = RunOwnedStateHandler.HandlerId,
            Payload = new JsonObject { ["name"] = "second" }
        });
        await WaitForRunAsync(runtime, second.Id, ExecutionRunStatuses.Succeeded);

        await WaitForMissingRunAsync(runtime, first.Id);
        Assert.Empty(await runtime.GetHistoryAsync(first.Id));
        Assert.Empty(await runtime.ListArtifactsAsync(first.Id));

        Assert.NotNull(await runtime.GetRunAsync(second.Id));
        Assert.NotEmpty(await runtime.GetHistoryAsync(second.Id));
        Assert.NotEmpty(await runtime.ListArtifactsAsync(second.Id));

        foreach (var (tableName, runIdColumnName) in new[]
                 {
                     ("vyral_execution_runs", "id"),
                     ("vyral_execution_events", "run_id"),
                     ("vyral_execution_artifacts", "run_id"),
                     ("vyral_execution_leases", "run_id"),
                     ("vyral_execution_timers", "run_id"),
                     ("vyral_execution_external_events", "run_id")
                 })
        {
            Assert.Equal(0, await CountRowsForRunAsync(path, tableName, runIdColumnName, first.Id));
            Assert.True(await CountRowsForRunAsync(path, tableName, runIdColumnName, second.Id) > 0);
        }
    }

    [Fact]
    public async Task LocalRuntime_InitializesSchemaMetadataWalAndArtifactDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-execution-schema-{Guid.NewGuid():N}.sqlite");
        var artifactDirectory = Path.Combine(Path.GetTempPath(), $"vyral-execution-artifacts-{Guid.NewGuid():N}");
        var runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = path,
            ArtifactDirectory = artifactDirectory,
            BusyTimeoutMs = 1234,
            MaxActiveRuns = 8,
            MaxRetainedTerminalRuns = 50,
            DefaultListLimit = 20,
            MaxListLimit = 100
        });

        var status = await runtime.GetAdapterStatusAsync();

        Assert.Equal("2", status.Adapter.Metadata["schemaVersion"]);
        Assert.Equal("wal", status.Adapter.Metadata["journalMode"]);
        Assert.Equal("normal", status.Adapter.Metadata["synchronous"]);
        Assert.Equal("1234", status.Adapter.Metadata["busyTimeoutMs"]);
        Assert.Equal(Path.GetFullPath(artifactDirectory), status.Adapter.Metadata["artifactDirectory"]);
        Assert.Equal("serialize_running_runs", status.Adapter.Metadata["concurrencyKeyPolicy"]);
        Assert.NotNull(status.OperationalPolicy);
        Assert.Equal(8, status.OperationalPolicy!.MaxActiveRuns);
        Assert.Equal(50, status.OperationalPolicy.MaxRetainedTerminalRuns);
        Assert.Equal(20, status.OperationalPolicy.DefaultListLimit);
        Assert.Equal(100, status.OperationalPolicy.MaxListLimit);
        Assert.Equal(20, status.OperationalPolicy.DefaultHistoryLimit);
        Assert.Equal(100, status.OperationalPolicy.MaxHistoryLimit);
        Assert.Equal("serialize_running_runs", status.OperationalPolicy.ConcurrencyKeyPolicy);
        Assert.Equal(100, status.OperationalPolicy.ConcurrencyRetryDelayMs);
        Assert.Equal("run_owned", status.OperationalPolicy.RetentionScope);
        Assert.NotNull(status.ResumePolicy);
        Assert.Equal(ExecutionResumePolicyModes.RestartRecovery, status.ResumePolicy!.Mode);
        Assert.Equal(ExecutionResumePolicyBehaviors.MayReexecuteHandler, status.ResumePolicy.InterruptedRunningBehavior);
        Assert.Equal(ExecutionResumePolicyBehaviors.DispatchWhenDue, status.ResumePolicy.ScheduledWaitingBehavior);
        Assert.Equal(ExecutionResumePolicyBehaviors.NeverResume, status.ResumePolicy.TerminalBehavior);
        Assert.Equal(ExecutionResumePolicyBehaviors.PluginOwned, status.ResumePolicy.PluginCheckpointBehavior);
        Assert.True(Directory.Exists(artifactDirectory));
        Assert.Equal(2, await ReadLongAsync(path, "PRAGMA user_version;"));
        Assert.Equal("2", await ReadStringAsync(path, "SELECT value FROM vyral_execution_metadata WHERE key = 'schemaVersion';"));
        Assert.Equal(Path.GetFullPath(artifactDirectory), await ReadStringAsync(path, "SELECT value FROM vyral_execution_metadata WHERE key = 'artifactDirectory';"));
        Assert.Equal("wal", (await ReadStringAsync(path, "PRAGMA journal_mode;")).ToLowerInvariant());
    }

    [Fact]
    public async Task LocalRuntime_OffloadsLargeArtifactsAndPrunesFilesWithRunRetention()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-execution-artifact-offload-{Guid.NewGuid():N}.sqlite");
        var artifactDirectory = Path.Combine(Path.GetTempPath(), $"vyral-execution-artifact-offload-{Guid.NewGuid():N}");
        var runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = path,
            ArtifactDirectory = artifactDirectory,
            Limits = new ExecutionRuntimeLimits
            {
                MaxArtifactBytes = 4_096,
                MaxArtifactInlineBytes = 32
            },
            MaxActiveRuns = 8,
            MaxRetainedTerminalRuns = 1,
            DefaultListLimit = 20,
            MaxListLimit = 100
        });
        runtime.RegisterHandler(new LargeArtifactHandler());

        var first = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = LargeArtifactHandler.HandlerId,
            Payload = new JsonObject
            {
                ["name"] = "first",
                ["size"] = 128
            }
        });
        await WaitForRunAsync(runtime, first.Id, ExecutionRunStatuses.Succeeded);
        var firstArtifact = Assert.Single(await runtime.ListArtifactsAsync(first.Id));
        Assert.Null(firstArtifact.Text);
        Assert.Null(firstArtifact.Content);
        Assert.Equal("local-file", firstArtifact.Metadata["storage"]);
        Assert.Equal("true", firstArtifact.Metadata["offloaded"]);
        Assert.Equal("false", firstArtifact.Metadata["inline"]);
        Assert.False(string.IsNullOrWhiteSpace(firstArtifact.Uri));
        Assert.True(File.Exists(firstArtifact.Uri));
        Assert.Equal(new string('x', 128), await File.ReadAllTextAsync(firstArtifact.Uri!));
        var fetchedFirstArtifact = await runtime.GetArtifactAsync(first.Id, firstArtifact.Name);
        Assert.NotNull(fetchedFirstArtifact);
        Assert.Equal(new string('x', 128), fetchedFirstArtifact!.Text);

        var second = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = LargeArtifactHandler.HandlerId,
            Payload = new JsonObject
            {
                ["name"] = "second",
                ["size"] = 128
            }
        });
        await WaitForRunAsync(runtime, second.Id, ExecutionRunStatuses.Succeeded);
        await WaitForMissingRunAsync(runtime, first.Id);
        await WaitForMissingFileAsync(firstArtifact.Uri!);

        Assert.False(File.Exists(firstArtifact.Uri));
        var secondArtifact = Assert.Single(await runtime.ListArtifactsAsync(second.Id));
        Assert.True(File.Exists(secondArtifact.Uri));
    }

    [Fact]
    public async Task LocalRuntime_MaintenanceReportsStatusAndPrunesTerminalRunsOnDemand()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-execution-maintenance-{Guid.NewGuid():N}.sqlite");
        var artifactDirectory = Path.Combine(Path.GetTempPath(), $"vyral-execution-maintenance-{Guid.NewGuid():N}");
        var runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = path,
            ArtifactDirectory = artifactDirectory,
            Limits = new ExecutionRuntimeLimits
            {
                MaxArtifactBytes = 4_096,
                MaxArtifactInlineBytes = 32
            },
            MaxActiveRuns = 8,
            MaxRetainedTerminalRuns = 50,
            DefaultListLimit = 20,
            MaxListLimit = 100
        });
        runtime.RegisterHandler(new LargeArtifactHandler());

        var first = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = LargeArtifactHandler.HandlerId,
            Payload = new JsonObject
            {
                ["name"] = "first",
                ["size"] = 128
            }
        });
        await WaitForRunAsync(runtime, first.Id, ExecutionRunStatuses.Succeeded);
        var firstArtifact = Assert.Single(await runtime.ListArtifactsAsync(first.Id));
        Assert.True(File.Exists(firstArtifact.Uri));

        var second = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = LargeArtifactHandler.HandlerId,
            Payload = new JsonObject
            {
                ["name"] = "second",
                ["size"] = 128
            }
        });
        await WaitForRunAsync(runtime, second.Id, ExecutionRunStatuses.Succeeded);

        IExecutionRuntimeMaintenance maintenance = runtime;
        var status = await maintenance.GetMaintenanceStatusAsync();
        Assert.Equal("local.sqlite", status.RuntimeKind);
        Assert.Equal(2, status.RunCounts[ExecutionRunStatuses.Succeeded]);
        Assert.Equal(2, status.RowCounts["artifacts"]);
        Assert.True(status.ArtifactFileCount >= 2);
        Assert.True(status.ArtifactBytes >= 256);

        var reconcile = await maintenance.ReconcileDispatchAsync(new ExecutionMaintenanceDispatchReconcileRequest
        {
            DryRun = true,
            Limit = 10
        });
        Assert.True(reconcile.DryRun);
        Assert.Empty(reconcile.CandidateRunIds);
        Assert.Equal(0, reconcile.Dispatched);

        var dryRun = await maintenance.PruneAsync(new ExecutionMaintenancePruneRequest
        {
            DryRun = true,
            RetainTerminalRuns = 1
        });
        Assert.True(dryRun.DryRun);
        Assert.Equal(1, dryRun.Runs);
        Assert.Contains(first.Id, dryRun.RunIds);
        Assert.True(File.Exists(firstArtifact.Uri));
        Assert.NotNull(await runtime.GetRunAsync(first.Id));

        var pruned = await maintenance.PruneAsync(new ExecutionMaintenancePruneRequest
        {
            DryRun = false,
            RetainTerminalRuns = 1
        });
        Assert.False(pruned.DryRun);
        Assert.Equal(1, pruned.Runs);
        Assert.Contains(first.Id, pruned.RunIds);
        Assert.True(pruned.Events > 0);
        Assert.Equal(1, pruned.Artifacts);
        Assert.Equal(1, pruned.ArtifactDirectories);
        Assert.False(File.Exists(firstArtifact.Uri));
        Assert.Null(await runtime.GetRunAsync(first.Id));
        Assert.NotNull(await runtime.GetRunAsync(second.Id));

        var after = await maintenance.GetMaintenanceStatusAsync();
        Assert.Equal(1, after.RunCounts[ExecutionRunStatuses.Succeeded]);
    }

    [Fact]
    public async Task LocalRuntime_ExternalWorkerLeasesHeartbeatsCheckpointsAndCompletesIdempotently()
    {
        var runtime = CreateRuntime();
        runtime.RegisterExternalHandler(ExternalWorkerHandler.Descriptor);

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = ExternalWorkerHandler.HandlerId,
            Payload = new JsonObject { ["paymentIntent"] = "pi_123" }
        });
        Assert.Equal(ExecutionRunStatuses.Queued, accepted.Status);

        var lease = await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "go-worker-a",
            HandlerIds = { ExternalWorkerHandler.HandlerId },
            TtlSeconds = 30
        });
        Assert.NotNull(lease);
        Assert.Equal(accepted.Id, lease!.Run.Id);
        Assert.Equal(ExecutionRunStatuses.Running, lease.Run.Status);
        Assert.Equal(1, lease.Run.Attempt);
        Assert.Null(await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "go-worker-b",
            HandlerIds = { ExternalWorkerHandler.HandlerId }
        }));

        var heartbeat = await runtime.HeartbeatExternalLeaseAsync(new ExecutionExternalWorkerHeartbeatRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker-a",
            TtlSeconds = 60
        });
        Assert.True(heartbeat.ExpiresAtUtc > lease.ExpiresAtUtc);

        var reported = await runtime.ReportExternalLeaseAsync(new ExecutionExternalWorkerReportRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker-a",
            Update = new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 1,
                Progress = 0.5,
                CurrentStep = "authorizing"
            }
        });
        Assert.Equal(0.5, reported.Progress);
        Assert.Equal("authorizing", reported.CurrentStep);

        await runtime.RecordExternalLeaseEventAsync(new ExecutionExternalWorkerEventRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker-a",
            Type = ExecutionEventTypes.Log,
            Message = "Authorization requested.",
            Details = new JsonObject { ["provider"] = "test" }
        });

        var artifact = await runtime.PutExternalLeaseArtifactAsync(new ExecutionExternalWorkerArtifactRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker-a",
            Artifact = new ExecutionArtifactWrite
            {
                Name = "authorization",
                Kind = ExecutionArtifactKinds.Json,
                Content = new JsonObject { ["state"] = "requested" }
            }
        });
        Assert.Equal(accepted.Id, artifact.RunId);

        var checkpoint = await runtime.CheckpointExternalLeaseAsync(new ExecutionExternalWorkerCheckpointRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker-a",
            Checkpoint = new ExecutionCheckpointWrite
            {
                Key = "payment-request",
                Content = new JsonObject { ["idempotencyKey"] = "pay:pi_123" }
            }
        });
        Assert.Equal(accepted.Id, checkpoint.RunId);
        var checkpointFromLease = await runtime.GetExternalLeaseCheckpointAsync(new ExecutionExternalWorkerCheckpointReadRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker-a",
            Key = "payment-request"
        });
        Assert.NotNull(checkpointFromLease);
        Assert.Equal("pay:pi_123", (await runtime.GetCheckpointAsync(accepted.Id, "payment-request"))!.Content!["idempotencyKey"]!.GetValue<string>());
        Assert.Single(await runtime.ListArtifactsAsync(accepted.Id));
        Assert.Contains(await runtime.GetHistoryAsync(accepted.Id), item => item.Type == ExecutionEventTypes.Log && item.Message == "Authorization requested.");

        var completion = new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker-a",
            Result = ExecutionRunResult.Succeeded(new JsonObject { ["paymentId"] = "pay_123" })
        };
        var completed = await runtime.CompleteExternalLeaseAsync(completion);
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
        Assert.Equal("pay_123", completed.Result!["paymentId"]!.GetValue<string>());

        var replayed = await runtime.CompleteExternalLeaseAsync(completion);
        Assert.Equal(completed.Id, replayed.Id);
        Assert.Equal(ExecutionRunStatuses.Succeeded, replayed.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.HeartbeatExternalLeaseAsync(new ExecutionExternalWorkerHeartbeatRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker-a"
        }));
    }

    [Fact]
    public async Task LocalRuntime_ExternalWorkerRecoversExpiredLeaseRejectsStaleCompletionAndHonorsCancellation()
    {
        var runtime = CreateRuntime();
        runtime.RegisterExternalHandler(ExternalWorkerHandler.Descriptor);
        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = ExternalWorkerHandler.HandlerId });
        var first = await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "go-worker-a",
            HandlerIds = { ExternalWorkerHandler.HandlerId },
            TtlSeconds = 0.01
        });
        Assert.NotNull(first);

        await Task.Delay(100);
        var recovered = await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "go-worker-b",
            HandlerIds = { ExternalWorkerHandler.HandlerId },
            RunId = accepted.Id,
            TtlSeconds = 60
        });
        Assert.NotNull(recovered);
        Assert.Equal(2, recovered!.Run.Attempt);
        Assert.NotEqual(first!.LeaseToken, recovered.LeaseToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = first.LeaseKey,
            LeaseToken = first.LeaseToken,
            WorkerId = "go-worker-a",
            Result = ExecutionRunResult.Succeeded()
        }));

        var cancelled = await runtime.CancelRunAsync(accepted.Id);
        Assert.True(cancelled!.CancellationRequested);
        var completed = await runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = recovered.LeaseKey,
            LeaseToken = recovered.LeaseToken,
            WorkerId = "go-worker-b",
            Result = ExecutionRunResult.Succeeded()
        });
        Assert.Equal(ExecutionRunStatuses.Cancelled, completed.Status);
        Assert.Equal(ExecutionFailureClasses.Cancelled, completed.FailureClass);
    }

    [Fact]
    public async Task LocalRuntime_ExternalWorkerDuplicateDeliveryLetsExactlyOneWorkerClaimRun()
    {
        var runtime = CreateRuntime();
        runtime.RegisterExternalHandler(ExternalWorkerHandler.Descriptor);
        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = ExternalWorkerHandler.HandlerId });

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = $"go-worker-{index}",
            HandlerIds = { ExternalWorkerHandler.HandlerId },
            RunId = accepted.Id,
            TtlSeconds = 60
        })));

        var lease = Assert.Single(claims, item => item is not null);
        Assert.Equal(accepted.Id, lease!.Run.Id);
        var running = await runtime.GetRunAsync(accepted.Id);
        Assert.Equal(ExecutionRunStatuses.Running, running!.Status);
        Assert.Equal(1, running.Attempt);
    }

    [Fact]
    public async Task LocalRuntime_ExternalWorkerRetriesThenFailsAfterRetryExhaustion()
    {
        var runtime = CreateRuntime();
        runtime.RegisterExternalHandler(ExternalWorkerHandler.Descriptor);
        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = ExternalWorkerHandler.HandlerId,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 2, InitialDelaySeconds = 0, MaxDelaySeconds = 0 }
        });
        var first = await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "go-worker-a",
            HandlerIds = { ExternalWorkerHandler.HandlerId }
        });
        Assert.NotNull(first);
        var waiting = await runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = first!.LeaseKey,
            LeaseToken = first.LeaseToken,
            WorkerId = "go-worker-a",
            Result = ExecutionRunResult.Failed(ExecutionFailureClasses.Transient, "callback unavailable")
        });
        Assert.Equal(ExecutionRunStatuses.Waiting, waiting.Status);

        var second = await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "go-worker-b",
            HandlerIds = { ExternalWorkerHandler.HandlerId },
            RunId = accepted.Id
        });
        Assert.NotNull(second);
        var failed = await runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = second!.LeaseKey,
            LeaseToken = second.LeaseToken,
            WorkerId = "go-worker-b",
            Result = ExecutionRunResult.Failed(ExecutionFailureClasses.Transient, "callback unavailable")
        });
        Assert.Equal(ExecutionRunStatuses.Failed, failed.Status);
        Assert.Equal(2, failed.Attempt);
    }

    [Fact]
    public async Task LocalRuntime_ExternalWorkerSuspendsForApprovalAndReceivesDurableOutcomeOnNextLease()
    {
        var runtime = CreateRuntime();
        runtime.RegisterExternalHandler(ExternalWorkerHandler.Descriptor);
        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = ExternalWorkerHandler.HandlerId });
        var first = await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "go-worker-a",
            HandlerIds = { ExternalWorkerHandler.HandlerId }
        });
        Assert.NotNull(first);

        var suspended = await runtime.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = first!.LeaseKey,
            LeaseToken = first.LeaseToken,
            WorkerId = "go-worker-a",
            Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
            Name = "approval",
            TimeoutAtUtc = DateTime.UtcNow.AddMinutes(5)
        });
        Assert.True(suspended.Suspended);
        Assert.Equal(ExecutionRunStatuses.Waiting, suspended.Run.Status);
        await runtime.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = "approval",
            Payload = new JsonObject { ["decision"] = "approved" }
        });

        var resumedLease = await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "go-worker-b",
            HandlerIds = { ExternalWorkerHandler.HandlerId },
            RunId = accepted.Id
        });
        Assert.NotNull(resumedLease);
        var outcome = await runtime.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = resumedLease!.LeaseKey,
            LeaseToken = resumedLease.LeaseToken,
            WorkerId = "go-worker-b",
            Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
            Name = "approval",
            TimeoutAtUtc = DateTime.UtcNow.AddMinutes(5)
        });
        Assert.False(outcome.Suspended);
        Assert.NotNull(outcome.Outcome);
        Assert.Equal(ExecutionWaitOutcomes.ExternalEvent, outcome.Outcome!.Outcome);
        Assert.Equal("approved", outcome.Outcome.Event!.Payload!["decision"]!.GetValue<string>());

        var completed = await runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = resumedLease.LeaseKey,
            LeaseToken = resumedLease.LeaseToken,
            WorkerId = "go-worker-b",
            Result = ExecutionRunResult.Succeeded()
        });
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
    }

    [Fact]
    public async Task LocalRuntime_DurablyWaitsForExternalApprovalAndReplaysHandlerWithEvent()
    {
        var runtime = CreateRuntime();
        var handler = new ApprovalWaitHandler();
        runtime.RegisterHandler(handler);

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = ApprovalWaitHandler.HandlerId });
        await handler.WaitRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Waiting);

        await runtime.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = "approval",
            Payload = new JsonObject { ["decision"] = "approved" }
        });
        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal("external_event", completed.Result!["outcome"]!.GetValue<string>());
        Assert.Equal("approved", completed.Result["decision"]!.GetValue<string>());
        Assert.Equal(2, handler.Executions);
        var history = await runtime.GetHistoryAsync(accepted.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitRegistered);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitResumed);
    }

    [Fact]
    public async Task LocalRuntime_ConsumesExternalEventRaisedBeforeDurableWaitRegistration()
    {
        var runtime = CreateRuntime();
        var handler = new ApprovalWaitHandler();
        runtime.RegisterHandler(handler);
        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = ApprovalWaitHandler.HandlerId,
            ScheduledAtUtc = DateTime.UtcNow.AddMilliseconds(150)
        });
        await runtime.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = "approval",
            Payload = new JsonObject { ["decision"] = "approved" }
        });

        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal("approved", completed.Result!["decision"]!.GetValue<string>());
        Assert.Equal(1, handler.Executions);
        var history = await runtime.GetHistoryAsync(accepted.Id);
        Assert.DoesNotContain(history, item => item.Type == ExecutionEventTypes.WaitRegistered);
    }

    [Fact]
    public async Task LocalRuntime_DurablyWaitsForTimerAndTimeoutWithoutHoldingWorkerSlot()
    {
        var runtime = CreateRuntime();
        var timerHandler = new TimerWaitHandler();
        var timeoutHandler = new TimeoutWaitHandler();
        runtime.RegisterHandler(timerHandler);
        runtime.RegisterHandler(timeoutHandler);

        var timerRun = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = TimerWaitHandler.HandlerId });
        var timeoutRun = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = TimeoutWaitHandler.HandlerId });
        await timerHandler.WaitRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await timeoutHandler.WaitRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForHistoryEventAsync(runtime, timerRun.Id, ExecutionEventTypes.WaitRegistered);
        await WaitForHistoryEventAsync(runtime, timeoutRun.Id, ExecutionEventTypes.WaitRegistered);

        var timerCompleted = await WaitForRunAsync(runtime, timerRun.Id, ExecutionRunStatuses.Succeeded);
        var timeoutCompleted = await WaitForRunAsync(runtime, timeoutRun.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(ExecutionWaitOutcomes.Timer, timerCompleted.Result!["outcome"]!.GetValue<string>());
        Assert.Equal(ExecutionWaitOutcomes.TimedOut, timeoutCompleted.Result!["outcome"]!.GetValue<string>());
        Assert.Equal(2, timerHandler.Executions);
        Assert.Equal(2, timeoutHandler.Executions);
    }

    [Fact]
    public async Task LocalRuntime_RecoversPersistedExternalEventWaitAfterRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-execution-durable-wait-{Guid.NewGuid():N}.sqlite");
        var first = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions { DatabasePath = path });
        var firstHandler = new ApprovalWaitHandler();
        first.RegisterHandler(firstHandler);
        var accepted = await first.StartRunAsync(new ExecutionRunRequest { HandlerId = ApprovalWaitHandler.HandlerId });
        await firstHandler.WaitRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRunAsync(first, accepted.Id, ExecutionRunStatuses.Waiting);

        var recovered = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions { DatabasePath = path });
        var recoveredHandler = new ApprovalWaitHandler();
        recovered.RegisterHandler(recoveredHandler);
        await recovered.DispatchReadyRunsAsync(recoverInterruptedRuns: true);
        await recovered.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = accepted.Id,
            Name = "approval",
            Payload = new JsonObject { ["decision"] = "approved" }
        });

        var completed = await WaitForRunAsync(recovered, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal("approved", completed.Result!["decision"]!.GetValue<string>());
        Assert.Equal(1, recoveredHandler.Executions);
    }

    [Fact]
    public async Task LocalRuntime_CancelsSuspendedDurableWaitWithoutWaitingForTimer()
    {
        var runtime = CreateRuntime();
        var handler = new TimerWaitHandler();
        runtime.RegisterHandler(handler);
        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest { HandlerId = TimerWaitHandler.HandlerId });
        await handler.WaitRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Waiting);

        var cancelled = await runtime.CancelRunAsync(accepted.Id);
        Assert.Equal(ExecutionRunStatuses.Cancelled, cancelled!.Status);
        await Task.Delay(400);
        var persisted = await runtime.GetRunAsync(accepted.Id);
        Assert.Equal(ExecutionRunStatuses.Cancelled, persisted!.Status);
        Assert.Equal(1, handler.Executions);
    }

    [Fact]
    public async Task LocalRuntime_EnforcesProductTenantWorkerArtifactAndAuditBoundaries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-execution-isolation-{Guid.NewGuid():N}.sqlite");
        var runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = path,
            ProductPolicies =
            [
                new LocalExecutionProductPolicy
                {
                    ProductId = "product-a",
                    AllowedHandlerIds = new HashSet<string>(StringComparer.Ordinal) { IsolatedHandler.HandlerId },
                    AllowedTenantIds = new HashSet<string>(StringComparer.Ordinal) { "tenant-a" },
                    AllowedServiceIdentities = new HashSet<string>(StringComparer.Ordinal) { "worker-product-a" },
                    MaxPayloadBytes = 64,
                    ArtifactPrefix = "product-a/tenant-a/",
                    RedactedJsonPropertyNames = new HashSet<string>(StringComparer.Ordinal) { "secret" }
                }
            ]
        });
        runtime.RegisterHandler(new IsolatedHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = IsolatedHandler.HandlerId
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = "not-allowed",
            Scope = new ExecutionScope { ProductId = "product-a", TenantId = "tenant-a" }
        }));

        var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
        {
            HandlerId = IsolatedHandler.HandlerId,
            Scope = new ExecutionScope { ProductId = "product-a", TenantId = "tenant-a", ServiceIdentity = "api-product-a" },
            Payload = new JsonObject { ["value"] = "safe" }
        });
        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal("product-a", completed.Scope!.ProductId);
        var artifact = Assert.Single(await runtime.ListArtifactsAsync(accepted.Id));
        Assert.StartsWith("product-a/tenant-a/", artifact.Name);
        var audit = Assert.Single(await runtime.GetHistoryAsync(accepted.Id), item => item.Type == "isolated.audit");
        Assert.Equal("product-a", audit.Context["productId"]);
        Assert.Equal("tenant-a", audit.Context["tenantId"]);
        Assert.Equal("[redacted]", audit.Details!["secret"]!.GetValue<string>());
    }

    [Fact]
    public async Task ArtifactRecordIngestion_RetriesAfterObjectPublishBeforeRecordWrite()
    {
        var runtime = CreateRuntime();
        var objects = new FileObjectStore(Path.Combine(
            Path.GetTempPath(),
            $"vyral-artifact-retry-{Guid.NewGuid():N}"));
        var records = new RetryOnceArtifactRecordStore();
        var ingestionOptions = new ArtifactRecordIngestionOptions();
        var ingestion = new ArtifactRecordIngestionService(records, objects, ingestionOptions);
        var adapter = new ExecutionRuntimeArtifactRecordIngestionAdapter(
            runtime,
            objects,
            ingestion,
            ingestionOptions);
        var manifest = new ArtifactRecordIngestManifest
        {
            Collection = "receipts",
            Record = new VyralRecord
            {
                Id = "receipt-1",
                PartitionKey = "tenant-a",
                Type = "test.receipt"
            },
            Artifact = new ArtifactRecordDescriptor
            {
                Container = "published",
                Key = "receipts/receipt-1.json",
                ContentType = "application/json"
            }
        };

        await using var content = new MemoryStream("{\"ok\":true}"u8.ToArray(), writable: false);
        var accepted = await adapter.StartAsync(manifest, content, "artifact-retry-1");
        var completed = await WaitForRunAsync(runtime, accepted.Id, ExecutionRunStatuses.Succeeded);

        Assert.Equal(2, completed.Attempt);
        Assert.Equal(2, records.UpsertCalls);
        Assert.NotNull(records.Record);
        var published = await objects.GetObjectAsync(new ObjectReadRequest
        {
            Container = "published",
            Key = "receipts/receipt-1.json"
        });
        Assert.NotNull(published);
        await using (published!.Content)
        using (var reader = new StreamReader(published.Content))
        {
            Assert.Equal("{\"ok\":true}", await reader.ReadToEndAsync());
        }
        Assert.Contains(
            await runtime.GetHistoryAsync(accepted.Id),
            item => item.Type == ExecutionEventTypes.RetryScheduled);
    }

    private static LocalExecutionRuntime CreateRuntime(ExecutionRuntimeLimits? limits = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-execution-{Guid.NewGuid():N}.sqlite");
        return new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = path,
            Limits = limits ?? ExecutionRuntimeLimits.Default,
            MaxActiveRuns = 8,
            MaxRetainedTerminalRuns = 50,
            DefaultListLimit = 20,
            MaxListLimit = 100
        });
    }

    private static async Task<ExecutionRun> WaitForRunAsync(IExecutionRuntime runtime, string id, string status)
    {
        ExecutionRun? run = null;
        // Durable waits resume through background dispatch after a persisted state transition.
        // Five seconds keeps this contract assertion stable when the full solution is executing
        // several test hosts concurrently on a constrained CI worker.
        for (var i = 0; i < 200; i++)
        {
            run = await runtime.GetRunAsync(id);
            if (run?.Status == status)
            {
                return run;
            }

            await Task.Delay(25);
        }

        var history = await runtime.GetHistoryAsync(id);
        var historySummary = string.Join(", ", history.Select(item => $"{item.Type}:{item.Status}"));
        throw new InvalidOperationException($"Run {id} did not reach {status}. Last status: {run?.Status ?? "(missing)"}. History: {historySummary}");
    }

    private static async Task WaitForHistoryEventAsync(IExecutionRuntime runtime, string id, string eventType)
    {
        IReadOnlyList<ExecutionTraceEvent> history = Array.Empty<ExecutionTraceEvent>();
        for (var i = 0; i < 200; i++)
        {
            history = await runtime.GetHistoryAsync(id);
            if (history.Any(item => item.Type == eventType))
            {
                return;
            }

            await Task.Delay(25);
        }

        var historySummary = string.Join(", ", history.Select(item => $"{item.Type}:{item.Status}"));
        throw new InvalidOperationException($"Run {id} did not record {eventType}. History: {historySummary}");
    }

    private static async Task WaitForMissingRunAsync(IExecutionRuntime runtime, string id)
    {
        ExecutionRun? run = null;
        for (var i = 0; i < 100; i++)
        {
            run = await runtime.GetRunAsync(id);
            if (run is null)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"Run {id} was not pruned. Last status: {run?.Status ?? "(missing)"}");
    }

    private static async Task WaitForMissingFileAsync(string path)
    {
        for (var i = 0; i < 100; i++)
        {
            if (!File.Exists(path))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"Artifact file {path} was not pruned.");
    }

    private static async Task<EmbeddingJob> WaitForEmbeddingJobAsync(ExecutionRuntimeEmbeddingJobAdapter adapter, string id, string status)
    {
        EmbeddingJob? job = null;
        for (var i = 0; i < 100; i++)
        {
            job = await adapter.GetAsync(id);
            if (job?.Status == status)
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"Embedding job {id} did not reach {status}. Last status: {job?.Status ?? "(missing)"}");
    }

    private static async Task InsertRunAsync(string path, ExecutionRun run)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO vyral_execution_runs (
                id,
                handler_id,
                plugin_id,
                status,
                idempotency_key,
                correlation_id,
                created_at_utc,
                updated_at_utc,
                scheduled_at_utc,
                started_at_utc,
                completed_at_utc,
                run_json
            )
            VALUES (
                $id,
                $handler_id,
                $plugin_id,
                $status,
                $idempotency_key,
                $correlation_id,
                $created_at_utc,
                $updated_at_utc,
                $scheduled_at_utc,
                $started_at_utc,
                $completed_at_utc,
                $run_json
            );";
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$handler_id", run.HandlerId);
        command.Parameters.AddWithValue("$plugin_id", run.PluginId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$idempotency_key", run.IdempotencyKey ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$correlation_id", run.CorrelationId);
        command.Parameters.AddWithValue("$created_at_utc", run.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", run.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$scheduled_at_utc", run.ScheduledAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$started_at_utc", run.StartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$completed_at_utc", run.CompletedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$run_json", JsonSerializer.Serialize(run, ExecutionJson.Options));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRowsForRunAsync(string path, string tableName, string runIdColumnName, string runId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE {runIdColumnName} = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    private static async Task<long> ReadLongAsync(string path, string sql)
    {
        var value = await ReadScalarAsync(path, sql);
        return Convert.ToInt64(value);
    }

    private static async Task<string> ReadStringAsync(string path, string sql)
    {
        var value = await ReadScalarAsync(path, sql);
        return Convert.ToString(value) ?? string.Empty;
    }

    private static async Task<object?> ReadScalarAsync(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static class ExternalWorkerHandler
    {
        public const string HandlerId = "test.external.payment-callback";

        public static ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            PluginId = "test.external",
            DisplayName = "External payment callback"
        };
    }

    private sealed class ApprovalWaitHandler : IExecutionHandler
    {
        public const string HandlerId = "test.durable-wait.approval";
        private int _executions;

        public int Executions => Volatile.Read(ref _executions);
        public TaskCompletionSource WaitRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Approval wait"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executions);
            WaitRegistered.TrySetResult();
            var wait = await context.WaitForExternalEventAsync("approval", DateTime.UtcNow.AddSeconds(5), ct);
            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["outcome"] = wait.Outcome,
                ["decision"] = wait.Event?.Payload?["decision"]?.GetValue<string>()
            });
        }
    }

    private sealed class TimerWaitHandler : IExecutionHandler
    {
        public const string HandlerId = "test.durable-wait.timer";
        private int _executions;

        public int Executions => Volatile.Read(ref _executions);
        public TaskCompletionSource WaitRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Timer wait"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executions);
            WaitRegistered.TrySetResult();
            var wait = await context.WaitForTimerAsync("reminder", DateTime.UtcNow.AddMilliseconds(300), new JsonObject { ["kind"] = "reminder" }, ct);
            return ExecutionRunResult.Succeeded(new JsonObject { ["outcome"] = wait.Outcome });
        }
    }

    private sealed class TimeoutWaitHandler : IExecutionHandler
    {
        public const string HandlerId = "test.durable-wait.timeout";
        private int _executions;

        public int Executions => Volatile.Read(ref _executions);
        public TaskCompletionSource WaitRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Timeout wait"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executions);
            WaitRegistered.TrySetResult();
            var wait = await context.WaitForExternalEventAsync("approval", DateTime.UtcNow.AddMilliseconds(300), ct);
            return ExecutionRunResult.Succeeded(new JsonObject { ["outcome"] = wait.Outcome });
        }
    }

    private sealed class IsolatedHandler : IExecutionHandler
    {
        public const string HandlerId = "test.isolated";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Isolated handler"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            await context.RecordEventAsync("isolated.audit", details: new JsonObject
            {
                ["secret"] = "must-not-reach-audit",
                ["safe"] = "visible"
            }, ct: ct);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "product-a/tenant-a/summary",
                Kind = ExecutionArtifactKinds.Json,
                Content = new JsonObject { ["ok"] = true }
            }, ct);
            return ExecutionRunResult.Succeeded();
        }
    }

    private sealed class CountingPlugin : IExecutionPlugin
    {
        public const string PluginId = "test.counting";

        public ExecutionPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = PluginId,
            Name = "Counting plugin",
            Version = "1.0.0",
            Handlers =
            {
                new ExecutionHandlerDescriptor
                {
                    HandlerId = CountingHandler.HandlerId,
                    PluginId = PluginId,
                    DisplayName = "Count items"
                }
            }
        };

        public IReadOnlyList<IExecutionHandler> Handlers { get; } = new[] { new CountingHandler() };
    }

    private sealed class CountingHandler : IExecutionHandler
    {
        public const string HandlerId = "test.counting.count";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            PluginId = CountingPlugin.PluginId,
            DisplayName = "Count items"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var items = context.Run.Payload?["items"]?.AsArray() ?? new JsonArray();
            var total = 0;
            for (var i = 0; i < items.Count; i++)
            {
                total += i + 1;
                await context.ReportAsync(new ExecutionRunUpdate
                {
                    Requested = items.Count,
                    Attempted = i + 1,
                    Succeeded = i + 1,
                    Failed = 0,
                    Progress = (i + 1) / (double)items.Count,
                    CurrentStep = $"item:{i}"
                }, ct);
            }

            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "summary",
                Kind = ExecutionArtifactKinds.Json,
                Content = new JsonObject
                {
                    ["count"] = items.Count,
                    ["total"] = total
                }
            }, ct);

            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["count"] = items.Count,
                ["total"] = total
            });
        }
    }

    private sealed class BlockingHandler : IExecutionHandler
    {
        public const string HandlerId = "test.blocking";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Blocking handler"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 0,
                Progress = 0,
                CurrentStep = "blocked"
            }, ct);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ExecutionRunResult.Succeeded();
        }
    }

    private sealed class SerialBlockingHandler : IExecutionHandler
    {
        public const string HandlerId = "test.serial.blocking";
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedCount;

        public int StartedCount => Volatile.Read(ref _startedCount);
        public Task FirstStarted => _firstStarted.Task;

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Serial blocking handler",
            ConcurrencyKey = "test.serial"
        };

        public void Release()
        {
            _release.TrySetResult();
        }

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var started = Interlocked.Increment(ref _startedCount);
            if (started == 1)
            {
                _firstStarted.TrySetResult();
            }

            await _release.Task.WaitAsync(ct);
            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["started"] = started
            });
        }
    }

    private sealed class RecordImportConcurrencyBlocker : IExecutionHandler
    {
        public const string HandlerId = "test.record-import-concurrency-blocker";
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Record import concurrency blocker",
            ConcurrencyKey = ExecutionRuntimeRecordImportJobAdapter.StoreWriteConcurrencyKey
        };

        public void Release() => _release.TrySetResult();

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return ExecutionRunResult.Succeeded();
        }
    }

    private sealed class CountingRecordImportStore : IRecordCollectionStore
    {
        private int _batchUpsertCalls;

        public int BatchUpsertCalls => Volatile.Read(ref _batchUpsertCalls);

        public Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteCollectionAsync(string collection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertRecordAsync(string collection, VyralRecord record, RecordWritePrecondition? precondition, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _batchUpsertCalls);
            return Task.FromResult(new RecordBatchUpsertResult
            {
                Collection = collection,
                Requested = request.Records.Count,
                Attempted = request.Records.Count,
                Succeeded = request.Records.Count
            });
        }

        public Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RetryOnceArtifactRecordStore : IRecordCollectionStore
    {
        private int _upsertCalls;

        public int UpsertCalls => Volatile.Read(ref _upsertCalls);
        public VyralRecord? Record { get; private set; }

        public Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<RecordCollectionPolicy?>(new RecordCollectionPolicy { Name = collection });

        public Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _upsertCalls) == 1)
            {
                throw new InvalidOperationException("simulated transient record write failure");
            }

            Record = record;
            return Task.CompletedTask;
        }

        public Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteCollectionAsync(string collection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RetryOnceHandler : IExecutionHandler
    {
        public const string HandlerId = "test.retry.once";
        private int _attempts;

        public int Attempts => _attempts;

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Retry once"
        };

        public Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt == 1)
            {
                return Task.FromResult(ExecutionRunResult.Failed(ExecutionFailureClasses.Transient, "first attempt fails"));
            }

            return Task.FromResult(ExecutionRunResult.Succeeded(new JsonObject
            {
                ["attempt"] = attempt
            }));
        }
    }

    private sealed class DelayedCountingHandler : IExecutionHandler
    {
        public const string HandlerId = "test.delayed.counting";
        private int _executions;

        public int Executions => _executions;

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Delayed count items"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executions);
            await Task.Delay(50, ct);
            var items = context.Run.Payload?["items"]?.AsArray() ?? new JsonArray();
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = items.Count,
                Attempted = items.Count,
                Succeeded = items.Count,
                Failed = 0,
                Progress = 1
            }, ct);
            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["count"] = items.Count
            });
        }
    }

    private sealed class RunOwnedStateHandler : IExecutionHandler
    {
        public const string HandlerId = "test.run-owned-state";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Run-owned state"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var name = context.Run.Payload?["name"]?.GetValue<string>() ?? context.Run.Id;
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = 1,
                Attempted = 1,
                Succeeded = 1,
                Progress = 1,
                CurrentStep = "state-written"
            }, ct);

            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "state",
                Kind = ExecutionArtifactKinds.Json,
                Content = new JsonObject
                {
                    ["name"] = name
                }
            }, ct);

            var lease = await context.TryAcquireLeaseAsync(
                $"test.run-owned-state:{context.Run.Id}",
                ttlSeconds: 300,
                metadata: new JsonObject { ["name"] = name },
                ct);
            Assert.NotNull(lease);

            await context.ScheduleTimerAsync(
                "follow-up",
                DateTime.UtcNow.AddMinutes(5),
                new JsonObject { ["name"] = name },
                ct);
            await context.RaiseEventAsync(
                "state-ready",
                new JsonObject { ["name"] = name },
                ct);

            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["name"] = name
            });
        }
    }

    private sealed class LargeArtifactHandler : IExecutionHandler
    {
        public const string HandlerId = "test.large-artifact";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Large artifact"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            var name = context.Run.Payload?["name"]?.GetValue<string>() ?? "artifact";
            var size = context.Run.Payload?["size"]?.GetValue<int>() ?? 128;
            var text = new string('x', size);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = name,
                Kind = ExecutionArtifactKinds.Text,
                MediaType = "text/plain",
                Text = text
            }, ct);

            return ExecutionRunResult.Succeeded(new JsonObject
            {
                ["name"] = name,
                ["size"] = size
            });
        }
    }

    private sealed class InvalidTransitionHandler : IExecutionHandler
    {
        public const string HandlerId = "test.invalid-transition";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Invalid transition"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
        {
            await context.ReportAsync(new ExecutionRunUpdate
            {
                Status = ExecutionRunStatuses.Queued
            }, ct);
            return ExecutionRunResult.Succeeded();
        }
    }

    private sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
    {
        public string ProviderId => "execution-test";
        public string ModelId => "execution-test-v1";
        public int Dimensions => 2;

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        {
            var first = text.Length % 2 == 0 ? 1f : 0f;
            return Task.FromResult(new[] { first, 1f - first });
        }
    }
}
