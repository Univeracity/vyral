using Microsoft.Extensions.DependencyInjection;
using Vyral.Execution;

namespace Vyral.Execution.Local;

public static class LocalExecutionRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddVyralLocalExecutionRuntime(
        this IServiceCollection services,
        LocalExecutionRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services.AddVyralLocalExecutionRuntime(_ => options);
    }

    public static IServiceCollection AddVyralLocalExecutionRuntime(
        this IServiceCollection services,
        Func<IServiceProvider, LocalExecutionRuntimeOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.AddSingleton(optionsFactory);
        services.AddSingleton(serviceProvider =>
        {
            var runtime = new LocalExecutionRuntime(serviceProvider.GetRequiredService<LocalExecutionRuntimeOptions>());
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
        services.AddSingleton<IExecutionRuntimeAdapter>(serviceProvider => serviceProvider.GetRequiredService<LocalExecutionRuntime>());
        services.AddSingleton<IExecutionRuntime>(serviceProvider => serviceProvider.GetRequiredService<LocalExecutionRuntime>());

        return services;
    }

    public static IServiceCollection AddVyralExecutionPlugin<TPlugin>(this IServiceCollection services)
        where TPlugin : class, IExecutionPlugin
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IExecutionPlugin, TPlugin>();
        return services;
    }

    public static IServiceCollection AddVyralExecutionHandler<THandler>(this IServiceCollection services)
        where THandler : class, IExecutionHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IExecutionHandler, THandler>();
        return services;
    }
}
