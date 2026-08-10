using Microsoft.Extensions.Configuration;

namespace Vyral.Server;

/// <summary>
/// Shared-service abuse boundary for CanonicalStore HTTP routes. It is intentionally independent
/// of any identity provider and limits source-address request volume before a storage transaction
/// is opened.
/// </summary>
public sealed class CanonicalRateLimitOptions
{
    public int PermitLimit { get; init; } = 120;
    public int WindowSeconds { get; init; } = 60;
    public int QueueLimit { get; init; }

    public static CanonicalRateLimitOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new CanonicalRateLimitOptions
        {
            PermitLimit = ParsePositive(configuration["Server:CanonicalRateLimit:PermitLimit"], 120, "Server:CanonicalRateLimit:PermitLimit"),
            WindowSeconds = ParsePositive(configuration["Server:CanonicalRateLimit:WindowSeconds"], 60, "Server:CanonicalRateLimit:WindowSeconds"),
            QueueLimit = ParseNonNegative(configuration["Server:CanonicalRateLimit:QueueLimit"], 0, "Server:CanonicalRateLimit:QueueLimit")
        };
        if (options.PermitLimit > 100_000) throw new InvalidOperationException("Server:CanonicalRateLimit:PermitLimit cannot exceed 100000.");
        if (options.WindowSeconds > 86_400) throw new InvalidOperationException("Server:CanonicalRateLimit:WindowSeconds cannot exceed 86400.");
        if (options.QueueLimit > 10_000) throw new InvalidOperationException("Server:CanonicalRateLimit:QueueLimit cannot exceed 10000.");
        return options;
    }

    private static int ParsePositive(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed <= 0) throw new InvalidOperationException($"{name} must be a positive integer.");
        return parsed;
    }

    private static int ParseNonNegative(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed < 0) throw new InvalidOperationException($"{name} must be a non-negative integer.");
        return parsed;
    }
}
