using System;
using Xunit;

namespace Vyral.Tests.Cloudflare;

public static class CloudflareR2LiveSettings
{
    public static string? AccountId =>
        Environment.GetEnvironmentVariable("VYRAL_CLOUDFLARE_ACCOUNT_ID");

    public static string? AccessKeyId =>
        Environment.GetEnvironmentVariable("VYRAL_R2_ACCESS_KEY_ID");

    public static string? SecretAccessKey =>
        Environment.GetEnvironmentVariable("VYRAL_R2_SECRET_ACCESS_KEY");

    public static string? Bucket =>
        Environment.GetEnvironmentVariable("VYRAL_R2_BUCKET");

    public static bool IsR2Configured =>
        !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(AccessKeyId) &&
        !string.IsNullOrWhiteSpace(SecretAccessKey) &&
        !string.IsNullOrWhiteSpace(Bucket);

    public static string UniquePrefix(string basePrefix = "vyral-r2-test")
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return $"{basePrefix}-{suffix}";
    }
}

public class CloudflareR2LiveConformanceStatusTests
{
    [Fact]
    public void LiveConformance_IsOptInByEnvironment()
    {
        Assert.Equal(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_CLOUDFLARE_ACCOUNT_ID")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_R2_ACCESS_KEY_ID")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_R2_SECRET_ACCESS_KEY")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_R2_BUCKET")),
            CloudflareR2LiveSettings.IsR2Configured);
    }
}

public sealed class CloudflareR2LiveFactAttribute : FactAttribute
{
    public CloudflareR2LiveFactAttribute()
    {
        if (!CloudflareR2LiveSettings.IsR2Configured)
        {
            Skip = "Set VYRAL_CLOUDFLARE_ACCOUNT_ID, VYRAL_R2_ACCESS_KEY_ID, VYRAL_R2_SECRET_ACCESS_KEY, and VYRAL_R2_BUCKET to run R2 live tests.";
        }
    }
}
