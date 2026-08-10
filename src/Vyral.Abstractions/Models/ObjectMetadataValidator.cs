using System;
using System.Collections.Generic;
using System.Linq;

namespace Vyral.Abstractions.Models;

public static class ObjectMetadataValidator
{
    public const string ReservedPrefix = "vyral_";

    public static void ValidateUserMetadata(Dictionary<string, string>? metadata)
    {
        if (metadata == null) return;

        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Object metadata keys are required.");
            }

            if (key.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Object metadata key '{key}' uses the reserved '{ReservedPrefix}' prefix.");
            }

            if (!IsPortableMetadataKey(key))
            {
                throw new InvalidOperationException($"Object metadata key '{key}' must start with a letter or '_' and contain only letters, digits, and '_'.");
            }

            if (value == null)
            {
                throw new InvalidOperationException($"Object metadata value for key '{key}' is required.");
            }
        }
    }

    private static bool IsPortableMetadataKey(string key)
    {
        return (char.IsLetter(key[0]) || key[0] == '_') &&
            key.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
