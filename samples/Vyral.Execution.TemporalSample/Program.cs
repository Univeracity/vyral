using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Vyral.Abstractions.Interfaces;
using Vyral.Execution;
using Vyral.Execution.Temporal;
using Vyral.Execution.Temporal.Hosting;
using Vyral.Execution.Temporal.Postgres;
using Vyral.Local;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    var mode = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "help";
    if (mode is "help" or "--help" or "-h")
    {
        PrintUsage();
        return 0;
    }
    if (mode is not ("worker" or "submit" or "status" or "preflight"))
    {
        Console.Error.WriteLine("Mode must be worker, submit, status, or preflight.");
        PrintUsage();
        return 2;
    }

    try
    {
        var configuration = SampleConfiguration.FromEnvironment();
        var store = new PostgresTemporalExecutionProjectionStore(configuration.ProjectionOptions);
        if (mode != "preflight")
        {
            await store.InitializeAsync();
        }

        var clientOptions = new TemporalClientConnectOptions(configuration.ExecutionOptions.TargetHost)
        {
            Namespace = configuration.ExecutionOptions.Namespace,
            Tls = configuration.ExecutionOptions.RequireTls
                ? new TlsOptions()
                : new TlsOptions { Disabled = true },
            ApiKey = configuration.ApiKey
        };
        ITemporalClient client = mode == "preflight"
            ? TemporalClient.CreateLazy(clientOptions)
            : await TemporalClient.ConnectAsync(clientOptions);

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Logging.ClearProviders();
        hostBuilder.Services.AddSingleton(client);
        hostBuilder.Services.AddSingleton(store);
        hostBuilder.Services.AddSingleton<ITemporalExecutionRuntimeStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTemporalExecutionProjectionStore>());
        hostBuilder.Services.AddSingleton<IObjectStore>(
            new FileObjectStore(configuration.ObjectRoot));
        var temporal = hostBuilder.Services
            .AddVyralTemporalExecution(configuration.ExecutionOptions)
            .AddPlugin<TemporalSamplePlugin>();
        if (mode == "worker")
        {
            temporal.AddHostedWorker(new TemporalExecutionWorkerHostOptions
            {
                WorkerId = $"temporal-sample-{Environment.ProcessId}"
            });
        }

        using var host = hostBuilder.Build();
        var runtime = host.Services.GetRequiredService<TemporalExecutionRuntimeAdapter>();

        return mode switch
        {
            "worker" => await RunWorkerAsync(host),
            "submit" => await SubmitAsync(
                runtime,
                host.Services.GetRequiredService<TemporalExecutionOutboxReconciler>()),
            "status" => await ShowStatusAsync(runtime),
            "preflight" => await RunPreflightAsync(
                host.Services.GetRequiredService<TemporalExecutionPreflight>()),
            _ => throw new InvalidOperationException("Unsupported Temporal sample mode.")
        };
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"Temporal sample failed ({ex.GetType().Name}). Check the documented configuration and dependency health.");
        return 1;
    }
}

