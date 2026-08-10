using System.Text.Json.Nodes;
using System.Text.Json;
using Vyral.Abstractions.Models;
using Vyral.Local;
using CliProgram = Vyral.Cli.Program;

namespace Vyral.Tests.Local;

public class CliWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task QueryCommand_RunsPortableQueryEnvelope()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "chunks",
            IndexedMetadata = new List<string> { "/metadata/status" }
        });
        await store.UpsertRecordAsync("chunks", new VyralRecord
        {
            Id = "active",
            PartitionKey = "tenant-a",
            Metadata = new JsonObject { ["status"] = "active" }
        });
        await store.UpsertRecordAsync("chunks", new VyralRecord
        {
            Id = "inactive",
            PartitionKey = "tenant-a",
            Metadata = new JsonObject { ["status"] = "inactive" }
        });

        var envelope = JsonSerializer.Serialize(new QueryEnvelope
        {
            Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
            Limit = 10
        });

        var result = await InvokeCliAsync(
            "query",
            "--db", dbPath,
            "--collection", "chunks",
            "--envelope", envelope);

        var page = JsonSerializer.Deserialize<RecordQueryResult>(result.Output, JsonOptions)!;

        Assert.Equal(0, result.ExitCode);
        var record = Assert.Single(page.Items);
        Assert.Equal("active", record.Id);
    }

    [Fact]
    public async Task InitCommand_InitializesSiblingCanonicalDatabase()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-init-{Guid.NewGuid():N}.sqlite");
        var canonicalPath = Path.ChangeExtension(dbPath, ".canonical.sqlite");

        var result = await InvokeCliAsync("init", "--db", dbPath);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(dbPath));
        Assert.True(File.Exists(canonicalPath));
        var canonical = new SqliteCanonicalStore(canonicalPath);
        Assert.Empty(await canonical.ListMigrationsAsync());
    }

    [Fact]
    public async Task SearchRecordsCommand_RunsEnvelopeFromFile()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-{Guid.NewGuid():N}.sqlite");
        var envelopePath = Path.Combine(Path.GetTempPath(), $"vyral-cli-query-{Guid.NewGuid():N}.json");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "chunks",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
            }
        });
        await store.UpsertRecordAsync("chunks", new VyralRecord
        {
            Id = "near",
            PartitionKey = "tenant-a",
            Vectors = new Dictionary<string, VyralVector>
            {
                ["contentEmbedding"] = new() { Values = new float[] { 1, 0 } }
            }
        });
        await store.UpsertRecordAsync("chunks", new VyralRecord
        {
            Id = "far",
            PartitionKey = "tenant-a",
            Vectors = new Dictionary<string, VyralVector>
            {
                ["contentEmbedding"] = new() { Values = new float[] { 0, 1 } }
            }
        });
        await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(new QueryEnvelope
        {
            Vector = new VectorSearchOptions
            {
                Field = "contentEmbedding",
                Value = new float[] { 1, 0 },
                Top = 1
            },
            Limit = 1
        }));

        var result = await InvokeCliAsync(
            "search-records",
            "--db", dbPath,
            "--collection", "chunks",
            "--envelope-file", envelopePath);

        var page = JsonSerializer.Deserialize<RecordSearchResult>(result.Output, JsonOptions)!;

        Assert.Equal(0, result.ExitCode);
        var match = Assert.Single(page.Items);
        Assert.Equal("near", match.Record.Id);
    }

    [Fact]
    public async Task ExportImportCommands_RoundTripTypedEnvelope()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-{Guid.NewGuid():N}.sqlite");
        var exportPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-export-{Guid.NewGuid():N}.json");
        var boundedExportPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-export-bounded-{Guid.NewGuid():N}.json");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "chunks" });
        await store.UpsertRecordAsync("chunks", new VyralRecord
        {
            Id = "chunk-1",
            PartitionKey = "tenant-a",
            Content = new JsonObject { ["text"] = "round trip" }
        });
        await store.UpsertRecordAsync("chunks", new VyralRecord
        {
            Id = "chunk-2",
            PartitionKey = "tenant-a",
            Content = new JsonObject { ["text"] = "partial export" }
        });

        var exportResult = await InvokeCliAsync(
            "export",
            "--db", dbPath,
            "--collection", "chunks",
            "--file", exportPath);
        var json = await File.ReadAllTextAsync(exportPath);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(0, exportResult.ExitCode);
        Assert.True(document.RootElement.TryGetProperty("collection", out _));
        Assert.True(document.RootElement.TryGetProperty("policy", out _));
        Assert.True(document.RootElement.TryGetProperty("records", out _));
        Assert.True(document.RootElement.TryGetProperty("contentHash", out _));

        var boundedResult = await InvokeCliAsync(
            "export",
            "--db", dbPath,
            "--collection", "chunks",
            "--file", boundedExportPath,
            "--max-records", "1",
            "--allow-partial");
        var boundedJson = await File.ReadAllTextAsync(boundedExportPath);
        using var boundedDocument = JsonDocument.Parse(boundedJson);

        Assert.Equal(0, boundedResult.ExitCode);
        Assert.True(boundedDocument.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, boundedDocument.RootElement.GetProperty("recordCount").GetInt32());

        await store.DeleteCollectionAsync("chunks");

        var importResult = await InvokeCliAsync(
            "import",
            "--db", dbPath,
            "--file", exportPath);

        var record = await store.GetRecordAsync("chunks", "tenant-a", "chunk-1");

        Assert.Equal(0, importResult.ExitCode);
        Assert.NotNull(record);
        Assert.Equal("round trip", record!.Content!["text"]!.ToString());
    }

    [Fact]
    public async Task PruneTracesCommand_PrunesWithDryRunSupport()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-traces-{Guid.NewGuid():N}.sqlite");
        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();
        var createdAt = DateTime.UtcNow.AddMinutes(-5);
        for (var i = 0; i < 3; i++)
        {
            await traces.WriteTraceAsync(new TraceRecord
            {
                Id = $"trace-{i}",
                Operation = "provider.run",
                StartedAt = createdAt.AddMinutes(i),
                CreatedAt = createdAt.AddMinutes(i),
                DurationMs = i
            });
        }

        var dryRun = await InvokeCliAsync(
            "prune-traces",
            "--db", dbPath,
            "--operation", "provider.run",
            "--keep-latest", "1",
            "--dry-run");
        var dryRunResult = JsonSerializer.Deserialize<TracePruneResult>(dryRun.Output, JsonOptions)!;

        Assert.Equal(0, dryRun.ExitCode);
        Assert.Equal(2, dryRunResult.MatchedCount);
        Assert.Equal(0, dryRunResult.DeletedCount);
        Assert.Equal(3, (await traces.ListTracesAsync("provider.run")).Count());

        var prune = await InvokeCliAsync(
            "prune-traces",
            "--db", dbPath,
            "--operation", "provider.run",
            "--keep-latest", "1");
        var pruneResult = JsonSerializer.Deserialize<TracePruneResult>(prune.Output, JsonOptions)!;

        Assert.Equal(0, prune.ExitCode);
        Assert.Equal(2, pruneResult.DeletedCount);
        Assert.Single(await traces.ListTracesAsync("provider.run"));

        var summaryResult = await InvokeCliAsync(
            "summarize-traces",
            "--db", dbPath,
            "--operation", "provider.run");
        var summary = JsonSerializer.Deserialize<TraceSummary>(summaryResult.Output, JsonOptions)!;

        Assert.Equal(0, summaryResult.ExitCode);
        Assert.Equal(1, summary.TotalCount);

        var export = await InvokeCliAsync(
            "export-traces",
            "--db", dbPath,
            "--operation", "provider.run");
        var bundle = JsonSerializer.Deserialize<TraceExportBundle>(export.Output, JsonOptions)!;

        Assert.Equal(0, export.ExitCode);
        Assert.Equal(1, bundle.TraceCount);
        Assert.StartsWith("sha256:", bundle.ContentHash);
    }

    [Fact]
    public async Task CanonicalExportRestoreCommands_RoundTripCompleteTenantSnapshot()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-{Guid.NewGuid():N}.sqlite");
        var canonicalPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-canonical-{Guid.NewGuid():N}.sqlite");
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-canonical-export-{Guid.NewGuid():N}.json");
        var migrationsPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-canonical-migrations-{Guid.NewGuid():N}.json");
        var store = new SqliteCanonicalStore(canonicalPath);
        await File.WriteAllTextAsync(migrationsPath, JsonSerializer.Serialize(new[]
        {
            new CanonicalMigration { Namespace = "cli-test", Id = "canonical.cli.v1", Checksum = "sha256:canonical-cli-v1" }
        }, JsonOptions));
        var migrate = await InvokeCliAsync(
            "canonical-migrate",
            "--db", dbPath,
            "--canonical-db", canonicalPath,
            "--file", migrationsPath);
        Assert.Equal(0, migrate.ExitCode);
        Assert.Single(await store.ListMigrationsAsync());

        await store.CommitAsync(new CanonicalTransactionRequest
        {
            TenantId = "tenant-a",
            IdempotencyKey = "claim-1",
            Mutations = new List<CanonicalMutation>
            {
                new()
                {
                    Document = new CanonicalDocument
                    {
                        TenantId = "tenant-a",
                        DocumentType = "claim",
                        Id = "claim-1",
                        SchemaVersion = "v1",
                        Data = new JsonObject { ["status"] = "approved" },
                        Indexes = new Dictionary<string, string> { ["status"] = "approved" }
                    }
                }
            },
            Outbox = new List<CanonicalOutboxWrite> { new() { Topic = "claim.approved", Key = "claim-1", Payload = new JsonObject { ["claimId"] = "claim-1" } } }
        });

        var export = await InvokeCliAsync(
            "canonical-export",
            "--db", dbPath,
            "--canonical-db", canonicalPath,
            "--tenant", "tenant-a",
            "--file", snapshotPath);
        var snapshot = JsonSerializer.Deserialize<CanonicalTenantSnapshot>(await File.ReadAllTextAsync(snapshotPath), JsonOptions)!;
        Assert.Equal(0, export.ExitCode);
        Assert.Equal("tenant-a", snapshot.TenantId);
        Assert.Single(snapshot.Documents);
        Assert.Single(snapshot.Outbox);
        Assert.StartsWith("sha256:", snapshot.ContentHash);

        await store.CommitAsync(new CanonicalTransactionRequest
        {
            TenantId = "tenant-a",
            IdempotencyKey = "claim-2",
            Mutations = new List<CanonicalMutation>
            {
                new()
                {
                    Document = new CanonicalDocument
                    {
                        TenantId = "tenant-a",
                        DocumentType = "claim",
                        Id = "claim-2",
                        SchemaVersion = "v1",
                        Data = new JsonObject { ["status"] = "draft" }
                    }
                }
            }
        });

        var restore = await InvokeCliAsync(
            "canonical-restore",
            "--db", dbPath,
            "--canonical-db", canonicalPath,
            "--tenant", "tenant-a",
            "--file", snapshotPath,
            "--expected-content-hash", snapshot.ContentHash);

        Assert.Equal(0, restore.ExitCode);
        Assert.NotNull(await store.GetDocumentAsync("tenant-a", "claim", "claim-1"));
        Assert.Null(await store.GetDocumentAsync("tenant-a", "claim", "claim-2"));
    }

    [Fact]
    public async Task CanonicalArchiveCommands_RoundTripManifestAndChunks()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-{Guid.NewGuid():N}.sqlite");
        var canonicalPath = Path.Combine(Path.GetTempPath(), $"vyral-cli-canonical-{Guid.NewGuid():N}.sqlite");
        var archiveDirectory = Path.Combine(Path.GetTempPath(), $"vyral-cli-canonical-archive-{Guid.NewGuid():N}");
        var store = new SqliteCanonicalStore(canonicalPath);
        await store.CommitAsync(new CanonicalTransactionRequest
        {
            TenantId = "tenant-a",
            IdempotencyKey = "archive-1",
            Mutations = new List<CanonicalMutation>
            {
                new()
                {
                    Document = new CanonicalDocument
                    {
                        TenantId = "tenant-a", DocumentType = "claim", Id = "claim-1", SchemaVersion = "v1",
                        Data = new JsonObject { ["text"] = new string('a', 512) }
                    }
                }
            }
        });

        var export = await InvokeCliAsync(
            "canonical-archive-export",
            "--db", dbPath,
            "--canonical-db", canonicalPath,
            "--tenant", "tenant-a",
            "--directory", archiveDirectory,
            "--chunk-bytes", "128");
        Assert.Equal(0, export.ExitCode);
        Assert.True(File.Exists(Path.Combine(archiveDirectory, "manifest.json")));
        Assert.True(Directory.GetFiles(archiveDirectory, "chunk-*.bin").Length > 1);

        await store.CommitAsync(new CanonicalTransactionRequest
        {
            TenantId = "tenant-a",
            IdempotencyKey = "archive-2",
            Mutations = new List<CanonicalMutation>
            {
                new()
                {
                    Document = new CanonicalDocument
                    {
                        TenantId = "tenant-a", DocumentType = "claim", Id = "claim-2", SchemaVersion = "v1",
                        Data = new JsonObject { ["status"] = "after" }
                    }
                }
            }
        });

        var restore = await InvokeCliAsync(
            "canonical-archive-restore",
            "--db", dbPath,
            "--canonical-db", canonicalPath,
            "--tenant", "tenant-a",
            "--directory", archiveDirectory);
        Assert.Equal(0, restore.ExitCode);
        Assert.NotNull(await store.GetDocumentAsync("tenant-a", "claim", "claim-1"));
        Assert.Null(await store.GetDocumentAsync("tenant-a", "claim", "claim-2"));
    }

    private static async Task<(int ExitCode, string Output, string Error)> InvokeCliAsync(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await CliProgram.Main(args);
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
