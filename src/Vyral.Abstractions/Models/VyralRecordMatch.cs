using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public class VyralRecordMatch
{
    [JsonPropertyName("record")]
    public VyralRecord Record { get; set; } = null!;

    [JsonPropertyName("score")]
    public float Score { get; set; }

    [JsonPropertyName("diagnostics")]
    public RetrievalDiagnostics? Diagnostics { get; set; }
}

public class RetrievalDiagnostics
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "vyral.retrieval.diagnostics.v1";

    [JsonPropertyName("resultIdentity")]
    public RetrievalResultIdentity? ResultIdentity { get; set; }

    [JsonPropertyName("scoreComponents")]
    public Dictionary<string, float> ScoreComponents { get; set; } = new();

    [JsonPropertyName("scoreNormalization")]
    public RetrievalScoreNormalization? ScoreNormalization { get; set; }

    [JsonPropertyName("candidateSources")]
    public List<string> CandidateSources { get; set; } = new();

    [JsonPropertyName("candidateCounts")]
    public Dictionary<string, int> CandidateCounts { get; set; } = new();

    [JsonPropertyName("reasonCodes")]
    public List<string> ReasonCodes { get; set; } = new();

    [JsonPropertyName("matchedFields")]
    public List<string> MatchedFields { get; set; } = new();

    [JsonPropertyName("matchedTerms")]
    public List<string> MatchedTerms { get; set; } = new();

    [JsonPropertyName("traceReferences")]
    public List<RetrievalTraceReference> TraceReferences { get; set; } = new();

    [JsonPropertyName("details")]
    public Dictionary<string, object?> Details { get; set; } = new();
}

public class RetrievalResultIdentity
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("etag")]
    public string? Etag { get; set; }

    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

public class RetrievalScoreNormalization
{
    [JsonPropertyName("finalScoreKind")]
    public string FinalScoreKind { get; set; } = string.Empty;

    [JsonPropertyName("vectorScoreKind")]
    public string? VectorScoreKind { get; set; }

    [JsonPropertyName("lexicalScoreKind")]
    public string? LexicalScoreKind { get; set; }

    [JsonPropertyName("hybridFusion")]
    public string? HybridFusion { get; set; }

    [JsonPropertyName("vectorDistanceFunction")]
    public string? VectorDistanceFunction { get; set; }

    [JsonPropertyName("vectorNormalization")]
    public string? VectorNormalization { get; set; }

    [JsonPropertyName("weights")]
    public Dictionary<string, float> Weights { get; set; } = new();

    [JsonPropertyName("parameters")]
    public Dictionary<string, object?> Parameters { get; set; } = new();
}

public class RetrievalTraceReference
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("redaction")]
    public string Redaction { get; set; } = "trace_ref_only";

    [JsonPropertyName("safe")]
    public bool Safe { get; set; } = true;
}
