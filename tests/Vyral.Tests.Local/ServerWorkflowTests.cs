using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Embeddings.Onnx;
using Vyral.Execution;
using Vyral.Local;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;
using Vyral.Providers.Onnx;
using Vyral.Primitives;
using Vyral.Server;

namespace Vyral.Tests.Local;

public class ServerWorkflowTests
{
    [Fact]
    public async Task Server_RunsRecordQuerySearchAndObjectWorkflow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            IndexedMetadata = new List<string> { "/metadata/status" },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
            }
        });
        await EnsureSuccessAsync(create);

        await UpsertAsync(client, "near", "active", new float[] { 1, 0 });
        await UpsertAsync(client, "far", "active", new float[] { 0, 1 });
        await UpsertAsync(client, "inactive", "inactive", new float[] { 1, 0 });

        var export = await client.GetFromJsonAsync<CollectionExportEnvelope>("/collections/chunks/export");
        Assert.NotNull(export);
        Assert.Equal("chunks", export!.Collection);
        Assert.Equal("chunks", export.Policy.Name);
        Assert.Equal(new[] { "far", "inactive", "near" }, export.Records.Select(record => record.Id));
        Assert.Equal(3, export.RecordCount);
        Assert.StartsWith("sha256:", export.ContentHash);
        Assert.NotNull(export.ExportedAt);

        var boundedFailure = await client.PostAsJsonAsync("/collections/chunks/export", new CollectionExportRequest
        {
            MaxRecords = 2
        });
        Assert.Equal(HttpStatusCode.BadRequest, boundedFailure.StatusCode);

        var boundedExport = await PostJsonAsync<CollectionExportEnvelope>(client, "/collections/chunks/export", new CollectionExportRequest
        {
            MaxRecords = 2,
            FailOnLimitExceeded = false
        });
        Assert.Equal(2, boundedExport.RecordCount);
        Assert.Equal(2, boundedExport.MaxRecords);
        Assert.True(boundedExport.Truncated);
        Assert.NotNull(boundedExport.ContinuationToken);
        Assert.StartsWith("sha256:", boundedExport.ContentHash);

        var partialImportDenied = await client.PostAsJsonAsync("/collections/chunks-partial-denied/import", new CollectionImportRequest
        {
            Snapshot = boundedExport,
            AllowCollectionRename = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, partialImportDenied.StatusCode);

        var partialImport = await PostJsonAsync<CollectionImportResult>(client, "/collections/chunks-partial/import", new CollectionImportRequest
        {
            Snapshot = boundedExport,
            AllowCollectionRename = true,
            AllowPartialSnapshot = true
        });
        Assert.Equal(2, partialImport.Records.Succeeded);

        var import = await PostJsonAsync<CollectionImportResult>(client, "/collections/chunks-copy/import", new CollectionImportRequest
        {
            Snapshot = export,
            AllowCollectionRename = true
        });
        Assert.Equal("chunks-copy", import.Collection);
        Assert.Equal("chunks", import.SourceCollection);
        Assert.Equal(CollectionImportPolicyStatuses.Created, import.PolicyStatus);
        Assert.Equal(3, import.RecordCount);
        Assert.Equal(export.ContentHash, import.ContentHash);
        Assert.True(import.ContentHashComparison.Matches);
        Assert.Equal(3, import.Records.Succeeded);

        var reimport = await PostJsonAsync<CollectionImportResult>(client, "/collections/chunks-copy/import", new CollectionImportRequest
        {
            Snapshot = export,
            AllowCollectionRename = true
        });
        Assert.Equal(CollectionImportPolicyStatuses.ExistingEquivalent, reimport.PolicyStatus);
        Assert.Equal(3, reimport.Records.Succeeded);

        var copiedPolicy = await client.GetFromJsonAsync<RecordCollectionPolicy>("/collections/chunks-copy");
        Assert.Equal("chunks-copy", copiedPolicy!.Name);
        var copiedPage = await PostJsonAsync<RecordQueryResult>(client, "/collections/chunks-copy/query", new QueryEnvelope());
        Assert.Equal(new[] { "far", "inactive", "near" }, copiedPage.Items.Select(record => record.Id));

        var renamedWithoutPermission = await client.PostAsJsonAsync("/collections/chunks-copy-denied/import", new CollectionImportRequest
        {
            Snapshot = export
        });
        Assert.Equal(HttpStatusCode.BadRequest, renamedWithoutPermission.StatusCode);

        var mismatch = await client.PostAsJsonAsync("/collections/chunks-bad/import", new CollectionImportRequest
        {
            Snapshot = export,
            AllowCollectionRename = true,
            ExpectedContentHash = "sha256:not-the-export-hash"
        });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        var firstPage = await PostJsonAsync<RecordQueryResult>(client, "/collections/chunks/query", new QueryEnvelope
        {
            Limit = 1,
            OrderBy = new List<OrderExpression> { new() { Path = "/id", Direction = "asc" } }
        });
        var secondPage = await PostJsonAsync<RecordQueryResult>(client, "/collections/chunks/query", new QueryEnvelope
        {
            Limit = 2,
            ContinuationToken = firstPage.ContinuationToken,
            OrderBy = new List<OrderExpression> { new() { Path = "/id", Direction = "asc" } }
        });

        Assert.Equal(new[] { "far" }, firstPage.Items.Select(record => record.Id));
        Assert.NotNull(firstPage.ContinuationToken);
        Assert.Equal(new[] { "inactive", "near" }, secondPage.Items.Select(record => record.Id));
        Assert.Null(secondPage.ContinuationToken);

        var search = await PostJsonAsync<RecordSearchResult>(client, "/collections/chunks/search", new QueryEnvelope
        {
            Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
            Vector = new VectorSearchOptions { Field = "contentEmbedding", Value = new float[] { 1, 0 }, Top = 2 }
        });

        Assert.Equal(new[] { "near", "far" }, search.Items.Select(match => match.Record.Id));
        var searchDiagnostics = search.Items[0].Diagnostics;
        Assert.NotNull(searchDiagnostics);
        Assert.Equal("chunks", searchDiagnostics!.ResultIdentity!.Collection);
        Assert.Equal("near", searchDiagnostics.ResultIdentity.Id);
        Assert.Equal("vector.raw_similarity", searchDiagnostics.ScoreNormalization!.FinalScoreKind);
        Assert.Equal(2, searchDiagnostics.CandidateCounts["searchCandidatePool"]);
        Assert.Equal(2, searchDiagnostics.CandidateCounts["returnedCandidates"]);
        Assert.Contains("candidate.source.vector", searchDiagnostics.ReasonCodes);
        Assert.Equal("sqlite-flat-scan", ((JsonElement)searchDiagnostics.Details["vectorIndexProvider"]!).GetString());
        Assert.False(((JsonElement)searchDiagnostics.Details["vectorIndexUsed"]!).GetBoolean());
        Assert.Equal("local_exact_scan", ((JsonElement)searchDiagnostics.Details["vectorIndexReason"]!).GetString());

        using var put = new HttpRequestMessage(HttpMethod.Put, "/objects/objects/docs/a.txt")
        {
            Content = new StringContent("hello", Encoding.UTF8, "text/plain")
        };
        put.Headers.Add("X-Vyral-Meta-kind", "sample");
        var putObject = await client.SendAsync(put);
        await EnsureSuccessAsync(putObject);

        var listed = await client.GetFromJsonAsync<ObjectListResult>("/objects/objects?prefix=docs/&limit=1");
        var getObject = await client.GetAsync("/objects/objects/docs/a.txt");
        var content = await getObject.Content.ReadAsStringAsync();

        Assert.NotNull(listed);
        Assert.Equal(new[] { "docs/a.txt" }, listed.Items.Select(item => item.Key));
        Assert.Equal("hello", content);
        Assert.Equal("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", getObject.Headers.GetValues("X-Vyral-Content-Hash").Single());
        Assert.Equal("sample", getObject.Headers.GetValues("X-Vyral-Meta-kind").Single());
    }

    [Fact]
    public async Task Server_ImportsAndExportsGraphEnvelopeOverCollectionRecords()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-graph-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var shapes = await client.GetFromJsonAsync<List<VyralGraphProviderShape>>("/graph/provider-shapes");
        Assert.NotNull(shapes);
        Assert.Contains(shapes!, shape => shape.ProviderId == VyralGraphProviderShapeIds.VyralCollection);

        var envelope = CreateServerGraphEnvelope();
        var preflight = await PostJsonAsync<VyralGraphCollectionImportPreflightResult>(client, "/collections/graphs/graph/import/preflight", new VyralGraphCollectionImportRequest
        {
            Envelope = envelope
        });

        Assert.True(preflight.ReadyToImport);
        Assert.True(preflight.WouldCreateCollection);
        Assert.Equal(5, preflight.RecordCount);

        var import = await PostJsonAsync<VyralGraphCollectionImportResult>(client, "/collections/graphs/graph/import", new VyralGraphCollectionImportRequest
        {
            Envelope = envelope
        });

        Assert.Equal("graphs", import.Collection);
        Assert.Equal("example-graph", import.GraphId);
        Assert.Equal(VyralGraphImportPolicyStatuses.Created, import.PolicyStatus);
        Assert.Equal(5, import.RecordCount);
        Assert.Equal(5, import.Records.Succeeded);

        var policy = await client.GetFromJsonAsync<RecordCollectionPolicy>("/collections/graphs");
        Assert.NotNull(policy);
        Assert.Contains(VyralGraphMetadataPaths.GraphId, policy!.IndexedMetadata);
        Assert.Contains(VyralGraphMetadataPaths.Predicate, policy.IndexedMetadata);

        var records = await PostJsonAsync<RecordQueryResult>(client, "/collections/graphs/query", new QueryEnvelope
        {
            Filter = FilterNode.Eq("/metadata/graphId", "example-graph"),
            OrderBy = new List<OrderExpression> { new() { Path = "/type", Direction = SortDirections.Asc } }
        });
        Assert.Equal(5, records.Items.Count);
        Assert.All(records.Items, record => Assert.DoesNotContain("/", record.Id, StringComparison.Ordinal));
        Assert.Contains(records.Items, record => record.Type == VyralGraphRecordTypes.Envelope);

        var export = await PostJsonAsync<VyralGraphCollectionExportResult>(client, "/collections/graphs/graph/export", new VyralGraphCollectionExportRequest
        {
            GraphId = "example-graph"
        });

        Assert.Equal("graphs", export.Collection);
        Assert.Equal(5, export.RecordCount);
        Assert.False(export.Truncated);
        Assert.Equal("example-graph", export.Envelope.Scope.GraphId);
        Assert.Equal(new[] { "passage:introduction", "work:analysis" }, export.Envelope.Nodes.Select(node => node.Id).OrderBy(id => id));
        Assert.Equal("edge:references", Assert.Single(export.Envelope.Edges).Id);
        Assert.Equal("assertion:reference", Assert.Single(export.Envelope.Assertions).Id);
        Assert.Empty(export.Envelope.Reviews);

        var inspection = await PostJsonAsync<VyralGraphCollectionInspectionResult>(client, "/collections/graphs/graph/inspect", new VyralGraphCollectionInspectionRequest
        {
            GraphId = "example-graph"
        });

        Assert.True(inspection.TraversalReady);
        Assert.Equal(5, inspection.RecordCount);
        Assert.Equal(2, inspection.NodeCount);
        Assert.Equal(1, inspection.EdgeCount);
        Assert.Equal(1, inspection.PredicateCounts["references"]);
        Assert.Empty(inspection.Anomalies);

        var doctor = await PostJsonAsync<VyralGraphDoctorResult>(client, "/collections/graphs/graph/doctor", new VyralGraphDoctorRequest
        {
            GraphId = "example-graph"
        });

        Assert.True(doctor.Ready);
        Assert.Equal("ready", doctor.Status);
        Assert.Equal(2, doctor.GraphNodeCount);

        var traversal = await PostJsonAsync<VyralGraphTraversalResult>(client, "/collections/graphs/graph/traverse", new VyralGraphTraversalRequest
        {
            GraphId = "example-graph",
            StartNodeIds = new List<string> { "work:analysis" },
            Profile = new VyralGraphTraversalProfile
            {
                Id = "work-to-passage",
                Direction = VyralGraphTraversalDirections.Outgoing,
                MaxDepth = 1,
                Predicates = new List<string> { "references" },
                EdgeLimit = 5,
                Limit = 10
            }
        });

        Assert.Equal("example-graph", traversal.GraphId);
        Assert.Equal(2, traversal.NodeCount);
        Assert.Equal(1, traversal.EdgeCount);
        Assert.Equal(new[] { "passage:introduction", "work:analysis" }, traversal.Projection.Nodes.Select(node => node.Id).OrderBy(id => id));
        Assert.Equal("edge:references", Assert.Single(traversal.Projection.Edges).Id);
        Assert.Equal("edge:references", traversal.Projection.Diagnostics?["pathExplanations"]?["passage:introduction"]?[0]?["edgeId"]?.GetValue<string>());
    }

    [Fact]
    public async Task Server_GraphJobsRunThroughExecutionRuntime()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-graph-job-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        using var importResponse = await client.PostAsJsonAsync("/collections/graphs/graph/import/jobs", new VyralGraphCollectionImportRequest
        {
            Envelope = CreateServerGraphEnvelope()
        });
        Assert.Equal(HttpStatusCode.Accepted, importResponse.StatusCode);
        var importAccepted = (await importResponse.Content.ReadFromJsonAsync<GraphJob>())!;
        Assert.False(string.IsNullOrWhiteSpace(importAccepted.Id));
        Assert.Equal(GraphJobKinds.Import, importAccepted.Kind);
        Assert.Equal("graphs", importAccepted.Collection);

        GraphJob? importCompleted = null;
        for (var i = 0; i < 50; i++)
        {
            importCompleted = await client.GetFromJsonAsync<GraphJob>($"/graph/jobs/{importAccepted.Id}");
            if (importCompleted is not null && importCompleted.Status == GraphJobStatuses.Succeeded)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(importCompleted);
        Assert.Equal(GraphJobStatuses.Succeeded, importCompleted!.Status);
        Assert.Equal(1, importCompleted.Progress, precision: 3);
        Assert.Equal(5, importCompleted.RecordCount);
        Assert.Equal(2, importCompleted.NodeCount);
        Assert.Equal(1, importCompleted.EdgeCount);
        Assert.NotNull(importCompleted.ImportResult);
        Assert.Equal("example-graph", importCompleted.ImportResult!.GraphId);

        await AssertJobBackedByExecutionRuntimeAsync(
            dbPath,
            importAccepted.Id,
            ExecutionRuntimeGraphJobAdapter.ImportHandlerId,
            ExecutionRuntimeGraphJobAdapter.PluginId);

        using var inspectResponse = await client.PostAsJsonAsync("/collections/graphs/graph/inspect/jobs", new VyralGraphCollectionInspectionRequest
        {
            GraphId = "example-graph"
        });
        Assert.Equal(HttpStatusCode.Accepted, inspectResponse.StatusCode);
        var inspectAccepted = (await inspectResponse.Content.ReadFromJsonAsync<GraphJob>())!;
        Assert.Equal(GraphJobKinds.Inspect, inspectAccepted.Kind);

        GraphJob? inspectCompleted = null;
        for (var i = 0; i < 50; i++)
        {
            inspectCompleted = await client.GetFromJsonAsync<GraphJob>($"/graph/jobs/{inspectAccepted.Id}");
            if (inspectCompleted is not null && inspectCompleted.Status == GraphJobStatuses.Succeeded)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(inspectCompleted);
        Assert.Equal(GraphJobStatuses.Succeeded, inspectCompleted!.Status);
        Assert.Equal(1, inspectCompleted.Progress, precision: 3);
        Assert.NotNull(inspectCompleted.InspectionResult);
        Assert.True(inspectCompleted.InspectionResult!.TraversalReady);
        Assert.Equal(5, inspectCompleted.InspectionResult.RecordCount);

        await AssertJobBackedByExecutionRuntimeAsync(
            dbPath,
            inspectAccepted.Id,
            ExecutionRuntimeGraphJobAdapter.InspectionHandlerId,
            ExecutionRuntimeGraphJobAdapter.PluginId);

        var jobs = await client.GetFromJsonAsync<List<GraphJob>>("/graph/jobs?limit=5");
        Assert.NotNull(jobs);
        Assert.Contains(jobs, item => item.Id == importAccepted.Id);
        Assert.Contains(jobs, item => item.Id == inspectAccepted.Id);
        Assert.All(jobs, item =>
        {
            Assert.Null(item.ImportResult);
            Assert.Null(item.InspectionResult);
            Assert.Null(item.DoctorResult);
        });

        var jobsWithResults = await client.GetFromJsonAsync<List<GraphJob>>("/graph/jobs?limit=5&includeResult=true");
        Assert.NotNull(jobsWithResults);
        Assert.Contains(jobsWithResults, item => item.Id == importAccepted.Id && item.ImportResult is not null);
        Assert.Contains(jobsWithResults, item => item.Id == inspectAccepted.Id && item.InspectionResult is not null);

        var runtime = await client.GetFromJsonAsync<ExecutionRuntimeSurface>("/execution/runtime");
        Assert.NotNull(runtime);
        Assert.Contains(runtime!.Plugins, plugin => plugin.PluginId == ExecutionRuntimeGraphJobAdapter.PluginId);
        Assert.Contains(runtime.Handlers, handler => handler.HandlerId == ExecutionRuntimeGraphJobAdapter.ImportHandlerId);
        Assert.Contains(runtime.Handlers, handler => handler.HandlerId == ExecutionRuntimeGraphJobAdapter.InspectionHandlerId);
    }

    [Fact]
    public async Task Server_RecordImportJobsPreserveRawRecordAndSnapshotContracts()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-record-import-job-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();
        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy { Name = "raw-records" }));

        var batch = new RecordBatchUpsertRequest
        {
            Records = new List<VyralRecord>
            {
                new()
                {
                    Id = "passage:1",
                    PartitionKey = "tenant-a",
                    Type = "example.source.passage",
                    Metadata = new JsonObject { ["graphNodeId"] = "node:1", ["sceneId"] = "scene:1" },
                    Content = new JsonObject { ["text"] = "Caller-shaped source passage." }
                },
                new()
                {
                    Id = "passage:2",
                    PartitionKey = "tenant-a",
                    Type = "example.source.passage",
                    Metadata = new JsonObject { ["graphNodeId"] = "node:2", ["canonScopeId"] = "canon:a" },
                    Content = new JsonObject { ["text"] = "Second caller-shaped source passage." }
                }
            }
        };
        using var batchRequest = new HttpRequestMessage(HttpMethod.Post, "/collections/raw-records/records/batch/jobs")
        {
            Content = JsonContent.Create(batch)
        };
        batchRequest.Headers.Add("Idempotency-Key", "raw-records-1");
        using var batchResponse = await client.SendAsync(batchRequest);
        Assert.Equal(HttpStatusCode.Accepted, batchResponse.StatusCode);
        var accepted = (await batchResponse.Content.ReadFromJsonAsync<RecordImportJob>())!;
        Assert.Equal(RecordImportJobKinds.BatchUpsert, accepted.Kind);
        Assert.Equal("raw-records", accepted.Collection);

        using var duplicateBatchRequest = new HttpRequestMessage(HttpMethod.Post, "/collections/raw-records/records/batch/jobs")
        {
            Content = JsonContent.Create(batch)
        };
        duplicateBatchRequest.Headers.Add("Idempotency-Key", "raw-records-1");
        using var duplicateBatchResponse = await client.SendAsync(duplicateBatchRequest);
        Assert.Equal(HttpStatusCode.Accepted, duplicateBatchResponse.StatusCode);
        Assert.Equal(accepted.Id, (await duplicateBatchResponse.Content.ReadFromJsonAsync<RecordImportJob>())!.Id);

        RecordImportJob? completed = null;
        for (var i = 0; i < 50; i++)
        {
            completed = await client.GetFromJsonAsync<RecordImportJob>($"/record-import/jobs/{accepted.Id}");
            if (completed?.Status == RecordImportJobStatuses.Succeeded)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(completed);
        Assert.Equal(RecordImportJobStatuses.Succeeded, completed!.Status);
        Assert.Equal(2, completed.Requested);
        Assert.Equal(2, completed.Succeeded);
        Assert.NotNull(completed.BatchResult);
        Assert.All(completed.BatchResult!.Items, item => Assert.Equal(RecordUpsertStatuses.Succeeded, item.Status));
        var batchArtifacts = await client.GetFromJsonAsync<List<ExecutionArtifact>>($"/execution/runs/{accepted.Id}/artifacts");
        Assert.Contains(batchArtifacts!, artifact => artifact.Name == "record-batch-upsert-result");
        await AssertJobBackedByExecutionRuntimeAsync(
            dbPath,
            accepted.Id,
            ExecutionRuntimeRecordImportJobAdapter.BatchUpsertHandlerId,
            ExecutionRuntimeRecordImportJobAdapter.PluginId);

        var snapshot = new CollectionExportEnvelope
        {
            Collection = "source-snapshot",
            Policy = new RecordCollectionPolicy { Name = "source-snapshot" },
            Records = new List<VyralRecord>
            {
                new()
                {
                    Id = "passage:3",
                    PartitionKey = "tenant-a",
                    Type = "example.source.passage",
                    Metadata = new JsonObject { ["graphNodeId"] = "node:3", ["sourceSpan"] = "12:31" },
                    Content = new JsonObject { ["text"] = "Imported source passage." }
                }
            },
            RecordCount = 1
        };
        using var importResponse = await client.PostAsJsonAsync("/collections/imported-records/import/jobs", new CollectionImportRequest
        {
            Snapshot = snapshot,
            AllowCollectionRename = true
        });
        Assert.Equal(HttpStatusCode.Accepted, importResponse.StatusCode);
        var importAccepted = (await importResponse.Content.ReadFromJsonAsync<RecordImportJob>())!;

        RecordImportJob? importCompleted = null;
        for (var i = 0; i < 50; i++)
        {
            importCompleted = await client.GetFromJsonAsync<RecordImportJob>($"/record-import/jobs/{importAccepted.Id}");
            if (importCompleted?.Status == RecordImportJobStatuses.Succeeded)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(importCompleted);
        Assert.Equal(RecordImportJobKinds.CollectionImport, importCompleted!.Kind);
        Assert.Equal("source-snapshot", importCompleted.SourceCollection);
        Assert.NotNull(importCompleted.ImportResult);
        Assert.Equal("imported-records", importCompleted.ImportResult!.Collection);
        Assert.Equal(1, importCompleted.ImportResult.Records.Succeeded);
        var importArtifacts = await client.GetFromJsonAsync<List<ExecutionArtifact>>($"/execution/runs/{importAccepted.Id}/artifacts");
        Assert.Contains(importArtifacts!, artifact => artifact.Name == "collection-import-result");
        await AssertJobBackedByExecutionRuntimeAsync(
            dbPath,
            importAccepted.Id,
            ExecutionRuntimeRecordImportJobAdapter.CollectionImportHandlerId,
            ExecutionRuntimeRecordImportJobAdapter.PluginId);

        var list = await client.GetFromJsonAsync<List<RecordImportJob>>("/record-import/jobs?limit=5&includeResult=true");
        Assert.NotNull(list);
        Assert.Contains(list, item => item.Id == accepted.Id && item.BatchResult is not null);
        Assert.Contains(list, item => item.Id == importAccepted.Id && item.ImportResult is not null);

        var runtime = await client.GetFromJsonAsync<ExecutionRuntimeSurface>("/execution/runtime");
        Assert.NotNull(runtime);
        Assert.Contains(runtime!.Plugins, plugin => plugin.PluginId == ExecutionRuntimeRecordImportJobAdapter.PluginId);
        Assert.Contains(runtime.Handlers, handler => handler.HandlerId == ExecutionRuntimeRecordImportJobAdapter.BatchUpsertHandlerId);
        Assert.Contains(runtime.Handlers, handler => handler.HandlerId == ExecutionRuntimeRecordImportJobAdapter.CollectionImportHandlerId);
    }

    [Fact]
    public async Task Server_RecordImportJobsRejectOversizedDurablePayloadBeforeCreatingARun()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-record-import-payload-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-record-import-payload-objects-{Guid.NewGuid():N}");
        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();
        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy { Name = "payload-limited" }));

        var request = new RecordBatchUpsertRequest
        {
            Records = Enumerable.Range(0, 1_000).Select(index => new VyralRecord
            {
                Id = $"payload-{index:D4}",
                PartitionKey = "tenant-a",
                Content = new JsonObject { ["text"] = new string('x', 1_500) }
            }).ToList()
        };

        using var response = await client.PostAsJsonAsync("/collections/payload-limited/records/batch/jobs", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Durable record import payload cannot exceed", await response.Content.ReadAsStringAsync());

        var jobs = await client.GetFromJsonAsync<List<RecordImportJob>>("/record-import/jobs");
        Assert.NotNull(jobs);
        Assert.Empty(jobs!);
    }

    [Fact]
    public async Task Server_RecordImportJobsSerializeConcurrentSqliteWriters()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-record-import-concurrency-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-record-import-concurrency-objects-{Guid.NewGuid():N}");
        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();
        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy { Name = "concurrent-imports" }));

        var submissions = Enumerable.Range(0, 12).Select(async batchIndex =>
        {
            var request = new RecordBatchUpsertRequest
            {
                Records = Enumerable.Range(0, 25).Select(recordIndex => new VyralRecord
                {
                    Id = $"batch-{batchIndex:D2}-record-{recordIndex:D2}",
                    PartitionKey = "tenant-a",
                    Content = new JsonObject { ["text"] = $"Concurrent durable import {batchIndex}-{recordIndex}." }
                }).ToList()
            };
            using var response = await client.PostAsJsonAsync("/collections/concurrent-imports/records/batch/jobs", request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<RecordImportJob>())!;
        });
        var accepted = await Task.WhenAll(submissions);

        var completed = await WaitForRecordImportJobsAsync(client, accepted.Select(job => job.Id), attempts: 500);
        Assert.Equal(accepted.Length, completed.Count);
        Assert.All(completed, job =>
        {
            Assert.Equal(RecordImportJobStatuses.Succeeded, job.Status);
            Assert.Equal(25, job.Requested);
            Assert.Equal(25, job.Succeeded);
            Assert.Equal(0, job.Failed);
        });

        var export = await client.GetFromJsonAsync<CollectionExportEnvelope>("/collections/concurrent-imports/export");
        Assert.NotNull(export);
        Assert.Equal(300, export!.Records.Count);
    }

    [Fact]
    public async Task Server_RecordImportJobsHonorSharedExecutionAccessScopes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-record-import-access-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-record-import-access-objects-{Guid.NewGuid():N}");
        await using var factory = CreateFactory(dbPath, objectsPath, settings: new Dictionary<string, string?>
        {
            ["Server:ExecutionAccess:AuthenticationMode"] = VyralExecutionAuthenticationModes.DevelopmentHeader,
            ["ExecutionRuntime:ProductPolicies:0:ProductId"] = "product-a",
            ["ExecutionRuntime:ProductPolicies:0:AllowedServiceIdentities:0"] = "product-a@tests.example",
            ["ExecutionRuntime:ProductPolicies:0:AllowedHandlerIds:0"] = ExecutionRuntimeRecordImportJobAdapter.BatchUpsertHandlerId,
            ["ExecutionRuntime:ProductPolicies:0:AllowedHandlerIds:1"] = ExecutionRuntimeRecordImportJobAdapter.CollectionImportHandlerId,
            ["ExecutionRuntime:ProductPolicies:0:AllowedHandlerIds:2"] = ExecutionRuntimeCollectionManagementAdapter.CreateHandlerId,
            ["ExecutionRuntime:ProductPolicies:0:AllowedHandlerIds:3"] = ExecutionRuntimeCollectionManagementAdapter.DeleteHandlerId,
            ["ExecutionRuntime:ProductPolicies:1:ProductId"] = "product-b",
            ["ExecutionRuntime:ProductPolicies:1:AllowedServiceIdentities:0"] = "product-b@tests.example",
            ["ExecutionRuntime:ProductPolicies:1:AllowedHandlerIds:0"] = ExecutionRuntimeRecordImportJobAdapter.BatchUpsertHandlerId,
            ["ExecutionRuntime:ProductPolicies:1:AllowedHandlerIds:1"] = ExecutionRuntimeRecordImportJobAdapter.CollectionImportHandlerId,
            ["ExecutionRuntime:ProductPolicies:1:AllowedHandlerIds:2"] = ExecutionRuntimeCollectionManagementAdapter.CreateHandlerId,
            ["ExecutionRuntime:ProductPolicies:1:AllowedHandlerIds:3"] = ExecutionRuntimeCollectionManagementAdapter.DeleteHandlerId,
            ["Server:ExecutionAccess:IdentityPolicies:0:Principal"] = "product-a@tests.example",
            ["Server:ExecutionAccess:IdentityPolicies:0:ProductId"] = "product-a",
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedTenantIds:0"] = "tenant-a",
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedHandlerIds:0"] = ExecutionRuntimeRecordImportJobAdapter.BatchUpsertHandlerId,
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedHandlerIds:1"] = ExecutionRuntimeRecordImportJobAdapter.CollectionImportHandlerId,
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedHandlerIds:2"] = ExecutionRuntimeCollectionManagementAdapter.CreateHandlerId,
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedHandlerIds:3"] = ExecutionRuntimeCollectionManagementAdapter.DeleteHandlerId,
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedOperations:0"] = ExecutionAccessOperations.StartRun,
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedOperations:1"] = ExecutionAccessOperations.ReadRun,
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedOperations:2"] = ExecutionAccessOperations.CancelRun,
            ["Server:ExecutionAccess:IdentityPolicies:1:Principal"] = "product-b@tests.example",
            ["Server:ExecutionAccess:IdentityPolicies:1:ProductId"] = "product-b",
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedTenantIds:0"] = "tenant-b",
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedHandlerIds:0"] = ExecutionRuntimeRecordImportJobAdapter.BatchUpsertHandlerId,
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedHandlerIds:1"] = ExecutionRuntimeRecordImportJobAdapter.CollectionImportHandlerId,
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedHandlerIds:2"] = ExecutionRuntimeCollectionManagementAdapter.CreateHandlerId,
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedHandlerIds:3"] = ExecutionRuntimeCollectionManagementAdapter.DeleteHandlerId,
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedOperations:0"] = ExecutionAccessOperations.StartRun,
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedOperations:1"] = ExecutionAccessOperations.ReadRun,
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedOperations:2"] = ExecutionAccessOperations.CancelRun
        });
        var productA = factory.CreateClient();
        productA.DefaultRequestHeaders.Add("X-Vyral-Development-Identity", "product-a@tests.example");
        var productB = factory.CreateClient();
        productB.DefaultRequestHeaders.Add("X-Vyral-Development-Identity", "product-b@tests.example");
        var createResponse = await productA.PostAsJsonAsync(
            "/collections?productId=product-a&tenantId=tenant-a",
            new RecordCollectionPolicy { Name = "scoped-imports" });
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        var createRun = (await createResponse.Content.ReadFromJsonAsync<ExecutionRun>())!;
        await WaitForExecutionRunAsync(productA, createRun.Id, ExecutionRunStatuses.Succeeded);

        var scopedRequest = new RecordBatchUpsertRequest
        {
            Records = new List<VyralRecord>
            {
                new() { Id = "scoped-record", PartitionKey = "tenant-a", Content = new JsonObject { ["text"] = "Scoped durable import." } }
            }
        };
        using var productASubmission = new HttpRequestMessage(HttpMethod.Post, "/collections/scoped-imports/records/batch/jobs?productId=product-a&tenantId=tenant-a")
        {
            Content = JsonContent.Create(scopedRequest)
        };
        productASubmission.Headers.Add("Idempotency-Key", "shared-record-import-key");
        using var submitted = await productA.SendAsync(productASubmission);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var job = (await submitted.Content.ReadFromJsonAsync<RecordImportJob>())!;

        Assert.Equal(HttpStatusCode.Forbidden, (await productB.GetAsync($"/record-import/jobs/{job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await productB.DeleteAsync($"/record-import/jobs/{job.Id}")).StatusCode);
        var visibleToProductB = await productB.GetFromJsonAsync<List<RecordImportJob>>("/record-import/jobs?includeResult=true");
        Assert.NotNull(visibleToProductB);
        Assert.Empty(visibleToProductB!);

        using var productBSubmission = new HttpRequestMessage(HttpMethod.Post, "/collections/scoped-imports/records/batch/jobs?productId=product-b&tenantId=tenant-b")
        {
            Content = JsonContent.Create(scopedRequest)
        };
        productBSubmission.Headers.Add("Idempotency-Key", "shared-record-import-key");
        using var productBSubmitted = await productB.SendAsync(productBSubmission);
        Assert.Equal(HttpStatusCode.Accepted, productBSubmitted.StatusCode);
        var productBJob = (await productBSubmitted.Content.ReadFromJsonAsync<RecordImportJob>())!;
        Assert.NotEqual(job.Id, productBJob.Id);

        var productBJobs = await productB.GetFromJsonAsync<List<RecordImportJob>>("/record-import/jobs?includeResult=true");
        Assert.NotNull(productBJobs);
        Assert.Contains(productBJobs!, item => item.Id == productBJob.Id);
        Assert.DoesNotContain(productBJobs, item => item.Id == job.Id);

        var visibleToProductA = await productA.GetFromJsonAsync<RecordImportJob>($"/record-import/jobs/{job.Id}");
        Assert.NotNull(visibleToProductA);
        Assert.Equal(job.Id, visibleToProductA!.Id);

        var missingScope = await productA.PostAsJsonAsync("/collections/scoped-imports/records/batch/jobs", new RecordBatchUpsertRequest
        {
            Records = new List<VyralRecord>
            {
                new() { Id = "missing-scope", PartitionKey = "tenant-a" }
            }
        });
        Assert.Equal(HttpStatusCode.Forbidden, missingScope.StatusCode);
    }

    [Fact]
    public async Task Server_RagContextCanUseGraphExpansion()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-graphrag-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            IndexedMetadata = new List<string> { "/metadata/graphNodeId" }
        }));
        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections/chunks/records", new VyralRecord
        {
            Id = "chunk:retention",
            PartitionKey = "tenant-a",
            Type = "rag.chunk",
            Metadata = new JsonObject
            {
                ["graphNodeId"] = "chunk:retention"
            },
            Content = new JsonObject
            {
                ["text"] = "Retention archive controls require release review and audit logging."
            }
        }));
        await PostJsonAsync<VyralGraphCollectionImportResult>(client, "/collections/graphs/graph/import", new VyralGraphCollectionImportRequest
        {
            Envelope = new VyralGraphEnvelope
            {
                Scope = new VyralGraphScope
                {
                    GraphId = "server-graphrag",
                    Namespace = "tests",
                    Collection = "chunks",
                    TenantId = "tenant-a",
                    PartitionKey = "tenant-a"
                },
                Nodes = new List<VyralGraphNode>
                {
                    new() { Id = "chunk:retention", Type = "chunk", Label = "Retention chunk" },
                    new() { Id = "control:audit", Type = "control", Label = "Audit logging" }
                },
                Edges = new List<VyralGraphEdge>
                {
                    new()
                    {
                        Id = "edge:retention-audit",
                        SourceId = "chunk:retention",
                        TargetId = "control:audit",
                        Predicate = "requires"
                    }
                }
            }
        });

        var contextRequest = new RagContextRequest
        {
            Retrieval = new RetrievalRequest
            {
                Query = "retention audit",
                Collections = new List<string> { "chunks" },
                Limit = 1
            },
            IncludeContextText = true,
            IncludeTrace = true,
            GraphExpansion = new RagContextGraphExpansionOptions
            {
                Collection = "graphs",
                GraphId = "server-graphrag",
                Profile = new VyralGraphTraversalProfile
                {
                    Direction = VyralGraphTraversalDirections.Outgoing,
                    MaxDepth = 1,
                    Predicates = new List<string> { "requires" }
                }
            }
        };
        var context = await PostJsonAsync<RagContextEnvelope>(client, "/rag/context", contextRequest);

        Assert.NotNull(context.GraphContext);
        Assert.Equal(RagContextGraphExpansionStatuses.Succeeded, context.GraphContext!.Status);
        Assert.Equal(1, context.GraphContext.EdgeCount);
        Assert.Equal(3, context.GraphContext.Provenance.Count);
        Assert.Contains(context.GraphContext.Provenance, item => item.EntityId == "edge:retention-audit" && item.Predicate == "requires");
        Assert.Equal(0, context.GraphContext.OmittedProvenanceCount);
        Assert.Contains("Graph context:", context.ContextText);
        Assert.Equal(RagContextGraphExpansionStatuses.Succeeded, context.Trace!["graphExpansion"]!["status"]!.GetValue<string>());
        Assert.Equal(3, context.Trace["graphExpansion"]!["provenanceCount"]!.GetValue<int>());

        var evaluation = await PostJsonAsync<RagContextEvaluationResult>(client, "/rag/context/evaluate", new RagContextEvaluationRequest
        {
            Cases = new List<RagContextEvaluationCase>
            {
                new()
                {
                    Name = "retention-audit",
                    Request = contextRequest,
                    ExpectedGraph = new RagContextExpectedGraph
                    {
                        NodeIds = new List<string> { "chunk:retention", "control:audit" },
                        EdgeIds = new List<string> { "edge:retention-audit" },
                        ProvenanceEntityIds = new List<string> { "edge:retention-audit" },
                        RequireGraphContextText = true,
                        RequireContextTextNotTruncated = true
                    }
                }
            }
        });

        Assert.Equal(1, evaluation.PassedCount);
        Assert.Equal(1.0, evaluation.PassRate);
        Assert.Equal(1.0, evaluation.NodeHitRate);
        Assert.Equal(1.0, evaluation.EdgeHitRate);
        Assert.True(Assert.Single(evaluation.Cases).Graph.Passed);
    }

    [Fact]
    public async Task Server_InspectsCollectionRecordVectorEmbeddingAndRagShape()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            IndexedMetadata = new List<string> { "/metadata/documentId", "/type" },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new()
                {
                    Name = "contentEmbedding",
                    Path = "/vectors/contentEmbedding/values",
                    Dimensions = 3,
                    Datatype = "float32",
                    DistanceFunction = "cosine",
                    IndexType = "quantizedFlat"
                }
            }
        });
        await EnsureSuccessAsync(create);

        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections/chunks/records", new VyralRecord
        {
            Id = "chunk-1",
            PartitionKey = "tenant-a",
            Type = "rag.chunk",
            Metadata = new JsonObject
            {
                ["documentId"] = "doc-a",
                ["embeddingProvider"] = "onnx",
                ["embeddingModel"] = "multi-qa-minilm"
            },
            Content = new JsonObject { ["text"] = "retention policy" },
            Vectors = new Dictionary<string, VyralVector>
            {
                ["contentEmbedding"] = new()
                {
                    Values = new float[] { 1, 0, 0 },
                    Dimensions = 3,
                    Model = "multi-qa-minilm",
                    SourceField = "content.text"
                }
            }
        }));
        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections/chunks/records", new VyralRecord
        {
            Id = "chunk-2",
            PartitionKey = "tenant-a",
            Type = "rag.chunk",
            Metadata = new JsonObject
            {
                ["documentId"] = "doc-b",
                ["embeddingProvider"] = "local-token-hash",
                ["embeddingModel"] = "local-token-hash"
            },
            Content = new JsonObject { ["text"] = "appeals deadline" }
        }));
        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections/chunks/records", new VyralRecord
        {
            Id = "chunk-3",
            PartitionKey = "tenant-b",
            Type = "rag.chunk",
            Metadata = new JsonObject
            {
                ["documentId"] = "doc-a",
                ["embeddingProvider"] = "onnx",
                ["embeddingModel"] = "multi-qa-minilm"
            },
            Content = new JsonObject { ["text"] = "second valid vector" },
            Vectors = new Dictionary<string, VyralVector>
            {
                ["contentEmbedding"] = new()
                {
                    Values = new float[] { 0, 1, 0 },
                    Dimensions = 3,
                    Model = "multi-qa-minilm",
                    SourceField = "content.text"
                }
            }
        }));
        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections/chunks/records", new VyralRecord
        {
            Id = "manifest-doc-a",
            PartitionKey = "tenant-a",
            Type = "rag.manifest",
            Metadata = new JsonObject { ["documentId"] = "doc-a" }
        }));

        var inspection = await client.GetFromJsonAsync<CollectionInspectionResult>("/collections/chunks/inspect?anomalyLimit=2");

        Assert.NotNull(inspection);
        Assert.Equal("chunks", inspection.Collection);
        Assert.Equal(4, inspection.RecordCount);
        Assert.Equal(2, inspection.PartitionCount);
        Assert.Equal(3, inspection.TypeCounts["rag.chunk"]);
        Assert.Equal(1, inspection.TypeCounts["rag.manifest"]);
        Assert.Equal(2, inspection.EmbeddingProviderCounts["onnx"]);
        Assert.Equal(1, inspection.EmbeddingProviderCounts["local-token-hash"]);
        Assert.Equal(2, inspection.EmbeddingModelCounts["multi-qa-minilm"]);
        Assert.Equal(1, inspection.EmbeddingModelCounts["local-token-hash"]);
        Assert.Equal(2, inspection.Rag.DocumentCount);
        Assert.Equal(3, inspection.Rag.ChunkCount);
        Assert.Equal(1, inspection.Rag.ManifestCount);
        Assert.Equal(3, inspection.Rag.ChunkRecordsWithDocumentIdCount);
        Assert.Equal(2, inspection.Rag.ChunkRecordsWithVectorCount);
        Assert.Equal(1, inspection.Rag.ChunkRecordsWithoutVectorCount);

        var vector = Assert.Single(inspection.Vectors);
        Assert.Equal("contentEmbedding", vector.Field);
        Assert.Equal("/vectors/contentEmbedding/values", vector.Path);
        Assert.Equal(3, vector.PolicyDimensions);
        Assert.Equal("quantizedFlat", vector.IndexType);
        Assert.Equal(4, vector.RecordCount);
        Assert.Equal(2, vector.PresentCount);
        Assert.Equal(1, vector.MissingCount);
        Assert.Equal(1, vector.NotApplicableCount);
        Assert.Equal(0, vector.DimensionMismatchCount);
        Assert.Equal(0.667, vector.PolicyCoverage, 3);
        Assert.Equal(2, vector.ModelCounts["multi-qa-minilm"]);
        Assert.Equal(2, vector.SourceFieldCounts["content.text"]);

        Assert.Empty(inspection.ExtraVectorFieldCounts);
        Assert.Equal(1, inspection.AnomalyCount);
        Assert.Equal(1, inspection.ReturnedAnomalyCount);
        var anomaly = Assert.Single(inspection.Anomalies);
        Assert.Equal("missingPolicyVector", anomaly.Kind);
        Assert.Equal("chunk-2", anomaly.Id);
    }

    [Fact]
    public async Task Server_RunsRetrievalAndPersistsTrace()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();
        var provider = new LocalTokenHashEmbeddingProvider(12);

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = provider.Dimensions }
            }
        });
        await EnsureSuccessAsync(create);

        var vector = await provider.GenerateEmbeddingAsync("retention policy details");
        var upsert = await client.PostAsJsonAsync("/collections/chunks/records", new VyralRecord
        {
            Id = "chunk-1",
            PartitionKey = "tenant-a",
            Content = new JsonObject { ["text"] = "retention policy details" },
            Vectors = new Dictionary<string, VyralVector>
            {
                ["contentEmbedding"] = new() { Values = vector, Dimensions = vector.Length }
            }
        });
        await EnsureSuccessAsync(upsert);

        var retrieval = await PostJsonAsync<RetrievalResultEnvelope>(client, "/search", new RetrievalRequest
        {
            Query = "retention policy details",
            Collections = new List<string> { "chunks" },
            SearchMode = "vector",
            Limit = 1,
            IncludeTrace = true
        });

        Assert.Equal("retention policy details", retrieval.Query);
        var match = Assert.Single(retrieval.Results);
        Assert.Equal("chunk-1", match.Record.Id);
        Assert.NotNull(match.Diagnostics);
        Assert.Equal("sqlite-flat-scan", ((JsonElement)match.Diagnostics!.Details["vectorIndexProvider"]!).GetString());
        Assert.False(((JsonElement)match.Diagnostics.Details["vectorIndexUsed"]!).GetBoolean());
        Assert.Empty(((JsonElement)match.Diagnostics.Details["vectorIndexFields"]!).EnumerateArray());
        Assert.NotNull(retrieval.Trace);
        Assert.Equal(12, retrieval.Trace!["embeddingDimensions"]!.GetValue<int>());

        var profiles = await client.GetFromJsonAsync<List<RetrievalProfileDescriptor>>("/retrieval/profiles");
        Assert.NotNull(profiles);
        Assert.Contains(profiles, profile => profile.Id == RetrievalProfileIds.Evidence && profile.SearchMode == SearchModes.Lexical);
        Assert.Contains(profiles, profile => profile.Id == RetrievalProfileIds.RerankPolish && profile.UsesRerank);

        var profileRetrieval = await PostJsonAsync<RetrievalResultEnvelope>(client, "/search", new RetrievalRequest
        {
            Profile = RetrievalProfileIds.Evidence,
            Query = "retention policy details",
            Collections = new List<string> { "chunks" }
        });
        var profileMatch = Assert.Single(profileRetrieval.Results);
        Assert.Equal("chunk-1", profileMatch.Record.Id);
        Assert.NotNull(profileRetrieval.Trace);
        Assert.Equal(RetrievalProfileIds.Evidence, profileRetrieval.Trace!["profile"]!.GetValue<string>());
        Assert.Equal(SearchModes.Lexical, profileRetrieval.Trace["searchMode"]!.GetValue<string>());

        var traceId = retrieval.Trace!["id"]!.ToString();
        var traceResponse = await client.GetAsync($"/traces/{traceId}");
        var traces = await client.GetFromJsonAsync<List<TraceRecord>>("/traces?operation=retrieval.search&limit=10");

        Assert.Equal(HttpStatusCode.OK, traceResponse.StatusCode);
        Assert.NotNull(traces);
        Assert.Contains(traces, trace => trace.Id == traceId);

        var summary = await client.GetFromJsonAsync<TraceSummary>("/traces/summary?operation=retrieval.search");
        Assert.NotNull(summary);
        Assert.True(summary.TotalCount >= 1);
        Assert.Contains(summary.Operations, operation => operation.Operation == "retrieval.search" && operation.Adapters.Contains(nameof(SqliteRecordCollectionStore)));

        var export = await PostJsonAsync<TraceExportBundle>(client, "/traces/export", new TraceExportRequest
        {
            Operation = "retrieval.search",
            Limit = 10
        });
        Assert.Contains(export.Traces, trace => trace.Id == traceId);
        Assert.Equal(export.Traces.Count, export.TraceCount);
        Assert.StartsWith("sha256:", export.ContentHash);

        var pruneDryRun = await PostJsonAsync<TracePruneResult>(client, "/traces/prune", new TracePruneRequest
        {
            Operation = "retrieval.search",
            KeepLatest = 0,
            DryRun = true
        });
        Assert.True(pruneDryRun.MatchedCount >= 1);
        Assert.Equal(0, pruneDryRun.DeletedCount);

        var context = await PostJsonAsync<RagContextEnvelope>(client, "/rag/context", new RagContextRequest
        {
            Retrieval = new RetrievalRequest
            {
                Query = "retention policy details",
                Collections = new List<string> { "chunks" },
                Limit = 1
            },
            MaxChars = 40,
            MaxCharsPerChunk = 40,
            IncludeTrace = true
        });

        var chunk = Assert.Single(context.Chunks);
        Assert.Equal("chunk-1", chunk.Id);
        Assert.Equal("retention policy details", chunk.Text);
        Assert.NotNull(context.Trace);

        var prompt = await PostJsonAsync<RagPromptEnvelope>(client, "/rag/prompt", new RagPromptRequest
        {
            Context = new RagContextRequest
            {
                Retrieval = new RetrievalRequest
                {
                    Query = "retention policy details",
                    Collections = new List<string> { "chunks" },
                    Limit = 1
                },
                MaxChars = 160,
                MaxCharsPerChunk = 80,
                IncludeTrace = true
            },
            Template = new RagPromptTemplateOptions
            {
                UserInstruction = "Summarize the retention policy details.",
                FailOnEmptyContext = true
            }
        });

        Assert.StartsWith("sha256:", prompt.PromptHash);
        Assert.Contains("Summarize the retention policy details.", prompt.Prompt);
        Assert.Contains("Context:", prompt.Prompt);
        Assert.Equal(new[] { "system", "user" }, prompt.Messages.Select(message => message.Role));
    }

    [Fact]
    public async Task Server_EvaluatesRetrievalQualityCases()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-eval-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();
        var provider = new LocalTokenHashEmbeddingProvider(12);

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = provider.Dimensions }
            }
        });
        await EnsureSuccessAsync(create);

        await UpsertRecordAsync(client, "retention", "tenant-a", "retention policy details", await provider.GenerateEmbeddingAsync("retention policy details"));
        await UpsertRecordAsync(client, "travel", "tenant-a", "travel reimbursement receipts", await provider.GenerateEmbeddingAsync("travel reimbursement receipts"));

        var evaluation = await PostJsonAsync<RetrievalEvaluationResult>(client, "/retrieval/evaluate", new RetrievalEvaluationRequest
        {
            DefaultK = 2,
            Cases = new List<RetrievalEvaluationCase>
            {
                new()
                {
                    Name = "retention-hit",
                    Request = new RetrievalRequest
                    {
                        Query = "retention policy details",
                        Collections = new List<string> { "chunks" },
                        PartitionKeys = new List<string> { "tenant-a" },
                        SearchMode = "vector",
                        Limit = 2
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "retention", PartitionKey = "tenant-a", Collection = "chunks" }
                    },
                    HardNegatives = new List<RetrievalEvaluationHardNegativeMatch>
                    {
                        new() { Id = "travel", PartitionKey = "tenant-a", Collection = "chunks", Reason = "finance policy near-neighbor" }
                    }
                },
                new()
                {
                    Name = "missing-miss",
                    Request = new RetrievalRequest
                    {
                        Query = "travel reimbursement receipts",
                        Collections = new List<string> { "chunks" },
                        PartitionKeys = new List<string> { "tenant-a" },
                        SearchMode = "vector",
                        Limit = 2
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "missing" }
                    }
                }
            }
        });

        Assert.Equal(2, evaluation.Requested);
        Assert.Equal(2, evaluation.Attempted);
        Assert.Equal(2, evaluation.Succeeded);
        Assert.Equal(0, evaluation.Failed);
        Assert.Equal(1, evaluation.HitCount);
        Assert.Equal(0.5, evaluation.HitRate, precision: 3);
        Assert.Equal(0.5, evaluation.MeanReciprocalRank, precision: 3);
        Assert.Equal(1, evaluation.HardNegativeCaseCount);
        Assert.Equal(1, evaluation.HardNegativeHitCount);
        Assert.Equal(1, evaluation.HardNegativeHitRate, precision: 3);
        Assert.Equal(0, evaluation.RerankCaseCount);
        Assert.Equal(0, evaluation.RerankFallbackCaseCount);
        Assert.Equal("succeeded", evaluation.Cases[0].Status);
        Assert.True(evaluation.Cases[0].Hit);
        Assert.Equal(1, evaluation.Cases[0].FirstRelevantRank);
        Assert.Equal(1, evaluation.Cases[0].MatchedCount);
        Assert.True(evaluation.Cases[0].HardNegativeHit);
        Assert.Equal(1, evaluation.Cases[0].HardNegativeMatchedCount);
        Assert.NotNull(evaluation.Cases[0].FirstHardNegativeRank);
        Assert.Equal("retention", evaluation.Cases[0].Expected[0].Id);
        Assert.Equal(1, evaluation.Cases[0].Expected[0].Rank);
        Assert.Equal("travel", evaluation.Cases[0].HardNegatives[0].Id);
        Assert.NotNull(evaluation.Cases[0].HardNegatives[0].Rank);
        Assert.NotEmpty(evaluation.Cases[0].TopResults);
        Assert.Contains(evaluation.Cases[0].TopResults, item => item.MatchedHardNegative);
        Assert.Equal(evaluation.Cases[0].TopResults.OrderBy(result => result.Rank).Select(result => result.Rank), evaluation.Cases[0].TopResults.Select(result => result.Rank));
        Assert.All(evaluation.Cases[0].TopResults, item => Assert.False(string.IsNullOrWhiteSpace(item.VectorIndexProvider)));
        Assert.All(evaluation.Cases[0].TopResults.Where(item => item.VectorIndexUsed), item => Assert.NotEmpty(item.VectorIndexFields));
        Assert.False(evaluation.Cases[1].Hit);
    }

    [Fact]
    public async Task Server_RetrievalEvaluationMatchesContainedIdsAndSourceSpans()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-eval-source-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "long-form-works"
        });
        await EnsureSuccessAsync(create);

        var passageText = "A lengthy written work opens with a detailed introduction, establishes its setting, and explains the events that follow.";
        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections/long-form-works/records", new VyralRecord
        {
            Id = "work-1-1-4",
            PartitionKey = "edition-a",
            Type = "rag.passage",
            Metadata = new JsonObject
            {
                ["containedIds"] = new JsonArray("work.1.1", "work.1.2", "work.1.3", "work.1.4")
            },
            Content = new JsonObject { ["text"] = passageText },
            Sources = new List<VyralSourceReference>
            {
                new()
                {
                    Id = "work.1.1-4",
                    Kind = "document.passage",
                    Uri = "document://sample-work/chapter-1",
                    Span = new VyralSourceSpan { CharStart = 0, CharEnd = passageText.Length }
                },
                new()
                {
                    Id = "work.1.2",
                    Kind = "document.section",
                    Uri = "document://sample-work/chapter-1#section-2",
                    Span = new VyralSourceSpan { CharStart = 60, CharEnd = passageText.Length }
                }
            }
        }));

        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections/long-form-works/records", new VyralRecord
        {
            Id = "work-2-1-4",
            PartitionKey = "edition-a",
            Type = "rag.passage",
            Metadata = new JsonObject
            {
                ["containedIds"] = new JsonArray("work.2.1", "work.2.2", "work.2.3", "work.2.4")
            },
            Content = new JsonObject { ["text"] = "A later section describes an organization facing a difficult decision and assigning follow-up tasks." },
            Sources = new List<VyralSourceReference>
            {
                new()
                {
                    Id = "work.2.1-4",
                    Kind = "document.passage",
                    Uri = "document://sample-work/chapter-2",
                    Span = new VyralSourceSpan { CharStart = 0, CharEnd = 72 }
                }
            }
        }));

        var evaluation = await PostJsonAsync<RetrievalEvaluationResult>(client, "/retrieval/evaluate", new RetrievalEvaluationRequest
        {
            DefaultK = 2,
            Cases = new List<RetrievalEvaluationCase>
            {
                new()
                {
                    Name = "contained-section-id",
                    Request = new RetrievalRequest
                    {
                        Query = "introduction setting events",
                        Collections = new List<string> { "long-form-works" },
                        PartitionKeys = new List<string> { "edition-a" },
                        SearchMode = SearchModes.Lexical,
                        Limit = 2
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "work.1.3", PartitionKey = "edition-a", Collection = "long-form-works" }
                    }
                },
                new()
                {
                    Name = "source-span-contained",
                    Request = new RetrievalRequest
                    {
                        Query = "written work establishes setting",
                        Collections = new List<string> { "long-form-works" },
                        PartitionKeys = new List<string> { "edition-a" },
                        SearchMode = SearchModes.Lexical,
                        Limit = 2
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new()
                        {
                            Sources = new List<VyralSourceReference>
                            {
                                new()
                                {
                                    Uri = "document://sample-work/chapter-1",
                                    Span = new VyralSourceSpan { CharStart = 10, CharEnd = 40 }
                                }
                            }
                        }
                    }
                }
            }
        });

        Assert.Equal(2, evaluation.Succeeded);
        Assert.All(evaluation.Cases, testCase => Assert.True(testCase.Hit));
        Assert.All(evaluation.Cases, testCase => Assert.Equal(1, testCase.FirstRelevantRank));
        Assert.All(evaluation.Cases.SelectMany(testCase => testCase.TopResults.Where(result => result.Id == "work-1-1-4")),
            result => Assert.True(result.MatchedExpected));
    }

    [Fact]
    public async Task Server_RetrievalEvaluationComparesVariants()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-eval-compare-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();
        var provider = new LocalTokenHashEmbeddingProvider(12);

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = provider.Dimensions }
            }
        });
        await EnsureSuccessAsync(create);

        await UpsertRecordAsync(client, "retention", "tenant-a", "retention policy details", await provider.GenerateEmbeddingAsync("retention policy details"));
        await UpsertRecordAsync(client, "travel", "tenant-a", "travel reimbursement receipts", await provider.GenerateEmbeddingAsync("travel reimbursement receipts"));

        var comparison = await PostJsonAsync<RetrievalEvaluationComparisonResult>(client, "/retrieval/evaluate/compare", new RetrievalEvaluationComparisonRequest
        {
            DefaultK = 2,
            IncludeCaseResults = false,
            IncludeTopResults = false,
            Variants = new List<RetrievalEvaluationVariant>
            {
                new() { Id = "evidence", Label = "Evidence profile", Profile = RetrievalProfileIds.Evidence },
                new() { Id = "vector", Label = "Vector-only", SearchMode = SearchModes.Vector }
            },
            Cases = new List<RetrievalEvaluationCase>
            {
                new()
                {
                    Name = "retention-hit",
                    Request = new RetrievalRequest
                    {
                        Query = "retention policy details",
                        Collections = new List<string> { "chunks" },
                        PartitionKeys = new List<string> { "tenant-a" },
                        Limit = 2
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "retention", PartitionKey = "tenant-a", Collection = "chunks" }
                    },
                    HardNegatives = new List<RetrievalEvaluationHardNegativeMatch>
                    {
                        new() { Id = "travel", PartitionKey = "tenant-a", Collection = "chunks", Reason = "near-neighbor" }
                    }
                },
                new()
                {
                    Name = "missing-miss",
                    Request = new RetrievalRequest
                    {
                        Query = "travel reimbursement receipts",
                        Collections = new List<string> { "chunks" },
                        PartitionKeys = new List<string> { "tenant-a" },
                        Limit = 2
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "missing" }
                    }
                }
            }
        });

        Assert.Equal(2, comparison.Requested);
        Assert.Equal(2, comparison.VariantsRequested);
        Assert.Equal(2, comparison.VariantsAttempted);
        Assert.Equal(2, comparison.VariantsSucceeded);
        Assert.Equal(0, comparison.VariantsFailed);
        Assert.Equal("evidence", comparison.BaselineVariantId);
        Assert.All(comparison.Variants, variant => Assert.Equal(EvaluationVariantStatuses.Succeeded, variant.Status));
        Assert.Equal(2, comparison.Variants[0].Metrics.Attempted);
        Assert.Equal(1, comparison.Variants[0].Metrics.HitCount);
        Assert.Empty(comparison.Variants[0].Cases);
        Assert.Null(comparison.Variants[0].DeltaFromBaseline);
        Assert.NotNull(comparison.Variants[1].DeltaFromBaseline);
    }

    [Fact]
    public async Task Server_RetrievalEvaluationJobsExposeCaseProgressAndResults()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-eval-case-job-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();
        var provider = new LocalTokenHashEmbeddingProvider(12);

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = provider.Dimensions }
            }
        });
        await EnsureSuccessAsync(create);

        await UpsertRecordAsync(client, "retention", "tenant-a", "retention policy details", await provider.GenerateEmbeddingAsync("retention policy details"));
        await UpsertRecordAsync(client, "travel", "tenant-a", "travel reimbursement receipts", await provider.GenerateEmbeddingAsync("travel reimbursement receipts"));

        var request = new RetrievalEvaluationRequest
        {
            DefaultK = 2,
            IncludeTopResults = true,
            Cases = new List<RetrievalEvaluationCase>
            {
                new()
                {
                    Name = "retention-hit",
                    Request = new RetrievalRequest
                    {
                        Query = "retention policy details",
                        Collections = new List<string> { "chunks" },
                        PartitionKeys = new List<string> { "tenant-a" },
                        Limit = 2,
                        SearchMode = SearchModes.Lexical
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "retention", PartitionKey = "tenant-a", Collection = "chunks" }
                    }
                }
            }
        };

        using var response = await client.PostAsJsonAsync("/retrieval/evaluate/jobs", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<RetrievalEvaluationJob>())!;
        Assert.False(string.IsNullOrWhiteSpace(accepted.Id));
        Assert.Equal(RetrievalEvaluationJobKinds.Evaluation, accepted.Kind);
        Assert.Equal(1, accepted.Requested);

        RetrievalEvaluationJob? completed = null;
        for (var i = 0; i < 50; i++)
        {
            completed = await client.GetFromJsonAsync<RetrievalEvaluationJob>($"/retrieval/evaluate/jobs/{accepted.Id}");
            if (completed is not null && completed.Status == RetrievalEvaluationJobStatuses.Succeeded)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(completed);
        Assert.Equal(RetrievalEvaluationJobStatuses.Succeeded, completed!.Status);
        Assert.Equal(1, completed.Progress, precision: 3);
        Assert.Equal(1, completed.CasesAttempted);
        Assert.Equal(1, completed.CasesSucceeded);
        Assert.Null(completed.CurrentCaseName);
        Assert.NotNull(completed.EvaluationResult);
        Assert.Equal(1, completed.EvaluationResult!.HitCount);
        var caseResult = Assert.Single(completed.EvaluationResult.Cases);
        Assert.True(caseResult.DurationMs >= 0);
        Assert.NotEmpty(caseResult.TopResults);

        await AssertJobBackedByExecutionRuntimeAsync(
            dbPath,
            accepted.Id,
            ExecutionRuntimeRetrievalEvaluationJobAdapter.EvaluationHandlerId,
            ExecutionRuntimeRetrievalEvaluationJobAdapter.PluginId);
    }

    [Fact]
    public async Task Server_RetrievalEvaluationComparisonJobsExposeProgressAndResults()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-eval-job-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();
        var provider = new LocalTokenHashEmbeddingProvider(12);

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = provider.Dimensions }
            }
        });
        await EnsureSuccessAsync(create);

        await UpsertRecordAsync(client, "retention", "tenant-a", "retention policy details", await provider.GenerateEmbeddingAsync("retention policy details"));
        await UpsertRecordAsync(client, "travel", "tenant-a", "travel reimbursement receipts", await provider.GenerateEmbeddingAsync("travel reimbursement receipts"));

        var request = new RetrievalEvaluationComparisonRequest
        {
            DefaultK = 2,
            IncludeCaseResults = true,
            IncludeTopResults = true,
            Variants = new List<RetrievalEvaluationVariant>
            {
                new() { Id = "lexical", SearchMode = SearchModes.Lexical },
                new() { Id = "vector", SearchMode = SearchModes.Vector }
            },
            Cases = new List<RetrievalEvaluationCase>
            {
                new()
                {
                    Name = "retention-hit",
                    Request = new RetrievalRequest
                    {
                        Query = "retention policy details",
                        Collections = new List<string> { "chunks" },
                        PartitionKeys = new List<string> { "tenant-a" },
                        Limit = 2
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "retention", PartitionKey = "tenant-a", Collection = "chunks" }
                    }
                }
            }
        };

        using var response = await client.PostAsJsonAsync("/retrieval/evaluate/compare/jobs", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<RetrievalEvaluationJob>())!;
        Assert.False(string.IsNullOrWhiteSpace(accepted.Id));
        Assert.Equal(1, accepted.Requested);
        Assert.Equal(2, accepted.VariantsRequested);

        RetrievalEvaluationJob? completed = null;
        for (var i = 0; i < 50; i++)
        {
            completed = await client.GetFromJsonAsync<RetrievalEvaluationJob>($"/retrieval/evaluate/jobs/{accepted.Id}");
            if (completed is not null && completed.Status == RetrievalEvaluationJobStatuses.Succeeded)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(completed);
        Assert.Equal(RetrievalEvaluationJobStatuses.Succeeded, completed!.Status);
        Assert.Equal(1, completed.Progress, precision: 3);
        Assert.Equal(2, completed.VariantsAttempted);
        Assert.Equal(2, completed.VariantsSucceeded);
        Assert.Null(completed.CurrentVariantId);
        Assert.NotNull(completed.Result);
        Assert.Equal(2, completed.Result!.Variants.Count);
        Assert.NotEmpty(completed.Result.Variants[0].Cases);

        await AssertJobBackedByExecutionRuntimeAsync(
            dbPath,
            accepted.Id,
            ExecutionRuntimeRetrievalEvaluationJobAdapter.ComparisonHandlerId,
            ExecutionRuntimeRetrievalEvaluationJobAdapter.PluginId);

        var jobs = await client.GetFromJsonAsync<List<RetrievalEvaluationJob>>("/retrieval/evaluate/jobs?limit=5");
        Assert.NotNull(jobs);
        Assert.Contains(jobs, item => item.Id == accepted.Id);
        Assert.All(jobs, item => Assert.Null(item.Result));

        var jobsWithResults = await client.GetFromJsonAsync<List<RetrievalEvaluationJob>>("/retrieval/evaluate/jobs?limit=5&includeResult=true");
        Assert.NotNull(jobsWithResults);
        Assert.Contains(jobsWithResults, item => item.Id == accepted.Id && item.Result is not null);
    }

    [Fact]
    public async Task Server_RetrievalEvaluationReportsRerankFallbackMetrics()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-eval-rerank-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 2);
        var client = factory.CreateClient();

        await CreatePayloadBudgetCollectionAsync(client);
        await UpsertPayloadBudgetRecordsAsync(client, contentChars: 2600, metadataChars: 100);

        var evaluation = await PostJsonAsync<RetrievalEvaluationResult>(client, "/retrieval/evaluate", new RetrievalEvaluationRequest
        {
            DefaultK = 1,
            IncludeTopResults = true,
            Cases = new List<RetrievalEvaluationCase>
            {
                new()
                {
                    Name = "rerank-fallback",
                    Request = new RetrievalRequest
                    {
                        Query = "target retention",
                        Collections = new List<string> { "payload-budget" },
                        SearchMode = "lexical",
                        Limit = 1,
                        Lexical = new LexicalSearchOptions
                        {
                            Fields = new List<string> { "/content/text" },
                            ScanLimit = 100,
                            Scoring = "bm25"
                        },
                        Rerank = new RerankOptions
                        {
                            Enabled = true,
                            Mode = "mechanics",
                            CandidateLimit = 40,
                            ContentField = "text",
                            MaxCandidateChars = 2000,
                            TimeoutSeconds = 30
                        }
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "candidate-00", PartitionKey = "tenant-a", Collection = "payload-budget" }
                    }
                }
            }
        });

        Assert.Equal(1, evaluation.RerankCaseCount);
        Assert.Equal(1, evaluation.RerankFallbackCaseCount);
        Assert.Equal(1, evaluation.RerankFallbackRate, precision: 3);
        var caseResult = Assert.Single(evaluation.Cases);
        Assert.Equal("succeeded", caseResult.Status);
        Assert.True(caseResult.RerankEnabled);
        Assert.True(caseResult.RerankFallbackApplied);
        Assert.Equal(LocalTokenOverlapRerankerProviderTarget.ProviderId, caseResult.RerankProvider);
        Assert.Equal("input_limit", caseResult.RerankProviderStatus);
        Assert.Equal(40, caseResult.RerankInputCandidateCount);
        Assert.True(caseResult.RerankProviderPayloadBytes > caseResult.RerankProviderMaxInputBytes);
        var top = Assert.Single(caseResult.TopResults);
        Assert.True(top.RerankFallbackApplied);
        Assert.Equal("input_limit", top.RerankProviderStatus);
    }

    [Fact]
    public async Task Server_RetrievalEvaluationReportsPerCaseFailures()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-eval-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var evaluation = await PostJsonAsync<RetrievalEvaluationResult>(client, "/retrieval/evaluate", new RetrievalEvaluationRequest
        {
            ContinueOnError = true,
            Cases = new List<RetrievalEvaluationCase>
            {
                new()
                {
                    Name = "invalid",
                    Request = new RetrievalRequest { Query = "retention", Collections = new List<string> { "missing" } },
                    Expected = new List<RetrievalEvaluationExpectedMatch> { new() { Id = "" } }
                }
            }
        });

        Assert.Equal(1, evaluation.Attempted);
        Assert.Equal(0, evaluation.Succeeded);
        Assert.Equal(1, evaluation.Failed);
        Assert.Equal("failed", evaluation.Cases[0].Status);
        Assert.Contains("expected match must include", evaluation.Cases[0].Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Server_RetrievalEvaluationRejectsOverlappingHardNegatives()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-eval-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var evaluation = await PostJsonAsync<RetrievalEvaluationResult>(client, "/retrieval/evaluate", new RetrievalEvaluationRequest
        {
            ContinueOnError = true,
            Cases = new List<RetrievalEvaluationCase>
            {
                new()
                {
                    Name = "overlap",
                    Request = new RetrievalRequest { Query = "retention", Collections = new List<string> { "missing" } },
                    Expected = new List<RetrievalEvaluationExpectedMatch> { new() { Id = "chunk-1" } },
                    HardNegatives = new List<RetrievalEvaluationHardNegativeMatch> { new() { Id = "chunk-1" } }
                }
            }
        });

        Assert.Equal(1, evaluation.Attempted);
        Assert.Equal(0, evaluation.Succeeded);
        Assert.Equal(1, evaluation.Failed);
        Assert.Equal("failed", evaluation.Cases[0].Status);
        Assert.Contains("hard-negative", evaluation.Cases[0].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, evaluation.Cases[0].HardNegativeCount);
    }

    [Fact]
    public async Task Server_RetrievesAcrossMultipleVectorFields()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-multivector-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();
        var provider = new LocalTokenHashEmbeddingProvider(12);

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = provider.Dimensions },
                new() { Name = "titleEmbedding", Path = "/vectors/titleEmbedding/values", Dimensions = provider.Dimensions }
            }
        });
        await EnsureSuccessAsync(create);

        var expected = await provider.GenerateEmbeddingAsync("appeal deadline");
        var unrelated = await provider.GenerateEmbeddingAsync("invoice payment approval");
        await UpsertMultiVectorRecordAsync(client, "content-hit", "tenant-a", "appeal deadline content", expected, unrelated);
        await UpsertMultiVectorRecordAsync(client, "title-hit", "tenant-a", "title carries appeal deadline", unrelated, expected);
        await UpsertMultiVectorRecordAsync(client, "miss", "tenant-a", "invoice payment approval", unrelated, unrelated);

        var result = await PostJsonAsync<RetrievalResultEnvelope>(client, "/search", new RetrievalRequest
        {
            Query = "appeal deadline",
            Collections = new List<string> { "chunks" },
            PartitionKeys = new List<string> { "tenant-a" },
            SearchMode = "vector",
            Limit = 2,
            IncludeTrace = true,
            VectorFields = new List<RetrievalVectorFieldQuery>
            {
                new() { Field = "contentEmbedding", Weight = 0.5f, CandidateLimit = 2 },
                new() { Field = "titleEmbedding", Weight = 0.5f, CandidateLimit = 2 }
            }
        });

        Assert.Equal(2, result.Results.Count);
        Assert.Contains(result.Results, match => match.Record.Id == "content-hit");
        Assert.Contains(result.Results, match => match.Record.Id == "title-hit");
        Assert.All(result.Results, match =>
        {
            Assert.NotNull(match.Diagnostics);
            Assert.Equal("vector.multi_field_weighted_normalized", match.Diagnostics!.ScoreNormalization?.VectorScoreKind);
            Assert.True(match.Diagnostics.ScoreComponents.ContainsKey("vectorAggregate"));
            Assert.Contains("fusion.multi_vector", match.Diagnostics.ReasonCodes);
        });
    }

    [Fact]
    public async Task Server_RunsProviderBackedRerankingForRetrieval()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-rerank-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 2);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "notes",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
            }
        });
        await EnsureSuccessAsync(create);

        foreach (var record in new[]
        {
            ("a-general", "travel reimbursement policy and meal expense rules"),
            ("z-retention", "active retention policy hold details")
        })
        {
            var upsert = await client.PostAsJsonAsync("/collections/notes/records", new VyralRecord
            {
                Id = record.Item1,
                PartitionKey = "tenant-a",
                Content = new JsonObject { ["text"] = record.Item2 },
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["contentEmbedding"] = new() { Values = new float[] { 1, 0 }, Dimensions = 2 }
                }
            });
            await EnsureSuccessAsync(upsert);
        }

        var retrieval = await PostJsonAsync<RetrievalResultEnvelope>(client, "/search", new RetrievalRequest
        {
            Query = "retention policy",
            Collections = new List<string> { "notes" },
            Limit = 1,
            IncludeTrace = true,
            Rerank = new RerankOptions
            {
                Enabled = true,
                CandidateLimit = 2,
                ContentField = "text",
                TimeoutSeconds = 30,
                MaxOutputBytes = 4096
            }
        });

        var match = Assert.Single(retrieval.Results);
        Assert.Equal("z-retention", match.Record.Id);
        Assert.NotNull(match.Diagnostics);
        Assert.Contains("rerank", match.Diagnostics!.CandidateSources);
        Assert.Equal(LocalTokenOverlapRerankerProviderTarget.ProviderId, ((JsonElement)match.Diagnostics.Details["rerankProvider"]!).GetString());
        Assert.NotNull(retrieval.Trace);
        Assert.True(retrieval.Trace!["rerankEnabled"]!.GetValue<bool>());
        var rerankTraceId = retrieval.Trace["rerankTraceId"]!.ToString();

        var providerTrace = await client.GetFromJsonAsync<TraceRecord>($"/traces/{rerankTraceId}");
        Assert.NotNull(providerTrace);
        Assert.Equal("provider.run", providerTrace!.Operation);
        Assert.Equal(LocalTokenOverlapRerankerProviderTarget.ProviderId, providerTrace.Adapter);
        Assert.Equal(ProviderCapabilityIds.AiRerank, ((JsonElement)providerTrace.Request["capability"]!).GetString());
        var rerankMetering = ((JsonElement)providerTrace.ResultSummary["metering"]!).Deserialize<List<AiMeteringReceipt>>(ProviderJson.Options);
        var rerankReceipt = Assert.Single(rerankMetering!);
        Assert.Equal(AiMeteringOutcomes.Succeeded, rerankReceipt.Outcome);
        Assert.Equal(1, rerankReceipt.Measurements.Single(item => item.Name == AiMeteringMeasurementNames.ProviderCalls).Value);

        var readiness = await client.GetFromJsonAsync<ProviderReadinessEnvelope>($"/providers/{LocalTokenOverlapRerankerProviderTarget.ProviderId}/readiness");
        Assert.NotNull(readiness);
        var rerankReadiness = Assert.Single(readiness!.Items, item => item.Capability == ProviderCapabilityIds.AiRerank);
        Assert.True(rerankReadiness.Ready);
        Assert.Equal(ProviderQualificationStatuses.Validated, rerankReadiness.QualificationStatus);
        Assert.Contains($"trace:{rerankTraceId}", rerankReadiness.EvidenceRefs);

        var doctor = await client.GetFromJsonAsync<ProviderDoctorResult>($"/providers/{LocalTokenOverlapRerankerProviderTarget.ProviderId}/doctor");
        Assert.NotNull(doctor);
        var readinessCheck = doctor!.Checks.Single(check => check.Id == $"readiness.{ProviderCapabilityIds.AiRerank}");
        Assert.Equal(ProviderDoctorStatuses.Ok, readinessCheck.Status);
    }

    [Fact]
    public async Task Server_RerankProviderPayloadExcludesLargeRecordMetadata()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-rerank-payload-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 2);
        var client = factory.CreateClient();

        await CreatePayloadBudgetCollectionAsync(client);
        await UpsertPayloadBudgetRecordsAsync(client, contentChars: 180, metadataChars: 12_000);

        var retrieval = await PostJsonAsync<RetrievalResultEnvelope>(client, "/search", new RetrievalRequest
        {
            Query = "target retention",
            Collections = new List<string> { "payload-budget" },
            SearchMode = "lexical",
            Limit = 1,
            IncludeTrace = true,
            Lexical = new LexicalSearchOptions
            {
                Fields = new List<string> { "/content/text" },
                ScanLimit = 100,
                Scoring = "bm25"
            },
            Rerank = new RerankOptions
            {
                Enabled = true,
                Mode = "review",
                CandidateLimit = 40,
                ContentField = "text",
                MaxCandidateChars = 500,
                TimeoutSeconds = 30
            }
        });

        var match = Assert.Single(retrieval.Results);
        Assert.NotNull(match.Diagnostics);
        Assert.Contains("rerank.applied", match.Diagnostics!.ReasonCodes);
        Assert.DoesNotContain("rerank.fallback", match.Diagnostics.ReasonCodes);
        Assert.Equal(40, match.Diagnostics.CandidateCounts["rerankInputCandidates"]);
        var payloadBytes = ((JsonElement)match.Diagnostics.Details["rerankProviderPayloadBytes"]!).GetInt32();
        var maxInputBytes = ((JsonElement)match.Diagnostics.Details["rerankProviderMaxInputBytes"]!).GetInt32();
        Assert.InRange(payloadBytes, 1, maxInputBytes);
        Assert.NotNull(retrieval.Trace);
        Assert.True(retrieval.Trace!["rerankEnabled"]!.GetValue<bool>());
        Assert.False(retrieval.Trace["rerankFallbackApplied"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Server_RerankFallsBackToPreRerankResultsWhenProviderRejectsInput()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-rerank-fallback-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 2);
        var client = factory.CreateClient();

        await CreatePayloadBudgetCollectionAsync(client);
        await UpsertPayloadBudgetRecordsAsync(client, contentChars: 2600, metadataChars: 100);

        var retrieval = await PostJsonAsync<RetrievalResultEnvelope>(client, "/search", new RetrievalRequest
        {
            Query = "target retention",
            Collections = new List<string> { "payload-budget" },
            SearchMode = "lexical",
            Limit = 1,
            IncludeTrace = true,
            Lexical = new LexicalSearchOptions
            {
                Fields = new List<string> { "/content/text" },
                ScanLimit = 100,
                Scoring = "bm25"
            },
            Rerank = new RerankOptions
            {
                Enabled = true,
                Mode = "mechanics",
                CandidateLimit = 40,
                ContentField = "text",
                MaxCandidateChars = 2000,
                TimeoutSeconds = 30
            }
        });

        var match = Assert.Single(retrieval.Results);
        Assert.NotNull(match.Diagnostics);
        Assert.Contains("rerank.fallback", match.Diagnostics!.ReasonCodes);
        Assert.Contains("rank.pre_rerank_retained", match.Diagnostics.ReasonCodes);
        Assert.Equal("input_limit", ((JsonElement)match.Diagnostics.Details["rerankProviderStatus"]!).GetString());
        Assert.Equal(40, match.Diagnostics.CandidateCounts["rerankInputCandidates"]);
        Assert.NotNull(retrieval.Trace);
        Assert.True(retrieval.Trace!["rerankFallbackApplied"]!.GetValue<bool>());
        Assert.Equal("input_limit", retrieval.Trace["rerankProviderStatus"]!.GetValue<string?>());

        var rerankTraceId = retrieval.Trace["rerankTraceId"]!.GetValue<string?>();
        Assert.False(string.IsNullOrWhiteSpace(rerankTraceId));
        var providerTrace = await client.GetFromJsonAsync<TraceRecord>($"/traces/{rerankTraceId}");
        Assert.NotNull(providerTrace);
        Assert.Equal("Rejected", ((JsonElement)providerTrace!.ResultSummary["status"]!).GetString());
    }

    [Fact]
    public async Task Server_IngestsRagTextAsChunkRecords()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            IndexedMetadata = new List<string> { "/metadata/documentId", "/metadata/chunkIndex" },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 12 }
            }
        });
        await EnsureSuccessAsync(create);

        var text = string.Join(" ", Enumerable.Repeat("Retention policy guidance requires audit records and immutable archive review windows.", 6));
        var result = await PostJsonAsync<RagIngestTextResult>(client, "/collections/chunks/rag/ingest-text", new RagIngestTextRequest
        {
            DocumentId = "policy-doc",
            PartitionKey = "tenant-a",
            Text = text,
            ContentField = "body",
            SourceUri = "file://policy.md",
            SourceLabel = "Policy",
            Metadata = new JsonObject { ["status"] = "active" },
            Options = new RagIngestionOptions { ChunkChars = 120, ChunkOverlapChars = 20, IncludeTrace = true }
        });

        Assert.Equal("chunks", result.Collection);
        Assert.Equal("policy-doc", result.DocumentId);
        Assert.Equal("contentEmbedding", result.EmbeddingField);
        Assert.Equal(12, result.Dimensions);
        Assert.True(result.ChunkCount > 1);
        Assert.Equal(result.ChunkCount, result.Chunks.Count);
        Assert.Equal(result.ChunkCount, result.CreatedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.ReusedCount);
        Assert.NotNull(result.Trace);

        var firstChunk = result.Chunks[0];
        Assert.StartsWith("sha256:", firstChunk.TextHash, StringComparison.Ordinal);
        Assert.Equal("created", firstChunk.Action);

        var record = await client.GetFromJsonAsync<VyralRecord>($"/collections/chunks/records/tenant-a/{firstChunk.Id}");

        Assert.NotNull(record);
        Assert.Equal("rag.chunk", record!.Type);
        Assert.Equal("active", record.Metadata!["status"]!.GetValue<string?>());
        Assert.Equal("policy-doc", record.Metadata["documentId"]!.GetValue<string?>());
        Assert.Equal(result.TextHash, record.Metadata["documentTextHash"]!.GetValue<string?>());
        Assert.Equal(firstChunk.TextHash, record.Metadata["textHash"]!.GetValue<string?>());
        Assert.Equal(0, record.Metadata["chunkIndex"]!.GetValue<int>());
        Assert.NotEmpty(record.Content!["body"]!.GetValue<string?>()!);
        Assert.Equal(12, record.Vectors!["contentEmbedding"].Values.Length);
        Assert.Equal("content.body", record.Vectors["contentEmbedding"].SourceField);
        Assert.Equal("file://policy.md", record.Sources![0].Uri);
        Assert.Equal(0, record.Sources[0].Span!.Extensions!["chunkIndex"].GetInt32());

        var context = await PostJsonAsync<RagContextEnvelope>(client, "/rag/context", new RagContextRequest
        {
            Retrieval = new RetrievalRequest
            {
                Query = record.Content!["body"]!.GetValue<string?>()!,
                Collections = new List<string> { "chunks" },
                PartitionKeys = new List<string> { "tenant-a" },
                Limit = 1
            },
            ContentField = "body",
            MaxChars = 200,
            MaxCharsPerChunk = 200
        });

        Assert.Single(context.Chunks);
        Assert.Equal(firstChunk.Id, context.Chunks[0].Id);

        var traceId = result.Trace!["id"]!.ToString();
        var trace = await client.GetFromJsonAsync<TraceRecord>($"/traces/{traceId}");
        Assert.NotNull(trace);
        Assert.Equal("rag.ingest_text", trace!.Operation);
        Assert.Equal("policy-doc", ((JsonElement)trace.Request["documentId"]!).GetString());
    }

    [Fact]
    public async Task Server_RagIngestionJobsRunThroughExecutionRuntime()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-rag-job-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            IndexedMetadata = new List<string> { "/metadata/documentId", "/metadata/chunkIndex" },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 12 }
            }
        });
        await EnsureSuccessAsync(create);

        using var response = await client.PostAsJsonAsync("/collections/chunks/rag/ingest-text/jobs", new RagIngestTextRequest
        {
            DocumentId = "policy-doc",
            PartitionKey = "tenant-a",
            Text = string.Join(" ", Enumerable.Repeat("Retention policy guidance requires audit records.", 4)),
            ContentField = "body",
            SourceUri = "file://policy.md",
            Options = new RagIngestionOptions { ChunkChars = 90, ChunkOverlapChars = 10 }
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<RagIngestionJob>())!;
        Assert.False(string.IsNullOrWhiteSpace(accepted.Id));
        Assert.Equal(RagIngestionJobKinds.Text, accepted.Kind);
        Assert.Equal("chunks", accepted.Collection);
        Assert.Equal(1, accepted.Requested);

        RagIngestionJob? completed = null;
        for (var i = 0; i < 50; i++)
        {
            completed = await client.GetFromJsonAsync<RagIngestionJob>($"/rag/ingestion/jobs/{accepted.Id}");
            if (completed is not null && completed.Status == RagIngestionJobStatuses.Succeeded)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(completed);
        Assert.Equal(RagIngestionJobStatuses.Succeeded, completed!.Status);
        Assert.Equal(1, completed.Progress, precision: 3);
        Assert.Equal(1, completed.Succeeded);
        Assert.NotNull(completed.TextResult);
        Assert.Equal("policy-doc", completed.TextResult!.DocumentId);
        Assert.True(completed.ChunkCount > 0);

        await AssertJobBackedByExecutionRuntimeAsync(
            dbPath,
            accepted.Id,
            ExecutionRuntimeRagIngestionJobAdapter.TextHandlerId,
            ExecutionRuntimeRagIngestionJobAdapter.PluginId);

        var jobs = await client.GetFromJsonAsync<List<RagIngestionJob>>("/rag/ingestion/jobs?limit=5");
        Assert.NotNull(jobs);
        Assert.Contains(jobs, item => item.Id == accepted.Id);
        Assert.All(jobs, item =>
        {
            Assert.Null(item.TextResult);
            Assert.Null(item.BatchResult);
        });

        var jobsWithResults = await client.GetFromJsonAsync<List<RagIngestionJob>>("/rag/ingestion/jobs?limit=5&includeResult=true");
        Assert.NotNull(jobsWithResults);
        Assert.Contains(jobsWithResults, item => item.Id == accepted.Id && item.TextResult is not null);

        var runtime = await client.GetFromJsonAsync<ExecutionRuntimeSurface>("/execution/runtime");
        Assert.NotNull(runtime);
        Assert.Contains(runtime!.Plugins, plugin => plugin.PluginId == ExecutionRuntimeRagIngestionJobAdapter.PluginId);
        Assert.Contains(runtime.Handlers, handler => handler.HandlerId == ExecutionRuntimeRagIngestionJobAdapter.TextHandlerId);
        Assert.Contains(runtime.Handlers, handler => handler.HandlerId == ExecutionRuntimeRagIngestionJobAdapter.BatchHandlerId);
        Assert.NotNull(runtime.Status.OperationalPolicy);
        Assert.NotNull(runtime.Status.ResumePolicy);
        Assert.Contains(ExecutionCapabilityIds.LocalDispatch, runtime.Status.Adapter.Capabilities);
        Assert.Contains(ExecutionCapabilityIds.DurableRuns, runtime.Status.Adapter.Capabilities);

        var maintenance = await client.GetFromJsonAsync<ExecutionMaintenanceStatus>("/execution/runtime/maintenance");
        Assert.NotNull(maintenance);
        Assert.Equal("local.sqlite", maintenance!.RuntimeKind);
        Assert.True(maintenance.RowCounts["runs"] >= 1);

        var dryRunPruneResponse = await client.PostAsJsonAsync(
            "/execution/runtime/maintenance/prune",
            new ExecutionMaintenancePruneRequest
            {
                DryRun = true,
                RetainTerminalRuns = 100
            });
        dryRunPruneResponse.EnsureSuccessStatusCode();
        var dryRunPrune = await dryRunPruneResponse.Content.ReadFromJsonAsync<ExecutionMaintenancePruneResult>();
        Assert.NotNull(dryRunPrune);
        Assert.True(dryRunPrune!.DryRun);
        Assert.Equal(100, dryRunPrune.RetainTerminalRuns);

        var reconcileResponse = await client.PostAsJsonAsync(
            "/execution/runtime/maintenance/reconcile",
            new ExecutionMaintenanceDispatchReconcileRequest { DryRun = true, Limit = 10 });
        reconcileResponse.EnsureSuccessStatusCode();
        var reconcile = await reconcileResponse.Content.ReadFromJsonAsync<ExecutionMaintenanceDispatchReconcileResult>();
        Assert.NotNull(reconcile);
        Assert.True(reconcile!.DryRun);

        var runtimeRuns = await client.GetFromJsonAsync<List<ExecutionRun>>(
            $"/execution/runs?handlerId={ExecutionRuntimeRagIngestionJobAdapter.TextHandlerId}&limit=5");
        Assert.NotNull(runtimeRuns);
        Assert.Contains(runtimeRuns, run => run.Id == accepted.Id && run.Result is null);

        var runtimeRunsWithResults = await client.GetFromJsonAsync<List<ExecutionRun>>(
            $"/execution/runs?handlerId={ExecutionRuntimeRagIngestionJobAdapter.TextHandlerId}&status={ExecutionRunStatuses.Succeeded}&limit=5&includeResult=true");
        Assert.NotNull(runtimeRunsWithResults);
        Assert.Contains(runtimeRunsWithResults, run => run.Id == accepted.Id && run.Result is not null);

        var runtimeRun = await client.GetFromJsonAsync<ExecutionRun>($"/execution/runs/{accepted.Id}");
        Assert.NotNull(runtimeRun);
        Assert.Equal(ExecutionRunStatuses.Succeeded, runtimeRun!.Status);
        Assert.Equal(ExecutionRuntimeRagIngestionJobAdapter.TextHandlerId, runtimeRun.HandlerId);
        Assert.Equal(ExecutionRuntimeRagIngestionJobAdapter.PluginId, runtimeRun.PluginId);
        Assert.NotNull(runtimeRun.Result);

        var boundedHistory = await client.GetFromJsonAsync<List<ExecutionTraceEvent>>($"/execution/runs/{accepted.Id}/history?limit=2");
        Assert.NotNull(boundedHistory);
        Assert.Equal(2, boundedHistory!.Count);

        var runtimeHistory = await client.GetFromJsonAsync<List<ExecutionTraceEvent>>($"/execution/runs/{accepted.Id}/history");
        Assert.NotNull(runtimeHistory);
        Assert.Contains(runtimeHistory, item => item.Type == ExecutionEventTypes.RunStarted);
        Assert.Contains(runtimeHistory, item => item.Type == ExecutionEventTypes.RunCompleted);
        Assert.Contains(runtimeHistory, item => item.Type == ExecutionEventTypes.ArtifactWritten);

        var runtimeArtifacts = await client.GetFromJsonAsync<List<ExecutionArtifact>>($"/execution/runs/{accepted.Id}/artifacts");
        Assert.NotNull(runtimeArtifacts);
        var artifact = Assert.Single(runtimeArtifacts, item => item.Name == "rag-ingestion-result");
        Assert.Equal(ExecutionArtifactKinds.Json, artifact.Kind);
        Assert.NotNull(artifact.Content);
        var artifactByName = await client.GetFromJsonAsync<ExecutionArtifact>($"/execution/runs/{accepted.Id}/artifacts/{artifact.Name}");
        Assert.NotNull(artifactByName);
        Assert.Equal(artifact.Id, artifactByName!.Id);
        Assert.NotNull(artifactByName.Content);
        var artifactById = await client.GetFromJsonAsync<ExecutionArtifact>($"/execution/runs/{accepted.Id}/artifacts/{artifact.Id}");
        Assert.NotNull(artifactById);
        Assert.Equal(artifact.Name, artifactById!.Name);

        var cancelledTerminalRun = await client.DeleteFromJsonAsync<ExecutionRun>($"/execution/runs/{accepted.Id}");
        Assert.NotNull(cancelledTerminalRun);
        Assert.Equal(ExecutionRunStatuses.Succeeded, cancelledTerminalRun!.Status);
    }

    [Fact]
    public async Task Server_BatchIngestsRagTextAndReportsPerItemFailures()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            IndexedMetadata = new List<string> { "/metadata/documentId", "/metadata/topic" },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 12 }
            }
        });
        await EnsureSuccessAsync(create);

        var result = await PostJsonAsync<RagIngestTextBatchResult>(client, "/collections/chunks/rag/ingest-text/batch", new RagIngestTextBatchRequest
        {
            ContinueOnError = true,
            Items = new List<RagIngestTextRequest>
            {
                new()
                {
                    DocumentId = "retention",
                    PartitionKey = "tenant-a",
                    Text = "Retention holds keep protected records from deletion.",
                    Metadata = new JsonObject { ["topic"] = "records" }
                },
                new()
                {
                    DocumentId = "empty",
                    PartitionKey = "tenant-a",
                    Text = ""
                },
                new()
                {
                    DocumentId = "travel",
                    PartitionKey = "tenant-a",
                    Text = "Travel reimbursement requires receipts and approval.",
                    Metadata = new JsonObject { ["topic"] = "finance" }
                }
            }
        });

        Assert.Equal("chunks", result.Collection);
        Assert.Equal(3, result.Requested);
        Assert.Equal(3, result.Attempted);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.False(result.StoppedOnError);
        Assert.Equal(2, result.ChunkCount);
        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(2, result.VectorGeneratedCount);
        Assert.Equal("succeeded", result.Items[0].Status);
        Assert.Equal("retention", result.Items[0].DocumentId);
        Assert.NotNull(result.Items[0].Result);
        Assert.Equal("failed", result.Items[1].Status);
        Assert.Contains("non-empty text", result.Items[1].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("succeeded", result.Items[2].Status);
        Assert.NotNull(result.Items[2].Result);

        var firstChunk = result.Items[0].Result!.Chunks[0];
        var record = await client.GetFromJsonAsync<VyralRecord>($"/collections/chunks/records/tenant-a/{firstChunk.Id}");
        Assert.NotNull(record);
        Assert.Equal("retention", record!.Metadata!["documentId"]!.GetValue<string?>());
    }

    [Fact]
    public async Task Server_BatchRagIngestionStopsOnFirstFailureByDefault()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 12 }
            }
        });
        await EnsureSuccessAsync(create);

        var result = await PostJsonAsync<RagIngestTextBatchResult>(client, "/collections/chunks/rag/ingest-text/batch", new RagIngestTextBatchRequest
        {
            Items = new List<RagIngestTextRequest>
            {
                new()
                {
                    DocumentId = "empty",
                    PartitionKey = "tenant-a",
                    Text = ""
                },
                new()
                {
                    DocumentId = "retention",
                    PartitionKey = "tenant-a",
                    Text = "Retention holds keep protected records from deletion."
                }
            }
        });

        Assert.Equal(2, result.Requested);
        Assert.Equal(1, result.Attempted);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.True(result.StoppedOnError);
        Assert.Single(result.Items);
        Assert.Equal("failed", result.Items[0].Status);
    }

    [Fact]
    public async Task Server_RagIngestionCanReplaceStaleDocumentChunks()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-rag-replace-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 8);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            IndexedMetadata = new List<string> { "/metadata/documentId", "/metadata/chunkIndex" },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 8 }
            }
        });
        await EnsureSuccessAsync(create);

        var originalText = string.Join(" ", Enumerable.Repeat("Original retention paragraph with archival details and review windows.", 10));
        var original = await PostJsonAsync<RagIngestTextResult>(client, "/collections/chunks/rag/ingest-text", new RagIngestTextRequest
        {
            DocumentId = "policy-doc",
            PartitionKey = "tenant-a",
            Text = originalText,
            Options = new RagIngestionOptions { ChunkChars = 120, ChunkOverlapChars = 10 }
        });
        Assert.True(original.ChunkCount > 1);
        Assert.Equal(original.ChunkCount, original.CreatedCount);
        Assert.All(original.Chunks, chunk => Assert.Equal("created", chunk.Action));

        var repeated = await PostJsonAsync<RagIngestTextResult>(client, "/collections/chunks/rag/ingest-text", new RagIngestTextRequest
        {
            DocumentId = "policy-doc",
            PartitionKey = "tenant-a",
            Text = originalText,
            Options = new RagIngestionOptions { ChunkChars = 120, ChunkOverlapChars = 10, ReplaceDocumentChunks = true, SkipUnchangedChunks = true }
        });

        Assert.Equal(original.ChunkCount, repeated.ReusedCount);
        Assert.Equal(0, repeated.CreatedCount);
        Assert.Equal(0, repeated.UpdatedCount);
        Assert.Equal(0, repeated.DeletedStaleCount);
        Assert.Equal(original.Chunks[0].Revision, repeated.Chunks[0].Revision);
        Assert.All(repeated.Chunks, chunk => Assert.Equal("reused", chunk.Action));

        var metadataChanged = await PostJsonAsync<RagIngestTextResult>(client, "/collections/chunks/rag/ingest-text", new RagIngestTextRequest
        {
            DocumentId = "policy-doc",
            PartitionKey = "tenant-a",
            Text = originalText,
            Metadata = new JsonObject { ["status"] = "active" },
            Options = new RagIngestionOptions { ChunkChars = 120, ChunkOverlapChars = 10, ReplaceDocumentChunks = true, SkipUnchangedChunks = true }
        });

        Assert.Equal(original.ChunkCount, metadataChanged.UpdatedCount);
        Assert.Equal(0, metadataChanged.ReusedCount);
        Assert.All(metadataChanged.Chunks, chunk => Assert.Equal("updated", chunk.Action));

        var replacement = await PostJsonAsync<RagIngestTextResult>(client, "/collections/chunks/rag/ingest-text", new RagIngestTextRequest
        {
            DocumentId = "policy-doc",
            PartitionKey = "tenant-a",
            Text = "Replacement retention summary with new authoritative wording.",
            Options = new RagIngestionOptions { ChunkChars = 120, ChunkOverlapChars = 10, ReplaceDocumentChunks = true }
        });

        Assert.Single(replacement.Chunks);
        Assert.Equal(1, replacement.CreatedCount);
        Assert.Equal(0, replacement.ReusedCount);
        Assert.Equal(original.ChunkCount, replacement.DeletedStaleCount);
        Assert.DoesNotContain(original.Chunks.Select(chunk => chunk.Id), id => replacement.Chunks.Any(chunk => chunk.Id == id));

        var query = await PostJsonAsync<RecordQueryResult>(client, "/collections/chunks/query", new QueryEnvelope
        {
            PartitionKeys = new List<string> { "tenant-a" },
            Filter = new FilterNode { Path = "/metadata/documentId", Op = "eq", Value = "policy-doc" }
        });
        Assert.Equal(replacement.ChunkCount, query.Items.Count);
        Assert.Equal(replacement.Chunks[0].Id, Assert.Single(query.Items).Id);

        var staleResponse = await client.GetAsync($"/collections/chunks/records/tenant-a/{original.Chunks[0].Id}");
        Assert.Equal(HttpStatusCode.NotFound, staleResponse.StatusCode);
    }

    [Fact]
    public async Task Server_RagIngestionCanPersistAndReuseManifestRecord()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-rag-manifest-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 8);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            IndexedMetadata = new List<string> { "/metadata/documentId", "/metadata/chunkIndex", "/type" },
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 8 }
            }
        });
        await EnsureSuccessAsync(create);

        var sourceText = string.Join(" ", Enumerable.Repeat("Manifest retention source text should be chunked without raw manifest storage.", 5));
        var original = await PostJsonAsync<RagIngestTextResult>(client, "/collections/chunks/rag/ingest-text", new RagIngestTextRequest
        {
            DocumentId = "manifest-doc",
            PartitionKey = "tenant-a",
            Text = sourceText,
            Options = new RagIngestionOptions { ChunkChars = 100, ChunkOverlapChars = 10, PersistManifest = true, IncludeTrace = true }
        });

        Assert.Equal("created", original.ManifestAction);
        Assert.Equal("manifest-doc-manifest", original.ManifestId);
        Assert.StartsWith("sha256:", original.ManifestHash, StringComparison.Ordinal);
        Assert.Equal(1, original.ManifestRevision);
        Assert.NotNull(original.Trace);
        Assert.Equal(original.ManifestId, original.Trace!["manifestId"]!.GetValue<string?>());

        var manifest = await client.GetFromJsonAsync<VyralRecord>($"/collections/chunks/records/tenant-a/{original.ManifestId}");
        Assert.NotNull(manifest);
        Assert.Equal("rag.manifest", manifest!.Type);
        Assert.Equal("v1", manifest.SchemaVersion);
        Assert.Equal("manifest-doc", manifest.Metadata!["documentId"]!.GetValue<string?>());
        Assert.Equal(original.ManifestHash, manifest.Metadata["manifestHash"]!.GetValue<string?>());
        Assert.Equal(original.ChunkCount, manifest.Metadata["chunkCount"]!.GetValue<int>());
        Assert.Null(manifest.Vectors);

        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("Manifest retention source text", manifestJson, StringComparison.Ordinal);
        Assert.Contains(original.Chunks[0].Id, manifestJson, StringComparison.Ordinal);

        var repeated = await PostJsonAsync<RagIngestTextResult>(client, "/collections/chunks/rag/ingest-text", new RagIngestTextRequest
        {
            DocumentId = "manifest-doc",
            PartitionKey = "tenant-a",
            Text = sourceText,
            Options = new RagIngestionOptions { ChunkChars = 100, ChunkOverlapChars = 10, PersistManifest = true, SkipUnchangedChunks = true, ReplaceDocumentChunks = true }
        });

        Assert.Equal(original.ChunkCount, repeated.ReusedCount);
        Assert.Equal("reused", repeated.ManifestAction);
        Assert.Equal(original.ManifestHash, repeated.ManifestHash);
        Assert.Equal(original.ManifestRevision, repeated.ManifestRevision);

        var query = await PostJsonAsync<RecordQueryResult>(client, "/collections/chunks/query", new QueryEnvelope
        {
            PartitionKeys = new List<string> { "tenant-a" },
            Filter = new FilterNode
            {
                Combine = "all",
                Children = new List<FilterNode>
                {
                    new() { Path = "/metadata/documentId", Op = "eq", Value = "manifest-doc" },
                    new() { Path = "/type", Op = "eq", Value = "rag.chunk" }
                }
            }
        });

        Assert.Equal(repeated.ChunkCount, query.Items.Count);
        Assert.DoesNotContain(query.Items, record => record.Type == "rag.manifest");
    }

    [Fact]
    public async Task Server_ExposesOpenApiContractForClientBoundary()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        using var contractRequest = new HttpRequestMessage(HttpMethod.Get, "/openapi/vyral.json");
        contractRequest.Headers.Add("X-Correlation-ID", "openapi-contract-test");
        var response = await client.SendAsync(contractRequest);
        var json = await response.Content.ReadAsStringAsync();
        var schemaResponse = await client.GetAsync("/contracts/schemas/vyral-public.schema.json");
        var schemaJson = await schemaResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("openapi-contract-test", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.True(schemaResponse.Headers.TryGetValues("X-Correlation-ID", out var schemaCorrelationIds));
        Assert.False(string.IsNullOrWhiteSpace(schemaCorrelationIds.Single()));

        using var document = JsonDocument.Parse(json);
        Assert.Equal("3.1.0", document.RootElement.GetProperty("openapi").GetString());
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("jsonSchemaDialect").GetString());
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
        Assert.Equal("application/schema+json", schemaResponse.Content.Headers.ContentType?.MediaType);
        using var schemaDocument = JsonDocument.Parse(schemaJson);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schemaDocument.RootElement.GetProperty("$schema").GetString());
        Assert.True(schemaDocument.RootElement.GetProperty("$defs").TryGetProperty("VyralRecord", out _));
        Assert.True(document.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("VyralApiKey", out _));
        Assert.True(document.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("VyralBearer", out _));

        var paths = document.RootElement.GetProperty("paths");
        Assert.Empty(paths.GetProperty("/health").GetProperty("get").GetProperty("security").EnumerateArray());
        Assert.Empty(paths.GetProperty("/readiness").GetProperty("get").GetProperty("security").EnumerateArray());
        Assert.Empty(paths.GetProperty("/openapi/vyral.json").GetProperty("get").GetProperty("security").EnumerateArray());
        Assert.Empty(paths.GetProperty("/contracts/schemas/vyral-public.schema.json").GetProperty("get").GetProperty("security").EnumerateArray());
        AssertPath(paths, "/health", "get");
        AssertPath(paths, "/readiness", "get");
        AssertPath(paths, "/ingest/record-artifact", "post");
        AssertPath(paths, "/execution/runtime", "get");
        AssertPath(paths, "/execution/runtime/effective", "get");
        AssertPath(paths, "/execution/runtime/maintenance", "get");
        AssertPath(paths, "/execution/runtime/maintenance/prune", "post");
        AssertPath(paths, "/execution/runtime/maintenance/reconcile", "post");
        AssertPath(paths, "/execution/workers/leases", "post");
        AssertPath(paths, "/execution/workers/leases/heartbeat", "post");
        AssertPath(paths, "/execution/workers/leases/reports", "post");
        AssertPath(paths, "/execution/workers/leases/events", "post");
        AssertPath(paths, "/execution/workers/leases/artifacts", "post");
        AssertPath(paths, "/execution/workers/leases/checkpoints", "post");
        AssertPath(paths, "/execution/workers/leases/checkpoints/read", "post");
        AssertPath(paths, "/execution/workers/leases/wait", "post");
        AssertPath(paths, "/execution/workers/leases/complete", "post");
        AssertPath(paths, "/execution/runs", "get", "post");
        AssertPath(paths, "/execution/runs/{runId}", "get", "delete");
        AssertPath(paths, "/execution/runs/{runId}/events", "post");
        AssertPath(paths, "/execution/runs/{runId}/history", "get");
        AssertPath(paths, "/execution/runs/{runId}/artifacts", "get");
        AssertPath(paths, "/execution/runs/{runId}/artifacts/{artifactRef}", "get");
        AssertPath(paths, "/execution/runs/{runId}/checkpoints/{key}", "get");
        AssertPath(paths, "/embedding-providers", "get");
        AssertPath(paths, "/embedding-providers/guidance", "get");
        AssertPath(paths, "/embedding-providers/doctor", "get");
        AssertPath(paths, "/embeddings", "post");
        AssertPath(paths, "/embeddings/jobs", "post", "get");
        AssertPath(paths, "/embeddings/jobs/{jobId}", "get", "delete");
        AssertPath(paths, "/providers", "get");
        AssertPath(paths, "/providers/capabilities", "get");
        AssertPath(paths, "/providers/readiness", "get");
        AssertPath(paths, "/providers/doctor", "get");
        AssertPath(paths, "/providers/{provider}", "get");
        AssertPath(paths, "/providers/{provider}/doctor", "get");
        AssertPath(paths, "/providers/{provider}/readiness", "get");
        AssertPath(paths, "/providers/quotas", "get");
        AssertPath(paths, "/providers/{provider}/quota", "get");
        AssertPath(paths, "/providers/{provider}/qualifications", "get");
        AssertPath(paths, "/providers/{provider}/models", "get");
        AssertPath(paths, "/providers/{provider}/qualify", "post");
        AssertPath(paths, "/providers/{provider}/run", "post");
        AssertPath(paths, "/providers/{provider}/jobs", "post");
        AssertPath(paths, "/provider-jobs", "get");
        AssertPath(paths, "/provider-jobs/{jobId}", "get", "delete");
        AssertPath(paths, "/openapi/vyral.json", "get");
        AssertPath(paths, "/contracts/schemas/vyral-public.schema.json", "get");
        AssertPath(paths, "/graph/provider-shapes", "get");
        AssertPath(paths, "/graph/provider-shapes/{providerId}", "get");
        AssertPath(paths, "/collections", "get", "post");
        AssertPath(paths, "/collections/{collection}", "get", "delete");
        AssertPath(paths, "/collections/{collection}/export", "get", "post");
        AssertPath(paths, "/collections/{collection}/import", "post");
        AssertPath(paths, "/collections/{collection}/import/jobs", "post");
        AssertPath(paths, "/collections/{collection}/graph/import", "post");
        AssertPath(paths, "/collections/{collection}/graph/import/preflight", "post");
        AssertPath(paths, "/collections/{collection}/graph/export", "post");
        AssertPath(paths, "/collections/{collection}/graph/traverse", "post");
        AssertPath(paths, "/collections/{collection}/graph/inspect", "post");
        AssertPath(paths, "/collections/{collection}/graph/doctor", "post");
        AssertPath(paths, "/collections/{collection}/graph/import/jobs", "post");
        AssertPath(paths, "/collections/{collection}/graph/inspect/jobs", "post");
        AssertPath(paths, "/collections/{collection}/graph/doctor/jobs", "post");
        AssertPath(paths, "/graph/jobs", "get");
        AssertPath(paths, "/graph/jobs/{jobId}", "get", "delete");
        AssertPath(paths, "/collections/{collection}/inspect", "get");
        AssertPath(paths, "/collections/{collection}/records", "post");
        AssertPath(paths, "/collections/{collection}/records/batch", "post");
        AssertPath(paths, "/collections/{collection}/records/batch/jobs", "post");
        AssertPath(paths, "/record-import/jobs", "get");
        AssertPath(paths, "/record-import/jobs/{jobId}", "get", "delete");
        AssertPath(paths, "/collections/{collection}/rag/ingest-text", "post");
        AssertPath(paths, "/collections/{collection}/rag/ingest-text/batch", "post");
        AssertPath(paths, "/collections/{collection}/rag/ingest-text/jobs", "post");
        AssertPath(paths, "/collections/{collection}/rag/ingest-text/batch/jobs", "post");
        AssertPath(paths, "/rag/ingestion/jobs", "get");
        AssertPath(paths, "/rag/ingestion/jobs/{jobId}", "get", "delete");
        AssertPath(paths, "/collections/{collection}/records/{pk}/{id}", "get", "delete");
        AssertPath(paths, "/collections/{collection}/query", "post");
        AssertPath(paths, "/collections/{collection}/search", "post");
        AssertPath(paths, "/objects/{container}", "get");
        AssertPath(paths, "/objects/{container}/{key}", "put", "get", "delete");
        AssertPath(paths, "/search", "post");
        AssertPath(paths, "/retrieval/profiles", "get");
        AssertPath(paths, "/retrieval/evaluate", "post");
        AssertPath(paths, "/retrieval/evaluate/compare", "post");
        AssertPath(paths, "/retrieval/evaluate/compare/jobs", "post");
        AssertPath(paths, "/retrieval/evaluate/jobs", "post", "get");
        AssertPath(paths, "/retrieval/evaluate/jobs/{jobId}", "get", "delete");
        AssertPath(paths, "/rag/context", "post");
        AssertPath(paths, "/rag/context/evaluate", "post");
        AssertPath(paths, "/rag/prompt", "post");
        AssertPath(paths, "/traces", "get");
        AssertPath(paths, "/traces/summary", "get");
        AssertPath(paths, "/traces/prune", "post");
        AssertPath(paths, "/traces/export", "post");
        AssertPath(paths, "/traces/{id}", "get");

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(
            "../../../contracts/schemas/vyral-public.schema.json#/$defs/VyralRecord",
            schemas.GetProperty("VyralRecord").GetProperty("$ref").GetString());
        Assert.True(schemas.TryGetProperty("VyralRecord", out _));
        Assert.True(schemas.TryGetProperty("ExecutionRuntimeSurface", out _));
        Assert.True(schemas.TryGetProperty("ArtifactRecordIngestManifest", out _));
        Assert.True(schemas.TryGetProperty("ArtifactRecordDescriptor", out _));
        Assert.True(schemas.TryGetProperty("ArtifactRecordIngestReceipt", out _));
        Assert.True(schemas.TryGetProperty("ExternalContextProof", out _));
        Assert.True(schemas.TryGetProperty("ExecutionRuntimeAdapterStatus", out _));
        Assert.True(schemas.TryGetProperty("ExecutionMaintenanceStatus", out _));
        Assert.True(schemas.TryGetProperty("ExecutionMaintenancePruneRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionMaintenancePruneResult", out _));
        Assert.True(schemas.TryGetProperty("ExecutionMaintenanceDispatchReconcileRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionMaintenanceDispatchReconcileResult", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalWorkerLeaseRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalWorkerLease", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalWorkerHeartbeatRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalWorkerReportRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalWorkerEventRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalWorkerArtifactRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalWorkerCheckpointRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalWorkerCheckpointReadRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalWorkerWaitRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalWorkerWaitResponse", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalWorkerCompletionRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionOperationalPolicy", out _));
        Assert.True(schemas.TryGetProperty("ExecutionResumePolicy", out _));
        Assert.True(schemas.TryGetProperty("ExecutionPluginDescriptor", out _));
        Assert.True(schemas.TryGetProperty("ExecutionRun", out _));
        Assert.True(schemas.TryGetProperty("ExecutionTraceEvent", out _));
        Assert.True(schemas.TryGetProperty("ExecutionArtifact", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalEventRequest", out _));
        Assert.True(schemas.TryGetProperty("ExecutionExternalEvent", out _));
        Assert.True(schemas.TryGetProperty("CollectionInspectionResult", out _));
        Assert.True(schemas.TryGetProperty("CollectionExportEnvelope", out _));
        Assert.True(schemas.TryGetProperty("CollectionExportRequest", out _));
        Assert.True(schemas.TryGetProperty("CollectionImportRequest", out _));
        Assert.True(schemas.TryGetProperty("CollectionImportResult", out _));
        Assert.True(schemas.TryGetProperty("CollectionSnapshotHashComparison", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphEnvelope", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphCollectionImportRequest", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphCollectionImportResult", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphCollectionImportPreflightResult", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphCollectionExportRequest", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphCollectionExportResult", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphTraversalRequest", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphTraversalResult", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphProviderShape", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphDoctorRequest", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphDoctorResult", out _));
        Assert.True(schemas.TryGetProperty("VyralGraphSeedCoverage", out _));
        Assert.True(schemas.TryGetProperty("GraphJob", out _));
        Assert.Contains(
            schemas.GetProperty("GraphJob").GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
            item => item == "running");
        Assert.True(schemas.GetProperty("VyralGraphProviderShape").GetProperty("properties").TryGetProperty("id", out _));
        Assert.True(schemas.GetProperty("CollectionExportEnvelope").GetProperty("properties").TryGetProperty("contentHash", out _));
        Assert.True(schemas.GetProperty("CollectionExportEnvelope").GetProperty("properties").TryGetProperty("truncated", out _));
        Assert.True(schemas.GetProperty("CollectionImportRequest").GetProperty("properties").TryGetProperty("allowPartialSnapshot", out _));
        Assert.True(schemas.TryGetProperty("RagCollectionInspection", out _));
        Assert.True(schemas.TryGetProperty("VectorFieldInspection", out _));
        Assert.True(schemas.TryGetProperty("CollectionInspectionAnomaly", out _));
        Assert.True(schemas.TryGetProperty("RecordBatchUpsertRequest", out _));
        Assert.True(schemas.TryGetProperty("RecordWritePrecondition", out _));
        Assert.True(schemas.TryGetProperty("RecordBatchUpsertResult", out _));
        Assert.True(schemas.TryGetProperty("RecordImportJob", out _));
        Assert.Contains(
            schemas.GetProperty("RecordImportJob").GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
            item => item == "batch_upsert");
        Assert.True(schemas.TryGetProperty("RagIngestTextRequest", out _));
        Assert.True(schemas.TryGetProperty("RagIngestTextBatchRequest", out _));
        Assert.True(schemas.TryGetProperty("RagIngestTextBatchResult", out _));
        Assert.True(schemas.TryGetProperty("RagIngestTextBatchItemResult", out _));
        Assert.True(schemas.TryGetProperty("RagIngestTextResult", out _));
        Assert.True(schemas.TryGetProperty("RagIngestActionSummary", out _));
        Assert.True(schemas.TryGetProperty("RagIngestHashComparison", out _));
        Assert.True(schemas.TryGetProperty("RagIngestChunkResult", out _));
        Assert.True(schemas.TryGetProperty("RagIngestStaleDeleteResult", out _));
        Assert.True(schemas.TryGetProperty("RagIngestionJob", out _));
        Assert.Contains(
            schemas.GetProperty("RagIngestionJob").GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
            item => item == "running");
        Assert.True(schemas.TryGetProperty("EmbeddingOptions", out _));
        Assert.True(schemas.TryGetProperty("RagIngestionOptions", out _));
        var ragIngestRequestProperties = schemas.GetProperty("RagIngestTextRequest").GetProperty("properties");
        Assert.True(ragIngestRequestProperties.TryGetProperty("embedding", out _));
        Assert.True(ragIngestRequestProperties.TryGetProperty("options", out _));
        Assert.False(ragIngestRequestProperties.TryGetProperty("embeddingPurpose", out _));
        Assert.False(ragIngestRequestProperties.TryGetProperty("passagePrefix", out _));
        Assert.False(ragIngestRequestProperties.TryGetProperty("dryRun", out _));
        var ragIngestionOptionsProperties = schemas.GetProperty("RagIngestionOptions").GetProperty("properties");
        Assert.True(ragIngestionOptionsProperties.TryGetProperty("persistManifest", out _));
        Assert.True(ragIngestionOptionsProperties.TryGetProperty("reuseExistingChunkVectors", out _));
        Assert.True(ragIngestionOptionsProperties.TryGetProperty("deduplicateExistingChunks", out _));
        Assert.True(ragIngestionOptionsProperties.TryGetProperty("chunkDedupeScope", out _));
        Assert.True(ragIngestionOptionsProperties.TryGetProperty("dryRun", out _));
        Assert.True(ragIngestionOptionsProperties.TryGetProperty("expectedPlanHash", out _));
        Assert.True(ragIngestionOptionsProperties.TryGetProperty("expectedManifestHash", out _));
        Assert.True(schemas.GetProperty("RagIngestTextResult").GetProperty("properties").TryGetProperty("manifestId", out _));
        Assert.True(schemas.GetProperty("RagIngestTextResult").GetProperty("properties").TryGetProperty("planHash", out _));
        Assert.True(schemas.GetProperty("RagIngestTextResult").GetProperty("properties").TryGetProperty("embeddingPurpose", out _));
        Assert.True(schemas.GetProperty("RagIngestTextResult").GetProperty("properties").TryGetProperty("actionSummary", out _));
        Assert.True(schemas.GetProperty("RagIngestTextResult").GetProperty("properties").TryGetProperty("planHashComparison", out _));
        Assert.True(schemas.GetProperty("RagIngestTextResult").GetProperty("properties").TryGetProperty("manifestHashComparison", out _));
        Assert.True(schemas.GetProperty("RagIngestTextResult").GetProperty("properties").TryGetProperty("staleDeletes", out _));
        Assert.True(schemas.GetProperty("RagIngestTextResult").GetProperty("properties").TryGetProperty("dryRun", out _));
        Assert.True(schemas.GetProperty("RagIngestTextResult").GetProperty("properties").TryGetProperty("vectorReusedCount", out _));
        Assert.True(schemas.GetProperty("RagIngestTextResult").GetProperty("properties").TryGetProperty("deduplicatedCount", out _));
        Assert.True(schemas.GetProperty("RagIngestChunkResult").GetProperty("properties").TryGetProperty("embeddingAction", out _));
        Assert.True(schemas.GetProperty("RagIngestChunkResult").GetProperty("properties").TryGetProperty("embeddingTextHash", out _));
        Assert.True(schemas.GetProperty("RagIngestChunkResult").GetProperty("properties").TryGetProperty("deduplicatedFromId", out _));
        Assert.True(schemas.TryGetProperty("TracePruneRequest", out _));
        Assert.True(schemas.TryGetProperty("TracePruneResult", out _));
        Assert.True(schemas.TryGetProperty("TraceSummary", out _));
        Assert.True(schemas.TryGetProperty("TraceOperationSummary", out _));
        var traceSummaryProperties = schemas.GetProperty("TraceSummary").GetProperty("properties");
        Assert.True(traceSummaryProperties.TryGetProperty("statusCounts", out _));
        Assert.True(traceSummaryProperties.TryGetProperty("failureClassCounts", out _));
        Assert.True(traceSummaryProperties.TryGetProperty("providerStatusCounts", out _));
        Assert.True(traceSummaryProperties.TryGetProperty("providerCounts", out _));
        Assert.True(traceSummaryProperties.TryGetProperty("capabilityCounts", out _));
        var traceOperationSummaryProperties = schemas.GetProperty("TraceOperationSummary").GetProperty("properties");
        Assert.True(traceOperationSummaryProperties.TryGetProperty("statusCounts", out _));
        Assert.True(traceOperationSummaryProperties.TryGetProperty("failureClassCounts", out _));
        Assert.True(traceOperationSummaryProperties.TryGetProperty("providerStatusCounts", out _));
        Assert.True(traceOperationSummaryProperties.TryGetProperty("providerCounts", out _));
        Assert.True(traceOperationSummaryProperties.TryGetProperty("capabilityCounts", out _));
        Assert.True(schemas.TryGetProperty("TraceExportRequest", out _));
        Assert.True(schemas.TryGetProperty("TraceExportBundle", out _));
        Assert.True(schemas.TryGetProperty("TraceExportWarning", out _));
        Assert.True(schemas.TryGetProperty("RagContextRetrievalMatch", out _));
        Assert.True(schemas.TryGetProperty("RagContextCitation", out _));
        Assert.True(schemas.TryGetProperty("RagContextAssemblyOptions", out _));
        Assert.True(schemas.TryGetProperty("RagContextGroupBudget", out _));
        Assert.True(schemas.TryGetProperty("RagContextGraphExpansionOptions", out _));
        Assert.True(schemas.TryGetProperty("RagContextEvaluationRequest", out _));
        Assert.True(schemas.TryGetProperty("RagContextEvaluationResult", out _));
        Assert.True(schemas.TryGetProperty("RagContextGraphEvaluationResult", out _));
        Assert.True(schemas.TryGetProperty("RagContextGraphEvaluationFailureModes", out _));
        Assert.True(schemas.TryGetProperty("RagContextGraphContext", out _));
        Assert.True(schemas.TryGetProperty("RagContextGraphExpansionSummary", out _));
        Assert.True(schemas.TryGetProperty("RagContextGraphSeedDiagnostic", out _));
        Assert.True(schemas.TryGetProperty("RagContextGraphProvenance", out _));
        Assert.True(schemas.TryGetProperty("RagPromptRequest", out _));
        Assert.True(schemas.TryGetProperty("RagPromptTemplateOptions", out _));
        Assert.True(schemas.TryGetProperty("RagPromptEnvelope", out _));
        Assert.True(schemas.TryGetProperty("RagPromptMessage", out _));
        var ragContextRequestProperties = schemas.GetProperty("RagContextRequest").GetProperty("properties");
        Assert.True(ragContextRequestProperties.TryGetProperty("retrieval", out _));
        Assert.True(ragContextRequestProperties.TryGetProperty("contextAssembly", out _));
        Assert.True(ragContextRequestProperties.TryGetProperty("graphExpansion", out _));
        Assert.True(ragContextRequestProperties.TryGetProperty("maxCitationsPerChunk", out _));
        Assert.True(ragContextRequestProperties.TryGetProperty("includeCitations", out _));
        Assert.True(ragContextRequestProperties.TryGetProperty("includeContextText", out _));
        Assert.False(ragContextRequestProperties.TryGetProperty("vectorFields", out _));
        Assert.False(ragContextRequestProperties.TryGetProperty("embeddingPurpose", out _));
        Assert.False(ragContextRequestProperties.TryGetProperty("queryPrefix", out _));
        Assert.False(ragContextRequestProperties.TryGetProperty("groupByPath", out _));
        Assert.True(schemas.GetProperty("RagContextEnvelope").GetProperty("properties").TryGetProperty("citations", out _));
        Assert.True(schemas.GetProperty("RagContextEnvelope").GetProperty("properties").TryGetProperty("contextText", out _));
        Assert.True(schemas.GetProperty("RagContextEnvelope").GetProperty("properties").TryGetProperty("contextTextHash", out _));
        Assert.True(schemas.GetProperty("RagContextEnvelope").GetProperty("properties").TryGetProperty("omittedCitationCount", out _));
        Assert.True(schemas.GetProperty("RagContextEnvelope").GetProperty("properties").TryGetProperty("graphContext", out _));
        Assert.True(schemas.GetProperty("RagContextEnvelope").GetProperty("properties").TryGetProperty("graphExpansion", out _));
        Assert.True(schemas.GetProperty("RagContextEvaluationResult").GetProperty("properties").TryGetProperty("failureCategoryCounts", out _));
        Assert.True(schemas.GetProperty("RagContextEvaluationCaseResult").GetProperty("properties").TryGetProperty("graphContribution", out _));
        Assert.True(schemas.GetProperty("RagContextGraphContext").GetProperty("properties").TryGetProperty("seedDiagnostics", out _));
        Assert.True(schemas.GetProperty("RagContextGraphContext").GetProperty("properties").TryGetProperty("limitsHit", out _));
        Assert.True(schemas.GetProperty("RagContextChunk").GetProperty("properties").TryGetProperty("citationIds", out _));
        Assert.True(schemas.TryGetProperty("QueryEnvelope", out _));
        var filterProperties = schemas.GetProperty("FilterNode").GetProperty("properties");
        Assert.True(filterProperties.TryGetProperty("combine", out _));
        Assert.True(filterProperties.TryGetProperty("children", out _));
        Assert.False(filterProperties.TryGetProperty("all", out _));
        Assert.False(filterProperties.TryGetProperty("any", out _));
        Assert.True(schemas.TryGetProperty("EmbeddingRequest", out _));
        Assert.True(schemas.TryGetProperty("EmbeddingResponse", out _));
        Assert.True(schemas.TryGetProperty("EmbeddingJob", out _));
        Assert.True(schemas.TryGetProperty("EmbeddingProviderGuidance", out _));
        Assert.True(schemas.GetProperty("EmbeddingProviderDescriptor").GetProperty("properties").TryGetProperty("defaultQueryPrefix", out _));
        Assert.True(schemas.GetProperty("EmbeddingProviderDescriptor").GetProperty("properties").TryGetProperty("defaultPassagePrefix", out _));
        Assert.True(schemas.GetProperty("EmbeddingRequest").GetProperty("properties").TryGetProperty("purpose", out _));
        Assert.True(schemas.GetProperty("EmbeddingRequest").GetProperty("properties").TryGetProperty("queryPrefix", out _));
        Assert.True(schemas.GetProperty("EmbeddingResponse").GetProperty("properties").TryGetProperty("purpose", out _));
        Assert.Contains(
            schemas.GetProperty("EmbeddingJob").GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
            item => item == "running");
        Assert.True(schemas.GetProperty("EmbeddingResult").GetProperty("properties").TryGetProperty("preparedTextLength", out _));
        Assert.True(schemas.GetProperty("EmbeddingResult").GetProperty("properties").TryGetProperty("prefixApplied", out _));
        Assert.True(schemas.TryGetProperty("ServerSecurityStatus", out _));
        Assert.True(schemas.TryGetProperty("ServerReadinessReport", out _));
        Assert.True(schemas.TryGetProperty("ServerReadinessCheck", out _));
        Assert.True(schemas.TryGetProperty("ServerProviderReadinessSummary", out _));
        Assert.True(schemas.TryGetProperty("ProviderProfile", out _));
        Assert.True(schemas.TryGetProperty("ProviderQualification", out _));
        Assert.True(schemas.TryGetProperty("ProviderQualificationRequest", out _));
        Assert.True(schemas.TryGetProperty("ProviderDoctorResult", out _));
        Assert.True(schemas.TryGetProperty("ProviderDoctorCheck", out _));
        Assert.True(schemas.TryGetProperty("ProviderReadinessEnvelope", out _));
        Assert.True(schemas.TryGetProperty("ProviderDisabledInfo", out _));
        Assert.True(schemas.TryGetProperty("ProviderCapabilityReadiness", out _));
        Assert.True(schemas.TryGetProperty("ProviderCapabilityMatrix", out _));
        Assert.True(schemas.TryGetProperty("ProviderCapabilityMatrixItem", out _));
        Assert.True(schemas.TryGetProperty("ProviderCapabilitySupport", out _));
        Assert.True(schemas.GetProperty("ProviderReadinessEnvelope").GetProperty("properties").TryGetProperty("disabledProviders", out _));
        Assert.True(schemas.GetProperty("ProviderCapabilityReadiness").GetProperty("properties").TryGetProperty("registrationStatus", out _));
        Assert.True(schemas.GetProperty("ProviderCapabilityReadiness").GetProperty("properties").TryGetProperty("registrationHint", out _));
        Assert.True(schemas.GetProperty("ProviderCapabilityMatrixItem").GetProperty("properties").TryGetProperty("supportsQuota", out _));
        Assert.True(schemas.GetProperty("ProviderCapabilityMatrixItem").GetProperty("properties").TryGetProperty("supportsAsyncJobs", out _));
        Assert.True(schemas.GetProperty("ProviderCapabilityMatrixItem").GetProperty("properties").TryGetProperty("supportsArtifacts", out _));
        Assert.True(schemas.TryGetProperty("ProviderModelListResult", out _));
        Assert.True(schemas.TryGetProperty("ProviderModelDescriptor", out _));
        Assert.True(schemas.TryGetProperty("ProviderQuotaResult", out _));
        Assert.True(schemas.TryGetProperty("ProviderQuotaBucket", out _));
        Assert.True(schemas.TryGetProperty("ProviderQuotaWindow", out _));
        Assert.True(schemas.TryGetProperty("ProviderModePolicy", out _));
        Assert.True(schemas.TryGetProperty("ProviderRunRequest", out _));
        var providerRunRequired = schemas.GetProperty("ProviderRunRequest").GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Contains("capability", providerRunRequired);
        Assert.Contains("payload", providerRunRequired);
        Assert.DoesNotContain("operation", providerRunRequired);
        Assert.DoesNotContain("mode", providerRunRequired);
        Assert.True(schemas.GetProperty("ProviderRunRequest").GetProperty("properties").TryGetProperty("modelId", out _));
        Assert.True(schemas.TryGetProperty("ProviderRunResult", out _));
        var providerRunResultSchema = schemas.GetProperty("ProviderRunResult");
        var providerRunResultProperties = providerRunResultSchema.GetProperty("properties");
        Assert.True(providerRunResultProperties.TryGetProperty("error", out _));
        Assert.True(providerRunResultProperties.TryGetProperty("rejection", out _));
        Assert.True(providerRunResultProperties.TryGetProperty("trace", out _));
        Assert.True(providerRunResultProperties.TryGetProperty("metering", out _));
        Assert.DoesNotContain(
            providerRunResultProperties.GetProperty("status").GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
            item => item is "Queued" or "Running");
        Assert.True(providerRunResultProperties.GetProperty("output").TryGetProperty("x-vyral-capabilityOutputs", out _));
        Assert.False(providerRunResultProperties.TryGetProperty("textOutput", out _));
        Assert.True(schemas.TryGetProperty("ProviderRunRejectionDiagnostic", out _));
        Assert.DoesNotContain(
            providerRunResultSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()),
            item => item == "trace");
        Assert.True(schemas.TryGetProperty("ProviderRunJob", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringContext", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringSubject", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringPeriod", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringMeasurement", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringEvidenceReference", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringIntegrity", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringReceipt", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringReviewFinding", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringScope", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringAggregatePeriod", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringAggregateMeasurement", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringAggregate", out _));
        Assert.True(schemas.TryGetProperty("AiMeteringReview", out _));
        Assert.Contains(
            "outcome",
            schemas.GetProperty("AiMeteringReceipt").GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            "aggregate",
            schemas.GetProperty("AiMeteringReview").GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            schemas.GetProperty("ProviderRunJob").GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
            item => item == "Queued");
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationJob", out _));
        var retrievalEvaluationJobProperties = schemas.GetProperty("RetrievalEvaluationJob").GetProperty("properties");
        Assert.True(retrievalEvaluationJobProperties.TryGetProperty("evaluationResult", out _));
        Assert.True(retrievalEvaluationJobProperties.TryGetProperty("casesAttempted", out _));
        Assert.True(retrievalEvaluationJobProperties.TryGetProperty("currentCaseName", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationProgress", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationComparisonProgress", out _));
        Assert.Contains(
            schemas.GetProperty("RetrievalEvaluationJob").GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
            item => item == "running");
        Assert.True(schemas.TryGetProperty("AiChatPayload", out _));
        Assert.True(schemas.TryGetProperty("AiChatResult", out _));
        Assert.True(schemas.TryGetProperty("AiExtractPayload", out _));
        Assert.True(schemas.TryGetProperty("AiExtractResult", out _));
        Assert.True(schemas.TryGetProperty("AiRerankPayload", out _));
        Assert.True(schemas.TryGetProperty("AiRerankResult", out _));
        Assert.True(schemas.TryGetProperty("AiReviewPayload", out _));
        Assert.True(schemas.TryGetProperty("AiReviewResult", out _));
        Assert.True(schemas.TryGetProperty("AiScaffoldPayload", out _));
        Assert.True(schemas.TryGetProperty("AiScaffoldResult", out _));
        Assert.True(schemas.TryGetProperty("AiReference", out _));
        Assert.True(schemas.TryGetProperty("AiToolPlanPayload", out _));
        Assert.True(schemas.TryGetProperty("AiToolPlanResult", out _));
        Assert.True(schemas.GetProperty("AiMessage").GetProperty("properties").TryGetProperty("contentBlocks", out _));
        Assert.True(schemas.GetProperty("ProviderRunRequest").GetProperty("properties").GetProperty("payload").TryGetProperty("x-vyral-capabilityPayloads", out _));
        Assert.True(schemas.GetProperty("ProviderRunRequest").GetProperty("properties").GetProperty("payload").GetProperty("x-vyral-capabilityPayloads").TryGetProperty("ai.toolPlan", out _));
        Assert.True(providerRunResultProperties.GetProperty("output").GetProperty("x-vyral-capabilityOutputs").TryGetProperty("ai.toolPlan", out _));
        Assert.True(schemas.TryGetProperty("RetrievalRequest", out _));
        Assert.True(schemas.TryGetProperty("RetrievalProfileDescriptor", out _));
        Assert.True(schemas.TryGetProperty("RetrievalProfileDefaults", out _));
        var retrievalRequestProperties = schemas.GetProperty("RetrievalRequest").GetProperty("properties");
        Assert.True(retrievalRequestProperties.TryGetProperty("profile", out _));
        Assert.True(retrievalRequestProperties.TryGetProperty("embedding", out _));
        Assert.True(retrievalRequestProperties.TryGetProperty("vectorFields", out _));
        Assert.False(retrievalRequestProperties.GetProperty("searchMode").TryGetProperty("default", out _));
        Assert.Contains("infers vector", retrievalRequestProperties.GetProperty("searchMode").GetProperty("description").GetString());
        Assert.False(retrievalRequestProperties.TryGetProperty("embeddingPurpose", out _));
        Assert.False(retrievalRequestProperties.TryGetProperty("queryPrefix", out _));
        Assert.False(retrievalRequestProperties.TryGetProperty("embeddingField", out _));
        Assert.True(schemas.TryGetProperty("RetrievalVectorFieldQuery", out _));
        var retrievalVectorFieldProperties = schemas.GetProperty("RetrievalVectorFieldQuery").GetProperty("properties");
        Assert.True(retrievalVectorFieldProperties.TryGetProperty("embedding", out _));
        Assert.False(retrievalVectorFieldProperties.TryGetProperty("embeddingPurpose", out _));
        Assert.False(retrievalVectorFieldProperties.TryGetProperty("queryPrefix", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationRequest", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationComparisonRequest", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationVariant", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationComparisonResult", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationVariantResult", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationMetrics", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationMetricDeltas", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationHardNegativeMatch", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationResult", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationCaseResult", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationHardNegativeResult", out _));
        Assert.True(schemas.TryGetProperty("RetrievalEvaluationTopResult", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationExpectedMatch").GetProperty("properties").TryGetProperty("sourceIds", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationExpectedMatch").GetProperty("properties").TryGetProperty("sources", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationHardNegativeMatch").GetProperty("properties").TryGetProperty("aliases", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationCase").GetProperty("properties").TryGetProperty("hardNegatives", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationResult").GetProperty("properties").TryGetProperty("hardNegativeHitRate", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationResult").GetProperty("properties").TryGetProperty("rerankFallbackCaseCount", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationCaseResult").GetProperty("properties").TryGetProperty("durationMs", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationCaseResult").GetProperty("properties").TryGetProperty("hardNegatives", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationCaseResult").GetProperty("properties").TryGetProperty("rerankFallbackApplied", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationTopResult").GetProperty("properties").TryGetProperty("matchedHardNegative", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationTopResult").GetProperty("properties").TryGetProperty("rerankProviderStatus", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationTopResult").GetProperty("properties").TryGetProperty("vectorIndexUsed", out _));
        Assert.True(schemas.GetProperty("RetrievalEvaluationTopResult").GetProperty("properties").TryGetProperty("vectorIndexFields", out _));
        Assert.True(schemas.TryGetProperty("LexicalSearchOptions", out _));
        Assert.True(schemas.TryGetProperty("HybridSearchOptions", out _));
        Assert.True(schemas.TryGetProperty("RerankOptions", out _));
        Assert.True(schemas.TryGetProperty("RetrievalDiagnostics", out _));
        Assert.True(schemas.TryGetProperty("RetrievalResultIdentity", out _));
        Assert.True(schemas.TryGetProperty("RetrievalScoreNormalization", out _));
        Assert.True(schemas.TryGetProperty("RetrievalTraceReference", out _));
        var diagnosticsProperties = schemas.GetProperty("RetrievalDiagnostics").GetProperty("properties");
        Assert.True(diagnosticsProperties.TryGetProperty("resultIdentity", out _));
        Assert.True(diagnosticsProperties.TryGetProperty("scoreNormalization", out _));
        Assert.True(diagnosticsProperties.TryGetProperty("candidateCounts", out _));
        Assert.True(diagnosticsProperties.TryGetProperty("reasonCodes", out _));
        Assert.True(diagnosticsProperties.TryGetProperty("traceReferences", out _));
        Assert.True(schemas.GetProperty("LexicalSearchOptions").GetProperty("properties").TryGetProperty("fieldBoosts", out _));
        Assert.True(schemas.GetProperty("LexicalSearchOptions").GetProperty("properties").TryGetProperty("bm25K1", out _));
        Assert.True(schemas.GetProperty("LexicalSearchOptions").GetProperty("properties").TryGetProperty("prefixMatching", out _));
        Assert.True(schemas.GetProperty("LexicalSearchOptions").GetProperty("properties").TryGetProperty("prefixMinChars", out _));
        Assert.True(schemas.GetProperty("LexicalSearchOptions").GetProperty("properties").TryGetProperty("requiredPhraseGroups", out _));
        Assert.True(schemas.GetProperty("HybridSearchOptions").GetProperty("properties").TryGetProperty("fusion", out _));
        Assert.True(schemas.GetProperty("HybridSearchOptions").GetProperty("properties").TryGetProperty("rrfK", out _));
    }

    [Fact]
    public async Task Server_AggregateRecordWriteReturnsDurableIdempotentAdmission()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-aggregate-admission-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-aggregate-admission-objects-{Guid.NewGuid():N}");
        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();
        await EnsureSuccessAsync(await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "aggregate-records"
        }));

        var payload = new RecordBatchUpsertRequest
        {
            Records =
            {
                new VyralRecord
                {
                    Id = "record-1",
                    PartitionKey = "tenant-a",
                    Type = "test.record"
                }
            }
        };

        static HttpRequestMessage CreateRequest(RecordBatchUpsertRequest value)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/collections/aggregate-records/records/batch")
            {
                Content = JsonContent.Create(value)
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", "aggregate-records-1");
            return request;
        }

        using var firstRequest = CreateRequest(payload);
        using var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        var admitted = await firstResponse.Content.ReadFromJsonAsync<RecordImportJob>();
        Assert.NotNull(admitted);
        Assert.Equal($"/record-import/jobs/{admitted!.Id}", firstResponse.Headers.Location?.ToString());
        Assert.Equal("upsertRecords", admitted.Admission.OperationId);
        Assert.False(admitted.Admission.Replayed);

        using var replayRequest = CreateRequest(payload);
        using var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<RecordImportJob>();
        Assert.NotNull(replay);
        Assert.Equal(admitted.Id, replay!.Id);
        Assert.Equal(admitted.Admission.AdmissionId, replay.Admission.AdmissionId);
        Assert.True(replay.Admission.Replayed);

        RecordImportJob? completed = null;
        for (var i = 0; i < 100; i++)
        {
            completed = await client.GetFromJsonAsync<RecordImportJob>($"/record-import/jobs/{admitted.Id}");
            if (completed?.Status == RecordImportJobStatuses.Succeeded)
            {
                break;
            }
            await Task.Delay(20);
        }
        Assert.NotNull(completed);
        Assert.Equal(RecordImportJobStatuses.Succeeded, completed!.Status);
        Assert.Equal("upsertRecords", completed.Admission.OperationId);

        var executionRun = await client.GetFromJsonAsync<ExecutionRun>($"/execution/runs/{admitted.Id}");
        Assert.NotNull(executionRun);
        Assert.Equal("upsertRecords", executionRun!.Admission.OperationId);
    }

    [Fact]
    public async Task Server_CollectionLifecycleReturnsDurableIdempotentAdmissions()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-collection-admission-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-collection-admission-objects-{Guid.NewGuid():N}");
        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();
        var policy = new RecordCollectionPolicy { Name = "durable-collection" };

        static HttpRequestMessage CreateRequest(RecordCollectionPolicy value)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/collections")
            {
                Content = JsonContent.Create(value)
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", "create-durable-collection-1");
            return request;
        }

        using var createRequest = CreateRequest(policy);
        using var createResponse = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        var createRun = (await createResponse.Content.ReadFromJsonAsync<ExecutionRun>())!;
        Assert.Equal("createCollection", createRun.Admission.OperationId);
        Assert.Equal($"/execution/runs/{createRun.Id}", createResponse.Headers.Location?.ToString());

        using var replayRequest = CreateRequest(policy);
        using var replayResponse = await client.SendAsync(replayRequest);
        var replay = (await replayResponse.Content.ReadFromJsonAsync<ExecutionRun>())!;
        Assert.Equal(createRun.Id, replay.Id);
        Assert.True(replay.Admission.Replayed);

        await WaitForExecutionRunAsync(client, createRun.Id, ExecutionRunStatuses.Succeeded);
        Assert.NotNull(await client.GetFromJsonAsync<RecordCollectionPolicy>("/collections/durable-collection"));

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/collections/durable-collection");
        deleteRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "delete-durable-collection-1");
        using var deleteResponse = await client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.Accepted, deleteResponse.StatusCode);
        var deleteRun = (await deleteResponse.Content.ReadFromJsonAsync<ExecutionRun>())!;
        Assert.Equal("deleteCollection", deleteRun.Admission.OperationId);
        await WaitForExecutionRunAsync(client, deleteRun.Id, ExecutionRunStatuses.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/collections/durable-collection")).StatusCode);
    }

    [Fact]
    public async Task Server_IngestsGenericRecordArtifactInOneMultipartRequest()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-generic-ingest-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-generic-ingest-objects-{Guid.NewGuid():N}");
        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "consumer-results",
            PartitionKeyPath = "/partitionKey",
            IndexedMetadata = new List<string> { "/metadata/receivedAt" }
        });
        await EnsureSuccessAsync(create);

        var manifest = new ArtifactRecordIngestManifest
        {
            Collection = "consumer-results",
            Record = new VyralRecord
            {
                Id = "result-1",
                PartitionKey = "consumer-a",
                Type = "consumer.result",
                Metadata = new JsonObject { ["receivedAt"] = "2026-07-27T12:00:00Z" },
                Content = new JsonObject { ["summary"] = "generic contract" }
            },
            Artifact = new ArtifactRecordDescriptor
            {
                Container = "consumer-artifacts",
                Key = "results/2026/result-1.json",
                ContentType = "application/json",
                Metadata = new Dictionary<string, string> { ["schema"] = "consumer.result" }
            }
        };
        static MultipartFormDataContent CreateForm(ArtifactRecordIngestManifest value)
        {
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"), "manifest");
            content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("{\"raw\":true}")), "artifact", "result.json");
            return content;
        }

        using var form = CreateForm(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest/record-artifact")
        {
            Content = form
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "artifact-record-ingest-1");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<ExecutionRun>();
        Assert.NotNull(accepted);
        Assert.Equal($"/execution/runs/{accepted!.Id}", response.Headers.Location?.ToString());
        Assert.Equal("ingestRecordArtifact", accepted.Admission.OperationId);
        Assert.Equal("accepted", accepted.Admission.Status);
        Assert.False(accepted.Admission.Replayed);
        Assert.Null(accepted.IdempotencyKey);

        ExecutionRun? completed = null;
        for (var i = 0; i < 100; i++)
        {
            completed = await client.GetFromJsonAsync<ExecutionRun>($"/execution/runs/{accepted.Id}");
            if (completed is not null && ExecutionRunStatuses.IsTerminal(completed.Status))
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(completed);
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed!.Status);
        Assert.Equal("ingestRecordArtifact", completed.Admission.OperationId);
        var receipt = completed.Result?.Deserialize<ArtifactRecordIngestReceipt>(ExecutionJson.Options);
        Assert.NotNull(receipt);
        Assert.True(receipt!.Accepted);
        Assert.Equal("consumer-results", receipt.Collection);
        Assert.Equal("result-1", receipt.RecordId);
        Assert.Equal("sha256:f528a13eb3833b6af304159a3db6785702540fce4c13674eec4e2df22fdffecf", receipt.Artifact.ContentHash);

        using var replayForm = CreateForm(manifest);
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/ingest/record-artifact")
        {
            Content = replayForm
        };
        replayRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "artifact-record-ingest-1");
        var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<ExecutionRun>();
        Assert.NotNull(replay);
        Assert.Equal(accepted.Id, replay!.Id);
        Assert.Equal(accepted.Admission.AdmissionId, replay.Admission.AdmissionId);
        Assert.True(replay.Admission.Replayed);

        var record = await client.GetFromJsonAsync<VyralRecord>("/collections/consumer-results/records/consumer-a/result-1");
        Assert.NotNull(record);
        Assert.Equal("consumer.result", record!.Type);
        var artifact = await client.GetStringAsync("/objects/consumer-artifacts/results/2026/result-1.json");
        Assert.Equal("{\"raw\":true}", artifact);
    }

    [Fact]
    public async Task Server_ExposesHealthStatusForClientStartupChecks()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<ServerHealthStatus>("/health");

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal("vyral-server", health.Service);
        Assert.Equal("0.3.0", health.Version);
        Assert.Equal("/openapi/vyral.json", health.ContractPath);
        Assert.Equal("/contracts/schemas/vyral-public.schema.json", health.SchemaContractPath);
        Assert.Equal(nameof(SqliteRecordCollectionStore), health.Storage.RecordStore);
        Assert.Equal(nameof(FileObjectStore), health.Storage.ObjectStore);
        Assert.Equal(nameof(SqliteTraceStore), health.Storage.TraceStore);
        Assert.Equal(LocalTokenHashEmbeddingProviderFactory.Provider, health.Embedding.Provider);
        Assert.Equal(LocalTokenHashEmbeddingProviderFactory.DefaultModelId, health.Embedding.ModelId);
        Assert.Equal(384, health.Embedding.Dimensions);
        Assert.False(health.Security.ApiKeyRequired);
        Assert.Equal("X-Vyral-Api-Key", health.Security.ApiKeyHeader);
        Assert.Equal(1, ((JsonElement)health.Security.ProviderRunLimits["maxConcurrentRuns"]!).GetInt32());
        Assert.Equal(100, ((JsonElement)health.Security.ProviderRunLimits["maxActiveJobs"]!).GetInt32());
        Assert.Equal("local", ((JsonElement)health.Security.ProviderRunLimits["jobPersistence"]!).GetString());

        var readiness = await client.GetFromJsonAsync<ServerReadinessReport>("/readiness");

        Assert.NotNull(readiness);
        Assert.True(readiness.Ready);
        Assert.Equal(ProviderDoctorStatuses.Warning, readiness.Status);
        Assert.Equal(nameof(SqliteRecordCollectionStore), readiness.Health.Storage.RecordStore);
        var recordStorageCheck = Assert.Single(readiness.Checks, check => check.Id == "storage.records");
        Assert.Equal(ProviderDoctorStatuses.Ok, recordStorageCheck.Status);
        var sqliteDiagnostics = ((JsonElement)recordStorageCheck.Details["sqlite"]!);
        Assert.True(sqliteDiagnostics.GetProperty("healthy").GetBoolean());
        Assert.Equal("ok", sqliteDiagnostics.GetProperty("quickCheck").GetString());
        Assert.Equal(0, sqliteDiagnostics.GetProperty("foreignKeyViolationCount").GetInt32());
        Assert.True(sqliteDiagnostics.GetProperty("pageCount").GetInt64() > 0);
        var objectStorageCheck = Assert.Single(readiness.Checks, check => check.Id == "storage.objects");
        Assert.Equal(ProviderDoctorStatuses.Ok, objectStorageCheck.Status);
        Assert.Equal("write_read_delete", ((JsonElement)objectStorageCheck.Details["probe"]!).GetString());
        Assert.Equal("vyral-readiness", ((JsonElement)objectStorageCheck.Details["container"]!).GetString());
        Assert.StartsWith("sha256:", ((JsonElement)objectStorageCheck.Details["contentHash"]!).GetString());
        var filesystemDiagnostics = ((JsonElement)objectStorageCheck.Details["filesystem"]!);
        Assert.True(filesystemDiagnostics.GetProperty("healthy").GetBoolean());
        Assert.True(filesystemDiagnostics.GetProperty("rootExists").GetBoolean());
        Assert.Equal(0, filesystemDiagnostics.GetProperty("missingMetadataCount").GetInt32());
        Assert.Equal(0, filesystemDiagnostics.GetProperty("orphanMetadataCount").GetInt32());
        Assert.Equal(0, filesystemDiagnostics.GetProperty("temporaryFileCount").GetInt32());
        Assert.Contains(readiness.Checks, check => check.Id == "storage.traces" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(readiness.Checks, check => check.Id == "security.api_key" && check.Status == ProviderDoctorStatuses.Warning);
        Assert.Contains(readiness.Checks, check => check.Id == "embedding.provider" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(readiness.Checks, check => check.Id == "providers.limits" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(readiness.Checks, check => check.Id == "providers.readiness" && check.Status == ProviderDoctorStatuses.Warning);
        Assert.Contains(readiness.Warnings, warning => warning.Contains("API key authentication is disabled", StringComparison.Ordinal));
        Assert.Contains(readiness.Warnings, warning => warning.Contains("Provider capabilities are callable but not qualified", StringComparison.Ordinal));
        Assert.Empty(readiness.Blockers);
        Assert.Equal(LocalTokenHashEmbeddingProviderFactory.Provider, ((JsonElement)readiness.Embedding["provider"]!).GetString());
        Assert.True(readiness.Providers.ProviderCount >= 1);
        Assert.True(readiness.Providers.CallableCapabilityCount >= 1);
        Assert.Equal(1, ((JsonElement)readiness.OperationalLimits["maxConcurrentRuns"]!).GetInt32());
        Assert.Equal("local", ((JsonElement)readiness.OperationalLimits["jobPersistence"]!).GetString());

        var providerMatrix = await client.GetFromJsonAsync<ProviderCapabilityMatrix>("/providers/capabilities");

        Assert.NotNull(providerMatrix);
        Assert.Contains(ProviderCapabilityIds.AiChat, providerMatrix.CapabilityIds);
        Assert.Contains(ProviderFailureClasses.RateLimit, providerMatrix.FailureClasses);
        Assert.Equal(1, ((JsonElement)providerMatrix.OperationalLimits["maxConcurrentRuns"]!).GetInt32());
        var localProvider = Assert.Single(providerMatrix.Items, item => item.Provider == DeterministicAiProviderTarget.ProviderId);
        Assert.True(localProvider.Registered);
        Assert.True(localProvider.Enabled);
        Assert.True(localProvider.Capabilities[ProviderCapabilityIds.AiExtract].Supported);
        Assert.True(localProvider.SupportsModelListing);
        Assert.True(localProvider.SupportsAsyncJobs);
        Assert.Contains(providerMatrix.DisabledProviders, item => item.ProviderId == "codex-cli");
        var disabledCodex = Assert.Single(providerMatrix.Items, item => item.Provider == "codex-cli");
        Assert.False(disabledCodex.Registered);
        Assert.False(disabledCodex.Enabled);
        Assert.False(disabledCodex.Capabilities[ProviderCapabilityIds.AiExtract].Supported);
        Assert.Contains("provider_disabled_by_configuration", disabledCodex.Capabilities[ProviderCapabilityIds.AiExtract].UnsupportedFeatures);

        var providers = await client.GetFromJsonAsync<List<EmbeddingProviderDescriptor>>("/embedding-providers");

        Assert.NotNull(providers);
        Assert.Contains(providers, provider =>
            provider.Provider == LocalTokenHashEmbeddingProviderFactory.Provider &&
            provider.Local &&
            provider.CpuOnly &&
            !provider.RequiresNetwork &&
            provider.SemanticQuality == "lexical");
        Assert.Contains(providers, provider =>
            provider.Provider == DeterministicHashEmbeddingProviderFactory.Provider &&
            provider.Local &&
            provider.CpuOnly &&
            !provider.RequiresNetwork &&
            provider.SemanticQuality == "mechanical");
        Assert.Contains(providers, provider => provider.Provider == "onnx-minilm-cpu" && provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == "onnx-minilm-gpu" && !provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == "onnx-bge-small-cpu" && provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == "onnx-bge-small-gpu" && !provider.CpuOnly);
        Assert.Contains(providers, provider => provider.Provider == "onnx-bge-base-cpu" && provider.CpuOnly && provider.DefaultQueryPrefix is not null);
        Assert.Contains(providers, provider => provider.Provider == "onnx-e5-small-cpu" && provider.CpuOnly && provider.DefaultQueryPrefix == OnnxEmbeddingProviders.E5QueryPrefix);
        Assert.Contains(providers, provider => provider.Provider == "onnx-e5-base-cpu" && provider.CpuOnly && provider.DefaultPassagePrefix == OnnxEmbeddingProviders.E5PassagePrefix);

        var guidance = await client.GetFromJsonAsync<List<EmbeddingProviderGuidance>>("/embedding-providers/guidance");

        Assert.NotNull(guidance);
        var tokenHashGuidance = Assert.Single(guidance, item => item.Provider == LocalTokenHashEmbeddingProviderFactory.Provider);
        Assert.False(tokenHashGuidance.RealisticForSemanticRetrieval);
        Assert.Contains(RetrievalProfileIds.Evidence, tokenHashGuidance.SuggestedRetrievalProfiles);
        var bgeGuidance = Assert.Single(guidance, item => item.Provider == "onnx-bge-small-cpu");
        Assert.True(bgeGuidance.RealisticForSemanticRetrieval);
        Assert.True(bgeGuidance.RequiresModelFiles);
        Assert.Equal("cpu-only", bgeGuidance.HardwareProfile);
        Assert.Contains(RetrievalProfileIds.Discovery, bgeGuidance.SuggestedRetrievalProfiles);
        Assert.Contains("lexical-baseline", bgeGuidance.SuggestedEvaluationVariants);

        var doctor = await client.GetFromJsonAsync<ProviderDoctorResult>("/embedding-providers/doctor");

        Assert.NotNull(doctor);
        Assert.Equal(LocalTokenHashEmbeddingProviderFactory.Provider, doctor.Provider);
        Assert.Equal(ProviderDoctorStatuses.Ok, doctor.Status);
        Assert.Contains(doctor.Checks, check => check.Id == "embedding.provider" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(doctor.Checks, check => check.Id == "embedding.dimensions" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(doctor.Checks, check => check.Id == "embedding.model_files" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(doctor.Checks, check => check.Id == "embedding.runtime" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(doctor.Checks, check => check.Id == "embedding.quality" && check.Status == ProviderDoctorStatuses.Ok);
        var qualityCheck = Assert.Single(doctor.Checks, check => check.Id == "embedding.quality");
        Assert.False(((JsonElement)qualityCheck.Details["realisticForSemanticRetrieval"]!).GetBoolean());
        Assert.Equal("cpu-only", ((JsonElement)qualityCheck.Details["hardwareProfile"]!).GetString());
    }

    [Fact]
    public async Task Server_EmbedsTextsForClientIngestion()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();
        var provider = new LocalTokenHashEmbeddingProvider(12);

        var response = await PostJsonAsync<EmbeddingResponse>(client, "/embeddings", new EmbeddingRequest
        {
            Texts = new List<string> { "retention policy", "travel reimbursement" }
        });

        Assert.Equal(LocalTokenHashEmbeddingProviderFactory.Provider, response.Provider);
        Assert.Equal(LocalTokenHashEmbeddingProviderFactory.DefaultModelId, response.ModelId);
        Assert.Equal(12, response.Dimensions);
        Assert.Equal(2, response.Items.Count);
        Assert.Equal(0, response.Items[0].Index);
        Assert.Equal("retention policy".Length, response.Items[0].TextLength);
        Assert.Equal("retention policy".Length, response.Items[0].PreparedTextLength);
        Assert.False(response.Items[0].PrefixApplied);
        Assert.Equal(12, response.Items[0].Values.Length);
        Assert.Equal(await provider.GenerateEmbeddingAsync("retention policy"), response.Items[0].Values);
    }

    [Fact]
    public async Task Server_EmbeddingJobsExposeProgressAndResults()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-embedding-job-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();
        var provider = new LocalTokenHashEmbeddingProvider(12);

        var embeddingRequest = new EmbeddingRequest
        {
            Texts = new List<string> { "retention policy", "travel reimbursement" },
            Purpose = "passage"
        };
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/embeddings/jobs")
        {
            Content = JsonContent.Create(embeddingRequest)
        };
        startRequest.Headers.Add("Idempotency-Key", "embedding-job-receipt-1");
        using var response = await client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<EmbeddingJob>())!;
        Assert.False(string.IsNullOrWhiteSpace(accepted.Id));
        Assert.Equal(2, accepted.Requested);
        Assert.Equal("passage", accepted.Purpose);
        Assert.Equal($"/embeddings/jobs/{accepted.Id}", response.Headers.Location?.ToString());
        Assert.Equal("vyral.admission.v1", accepted.Admission.Version);
        Assert.Equal("startEmbeddingJob", accepted.Admission.OperationId);
        Assert.Equal("accepted", accepted.Admission.Status);
        Assert.Equal(accepted.Id, accepted.Admission.ResourceId);
        Assert.False(accepted.Admission.Replayed);
        Assert.NotEqual("embedding-job-receipt-1", accepted.Admission.IdempotencyKeyHash);

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/embeddings/jobs")
        {
            Content = JsonContent.Create(embeddingRequest)
        };
        replayRequest.Headers.Add("Idempotency-Key", "embedding-job-receipt-1");
        using var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);
        var replayed = (await replayResponse.Content.ReadFromJsonAsync<EmbeddingJob>())!;
        Assert.Equal(accepted.Id, replayed.Id);
        Assert.Equal(accepted.Admission.AdmissionId, replayed.Admission.AdmissionId);
        Assert.True(replayed.Admission.Replayed);

        EmbeddingJob? completed = null;
        for (var i = 0; i < 50; i++)
        {
            completed = await client.GetFromJsonAsync<EmbeddingJob>($"/embeddings/jobs/{accepted.Id}");
            if (completed is not null && completed.Status == EmbeddingJobStatuses.Succeeded)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(completed);
        Assert.Equal(EmbeddingJobStatuses.Succeeded, completed!.Status);
        Assert.Equal(1, completed.Progress, precision: 3);
        Assert.Equal(2, completed.Succeeded);
        Assert.NotNull(completed.Result);
        Assert.Equal(2, completed.Result!.Items.Count);
        Assert.Equal(await provider.GenerateEmbeddingAsync("retention policy"), completed.Result.Items[0].Values);

        var jobs = await client.GetFromJsonAsync<List<EmbeddingJob>>("/embeddings/jobs?limit=5");
        Assert.NotNull(jobs);
        Assert.Contains(jobs, item => item.Id == accepted.Id);
        Assert.All(jobs, item => Assert.Null(item.Result));

        var jobsWithResults = await client.GetFromJsonAsync<List<EmbeddingJob>>("/embeddings/jobs?limit=5&includeResult=true");
        Assert.NotNull(jobsWithResults);
        Assert.Contains(jobsWithResults, item => item.Id == accepted.Id && item.Result is not null);

        await AssertJobBackedByExecutionRuntimeAsync(
            dbPath,
            accepted.Id,
            ExecutionRuntimeEmbeddingJobAdapter.HandlerId,
            ExecutionRuntimeEmbeddingJobAdapter.PluginId);

        var runtime = await client.GetFromJsonAsync<ExecutionRuntimeSurface>("/execution/runtime");
        Assert.NotNull(runtime);
        Assert.False(string.IsNullOrWhiteSpace(runtime!.Status.Adapter.RuntimeKind));
        Assert.Contains(ExecutionCapabilityIds.LocalDispatch, runtime.Status.Adapter.Capabilities);
        Assert.Contains(ExecutionCapabilityIds.DurableRuns, runtime.Status.Adapter.Capabilities);
        Assert.Contains(runtime.Plugins, plugin => plugin.PluginId == ExecutionRuntimeEmbeddingJobAdapter.PluginId);
        Assert.Contains(runtime.Handlers, handler => handler.HandlerId == ExecutionRuntimeEmbeddingJobAdapter.HandlerId);

        var runtimeRuns = await client.GetFromJsonAsync<List<ExecutionRun>>($"/execution/runs?handlerId={ExecutionRuntimeEmbeddingJobAdapter.HandlerId}&limit=5");
        Assert.NotNull(runtimeRuns);
        Assert.Contains(runtimeRuns, run => run.Id == accepted.Id && run.Result is null);

        var runtimeRun = await client.GetFromJsonAsync<ExecutionRun>($"/execution/runs/{accepted.Id}");
        Assert.NotNull(runtimeRun);
        Assert.Equal(ExecutionRunStatuses.Succeeded, runtimeRun!.Status);
        Assert.NotNull(runtimeRun.Result);

        var runtimeHistory = await client.GetFromJsonAsync<List<ExecutionTraceEvent>>($"/execution/runs/{accepted.Id}/history");
        Assert.NotNull(runtimeHistory);
        Assert.Contains(runtimeHistory, item => item.Type == ExecutionEventTypes.RunStarted);
        Assert.Contains(runtimeHistory, item => item.Type == ExecutionEventTypes.RunCompleted);

        var runtimeArtifacts = await client.GetFromJsonAsync<List<ExecutionArtifact>>($"/execution/runs/{accepted.Id}/artifacts");
        Assert.NotNull(runtimeArtifacts);
        Assert.Empty(runtimeArtifacts);
    }

    [Fact]
    public async Task Server_ExecutionRuntimeStartsRegisteredHandlerOverHttp()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-execution-start-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/execution/runs", new ExecutionRunRequest
        {
            HandlerId = ExecutionRuntimeEmbeddingJobAdapter.HandlerId,
            PluginId = ExecutionRuntimeEmbeddingJobAdapter.PluginId,
            Payload = JsonSerializer.SerializeToNode(new EmbeddingRequest
            {
                Texts = new List<string> { "project import", "advisory copy" },
                Purpose = EmbeddingPurposes.Passage
            }, ExecutionJson.Options),
            CorrelationId = "consumer:start-smoke",
            IdempotencyKey = $"consumer:start-smoke:{Guid.NewGuid():N}",
            Tags =
            {
                ["projectId"] = "project-a",
                ["pipelineType"] = "import"
            },
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 1 }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<ExecutionRun>())!;
        Assert.False(string.IsNullOrWhiteSpace(accepted.Id));
        Assert.Equal($"/execution/runs/{accepted.Id}", response.Headers.Location?.ToString());
        Assert.Equal(ExecutionRuntimeEmbeddingJobAdapter.HandlerId, accepted.HandlerId);
        Assert.Equal(ExecutionRuntimeEmbeddingJobAdapter.PluginId, accepted.PluginId);
        Assert.Equal("consumer:start-smoke", accepted.CorrelationId);
        Assert.Equal("project-a", accepted.Tags["projectId"]);
        Assert.Null(accepted.IdempotencyKey);
        Assert.Equal("startExecutionRun", accepted.Admission.OperationId);
        Assert.Equal("accepted", accepted.Admission.Status);
        Assert.Equal(accepted.Id, accepted.Admission.ResourceId);
        Assert.False(string.IsNullOrWhiteSpace(accepted.Admission.IdempotencyKeyHash));

        ExecutionRun? completed = null;
        for (var i = 0; i < 50; i++)
        {
            completed = await client.GetFromJsonAsync<ExecutionRun>($"/execution/runs/{accepted.Id}");
            if (completed is not null && completed.Status == ExecutionRunStatuses.Succeeded)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(completed);
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed!.Status);
        Assert.Equal(accepted.Admission.AdmissionId, completed.Admission.AdmissionId);
        Assert.Equal(2, completed.Succeeded);
        Assert.NotNull(completed.Result);

        var listed = await client.GetFromJsonAsync<List<ExecutionRun>>(
            $"/execution/runs?handlerId={ExecutionRuntimeEmbeddingJobAdapter.HandlerId}&includeResult=true&limit=5");
        Assert.NotNull(listed);
        Assert.Contains(listed, run => run.Id == accepted.Id && run.Result is not null);

        var filtered = await client.GetFromJsonAsync<List<ExecutionRun>>(
            $"/execution/runs?pluginId={ExecutionRuntimeEmbeddingJobAdapter.PluginId}&correlationId=consumer:start-smoke&tag.projectId=project-a&tag.pipelineType=import&includeResult=false&limit=5");
        Assert.NotNull(filtered);
        var filteredRun = Assert.Single(filtered, run => run.Id == accepted.Id);
        Assert.Null(filteredRun.Result);
    }

    [Fact]
    public async Task Server_ExecutionRuntimeDoesNotReportDurableRejectionAsAccepted()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-execution-rejected-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/execution/runs")
        {
            Content = JsonContent.Create(new ExecutionRunRequest
            {
                HandlerId = "missing.handler",
                Payload = new JsonObject()
            })
        };
        request.Headers.Add("Idempotency-Key", "missing-handler-receipt-1");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Admission rejected", problem.GetProperty("title").GetString());
        var admission = problem.GetProperty("admission").Deserialize<AdmissionReceipt>(ExecutionJson.Options);
        Assert.NotNull(admission);
        Assert.Equal("rejected", admission!.Status);
        Assert.Equal(ExecutionFailureClasses.HandlerMissing, admission.FailureClass);
        Assert.False(string.IsNullOrWhiteSpace(admission.ResourceId));
        Assert.NotNull(await client.GetFromJsonAsync<ExecutionRun>(admission.StatusUri));
    }

    [Fact]
    public async Task Server_EmbedsTextsWithPurposePrefix()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, embeddingDimensions: 12);
        var client = factory.CreateClient();
        var provider = new LocalTokenHashEmbeddingProvider(12);

        var response = await PostJsonAsync<EmbeddingResponse>(client, "/embeddings", new EmbeddingRequest
        {
            Texts = new List<string> { "injunction deadline" },
            Purpose = "query",
            QueryPrefix = "query: "
        });

        Assert.Equal("query", response.Purpose);
        Assert.Single(response.Items);
        Assert.Equal("injunction deadline".Length, response.Items[0].TextLength);
        Assert.Equal("query: injunction deadline".Length, response.Items[0].PreparedTextLength);
        Assert.True(response.Items[0].PrefixApplied);
        Assert.Equal("query: ".Length, response.Items[0].PrefixLength);
        Assert.Equal(await provider.GenerateEmbeddingAsync("query: injunction deadline"), response.Items[0].Values);
    }

    [Fact]
    public async Task Server_ExposesAndRunsDeterministicAiProviderByDefault()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<List<ProviderProfile>>("/providers");
        Assert.NotNull(providers);
        Assert.Contains(providers, provider => provider.Id == DeterministicAiProviderTarget.ProviderId && provider.Local && !provider.RequiresNetwork);
        Assert.Contains(providers, provider => provider.Id == LocalTokenOverlapRerankerProviderTarget.ProviderId && provider.Local && !provider.RequiresNetwork);
        Assert.Contains(providers, provider => provider.Id == OnnxCrossEncoderRerankerProviderTargets.CpuProviderId && provider.Local && !provider.RequiresNetwork);
        Assert.Contains(providers, provider => provider.Id == OnnxCrossEncoderRerankerProviderTargets.GpuProviderId && provider.Local && !provider.RequiresNetwork);
        Assert.DoesNotContain(providers, provider => provider.Id == "codex-cli");

        var descriptor = await client.GetFromJsonAsync<ProviderTargetDescriptor>($"/providers/{DeterministicAiProviderTarget.ProviderId}");
        Assert.NotNull(descriptor);
        Assert.Contains(descriptor.Capabilities, capability => capability.Id == ProviderCapabilityIds.AiChat);
        Assert.Contains(descriptor.Capabilities, capability => capability.Id == ProviderCapabilityIds.AiRerank);
        var chatCapability = descriptor.Capabilities.Single(capability => capability.Id == ProviderCapabilityIds.AiChat);
        Assert.Contains(chatCapability.ModePolicies, policy => policy.Id == "advisory" && !policy.AllowNetwork);

        var modelCatalog = await client.GetFromJsonAsync<ProviderModelListResult>($"/providers/{DeterministicAiProviderTarget.ProviderId}/models");
        Assert.NotNull(modelCatalog);
        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, modelCatalog.Status);
        Assert.Equal("local-static", modelCatalog.Source);
        var localModel = Assert.Single(modelCatalog.Items);
        Assert.Equal(DeterministicAiProviderTarget.ProviderId, localModel.Id);
        Assert.True(localModel.Default);
        Assert.Contains(ProviderCapabilityIds.AiChat, localModel.Capabilities);

        var quota = await client.GetFromJsonAsync<ProviderQuotaResult>($"/providers/{DeterministicAiProviderTarget.ProviderId}/quota");
        Assert.NotNull(quota);
        Assert.Equal(DeterministicAiProviderTarget.ProviderId, quota.Provider);
        Assert.Equal(ProviderQuotaStatuses.Unsupported, quota.Status);
        Assert.True(quota.Advisory);

        var quotas = await client.GetFromJsonAsync<List<ProviderQuotaResult>>("/providers/quotas");
        Assert.NotNull(quotas);
        Assert.Contains(quotas, item => item.Provider == DeterministicAiProviderTarget.ProviderId && item.Status == ProviderQuotaStatuses.Unsupported);

        var rerankerDescriptor = await client.GetFromJsonAsync<ProviderTargetDescriptor>($"/providers/{LocalTokenOverlapRerankerProviderTarget.ProviderId}");
        Assert.NotNull(rerankerDescriptor);
        var rerankerCapability = Assert.Single(rerankerDescriptor.Capabilities);
        Assert.Equal(ProviderCapabilityIds.AiRerank, rerankerCapability.Id);

        var rerankerCatalog = await client.GetFromJsonAsync<ProviderModelListResult>($"/providers/{LocalTokenOverlapRerankerProviderTarget.ProviderId}/models");
        Assert.NotNull(rerankerCatalog);
        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, rerankerCatalog.Status);
        var rerankerModel = Assert.Single(rerankerCatalog.Items);
        Assert.Equal(LocalTokenOverlapRerankerProviderTarget.ProviderId, rerankerModel.Id);

        var onnxCatalog = await client.GetFromJsonAsync<ProviderModelListResult>($"/providers/{OnnxCrossEncoderRerankerProviderTargets.CpuProviderId}/models");
        Assert.NotNull(onnxCatalog);
        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, onnxCatalog.Status);
        Assert.Equal(OnnxCrossEncoderRerankerProviderTargets.DefaultCpuModelId, onnxCatalog.DefaultModelId);

        var doctor = await client.GetFromJsonAsync<ProviderDoctorResult>($"/providers/{DeterministicAiProviderTarget.ProviderId}/doctor");
        Assert.NotNull(doctor);
        Assert.Equal(ProviderDoctorStatuses.Warning, doctor.Status);
        Assert.Contains(doctor.Checks, check => check.Id == "local.availability" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(doctor.Checks, check => check.Id == "model.catalog" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(doctor.Checks, check => check.Id == $"readiness.{ProviderCapabilityIds.AiChat}" && check.Status == ProviderDoctorStatuses.Warning);

        var allDoctor = await client.GetFromJsonAsync<List<ProviderDoctorResult>>("/providers/doctor");
        Assert.NotNull(allDoctor);
        Assert.Contains(allDoctor, item => item.Provider == DeterministicAiProviderTarget.ProviderId);

        var staticQualifications = await client.GetFromJsonAsync<List<ProviderQualification>>($"/providers/{DeterministicAiProviderTarget.ProviderId}/qualifications");
        Assert.NotNull(staticQualifications);
        Assert.Contains(staticQualifications, qualification =>
            qualification.Capability == ProviderCapabilityIds.AiChat &&
            qualification.Status == ProviderQualificationStatuses.Unvalidated &&
            qualification.DriftTriggers.Contains("config_hash_changed"));

        var initialReadiness = await client.GetFromJsonAsync<ProviderReadinessEnvelope>($"/providers/{DeterministicAiProviderTarget.ProviderId}/readiness");
        Assert.NotNull(initialReadiness);
        var initialChatReadiness = initialReadiness.Items.Single(item => item.Capability == ProviderCapabilityIds.AiChat);
        Assert.True(initialChatReadiness.Callable);
        Assert.False(initialChatReadiness.Ready);
        Assert.True(initialChatReadiness.CanRunUnvalidated);
        Assert.Equal(ProviderQualificationStatuses.Unvalidated, initialChatReadiness.QualificationStatus);
        Assert.Equal("unvalidated", initialChatReadiness.Reason);
        Assert.Contains("run", initialChatReadiness.Operations);
        Assert.Contains("advisory", initialChatReadiness.Modes);
        Assert.False(initialChatReadiness.AuthRequired);
        Assert.Equal(120, ((JsonElement)initialChatReadiness.OperationalLimits["defaultTimeoutSeconds"]!).GetInt32());

        var qualified = await PostJsonAsync<List<ProviderQualification>>(client, $"/providers/{DeterministicAiProviderTarget.ProviderId}/qualify", new ProviderQualificationRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Mode = "mechanics"
        });

        var chatQualification = Assert.Single(qualified);
        Assert.Equal(ProviderCapabilityIds.AiChat, chatQualification.Capability);
        Assert.Equal(ProviderQualificationStatuses.Validated, chatQualification.Status);
        Assert.NotNull(chatQualification.LastValidatedAt);
        var evidenceTraceRef = Assert.Single(chatQualification.EvidenceRefs, reference => reference.StartsWith("trace:", StringComparison.Ordinal));
        var evidenceTrace = await client.GetFromJsonAsync<TraceRecord>($"/traces/{evidenceTraceRef["trace:".Length..]}");
        Assert.NotNull(evidenceTrace);
        Assert.Equal("provider.run", evidenceTrace!.Operation);

        var refreshedQualifications = await client.GetFromJsonAsync<List<ProviderQualification>>($"/providers/{DeterministicAiProviderTarget.ProviderId}/qualifications");
        Assert.NotNull(refreshedQualifications);
        var refreshedChatQualification = refreshedQualifications.Single(qualification => qualification.Capability == ProviderCapabilityIds.AiChat);
        Assert.Equal(ProviderQualificationStatuses.Validated, refreshedChatQualification.Status);
        Assert.NotNull(refreshedChatQualification.LastValidatedAt);
        Assert.Contains(evidenceTraceRef, refreshedChatQualification.EvidenceRefs);

        var refreshedReadiness = await client.GetFromJsonAsync<ProviderReadinessEnvelope>("/providers/readiness");
        Assert.NotNull(refreshedReadiness);
        var refreshedChatReadiness = refreshedReadiness.Items.Single(item =>
            item.Provider == DeterministicAiProviderTarget.ProviderId &&
            item.Capability == ProviderCapabilityIds.AiChat);
        Assert.True(refreshedChatReadiness.Callable);
        Assert.True(refreshedChatReadiness.Ready);
        Assert.False(refreshedChatReadiness.CanRunUnvalidated);
        Assert.Equal(ProviderQualificationStatuses.Validated, refreshedChatReadiness.QualificationStatus);
        Assert.Equal("validated", refreshedChatReadiness.Reason);
        Assert.Contains(evidenceTraceRef, refreshedChatReadiness.EvidenceRefs);

        var qualificationTraces = await client.GetFromJsonAsync<List<TraceRecord>>("/traces?operation=provider.qualification&limit=10");
        Assert.NotNull(qualificationTraces);
        Assert.Contains(qualificationTraces, trace => trace.Adapter == DeterministicAiProviderTarget.ProviderId);

        var runResponse = await client.PostAsJsonAsync($"/providers/{DeterministicAiProviderTarget.ProviderId}/run", new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiChatRequest
            {
                Messages = new List<AiMessage>
                {
                    new() { Role = "user", Content = "Summarize the selected chunks with private token provider-trace-secret." }
                }
            }),
            MeteringContext = new AiMeteringContext
            {
                RunnerSessionId = "runner-session-test",
                ProviderThreadId = "provider-thread-test",
                TurnId = "turn-test"
            }
        });
        Assert.Equal(HttpStatusCode.Accepted, runResponse.StatusCode);
        var admittedProviderRun = await runResponse.Content.ReadFromJsonAsync<ProviderRunJob>();
        Assert.NotNull(admittedProviderRun);
        Assert.Equal("runProviderCapability", admittedProviderRun!.Admission.OperationId);
        Assert.Equal($"/provider-jobs/{admittedProviderRun.Id}", runResponse.Headers.Location?.ToString());
        ProviderRunJob? completedProviderRun = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            completedProviderRun = await client.GetFromJsonAsync<ProviderRunJob>($"/provider-jobs/{admittedProviderRun.Id}");
            if (completedProviderRun is not null && completedProviderRun.Status is not (ProviderJobStatus.Queued or ProviderJobStatus.Running))
            {
                break;
            }

            await Task.Delay(20);
        }
        Assert.NotNull(completedProviderRun);
        Assert.Equal(ProviderJobStatus.Succeeded, completedProviderRun!.Status);
        Assert.Equal("runProviderCapability", completedProviderRun.Admission.OperationId);
        var result = Assert.IsType<ProviderRunResult>(completedProviderRun.Result);

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(DeterministicAiProviderTarget.ProviderId, result.Provider);
        Assert.Contains("deterministic advisory response", result.Output["message"]?["content"]?.GetValue<string>());
        Assert.NotNull(result.Trace);
        var resultTrace = result.Trace!;
        Assert.Equal(ProviderBoundary.AuthorityBoundary, resultTrace.AuthorityBoundary);
        var metering = Assert.Single(result.Metering);
        Assert.Single(completedProviderRun.Metering);
        Assert.Equal(metering.ReceiptId, completedProviderRun.Metering[0].ReceiptId);
        Assert.Equal(completedProviderRun.Id, metering.Subject.ProviderRunId);
        Assert.Equal(completedProviderRun.Id, metering.Subject.ExecutionRunId);
        Assert.Equal("runner-session-test", metering.Subject.RunnerSessionId);
        Assert.Equal("provider-thread-test", metering.Subject.ProviderThreadId);
        Assert.Equal("turn-test", metering.Subject.TurnId);
        Assert.Equal(AiMeteringCompleteness.Partial, metering.Completeness);
        Assert.Equal(AiMeteringAttestationLevels.SelfReported, metering.AttestationLevel);
        Assert.Null(metering.Integrity);
        Assert.Equal(1, metering.Measurements.Single(item => item.Name == AiMeteringMeasurementNames.ProviderCalls).Value);
        Assert.True(metering.Measurements.Single(item => item.Name == AiMeteringMeasurementNames.PayloadBytesIn).Value > 0);
        var meteringHash = AiMeteringCryptography.ComputeReceiptEnvelopeHash(metering);
        Assert.Contains(meteringHash, resultTrace.MeteringReceiptHashes);

        var traceResponse = await client.GetAsync($"/traces/{resultTrace.TraceId}");
        var traceJson = await traceResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, traceResponse.StatusCode);
        Assert.DoesNotContain("provider-trace-secret", traceJson, StringComparison.Ordinal);

        var trace = JsonSerializer.Deserialize<TraceRecord>(traceJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(resultTrace.TraceId, trace.Id);
        Assert.Equal("provider.run", trace.Operation);
        Assert.Equal(DeterministicAiProviderTarget.ProviderId, trace.Adapter);
        Assert.Equal(ProviderCapabilityIds.AiChat, ((JsonElement)trace.Request["capability"]!).GetString());
        Assert.Equal(resultTrace.InputHash, ((JsonElement)trace.Request["payloadHash"]!).GetString());
        Assert.Equal("Succeeded", ((JsonElement)trace.ResultSummary["status"]!).GetString());
        Assert.Equal(resultTrace.OutputHash, ((JsonElement)trace.ResultSummary["outputHash"]!).GetString());
        Assert.Contains(meteringHash, ((JsonElement)trace.ResultSummary["meteringReceiptHashes"]!).EnumerateArray().Select(item => item.GetString()));

        var traces = await client.GetFromJsonAsync<List<TraceRecord>>("/traces?operation=provider.run&limit=10");
        Assert.NotNull(traces);
        Assert.Contains(traces, stored => stored.Id == resultTrace.TraceId);

        var jobResponse = await client.PostAsJsonAsync($"/providers/{DeterministicAiProviderTarget.ProviderId}/jobs", new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiChatRequest
            {
                Messages = new List<AiMessage>
                {
                    new() { Role = "user", Content = "Run as an async provider job." }
                }
            })
        });
        Assert.Equal(HttpStatusCode.Accepted, jobResponse.StatusCode);
        var job = (await jobResponse.Content.ReadFromJsonAsync<ProviderRunJob>())!;
        Assert.Equal(ProviderJobStatus.Queued, job.Status);
        Assert.StartsWith("sha256:", job.RequestHash);

        ProviderRunJob? completedJob = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            completedJob = await client.GetFromJsonAsync<ProviderRunJob>($"/provider-jobs/{job.Id}");
            if (completedJob is not null && completedJob.Status != ProviderJobStatus.Queued && completedJob.Status != ProviderJobStatus.Running)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.NotNull(completedJob);
        Assert.Equal(ProviderJobStatus.Succeeded, completedJob!.Status);
        Assert.NotNull(completedJob.Result);
        var completedResult = completedJob.Result!;
        Assert.Equal(ProviderRunStatus.Succeeded, completedResult.Status);
        Assert.NotNull(completedResult.Trace);
        Assert.Equal(completedResult.Trace!.TraceId, completedJob.TraceId);
        Assert.True(completedJob.DurationMs >= 0);

        var jobs = await client.GetFromJsonAsync<List<ProviderRunJob>>($"/provider-jobs?provider={DeterministicAiProviderTarget.ProviderId}");
        Assert.NotNull(jobs);
        Assert.Contains(jobs, item => item.Id == job.Id);
        Assert.All(jobs, item => Assert.Null(item.Result));

        var jobsWithResults = await client.GetFromJsonAsync<List<ProviderRunJob>>($"/provider-jobs?provider={DeterministicAiProviderTarget.ProviderId}&includeResult=true");
        Assert.NotNull(jobsWithResults);
        Assert.Contains(jobsWithResults, item => item.Id == job.Id && item.Result is not null);

        var jobTrace = await client.GetFromJsonAsync<TraceRecord>($"/traces/{completedJob.TraceId}");
        Assert.NotNull(jobTrace);
        Assert.Equal(job.Id, ((JsonElement)jobTrace.Request["jobId"]!).GetString());

        await AssertJobBackedByExecutionRuntimeAsync(
            dbPath,
            job.Id,
            ExecutionRuntimeProviderRunJobAdapter.HandlerId,
            ExecutionRuntimeProviderRunJobAdapter.PluginId);

        var cancelledCompletedJob = await client.DeleteFromJsonAsync<ProviderRunJob>($"/provider-jobs/{job.Id}");
        Assert.NotNull(cancelledCompletedJob);
        Assert.Equal(ProviderJobStatus.Succeeded, cancelledCompletedJob!.Status);
        Assert.True(cancelledCompletedJob.CancellationRequested);
    }

    [Fact]
    public async Task Server_SignsProviderMeteringWhenConfigured()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-signed-metering-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");
        var keyPath = Path.Combine(Path.GetTempPath(), $"vyral-server-metering-{Guid.NewGuid():N}.pem");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await File.WriteAllTextAsync(keyPath, key.ExportECPrivateKeyPem());
        try
        {
            await using var factory = CreateFactory(dbPath, objectsPath, settings: new Dictionary<string, string?>
            {
                ["Providers:Metering:SigningKeyPath"] = keyPath,
                ["Providers:Metering:Issuer"] = "spiffe://vyral.test/provider-runner",
                ["Providers:Metering:KeyId"] = "provider-runner-key"
            });
            var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync($"/providers/{DeterministicAiProviderTarget.ProviderId}/run", new ProviderRunRequest
            {
                Capability = ProviderCapabilityIds.AiChat,
                Payload = ProviderJson.ToJsonObject(new AiChatRequest
                {
                    Messages = { new AiMessage { Role = "user", Content = "signed metering test" } }
                }),
                MeteringContext = new AiMeteringContext { RunnerSessionId = "session-signed" }
            });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var admitted = (await response.Content.ReadFromJsonAsync<ProviderRunJob>())!;

            ProviderRunJob? completed = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                completed = await client.GetFromJsonAsync<ProviderRunJob>($"/provider-jobs/{admitted.Id}");
                if (completed?.Status is not (ProviderJobStatus.Queued or ProviderJobStatus.Running))
                {
                    break;
                }
                await Task.Delay(20);
            }

            Assert.NotNull(completed);
            Assert.Equal(ProviderJobStatus.Succeeded, completed!.Status);
            var receipt = Assert.Single(completed.Metering);
            Assert.Equal(AiMeteringAttestationLevels.ObserverSigned, receipt.AttestationLevel);
            Assert.Equal(AiMeteringOutcomes.Succeeded, receipt.Outcome);
            Assert.Equal("session-signed", receipt.Subject.RunnerSessionId);
            Assert.Null(receipt.Sequence);
            var verified = AiMeteringCryptography.VerifyReceipt(
                receipt,
                key,
                "spiffe://vyral.test/provider-runner",
                "provider-runner-key");
            Assert.True(verified.Valid, string.Join("; ", verified.Errors));
            Assert.Contains(
                AiMeteringCryptography.ComputeReceiptEnvelopeHash(receipt),
                completed.Result!.Trace!.MeteringReceiptHashes);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task Server_ProviderReadinessUsesFreshestQualificationTime()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-qualification-freshness-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var baseline = await client.GetFromJsonAsync<List<ProviderQualification>>($"/providers/{DeterministicAiProviderTarget.ProviderId}/qualifications");
        Assert.NotNull(baseline);
        var aiExtract = baseline!.Single(qualification => qualification.Capability == ProviderCapabilityIds.AiExtract);

        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();
        var now = DateTime.UtcNow;
        var validatedTraceId = Guid.NewGuid().ToString("N");
        await traces.WriteTraceAsync(new TraceRecord
        {
            Id = validatedTraceId,
            Operation = "provider.qualification",
            Adapter = DeterministicAiProviderTarget.ProviderId,
            StartedAt = now,
            CreatedAt = now,
            Request = new Dictionary<string, object?>
            {
                ["provider"] = DeterministicAiProviderTarget.ProviderId,
                ["capability"] = ProviderCapabilityIds.AiExtract,
                ["mode"] = "mechanics",
                ["configHash"] = aiExtract.ConfigHash
            },
            ResultSummary = new Dictionary<string, object?>
            {
                ["status"] = ProviderQualificationStatuses.Validated,
                ["configHash"] = aiExtract.ConfigHash,
                ["operationSet"] = aiExtract.OperationSet,
                ["evidenceRefs"] = new[] { $"trace:{validatedTraceId}" }
            }
        });
        await traces.WriteTraceAsync(new TraceRecord
        {
            Operation = "provider.qualification",
            Adapter = DeterministicAiProviderTarget.ProviderId,
            StartedAt = now.AddMinutes(-10),
            CreatedAt = now.AddMinutes(1),
            Request = new Dictionary<string, object?>
            {
                ["provider"] = DeterministicAiProviderTarget.ProviderId,
                ["capability"] = ProviderCapabilityIds.AiExtract,
                ["mode"] = "mechanics",
                ["configHash"] = aiExtract.ConfigHash
            },
            ResultSummary = new Dictionary<string, object?>
            {
                ["status"] = ProviderQualificationStatuses.Failed,
                ["configHash"] = aiExtract.ConfigHash,
                ["operationSet"] = aiExtract.OperationSet
            }
        });

        var readiness = await client.GetFromJsonAsync<ProviderReadinessEnvelope>($"/providers/{DeterministicAiProviderTarget.ProviderId}/readiness");

        Assert.NotNull(readiness);
        var extractReadiness = readiness!.Items.Single(item => item.Capability == ProviderCapabilityIds.AiExtract);
        Assert.True(extractReadiness.Ready);
        Assert.Equal(ProviderQualificationStatuses.Validated, extractReadiness.QualificationStatus);
        Assert.Contains($"trace:{validatedTraceId}", extractReadiness.EvidenceRefs);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("enabled")]
    public async Task Server_AcceptsBooleanAliasesForLiveProviderTargets(string enabledValue)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, settings: new Dictionary<string, string?>
        {
            ["Providers:EnableLiveTargets"] = enabledValue
        });
        var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<List<ProviderProfile>>("/providers");

        Assert.NotNull(providers);
        Assert.Contains(providers, provider => provider.Id == "codex-cli");
        Assert.Contains(providers, provider => provider.Id == "claude-cli");
        Assert.Contains(providers, provider => provider.Id == "gemini-cli");
        Assert.Contains(providers, provider => provider.Id == "grok-build-cli");
    }

    [Fact]
    public void Server_RejectsWorkspaceAgentRegistrationWithoutApiKeyAuthentication()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        using var factory = CreateFactory(dbPath, objectsPath, settings: new Dictionary<string, string?>
        {
            ["Providers:EnableLiveTargets"] = "true",
            ["Providers:WorkspaceAgent:Enabled"] = "true"
        });

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("API-key authentication", error.Message);
    }

    [Fact]
    public async Task Server_RegistersWorkspaceAgentOnlyWithExplicitOptInAndAuthentication()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"vyral-workspaces-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"vyral-workspace-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(stagingRoot);

        try
        {
            await using var factory = CreateFactory(dbPath, objectsPath, settings: new Dictionary<string, string?>
            {
                ["Providers:EnableLiveTargets"] = "true",
                ["Providers:WorkspaceAgent:Enabled"] = "true",
                ["Providers:WorkspaceAgent:AgentCommand"] = "/bin/true",
                ["Providers:WorkspaceAgent:AllowedWorkspaceRoots:0"] = workspaceRoot,
                ["Providers:WorkspaceAgent:StagingRoot"] = stagingRoot,
                ["Providers:WorkspaceAgent:ToolSearchPaths:0"] = "/usr/bin",
                ["Server:ApiKey"] = "workspace-test-key"
            });
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(ServerAccessOptions.DefaultApiKeyHeader, "workspace-test-key");

            var providers = await client.GetFromJsonAsync<List<ProviderProfile>>("/providers");

            Assert.NotNull(providers);
            Assert.Contains(providers, provider => provider.Id == "workspace-cli");
        }
        finally
        {
            try { Directory.Delete(workspaceRoot, recursive: true); } catch { }
            try { Directory.Delete(stagingRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Server_DisabledLiveProviderReadinessReturnsCapabilityBlockers()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-disabled-ready-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var readiness = await client.GetFromJsonAsync<ProviderReadinessEnvelope>("/providers/codex-cli/readiness");

        Assert.NotNull(readiness);
        var disabled = Assert.Single(readiness!.DisabledProviders);
        Assert.Equal("codex-cli", disabled.ProviderId);
        Assert.Equal("disabled", disabled.RegistrationStatus);
        Assert.Contains("Providers:EnableLiveTargets=true", disabled.Hint);
        Assert.NotEmpty(readiness.Items);
        var extract = readiness.Items.Single(item => item.Capability == ProviderCapabilityIds.AiExtract);
        Assert.Equal("codex-cli", extract.Provider);
        Assert.Equal("disabled", extract.RegistrationStatus);
        Assert.Contains("Providers:EnableLiveTargets=true", extract.RegistrationHint);
        Assert.False(extract.Callable);
        Assert.False(extract.Ready);
        Assert.False(extract.CanRunUnvalidated);
        Assert.Equal("provider_disabled", extract.Reason);
        Assert.Contains("run", extract.Operations);
    }

    [Fact]
    public async Task Server_BatchUpsertsRecordsAndReportsPerRecordFailures()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "chunks",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
            }
        });
        await EnsureSuccessAsync(create);

        var result = await PostJsonAsync<RecordBatchUpsertResult>(client, "/collections/chunks/records/batch", new RecordBatchUpsertRequest
        {
            ContinueOnError = true,
            Records = new List<VyralRecord>
            {
                new()
                {
                    Id = "ok",
                    PartitionKey = "tenant-a",
                    Vectors = new Dictionary<string, VyralVector>
                    {
                        ["contentEmbedding"] = new() { Values = new float[] { 1, 0 }, Dimensions = 2 }
                    }
                },
                new()
                {
                    Id = "bad",
                    PartitionKey = "tenant-a",
                    Vectors = new Dictionary<string, VyralVector>
                    {
                        ["contentEmbedding"] = new() { Values = new float[] { 1, 0, 0 }, Dimensions = 3 }
                    }
                }
            }
        });

        Assert.Equal("chunks", result.Collection);
        Assert.Equal(2, result.Requested);
        Assert.Equal(2, result.Attempted);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.False(result.StoppedOnError);
        Assert.Equal(RecordUpsertStatuses.Succeeded, result.Items[0].Status);
        Assert.Equal("rev:1", result.Items[0].Etag);
        Assert.Equal(1, result.Items[0].Revision);
        Assert.Equal("failed", result.Items[1].Status);
        Assert.Contains("dimensions", result.Items[1].Error, StringComparison.OrdinalIgnoreCase);

        var record = await client.GetFromJsonAsync<VyralRecord>("/collections/chunks/records/tenant-a/ok");
        Assert.NotNull(record);
        Assert.Equal("ok", record.Id);
    }

    [Fact]
    public async Task Server_RecordWritesHonorHttpAndBatchPreconditions()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var createCollection = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy { Name = "guarded" });
        await EnsureSuccessAsync(createCollection);

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/collections/guarded/records")
        {
            Content = JsonContent.Create(new VyralRecord
            {
                Id = "state",
                PartitionKey = "tenant-a",
                Content = new JsonObject { ["status"] = "pending" }
            })
        };
        createRequest.Headers.TryAddWithoutValidation("If-None-Match", "*");
        var createResponse = await client.SendAsync(createRequest);
        await EnsureSuccessAsync(createResponse);
        var created = await createResponse.Content.ReadFromJsonAsync<VyralRecord>();
        Assert.NotNull(created);
        Assert.Equal("rev:1", created!.Etag);

        var duplicateRequest = new HttpRequestMessage(HttpMethod.Post, "/collections/guarded/records")
        {
            Content = JsonContent.Create(new VyralRecord { Id = "state", PartitionKey = "tenant-a" })
        };
        duplicateRequest.Headers.TryAddWithoutValidation("If-None-Match", "*");
        var duplicateResponse = await client.SendAsync(duplicateRequest);
        Assert.Equal(HttpStatusCode.PreconditionFailed, duplicateResponse.StatusCode);

        var updateRequest = new HttpRequestMessage(HttpMethod.Post, "/collections/guarded/records")
        {
            Content = JsonContent.Create(new VyralRecord
            {
                Id = "state",
                PartitionKey = "tenant-a",
                Content = new JsonObject { ["status"] = "accepted" }
            })
        };
        updateRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{created.Etag}\"");
        var updateResponse = await client.SendAsync(updateRequest);
        await EnsureSuccessAsync(updateResponse);
        var updated = await updateResponse.Content.ReadFromJsonAsync<VyralRecord>();
        Assert.NotNull(updated);
        Assert.Equal("rev:2", updated!.Etag);

        var staleRequest = new HttpRequestMessage(HttpMethod.Post, "/collections/guarded/records")
        {
            Content = JsonContent.Create(new VyralRecord { Id = "state", PartitionKey = "tenant-a" })
        };
        staleRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{created.Etag}\"");
        var staleResponse = await client.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);

        var batch = await PostJsonAsync<RecordBatchUpsertResult>(client, "/collections/guarded/records/batch", new RecordBatchUpsertRequest
        {
            ContinueOnError = true,
            Records = new List<VyralRecord>
            {
                new() { Id = "batch-created", PartitionKey = "tenant-a" },
                new() { Id = "state", PartitionKey = "tenant-a" }
            },
            Preconditions = new List<RecordWritePrecondition?>
            {
                new() { IfNoneMatch = "*" },
                new() { ExpectedRevision = 1 }
            }
        });

        Assert.Equal(1, batch.Succeeded);
        Assert.Equal(1, batch.Failed);
        Assert.Equal(RecordUpsertStatuses.Succeeded, batch.Items[0].Status);
        Assert.Equal(RecordUpsertStatuses.Failed, batch.Items[1].Status);
        Assert.Contains("precondition", batch.Items[1].Error, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task UpsertAsync(HttpClient client, string id, string status, float[] vector)
    {
        var response = await client.PostAsJsonAsync("/collections/chunks/records", new VyralRecord
        {
            Id = id,
            PartitionKey = "tenant-a",
            Metadata = new JsonObject { ["status"] = status },
            Content = new JsonObject { ["text"] = id },
            Vectors = new Dictionary<string, VyralVector>
            {
                ["contentEmbedding"] = new() { Values = vector, Dimensions = vector.Length }
            }
        });
        await EnsureSuccessAsync(response);
    }

    private static VyralGraphEnvelope CreateServerGraphEnvelope()
    {
        return new VyralGraphEnvelope
        {
            Scope = new VyralGraphScope
            {
                GraphId = "example-graph",
                Namespace = "long-form-work",
                Collection = "passages",
                TenantId = "tenant-a",
                PartitionKey = "edition:sample"
            },
            Metadata = new JsonObject
            {
                ["fixture"] = "server-workflow"
            },
            Nodes = new List<VyralGraphNode>
            {
                new()
                {
                    Id = "work:analysis",
                    Type = "work",
                    Label = "Analysis"
                },
                new()
                {
                    Id = "passage:introduction",
                    Type = "passage",
                    Label = "Introduction",
                    SourceSpans = new List<VyralGraphSourceSpan>
                    {
                        new()
                        {
                            SourceRef = "document:sample:section:1",
                            CharStart = 0,
                            CharEnd = 40,
                            Unit = "utf16"
                        }
                    }
                }
            },
            Edges = new List<VyralGraphEdge>
            {
                new()
                {
                    Id = "edge:references",
                    SourceId = "work:analysis",
                    TargetId = "passage:introduction",
                    Predicate = "references"
                }
            },
            Assertions = new List<VyralGraphAssertion>
            {
                new()
                {
                    Id = "assertion:reference",
                    SubjectId = "edge:references",
                    SubjectKind = VyralGraphSubjectKinds.Edge,
                    Status = VyralGraphAssertionStatuses.Proposed,
                    Method = "fixture",
                    Actor = "test"
                }
            }
        };
    }

    private static async Task UpsertRecordAsync(HttpClient client, string id, string partitionKey, string text, float[] vector)
    {
        var response = await client.PostAsJsonAsync("/collections/chunks/records", new VyralRecord
        {
            Id = id,
            PartitionKey = partitionKey,
            Content = new JsonObject { ["text"] = text },
            Vectors = new Dictionary<string, VyralVector>
            {
                ["contentEmbedding"] = new() { Values = vector, Dimensions = vector.Length }
            }
        });
        await EnsureSuccessAsync(response);
    }

    private static async Task CreatePayloadBudgetCollectionAsync(HttpClient client)
    {
        var create = await client.PostAsJsonAsync("/collections", new RecordCollectionPolicy
        {
            Name = "payload-budget",
            VectorPolicies = new List<VectorFieldPolicy>
            {
                new() { Name = "contentEmbedding", Path = "/vectors/contentEmbedding/values", Dimensions = 2 }
            }
        });
        await EnsureSuccessAsync(create);
    }

    private static async Task UpsertPayloadBudgetRecordsAsync(HttpClient client, int contentChars, int metadataChars)
    {
        var largeMetadata = new string('m', metadataChars);
        var largeSourceLabel = new string('s', Math.Max(1, metadataChars / 2));
        for (var index = 0; index < 40; index++)
        {
            var text = $"target retention candidate {index:D2} " + new string((char)('a' + (index % 20)), Math.Max(0, contentChars));
            var response = await client.PostAsJsonAsync("/collections/payload-budget/records", new VyralRecord
            {
                Id = $"candidate-{index:D2}",
                PartitionKey = "tenant-a",
                Type = "chunk",
                Metadata = new JsonObject
                {
                    ["status"] = "active",
                    ["large"] = largeMetadata
                },
                Content = new JsonObject { ["text"] = text },
                Sources = new List<VyralSourceReference>
                {
                    new()
                    {
                        Id = $"source-{index:D2}",
                        Kind = "test",
                        Uri = $"file://payload/{index:D2}.txt",
                        Label = largeSourceLabel
                    }
                },
                Vectors = new Dictionary<string, VyralVector>
                {
                    ["contentEmbedding"] = new() { Values = new float[] { 1, 0 }, Dimensions = 2 }
                }
            });
            await EnsureSuccessAsync(response);
        }
    }

    private static async Task UpsertMultiVectorRecordAsync(HttpClient client, string id, string partitionKey, string text, float[] contentVector, float[] titleVector)
    {
        var response = await client.PostAsJsonAsync("/collections/chunks/records", new VyralRecord
        {
            Id = id,
            PartitionKey = partitionKey,
            Content = new JsonObject { ["text"] = text },
            Vectors = new Dictionary<string, VyralVector>
            {
                ["contentEmbedding"] = new() { Values = contentVector, Dimensions = contentVector.Length },
                ["titleEmbedding"] = new() { Values = titleVector, Dimensions = titleVector.Length }
            }
        });
        await EnsureSuccessAsync(response);
    }

    private static async Task<T> PostJsonAsync<T>(HttpClient client, string path, object payload)
    {
        var response = await client.PostAsJsonAsync(path, payload);
        await EnsureSuccessAsync(response);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }

        var statusUri = response.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("Accepted response did not include Location.");
        var resultProperty = typeof(T) == typeof(CollectionImportResult) ? "importResult"
            : typeof(T) == typeof(RecordBatchUpsertResult) ? "batchResult"
            : typeof(T) == typeof(VyralGraphCollectionImportResult) ? "importResult"
            : typeof(T) == typeof(RagIngestTextResult) ? "textResult"
            : typeof(T) == typeof(RagIngestTextBatchResult) ? "batchResult"
            : throw new InvalidOperationException($"No accepted-job projection is defined for {typeof(T).Name}.");

        for (var i = 0; i < 200; i++)
        {
            using var statusResponse = await client.GetAsync(statusUri);
            await EnsureSuccessAsync(statusResponse);
            var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
            var state = status.GetProperty("status").GetString();
            if (string.Equals(state, "succeeded", StringComparison.Ordinal))
            {
                return status.GetProperty(resultProperty).Deserialize<T>(ExecutionJson.Options)!;
            }
            if (state is "failed" or "cancelled" or "rejected" or "timed_out")
            {
                throw new InvalidOperationException($"Admitted {typeof(T).Name} job terminated as {state}: {status}");
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Admitted {typeof(T).Name} job did not complete in time.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}). Body: {body}");
    }

    private static void AssertPath(JsonElement paths, string path, params string[] methods)
    {
        Assert.True(paths.TryGetProperty(path, out var operations), $"OpenAPI contract missing path {path}.");
        foreach (var method in methods)
        {
            Assert.True(operations.TryGetProperty(method, out _), $"OpenAPI contract missing {method.ToUpperInvariant()} {path}.");
        }
    }

    [Fact]
    public async Task CliProvider_ReadinessReflectsCommandResolutionFailure()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-cliready-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, settings: new Dictionary<string, string?>
        {
            ["Providers:EnableLiveTargets"] = "true",
            ["Providers:Gemini:Command"] = "vyral-nonexistent-gemini-command"
        });
        var client = factory.CreateClient();

        var readiness = await client.GetFromJsonAsync<ProviderReadinessEnvelope>("/providers/gemini-cli/readiness");
        Assert.NotNull(readiness);
        Assert.NotEmpty(readiness.Items);
        Assert.All(readiness.Items, item =>
        {
            Assert.False(item.Callable);
            Assert.False(item.Ready);
            Assert.False(item.CanRunUnvalidated);
            Assert.Equal("command_not_found", item.Reason);
        });
    }

    [Fact]
    public async Task Server_ExposesExternalWorkerLeaseProtocolOverJson()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-external-worker-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-external-worker-objects-{Guid.NewGuid():N}");
        const string handlerId = "test.external.callback";
        await using var factory = CreateFactory(dbPath, objectsPath, settings: new Dictionary<string, string?>
        {
            ["ExecutionRuntime:ExternalHandlers:0:HandlerId"] = handlerId,
            ["ExecutionRuntime:ExternalHandlers:0:PluginId"] = "test.external",
            ["ExecutionRuntime:ExternalHandlers:0:DisplayName"] = "External callback"
        });
        var client = factory.CreateClient();

        var acceptedResponse = await client.PostAsJsonAsync("/execution/runs", new ExecutionRunRequest
        {
            HandlerId = handlerId,
            IdempotencyKey = "worker-callback-secret",
            Payload = new JsonObject { ["callbackId"] = "cb_123" }
        });
        Assert.Equal(HttpStatusCode.Accepted, acceptedResponse.StatusCode);
        var accepted = (await acceptedResponse.Content.ReadFromJsonAsync<ExecutionRun>())!;

        var leasedResponse = await client.PostAsJsonAsync("/execution/workers/leases", new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "go-worker",
            HandlerIds = { handlerId }
        });
        Assert.Equal(HttpStatusCode.OK, leasedResponse.StatusCode);
        var lease = (await leasedResponse.Content.ReadFromJsonAsync<ExecutionExternalWorkerLease>())!;
        Assert.Equal(accepted.Id, lease.Run.Id);
        Assert.Equal("startExecutionRun", lease.Run.Admission.OperationId);
        Assert.NotEmpty(lease.Run.Admission.AdmissionId);
        Assert.Null(lease.Run.IdempotencyKey);

        var reportResponse = await client.PostAsJsonAsync("/execution/workers/leases/reports", new ExecutionExternalWorkerReportRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker",
            Update = new ExecutionRunUpdate { Progress = 0.5, CurrentStep = "callback-received" }
        });
        reportResponse.EnsureSuccessStatusCode();
        var reported = (await reportResponse.Content.ReadFromJsonAsync<ExecutionRun>())!;
        Assert.NotEmpty(reported.Admission.AdmissionId);
        Assert.Null(reported.IdempotencyKey);

        var workerEventResponse = await client.PostAsJsonAsync("/execution/workers/leases/events", new ExecutionExternalWorkerEventRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker",
            Type = ExecutionEventTypes.Log,
            Message = "Callback received."
        });
        Assert.Equal(HttpStatusCode.NoContent, workerEventResponse.StatusCode);

        var artifactResponse = await client.PostAsJsonAsync("/execution/workers/leases/artifacts", new ExecutionExternalWorkerArtifactRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker",
            Artifact = new ExecutionArtifactWrite
            {
                Name = "callback-request",
                Content = new JsonObject { ["callbackId"] = "cb_123" }
            }
        });
        artifactResponse.EnsureSuccessStatusCode();

        var checkpointResponse = await client.PostAsJsonAsync("/execution/workers/leases/checkpoints", new ExecutionExternalWorkerCheckpointRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker",
            Checkpoint = new ExecutionCheckpointWrite
            {
                Key = "callback",
                Content = new JsonObject { ["deliveryKey"] = "callback:cb_123" }
            }
        });
        checkpointResponse.EnsureSuccessStatusCode();

        var checkpointFromLeaseResponse = await client.PostAsJsonAsync("/execution/workers/leases/checkpoints/read", new ExecutionExternalWorkerCheckpointReadRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker",
            Key = "callback"
        });
        checkpointFromLeaseResponse.EnsureSuccessStatusCode();
        var checkpointFromLease = await checkpointFromLeaseResponse.Content.ReadFromJsonAsync<ExecutionCheckpoint>();
        Assert.NotNull(checkpointFromLease);
        Assert.Equal("callback:cb_123", checkpointFromLease!.Content!["deliveryKey"]!.GetValue<string>());

        var waitResponse = await client.PostAsJsonAsync("/execution/workers/leases/wait", new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = "go-worker",
            Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
            Name = "approval",
            TimeoutAtUtc = DateTime.UtcNow.AddMinutes(5)
        });
        waitResponse.EnsureSuccessStatusCode();
        var suspended = (await waitResponse.Content.ReadFromJsonAsync<ExecutionExternalWorkerWaitResponse>())!;
        Assert.True(suspended.Suspended);
        Assert.NotEmpty(suspended.Run.Admission.AdmissionId);
        Assert.Null(suspended.Run.IdempotencyKey);

        var eventResponse = await client.PostAsJsonAsync($"/execution/runs/{accepted.Id}/events", new ExecutionExternalEventRequest
        {
            Name = "approval",
            Payload = new JsonObject { ["decision"] = "approved" }
        });
        eventResponse.EnsureSuccessStatusCode();

        var resumedLeaseResponse = await client.PostAsJsonAsync("/execution/workers/leases", new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "go-worker",
            HandlerIds = { handlerId },
            RunId = accepted.Id
        });
        resumedLeaseResponse.EnsureSuccessStatusCode();
        var resumedLease = (await resumedLeaseResponse.Content.ReadFromJsonAsync<ExecutionExternalWorkerLease>())!;
        Assert.NotEmpty(resumedLease.Run.Admission.AdmissionId);
        Assert.Null(resumedLease.Run.IdempotencyKey);

        var outcomeResponse = await client.PostAsJsonAsync("/execution/workers/leases/wait", new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = resumedLease.LeaseKey,
            LeaseToken = resumedLease.LeaseToken,
            WorkerId = "go-worker",
            Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
            Name = "approval",
            TimeoutAtUtc = DateTime.UtcNow.AddMinutes(5)
        });
        outcomeResponse.EnsureSuccessStatusCode();
        var outcome = (await outcomeResponse.Content.ReadFromJsonAsync<ExecutionExternalWorkerWaitResponse>())!;
        Assert.False(outcome.Suspended);
        Assert.NotEmpty(outcome.Run.Admission.AdmissionId);
        Assert.Null(outcome.Run.IdempotencyKey);
        Assert.Equal("approved", outcome.Outcome!.Event!.Payload!["decision"]!.GetValue<string>());

        var completionResponse = await client.PostAsJsonAsync("/execution/workers/leases/complete", new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = resumedLease.LeaseKey,
            LeaseToken = resumedLease.LeaseToken,
            WorkerId = "go-worker",
            Result = ExecutionRunResult.Succeeded(new JsonObject { ["callback"] = "delivered" })
        });
        completionResponse.EnsureSuccessStatusCode();
        var completed = (await completionResponse.Content.ReadFromJsonAsync<ExecutionRun>())!;
        Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
        Assert.NotEmpty(completed.Admission.AdmissionId);
        Assert.Null(completed.IdempotencyKey);
        Assert.Equal("delivered", completed.Result!["callback"]!.GetValue<string>());

        var checkpoint = await client.GetFromJsonAsync<ExecutionCheckpoint>($"/execution/runs/{accepted.Id}/checkpoints/callback");
        Assert.NotNull(checkpoint);
        Assert.Equal("callback:cb_123", checkpoint!.Content!["deliveryKey"]!.GetValue<string>());
        var artifacts = await client.GetFromJsonAsync<List<ExecutionArtifact>>($"/execution/runs/{accepted.Id}/artifacts");
        Assert.Contains(artifacts!, item => item.Name == "callback-request");
    }

    [Fact]
    public async Task Server_ExecutionAccessBindsVerifiedDevelopmentIdentityToProductAndWorkerPolicies()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-execution-access-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-execution-access-objects-{Guid.NewGuid():N}");
        const string handlerId = "test.product.callback";
        await using var factory = CreateFactory(dbPath, objectsPath, settings: new Dictionary<string, string?>
        {
            ["Server:ExecutionAccess:AuthenticationMode"] = VyralExecutionAuthenticationModes.DevelopmentHeader,
            ["ExecutionRuntime:ExternalHandlers:0:HandlerId"] = handlerId,
            ["ExecutionRuntime:ExternalHandlers:0:PluginId"] = "test.product",
            ["ExecutionRuntime:ProductPolicies:0:ProductId"] = "product-a",
            ["ExecutionRuntime:ProductPolicies:0:AllowedServiceIdentities:0"] = "product-a-worker",
            ["Server:ExecutionAccess:IdentityPolicies:0:Principal"] = "product-a@tests.example",
            ["Server:ExecutionAccess:IdentityPolicies:0:ProductId"] = "product-a",
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedTenantIds:0"] = "tenant-a",
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedHandlerIds:0"] = handlerId,
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedOperations:0"] = ExecutionAccessOperations.StartRun,
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedOperations:1"] = ExecutionAccessOperations.ReadRun,
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedOperations:2"] = ExecutionAccessOperations.RaiseEvent,
            ["Server:ExecutionAccess:IdentityPolicies:1:Principal"] = "worker@tests.example",
            ["Server:ExecutionAccess:IdentityPolicies:1:ProductId"] = "product-a",
            ["Server:ExecutionAccess:IdentityPolicies:1:WorkerId"] = "product-a-worker",
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedTenantIds:0"] = "tenant-a",
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedHandlerIds:0"] = handlerId,
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedOperations:0"] = ExecutionAccessOperations.Worker
        });
        var productClient = factory.CreateClient();
        productClient.DefaultRequestHeaders.Add("X-Vyral-Development-Identity", "product-a@tests.example");
        var effectiveRuntime = await productClient.GetFromJsonAsync<EffectiveExecutionRuntimeSurface>("/execution/runtime/effective?productId=product-a&tenantId=tenant-a");
        Assert.NotNull(effectiveRuntime);
        Assert.True(effectiveRuntime!.Scope.SharedExecution);
        Assert.True(effectiveRuntime.Scope.ScopeRequired);
        Assert.Equal("product-a", effectiveRuntime.Scope.ProductId);
        Assert.Equal("tenant-a", effectiveRuntime.Scope.TenantId);
        Assert.Equal(new[] { handlerId }, effectiveRuntime.Handlers.Select(handler => handler.HandlerId));
        Assert.Null(effectiveRuntime.Status.Details);
        Assert.Equal(HttpStatusCode.Forbidden, (await productClient.GetAsync("/execution/runtime")).StatusCode);

        var wrongDiscoveryScope = await productClient.GetAsync("/execution/runtime/effective?productId=product-a&tenantId=tenant-b");
        Assert.Equal(HttpStatusCode.Forbidden, wrongDiscoveryScope.StatusCode);
        var acceptedResponse = await productClient.PostAsJsonAsync("/execution/runs", new ExecutionRunRequest
        {
            HandlerId = handlerId,
            Scope = new ExecutionScope { ProductId = "product-a", TenantId = "tenant-a", ServiceIdentity = "untrusted" }
        });
        Assert.Equal(HttpStatusCode.Accepted, acceptedResponse.StatusCode);
        var accepted = (await acceptedResponse.Content.ReadFromJsonAsync<ExecutionRun>())!;
        Assert.Equal("product-a@tests.example", accepted.Scope!.ServiceIdentity);

        var wrongProduct = await productClient.PostAsJsonAsync("/execution/runs", new ExecutionRunRequest
        {
            HandlerId = handlerId,
            Scope = new ExecutionScope { ProductId = "product-b", TenantId = "tenant-a" }
        });
        Assert.Equal(HttpStatusCode.Forbidden, wrongProduct.StatusCode);

        var untrustedClient = factory.CreateClient();
        untrustedClient.DefaultRequestHeaders.Add("X-Vyral-Development-Identity", "other@tests.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await untrustedClient.GetAsync($"/execution/runs/{accepted.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await untrustedClient.GetAsync("/execution/runtime/effective?productId=product-a&tenantId=tenant-a")).StatusCode);

        var workerClient = factory.CreateClient();
        workerClient.DefaultRequestHeaders.Add("X-Vyral-Development-Identity", "worker@tests.example");
        var wrongWorker = await workerClient.PostAsJsonAsync("/execution/workers/leases", new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "impersonated-worker",
            HandlerIds = { handlerId }
        });
        Assert.Equal(HttpStatusCode.Forbidden, wrongWorker.StatusCode);

        var leaseResponse = await workerClient.PostAsJsonAsync("/execution/workers/leases", new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = "product-a-worker",
            HandlerIds = { handlerId },
            RunId = accepted.Id
        });
        leaseResponse.EnsureSuccessStatusCode();
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string dbPath,
        string objectsPath,
        int? embeddingDimensions = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("DatabasePath", dbPath);
                builder.UseSetting("ObjectsPath", objectsPath);
                if (embeddingDimensions.HasValue)
                {
                    builder.UseSetting("Embedding:Dimensions", embeddingDimensions.Value.ToString());
                }
                if (settings is not null)
                {
                    foreach (var (key, value) in settings)
                    {
                        builder.UseSetting(key, value);
                    }
                }
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        ["DatabasePath"] = dbPath,
                        ["ObjectsPath"] = objectsPath
                    };
                    if (embeddingDimensions.HasValue)
                    {
                        values["Embedding:Dimensions"] = embeddingDimensions.Value.ToString();
                    }
                    if (settings is not null)
                    {
                        foreach (var (key, value) in settings)
                        {
                            values[key] = value;
                        }
                    }

                    configuration.AddInMemoryCollection(values);
                });
            });
    }

    private static async Task<IReadOnlyList<RecordImportJob>> WaitForRecordImportJobsAsync(
        HttpClient client,
        IEnumerable<string> jobIds,
        int attempts)
    {
        var ids = jobIds.ToList();
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var jobs = await Task.WhenAll(ids.Select(id => client.GetFromJsonAsync<RecordImportJob>($"/record-import/jobs/{id}")));
            if (jobs.All(job => job is not null && job.Status is not (RecordImportJobStatuses.Queued or RecordImportJobStatuses.Running)))
            {
                return jobs.Where(job => job is not null).Cast<RecordImportJob>().ToList();
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException("Record import jobs did not reach terminal states before the polling limit.");
    }

    private static async Task<ExecutionRun> WaitForExecutionRunAsync(
        HttpClient client,
        string runId,
        string expectedStatus)
    {
        ExecutionRun? run = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            run = await client.GetFromJsonAsync<ExecutionRun>($"/execution/runs/{runId}");
            if (run?.Status == expectedStatus)
            {
                return run;
            }
            if (run is not null && ExecutionRunStatuses.IsTerminal(run.Status))
            {
                break;
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException(
            $"Execution run {runId} reached {run?.Status ?? "missing"}, expected {expectedStatus}.");
    }

    private static async Task AssertJobBackedByExecutionRuntimeAsync(
        string dbPath,
        string jobId,
        string expectedHandlerId,
        string expectedPluginId)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT handler_id, plugin_id, status
            FROM vyral_execution_runs
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", jobId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Job should be backed by a persisted execution runtime run.");
        Assert.Equal(expectedHandlerId, reader.GetString(0));
        Assert.Equal(expectedPluginId, reader.GetString(1));
        Assert.Equal(ExecutionRunStatuses.Succeeded, reader.GetString(2));
    }
}
