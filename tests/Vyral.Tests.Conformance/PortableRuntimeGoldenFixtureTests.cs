using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vyral.Abstractions.Models;

namespace Vyral.Tests.Conformance;

public sealed class PortableRuntimeGoldenFixtureTests
{
    private const string ManifestResource = "Vyral.Tests.Conformance.runtime-v1-manifest.json";
    private const string HashingResource = "Vyral.Tests.Conformance.runtime-v1-primitives-hashing.json";

    [Fact]
    public void PortableHashingGoldensMatchTheDotNetContract()
    {
        var manifestBytes = ReadResource(ManifestResource);
        using var manifest = JsonDocument.Parse(manifestBytes);
        Assert.Equal("1.0.0", manifest.RootElement.GetProperty("fixtureVersion").GetString());
        Assert.Equal("0.3.0", manifest.RootElement.GetProperty("contractVersion").GetString());

        var descriptor = manifest.RootElement
            .GetProperty("scenarios")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "primitives.hashing.v1");
        Assert.Equal("primitives.hashing.v1", descriptor.GetProperty("id").GetString());
        Assert.Equal("vyral.runtime.contracts.v1", descriptor.GetProperty("profile").GetString());
        Assert.Equal("golden", descriptor.GetProperty("kind").GetString());

        var scenarioBytes = ReadResource(HashingResource);
        var actualDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(scenarioBytes));
        Assert.Equal(descriptor.GetProperty("sha256").GetString(), actualDigest);

        using var scenario = JsonDocument.Parse(scenarioBytes);
        Assert.Equal(descriptor.GetProperty("id").GetString(), scenario.RootElement.GetProperty("id").GetString());
        Assert.Equal("1.0.0", scenario.RootElement.GetProperty("fixtureVersion").GetString());

        foreach (var step in scenario.RootElement.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("operation").GetString();
            var arguments = step.GetProperty("arguments");
            var actual = operation switch
            {
                "hash.sha256-utf8" => Hash(arguments.GetProperty("text").GetString()!),
                "canonical.transaction-id" => CanonicalTransactionHasher.CreateTransactionId(
                    arguments.GetProperty("tenantId").GetString()!,
                    arguments.GetProperty("idempotencyKey").GetString()!),
                "canonical.lease-token-hash" => CanonicalTransactionHasher.HashLeaseToken(
                    arguments.GetProperty("token").GetString()!),
                _ => throw new InvalidOperationException($"Unknown portable golden operation '{operation}'.")
            };

            Assert.Equal(step.GetProperty("expect").GetProperty("value").GetString(), actual);
        }
    }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PortableRuntimeGoldenFixtureTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
