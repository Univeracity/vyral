using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Vyral.Abstractions.Interfaces;

public interface IRagPromptService
{
    Task<RagPromptEnvelope> BuildPromptAsync(RagPromptRequest request, CancellationToken ct = default);
}

public class RagPromptRequest
{
    [JsonPropertyName("context")]
    public RagContextRequest Context { get; set; } = new();

    [JsonPropertyName("template")]
    public RagPromptTemplateOptions Template { get; set; } = new();
}

public class RagPromptTemplateOptions
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = PromptFormats.ChatMarkdown;

    [JsonPropertyName("systemInstruction")]
    public string? SystemInstruction { get; set; }

    [JsonPropertyName("userInstruction")]
    public string? UserInstruction { get; set; }

    [JsonPropertyName("citationInstruction")]
    public string? CitationInstruction { get; set; }

    [JsonPropertyName("includeCitationInstruction")]
    public bool IncludeCitationInstruction { get; set; } = true;

    [JsonPropertyName("failOnEmptyContext")]
    public bool FailOnEmptyContext { get; set; }

    [JsonPropertyName("maxPromptChars")]
    public int? MaxPromptChars { get; set; }
}

public class RagPromptEnvelope
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    /// <summary>Format of the assembled prompt. Echoes the value set in RagPromptTemplateOptions.Format. Valid values are defined in <see cref="PromptFormats"/>.</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("promptHash")]
    public string PromptHash { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<RagPromptMessage> Messages { get; set; } = new();

    [JsonPropertyName("context")]
    public RagContextEnvelope Context { get; set; } = new();

    [JsonPropertyName("trace")]
    public JsonObject? Trace { get; set; }
}

public class RagPromptMessage
{
    /// <summary>Message role. Values mirror AiRoles in Vyral.Providers.Abstractions: "system", "user", "assistant".</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

public static class PromptFormats
{
    /// <summary>Chat-style prompt with Markdown context blocks; compatible with most LLM chat APIs.</summary>
    public const string ChatMarkdown = "chat-markdown";
    /// <summary>Plain text prompt with context inserted inline.</summary>
    public const string PlainText = "plain-text";
}