static async Task<int> RunWorkerAsync(IHost host)
{
    using var shutdown = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;

    try
    {
        Console.WriteLine("mode=worker ready=true topology=redacted stop=Ctrl+C");
        await host.RunAsync(shutdown.Token);
        Console.WriteLine("mode=worker stopped=true");
        return 0;
    }
    finally
    {
        shutdown.Cancel();
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<int> SubmitAsync(
    TemporalExecutionRuntimeAdapter runtime,
    TemporalExecutionOutboxReconciler reconciler)
{
    var idempotencyKey = $"temporal-sample:{Guid.NewGuid():N}";
    var accepted = await runtime.StartRunAsync(new ExecutionRunRequest
    {
        HandlerId = TemporalSamplePlugin.HandlerId,
        PluginId = TemporalSamplePlugin.PluginId,
        IdempotencyKey = idempotencyKey,
        CorrelationId = idempotencyKey,
        Payload = new JsonObject
        {
            ["items"] = new JsonArray("portable", "durable", "observable")
        },
        RetryPolicy = new ExecutionRetryPolicy
        {
            MaxAttempts = 2,
            InitialDelaySeconds = 1,
            BackoffMultiplier = 2,
            MaxDelaySeconds = 5
        },
        Tags =
        {
            ["sample"] = "temporal"
        }
    });
    Console.WriteLine($"mode=submit run={accepted.Id} accepted={accepted.Status}");

    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    ExecutionRun? current = accepted;
    while (!ExecutionRunStatuses.IsTerminal(current.Status))
    {
        timeout.Token.ThrowIfCancellationRequested();
        _ = await reconciler.ReconcileAsync(ct: timeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        current = await runtime.GetRunAsync(accepted.Id, ct: timeout.Token)
            ?? throw new InvalidOperationException("Submitted Temporal run disappeared from the projection store.");
    }

    var artifacts = await runtime.ListArtifactsAsync(current.Id, timeout.Token);
    Console.WriteLine(
        $"run={current.Id} status={current.Status} attempt={current.Attempt} artifacts={artifacts.Count}");
    Console.WriteLine($"result={current.Result?.ToJsonString(ExecutionJson.Options) ?? "null"}");
    foreach (var artifact in artifacts)
    {
        var storage = artifact.Metadata.GetValueOrDefault("storage") ??
            (artifact.Metadata.GetValueOrDefault("inline") == "true" ? "inline" : "external");
        Console.WriteLine(
            $"artifact={artifact.Name} storage={storage} bytes={artifact.SizeBytes} hash={artifact.ContentHash[..16]}");
    }

    return current.Status == ExecutionRunStatuses.Succeeded ? 0 : 1;
}

static async Task<int> ShowStatusAsync(TemporalExecutionRuntimeAdapter runtime)
{
    var status = await runtime.GetAdapterStatusAsync();
    Console.WriteLine(
        $"mode=status adapter={status.Adapter.AdapterId} kind={status.Adapter.RuntimeKind} available={status.Available} status={status.Status}");
    Console.WriteLine(
        $"qualification={status.Details?["qualification"]} active={status.ActiveRuns ?? 0} " +
        $"coordinators={status.Details?["activeCoordinators"] ?? 0} " +
        $"starts={status.Details?["pendingStartDispatches"] ?? 0} " +
        $"signals={status.Details?["pendingSignalDispatches"] ?? 0} " +
        $"cancellations={status.Details?["pendingCancellationDispatches"] ?? 0}");
    return status.Available ? 0 : 1;
}

static async Task<int> RunPreflightAsync(TemporalExecutionPreflight preflight)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var result = await preflight.RunAsync(timeout.Token);
    Console.WriteLine(
        $"mode=preflight ready={result.Ready} blockers={result.BlockerCount} warnings={result.WarningCount} qualification={result.Qualification}");
    foreach (var check in result.Checks)
    {
        Console.WriteLine($"check={check.Name} outcome={check.Outcome} code={check.Code}");
    }
    if (result.Details.TryGetValue("workflowPollers", out var workflowPollers) &&
        result.Details.TryGetValue("activityPollers", out var activityPollers))
    {
        Console.WriteLine($"workerPollers workflow={workflowPollers} activity={activityPollers}");
    }
    if (result.Details.TryGetValue("workerVersioningMode", out var versioningMode) &&
        result.Details.TryGetValue("workerDeploymentNameHash", out var deploymentHash) &&
        result.Details.TryGetValue("workerBuildIdHash", out var buildHash) &&
        result.Details.TryGetValue("workflowCurrentBuildPollers", out var currentWorkflowPollers) &&
        result.Details.TryGetValue("activityCurrentBuildPollers", out var currentActivityPollers) &&
        result.Details.TryGetValue("workflowOtherBuildPollers", out var otherWorkflowPollers) &&
        result.Details.TryGetValue("activityOtherBuildPollers", out var otherActivityPollers))
    {
        Console.WriteLine(
            $"workerCompatibility mode={versioningMode} deploymentHash={deploymentHash} " +
            $"buildHash={buildHash} currentWorkflow={currentWorkflowPollers} " +
            $"currentActivity={currentActivityPollers} otherWorkflow={otherWorkflowPollers} " +
            $"otherActivity={otherActivityPollers}");
    }
    if (result.Details.TryGetValue("activeCoordinators", out var activeCoordinators) &&
        result.Details.TryGetValue("coordinatorsExamined", out var coordinatorsExamined) &&
        result.Details.TryGetValue("staleRunsDetected", out var staleRunsDetected))
    {
        Console.WriteLine(
            $"coordinatorHealth active={activeCoordinators} examined={coordinatorsExamined} " +
            $"staleDetected={staleRunsDetected}");
    }
    return result.Ready ? 0 : 1;
}

static void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run --project samples/Vyral.Execution.TemporalSample -- <worker|submit|status|preflight>");
}

sealed record SampleConfiguration(
    TemporalExecutionOptions ExecutionOptions,
    PostgresTemporalProjectionOptions ProjectionOptions,
    string ObjectRoot,
    string? ApiKey)
{
    public static SampleConfiguration FromEnvironment()
    {
        var targetHost = Required("VYRAL_EXECUTION_TEMPORAL_TARGET_HOST");
        var connectionString = Required("VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING");
        var temporalTls = Boolean("VYRAL_EXECUTION_TEMPORAL_REQUIRE_TLS", true);
        var postgresTls = Boolean("VYRAL_TEMPORAL_POSTGRES_REQUIRE_TLS", true);
        var execution = new TemporalExecutionOptions
        {
            AdapterId = Optional("VYRAL_EXECUTION_TEMPORAL_ADAPTER_ID") ?? "temporal-sample",
            AdapterNamespace = Optional("VYRAL_EXECUTION_TEMPORAL_ADAPTER_NAMESPACE") ?? "sample",
            TargetHost = targetHost,
            Namespace = Optional("VYRAL_EXECUTION_TEMPORAL_NAMESPACE") ?? "default",
            TaskQueue = Optional("VYRAL_EXECUTION_TEMPORAL_TASK_QUEUE") ?? "vyral-execution-sample",
            WorkerDeploymentName = Optional("VYRAL_EXECUTION_TEMPORAL_WORKER_DEPLOYMENT_NAME") ??
                "vyral-execution",
            WorkerBuildId = Optional("VYRAL_EXECUTION_TEMPORAL_WORKER_BUILD_ID"),
            ArtifactObjectContainer = Optional("VYRAL_EXECUTION_TEMPORAL_ARTIFACT_OBJECT_CONTAINER") ??
                "vyral-execution-sample",
            RequireTls = temporalTls,
            ReconciliationBatchSize = 100,
            Limits = new ExecutionRuntimeLimits
            {
                MaxArtifactInlineBytes = 1_024
            }
        };
        var projection = new PostgresTemporalProjectionOptions
        {
            ConnectionString = connectionString,
            DatabaseSchema = Optional("VYRAL_TEMPORAL_POSTGRES_SCHEMA") ?? "vyral_temporal_sample",
            RequireTls = postgresTls,
            DispatchClaimSeconds = 15,
            DispatchRetrySeconds = 1
        };
        execution.Validate();
        projection.Validate();

        return new SampleConfiguration(
            execution,
            projection,
            Path.GetFullPath(Optional("VYRAL_TEMPORAL_SAMPLE_OBJECT_ROOT") ??
                Path.Combine(".vyral", "temporal-sample-objects")),
            Optional("VYRAL_TEMPORAL_API_KEY"));
    }

    private static string Required(string name) => Optional(name) ??
        throw new InvalidOperationException($"Required environment variable '{name}' is missing.");

    private static string? Optional(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool Boolean(string name, bool fallback)
    {
        var value = Optional(name);
        if (value is null) return fallback;
        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Environment variable '{name}' must be true or false.");
    }
}

sealed class TemporalSamplePlugin : IExecutionPlugin
{
    public const string PluginId = "sample.temporal.work";
    public const string HandlerId = "sample.temporal.work.digest";

    private readonly IExecutionHandler[] _handlers = { new DigestHandler() };

    public TemporalSamplePlugin()
    {
        Descriptor = ExecutionDescriptors.Plugin(
            PluginId,
            "Temporal sample work",
            "1.0.0",
            plugin => plugin.AddHandler(_handlers[0].Descriptor));
    }

    public ExecutionPluginDescriptor Descriptor { get; }
    public IReadOnlyList<IExecutionHandler> Handlers => _handlers;

    private sealed class DigestHandler : IExecutionHandler
    {
        public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
            HandlerId,
            "Digest sample items",
            handler => handler
                .WithPluginId(PluginId)
                .WithDescription("Creates a digest and an object-store-backed audit artifact.")
                .WithTag("sample", "temporal"));

        public async Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default)
        {
            var items = context.Run.Payload?["items"]?.AsArray()
                .Select(item => item?.GetValue<string>() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray() ?? Array.Empty<string>();
            var digest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join("\n", items)))).ToLowerInvariant();

            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = items.Length,
                Attempted = items.Length,
                Succeeded = items.Length,
                Failed = 0,
                Progress = 1,
                CurrentStep = "digest",
                StatusDetails = new JsonObject { ["phase"] = "writing-artifact" }
            }, ct);
            await context.PutCheckpointAsync(new ExecutionCheckpointWrite
            {
                Key = "digest",
                Content = new JsonObject { ["sha256"] = digest }
            }, ct);
            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "temporal-sample-audit.txt",
                Kind = ExecutionArtifactKinds.Text,
                MediaType = "text/plain",
                Text = $"sha256={digest}\nitems={string.Join(",", items)}\n{new string('=', 4_096)}",
                Metadata =
                {
                    ["sample"] = "temporal"
                }
            }, ct);

            return ExecutionRunResult.Succeeded(
                new JsonObject
                {
                    ["itemCount"] = items.Length,
                    ["digest"] = digest
                },
                new JsonObject { ["phase"] = "completed" });
        }
    }
}
