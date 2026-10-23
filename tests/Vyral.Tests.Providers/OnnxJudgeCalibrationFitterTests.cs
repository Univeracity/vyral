using System.Security.Cryptography;
using System.Text;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Onnx;

namespace Vyral.Tests.Providers;

public class OnnxJudgeCalibrationFitterTests
{
    [Fact]
    public void Fit_RaisesTemperatureForOverconfidentButOftenWrongLogits()
    {
        // The model is consistently very confident (large logit gap) but correct only ~60% of the
        // time. A well-fit temperature should soften that overconfidence: NLL at the fitted
        // temperature must beat NLL at temperature 1 (raw, unscaled logits).
        var random = new Random(42);
        var examples = new List<OnnxJudgeCalibrationFitter.LabeledExample>();
        for (var i = 0; i < 200; i++)
        {
            var correctIndex = random.Next(0, 2);
            var confidentLogits = correctIndex == 0 ? new[] { 8.0, -8.0 } : new[] { -8.0, 8.0 };
            // ~40% of the time the confident prediction is actually wrong.
            var actualCorrect = random.NextDouble() < 0.4 ? 1 - correctIndex : correctIndex;
            examples.Add(new OnnxJudgeCalibrationFitter.LabeledExample(confidentLogits, actualCorrect));
        }

        var calibration = OnnxJudgeCalibrationFitter.Fit(examples, HumanContext());

        Assert.True(calibration.Temperature > 1.5, $"Expected temperature to be raised well above 1, got {calibration.Temperature}.");
        Assert.Equal(examples.Count, calibration.SampleCount);
        Assert.NotNull(calibration.FittedAtUtc);
        Assert.True(calibration.IsFitted);

        var nllAtOne = OnnxJudgeCalibrationFitter.NegativeLogLikelihood(examples, 1.0);
        var nllAtFitted = OnnxJudgeCalibrationFitter.NegativeLogLikelihood(examples, calibration.Temperature);
        Assert.True(nllAtFitted < nllAtOne, $"Fitted NLL ({nllAtFitted}) should beat NLL at T=1 ({nllAtOne}).");
    }

    [Fact]
    public void Fit_RecoversTemperatureNearOneWhenLabelsAreGeneratedFromTheModelsOwnDistribution()
    {
        // "Well calibrated" means the model's stated confidence already matches its actual accuracy.
        // Simulate that directly: draw a random logit gap, compute softmax(T=1), and sample the label
        // from that exact distribution. The MLE temperature over many such examples should land near 1.
        var random = new Random(7);
        var examples = new List<OnnxJudgeCalibrationFitter.LabeledExample>();
        for (var i = 0; i < 4000; i++)
        {
            var gap = random.NextDouble() * 2.4 + 0.1;
            var logits = new[] { gap, -gap };
            var probabilities = OnnxJudgeCalibrationFitter.Softmax(logits);
            var correctIndex = random.NextDouble() < probabilities[0] ? 0 : 1;
            examples.Add(new OnnxJudgeCalibrationFitter.LabeledExample(logits, correctIndex));
        }

        var calibration = OnnxJudgeCalibrationFitter.Fit(examples, HumanContext());

        Assert.InRange(calibration.Temperature, 0.7, 1.4);
    }

    [Fact]
    public void Softmax_SumsToOneAndPicksLargestLogit()
    {
        var probabilities = OnnxJudgeCalibrationFitter.Softmax(new[] { 1.0, 3.0, 0.5 });

        Assert.InRange(probabilities.Sum(), 0.999, 1.001);
        Assert.Equal(1, Array.IndexOf(probabilities, probabilities.Max()));
    }

    [Fact]
    public void Fit_ThrowsWithoutExamples()
    {
        Assert.Throws<ArgumentException>(() => OnnxJudgeCalibrationFitter.Fit(Array.Empty<OnnxJudgeCalibrationFitter.LabeledExample>()));
    }

