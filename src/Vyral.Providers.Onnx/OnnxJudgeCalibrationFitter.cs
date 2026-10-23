namespace Vyral.Providers.Onnx;

/// <summary>
/// Fits a single temperature for <see cref="OnnxJudgeCalibration"/> by minimizing negative
/// log-likelihood (NLL) over caller-supplied labeled examples. This is the "harness" for
/// calibrating the ONNX judge target: run the classifier over a labeled set (questions with a
/// known correct option), collect the raw logits, call <see cref="Fit"/>, and save the result next
/// to the model so the provider target can load it. It is a real, minimal training procedure — a
/// one-parameter convex-ish fit via golden-section search — not a stand-in.
/// </summary>
public static class OnnxJudgeCalibrationFitter
{
    /// <summary>One question's per-option logits plus which option index was actually correct.</summary>
    public readonly record struct LabeledExample(double[] Logits, int CorrectIndex);

    /// <summary>
    /// Fits temperature mechanics without a provenance closure. The returned artifact is useful
    /// for diagnostics but deliberately reports <see cref="OnnxJudgeCalibration.IsFitted"/> as
    /// false. Use the overload accepting <see cref="OnnxJudgeCalibrationFitContext"/> for a fit
    /// that may drive calibrated answers.
    /// </summary>
    public static OnnxJudgeCalibration Fit(
        IReadOnlyList<LabeledExample> examples,
        string? modelId = null,
        double minTemperature = 0.05,
        double maxTemperature = 10.0,
        int iterations = 100)
    {
        return FitCore(examples, modelId, null, minTemperature, maxTemperature, iterations);
    }

    /// <summary>
    /// Fits a calibration that is eligible to report <c>calibrated=true</c>. The supplied closure
    /// must attest to human labels and bind the fit to its exact dataset, declared model id,
    /// question type, hypothesis template, and entailment class.
    /// </summary>
    public static OnnxJudgeCalibration Fit(
        IReadOnlyList<LabeledExample> examples,
        OnnxJudgeCalibrationFitContext context,
        double minTemperature = 0.05,
        double maxTemperature = 10.0,
        int iterations = 100)
    {
        ArgumentNullException.ThrowIfNull(examples);
        ArgumentNullException.ThrowIfNull(context);
        ValidateExamples(examples);
        if (!string.Equals(context.LabelSource, OnnxJudgeCalibration.HumanLabelSource, StringComparison.Ordinal))
        {
            throw new ArgumentException("ONNX judge calibration labels must carry an explicit human source attestation.", nameof(context));
        }

        var closure = new OnnxJudgeCalibration
        {
            Schema = OnnxJudgeCalibration.CurrentSchema,
            Temperature = 1,
            DatasetId = context.DatasetId,
            DatasetVersion = context.DatasetVersion,
            DatasetSha256 = context.DatasetSha256,
            LabelSource = context.LabelSource,
            ModelId = context.ModelId,
            QuestionType = context.QuestionType,
            HypothesisTemplateSha256 = context.HypothesisTemplateSha256,
            EntailmentIndex = context.EntailmentIndex,
            SampleCount = examples.Count,
            FittedNegativeLogLikelihood = 0,
            FittedAtUtc = DateTime.UtcNow
        };
        if (!closure.IsFitted)
        {
            throw new ArgumentException("ONNX judge calibration fit context is incomplete or invalid.", nameof(context));
        }

        return FitCore(examples, context.ModelId, context, minTemperature, maxTemperature, iterations);
    }

    private static OnnxJudgeCalibration FitCore(
        IReadOnlyList<LabeledExample> examples,
        string? modelId,
        OnnxJudgeCalibrationFitContext? context,
        double minTemperature,
        double maxTemperature,
        int iterations)
    {
        ValidateExamplesAndSearch(examples, minTemperature, maxTemperature, iterations);
        var temperature = GoldenSectionMinimize(t => NegativeLogLikelihoodUnchecked(examples, t), minTemperature, maxTemperature, iterations);

        return new OnnxJudgeCalibration
        {
            Schema = context is null ? null : OnnxJudgeCalibration.CurrentSchema,
            Temperature = temperature,
            ModelId = modelId,
            SampleCount = examples.Count,
            FittedNegativeLogLikelihood = NegativeLogLikelihoodUnchecked(examples, temperature),
            FittedAtUtc = DateTime.UtcNow,
            DatasetId = context?.DatasetId,
            DatasetVersion = context?.DatasetVersion,
            DatasetSha256 = context?.DatasetSha256,
            LabelSource = context?.LabelSource,
            QuestionType = context?.QuestionType,
            HypothesisTemplateSha256 = context?.HypothesisTemplateSha256,
            EntailmentIndex = context?.EntailmentIndex
        };
    }

