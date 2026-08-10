using System;
using System.Collections.Generic;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Embeddings.Onnx;

public static class OnnxEmbeddingProviders
{
    public const string MiniLmCpuProvider = "onnx-minilm-cpu";
    public const string MiniLmGpuProvider = "onnx-minilm-gpu";
    public const string MultiQaMiniLmCpuProvider = "onnx-multi-qa-minilm-cpu";
    public const string MultiQaMiniLmGpuProvider = "onnx-multi-qa-minilm-gpu";
    public const string BgeSmallCpuProvider = "onnx-bge-small-cpu";
    public const string BgeSmallGpuProvider = "onnx-bge-small-gpu";
    public const string BgeBaseCpuProvider = "onnx-bge-base-cpu";
    public const string BgeBaseGpuProvider = "onnx-bge-base-gpu";
    public const string E5SmallCpuProvider = "onnx-e5-small-cpu";
    public const string E5SmallGpuProvider = "onnx-e5-small-gpu";
    public const string E5BaseCpuProvider = "onnx-e5-base-cpu";
    public const string E5BaseGpuProvider = "onnx-e5-base-gpu";
    public const string GenericProvider = "onnx-transformer";
    public const string BgeQueryPrefix = "Represent this sentence for searching relevant passages: ";
    public const string E5QueryPrefix = "query: ";
    public const string E5PassagePrefix = "passage: ";

    public static IReadOnlyList<IEmbeddingProviderFactory> CreateFactories()
    {
        return new IEmbeddingProviderFactory[]
        {
            new OnnxTransformerEmbeddingProviderFactory(),
            new OnnxMiniLmCpuEmbeddingProviderFactory(),
            new OnnxMiniLmGpuEmbeddingProviderFactory(),
            new OnnxMultiQaMiniLmCpuEmbeddingProviderFactory(),
            new OnnxMultiQaMiniLmGpuEmbeddingProviderFactory(),
            new OnnxBgeSmallCpuEmbeddingProviderFactory(),
            new OnnxBgeSmallGpuEmbeddingProviderFactory(),
            new OnnxBgeBaseCpuEmbeddingProviderFactory(),
            new OnnxBgeBaseGpuEmbeddingProviderFactory(),
            new OnnxE5SmallCpuEmbeddingProviderFactory(),
            new OnnxE5SmallGpuEmbeddingProviderFactory(),
            new OnnxE5BaseCpuEmbeddingProviderFactory(),
            new OnnxE5BaseGpuEmbeddingProviderFactory()
        };
    }
}

public sealed class OnnxTransformerEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.GenericProvider,
        DisplayName = "Configurable ONNX transformer embeddings",
        Description = "Local ONNX Runtime transformer embeddings with WordPiece tokenization, mean/CLS pooling, CPU execution, and optional CUDA-preferred execution that can fall back to CPU.",
        DefaultModelId = "configured-onnx-transformer",
        DefaultDimensions = 384,
        Local = true,
        CpuOnly = false,
        RequiresNetwork = false,
        SemanticQuality = "semantic"
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(options);
    }
}

public sealed class OnnxMiniLmCpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/all-MiniLM-L6-v2:model_quantized";
    public const int DefaultDimensions = 384;
    public const string DefaultModelPath = ".vyral/models/all-MiniLM-L6-v2-quantized/onnx/model_quantized.onnx";
    public const string DefaultVocabPath = ".vyral/models/all-MiniLM-L6-v2-quantized/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.MiniLmCpuProvider,
        DisplayName = "MiniLM ONNX CPU embeddings",
        Description = "CPU-oriented quantized all-MiniLM-L6-v2 ONNX embeddings for local semantic retrieval development.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = true,
        RequiresNetwork = false,
        SemanticQuality = "semantic"
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        return ApplyDefaults(options, cpuOnly: true);
    }

    internal static EmbeddingProviderOptions ApplyDefaults(EmbeddingProviderOptions options, bool cpuOnly)
    {
        return new EmbeddingProviderOptions
        {
            Provider = options.Provider,
            ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId,
            Dimensions = options.Dimensions ?? DefaultDimensions,
            ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath,
            VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath,
            ExecutionProvider = cpuOnly
                ? "cpu"
                : string.IsNullOrWhiteSpace(options.ExecutionProvider) ? "cudaPreferred" : options.ExecutionProvider,
            MaxTokens = options.MaxTokens ?? 256,
            Lowercase = options.Lowercase ?? true,
            Normalize = options.Normalize ?? true,
            Pooling = string.IsNullOrWhiteSpace(options.Pooling) ? "mean" : options.Pooling,
            OutputName = options.OutputName,
            IntraOpNumThreads = options.IntraOpNumThreads ?? 1,
            InterOpNumThreads = options.InterOpNumThreads ?? 1,
            ExecutionMode = options.ExecutionMode,
            CudaDeviceId = options.CudaDeviceId,
            CudaMemoryLimitMb = options.CudaMemoryLimitMb ?? (cpuOnly ? null : 512),
            QueryPrefix = options.QueryPrefix,
            PassagePrefix = options.PassagePrefix,
            SymmetricPrefix = options.SymmetricPrefix,
            AdditionalProperties = options.AdditionalProperties
        };
    }
}

