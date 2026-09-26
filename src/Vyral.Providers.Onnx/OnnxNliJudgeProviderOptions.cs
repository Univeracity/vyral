using Vyral.Embeddings.Onnx;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Onnx;

public sealed class OnnxNliJudgeProviderOptions
{
    public string ProviderId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? ModelId { get; set; }

    public string? ModelPath { get; set; }

    public string? VocabPath { get; set; }

    public string ExecutionProvider { get; set; } = string.Empty;

    /// <summary>
    /// Default 512 — the default judge model's own max_position_embeddings ceiling, not an arbitrary
    /// choice. Longer premises are truncated while preserving the hypothesis; a hypothesis that
    /// cannot fit alongside a nonempty premise fails closed. See OnnxNliClassifierOptions.MaxTokens.
    /// </summary>
    public int MaxTokens { get; set; } = 512;

    public int BatchSize { get; set; } = 8;

    public bool? Lowercase { get; set; }

    public string? OutputName { get; set; }

    /// <summary>
    /// Class index the loaded checkpoint uses for "entailment" in its logits vector. NLI checkpoints
    /// do not agree on class order — the default model's own config.json id2label is {0: ENTAILMENT,
    /// 1: NEUTRAL, 2: CONTRADICTION} (index 0), which is why this defaults to 0, not the index-2
    /// convention other MNLI checkpoints use. Swapping models requires re-confirming this value
    /// against the new checkpoint's config.json, not assuming it carries over.
    /// </summary>
    public int EntailmentIndex { get; set; } = OnnxNliJudgeProviderTargets.DefaultEntailmentIndex;

    /// <summary>
    /// Class names in checkpoint index order, used to label <see cref="AiJudgeAnswer.RawClassProbabilities"/>
    /// on Noul answers (e.g. {0: "entailment", 1: "neutral", 2: "contradiction"} for the default
    /// model, matching its own config.json id2label — confirm and update when swapping checkpoints,
    /// same as <see cref="EntailmentIndex"/>). Null disables RawClassProbabilities entirely rather
    /// than guessing generic names for a checkpoint this wasn't confirmed against.
    /// </summary>
    public string[]? ClassLabels { get; set; } = OnnxNliJudgeProviderTargets.DefaultClassLabels;

    public int? IntraOpNumThreads { get; set; } = 1;

    public int? InterOpNumThreads { get; set; } = 1;

    public string? ExecutionMode { get; set; }

    public int? CudaDeviceId { get; set; }

    public long? CudaMemoryLimitMb { get; set; }

    public bool CpuOnly { get; set; } = true;

    /// <summary>Path to a fitted <see cref="OnnxJudgeCalibration"/> JSON file. Missing/unset means uncalibrated (temperature 1.0).</summary>
    public string? CalibrationPath { get; set; }

    /// <summary>
    /// Hypothesis template for Choice options. "{label}" is replaced with the option's label; "{prompt}"
    /// (unused by the generic default) is replaced with the question's prompt, for tasks where the
    /// claim/question text must be part of the hypothesis itself rather than a separate premise —
    /// e.g. citation-relation checking: "The passage {label} the claim: {prompt}".
    /// </summary>
    public string ChoiceHypothesisTemplate { get; set; } = "This example is about: {label}.";

    /// <summary>
    /// Hypothesis template for Score levels. Same "{label}"/"{prompt}" placeholders as
    /// <see cref="ChoiceHypothesisTemplate"/>, kept as a separate default because an ordered-scale
    /// rating ("the correct level is low/medium/high") reads more naturally than a topic-membership
    /// phrasing ("this example is about low/medium/high").
    /// </summary>
    public string ScoreHypothesisTemplate { get; set; } = "On this scale, the correct level is: {label}.";

    public OnnxNliClassifierOptions ToRuntimeOptions()
    {
        return new OnnxNliClassifierOptions
        {
            ModelId = ModelId,
            ModelPath = ModelPath ?? string.Empty,
            VocabPath = VocabPath,
            ExecutionProvider = ExecutionProvider,
            MaxTokens = MaxTokens,
            BatchSize = BatchSize,
            Lowercase = Lowercase ?? true,
            OutputName = OutputName,
            IntraOpNumThreads = IntraOpNumThreads,
            InterOpNumThreads = InterOpNumThreads,
            ExecutionMode = ExecutionMode,
            CudaDeviceId = CudaDeviceId,
            CudaMemoryLimitMb = CudaMemoryLimitMb
        };
    }
}

public static class OnnxNliJudgeProviderTargets
{
    public const string CpuProviderId = "onnx-nli-judge-cpu";
    public const string GpuProviderId = "onnx-nli-judge-gpu";

