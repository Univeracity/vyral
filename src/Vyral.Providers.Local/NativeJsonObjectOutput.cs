using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Local;

public static class LlamaCppChatOutputFormats
{
    public const string Text = "text";
    public const string JsonObjectV1 = "json-object-v1";
}

internal static class NativeJsonObjectOutput
{
    // Fixed owner policy, never supplied by a prompt or caller schema. Native
    // grammar acceptance/enforcement must also be qualified at the pinned build.
    internal const string Grammar = """
        root ::= ws object ws
        object ::= "{" ws (string ws ":" ws value (ws "," ws string ws ":" ws value)*)? ws "}"
        array ::= "[" ws (value (ws "," ws value)*)? ws "]"
        value ::= object | array | string | number | "true" | "false" | "null"
        string ::= "\"" ([^"\\\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F]{4}))* "\""
        number ::= "-"? ("0" | [1-9] [0-9]*) ("." [0-9]+)? ([eE] [+-]? [0-9]+)?
        ws ::= [ \t\n\r]*
        """;
    internal static string PolicySha256 { get; } = ProviderHash.Sha256(
        $"native-json-object-v1|depth:64|unique-properties|finite-numbers|strict-unicode|exact-echo|{Grammar}");

    internal static bool Confirmed(JsonObject response) =>
        response["generation_settings"] is JsonObject settings &&
        settings["grammar"] is JsonValue grammar && grammar.TryGetValue<string>(out var text) && text == Grammar &&
        settings["grammar_lazy"] is JsonValue lazy && lazy.TryGetValue<bool>(out var isLazy) && !isLazy &&
        settings["samplers"] is JsonArray { Count: 0 } &&
        settings["backend_sampling"] is JsonValue backend && backend.TryGetValue<bool>(out var isBackend) && !isBackend;

    internal static bool IsCompleteObject(string text) => RejectionReason(text) is null;

    // Closed non-content diagnostics. Never expose parser messages or property names.
    internal static string? RejectionReason(string text)
    {
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            using var document = JsonDocument.Parse(utf8.GetBytes(text), new JsonDocumentOptions { MaxDepth = 64 });
            return document.RootElement.ValueKind != JsonValueKind.Object
                ? "non_object_root" : Validate(document.RootElement, utf8);
        }
        catch (EncoderFallbackException) { return "invalid_unicode"; }
        catch (JsonException) { return "parse_or_depth_invalid"; }
        catch (ArgumentException) { return "invalid_unicode"; }
        catch (InvalidOperationException) { return "invalid_unicode"; }
    }

    private static string? Validate(JsonElement value, UTF8Encoding utf8)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    utf8.GetByteCount(property.Name);
                    if (!names.Add(property.Name)) return "duplicate_property";
                    var reason = Validate(property.Value, utf8);
                    if (reason is not null) return reason;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    var reason = Validate(item, utf8);
                    if (reason is not null) return reason;
                }
                return null;
            case JsonValueKind.String:
                utf8.GetByteCount(value.GetString()!); return null;
            case JsonValueKind.Number:
                return value.TryGetDouble(out var number) && double.IsFinite(number) ? null : "nonfinite_number";
            default:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
                    ? null : "parse_or_depth_invalid";
        }
    }
}
