using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Temporalio.Client;
using Vyral.Execution;
using Vyral.Execution.Temporal;
using Vyral.Execution.Temporal.Postgres;

namespace Vyral.Tests.Temporal;

public sealed class TemporalWorkerProcessRecoveryTests
{
    private const string ActivityHandlerId = "qualification.temporal.process-restart.activity";
    private const string ActivityCheckpointKey = "activity-worker";
    private const string DispatchHandlerId = "qualification.temporal.process-restart.dispatch";
    private const string WaitHandlerId = "qualification.temporal.process-restart.wait";
    private const string WaitEventName = "restart-approval";

    [TemporalContainerFact]
    public async Task TemporalContainer_RecoversAcrossRealWorkerProcessRestarts()
    {
        await using var host = await ProcessRecoveryHost.CreateAsync();

        var firstWorker = await host.StartWorkerAsync();
        var activityRun = await host.Runtime.StartRunAsync(Request(ActivityHandlerId, "activity"));
        var checkpoint = await WaitForCheckpointAsync(host.Runtime, activityRun.Id, ActivityCheckpointKey);
        Assert.NotNull(checkpoint.Content);
        Assert.Equal(firstWorker.ProcessId, checkpoint.Content!["workerProcessId"]!.GetValue<int>());
        await host.StopWorkerAsync(firstWorker);

        var secondWorker = await host.StartWorkerAsync();
        var recoveredActivity = await WaitForStatusAsync(
            host.Runtime,
            activityRun.Id,
            ExecutionRunStatuses.Succeeded,
            TimeSpan.FromSeconds(75));
        Assert.Equal(1, recoveredActivity.Attempt);
        Assert.True(recoveredActivity.Result!["restarted"]!.GetValue<bool>());
        Assert.Equal(firstWorker.ProcessId, recoveredActivity.Result["initialWorkerProcessId"]!.GetValue<int>());
        Assert.Equal(secondWorker.ProcessId, recoveredActivity.Result["completionWorkerProcessId"]!.GetValue<int>());
        Assert.NotEqual(firstWorker.ProcessId, secondWorker.ProcessId);
        await host.StopWorkerAsync(secondWorker);

        var dispatchRun = await host.PersistRunWithoutDispatchAsync(Request(DispatchHandlerId, "dispatch"));
        var beforeReconciliation = await host.Runtime.GetAdapterStatusAsync();
        Assert.Equal(1, beforeReconciliation.Details!["pendingStartDispatches"]!.GetValue<int>());
        var reconciliation = await host.ReconcileStartsAsync();
        Assert.Equal(1, reconciliation.Delivered);
        Assert.Equal(0, reconciliation.Failed);

        var thirdWorker = await host.StartWorkerAsync();
        var recoveredDispatch = await WaitForStatusAsync(
            host.Runtime,
            dispatchRun.Id,
            ExecutionRunStatuses.Succeeded,
            TimeSpan.FromSeconds(30));
        Assert.Equal(thirdWorker.ProcessId, recoveredDispatch.Result!["workerProcessId"]!.GetValue<int>());

        var waitRun = await host.Runtime.StartRunAsync(Request(WaitHandlerId, "wait"));
        var waiting = await WaitForStatusAsync(
            host.Runtime,
            waitRun.Id,
            ExecutionRunStatuses.Waiting,
            TimeSpan.FromSeconds(30));
        Assert.Equal(1, waiting.Attempt);
        await host.StopWorkerAsync(thirdWorker);

        _ = await host.Runtime.RaiseEventAsync(new ExecutionExternalEventRequest
        {
            RunId = waitRun.Id,
            Name = WaitEventName,
            Payload = new JsonObject { ["decision"] = "approved" }
        });
        var fourthWorker = await host.StartWorkerAsync();
        var recoveredWait = await WaitForStatusAsync(
            host.Runtime,
            waitRun.Id,
            ExecutionRunStatuses.Succeeded,
            TimeSpan.FromSeconds(30));
        Assert.Equal(2, recoveredWait.Attempt);
        Assert.Equal(fourthWorker.ProcessId, recoveredWait.Result!["workerProcessId"]!.GetValue<int>());
        Assert.Equal(ExecutionWaitOutcomes.ExternalEvent, recoveredWait.Result["outcome"]!.GetValue<string>());
        Assert.Equal("approved", recoveredWait.Result["decision"]!.GetValue<string>());
        Assert.NotEqual(thirdWorker.ProcessId, fourthWorker.ProcessId);

        var history = await host.Runtime.GetHistoryAsync(waitRun.Id);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitRegistered);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.ExternalEventRaised);
        Assert.Contains(history, item => item.Type == ExecutionEventTypes.WaitResumed);
    }

    private static ExecutionRunRequest Request(string handlerId, string scenario) => new()
    {
        HandlerId = handlerId,
        IdempotencyKey = $"process-restart:{scenario}:{Guid.NewGuid():N}"
    };

    private static async Task<ExecutionCheckpoint> WaitForCheckpointAsync(
        IExecutionRuntime runtime,
        string runId,
        string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            var checkpoint = await runtime.GetCheckpointAsync(runId, key, timeout.Token);
            if (checkpoint is not null) return checkpoint;
            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException("Temporal worker did not persist the restart checkpoint.");
    }

    private static async Task<ExecutionRun> WaitForStatusAsync(
        IExecutionRuntime runtime,
        string runId,
        string expectedStatus,
        TimeSpan duration)
    {
        using var timeout = new CancellationTokenSource(duration);
        ExecutionRun? current = null;
        while (!timeout.IsCancellationRequested)
        {
            current = await runtime.GetRunAsync(runId, ct: timeout.Token);
            if (current?.Status == expectedStatus) return current;
            if (current is not null && ExecutionRunStatuses.IsTerminal(current.Status))
            {
                throw new InvalidOperationException(
                    $"Run reached terminal status {current.Status} instead of {expectedStatus}; " +
                    $"failure: {current.FailureClass ?? "none"}; error: {current.Error ?? "none"}.");
            }
            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException(
            $"Run did not reach {expectedStatus}; last status was {current?.Status ?? "missing"}.");
    }

    private sealed class DescriptorOnlyHandler : IExecutionHandler
    {
        public DescriptorOnlyHandler(string handlerId, string displayName)
        {
            Descriptor = new ExecutionHandlerDescriptor
            {
                HandlerId = handlerId,
                DisplayName = displayName
            };
        }

        public ExecutionHandlerDescriptor Descriptor { get; }

        public Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Descriptor-only handlers must execute in the worker process.");
    }

    private sealed class ProcessRecoveryHost : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _schema;
        private readonly string _objectRoot;
        private readonly string _readyRoot;
        private readonly string _workerAssembly;
        private readonly PostgresTemporalExecutionProjectionStore _store;
        private readonly ITemporalCoordinatorClient _coordinator;
        private WorkerProcess? _activeWorker;

        private ProcessRecoveryHost(
            string connectionString,
            string schema,
            string objectRoot,
            string readyRoot,
            string workerAssembly,
            TemporalExecutionOptions options,
            TemporalExecutionRuntimeAdapter runtime,
            PostgresTemporalExecutionProjectionStore store,
            ITemporalCoordinatorClient coordinator)
        {
            _connectionString = connectionString;
            _schema = schema;
            _objectRoot = objectRoot;
            _readyRoot = readyRoot;
            _workerAssembly = workerAssembly;
            Options = options;
            Runtime = runtime;
            _store = store;
            _coordinator = coordinator;
        }

        public TemporalExecutionOptions Options { get; }
        public TemporalExecutionRuntimeAdapter Runtime { get; }

        public static async Task<ProcessRecoveryHost> CreateAsync()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var connectionString = Required("VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING");
            var targetHost = Optional("VYRAL_EXECUTION_TEMPORAL_TARGET_HOST") ?? "127.0.0.1:37233";
            var temporalNamespace = Optional("VYRAL_EXECUTION_TEMPORAL_NAMESPACE") ?? "vyral-qualification";
            var schema = TemporalGateSettings.SchemaName(
                $"vyral_temporal_restart_{suffix[..16]}",
                "restart",
                suffix);
            var stateBase = Optional("VYRAL_TEMPORAL_GATE_OBJECT_ROOT") ?? Path.GetTempPath();
            var objectRoot = Path.GetFullPath(Path.Combine(
                stateBase,
                TemporalGateSettings.PortableName($"restart-objects-{suffix}", "restart-objects", suffix)));
            var readyRoot = Path.GetFullPath(Path.Combine(
                stateBase,
                TemporalGateSettings.PortableName($"restart-ready-{suffix}", "restart-ready", suffix)));
            var options = new TemporalExecutionOptions
            {
                AdapterId = "temporal-process-restart-gate",
                AdapterNamespace = TemporalGateSettings.PortableName(
                    $"restart-{suffix[..16]}",
                    "restart",
                    suffix),
                TargetHost = targetHost,
                Namespace = temporalNamespace,
                TaskQueue = TemporalGateSettings.PortableName(
                    $"vyral-temporal-restart-{suffix}",
                    "restart",
                    suffix),
                ArtifactObjectContainer = "vyral-temporal-restart",
                RequireTls = TemporalGateSettings.TemporalTlsRequired,
                ReconciliationBatchSize = 100
            };
            var projectionOptions = new PostgresTemporalProjectionOptions
            {
                ConnectionString = connectionString,
                DatabaseSchema = schema,
                RequireTls = TemporalGateSettings.PostgresTlsRequired,
                DispatchClaimSeconds = 5,
                DispatchRetrySeconds = 1
            };
            options.Validate();
            projectionOptions.Validate();
            Directory.CreateDirectory(objectRoot);
            Directory.CreateDirectory(readyRoot);

            var store = new PostgresTemporalExecutionProjectionStore(projectionOptions);
            await store.InitializeAsync();
            var client = await TemporalClient.ConnectAsync(
                TemporalGateSettings.ClientOptions(targetHost, temporalNamespace));
            var coordinator = new TemporalSdkCoordinatorClient(client, options.TaskQueue);
            var runtime = new TemporalExecutionRuntimeAdapter(store, coordinator, options);
            runtime.RegisterHandler(new DescriptorOnlyHandler(ActivityHandlerId, "Activity restart descriptor"));
            runtime.RegisterHandler(new DescriptorOnlyHandler(DispatchHandlerId, "Dispatch recovery descriptor"));
            runtime.RegisterHandler(new DescriptorOnlyHandler(WaitHandlerId, "Wait restart descriptor"));

            return new ProcessRecoveryHost(
                connectionString,
                schema,
                objectRoot,
                readyRoot,
                ResolveWorkerAssembly(),
                options,
                runtime,
                store,
                coordinator);
        }

        public async Task<ExecutionRun> PersistRunWithoutDispatchAsync(ExecutionRunRequest request)
        {
            ExecutionContractValidator.ValidateRunRequest(request, Options.Limits);
            var descriptor = Runtime.ListHandlers().Single(item => item.HandlerId == request.HandlerId);
            var run = TemporalExecutionDialect.CreateRun(request, descriptor, Options);
            var creation = await _store.CreateRunWithPendingStartAsync(new TemporalProjectionRunStart
            {
                Run = run,
                WorkflowId = TemporalExecutionIdentity.CreateWorkflowId(Options.AdapterNamespace, run.Id),
                Generation = 1,
                ProjectionRevision = 1,
                DispatchId = Guid.NewGuid().ToString("N"),
                RequestHash = TemporalExecutionDialect.CreateRequestHash(run)
            });
            Assert.False(creation.Replayed);
            return creation.Run;
        }

        public Task<TemporalDispatchReconcileResult> ReconcileStartsAsync() =>
            new TemporalExecutionDispatchReconciler(
                _store,
                _coordinator,
                Options.AdapterNamespace).ReconcileAsync(Options.ReconciliationBatchSize);

        public async Task<WorkerProcess> StartWorkerAsync()
        {
            if (_activeWorker is not null)
                throw new InvalidOperationException("A Temporal qualification worker is already active.");

            var readyFile = Path.Combine(_readyRoot, $"ready-{Guid.NewGuid():N}");
            var start = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = FindRepositoryRoot(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(_workerAssembly);
            start.Environment["VYRAL_EXECUTION_TEMPORAL_ADAPTER_ID"] = Options.AdapterId;
            start.Environment["VYRAL_EXECUTION_TEMPORAL_ADAPTER_NAMESPACE"] = Options.AdapterNamespace;
            start.Environment["VYRAL_EXECUTION_TEMPORAL_TARGET_HOST"] = Options.TargetHost;
            start.Environment["VYRAL_EXECUTION_TEMPORAL_NAMESPACE"] = Options.Namespace;
            start.Environment["VYRAL_EXECUTION_TEMPORAL_TASK_QUEUE"] = Options.TaskQueue;
            start.Environment["VYRAL_EXECUTION_TEMPORAL_WORKER_DEPLOYMENT_NAME"] =
                Options.WorkerDeploymentName;
            if (Options.WorkerBuildId is not null)
            {
                start.Environment["VYRAL_EXECUTION_TEMPORAL_WORKER_BUILD_ID"] = Options.WorkerBuildId;
            }
            start.Environment["VYRAL_EXECUTION_TEMPORAL_ARTIFACT_OBJECT_CONTAINER"] = Options.ArtifactObjectContainer;
            start.Environment["VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING"] = _connectionString;
            start.Environment["VYRAL_TEMPORAL_POSTGRES_SCHEMA"] = _schema;
            start.Environment["VYRAL_TEMPORAL_GATE_OBJECT_ROOT"] = _objectRoot;
            start.Environment["VYRAL_TEMPORAL_GATE_WORKER_READY_FILE"] = readyFile;

            var process = Process.Start(start) ??
                throw new InvalidOperationException("Temporal qualification worker process did not start.");
            var worker = new WorkerProcess(
                process,
                process.StandardOutput.ReadToEndAsync(),
                process.StandardError.ReadToEndAsync());
            _activeWorker = worker;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!File.Exists(readyFile))
                {
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            $"Temporal qualification worker exited during startup: {await worker.RedactedOutputAsync()}");
                    }
                    await Task.Delay(50, timeout.Token);
                }
                return worker;
            }
            catch
            {
                await StopWorkerAsync(worker);
                throw;
            }
        }

        public async Task StopWorkerAsync(WorkerProcess worker)
        {
            if (!ReferenceEquals(_activeWorker, worker))
                throw new InvalidOperationException("Temporal qualification worker identity changed unexpectedly.");
            await worker.StopAsync();
            _activeWorker = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_activeWorker is not null) await StopWorkerAsync(_activeWorker);
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE;";
            await command.ExecuteNonQueryAsync();
            if (Directory.Exists(_objectRoot)) Directory.Delete(_objectRoot, recursive: true);
            if (Directory.Exists(_readyRoot)) Directory.Delete(_readyRoot, recursive: true);
        }

        private static string ResolveWorkerAssembly()
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var path = Path.Combine(
                FindRepositoryRoot(),
                "tests",
                "Vyral.Tests.Temporal.WorkerHost",
                "bin",
                configuration,
                "net10.0",
                "Vyral.Tests.Temporal.WorkerHost.dll");
            if (!File.Exists(path))
                throw new InvalidOperationException("The Temporal qualification worker host was not built.");
            return path;
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Vyral.sln"))) return current.FullName;
                current = current.Parent;
            }
            throw new InvalidOperationException("Vyral repository root was not found.");
        }

        private static string Required(string name) => Optional(name) ??
            throw new InvalidOperationException($"Required gate setting '{name}' is missing.");

        private static string? Optional(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    private sealed class WorkerProcess
    {
        private readonly Process _process;
        private readonly int _processId;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;

        public WorkerProcess(Process process, Task<string> stdout, Task<string> stderr)
        {
            _process = process;
            _processId = process.Id;
            _stdout = stdout;
            _stderr = stderr;
        }

        public int ProcessId => _processId;

        public async Task StopAsync()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                _process.Dispose();
            }
        }

        public async Task<string> RedactedOutputAsync()
        {
            var output = new StringBuilder();
            output.Append((await _stdout).Trim());
            if (output.Length > 0) output.Append(' ');
            output.Append((await _stderr).Trim());
            return output.ToString();
        }
    }
}
