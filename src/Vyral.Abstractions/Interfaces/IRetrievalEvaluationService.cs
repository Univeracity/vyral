using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vyral.Primitives;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public interface IRetrievalEvaluationService
{
    Task<RetrievalEvaluationResult> EvaluateAsync(
        RetrievalEvaluationRequest request,
        CancellationToken ct = default,
        IProgress<RetrievalEvaluationProgress>? progress = null);

    Task<RetrievalEvaluationComparisonResult> CompareAsync(
        RetrievalEvaluationComparisonRequest request,
        CancellationToken ct = default,
        IProgress<RetrievalEvaluationComparisonProgress>? progress = null);
}

public class RetrievalEvaluationRequest
{
    [JsonPropertyName("cases")]
    public List<RetrievalEvaluationCase> Cases { get; set; } = new();

    [JsonPropertyName("continueOnError")]
    public bool ContinueOnError { get; set; } = true;

    [JsonPropertyName("defaultK")]
    public int? DefaultK { get; set; }

    [JsonPropertyName("includeTopResults")]
    public bool IncludeTopResults { get; set; } = true;
}

public class RetrievalEvaluationComparisonRequest
{
    [JsonPropertyName("cases")]
    public List<RetrievalEvaluationCase> Cases { get; set; } = new();

    [JsonPropertyName("variants")]
    public List<RetrievalEvaluationVariant> Variants { get; set; } = new();

    [JsonPropertyName("continueOnError")]
    public bool ContinueOnError { get; set; } = true;

    [JsonPropertyName("defaultK")]
    public int? DefaultK { get; set; }

    [JsonPropertyName("includeTopResults")]
    public bool IncludeTopResults { get; set; }

    [JsonPropertyName("includeCaseResults")]
    public bool IncludeCaseResults { get; set; }
}

public class RetrievalEvaluationVariant
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    [JsonPropertyName("collections")]
    public List<string>? Collections { get; set; }

    [JsonPropertyName("partitionKeys")]
    public List<string>? PartitionKeys { get; set; }

    [JsonPropertyName("filter")]
    public FilterNode? Filter { get; set; }

    [JsonPropertyName("embedding")]
    public EmbeddingOptions? Embedding { get; set; }

    [JsonPropertyName("vectorFields")]
    public List<RetrievalVectorFieldQuery>? VectorFields { get; set; }

    [JsonPropertyName("searchMode")]
    public string? SearchMode { get; set; }

    [JsonPropertyName("lexical")]
    public LexicalSearchOptions? Lexical { get; set; }

    [JsonPropertyName("hybrid")]
    public HybridSearchOptions? Hybrid { get; set; }

    [JsonPropertyName("rerank")]
    public RerankOptions? Rerank { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("minScore")]
    public float? MinScore { get; set; }

    [JsonPropertyName("includeTrace")]
    public bool? IncludeTrace { get; set; }
}

public class RetrievalEvaluationCase
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("request")]
    public RetrievalRequest Request { get; set; } = new();

    [JsonPropertyName("expected")]
    public List<RetrievalEvaluationExpectedMatch> Expected { get; set; } = new();

    [JsonPropertyName("hardNegatives")]
    public List<RetrievalEvaluationHardNegativeMatch> HardNegatives { get; set; } = new();

    [JsonPropertyName("k")]
    public int? K { get; set; }

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }
}

public class RetrievalEvaluationExpectedMatch
{
    /// <summary>
    /// Expected record id. When omitted, aliases, sourceIds, or sources must
    /// identify the expected match.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    [JsonPropertyName("collection")]
    public string? Collection { get; set; }

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();

    [JsonPropertyName("sourceIds")]
    public List<string> SourceIds { get; set; } = new();

    [JsonPropertyName("sources")]
    public List<VyralSourceReference> Sources { get; set; } = new();

    [JsonPropertyName("relevance")]
    public double Relevance { get; set; } = 1.0;
}

public class RetrievalEvaluationHardNegativeMatch
{
    /// <summary>
    /// Hard-negative record id. When omitted, aliases, sourceIds, or sources
    /// must identify the hard negative.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    [JsonPropertyName("collection")]
    public string? Collection { get; set; }

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();

    [JsonPropertyName("sourceIds")]
    public List<string> SourceIds { get; set; } = new();

