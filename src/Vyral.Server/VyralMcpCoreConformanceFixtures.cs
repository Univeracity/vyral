using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

#pragma warning disable MCPEXP001
#pragma warning disable MCP9005

namespace Vyral.Server;

/// <summary>
/// Core protocol fixtures required by the official MCP conformance runner. These are registered
/// only by Development-only conformance mode and are never part of Vyral's product catalog.
/// </summary>
[McpServerToolType]
public sealed class VyralMcpCoreConformanceTools
{
    private const string TestImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";
    private const string TestAudioBase64 =
        "UklGRiYAAABXQVZFZm10IBAAAAABAAEAQB8AAAB9AAACABAAZGF0YQIAAAA=";

    [McpServerTool(Name = "test_simple_text")]
    [Description("Returns the text fixture required by MCP conformance.")]
    public static string SimpleText() => "This is a simple text response for testing.";

    [McpServerTool(Name = "test_image_content")]
    [Description("Returns an image content block required by MCP conformance.")]
    public static ImageContentBlock ImageContent() => new()
    {
        Data = Convert.FromBase64String(TestImageBase64),
        MimeType = "image/png"
    };

    [McpServerTool(Name = "test_audio_content")]
    [Description("Returns an audio content block required by MCP conformance.")]
    public static AudioContentBlock AudioContent() => new()
    {
        Data = Convert.FromBase64String(TestAudioBase64),
        MimeType = "audio/wav"
    };

    [McpServerTool(Name = "test_embedded_resource")]
    [Description("Returns an embedded resource block required by MCP conformance.")]
    public static EmbeddedResourceBlock EmbeddedResource() => new()
    {
        Resource = new TextResourceContents
        {
            Uri = "test://embedded-resource",
            MimeType = "text/plain",
            Text = "This is an embedded resource content."
        }
    };

    [McpServerTool(Name = "test_multiple_content_types")]
    [Description("Returns mixed MCP content blocks required by MCP conformance.")]
    public static ContentBlock[] MultipleContentTypes() =>
    [
        new TextContentBlock { Text = "Multiple content types test:" },
        ImageContent(),
        new EmbeddedResourceBlock
        {
            Resource = new TextResourceContents
            {
                Uri = "test://mixed-content-resource",
                MimeType = "application/json",
                Text = "{ \"test\": \"data\", \"value\": 123 }"
            }
        }
    ];

    [McpServerTool(Name = "test_error_handling")]
    [Description("Raises the tool error required by MCP conformance.")]
    public static string ErrorHandling() =>
        throw new InvalidOperationException("This tool intentionally returns an error for testing.");

    [McpServerTool(Name = "test_tool_with_progress")]
    [Description("Emits bounded progress notifications required by MCP conformance.")]
    public static async Task<string> ToolWithProgress(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        var progressToken = context.Params!.ProgressToken;
        if (progressToken is not null)
        {
            foreach (var progress in new[] { 0, 50, 100 })
            {
                await server.NotifyProgressAsync(
                    progressToken.Value,
                    new ProgressNotificationValue { Progress = progress, Total = 100 },
                    cancellationToken: cancellationToken);
            }
        }
        return progressToken?.ToString() ?? "No progress token provided";
    }
}

[McpServerResourceType]
public sealed class VyralMcpCoreConformanceResources
{
    private const string TestImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    [McpServerResource(UriTemplate = "test://static-text", Name = "Static Text Resource", MimeType = "text/plain")]
    [Description("Static text fixture required by MCP conformance.")]
    public static string StaticText() => "This is the content of the static text resource.";

    [McpServerResource(UriTemplate = "test://static-binary", Name = "Static Binary Resource", MimeType = "image/png")]
    [Description("Static binary fixture required by MCP conformance.")]
    public static BlobResourceContents StaticBinary() => new()
    {
        Uri = "test://static-binary",
        MimeType = "image/png",
        Blob = Convert.FromBase64String(TestImageBase64)
    };

    [McpServerResource(UriTemplate = "test://template/{id}/data", Name = "Resource Template", MimeType = "application/json")]
    [Description("Parameterized resource fixture required by MCP conformance.")]
    public static TextResourceContents TemplateResource(string id) => new()
    {
        Uri = $"test://template/{id}/data",
        MimeType = "application/json",
        Text = JsonSerializer.Serialize(new { id, templateTest = true, data = $"Data for ID: {id}" })
    };
}

[McpServerPromptType]
public sealed class VyralMcpCoreConformancePrompts
{
    private const string TestImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    [McpServerPrompt(Name = "test_prompt_with_arguments")]
    [Description("Parameterized prompt fixture required by MCP conformance.")]
    public static string ParameterizedPrompt(string arg1, string arg2) =>
        $"Prompt with arguments: arg1={arg1}, arg2={arg2}";

