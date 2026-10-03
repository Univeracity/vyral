using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Local;

/// <summary>Explicit binding to an operator-managed, single-slot local llama.cpp process.</summary>
public sealed class LlamaCppRuntimeOptions
{
    public Uri Endpoint { get; init; } = new("http://127.0.0.1:8080/");
    public string ModelId { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public string ModelSha256 { get; init; } = "";
    public string BuildInfo { get; init; } = "";
    public string TemplateSha256 { get; init; } = "";
    public string TemplateProbeSha256 { get; init; } = "";
    public int ContextTokens { get; init; } = 2048;
    public int MaxOutputTokens { get; init; } = 512;
    public int MaxResponseBytes { get; init; } = 1048576;

    internal void Validate()
    {
        if (Endpoint.Scheme != "http" || !IPAddress.TryParse(Endpoint.Host, out var ip) || !IPAddress.IsLoopback(ip)
            || Endpoint.AbsolutePath != "/" || Endpoint.Query != "" || Endpoint.UserInfo != "" || Endpoint.Fragment != "")
            throw new ArgumentException("Runtime endpoint must be a literal loopback HTTP origin.");
        if (string.IsNullOrWhiteSpace(ModelId) || !Path.IsPathFullyQualified(ModelPath) || string.IsNullOrWhiteSpace(BuildInfo)
            || !Regex.IsMatch(ModelSha256, "^[a-f0-9]{64}$") || !Regex.IsMatch(TemplateSha256, "^[a-f0-9]{64}$")
            || !Regex.IsMatch(TemplateProbeSha256, "^[a-f0-9]{64}$")
            || ContextTokens < 2 || MaxOutputTokens < 1 || MaxOutputTokens >= ContextTokens || MaxResponseBytes is < 256 or > 4194304)
            throw new ArgumentException("Pin model bytes, runtime build, template, context window and bounded output.");
    }
}

public sealed class LlamaCppRuntimeSessionFactory : ILocalLogprobSessionFactory
{
    private readonly LlamaCppRuntimeOptions _options;
    public LlamaCppRuntimeSessionFactory(LlamaCppRuntimeOptions options)
    {
        options.Validate(); _options = options;
        BindingId = ProviderHash.Sha256($"llamacpp-native-v1|{options.Endpoint}|{options.ModelId}|{options.ModelSha256}|{options.BuildInfo}|{options.TemplateSha256}|{options.TemplateProbeSha256}|{options.ContextTokens}|{options.MaxOutputTokens}|{options.MaxResponseBytes}");
    }
    public string BindingId { get; }
    public string ModelId => _options.ModelId;
    public string Auth => "none";
    public bool RequiresNetwork => true; // Loopback HTTP is still network transport.
    public async ValueTask<ILocalLogprobSession> OpenAsync(CancellationToken ct)
    {
        var session = new LlamaCppRuntimeSession(_options);
        try { await session.ValidateBindingAsync(ct); return session; }
        catch { await session.DisposeAsync(); throw; }
    }
}

internal sealed class LlamaCppRuntimeSession : ILocalLogprobSession
{
    private readonly LlamaCppRuntimeOptions _options;
    private readonly HttpClient _client;
    public LlamaCppRuntimeSession(LlamaCppRuntimeOptions options)
    {
        _options = options;
        _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = options.Endpoint, Timeout = Timeout.InfiniteTimeSpan };
    }
    public int ContextTokens => _options.ContextTokens;
    public ValueTask DisposeAsync() { _client.Dispose(); return ValueTask.CompletedTask; }

