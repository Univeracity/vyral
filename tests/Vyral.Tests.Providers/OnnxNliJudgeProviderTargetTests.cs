using Vyral.Providers.Abstractions;
using Vyral.Providers.Onnx;

namespace Vyral.Tests.Providers;

public class OnnxNliJudgeProviderTargetTests
{
    [Fact]
    public void OnnxJudgeProvider_CustomModelDoesNotInheritDefaultModelFiles()
    {
        var options = OnnxNliJudgeProviderTargets.ApplyDefaults(
            new OnnxNliJudgeProviderOptions { ModelPath = "/models/custom/onnx/model.onnx" }, cpuOnly: true);

        Assert.Null(options.VocabPath);
        Assert.Null(options.CalibrationPath);
        Assert.Equal(OnnxNliJudgeProviderTargets.DefaultCpuVocabPath,
            OnnxNliJudgeProviderTargets.ApplyDefaults(null, cpuOnly: true).VocabPath);
    }

    [Fact]
    public async Task OnnxJudgeProvider_ExposesLocalNetworkFreeJudgeCapability()
    {
        var provider = OnnxNliJudgeProviderTargets.CreateCpu();
        var gpuProvider = OnnxNliJudgeProviderTargets.CreateGpu();

        Assert.Equal(OnnxNliJudgeProviderTargets.CpuProviderId, provider.Profile.Id);
        Assert.Equal("onnx", provider.Profile.Family);
        Assert.True(provider.Profile.Local);
        Assert.False(provider.Profile.RequiresNetwork);
        Assert.Equal(ProviderAuthTypes.None, provider.Profile.Auth);

        var capability = Assert.Single(provider.Capabilities);
        Assert.Equal(ProviderCapabilityIds.AiJudge, capability.Id);

        var catalog = await provider.ListModelsAsync();
        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, catalog.Status);
        var model = Assert.Single(catalog.Items);
        // The default model/vocab/calibration files are untracked (downloaded separately; see
        // tools/Vyral.OnnxJudgeCalibration) and may or may not be present in a given checkout, so
        // this default-target test only checks the metadata shape, not a specific calibrated value —
        // OnnxJudgeProvider_ClassifiesEntailmentWithUntrackedModelAndConfirmsEntailmentIndex asserts
        // the real calibrated value against files it requires via VYRAL_ONNX_JUDGE_MODEL_DIR.
        Assert.IsType<bool>(model.Metadata["calibrated"]);
        Assert.IsType<double>(model.Metadata["calibrationTemperature"]);

