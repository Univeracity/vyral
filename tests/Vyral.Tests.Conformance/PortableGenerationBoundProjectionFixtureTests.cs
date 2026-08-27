using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Models;

namespace Vyral.Tests.Conformance;

public sealed class PortableGenerationBoundProjectionFixtureTests
{
    private const string ScenarioId = "records.projection-generation.v1";
    private const string ManifestResource = "Vyral.Tests.Conformance.runtime-v1-manifest.json";
    private const string ScenarioResource = "Vyral.Tests.Conformance.runtime-v1-record-search-projection-generation.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void DescriptorDigestMatchesPortableGoldenAndPackagedFixture()
    {
        var scenarioBytes = ReadResource(ScenarioResource);
        using var manifest = JsonDocument.Parse(ReadResource(ManifestResource));
        var descriptor = manifest.RootElement
            .GetProperty("scenarios")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == ScenarioId);
        Assert.Equal(
            descriptor.GetProperty("sha256").GetString(),
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(scenarioBytes)));
        Assert.Equal("vyral.runtime.retrieval-generation.v1", descriptor.GetProperty("profile").GetString());

        using var scenario = JsonDocument.Parse(scenarioBytes);
        var step = Assert.Single(scenario.RootElement.GetProperty("steps").EnumerateArray());
        var golden = step.GetProperty("arguments").GetProperty("descriptor")
            .Deserialize<RecordSearchProjectionGenerationDescriptor>(JsonOptions)!;
        RecordSearchProjectionGenerationContract.ValidateDescriptor(golden);
        Assert.Equal(
            step.GetProperty("expect").GetProperty("value").GetString(),
            RecordSearchProjectionGenerationContract.ComputeDescriptorDigest(golden));

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "record-search-projection-generation.v1.valid.json");
        var fixtureNode = JsonNode.Parse(File.ReadAllText(fixturePath));
        var goldenNode = JsonNode.Parse(step.GetProperty("arguments").GetProperty("descriptor").GetRawText());
        Assert.True(JsonNode.DeepEquals(goldenNode, fixtureNode));

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "contracts", "record-search-projection-generation.v1.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        Assert.Equal(RecordSearchProjectionGenerationSchemas.DescriptorV1, schema["$defs"]!["generationDescriptor"]!["properties"]!["schema"]!["const"]!.GetValue<string>());
        Assert.NotNull(schema["$defs"]!["searchRequest"]);
        Assert.NotNull(schema["$defs"]!["searchResult"]);
        Assert.NotNull(schema["$defs"]!["buildRequest"]);
        Assert.NotNull(schema["$defs"]!["buildReceipt"]);
        Assert.NotNull(schema["$defs"]!["inspection"]);
    }

    [Fact]
    public void DescriptorDigestIgnoresInputObjectAndSetOrderingButValidationRequiresCanonicalWireOrder()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "record-search-projection-generation.v1.valid.json");
        var canonical = JsonSerializer.Deserialize<RecordSearchProjectionGenerationDescriptor>(File.ReadAllText(path), JsonOptions)!;
        var reordered = JsonSerializer.Deserialize<RecordSearchProjectionGenerationDescriptor>(File.ReadAllText(path), JsonOptions)!;
        reordered.ExpectedPartitions.Reverse();
        reordered.Capabilities.Reverse();
        reordered.Artifacts.Reverse();

        Assert.Equal(
            RecordSearchProjectionGenerationContract.ComputeDescriptorDigest(canonical),
            RecordSearchProjectionGenerationContract.ComputeDescriptorDigest(reordered));
        Assert.Throws<InvalidOperationException>(() =>
            RecordSearchProjectionGenerationContract.ValidateDescriptor(reordered));
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PortableGenerationBoundProjectionFixtureTests).Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