public sealed class OnnxMiniLmGpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/all-MiniLM-L6-v2:model";
    public const int DefaultDimensions = 384;
    public const string DefaultModelPath = ".vyral/models/all-MiniLM-L6-v2-fp32/onnx/model.onnx";
    public const string DefaultVocabPath = ".vyral/models/all-MiniLM-L6-v2-fp32/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.MiniLmGpuProvider,
        DisplayName = "MiniLM ONNX GPU-preferred embeddings",
        Description = "GPU-preferred all-MiniLM-L6-v2 ONNX embeddings that attempt CUDA execution and cleanly fall back to CPU unless CUDA is required.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = false,
        RequiresNetwork = false,
        SemanticQuality = "semantic"
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        var defaults = OnnxMiniLmCpuEmbeddingProviderFactory.ApplyDefaults(options, cpuOnly: false);
        defaults.ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId;
        defaults.ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath;
        defaults.VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath;
        defaults.Dimensions = options.Dimensions ?? DefaultDimensions;
        return defaults;
    }
}

public sealed class OnnxMultiQaMiniLmCpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/multi-qa-MiniLM-L6-cos-v1:model_quantized";
    public const int DefaultDimensions = 384;
    public const string DefaultModelPath = ".vyral/models/multi-qa-MiniLM-L6-cos-v1-quantized/onnx/model_quantized.onnx";
    public const string DefaultVocabPath = ".vyral/models/multi-qa-MiniLM-L6-cos-v1-quantized/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.MultiQaMiniLmCpuProvider,
        DisplayName = "Multi-QA MiniLM ONNX CPU embeddings",
        Description = "CPU-oriented quantized multi-qa-MiniLM-L6-cos-v1 ONNX embeddings for local question-answer retrieval development.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = true,
        RequiresNetwork = false,
        SemanticQuality = "semantic"
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        return ApplyDefaults(options, cpuOnly: true);
    }

    internal static EmbeddingProviderOptions ApplyDefaults(EmbeddingProviderOptions options, bool cpuOnly)
    {
        return new EmbeddingProviderOptions
        {
            Provider = options.Provider,
            ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId,
            Dimensions = options.Dimensions ?? DefaultDimensions,
            ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath,
            VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath,
            ExecutionProvider = cpuOnly
                ? "cpu"
                : string.IsNullOrWhiteSpace(options.ExecutionProvider) ? "cudaPreferred" : options.ExecutionProvider,
            MaxTokens = options.MaxTokens ?? 256,
            Lowercase = options.Lowercase ?? true,
            Normalize = options.Normalize ?? true,
            Pooling = string.IsNullOrWhiteSpace(options.Pooling) ? "mean" : options.Pooling,
            OutputName = options.OutputName,
            IntraOpNumThreads = options.IntraOpNumThreads ?? 1,
            InterOpNumThreads = options.InterOpNumThreads ?? 1,
            ExecutionMode = options.ExecutionMode,
            CudaDeviceId = options.CudaDeviceId,
            CudaMemoryLimitMb = options.CudaMemoryLimitMb ?? (cpuOnly ? null : 512),
            QueryPrefix = options.QueryPrefix,
            PassagePrefix = options.PassagePrefix,
            SymmetricPrefix = options.SymmetricPrefix,
            AdditionalProperties = options.AdditionalProperties
        };
    }
}

public sealed class OnnxMultiQaMiniLmGpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/multi-qa-MiniLM-L6-cos-v1:model";
    public const int DefaultDimensions = 384;
    public const string DefaultModelPath = ".vyral/models/multi-qa-MiniLM-L6-cos-v1-fp32/onnx/model.onnx";
    public const string DefaultVocabPath = ".vyral/models/multi-qa-MiniLM-L6-cos-v1-fp32/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.MultiQaMiniLmGpuProvider,
        DisplayName = "Multi-QA MiniLM ONNX GPU-preferred embeddings",
        Description = "GPU-preferred multi-qa-MiniLM-L6-cos-v1 ONNX embeddings that attempt CUDA execution and cleanly fall back to CPU unless CUDA is required.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = false,
        RequiresNetwork = false,
        SemanticQuality = "semantic"
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        var defaults = OnnxMultiQaMiniLmCpuEmbeddingProviderFactory.ApplyDefaults(options, cpuOnly: false);
        defaults.ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId;
        defaults.ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath;
        defaults.VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath;
        defaults.Dimensions = options.Dimensions ?? DefaultDimensions;
        return defaults;
    }
}

public sealed class OnnxBgeSmallCpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/bge-small-en-v1.5:model_quantized";
    public const int DefaultDimensions = 384;
    public const string DefaultModelPath = ".vyral/models/bge-small-en-v1.5-quantized/onnx/model_quantized.onnx";
    public const string DefaultVocabPath = ".vyral/models/bge-small-en-v1.5-quantized/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.BgeSmallCpuProvider,
        DisplayName = "BGE Small ONNX CPU embeddings",
        Description = "CPU-oriented quantized bge-small-en-v1.5 ONNX embeddings for realistic local English RAG retrieval development.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = true,
        RequiresNetwork = false,
        SemanticQuality = "semantic",
        DefaultQueryPrefix = OnnxEmbeddingProviders.BgeQueryPrefix
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        return ApplyDefaults(options, cpuOnly: true);
    }

    internal static EmbeddingProviderOptions ApplyDefaults(EmbeddingProviderOptions options, bool cpuOnly)
    {
        return new EmbeddingProviderOptions
        {
            Provider = options.Provider,
            ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId,
            Dimensions = options.Dimensions ?? DefaultDimensions,
            ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath,
            VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath,
            ExecutionProvider = cpuOnly
                ? "cpu"
                : string.IsNullOrWhiteSpace(options.ExecutionProvider) ? "cudaPreferred" : options.ExecutionProvider,
            MaxTokens = options.MaxTokens ?? 512,
            Lowercase = options.Lowercase ?? true,
            Normalize = options.Normalize ?? true,
            Pooling = string.IsNullOrWhiteSpace(options.Pooling) ? "cls" : options.Pooling,
            OutputName = options.OutputName,
            IntraOpNumThreads = options.IntraOpNumThreads ?? 1,
            InterOpNumThreads = options.InterOpNumThreads ?? 1,
            ExecutionMode = options.ExecutionMode,
            CudaDeviceId = options.CudaDeviceId,
            CudaMemoryLimitMb = options.CudaMemoryLimitMb ?? (cpuOnly ? null : 512),
            QueryPrefix = string.IsNullOrWhiteSpace(options.QueryPrefix) ? OnnxEmbeddingProviders.BgeQueryPrefix : options.QueryPrefix,
            PassagePrefix = options.PassagePrefix,
            SymmetricPrefix = options.SymmetricPrefix,
            AdditionalProperties = options.AdditionalProperties
        };
    }
}

public sealed class OnnxBgeSmallGpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/bge-small-en-v1.5:model";
    public const int DefaultDimensions = 384;
    public const string DefaultModelPath = ".vyral/models/bge-small-en-v1.5-fp32/onnx/model.onnx";
    public const string DefaultVocabPath = ".vyral/models/bge-small-en-v1.5-fp32/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.BgeSmallGpuProvider,
        DisplayName = "BGE Small ONNX GPU-preferred embeddings",
        Description = "GPU-preferred bge-small-en-v1.5 ONNX embeddings that attempt CUDA execution and cleanly fall back to CPU unless CUDA is required.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = false,
        RequiresNetwork = false,
        SemanticQuality = "semantic",
        DefaultQueryPrefix = OnnxEmbeddingProviders.BgeQueryPrefix
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        var defaults = OnnxBgeSmallCpuEmbeddingProviderFactory.ApplyDefaults(options, cpuOnly: false);
        defaults.ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId;
        defaults.ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath;
        defaults.VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath;
        defaults.Dimensions = options.Dimensions ?? DefaultDimensions;
        return defaults;
    }
}

