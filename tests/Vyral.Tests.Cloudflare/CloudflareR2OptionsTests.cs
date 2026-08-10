using System;
using Vyral.Cloudflare;
using Xunit;

namespace Vyral.Tests.Cloudflare;

public class CloudflareR2OptionsTests
{
    [Fact]
    public void ResolveServiceUrl_BuildsCloudflareEndpointFromAccountId()
    {
        var options = new CloudflareR2Options
        {
            AccountId = "abc123"
        };

        Assert.Equal("https://abc123.r2.cloudflarestorage.com", options.ResolveServiceUrl());
    }

    [Fact]
    public void ResolveServiceUrl_UsesConfiguredAbsoluteEndpoint()
    {
        var options = new CloudflareR2Options
        {
            ServiceUrl = "https://example.test/custom/"
        };

        Assert.Equal("https://example.test/custom", options.ResolveServiceUrl());
    }

    [Fact]
    public void ValidateCredentials_RequiresAccessKeyAndSecret()
    {
        var options = new CloudflareR2Options
        {
            AccountId = "abc123"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.ValidateCredentials());
        Assert.Contains("AccessKeyId", ex.Message, StringComparison.Ordinal);
    }
}
