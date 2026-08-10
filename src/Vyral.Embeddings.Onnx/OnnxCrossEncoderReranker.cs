using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Vyral.Embeddings.Onnx;

public sealed class OnnxCrossEncoderReranker : IDisposable
{
    private readonly InferenceSession _session;
    private readonly HashSet<string> _inputNames;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly int _batchSize;
    private readonly string? _outputName;
    private readonly OnnxCrossEncoderScoreMode _scoreMode;

    public string ProviderId { get; }
    public string ModelId { get; }
    public string ModelPath { get; }
    public string VocabPath { get; }
    public int MaxTokens { get; }
    public int BatchSize { get; }
    public bool Lowercase { get; }
    public string? OutputName { get; }
    public string ScoreMode { get; }
    public string? ExecutionModeName { get; }
    public string ActiveExecutionProvider { get; }
    public string? ExecutionProviderFallbackReason { get; }
    public int? IntraOpNumThreads { get; }
    public int? InterOpNumThreads { get; }
    public long? CudaMemoryLimitMb { get; }

    public OnnxCrossEncoderReranker(OnnxCrossEncoderRerankerOptions options)
    {
        var resolved = ResolveOptions(options);
        ProviderId = string.IsNullOrWhiteSpace(options.ProviderId) ? "onnx-cross-encoder-reranker" : options.ProviderId.Trim();
        ModelId = resolved.ModelId;
        ModelPath = resolved.ModelPath;
        VocabPath = resolved.VocabPath;
        MaxTokens = resolved.MaxTokens;
        BatchSize = resolved.BatchSize;
        Lowercase = resolved.Lowercase;
        OutputName = resolved.OutputName;
        ScoreMode = resolved.ScoreMode.ToString();
        ExecutionModeName = resolved.ExecutionMode?.ToString();
        IntraOpNumThreads = resolved.IntraOpNumThreads;
        InterOpNumThreads = resolved.InterOpNumThreads;
        CudaMemoryLimitMb = resolved.CudaMemoryLimitMb;

        _maxTokens = resolved.MaxTokens;
        _batchSize = resolved.BatchSize;
        _outputName = resolved.OutputName;
        _scoreMode = resolved.ScoreMode;
        _tokenizer = new WordPieceTokenizer(resolved.VocabPath, resolved.Lowercase);
        _session = CreateSession(
            resolved.ModelPath,
            resolved,
            out var activeExecutionProvider,
            out var fallbackReason);
        ActiveExecutionProvider = activeExecutionProvider;
        ExecutionProviderFallbackReason = fallbackReason;
        _inputNames = _session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public Task<IReadOnlyList<OnnxCrossEncoderRerankScore>> RerankAsync(
        string query,
        IReadOnlyList<OnnxCrossEncoderRerankCandidate> candidates,
        int? limit = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("ONNX cross-encoder reranker requires query.");
        }

        if (candidates.Count == 0)
        {
            throw new ArgumentException("ONNX cross-encoder reranker requires at least one candidate.");
        }

        var resultLimit = limit.GetValueOrDefault(candidates.Count);
        if (resultLimit <= 0)
        {
            throw new ArgumentException("ONNX cross-encoder reranker limit must be positive.");
        }

        var scores = new List<OnnxCrossEncoderRerankScore>(candidates.Count);
        for (var offset = 0; offset < candidates.Count; offset += _batchSize)
        {
            ct.ThrowIfCancellationRequested();
            scores.AddRange(ScoreBatch(query, candidates, offset, Math.Min(_batchSize, candidates.Count - offset)));
        }

        var ranked = scores
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.OriginalIndex)
            .Take(resultLimit)
            .ToList();

