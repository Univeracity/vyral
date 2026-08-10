using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Conformance;

public sealed class PortableRagIngestionPlanGoldenFixtureTests
{
    private const string ScenarioId = "rag.ingestion-plan.v1";
    private const string ManifestResource =
        "Vyral.Tests.Conformance.runtime-v1-manifest.json";
    private const string ScenarioResource =
        "Vyral.Tests.Conformance.runtime-v1-rag-ingestion-plan.json";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task LocalRagPlannerMatchesPortableHashGolden()
    {
        var manifestBytes = ReadResource(ManifestResource);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var descriptor = manifest.RootElement
            .GetProperty("scenarios")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("id").GetString() == ScenarioId);

        var scenarioBytes = ReadResource(ScenarioResource);
        var digest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(scenarioBytes));
        Assert.Equal(
            descriptor.GetProperty("sha256").GetString(),
            digest);

        using var scenario = JsonDocument.Parse(scenarioBytes);
        var step = scenario.RootElement.GetProperty("steps")[0];
        var arguments = step.GetProperty("arguments");
        var providerOptions = arguments.GetProperty("provider");
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"vyral-rag-plan-golden-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteRecordCollectionStore(databasePath);
            await store.InitializeAsync();
            await store.CreateCollectionAsync(
                Deserialize<RecordCollectionPolicy>(
                    arguments.GetProperty("policy")));
            var provider = new DeterministicHashEmbeddingProvider(
                providerOptions.GetProperty("dimensions").GetInt32());
            Assert.Equal(
                providerOptions.GetProperty("provider").GetString(),
                provider.ProviderId);
            var service = new LocalRagIngestionService(store, provider);
            var result = await service.IngestTextAsync(
                arguments.GetProperty("collection").GetString()!,
                Deserialize<RagIngestTextRequest>(
                    arguments.GetProperty("request")));

            var actual = JsonSerializer.SerializeToNode(
                new
                {
                    planHash = result.PlanHash,
                    manifestHash = result.ManifestHash,
                    textHash = result.TextHash,
                    chunkCount = result.ChunkCount,
                    createdCount = result.CreatedCount,
                    vectorGeneratedCount =
                        result.VectorGeneratedCount,
                    manifestAction = result.ManifestAction,
                    chunkIds = result.Chunks
                        .Select(item => item.Id)
                        .ToArray(),
                    chunkTextHashes = result.Chunks
                        .Select(item => item.TextHash)
                        .ToArray(),
                    embeddingTextHashes = result.Chunks
                        .Select(item => item.EmbeddingTextHash)
                        .ToArray(),
                    spans = result.Chunks
                        .Select(item => new[]
                        {
                            item.CharStart,
                            item.CharEnd
                        })
                        .ToArray()
                },
                JsonOptions);
            var expected = JsonNode.Parse(
                step.GetProperty("expect")
                    .GetProperty("value")
                    .GetRawText());
            Assert.True(
                JsonNode.DeepEquals(expected, actual),
                $"RAG plan golden produced " +
                $"{actual?.ToJsonString() ?? "null"}, expected " +
                $"{expected?.ToJsonString() ?? "null"}.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }

    private static T Deserialize<T>(JsonElement value)
    {
        return JsonSerializer.Deserialize<T>(
            value.GetRawText(), JsonOptions)
            ?? throw new InvalidOperationException(
                $"Fixture value did not deserialize as " +
                typeof(T).Name + ".");
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(
            PortableRagIngestionPlanGoldenFixtureTests)
            .Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{name}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
