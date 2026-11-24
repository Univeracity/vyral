using Vyral.Providers.Abstractions;

namespace Vyral.Tests.Conformance;

public sealed class AiDecisionPolicyConformanceTests
{
    private static readonly DateTimeOffset EvaluatedAt = DateTimeOffset.Parse("2026-10-23T05:30:00Z");

    [Fact]
    public void CalibratedChoiceSelectsAcceptAndCarriesBoundedEvidence()
    {
        var policy = ChoicePolicy();
        var result = Result(new AiJudgeAnswer
        {
            QuestionId = "reuse",
            Choice = "same_item",
            Probabilities = new() { ["same_item"] = 0.94, ["different_item"] = 0.06 },
            Confidence = 0.94,
            Calibrated = true
        });

        var receipt = AiDecisionPolicyEvaluator.Evaluate(policy, result, EvaluatedAt);

        Assert.Equal(AiDecisionOutcomes.Accept, receipt.Outcome);
        Assert.Equal(AiDecisionReasons.Accepted, receipt.Reason);
        Assert.Null(receipt.EscalationRoute);
        Assert.Equal("same_item", receipt.Choice);
        Assert.Equal("local-onnx-nli", receipt.Provider);
        Assert.Equal("onnx-nli-fixture", receipt.ModelId);
        Assert.Equal("trace-1", receipt.Evidence.TraceId);
        Assert.StartsWith("sha256:", receipt.PolicyHash);
        Assert.StartsWith("sha256:", receipt.Evidence.AnswerHash);
        Assert.Equal(EvaluatedAt, receipt.EvaluatedAtUtc);
    }

    [Fact]
    public void DefaultPolicyEscalatesUncalibratedAnswersEvenWhenTheChoiceLooksCertain()
    {
        var result = Result(new AiJudgeAnswer
        {
            QuestionId = "reuse",
            Choice = "same_item",
            Confidence = 0.99,
            Calibrated = false
        });

        var receipt = AiDecisionPolicyEvaluator.Evaluate(ChoicePolicy(), result, EvaluatedAt);

        Assert.Equal(AiDecisionOutcomes.Escalate, receipt.Outcome);
        Assert.Equal(AiDecisionReasons.Uncalibrated, receipt.Reason);
        Assert.Equal("manual-review", receipt.EscalationRoute);
    }

    [Fact]
    public void ExplicitMechanicsPolicyCanBranchOnAnUncalibratedDeterministicFixture()
    {
        var policy = ChoicePolicy();
        policy.Evidence.RequireCalibrated = false;
        policy.Evidence.AllowedValidationStatuses.Add(AiValidationStatuses.Deterministic);
        var result = Result(new AiJudgeAnswer
        {
            QuestionId = "reuse",
            Choice = "different_item",
            Confidence = 0.92,
            Calibrated = false
        }, AiValidationStatuses.Deterministic);

        var receipt = AiDecisionPolicyEvaluator.Evaluate(policy, result, EvaluatedAt);

        Assert.Equal(AiDecisionOutcomes.Reject, receipt.Outcome);
        Assert.Equal(AiDecisionReasons.Rejected, receipt.Reason);
    }

    [Fact]
    public void ProbabilityDeadbandEscalatesRatherThanInventingABranch()
    {
        var policy = ProbabilityPolicy();
        var result = Result(new AiJudgeAnswer
        {
            QuestionId = "supported",
            Probability = 0.55,
            Confidence = 0.55,
            Calibrated = true
        });

        var receipt = AiDecisionPolicyEvaluator.Evaluate(policy, result, EvaluatedAt);

        Assert.Equal(AiDecisionOutcomes.Escalate, receipt.Outcome);
        Assert.Equal(AiDecisionReasons.NoRuleMatch, receipt.Reason);
        Assert.Equal(0.55, receipt.Value);
    }

