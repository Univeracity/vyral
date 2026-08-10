using System.Security.Cryptography;
using System.Text.Json;
using Vyral.Abstractions.Interfaces;
using Vyral.Local;

namespace Vyral.Tests.Conformance;

public sealed class PortableEmbeddingGoldenFixtureTests
{
    private const string ManifestResource = "Vyral.Tests.Conformance.runtime-v1-manifest.json";
    private const string ScenarioResource = "Vyral.Tests.Conformance.runtime-v1-embedding-vectors.json";

    [Fact]
    public async Task LocalEmbeddingProvidersMatchPortableFloat32Goldens()
    {
        var manifestBytes = ReadResource(ManifestResource);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var descriptor = manifest.RootElement
            .GetProperty("scenarios")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "embeddings.vectors.v1");

        var scenarioBytes = ReadResource(ScenarioResource);
        var actualDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(scenarioBytes));
        Assert.Equal(descriptor.GetProperty("sha256").GetString(), actualDigest);

        using var scenario = JsonDocument.Parse(scenarioBytes);
        foreach (var step in scenario.RootElement.GetProperty("steps").EnumerateArray())
        {
            var arguments = step.GetProperty("arguments");
            var provider = CreateProvider(
                arguments.GetProperty("provider").GetString()!,
                arguments.GetProperty("dimensions").GetInt32());
            var vector = await provider.GenerateEmbeddingAsync(
                arguments.GetProperty("text").GetString()!);
            var bytes = new byte[vector.Length * sizeof(float)];
            Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);

            var expected = step.GetProperty("expect").GetProperty("value");
            Assert.Equal(expected.GetProperty("provider").GetString(), provider.ProviderId);
            Assert.Equal(expected.GetProperty("modelId").GetString(), provider.ModelId);
            Assert.Equal(expected.GetProperty("dimensions").GetInt32(), provider.Dimensions);
            Assert.Equal(
                expected.GetProperty("float32LittleEndianHex").GetString(),
                Convert.ToHexStringLower(bytes));
        }
    }

    private static IEmbeddingProvider CreateProvider(string provider, int dimensions) =>
        provider switch
        {
            "deterministic-hash" => new DeterministicHashEmbeddingProvider(dimensions),
            "local-token-hash" => new LocalTokenHashEmbeddingProvider(dimensions),
            _ => throw new InvalidOperationException(
                $"Portable fixture embedding provider '{provider}' is unsupported.")
        };

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PortableEmbeddingGoldenFixtureTests)
            .Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
