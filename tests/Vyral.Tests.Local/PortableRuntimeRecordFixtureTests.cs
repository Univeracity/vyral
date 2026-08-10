using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Local;

public sealed class PortableRuntimeRecordFixtureTests
{
    private const string ManifestResource = "Vyral.Tests.Local.runtime-v1-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Theory]
    [InlineData(
        "records.core-crud.v1",
        "Vyral.Tests.Local.runtime-v1-records-core-crud.json")]
    [InlineData(
        "records.query-semantics.v1",
        "Vyral.Tests.Local.runtime-v1-records-query-semantics.json")]
    public async Task PortableRecordScenarioMatchesTheDotNetStore(
        string scenarioId,
        string scenarioResource)
    {
        var manifestBytes = ReadResource(ManifestResource);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var descriptor = manifest.RootElement
            .GetProperty("scenarios")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == scenarioId);
        Assert.Equal("stateful", descriptor.GetProperty("kind").GetString());
        Assert.Equal("vyral.runtime.data-rag.v1", descriptor.GetProperty("profile").GetString());

        var scenarioBytes = ReadResource(scenarioResource);
        var actualDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(scenarioBytes));
        Assert.Equal(descriptor.GetProperty("sha256").GetString(), actualDigest);

        using var scenario = JsonDocument.Parse(scenarioBytes);
        Assert.Equal(scenarioId, scenario.RootElement.GetProperty("id").GetString());

        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"vyral-portable-records-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteRecordCollectionStore(databasePath);
            await store.InitializeAsync();

            foreach (var step in scenario.RootElement.GetProperty("steps").EnumerateArray())
            {
                await AssertStepAsync(store, step);
            }
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }

    private static async Task AssertStepAsync(
        SqliteRecordCollectionStore store,
        JsonElement step)
    {
        var stepId = step.GetProperty("id").GetString()!;
        var operation = step.GetProperty("operation").GetString()!;
        var arguments = step.GetProperty("arguments");
        var expectation = step.GetProperty("expect");

        JsonNode? value = null;
        Exception? error = null;
        try
        {
            value = await ExecuteAsync(store, operation, arguments);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (expectation.TryGetProperty("value", out var expectedValue))
        {
            Assert.True(
                error is null,
                $"Stateful step '{stepId}' failed unexpectedly: {error}");
            var expectedNode = JsonNode.Parse(expectedValue.GetRawText());
            Assert.True(
                JsonNode.DeepEquals(expectedNode, value),
                $"Stateful step '{stepId}' produced {value?.ToJsonString() ?? "null"}, " +
                $"expected {expectedNode?.ToJsonString() ?? "null"}.");
            return;
        }

        var expectedError = expectation.GetProperty("error");
        Assert.NotNull(error);
        Assert.Equal(
            expectedError.GetProperty("class").GetString(),
            ClassifyError(error!));
        if (expectedError.TryGetProperty("messageContains", out var messageContains))
        {
            Assert.Contains(
                messageContains.GetString()!,
                error!.Message,
                StringComparison.Ordinal);
        }
    }

    private static async Task<JsonNode?> ExecuteAsync(
        SqliteRecordCollectionStore store,
        string operation,
        JsonElement arguments)
    {
        switch (operation)
        {
            case "records.collection.create":
                await store.CreateCollectionAsync(
                    Deserialize<RecordCollectionPolicy>(arguments.GetProperty("policy")));
                return null;
            case "records.collection.list":
                return JsonSerializer.SerializeToNode(
                    (await store.GetCollectionsAsync()).ToList(),
                    JsonOptions);
            case "records.collection.delete":
                await store.DeleteCollectionAsync(arguments.GetProperty("collection").GetString()!);
                return null;
            case "records.record.upsert":
            {
                var record = Deserialize<VyralRecord>(arguments.GetProperty("record"));
                var precondition = arguments.TryGetProperty("precondition", out var rawPrecondition)
                    ? Deserialize<RecordWritePrecondition>(rawPrecondition)
                    : null;
                await store.UpsertRecordAsync(
                    arguments.GetProperty("collection").GetString()!,
                    record,
                    precondition);
                return ProjectWrite(record);
            }
            case "records.record.get":
            {
                var record = await store.GetRecordAsync(
                    arguments.GetProperty("collection").GetString()!,
                    arguments.GetProperty("partitionKey").GetString()!,
                    arguments.GetProperty("id").GetString()!);
                return ProjectRead(record);
            }
            case "records.record.batch-upsert":
            {
                var records = Deserialize<List<VyralRecord>>(arguments.GetProperty("records"));
                var result = await store.UpsertRecordsAsync(
                    arguments.GetProperty("collection").GetString()!,
                    new RecordBatchUpsertRequest
                    {
                        Records = records,
                        ContinueOnError = arguments.GetProperty("continueOnError").GetBoolean()
                    });
                return new JsonObject
                {
                    ["collection"] = result.Collection,
                    ["requested"] = result.Requested,
                    ["attempted"] = result.Attempted,
                    ["succeeded"] = result.Succeeded,
                    ["failed"] = result.Failed,
                    ["stoppedOnError"] = result.StoppedOnError,
                    ["statuses"] = new JsonArray(
                        result.Items.Select(item => JsonValue.Create(item.Status)).ToArray())
                };
            }
            case "records.query":
            {
                var result = await store.QueryRecordsPageAsync(
                    arguments.GetProperty("collection").GetString()!,
                    Deserialize<QueryEnvelope>(arguments.GetProperty("query")));
                return new JsonObject
                {
                    ["ids"] = new JsonArray(
                        result.Items.Select(item => JsonValue.Create(item.Id)).ToArray()),
                    ["continuationToken"] = result.ContinuationToken
                };
            }
            case "records.search":
            {
                var result = await store.SearchRecordsPageAsync(
                    arguments.GetProperty("collection").GetString()!,
                    Deserialize<QueryEnvelope>(arguments.GetProperty("query")));
                return new JsonObject
                {
                    ["ids"] = new JsonArray(
                        result.Items.Select(item => JsonValue.Create(item.Record.Id)).ToArray()),
                    ["scores"] = new JsonArray(
                        result.Items.Select(item => JsonValue.Create(item.Score)).ToArray()),
                    ["continuationToken"] = result.ContinuationToken
                };
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported record-store scenario operation '{operation}'.");
        }
    }

    private static JsonObject ProjectWrite(VyralRecord record) => new()
    {
        ["id"] = record.Id,
        ["partitionKey"] = record.PartitionKey,
        ["etag"] = record.Etag,
        ["revision"] = record.Revision,
        ["vectorDimensions"] = record.Vectors?.GetValueOrDefault("embedding")?.Dimensions
    };

    private static JsonNode? ProjectRead(VyralRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        JsonNode? extension = null;
        if (record.AdditionalProperties?.TryGetValue("extension", out var rawExtension) == true)
        {
            extension = JsonNode.Parse(rawExtension.GetRawText());
        }

        return new JsonObject
        {
            ["id"] = record.Id,
            ["partitionKey"] = record.PartitionKey,
            ["type"] = record.Type,
            ["schemaVersion"] = record.SchemaVersion,
            ["metadata"] = record.Metadata?.DeepClone(),
            ["content"] = record.Content?.DeepClone(),
            ["extension"] = extension,
            ["etag"] = record.Etag,
            ["revision"] = record.Revision,
            ["vectorDimensions"] = record.Vectors?.GetValueOrDefault("embedding")?.Dimensions
        };
    }

    private static string ClassifyError(Exception error) =>
        error switch
        {
            InvalidOperationException when error.Message.Contains(
                "precondition failed",
                StringComparison.OrdinalIgnoreCase) => "precondition-failed",
            ArgumentException => "validation",
            NotSupportedException => "validation",
            InvalidOperationException => "validation",
            _ => "unexpected"
        };

    private static T Deserialize<T>(JsonElement element) where T : class =>
        JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions)
        ?? throw new InvalidOperationException(
            $"Portable fixture value could not be deserialized as {typeof(T).Name}.");

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PortableRuntimeRecordFixtureTests)
            .Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
