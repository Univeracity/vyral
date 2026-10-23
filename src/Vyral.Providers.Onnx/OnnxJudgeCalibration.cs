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
    public const string CurrentSchema = "vyral.onnx-judge-calibration.v2";
    public const string HumanLabelSource = "human";
    public const int MaxArtifactBytes = 64 * 1024;

    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

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

    [JsonPropertyName("datasetId")]
    public string? DatasetId { get; set; }

    [JsonPropertyName("datasetVersion")]
    public string? DatasetVersion { get; set; }

    [JsonPropertyName("datasetSha256")]
    public string? DatasetSha256 { get; set; }

    [JsonPropertyName("labelSource")]
    public string? LabelSource { get; set; }

    [JsonPropertyName("questionType")]
    public string? QuestionType { get; set; }

    [JsonPropertyName("hypothesisTemplateSha256")]
    public string? HypothesisTemplateSha256 { get; set; }

    [JsonPropertyName("entailmentIndex")]
    public int? EntailmentIndex { get; set; }

    /// <summary>
    /// True only for a complete v2 fit that carries an explicit human-label attestation and is
    /// bound to a dataset, model, question type, hypothesis template, and entailment class.
    /// The attestation is operator-supplied provenance, not independent proof of how labels were
    /// created.
    /// </summary>
    [JsonIgnore]
    public bool IsFitted =>
        string.Equals(Schema, CurrentSchema, StringComparison.Ordinal) &&
        string.Equals(LabelSource, HumanLabelSource, StringComparison.Ordinal) &&
        IsRequiredText(DatasetId) &&
        IsRequiredText(DatasetVersion) &&
        IsSha256(DatasetSha256) &&
        IsRequiredText(ModelId) &&
        IsKnownQuestionType(QuestionType) &&
        IsSha256(HypothesisTemplateSha256) &&
        EntailmentIndex >= 0 &&
        SampleCount > 0 &&
        FittedNegativeLogLikelihood is >= 0 and < double.PositiveInfinity &&
        FittedAtUtc is { Kind: DateTimeKind.Utc } &&
        double.IsFinite(Temperature) && Temperature > 0;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static OnnxJudgeCalibration Identity(string? modelId = null) => new() { Temperature = 1.0, ModelId = modelId };

    public bool IsApplicableTo(string questionType, string hypothesisTemplate, int entailmentIndex, string? modelId)
    {
        if (!IsFitted ||
            !string.Equals(QuestionType, questionType, StringComparison.Ordinal) ||
            !string.Equals(ModelId, modelId, StringComparison.Ordinal) ||
            EntailmentIndex != entailmentIndex)
        {
            return false;
        }

        return string.Equals(
            HypothesisTemplateSha256,
            Vyral.Providers.Abstractions.ProviderHash.Sha256(hypothesisTemplate),
            StringComparison.Ordinal);
    }

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

        var json = OnnxJudgeBoundedFile.ReadAllBytes(path, MaxArtifactBytes, $"Calibration file '{path}'");
        OnnxJudgeCalibration? calibration;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Calibration file '{path}' must be a JSON object.");
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                    throw new InvalidOperationException($"Calibration file '{path}' contains duplicate property '{property.Name}'.");
            }
            calibration = JsonSerializer.Deserialize<OnnxJudgeCalibration>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Calibration file '{path}' is invalid JSON: {ex.Message}", ex);
        }
        if (calibration is null || !double.IsFinite(calibration.Temperature) || calibration.Temperature <= 0)
        {
            throw new InvalidOperationException($"Calibration file '{path}' does not contain a valid positive temperature.");
        }

        if (calibration.SampleCount < 0 ||
            calibration.FittedNegativeLogLikelihood is < 0 or double.PositiveInfinity ||
            calibration.EntailmentIndex < 0)
        {
            throw new InvalidOperationException($"Calibration file '{path}' contains invalid fitted values.");
        }

        var claimsCurrentSchema = string.Equals(calibration.Schema, CurrentSchema, StringComparison.Ordinal);
        var carriesProvenance = calibration.Schema is not null ||
            calibration.DatasetId is not null || calibration.DatasetVersion is not null ||
            calibration.DatasetSha256 is not null || calibration.LabelSource is not null ||
            calibration.QuestionType is not null || calibration.HypothesisTemplateSha256 is not null ||
            calibration.EntailmentIndex is not null;
        if ((claimsCurrentSchema || carriesProvenance) && !calibration.IsFitted)
        {
            throw new InvalidOperationException(
                $"Calibration file '{path}' has incomplete or unsupported provenance and cannot be reported as fitted.");
        }

        return calibration;
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.Equals(Schema, CurrentSchema, StringComparison.Ordinal) && !IsFitted)
        {
            throw new InvalidOperationException("A v2 ONNX judge calibration must contain a complete human-labeled fit closure before it can be saved.");
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        if (json.Length > MaxArtifactBytes)
            throw new InvalidOperationException($"Calibration artifact exceeds {MaxArtifactBytes} UTF-8 bytes.");

        var targetPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, json);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool IsRequiredText(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.Length <= 256 && !value.Any(char.IsControl);

    private static bool IsSha256(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsKnownQuestionType(string? value) => value is
        Vyral.Providers.Abstractions.AiJudgeQuestionTypes.Choice or
        Vyral.Providers.Abstractions.AiJudgeQuestionTypes.Noul or
        Vyral.Providers.Abstractions.AiJudgeQuestionTypes.Score;
}

/// <summary>Complete closure required before a fitted calibration may report calibrated output.</summary>
public sealed class OnnxJudgeCalibrationFitContext
{
    public string DatasetId { get; init; } = string.Empty;
    public string DatasetVersion { get; init; } = string.Empty;
    public string DatasetSha256 { get; init; } = string.Empty;
    public string LabelSource { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string QuestionType { get; init; } = string.Empty;
    public string HypothesisTemplateSha256 { get; init; } = string.Empty;
    public int EntailmentIndex { get; init; }
}
