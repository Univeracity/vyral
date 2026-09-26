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

        var calibration = OnnxJudgeCalibrationFitter.Fit(examples, modelId: "test-model");

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

        var calibration = OnnxJudgeCalibrationFitter.Fit(examples);

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
    public void Identity_IsUncalibrated()
    {
        var calibration = OnnxJudgeCalibration.Identity("test-model");

        Assert.Equal(1.0, calibration.Temperature);
        Assert.False(calibration.IsFitted);
    }
}
