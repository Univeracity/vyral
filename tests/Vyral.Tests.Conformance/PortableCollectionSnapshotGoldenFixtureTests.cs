using System.Security.Cryptography;
using System.Text.Json;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Tests.Conformance;

public sealed class PortableCollectionSnapshotGoldenFixtureTests
{
    private const string ManifestResource = "Vyral.Tests.Conformance.runtime-v1-manifest.json";
    private const string ScenarioResource = "Vyral.Tests.Conformance.runtime-v1-collection-snapshot-hash.json";

    [Fact]
    public void CollectionSnapshotHashMatchesThePortableGolden()
    {
        var manifestBytes = ReadResource(ManifestResource);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var descriptor = manifest.RootElement
            .GetProperty("scenarios")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "records.snapshot-hash.v1");

        var scenarioBytes = ReadResource(ScenarioResource);
        var actualDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(scenarioBytes));
        Assert.Equal(descriptor.GetProperty("sha256").GetString(), actualDigest);

        using var scenario = JsonDocument.Parse(scenarioBytes);
        var step = Assert.Single(scenario.RootElement.GetProperty("steps").EnumerateArray());
        var snapshot = JsonSerializer.Deserialize<CollectionExportEnvelope>(
            step.GetProperty("arguments").GetProperty("snapshot").GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(snapshot);

        Assert.Equal(
            step.GetProperty("expect").GetProperty("value").GetString(),
            RecordCollectionStoreExtensions.ComputeCollectionSnapshotHash(snapshot!));
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PortableCollectionSnapshotGoldenFixtureTests)
            .Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
