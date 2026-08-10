using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public sealed class LocalTokenHashEmbeddingProvider : IEmbeddingProvider
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a",
        "an",
        "and",
        "are",
        "as",
        "at",
        "be",
        "by",
        "for",
        "from",
        "in",
        "is",
        "it",
        "of",
        "on",
        "or",
        "that",
        "the",
        "to",
        "with"
    };

    public string ProviderId => LocalTokenHashEmbeddingProviderFactory.Provider;
    public int Dimensions { get; }
    public string ModelId { get; }

    public LocalTokenHashEmbeddingProvider(int dimensions = LocalTokenHashEmbeddingProviderFactory.DefaultDimensions, string? modelId = null)
    {
        if (dimensions <= 0)
        {
            throw new InvalidOperationException("Embedding dimensions must be greater than zero.");
        }

        Dimensions = dimensions;
        ModelId = string.IsNullOrWhiteSpace(modelId)
            ? LocalTokenHashEmbeddingProviderFactory.DefaultModelId
            : modelId;
    }

    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var vector = new float[Dimensions];
        var tokens = Tokenize(text).ToList();
        if (tokens.Count == 0)
        {
            AddFeature(vector, "empty", 1.0f);
            return Task.FromResult(Normalize(vector));
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var token = tokens[index];
            var tokenWeight = StopWords.Contains(token) ? 0.15f : 1.0f;
            AddFeature(vector, "tok:" + token, tokenWeight);

            if (!StopWords.Contains(token))
            {
                foreach (var gram in CharacterNgrams(token, 3))
                {
                    AddFeature(vector, "tri:" + gram, 0.20f);
                }
            }

            if (index + 1 < tokens.Count)
            {
                var next = tokens[index + 1];
                if (!StopWords.Contains(token) || !StopWords.Contains(next))
                {
                    AddFeature(vector, "bi:" + token + " " + next, 1.35f);
                }
            }
        }

        return Task.FromResult(Normalize(vector));
    }

    private void AddFeature(float[] vector, string feature, float weight)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(feature));
        var first = BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(0, 4));
        var second = BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(4, 4));
        var index = (int)(first % (uint)Dimensions);
        var sign = (second & 1) == 0 ? 1.0f : -1.0f;
        vector[index] += sign * weight;
    }

    private static float[] Normalize(float[] vector)
    {
        double magnitude = 0;
        for (var index = 0; index < vector.Length; index++)
        {
            magnitude += vector[index] * vector[index];
        }

        if (magnitude <= 0)
        {
            return vector;
        }

        var scale = 1.0 / Math.Sqrt(magnitude);
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(vector[index] * scale);
        }

        return vector;
    }

    private static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString().ToLower(CultureInfo.InvariantCulture));
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static IEnumerable<string> CharacterNgrams(string token, int ngramLength)
    {
        if (token.Length < ngramLength)
        {
            yield return token;
            yield break;
        }

        for (var index = 0; index <= token.Length - ngramLength; index++)
        {
            yield return token.Substring(index, ngramLength);
        }
    }
}

public sealed class LocalTokenHashEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string Provider = "local-token-hash";
    public const int DefaultDimensions = 384;
    public const string DefaultModelId = "local-token-hash-embedding-v1";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = Provider,
        DisplayName = "Local token hash embeddings",
        Description = "CPU-only model-free lexical embeddings for local RAG development. Similar token and phrase overlap produces similar vectors without network or model files.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = true,
        RequiresNetwork = false,
        SemanticQuality = "lexical"
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        var dimensions = options.Dimensions ?? DefaultDimensions;
        if (dimensions <= 0)
        {
            throw new InvalidOperationException("Embedding dimensions must be greater than zero.");
        }

        return new LocalTokenHashEmbeddingProvider(dimensions, options.ModelId);
    }
}
