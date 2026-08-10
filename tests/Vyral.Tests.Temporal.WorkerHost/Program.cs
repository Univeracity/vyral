using System.Text.Json.Nodes;
using Temporalio.Client;
using Vyral.Execution;
using Vyral.Execution.Temporal;
using Vyral.Execution.Temporal.Hosting;
using Vyral.Execution.Temporal.Postgres;
using Vyral.Local;

return await RunAsync();

static async Task<int> RunAsync()
{
    try
    {
        var configuration = WorkerConfiguration.FromEnvironment();
        var store = new PostgresTemporalExecutionProjectionStore(configuration.ProjectionOptions);
        await store.InitializeAsync();
        var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(
            configuration.ExecutionOptions.TargetHost)
        {
            Namespace = configuration.ExecutionOptions.Namespace,
            Tls = configuration.TlsOptions,
            ApiKey = configuration.ApiKey
        });
        var coordinator = new TemporalSdkCoordinatorClient(
            client,
            configuration.ExecutionOptions.TaskQueue);
        var runtime = new TemporalExecutionRuntimeAdapter(
            store,
            coordinator,
            configuration.ExecutionOptions);
        runtime.RegisterHandler(new RestartingActivityHandler());
        runtime.RegisterHandler(new AbsentDispatchHandler());
        runtime.RegisterHandler(new RestartingWaitHandler());

        using var worker = new TemporalExecutionWorker(
            client,
            configuration.ExecutionOptions.TaskQueue,
            runtime,
            store,
            configuration.ExecutionOptions,
            $"qualification-worker-{Environment.ProcessId}",
            new FileObjectStore(configuration.ObjectRoot));
        var reconciler = new TemporalExecutionOutboxReconciler(
            store,
            coordinator,
            configuration.ExecutionOptions);
        var workerTask = worker.ExecuteAsync();
        var reconcilerTask = ReconcileUntilStoppedAsync(reconciler);

        await Task.Delay(250);
        if (workerTask.IsCompleted || reconcilerTask.IsCompleted)
            throw new InvalidOperationException("Temporal qualification worker did not remain active.");
        await File.WriteAllTextAsync(configuration.ReadyFile, "ready\n");
        Console.WriteLine("temporal-worker-host=ready topology=redacted");
        await Task.WhenAny(workerTask, reconcilerTask);
        throw new InvalidOperationException("Temporal qualification worker stopped unexpectedly.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"temporal-worker-host=failed type={ex.GetType().Name}");
        return 1;
    }
}

static async Task ReconcileUntilStoppedAsync(TemporalExecutionOutboxReconciler reconciler)
{
    while (true)
    {
        try
        {
            _ = await reconciler.ReconcileAsync();
        }
        catch
        {
            // Dispatch state is durable. A transient dependency failure remains eligible for the
            // next bounded reconciliation pass rather than terminating the worker host.
        }
        await Task.Delay(100);
    }
}