    [Fact]
    public void InvalidAnswerShapeTakesPrecedenceOverEvidenceThresholds()
    {
        var policy = new AiDecisionPolicy
        {
            Id = "score-gate",
            Version = "1",
            QuestionId = "quality",
            AnswerKind = AiDecisionAnswerKinds.Score,
            Accept = new() { Operator = AiDecisionOperators.AtLeast, Threshold = 2 },
            Reject = new() { Operator = AiDecisionOperators.AtMost, Threshold = 1 },
            Evidence = new() { RequireCalibrated = true },
            EscalationRoute = "manual-review"
        };
        var result = Result(new AiJudgeAnswer
        {
            QuestionId = "quality",
            Score = -0.1,
            Calibrated = false
        });

        var receipt = AiDecisionPolicyEvaluator.Evaluate(policy, result, EvaluatedAt);

        Assert.Equal(AiDecisionOutcomes.Escalate, receipt.Outcome);
        Assert.Equal(AiDecisionReasons.AnswerShapeMismatch, receipt.Reason);
    }

    [Fact]
    public void EvidenceThresholdsFailClosedWhenProviderDoesNotReportTheMetric()
    {
        var policy = ChoicePolicy();
        policy.Evidence.MinimumRotationAgreement = 0.8;
        var result = Result(new AiJudgeAnswer
        {
            QuestionId = "reuse",
            Choice = "same_item",
            Confidence = 0.92,
            Calibrated = true,
            RotationAgreement = null
        });

        var receipt = AiDecisionPolicyEvaluator.Evaluate(policy, result, EvaluatedAt);

        Assert.Equal(AiDecisionOutcomes.Escalate, receipt.Outcome);
        Assert.Equal(AiDecisionReasons.RotationAgreementUnavailable, receipt.Reason);
    }

    [Fact]
    public void FailedProviderRunStillProducesAnEscalationReceiptWithoutParsingOutput()
    {
        var result = Result(new AiJudgeAnswer { QuestionId = "reuse" });
        result.Status = ProviderRunStatus.TimedOut;
        result.ProviderStatus = "timeout";
        result.Output.Clear();

        var receipt = AiDecisionPolicyEvaluator.Evaluate(ChoicePolicy(), result, EvaluatedAt);

        Assert.Equal(AiDecisionOutcomes.Escalate, receipt.Outcome);
        Assert.Equal(AiDecisionReasons.ProviderNotSucceeded, receipt.Reason);
        Assert.Null(receipt.Evidence.AnswerHash);
    }

    [Fact]
    public void DuplicateMatchingAnswersEscalateAsAmbiguous()
    {
        var answer = new AiJudgeAnswer { QuestionId = "reuse", Choice = "same_item", Confidence = 0.9, Calibrated = true };
        var result = Result(answer);
        result.Output = ProviderJson.ToJsonObject(new AiJudgeResult
        {
            Answers = new() { answer, answer },
            ValidationStatus = AiValidationStatuses.ProviderJson,
            ModelId = "onnx-nli-fixture"
        });

        var receipt = AiDecisionPolicyEvaluator.Evaluate(ChoicePolicy(), result, EvaluatedAt);

        Assert.Equal(AiDecisionReasons.AnswerAmbiguous, receipt.Reason);
        Assert.Equal(AiDecisionOutcomes.Escalate, receipt.Outcome);
    }

    [Fact]
    public void MalformedProviderMetricsEscalateWithoutRunningPolicyComparisons()
    {
        var result = Result(new AiJudgeAnswer { QuestionId = "reuse" });
        result.Output = System.Text.Json.Nodes.JsonNode.Parse(
            """{"answers":[{"questionId":"reuse","choice":"same_item","confidence":"certain","calibrated":true}],"validationStatus":"provider_json"}""")!.AsObject();

        var receipt = AiDecisionPolicyEvaluator.Evaluate(ChoicePolicy(), result, EvaluatedAt);

        Assert.Equal(AiDecisionOutcomes.Escalate, receipt.Outcome);
        Assert.Equal(AiDecisionReasons.InvalidProviderOutput, receipt.Reason);
        Assert.Null(receipt.Evidence.AnswerHash);
    }

