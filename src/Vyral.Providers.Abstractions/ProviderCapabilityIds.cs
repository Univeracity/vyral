namespace Vyral.Providers.Abstractions;

public static class ProviderCapabilityIds
{
    public const string AiEmbedding = "ai.embedding";
    public const string AiChat = "ai.chat";
    public const string AiExtract = "ai.extract";
    public const string AiRerank = "ai.rerank";
    public const string AiReview = "ai.review";
    public const string AiScaffold = "ai.scaffold";
    public const string AiToolPlan = "ai.toolPlan";
    public const string RetrievalSearch = "retrieval.search";
    public const string RetrievalIndex = "retrieval.index";
    public const string StorageObject = "storage.object";
    public const string ComputeJob = "compute.job";
    public const string AgentJob = "agent.job";
    /// <summary>
    /// A host-enforced coding-agent run scoped to one checked-out workspace. Unlike
    /// <see cref="AgentJob"/>, this capability requires an explicit write policy,
    /// host-controlled validation execution, and a reconciled change set.
    /// </summary>
    public const string AgentWorkspace = "agent.workspace";
}
