using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vyral.Primitives;

namespace Vyral.Abstractions.Models;

public class EmbeddingProviderOptions
{
    // -----------------------------------------------------------------------
    // Provider-agnostic fields — apply to all embedding providers
    // -----------------------------------------------------------------------

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "local-token-hash";

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("dimensions")]
    public int? Dimensions { get; set; }

    [JsonPropertyName("queryPrefix")]
    public string? QueryPrefix { get; set; }

    [JsonPropertyName("passagePrefix")]
    public string? PassagePrefix { get; set; }

    [JsonPropertyName("symmetricPrefix")]
    public string? SymmetricPrefix { get; set; }

    // -----------------------------------------------------------------------
    // ONNX runtime fields — only relevant for local ONNX embedding providers
    // -----------------------------------------------------------------------

    [JsonPropertyName("modelPath")]
    public string? ModelPath { get; set; }

    [JsonPropertyName("vocabPath")]
    public string? VocabPath { get; set; }

    [JsonPropertyName("executionProvider")]
    public string? ExecutionProvider { get; set; }

    [JsonPropertyName("maxTokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("lowercase")]
    public bool? Lowercase { get; set; }

    [JsonPropertyName("normalize")]
    public bool? Normalize { get; set; }

    [JsonPropertyName("pooling")]
    public string? Pooling { get; set; }

    [JsonPropertyName("outputName")]
    public string? OutputName { get; set; }

    [JsonPropertyName("intraOpNumThreads")]
    public int? IntraOpNumThreads { get; set; }

    [JsonPropertyName("interOpNumThreads")]
    public int? InterOpNumThreads { get; set; }

    [JsonPropertyName("executionMode")]
    public string? ExecutionMode { get; set; }

    [JsonPropertyName("cudaDeviceId")]
    public int? CudaDeviceId { get; set; }

    [JsonPropertyName("cudaMemoryLimitMb")]
    public long? CudaMemoryLimitMb { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public class EmbeddingProviderDescriptor
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("defaultModelId")]
    public string DefaultModelId { get; set; } = string.Empty;

    [JsonPropertyName("defaultDimensions")]
    public int DefaultDimensions { get; set; }

    [JsonPropertyName("local")]
    public bool Local { get; set; }

    [JsonPropertyName("cpuOnly")]
    public bool CpuOnly { get; set; }

    [JsonPropertyName("requiresNetwork")]
    public bool RequiresNetwork { get; set; }

    [JsonPropertyName("semanticQuality")]
    public string SemanticQuality { get; set; } = string.Empty;

    [JsonPropertyName("defaultQueryPrefix")]
    public string? DefaultQueryPrefix { get; set; }

    [JsonPropertyName("defaultPassagePrefix")]
    public string? DefaultPassagePrefix { get; set; }

    [JsonPropertyName("defaultSymmetricPrefix")]
    public string? DefaultSymmetricPrefix { get; set; }
}

public class EmbeddingProviderGuidance
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("semanticQuality")]
    public string SemanticQuality { get; set; } = string.Empty;

    [JsonPropertyName("defaultDimensions")]
    public int DefaultDimensions { get; set; }

    [JsonPropertyName("hardwareProfile")]
    public string HardwareProfile { get; set; } = string.Empty;

    [JsonPropertyName("requiresModelFiles")]
    public bool RequiresModelFiles { get; set; }

    [JsonPropertyName("realisticForSemanticRetrieval")]
    public bool RealisticForSemanticRetrieval { get; set; }

    [JsonPropertyName("defaultQueryPrefix")]
    public string? DefaultQueryPrefix { get; set; }

    [JsonPropertyName("defaultPassagePrefix")]
    public string? DefaultPassagePrefix { get; set; }

    [JsonPropertyName("recommendedFor")]
    public List<string> RecommendedFor { get; set; } = new();

    [JsonPropertyName("cautions")]
    public List<string> Cautions { get; set; } = new();

    [JsonPropertyName("suggestedRetrievalProfiles")]
    public List<string> SuggestedRetrievalProfiles { get; set; } = new();

    [JsonPropertyName("suggestedEvaluationVariants")]
    public List<string> SuggestedEvaluationVariants { get; set; } = new();

    [JsonPropertyName("selectionNotes")]
    public List<string> SelectionNotes { get; set; } = new();
}

public class EmbeddingRequest
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("texts")]
    public List<string>? Texts { get; set; }

    /// <summary>
    /// Semantic purpose of the embedding. Valid values are defined in <see cref="EmbeddingPurposes"/>.
    /// When null, the provider defaults to <see cref="EmbeddingPurposes.Symmetric"/>.
    /// </summary>
    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }

    [JsonPropertyName("queryPrefix")]
    public string? QueryPrefix { get; set; }

    [JsonPropertyName("passagePrefix")]
    public string? PassagePrefix { get; set; }

    [JsonPropertyName("symmetricPrefix")]
    public string? SymmetricPrefix { get; set; }
}