    internal async Task ValidateBindingAsync(CancellationToken ct)
    {
        // These are consistency checks within the trusted operator-owned local process boundary,
        // not cryptographic proof that an arbitrary HTTP peer loaded these bytes.
        await using var file = File.OpenRead(_options.ModelPath);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (digest != _options.ModelSha256) throw new LocalScoringException("model_digest_mismatch", ProviderFailureClasses.Configuration);
        var props = await SendAsync(HttpMethod.Get, "props", null, ct);
        if (props["model_path"] is not JsonValue path || !path.TryGetValue<string>(out var reportedPath)
            || !Path.IsPathFullyQualified(reportedPath) || Path.GetFullPath(reportedPath) != Path.GetFullPath(_options.ModelPath)
            || props["build_info"]?.GetValue<string>() != _options.BuildInfo
            || props["chat_template"] is not JsonValue template || !template.TryGetValue<string>(out var text)
            || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))) != _options.TemplateSha256
            || props["default_generation_settings"]?["n_ctx"]?.GetValue<int>() != ContextTokens
            || props["total_slots"]?.GetValue<int>() != 1)
            throw new LocalScoringException("runtime_binding_mismatch", ProviderFailureClasses.Configuration);
        var probe = await SendAsync(HttpMethod.Post, "apply-template", new() {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "vyral-template-binding-v1" }) }, ct);
        if (probe["prompt"] is not JsonValue rendered || !rendered.TryGetValue<string>(out var probeText)
            || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(probeText))) != _options.TemplateProbeSha256)
            throw new LocalScoringException("template_probe_mismatch", ProviderFailureClasses.Configuration);
    }

    internal async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null) request.Content = JsonContent.Create(payload, options: ProviderJson.Options);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new LocalScoringException(response.StatusCode switch {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "runtime_auth_failed",
                HttpStatusCode.TooManyRequests => "runtime_rate_limited",
                _ => "runtime_unavailable" }, response.StatusCode switch {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderFailureClasses.Auth,
                HttpStatusCode.TooManyRequests => ProviderFailureClasses.RateLimit,
                _ => ProviderFailureClasses.ProviderUnavailable });
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > _options.MaxResponseBytes)
                throw new LocalScoringException("runtime_response_limit", ProviderFailureClasses.Policy);
            output.Write(buffer, 0, read);
        }
        return JsonNode.Parse(output.ToArray()) as JsonObject
            ?? throw new LocalScoringException("invalid_runtime_response", ProviderFailureClasses.Schema);
    }

    private async Task<int[]> TokenizeAsync(string content, bool special, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Post, "tokenize",
            new() { ["content"] = content, ["add_special"] = special, ["parse_special"] = special }, ct);
        if (response["tokens"] is not JsonArray tokens || tokens.Count == 0
            || tokens.Any(t => t is not JsonValue value || !value.TryGetValue<int>(out var id) || id < 0))
            throw new LocalScoringException("invalid_runtime_tokens", ProviderFailureClasses.Schema);
        return tokens.Select(t => t!.GetValue<int>()).ToArray();
    }

    public async ValueTask<LocalDecisionToken> ResolveLabelAsync(string label, CancellationToken ct)
    {
        var tokens = await TokenizeAsync(label, false, ct);
        if (tokens.Length != 1) throw new LocalScoringException("label_not_single_token", ProviderFailureClasses.Unsupported);
        var decoded = await SendAsync(HttpMethod.Post, "detokenize", new() { ["tokens"] = new JsonArray(JsonValue.Create(tokens[0])) }, ct);
        if (decoded["content"]?.GetValue<string>() != label)
            throw new LocalScoringException("label_bytes_mismatch", ProviderFailureClasses.Schema);
        return new(tokens[0], Encoding.UTF8.GetBytes(label));
    }

    public async ValueTask<LocalScoringPrompt> PrepareAsync(string prompt, CancellationToken ct)
        => await PrepareChatAsync(new() { new() { Role = AiRoles.User, Content = prompt } }, ct);

    internal async Task<LocalScoringPrompt> PrepareChatAsync(List<AiMessage> messages, CancellationToken ct)
    {
        var templated = await SendAsync(HttpMethod.Post, "apply-template",
            new() { ["messages"] = ProviderJson.ToJsonObject(new AiChatRequest { Messages = messages })["messages"]!.DeepClone() }, ct);
        if (templated["prompt"] is not JsonValue value || !value.TryGetValue<string>(out var prompt) || string.IsNullOrEmpty(prompt))
            throw new LocalScoringException("invalid_template_response", ProviderFailureClasses.Schema);
        return new(await TokenizeAsync(prompt, true, ct));
    }

    internal Task<JsonObject> CompleteAsync(LocalScoringPrompt prompt, int maxTokens, bool score, CancellationToken ct)
    {
        if (maxTokens < 1 || maxTokens > _options.MaxOutputTokens || prompt.TokenIds.Length > ContextTokens - maxTokens)
            throw new LocalScoringException("context_limit_exceeded", ProviderFailureClasses.Policy);
        return SendAsync(HttpMethod.Post, "completion", new() {
            ["prompt"] = new JsonArray(prompt.TokenIds.Select(id => JsonValue.Create(id)).ToArray()),
            ["n_predict"] = maxTokens, ["cache_prompt"] = false, ["stream"] = false,
            ["temperature"] = -1, ["seed"] = 0,
            ["n_probs"] = score ? 256 : 0, ["post_sampling_probs"] = false,
            ["samplers"] = new JsonArray(), ["return_tokens"] = true }, ct);
    }

    public async ValueTask<LocalDecisionScore> ScoreAsync(LocalScoringPrompt prompt,
        IReadOnlyList<LocalDecisionToken> labels, CancellationToken ct)
    {
        var response = await CompleteAsync(prompt, 1, true, ct);
        if (response["probs"] is not null && response["completion_probabilities"] is not null)
            throw new LocalScoringException("ambiguous_scoring_response", ProviderFailureClasses.Schema);
        var scoredPositions = response["probs"] ?? response["completion_probabilities"];
        if (response["truncated"]?.GetValue<bool>() != false || response["tokens_predicted"]?.GetValue<int>() != 1
            || scoredPositions is not JsonArray positions || positions.Count != 1
            || positions[0]?["top_logprobs"] is not JsonArray probabilities)
            throw new LocalScoringException("invalid_scoring_response", ProviderFailureClasses.Schema);
        var entries = new List<LocalLabelLogprob>(); var seen = new HashSet<int>();
        foreach (var probability in probabilities)
        {
            if (probability is not JsonObject p || p["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var id) || id < 0
                || !seen.Add(id) || p["logprob"] is not JsonValue logValue || !logValue.TryGetValue<double>(out var logprob)
                || !double.IsFinite(logprob) || logprob > 0 || p["bytes"] is not JsonArray bytes
                || bytes.Any(b => b is not JsonValue v || !v.TryGetValue<int>(out var n) || n is < 0 or > 255))
                throw new LocalScoringException("invalid_probability_entry", ProviderFailureClasses.Schema);
            if (labels.Any(label => label.Id == id))
                entries.Add(new(id, bytes.Select(b => (byte)b!.GetValue<int>()).ToArray(), logprob));
        }
        return new(entries);
    }
}
