using Vyral.Providers.Abstractions;
using Vyral.Providers.Onnx;
using Vyral.Tests.Conformance;

namespace Vyral.Tests.Providers;

public class OnnxNliJudgeConformanceTests : AiJudgeProviderConformanceTests
{
    protected override IProviderTarget CreateProvider()
    {
        var modelDirectory = ResolveModelDirectory(Environment.GetEnvironmentVariable("VYRAL_ONNX_JUDGE_MODEL_DIR")!);
        return new OnnxNliJudgeProviderTarget(new OnnxNliJudgeProviderOptions
        {
            ProviderId = "conformance-onnx-judge",
            DisplayName = "Conformance ONNX judge",
            ModelId = "conformance-onnx-judge",
            ModelPath = modelDirectory,
            EntailmentIndex = OnnxNliJudgeProviderTargets.DefaultEntailmentIndex,
            ExecutionProvider = "cpu",
            MaxTokens = 128,
            BatchSize = 4,
            CpuOnly = true
        });
    }

    [OnnxJudgeModelFact]
    public Task AiJudge_ChoiceProbabilitiesSumToOneAndChoiceIsTheArgmax() =>
        RunAiJudge_ChoiceProbabilitiesSumToOneAndChoiceIsTheArgmax();

    [OnnxJudgeModelFact]
    public Task AiJudge_NoulProbabilityAndDerivedConfidenceAreInUnitRange() =>
        RunAiJudge_NoulProbabilityAndDerivedConfidenceAreInUnitRange();

    [OnnxJudgeModelFact]
    public Task AiJudge_ScoreIsWithinLevelRangeAndLegendCoversEveryLevel() =>
        RunAiJudge_ScoreIsWithinLevelRangeAndLegendCoversEveryLevel();

    [OnnxJudgeModelFact]
    public Task AiJudge_BatchesMixedQuestionTypesInOneRequest() =>
        RunAiJudge_BatchesMixedQuestionTypesInOneRequest();

    [OnnxJudgeModelFact]
    public Task AiJudge_TwoChoiceQuestionsWithSwappedOptionLabelsAreNotIdentical() =>
        RunAiJudge_TwoChoiceQuestionsWithSwappedOptionLabelsAreNotIdentical();

    [OnnxJudgeModelFact]
    public Task AiJudge_EveryAnswerReportsTheCalibratedFlag() =>
        RunAiJudge_EveryAnswerReportsTheCalibratedFlag();

    // The next three are structural-validation checks and do not need the real model, but run them
    // under the same live gate anyway: without VYRAL_ONNX_JUDGE_MODEL_DIR there is no meaningful
    // "this provider" to test conformance against, and OnnxNliJudgeProviderTarget's own missing-model
    // behavior is already covered by OnnxNliJudgeProviderTargetTests.

    [OnnxJudgeModelFact]
    public Task AiJudge_RejectsChoiceQuestionWithNoOptionsBeforeAnyProviderWork() =>
        RunAiJudge_RejectsChoiceQuestionWithNoOptionsBeforeAnyProviderWork();

    [OnnxJudgeModelFact]
    public Task AiJudge_RejectsScoreQuestionWithFewerThanTwoLevelsBeforeAnyProviderWork() =>
        RunAiJudge_RejectsScoreQuestionWithFewerThanTwoLevelsBeforeAnyProviderWork();

    [OnnxJudgeModelFact]
    public Task AiJudge_RejectsUnknownQuestionTypeBeforeAnyProviderWork() =>
        RunAiJudge_RejectsUnknownQuestionTypeBeforeAnyProviderWork();

    private static string ResolveModelDirectory(string configured)
    {
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Vyral.sln")))
        {
            directory = directory.Parent;
        }

        return Path.GetFullPath(Path.Combine(directory?.FullName ?? Directory.GetCurrentDirectory(), configured));
    }
}
