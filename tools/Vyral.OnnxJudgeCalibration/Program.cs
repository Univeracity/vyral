using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Vyral.Embeddings.Onnx;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Onnx;

// The calibration harness for the ONNX NLI judge target (see OnnxJudgeCalibrationFitter's doc
// comment): run the classifier over a labeled set of (context, options, correctId) examples using
// the exact production hypothesis template, collect entailment-class logits per option, fit one
// temperature by minimizing NLL, and save the result next to the model at the path
// OnnxNliJudgeProviderOptions.CalibrationPath expects. This is not synthetic — it exercises the real
// ONNX model.
//
// Usage:
//   dotnet run --project tools/Vyral.OnnxJudgeCalibration -- \
//     <examples.jsonl> <modelDir> <vocabPath> <entailmentIndex> <outputCalibrationPath> <modelId> [hypothesisTemplate] [datasetManifestPath]

if (args.Length is < 6 or > 8)
{
    Console.Error.WriteLine("Usage: <examples.jsonl> <modelDir> <vocabPath> <entailmentIndex> <outputCalibrationPath> <modelId> [hypothesisTemplate] [datasetManifestPath]");
    return 1;
}

var examplesPath = args[0];
var modelDir = args[1];
var vocabPath = args[2];
if (!int.TryParse(args[3], out var entailmentIndex) || entailmentIndex < 0)
{
    Console.Error.WriteLine("entailmentIndex must be a non-negative integer confirmed from the checkpoint's class map.");
    return 1;
}
var outputPath = args[4];
var modelId = args[5];
var hypothesisTemplate = args.Length > 6 ? args[6] : "This example is about: {label}.";
var manifestPath = args.Length > 7 ? args[7] : examplesPath + ".manifest.json";

if (string.IsNullOrWhiteSpace(modelId) ||
    !string.Equals(modelId, modelId.Trim(), StringComparison.Ordinal) ||
    modelId.Length > 256 || modelId.Any(char.IsControl))
{
    Console.Error.WriteLine("modelId is required, cannot exceed 256 characters, and cannot contain surrounding whitespace or control characters.");
    return 1;
}

if (string.IsNullOrWhiteSpace(hypothesisTemplate) ||
    !hypothesisTemplate.Contains("{label}", StringComparison.Ordinal) ||
    hypothesisTemplate.Contains("{prompt}", StringComparison.Ordinal))
{
    Console.Error.WriteLine("hypothesisTemplate must contain {label} and cannot contain {prompt}; this dataset schema has no per-example prompt field.");
    return 1;
}

