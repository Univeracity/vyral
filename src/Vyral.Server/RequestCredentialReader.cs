using System.Security.Cryptography;
using System.Text;

namespace Vyral.Server;

internal static class RequestCredentialReader
{
    internal static string? ReadApiKey(HttpRequest request, string headerName)
    {
        if (string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            return ParseBearer(request.Headers.Authorization.ToString());
        }

        var hasHeader = request.Headers.TryGetValue(headerName, out var headerValue);
        var hasAuthorization = request.Headers.TryGetValue("Authorization", out var authorizationValue);
        var suppliedHeader = hasHeader ? headerValue.ToString() : null;
        var suppliedBearer = hasAuthorization ? ParseBearer(authorizationValue.ToString()) : null;
        if (hasHeader && hasAuthorization)
        {
            return !string.IsNullOrWhiteSpace(suppliedHeader) &&
                suppliedBearer is not null &&
                SecureEquals(suppliedHeader, suppliedBearer)
                    ? suppliedHeader
                    : null;
        }

        return hasHeader ? suppliedHeader : suppliedBearer;
    }

    internal static string? ReadBearer(HttpRequest request, string alternateHeader)
    {
        var hasAlternate = request.Headers.TryGetValue(alternateHeader, out var alternateValue);
        var hasAuthorization = request.Headers.TryGetValue("Authorization", out var authorizationValue);
        var alternate = hasAlternate ? ParseBearer(alternateValue.ToString()) : null;
        var authorization = hasAuthorization ? ParseBearer(authorizationValue.ToString()) : null;
        if (hasAlternate && hasAuthorization)
        {
            return alternate is not null &&
                authorization is not null &&
                SecureEquals(alternate, authorization)
                    ? alternate
                    : null;
        }

        return hasAlternate ? alternate : authorization;
    }

    private static string? ParseBearer(string value)
    {
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = value[prefix.Length..].Trim();
        return token.Length > 0 ? token : null;
    }

    private static bool SecureEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
