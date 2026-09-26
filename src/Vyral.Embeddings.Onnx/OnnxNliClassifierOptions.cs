namespace Vyral.Embeddings.Onnx;

/// <summary>
/// Options for a local zero-shot NLI/entailment classifier. Structurally identical to
/// <see cref="OnnxCrossEncoderRerankerOptions"/> (same tokenizer, same session plumbing); kept as a
/// separate options type because the output shape differs (raw per-class logits rather than one
/// collapsed relevance score) and future NLI-specific knobs should not widen the reranker's options.
/// </summary>
public sealed class OnnxNliClassifierOptions
{
    public string? ModelId { get; set; }
    public string ModelPath { get; set; } = string.Empty;
    public string? VocabPath { get; set; }
    public string ExecutionProvider { get; set; } = "cpu";

    /// <summary>
    /// Default is 512 — MobileBERT-family checkpoints' own <c>max_position_embeddings</c> ceiling
    /// (confirmed against the default judge model's config.json), not an arbitrary round number.
    /// A long real-world premise can still exceed even this; EncodePair fails closed rather than
    /// silently truncating a hypothesis to nothing when that happens (BUG-20260918-061159-20F269).
    /// Judging passages routinely longer than ~512 tokens needs premise chunking upstream, not a
    /// larger MaxTokens — this option cannot exceed what the checkpoint's position embeddings support.
    /// </summary>
    public int MaxTokens { get; set; } = 512;
    public int BatchSize { get; set; } = 8;
    public bool Lowercase { get; set; } = true;
    public string? OutputName { get; set; }
    public int? IntraOpNumThreads { get; set; } = 1;
    public int? InterOpNumThreads { get; set; } = 1;
    public string? ExecutionMode { get; set; }
    public int? CudaDeviceId { get; set; }
    public long? CudaMemoryLimitMb { get; set; }
}

public sealed class OnnxNliHypothesis
{
    public string Id { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
}

/// <summary>Raw per-class logits for one (premise, hypothesis) pair, in the model's own class order.</summary>
public sealed class OnnxNliClassification
{
    public string Id { get; init; } = string.Empty;
    public int OriginalIndex { get; init; }
    public double[] Logits { get; init; } = Array.Empty<double>();
}
