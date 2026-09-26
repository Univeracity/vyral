using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Vyral.Embeddings.Onnx;

/// <summary>
/// Local, in-process zero-shot NLI/entailment scorer. Mirrors <see cref="OnnxCrossEncoderReranker"/>'s
/// tokenizer and session plumbing exactly (same WordPiece pair encoding, same execution-provider
/// fallback), but returns raw per-class logits instead of one collapsed score: entailment-style
/// classification needs the full distribution (entailment/neutral/contradiction, in whatever order
/// the loaded checkpoint uses) for calibration and for combining across candidate options.
/// </summary>
public sealed class OnnxNliClassifier : IDisposable
{
    private readonly InferenceSession _session;
    private readonly HashSet<string> _inputNames;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly int _batchSize;
    private readonly string? _outputName;

    public string ModelPath { get; }
    public string VocabPath { get; }
    public string ActiveExecutionProvider { get; }
    public string? ExecutionProviderFallbackReason { get; }
    public int NumClasses { get; private set; } = -1;

    public OnnxNliClassifier(OnnxNliClassifierOptions options)
    {
        var resolved = ResolveOptions(options);
        ModelPath = resolved.ModelPath;
        VocabPath = resolved.VocabPath;
        _maxTokens = resolved.MaxTokens;
        _batchSize = resolved.BatchSize;
        _outputName = resolved.OutputName;
        _tokenizer = new WordPieceTokenizer(resolved.VocabPath, resolved.Lowercase);
        _session = CreateSession(resolved.ModelPath, resolved, out var activeExecutionProvider, out var fallbackReason);
        ActiveExecutionProvider = activeExecutionProvider;
        ExecutionProviderFallbackReason = fallbackReason;
        _inputNames = _session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public Task<IReadOnlyList<OnnxNliClassification>> ClassifyAsync(
        string premise,
        IReadOnlyList<OnnxNliHypothesis> hypotheses,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(premise))
        {
            throw new ArgumentException("ONNX NLI classifier requires a premise.");
        }

        if (hypotheses.Count == 0)
        {
            throw new ArgumentException("ONNX NLI classifier requires at least one hypothesis.");
        }

        var results = new List<OnnxNliClassification>(hypotheses.Count);
        for (var offset = 0; offset < hypotheses.Count; offset += _batchSize)
        {
            ct.ThrowIfCancellationRequested();
            results.AddRange(ClassifyBatch(premise, hypotheses, offset, Math.Min(_batchSize, hypotheses.Count - offset)));
        }

        return Task.FromResult<IReadOnlyList<OnnxNliClassification>>(results);
    }

    public void Dispose() => _session.Dispose();

