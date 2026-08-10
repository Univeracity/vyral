using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public sealed class AiMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = AiRoles.User;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Structured content blocks for multi-modal or tool-result messages.
    /// Providers that support content blocks use this; others fall back to Content.
    /// Shape per block: {"type": "text"|"image_url"|"tool_result", ...}
    /// </summary>
    [JsonPropertyName("contentBlocks")]
    public JsonArray? ContentBlocks { get; set; }
}

public sealed class AiChatRequest
{
    [JsonPropertyName("messages")]
    public List<AiMessage> Messages { get; set; } = new();

    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("maxOutputChars")]
    public int? MaxOutputChars { get; set; }
}

public sealed class AiChatResult
{
    [JsonPropertyName("message")]
    public AiMessage Message { get; set; } = new() { Role = AiRoles.Assistant };

    [JsonPropertyName("stopReason")]
    public string StopReason { get; set; } = AiStopReasons.Complete;
}

public sealed class AiExtractRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("schema")]
    public JsonObject? Schema { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }
}

public sealed class AiExtractResult
{
    [JsonPropertyName("data")]
    public JsonObject Data { get; set; } = new();

    [JsonPropertyName("validationStatus")]
    public string ValidationStatus { get; set; } = AiValidationStatuses.NotValidated;
}

public sealed class AiRerankCandidate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }
}

public sealed class AiRerankRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("candidates")]
    public List<AiRerankCandidate> Candidates { get; set; } = new();

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

public sealed class AiRerankItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }
}

public sealed class AiRerankResult
{
    [JsonPropertyName("items")]
    public List<AiRerankItem> Items { get; set; } = new();

    [JsonPropertyName("validationStatus")]
    public string ValidationStatus { get; set; } = AiValidationStatuses.NotValidated;
}

public sealed class AiToolDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("inputSchema")]
    public JsonObject? InputSchema { get; set; }
}

public sealed class AiToolPlanRequest
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("tools")]
    public List<AiToolDefinition> Tools { get; set; } = new();
}

public sealed class AiToolCallProposal
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonObject Arguments { get; set; } = new();

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; set; } = true;

    [JsonPropertyName("rationale")]
    public string? Rationale { get; set; }
}

public sealed class AiToolPlanResult
{
    [JsonPropertyName("calls")]
    public List<AiToolCallProposal> Calls { get; set; } = new();

    [JsonPropertyName("validationStatus")]
    public string ValidationStatus { get; set; } = AiValidationStatuses.NotValidated;
}

public sealed class AiReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = AiReferenceKinds.Context;

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }
}

public sealed class AiReviewRequest
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("references")]
    public List<AiReference> References { get; set; } = new();

    [JsonPropertyName("maxFindings")]
    public int? MaxFindings { get; set; }
}

public sealed class AiReviewLocation
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("column")]
    public int? Column { get; set; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }
}

public sealed class AiReviewFinding
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = AiReviewSeverities.Info;

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public AiReviewLocation? Location { get; set; }

    [JsonPropertyName("evidenceRefs")]
    public List<string> EvidenceRefs { get; set; } = new();

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}

public sealed class AiReviewResult
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("findings")]
    public List<AiReviewFinding> Findings { get; set; } = new();

    [JsonPropertyName("validationStatus")]
    public string ValidationStatus { get; set; } = AiValidationStatuses.NotValidated;

    [JsonPropertyName("references")]
    public List<AiReference> References { get; set; } = new();
}

public sealed class AiScaffoldRequest
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("references")]
    public List<AiReference> References { get; set; } = new();

    [JsonPropertyName("allowedPaths")]
    public List<string> AllowedPaths { get; set; } = new();

    [JsonPropertyName("maxArtifacts")]
    public int? MaxArtifacts { get; set; }
}

public sealed class AiScaffoldArtifact
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = AiArtifactKinds.File;

    [JsonPropertyName("action")]
    public string Action { get; set; } = AiArtifactActions.Propose;

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("evidenceRefs")]
    public List<string> EvidenceRefs { get; set; } = new();

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }
}

public sealed class AiScaffoldResult
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("artifacts")]
    public List<AiScaffoldArtifact> Artifacts { get; set; } = new();

    [JsonPropertyName("validationStatus")]
    public string ValidationStatus { get; set; } = AiValidationStatuses.NotValidated;

    [JsonPropertyName("references")]
    public List<AiReference> References { get; set; } = new();
}

// ---------------------------------------------------------------------------
// String constant classes for AI capability discriminators
// ---------------------------------------------------------------------------

public static class AiRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
}

public static class AiStopReasons
{
    public const string Complete = "complete";
    public const string Cancelled = "cancelled";
    public const string Timeout = "timeout";
    /// <summary>Output was cut short by a token or character limit.</summary>
    public const string Length = "length";
}

public static class AiValidationStatuses
{
    /// <summary>No schema validation was performed on the output.</summary>
    public const string NotValidated = "not_validated";
    /// <summary>Output was generated deterministically (e.g. from a test fixture); not a real model call.</summary>
    public const string Deterministic = "deterministic";
    /// <summary>Provider returned structured JSON that was used without further schema validation.</summary>
    public const string ProviderJson = "provider_json";
}

public static class AiReviewSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Critical = "critical";
}

public static class AiReferenceKinds
{
    public const string Context = "context";
}

public static class AiArtifactKinds
{
    public const string File = "file";
}

public static class AiArtifactActions
{
    public const string Propose = "propose";
}
