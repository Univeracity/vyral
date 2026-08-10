using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;

namespace Vyral.Local;

public class DeterministicHashEmbeddingProvider : IEmbeddingProvider
{
    private readonly string _modelId;

    public string ProviderId => "deterministic-hash";
    public int Dimensions { get; }
    public string ModelId => _modelId;

    public DeterministicHashEmbeddingProvider(int dimensions = 64, string? modelId = null)
    {
        if (dimensions <= 0)
        {
            throw new InvalidOperationException("Embedding dimensions must be greater than zero.");
        }

        Dimensions = dimensions;
        _modelId = string.IsNullOrWhiteSpace(modelId)
            ? "deterministic-hash-embedding-v1"
            : modelId;
    }

    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var vector = new float[Dimensions];

        for (int i = 0; i < Dimensions; i++)
        {
            // Use segments of the hash to fill the vector
            var hashIndex = (i * 4) % hash.Length;
            var value = BitConverter.ToInt32(hash, hashIndex);
            vector[i] = (float)value / int.MaxValue;
        }

        var magnitude = 0.0;
        for (int i = 0; i < vector.Length; i++)
        {
            magnitude += vector[i] * vector[i];
        }

        magnitude = Math.Sqrt(magnitude);
        if (magnitude > 0)
        {
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / magnitude);
            }
        }

        return Task.FromResult(vector);
    }
}

[Obsolete("Use DeterministicHashEmbeddingProvider. This alias remains for source compatibility.")]
public class FakeEmbeddingProvider : DeterministicHashEmbeddingProvider
{
    public FakeEmbeddingProvider(int dimensions = 64)
        : base(dimensions, "fake-hash-embedding-v1")
    {
    }
}
