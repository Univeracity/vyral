using System;
using System.Collections.Generic;
using System.Linq;
using Vyral.Abstractions.Interfaces;

namespace Vyral.Abstractions.Models;

public class EmbeddingProviderRegistry
{
    private readonly Dictionary<string, IEmbeddingProviderFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    public EmbeddingProviderRegistry(IEnumerable<IEmbeddingProviderFactory> factories)
    {
        foreach (var factory in factories)
        {
            if (string.IsNullOrWhiteSpace(factory.Descriptor.Provider))
            {
                throw new InvalidOperationException("Embedding provider factory descriptor provider is required.");
            }

            _factories[factory.Descriptor.Provider] = factory;
        }
    }

    public IReadOnlyList<EmbeddingProviderDescriptor> GetProviders()
    {
        return _factories.Values
            .Select(factory => factory.Descriptor)
            .OrderBy(descriptor => descriptor.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        var factory = GetFactory(options.Provider);
        return factory.Create(factory.ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        return GetFactory(options.Provider).ResolveOptions(options);
    }

    private IEmbeddingProviderFactory GetFactory(string provider)
    {
        if (!_factories.TryGetValue(provider, out var factory))
        {
            var providers = string.Join(", ", _factories.Keys.OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException($"Embedding provider '{provider}' is not registered. Registered providers: {providers}.");
        }

        return factory;
    }
}
