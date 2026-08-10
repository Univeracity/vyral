using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Vyral.Server;

public sealed class ServerAccessOptions
{
    public const string DefaultApiKeyHeader = "X-Vyral-Api-Key";

    public bool RequireApiKey { get; init; }
    public string ApiKeyHeader { get; init; } = DefaultApiKeyHeader;
    public IReadOnlyList<string> ApiKeys { get; init; } = Array.Empty<string>();
    public IReadOnlySet<string> PublicPaths { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/health",
        "/readiness",
        "/openapi/vyral.json",
        "/contracts/schemas/vyral-public.schema.json"
    };

    public bool Enabled => RequireApiKey;

    public static ServerAccessOptions FromConfiguration(IConfiguration configuration)
    {
        var keys = configuration.GetSection("Server:ApiKeys")
            .GetChildren()
            .Select(section => section.Value)
            .Append(configuration["Server:ApiKey"])
            .Append(configuration["VYRAL_API_KEY"])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var requireApiKey = ParseBool(configuration["Server:RequireApiKey"]) ?? keys.Count > 0;
        if (requireApiKey && keys.Count == 0)
        {
            throw new InvalidOperationException("Server:RequireApiKey is true, but no Server:ApiKey or Server:ApiKeys values are configured.");
        }

        var apiKeyHeader = FirstNonEmpty(
            configuration["Server:ApiKeyHeader"],
            configuration["VYRAL_API_KEY_HEADER"],
            DefaultApiKeyHeader)!;

        return new ServerAccessOptions
        {
            RequireApiKey = requireApiKey,
            ApiKeyHeader = apiKeyHeader,
            ApiKeys = keys
        };
    }

    public bool RequiresAuthentication(HttpContext context)
    {
        if (!Enabled)
        {
            return false;
        }

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            return false;
        }

        return !PublicPaths.Contains(context.Request.Path.Value ?? string.Empty);
    }

    public bool IsAuthorized(HttpRequest request)
    {
        if (!Enabled)
        {
            return true;
        }

        var supplied = GetSuppliedApiKey(request);
        return !string.IsNullOrWhiteSpace(supplied) && ApiKeys.Any(key => SecureEquals(key, supplied));
    }

    private string? GetSuppliedApiKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue(ApiKeyHeader, out var headerValue) && !string.IsNullOrWhiteSpace(headerValue.ToString()))
        {
            return headerValue.ToString();
        }

        var authorization = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        return authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            ? authorization[bearer.Length..].Trim()
            : null;
    }

    private static bool SecureEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static bool? ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException("Server:RequireApiKey must be true or false.");
        }

        return parsed;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
