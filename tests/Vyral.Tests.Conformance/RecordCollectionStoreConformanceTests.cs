using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Tests.Conformance;

public abstract class RecordCollectionStoreConformanceTests
{
    protected abstract Task<IRecordCollectionStore> CreateStoreAsync();

    protected async Task RunRecordStore_RoundTripsCollectionPolicyAndListsDeterministically()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "z-records",
                PartitionKeyPath = "/partitionKey",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 3 }
                }
            });
            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "a-records" });

            var policy = await store.GetCollectionPolicyAsync("z-records");
            var collections = (await store.GetCollectionsAsync()).ToList();

            Assert.NotNull(policy);
            Assert.Equal("z-records", policy.Name);
            Assert.Equal("/partitionKey", policy.PartitionKeyPath);
            var vectorPolicy = Assert.Single(policy.VectorPolicies);
            Assert.Equal("contentEmbedding", vectorPolicy.Name);
            Assert.Equal(3, vectorPolicy.Dimensions);
            Assert.Equal(new[] { "a-records", "z-records" }, collections);
        });
    }

    protected async Task RunRecordStore_AllowsIdempotentCollectionCreateAndRejectsPolicyChange()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "records",
                PartitionKeyPath = "/partitionKey",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 3 }
                }
            });

            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "records",
                PartitionKeyPath = "/partitionKey",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 3 }
                }
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateCollectionAsync(new RecordCollectionPolicy
                {
                    Name = "records",
                    PartitionKeyPath = "/partitionKey",
                    VectorPolicies = new List<VectorFieldPolicy>
                    {
                        new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 4 }
                    }
                }));
        });
    }

    protected async Task RunRecordStore_RoundTripsRecordsWithRevisionAndEtag()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "records" });

            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "1",
                PartitionKey = "tenant-a",
                Type = "chunk",
                Metadata = new JsonObject { ["status"] = "active" },
                Content = new JsonObject { ["text"] = "hello" }
            });

            var record = await store.GetRecordAsync("records", "tenant-a", "1");

            Assert.NotNull(record);
            Assert.Equal("1", record.Id);
            Assert.Equal("tenant-a", record.PartitionKey);
            Assert.Equal("rev:1", record.Etag);
            Assert.Equal(1, record.Revision);
        });
    }

    protected async Task RunRecordStore_IncrementsRevisionAndPreservesCreatedAtOnUpdate()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "records" });

            await store.UpsertRecordAsync("records", new VyralRecord { Id = "1", PartitionKey = "tenant-a" });
            var first = await store.GetRecordAsync("records", "tenant-a", "1");

            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "1",
                PartitionKey = "tenant-a",
                Content = new JsonObject { ["text"] = "updated" }
            });
            var second = await store.GetRecordAsync("records", "tenant-a", "1");

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first.CreatedAt, second.CreatedAt);
            Assert.Equal(2, second.Revision);
            Assert.Equal("rev:2", second.Etag);
        });
    }

    protected async Task RunRecordStore_BatchUpsertHonorsErrorPolicyAndRevisionSemantics()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "records" });

            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "existing",
                PartitionKey = "tenant-a",
                Content = new JsonObject { ["text"] = "first" }
            });
            var first = await store.GetRecordAsync("records", "tenant-a", "existing");
            Assert.NotNull(first);

            var stopped = await store.UpsertRecordsAsync("records", new RecordBatchUpsertRequest
            {
                ContinueOnError = false,
                Records = new List<VyralRecord>
                {
                    new()
                    {
                        Id = "existing",
                        PartitionKey = "tenant-a",
                        Content = new JsonObject { ["text"] = "updated" }
                    },
                    new() { Id = "bad/id", PartitionKey = "tenant-a" },
                    new() { Id = "after-stop", PartitionKey = "tenant-a" }
                }
            });

            Assert.Equal(3, stopped.Requested);
            Assert.Equal(2, stopped.Attempted);
            Assert.Equal(1, stopped.Succeeded);
            Assert.Equal(1, stopped.Failed);
            Assert.True(stopped.StoppedOnError);
            Assert.Equal(new[] { 0, 1 }, stopped.Items.Select(item => item.Index));
            Assert.Equal(RecordUpsertStatuses.Succeeded, stopped.Items[0].Status);
            Assert.Equal(RecordUpsertStatuses.Failed, stopped.Items[1].Status);
            Assert.Null(await store.GetRecordAsync("records", "tenant-a", "after-stop"));

            var updated = await store.GetRecordAsync("records", "tenant-a", "existing");
            Assert.NotNull(updated);
            Assert.Equal(first.CreatedAt, updated.CreatedAt);
            Assert.Equal(2, updated.Revision);
            Assert.Equal("rev:2", updated.Etag);

            var continued = await store.UpsertRecordsAsync("records", new RecordBatchUpsertRequest
            {
                ContinueOnError = true,
                Records = new List<VyralRecord>
                {
                    new() { Id = "bad/again", PartitionKey = "tenant-a" },
                    new() { Id = "after-error", PartitionKey = "tenant-a" }
                }
            });

            Assert.Equal(2, continued.Requested);
            Assert.Equal(2, continued.Attempted);
            Assert.Equal(1, continued.Succeeded);
            Assert.Equal(1, continued.Failed);
            Assert.False(continued.StoppedOnError);
            Assert.Equal(RecordUpsertStatuses.Failed, continued.Items[0].Status);
            Assert.Equal(RecordUpsertStatuses.Succeeded, continued.Items[1].Status);
            Assert.NotNull(await store.GetRecordAsync("records", "tenant-a", "after-error"));
        });
    }

    protected async Task RunRecordStore_EnforcesWritePreconditions()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "records" });

            var created = new VyralRecord
            {
                Id = "guarded",
                PartitionKey = "tenant-a",
                Content = new JsonObject { ["text"] = "first" }
            };
            await store.UpsertRecordAsync(
                "records",
                created,
                new RecordWritePrecondition { IfNoneMatch = "*" });

            Assert.Equal(1, created.Revision);
            Assert.Equal("rev:1", created.Etag);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync(
                    "records",
                    new VyralRecord { Id = "guarded", PartitionKey = "tenant-a" },
                    new RecordWritePrecondition { IfNoneMatch = "*" }));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync(
                    "records",
                    new VyralRecord { Id = "guarded", PartitionKey = "tenant-a" },
                    new RecordWritePrecondition { ExpectedRevision = 2 }));

            var updated = new VyralRecord
            {
                Id = "guarded",
                PartitionKey = "tenant-a",
                Content = new JsonObject { ["text"] = "updated" }
            };
            await store.UpsertRecordAsync(
                "records",
                updated,
                new RecordWritePrecondition { IfMatch = created.Etag });

            Assert.Equal(2, updated.Revision);
            Assert.Equal("rev:2", updated.Etag);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync(
                    "records",
                    new VyralRecord { Id = "guarded", PartitionKey = "tenant-a" },
                    new RecordWritePrecondition { ExpectedEtag = created.Etag }));

            var batch = await store.UpsertRecordsAsync("records", new RecordBatchUpsertRequest
            {
                ContinueOnError = true,
                Records = new List<VyralRecord>
                {
                    new() { Id = "batch-created", PartitionKey = "tenant-a" },
                    new() { Id = "guarded", PartitionKey = "tenant-a" }
                },
                Preconditions = new List<RecordWritePrecondition?>
                {
                    new() { IfNoneMatch = "*" },
                    new() { ExpectedRevision = 1 }
                }
            });

            Assert.Equal(2, batch.Attempted);
            Assert.Equal(1, batch.Succeeded);
            Assert.Equal(1, batch.Failed);
            Assert.Equal(RecordUpsertStatuses.Succeeded, batch.Items[0].Status);
            Assert.Equal(RecordUpsertStatuses.Failed, batch.Items[1].Status);
            Assert.Contains("precondition", batch.Items[1].Error, StringComparison.OrdinalIgnoreCase);
        });
    }

    protected async Task RunRecordStore_EnforcesConcurrentWritePreconditions()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "records" });

            const int contenderCount = 12;
            var createAttempts = await Task.WhenAll(Enumerable.Range(0, contenderCount)
                .Select(index => TryUpsertRecordAsync(
                    store,
                    "records",
                    new VyralRecord
                    {
                        Id = "create-only",
                        PartitionKey = "tenant-a",
                        Content = new JsonObject { ["attempt"] = index }
                    },
                    new RecordWritePrecondition { IfNoneMatch = "*" })));

            Assert.Equal(1, createAttempts.Count(succeeded => succeeded));
            var created = await store.GetRecordAsync("records", "tenant-a", "create-only");
            Assert.NotNull(created);
            Assert.Equal(1, created!.Revision);
            Assert.Equal("rev:1", created.Etag);

            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "compare-and-set",
                PartitionKey = "tenant-a",
                Content = new JsonObject { ["attempt"] = "seed" }
            });

            var updateAttempts = await Task.WhenAll(Enumerable.Range(0, contenderCount)
                .Select(index => TryUpsertRecordAsync(
                    store,
                    "records",
                    new VyralRecord
                    {
                        Id = "compare-and-set",
                        PartitionKey = "tenant-a",
                        Content = new JsonObject { ["attempt"] = index }
                    },
                    new RecordWritePrecondition { ExpectedRevision = 1 })));

            Assert.Equal(1, updateAttempts.Count(succeeded => succeeded));
            var updated = await store.GetRecordAsync("records", "tenant-a", "compare-and-set");
            Assert.NotNull(updated);
            Assert.Equal(2, updated!.Revision);
            Assert.Equal("rev:2", updated.Etag);
        });
    }

    protected async Task RunRecordStore_DeletesRecordsIdempotently()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "records" });
            await store.UpsertRecordAsync("records", new VyralRecord { Id = "1", PartitionKey = "tenant-a" });

            await store.DeleteRecordAsync("records", "tenant-a", "1");
            await store.DeleteRecordAsync("records", "tenant-a", "1");

            Assert.Null(await store.GetRecordAsync("records", "tenant-a", "1"));
        });
    }

    protected async Task RunRecordStore_DeletesCollectionsIdempotently()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "records",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
                }
            });
            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "1",
                PartitionKey = "tenant-a",
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["contentEmbedding"] = new() { Values = new float[] { 1, 0 } }
                }
            });

            await store.DeleteCollectionAsync("records");
            await store.DeleteCollectionAsync("records");

            var collections = await store.GetCollectionsAsync();

            Assert.Null(await store.GetCollectionPolicyAsync("records"));
            Assert.DoesNotContain("records", collections);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync("records", new VyralRecord { Id = "2", PartitionKey = "tenant-a" }));
        });
    }

    protected async Task RunRecordStore_RejectsNonPortableCollectionPolicyShape()
    {
        await WithStoreAsync(async store =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateCollectionAsync(new RecordCollectionPolicy
                {
                    Name = "records",
                    PartitionKeyPath = "/tenantId"
                }));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateCollectionAsync(new RecordCollectionPolicy
                {
                    Name = "records",
                    VectorPolicies = new List<VectorFieldPolicy>
                    {
                        new() { Name = "contentEmbedding", Path = "/embeddings/content", Dimensions = 2 }
                    }
                }));
        });
    }

    protected async Task RunRecordStore_RejectsNonPortableIdentities()
    {
        await WithStoreAsync(async store =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "bad/name" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "bad name" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateCollectionAsync(new RecordCollectionPolicy { Name = new string('a', RecordIdentityValidator.MaxCollectionNameLength + 1) }));

            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "records" });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync("records", new VyralRecord { Id = "bad/id", PartitionKey = "tenant-a" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync("records", new VyralRecord { Id = @"bad\id", PartitionKey = "tenant-a" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync("records", new VyralRecord { Id = "bad?id", PartitionKey = "tenant-a" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync("records", new VyralRecord { Id = "bad#id", PartitionKey = "tenant-a" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync("records", new VyralRecord { Id = new string('r', RecordIdentityValidator.MaxRecordIdUtf8Bytes + 1), PartitionKey = "tenant-a" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync("records", new VyralRecord { Id = "valid-id", PartitionKey = new string('p', RecordIdentityValidator.MaxPartitionKeyUtf8Bytes + 1) }));
        });
    }

    protected async Task RunRecordStore_QueriesByPortableMetadataFilter()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "records" });

            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "active",
                PartitionKey = "tenant-a",
                Metadata = new JsonObject { ["status"] = "active", ["score"] = 10 }
            });
            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "inactive",
                PartitionKey = "tenant-a",
                Metadata = new JsonObject { ["status"] = "inactive", ["score"] = 10 }
            });

            var records = (await store.QueryAllRecordsAsync("records", new QueryEnvelope
            {
                PartitionKeys = new List<string> { "tenant-a" },
                Filter = new FilterNode
                {
                    Combine = FilterCombineModes.All,
                    Children = new List<FilterNode>
                    {
                        new() { Path = "/metadata/status", Op = FilterOps.Eq, Value = "active" },
                        new() { Path = "/metadata/score", Op = FilterOps.Gte, Value = 5 }
                    }
                }
            })).ToList();

            Assert.Single(records);
            Assert.Equal("active", records[0].Id);
        });
    }

    protected async Task RunRecordStore_QueriesPortableLogicalNullAndOrderingPredicates()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "records",
                IndexedMetadata = new List<string>
                {
                    "/metadata/status",
                    "/metadata/score",
                    "/metadata/nullable"
                }
            });

            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "alpha",
                PartitionKey = "tenant-a",
                Metadata = new JsonObject
                {
                    ["status"] = "active",
                    ["score"] = 10,
                    ["nullable"] = null
                }
            });
            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "beta",
                PartitionKey = "tenant-a",
                Metadata = new JsonObject
                {
                    ["status"] = "preview",
                    ["score"] = 5
                }
            });
            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "gamma",
                PartitionKey = "tenant-a",
                Metadata = new JsonObject
                {
                    ["status"] = "inactive",
                    ["score"] = 8,
                    ["nullable"] = "set"
                }
            });
            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "outside-partition",
                PartitionKey = "tenant-b",
                Metadata = new JsonObject
                {
                    ["status"] = "active",
                    ["score"] = 99
                }
            });

            var anyOrdered = (await store.QueryAllRecordsAsync("records", new QueryEnvelope
            {
                PartitionKeys = new List<string> { "tenant-a" },
                Filter = new FilterNode
                {
                    Combine = FilterCombineModes.Any,
                    Children = new List<FilterNode>
                    {
                        new() { Path = "/metadata/status", Op = FilterOps.Eq, Value = "preview" },
                        new() { Path = "/metadata/score", Op = FilterOps.Gt, Value = 9 }
                    }
                },
                OrderBy = new List<OrderExpression> { new() { Path = "/metadata/score", Direction = SortDirections.Desc } }
            })).Select(record => record.Id).ToList();

            var missingNullable = (await store.QueryAllRecordsAsync("records", new QueryEnvelope
            {
                PartitionKeys = new List<string> { "tenant-a" },
                Filter = new FilterNode
                {
                    Combine = FilterCombineModes.All,
                    Children = new List<FilterNode>
                    {
                        new() { Path = "/metadata/status", Op = FilterOps.In, Value = new[] { "active", "preview" } },
                        new() { Path = "/metadata/nullable", Op = FilterOps.Exists, Value = false }
                    }
                }
            })).Select(record => record.Id).ToList();

            var explicitNull = (await store.QueryAllRecordsAsync("records", new QueryEnvelope
            {
                Filter = new FilterNode { Path = "/metadata/nullable", Op = FilterOps.Eq, Value = null }
            })).Select(record => record.Id).ToList();

            var nonNull = (await store.QueryAllRecordsAsync("records", new QueryEnvelope
            {
                Filter = new FilterNode { Path = "/metadata/nullable", Op = FilterOps.Neq, Value = null }
            })).Select(record => record.Id).ToList();

            Assert.Equal(new[] { "alpha", "beta" }, anyOrdered);
            Assert.Equal(new[] { "beta" }, missingNullable);
            Assert.Equal(new[] { "alpha" }, explicitNull);
            Assert.Equal(new[] { "gamma" }, nonNull);
        });
    }

    protected async Task RunRecordStore_RejectsNonScalarFilterValues()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "records",
                IndexedMetadata = new List<string> { "/metadata/status" }
            });

            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "alpha",
                PartitionKey = "tenant-a",
                Metadata = new JsonObject { ["status"] = "active" },
                Content = new JsonObject { ["text"] = "active retrieval text" }
            });

            var invalidFilters = new List<FilterNode>
            {
                new() { Path = "/metadata/status", Op = FilterOps.Eq, Value = new JsonObject { ["nested"] = "active" } },
                new() { Path = "/metadata/status", Op = FilterOps.Eq, Value = new JsonArray("active") },
                new() { Path = "/metadata/status", Op = FilterOps.In, Value = new JsonArray(new JsonObject { ["nested"] = "active" }) },
                new() { Path = "/metadata/status", Op = FilterOps.Exists, Value = new JsonObject { ["present"] = true } },
                new() { Path = "/metadata/status", Op = FilterOps.Exists, Value = "true" },
                new() { Path = "/metadata/status", Op = FilterOps.Exists, Value = 1 },
                new() { Path = "/content/text", Op = FilterOps.Contains, Value = 5 },
                new() { Path = "/metadata/status", Op = "starts_with", Value = "active" }
            };

            foreach (var filter in invalidFilters)
            {
                await Assert.ThrowsAsync<NotSupportedException>(() =>
                    store.QueryRecordsPageAsync("records", new QueryEnvelope { Filter = filter }));
            }
        });
    }

    protected async Task RunRecordStore_RejectsInvalidRecordVectors()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "records",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
                }
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync("records", new VyralRecord
                {
                    Id = "undefined",
                    PartitionKey = "tenant-a",
                    Vectors = new Dictionary<string, VyralVector>
                    {
                        ["otherEmbedding"] = new() { Values = new float[] { 1, 0 } }
                    }
                }));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertRecordAsync("records", new VyralRecord
                {
                    Id = "dimension-mismatch",
                    PartitionKey = "tenant-a",
                    Vectors = new Dictionary<string, VyralVector>
                    {
                        ["contentEmbedding"] = new() { Values = new float[] { 1, 0, 0 } }
                    }
                }));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SearchRecordsPageAsync("records", new QueryEnvelope
                {
                    Vector = new VectorSearchOptions { Field = "contentEmbedding", Value = new float[] { 1, 0, 0 }, Top = 5 }
                }));
        });
    }

    protected async Task RunRecordStore_FiltersWithPortableStringPredicates()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "records",
                IndexedMetadata = new List<string> { "/metadata/category" }
            });

            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "match",
                PartitionKey = "tenant-a",
                Metadata = new JsonObject { ["category"] = "guide:retrieval" },
                Content = new JsonObject { ["text"] = "reliable retrieval local testing" }
            });
            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "no-match",
                PartitionKey = "tenant-a",
                Metadata = new JsonObject { ["category"] = "note:retrieval" },
                Content = new JsonObject { ["text"] = "reliable local testing" }
            });

            var records = (await store.QueryAllRecordsAsync("records", new QueryEnvelope
            {
                Filter = new FilterNode
                {
                    Combine = FilterCombineModes.All,
                    Children = new List<FilterNode>
                    {
                        new() { Path = "/content/text", Op = FilterOps.Contains, Value = "retrieval" },
                        new() { Path = "/metadata/category", Op = FilterOps.StartsWith, Value = "guide:" }
                    }
                }
            })).ToList();

            Assert.Single(records);
            Assert.Equal("match", records[0].Id);
        });
    }

    protected async Task RunRecordStore_PaginatesQueriesWithContinuationToken()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "records" });

            for (var i = 1; i <= 3; i++)
            {
                await store.UpsertRecordAsync("records", new VyralRecord
                {
                    Id = $"record-{i}",
                    PartitionKey = "tenant-a"
                });
            }

            var first = await store.QueryRecordsPageAsync("records", new QueryEnvelope
            {
                Limit = 2,
                OrderBy = new List<OrderExpression> { new() { Path = "/id", Direction = SortDirections.Asc } }
            });
            var second = await store.QueryRecordsPageAsync("records", new QueryEnvelope
            {
                Limit = 2,
                ContinuationToken = first.ContinuationToken,
                OrderBy = new List<OrderExpression> { new() { Path = "/id", Direction = SortDirections.Asc } }
            });

            Assert.Equal(new[] { "record-1", "record-2" }, first.Items.Select(r => r.Id));
            Assert.NotNull(first.ContinuationToken);
            Assert.Equal(new[] { "record-3" }, second.Items.Select(r => r.Id));
            Assert.Null(second.ContinuationToken);
        });
    }

    protected async Task RunRecordStore_QueryConvenienceHonorsBoundedAndUnboundedPaging()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "records" });

            for (var i = 1; i <= 3; i++)
            {
                await store.UpsertRecordAsync("records", new VyralRecord
                {
                    Id = $"record-{i}",
                    PartitionKey = "tenant-a"
                });
            }

            var bounded = (await store.QueryRecordsPageAsync("records", new QueryEnvelope
            {
                Limit = 2,
                OrderBy = new List<OrderExpression> { new() { Path = "/id", Direction = SortDirections.Asc } }
            })).Items.Select(record => record.Id).ToList();

            var unbounded = (await store.QueryAllRecordsAsync("records", new QueryEnvelope
            {
                OrderBy = new List<OrderExpression> { new() { Path = "/id", Direction = SortDirections.Asc } }
            })).Select(record => record.Id).ToList();

            Assert.Equal(new[] { "record-1", "record-2" }, bounded);
            Assert.Equal(new[] { "record-1", "record-2", "record-3" }, unbounded);
        });
    }

    protected async Task RunRecordStore_RejectsInvalidQueryLimit()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy { Name = "records" });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.QueryRecordsPageAsync("records", new QueryEnvelope { Limit = 0 }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.QueryRecordsPageAsync("records", new QueryEnvelope { Limit = -1 }));
        });
    }

    protected async Task RunRecordStore_RejectsInvalidSearchLimitsAndVectorTop()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "records",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
                }
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SearchRecordsPageAsync("records", new QueryEnvelope
                {
                    Limit = 0,
                    Vector = new VectorSearchOptions { Field = "contentEmbedding", Value = new float[] { 1, 0 }, Top = 5 }
                }));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SearchRecordsPageAsync("records", new QueryEnvelope
                {
                    Vector = new VectorSearchOptions { Field = "contentEmbedding", Value = new float[] { 1, 0 }, Top = 0 }
                }));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SearchRecordsPageAsync("records", new QueryEnvelope
                {
                    Vector = new VectorSearchOptions { Field = "missingEmbedding", Value = new float[] { 1, 0 }, Top = 5 }
                }));
        });
    }

    protected async Task RunRecordStore_SearchesVectorsWithFilters()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "records",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
                }
            });

            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "near",
                PartitionKey = "tenant-a",
                Metadata = new JsonObject { ["status"] = "active" },
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["contentEmbedding"] = new() { Values = new float[] { 1.0f, 0.1f } }
                }
            });
            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "filtered",
                PartitionKey = "tenant-a",
                Metadata = new JsonObject { ["status"] = "inactive" },
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["contentEmbedding"] = new() { Values = new float[] { 1.0f, 0.0f } }
                }
            });

            var matches = (await store.SearchAllRecordsAsync("records", new QueryEnvelope
            {
                Filter = new FilterNode { Path = "/metadata/status", Op = FilterOps.Eq, Value = "active" },
                Vector = new VectorSearchOptions { Field = "contentEmbedding", Value = new float[] { 1.0f, 0.0f }, Top = 5 }
            })).ToList();

            Assert.Single(matches);
            Assert.Equal("near", matches[0].Record.Id);
        });
    }

    protected async Task RunRecordStore_PaginatesVectorSearchWithContinuationToken()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "records",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
                }
            });

            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "a",
                PartitionKey = "tenant-a",
                Vectors = new Dictionary<string, VyralVector> { ["contentEmbedding"] = new() { Values = new float[] { 1, 0 } } }
            });
            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "b",
                PartitionKey = "tenant-a",
                Vectors = new Dictionary<string, VyralVector> { ["contentEmbedding"] = new() { Values = new float[] { 0.9f, 0.1f } } }
            });
            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "c",
                PartitionKey = "tenant-a",
                Vectors = new Dictionary<string, VyralVector> { ["contentEmbedding"] = new() { Values = new float[] { 0.8f, 0.2f } } }
            });

            var first = await store.SearchRecordsPageAsync("records", new QueryEnvelope
            {
                Limit = 2,
                Vector = new VectorSearchOptions { Field = "contentEmbedding", Value = new float[] { 1, 0 }, Top = 3 }
            });
            var second = await store.SearchRecordsPageAsync("records", new QueryEnvelope
            {
                Limit = 2,
                ContinuationToken = first.ContinuationToken,
                Vector = new VectorSearchOptions { Field = "contentEmbedding", Value = new float[] { 1, 0 }, Top = 3 }
            });

            Assert.Equal(new[] { "a", "b" }, first.Items.Select(m => m.Record.Id));
            Assert.NotNull(first.ContinuationToken);
            Assert.Equal(new[] { "c" }, second.Items.Select(m => m.Record.Id));
            Assert.Null(second.ContinuationToken);
        });
    }

    protected async Task RunRecordStore_VectorSearchConvenienceHonorsBoundedAndUnboundedPaging()
    {
        await WithStoreAsync(async store =>
        {
            await store.CreateCollectionAsync(new RecordCollectionPolicy
            {
                Name = "records",
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
                }
            });

            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "a",
                PartitionKey = "tenant-a",
                Vectors = new Dictionary<string, VyralVector> { ["contentEmbedding"] = new() { Values = new float[] { 1, 0 } } }
            });
            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "b",
                PartitionKey = "tenant-a",
                Vectors = new Dictionary<string, VyralVector> { ["contentEmbedding"] = new() { Values = new float[] { 0.9f, 0.1f } } }
            });
            await store.UpsertRecordAsync("records", new VyralRecord
            {
                Id = "c",
                PartitionKey = "tenant-a",
                Vectors = new Dictionary<string, VyralVector> { ["contentEmbedding"] = new() { Values = new float[] { 0.8f, 0.2f } } }
            });

            var bounded = (await store.SearchRecordsPageAsync("records", new QueryEnvelope
            {
                Limit = 2,
                Vector = new VectorSearchOptions { Field = "contentEmbedding", Value = new float[] { 1, 0 }, Top = 3 }
            })).Items.Select(match => match.Record.Id).ToList();

            var unbounded = (await store.SearchAllRecordsAsync("records", new QueryEnvelope
            {
                Vector = new VectorSearchOptions { Field = "contentEmbedding", Value = new float[] { 1, 0 }, Top = 3 }
            })).Select(match => match.Record.Id).ToList();

            Assert.Equal(new[] { "a", "b" }, bounded);
            Assert.Equal(new[] { "a", "b", "c" }, unbounded);
        });
    }

    private async Task WithStoreAsync(Func<IRecordCollectionStore, Task> scenario)
    {
        var store = await CreateStoreAsync();
        try
        {
            await scenario(store);
        }
        finally
        {
            if (store is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (store is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static async Task<bool> TryUpsertRecordAsync(
        IRecordCollectionStore store,
        string collection,
        VyralRecord record,
        RecordWritePrecondition precondition)
    {
        try
        {
            await store.UpsertRecordAsync(collection, record, precondition);
            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("precondition", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }
}