        return Task.FromResult<IReadOnlyList<OnnxCrossEncoderRerankScore>>(ranked);
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    private List<OnnxCrossEncoderRerankScore> ScoreBatch(string query, IReadOnlyList<OnnxCrossEncoderRerankCandidate> candidates, int offset, int count)
    {
        var encodings = new List<WordPiecePairEncoding>(count);
        for (var i = 0; i < count; i++)
        {
            encodings.Add(_tokenizer.EncodePair(query, candidates[offset + i].Text, _maxTokens));
        }

        var sequenceLength = Math.Max(1, encodings.Max(encoding => encoding.InputIds.Length));
        var inputIds = new long[count * sequenceLength];
        var attentionMask = new long[count * sequenceLength];
        var tokenTypeIds = new long[count * sequenceLength];

        Array.Fill(inputIds, _tokenizer.PadId);

        for (var row = 0; row < encodings.Count; row++)
        {
            var encoding = encodings[row];
            for (var column = 0; column < encoding.InputIds.Length; column++)
            {
                var index = (row * sequenceLength) + column;
                inputIds[index] = encoding.InputIds[column];
                attentionMask[index] = encoding.AttentionMask[column];
                tokenTypeIds[index] = encoding.TokenTypeIds[column];
            }
        }

        var shape = new[] { count, sequenceLength };
        if (!_inputNames.Contains("input_ids"))
        {
            var names = string.Join(", ", _inputNames);
            throw new InvalidOperationException($"ONNX cross-encoder model must expose input_ids. Available inputs: {names}.");
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, shape))
        };

        if (_inputNames.Contains("attention_mask"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, shape)));
        }

        if (_inputNames.Contains("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, shape)));
        }

        using var results = _session.Run(inputs);
        var output = ResolveOutput(results);
        var scores = new List<OnnxCrossEncoderRerankScore>(count);

        for (var row = 0; row < count; row++)
        {
            scores.Add(new OnnxCrossEncoderRerankScore
            {
                Id = candidates[offset + row].Id,
                OriginalIndex = offset + row,
                Score = ResolveScore(output, row)
            });
        }

        return scores;
    }

    private DenseTensor<float> ResolveOutput(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        var value = string.IsNullOrWhiteSpace(_outputName)
            ? results.FirstOrDefault()
            : results.FirstOrDefault(result => string.Equals(result.Name, _outputName, StringComparison.Ordinal));

        if (value == null)
        {
            var names = string.Join(", ", results.Select(result => result.Name));
            throw new InvalidOperationException($"ONNX cross-encoder output '{_outputName}' was not found. Available outputs: {names}.");
        }

        return value.AsTensor<float>() as DenseTensor<float>
            ?? throw new InvalidOperationException($"ONNX cross-encoder output '{value.Name}' is not a dense float tensor.");
    }

    private double ResolveScore(DenseTensor<float> output, int row)
    {
        return output.Dimensions.Length switch
        {
            1 => ScoreSingleLogit(output[row]),
            2 => ScoreVector(output, row, output.Dimensions[1]),
            3 when output.Dimensions[1] == 1 => ScoreVector(output, row, output.Dimensions[2]),
            _ => throw new InvalidOperationException($"ONNX cross-encoder output rank {output.Dimensions.Length} is not supported.")
        };
    }

    private double ScoreVector(DenseTensor<float> output, int row, int width)
    {
        if (width <= 0)
        {
            throw new InvalidOperationException("ONNX cross-encoder output must contain at least one score column.");
        }

        var first = output.Dimensions.Length == 2 ? output[row, 0] : output[row, 0, 0];
        if (width == 1)
        {
            return ScoreSingleLogit(first);
        }

        var second = output.Dimensions.Length == 2 ? output[row, 1] : output[row, 0, 1];
        return _scoreMode switch
        {
            OnnxCrossEncoderScoreMode.Logit => second,
            OnnxCrossEncoderScoreMode.Sigmoid => Sigmoid(second),
            OnnxCrossEncoderScoreMode.SoftmaxFirst => Softmax(first, second).First,
            OnnxCrossEncoderScoreMode.SoftmaxSecond => Softmax(first, second).Second,
            OnnxCrossEncoderScoreMode.Auto => Softmax(first, second).Second,
            _ => second
        };
    }

    private double ScoreSingleLogit(float logit)
    {
        return _scoreMode switch
        {
            OnnxCrossEncoderScoreMode.Logit => logit,
            OnnxCrossEncoderScoreMode.Sigmoid => Sigmoid(logit),
            OnnxCrossEncoderScoreMode.SoftmaxFirst => 1.0 - Sigmoid(logit),
            OnnxCrossEncoderScoreMode.SoftmaxSecond => Sigmoid(logit),
            OnnxCrossEncoderScoreMode.Auto => Sigmoid(logit),
            _ => logit
        };
    }

    private static double Sigmoid(double value)
    {
        if (value >= 0)
        {
            var z = Math.Exp(-value);
            return 1.0 / (1.0 + z);
        }

        var exp = Math.Exp(value);
        return exp / (1.0 + exp);
    }

    private static (double First, double Second) Softmax(double first, double second)
    {
        var max = Math.Max(first, second);
        var firstExp = Math.Exp(first - max);
        var secondExp = Math.Exp(second - max);
        var sum = firstExp + secondExp;
        return (firstExp / sum, secondExp / sum);
    }

    private static InferenceSession CreateSession(
        string modelPath,
        ResolvedOnnxCrossEncoderOptions resolved,
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

    private static SessionOptions CreateSessionOptions(ResolvedOnnxCrossEncoderOptions resolved)
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

    private static void AppendCudaExecutionProvider(SessionOptions options, ResolvedOnnxCrossEncoderOptions resolved)
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

    private static Dictionary<string, string> BuildCudaProviderOptions(ResolvedOnnxCrossEncoderOptions resolved)
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

    private static ResolvedOnnxCrossEncoderOptions ResolveOptions(OnnxCrossEncoderRerankerOptions options)
    {
        var modelPath = ResolveModelPath(options.ModelPath);
        var vocabPath = ResolveVocabPath(options.VocabPath, modelPath);
        var executionProvider = ParseExecutionProvider(options.ExecutionProvider);
        var scoreMode = ParseScoreMode(options.ScoreMode);
        var executionMode = ParseExecutionMode(options.ExecutionMode);
        var maxTokens = options.MaxTokens ?? 512;
        if (maxTokens < 3)
        {
            throw new InvalidOperationException("ONNX cross-encoder maxTokens must be at least 3.");
        }

        var batchSize = options.BatchSize ?? 8;
        if (batchSize <= 0)
        {
            throw new InvalidOperationException("ONNX cross-encoder batchSize must be a positive integer.");
        }

        ValidatePositive(options.IntraOpNumThreads, "intraOpNumThreads");
        ValidatePositive(options.InterOpNumThreads, "interOpNumThreads");
        ValidateNonNegative(options.CudaDeviceId, "cudaDeviceId");
        ValidatePositive(options.CudaMemoryLimitMb, "cudaMemoryLimitMb");

        return new ResolvedOnnxCrossEncoderOptions(
            modelPath,
            vocabPath,
            string.IsNullOrWhiteSpace(options.ModelId) ? Path.GetFileNameWithoutExtension(modelPath) : options.ModelId!,
            executionProvider,
            maxTokens,
            batchSize,
            options.Lowercase ?? true,
            options.OutputName,
            scoreMode,
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
            throw new InvalidOperationException("ONNX cross-encoder modelPath is required.");
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

        throw new FileNotFoundException($"ONNX cross-encoder model file was not found: {configured}", configured);
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

            throw new FileNotFoundException($"ONNX cross-encoder vocabulary file was not found: {configured}", configured);
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

        throw new FileNotFoundException($"ONNX cross-encoder vocabulary file was not found near model: {modelPath}", modelPath);
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

    private static OnnxCrossEncoderScoreMode ParseScoreMode(string? value)
    {
        return (value ?? "auto").ToLowerInvariant() switch
        {
            "auto" => OnnxCrossEncoderScoreMode.Auto,
            "logit" or "raw" => OnnxCrossEncoderScoreMode.Logit,
            "sigmoid" => OnnxCrossEncoderScoreMode.Sigmoid,
            "softmax-first" or "softmaxfirst" or "softmax_0" => OnnxCrossEncoderScoreMode.SoftmaxFirst,
            "softmax-second" or "softmaxsecond" or "softmax_1" => OnnxCrossEncoderScoreMode.SoftmaxSecond,
            _ => throw new InvalidOperationException($"ONNX cross-encoder scoreMode '{value}' is not supported.")
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
            throw new InvalidOperationException($"ONNX cross-encoder {name} must be a positive integer.");
        }
    }

    private static void ValidateNonNegative(int? value, string name)
    {
        if (value.HasValue && value.Value < 0)
        {
            throw new InvalidOperationException($"ONNX cross-encoder {name} must be a non-negative integer.");
        }
    }

    private static void ValidatePositive(long? value, string name)
    {
        if (value.HasValue && value.Value <= 0)
        {
            throw new InvalidOperationException($"ONNX cross-encoder {name} must be a positive integer.");
        }
    }

    private sealed record ResolvedOnnxCrossEncoderOptions(
        string ModelPath,
        string VocabPath,
        string ModelId,
        OnnxExecutionProviderPreference ExecutionProviderPreference,
        int MaxTokens,
        int BatchSize,
        bool Lowercase,
        string? OutputName,
        OnnxCrossEncoderScoreMode ScoreMode,
        int? IntraOpNumThreads,
        int? InterOpNumThreads,
        ExecutionMode? ExecutionMode,
        int? CudaDeviceId,
        long? CudaMemoryLimitMb);
}
