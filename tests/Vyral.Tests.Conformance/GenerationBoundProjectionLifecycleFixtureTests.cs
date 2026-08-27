using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Conformance;

public sealed class GenerationBoundProjectionLifecycleFixtureTests
{
    private const string ScenarioId = "records.projection-generation-lifecycle.v1";
    private const string ScenarioResource = "Vyral.Tests.Conformance.runtime-v1-generation-bound-lifecycle.json";
    private const string ManifestResource = "Vyral.Tests.Conformance.runtime-v1-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task LocalReferenceExecutesPortableLifecycleScenario()
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

        using var scenario = JsonDocument.Parse(scenarioBytes);
        var projection = new LocalGenerationBoundRecordSearchProjection(
            new LocalGenerationBoundRecordSearchProjectionOptions
            {
                ContinuationSigningKey = SHA256.HashData("portable-generation-fixture-key"u8.ToArray())
            });
        var continuations = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var step in scenario.RootElement.GetProperty("steps").EnumerateArray())
        {
            var actual = await ExecuteAsync(projection, continuations, step);
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(step.GetProperty("expect").GetProperty("value").GetRawText()),
                JsonSerializer.SerializeToNode(actual, JsonOptions)),
                $"Lifecycle step '{step.GetProperty("id").GetString()}' did not match its portable expectation.");
        }
    }

    private static async Task<object> ExecuteAsync(
        LocalGenerationBoundRecordSearchProjection projection,
        IDictionary<string, string> continuations,
        JsonElement step)
    {
        var operation = step.GetProperty("operation").GetString();
        var arguments = step.GetProperty("arguments");
        switch (operation)
        {
            case "records.projection-generation-publish":
                var descriptor = arguments.GetProperty("descriptor")
                    .Deserialize<RecordSearchProjectionGenerationDescriptor>(JsonOptions)!;
                var documents = arguments.GetProperty("documents")
                    .EnumerateArray()
                    .Select(item => new LocalRecordSearchProjectionDocument
                    {
                        Candidate = new RecordSearchProjectionCandidate
                        {
                            PartitionKey = item.GetProperty("partitionKey").GetString()!,
                            Id = item.GetProperty("id").GetString()!,
                            Revision = item.GetProperty("revision").GetInt32()
                        },
                        SearchText = item.GetProperty("searchText").GetString()!
                    })
                    .ToList();
                projection.PublishGeneration(new LocalRecordSearchProjectionGeneration
                {
                    Descriptor = descriptor,
                    Documents = documents
                });
                return new { status = "ok" };

            case "records.projection-generation-activate":
                projection.ActivateGeneration(Collection(arguments), Generation(arguments));
                return new { status = "ok" };

            case "records.projection-generation-retire":
                projection.RetireGeneration(Collection(arguments), Generation(arguments));
                return new { status = "ok" };

            case "records.projection-generation-set-available":
                projection.SetAvailablePartitions(
                    Collection(arguments),
                    Generation(arguments),
                    arguments.GetProperty("availablePartitions").EnumerateArray().Select(item => item.GetString()!));
                return new { status = "ok" };

            case "records.projection-generation-inspect":
                var inspection = await projection.InspectGenerationAsync(
                    new RecordCollectionPolicy { Name = Collection(arguments) });
                Assert.NotNull(inspection);
                return new
                {
                    generationId = inspection.Descriptor.GenerationId,
                    state = inspection.State,
                    coverageStatus = inspection.CoverageStatus,
                    availablePartitions = inspection.AvailablePartitions
                };

            case "records.projection-generation-search":
                return await SearchAsync(projection, continuations, arguments);

            default:
                throw new InvalidOperationException($"Unsupported lifecycle fixture operation '{operation}'.");
        }
    }

    private static async Task<object> SearchAsync(
        LocalGenerationBoundRecordSearchProjection projection,
        IDictionary<string, string> continuations,
        JsonElement arguments)
    {
        string? continuation = null;
        if (arguments.TryGetProperty("continuationRef", out var continuationRef))
        {
            continuation = continuations[continuationRef.GetString()!];
        }
        if (arguments.TryGetProperty("tamperContinuationRef", out var tamperRef))
        {
            continuation = Tamper(continuations[tamperRef.GetString()!]);
        }

        var request = new GenerationBoundRecordSearchProjectionRequest
        {
            ExpectedDescriptorDigest = arguments.TryGetProperty("expectedDescriptorDigest", out var digest)
                ? digest.GetString()
                : null,
            Query = new QueryEnvelope
            {
                PartitionKeys = arguments.GetProperty("partitionKeys")
                    .EnumerateArray()
                    .Select(item => item.GetString()!)
                    .ToList(),
                Lexical = new LexicalSearchOptions
                {
                    Query = arguments.GetProperty("query").GetString()!,
                    ScanLimit = 100
                },
                Limit = arguments.GetProperty("limit").GetInt32(),
                ContinuationToken = continuation
            }
        };
        var result = await projection.SearchGenerationAsync(
            new RecordCollectionPolicy { Name = Collection(arguments) },
            request);
        if (arguments.TryGetProperty("saveContinuationAs", out var saveAs))
        {
            Assert.False(string.IsNullOrWhiteSpace(result.ContinuationToken));
            continuations[saveAs.GetString()!] = result.ContinuationToken!;
        }
        return new
        {
            status = result.Status,
            generationId = result.GenerationId,
            ids = result.Items.Select(item => item.Id).ToList(),
            continuation = result.ContinuationToken is null ? "absent" : "present",
            coverageStatus = result.Coverage.Status,
            coveredPartitions = result.Coverage.CoveredPartitions,
            missingPartitions = result.Coverage.MissingPartitions,
            failureCode = result.Failure?.Code
        };
    }

    private static string Collection(JsonElement arguments) =>
        arguments.GetProperty("collection").GetString()!;

    private static string Generation(JsonElement arguments) =>
        arguments.GetProperty("generationId").GetString()!;

    private static string Tamper(string token)
    {
        var signatureStart = token.IndexOf('.', StringComparison.Ordinal) + 1;
        var replacement = token[signatureStart] == 'A' ? 'B' : 'A';
        return token[..signatureStart] + replacement + token[(signatureStart + 1)..];
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(GenerationBoundProjectionLifecycleFixtureTests).Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