    [Fact]
    public void MalformedTraceHashesEscalateWithoutCopyingInvalidEvidence()
    {
        var result = Result(new AiJudgeAnswer
        {
            QuestionId = "reuse",
            Choice = "same_item",
            Confidence = 0.94,
            Calibrated = true
        });
        result.Trace!.InputHash = "not-a-sha256-digest";

        var receipt = AiDecisionPolicyEvaluator.Evaluate(ChoicePolicy(), result, EvaluatedAt);

        Assert.Equal(AiDecisionOutcomes.Escalate, receipt.Outcome);
        Assert.Equal(AiDecisionReasons.InvalidProviderEvidence, receipt.Reason);
        Assert.Null(receipt.Evidence.InputHash);
        Assert.Null(receipt.Evidence.AnswerHash);
    }

    [Fact]
    public void InvalidOrOverlappingPoliciesAreRejectedBeforeEvaluation()
    {
        var probability = ProbabilityPolicy();
        probability.Accept.Threshold = 0.4;
        probability.Reject.Threshold = 0.6;
        Assert.Contains(AiDecisionPolicyEvaluator.Validate(probability), error => error.Contains("must not overlap", StringComparison.Ordinal));

        var wrongCapability = AiDecisionPolicyEvaluator.Validate(ChoicePolicy(), ProviderCapabilityIds.AiChat);
        Assert.Contains("decision policies require capability ai.judge", wrongCapability);
        Assert.Throws<ArgumentException>(() => AiDecisionPolicyEvaluator.ComputePolicyHash(probability));

        var nullValues = ChoicePolicy();
        nullValues.Accept.Values = null!;
        Assert.Contains(AiDecisionPolicyEvaluator.Validate(nullValues), error => error.Contains("accept.values", StringComparison.Ordinal));

        var unknown = System.Text.Json.JsonSerializer.Deserialize<AiDecisionPolicy>(
            """{"schema":"vyral.ai-decision-policy.v1","id":"p","version":"1","questionId":"q","answerKind":"choice","accept":{"operator":"in","values":["yes"]},"reject":{"operator":"in","values":["no"]},"evidence":{"requireCalibrated":true,"allowedValidationStatuses":[]},"escalationRoute":"human","provider":"must-not-control-policy"}""",
            ProviderJson.Options)!;
        Assert.Contains("policy contains unknown properties", AiDecisionPolicyEvaluator.Validate(unknown));
    }

    [Fact]
    public void PolicyHashIsStableAcrossDictionaryFreeRoundTrips()
    {
        var policy = ChoicePolicy();
        var serialized = ProviderJson.ToJsonObject(policy).ToJsonString(ProviderJson.Options);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<AiDecisionPolicy>(serialized, ProviderJson.Options)!;

        Assert.Equal(AiDecisionPolicyEvaluator.ComputePolicyHash(policy), AiDecisionPolicyEvaluator.ComputePolicyHash(roundTripped));
        Assert.Equal("sha256:0bafeabfa2a825d058d609434ec600f7ea9675153362c0b1d2aed03cbef872b3", AiDecisionPolicyEvaluator.ComputePolicyHash(policy));
    }

    [Theory]
    [InlineData(1.0, 0.0, "sha256:f0fb947a86f2de6b54cb7d861aa8cdc9c173589502e09b62214f5e8e6e868ad4")]
    [InlineData(0.000001, 0.0000001, "sha256:7e392c129d679769469aef4f83602862b37097ccf1f3e94c42f5ed824700270c")]
    [InlineData(1e20, 1.0, "sha256:f3c6db3fb5d1fbb735cb97f43204a7e077295fb8bd4bb6f89b1e27edfdf7c46b")]
    [InlineData(0.30000000000000004, 0.2, "sha256:dc93dd956e257d8f41407261ab38c53e554c5f4b717155fa015e58fc46578e79")]
    public void CanonicalNumberHashesMatchThePythonRuntime(double accept, double reject, string expected)
    {
        var policy = new AiDecisionPolicy
        {
            Id = "x",
            Version = "1",
            QuestionId = "q",
            AnswerKind = AiDecisionAnswerKinds.Score,
            Accept = new() { Operator = AiDecisionOperators.AtLeast, Threshold = accept },
            Reject = new() { Operator = AiDecisionOperators.AtMost, Threshold = reject },
            Evidence = new() { RequireCalibrated = true },
            EscalationRoute = "human"
        };

        Assert.Equal(expected, AiDecisionPolicyEvaluator.ComputePolicyHash(policy));
    }

