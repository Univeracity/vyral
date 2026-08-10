using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;
using System.Text.Json.Nodes;
using Xunit;

namespace Vyral.Tests.Local;

public class SqliteTraceStoreTests
{
    [Fact]
    public async Task TraceStore_WritesGetsAndListsTraces()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-traces-{Guid.NewGuid():N}.sqlite");
        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();

        var trace = new TraceRecord
        {
            Operation = "retrieval.search",
            Adapter = "test",
            StartedAt = DateTime.UtcNow,
            DurationMs = 12.5,
            Request = new Dictionary<string, object?> { ["query"] = "hello" },
            ResultSummary = new Dictionary<string, object?> { ["returnedCount"] = 1 }
        };

        await traces.WriteTraceAsync(trace);

        var loaded = await traces.GetTraceAsync(trace.Id);
        var listed = (await traces.ListTracesAsync("retrieval.search", limit: 10)).ToList();
        var summary = await traces.SummarizeTracesAsync();

        Assert.NotNull(loaded);
        Assert.Equal(trace.Id, loaded.Id);
        Assert.Single(listed);
        Assert.Equal(trace.Id, listed[0].Id);
        Assert.Equal(1, summary.TotalCount);
        var operation = Assert.Single(summary.Operations);
        Assert.Equal("retrieval.search", operation.Operation);
        Assert.Equal(1, operation.Count);
        Assert.Equal(new[] { "test" }, operation.Adapters);
    }

    [Fact]
    public async Task TraceStore_AppliesDefaultAndMaximumListLimits()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-trace-list-limits-{Guid.NewGuid():N}.sqlite");
        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();

        var createdAt = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 105; i++)
        {
            await traces.WriteTraceAsync(new TraceRecord
            {
                Id = $"trace-{i:D3}",
                Operation = "provider.run",
                Adapter = "test",
                StartedAt = createdAt.AddSeconds(i),
                CreatedAt = createdAt.AddSeconds(i),
                DurationMs = i
            });
        }

        var defaultList = (await traces.ListTracesAsync("provider.run")).ToList();
        var explicitList = (await traces.ListTracesAsync("provider.run", limit: 105)).ToList();
        var tooLarge = await Assert.ThrowsAsync<InvalidOperationException>(() => traces.ListTracesAsync("provider.run", limit: 5001));

        Assert.Equal(100, defaultList.Count);
        Assert.Equal("trace-104", defaultList[0].Id);
        Assert.DoesNotContain(defaultList, trace => trace.Id == "trace-000");
        Assert.Equal(105, explicitList.Count);
        Assert.Contains("cannot exceed 5000", tooLarge.Message);
    }

    [Fact]
    public async Task TraceStore_SummaryIncludesOperationalDiagnosticCounts()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-trace-summary-diagnostics-{Guid.NewGuid():N}.sqlite");
        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();

        await traces.WriteTraceAsync(new TraceRecord
        {
            Id = "provider-success",
            Operation = "provider.run",
            Adapter = "codex-cli",
            StartedAt = DateTime.UtcNow.AddSeconds(-3),
            CreatedAt = DateTime.UtcNow.AddSeconds(-3),
            DurationMs = 20,
            Request = new Dictionary<string, object?>
            {
                ["provider"] = "codex-cli",
                ["capability"] = "ai.extract"
            },
            ResultSummary = new Dictionary<string, object?>
            {
                ["status"] = "Succeeded",
                ["providerStatus"] = "ok"
            }
        });
        await traces.WriteTraceAsync(new TraceRecord
        {
            Id = "provider-quota",
            Operation = "provider.run",
            Adapter = "codex-cli",
            StartedAt = DateTime.UtcNow.AddSeconds(-2),
            CreatedAt = DateTime.UtcNow.AddSeconds(-2),
            DurationMs = 8,
            Request = new Dictionary<string, object?>
            {
                ["provider"] = "codex-cli",
                ["capability"] = "ai.review"
            },
            ResultSummary = new Dictionary<string, object?>
            {
                ["status"] = "Failed",
                ["failureClass"] = "quota",
                ["providerStatus"] = "quota_exceeded"
            }
        });
        await traces.WriteTraceAsync(new TraceRecord
        {
            Id = "qualification",
            Operation = "provider.qualification",
            Adapter = "gemini-cli",
            StartedAt = DateTime.UtcNow.AddSeconds(-1),
            CreatedAt = DateTime.UtcNow.AddSeconds(-1),
            DurationMs = 3,
            Request = new Dictionary<string, object?>
            {
                ["provider"] = "gemini-cli",
                ["capability"] = "ai.chat"
            },
            ResultSummary = new Dictionary<string, object?>
            {
                ["status"] = "validated"
            }
        });

        var summary = await traces.SummarizeTracesAsync();
        var providerRun = summary.Operations.Single(operation => operation.Operation == "provider.run");
        var qualification = summary.Operations.Single(operation => operation.Operation == "provider.qualification");

        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(1, summary.StatusCounts["Succeeded"]);
        Assert.Equal(1, summary.StatusCounts["Failed"]);
        Assert.Equal(1, summary.StatusCounts["validated"]);
        Assert.Equal(1, summary.FailureClassCounts["quota"]);
        Assert.Equal(1, summary.ProviderStatusCounts["ok"]);
        Assert.Equal(1, summary.ProviderStatusCounts["quota_exceeded"]);
        Assert.Equal(2, summary.ProviderCounts["codex-cli"]);
        Assert.Equal(1, summary.ProviderCounts["gemini-cli"]);
        Assert.Equal(1, summary.CapabilityCounts["ai.extract"]);
        Assert.Equal(1, summary.CapabilityCounts["ai.review"]);
        Assert.Equal(1, summary.CapabilityCounts["ai.chat"]);

        Assert.Equal(2, providerRun.Count);
        Assert.Equal(new[] { "codex-cli" }, providerRun.Adapters);
        Assert.Equal(1, providerRun.StatusCounts["Succeeded"]);
        Assert.Equal(1, providerRun.StatusCounts["Failed"]);
        Assert.Equal(1, providerRun.FailureClassCounts["quota"]);
        Assert.Equal(2, providerRun.ProviderCounts["codex-cli"]);
        Assert.Equal(1, providerRun.CapabilityCounts["ai.extract"]);
        Assert.Equal(1, providerRun.CapabilityCounts["ai.review"]);

        Assert.Equal(1, qualification.Count);
        Assert.Equal(new[] { "gemini-cli" }, qualification.Adapters);
        Assert.Equal(1, qualification.StatusCounts["validated"]);
        Assert.Equal(1, qualification.ProviderCounts["gemini-cli"]);
        Assert.Equal(1, qualification.CapabilityCounts["ai.chat"]);
    }

    [Fact]
    public async Task TraceStore_PrunesWithDryRunKeepLatestAndOperationScope()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-trace-prune-{Guid.NewGuid():N}.sqlite");
        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();

        var createdAt = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 4; i++)
        {
            await traces.WriteTraceAsync(new TraceRecord
            {
                Id = $"provider-{i}",
                Operation = "provider.run",
                Adapter = "test",
                StartedAt = createdAt.AddMinutes(i),
                CreatedAt = createdAt.AddMinutes(i),
                DurationMs = i
            });
        }

        await traces.WriteTraceAsync(new TraceRecord
        {
            Id = "retrieval-0",
            Operation = "retrieval.search",
            Adapter = "test",
            StartedAt = createdAt,
            CreatedAt = createdAt,
            DurationMs = 1
        });

        var dryRun = await traces.PruneTracesAsync(new TracePruneRequest
        {
            Operation = "provider.run",
            KeepLatest = 1,
            DryRun = true
        });
        Assert.Equal(3, dryRun.MatchedCount);
        Assert.Equal(0, dryRun.DeletedCount);
        Assert.Equal(new[] { "provider-0", "provider-1", "provider-2" }, dryRun.MatchedIds.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Empty(dryRun.DeletedIds);
        Assert.Equal(4, (await traces.ListTracesAsync("provider.run")).Count());

        var pruned = await traces.PruneTracesAsync(new TracePruneRequest
        {
            Operation = "provider.run",
            KeepLatest = 1
        });

        Assert.Equal(3, pruned.MatchedCount);
        Assert.Equal(3, pruned.DeletedCount);
        Assert.Equal(new[] { "provider-0", "provider-1", "provider-2" }, pruned.MatchedIds.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(new[] { "provider-0", "provider-1", "provider-2" }, pruned.DeletedIds.OrderBy(id => id, StringComparer.Ordinal));

        var remainingProvider = (await traces.ListTracesAsync("provider.run")).ToList();
        var remainingRetrieval = (await traces.ListTracesAsync("retrieval.search")).ToList();
        Assert.Single(remainingProvider);
        Assert.Equal("provider-3", remainingProvider[0].Id);
        Assert.Single(remainingRetrieval);
        Assert.Equal("retrieval-0", remainingRetrieval[0].Id);
    }

    [Fact]
    public async Task TraceStore_AppliesDefaultAndMaximumPruneLimits()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-trace-prune-limits-{Guid.NewGuid():N}.sqlite");
        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();

        var createdAt = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 105; i++)
        {
            await traces.WriteTraceAsync(new TraceRecord
            {
                Id = $"provider-{i:D3}",
                Operation = "provider.run",
                Adapter = "test",
                StartedAt = createdAt.AddSeconds(i),
                CreatedAt = createdAt.AddSeconds(i),
                DurationMs = i
            });
        }

        var pruned = await traces.PruneTracesAsync(new TracePruneRequest
        {
            Operation = "provider.run"
        });
        var remaining = (await traces.ListTracesAsync("provider.run", limit: 105)).ToList();
        var tooLarge = await Assert.ThrowsAsync<InvalidOperationException>(() => traces.PruneTracesAsync(new TracePruneRequest
        {
            Operation = "provider.run",
            Limit = 5001
        }));

        Assert.Equal(100, pruned.Limit);
        Assert.Equal(100, pruned.MatchedCount);
        Assert.Equal(100, pruned.DeletedCount);
        Assert.Equal(5, remaining.Count);
        Assert.Contains("cannot exceed 5000", tooLarge.Message);
    }

    [Fact]
    public async Task TraceStore_ExportsBundleWithContentHashAndUnsafeWarnings()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-trace-export-{Guid.NewGuid():N}.sqlite");
        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();

        await traces.WriteTraceAsync(new TraceRecord
        {
            Id = "unsafe-trace",
            Operation = "provider.run",
            Adapter = "test",
            StartedAt = DateTime.UtcNow,
            DurationMs = 3,
            Request = new Dictionary<string, object?> { ["authorization"] = "Bearer secret-value" },
            ResultSummary = new Dictionary<string, object?> { ["status"] = "succeeded" }
        });

        var bundle = await traces.ExportTracesAsync(new TraceExportRequest
        {
            Operation = "provider.run",
            Limit = 10
        });

        Assert.Equal("vyral.trace-export.v1", bundle.FormatVersion);
        Assert.Equal("provider.run", bundle.Operation);
        Assert.Equal(1, bundle.TraceCount);
        Assert.Equal(2, bundle.WarningCount);
        Assert.StartsWith("sha256:", bundle.ContentHash);
        Assert.Contains(bundle.Warnings, warning => warning.TraceId == "unsafe-trace" && warning.Location == "request.authorization" && warning.Reason == "sensitive_field_name");
        Assert.Contains(bundle.Warnings, warning => warning.TraceId == "unsafe-trace" && warning.Location == "request.authorization" && warning.Reason == "bearer_token_value");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => traces.ExportTracesAsync(new TraceExportRequest
        {
            Operation = "provider.run",
            FailOnUnsafeContent = true
        }));
        Assert.Contains("potentially unsafe", exception.Message);
    }

    [Fact]
    public async Task RetrievalService_PersistsTraceWhenTraceStoreIsConfigured()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-retrieval-traces-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();

        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "chunks",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 4 }
            }
        });

        var embeddingProvider = new DeterministicHashEmbeddingProvider(4);
        var vector = await embeddingProvider.GenerateEmbeddingAsync("hello");
        await store.UpsertRecordAsync("chunks", new VyralRecord
        {
            Id = "1",
            PartitionKey = "tenant-a",
            Content = new JsonObject { ["text"] = "hello" },
            Vectors = new Dictionary<string, VyralVector>
            {
                ["contentEmbedding"] = new() { Values = vector }
            }
        });

        var retrieval = new LocalRetrievalService(store, embeddingProvider, traces);
        var result = await retrieval.SearchAsync(new RetrievalRequest
        {
            Query = "hello",
            Collections = new List<string> { "chunks" },
            IncludeTrace = true
        });

        Assert.NotNull(result.Trace);
        var traceId = result.Trace!["id"]!.ToString();
        var loaded = await traces.GetTraceAsync(traceId!);

        Assert.NotNull(loaded);
        Assert.Equal("retrieval.search", loaded!.Operation);
    }
}