    /// <summary>
    /// Xenova/mobilebert-uncased-mnli, selected over DeBERTa-v3/RoBERTa NLI checkpoints (also
    /// evaluated) because those use SentencePiece or byte-pair tokenizers this package's
    /// WordPiece-only tokenizer cannot read — the same constraint the existing ONNX cross-encoder
    /// reranker's default model already satisfies. MobileBERT-uncased-mnli is WordPiece (shares the
    /// reranker default's tokenizer family), well-established (16k+ downloads at selection time), and
    /// its quantized ONNX export is ~26MB, the smallest of the WordPiece-compatible NLI candidates
    /// checked, fitting the local-first "no large download by default" posture. Its published
    /// id2label order is {0: ENTAILMENT, 1: NEUTRAL, 2: CONTRADICTION} — confirmed directly against
    /// the model's own config.json, not assumed from a generic MNLI convention, hence EntailmentIndex
    /// defaults to 0 below rather than the more common index-2 convention some other checkpoints use.
    /// </summary>
    public const string DefaultModelId = "Xenova/mobilebert-uncased-mnli:model_quantized";
    public const string DefaultCpuModelPath = ".vyral/models/mobilebert-uncased-mnli-quantized/onnx/model_quantized.onnx";
    public const string DefaultGpuModelPath = ".vyral/models/mobilebert-uncased-mnli-fp32/onnx/model.onnx";
    public const string DefaultCpuVocabPath = ".vyral/models/mobilebert-uncased-mnli-quantized/vocab.txt";
    public const string DefaultGpuVocabPath = ".vyral/models/mobilebert-uncased-mnli-fp32/vocab.txt";
    public const string DefaultCalibrationPath = ".vyral/models/mobilebert-uncased-mnli-quantized/calibration.json";
    public const int DefaultEntailmentIndex = 0;

    /// <summary>Default model's own config.json id2label order: {0: ENTAILMENT, 1: NEUTRAL, 2: CONTRADICTION}.</summary>
    public static readonly string[] DefaultClassLabels = { "entailment", "neutral", "contradiction" };

    public static OnnxNliJudgeProviderTarget CreateCpu(OnnxNliJudgeProviderOptions? overrides = null) =>
        new(ApplyDefaults(overrides, cpuOnly: true));

    public static OnnxNliJudgeProviderTarget CreateGpu(OnnxNliJudgeProviderOptions? overrides = null) =>
        new(ApplyDefaults(overrides, cpuOnly: false));

    public static OnnxNliJudgeProviderOptions ApplyDefaults(OnnxNliJudgeProviderOptions? overrides, bool cpuOnly)
    {
        overrides ??= new OnnxNliJudgeProviderOptions();
        var useDefaultModel = string.IsNullOrWhiteSpace(overrides.ModelPath);
        return new OnnxNliJudgeProviderOptions
        {
            ProviderId = string.IsNullOrWhiteSpace(overrides.ProviderId) ? (cpuOnly ? CpuProviderId : GpuProviderId) : overrides.ProviderId,
            DisplayName = string.IsNullOrWhiteSpace(overrides.DisplayName)
                ? (cpuOnly ? "ONNX NLI judge CPU" : "ONNX NLI judge GPU-preferred")
                : overrides.DisplayName,
            ModelId = string.IsNullOrWhiteSpace(overrides.ModelId) ? DefaultModelId : overrides.ModelId,
            ModelPath = useDefaultModel ? (cpuOnly ? DefaultCpuModelPath : DefaultGpuModelPath) : overrides.ModelPath,
            VocabPath = string.IsNullOrWhiteSpace(overrides.VocabPath)
                ? (useDefaultModel ? (cpuOnly ? DefaultCpuVocabPath : DefaultGpuVocabPath) : null)
                : overrides.VocabPath,
            ExecutionProvider = cpuOnly ? "cpu" : (string.IsNullOrWhiteSpace(overrides.ExecutionProvider) ? "cudaPreferred" : overrides.ExecutionProvider),
            MaxTokens = overrides.MaxTokens <= 0 ? 512 : overrides.MaxTokens,
            BatchSize = overrides.BatchSize <= 0 ? 8 : overrides.BatchSize,
            Lowercase = overrides.Lowercase ?? true,
            OutputName = overrides.OutputName,
            EntailmentIndex = overrides.EntailmentIndex,
            ClassLabels = overrides.ClassLabels,
            IntraOpNumThreads = overrides.IntraOpNumThreads ?? 1,
            InterOpNumThreads = overrides.InterOpNumThreads ?? 1,
            ExecutionMode = overrides.ExecutionMode,
            CudaDeviceId = overrides.CudaDeviceId,
            CudaMemoryLimitMb = overrides.CudaMemoryLimitMb ?? (cpuOnly ? null : 512),
            CpuOnly = cpuOnly,
            CalibrationPath = string.IsNullOrWhiteSpace(overrides.CalibrationPath)
                ? (useDefaultModel ? DefaultCalibrationPath : null)
                : overrides.CalibrationPath,
            ChoiceHypothesisTemplate = string.IsNullOrWhiteSpace(overrides.ChoiceHypothesisTemplate) ? "This example is about: {label}." : overrides.ChoiceHypothesisTemplate,
            ScoreHypothesisTemplate = string.IsNullOrWhiteSpace(overrides.ScoreHypothesisTemplate) ? "On this scale, the correct level is: {label}." : overrides.ScoreHypothesisTemplate
        };
    }
}
