using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Vyral.Abstractions.Interfaces;
using Vyral.Execution;

namespace Vyral.Execution.Temporal.Hosting;

/// <summary>
/// Process-local settings for the hosted Vyral Temporal worker and its durable outbox pump.
/// Provider credentials and Temporal client construction remain host-owned services.
/// </summary>
public sealed class TemporalExecutionWorkerHostOptions
{
    public required string WorkerId { get; init; }
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconciliationFailureDelay { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate(TemporalExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(WorkerId) ||
            WorkerId.Length > options.Limits.MaxIdChars ||
            WorkerId.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Temporal worker id must be 1-{options.Limits.MaxIdChars} non-control characters.");
        }
        ValidateDelay(ReconciliationInterval, nameof(ReconciliationInterval));
        ValidateDelay(ReconciliationFailureDelay, nameof(ReconciliationFailureDelay));
    }

    private static void ValidateDelay(TimeSpan value, string name)
    {
        if (value < TimeSpan.FromMilliseconds(100) || value > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                $"Temporal worker {name} must be between 100 milliseconds and 5 minutes.");
        }
    }
}

/// <summary>
/// Registers portable plugins and optionally the provider-owned hosted worker on one Temporal
/// runtime composition.
/// </summary>
public sealed class VyralTemporalExecutionBuilder
{
    private readonly TemporalExecutionOptions _options;

    internal VyralTemporalExecutionBuilder(IServiceCollection services, TemporalExecutionOptions options)
    {
        Services = services;
        _options = options;
    }

    public IServiceCollection Services { get; }

    public VyralTemporalExecutionBuilder AddPlugin<TPlugin>()
        where TPlugin : class, IExecutionPlugin
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IExecutionPlugin, TPlugin>());
        return this;
    }

    public VyralTemporalExecutionBuilder AddHandler<THandler>()
        where THandler : class, IExecutionHandler
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IExecutionHandler, THandler>());
        return this;
    }

    /// <summary>
    /// Adds the official Temporal Generic Host worker plus a bounded, cancellation-aware pump for
    /// Vyral's durable start, signal, and cancellation outboxes.
    /// </summary>
    public VyralTemporalExecutionBuilder AddHostedWorker(TemporalExecutionWorkerHostOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(hostOptions);
        hostOptions.Validate(_options);
        if (Services.Any(item => item.ServiceType == typeof(TemporalExecutionWorkerHostOptions)))
        {
            throw new InvalidOperationException("A Vyral Temporal hosted worker is already registered.");
        }

        Services.AddSingleton(hostOptions);
        Services.AddLogging();
        Services.TryAddSingleton<ITemporalExecutionAttemptHandler>(serviceProvider =>
            new TemporalExecutionAttemptHandler(
                serviceProvider.GetRequiredService<ITemporalExecutionHandlerResolver>(),
                serviceProvider.GetRequiredService<ITemporalExecutionRuntimeStore>(),
                serviceProvider.GetRequiredService<TemporalExecutionOptions>(),
                hostOptions.WorkerId,
                serviceProvider.GetService<IObjectStore>()));
        Services.TryAddSingleton<ITemporalExecutionActivityExecutor, TemporalProjectionActivityExecutor>();

        var deployment = TemporalWorkerCompatibility.Resolve(_options);
        Services
            .AddHostedTemporalWorker(
                _options.TaskQueue,
                TemporalWorkerCompatibility.CreateWorkerDeploymentOptions(deployment))
            .AddSingletonActivities<TemporalExecutionActivities>()
            .AddWorkflow<TemporalRunCoordinatorWorkflow>();
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, TemporalExecutionOutboxHostedService>());
        return this;
    }
}

/// <summary>
/// Dependency-injection composition for the API and worker sides of Vyral's Temporal adapter.
/// The host must register one <see cref="ITemporalClient"/>, one
/// <see cref="ITemporalExecutionRuntimeStore"/>, and an <see cref="IObjectStore"/> when preflight
/// or artifact offload is required.
/// </summary>
public static class TemporalExecutionServiceCollectionExtensions
{
    public static VyralTemporalExecutionBuilder AddVyralTemporalExecution(
        this IServiceCollection services,
        TemporalExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (services.Any(item => item.ServiceType == typeof(TemporalExecutionOptions)))
        {
            throw new InvalidOperationException("Vyral Temporal execution is already registered.");
        }

        services.AddSingleton(options);
        services.AddSingleton<ITemporalExecutionProjectionStore>(serviceProvider =>
            serviceProvider.GetRequiredService<ITemporalExecutionRuntimeStore>());
        services.TryAddSingleton<TemporalSdkCoordinatorClient>(serviceProvider =>
            new TemporalSdkCoordinatorClient(
                serviceProvider.GetRequiredService<ITemporalClient>(),
                options.TaskQueue));
        services.TryAddSingleton<ITemporalCoordinatorClient>(serviceProvider =>
            serviceProvider.GetRequiredService<TemporalSdkCoordinatorClient>());
        services.AddSingleton<TemporalExecutionRuntimeAdapter>(serviceProvider =>
        {
            var runtime = new TemporalExecutionRuntimeAdapter(
                serviceProvider.GetRequiredService<ITemporalExecutionRuntimeStore>(),
                serviceProvider.GetRequiredService<ITemporalCoordinatorClient>(),
                options);
            foreach (var plugin in serviceProvider.GetServices<IExecutionPlugin>())
            {
                runtime.RegisterPlugin(plugin);
            }
            foreach (var handler in serviceProvider.GetServices<IExecutionHandler>())
            {
                runtime.RegisterHandler(handler);
            }
            return runtime;
        });
        services.AddSingleton<ITemporalExecutionHandlerResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<TemporalExecutionRuntimeAdapter>());
        services.AddSingleton<IExecutionRuntimeAdapter>(serviceProvider =>
            serviceProvider.GetRequiredService<TemporalExecutionRuntimeAdapter>());
        services.AddSingleton<IExecutionRuntime>(serviceProvider =>
            serviceProvider.GetRequiredService<TemporalExecutionRuntimeAdapter>());
        services.TryAddSingleton<TemporalExecutionOutboxReconciler>();
        services.TryAddSingleton<TemporalExecutionPreflight>(serviceProvider =>
            new TemporalExecutionPreflight(
                serviceProvider.GetRequiredService<ITemporalClient>(),
                serviceProvider.GetRequiredService<ITemporalExecutionRuntimeStore>(),
                serviceProvider.GetRequiredService<IObjectStore>(),
                options));

        return new VyralTemporalExecutionBuilder(services, options);
    }
}

internal sealed class TemporalExecutionOutboxHostedService : BackgroundService
{
    private readonly TemporalExecutionOutboxReconciler _reconciler;
    private readonly TemporalExecutionWorkerHostOptions _options;
    private readonly ILogger<TemporalExecutionOutboxHostedService> _logger;

    public TemporalExecutionOutboxHostedService(
        TemporalExecutionOutboxReconciler reconciler,
        TemporalExecutionWorkerHostOptions options,
        ILogger<TemporalExecutionOutboxHostedService> logger)
    {
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = _options.ReconciliationInterval;
            try
            {
                var result = await _reconciler.ReconcileAsync(ct: stoppingToken);
                if (result.Examined > 0)
                {
                    _logger.LogInformation(
                        "Temporal outbox reconciliation examined {Examined}, delivered {Delivered}, and retained {Failed} item(s).",
                        result.Examined,
                        result.Delivered,
                        result.Failed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                delay = _options.ReconciliationFailureDelay;
                _logger.LogWarning(
                    "Temporal outbox reconciliation failed; durable items remain pending for a bounded retry.");
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