    [JsonPropertyName("sources")]
    public List<VyralSourceReference> Sources { get; set; } = new();

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public class RetrievalEvaluationResult
{
    [JsonPropertyName("requested")]
    public int Requested { get; set; }

    [JsonPropertyName("attempted")]
    public int Attempted { get; set; }

    [JsonPropertyName("succeeded")]
    public int Succeeded { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("stoppedOnError")]
    public bool StoppedOnError { get; set; }

    [JsonPropertyName("hitCount")]
    public int HitCount { get; set; }

    [JsonPropertyName("hitRate")]
    public double HitRate { get; set; }

    [JsonPropertyName("meanReciprocalRank")]
    public double MeanReciprocalRank { get; set; }

    [JsonPropertyName("meanPrecisionAtK")]
    public double MeanPrecisionAtK { get; set; }

    [JsonPropertyName("meanRecallAtK")]
    public double MeanRecallAtK { get; set; }

    [JsonPropertyName("meanNdcgAtK")]
    public double MeanNdcgAtK { get; set; }

    [JsonPropertyName("hardNegativeCaseCount")]
    public int HardNegativeCaseCount { get; set; }

    [JsonPropertyName("hardNegativeHitCount")]
    public int HardNegativeHitCount { get; set; }

    [JsonPropertyName("hardNegativeHitRate")]
    public double HardNegativeHitRate { get; set; }

    [JsonPropertyName("meanHardNegativeRateAtK")]
    public double MeanHardNegativeRateAtK { get; set; }

    [JsonPropertyName("rerankCaseCount")]
    public int RerankCaseCount { get; set; }

    [JsonPropertyName("rerankFallbackCaseCount")]
    public int RerankFallbackCaseCount { get; set; }

    [JsonPropertyName("rerankFallbackRate")]
    public double RerankFallbackRate { get; set; }

    [JsonPropertyName("cases")]
    public List<RetrievalEvaluationCaseResult> Cases { get; set; } = new();
}

public class RetrievalEvaluationComparisonResult
{
    [JsonPropertyName("requested")]
    public int Requested { get; set; }

    [JsonPropertyName("variantsRequested")]
    public int VariantsRequested { get; set; }

    [JsonPropertyName("variantsAttempted")]
    public int VariantsAttempted { get; set; }

    [JsonPropertyName("variantsSucceeded")]
    public int VariantsSucceeded { get; set; }

    [JsonPropertyName("variantsFailed")]
    public int VariantsFailed { get; set; }

    [JsonPropertyName("stoppedOnError")]
    public bool StoppedOnError { get; set; }

    [JsonPropertyName("baselineVariantId")]
    public string? BaselineVariantId { get; set; }

    [JsonPropertyName("variants")]
    public List<RetrievalEvaluationVariantResult> Variants { get; set; } = new();
}

public class RetrievalEvaluationProgress
{
    [JsonPropertyName("currentCaseIndex")]
    public int? CurrentCaseIndex { get; set; }

    [JsonPropertyName("currentCaseName")]
    public string? CurrentCaseName { get; set; }

    [JsonPropertyName("requested")]
    public int Requested { get; set; }

    [JsonPropertyName("casesAttempted")]
    public int CasesAttempted { get; set; }

    [JsonPropertyName("casesSucceeded")]
    public int CasesSucceeded { get; set; }

    [JsonPropertyName("casesFailed")]
    public int CasesFailed { get; set; }

    [JsonPropertyName("result")]
    public RetrievalEvaluationResult? Result { get; set; }
}

public class RetrievalEvaluationComparisonProgress
{
    [JsonPropertyName("currentVariantId")]
    public string? CurrentVariantId { get; set; }

    [JsonPropertyName("currentVariantIndex")]
    public int? CurrentVariantIndex { get; set; }

    [JsonPropertyName("requested")]
    public int Requested { get; set; }

    [JsonPropertyName("variantsRequested")]
    public int VariantsRequested { get; set; }

    [JsonPropertyName("variantsAttempted")]
    public int VariantsAttempted { get; set; }

    [JsonPropertyName("variantsSucceeded")]
    public int VariantsSucceeded { get; set; }

    [JsonPropertyName("variantsFailed")]
    public int VariantsFailed { get; set; }

    [JsonPropertyName("result")]
    public RetrievalEvaluationComparisonResult? Result { get; set; }
}

public static class RetrievalEvaluationJobKinds
{
    public const string Evaluation = "evaluation";
    public const string Comparison = "comparison";
}

public static class RetrievalEvaluationJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
}

public class RetrievalEvaluationJob
{
    [JsonPropertyName("admission")]
    public AdmissionReceipt Admission { get; set; } = new();

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = RetrievalEvaluationJobKinds.Comparison;