public sealed class OnnxBgeBaseCpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/bge-base-en-v1.5:model_quantized";
    public const int DefaultDimensions = 768;
    public const string DefaultModelPath = ".vyral/models/bge-base-en-v1.5-quantized/onnx/model_quantized.onnx";
    public const string DefaultVocabPath = ".vyral/models/bge-base-en-v1.5-quantized/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.BgeBaseCpuProvider,
        DisplayName = "BGE Base ONNX CPU embeddings",
        Description = "CPU-oriented quantized bge-base-en-v1.5 ONNX embeddings for higher-quality local English RAG retrieval development.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = true,
        RequiresNetwork = false,
        SemanticQuality = "semantic",
        DefaultQueryPrefix = OnnxEmbeddingProviders.BgeQueryPrefix
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        return ApplyDefaults(options, cpuOnly: true);
    }

    internal static EmbeddingProviderOptions ApplyDefaults(EmbeddingProviderOptions options, bool cpuOnly)
    {
        var defaults = OnnxBgeSmallCpuEmbeddingProviderFactory.ApplyDefaults(options, cpuOnly);
        defaults.ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId;
        defaults.ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath;
        defaults.VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath;
        defaults.Dimensions = options.Dimensions ?? DefaultDimensions;
        return defaults;
    }
}

public sealed class OnnxBgeBaseGpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/bge-base-en-v1.5:model";
    public const int DefaultDimensions = 768;
    public const string DefaultModelPath = ".vyral/models/bge-base-en-v1.5-fp32/onnx/model.onnx";
    public const string DefaultVocabPath = ".vyral/models/bge-base-en-v1.5-fp32/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.BgeBaseGpuProvider,
        DisplayName = "BGE Base ONNX GPU-preferred embeddings",
        Description = "GPU-preferred bge-base-en-v1.5 ONNX embeddings that attempt CUDA execution and cleanly fall back to CPU unless CUDA is required.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = false,
        RequiresNetwork = false,
        SemanticQuality = "semantic",
        DefaultQueryPrefix = OnnxEmbeddingProviders.BgeQueryPrefix
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        var defaults = OnnxBgeBaseCpuEmbeddingProviderFactory.ApplyDefaults(options, cpuOnly: false);
        defaults.ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId;
        defaults.ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath;
        defaults.VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath;
        defaults.Dimensions = options.Dimensions ?? DefaultDimensions;
        return defaults;
    }
}

public sealed class OnnxE5SmallCpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/e5-small-v2:model_quantized";
    public const int DefaultDimensions = 384;
    public const string DefaultModelPath = ".vyral/models/e5-small-v2-quantized/onnx/model_quantized.onnx";
    public const string DefaultVocabPath = ".vyral/models/e5-small-v2-quantized/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.E5SmallCpuProvider,
        DisplayName = "E5 Small ONNX CPU embeddings",
        Description = "CPU-oriented quantized e5-small-v2 ONNX embeddings with query/passage prefix defaults for asymmetric retrieval development.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = true,
        RequiresNetwork = false,
        SemanticQuality = "semantic",
        DefaultQueryPrefix = OnnxEmbeddingProviders.E5QueryPrefix,
        DefaultPassagePrefix = OnnxEmbeddingProviders.E5PassagePrefix
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        return ApplyDefaults(options, cpuOnly: true);
    }

    internal static EmbeddingProviderOptions ApplyDefaults(EmbeddingProviderOptions options, bool cpuOnly)
    {
        var defaults = OnnxMiniLmCpuEmbeddingProviderFactory.ApplyDefaults(options, cpuOnly);
        defaults.ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId;
        defaults.ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath;
        defaults.VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath;
        defaults.Dimensions = options.Dimensions ?? DefaultDimensions;
        defaults.MaxTokens = options.MaxTokens ?? 512;
        defaults.QueryPrefix = string.IsNullOrWhiteSpace(options.QueryPrefix) ? OnnxEmbeddingProviders.E5QueryPrefix : options.QueryPrefix;
        defaults.PassagePrefix = string.IsNullOrWhiteSpace(options.PassagePrefix) ? OnnxEmbeddingProviders.E5PassagePrefix : options.PassagePrefix;
        defaults.SymmetricPrefix = options.SymmetricPrefix;
        return defaults;
    }
}