        var gpuCatalog = await gpuProvider.ListModelsAsync();
        var gpuModel = Assert.Single(gpuCatalog.Items);
        Assert.Equal("cudaPreferred", gpuModel.Metadata["executionProvider"]);
    }

    [Fact]
    public async Task OnnxJudgeProvider_DoctorReportsMissingModelFilesAndUncalibratedWarning()
    {
        var provider = new OnnxNliJudgeProviderTarget(new OnnxNliJudgeProviderOptions
        {
            ProviderId = "test-onnx-judge",
            DisplayName = "Test ONNX judge",
            ModelId = "missing-model",
            ModelPath = ".vyral/models/missing-nli/onnx/model.onnx",
            VocabPath = ".vyral/models/missing-nli/vocab.txt",
            CalibrationPath = ".vyral/models/missing-nli/calibration.json",
            ExecutionProvider = "cpu",
            MaxTokens = 64,
            BatchSize = 2,
            CpuOnly = true
        });

        var doctor = await provider.DiagnoseAsync();

        Assert.Equal(ProviderDoctorStatuses.Failed, doctor.Status);
        Assert.Contains(doctor.Checks, check => check.Id == "model.file" && check.Status == ProviderDoctorStatuses.Failed);
        Assert.Contains(doctor.Checks, check => check.Id == "tokenizer.vocab" && check.Status == ProviderDoctorStatuses.Failed);
        Assert.Contains(doctor.Checks, check => check.Id == "calibration.status" && check.Status == ProviderDoctorStatuses.Warning);
        Assert.Contains(doctor.Checks, check => check.Id == "judgment.equivalence" && check.Status == ProviderDoctorStatuses.Warning);
    }

    [Fact]
    public async Task OnnxJudgeProvider_ReturnsNotConfiguredWhenModelFilesAreMissing()
    {
        var provider = new OnnxNliJudgeProviderTarget(new OnnxNliJudgeProviderOptions
        {
            ProviderId = "test-onnx-judge",
            DisplayName = "Test ONNX judge",
            ModelId = "missing-model",
            ModelPath = ".vyral/models/missing-nli/onnx/model.onnx",
            VocabPath = ".vyral/models/missing-nli/vocab.txt",
            ExecutionProvider = "cpu",
            MaxTokens = 64,
            BatchSize = 2,
            CpuOnly = true
        });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "context",
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Choice,
                        Prompt = "prompt",
                        Options = new List<AiJudgeOption> { new() { Id = "a", Label = "a" }, new() { Id = "b", Label = "b" } }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.NotConfigured, result.Status);
        Assert.Equal(ProviderFailureClasses.Configuration, result.FailureClass);
        Assert.Equal("model_files_missing", result.ProviderStatus);
    }

    [Fact]
    public void OnnxJudgeProvider_CreatesQualificationSmokeRequestWithChoiceQuestion()
    {
        var provider = OnnxNliJudgeProviderTargets.CreateCpu();

        var requests = provider.CreateQualificationRequests(new ProviderQualificationRequest { Capability = ProviderCapabilityIds.AiJudge });

        var request = Assert.Single(requests);
        Assert.Equal(ProviderCapabilityIds.AiJudge, request.Capability);
        Assert.Equal("mechanics", request.Mode);
        var payload = ProviderJson.DeserializePayload<AiJudgeRequest>(request);
        Assert.Single(payload.Questions);
        Assert.Equal(AiJudgeQuestionTypes.Choice, payload.Questions[0].Type);
    }

    [OnnxJudgeModelFact]
    public async Task OnnxJudgeProvider_ClassifiesEntailmentWithUntrackedModelAndConfirmsEntailmentIndex()
    {
        var modelDirectory = ResolveModelDirectory(Environment.GetEnvironmentVariable("VYRAL_ONNX_JUDGE_MODEL_DIR")!);
        var provider = new OnnxNliJudgeProviderTarget(new OnnxNliJudgeProviderOptions
        {
            ProviderId = "live-onnx-judge",
            DisplayName = "Live ONNX judge",
            ModelId = "live-onnx-judge",
            ModelPath = modelDirectory,
            EntailmentIndex = OnnxNliJudgeProviderTargets.DefaultEntailmentIndex,
            ExecutionProvider = "cpu",
            MaxTokens = 128,
            BatchSize = 4,
            CpuOnly = true
        });

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
                            new() { Id = "retention", Label = "records retention and deletion policy" },
                            new() { Id = "weather", Label = "weather forecasting" }
                        }
                    },
                    new() { Id = "q2", Type = AiJudgeQuestionTypes.Noul, Prompt = "This passage is about interplanetary space travel." },
                    new() { Id = "q3", Type = AiJudgeQuestionTypes.Noul, Prompt = "This passage mentions employee travel reimbursement." },
                    new()
                    {
                        Id = "q4",
                        Type = AiJudgeQuestionTypes.Score,
                        Prompt = "how strictly controlled is the described process",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "unrestricted", Label = "no restrictions at all on who may act or when" },
                            new() { Id = "some_conditions", Label = "allowed under some conditions" },
                            new() { Id = "requires_authorization", Label = "requires a specific prior authorization before it may occur" }
                        }
                    }
                }
            })
        });

        Assert.True(result.Status == ProviderRunStatus.Succeeded,
            $"Expected ONNX judge success, got {result.Status}: {result.Output.ToJsonString()}");
        var answers = result.Output["answers"]!.AsArray();

        var choiceAnswer = answers.Single(a => a!["questionId"]!.GetValue<string>() == "q1")!;
        Assert.Equal("retention", choiceAnswer["choice"]?.GetValue<string>());

        // A claim clearly unrelated to the passage should score low entailment probability.
        var unrelatedNoul = answers.Single(a => a!["questionId"]!.GetValue<string>() == "q2")!;
        Assert.True(unrelatedNoul["probability"]!.GetValue<double>() < 0.3,
            $"Expected low entailment probability for an unrelated claim (confirms EntailmentIndex={OnnxNliJudgeProviderTargets.DefaultEntailmentIndex} maps to the model's real ENTAILMENT class, not CONTRADICTION/NEUTRAL); got {unrelatedNoul["probability"]}.");

        // A claim clearly supported by the passage should score high entailment probability.
        var relatedNoul = answers.Single(a => a!["questionId"]!.GetValue<string>() == "q3")!;
        Assert.True(relatedNoul["probability"]!.GetValue<double>() > 0.7,
            $"Expected high entailment probability for a supported claim; got {relatedNoul["probability"]}.");

        // The passage describes a process gated on "authorized release" — the highest-restriction
        // level (index 2) should carry the most weight, pulling the expected position above the
        // scale's midpoint (1.0 of a 0-2 scale).
        var scoreAnswer = answers.Single(a => a!["questionId"]!.GetValue<string>() == "q4")!;
        var score = scoreAnswer["score"]!.GetValue<double>();
        Assert.InRange(score, 0.0, 2.0);
        Assert.True(score > 1.0, $"Expected the weighted position to lean toward the highest-restriction level for an authorization-gated process; got {score}.");
        var legend = scoreAnswer["legend"]!.AsObject();
        Assert.Equal("requires a specific prior authorization before it may occur", legend["requires_authorization"]?.GetValue<string>());
    }

    /// <summary>
    /// Originally written while chasing BUG-20260918-061159-20F269 under the hypothesis that
    /// concurrency or batching was the trigger. That hypothesis turned out to be wrong — the real
    /// cause was premise length relative to MaxTokens, unrelated to question count or concurrency
    /// (see OnnxJudgeProvider_LongPremiseTruncatesInsteadOfDegeneratingToUniformOutput and
    /// OnnxJudgeProvider_ExceedingTheModelsPositionLimitFailsClosed below for the actual regression
    /// coverage). Kept as a real, separate invariant worth having: this asserts a shared provider
    /// instance is safe under concurrent load, which the original investigation's non-repro on this
    /// axis already established but hadn't pinned down as a standing test.
    /// </summary>
    [OnnxJudgeModelFact]
    public async Task OnnxJudgeProvider_ProducesConsistentNonDegenerateResultsUnderConcurrentRequests()
    {
        var modelDirectory = ResolveModelDirectory(Environment.GetEnvironmentVariable("VYRAL_ONNX_JUDGE_MODEL_DIR")!);
        var provider = new OnnxNliJudgeProviderTarget(new OnnxNliJudgeProviderOptions
        {
            ProviderId = "concurrency-check",
            DisplayName = "Concurrency check",
            ModelId = "concurrency-check",
            ModelPath = modelDirectory,
            EntailmentIndex = OnnxNliJudgeProviderTargets.DefaultEntailmentIndex,
            ExecutionProvider = "cpu",
            MaxTokens = 128,
            BatchSize = 8,
            CpuOnly = true
        });

        Task<ProviderRunResult> RunOneAsync() => provider.RunAsync(new ProviderRunRequest
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
                            new() { Id = "retention", Label = "records retention and deletion policy" },
                            new() { Id = "travel", Label = "employee travel reimbursement" },
                            new() { Id = "weather", Label = "weather forecasting" }
                        }
                    }
                }
            })
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => RunOneAsync()));

        Assert.All(results, r => Assert.Equal(ProviderRunStatus.Succeeded, r.Status));
        var probabilities = results.Select(r => r.Output["answers"]!.AsArray()[0]!["probabilities"]!.ToJsonString()).ToArray();

        // Every run asks the identical question, so every answer must be identical to the first...
        Assert.All(probabilities, p => Assert.Equal(probabilities[0], p));
        // ...and none may be the degenerate all-equal distribution the reported bug produces.
        var firstProbabilities = results[0].Output["answers"]!.AsArray()[0]!["probabilities"]!.AsObject();
        var distinctValues = firstProbabilities.Select(pair => pair.Value!.GetValue<double>()).Distinct().Count();
        Assert.True(distinctValues > 1, "Expected a differentiated distribution across the three options, not a uniform one.");
    }

    private const string LongPolicyPremise =
        "Effective the first of the fiscal year, all departments must follow the updated records management framework described below. Financial statements, vendor contracts, and payroll files are retained for the statutory period required by the jurisdiction in which the originating office operates, currently seven years from the date of last activity on the file. Marketing collateral, internal meeting notes, and draft correspondence carry no mandatory retention period and may be disposed of once their operational usefulness has passed, subject to any active legal hold. Records subject to a litigation or regulatory hold may not be destroyed, altered, or transferred outside the organization's custody until the hold has been formally released in writing by the legal department; this restriction applies even if the record's ordinary retention period has already expired. Employees who discover that a record under an active hold has been inadvertently scheduled for destruction must notify the records officer immediately and suspend the destruction pending review. Physical records awaiting destruction are stored in the secure holding area on the third floor and are shredded on a monthly schedule by the contracted document destruction vendor, who provides a certificate of destruction for each batch processed. Electronic records follow the same retention rules but are purged automatically by the records management system unless a hold flag has been applied to the relevant record or folder. " +
        "Separately, employees traveling on company business must submit an expense report within thirty days of return, itemizing airfare, lodging, ground transportation, and meals, each supported by an original receipt; per diem allowances apply only when an itemized meal receipt is unavailable. Reimbursement requests missing a required receipt are returned to the submitting employee for correction rather than processed with the missing item excluded. International travel additionally requires pre-approval from the traveler's manager and a currency-conversion note showing the exchange rate used, sourced from the finance department's approved reference rate for the date of the expense. Questions about whether a specific record falls under an active hold, or about which expenses qualify for per diem treatment, should be directed to the records officer or the travel desk respectively before any destruction, transfer, or reimbursement action is taken.";

    /// <summary>
    /// Regression test for BUG-20260918-061159-20F269's actual root cause (not the original,
    /// mischaracterized "batching" theory): a premise long enough to approach the token budget used
    /// to silently truncate the hypothesis — the part that varies per option — to nothing, so every
    /// option encoded the same content and the model returned identical, uniform output regardless of
    /// which option was "supposed" to be asked about. LongPolicyPremise is ~2,250 characters — long
    /// enough to have triggered the bug against the old 256-token default, comfortably within the
    /// fixed 512-token default. With truncateFirstBeforeSecond now favoring the premise for
    /// truncation instead of the hypothesis, this must still differentiate correctly.
    /// </summary>
    [OnnxJudgeModelFact]
    public async Task OnnxJudgeProvider_LongPremiseTruncatesInsteadOfDegeneratingToUniformOutput()
    {
        var modelDirectory = ResolveModelDirectory(Environment.GetEnvironmentVariable("VYRAL_ONNX_JUDGE_MODEL_DIR")!);
        var provider = new OnnxNliJudgeProviderTarget(new OnnxNliJudgeProviderOptions
        {
            ProviderId = "long-premise-check",
            DisplayName = "Long premise check",
            ModelId = "long-premise-check",
            ModelPath = modelDirectory,
            EntailmentIndex = OnnxNliJudgeProviderTargets.DefaultEntailmentIndex,
            ExecutionProvider = "cpu",
            CpuOnly = true
            // MaxTokens intentionally left at the (now 512) default rather than set explicitly, so
            // this test also pins that default actually being large enough for a realistic passage.
        });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = LongPolicyPremise,
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Choice,
                        Prompt = "what topic does this passage primarily describe",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "retention", Label = "records retention and litigation holds" },
                            new() { Id = "weather", Label = "weather forecasting" }
                        }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var answer = result.Output["answers"]!.AsArray()[0]!;
        Assert.Equal("retention", answer["choice"]?.GetValue<string>());
        var probabilities = answer["probabilities"]!.AsObject();
        Assert.True(probabilities.Select(pair => pair.Value!.GetValue<double>()).Distinct().Count() > 1,
            "Expected a differentiated distribution; a uniform split would indicate the hypothesis was truncated away again.");
    }

    /// <summary>
    /// The complementary boundary case, regardless of truncation
    /// direction: fail closed rather than silently proceeding when truncation cannot avoid emptying a
    /// segment entirely. Reversing the priority (previous test) does not remove this failure mode, it
    /// only moves which segment is at risk: now a pathologically long HYPOTHESIS is what can force the
    /// premise all the way to zero tokens — a "judgment" with no context behind it at all, which is
    /// exactly as degenerate as the original bug, just triggered from the other side. Setting
    /// ChoiceHypothesisTemplate to "{prompt}" and using a several-thousand-character prompt forces
    /// every option's hypothesis alone to exceed the whole token budget, so even eliminating the
    /// entire (short, ordinary) premise isn't enough room — the case the guard exists for.
    /// </summary>
    [OnnxJudgeModelFact]
    public async Task OnnxJudgeProvider_HypothesisAloneExceedingTheBudgetFailsClosed()
    {
        var modelDirectory = ResolveModelDirectory(Environment.GetEnvironmentVariable("VYRAL_ONNX_JUDGE_MODEL_DIR")!);
        var provider = new OnnxNliJudgeProviderTarget(new OnnxNliJudgeProviderOptions
        {
            ProviderId = "over-budget-check",
            DisplayName = "Over budget check",
            ModelId = "over-budget-check",
            ModelPath = modelDirectory,
            EntailmentIndex = OnnxNliJudgeProviderTargets.DefaultEntailmentIndex,
            ExecutionProvider = "cpu",
            CpuOnly = true,
            ChoiceHypothesisTemplate = "{prompt}"
        });

        var enormousPrompt = string.Concat(Enumerable.Repeat(LongPolicyPremise, 3));

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "Records subject to a retention hold may be deleted after an authorized release.",
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Choice,
                        Prompt = enormousPrompt,
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "a", Label = "unused — template ignores {label}" },
                            new() { Id = "b", Label = "unused — template ignores {label}" }
                        }
                    }
                }
            })
        });

        Assert.NotEqual(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(ProviderFailureClasses.Configuration, result.FailureClass);
    }

    /// <summary>
    /// Regression coverage for SUG-20260918-102018-D06D72: claim verification (supports/contradicts/
    /// says-nothing) is a single-hypothesis question, not a topic selection across N candidate
    /// hypotheses. RawClassProbabilities on a Noul answer exposes the
    /// checkpoint's own native entailment/neutral/contradiction split directly for one (premise,
    /// claim) pair, which for a genuine NLI checkpoint IS a supports/contradicts/says_nothing answer
    /// with no per-option hypothesis engineering needed. Three clearly-distinct cases against the
    /// same premise confirm each claim's dominant raw class matches its obvious relation, not just
    /// that the numbers are shaped right.
    /// </summary>
    [OnnxJudgeModelFact]
    public async Task OnnxJudgeProvider_NoulExposesRawClassProbabilitiesForClaimVerification()
    {
        var modelDirectory = ResolveModelDirectory(Environment.GetEnvironmentVariable("VYRAL_ONNX_JUDGE_MODEL_DIR")!);
        var provider = new OnnxNliJudgeProviderTarget(new OnnxNliJudgeProviderOptions
        {
            ProviderId = "claim-verification-check",
            DisplayName = "Claim verification check",
            ModelId = "claim-verification-check",
            ModelPath = modelDirectory,
            EntailmentIndex = OnnxNliJudgeProviderTargets.DefaultEntailmentIndex,
            ClassLabels = OnnxNliJudgeProviderTargets.DefaultClassLabels,
            ExecutionProvider = "cpu",
            CpuOnly = true
        });

        const string premise = "The invoice lists a subtotal of $380 plus $45 in shipping, for a total of $425.";

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = premise,
                Questions = new List<AiJudgeQuestion>
                {
                    new() { Id = "supports", Type = AiJudgeQuestionTypes.Noul, Prompt = "The invoice total exceeds $400." },
                    new() { Id = "contradicts", Type = AiJudgeQuestionTypes.Noul, Prompt = "The invoice total is under $100." },
                    new() { Id = "says_nothing", Type = AiJudgeQuestionTypes.Noul, Prompt = "The invoice was paid by wire transfer." }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var answers = result.Output["answers"]!.AsArray();

        foreach (var questionId in new[] { "supports", "contradicts", "says_nothing" })
        {
            var answer = answers.Single(a => a!["questionId"]!.GetValue<string>() == questionId)!;
            var raw = answer["rawClassProbabilities"]!.AsObject();
            Assert.Equal(3, raw.Count);
            var sum = raw.Select(pair => pair.Value!.GetValue<double>()).Sum();
            Assert.InRange(sum, 0.98, 1.02);

            var dominant = raw.OrderByDescending(pair => pair.Value!.GetValue<double>()).First().Key;
            var expected = questionId switch
            {
                "supports" => "entailment",
                "contradicts" => "contradiction",
                "says_nothing" => "neutral",
                _ => throw new InvalidOperationException()
            };
            Assert.True(dominant == expected, $"Question '{questionId}': expected dominant raw class '{expected}', got '{dominant}' ({raw.ToJsonString()}).");
        }
    }

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

public sealed class OnnxJudgeModelFactAttribute : FactAttribute
{
    public OnnxJudgeModelFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_ONNX_JUDGE_MODEL_DIR")))
        {
            Skip = "Set VYRAL_ONNX_JUDGE_MODEL_DIR to an untracked ONNX NLI model directory to run judge live tests.";
        }
    }
}
