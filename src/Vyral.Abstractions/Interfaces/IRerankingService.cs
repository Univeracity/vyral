using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Vyral.Abstractions.Interfaces;

public interface IRerankingService
{
    Task<RerankResult> RerankAsync(RerankRequest request, CancellationToken ct = default);
}

public class RerankOptions
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>
    /// Provider execution mode. Valid values are defined in ProviderModes in Vyral.Providers.Abstractions.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "advisory";

    [JsonPropertyName("candidateLimit")]
    public int? CandidateLimit { get; set; }

    [JsonPropertyName("maxCandidateChars")]
    public int MaxCandidateChars { get; set; } = 2000;

    [JsonPropertyName("contentField")]
    public string? ContentField { get; set; }

    /// <summary>
    /// Blend weights for the final score: finalScore = (rerankerScore * RerankScoreWeight) + (originalScore * OriginalScoreWeight).
    /// Default (1.0 / 0.0) = trust the reranker completely and discard the original retrieval score.
    /// For a blended result, use e.g. RerankScoreWeight = 0.7f, OriginalScoreWeight = 0.3f.
    /// Weights do not need to sum to 1.0; the service does not normalize them.
    /// </summary>
    [JsonPropertyName("rerankScoreWeight")]
    public float RerankScoreWeight { get; set; } = 1.0f;

    [JsonPropertyName("originalScoreWeight")]
    public float OriginalScoreWeight { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; set; }

    [JsonPropertyName("maxOutputBytes")]
    public int? MaxOutputBytes { get; set; }

    [JsonPropertyName("fallbackOnFailure")]
    public bool FallbackOnFailure { get; set; } = true;
}

public class RerankRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 10;

    [JsonPropertyName("options")]
    public RerankOptions Options { get; set; } = new();

    [JsonPropertyName("candidates")]
    public List<RerankCandidate> Candidates { get; set; } = new();
}

public class RerankCandidate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("recordId")]
    public string RecordId { get; set; } = string.Empty;

    [JsonPropertyName("originalRank")]
    public int OriginalRank { get; set; }

    [JsonPropertyName("originalScore")]
    public float OriginalScore { get; set; }

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }
}

public class RerankResult
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("inputCandidateCount")]
    public int InputCandidateCount { get; set; }

    [JsonPropertyName("providerPayloadBytes")]
    public int? ProviderPayloadBytes { get; set; }

    [JsonPropertyName("providerMaxInputBytes")]
    public int? ProviderMaxInputBytes { get; set; }

    [JsonPropertyName("fallbackApplied")]
    public bool FallbackApplied { get; set; }

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("providerStatus")]
    public string? ProviderStatus { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("items")]
    public List<RerankResultItem> Items { get; set; } = new();
}

public class RerankResultItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("score")]
    public float Score { get; set; }
}

public sealed class RerankProviderException : InvalidOperationException
{
    public RerankProviderException(
        string message,
        string provider,
        string status,
        string? failureClass,
        string? providerStatus,
        string? traceId,
        int? providerPayloadBytes,
        int? providerMaxInputBytes,
        int? candidateCount)
        : base(message)
    {
        Provider = provider;
        Status = status;
        FailureClass = failureClass;
        ProviderStatus = providerStatus;
        TraceId = traceId;
        ProviderPayloadBytes = providerPayloadBytes;
        ProviderMaxInputBytes = providerMaxInputBytes;
        CandidateCount = candidateCount;
    }

    public string Provider { get; }
    /// <summary>Stringified ProviderRunStatus enum value (e.g. "Failed", "TimedOut", "Rejected"). See ProviderRunStatus in Vyral.Providers.Abstractions.</summary>
    public string Status { get; }
    public string? FailureClass { get; }
    public string? ProviderStatus { get; }
    public string? TraceId { get; }
    public int? ProviderPayloadBytes { get; }
    public int? ProviderMaxInputBytes { get; }
    public int? CandidateCount { get; }
}
