using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vyral.Providers.Onnx;

/// <summary>
/// Operator attestation for a local calibration dataset. The digest proves which bytes were fit;
/// <see cref="LabelSource"/> records the operator's provenance assertion but cannot independently
/// prove that a human created the labels.
/// </summary>
public sealed class OnnxJudgeCalibrationDatasetManifest
{
    public const string SchemaV1 = "vyral.onnx-judge-calibration-dataset.v1";
    public const int MaxManifestBytes = 64 * 1024;
    public const long MaxDatasetBytes = 256L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [JsonRequired]
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = SchemaV1;

    [JsonRequired]
    [JsonPropertyName("datasetId")]
    public string DatasetId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("datasetVersion")]
    public string DatasetVersion { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("labelSource")]
    public string LabelSource { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("datasetSha256")]
    public string DatasetSha256 { get; set; } = string.Empty;

    public static OnnxJudgeCalibrationDatasetManifest LoadAndVerify(string manifestPath, string datasetPath)
    {
        _ = LoadAndVerifyBytes(manifestPath, datasetPath, out var manifest);
        return manifest;
    }

    /// <summary>
    /// Loads and verifies the manifest, then returns the exact dataset bytes covered by its digest.
    /// Consumers fitting a calibration should parse these bytes rather than reopen the path.
    /// </summary>
    public static byte[] LoadAndVerifyBytes(
        string manifestPath,
        string datasetPath,
        out OnnxJudgeCalibrationDatasetManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        var manifestBytes = OnnxJudgeBoundedFile.ReadAllBytes(manifestPath, MaxManifestBytes, "Calibration dataset manifest");
        RejectDuplicateProperties(manifestBytes, manifestPath);
        try
        {
            manifest = JsonSerializer.Deserialize<OnnxJudgeCalibrationDatasetManifest>(manifestBytes, JsonOptions)
                ?? throw new InvalidOperationException("Calibration dataset manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Calibration dataset manifest '{manifestPath}' is invalid JSON: {ex.Message}", ex);
        }

        var errors = manifest.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Calibration dataset manifest '{manifestPath}' is invalid: {string.Join("; ", errors)}");
        }

        var datasetBytes = OnnxJudgeBoundedFile.ReadAllBytes(datasetPath, MaxDatasetBytes, "Calibration dataset");
        var actual = "sha256:" + Convert.ToHexString(SHA256.HashData(datasetBytes)).ToLowerInvariant();
        if (!string.Equals(actual, manifest.DatasetSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Calibration dataset digest does not match manifest '{manifestPath}'.");
        }

        return datasetBytes;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!string.Equals(Schema, SchemaV1, StringComparison.Ordinal))
            errors.Add($"schema must be {SchemaV1}");
        RequireText(DatasetId, "datasetId", errors);
        RequireText(DatasetVersion, "datasetVersion", errors);
        if (!string.Equals(LabelSource, OnnxJudgeCalibration.HumanLabelSource, StringComparison.Ordinal))
            errors.Add("labelSource must be human");
        if (!IsSha256(DatasetSha256))
            errors.Add("datasetSha256 must be a lowercase sha256 digest");
        return errors;
    }

    private static void RejectDuplicateProperties(byte[] json, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Calibration dataset manifest '{path}' must be a JSON object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidOperationException($"Calibration dataset manifest '{path}' contains a duplicate '{property.Name}' property.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Calibration dataset manifest '{path}' is invalid JSON: {ex.Message}", ex);
        }
    }

    private static void RequireText(string? value, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > 256 || value.Any(char.IsControl))
            errors.Add($"{name} is required, must not exceed 256 characters, and cannot contain control characters");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static class OnnxJudgeBoundedFile
{
    public static byte[] ReadAllBytes(string path, long maxBytes, string name)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new MemoryStream(input.Length is > 0 and <= int.MaxValue ? (int)Math.Min(input.Length, maxBytes) : 0);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new InvalidOperationException($"{name} exceeds {maxBytes} bytes.");
                output.Write(buffer, 0, read);
            }

            if (total == 0)
                throw new InvalidOperationException($"{name} must contain between 1 and {maxBytes} bytes.");
            return output.ToArray();
        }
        catch (FileNotFoundException)
        {
            throw new FileNotFoundException($"{name} was not found.", path);
        }
    }
}