public class EmbeddingResponse
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("dimensions")]
    public int Dimensions { get; set; }

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = EmbeddingPurposes.Symmetric;

    [JsonPropertyName("items")]
    public List<EmbeddingResult> Items { get; set; } = new();
}

public static class EmbeddingJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
}

public class EmbeddingJob
{
    [JsonPropertyName("admission")]
    public AdmissionReceipt Admission { get; set; } = new();

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = EmbeddingJobStatuses.Queued;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("dimensions")]
    public int Dimensions { get; set; }

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = EmbeddingPurposes.Symmetric;

    [JsonPropertyName("requestHash")]
    public string RequestHash { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; set; }

    [JsonPropertyName("cancellationRequested")]
    public bool CancellationRequested { get; set; }

    [JsonPropertyName("requested")]
    public int Requested { get; set; }

    [JsonPropertyName("attempted")]
    public int Attempted { get; set; }

    [JsonPropertyName("succeeded")]
    public int Succeeded { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("currentIndex")]
    public int? CurrentIndex { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("result")]
    public EmbeddingResponse? Result { get; set; }
}

public class EmbeddingResult
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("textLength")]
    public int TextLength { get; set; }

    [JsonPropertyName("preparedTextLength")]
    public int PreparedTextLength { get; set; }

    [JsonPropertyName("prefixApplied")]
    public bool PrefixApplied { get; set; }

    [JsonPropertyName("prefixLength")]
    public int PrefixLength { get; set; }

    [JsonPropertyName("values")]
    public float[] Values { get; set; } = Array.Empty<float>();
}

public static class EmbeddingPurposes
{
    public const string Query = "query";
    public const string Passage = "passage";
    public const string Document = "document";
    public const string Symmetric = "symmetric";
}

public class PreparedEmbeddingText
{
    public PreparedEmbeddingText(
        string originalText,
        string preparedText,
        string purpose,
        bool prefixApplied,
        int prefixLength)
    {
        OriginalText = originalText;
        PreparedText = preparedText;
        Purpose = purpose;
        PrefixApplied = prefixApplied;
        PrefixLength = prefixLength;
    }

    public string OriginalText { get; }

    public string PreparedText { get; }

    public string Purpose { get; }

    public bool PrefixApplied { get; }

    public int PrefixLength { get; }
}

public static class EmbeddingTextPreparer
{
    public static PreparedEmbeddingText Prepare(
        string text,
        string? purpose,
        string? queryPrefix,
        string? passagePrefix,
        string? symmetricPrefix)
    {
        var normalizedPurpose = NormalizePurpose(purpose);
        var prefix = ResolvePrefix(normalizedPurpose, queryPrefix, passagePrefix, symmetricPrefix);
        var preparedText = text;
        var prefixApplied = false;

        if (!string.IsNullOrEmpty(prefix) &&
            !text.StartsWith(prefix, StringComparison.Ordinal))
        {
            preparedText = prefix + text;
            prefixApplied = true;
        }

        return new PreparedEmbeddingText(
            text,
            preparedText,
            normalizedPurpose,
            prefixApplied,
            prefix?.Length ?? 0);
    }

    public static string NormalizePurpose(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            return EmbeddingPurposes.Symmetric;
        }

        return purpose.Trim().ToLowerInvariant() switch
        {
            EmbeddingPurposes.Query => EmbeddingPurposes.Query,
            EmbeddingPurposes.Passage => EmbeddingPurposes.Passage,
            EmbeddingPurposes.Document => EmbeddingPurposes.Document,
            EmbeddingPurposes.Symmetric => EmbeddingPurposes.Symmetric,
            _ => throw new InvalidOperationException("Embedding purpose must be 'query', 'passage', 'document', or 'symmetric'.")
        };
    }

    public static string? ResolvePrefix(
        string purpose,
        string? queryPrefix,
        string? passagePrefix,
        string? symmetricPrefix)
    {
        return purpose switch
        {
            EmbeddingPurposes.Query => EmptyToNull(queryPrefix),
            EmbeddingPurposes.Passage => EmptyToNull(passagePrefix),
            EmbeddingPurposes.Document => EmptyToNull(passagePrefix),
            EmbeddingPurposes.Symmetric => EmptyToNull(symmetricPrefix),
            _ => EmptyToNull(symmetricPrefix)
        };
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
