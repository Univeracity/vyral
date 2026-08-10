using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Execution.Temporal;
using Vyral.Execution.Temporal.Hosting;
using Vyral.Execution.Temporal.Postgres;

namespace Vyral.Tests.Temporal;

public sealed class TemporalExecutionServiceCollectionTests
{
    [Fact]
    public void Registration_ComposesOnePortableRuntimeAndOperatorSurfaces()
    {
        var services = CreateServices();
        services
            .AddVyralTemporalExecution(CreateExecutionOptions())
            .AddPlugin<RegistrationPlugin>()
            .AddHandler<StandaloneHandler>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var runtime = provider.GetRequiredService<TemporalExecutionRuntimeAdapter>();

        Assert.Same(runtime, provider.GetRequiredService<IExecutionRuntime>());
        Assert.Same(runtime, provider.GetRequiredService<IExecutionRuntimeAdapter>());
        Assert.Same(runtime, provider.GetRequiredService<ITemporalExecutionHandlerResolver>());
        Assert.Same(
            provider.GetRequiredService<ITemporalExecutionRuntimeStore>(),
            provider.GetRequiredService<ITemporalExecutionProjectionStore>());
        Assert.IsType<TemporalSdkCoordinatorClient>(provider.GetRequiredService<ITemporalCoordinatorClient>());
        Assert.NotNull(provider.GetRequiredService<TemporalExecutionOutboxReconciler>());
        Assert.NotNull(provider.GetRequiredService<TemporalExecutionPreflight>());
        Assert.Contains(runtime.ListPlugins(), item => item.PluginId == RegistrationPlugin.PluginId);
        Assert.Contains(runtime.ListHandlers(), item => item.HandlerId == RegistrationPlugin.HandlerId);
        Assert.Contains(runtime.ListHandlers(), item => item.HandlerId == StandaloneHandler.HandlerId);
    }

    [Fact]
    public void HostedWorker_UsesOfficialHostLifecycleAndAddsDurableOutboxPump()
    {
        var services = CreateServices();
        var builder = services.AddVyralTemporalExecution(CreateExecutionOptions());
        builder.AddHostedWorker(new TemporalExecutionWorkerHostOptions
        {
            WorkerId = "qualification-worker",
            ReconciliationInterval = TimeSpan.FromMilliseconds(250),
            ReconciliationFailureDelay = TimeSpan.FromSeconds(1)
        });

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        Assert.Equal(2, hostedServices.Length);
        Assert.Contains(hostedServices, item =>
            item.GetType().FullName == "Temporalio.Extensions.Hosting.TemporalWorkerService");
        Assert.Contains(hostedServices, item =>
            item.GetType().Name == "TemporalExecutionOutboxHostedService");
        Assert.IsType<TemporalExecutionAttemptHandler>(
            provider.GetRequiredService<ITemporalExecutionAttemptHandler>());
        Assert.IsType<TemporalProjectionActivityExecutor>(
            provider.GetRequiredService<ITemporalExecutionActivityExecutor>());
        Assert.Throws<InvalidOperationException>(() => builder.AddHostedWorker(
            new TemporalExecutionWorkerHostOptions { WorkerId = "second-worker" }));
    }

    [Fact]
    public void Registration_RejectsInvalidOrAmbiguousHostCompositionBeforeStartup()
    {
        var invalidServices = CreateServices();
        Assert.Throws<InvalidOperationException>(() => invalidServices.AddVyralTemporalExecution(
            new TemporalExecutionOptions
            {
                TargetHost = "temporal.example.invalid:7233",
                RequireTls = false
            }));

        var services = CreateServices();
        var builder = services.AddVyralTemporalExecution(CreateExecutionOptions());
        Assert.Throws<InvalidOperationException>(() => services.AddVyralTemporalExecution(CreateExecutionOptions()));
        Assert.Throws<InvalidOperationException>(() => builder.AddHostedWorker(
            new TemporalExecutionWorkerHostOptions { WorkerId = "\nunsafe" }));
        Assert.Throws<InvalidOperationException>(() => builder.AddHostedWorker(
            new TemporalExecutionWorkerHostOptions
            {
                WorkerId = "worker",
                ReconciliationInterval = TimeSpan.Zero
            }));
    }

    [Fact]
    public void Registration_AllowsInlineOnlyRuntimeButRequiresObjectStoreWhenPreflightIsResolved()
    {
        var services = CreateServices(includeObjectStore: false);
        services.AddVyralTemporalExecution(CreateExecutionOptions());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.NotNull(provider.GetRequiredService<TemporalExecutionRuntimeAdapter>());
        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<TemporalExecutionPreflight>());
    }

    private static ServiceCollection CreateServices(bool includeObjectStore = true)
    {
        var services = new ServiceCollection();
        var store = new PostgresTemporalExecutionProjectionStore(new PostgresTemporalProjectionOptions
        {
            ConnectionString = "Host=127.0.0.1;Database=vyral;Username=vyral;Password=unused;SSL Mode=Disable",
            RequireTls = false
        });
        services.AddSingleton(store);
        services.AddSingleton<ITemporalExecutionRuntimeStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTemporalExecutionProjectionStore>());
        if (includeObjectStore)
        {
            services.AddSingleton<IObjectStore, NoOpObjectStore>();
        }
        services.AddSingleton<ITemporalClient>(TemporalClient.CreateLazy(
            new TemporalClientConnectOptions("127.0.0.1:7233")
            {
                Namespace = "default",
                Tls = new TlsOptions { Disabled = true }
            }));
        return services;
    }

    private static TemporalExecutionOptions CreateExecutionOptions() => new()
    {
        TargetHost = "127.0.0.1:7233",
        RequireTls = false,
        AdapterNamespace = "qualification",
        TaskQueue = "qualification-temporal",
        WorkerDeploymentName = "qualification-temporal"
    };

    private sealed class RegistrationPlugin : IExecutionPlugin
    {
        public const string PluginId = "qualification.temporal.registration";
        public const string HandlerId = "qualification.temporal.registration.plugin-handler";
        private readonly IExecutionHandler[] _handlers = [new PluginHandler()];

        public RegistrationPlugin()
        {
            Descriptor = ExecutionDescriptors.Plugin(
                PluginId,
                "Temporal registration qualification",
                "1.0.0",
                plugin => plugin.AddHandler(_handlers[0].Descriptor));
        }

        public ExecutionPluginDescriptor Descriptor { get; }
        public IReadOnlyList<IExecutionHandler> Handlers => _handlers;

        private sealed class PluginHandler : IExecutionHandler
        {
            public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
                HandlerId,
                "Temporal registered plugin handler",
                handler => handler.WithPluginId(PluginId));

            public Task<ExecutionRunResult> ExecuteAsync(
                IExecutionRunContext context,
                CancellationToken ct = default) =>
                Task.FromResult(ExecutionRunResult.Succeeded());
        }
    }

    private sealed class StandaloneHandler : IExecutionHandler
    {
        public const string HandlerId = "qualification.temporal.registration.standalone-handler";

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            DisplayName = "Temporal registered standalone handler"
        };

        public Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default) =>
            Task.FromResult(ExecutionRunResult.Succeeded());
    }

    private sealed class NoOpObjectStore : IObjectStore
    {
        public Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
