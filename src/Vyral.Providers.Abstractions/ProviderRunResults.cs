using System.Text.Json;

namespace Vyral.Providers.Abstractions;

/// <summary>
/// Typed extraction helpers for ProviderRunResult.Output — the exit-side
/// counterpart to ProviderRunRequests. Each method deserializes Output
/// using the shared ProviderJson.Options (camelCase, web defaults).
/// Returns a new empty instance if Output does not match the expected shape.
/// </summary>
public static class ProviderRunResults
{
    public static AiChatResult GetChat(ProviderRunResult result)
        => Deserialize<AiChatResult>(result);

    public static AiExtractResult GetExtract(ProviderRunResult result)
        => Deserialize<AiExtractResult>(result);

    public static AiRerankResult GetRerank(ProviderRunResult result)
        => Deserialize<AiRerankResult>(result);

    public static AiReviewResult GetReview(ProviderRunResult result)
        => Deserialize<AiReviewResult>(result);

    public static AiScaffoldResult GetScaffold(ProviderRunResult result)
        => Deserialize<AiScaffoldResult>(result);

    public static AiToolPlanResult GetToolPlan(ProviderRunResult result)
        => Deserialize<AiToolPlanResult>(result);

    public static WorkspaceCodingAgentResult GetWorkspaceCodingAgent(ProviderRunResult result)
        => Deserialize<WorkspaceCodingAgentResult>(result);

    private static T Deserialize<T>(ProviderRunResult result) where T : new()
    {
        if (result.Status != ProviderRunStatus.Succeeded)
        {
            var msg = $"Cannot read output from a '{result.Status}' provider run";
            if (result.FailureClass is not null) msg += $" (failure: {result.FailureClass})";
            if (result.Error is not null) msg += $": {result.Error}";
            throw new InvalidOperationException(msg);
        }

        return JsonSerializer.Deserialize<T>(result.Output, ProviderJson.Options) ?? new T();
    }
}
