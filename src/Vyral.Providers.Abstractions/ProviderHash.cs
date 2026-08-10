using System.Security.Cryptography;
using System.Text;

namespace Vyral.Providers.Abstractions;

public static class ProviderHash
{
    public static string Sha256(string? text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
