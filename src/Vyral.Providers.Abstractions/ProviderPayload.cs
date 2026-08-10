using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vyral.Providers.Abstractions;

public static class ProviderPayload
{
    public static string? GetString(JsonObject payload, string propertyName)
    {
        if (!payload.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return node.ToJsonString();
    }

    public static bool? GetBoolean(JsonObject payload, string propertyName)
    {
        if (!payload.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;
    }

    public static string RequiredString(JsonObject payload, string propertyName)
    {
        var value = GetString(payload, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Provider payload requires '{propertyName}'.");
        }

        return value;
    }

    public static JsonObject Clone(JsonObject payload)
    {
        return JsonSerializer.Deserialize<JsonObject>(payload.ToJsonString()) ?? new JsonObject();
    }
}
