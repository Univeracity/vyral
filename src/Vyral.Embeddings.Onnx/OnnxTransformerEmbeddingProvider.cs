using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Embeddings.Onnx;

public sealed class OnnxTransformerEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly InferenceSession _session;
    private readonly HashSet<string> _inputNames;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly bool _normalize;
    private readonly OnnxPoolingMode _pooling;
    private readonly string? _outputName;

    public string ProviderId { get; }
    public string ModelId { get; }
    public int Dimensions { get; }
    public string ModelPath { get; }
    public string VocabPath { get; }
    public int MaxTokens { get; }
    public bool Lowercase { get; }
    public bool Normalize { get; }
    public string Pooling { get; }
    public string? OutputName { get; }
    public string? ExecutionModeName { get; }
    public string ActiveExecutionProvider { get; }
    public string? ExecutionProviderFallbackReason { get; }
    public int? IntraOpNumThreads { get; }
    public int? InterOpNumThreads { get; }
    public long? CudaMemoryLimitMb { get; }

    public OnnxTransformerEmbeddingProvider(EmbeddingProviderOptions options)
    {
        var resolved = ResolveOptions(options);
        ProviderId = options.Provider;
        ModelId = resolved.ModelId;
        _maxTokens = resolved.MaxTokens;
        _normalize = resolved.Normalize;
        _pooling = resolved.Pooling;
        _outputName = resolved.OutputName;
        _tokenizer = new WordPieceTokenizer(resolved.VocabPath, resolved.Lowercase);
        ModelPath = resolved.ModelPath;
        VocabPath = resolved.VocabPath;
        MaxTokens = resolved.MaxTokens;
        Lowercase = resolved.Lowercase;
        Normalize = resolved.Normalize;
        Pooling = resolved.Pooling.ToString();
        OutputName = resolved.OutputName;
        ExecutionModeName = resolved.ExecutionMode?.ToString();
        IntraOpNumThreads = resolved.IntraOpNumThreads;
        InterOpNumThreads = resolved.InterOpNumThreads;
        CudaMemoryLimitMb = resolved.CudaMemoryLimitMb;

        _session = CreateSession(
            resolved.ModelPath,
            resolved,
            out var activeExecutionProvider,
            out var fallbackReason);
        ActiveExecutionProvider = activeExecutionProvider;
        ExecutionProviderFallbackReason = fallbackReason;
        Dimensions = resolved.Dimensions ?? ResolveDimensions(resolved.ModelPath, _session);
        _inputNames = _session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var tokenIds = _tokenizer.Encode(text, _maxTokens);
        var attentionMask = Enumerable.Repeat(1L, tokenIds.Length).ToArray();
        var tokenTypeIds = new long[tokenIds.Length];
        var shape = new[] { 1, tokenIds.Length };

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(tokenIds, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, shape))
        };

        if (_inputNames.Contains("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, shape)));
        }

        using var results = _session.Run(inputs);
        var output = ResolveOutput(results);
        var embedding = output.Dimensions.Length switch
        {
            2 => ReadBatchEmbedding(output),
            3 => PoolSequenceEmbedding(output, attentionMask),
            _ => throw new InvalidOperationException($"ONNX embedding output rank {output.Dimensions.Length} is not supported.")
        };

        if (embedding.Length != Dimensions)
        {
            throw new InvalidOperationException($"ONNX embedding output has {embedding.Length} dimensions, but provider is configured for {Dimensions}.");
        }

        if (_normalize)
        {
            NormalizeInPlace(embedding);
        }

        return Task.FromResult(embedding);
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    private DenseTensor<float> ResolveOutput(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        var value = string.IsNullOrWhiteSpace(_outputName)
            ? results.FirstOrDefault()
            : results.FirstOrDefault(result => string.Equals(result.Name, _outputName, StringComparison.Ordinal));

        if (value == null)
        {
            var names = string.Join(", ", results.Select(result => result.Name));
            throw new InvalidOperationException($"ONNX output '{_outputName}' was not found. Available outputs: {names}.");
        }

        return value.AsTensor<float>() as DenseTensor<float>
            ?? throw new InvalidOperationException($"ONNX output '{value.Name}' is not a dense float tensor.");
    }

    private static float[] ReadBatchEmbedding(DenseTensor<float> output)
    {
        if (output.Dimensions[0] != 1)
        {
            throw new InvalidOperationException("ONNX embedding provider expects a batch size of 1.");
        }

        var dimensions = output.Dimensions[1];
        var embedding = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            embedding[i] = output[0, i];
        }

        return embedding;
    }

    private float[] PoolSequenceEmbedding(DenseTensor<float> output, long[] attentionMask)
    {
        if (output.Dimensions[0] != 1)
        {
            throw new InvalidOperationException("ONNX embedding provider expects a batch size of 1.");
        }

        var sequenceLength = output.Dimensions[1];
        var dimensions = output.Dimensions[2];
        var embedding = new float[dimensions];

        if (_pooling == OnnxPoolingMode.Cls)
        {
            for (var i = 0; i < dimensions; i++)
            {
                embedding[i] = output[0, 0, i];
            }

            return embedding;
        }

        var tokenCount = 0;
        for (var token = 0; token < sequenceLength && token < attentionMask.Length; token++)
        {
            if (attentionMask[token] == 0)
            {
                continue;
            }

            tokenCount++;
            for (var dimension = 0; dimension < dimensions; dimension++)
            {
                embedding[dimension] += output[0, token, dimension];
            }
        }

        if (tokenCount == 0)
        {
            return embedding;
        }

        for (var dimension = 0; dimension < embedding.Length; dimension++)
        {
            embedding[dimension] /= tokenCount;
        }

        return embedding;
    }

    private static void NormalizeInPlace(float[] embedding)
    {
        var norm = TensorPrimitives.Norm(embedding.AsSpan());
        if (norm <= 0)
        {
            return;
        }

        TensorPrimitives.Divide(embedding.AsSpan(), norm, embedding.AsSpan());
    }

    private static InferenceSession CreateSession(
        string modelPath,
        ResolvedOnnxOptions resolved,
        out string activeExecutionProvider,
        out string? fallbackReason)
    {
        fallbackReason = null;

        if (resolved.ExecutionProviderPreference == OnnxExecutionProviderPreference.Cpu)
        {
            activeExecutionProvider = "cpu";
            return new InferenceSession(modelPath, CreateSessionOptions(resolved));
        }

        try
        {
            var options = CreateSessionOptions(resolved);
            AppendCudaExecutionProvider(options, resolved);
            var session = new InferenceSession(modelPath, options);
            activeExecutionProvider = "cuda";
            return session;
        }
        catch (Exception ex) when (resolved.ExecutionProviderPreference == OnnxExecutionProviderPreference.CudaPreferred)
        {
            activeExecutionProvider = "cpu";
            fallbackReason = UnwrapException(ex).Message;
            return new InferenceSession(modelPath, CreateSessionOptions(resolved));
        }
    }

    private static SessionOptions CreateSessionOptions(ResolvedOnnxOptions resolved)
    {
        var options = new SessionOptions();

        if (resolved.IntraOpNumThreads.HasValue)
        {
            options.IntraOpNumThreads = resolved.IntraOpNumThreads.Value;
        }

        if (resolved.InterOpNumThreads.HasValue)
        {
            options.InterOpNumThreads = resolved.InterOpNumThreads.Value;
        }

        if (resolved.ExecutionMode.HasValue)
        {
            options.ExecutionMode = resolved.ExecutionMode.Value;
        }

        return options;
    }

    private static void AppendCudaExecutionProvider(SessionOptions options, ResolvedOnnxOptions resolved)
    {
        var cudaProviderOptionsMethod = typeof(SessionOptions)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(candidate =>
                candidate.Name == "AppendExecutionProvider_CUDA" &&
                candidate.GetParameters().Length == 1 &&
                candidate.GetParameters()[0].ParameterType == typeof(OrtCUDAProviderOptions));

        if (cudaProviderOptionsMethod != null)
        {
            using var cudaOptions = new OrtCUDAProviderOptions();
            cudaOptions.UpdateOptions(BuildCudaProviderOptions(resolved));
            cudaProviderOptionsMethod.Invoke(options, new object[] { cudaOptions });
            return;
        }

        var method = typeof(SessionOptions)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(candidate =>
                candidate.Name == "AppendExecutionProvider_CUDA" &&
                candidate.GetParameters().Length <= 1);

        if (method == null)
        {
            throw new InvalidOperationException("The loaded ONNX Runtime package does not expose the CUDA execution provider. Use Microsoft.ML.OnnxRuntime.Gpu or select CPU execution.");
        }

        var parameters = method.GetParameters().Length == 0 ? Array.Empty<object>() : new object[] { resolved.CudaDeviceId ?? 0 };
        method.Invoke(options, parameters);
    }

    private static Dictionary<string, string> BuildCudaProviderOptions(ResolvedOnnxOptions resolved)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["device_id"] = (resolved.CudaDeviceId ?? 0).ToString()
        };

        if (resolved.CudaMemoryLimitMb.HasValue)
        {
            options["gpu_mem_limit"] = checked(resolved.CudaMemoryLimitMb.Value * 1024L * 1024L).ToString();
        }

        return options;
    }

    private static Exception UnwrapException(Exception exception)
    {
        return exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception;
    }

    private static int ResolveDimensions(string modelPath, InferenceSession session)
    {
        var configPath = Path.Combine(Path.GetDirectoryName(modelPath) ?? string.Empty, "..", "config.json");
        configPath = Path.GetFullPath(configPath);
        if (File.Exists(configPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.TryGetProperty("hidden_size", out var hiddenSize) && hiddenSize.TryGetInt32(out var dimensions))
            {
                return dimensions;
            }
        }

        foreach (var output in session.OutputMetadata.Values)
        {
            var dimensions = output.Dimensions;
            if (dimensions.Length >= 2 && dimensions[^1] > 0)
            {
                return dimensions[^1];
            }
        }

        throw new InvalidOperationException("ONNX embedding dimensions could not be inferred. Set embedding provider dimensions explicitly.");
    }

    private static ResolvedOnnxOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        var modelPath = ResolveModelPath(options.ModelPath);
        var vocabPath = ResolveVocabPath(options.VocabPath, modelPath);
        var executionProvider = ParseExecutionProvider(options.ExecutionProvider);
        var pooling = ParsePooling(options.Pooling);
        var executionMode = ParseExecutionMode(options.ExecutionMode);
        var maxTokens = options.MaxTokens ?? 256;
        if (maxTokens < 2)
        {
            throw new InvalidOperationException("ONNX embedding maxTokens must be at least 2.");
        }
        ValidatePositive(options.IntraOpNumThreads, "intraOpNumThreads");
        ValidatePositive(options.InterOpNumThreads, "interOpNumThreads");
        ValidateNonNegative(options.CudaDeviceId, "cudaDeviceId");
        ValidatePositive(options.CudaMemoryLimitMb, "cudaMemoryLimitMb");

        return new ResolvedOnnxOptions(
            modelPath,
            vocabPath,
            string.IsNullOrWhiteSpace(options.ModelId) ? Path.GetFileNameWithoutExtension(modelPath) : options.ModelId!,
            options.Dimensions,
            executionProvider,
            maxTokens,
            options.Lowercase ?? true,
            options.Normalize ?? true,
            pooling,
            options.OutputName,
            options.IntraOpNumThreads,
            options.InterOpNumThreads,
            executionMode,
            options.CudaDeviceId,
            options.CudaMemoryLimitMb);
    }

    private static string ResolveModelPath(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("ONNX embedding modelPath is required.");
        }

        foreach (var path in ExpandConfiguredPaths(configured))
        {
            if (File.Exists(path))
            {
                return path;
            }

            if (Directory.Exists(path))
            {
                var candidates = new[]
                {
                    Path.Combine(path, "model_quantized.onnx"),
                    Path.Combine(path, "model.onnx"),
                    Path.Combine(path, "onnx", "model_quantized.onnx"),
                    Path.Combine(path, "onnx", "model.onnx")
                };

                var match = candidates.FirstOrDefault(File.Exists);
                if (match != null)
                {
                    return Path.GetFullPath(match);
                }
            }
        }

        throw new FileNotFoundException($"ONNX embedding model file was not found: {configured}", configured);
    }

    private static string ResolveVocabPath(string? configured, string modelPath)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var configuredPath in ExpandConfiguredPaths(configured))
            {
                if (File.Exists(configuredPath))
                {
                    return configuredPath;
                }
            }

            throw new FileNotFoundException($"ONNX tokenizer vocabulary file was not found: {configured}", configured);
        }

        var modelDirectory = Path.GetDirectoryName(modelPath) ?? string.Empty;
        var candidates = new[]
        {
            Path.Combine(modelDirectory, "vocab.txt"),
            Path.Combine(modelDirectory, "..", "vocab.txt")
        };

        var match = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (match != null)
        {
            return match;
        }

        throw new FileNotFoundException($"ONNX tokenizer vocabulary file was not found near model: {modelPath}", modelPath);
    }

    private static IEnumerable<string> ExpandConfiguredPaths(string configured)
    {
        yield return Path.GetFullPath(configured);

        if (Path.IsPathRooted(configured))
        {
            yield break;
        }

        foreach (var path in ExpandFromAncestors(Directory.GetCurrentDirectory(), configured))
        {
            yield return path;
        }

        foreach (var path in ExpandFromAncestors(AppContext.BaseDirectory, configured))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> ExpandFromAncestors(string startPath, string relativePath)
    {
        var directory = new DirectoryInfo(startPath);
        if (File.Exists(startPath))
        {
            directory = new FileInfo(startPath).Directory!;
        }

        while (directory != null)
        {
            yield return Path.GetFullPath(Path.Combine(directory.FullName, relativePath));
            directory = directory.Parent;
        }
    }

    private static OnnxExecutionProviderPreference ParseExecutionProvider(string? value)
    {
        return (value ?? "cpu").ToLowerInvariant() switch
        {
            "cpu" => OnnxExecutionProviderPreference.Cpu,
            "cuda" or "cuda-preferred" or "cudapreferred" or "gpu" or "gpu-preferred" or "gpupreferred" => OnnxExecutionProviderPreference.CudaPreferred,
            "cuda-required" or "cudarequired" or "gpu-required" or "gpurequired" => OnnxExecutionProviderPreference.CudaRequired,
            _ => throw new InvalidOperationException($"ONNX execution provider '{value}' is not supported.")
        };
    }

    private static OnnxPoolingMode ParsePooling(string? value)
    {
        return (value ?? "mean").ToLowerInvariant() switch
        {
            "mean" => OnnxPoolingMode.Mean,
            "cls" => OnnxPoolingMode.Cls,
            _ => throw new InvalidOperationException($"ONNX pooling mode '{value}' is not supported.")
        };
    }

    private static ExecutionMode? ParseExecutionMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.ToLowerInvariant() switch
        {
            "sequential" => ExecutionMode.ORT_SEQUENTIAL,
            "parallel" => ExecutionMode.ORT_PARALLEL,
            _ => throw new InvalidOperationException($"ONNX execution mode '{value}' is not supported.")
        };
    }

    private static void ValidatePositive(int? value, string name)
    {
        if (value.HasValue && value.Value <= 0)
        {
            throw new InvalidOperationException($"ONNX {name} must be a positive integer.");
        }
    }

    private static void ValidateNonNegative(int? value, string name)
    {
        if (value.HasValue && value.Value < 0)
        {
            throw new InvalidOperationException($"ONNX {name} must be a non-negative integer.");
        }
    }

    private static void ValidatePositive(long? value, string name)
    {
        if (value.HasValue && value.Value <= 0)
        {
            throw new InvalidOperationException($"ONNX {name} must be a positive integer.");
        }
    }

    private sealed record ResolvedOnnxOptions(
        string ModelPath,
        string VocabPath,
        string ModelId,
        int? Dimensions,
        OnnxExecutionProviderPreference ExecutionProviderPreference,
        int MaxTokens,
        bool Lowercase,
        bool Normalize,
        OnnxPoolingMode Pooling,
        string? OutputName,
        int? IntraOpNumThreads,
        int? InterOpNumThreads,
        ExecutionMode? ExecutionMode,
        int? CudaDeviceId,
        long? CudaMemoryLimitMb);
}
