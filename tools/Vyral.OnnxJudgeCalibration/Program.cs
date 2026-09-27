using System.Text.Json;
using System.Text.Json.Serialization;
using Vyral.Embeddings.Onnx;
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
//     <examples.jsonl> <modelDir> <vocabPath> <entailmentIndex> <outputCalibrationPath> [modelId] [hypothesisTemplate]

if (args.Length < 5)
{
    Console.Error.WriteLine("Usage: <examples.jsonl> <modelDir> <vocabPath> <entailmentIndex> <outputCalibrationPath> [modelId] [hypothesisTemplate]");
    return 1;
}

var examplesPath = args[0];
var modelDir = args[1];
var vocabPath = args[2];
var entailmentIndex = int.Parse(args[3]);
var outputPath = args[4];
var modelId = args.Length > 5 ? args[5] : null;
var hypothesisTemplate = args.Length > 6 ? args[6] : "This example is about: {label}.";

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var examples = File.ReadLines(examplesPath)
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .Select(line => JsonSerializer.Deserialize<CalibrationExample>(line, jsonOptions) ?? throw new InvalidOperationException($"Could not parse line: {line}"))
    .ToList();

Console.WriteLine($"Loaded {examples.Count} labeled examples from {examplesPath}.");

using var classifier = new OnnxNliClassifier(new OnnxNliClassifierOptions
{
    ModelPath = modelDir,
    VocabPath = string.IsNullOrWhiteSpace(vocabPath) ? null : vocabPath,
    ExecutionProvider = "cpu",
    MaxTokens = 256,
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
var calibration = OnnxJudgeCalibrationFitter.Fit(labeled, modelId);
Console.WriteLine($"NLL at T=1.0: {nllAtOne:F4}");
Console.WriteLine($"Fitted temperature: {calibration.Temperature:F4}, NLL at fitted T: {calibration.FittedNegativeLogLikelihood:F4}");

calibration.Save(outputPath);
Console.WriteLine($"Saved calibration to {outputPath}");
return 0;

internal sealed class CalibrationExample
{
    [JsonPropertyName("context")]
    public string Context { get; set; } = string.Empty;

    [JsonPropertyName("options")]
    public List<CalibrationExampleOption> Options { get; set; } = new();

    [JsonPropertyName("correctId")]
    public string CorrectId { get; set; } = string.Empty;
}

internal sealed class CalibrationExampleOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}
