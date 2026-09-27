using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Providers;

public class DeterministicAiJudgeConformanceTests : AiJudgeProviderConformanceTests
{
    protected override IProviderTarget CreateProvider() => new DeterministicAiProviderTarget();

    [Fact]
    public Task AiJudge_ChoiceProbabilitiesSumToOneAndChoiceIsTheArgmax() =>
        RunAiJudge_ChoiceProbabilitiesSumToOneAndChoiceIsTheArgmax();

    [Fact]
    public Task AiJudge_NoulProbabilityAndDerivedConfidenceAreInUnitRange() =>
        RunAiJudge_NoulProbabilityAndDerivedConfidenceAreInUnitRange();

    [Fact]
    public Task AiJudge_ScoreIsWithinLevelRangeAndLegendCoversEveryLevel() =>
        RunAiJudge_ScoreIsWithinLevelRangeAndLegendCoversEveryLevel();

    [Fact]
    public Task AiJudge_BatchesMixedQuestionTypesInOneRequest() =>
        RunAiJudge_BatchesMixedQuestionTypesInOneRequest();

    [Fact]
    public Task AiJudge_TwoChoiceQuestionsWithSwappedOptionLabelsAreNotIdentical() =>
        RunAiJudge_TwoChoiceQuestionsWithSwappedOptionLabelsAreNotIdentical();

    [Fact]
    public Task AiJudge_EveryAnswerReportsTheCalibratedFlag() =>
        RunAiJudge_EveryAnswerReportsTheCalibratedFlag();

    [Fact]
    public Task AiJudge_RejectsChoiceQuestionWithNoOptionsBeforeAnyProviderWork() =>
        RunAiJudge_RejectsChoiceQuestionWithNoOptionsBeforeAnyProviderWork();

    [Fact]
    public Task AiJudge_RejectsScoreQuestionWithFewerThanTwoLevelsBeforeAnyProviderWork() =>
        RunAiJudge_RejectsScoreQuestionWithFewerThanTwoLevelsBeforeAnyProviderWork();

    [Fact]
    public Task AiJudge_RejectsUnknownQuestionTypeBeforeAnyProviderWork() =>
        RunAiJudge_RejectsUnknownQuestionTypeBeforeAnyProviderWork();
}
