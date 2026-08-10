using Vyral.Embeddings.Onnx;

namespace Vyral.Providers.Onnx;

public sealed class OnnxCrossEncoderRerankerProviderOptions
{
    public string ProviderId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? ModelId { get; set; }

    public string? ModelPath { get; set; }

    public string? VocabPath { get; set; }

    public string ExecutionProvider { get; set; } = string.Empty;

    public int MaxTokens { get; set; } = 512;

    public int BatchSize { get; set; } = 8;

    public bool? Lowercase { get; set; }

    public string? OutputName { get; set; }

    public string ScoreMode { get; set; } = "auto";

    public int? IntraOpNumThreads { get; set; } = 1;

    public int? InterOpNumThreads { get; set; } = 1;

    public string? ExecutionMode { get; set; }

    public int? CudaDeviceId { get; set; }

    public long? CudaMemoryLimitMb { get; set; }

    public bool CpuOnly { get; set; } = true;

    public OnnxCrossEncoderRerankerOptions ToRuntimeOptions()
    {
        return new OnnxCrossEncoderRerankerOptions
        {
            ProviderId = ProviderId,
            ModelId = ModelId,
            ModelPath = ModelPath,
            VocabPath = VocabPath,
            ExecutionProvider = ExecutionProvider,
            MaxTokens = MaxTokens,
            BatchSize = BatchSize,
            Lowercase = Lowercase,
            OutputName = OutputName,
            ScoreMode = ScoreMode,
            IntraOpNumThreads = IntraOpNumThreads,
            InterOpNumThreads = InterOpNumThreads,
            ExecutionMode = ExecutionMode,
            CudaDeviceId = CudaDeviceId,
            CudaMemoryLimitMb = CudaMemoryLimitMb
        };
    }
}

public static class OnnxCrossEncoderRerankerProviderTargets
{
    public const string CpuProviderId = "onnx-cross-encoder-reranker-cpu";
    public const string GpuProviderId = "onnx-cross-encoder-reranker-gpu";
    public const string GenericProviderId = "onnx-cross-encoder-reranker";
    public const string DefaultCpuModelId = "Xenova/ms-marco-MiniLM-L-6-v2:model_quantized";
    public const string DefaultGpuModelId = "Xenova/ms-marco-MiniLM-L-6-v2:model";
    public const string DefaultCpuModelPath = ".vyral/models/ms-marco-MiniLM-L-6-v2-quantized/onnx/model_quantized.onnx";
    public const string DefaultGpuModelPath = ".vyral/models/ms-marco-MiniLM-L-6-v2-fp32/onnx/model.onnx";
    public const string DefaultCpuVocabPath = ".vyral/models/ms-marco-MiniLM-L-6-v2-quantized/vocab.txt";
    public const string DefaultGpuVocabPath = ".vyral/models/ms-marco-MiniLM-L-6-v2-fp32/vocab.txt";

    public static OnnxCrossEncoderRerankerProviderTarget CreateCpu(OnnxCrossEncoderRerankerProviderOptions? overrides = null)
    {
        return new OnnxCrossEncoderRerankerProviderTarget(ApplyDefaults(overrides, cpuOnly: true));
    }

    public static OnnxCrossEncoderRerankerProviderTarget CreateGpu(OnnxCrossEncoderRerankerProviderOptions? overrides = null)
    {
        return new OnnxCrossEncoderRerankerProviderTarget(ApplyDefaults(overrides, cpuOnly: false));
    }

    public static OnnxCrossEncoderRerankerProviderOptions ApplyDefaults(OnnxCrossEncoderRerankerProviderOptions? overrides, bool cpuOnly)
    {
        overrides ??= new OnnxCrossEncoderRerankerProviderOptions();
        return new OnnxCrossEncoderRerankerProviderOptions
        {
            ProviderId = string.IsNullOrWhiteSpace(overrides.ProviderId)
                ? cpuOnly ? CpuProviderId : GpuProviderId
                : overrides.ProviderId,
            DisplayName = string.IsNullOrWhiteSpace(overrides.DisplayName)
                ? cpuOnly ? "ONNX cross-encoder reranker CPU" : "ONNX cross-encoder reranker GPU-preferred"
                : overrides.DisplayName,
            ModelId = string.IsNullOrWhiteSpace(overrides.ModelId)
                ? cpuOnly ? DefaultCpuModelId : DefaultGpuModelId
                : overrides.ModelId,
            ModelPath = string.IsNullOrWhiteSpace(overrides.ModelPath)
                ? cpuOnly ? DefaultCpuModelPath : DefaultGpuModelPath
                : overrides.ModelPath,
            VocabPath = string.IsNullOrWhiteSpace(overrides.VocabPath)
                ? cpuOnly ? DefaultCpuVocabPath : DefaultGpuVocabPath
                : overrides.VocabPath,
            ExecutionProvider = cpuOnly
                ? "cpu"
                : string.IsNullOrWhiteSpace(overrides.ExecutionProvider) ? "cudaPreferred" : overrides.ExecutionProvider,
            MaxTokens = overrides.MaxTokens <= 0 ? 512 : overrides.MaxTokens,
            BatchSize = overrides.BatchSize <= 0 ? 8 : overrides.BatchSize,
            Lowercase = overrides.Lowercase ?? true,
            OutputName = overrides.OutputName,
            ScoreMode = string.IsNullOrWhiteSpace(overrides.ScoreMode) ? "auto" : overrides.ScoreMode,
            IntraOpNumThreads = overrides.IntraOpNumThreads ?? 1,
            InterOpNumThreads = overrides.InterOpNumThreads ?? 1,
            ExecutionMode = overrides.ExecutionMode,
            CudaDeviceId = overrides.CudaDeviceId,
            CudaMemoryLimitMb = overrides.CudaMemoryLimitMb ?? (cpuOnly ? null : 512),
            CpuOnly = cpuOnly
        };
    }
}
