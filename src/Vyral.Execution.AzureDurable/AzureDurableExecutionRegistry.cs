using System.Collections.Concurrent;
using Vyral.Execution;

namespace Vyral.Execution.AzureDurable;

public sealed class AzureDurableExecutionRegistry
{
    private readonly ConcurrentDictionary<string, IExecutionHandler> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExecutionPluginDescriptor> _plugins = new(StringComparer.Ordinal);
    private readonly ExecutionRuntimeLimits _limits;

    public AzureDurableExecutionRegistry(ExecutionRuntimeLimits? limits = null)
    {
        _limits = limits ?? ExecutionRuntimeLimits.Default;
    }

    public void RegisterHandler(IExecutionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ExecutionContractValidator.ValidateHandlerDescriptor(handler.Descriptor, _limits);
        if (string.IsNullOrWhiteSpace(handler.Descriptor.HandlerId))
        {
            throw new InvalidOperationException("Execution handler id is required.");
        }

        _handlers[handler.Descriptor.HandlerId.Trim()] = handler;
    }

    public void RegisterPlugin(IExecutionPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ExecutionContractValidator.ValidatePluginDescriptor(plugin.Descriptor, _limits);
        foreach (var handler in plugin.Handlers)
        {
            RegisterHandler(handler);
        }

        _plugins[plugin.Descriptor.PluginId.Trim()] = Clone(plugin.Descriptor);
    }

    public bool TryGetHandler(string handlerId, out IExecutionHandler handler)
    {
        handlerId = string.IsNullOrWhiteSpace(handlerId) ? string.Empty : handlerId.Trim();
        return _handlers.TryGetValue(handlerId, out handler!);
    }

    public IReadOnlyList<ExecutionPluginDescriptor> ListPlugins()
    {
        return _plugins.Values
            .Select(Clone)
            .OrderBy(descriptor => descriptor.PluginId, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<ExecutionHandlerDescriptor> ListHandlers()
    {
        return _handlers.Values
            .Select(handler => Clone(handler.Descriptor))
            .OrderBy(descriptor => descriptor.HandlerId, StringComparer.Ordinal)
            .ToList();
    }

    private static ExecutionPluginDescriptor Clone(ExecutionPluginDescriptor descriptor)
    {
        return new ExecutionPluginDescriptor
        {
            PluginId = descriptor.PluginId,
            Name = descriptor.Name,
            Version = descriptor.Version,
            Handlers = descriptor.Handlers.Select(Clone).ToList()
        };
    }

    private static ExecutionHandlerDescriptor Clone(ExecutionHandlerDescriptor descriptor)
    {
        return new ExecutionHandlerDescriptor
        {
            HandlerId = descriptor.HandlerId,
            PluginId = descriptor.PluginId,
            DisplayName = descriptor.DisplayName,
            Description = descriptor.Description,
            MaxAttempts = descriptor.MaxAttempts,
            ConcurrencyKey = descriptor.ConcurrencyKey,
            Tags = new Dictionary<string, string>(descriptor.Tags, StringComparer.Ordinal)
        };
    }
}
