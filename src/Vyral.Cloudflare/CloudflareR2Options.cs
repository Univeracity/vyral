using System;

namespace Vyral.Cloudflare;

public sealed class CloudflareR2Options
{
    public string? AccountId { get; init; }
    public string? AccessKeyId { get; init; }
    public string? SecretAccessKey { get; init; }
    public string? ServiceUrl { get; init; }
    public string AuthenticationRegion { get; init; } = "auto";
    public bool ForcePathStyle { get; init; } = true;
    public bool DisablePutObjectPayloadSigning { get; init; } = true;
    public bool DisablePutObjectDefaultChecksumValidation { get; init; } = true;

    public string ResolveServiceUrl()
    {
        if (!string.IsNullOrWhiteSpace(ServiceUrl))
        {
            var trimmed = ServiceUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException("Cloudflare R2 service URL must be an absolute HTTP(S) URL.");
            }

            return trimmed;
        }

        if (string.IsNullOrWhiteSpace(AccountId))
        {
            throw new InvalidOperationException("Cloudflare R2 object store requires Cloudflare:R2:AccountId or VYRAL_CLOUDFLARE_ACCOUNT_ID when no service URL is configured.");
        }

        return $"https://{AccountId.Trim()}.r2.cloudflarestorage.com";
    }

    public void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(AccessKeyId))
        {
            throw new InvalidOperationException("Cloudflare R2 object store requires Cloudflare:R2:AccessKeyId or VYRAL_R2_ACCESS_KEY_ID.");
        }

        if (string.IsNullOrWhiteSpace(SecretAccessKey))
        {
            throw new InvalidOperationException("Cloudflare R2 object store requires Cloudflare:R2:SecretAccessKey or VYRAL_R2_SECRET_ACCESS_KEY.");
        }
    }
}
