using System.Security.Cryptography;
using Vyral.Providers.Abstractions;

namespace Vyral.Server;

public sealed class ProviderMeteringOptions
{
    public string? SigningKeyPath { get; init; }
    public string? Issuer { get; init; }
    public string? KeyId { get; init; }

    public static ProviderMeteringOptions FromConfiguration(IConfiguration configuration) => new()
    {
        SigningKeyPath = EmptyToNull(configuration["Providers:Metering:SigningKeyPath"]),
        Issuer = EmptyToNull(configuration["Providers:Metering:Issuer"]),
        KeyId = EmptyToNull(configuration["Providers:Metering:KeyId"])
    };

    public EcdsaAiMeteringReceiptSigner? CreateSigner()
    {
        if (SigningKeyPath is null)
        {
            if (Issuer is not null || KeyId is not null)
            {
                throw new InvalidOperationException("Providers:Metering:SigningKeyPath is required when a metering issuer or key id is configured.");
            }
            return null;
        }
        if (Issuer is null || KeyId is null)
        {
            throw new InvalidOperationException("Providers:Metering:Issuer and Providers:Metering:KeyId are required with a metering signing key.");
        }

        var fullPath = Path.GetFullPath(SigningKeyPath);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"Provider metering signing key was not found at '{fullPath}'.");
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(fullPath));
            if (!AiMeteringCryptography.IsP256(key))
            {
                throw new InvalidOperationException("Provider metering signing key must use the NIST P-256 curve.");
            }
            ECParameters privateParameters;
            try
            {
                privateParameters = key.ExportParameters(includePrivateParameters: true);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException("Provider metering signing key must contain private key material.", ex);
            }
            if (privateParameters.D is not { Length: > 0 })
            {
                throw new InvalidOperationException("Provider metering signing key must contain private key material.");
            }
            return new EcdsaAiMeteringReceiptSigner(key, Issuer, KeyId, ownsKey: true);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
