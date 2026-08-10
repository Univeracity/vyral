namespace Vyral.Execution;

public static class ExecutionDescriptors
{
    public static ExecutionHandlerDescriptor Handler(
        string handlerId,
        string displayName,
        Action<ExecutionHandlerDescriptorBuilder>? configure = null,
        ExecutionRuntimeLimits? limits = null)
    {
        var builder = new ExecutionHandlerDescriptorBuilder(handlerId, displayName);
        configure?.Invoke(builder);
        return builder.Build(limits);
    }

    public static ExecutionPluginDescriptor Plugin(
        string pluginId,
        string name,
        string version,
        Action<ExecutionPluginDescriptorBuilder>? configure = null,
        ExecutionRuntimeLimits? limits = null)
    {
        var builder = new ExecutionPluginDescriptorBuilder(pluginId, name, version);
        configure?.Invoke(builder);
        return builder.Build(limits);
    }
}

public sealed class ExecutionHandlerDescriptorBuilder
{
    private readonly ExecutionHandlerDescriptor _descriptor;

    public ExecutionHandlerDescriptorBuilder(string handlerId, string displayName)
    {
        _descriptor = new ExecutionHandlerDescriptor
        {
            HandlerId = NormalizeRequired(handlerId, "Handler id"),
            DisplayName = NormalizeRequired(displayName, "Handler display name")
        };
    }

    public ExecutionHandlerDescriptorBuilder WithPluginId(string? pluginId)
    {
        _descriptor.PluginId = NormalizeOptional(pluginId);
        return this;
    }

    public ExecutionHandlerDescriptorBuilder WithDescription(string? description)
    {
        _descriptor.Description = NormalizeOptional(description);
        return this;
    }

    public ExecutionHandlerDescriptorBuilder WithMaxAttempts(int maxAttempts)
    {
        _descriptor.MaxAttempts = maxAttempts;
        return this;
    }

    public ExecutionHandlerDescriptorBuilder WithConcurrencyKey(string? concurrencyKey)
    {
        _descriptor.ConcurrencyKey = NormalizeOptional(concurrencyKey);
        return this;
    }

    public ExecutionHandlerDescriptorBuilder WithTag(string key, string value)
    {
        _descriptor.Tags[NormalizeRequired(key, "Tag key")] = value ?? string.Empty;
        return this;
    }

    public ExecutionHandlerDescriptor Build(ExecutionRuntimeLimits? limits = null)
    {
        var descriptor = Clone(_descriptor);
        ExecutionContractValidator.ValidateHandlerDescriptor(descriptor, limits);
        return descriptor;
    }

    internal ExecutionHandlerDescriptor BuildForPlugin(string pluginId, ExecutionRuntimeLimits? limits = null)
    {
        var descriptor = Clone(_descriptor);
        descriptor.PluginId ??= pluginId;
        ExecutionContractValidator.ValidateHandlerDescriptor(descriptor, limits);
        return descriptor;
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

    private static string NormalizeRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed class ExecutionPluginDescriptorBuilder
{
    private readonly List<ExecutionHandlerDescriptor> _handlers = new();
    private readonly ExecutionPluginDescriptor _descriptor;

    public ExecutionPluginDescriptorBuilder(string pluginId, string name, string version)
    {
        _descriptor = new ExecutionPluginDescriptor
        {
            PluginId = NormalizeRequired(pluginId, "Plugin id"),
            Name = NormalizeRequired(name, "Plugin name"),
            Version = NormalizeRequired(version, "Plugin version")
        };
    }

    public ExecutionPluginDescriptorBuilder WithVersion(string version)
    {
        _descriptor.Version = NormalizeRequired(version, "Plugin version");
        return this;
    }

    public ExecutionPluginDescriptorBuilder AddHandler(ExecutionHandlerDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var clone = Clone(descriptor);
        clone.PluginId ??= _descriptor.PluginId;
        _handlers.Add(clone);
        return this;
    }

    public ExecutionPluginDescriptorBuilder AddHandler(
        string handlerId,
        string displayName,
        Action<ExecutionHandlerDescriptorBuilder>? configure = null)
    {
        var builder = new ExecutionHandlerDescriptorBuilder(handlerId, displayName);
        configure?.Invoke(builder);
        _handlers.Add(builder.BuildForPlugin(_descriptor.PluginId));
        return this;
    }

    public ExecutionPluginDescriptor Build(ExecutionRuntimeLimits? limits = null)
    {
        var descriptor = new ExecutionPluginDescriptor
        {
            PluginId = _descriptor.PluginId,
            Name = _descriptor.Name,
            Version = _descriptor.Version,
            Handlers = _handlers.Select(Clone).ToList()
        };
        ExecutionContractValidator.ValidatePluginDescriptor(descriptor, limits);
        return descriptor;
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

    private static string NormalizeRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return value.Trim();
    }
}

public sealed class DelegateExecutionHandler : IExecutionHandler
{
    private readonly Func<IExecutionRunContext, CancellationToken, Task<ExecutionRunResult>> _execute;

    public DelegateExecutionHandler(
        ExecutionHandlerDescriptor descriptor,
        Func<IExecutionRunContext, CancellationToken, Task<ExecutionRunResult>> execute,
        ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        ExecutionContractValidator.ValidateHandlerDescriptor(descriptor, limits);
        Descriptor = Clone(descriptor);
    }

    public ExecutionHandlerDescriptor Descriptor { get; }

    public Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default)
    {
        return _execute(context, ct);
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

public sealed class StaticExecutionPlugin : IExecutionPlugin
{
    public StaticExecutionPlugin(
        ExecutionPluginDescriptor descriptor,
        IEnumerable<IExecutionHandler> handlers,
        ExecutionRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handlers);
        var handlerList = handlers.ToList();
        Descriptor = Clone(descriptor, handlerList, limits);
        Handlers = handlerList.ToArray();
    }

    public ExecutionPluginDescriptor Descriptor { get; }
    public IReadOnlyList<IExecutionHandler> Handlers { get; }

    private static ExecutionPluginDescriptor Clone(
        ExecutionPluginDescriptor descriptor,
        IReadOnlyList<IExecutionHandler> handlers,
        ExecutionRuntimeLimits? limits)
    {
        var clone = new ExecutionPluginDescriptor
        {
            PluginId = descriptor.PluginId,
            Name = descriptor.Name,
            Version = descriptor.Version,
            Handlers = descriptor.Handlers.Count == 0
                ? handlers.Select(handler => Clone(handler.Descriptor, descriptor.PluginId)).ToList()
                : descriptor.Handlers.Select(handler => Clone(handler, descriptor.PluginId)).ToList()
        };
        ExecutionContractValidator.ValidatePluginDescriptor(clone, limits);
        return clone;
    }

    private static ExecutionHandlerDescriptor Clone(ExecutionHandlerDescriptor descriptor, string pluginId)
    {
        return new ExecutionHandlerDescriptor
        {
            HandlerId = descriptor.HandlerId,
            PluginId = string.IsNullOrWhiteSpace(descriptor.PluginId) ? pluginId : descriptor.PluginId,
            DisplayName = descriptor.DisplayName,
            Description = descriptor.Description,
            MaxAttempts = descriptor.MaxAttempts,
            ConcurrencyKey = descriptor.ConcurrencyKey,
            Tags = new Dictionary<string, string>(descriptor.Tags, StringComparer.Ordinal)
        };
    }
}
