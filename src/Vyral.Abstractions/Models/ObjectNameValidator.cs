using System;
using System.IO;
using System.Linq;

namespace Vyral.Abstractions.Models;

public static class ObjectNameValidator
{
    public static void ValidateContainer(string container)
    {
        if (string.IsNullOrWhiteSpace(container)) throw new ArgumentException("Container is required.", nameof(container));
        if (container.Length is < 3 or > 63)
        {
            throw new InvalidOperationException("Container must be between 3 and 63 characters.");
        }

        if (!char.IsLetterOrDigit(container[0]) || !char.IsLetterOrDigit(container[^1]))
        {
            throw new InvalidOperationException("Container must start and end with a letter or digit.");
        }

        if (!container.All(c => c is >= 'a' and <= 'z' || char.IsDigit(c) || c == '-'))
        {
            throw new InvalidOperationException("Container can only contain lowercase letters, digits, and '-'.");
        }

        if (container.Contains("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Container cannot contain consecutive '-' characters.");
        }
    }

    public static string NormalizeObjectKey(string key, bool allowTrailingSlash = false)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Object key is required.", nameof(key));

        var normalized = key.Replace('\\', '/').TrimStart('/');
        var validationPath = allowTrailingSlash ? normalized.TrimEnd('/') : normalized;
        if (Path.IsPathRooted(key) || validationPath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidOperationException("Object key must be relative and cannot contain traversal segments.");
        }

        return normalized;
    }
}
