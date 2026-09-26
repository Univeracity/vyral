using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vyral.Providers.Onnx;

/// <summary>
/// A fitted temperature-scaling calibration for one NLI checkpoint: entailment logits are divided
/// by <see cref="Temperature"/> before softmax. Temperature scaling (Guo et al., "On Calibration of
/// Modern Neural Networks", 2017) is a one-parameter, monotonic rescaling — it cannot fix a model
/// that is wrong, only how confidently it reports being right or wrong. It is deliberately the
/// smallest honest thing to do here: fine-tuning the NLI model itself would need a training
/// framework, labeled corpora, and GPU budget this repo does not carry, and would stop being a
/// "local-first, no-download-by-default" adapter. See <see cref="OnnxJudgeCalibrationFitter"/> for
/// how a temperature is fit from labeled examples.
/// </summary>
public sealed class OnnxJudgeCalibration
{
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 1.0;

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("sampleCount")]
    public int SampleCount { get; set; }

    [JsonPropertyName("fittedNegativeLogLikelihood")]
    public double? FittedNegativeLogLikelihood { get; set; }

    [JsonPropertyName("fittedAtUtc")]
    public DateTime? FittedAtUtc { get; set; }

    /// <summary>True only when this calibration came from a fitted temperature, not the identity default.</summary>
    [JsonIgnore]
    public bool IsFitted => SampleCount > 0 && FittedAtUtc.HasValue;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static OnnxJudgeCalibration Identity(string? modelId = null) => new() { Temperature = 1.0, ModelId = modelId };

    public double ApplyToLogit(double logit) => logit / Temperature;

    public double[] ApplyToLogits(IReadOnlyList<double> logits)
    {
        var scaled = new double[logits.Count];
        for (var i = 0; i < logits.Count; i++)
        {
            scaled[i] = logits[i] / Temperature;
        }

        return scaled;
    }

    public static OnnxJudgeCalibration? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        var calibration = JsonSerializer.Deserialize<OnnxJudgeCalibration>(json, JsonOptions);
        if (calibration is null || calibration.Temperature <= 0)
        {
            throw new InvalidOperationException($"Calibration file '{path}' does not contain a valid positive temperature.");
        }

        return calibration;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