    private List<OnnxNliClassification> ClassifyBatch(string premise, IReadOnlyList<OnnxNliHypothesis> hypotheses, int offset, int count)
    {
        var encodings = new List<WordPiecePairEncoding>(count);
        for (var i = 0; i < count; i++)
        {
            // truncateFirstBeforeSecond: true — premise (first) absorbs truncation, hypothesis
            // (second) survives intact. Reversed from the default because the hypothesis is what
            // varies per option; see EncodePair's doc comment and BUG-20260918-061159-20F269.
            encodings.Add(_tokenizer.EncodePair(premise, hypotheses[offset + i].Text, _maxTokens, truncateFirstBeforeSecond: true));
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
            throw new InvalidOperationException($"ONNX NLI model must expose input_ids. Available inputs: {names}.");
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

        using var runResults = _session.Run(inputs);
        var output = ResolveOutput(runResults);
        NumClasses = output.Dimensions[^1];

        var classifications = new List<OnnxNliClassification>(count);
        for (var row = 0; row < count; row++)
        {
            var logits = new double[NumClasses];
            for (var cls = 0; cls < NumClasses; cls++)
            {
                logits[cls] = output.Dimensions.Length == 2 ? output[row, cls] : output[row, 0, cls];
            }

            classifications.Add(new OnnxNliClassification
            {
                Id = hypotheses[offset + row].Id,
                OriginalIndex = offset + row,
                Logits = logits
            });
        }

        return classifications;
    }

    private DenseTensor<float> ResolveOutput(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        var value = string.IsNullOrWhiteSpace(_outputName)
            ? results.FirstOrDefault()
            : results.FirstOrDefault(result => string.Equals(result.Name, _outputName, StringComparison.Ordinal));

        if (value == null)
        {
            var names = string.Join(", ", results.Select(result => result.Name));
            throw new InvalidOperationException($"ONNX NLI output '{_outputName}' was not found. Available outputs: {names}.");
        }

        var tensor = value.AsTensor<float>() as DenseTensor<float>
            ?? throw new InvalidOperationException($"ONNX NLI output '{value.Name}' is not a dense float tensor.");

        if (tensor.Dimensions.Length is not (2 or 3))
        {
            throw new InvalidOperationException($"ONNX NLI output rank {tensor.Dimensions.Length} is not supported.");
        }

        return tensor;
    }

    private static InferenceSession CreateSession(
        string modelPath,
        ResolvedOnnxNliOptions resolved,
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

    private static SessionOptions CreateSessionOptions(ResolvedOnnxNliOptions resolved)
    {
        var options = new SessionOptions();
        if (resolved.IntraOpNumThreads.HasValue) options.IntraOpNumThreads = resolved.IntraOpNumThreads.Value;
        if (resolved.InterOpNumThreads.HasValue) options.InterOpNumThreads = resolved.InterOpNumThreads.Value;
        if (resolved.ExecutionMode.HasValue) options.ExecutionMode = resolved.ExecutionMode.Value;
        return options;
    }

    private static void AppendCudaExecutionProvider(SessionOptions options, ResolvedOnnxNliOptions resolved)
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

    private static Dictionary<string, string> BuildCudaProviderOptions(ResolvedOnnxNliOptions resolved)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal) { ["device_id"] = (resolved.CudaDeviceId ?? 0).ToString() };
        if (resolved.CudaMemoryLimitMb.HasValue)
        {
            options["gpu_mem_limit"] = checked(resolved.CudaMemoryLimitMb.Value * 1024L * 1024L).ToString();
        }

        return options;
    }

    private static Exception UnwrapException(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } ? exception.InnerException : exception;

    private static ResolvedOnnxNliOptions ResolveOptions(OnnxNliClassifierOptions options)
    {
        var modelPath = ResolveModelPath(options.ModelPath);
        var vocabPath = ResolveVocabPath(options.VocabPath, modelPath);
        var executionProvider = ParseExecutionProvider(options.ExecutionProvider);
        var executionMode = ParseExecutionMode(options.ExecutionMode);
        var maxTokens = options.MaxTokens <= 0 ? 256 : options.MaxTokens;
        if (maxTokens < 3)
        {
            throw new InvalidOperationException("ONNX NLI maxTokens must be at least 3.");
        }

        var batchSize = options.BatchSize <= 0 ? 8 : options.BatchSize;

        return new ResolvedOnnxNliOptions(
            modelPath,
            vocabPath,
            executionProvider,
            maxTokens,
            batchSize,
            options.Lowercase,
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
            throw new InvalidOperationException("ONNX NLI modelPath is required.");
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

        throw new FileNotFoundException($"ONNX NLI model file was not found: {configured}", configured);
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

            throw new FileNotFoundException($"ONNX NLI vocabulary file was not found: {configured}", configured);
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

        throw new FileNotFoundException($"ONNX NLI vocabulary file was not found near model: {modelPath}", modelPath);
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

    private sealed record ResolvedOnnxNliOptions(
        string ModelPath,
        string VocabPath,
        OnnxExecutionProviderPreference ExecutionProviderPreference,
        int MaxTokens,
        int BatchSize,
        bool Lowercase,
        string? OutputName,
        int? IntraOpNumThreads,
        int? InterOpNumThreads,
        ExecutionMode? ExecutionMode,
        int? CudaDeviceId,
        long? CudaMemoryLimitMb);
}
