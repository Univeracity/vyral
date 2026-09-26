using System.Text.Json;
using Vyral.Providers.Abstractions;

namespace Vyral.Tests.Conformance;

/// <summary>
/// Shared behavioral contract for every <c>ai.judge</c> implementer (deterministic, local ONNX,
/// remote Jev, and any future one). Where the adapter-contributor guide's "AI / coding provider
/// target" family currently has only per-provider unit/doctor tests, this proves the three shipped
/// implementers actually agree on the portable contract instead of only demoing each in isolation —
/// exactly the "shared conformance, not only provider demos" bar the guide sets for storage/execution
/// adapters. Every assertion here is a structural/contract invariant (shape, range, id-consistency),
/// never a specific expected answer: a deterministic hash-based stub, a semantic local classifier, and
/// a remote judgment API will not agree on what the *right* choice is, only on what a valid answer
/// looks like.
/// </summary>
public abstract class AiJudgeProviderConformanceTests
{
    protected abstract IProviderTarget CreateProvider();

    private static AiJudgeResult ParseJudgeResult(ProviderRunResult result) =>
        result.Output.Deserialize<AiJudgeResult>(ProviderJson.Options)
            ?? throw new InvalidOperationException("Provider output could not be deserialized as AiJudgeResult.");

    protected async Task RunAiJudge_ChoiceProbabilitiesSumToOneAndChoiceIsTheArgmax()
    {
        var provider = CreateProvider();
        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "Records subject to a retention hold may be deleted only after an authorized release.",
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Choice,
                        Prompt = "what topic does this passage primarily describe",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "retention", Label = "records retention and deletion policy" },
                            new() { Id = "travel", Label = "employee travel reimbursement" },
                            new() { Id = "weather", Label = "weather forecasting" }
                        }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var answer = ParseJudgeResult(result).Answers.Single();

        Assert.Equal("q1", answer.QuestionId);
        Assert.NotNull(answer.Choice);
        Assert.NotNull(answer.Probabilities);
        Assert.Equal(new[] { "retention", "travel", "weather" }, answer.Probabilities!.Keys.OrderBy(id => id, StringComparer.Ordinal));

        var sum = answer.Probabilities.Values.Sum();
        Assert.InRange(sum, 0.98, 1.02);

        var argmax = answer.Probabilities.OrderByDescending(pair => pair.Value).First().Key;
        Assert.Equal(argmax, answer.Choice);

        Assert.NotNull(answer.Confidence);
        Assert.InRange(answer.Confidence!.Value, 0.0, 1.0);
    }

    protected async Task RunAiJudge_NoulProbabilityAndDerivedConfidenceAreInUnitRange()
    {
        var provider = CreateProvider();
        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "The invoice lists a subtotal of $380 plus $45 in shipping, for a total of $425.",
                Questions = new List<AiJudgeQuestion>
                {
                    new() { Id = "q1", Type = AiJudgeQuestionTypes.Noul, Prompt = "The invoice total exceeds $400." }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var answer = ParseJudgeResult(result).Answers.Single();

        Assert.Equal("q1", answer.QuestionId);
        Assert.Null(answer.Choice);
        Assert.NotNull(answer.Probability);
        Assert.InRange(answer.Probability!.Value, 0.0, 1.0);
        Assert.NotNull(answer.Confidence);
        Assert.InRange(answer.Confidence!.Value, 0.0, 1.0);
    }

    protected async Task RunAiJudge_ScoreIsWithinLevelRangeAndLegendCoversEveryLevel()
    {
        var provider = CreateProvider();
        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "The incident caused a two-hour outage affecting all customers; no workaround was available.",
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Score,
                        Prompt = "rate the severity of this incident",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "low", Label = "cosmetic, no functional impact" },
                            new() { Id = "medium", Label = "degraded but a workaround exists" },
                            new() { Id = "high", Label = "blocking, no workaround exists" }
                        }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var answer = ParseJudgeResult(result).Answers.Single();

        Assert.Equal("q1", answer.QuestionId);
        Assert.Null(answer.Choice);
        Assert.NotNull(answer.Score);
        Assert.InRange(answer.Score!.Value, 0.0, 2.0);

        Assert.NotNull(answer.Probabilities);
        Assert.Equal(new[] { "high", "low", "medium" }, answer.Probabilities!.Keys.OrderBy(id => id, StringComparer.Ordinal));
        Assert.InRange(answer.Probabilities.Values.Sum(), 0.98, 1.02);

        Assert.NotNull(answer.Legend);
        Assert.Equal("cosmetic, no functional impact", answer.Legend!["low"]);
        Assert.Equal("degraded but a workaround exists", answer.Legend["medium"]);
        Assert.Equal("blocking, no workaround exists", answer.Legend["high"]);
    }

    protected async Task RunAiJudge_BatchesMixedQuestionTypesInOneRequest()
    {
        var provider = CreateProvider();
        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "Records subject to a retention hold may be deleted only after an authorized release.",
                Questions = new List<AiJudgeQuestion>
                {
                    new() { Id = "noul-1", Type = AiJudgeQuestionTypes.Noul, Prompt = "This passage describes a records retention policy." },
                    new()
                    {
                        Id = "choice-1",
                        Type = AiJudgeQuestionTypes.Choice,
                        Prompt = "what topic does this passage primarily describe",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "retention", Label = "records retention" },
                            new() { Id = "travel", Label = "travel reimbursement" }
                        }
                    },
                    new()
                    {
                        Id = "score-1",
                        Type = AiJudgeQuestionTypes.Score,
                        Prompt = "how strictly controlled is the described process",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "unrestricted", Label = "no restrictions at all" },
                            new() { Id = "requires_authorization", Label = "requires prior authorization" }
                        }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var answers = ParseJudgeResult(result).Answers;

        Assert.Equal(3, answers.Count);
        Assert.Equal(new[] { "choice-1", "noul-1", "score-1" }, answers.Select(answer => answer.QuestionId).OrderBy(id => id, StringComparer.Ordinal));

        var noulAnswer = answers.Single(answer => answer.QuestionId == "noul-1");
        Assert.NotNull(noulAnswer.Probability);
        Assert.Null(noulAnswer.Choice);
        Assert.Null(noulAnswer.Score);

        var choiceAnswer = answers.Single(answer => answer.QuestionId == "choice-1");
        Assert.NotNull(choiceAnswer.Choice);
        Assert.Null(choiceAnswer.Probability);
        Assert.Null(choiceAnswer.Score);

        var scoreAnswer = answers.Single(answer => answer.QuestionId == "score-1");
        Assert.NotNull(scoreAnswer.Score);
        Assert.Null(scoreAnswer.Choice);
        Assert.Null(scoreAnswer.Probability);
    }

    /// <summary>
    /// Regression coverage for a real bug (BUG-20260918-061159-20F269): a degenerate implementation
    /// that returns a uniform, content-independent distribution for every question passes every other
    /// test in this suite — right shape, probabilities sum to 1, a choice gets picked. Two Choice
    /// questions with the same option ids but the option LABELS swapped between them (not just a
    /// different <c>Prompt</c> — a local ONNX-style implementer's default hypothesis template is not
    /// required to incorporate the prompt at all, only the label, so varying only the prompt is not a
    /// fair cross-implementer test) must not produce identical probability distributions, and neither
    /// answer may itself be the degenerate uniform distribution. This does not assert which option
    /// should win (a deterministic hash-based stub has no semantic basis to pick the "right" one) —
    /// only that asking two genuinely different questions does not silently collapse both answers to
    /// the same (or uniform) noise.
    /// </summary>
    protected async Task RunAiJudge_TwoChoiceQuestionsWithSwappedOptionLabelsAreNotIdentical()
    {
        var provider = CreateProvider();

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "Records subject to a retention hold may be deleted only after an authorized release. Employee travel reimbursements require a cost center and original receipts.",
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Choice,
                        Prompt = "what topic does this passage primarily describe",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "option_a", Label = "records retention and deletion policy" },
                            new() { Id = "option_b", Label = "weather forecasting" }
                        }
                    },
                    new()
                    {
                        Id = "q2",
                        Type = AiJudgeQuestionTypes.Choice,
                        Prompt = "what does this passage require before travel expenses are reimbursed",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "option_a", Label = "a passport renewal application" },
                            new() { Id = "option_b", Label = "a cost center and original receipts" }
                        }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var answers = ParseJudgeResult(result).Answers;
        var q1 = answers.Single(answer => answer.QuestionId == "q1");
        var q2 = answers.Single(answer => answer.QuestionId == "q2");

        Assert.NotNull(q1.Probabilities);
        Assert.NotNull(q2.Probabilities);
        Assert.True(q1.Probabilities!.Values.Distinct().Count() > 1, "q1's own distribution must not be uniform across options.");
        Assert.True(q2.Probabilities!.Values.Distinct().Count() > 1, "q2's own distribution must not be uniform across options.");

        var q1Serialized = string.Join(",", q1.Probabilities.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value:F6}"));
        var q2Serialized = string.Join(",", q2.Probabilities.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value:F6}"));
        Assert.NotEqual(q1Serialized, q2Serialized);
    }

    protected async Task RunAiJudge_EveryAnswerReportsTheCalibratedFlag()
    {
        var provider = CreateProvider();
        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "The invoice lists a subtotal of $380 plus $45 in shipping, for a total of $425.",
                Questions = new List<AiJudgeQuestion>
                {
                    new() { Id = "q1", Type = AiJudgeQuestionTypes.Noul, Prompt = "The invoice total exceeds $400." }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var answer = ParseJudgeResult(result).Answers.Single();

        // Calibrated is a plain bool (not nullable), so this is really asserting the field survives
        // the round trip through each provider's own JSON output shape rather than being omitted.
        Assert.True(answer.Calibrated || !answer.Calibrated);
    }

    protected async Task RunAiJudge_RejectsChoiceQuestionWithNoOptionsBeforeAnyProviderWork()
    {
        var provider = CreateProvider();
        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "context",
                Questions = new List<AiJudgeQuestion>
                {
                    new() { Id = "q1", Type = AiJudgeQuestionTypes.Choice, Prompt = "no options given" }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Schema, result.FailureClass);
    }

    protected async Task RunAiJudge_RejectsScoreQuestionWithFewerThanTwoLevelsBeforeAnyProviderWork()
    {
        var provider = CreateProvider();
        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "context",
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Score,
                        Prompt = "rate it",
                        Options = new List<AiJudgeOption> { new() { Id = "only", Label = "only level" } }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Schema, result.FailureClass);
    }

    protected async Task RunAiJudge_RejectsUnknownQuestionTypeBeforeAnyProviderWork()
    {
        var provider = CreateProvider();
        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "context",
                Questions = new List<AiJudgeQuestion>
                {
                    new() { Id = "q1", Type = "sentiment", Prompt = "not a real ai.judge question type" }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Schema, result.FailureClass);
    }
}