public sealed class OnnxE5SmallGpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/e5-small-v2:model";
    public const int DefaultDimensions = 384;
    public const string DefaultModelPath = ".vyral/models/e5-small-v2-fp32/onnx/model.onnx";
    public const string DefaultVocabPath = ".vyral/models/e5-small-v2-fp32/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.E5SmallGpuProvider,
        DisplayName = "E5 Small ONNX GPU-preferred embeddings",
        Description = "GPU-preferred e5-small-v2 ONNX embeddings with query/passage prefix defaults that attempt CUDA execution and cleanly fall back to CPU unless CUDA is required.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = false,
        RequiresNetwork = false,
        SemanticQuality = "semantic",
        DefaultQueryPrefix = OnnxEmbeddingProviders.E5QueryPrefix,
        DefaultPassagePrefix = OnnxEmbeddingProviders.E5PassagePrefix
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        var defaults = OnnxE5SmallCpuEmbeddingProviderFactory.ApplyDefaults(options, cpuOnly: false);
        defaults.ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId;
        defaults.ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath;
        defaults.VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath;
        defaults.Dimensions = options.Dimensions ?? DefaultDimensions;
        return defaults;
    }
}

public sealed class OnnxE5BaseCpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/e5-base-v2:model_quantized";
    public const int DefaultDimensions = 768;
    public const string DefaultModelPath = ".vyral/models/e5-base-v2-quantized/onnx/model_quantized.onnx";
    public const string DefaultVocabPath = ".vyral/models/e5-base-v2-quantized/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.E5BaseCpuProvider,
        DisplayName = "E5 Base ONNX CPU embeddings",
        Description = "CPU-oriented quantized e5-base-v2 ONNX embeddings with query/passage prefix defaults for higher-quality asymmetric retrieval development.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = true,
        RequiresNetwork = false,
        SemanticQuality = "semantic",
        DefaultQueryPrefix = OnnxEmbeddingProviders.E5QueryPrefix,
        DefaultPassagePrefix = OnnxEmbeddingProviders.E5PassagePrefix
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        return ApplyDefaults(options, cpuOnly: true);
    }

    internal static EmbeddingProviderOptions ApplyDefaults(EmbeddingProviderOptions options, bool cpuOnly)
    {
        var defaults = OnnxE5SmallCpuEmbeddingProviderFactory.ApplyDefaults(options, cpuOnly);
        defaults.ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId;
        defaults.ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath;
        defaults.VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath;
        defaults.Dimensions = options.Dimensions ?? DefaultDimensions;
        return defaults;
    }
}

public sealed class OnnxE5BaseGpuEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    public const string DefaultModelId = "Xenova/e5-base-v2:model";
    public const int DefaultDimensions = 768;
    public const string DefaultModelPath = ".vyral/models/e5-base-v2-fp32/onnx/model.onnx";
    public const string DefaultVocabPath = ".vyral/models/e5-base-v2-fp32/vocab.txt";

    public EmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        Provider = OnnxEmbeddingProviders.E5BaseGpuProvider,
        DisplayName = "E5 Base ONNX GPU-preferred embeddings",
        Description = "GPU-preferred e5-base-v2 ONNX embeddings with query/passage prefix defaults that attempt CUDA execution and cleanly fall back to CPU unless CUDA is required.",
        DefaultModelId = DefaultModelId,
        DefaultDimensions = DefaultDimensions,
        Local = true,
        CpuOnly = false,
        RequiresNetwork = false,
        SemanticQuality = "semantic",
        DefaultQueryPrefix = OnnxEmbeddingProviders.E5QueryPrefix,
        DefaultPassagePrefix = OnnxEmbeddingProviders.E5PassagePrefix
    };

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        return new OnnxTransformerEmbeddingProvider(ResolveOptions(options));
    }

    public EmbeddingProviderOptions ResolveOptions(EmbeddingProviderOptions options)
    {
        var defaults = OnnxE5BaseCpuEmbeddingProviderFactory.ApplyDefaults(options, cpuOnly: false);
        defaults.ModelId = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId;
        defaults.ModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? DefaultModelPath : options.ModelPath;
        defaults.VocabPath = string.IsNullOrWhiteSpace(options.VocabPath) ? DefaultVocabPath : options.VocabPath;
        defaults.Dimensions = options.Dimensions ?? DefaultDimensions;
        return defaults;
    }
}
