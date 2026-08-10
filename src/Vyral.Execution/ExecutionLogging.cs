using System.Text.Json.Nodes;

namespace Vyral.Execution;

public sealed class ExecutionLogRecord
{
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "info";
    public string? Layer { get; set; }
    public string? Operation { get; set; }
    public string? StepId { get; set; }
    public JsonObject? Details { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.Ordinal);
}

public static class ExecutionRunContextLoggingExtensions
{
    public static Task LogAsync(
        this IExecutionRunContext context,
        string message,
        string severity = "info",
        string? layer = null,
        string? operation = null,
        string? stepId = null,
        JsonObject? details = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var record = new ExecutionLogRecord
        {
            Message = message ?? string.Empty,
            Severity = string.IsNullOrWhiteSpace(severity) ? "info" : severity.Trim(),
            Layer = Normalize(layer),
            Operation = Normalize(operation),
            StepId = Normalize(stepId),
            Details = CloneObject(details),
            Attributes = attributes is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(attributes, StringComparer.Ordinal)
        };

        return context.RecordEventAsync(
            ExecutionEventTypes.Log,
            record.Message,
            record.Severity,
            ToDetails(record),
            ct);
    }

    public static Task LogInfoAsync(
        this IExecutionRunContext context,
        string message,
        string? layer = null,
        string? operation = null,
        string? stepId = null,
        JsonObject? details = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        CancellationToken ct = default)
    {
        return context.LogAsync(message, "info", layer, operation, stepId, details, attributes, ct);
    }

    public static Task LogWarningAsync(
        this IExecutionRunContext context,
        string message,
        string? layer = null,
        string? operation = null,
        string? stepId = null,
        JsonObject? details = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        CancellationToken ct = default)
    {
        return context.LogAsync(message, "warning", layer, operation, stepId, details, attributes, ct);
    }

    private static JsonObject ToDetails(ExecutionLogRecord record)
    {
        var payload = new JsonObject();
        if (!string.IsNullOrWhiteSpace(record.Layer))
        {
            payload["layer"] = record.Layer;
        }

        if (!string.IsNullOrWhiteSpace(record.Operation))
        {
            payload["operation"] = record.Operation;
        }

        if (!string.IsNullOrWhiteSpace(record.StepId))
        {
            payload["stepId"] = record.StepId;
        }

        if (record.Attributes.Count > 0)
        {
            payload["attributes"] = new JsonObject(record.Attributes
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => KeyValuePair.Create<string, JsonNode?>(item.Key, item.Value))
                .ToArray());
        }

        if (record.Details is not null)
        {
            payload["details"] = record.Details.DeepClone();
        }

        return payload;
    }

    private static JsonObject? CloneObject(JsonObject? value)
    {
        return value is null ? null : JsonNode.Parse(value.ToJsonString(ExecutionJson.Options)) as JsonObject;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
