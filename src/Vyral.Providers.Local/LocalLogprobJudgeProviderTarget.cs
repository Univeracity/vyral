using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Local;

/// <summary>Portable Choice scoring mechanics over an explicitly supplied local runtime.</summary>
public sealed class LocalLogprobJudgeProviderTarget : IProviderTarget
{
    private readonly ILocalLogprobSessionFactory _factory;
    private readonly LocalLogprobJudgeOptions _options;
    public LocalLogprobJudgeProviderTarget(ILocalLogprobSessionFactory factory, LocalLogprobJudgeOptions? options = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? new();
        if (string.IsNullOrWhiteSpace(factory.BindingId) || string.IsNullOrWhiteSpace(factory.ModelId)
            || _options.Rotations < 1 || _options.MaxQuestions < 1 || _options.MaxOptions is < 2 or > 26
            || _options.MaxScoringCalls < 1 || _options.MaxInputBytes < 1 || _options.MaxOutputBytes < 1
            || _options.TimeoutSeconds < 1)
            throw new ArgumentException("A pinned runtime binding and positive bounded judge limits are required.");
        Profile = new() { Id = "local-logprob-judge", DisplayName = "Local log-probability judge",
            Family = "local", Local = true, RequiresNetwork = factory.RequiresNetwork, Auth = factory.Auth,
            ConfigHash = ProviderHash.Sha256($"local-logprob-judge-v1|{factory.BindingId}|{JsonSerializer.Serialize(_options)}") };
        Capabilities = new[] { new ProviderCapabilityDescriptor { Id = ProviderCapabilityIds.AiJudge,
            Operations = new() { "run" }, ToolPolicy = ProviderToolPolicies.CallerOwned,
            InputLimits = new() { ["questionTypes"] = new[] { AiJudgeQuestionTypes.Choice },
                ["maxQuestions"] = _options.MaxQuestions, ["maxOptions"] = _options.MaxOptions,
                ["maxScoringCalls"] = _options.MaxScoringCalls },
            OutputLimits = new() { ["maxOutputBytes"] = _options.MaxOutputBytes },
            ModePolicies = new() { new() { Id = ProviderModes.Mechanics, AllowNetwork = factory.RequiresNetwork,
                AllowedOutputKinds = new() { ProviderOutputKinds.Judgment }, MaxInputBytes = _options.MaxInputBytes,
                MaxOutputBytes = _options.MaxOutputBytes, TimeoutSeconds = _options.TimeoutSeconds } },
            UnsupportedFeatures = new() { "noul", "score", "calibrated_likelihoods", "tools", "source_writes" } } };
    }

