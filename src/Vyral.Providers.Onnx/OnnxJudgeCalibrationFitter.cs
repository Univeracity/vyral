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

    public static OnnxJudgeCalibration Fit(
        IReadOnlyList<LabeledExample> examples,
        string? modelId = null,
        double minTemperature = 0.05,
        double maxTemperature = 10.0,
        int iterations = 100)
    {
        if (examples.Count == 0)
        {
            throw new ArgumentException("Calibration requires at least one labeled example.", nameof(examples));
        }

        foreach (var example in examples)
        {
            if (example.Logits.Length < 2)
            {
                throw new ArgumentException("Each labeled example requires logits for at least two options.", nameof(examples));
            }

            if (example.CorrectIndex < 0 || example.CorrectIndex >= example.Logits.Length)
            {
                throw new ArgumentException("CorrectIndex must index into Logits.", nameof(examples));
            }
        }

        if (minTemperature <= 0 || maxTemperature <= minTemperature)
        {
            throw new ArgumentException("Temperature search range must satisfy 0 < minTemperature < maxTemperature.");
        }

        var temperature = GoldenSectionMinimize(t => NegativeLogLikelihood(examples, t), minTemperature, maxTemperature, iterations);

        return new OnnxJudgeCalibration
        {
            Temperature = temperature,
            ModelId = modelId,
            SampleCount = examples.Count,
            FittedNegativeLogLikelihood = NegativeLogLikelihood(examples, temperature),
            FittedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>Mean NLL of the correct option under softmax(logits / temperature) across all examples.</summary>
    public static double NegativeLogLikelihood(IReadOnlyList<LabeledExample> examples, double temperature)
    {
        var total = 0.0;
        foreach (var example in examples)
        {
            var probabilities = Softmax(example.Logits, temperature);
            var probability = Math.Max(probabilities[example.CorrectIndex], 1e-12);
            total -= Math.Log(probability);
        }

        return total / examples.Count;
    }

    public static double[] Softmax(IReadOnlyList<double> logits, double temperature = 1.0)
    {
        var scaled = logits.Select(logit => logit / temperature).ToArray();
        var max = scaled.Max();
        var exponentials = scaled.Select(value => Math.Exp(value - max)).ToArray();
        var sum = exponentials.Sum();
        return exponentials.Select(value => value / sum).ToArray();
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
