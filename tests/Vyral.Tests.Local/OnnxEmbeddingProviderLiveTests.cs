using Vyral.Abstractions.Models;
using Vyral.Embeddings.Onnx;

namespace Vyral.Tests.Local;

public sealed class OnnxModelFactAttribute : FactAttribute
{
    public OnnxModelFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_ONNX_MODEL_DIR")))
        {
            Skip = "Set VYRAL_ONNX_MODEL_DIR to an untracked ONNX model directory to run ONNX embedding live tests.";
        }
    }
}

public class OnnxEmbeddingProviderLiveTests
{
    [OnnxModelFact]
    public async Task OnnxProvider_GeneratesNormalizedSemanticVectorFromUntrackedModel()
    {
        var modelDirectory = ResolveModelDirectory(Environment.GetEnvironmentVariable("VYRAL_ONNX_MODEL_DIR")!);
        using var provider = new OnnxTransformerEmbeddingProvider(new EmbeddingProviderOptions
        {
            Provider = OnnxEmbeddingProviders.GenericProvider,
            ModelId = "live-onnx-test",
            ModelPath = modelDirectory,
            Dimensions = 384,
            ExecutionProvider = Environment.GetEnvironmentVariable("VYRAL_ONNX_EXECUTION_PROVIDER") ?? "cpu",
            MaxTokens = ParseOptionalInt(Environment.GetEnvironmentVariable("VYRAL_ONNX_MAX_TOKENS")) ?? 128,
            Pooling = Environment.GetEnvironmentVariable("VYRAL_ONNX_POOLING")
        });

        var vector = await provider.GenerateEmbeddingAsync("retention policy archive deletion guidance");

        Assert.Equal(384, vector.Length);
        Assert.InRange(Math.Sqrt(vector.Sum(value => value * value)), 0.99, 1.01);
        Assert.Contains(vector, value => Math.Abs(value) > 0.0001f);
    }

    private static string ResolveModelDirectory(string configured)
    {
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Vyral.sln")))
        {
            directory = directory.Parent;
        }

        return Path.GetFullPath(Path.Combine(directory?.FullName ?? Directory.GetCurrentDirectory(), configured));
    }

    private static int? ParseOptionalInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }
}