    private static void ValidateExamplesAndSearch(
        IReadOnlyList<LabeledExample> examples,
        double minTemperature,
        double maxTemperature,
        int iterations)
    {
        ValidateExamples(examples);

        if (!double.IsFinite(minTemperature) || !double.IsFinite(maxTemperature) ||
            minTemperature <= 0 || maxTemperature <= minTemperature)
        {
            throw new ArgumentException("Temperature search range must satisfy 0 < minTemperature < maxTemperature.");
        }

        if (iterations is <= 0 or > 10_000)
        {
            throw new ArgumentException("Calibration iterations must be between 1 and 10000.", nameof(iterations));
        }
    }

    /// <summary>Mean NLL of the correct option under softmax(logits / temperature) across all examples.</summary>
    public static double NegativeLogLikelihood(IReadOnlyList<LabeledExample> examples, double temperature)
    {
        ValidateExamples(examples);
        ValidateTemperature(temperature);
        return NegativeLogLikelihoodUnchecked(examples, temperature);
    }

    private static double NegativeLogLikelihoodUnchecked(IReadOnlyList<LabeledExample> examples, double temperature)
    {
        var total = 0.0;
        foreach (var example in examples)
        {
            var probabilities = SoftmaxUnchecked(example.Logits, temperature);
            var probability = Math.Max(probabilities[example.CorrectIndex], 1e-12);
            total -= Math.Log(probability);
        }

        return total / examples.Count;
    }

    public static double[] Softmax(IReadOnlyList<double> logits, double temperature = 1.0)
    {
        ArgumentNullException.ThrowIfNull(logits);
        if (logits.Count == 0 || logits.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("Softmax requires at least one finite logit.", nameof(logits));
        ValidateTemperature(temperature);
        return SoftmaxUnchecked(logits, temperature);
    }

    private static double[] SoftmaxUnchecked(IReadOnlyList<double> logits, double temperature)
    {
        var scaled = logits.Select(logit => logit / temperature).ToArray();
        var max = scaled.Max();
        var exponentials = scaled.Select(value => Math.Exp(value - max)).ToArray();
        var sum = exponentials.Sum();
        return exponentials.Select(value => value / sum).ToArray();
    }

    private static void ValidateExamples(IReadOnlyList<LabeledExample> examples)
    {
        ArgumentNullException.ThrowIfNull(examples);
        if (examples.Count == 0)
            throw new ArgumentException("Calibration requires at least one labeled example.", nameof(examples));

        foreach (var example in examples)
        {
            if (example.Logits is null || example.Logits.Length < 2 || example.Logits.Any(value => !double.IsFinite(value)))
                throw new ArgumentException("Each labeled example requires at least two finite logits.", nameof(examples));
            if (example.CorrectIndex < 0 || example.CorrectIndex >= example.Logits.Length)
                throw new ArgumentException("CorrectIndex must index into Logits.", nameof(examples));
        }
    }

    private static void ValidateTemperature(double temperature)
    {
        if (!double.IsFinite(temperature) || temperature <= 0)
            throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be finite and positive.");
    }

    /// <summary>
    /// Golden-section search for the minimizer of <paramref name="objective"/> over [<paramref name="lo"/>, <paramref name="hi"/>].
    /// Temperature-scaling NLL is not guaranteed strictly unimodal for arbitrary logits, but is well-behaved in
    /// practice for this use (a single global-ish minimum); this is a dependency-free, deterministic, good-enough
    /// search rather than a claim of global optimality.
    /// </summary>
    private static double GoldenSectionMinimize(Func<double, double> objective, double lo, double hi, int iterations)
    {
        const double invPhi = 0.6180339887498949;
        var a = lo;
        var b = hi;
        var c = b - (b - a) * invPhi;
        var d = a + (b - a) * invPhi;
        var fc = objective(c);
        var fd = objective(d);

        for (var i = 0; i < iterations && b - a > 1e-9; i++)
        {
            if (fc < fd)
            {
                b = d;
                d = c;
                fd = fc;
                c = b - (b - a) * invPhi;
                fc = objective(c);
            }
            else
            {
                a = c;
                c = d;
                fc = fd;
                d = a + (b - a) * invPhi;
                fd = objective(d);
            }
        }

        return (a + b) / 2.0;
    }
}
