using System;
using System.IO;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;
using Vyral.Embeddings.Onnx;
using Vyral.Local;

namespace Vyral.Tests.Local;

public class EmbeddingProviderTests
{
    [Fact]
    public async Task LocalRegistry_CreatesLocalTokenHashProviderWithUsefulLexicalSignal()
    {
        var provider = LocalEmbeddingProviders.Create(new EmbeddingProviderOptions
        {
            Provider = LocalTokenHashEmbeddingProviderFactory.Provider,
            Dimensions = 128
        });

        var retention = await provider.GenerateEmbeddingAsync("employee retention policy and document archive rules");
        var retentionQuestion = await provider.GenerateEmbeddingAsync("what are the document retention policy rules");
        var travel = await provider.GenerateEmbeddingAsync("travel reimbursement meals mileage and hotel expenses");

        Assert.Equal(LocalTokenHashEmbeddingProviderFactory.Provider, provider.ProviderId);
        Assert.Equal(LocalTokenHashEmbeddingProviderFactory.DefaultModelId, provider.ModelId);
        Assert.Equal(128, provider.Dimensions);
        Assert.True(Cosine(retention, retentionQuestion) > Cosine(retention, travel));
    }

    [Fact]
    public async Task LocalRegistry_CreatesDeterministicHashProviderWithConfiguredDimensionsAndModel()
    {
        var provider = LocalEmbeddingProviders.Create(new EmbeddingProviderOptions
        {
            Provider = DeterministicHashEmbeddingProviderFactory.Provider,
            ModelId = "local-test-model",
            Dimensions = 12
        });

        var vector = await provider.GenerateEmbeddingAsync("retention policy");

        Assert.Equal(DeterministicHashEmbeddingProviderFactory.Provider, provider.ProviderId);
        Assert.Equal("local-test-model", provider.ModelId);
        Assert.Equal(12, provider.Dimensions);
        Assert.Equal(12, vector.Length);
    }

    [Fact]
    public void LocalRegistry_RejectsUnknownProvider()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            LocalEmbeddingProviders.Create(new EmbeddingProviderOptions
            {
                Provider = "missing-provider"
            }));

        Assert.Contains("missing-provider", error.Message);
        Assert.Contains(DeterministicHashEmbeddingProviderFactory.Provider, error.Message);
    }

    [Fact]
    public void CombinedRegistry_ExposesOnnxPresetsAndRejectsMissingModelPathClearly()
    {
        var registry = new EmbeddingProviderRegistry(LocalEmbeddingProviders.CreateFactories().Concat(OnnxEmbeddingProviders.CreateFactories()));
        var providers = registry.GetProviders();

        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.BgeBaseCpuProvider && provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.BgeBaseGpuProvider && !provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.BgeSmallCpuProvider && provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.BgeSmallGpuProvider && !provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.E5BaseCpuProvider && provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.E5BaseGpuProvider && !provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.E5SmallCpuProvider && provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.E5SmallGpuProvider && !provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.MiniLmCpuProvider && provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.MiniLmGpuProvider && !provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.MultiQaMiniLmCpuProvider && provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.MultiQaMiniLmGpuProvider && !provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == OnnxEmbeddingProviders.E5SmallCpuProvider && provider.DefaultQueryPrefix == OnnxEmbeddingProviders.E5QueryPrefix);

        var e5Options = registry.ResolveOptions(new EmbeddingProviderOptions { Provider = OnnxEmbeddingProviders.E5BaseCpuProvider });
        Assert.Equal(768, e5Options.Dimensions);
        Assert.Equal(OnnxEmbeddingProviders.E5QueryPrefix, e5Options.QueryPrefix);
        Assert.Equal(OnnxEmbeddingProviders.E5PassagePrefix, e5Options.PassagePrefix);

        var bgeOptions = registry.ResolveOptions(new EmbeddingProviderOptions { Provider = OnnxEmbeddingProviders.BgeBaseCpuProvider });
        Assert.Equal(768, bgeOptions.Dimensions);
        Assert.Equal(OnnxEmbeddingProviders.BgeQueryPrefix, bgeOptions.QueryPrefix);

        var error = Assert.Throws<FileNotFoundException>(() =>
            registry.Create(new EmbeddingProviderOptions
            {
                Provider = OnnxEmbeddingProviders.MiniLmCpuProvider,
                ModelPath = ".vyral/models/missing/model.onnx",
                VocabPath = ".vyral/models/missing/vocab.txt"
            }));

        Assert.Contains("ONNX embedding model file was not found", error.Message);
    }

    private static float Cosine(float[] left, float[] right)
    {
        var dot = 0.0f;
        var leftMagnitude = 0.0f;
        var rightMagnitude = 0.0f;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        return dot / (float)(Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }
}