    [Fact]
    public void CanonicalUnicodeOrderingMatchesThePythonRuntime()
    {
        var policy = new AiDecisionPolicy
        {
            Id = "unicode",
            Version = "1",
            QuestionId = "q",
            AnswerKind = AiDecisionAnswerKinds.Choice,
            Accept = new() { Operator = AiDecisionOperators.In, Values = new() { "😀" } },
            Reject = new() { Operator = AiDecisionOperators.In, Values = new() { "\ue000" } },
            Evidence = new() { RequireCalibrated = true },
            EscalationRoute = "human"
        };
        var result = Result(new AiJudgeAnswer
        {
            QuestionId = "q",
            Choice = "😀",
            Probabilities = new() { ["\ue000"] = 0.4, ["😀"] = 0.6 },
            Calibrated = true
        });

        var receipt = AiDecisionPolicyEvaluator.Evaluate(policy, result, EvaluatedAt);

        Assert.Equal("sha256:624834f6b16ad8bf10b7bef8c8eab645e95f48387d9307ac941bf0f51818975d", receipt.PolicyHash);
        Assert.Equal("sha256:9c3452fa261ecef3eb5a67c6b80d28c510116b21423f7f1bc34185f594ad1565", receipt.Evidence.AnswerHash);
    }

    private static AiDecisionPolicy ChoicePolicy() => new()
    {
        Id = "catalog-item-reuse",
        Version = "1.0.0",
        QuestionId = "reuse",
        AnswerKind = AiDecisionAnswerKinds.Choice,
        Accept = new() { Operator = AiDecisionOperators.In, Values = new() { "same_item" } },
        Reject = new() { Operator = AiDecisionOperators.In, Values = new() { "different_item" } },
        Evidence = new() { RequireCalibrated = true, MinimumConfidence = 0.8 },
        EscalationRoute = "manual-review"
    };

    private static AiDecisionPolicy ProbabilityPolicy() => new()
    {
        Id = "claim-support",
        Version = "1.0.0",
        QuestionId = "supported",
        AnswerKind = AiDecisionAnswerKinds.Probability,
        Accept = new() { Operator = AiDecisionOperators.AtLeast, Threshold = 0.8 },
        Reject = new() { Operator = AiDecisionOperators.AtMost, Threshold = 0.2 },
        Evidence = new() { RequireCalibrated = true },
        EscalationRoute = "manual-review"
    };

    private static ProviderRunResult Result(AiJudgeAnswer answer, string validationStatus = AiValidationStatuses.ProviderJson) => new()
    {
        Status = ProviderRunStatus.Succeeded,
        Provider = "local-onnx-nli",
        Capability = ProviderCapabilityIds.AiJudge,
        Operation = "run",
        Mode = "mechanics",
        ProviderStatus = "ok",
        Output = ProviderJson.ToJsonObject(new AiJudgeResult
        {
            Answers = new() { answer },
            ValidationStatus = validationStatus,
            ModelId = "onnx-nli-fixture"
        }),
        Trace = new()
        {
            TraceId = "trace-1",
            Provider = "local-onnx-nli",
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Mode = "mechanics",
            AdapterId = "onnx-nli",
            ModelId = "onnx-nli-fixture",
            ConfigHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            InputHash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            OutputHash = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        }
    };
}