OnnxJudgeCalibrationDatasetManifest manifest;
byte[] exampleBytes;
try
{
    exampleBytes = OnnxJudgeCalibrationDatasetManifest.LoadAndVerifyBytes(
        manifestPath,
        examplesPath,
        out manifest);
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or DecoderFallbackException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
};
List<CalibrationExample> examples;
try
{
    examples = LoadExamples(exampleBytes, jsonOptions);
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or DecoderFallbackException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

Console.WriteLine($"Loaded {examples.Count} human-labeled examples from {examplesPath} ({manifest.DatasetSha256}).");

using var classifier = new OnnxNliClassifier(new OnnxNliClassifierOptions
{
    ModelPath = modelDir,
    VocabPath = string.IsNullOrWhiteSpace(vocabPath) ? null : vocabPath,
    ExecutionProvider = "cpu",
    MaxTokens = 512,
    BatchSize = 8
});

var labeled = new List<OnnxJudgeCalibrationFitter.LabeledExample>(examples.Count);
var correctRaw = 0;

for (var i = 0; i < examples.Count; i++)
{
    var example = examples[i];
    var hypotheses = example.Options
        .Select(option => new OnnxNliHypothesis { Id = option.Id, Text = hypothesisTemplate.Replace("{label}", option.Label) })
        .ToList();

    var classifications = await classifier.ClassifyAsync(example.Context, hypotheses);
    if (entailmentIndex < 0 || entailmentIndex >= classifications[0].Logits.Length)
    {
        Console.Error.WriteLine($"entailmentIndex {entailmentIndex} out of range for a {classifications[0].Logits.Length}-class model.");
        return 1;
    }

    var entailmentLogits = classifications.OrderBy(c => c.OriginalIndex).Select(c => c.Logits[entailmentIndex]).ToArray();
    var correctPosition = example.Options.FindIndex(o => o.Id == example.CorrectId);
    if (correctPosition < 0)
    {
        Console.Error.WriteLine($"Example {i}: correctId '{example.CorrectId}' not found among options.");
        return 1;
    }

    var probabilities = OnnxJudgeCalibrationFitter.Softmax(entailmentLogits);
    var predictedPosition = Array.IndexOf(probabilities, probabilities.Max());
    if (predictedPosition == correctPosition)
    {
        correctRaw++;
    }

    labeled.Add(new OnnxJudgeCalibrationFitter.LabeledExample(entailmentLogits, correctPosition));
    Console.WriteLine($"[{i + 1}/{examples.Count}] correct={example.CorrectId} predicted={example.Options[predictedPosition].Id} p={probabilities[predictedPosition]:F3} {(predictedPosition == correctPosition ? "OK" : "MISS")}");
}

Console.WriteLine($"Raw accuracy: {correctRaw}/{examples.Count} = {(double)correctRaw / examples.Count:P1}");

var nllAtOne = OnnxJudgeCalibrationFitter.NegativeLogLikelihood(labeled, 1.0);
var calibration = OnnxJudgeCalibrationFitter.Fit(labeled, new OnnxJudgeCalibrationFitContext
{
    DatasetId = manifest.DatasetId,
    DatasetVersion = manifest.DatasetVersion,
    DatasetSha256 = manifest.DatasetSha256,
    LabelSource = manifest.LabelSource,
    ModelId = modelId,
    QuestionType = AiJudgeQuestionTypes.Choice,
    HypothesisTemplateSha256 = ProviderHash.Sha256(hypothesisTemplate),
    EntailmentIndex = entailmentIndex
});
Console.WriteLine($"NLL at T=1.0: {nllAtOne:F4}");
Console.WriteLine($"Fitted temperature: {calibration.Temperature:F4}, NLL at fitted T: {calibration.FittedNegativeLogLikelihood:F4}");

calibration.Save(outputPath);
Console.WriteLine($"Saved calibration to {outputPath}");
return 0;

static List<CalibrationExample> LoadExamples(byte[] utf8Bytes, JsonSerializerOptions options)
{
    const int maxExamples = 100_000;
    const int maxLineBytes = 2 * 1024 * 1024;
    var examples = new List<CalibrationExample>();
    var lineNumber = 0;
    using var input = new MemoryStream(utf8Bytes, writable: false);
    using var reader = new StreamReader(
        input,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        detectEncodingFromByteOrderMarks: true);
    while (reader.ReadLine() is { } line)
    {
        lineNumber++;
        if (string.IsNullOrWhiteSpace(line)) continue;
        if (Encoding.UTF8.GetByteCount(line) > maxLineBytes)
            throw new InvalidOperationException($"Calibration example line {lineNumber} exceeds {maxLineBytes} UTF-8 bytes.");
        if (examples.Count >= maxExamples)
            throw new InvalidOperationException($"Calibration dataset cannot contain more than {maxExamples} examples.");

        CalibrationExample example;
        try
        {
            RejectDuplicateProperties(line, lineNumber);
            example = JsonSerializer.Deserialize<CalibrationExample>(line, options)
                ?? throw new InvalidOperationException($"Calibration example line {lineNumber} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Calibration example line {lineNumber} is invalid JSON: {ex.Message}", ex);
        }
        ValidateExample(example, lineNumber);
        examples.Add(example);
    }

    if (examples.Count == 0) throw new InvalidOperationException("Calibration dataset requires at least one example.");
    return examples;
}

static void RejectDuplicateProperties(string json, int lineNumber)
{
    using var document = JsonDocument.Parse(json);
    Visit(document.RootElement);
    return;

    void Visit(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidOperationException(
                        $"Calibration example line {lineNumber} contains duplicate property '{property.Name}'.");
                Visit(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Visit(item);
        }
    }
}

static void ValidateExample(CalibrationExample example, int lineNumber)
{
    if (string.IsNullOrWhiteSpace(example.Context))
        throw new InvalidOperationException($"Calibration example line {lineNumber} requires context.");
    if (example.Options is null || example.Options.Count is < 2 or > 100)
        throw new InvalidOperationException($"Calibration example line {lineNumber} requires between 2 and 100 options.");
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var option in example.Options)
    {
        if (string.IsNullOrWhiteSpace(option.Id) || option.Id.Length > 160 || option.Id.Any(char.IsControl))
            throw new InvalidOperationException($"Calibration example line {lineNumber} has an invalid option id.");
        if (string.IsNullOrWhiteSpace(option.Label) || option.Label.Length > 4096 || option.Label.Any(char.IsControl))
            throw new InvalidOperationException($"Calibration example line {lineNumber} has an invalid option label.");
        if (!ids.Add(option.Id))
            throw new InvalidOperationException($"Calibration example line {lineNumber} duplicates option id '{option.Id}'.");
    }
    if (string.IsNullOrWhiteSpace(example.CorrectId) || !ids.Contains(example.CorrectId))
        throw new InvalidOperationException($"Calibration example line {lineNumber} correctId must identify exactly one option.");
}

internal sealed class CalibrationExample
{
    [JsonRequired]
    [JsonPropertyName("context")]
    public string Context { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("options")]
    public List<CalibrationExampleOption> Options { get; set; } = new();

    [JsonRequired]
    [JsonPropertyName("correctId")]
    public string CorrectId { get; set; } = string.Empty;
}

internal sealed class CalibrationExampleOption
{
    [JsonRequired]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}
