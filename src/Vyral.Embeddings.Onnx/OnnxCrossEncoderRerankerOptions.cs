namespace Vyral.Embeddings.Onnx;

public sealed class OnnxCrossEncoderRerankerOptions
{
    public string ProviderId { get; set; } = "onnx-cross-encoder-reranker";

    public string? ModelId { get; set; }

    public string? ModelPath { get; set; }

    public string? VocabPath { get; set; }

    public string? ExecutionProvider { get; set; }

    public int? MaxTokens { get; set; }

    public int? BatchSize { get; set; }

    public bool? Lowercase { get; set; }

    public string? OutputName { get; set; }

    public string? ScoreMode { get; set; }

    public int? IntraOpNumThreads { get; set; }

    public int? InterOpNumThreads { get; set; }

    public string? ExecutionMode { get; set; }

    public int? CudaDeviceId { get; set; }

    public long? CudaMemoryLimitMb { get; set; }
}

public sealed class OnnxCrossEncoderRerankCandidate
{
    public string Id { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

public sealed class OnnxCrossEncoderRerankScore
{
    public string Id { get; set; } = string.Empty;

    public double Score { get; set; }

    public int OriginalIndex { get; set; }
}