    [JsonPropertyName("status")]
    public string Status { get; set; } = RetrievalEvaluationJobStatuses.Queued;

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

    [JsonPropertyName("casesAttempted")]
    public int CasesAttempted { get; set; }

    [JsonPropertyName("casesSucceeded")]
    public int CasesSucceeded { get; set; }

    [JsonPropertyName("casesFailed")]
    public int CasesFailed { get; set; }

    [JsonPropertyName("currentCaseIndex")]
    public int? CurrentCaseIndex { get; set; }

    [JsonPropertyName("currentCaseName")]
    public string? CurrentCaseName { get; set; }

    [JsonPropertyName("variantsRequested")]
    public int VariantsRequested { get; set; }

    [JsonPropertyName("variantsAttempted")]
    public int VariantsAttempted { get; set; }

    [JsonPropertyName("variantsSucceeded")]
    public int VariantsSucceeded { get; set; }

    [JsonPropertyName("variantsFailed")]
    public int VariantsFailed { get; set; }

    [JsonPropertyName("currentVariantId")]
    public string? CurrentVariantId { get; set; }

    [JsonPropertyName("currentVariantIndex")]
    public int? CurrentVariantIndex { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("evaluationResult")]
    public RetrievalEvaluationResult? EvaluationResult { get; set; }

    [JsonPropertyName("result")]
    public RetrievalEvaluationComparisonResult? Result { get; set; }
}

public class RetrievalEvaluationVariantResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("durationMs")]
    public double DurationMs { get; set; }

    [JsonPropertyName("metrics")]
    public RetrievalEvaluationMetrics Metrics { get; set; } = new();

    [JsonPropertyName("deltaFromBaseline")]
    public RetrievalEvaluationMetricDeltas? DeltaFromBaseline { get; set; }

    [JsonPropertyName("cases")]
    public List<RetrievalEvaluationCaseResult> Cases { get; set; } = new();
}

public class RetrievalEvaluationMetrics
{
    [JsonPropertyName("requested")]
    public int Requested { get; set; }

    [JsonPropertyName("attempted")]
    public int Attempted { get; set; }

    [JsonPropertyName("succeeded")]
    public int Succeeded { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("stoppedOnError")]
    public bool StoppedOnError { get; set; }

    [JsonPropertyName("hitCount")]
    public int HitCount { get; set; }

    [JsonPropertyName("hitRate")]
    public double HitRate { get; set; }

    [JsonPropertyName("meanReciprocalRank")]
    public double MeanReciprocalRank { get; set; }

    [JsonPropertyName("meanPrecisionAtK")]
    public double MeanPrecisionAtK { get; set; }

    [JsonPropertyName("meanRecallAtK")]
    public double MeanRecallAtK { get; set; }

    [JsonPropertyName("meanNdcgAtK")]
    public double MeanNdcgAtK { get; set; }

    [JsonPropertyName("hardNegativeCaseCount")]
    public int HardNegativeCaseCount { get; set; }

    [JsonPropertyName("hardNegativeHitCount")]
    public int HardNegativeHitCount { get; set; }

    [JsonPropertyName("hardNegativeHitRate")]
    public double HardNegativeHitRate { get; set; }

    [JsonPropertyName("meanHardNegativeRateAtK")]
    public double MeanHardNegativeRateAtK { get; set; }

    [JsonPropertyName("rerankCaseCount")]
    public int RerankCaseCount { get; set; }

    [JsonPropertyName("rerankFallbackCaseCount")]
    public int RerankFallbackCaseCount { get; set; }

    [JsonPropertyName("rerankFallbackRate")]
    public double RerankFallbackRate { get; set; }
}

public class RetrievalEvaluationMetricDeltas
{
    [JsonPropertyName("hitRate")]
    public double HitRate { get; set; }

    [JsonPropertyName("meanReciprocalRank")]
    public double MeanReciprocalRank { get; set; }

    [JsonPropertyName("meanPrecisionAtK")]
    public double MeanPrecisionAtK { get; set; }

    [JsonPropertyName("meanRecallAtK")]
    public double MeanRecallAtK { get; set; }

    [JsonPropertyName("meanNdcgAtK")]
    public double MeanNdcgAtK { get; set; }

    [JsonPropertyName("hardNegativeHitRate")]
    public double HardNegativeHitRate { get; set; }

    [JsonPropertyName("meanHardNegativeRateAtK")]
    public double MeanHardNegativeRateAtK { get; set; }

    [JsonPropertyName("rerankFallbackRate")]
    public double RerankFallbackRate { get; set; }
}

