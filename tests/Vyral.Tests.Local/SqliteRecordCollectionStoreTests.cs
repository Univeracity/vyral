using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;
using System.Text.Json.Nodes;
using Xunit;

namespace Vyral.Tests.Local;

public class SqliteRecordCollectionStoreTests
{
    private static async Task<SqliteRecordCollectionStore> CreateStoreAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        return store;
    }

    [Fact]
    public void QueryBuilder_UsesMetadataProjectionForIndexedFilters()
    {
        var builder = new SqliteQueryBuilder();

        var (sql, parameters) = builder.BuildQuery(
            "items",
            new QueryEnvelope
            {
                Filter = new FilterNode
                {
                    Combine = "all",
                    Children = new List<FilterNode>
                    {
                        new() { Path = "/metadata/status", Op = "eq", Value = "active" },
                        new() { Path = "/metadata/score", Op = "gte", Value = 10 }
                    }
                }
            },
            new[] { "/metadata/status", "/metadata/score" });

        Assert.Contains("vyral_record_metadata_index", sql);
        Assert.Contains("mi.value_text", sql);
        Assert.Contains("mi.value_number", sql);
        Assert.DoesNotContain("json_extract", sql);
        Assert.Contains(parameters, parameter => parameter.ParameterName == "$p2" && parameter.Value?.ToString() == "active");
        Assert.Contains(parameters, parameter => parameter.ParameterName == "$p4" && Convert.ToDouble(parameter.Value) == 10);
    }

    [Fact]
    public void QueryBuilder_UsesMetadataProjectionForIndexedOrdering()
    {
        var builder = new SqliteQueryBuilder();

        var (sql, parameters) = builder.BuildQuery(
            "items",
            new QueryEnvelope
            {
                OrderBy = new List<OrderExpression>
                {
                    new() { Path = "/metadata/score", Direction = "desc" }
                }
            },
            new[] { "/metadata/score" });

        Assert.Contains("vyral_record_metadata_index", sql);
        Assert.Contains("COALESCE(mi.value_number, mi.value_text, mi.value_bool, mi.value_json)", sql);
        Assert.DoesNotContain("json_extract", sql);
        Assert.Contains(parameters, parameter => parameter.Value?.ToString() == "/metadata/score");
    }

    [Fact]
    public void QueryBuilder_UsesMetadataProjectionForIndexedStringPredicates()
    {
        var builder = new SqliteQueryBuilder();

        var (sql, _) = builder.BuildQuery(
            "items",
            new QueryEnvelope
            {
                Filter = new FilterNode
                {
                    Combine = "all",
                    Children = new List<FilterNode>
                    {
                        new() { Path = "/metadata/category", Op = "startsWith", Value = "guide:" },
                        new() { Path = "/metadata/title", Op = "contains", Value = "retrieval" }
                    }
                }
            },
            new[] { "/metadata/category", "/metadata/title" });

        Assert.Contains("vyral_record_metadata_index", sql);
        Assert.Contains("substr(mi.value_text", sql);
        Assert.Contains("instr(mi.value_text", sql);
        Assert.DoesNotContain("json_extract", sql);
    }

    [Fact]
    public void QueryBuilder_UsesMetadataProjectionForIndexedVectorCandidateFilters()
    {
        var builder = new SqliteQueryBuilder();

        var (sql, _) = builder.BuildVectorCandidateQuery(
            "items",
            new QueryEnvelope
            {
                Filter = new FilterNode
                {
                    Combine = "all",
                    Children = new List<FilterNode>
                    {
                        new() { Path = "/metadata/status", Op = "eq", Value = "active" },
                        new() { Path = "/metadata/score", Op = "gte", Value = 10 }
                    }
                },
                Vector = new VectorSearchOptions { Field = "vec", Value = new float[] { 1, 0 }, Top = 5 }
            },
            new[] { "/metadata/status", "/metadata/score" });

        Assert.Contains("JOIN vyral_record_vectors", sql);
        Assert.Contains("vyral_record_metadata_index", sql);
        Assert.Contains("mi.value_text", sql);
        Assert.Contains("mi.value_number", sql);
        Assert.DoesNotContain("json_extract", sql);
    }

    [Fact]
    public void QueryBuilder_UsesMetadataProjectionForIndexedLexicalCandidateFilters()
    {
        var builder = new SqliteQueryBuilder();

        var (sql, _) = builder.BuildLexicalFtsCandidateQuery(
            "items",
            new QueryEnvelope
            {
                Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
                Lexical = new LexicalSearchOptions { Query = "retrieval" },
                Limit = 25
            },
            "retrieval",
            new[] { "/metadata/status" });

        Assert.Contains("JOIN vyral_record_fts", sql);
        Assert.Contains("vyral_record_metadata_index", sql);
        Assert.Contains("mi.value_text", sql);
        Assert.DoesNotContain("json_extract", sql);
    }

    [Fact]
    public async Task CreateCollection_StoresPolicyAndListsDeterministically()
    {
        var store = await CreateStoreAsync();

        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "z-items",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "v1", Path = "/vectors/v1/values", Dimensions = 3 }
            }
        });
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "a-items" });

        var retrieved = await store.GetCollectionPolicyAsync("z-items");
        var collections = (await store.GetCollectionsAsync()).ToList();

        Assert.NotNull(retrieved);
        Assert.Equal("z-items", retrieved.Name);
        Assert.Single(retrieved.VectorPolicies);
        Assert.Equal(3, retrieved.VectorPolicies[0].Dimensions);
        Assert.Equal(new[] { "a-items", "z-items" }, collections);
    }

    [Fact]
    public async Task CreateCollection_AllowsIdempotentCreateAndRejectsPolicyChange()
    {
        var store = await CreateStoreAsync();
        var policy = new RecordCollectionPolicy
        {
            Name = "items",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "vec", Path = "/vectors/vec/values", Dimensions = 2 }
            },
            IndexedMetadata = new List<string> { "/metadata/status" }
        };

        await store.CreateCollectionAsync(policy);
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "items",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "vec", Path = "/vectors/vec/values", Dimensions = 2 }
            },
            IndexedMetadata = new List<string> { "/metadata/status" }
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "items",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "vec", Path = "/vectors/vec/values", Dimensions = 3 }
                },
                IndexedMetadata = new List<string> { "/metadata/status" }
            }));

        Assert.Contains("different policy", error.Message);
        var retrieved = await store.GetCollectionPolicyAsync("items");
        Assert.Equal(2, retrieved?.VectorPolicies.Single().Dimensions);
    }

    [Fact]
    public async Task CreateCollection_RejectsInvalidVectorPolicy()
    {
        var store = await CreateStoreAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "items",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "vec", Path = "/vectors/vec/values", Dimensions = 0 }
                }
            }));
    }

    [Fact]
    public async Task CreateCollection_RejectsNonPortablePolicyShape()
    {
        var store = await CreateStoreAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "items",
                PartitionKeyPath = "/tenantId"
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "items",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "contentEmbedding", Path = "/embeddings/content", Dimensions = 2 }
                }
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "items",
                IndexedMetadata = new List<string> { "/metadata/status", "/metadata/status" }
            }));
    }

    [Fact]
    public async Task Upsert_ValidatesVectorDimensionsDatatypeAndDistanceFunction()
    {
        var store = await CreateStoreAsync();

        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "items",
            VectorPolicies = new List<VectorFieldPolicy> { new() { Name = "vec", Path = "/vectors/vec/values", Dimensions = 2 } }
        });

        var invalidRecord = new VyralRecord
        {
            Id = "1",
            PartitionKey = "A",
            Vectors = new Dictionary<string, VyralVector>
            {
                ["vec"] = new() { Values = new float[] { 1, 2, 3 } }
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertRecordAsync("items", invalidRecord));
    }

    [Fact]
    public async Task Upsert_IncrementsRevisionFromStoredState()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "items" });

        await store.UpsertRecordAsync("items", new VyralRecord { Id = "1", PartitionKey = "A" });
        await store.UpsertRecordAsync("items", new VyralRecord { Id = "1", PartitionKey = "A" });

        var record = await store.GetRecordAsync("items", "A", "1");

        Assert.Equal(2, record?.Revision);
        Assert.Equal("rev:2", record?.Etag);
        Assert.NotNull(record?.CreatedAt);
        Assert.NotNull(record?.UpdatedAt);
    }

    [Fact]
    public async Task QueryRecords_SupportsJsonPointerFiltersAndInOperator()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "items" });

        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "1",
            PartitionKey = "A",
            Metadata = new JsonObject { ["status"] = "active", ["score"] = 10 }
        });
        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "2",
            PartitionKey = "A",
            Metadata = new JsonObject { ["status"] = "inactive", ["score"] = 2 }
        });
        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "3",
            PartitionKey = "B",
            Metadata = new JsonObject { ["status"] = "preview", ["score"] = 7 }
        });

        var query = new QueryEnvelope
        {
            Filter = new FilterNode
            {
                Combine = "all",
                Children = new List<FilterNode>
                {
                    new() { Path = "/metadata/status", Op = "in", Value = new[] { "active", "preview" } },
                    new() { Path = "/metadata/score", Op = "gte", Value = 7 }
                }
            },
            OrderBy = new List<OrderExpression> { new() { Path = "/id", Direction = "asc" } }
        };

        var results = (await store.QueryRecordsAsync("items", query)).Select(r => r.Id).ToList();

        Assert.Equal(new[] { "1", "3" }, results);
    }

    [Fact]
    public async Task QueryRecords_RejectsObjectFilterValues()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "items" });

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.QueryRecordsAsync("items", new QueryEnvelope
            {
                Filter = new FilterNode
                {
                    Path = "/metadata/status",
                    Op = "eq",
                    Value = new JsonObject { ["nested"] = true }
                }
            }));

        Assert.Contains("scalar JSON values", error.Message);
    }

    [Fact]
    public async Task QueryRecords_OrdersByIndexedMetadataProjection()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "items",
            IndexedMetadata = new List<string> { "/metadata/score" }
        });

        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "low",
            PartitionKey = "A",
            Metadata = new JsonObject { ["score"] = 1 }
        });
        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "high",
            PartitionKey = "A",
            Metadata = new JsonObject { ["score"] = 10 }
        });
        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "mid",
            PartitionKey = "A",
            Metadata = new JsonObject { ["score"] = 5 }
        });

        var results = (await store.QueryRecordsAsync("items", new QueryEnvelope
        {
            OrderBy = new List<OrderExpression> { new() { Path = "/metadata/score", Direction = "desc" } }
        })).Select(record => record.Id).ToList();

        Assert.Equal(new[] { "high", "mid", "low" }, results);
    }

    [Fact]
    public async Task QueryRecords_DistinguishesNullFromMissingForExistsAndEqNull()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "items" });

        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "null",
            PartitionKey = "A",
            Metadata = new JsonObject { ["nullable"] = null! }
        });
        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "missing",
            PartitionKey = "A",
            Metadata = new JsonObject { ["status"] = "active" }
        });

        var exists = (await store.QueryRecordsAsync("items", new QueryEnvelope
        {
            Filter = new FilterNode { Path = "/metadata/nullable", Op = "exists", Value = true }
        })).Select(r => r.Id).ToList();
        var existsFactory = (await store.QueryRecordsAsync("items", new QueryEnvelope
        {
            Filter = FilterNode.Exists("/metadata/nullable")
        })).Select(r => r.Id).ToList();
        var eqNull = (await store.QueryRecordsAsync("items", new QueryEnvelope
        {
            Filter = new FilterNode { Path = "/metadata/nullable", Op = "eq", Value = null }
        })).Select(r => r.Id).ToList();
        var missing = (await store.QueryRecordsAsync("items", new QueryEnvelope
        {
            Filter = new FilterNode { Path = "/metadata/nullable", Op = "exists", Value = false }
        })).Select(r => r.Id).ToList();

        Assert.Equal(new[] { "null" }, exists);
        Assert.Equal(new[] { "null" }, existsFactory);
        Assert.Equal(new[] { "null" }, eqNull);
        Assert.Equal(new[] { "missing" }, missing);
    }

    [Fact]
    public async Task QueryRecords_SupportsStringPredicates()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "items",
            IndexedMetadata = new List<string> { "/metadata/category" }
        });

        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "match",
            PartitionKey = "A",
            Metadata = new JsonObject { ["category"] = "guide:retrieval" },
            Content = new JsonObject { ["text"] = "reliable retrieval local testing" }
        });
        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "no-match",
            PartitionKey = "A",
            Metadata = new JsonObject { ["category"] = "note:retrieval" },
            Content = new JsonObject { ["text"] = "reliable local testing" }
        });

        var results = (await store.QueryRecordsAsync("items", new QueryEnvelope
        {
            Filter = new FilterNode
            {
                Combine = "all",
                Children = new List<FilterNode>
                {
                    new() { Path = "/content/text", Op = "contains", Value = "retrieval" },
                    new() { Path = "/metadata/category", Op = "startsWith", Value = "guide:" }
                }
            }
        })).Select(record => record.Id).ToList();

        Assert.Equal(new[] { "match" }, results);
    }

    [Fact]
    public async Task SearchRecords_AppliesMetadataFiltersAndRejectsDimensionMismatch()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "items",
            VectorPolicies = new List<VectorFieldPolicy> { new() { Name = "vec", Path = "/vectors/vec/values", Dimensions = 2 } }
        });

        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "near-active",
            PartitionKey = "A",
            Metadata = new JsonObject { ["status"] = "active" },
            Vectors = new Dictionary<string, VyralVector> { ["vec"] = new() { Values = new float[] { 1.0f, 0.1f } } }
        });
        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "near-inactive",
            PartitionKey = "A",
            Metadata = new JsonObject { ["status"] = "inactive" },
            Vectors = new Dictionary<string, VyralVector> { ["vec"] = new() { Values = new float[] { 1.0f, 0.0f } } }
        });

        var query = new QueryEnvelope
        {
            Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
            Vector = new VectorSearchOptions { Field = "vec", Value = new float[] { 1.0f, 0.0f }, Top = 5 }
        };

        var results = (await store.SearchRecordsAsync("items", query)).ToList();

        Assert.Single(results);
        Assert.Equal("near-active", results[0].Record.Id);

        query.Vector.Value = new float[] { 1.0f, 0.0f, 0.0f };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SearchRecordsAsync("items", query));
    }

    [Fact]
    public async Task SearchRecords_SupportsLexicalSearchOverRecordFields()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "pages",
            IndexedMetadata = new List<string> { "/metadata/status" }
        });

        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "page-001",
            PartitionKey = "case-a",
            Metadata = new JsonObject
            {
                ["status"] = "active",
                ["referenceId"] = "RECORD-000123"
            },
            Content = new JsonObject
            {
                ["text"] = "RECORD-000123 discusses the exact update deadline."
            }
        });
        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "page-002",
            PartitionKey = "case-a",
            Metadata = new JsonObject
            {
                ["status"] = "active",
                ["referenceId"] = "RECORD-000456"
            },
            Content = new JsonObject
            {
                ["text"] = "This page discusses unrelated scheduling context."
            }
        });

        var results = (await store.SearchRecordsAsync("pages", new QueryEnvelope
        {
            Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
            Lexical = new LexicalSearchOptions
            {
                Query = "RECORD-000123 update deadline",
                Fields = new List<string> { "/content/text", "/metadata/referenceId" },
                MinScore = 0.5f
            },
            Limit = 5
        })).ToList();

        var match = Assert.Single(results);
        Assert.Equal("page-001", match.Record.Id);
        Assert.NotNull(match.Diagnostics);
        Assert.Contains("/metadata/referenceId", match.Diagnostics!.MatchedFields);
        Assert.Contains("lexical", match.Diagnostics.CandidateSources);
        Assert.Equal("pages", match.Diagnostics.ResultIdentity!.Collection);
        Assert.Equal("page-001", match.Diagnostics.ResultIdentity.Id);
        Assert.Equal("lexical.score", match.Diagnostics.ScoreNormalization!.FinalScoreKind);
        Assert.Equal("lexical.bm25", match.Diagnostics.ScoreNormalization.LexicalScoreKind);
        Assert.Equal(2, match.Diagnostics.CandidateCounts["searchCandidatePool"]);
        Assert.Equal(1, match.Diagnostics.CandidateCounts["returnedCandidates"]);
        Assert.Contains("candidate.source.lexical", match.Diagnostics.ReasonCodes);
    }

    [Fact]
    public async Task SearchRecords_UsesFtsCandidatesBeyondScanOrder()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-fts-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "pages" });

        for (var index = 0; index < 20; index++)
        {
            await store.UpsertRecordAsync("pages", new VyralRecord
            {
                Id = $"page-{index:00}",
                PartitionKey = "case-a",
                Content = new JsonObject
                {
                    ["text"] = "Routine scheduling background without the target phrase."
                }
            });
        }

        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "zz-target",
            PartitionKey = "case-a",
            Content = new JsonObject
            {
                ["text"] = "This administrative record contains raretokenalpha for retrieval."
            }
        });

        var results = (await store.SearchRecordsAsync("pages", new QueryEnvelope
        {
            Lexical = new LexicalSearchOptions
            {
                Query = "raretokenalpha",
                Fields = new List<string> { "/content/text" },
                ScanLimit = 3,
                Top = 5
            },
            Limit = 5
        })).ToList();

        var match = Assert.Single(results);
        Assert.Equal("zz-target", match.Record.Id);
        Assert.NotNull(match.Diagnostics);
        Assert.Equal("sqlite_fts5", match.Diagnostics!.Details["lexicalCandidateSource"]);
        Assert.Equal(1, Convert.ToInt32(match.Diagnostics.Details["lexicalCandidateCount"]));

        var allTermResults = (await store.SearchRecordsAsync("pages", new QueryEnvelope
        {
            Lexical = new LexicalSearchOptions
            {
                Query = "administrative raretokenalpha",
                Fields = new List<string> { "/content/text" },
                MatchMode = "all",
                ScanLimit = 3,
                Top = 5
            },
            Limit = 5
        })).ToList();

        Assert.Equal("zz-target", Assert.Single(allTermResults).Record.Id);

        await store.DeleteRecordAsync("pages", "case-a", "zz-target");

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        Assert.Equal(20, await CountRowsAsync(connection, "vyral_record_fts"));
    }

    [Fact]
    public async Task SearchRecords_UsesQuotedPhraseForFtsCandidatesAndScoring()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "pages" });

        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "phrase-target",
            PartitionKey = "case-a",
            Content = new JsonObject
            {
                ["text"] = "The order sets a preliminary injunction deadline for briefing."
            }
        });
        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "phrase-noise",
            PartitionKey = "case-a",
            Content = new JsonObject
            {
                ["text"] = "The injunction was mentioned before a separate preliminary deadline."
            }
        });

        var results = (await store.SearchRecordsAsync("pages", new QueryEnvelope
        {
            Lexical = new LexicalSearchOptions
            {
                Query = "\"preliminary injunction\"",
                Fields = new List<string> { "/content/text" },
                Top = 5,
                ScanLimit = 1
            },
            Limit = 5
        })).ToList();

        var match = Assert.Single(results);
        Assert.Equal("phrase-target", match.Record.Id);
        Assert.NotNull(match.Diagnostics);
        Assert.Equal("sqlite_fts5", match.Diagnostics!.Details["lexicalCandidateSource"]);
        Assert.Equal("\"preliminary injunction\"", match.Diagnostics.Details["lexicalFtsExpression"]);
        var matchedPhrases = Assert.IsType<List<string>>(match.Diagnostics.Details["matchedPhrases"]);
        Assert.Contains("preliminary injunction", matchedPhrases);
        Assert.True(match.Diagnostics.ScoreComponents["phraseBoost"] > 0);
    }

    [Fact]
    public async Task SearchRecords_RequiredPhraseGroupsConstrainFtsCandidatesAndSupportAlternatives()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "pages" });

        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "wrong-aspect",
            PartitionKey = "case-a",
            Content = new JsonObject { ["text"] = "Browser network diagnostics covers throughput stability." }
        });
        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "target",
            PartitionKey = "case-a",
            Content = new JsonObject { ["text"] = "Browser network diagnostics includes access latency measurements." }
        });

        var results = (await store.SearchRecordsAsync("pages", new QueryEnvelope
        {
            Lexical = new LexicalSearchOptions
            {
                Query = "browser diagnostics latency",
                Fields = new List<string> { "/content/text" },
                RequiredPhraseGroups = new List<List<string>>
                {
                    new() { "browser network diagnostics" },
                    new() { "loaded latency", "access latency" }
                },
                ScanLimit = 1,
                Top = 5
            },
            Limit = 5
        })).ToList();

        var match = Assert.Single(results);
        Assert.Equal("target", match.Record.Id);
        Assert.NotNull(match.Diagnostics);
        Assert.Equal("sqlite_fts5", match.Diagnostics!.Details["lexicalCandidateSource"]);
        Assert.Contains("browser network diagnostics", match.Diagnostics.Details["lexicalFtsExpression"]!.ToString());
        var matchedGroups = Assert.IsType<List<List<string>>>(match.Diagnostics.Details["matchedRequiredPhraseGroups"]);
        Assert.Equal(new[] { "browser network diagnostics" }, matchedGroups[0]);
        Assert.Equal(new[] { "access latency" }, matchedGroups[1]);
    }

    [Fact]
    public async Task SearchRecords_RequiredPhraseGroupsDoNotConsumeFtsScanLimitAcrossJsonArrayValues()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "pages" });

        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "a-split-values",
            PartitionKey = "case-a",
            Content = new JsonObject
            {
                ["aliases"] = new JsonArray("browser network", "diagnostics access", "latency")
            }
        });
        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "z-atomic-target",
            PartitionKey = "case-a",
            Content = new JsonObject
            {
                ["aliases"] = new JsonArray("browser network diagnostics", "access latency")
            }
        });

        var results = (await store.SearchRecordsAsync("pages", new QueryEnvelope
        {
            Lexical = new LexicalSearchOptions
            {
                Query = "browser diagnostics latency",
                Fields = new List<string> { "/content/aliases" },
                RequiredPhraseGroups = new List<List<string>>
                {
                    new() { "browser network diagnostics" },
                    new() { "access latency" }
                },
                ScanLimit = 1,
                Top = 5
            },
            Limit = 5
        })).ToList();

        var match = Assert.Single(results);
        Assert.Equal("z-atomic-target", match.Record.Id);
        Assert.Equal(1, match.Diagnostics!.Details["lexicalCandidateCount"]);
        var matchedGroups = Assert.IsType<List<List<string>>>(match.Diagnostics.Details["matchedRequiredPhraseGroups"]);
        Assert.Equal(new[] { "browser network diagnostics" }, matchedGroups[0]);
        Assert.Equal(new[] { "access latency" }, matchedGroups[1]);
    }

    [Fact]
    public async Task InitializeAsync_RebuildsExistingFtsRowsWithAtomicJsonValueBoundaries()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "pages" });

        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "a-split-values",
            PartitionKey = "case-a",
            Content = new JsonObject
            {
                ["aliases"] = new JsonArray("browser network", "diagnostics access", "latency")
            }
        });
        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "z-atomic-target",
            PartitionKey = "case-a",
            Content = new JsonObject
            {
                ["aliases"] = new JsonArray("browser network diagnostics", "access latency")
            }
        });

        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM vyral_record_fts;
                INSERT INTO vyral_record_fts (collection, partitionKey, record_id, text)
                SELECT collection, partitionKey, id, content_json FROM vyral_records;
                DELETE FROM vyral_migrations WHERE id = 'fts:atomic-json-values:1';";
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = new SqliteRecordCollectionStore(dbPath);
        await upgraded.InitializeAsync();

        var results = (await upgraded.SearchRecordsAsync("pages", new QueryEnvelope
        {
            Lexical = new LexicalSearchOptions
            {
                Query = "browser diagnostics latency",
                Fields = new List<string> { "/content/aliases" },
                RequiredPhraseGroups = new List<List<string>>
                {
                    new() { "browser network diagnostics" },
                    new() { "access latency" }
                },
                ScanLimit = 1,
                Top = 5
            },
            Limit = 5
        })).ToList();

        Assert.Equal("z-atomic-target", Assert.Single(results).Record.Id);
    }

    [Fact]
    public async Task SearchRecords_RequiredPhraseGroupsRejectEmptyPhrases()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "pages" });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SearchRecordsAsync("pages", new QueryEnvelope
        {
            Lexical = new LexicalSearchOptions
            {
                Query = "browser diagnostics",
                RequiredPhraseGroups = new List<List<string>> { new() { " " } }
            }
        }));

        Assert.Contains("required phrases", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchRecords_CanUsePrefixMatchingForPartialTerms()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "pages" });

        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "prefix-target",
            PartitionKey = "case-a",
            Content = new JsonObject
            {
                ["text"] = "The preliminary injunction deadline appears in the administrative record."
            }
        });
        await store.UpsertRecordAsync("pages", new VyralRecord
        {
            Id = "prefix-noise",
            PartitionKey = "case-a",
            Content = new JsonObject
            {
                ["text"] = "The preliminary scheduling order omits the requested deadline."
            }
        });

        var results = (await store.SearchRecordsAsync("pages", new QueryEnvelope
        {
            Lexical = new LexicalSearchOptions
            {
                Query = "prelim injunc deadl",
                Fields = new List<string> { "/content/text" },
                MatchMode = "all",
                PrefixMatching = true,
                PrefixMinChars = 3,
                Top = 5,
                ScanLimit = 1
            },
            Limit = 5
        })).ToList();

        var match = Assert.Single(results);
        Assert.Equal("prefix-target", match.Record.Id);
        Assert.NotNull(match.Diagnostics);
        Assert.Equal("prelim* AND injunc* AND deadl*", match.Diagnostics!.Details["lexicalFtsExpression"]);
        Assert.True((bool)match.Diagnostics.Details["lexicalPrefixMatching"]!);
        var matchedPrefixTerms = Assert.IsType<List<string>>(match.Diagnostics.Details["matchedPrefixTerms"]);
        Assert.Contains("prelim", matchedPrefixTerms);
        Assert.Contains("injunc", matchedPrefixTerms);
        Assert.Contains("deadl", matchedPrefixTerms);
    }

    [Fact]
    public async Task SearchRecords_UsesConfiguredDistanceFunctionForRanking()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "dot-items",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "vec", Path = "/vectors/vec/values", Dimensions = 2, DistanceFunction = "dotProduct" }
            }
        });
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "euclidean-items",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "vec", Path = "/vectors/vec/values", Dimensions = 2, DistanceFunction = "euclidean" }
            }
        });

        await store.UpsertRecordAsync("dot-items", new VyralRecord
        {
            Id = "small-magnitude",
            PartitionKey = "A",
            Vectors = new Dictionary<string, VyralVector>
            {
                ["vec"] = new() { Values = new float[] { 1.0f, 0.0f }, DistanceFunction = "dotProduct" }
            }
        });
        await store.UpsertRecordAsync("dot-items", new VyralRecord
        {
            Id = "large-magnitude",
            PartitionKey = "A",
            Vectors = new Dictionary<string, VyralVector>
            {
                ["vec"] = new() { Values = new float[] { 10.0f, 0.0f }, DistanceFunction = "dotProduct" }
            }
        });
        await store.UpsertRecordAsync("euclidean-items", new VyralRecord
        {
            Id = "close",
            PartitionKey = "A",
            Vectors = new Dictionary<string, VyralVector>
            {
                ["vec"] = new() { Values = new float[] { 1.2f, 0.0f }, DistanceFunction = "euclidean" }
            }
        });
        await store.UpsertRecordAsync("euclidean-items", new VyralRecord
        {
            Id = "far",
            PartitionKey = "A",
            Vectors = new Dictionary<string, VyralVector>
            {
                ["vec"] = new() { Values = new float[] { 10.0f, 0.0f }, DistanceFunction = "euclidean" }
            }
        });

        var dotMatches = (await store.SearchRecordsAsync("dot-items", new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "vec", Value = new float[] { 1.0f, 0.0f }, Top = 2 }
        })).ToList();
        var euclideanMatches = (await store.SearchRecordsAsync("euclidean-items", new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "vec", Value = new float[] { 1.0f, 0.0f }, Top = 2 }
        })).ToList();

        Assert.Equal(new[] { "large-magnitude", "small-magnitude" }, dotMatches.Select(match => match.Record.Id));
        Assert.Equal(new[] { "close", "far" }, euclideanMatches.Select(match => match.Record.Id));
        Assert.True(euclideanMatches[0].Score > euclideanMatches[1].Score);
    }

    [Fact]
    public async Task Upsert_RemovesStaleVectorsWhenRecordNoLongerContainsVector()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "items",
            VectorPolicies = new List<VectorFieldPolicy> { new() { Name = "vec", Path = "/vectors/vec/values", Dimensions = 2 } }
        });

        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "1",
            PartitionKey = "A",
            Vectors = new Dictionary<string, VyralVector> { ["vec"] = new() { Values = new float[] { 1, 0 } } }
        });
        await store.UpsertRecordAsync("items", new VyralRecord { Id = "1", PartitionKey = "A" });

        var results = await store.SearchRecordsAsync("items", new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "vec", Value = new float[] { 1, 0 }, Top = 5 }
        });

        Assert.Empty(results);
    }

    [Fact]
    public async Task Upsert_AllowsConcurrentIndependentWrites()
    {
        var store = await CreateStoreAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "items" });

        var writes = Enumerable.Range(0, 20).Select(index =>
            store.UpsertRecordAsync("items", new VyralRecord
            {
                Id = $"record-{index:00}",
                PartitionKey = "A",
                Content = new JsonObject { ["index"] = index }
            }));

        await Task.WhenAll(writes);

        var records = (await store.QueryRecordsAsync("items", new QueryEnvelope
        {
            OrderBy = new List<OrderExpression> { new() { Path = "/id", Direction = "asc" } }
        })).ToList();

        Assert.Equal(20, records.Count);
        Assert.Equal("record-00", records[0].Id);
        Assert.Equal("record-19", records[^1].Id);
    }

    [Fact]
    public async Task DeleteCollection_RemovesRecordsVectorsAndMetadataIndexes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-delete-collection-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "items",
            IndexedMetadata = new List<string> { "/metadata/status" },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
            }
        });
        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "1",
            PartitionKey = "A",
            Metadata = new JsonObject { ["status"] = "active" },
            Vectors = new Dictionary<string, VyralVector>
            {
                ["contentEmbedding"] = new() { Values = new float[] { 1, 0 } }
            }
        });

        await store.DeleteCollectionAsync("items");
        await store.DeleteCollectionAsync("items");

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        Assert.Null(await store.GetCollectionPolicyAsync("items"));
        Assert.Empty(await store.GetCollectionsAsync());
        Assert.Equal(0, await CountRowsAsync(connection, "vyral_collections"));
        Assert.Equal(0, await CountRowsAsync(connection, "vyral_records"));
        Assert.Equal(0, await CountRowsAsync(connection, "vyral_record_vectors"));
        Assert.Equal(0, await CountRowsAsync(connection, "vyral_record_metadata_index"));
        Assert.Equal(0, await CountRowsAsync(connection, "vyral_record_fts"));
    }

    [Fact]
    public async Task DeleteRecord_RemovesVectorsMetadataIndexesAndFtsRows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-delete-record-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "items",
            IndexedMetadata = new List<string> { "/metadata/status" },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
            }
        });
        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "1",
            PartitionKey = "A",
            Metadata = new JsonObject { ["status"] = "active" },
            Content = new JsonObject { ["text"] = "reliable local retrieval" },
            Vectors = new Dictionary<string, VyralVector>
            {
                ["contentEmbedding"] = new() { Values = new float[] { 1, 0 } }
            }
        });

        await store.DeleteRecordAsync("items", "A", "1");
        await store.DeleteRecordAsync("items", "A", "1");

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        Assert.Null(await store.GetRecordAsync("items", "A", "1"));
        Assert.Equal(0, await CountRowsAsync(connection, "vyral_records"));
        Assert.Equal(0, await CountRowsAsync(connection, "vyral_record_vectors"));
        Assert.Equal(0, await CountRowsAsync(connection, "vyral_record_metadata_index"));
        Assert.Equal(0, await CountRowsAsync(connection, "vyral_record_fts"));
    }

    [Fact]
    public async Task Initialize_RecordsSchemaMigrationVersion()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-migrations-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM vyral_migrations WHERE id IN ('schema:1', 'schema:2');";

        var count = (long)(await command.ExecuteScalarAsync())!;
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Upsert_ProjectsConfiguredMetadataIndexes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-metadata-index-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "items",
            IndexedMetadata = new List<string> { "/metadata/status", "/metadata/score", "/type" }
        });

        await store.UpsertRecordAsync("items", new VyralRecord
        {
            Id = "1",
            PartitionKey = "A",
            Type = "chunk",
            Metadata = new JsonObject
            {
                ["status"] = "active",
                ["score"] = 42
            }
        });

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT path, value_text, value_number
            FROM vyral_record_metadata_index
            WHERE collection = 'items' AND partitionKey = 'A' AND record_id = '1'
            ORDER BY path;";

        var rows = new List<(string Path, string? Text, double? Number)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2)));
        }

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, row => row.Path == "/metadata/status" && row.Text == "active");
        Assert.Contains(rows, row => row.Path == "/metadata/score" && row.Number == 42);
        Assert.Contains(rows, row => row.Path == "/type" && row.Text == "chunk");
    }

    private static async Task<long> CountRowsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
