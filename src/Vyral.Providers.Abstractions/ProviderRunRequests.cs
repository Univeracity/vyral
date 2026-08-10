using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vyral.Providers.Abstractions;

/// <summary>
/// Typed factory helpers for ProviderRunRequest. Each method names the
/// capability, serializes the typed payload, and applies sensible defaults.
/// The underlying ProviderRunRequest is open for extension; these cover the
/// standard AiCapabilityModels cases.
/// </summary>
public static class ProviderRunRequests
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static ProviderRunRequest ForChat(
        AiChatRequest chat,
        string? provider = null,
        string? modelId = null,
        string mode = ProviderModes.Advisory,
        int? timeoutSeconds = null)
        => Build(ProviderCapabilityIds.AiChat, chat, provider, modelId, mode, timeoutSeconds);

    public static ProviderRunRequest ForExtract(
        AiExtractRequest extract,
        string? provider = null,
        string? modelId = null,
        string mode = ProviderModes.Advisory,
        int? timeoutSeconds = null)
        => Build(ProviderCapabilityIds.AiExtract, extract, provider, modelId, mode, timeoutSeconds);

    public static ProviderRunRequest ForRerank(
        AiRerankRequest rerank,
        string? provider = null,
        string? modelId = null,
        string mode = ProviderModes.Advisory,
        int? timeoutSeconds = null)
        => Build(ProviderCapabilityIds.AiRerank, rerank, provider, modelId, mode, timeoutSeconds);

    public static ProviderRunRequest ForReview(
        AiReviewRequest review,
        string? provider = null,
        string? modelId = null,
        string mode = ProviderModes.Advisory,
        int? timeoutSeconds = null)
        => Build(ProviderCapabilityIds.AiReview, review, provider, modelId, mode, timeoutSeconds);

    public static ProviderRunRequest ForScaffold(
        AiScaffoldRequest scaffold,
        string? provider = null,
        string? modelId = null,
        string mode = ProviderModes.Advisory,
        int? timeoutSeconds = null)
        => Build(ProviderCapabilityIds.AiScaffold, scaffold, provider, modelId, mode, timeoutSeconds);

    public static ProviderRunRequest ForToolPlan(
        AiToolPlanRequest toolPlan,
        string? provider = null,
        string? modelId = null,
        string mode = ProviderModes.Advisory,
        int? timeoutSeconds = null)
        => Build(ProviderCapabilityIds.AiToolPlan, toolPlan, provider, modelId, mode, timeoutSeconds);

    /// <summary>
    /// Creates an explicitly write-capable workspace coding-agent request. This is
    /// intentionally separate from advisory AI scaffold and tool-plan runs.
    /// </summary>
    public static ProviderRunRequest ForWorkspaceCodingAgent(
        WorkspaceCodingAgentRequest codingAgent,
        string? provider = null,
        string? modelId = null,
        string mode = ProviderModes.Autonomous,
        int? timeoutSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(codingAgent);
        WorkspaceCodingAgentContract.ValidateRequest(codingAgent);
        if (!string.Equals(mode, ProviderModes.Autonomous, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Workspace coding-agent runs require autonomous mode.", nameof(mode));
        }

        return Build(ProviderCapabilityIds.AgentWorkspace, codingAgent, provider, modelId, mode, timeoutSeconds);
    }

    private static ProviderRunRequest Build<T>(
        string capability,
        T payload,
        string? provider,
        string? modelId,
        string mode,
        int? timeoutSeconds)
    {
        var json = JsonSerializer.SerializeToNode(payload, _options);
        return new ProviderRunRequest
        {
            Provider = provider,
            Capability = capability,
            ModelId = modelId,
            Mode = mode,
            Payload = json?.AsObject() ?? new JsonObject(),
            TimeoutSeconds = timeoutSeconds
        };
    }
}
