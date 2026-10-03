using System.Text;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;

namespace Vyral.Tests.Providers;

public class LocalLogprobJudgeProviderTests
{
    private static ProviderRunRequest Request(string type = AiJudgeQuestionTypes.Choice, int questions = 1) =>
        ProviderRunRequests.ForJudge(new() { Context = "Generic passage.", Questions = Enumerable.Range(0, questions).Select(i => new AiJudgeQuestion {
            Id = $"q{i}", Type = type, Prompt = "Which option fits?",
            Options = new() { new() { Id = "a", Label = "correct" }, new() { Id = "b", Label = "other" }, new() { Id = "c", Label = "third" } }
        }).ToList() });

    [Fact]
    public async Task RotationsMapBackAndExposeRawMassAgreementAndUncalibratedScores()
    {
        var factory = new FakeFactory();
        var provider = new LocalLogprobJudgeProviderTarget(factory);
        var result = await provider.RunAsync(Request(questions: 2));
        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var answers = ProviderRunResults.GetJudge(result).Answers;
        Assert.Equal(2, answers.Count);
        foreach (var answer in answers)
        {
            Assert.Equal("a", answer.Choice); Assert.False(answer.Calibrated);
            Assert.Equal(1, answer.RotationAgreement); Assert.Equal(0.3, answer.LabelMassCoverage!.Value, 10);
            Assert.Equal(0.2, answer.RawLabelProbabilities!["a"], 10);
            Assert.Equal(2.0 / 3, answer.Probabilities!["a"], 10);
            Assert.Equal(1, answer.Probabilities.Values.Sum(), 10);
        }
        Assert.Equal(1, factory.Opens); Assert.Equal(1, factory.Session.Disposals);
        Assert.Equal(6, factory.Session.Prepared); Assert.Equal(6, factory.Session.Scored);
        Assert.True(factory.Session.FirstScoreSawCompletePreparation);
    }

