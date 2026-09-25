using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Vyral.Server;

namespace Vyral.Tests.Local;

public sealed class ObjectAccessServerTests
{
    [Fact]
    public async Task ObjectRoutes_RequireIdentityContainerPrefixAndOperation()
    {
        await using var factory = CreateFactory(Policies());
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Vyral-Development-Identity", "owner@tests.example");

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/objects/publisure-masters/tenant-a/song/one.wav",
            new StringContent("owner audio", Encoding.UTF8, "audio/wav"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/objects/publisure-masters/tenant-a/song/one.wav")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/objects/publisure-masters?prefix=tenant-a%2Fsong%2F")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/objects/publisure-masters/tenant-a/song/one.wav")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync("/objects/publisure-masters/tenant-b/song/one.wav",
            new StringContent("foreign"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/objects/publisure-masters/tenant-b/song/one.wav")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/objects/publisure-masters/tenant-b/song/one.wav")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/objects/publisure-masters?prefix=tenant-b%2F")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/objects/publisure-masters?prefix=tenant-a")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/objects/publisure-masters")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/objects/publisure-masters/tenant-aa/song/one.wav")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/objects/other-container/tenant-a/song/one.wav")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().GetAsync("/objects/publisure-masters/tenant-a/song/one.wav")).StatusCode);
    }

    [Fact]
    public async Task ObjectRoutes_DistinguishReadListWriteAndDelete()
    {
        var policies = Policies();
        policies.Remove("Server:ObjectAccess:IdentityPolicies:0:AllowedOperations:1");
        policies.Remove("Server:ObjectAccess:IdentityPolicies:0:AllowedOperations:2");
        policies.Remove("Server:ObjectAccess:IdentityPolicies:0:AllowedOperations:3");
        await using var factory = CreateFactory(policies);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Vyral-Development-Identity", "owner@tests.example");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/objects/publisure-masters/tenant-a/missing.wav")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/objects/publisure-masters?prefix=tenant-a%2F")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync("/objects/publisure-masters/tenant-a/new.wav", new StringContent("data"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/objects/publisure-masters/tenant-a/new.wav")).StatusCode);
    }

    [Fact]
    public async Task ObjectRoutes_AcceptEncodedEmailTenantPrefix()
    {
        var policies = Policies();
        policies["Server:ObjectAccess:IdentityPolicies:0:AllowedKeyPrefixes:0"] = "email:owner@example.test/";
        await using var factory = CreateFactory(policies);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Vyral-Development-Identity", "owner@tests.example");

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/objects/publisure-masters/email%3Aowner%40example.test/missing.wav")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/objects/publisure-masters?prefix=email%3Aowner%40example.test%2F")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/objects/publisure-masters/email%3Aother%40example.test/missing.wav")).StatusCode);
    }

    [Fact]
    public async Task ObjectRoutes_RequireApiKeyAndWorkloadIdentityWhenBothConfigured()
    {
        var policies = Policies();
        policies["Server:ApiKey"] = "host-secret";
        await using var factory = CreateFactory(policies);
        var identityOnly = factory.CreateClient();
        identityOnly.DefaultRequestHeaders.Add("X-Vyral-Development-Identity", "owner@tests.example");
        Assert.Equal(HttpStatusCode.Unauthorized, (await identityOnly.GetAsync("/objects/publisure-masters/tenant-a/missing.wav")).StatusCode);

        var apiKeyOnly = factory.CreateClient();
        apiKeyOnly.DefaultRequestHeaders.Add("X-Vyral-Api-Key", "host-secret");
        Assert.Equal(HttpStatusCode.Forbidden, (await apiKeyOnly.GetAsync("/objects/publisure-masters/tenant-a/missing.wav")).StatusCode);

        identityOnly.DefaultRequestHeaders.Add("X-Vyral-Api-Key", "host-secret");
        Assert.Equal(HttpStatusCode.NotFound, (await identityOnly.GetAsync("/objects/publisure-masters/tenant-a/missing.wav")).StatusCode);
    }

    [Fact]
    public async Task ObjectRoutes_RejectInvalidGoogleIdentity()
    {
        var policies = Policies();
        policies["Server:ObjectAccess:AuthenticationMode"] = ObjectAuthenticationModes.GoogleOidc;
        policies["Server:ObjectAccess:AllowedAudiences:0"] = "https://vyral.example.test";
        await using var factory = CreateFactory(policies);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Serverless-Authorization", "Bearer not-a-jwt");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/objects/publisure-masters/tenant-a/missing.wav")).StatusCode);
    }

    [Fact]
    public void ObjectAccess_RejectsDevelopmentIdentityOutsideDevelopment()
    {
        var options = VyralObjectAccessOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(Policies()).Build());
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Production };
        Assert.Throws<InvalidOperationException>(() => new VyralObjectAccess(options, environment,
            [new DevelopmentHeaderObjectIdentityAuthenticator()]));
    }

    [Theory]
    [InlineData("tenant-a", "segment boundary")]
    [InlineData("tenant-a/../tenant-b/", "traversal")]
    [InlineData("tenant-a//", "empty segment")]
    public void ObjectAccess_RejectsUnsafeConfiguredPrefix(string prefix, string _)
    {
        var policies = Policies();
        policies["Server:ObjectAccess:IdentityPolicies:0:AllowedKeyPrefixes:0"] = prefix;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(policies).Build();
        Assert.ThrowsAny<Exception>(() => VyralObjectAccessOptions.FromConfiguration(configuration));
    }

    private static Dictionary<string, string?> Policies() => new()
    {
        ["Server:ObjectAccess:AuthenticationMode"] = ObjectAuthenticationModes.DevelopmentHeader,
        ["Server:ObjectAccess:IdentityPolicies:0:Principal"] = "owner@tests.example",
        ["Server:ObjectAccess:IdentityPolicies:0:Container"] = "publisure-masters",
        ["Server:ObjectAccess:IdentityPolicies:0:AllowedKeyPrefixes:0"] = "tenant-a/",
        ["Server:ObjectAccess:IdentityPolicies:0:AllowedOperations:0"] = ObjectAccessOperations.Read,
        ["Server:ObjectAccess:IdentityPolicies:0:AllowedOperations:1"] = ObjectAccessOperations.List,
        ["Server:ObjectAccess:IdentityPolicies:0:AllowedOperations:2"] = ObjectAccessOperations.Write,
        ["Server:ObjectAccess:IdentityPolicies:0:AllowedOperations:3"] = ObjectAccessOperations.Delete
    };

    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?> configuration)
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-object-access-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("DatabasePath", Path.Combine(root, "data.sqlite"));
            builder.UseSetting("ObjectsPath", Path.Combine(root, "objects"));
            foreach (var (key, value) in configuration) builder.UseSetting(key, value);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration));
        });
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Vyral.Tests.Local";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