public static class EvaluationVariantStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public class RetrievalEvaluationCaseResult
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    /// <summary>Outcome of this evaluation case. Valid values are defined in <see cref="EvaluationCaseStatuses"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("durationMs")]
    public double DurationMs { get; set; }

    [JsonPropertyName("k")]
    public int K { get; set; }

    [JsonPropertyName("expectedCount")]
    public int ExpectedCount { get; set; }

    [JsonPropertyName("retrievedCount")]
    public int RetrievedCount { get; set; }

    [JsonPropertyName("matchedCount")]
    public int MatchedCount { get; set; }

    [JsonPropertyName("hit")]
    public bool Hit { get; set; }

    [JsonPropertyName("firstRelevantRank")]
    public int? FirstRelevantRank { get; set; }

    [JsonPropertyName("reciprocalRank")]
    public double ReciprocalRank { get; set; }

    [JsonPropertyName("precisionAtK")]
    public double PrecisionAtK { get; set; }

    [JsonPropertyName("recallAtK")]
    public double RecallAtK { get; set; }

    [JsonPropertyName("ndcgAtK")]
    public double NdcgAtK { get; set; }

    [JsonPropertyName("hardNegativeCount")]
    public int HardNegativeCount { get; set; }

    [JsonPropertyName("hardNegativeMatchedCount")]
    public int HardNegativeMatchedCount { get; set; }

    [JsonPropertyName("hardNegativeHit")]
    public bool HardNegativeHit { get; set; }

    [JsonPropertyName("firstHardNegativeRank")]
    public int? FirstHardNegativeRank { get; set; }

    [JsonPropertyName("hardNegativeRateAtK")]
    public double HardNegativeRateAtK { get; set; }

    [JsonPropertyName("rerankEnabled")]
    public bool RerankEnabled { get; set; }

    [JsonPropertyName("rerankProvider")]
    public string? RerankProvider { get; set; }

    [JsonPropertyName("rerankTraceId")]
    public string? RerankTraceId { get; set; }

    [JsonPropertyName("rerankFallbackApplied")]
    public bool RerankFallbackApplied { get; set; }

    [JsonPropertyName("rerankFailureClass")]
    public string? RerankFailureClass { get; set; }

    [JsonPropertyName("rerankProviderStatus")]
    public string? RerankProviderStatus { get; set; }

    [JsonPropertyName("rerankInputCandidateCount")]
    public int RerankInputCandidateCount { get; set; }

    [JsonPropertyName("rerankProviderPayloadBytes")]
    public int RerankProviderPayloadBytes { get; set; }

    [JsonPropertyName("rerankProviderMaxInputBytes")]
    public int RerankProviderMaxInputBytes { get; set; }

    [JsonPropertyName("expected")]
    public List<RetrievalEvaluationExpectedResult> Expected { get; set; } = new();

    [JsonPropertyName("hardNegatives")]
    public List<RetrievalEvaluationHardNegativeResult> HardNegatives { get; set; } = new();

    [JsonPropertyName("topResults")]
    public List<RetrievalEvaluationTopResult> TopResults { get; set; } = new();
}

public class RetrievalEvaluationExpectedResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    [JsonPropertyName("collection")]
    public string? Collection { get; set; }

    [JsonPropertyName("relevance")]
    public double Relevance { get; set; }

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    [JsonPropertyName("score")]
    public float? Score { get; set; }
}

public class RetrievalEvaluationHardNegativeResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string? PartitionKey { get; set; }

    [JsonPropertyName("collection")]
    public string? Collection { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    [JsonPropertyName("score")]
    public float? Score { get; set; }
}

public static class EvaluationCaseStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public class RetrievalEvaluationTopResult
{
    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("score")]
    public float Score { get; set; }

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("matchedExpected")]
    public bool MatchedExpected { get; set; }

    [JsonPropertyName("matchedHardNegative")]
    public bool MatchedHardNegative { get; set; }

    [JsonPropertyName("rerankFallbackApplied")]
    public bool RerankFallbackApplied { get; set; }

    [JsonPropertyName("rerankProviderStatus")]
    public string? RerankProviderStatus { get; set; }

    [JsonPropertyName("vectorIndexUsed")]
    public bool VectorIndexUsed { get; set; }

    [JsonPropertyName("vectorIndexProvider")]
    public string? VectorIndexProvider { get; set; }

    [JsonPropertyName("vectorIndexFields")]
    public List<string> VectorIndexFields { get; set; } = new();

    [JsonPropertyName("snippet")]
    public string? Snippet { get; set; }
}