    public ProviderProfile Profile { get; }
    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }

    public async Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var trace = new ProviderTraceEvent { Provider = Profile.Id, Capability = request.Capability,
            Operation = request.Operation, Mode = request.Mode, ModelId = _factory.ModelId,
            AdapterId = "local-logprob-judge-v1", ConfigHash = Profile.ConfigHash,
            InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)) };
        ProviderRunResult Result(ProviderRunStatus status, string reason, string? failure = null, JsonObject? output = null)
        {
            trace.DurationMs = clock.Elapsed.TotalMilliseconds; trace.FailureClass = failure;
            trace.OutputHash = output is null ? null : ProviderHash.Sha256(output.ToJsonString(ProviderJson.Options));
            return new() { Provider = Profile.Id, Capability = request.Capability, Operation = request.Operation,
                Mode = request.Mode, Status = status, ProviderStatus = reason, FailureClass = failure,
                Output = output ?? new(), Trace = trace };
        }
        if (request.Capability != ProviderCapabilityIds.AiJudge || request.Operation != "run"
            || request.Mode != ProviderModes.Mechanics || request.ModelId is not null && request.ModelId != _factory.ModelId
            || request.ContextRefs.Count != 0 || request.ArtifactDirectory is not null)
            return Result(ProviderRunStatus.Unsupported, "unsupported_request", ProviderFailureClasses.Unsupported);
        if (request.TimeoutSeconds is <= 0 || request.MaxOutputBytes is <= 0)
            return Result(ProviderRunStatus.Rejected, "invalid_request_limits", ProviderFailureClasses.Policy);
        if (Encoding.UTF8.GetByteCount(request.Payload.ToJsonString(ProviderJson.Options)) > _options.MaxInputBytes)
            return Result(ProviderRunStatus.Rejected, "input_limit", ProviderFailureClasses.Policy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(request.TimeoutSeconds ?? _options.TimeoutSeconds, _options.TimeoutSeconds)));
        ILocalLogprobSession? session = null;
        ProviderRunResult result;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (request.Payload.Any(pair => pair.Key is not ("context" or "questions" or "references")))
                throw new LocalScoringException("unsupported_payload", ProviderFailureClasses.Unsupported);
            var payload = ProviderJson.DeserializePayload<AiJudgeRequest>(request);
            Validate(payload);
            var calls = payload.Questions.Sum(q => Math.Min(_options.Rotations, q.Options.Count));
            if (calls > _options.MaxScoringCalls)
                throw new LocalScoringException("scoring_call_limit", ProviderFailureClasses.Policy);
            session = await _factory.OpenAsync(deadline.Token).AsTask().WaitAsync(deadline.Token);
            if (session.ContextTokens < 2)
                throw new LocalScoringException("unknown_context_limit", ProviderFailureClasses.Unsupported);
            var labels = new List<LocalDecisionToken>();
            for (var index = 0; index < payload.Questions.Max(q => q.Options.Count); index++)
            {
                var label = ((char)('A' + index)).ToString();
                var token = await session.ResolveLabelAsync(label, deadline.Token).AsTask().WaitAsync(deadline.Token);
                if (token is null || token.Bytes is null || token.Id < 0 || !token.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(label))
                    || labels.Any(previous => previous.Id == token.Id))
                    throw new LocalScoringException("invalid_label_mapping", ProviderFailureClasses.Schema);
                labels.Add(token);
            }
            // Prepare the entire batch before its first inference, including every rotation.
            var prepared = new List<(AiJudgeQuestion Question, int Rotation, LocalScoringPrompt Prompt)>();
            foreach (var question in payload.Questions)
                for (var rotation = 0; rotation < Math.Min(_options.Rotations, question.Options.Count); rotation++)
                {
                    var prompt = await session.PrepareAsync(BuildPrompt(payload.Context, question, rotation), deadline.Token).AsTask().WaitAsync(deadline.Token);
                    if (prompt is null || prompt.TokenIds is null || prompt.TokenIds.Length == 0 || prompt.TokenIds.Any(id => id < 0))
                        throw new LocalScoringException("invalid_prompt_tokens", ProviderFailureClasses.Schema);
                    if (prompt.TokenIds.Length >= session.ContextTokens)
                        throw new LocalScoringException("context_limit_exceeded", ProviderFailureClasses.Policy);
                    prepared.Add((question, rotation, prompt));
                }
            var answers = new List<AiJudgeAnswer>();
            foreach (var question in payload.Questions)
            {
                var count = question.Options.Count;
                var normalized = new double[count]; var raw = new double[count];
                var winners = new List<int>(); var coverage = 1.0;
                foreach (var item in prepared.Where(p => ReferenceEquals(p.Question, question)))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var score = await session.ScoreAsync(item.Prompt, labels.Take(count).ToArray(), deadline.Token).AsTask().WaitAsync(deadline.Token);
                    if (score is null || score.Labels is null || !score.FullVocabulary || score.OutputPositions != 1)
                        throw new LocalScoringException("invalid_probability_basis", ProviderFailureClasses.Schema);
                    if (score.Labels.Count != count || score.Labels.Any(p => p is null) || score.Labels.Select(p => p.Id).Distinct().Count() != count)
                        throw new LocalScoringException("incomplete_label_probabilities", ProviderFailureClasses.Schema);
                    var probabilities = new double[count];
                    for (var index = 0; index < count; index++)
                    {
                        var expected = labels[index];
                        var probability = score.Labels.SingleOrDefault(p => p.Id == expected.Id);
                        if (probability is null || probability.Bytes is null || !probability.Bytes.SequenceEqual(expected.Bytes)
                            || !double.IsFinite(probability.LogProbability) || probability.LogProbability > 0)
                            throw new LocalScoringException("invalid_label_probability", ProviderFailureClasses.Schema);
                        probabilities[(index + item.Rotation) % count] = Math.Exp(probability.LogProbability);
                    }
                    var mass = probabilities.Sum();
                    if (!double.IsFinite(mass) || mass <= 0 || mass > 1.0 + 1e-6)
                        throw new LocalScoringException("invalid_label_mass", ProviderFailureClasses.Schema);
                    coverage = Math.Min(coverage, Math.Min(1.0, mass));
                    winners.Add(Winner(probabilities));
                    for (var index = 0; index < count; index++) { raw[index] += probabilities[index]; normalized[index] += probabilities[index] / mass; }
                }
                var rotations = winners.Count; var winner = Winner(normalized);
                answers.Add(new() { QuestionId = question.Id, Choice = question.Options[winner].Id,
                    Probabilities = Enumerable.Range(0, count).ToDictionary(i => question.Options[i].Id, i => normalized[i] / rotations),
                    RawLabelProbabilities = Enumerable.Range(0, count).ToDictionary(i => question.Options[i].Id, i => raw[i] / rotations),
                    LabelMassCoverage = coverage, RotationAgreement = winners.Count(i => i == winner) / (double)rotations,
                    Confidence = normalized[winner] / rotations, Calibrated = false });
            }
            deadline.Token.ThrowIfCancellationRequested();
            var output = ProviderJson.ToJsonObject(new AiJudgeResult { Answers = answers, ModelId = _factory.ModelId });
            result = Encoding.UTF8.GetByteCount(output.ToJsonString(ProviderJson.Options)) > Math.Min(request.MaxOutputBytes ?? _options.MaxOutputBytes, _options.MaxOutputBytes)
                ? Result(ProviderRunStatus.Failed, "output_limit", ProviderFailureClasses.Policy)
                : Result(ProviderRunStatus.Succeeded, "ok", output: output);
        }
        catch (LocalScoringException ex)
        {
            result = Result(ex.FailureClass == ProviderFailureClasses.Unsupported ? ProviderRunStatus.Unsupported : ProviderRunStatus.Rejected,
                ex.Reason, ex.FailureClass);
        }
        catch (OperationCanceledException)
        {
            result = Result(ct.IsCancellationRequested ? ProviderRunStatus.Cancelled : ProviderRunStatus.TimedOut,
                ct.IsCancellationRequested ? "cancelled" : "deadline_exceeded",
                ct.IsCancellationRequested ? ProviderFailureClasses.Cancelled : ProviderFailureClasses.Timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or IOException or HttpRequestException)
        {
            result = Result(ProviderRunStatus.Failed, "scoring_failed", ProviderFailureClasses.Schema);
        }
        if (session is not null)
        {
            // Session implementations must enforce their own bounded cleanup independently of caller cancellation.
            try { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { if (result.Status == ProviderRunStatus.Succeeded) result = Result(ProviderRunStatus.Failed, "cleanup_failed", ProviderFailureClasses.ProviderUnavailable); }
        }
        return result;
    }

    private void Validate(AiJudgeRequest payload)
    {
        if (payload.Questions is null || payload.Questions.Count == 0 || payload.Questions.Count > _options.MaxQuestions
            || payload.Questions.Any(q => q is null || string.IsNullOrWhiteSpace(q.Id) || string.IsNullOrWhiteSpace(q.Prompt))
            || payload.Questions.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count() != payload.Questions.Count)
            throw new LocalScoringException("invalid_questions", ProviderFailureClasses.Schema);
        if (payload.References is null || payload.References.Count != 0)
            throw new LocalScoringException("references_unsupported", ProviderFailureClasses.Unsupported);
        foreach (var question in payload.Questions)
        {
            if (question.Type != AiJudgeQuestionTypes.Choice)
                throw new LocalScoringException("unsupported_question_type", ProviderFailureClasses.Unsupported);
            if (question.Options is null || question.Options.Count is < 2 || question.Options.Count > _options.MaxOptions
                || question.Options.Any(o => o is null || string.IsNullOrWhiteSpace(o.Id) || string.IsNullOrWhiteSpace(o.Label))
                || question.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != question.Options.Count)
                throw new LocalScoringException("invalid_options", ProviderFailureClasses.Schema);
        }
    }
    private static int Winner(IReadOnlyList<double> values) => Enumerable.Range(0, values.Count).OrderByDescending(i => values[i]).ThenBy(i => i).First();
    private static string BuildPrompt(string? context, AiJudgeQuestion question, int rotation)
    {
        var text = new StringBuilder("Select the best option for the question using the supplied context. Reply with exactly one option letter.\nContext:\n");
        text.Append(context).Append("\nQuestion:\n").Append(question.Prompt).Append("\nOptions:\n");
        for (var index = 0; index < question.Options.Count; index++)
            text.Append((char)('A' + index)).Append(": ").Append(question.Options[(index + rotation) % question.Options.Count].Label).Append('\n');
        return text.ToString();
    }
}