    [McpServerPrompt(Name = "test_prompt_with_embedded_resource")]
    [Description("Embedded-resource prompt fixture required by MCP conformance.")]
    public static IEnumerable<PromptMessage> PromptWithEmbeddedResource(string resourceUri) =>
    [
        new PromptMessage
        {
            Role = Role.User,
            Content = new EmbeddedResourceBlock
            {
                Resource = new TextResourceContents
                {
                    Uri = resourceUri,
                    Text = "Embedded resource content for testing.",
                    MimeType = "text/plain"
                }
            }
        },
        new PromptMessage
        {
            Role = Role.User,
            Content = new TextContentBlock { Text = "Please process the embedded resource above." }
        }
    ];

    [McpServerPrompt(Name = "test_prompt_with_image")]
    [Description("Image prompt fixture required by MCP conformance.")]
    public static IEnumerable<PromptMessage> PromptWithImage() =>
    [
        new PromptMessage
        {
            Role = Role.User,
            Content = new ImageContentBlock
            {
                MimeType = "image/png",
                Data = Convert.FromBase64String(TestImageBase64)
            }
        },
        new PromptMessage
        {
            Role = Role.User,
            Content = new TextContentBlock { Text = "Please analyze the image above." }
        }
    ];

    [McpServerPrompt(Name = "test_input_required_result_prompt")]
    [Description("MRTR prompt fixture required by MCP conformance.")]
    public static GetPromptResult InputRequiredPrompt(RequestContext<GetPromptRequestParams> context)
    {
        if (context.Params!.InputResponses is { } responses &&
            responses.TryGetValue("user_context", out var response))
        {
            var result = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
            var text = TryReadString(result?.Content, "context") ?? "(unknown)";
            return new GetPromptResult
            {
                Description = "Prompt customized with elicited user context.",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock { Text = $"Please continue using context: {text}" }
                    }
                ]
            };
        }

        throw new InputRequiredException(new Dictionary<string, InputRequest>
        {
            ["user_context"] = InputRequest.ForElicitation(new ElicitRequestParams
            {
                Message = "What context should the prompt use?",
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties =
                    {
                        ["context"] = new ElicitRequestParams.StringSchema()
                    },
                    Required = ["context"]
                }
            })
        });
    }

    private static string? TryReadString(IDictionary<string, JsonElement>? content, string key) =>
        content is not null && content.TryGetValue(key, out var element)
            ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
            : null;
}

[McpServerToolType]
public sealed class VyralMcpMrtrConformanceTools
{
    private const string RequestStateToken = "mrtr-conformance-state-v1";

    [McpServerTool(Name = "test_input_required_result_elicitation")]
    [Description("Requests one elicitation response through an input-required result.")]
    public static CallToolResult Elicitation(RequestContext<CallToolRequestParams> context)
    {
        if (context.Params!.InputResponses is { } responses && responses.TryGetValue("user_name", out var response))
        {
            var result = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
            return TextResult($"Hello, {TryReadString(result?.Content, "name") ?? "world"}!");
        }
        throw new InputRequiredException(new Dictionary<string, InputRequest>
        {
            ["user_name"] = ElicitString("What is your name?", "name")
        });
    }

    [McpServerTool(Name = "test_input_required_result_sampling")]
    [Description("Requests one sampling response through an input-required result.")]
    public static CallToolResult Sampling(RequestContext<CallToolRequestParams> context)
    {
        if (context.Params!.InputResponses is { } responses && responses.TryGetValue("capital_question", out var response))
        {
            var text = response.Deserialize(InputResponse.CreateMessageResultJsonTypeInfo)?
                .Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "(no text)";
            return TextResult($"Sampling said: {text}");
        }
        throw new InputRequiredException(new Dictionary<string, InputRequest>
        {
            ["capital_question"] = SamplingRequest("What is the capital of France?")
        });
    }

    [McpServerTool(Name = "test_input_required_result_list_roots")]
    [Description("Requests the client roots through an input-required result.")]
    public static CallToolResult ListRoots(RequestContext<CallToolRequestParams> context)
    {
        if (context.Params!.InputResponses is { } responses && responses.TryGetValue("client_roots", out var response))
        {
            var count = response.Deserialize(InputResponse.ListRootsResultJsonTypeInfo)?.Roots?.Count ?? 0;
            return TextResult($"Got {count} root(s) from the client.");
        }
        throw new InputRequiredException(new Dictionary<string, InputRequest>
        {
            ["client_roots"] = InputRequest.ForRootsList(new ListRootsRequestParams())
        });
    }

    [McpServerTool(Name = "test_input_required_result_request_state")]
    [Description("Round-trips opaque request state through an input-required result.")]
    public static CallToolResult RequestState(RequestContext<CallToolRequestParams> context)
    {
        if (context.Params!.RequestState is { } state)
            return TextResult(state == RequestStateToken ? "state-ok" : "state-mismatch");
        throw new InputRequiredException(
            new Dictionary<string, InputRequest> { ["confirm"] = ElicitBoolean("Please confirm", "ok") },
            RequestStateToken);
    }

