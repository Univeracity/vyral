using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vyral.Abstractions.Models;

public static class FilterValueNormalizer
{
    /// <summary>
    /// Walks a filter tree and validates every leaf node's operator and value shape.
    /// Throws <see cref="NotSupportedException"/> on the first invalid node.
    /// Safe to call before passing a filter to any store backend.
    /// </summary>
    public static void ValidateFilter(FilterNode? node)
    {
        if (node is null)
        {
            return;
        }

        if (node.Children != null && !string.IsNullOrWhiteSpace(node.Combine))
        {
            foreach (var child in node.Children)
            {
                ValidateFilter(child);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(node.Path))
        {
            ValidateLeafValue(node.Op ?? FilterOps.Eq, node.Value);
        }
    }

    /// <summary>
    /// Validates a single leaf operator/value pair. Throws <see cref="NotSupportedException"/>
    /// if the operator is unrecognized, the value shape is unsupported for that operator,
    /// or null is used with an operator that does not accept null.
    /// </summary>
    public static void ValidateLeafValue(string op, object? value)
    {
        var normalizedOp = NormalizeOperator(op);
        if (normalizedOp == FilterOps.In)
        {
            _ = NormalizeScalarList(value);
            return;
        }

        if (normalizedOp == FilterOps.Exists)
        {
            _ = NormalizeExistsValue(value);
            return;
        }

        var normalizedValue = NormalizeScalar(value);
        if (normalizedOp is FilterOps.Contains or FilterOps.StartsWith && normalizedValue is not string)
        {
            throw new NotSupportedException($"Filter operator '{op}' requires a string value.");
        }

        if (normalizedValue == null && normalizedOp is not FilterOps.Eq and not FilterOps.Neq and not FilterOps.Exists)
        {
            throw new NotSupportedException($"Operator '{op}' cannot be used with null values.");
        }
    }

    /// <summary>
    /// Returns the canonical <see cref="FilterOps"/> value for the given operator string.
    /// Trims input and accepts operator names case-insensitively; both <c>"startswith"</c>
    /// and <c>"startsWith"</c> return <see cref="FilterOps.StartsWith"/>.
    /// Null or whitespace returns <see cref="FilterOps.Eq"/>.
    /// Throws <see cref="NotSupportedException"/> for unrecognized operators.
    /// </summary>
    public static string NormalizeOperator(string? op)
    {
        var normalizedOp = string.IsNullOrWhiteSpace(op)
            ? FilterOps.Eq
            : op.Trim().ToLowerInvariant();

        return normalizedOp switch
        {
            FilterOps.Eq => FilterOps.Eq,
            FilterOps.Neq => FilterOps.Neq,
            FilterOps.In => FilterOps.In,
            FilterOps.Gt => FilterOps.Gt,
            FilterOps.Gte => FilterOps.Gte,
            FilterOps.Lt => FilterOps.Lt,
            FilterOps.Lte => FilterOps.Lte,
            FilterOps.Exists => FilterOps.Exists,
            FilterOps.Contains => FilterOps.Contains,
            "startswith" => FilterOps.StartsWith,
            _ => throw new NotSupportedException($"Filter operator '{op}' is not supported.")
        };
    }

    /// <summary>
    /// Returns supported scalar CLR values unchanged: string, bool, null, and built-in numeric types.
    /// Unwraps <see cref="System.Text.Json.JsonElement"/> and <see cref="System.Text.Json.Nodes.JsonValue"/>
    /// scalars to CLR primitives. JsonElement numbers become long when possible, otherwise double.
    /// Throws <see cref="NotSupportedException"/> for objects, non-In arrays, or unsupported types.
    /// </summary>
    public static object? NormalizeScalar(object? value)
    {
        if (value is JsonElement element)
        {
            return NormalizeJsonElement(element);
        }

        if (value is JsonValue jsonValue)
        {
            return NormalizeJsonValue(jsonValue);
        }

        if (value is JsonNode)
        {
            throw new NotSupportedException("Filter values must be scalar JSON values. Use the 'in' operator for scalar arrays.");
        }

        if (value is null || value is string || value is bool || IsNumber(value))
        {
            return value;
        }

        if (value is IEnumerable)
        {
            throw new NotSupportedException("Filter values must be scalar JSON values. Use the 'in' operator for scalar arrays.");
        }

        throw new NotSupportedException($"Filter value type '{value.GetType().Name}' is not supported.");
    }

    /// <summary>
    /// Normalizes an array value for the <see cref="FilterOps.In"/> operator into a list of supported
    /// scalar values. Accepts non-string <see cref="System.Collections.IEnumerable"/>,
    /// <see cref="System.Text.Json.JsonElement"/> arrays, and <see cref="System.Text.Json.Nodes.JsonArray"/>.
    /// Each element is normalized via <see cref="NormalizeScalar"/>.
    /// Throws <see cref="NotSupportedException"/> if the value is not an array or contains non-scalar elements.
    /// </summary>
    public static IReadOnlyList<object?> NormalizeScalarList(object? value)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new NotSupportedException("The 'in' operator requires an array value.");
            }

            return element.EnumerateArray()
                .Select(NormalizeJsonElement)
                .ToList();
        }

        if (value is JsonArray jsonArray)
        {
            return jsonArray
                .Select(NormalizeScalar)
                .ToList();
        }

        if (value is JsonNode)
        {
            throw new NotSupportedException("The 'in' operator requires an array value.");
        }

        if (value is string)
        {
            throw new NotSupportedException("The 'in' operator requires an array value.");
        }

        if (value is IEnumerable enumerable)
        {
            var result = new List<object?>();
            foreach (var item in enumerable)
            {
                result.Add(NormalizeScalar(item));
            }

            return result;
        }

        throw new NotSupportedException("The 'in' operator requires an array value.");
    }

    /// <summary>
    /// Normalizes the value for <see cref="FilterOps.Exists"/>. Null or omitted values mean
    /// <c>true</c>, explicit bool values are respected, and all other scalar values are rejected.
    /// </summary>
    public static bool NormalizeExistsValue(object? value)
    {
        var normalized = NormalizeScalar(value);
        return normalized switch
        {
            null => true,
            bool boolean => boolean,
            _ => throw new NotSupportedException("Filter operator 'exists' requires a boolean value or null.")
        };
    }

    private static object? NormalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => throw new NotSupportedException("Filter values must be scalar JSON values. Use the 'in' operator for scalar arrays.")
        };
    }

    private static object? NormalizeJsonValue(JsonValue value)
    {
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<bool>(out var boolean)) return boolean;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<long>(out var longInteger)) return longInteger;
        if (value.TryGetValue<double>(out var doubleValue)) return doubleValue;
        if (value.TryGetValue<decimal>(out var decimalValue)) return decimalValue;
        if (value.TryGetValue<JsonElement>(out var element)) return NormalizeJsonElement(element);
        throw new NotSupportedException("Filter value type is not supported.");
    }

    private static bool IsNumber(object value)
    {
        return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }
}
