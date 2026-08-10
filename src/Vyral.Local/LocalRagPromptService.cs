using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;

namespace Vyral.Local;

public class LocalRagPromptService : IRagPromptService
{
    private const string ChatMarkdownFormat = "chat-markdown";
    private const string DefaultSystemInstruction = "Answer the user's question using only the provided context. If the context is insufficient, say that the context does not contain enough information.";
    private const string DefaultCitationInstruction = "Cite supporting claims with the provided citation ids, such as [c1].";

    private readonly IRagContextService _contextService;

    public LocalRagPromptService(IRagContextService contextService)
    {
        _contextService = contextService;
    }

    public async Task<RagPromptEnvelope> BuildPromptAsync(RagPromptRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);

        var template = request.Template ?? new RagPromptTemplateOptions();
        var format = NormalizeFormat(template.Format);
        var contextRequest = CloneContextRequest(request.Context);
        contextRequest.IncludeContextText = true;
        contextRequest.IncludeCitations = true;

        var context = await _contextService.BuildContextAsync(contextRequest, ct);
        if (template.FailOnEmptyContext && context.Chunks.Count == 0)
        {
            throw new InvalidOperationException("RAG prompt assembly produced no context chunks.");
        }

        var system = string.IsNullOrWhiteSpace(template.SystemInstruction)
            ? DefaultSystemInstruction
            : template.SystemInstruction.Trim();
        var user = string.IsNullOrWhiteSpace(template.UserInstruction)
            ? context.Query
            : template.UserInstruction.Trim();
        var citationInstruction = template.IncludeCitationInstruction
            ? string.IsNullOrWhiteSpace(template.CitationInstruction)
                ? DefaultCitationInstruction
                : template.CitationInstruction.Trim()
            : null;

        var userContent = RenderUserContent(user, context.ContextText ?? string.Empty, citationInstruction);
        var messages = new List<RagPromptMessage>
        {
            new() { Role = "system", Content = system },
            new() { Role = "user", Content = userContent }
        };
        var prompt = RenderPrompt(messages);
        if (template.MaxPromptChars.HasValue && prompt.Length > template.MaxPromptChars.Value)
        {
            throw new InvalidOperationException($"RAG prompt length {prompt.Length} exceeds maxPromptChars {template.MaxPromptChars.Value}.");
        }

        var envelope = new RagPromptEnvelope
        {
            Query = context.Query,
            Format = format,
            Prompt = prompt,
            PromptHash = $"sha256:{Sha256Hex(prompt)}",
            Messages = messages,
            Context = context
        };

        if (request.Context.IncludeTrace)
        {
            envelope.Trace = new JsonObject
            {
                ["promptHash"] = envelope.PromptHash,
                ["promptChars"] = envelope.Prompt.Length,
                ["messageCount"] = messages.Count,
                ["format"] = format,
                ["contextTextHash"] = context.ContextTextHash ?? string.Empty,
                ["contextChunkCount"] = context.Chunks.Count,
                ["citationCount"] = context.Citations.Count,
                ["failOnEmptyContext"] = template.FailOnEmptyContext
            };
        }

        return envelope;
    }

    private static void ValidateRequest(RagPromptRequest request)
    {
        if (request.Context is null)
        {
            throw new InvalidOperationException("RAG prompt request requires a context request.");
        }

        _ = NormalizeFormat(request.Template?.Format);

        if (request.Template?.MaxPromptChars is <= 0)
        {
            throw new InvalidOperationException("RAG prompt maxPromptChars must be greater than zero when provided.");
        }
    }

    private static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return ChatMarkdownFormat;
        }

        return format.Trim().ToLowerInvariant() switch
        {
            ChatMarkdownFormat => ChatMarkdownFormat,
            _ => throw new InvalidOperationException($"RAG prompt format '{format}' is not supported.")
        };
    }

    private static RagContextRequest CloneContextRequest(RagContextRequest source)
    {
        return new RagContextRequest
        {
            Retrieval = source.Retrieval,
            ContentField = source.ContentField,
            MaxChars = source.MaxChars,
            MaxCharsPerChunk = source.MaxCharsPerChunk,
            ContextAssembly = source.ContextAssembly,
            GraphExpansion = source.GraphExpansion,
            IncludeRecords = source.IncludeRecords,
            IncludeCitations = true,
            IncludeContextText = true,
            IncludeTrace = source.IncludeTrace
        };
    }

    private static string RenderUserContent(string userInstruction, string contextText, string? citationInstruction)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Question:");
        builder.AppendLine(userInstruction);
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(citationInstruction))
        {
            builder.AppendLine("Citation rule:");
            builder.AppendLine(citationInstruction);
            builder.AppendLine();
        }

        builder.AppendLine(contextText);
        return builder.ToString().TrimEnd();
    }

    private static string RenderPrompt(IReadOnlyList<RagPromptMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append(message.Role.ToUpperInvariant());
            builder.AppendLine(":");
            builder.AppendLine(message.Content);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