sealed record WorkerConfiguration(
    TemporalExecutionOptions ExecutionOptions,
    PostgresTemporalProjectionOptions ProjectionOptions,
    string ObjectRoot,
    string ReadyFile,
    TlsOptions TlsOptions,
    string? ApiKey)
{
    public static WorkerConfiguration FromEnvironment()
    {
        var liveGate = string.Equals(Optional("VYRAL_TEMPORAL_LIVE_GATE"), "1", StringComparison.Ordinal);
        var temporalTls = Boolean("VYRAL_EXECUTION_TEMPORAL_REQUIRE_TLS", liveGate);
        var postgresTls = Boolean("VYRAL_TEMPORAL_POSTGRES_REQUIRE_TLS", liveGate);
        var apiKey = Optional("VYRAL_TEMPORAL_API_KEY");
        var rootCa = Optional("VYRAL_TEMPORAL_TLS_ROOT_CA_PATH");
        var clientCert = Optional("VYRAL_TEMPORAL_TLS_CLIENT_CERT_PATH");
        var clientKey = Optional("VYRAL_TEMPORAL_TLS_CLIENT_KEY_PATH");
        if ((clientCert is null) != (clientKey is null))
            throw new InvalidOperationException("Temporal worker mTLS certificate and key must be configured together.");
        if (!temporalTls && (apiKey is not null || clientCert is not null || rootCa is not null))
            throw new InvalidOperationException("Temporal worker credentials and certificate settings require TLS.");

        var execution = new TemporalExecutionOptions
        {
            AdapterId = Required("VYRAL_EXECUTION_TEMPORAL_ADAPTER_ID"),
            AdapterNamespace = Required("VYRAL_EXECUTION_TEMPORAL_ADAPTER_NAMESPACE"),
            TargetHost = Required("VYRAL_EXECUTION_TEMPORAL_TARGET_HOST"),
            Namespace = Required("VYRAL_EXECUTION_TEMPORAL_NAMESPACE"),
            TaskQueue = Required("VYRAL_EXECUTION_TEMPORAL_TASK_QUEUE"),
            WorkerDeploymentName = Optional("VYRAL_EXECUTION_TEMPORAL_WORKER_DEPLOYMENT_NAME") ??
                "vyral-execution",
            WorkerBuildId = Optional("VYRAL_EXECUTION_TEMPORAL_WORKER_BUILD_ID"),
            ArtifactObjectContainer = Required("VYRAL_EXECUTION_TEMPORAL_ARTIFACT_OBJECT_CONTAINER"),
            RequireTls = temporalTls,
            ReconciliationBatchSize = 100
        };
        var projection = new PostgresTemporalProjectionOptions
        {
            ConnectionString = Required("VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING"),
            DatabaseSchema = Required("VYRAL_TEMPORAL_POSTGRES_SCHEMA"),
            RequireTls = postgresTls,
            DispatchClaimSeconds = 5,
            DispatchRetrySeconds = 1
        };
        execution.Validate();
        projection.Validate();
        return new WorkerConfiguration(
            execution,
            projection,
            Path.GetFullPath(Required("VYRAL_TEMPORAL_GATE_OBJECT_ROOT")),
            Path.GetFullPath(Required("VYRAL_TEMPORAL_GATE_WORKER_READY_FILE")),
            temporalTls
                ? new TlsOptions
                {
                    ServerRootCACert = ReadPem(rootCa, "root CA"),
                    Domain = Optional("VYRAL_TEMPORAL_TLS_DOMAIN"),
                    ClientCert = ReadPem(clientCert, "client certificate"),
                    ClientPrivateKey = ReadPem(clientKey, "client private key")
                }
                : new TlsOptions { Disabled = true },
            apiKey);
    }

    private static byte[]? ReadPem(string? path, string description)
    {
        if (path is null) return null;
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists || file.Length is < 1 or > 1_048_576)
            throw new InvalidOperationException($"Temporal worker {description} file is missing or invalid.");
        return File.ReadAllBytes(fullPath);
    }

    private static bool Boolean(string name, bool fallback)
    {
        var value = Optional(name);
        if (value is null) return fallback;
        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Temporal worker setting '{name}' must be true or false.");
    }

    private static string? Optional(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string Required(string name)
    {
        var value = Optional(name);
        return value is null
            ? throw new InvalidOperationException($"Required worker setting '{name}' is missing.")
            : value;
    }
}

sealed class RestartingActivityHandler : IExecutionHandler
{
    public const string HandlerId = "qualification.temporal.process-restart.activity";
    public const string CheckpointKey = "activity-worker";

    public ExecutionHandlerDescriptor Descriptor { get; } = new()
    {
        HandlerId = HandlerId,
        DisplayName = "Temporal process restart activity"
    };

    public async Task<ExecutionRunResult> ExecuteAsync(
        IExecutionRunContext context,
        CancellationToken ct = default)
    {
        var checkpoint = await context.GetCheckpointAsync(CheckpointKey, ct);
        if (checkpoint is null)
        {
            await context.PutCheckpointAsync(new ExecutionCheckpointWrite
            {
                Key = CheckpointKey,
                Content = new JsonObject { ["workerProcessId"] = Environment.ProcessId }
            }, ct);
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            throw new InvalidOperationException("The restart qualification activity was not interrupted.");
        }

        var checkpointContent = checkpoint.Content ??
            throw new InvalidOperationException("The restart qualification checkpoint has no content.");
        return ExecutionRunResult.Succeeded(new JsonObject
        {
            ["restarted"] = true,
            ["initialWorkerProcessId"] = checkpointContent["workerProcessId"]?.GetValue<int>(),
            ["completionWorkerProcessId"] = Environment.ProcessId
        });
    }
}

sealed class AbsentDispatchHandler : IExecutionHandler
{
    public const string HandlerId = "qualification.temporal.process-restart.dispatch";

    public ExecutionHandlerDescriptor Descriptor { get; } = new()
    {
        HandlerId = HandlerId,
        DisplayName = "Temporal absent worker dispatch"
    };

    public Task<ExecutionRunResult> ExecuteAsync(
        IExecutionRunContext context,
        CancellationToken ct = default) =>
        Task.FromResult(ExecutionRunResult.Succeeded(new JsonObject
        {
            ["workerProcessId"] = Environment.ProcessId
        }));
}

sealed class RestartingWaitHandler : IExecutionHandler
{
    public const string HandlerId = "qualification.temporal.process-restart.wait";
    public const string EventName = "restart-approval";

    public ExecutionHandlerDescriptor Descriptor { get; } = new()
    {
        HandlerId = HandlerId,
        DisplayName = "Temporal process restart durable wait"
    };

    public async Task<ExecutionRunResult> ExecuteAsync(
        IExecutionRunContext context,
        CancellationToken ct = default)
    {
        var outcome = await context.WaitForExternalEventAsync(
            EventName,
            DateTime.UtcNow.AddMinutes(2),
            ct);
        return ExecutionRunResult.Succeeded(new JsonObject
        {
            ["workerProcessId"] = Environment.ProcessId,
            ["outcome"] = outcome.Outcome,
            ["decision"] = outcome.Event?.Payload?["decision"]?.GetValue<string>()
        });
    }
}
