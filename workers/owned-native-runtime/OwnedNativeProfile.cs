using Vyral.Providers.Local;

// Shared closed transport DTO. JsonSerializerOptions in the controller refuse
// unknown fields/types; option validation rejects explicit null/unknown modes.
sealed record NativeProfile
{
    // Finite allocation sanity limit, matching the worker reservation contract.
    // A configured capacity is not qualification of a model or native runtime.
    public const int MaximumContextTokens = 1048576;
    public required string Endpoint { get; init; } public required string ModelId { get; init; }
    public required string ModelPath { get; init; } public required string ModelSha256 { get; init; }
    public required string BuildInfo { get; init; } public required string TemplateSha256 { get; init; }
    public required string TemplateProbeSha256 { get; init; } public required int ContextTokens { get; init; }
    public required int MaxOutputTokens { get; init; } public required int MaxResponseBytes { get; init; }
    public string ChatOutputFormat { get; init; } = LlamaCppChatOutputFormats.Text;
    // Explicit owner selection; omission preserves the runtime's existing template default.
    // The exact profile digest and observed template-probe binding cover this setting.
    public bool? EnableThinking { get; init; }
    public string? ListenEndpoint { get; init; }
    public int? HttpThreads { get; init; }

    public LlamaCppRuntimeOptions ToOptions()
    {
        if (ContextTokens is < 2 or > MaximumContextTokens)
            throw new ArgumentException("native_context_limit");
        return new() {
        Endpoint = new Uri(Endpoint), ModelId = ModelId, ModelPath = ModelPath,
        ModelSha256 = ModelSha256, BuildInfo = BuildInfo, TemplateSha256 = TemplateSha256,
        TemplateProbeSha256 = TemplateProbeSha256, ContextTokens = ContextTokens,
        MaxOutputTokens = MaxOutputTokens, MaxResponseBytes = MaxResponseBytes, ChatOutputFormat = ChatOutputFormat };
    }
}