    [Theory]
    [InlineData("missing")] [InlineData("bytes")] [InlineData("duplicate")]
    [InlineData("nan")] [InlineData("positive")] [InlineData("mass")]
    [InlineData("basis")] [InlineData("positions")]
    public async Task InvalidProbabilityEvidenceFailsWithoutPartialAnswersAndDisposes(string fault)
    {
        var factory = new FakeFactory(); factory.Session.Fault = fault;
        var result = await new LocalLogprobJudgeProviderTarget(factory).RunAsync(Request());
        Assert.NotEqual(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(ProviderFailureClasses.Schema, result.FailureClass);
        Assert.Empty(result.Output); Assert.Equal(1, factory.Session.Disposals);
    }

    [Theory]
    [InlineData(AiJudgeQuestionTypes.Noul)] [InlineData(AiJudgeQuestionTypes.Score)]
    public async Task UnsupportedTypesAreRejectedBeforeAcquisition(string type)
    {
        var factory = new FakeFactory();
        var result = await new LocalLogprobJudgeProviderTarget(factory).RunAsync(Request(type));
        Assert.Equal(ProviderRunStatus.Unsupported, result.Status); Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public async Task EntireBatchIsPreflightedBeforeInferenceAndContextOverflowDoesNotTruncate()
    {
        var factory = new FakeFactory(); factory.Session.OverflowAt = 3;
        var result = await new LocalLogprobJudgeProviderTarget(factory).RunAsync(Request());
        Assert.Equal("context_limit_exceeded", result.ProviderStatus);
        Assert.Equal(ProviderFailureClasses.Policy, result.FailureClass);
        Assert.Equal(0, factory.Session.Scored); Assert.Equal(1, factory.Session.Disposals);
    }

    [Fact]
    public async Task OutputLimitAndCallBudgetAreEnforced()
    {
        var factory = new FakeFactory(); var request = Request(); request.MaxOutputBytes = 1;
        var result = await new LocalLogprobJudgeProviderTarget(factory).RunAsync(request);
        Assert.Equal("output_limit", result.ProviderStatus); Assert.Empty(result.Output);
        factory = new();
        result = await new LocalLogprobJudgeProviderTarget(factory, new() { MaxScoringCalls = 2 }).RunAsync(Request());
        Assert.Equal("scoring_call_limit", result.ProviderStatus); Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public async Task CancellationBeforeAcquisitionHasNoEffects()
    {
        var factory = new FakeFactory(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = await new LocalLogprobJudgeProviderTarget(factory).RunAsync(Request(), cancellation.Token);
        Assert.Equal(ProviderRunStatus.Cancelled, result.Status); Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public async Task CancellationDuringScoringDisposesAndNeverReturnsEarlierAnswers()
    {
        var factory = new FakeFactory(); using var cancellation = new CancellationTokenSource();
        factory.Session.Cancel = cancellation;
        var result = await new LocalLogprobJudgeProviderTarget(factory).RunAsync(Request(questions: 2), cancellation.Token);
        Assert.Equal(ProviderRunStatus.Cancelled, result.Status); Assert.Empty(result.Output);
        Assert.Equal(1, factory.Session.Disposals);
    }

    [Fact]
    public async Task RotationDisagreementIsAvailableSeparatelyFromConfidence()
    {
        var factory = new FakeFactory(); factory.Session.Fault = "disagreement";
        var answer = ProviderRunResults.GetJudge(await new LocalLogprobJudgeProviderTarget(factory).RunAsync(Request())).Answers.Single();
        Assert.Equal("a", answer.Choice); Assert.Equal(2.0 / 3, answer.RotationAgreement!.Value, 10);
    }

    [Fact]
    public async Task TokenCeilingCannotBeSilentlyIgnoredByDeterministicProvider()
    {
        var result = await new DeterministicAiProviderTarget().RunAsync(ProviderRunRequests.ForChat(
            new() { Messages = new() { new() { Role = AiRoles.User, Content = "Generic prompt" } }, MaxOutputTokens = 1 }));
        Assert.Equal(ProviderRunStatus.Unsupported, result.Status);
        Assert.Equal("token_ceiling_unsupported", result.ProviderStatus);
    }

    private sealed class FakeFactory : ILocalLogprobSessionFactory
    {
        public int Opens; public FakeSession Session { get; } = new();
        public string BindingId => "fixture-v1"; public string ModelId => "fixture"; public bool RequiresNetwork => false;
        public ValueTask<ILocalLogprobSession> OpenAsync(CancellationToken ct) { Opens++; return ValueTask.FromResult<ILocalLogprobSession>(Session); }
    }

    private sealed class FakeSession : ILocalLogprobSession
    {
        public int ContextTokens => 100;
        public int Prepared, Scored, Disposals, OverflowAt;
        public bool FirstScoreSawCompletePreparation;
        public string? Fault;
        public CancellationTokenSource? Cancel;
        private readonly Dictionary<int, int> _winner = new();
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        public ValueTask<LocalDecisionToken> ResolveLabelAsync(string label, CancellationToken ct) =>
            ValueTask.FromResult(new LocalDecisionToken(label[0], Encoding.UTF8.GetBytes(label)));
        public ValueTask<LocalScoringPrompt> PrepareAsync(string prompt, CancellationToken ct)
        {
            Prepared++; _winner[Prepared] = Enumerable.Range(0, 3).Single(i => prompt.Contains($"{(char)('A' + i)}: correct"));
            return ValueTask.FromResult(new LocalScoringPrompt(OverflowAt == Prepared ? new int[100] : new[] { Prepared }));
        }
        public ValueTask<LocalDecisionScore> ScoreAsync(LocalScoringPrompt prompt, IReadOnlyList<LocalDecisionToken> labels, CancellationToken ct)
        {
            Scored++; if (Scored == 1) FirstScoreSawCompletePreparation = Prepared >= 3;
            if (Cancel is not null) { Cancel.Cancel(); ct.ThrowIfCancellationRequested(); }
            var winner = _winner[prompt.TokenIds[0]];
            if (Fault == "disagreement" && Scored == 3) winner = (winner + 1) % 3;
            var probabilities = labels.Select((label, i) => new LocalLabelLogprob(label.Id, label.Bytes, Math.Log(i == winner ? 0.2 : 0.05))).ToList();
            if (Fault == "missing") probabilities.RemoveAt(0);
            if (Fault == "duplicate") probabilities[1] = probabilities[0];
            if (Fault == "bytes") probabilities[0] = probabilities[0] with { Bytes = new byte[] { 0 } };
            if (Fault == "nan") probabilities[0] = probabilities[0] with { LogProbability = double.NaN };
            if (Fault == "positive") probabilities[0] = probabilities[0] with { LogProbability = 1 };
            if (Fault == "mass") probabilities = probabilities.Select(p => p with { LogProbability = Math.Log(0.5) }).ToList();
            return ValueTask.FromResult(new LocalDecisionScore(probabilities, Fault == "positions" ? 2 : 1, Fault != "basis"));
        }
    }
}
