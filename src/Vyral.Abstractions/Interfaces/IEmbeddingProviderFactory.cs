using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public interface IEmbeddingProviderFactory
{
    EmbeddingProviderDescriptor Descriptor { get; }
    EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options) => options;
    IEmbeddingProvider Create(EmbeddingProviderOptions options);
}
