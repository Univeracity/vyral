using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public static class LocalEmbeddingProviders
{
    public static IReadOnlyList<IEmbeddingProviderFactory> CreateFactories()
    {
        return new IEmbeddingProviderFactory[]
        {
            new LocalTokenHashEmbeddingProviderFactory(),
            new DeterministicHashEmbeddingProviderFactory()
        };
    }

    public static EmbeddingProviderRegistry CreateRegistry()
    {
        return new EmbeddingProviderRegistry(CreateFactories());
    }

    public static IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return CreateRegistry().Create(options);
    }
}
