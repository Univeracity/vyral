using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Models;

namespace Vyral.Tests.Conformance;

public sealed class PortableGraphRecordMappingGoldenFixtureTests
{
    private const string ScenarioId = "graph.record-mapping.v1";
    private const string ManifestResource = "Vyral.Tests.Conformance.runtime-v1-manifest.json";
    private const string ScenarioResource = "Vyral.Tests.Conformance.runtime-v1-graph-record-mapping.json";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void GraphRecordMappingMatchesThePortableGolden()
    {
        var manifestBytes = ReadResource(ManifestResource);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var descriptor = manifest.RootElement
            .GetProperty("scenarios")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == ScenarioId);

        var scenarioBytes = ReadResource(ScenarioResource);
        var actualDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(scenarioBytes));
        Assert.Equal(descriptor.GetProperty("sha256").GetString(), actualDigest);

        using var scenario = JsonDocument.Parse(scenarioBytes);
        var step = Assert.Single(scenario.RootElement.GetProperty("steps").EnumerateArray());
        var envelope = JsonSerializer.Deserialize<VyralGraphEnvelope>(
            step.GetProperty("arguments").GetProperty("envelope").GetRawText(),
            JsonOptions);
        Assert.NotNull(envelope);

        var mapped = VyralGraphRecordMapper.ToRecords(envelope!);
        var projectedRecords = new JsonArray();
        foreach (var record in mapped)
        {
            var entityKey = record.Type["graph.".Length..];
            var entityId = record.Content?[entityKey]?["id"]?.GetValue<string>();
            var sources = new JsonArray();
            foreach (var source in record.Sources ?? new List<VyralSourceReference>())
            {
                sources.Add(new JsonObject
                {
                    ["id"] = source.Id,
                    ["kind"] = source.Kind,
                    ["uri"] = source.Uri,
                    ["label"] = source.Label,
                    ["span"] = new JsonObject
                    {
                        ["charStart"] = source.Span?.CharStart,
                        ["charEnd"] = source.Span?.CharEnd,
                        ["anchor"] = source.Span?.Anchor,
                        ["unit"] = ReadExtension(source.Span, "unit"),
                        ["textHash"] = ReadExtension(source.Span, "textHash")
                    }
                });
            }

            projectedRecords.Add(new JsonObject
            {
                ["id"] = record.Id,
                ["partitionKey"] = record.PartitionKey,
                ["type"] = record.Type,
                ["schemaVersion"] = record.SchemaVersion,
                ["metadata"] = record.Metadata?.DeepClone(),
                ["contentKind"] = record.Content?["kind"]?.GetValue<string>(),
                ["contentText"] = record.Content?["text"]?.GetValue<string>(),
                ["entityId"] = entityId,
                ["sources"] = sources
            });
        }

        var actual = new JsonObject
        {
            ["partitionKey"] = VyralGraphRecordMapper.ResolvePartitionKey(envelope!.Scope),
            ["recordCount"] = mapped.Count,
            ["records"] = projectedRecords
        };
        var expected = JsonNode.Parse(
            step.GetProperty("expect").GetProperty("value").GetRawText());
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"Graph mapping produced {actual.ToJsonString()}, " +
            $"expected {expected?.ToJsonString() ?? "null"}.");
    }

    private static string? ReadExtension(VyralSourceSpan? span, string name)
    {
        if (span?.Extensions?.TryGetValue(name, out var value) != true)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PortableGraphRecordMappingGoldenFixtureTests)
            .Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
