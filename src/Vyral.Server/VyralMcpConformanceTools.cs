using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

#pragma warning disable MCPEXP001

namespace Vyral.Server;

/// <summary>
/// Official-runner diagnostic fixtures. Program registration is restricted to explicit
/// Development-only conformance mode so these names can never enlarge the product surface.
/// </summary>
[McpServerToolType]
public sealed class VyralMcpConformanceTools
{
    [McpServerTool(Name = "greet")]
    [Description("Returns a synchronous greeting for task conformance.")]
    public static string Greet(string name) => $"Hello, {name}!";

    [McpServerTool(Name = "slow_compute")]
    [Description("Completes after a bounded delay for optional task conformance.")]
    public static async Task<string> SlowCompute(
        int seconds,
        string? label,
        CancellationToken cancellationToken)
    {
        if (seconds is < 0 or > 10) throw new McpException("seconds must be between 0 and 10.");
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        return $"Computed {label ?? "result"}";
    }

    [McpServerTool(Name = "failing_job")]
    [Description("Produces a tool-level failure inside a required task.")]
    public static async Task<string> FailingJob(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        throw new InvalidOperationException("The conformance task failed.");
    }

    [McpServerTool(Name = "protocol_error_job")]
    [Description("Produces a protocol-level task failure.")]
    public static string ProtocolErrorJob() =>
        throw new McpProtocolException("The conformance task encountered a protocol error.", McpErrorCode.InternalError);

    [McpServerTool(Name = "confirm_delete")]
    [Description("Waits for elicitation before confirming a diagnostic action.")]
    public static async Task<string> ConfirmDelete(
        McpServer server,
        string filename,
        CancellationToken cancellationToken)
    {
        var result = await server.ElicitAsync(CreateConfirmationRequest($"Delete {filename}?"), cancellationToken);
        return result.Action == "accept" ? $"Deleted {filename}" : $"Did not delete {filename}";
    }

    [McpServerTool(Name = "multi_input")]
    [Description("Waits for two independent elicitation responses.")]
    public static async Task<string> MultiInput(McpServer server, CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            server.ElicitAsync(CreateConfirmationRequest("Confirm the first operation."), cancellationToken).AsTask(),
            server.ElicitAsync(CreateConfirmationRequest("Confirm the second operation."), cancellationToken).AsTask());
        return "Both inputs received.";
    }

    [McpServerTool(Name = "test_tool_with_task")]
    [Description("Collects input synchronously, then completes through a task.")]
    public static string ToolWithTask(RequestContext<CallToolRequestParams> context)
    {
        if (context.Params!.InputResponses is { } responses &&
            responses.TryGetValue("user_name", out var response))
        {
            var elicitation = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
            var name = elicitation?.Content?["name"].GetString() ?? "world";
            return $"Hello, {name}!";
        }

        throw new InputRequiredException(new Dictionary<string, InputRequest>
        {
            ["user_name"] = InputRequest.ForElicitation(new ElicitRequestParams
            {
                Message = "What is your name?",
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties =
                    {
                        ["name"] = new ElicitRequestParams.StringSchema()
                    },
                    Required = ["name"]
                }
            })
        });
    }

    [McpServerTool(Name = "test_missing_capability")]
    [Description("Requires the sampling capability for SEP-2575 conformance diagnostics.")]
#pragma warning disable MCP9005
    public static string MissingCapability(RequestContext<CallToolRequestParams> context)
    {
        if (context.Server.ClientCapabilities?.Sampling is not null)
            return "Client declared the sampling capability; tool executed.";

        throw new MissingRequiredClientCapabilityException(
            new ClientCapabilities { Sampling = new SamplingCapability() },
            "sampling capability required but not declared by client");
    }
#pragma warning restore MCP9005

    [McpServerTool(Name = "test_streaming_elicitation")]
    [Description("Returns only result frames for SEP-2575 stream-discipline diagnostics.")]
    public static string StreamingElicitation() =>
        "stream observed: result frames only, no top-level requests";

    [McpServerTool(Name = "test_logging_tool")]
    [Description("Attempts a client log for SEP-2575 per-request log-level diagnostics.")]
    public static string LoggingTool(RequestContext<CallToolRequestParams> context)
    {
#pragma warning disable MCP9004, MCP9005
        var logger = context.Server.AsClientLoggerProvider().CreateLogger(nameof(VyralMcpConformanceTools));
#pragma warning restore MCP9004, MCP9005
        logger.LogInformation("test_logging_tool executed");
        return "Log attempted; framework gates on per-request logLevel metadata.";
    }

    [McpServerTool(Name = "test_header_tool")]
    [Description("Exercises x-mcp-header validation for the official SEP-2243 conformance scenario.")]
    public static string HeaderTool(
        [McpHeader("Region"), Description("A non-secret routing region.")] string region,
        [Description("Opaque diagnostic query text.")] string query) =>
        $"Executed in region {region}: {query}";

    private static ElicitRequestParams CreateConfirmationRequest(string message) =>
        new()
        {
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties =
                {
                    ["confirm"] = new ElicitRequestParams.BooleanSchema()
                },
                Required = ["confirm"]
            }
        };
}

/// <summary>
/// A prompt exists only because the official SEP-2549 scenario exercises every cacheable
/// list method. Vyral's product MCP surface intentionally advertises no prompts.
/// </summary>
[McpServerPromptType]
public sealed class VyralMcpConformancePrompts
{
    [McpServerPrompt(Name = "test_simple_prompt")]
    [Description("Minimal prompt used only by the official caching conformance scenario.")]
    public static string SimplePrompt() => "Vyral MCP conformance prompt.";
}