    [McpServerTool(Name = "test_input_required_result_multiple_inputs")]
    [Description("Requests multiple input kinds through one input-required result.")]
    public static CallToolResult MultipleInputs(RequestContext<CallToolRequestParams> context)
    {
        if (context.Params!.InputResponses is { Count: >= 3 }) return TextResult("multiple-inputs-ok");
        throw new InputRequiredException(
            new Dictionary<string, InputRequest>
            {
                ["user_name"] = ElicitString("What is your name?", "name"),
                ["greeting"] = SamplingRequest("Generate a greeting"),
                ["client_roots"] = InputRequest.ForRootsList(new ListRootsRequestParams())
            },
            "multi-input-state");
    }

    [McpServerTool(Name = "test_input_required_result_multi_round")]
    [Description("Requests inputs across multiple input-required result rounds.")]
    public static CallToolResult MultiRound(RequestContext<CallToolRequestParams> context)
    {
        if (context.Params!.RequestState is null)
            throw new InputRequiredException(
                new Dictionary<string, InputRequest> { ["step1"] = ElicitString("Step 1: What is your name?", "name") },
                "round-1");
        if (context.Params.RequestState == "round-1")
            throw new InputRequiredException(
                new Dictionary<string, InputRequest> { ["step2"] = ElicitString("Step 2: What is your favorite color?", "color") },
                "round-2");
        return TextResult("multi-round-ok");
    }

    [McpServerTool(Name = "test_incomplete_result_elicitation")]
    [Description("Requests elicitation and detects an incomplete input response set.")]
    public static CallToolResult MissingResponse(RequestContext<CallToolRequestParams> context) => Elicitation(context);

    [McpServerTool(Name = "test_input_required_result_tampered_state")]
    [Description("Rejects input-required request state that fails integrity verification.")]
    public static CallToolResult TamperedState(RequestContext<CallToolRequestParams> context)
    {
        if (context.Params!.RequestState is { } state)
        {
            if (!VerifyRequestState(state))
                throw new McpProtocolException("requestState failed integrity verification.", McpErrorCode.InvalidParams);
            return TextResult("tampered-state-ok");
        }
        throw new InputRequiredException(
            new Dictionary<string, InputRequest> { ["confirm"] = ElicitBoolean("Please confirm", "ok") },
            SignRequestState());
    }

    [McpServerTool(Name = "test_input_required_result_capabilities")]
    [Description("Requests only input kinds declared by the client capabilities.")]
    public static CallToolResult CapabilityCheck(RequestContext<CallToolRequestParams> context)
    {
        if (context.Params!.InputResponses is { Count: > 0 }) return TextResult("capability-check-ok");
        var capabilities = context.JsonRpcRequest.Context?.ClientCapabilities;
        var requests = new Dictionary<string, InputRequest>();
        if (capabilities?.Sampling is not null) requests["capital_question"] = SamplingRequest("What is the capital of France?");
        if (capabilities?.Elicitation is not null) requests["user_name"] = ElicitString("What is your name?", "name");
        if (capabilities?.Roots is not null) requests["client_roots"] = InputRequest.ForRootsList(new ListRootsRequestParams());
        if (requests.Count == 0) return TextResult("capability-check-ok: no MRTR capabilities");
        throw new InputRequiredException(requests);
    }

    private static InputRequest ElicitString(string message, string property) =>
        InputRequest.ForElicitation(new ElicitRequestParams
        {
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = { [property] = new ElicitRequestParams.StringSchema() },
                Required = [property]
            }
        });

    private static InputRequest ElicitBoolean(string message, string property) =>
        InputRequest.ForElicitation(new ElicitRequestParams
        {
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = { [property] = new ElicitRequestParams.BooleanSchema() },
                Required = [property]
            }
        });

    private static InputRequest SamplingRequest(string prompt) =>
        InputRequest.ForSampling(new CreateMessageRequestParams
        {
            Messages =
            [
                new SamplingMessage
                {
                    Role = Role.User,
                    Content = [new TextContentBlock { Text = prompt }]
                }
            ],
            MaxTokens = 100
        });

    private static CallToolResult TextResult(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }]
    };

    private static string? TryReadString(IDictionary<string, JsonElement>? content, string key) =>
        content is not null && content.TryGetValue(key, out var element)
            ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
            : null;

    private static string SignRequestState()
    {
        var nonce = Guid.NewGuid().ToString("N");
        return $"{nonce}.{ComputeDigest(nonce)}";
    }

    private static bool VerifyRequestState(string state)
    {
        var separator = state.LastIndexOf('.');
        if (separator <= 0 || separator == state.Length - 1) return false;
        var nonce = state[..separator];
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(state[(separator + 1)..]),
            Encoding.UTF8.GetBytes(ComputeDigest(nonce)));
    }

    // This checksum is a cross-process conformance fixture, not an authentication boundary.
    private static string ComputeDigest(string nonce) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"mrtr-conformance-state-v1:{nonce}")));
}