    [Fact]
    public void CalibrationMathRejectsNonFiniteOrStructurallyInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => OnnxJudgeCalibrationFitter.Fit(
            new[] { new OnnxJudgeCalibrationFitter.LabeledExample(new[] { double.NaN, 1.0 }, 0) }));
        Assert.Throws<ArgumentException>(() => OnnxJudgeCalibrationFitter.Fit(
            new[] { new OnnxJudgeCalibrationFitter.LabeledExample(new[] { 1.0, 0.0 }, 0) },
            iterations: 0));
        Assert.Throws<ArgumentException>(() => OnnxJudgeCalibrationFitter.Softmax(Array.Empty<double>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => OnnxJudgeCalibrationFitter.Softmax(new[] { 1.0 }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OnnxJudgeCalibrationFitter.NegativeLogLikelihood(
            new[] { new OnnxJudgeCalibrationFitter.LabeledExample(new[] { 1.0, 0.0 }, 0) },
            double.PositiveInfinity));
    }

    [Fact]
    public void Fit_WithoutHumanProvenanceCannotReportCalibrated()
    {
        var calibration = OnnxJudgeCalibrationFitter.Fit(
            new[] { new OnnxJudgeCalibrationFitter.LabeledExample(new[] { 2.0, 1.0 }, 0) },
            modelId: "test-model");

        Assert.False(calibration.IsFitted);
        Assert.Null(calibration.LabelSource);
    }

    [Fact]
    public void Fit_RejectsNonHumanOrIncompleteProvenance()
    {
        var examples = new[]
        {
            new OnnxJudgeCalibrationFitter.LabeledExample(new[] { 2.0, 1.0 }, 0)
        };
        var nonHuman = HumanContext();
        nonHuman = new OnnxJudgeCalibrationFitContext
        {
            DatasetId = nonHuman.DatasetId,
            DatasetVersion = nonHuman.DatasetVersion,
            DatasetSha256 = nonHuman.DatasetSha256,
            LabelSource = "jev",
            ModelId = nonHuman.ModelId,
            QuestionType = nonHuman.QuestionType,
            HypothesisTemplateSha256 = nonHuman.HypothesisTemplateSha256,
            EntailmentIndex = nonHuman.EntailmentIndex
        };

        Assert.Throws<ArgumentException>(() => OnnxJudgeCalibrationFitter.Fit(examples, nonHuman));

        var incomplete = HumanContext();
        incomplete = new OnnxJudgeCalibrationFitContext
        {
            DatasetId = incomplete.DatasetId,
            DatasetVersion = incomplete.DatasetVersion,
            DatasetSha256 = "not-a-digest",
            LabelSource = incomplete.LabelSource,
            ModelId = incomplete.ModelId,
            QuestionType = incomplete.QuestionType,
            HypothesisTemplateSha256 = incomplete.HypothesisTemplateSha256,
            EntailmentIndex = incomplete.EntailmentIndex
        };
        Assert.Throws<ArgumentException>(() => OnnxJudgeCalibrationFitter.Fit(examples, incomplete));
    }

    [Fact]
    public void FittedCalibrationAppliesOnlyToItsBoundScoringClosure()
    {
        var calibration = OnnxJudgeCalibrationFitter.Fit(
            new[] { new OnnxJudgeCalibrationFitter.LabeledExample(new[] { 2.0, 1.0 }, 0) },
            HumanContext());

        Assert.True(calibration.IsApplicableTo(AiJudgeQuestionTypes.Choice, "This example is about: {label}.", 0, "test-model"));
        Assert.False(calibration.IsApplicableTo(AiJudgeQuestionTypes.Score, "This example is about: {label}.", 0, "test-model"));
        Assert.False(calibration.IsApplicableTo(AiJudgeQuestionTypes.Choice, "Different: {label}.", 0, "test-model"));
        Assert.False(calibration.IsApplicableTo(AiJudgeQuestionTypes.Choice, "This example is about: {label}.", 1, "test-model"));
        Assert.False(calibration.IsApplicableTo(AiJudgeQuestionTypes.Choice, "This example is about: {label}.", 0, "other-model"));
    }

    [Fact]
    public void DatasetManifestRequiresHumanSourceAndExactBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "vyral-calibration-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var datasetPath = Path.Combine(root, "examples.jsonl");
            var manifestPath = datasetPath + ".manifest.json";
            var dataset = "{\"context\":\"x\"}\n";
            File.WriteAllText(datasetPath, dataset);
            var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataset))).ToLowerInvariant();
            File.WriteAllText(manifestPath,
                $$"""{"schema":"vyral.onnx-judge-calibration-dataset.v1","datasetId":"fixture","datasetVersion":"1","labelSource":"human","datasetSha256":"{{digest}}"}""");

            var verifiedBytes = OnnxJudgeCalibrationDatasetManifest.LoadAndVerifyBytes(
                manifestPath,
                datasetPath,
                out var manifest);
            Assert.Equal("fixture", manifest.DatasetId);
            Assert.Equal(Encoding.UTF8.GetBytes(dataset), verifiedBytes);

            File.AppendAllText(datasetPath, " ");
            Assert.Throws<InvalidOperationException>(() =>
                OnnxJudgeCalibrationDatasetManifest.LoadAndVerify(manifestPath, datasetPath));

            File.WriteAllText(datasetPath, dataset);
            File.WriteAllText(manifestPath,
                $$"""{"schema":"vyral.onnx-judge-calibration-dataset.v1","datasetId":"fixture","datasetVersion":"1","labelSource":"jev","datasetSha256":"{{digest}}"}""");
            Assert.Throws<InvalidOperationException>(() =>
                OnnxJudgeCalibrationDatasetManifest.LoadAndVerify(manifestPath, datasetPath));

            File.WriteAllText(manifestPath,
                $$"""{"schema":"vyral.onnx-judge-calibration-dataset.v1","datasetId":"fixture","DatasetId":"ambiguous","datasetVersion":"1","labelSource":"human","datasetSha256":"{{digest}}"}""");
            Assert.Throws<InvalidOperationException>(() =>
                OnnxJudgeCalibrationDatasetManifest.LoadAndVerify(manifestPath, datasetPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CalibrationArtifactTreatsLegacyAsUncalibratedAndRejectsAmbiguousJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "vyral-calibration-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "calibration.json");
        try
        {
            File.WriteAllText(path,
                """{"temperature":0.9,"modelId":"legacy","sampleCount":30,"fittedNegativeLogLikelihood":0.2,"fittedAtUtc":"2026-10-23T00:00:00Z"}""");
            var legacy = OnnxJudgeCalibration.TryLoad(path);
            Assert.NotNull(legacy);
            Assert.False(legacy!.IsFitted);

            File.WriteAllText(path, """{"temperature":1,"temperature":2}""");
            Assert.Throws<InvalidOperationException>(() => OnnxJudgeCalibration.TryLoad(path));

            File.WriteAllText(path, """{"temperature":1,"unknown":true}""");
            Assert.Throws<InvalidOperationException>(() => OnnxJudgeCalibration.TryLoad(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Identity_IsUncalibrated()
    {
        var calibration = OnnxJudgeCalibration.Identity("test-model");

        Assert.Equal(1.0, calibration.Temperature);
        Assert.False(calibration.IsFitted);
    }

    private static OnnxJudgeCalibrationFitContext HumanContext() => new()
    {
        DatasetId = "fixture",
        DatasetVersion = "1.0.0",
        DatasetSha256 = "sha256:" + new string('a', 64),
        LabelSource = OnnxJudgeCalibration.HumanLabelSource,
        ModelId = "test-model",
        QuestionType = AiJudgeQuestionTypes.Choice,
        HypothesisTemplateSha256 = ProviderHash.Sha256("This example is about: {label}."),
        EntailmentIndex = 0
    };
}
