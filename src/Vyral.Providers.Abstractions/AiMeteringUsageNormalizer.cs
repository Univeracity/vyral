using System.Text.Json.Nodes;

namespace Vyral.Providers.Abstractions;

/// <summary>
/// Normalizes the common numeric portion of provider usage packets. Adapters call this on an
/// explicitly identified usage object; Vyral does not search arbitrary model output for counters.
/// </summary>
public static class AiMeteringUsageNormalizer
{
    public static IReadOnlyList<AiMeteringMeasurement> Normalize(
        JsonObject? usage,
        string source = AiMeteringSources.ProviderResponse,
        string quality = AiMeteringQualities.Reported,
        string? sourceId = null)
    {
        if (usage is null)
        {
            return Array.Empty<AiMeteringMeasurement>();
        }

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        Add(values, AiMeteringMeasurementNames.InputTokens, Read(usage, "input_tokens", "inputTokens", "prompt_tokens", "promptTokens"));
        Add(values, AiMeteringMeasurementNames.OutputTokens, Read(usage, "output_tokens", "outputTokens", "completion_tokens", "completionTokens"));
        Add(values, AiMeteringMeasurementNames.TotalTokens, Read(usage, "total_tokens", "totalTokens"));
        Add(values, AiMeteringMeasurementNames.CachedInputTokens, Read(usage, "cached_input_tokens", "cachedInputTokens", "cachedReadTokens"));
        Add(values, AiMeteringMeasurementNames.CacheWriteInputTokens, Read(usage, "cache_write_input_tokens", "cacheWriteInputTokens", "cacheCreationTokens"));
        Add(values, AiMeteringMeasurementNames.ReasoningOutputTokens, Read(usage, "reasoning_output_tokens", "reasoningOutputTokens", "reasoningTokens"));
        Add(values, AiMeteringMeasurementNames.ModelCalls, Read(usage, "model_calls", "modelCalls"));
        Add(values, AiMeteringMeasurementNames.Turns, Read(usage, "num_turns", "numTurns", "turns"));
        Add(values, AiMeteringMeasurementNames.ToolCalls, Read(usage, "tool_calls", "toolCalls"));

        if (usage["input_tokens_details"] is JsonObject inputDetails)
        {
            AddMax(values, AiMeteringMeasurementNames.CachedInputTokens, Read(inputDetails, "cached_tokens"));
        }
        if (usage["inputTokensDetails"] is JsonObject camelInputDetails)
        {
            AddMax(values, AiMeteringMeasurementNames.CachedInputTokens, Read(camelInputDetails, "cachedTokens"));
        }
        if (usage["output_tokens_details"] is JsonObject outputDetails)
        {
            AddMax(values, AiMeteringMeasurementNames.ReasoningOutputTokens, Read(outputDetails, "reasoning_tokens"));
        }
        if (usage["outputTokensDetails"] is JsonObject camelOutputDetails)
        {
            AddMax(values, AiMeteringMeasurementNames.ReasoningOutputTokens, Read(camelOutputDetails, "reasoningTokens"));
        }

        var result = values.Select(item => new AiMeteringMeasurement
        {
            Name = item.Key,
            Value = item.Value,
            Unit = item.Key.StartsWith("tokens.", StringComparison.Ordinal) ? AiMeteringUnits.Tokens : AiMeteringUnits.Count,
            Source = source,
            Quality = quality,
            SourceId = sourceId
        }).ToList();

        if (!values.ContainsKey(AiMeteringMeasurementNames.TotalTokens) &&
            values.TryGetValue(AiMeteringMeasurementNames.InputTokens, out var input) &&
            values.TryGetValue(AiMeteringMeasurementNames.OutputTokens, out var output) &&
            input <= AiMeteringValidator.MaxPortableInteger - output)
        {
            result.Add(new AiMeteringMeasurement
            {
                Name = AiMeteringMeasurementNames.TotalTokens,
                Value = input + output,
                Unit = AiMeteringUnits.Tokens,
                Source = AiMeteringSources.ConsumerInference,
                Quality = AiMeteringQualities.Estimated,
                SourceId = sourceId,
                Method = "sum of reported input and output token counts"
            });
        }

        return result;
    }

    public static void AppendTo(AiMeteringReceipt receipt, JsonObject? usage, string? sourceId = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Measurements.AddRange(Normalize(usage, sourceId: sourceId));
    }

    private static long? Read(JsonObject value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!value.TryGetPropertyValue(name, out var node) || node is not JsonValue scalar)
            {
                continue;
            }
            var raw = scalar.ToJsonString();
            if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var integer) &&
                integer >= 0 && integer <= AiMeteringValidator.MaxPortableInteger)
            {
                return integer;
            }
            if (decimal.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) &&
                number >= 0 && decimal.Truncate(number) == number && number <= AiMeteringValidator.MaxPortableInteger)
            {
                return (long)number;
            }
        }
        return null;
    }

    private static void Add(Dictionary<string, long> values, string name, long? value)
    {
        if (value.HasValue)
        {
            values[name] = value.Value;
        }
    }

    private static void AddMax(Dictionary<string, long> values, string name, long? value)
    {
        if (value.HasValue && (!values.TryGetValue(name, out var existing) || value.Value > existing))
        {
            values[name] = value.Value;
        }
    }
}
