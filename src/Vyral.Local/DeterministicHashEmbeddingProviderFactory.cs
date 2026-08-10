using System;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public class DeterministicHashEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string Provider = "deterministic-hash";
    public const int DefaultDimensions = 64;
    public const string DefaultModelId = "deterministic-hash-embedding-v1";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = Provider,
        DisplayName = "Deterministic hash embeddings",
        Description = "CPU-only deterministic vectors for local storage, policy, query, and integration testing. These vectors are not semantic embeddings.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = true,
        RequiresNetwork = false,
        SemanticQuality = "mechanical"
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        var dimensions = options.Dimensions ?? DefaultDimensions;
        if (dimensions <= 0)
        {
            throw new InvalidOperationException("Embedding dimensions must be greater than zero.");
        }

        return new DeterministicHashEmbeddingProvider(dimensions, options.ModelId);
    }
}
