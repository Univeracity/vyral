using Vyral.Providers.Abstractions;
using Vyral.Providers.Jev;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Providers;

public class JevJudgeConformanceTests : AiJudgeProviderConformanceTests
{
    protected override IProviderTarget CreateProvider()
    {
        var apiKey = Environment.GetEnvironmentVariable("JEV_API_KEY") ?? Environment.GetEnvironmentVariable("TYPESAFE_API_TOKEN_KEY");
        return new JevProviderTarget(new JevProviderOptions { ApiKey = apiKey, ModelId = "jev-1.13.0" });
    }

    [JevLiveFact]
    public Task AiJudge_ChoiceProbabilitiesSumToOneAndChoiceIsTheArgmax() =>
        RunAiJudge_ChoiceProbabilitiesSumToOneAndChoiceIsTheArgmax();

    [JevLiveFact]
    public Task AiJudge_NoulProbabilityAndDerivedConfidenceAreInUnitRange() =>
        RunAiJudge_NoulProbabilityAndDerivedConfidenceAreInUnitRange();

    [JevLiveFact]
    public Task AiJudge_ScoreIsWithinLevelRangeAndLegendCoversEveryLevel() =>
        RunAiJudge_ScoreIsWithinLevelRangeAndLegendCoversEveryLevel();

    [JevLiveFact]
    public Task AiJudge_BatchesMixedQuestionTypesInOneRequest() =>
        RunAiJudge_BatchesMixedQuestionTypesInOneRequest();

    [JevLiveFact]
    public Task AiJudge_TwoChoiceQuestionsWithSwappedOptionLabelsAreNotIdentical() =>
        RunAiJudge_TwoChoiceQuestionsWithSwappedOptionLabelsAreNotIdentical();

    [JevLiveFact]
    public Task AiJudge_EveryAnswerReportsTheCalibratedFlag() =>
        RunAiJudge_EveryAnswerReportsTheCalibratedFlag();

    // These three are structural-validation checks (ValidateQuestions in JevProviderTarget) that run
    // before any HTTP call — no live key is actually spent on them — but they still require the
    // JevProviderTarget to be constructed with a key configured (auth.api_key is checked first), so
    // they stay under the same live gate as the rest of this conformance suite.

    [JevLiveFact]
    public Task AiJudge_RejectsChoiceQuestionWithNoOptionsBeforeAnyProviderWork() =>
        RunAiJudge_RejectsChoiceQuestionWithNoOptionsBeforeAnyProviderWork();

    [JevLiveFact]
    public Task AiJudge_RejectsScoreQuestionWithFewerThanTwoLevelsBeforeAnyProviderWork() =>
        RunAiJudge_RejectsScoreQuestionWithFewerThanTwoLevelsBeforeAnyProviderWork();

    [JevLiveFact]
    public Task AiJudge_RejectsUnknownQuestionTypeBeforeAnyProviderWork() =>
        RunAiJudge_RejectsUnknownQuestionTypeBeforeAnyProviderWork();
}
