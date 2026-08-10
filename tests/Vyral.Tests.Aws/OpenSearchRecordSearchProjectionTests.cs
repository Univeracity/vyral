using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Aws;

namespace Vyral.Tests.Aws;

public sealed class OpenSearchRecordSearchProjectionTests
{
    [Fact]
    public async Task Projection_UsesRevisionFencedDerivedDocumentsAndDoesNotCopyContent()
    {
        var transport = new CapturingOpenSearchTransport();
        var projection = CreateProjection(transport);
        var policy = CreatePolicy();
        var record = CreateRecord(revision: 4);

        await projection.EnsureCollectionAsync(policy);
        await projection.ProjectAsync(RecordSearchProjectionChange.Upsert(policy.Name, record, "42"));

        var provision = transport.Requests[0];
        Assert.Equal(HttpMethod.Put, provision.Method);
        Assert.Contains("knn_vector", provision.JsonBody);
        Assert.Contains("faiss", provision.JsonBody);
        Assert.Contains("dimension", provision.JsonBody);

        var write = transport.Requests[1];
        Assert.Equal(HttpMethod.Put, write.Method);
        Assert.Contains("version=4", write.PathAndQuery);
        Assert.Contains("version_type=external_gte", write.PathAndQuery);
        Assert.Contains("partitionKey", write.JsonBody);
        Assert.Contains("vectors", write.JsonBody);
        Assert.DoesNotContain("very-sensitive-content", write.JsonBody, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata-secret", write.JsonBody, StringComparison.Ordinal);
        Assert.DoesNotContain("content", write.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_ReturnsCandidatesThatAreHydratedAndRevisionCheckedAgainstCanonicalStore()
    {
        var transport = new CapturingOpenSearchTransport();
        transport.EnqueueJson("""
            { "hits": { "hits": [
              { "_score": 0.91, "_source": { "partitionKey": "tenant-a", "id": "current", "revision": 3 } },
              { "_score": 0.87, "_source": { "partitionKey": "tenant-a", "id": "stale", "revision": 2 } }
            ] } }
            """);
        var projection = CreateProjection(transport);
        var policy = CreatePolicy();
        var current = CreateRecord(revision: 3);
        var staleNow = CreateRecord(id: "stale", revision: 3);
        var canonical = new ProjectionCanonicalStore(current, staleNow);

        var result = await projection.SearchAndHydrateAsync(canonical, policy, new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "embedding", Value = new[] { 0.1f, 0.2f, 0.3f }, Top = 10 },
            PartitionKeys = new List<string> { "tenant-a" },
            Filter = FilterNode.Eq("/metadata/status", "approved")
        });

        Assert.Equal("eventual", result.Consistency);
        Assert.Single(result.Items);
        Assert.Equal("current", result.Items[0].Record.Id);
        Assert.Equal(1, result.StaleCandidatesDiscarded);
        var search = Assert.Single(transport.Requests);
        Assert.Contains("knn", search.JsonBody);
        Assert.Contains("partitionKey", search.JsonBody);
        Assert.DoesNotContain("very-sensitive-content", search.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_SearchAddressesNestedVectorAndPolicyFilterMappings()
    {
        var transport = new CapturingOpenSearchTransport();
        transport.EnqueueJson("""
            { "hits": { "hits": [] } }
            """);
        var projection = CreateProjection(transport);
        var policy = CreatePolicy();

        await projection.SearchAsync(policy, new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "embedding", Value = new[] { 0.1f, 0.2f, 0.3f }, Top = 1 },
            Filter = FilterNode.Eq("/metadata/status", "approved")
        });

        var search = Assert.Single(transport.Requests);
        using var request = JsonDocument.Parse(search.JsonBody);
        var knn = request.RootElement.GetProperty("query").GetProperty("knn");
        var vectorField = Assert.Single(knn.EnumerateObject());
        Assert.StartsWith("vectors.v_", vectorField.Name, StringComparison.Ordinal);
        Assert.Contains("\"filters.f_", search.JsonBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"knn\":{\"v_", search.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_RejectsRequestsThatExceedItsCandidateSafetyCap()
    {
        var transport = new CapturingOpenSearchTransport();
        var projection = CreateProjection(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => projection.SearchAsync(CreatePolicy(), new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "embedding", Value = new[] { 0.1f, 0.2f, 0.3f }, Top = 101 }
        }));

        Assert.Contains("maximum candidate count (100)", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Projection_IndexesCompoundPolicyValuesAsPresenceWithoutCopyingThem()
    {
        var transport = new CapturingOpenSearchTransport();
        var projection = CreateProjection(transport);
        var policy = CreatePolicy();
        policy.IndexedMetadata.Add("/metadata/labels");
        var record = CreateRecord();
        record.Metadata!["labels"] = new JsonArray("private-label");

        await projection.EnsureCollectionAsync(policy);
        await projection.ProjectAsync(RecordSearchProjectionChange.Upsert(policy.Name, record));

        var write = transport.Requests[1];
        Assert.Contains("j:", write.JsonBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private-label", write.JsonBody, StringComparison.Ordinal);

        transport.EnqueueJson("""
            { "hits": { "hits": [] } }
            """);
        await projection.SearchAsync(policy, new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "embedding", Value = new[] { 0.1f, 0.2f, 0.3f }, Top = 1 },
            Filter = FilterNode.Exists("/metadata/labels")
        });

        Assert.Contains("\"exists\"", transport.Requests[2].JsonBody, StringComparison.Ordinal);
        Assert.Contains("\"filters.f_", transport.Requests[2].JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_PreservesExistsFalseAndJsonDeserializedInFilterSemantics()
    {
        var transport = new CapturingOpenSearchTransport();
        transport.EnqueueJson("""
            { "hits": { "hits": [] } }
            """);
        transport.EnqueueJson("""
            { "hits": { "hits": [] } }
            """);
        transport.EnqueueJson("""
            { "hits": { "hits": [] } }
            """);
        transport.EnqueueJson("""
            { "hits": { "hits": [] } }
            """);
        var projection = CreateProjection(transport);
        using var inValuesDocument = JsonDocument.Parse("[\"approved\",\"pending\"]");
        using var prefixValueDocument = JsonDocument.Parse("\"appro\"");

        await projection.SearchAsync(CreatePolicy(), new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "embedding", Value = new[] { 0.1f, 0.2f, 0.3f }, Top = 1 },
            Filter = FilterNode.Leaf("/metadata/status", "IN", inValuesDocument.RootElement.Clone())
        });
        await projection.SearchAsync(CreatePolicy(), new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "embedding", Value = new[] { 0.1f, 0.2f, 0.3f }, Top = 1 },
            Filter = FilterNode.Leaf("/metadata/status", FilterOps.Exists, false)
        });
        await projection.SearchAsync(CreatePolicy(), new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "embedding", Value = new[] { 0.1f, 0.2f, 0.3f }, Top = 1 },
            Filter = new FilterNode { Path = "/metadata/status", Value = "approved" }
        });
        await projection.SearchAsync(CreatePolicy(), new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "embedding", Value = new[] { 0.1f, 0.2f, 0.3f }, Top = 1 },
            Filter = FilterNode.Leaf("/metadata/status", FilterOps.StartsWith, prefixValueDocument.RootElement.Clone())
        });

        Assert.Contains("\"terms\"", transport.Requests[0].JsonBody, StringComparison.Ordinal);
        Assert.Contains("s:approved", transport.Requests[0].JsonBody, StringComparison.Ordinal);
        Assert.Contains("s:pending", transport.Requests[0].JsonBody, StringComparison.Ordinal);
        Assert.Contains("\"must_not\"", transport.Requests[1].JsonBody, StringComparison.Ordinal);
        Assert.Contains("\"exists\"", transport.Requests[1].JsonBody, StringComparison.Ordinal);
        Assert.Contains("\"term\"", transport.Requests[2].JsonBody, StringComparison.Ordinal);
        Assert.Contains("\"wildcard\"", transport.Requests[3].JsonBody, StringComparison.Ordinal);
        Assert.Contains("s:appro*", transport.Requests[3].JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamoDbStreamConsumer_UsesNewAndOldImagesAndLeavesOrderingToRevisionFence()
    {
        var projection = new CapturingProjection();
        var consumer = new DynamoDbStreamsRecordProjectionConsumer(
            projection,
            arn => arn.EndsWith("/records", StringComparison.Ordinal) ? "evidence" : null);
        var upsert = CreateRecord(revision: 7);
        var deleted = CreateRecord(id: "deleted", revision: 2);
        var json = JsonSerializer.Serialize(new
        {
            Records = new object[]
            {
                new
                {
                    eventName = "MODIFY",
                    eventSourceARN = "arn:aws:dynamodb:us-east-1:123:table/records",
                    dynamodb = new { SequenceNumber = "100", NewImage = new { doc = new { S = JsonSerializer.Serialize(upsert) } } }
                },
                new
                {
                    eventName = "REMOVE",
                    eventSourceARN = "arn:aws:dynamodb:us-east-1:123:table/records",
                    dynamodb = new { SequenceNumber = "101", OldImage = new { doc = new { S = JsonSerializer.Serialize(deleted) } } }
                }
            }
        });

        await consumer.ProcessAsync(json);

        Assert.Collection(projection.Changes,
            change =>
            {
                Assert.Equal(RecordSearchProjectionOperations.Upsert, change.Operation);
                Assert.Equal(7, change.Revision);
                Assert.Equal("100", change.SourceSequence);
            },
            change =>
            {
                Assert.Equal(RecordSearchProjectionOperations.Delete, change.Operation);
                Assert.Equal(2, change.Revision);
                Assert.Equal("101", change.SourceSequence);
            });
    }

    [Fact]
    public async Task Projection_RefusesToCopyRecordsUntilTheCanonicalPolicyIsAvailable()
    {
        var transport = new CapturingOpenSearchTransport();
        var projection = CreateProjection(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            projection.ProjectAsync(RecordSearchProjectionChange.Upsert("evidence", CreateRecord(revision: 1))));

        Assert.Contains("canonical collection policy", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Projection_ResolvesCanonicalPolicyForAStatelessStreamWorker()
    {
        var transport = new CapturingOpenSearchTransport();
        var policy = CreatePolicy();
        var canonical = new ProjectionCanonicalStore { Policy = policy };
        var projection = new OpenSearchRecordSearchProjection(
            transport,
            new OpenSearchRecordSearchProjectionOptions(),
            canonical);

        await projection.ProjectAsync(RecordSearchProjectionChange.Upsert(policy.Name, CreateRecord(revision: 1)));

        var request = Assert.Single(transport.Requests);
        Assert.Contains("version=1", request.PathAndQuery);
        Assert.DoesNotContain("metadata-secret", request.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_UsesPolicyAwareIndexNamesAcrossProvisioningWritesSearchAndDeletion()
    {
        var transport = new CapturingOpenSearchTransport();
        var policy = CreatePolicy();
        var projection = new OpenSearchRecordSearchProjection(
            transport,
            new OpenSearchRecordSearchProjectionOptions
            {
                PolicyIndexNameFactory = configured => $"vyral-{configured.Name}-v{configured.VectorPolicies[0].Dimensions}"
            },
            new ProjectionCanonicalStore { Policy = policy });

        await projection.EnsureCollectionAsync(policy);
        await projection.ProjectAsync(RecordSearchProjectionChange.Upsert(policy.Name, CreateRecord(revision: 1)));
        transport.EnqueueJson("""
            { "hits": { "hits": [] } }
            """);
        await projection.SearchAsync(policy, new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "embedding", Value = new[] { 0.1f, 0.2f, 0.3f }, Top = 1 }
        });
        await projection.DeleteCollectionAsync(policy);

        Assert.All(transport.Requests, request => Assert.StartsWith("/vyral-evidence-v3", request.PathAndQuery, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Projection_DefaultIndexNameStaysWithinOpenSearchLimitForAValidLongCollectionName()
    {
        var transport = new CapturingOpenSearchTransport();
        var projection = CreateProjection(transport);
        var policy = CreatePolicy();
        policy.Name = new string('a', RecordIdentityValidator.MaxCollectionNameLength);

        await projection.EnsureCollectionAsync(policy);

        var index = Assert.Single(transport.Requests).PathAndQuery.Trim('/');
        Assert.True(index.Length <= 255);
        Assert.StartsWith("vyral-", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_DefaultIndexNameCreatesANewGenerationForAMappingChange()
    {
        var transport = new CapturingOpenSearchTransport();
        var projection = CreateProjection(transport);
        var original = CreatePolicy();
        var equivalentReordered = CreatePolicy();
        equivalentReordered.IndexedMetadata.Reverse();
        var changed = CreatePolicy();
        changed.IndexedMetadata.Add("/metadata/category");

        await projection.EnsureCollectionAsync(original);
        await projection.EnsureCollectionAsync(equivalentReordered);
        await projection.EnsureCollectionAsync(changed);

        var originalIndex = transport.Requests[0].PathAndQuery.Trim('/');
        var reorderedIndex = transport.Requests[1].PathAndQuery.Trim('/');
        var changedIndex = transport.Requests[2].PathAndQuery.Trim('/');
        Assert.Equal(originalIndex, reorderedIndex);
        Assert.NotEqual(originalIndex, changedIndex);
    }

    [Fact]
    public async Task Projection_DefaultIndexNameRequiresPolicyForADeletionAfterWorkerRestart()
    {
        var projection = CreateProjection(new CapturingOpenSearchTransport());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            projection.ProjectAsync(RecordSearchProjectionChange.Delete("evidence", "tenant-a", "removed", 1)));

        Assert.Contains("requires the canonical collection policy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_PolicyAwareIndexNameRequiresPolicyForADeletionAfterWorkerRestart()
    {
        var projection = new OpenSearchRecordSearchProjection(
            new CapturingOpenSearchTransport(),
            new OpenSearchRecordSearchProjectionOptions
            {
                PolicyIndexNameFactory = policy => "vyral-" + policy.Name
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            projection.ProjectAsync(RecordSearchProjectionChange.Delete("evidence", "tenant-a", "removed", 1)));

        Assert.Contains("requires the canonical collection policy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AwsSigV4Transport_SignsRelativeDataPlaneRequestsWithoutLeakingCredentials()
    {
        var handler = new CaptureHttpHandler();
        using var client = new HttpClient(handler);
        using var transport = new AwsSigV4OpenSearchTransport(
            new Uri("https://search.example.us-east-1.es.amazonaws.com"),
            "us-east-1",
            new BasicAWSCredentials("AKIDEXAMPLE", "secret-not-in-header"),
            httpClient: client,
            utcNow: () => new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        await transport.SendAsync(HttpMethod.Put, "/vyral-test/_doc/a?version=2&version_type=external_gte", "{\"safe\":true}");

        Assert.Equal("https://search.example.us-east-1.es.amazonaws.com/vyral-test/_doc/a?version=2&version_type=external_gte", handler.RequestUri);
        Assert.Contains("Credential=AKIDEXAMPLE/20250102/us-east-1/es/aws4_request", handler.Authorization);
        Assert.DoesNotContain("secret-not-in-header", handler.Authorization, StringComparison.Ordinal);
        Assert.Equal("{\"safe\":true}", handler.Body);
        Assert.Equal("20250102T030405Z", handler.AmzDate);
    }

    [Fact]
    public async Task AwsSigV4Transport_RejectsUnsafeEndpointsAndEscapingRequestPaths()
    {
        var credentials = new BasicAWSCredentials("AKIDEXAMPLE", "secret-not-in-header");

        Assert.Throws<ArgumentException>(() => new AwsSigV4OpenSearchTransport(
            new Uri("http://search.example.com"), "us-east-1", credentials));
        Assert.Throws<ArgumentException>(() => new AwsSigV4OpenSearchTransport(
            new Uri("https://user:password@search.example.com"), "us-east-1", credentials));
        Assert.Throws<ArgumentException>(() => new AwsSigV4OpenSearchTransport(
            new Uri("https://search.example.com/prefixed"), "us-east-1", credentials));

        using var transport = new AwsSigV4OpenSearchTransport(
            new Uri("https://search.example.com"), "us-east-1", credentials);
        await Assert.ThrowsAsync<ArgumentException>(() => transport.SendAsync(HttpMethod.Get, "//other.example.com/_search", null));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.SendAsync(HttpMethod.Get, "/index/_search#fragment", null));
    }

    [Fact]
    public async Task Projection_FailureDoesNotExposeProviderResponseBody()
    {
        var transport = new CapturingOpenSearchTransport();
        transport.Enqueue(503, "provider diagnostics contain private-document-identifier");
        var projection = CreateProjection(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => projection.SearchAsync(CreatePolicy(), new QueryEnvelope
        {
            Vector = new VectorSearchOptions { Field = "embedding", Value = new[] { 0.1f, 0.2f, 0.3f }, Top = 1 }
        }));

        Assert.Contains("HTTP 503", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-document-identifier", exception.Message, StringComparison.Ordinal);
    }

    private static OpenSearchRecordSearchProjection CreateProjection(CapturingOpenSearchTransport transport) => new(
        transport,
        new OpenSearchRecordSearchProjectionOptions { MaximumCandidates = 100 });

    private static RecordCollectionPolicy CreatePolicy() => new()
    {
        Name = "evidence",
        VectorPolicies = new List<VectorFieldPolicy>
        {
            new()
            {
                Name = "embedding",
                Path = "/vectors/embedding/values",
                Dimensions = 3,
                DistanceFunction = DistanceFunctions.Cosine,
                IndexType = IndexTypes.DiskAnn
            }
        },
        IndexedMetadata = new List<string> { "/metadata/status", "/metadata/tenantId" }
    };

    private static VyralRecord CreateRecord(string id = "current", int revision = 1) => new()
    {
        Id = id,
        PartitionKey = "tenant-a",
        Type = "claim",
        Revision = revision,
        Metadata = new JsonObject
        {
            ["status"] = "approved",
            ["tenantId"] = "tenant-a",
            ["unindexedSecret"] = "metadata-secret"
        },
        Content = new JsonObject { ["text"] = "very-sensitive-content" },
        Vectors = new Dictionary<string, VyralVector>
        {
            ["embedding"] = new() { Values = new[] { 0.1f, 0.2f, 0.3f }, Dimensions = 3 }
        }
    };

    private sealed class CapturingOpenSearchTransport : IOpenSearchTransport
    {
        private readonly Queue<OpenSearchTransportResponse> _responses = new();
        public List<(HttpMethod Method, string PathAndQuery, string JsonBody)> Requests { get; } = new();

        public void EnqueueJson(string json) => Enqueue(200, json);

        public void Enqueue(int statusCode, string body) =>
            _responses.Enqueue(new OpenSearchTransportResponse { StatusCode = statusCode, Body = body });

        public Task<OpenSearchTransportResponse> SendAsync(HttpMethod method, string pathAndQuery, string? jsonBody, CancellationToken ct = default)
        {
            Requests.Add((method, pathAndQuery, jsonBody ?? string.Empty));
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new OpenSearchTransportResponse { StatusCode = 200, Body = "{}" });
        }
    }

    private sealed class CapturingProjection : IRecordSearchProjection
    {
        public List<RecordSearchProjectionChange> Changes { get; } = new();
        public Task ProjectAsync(RecordSearchProjectionChange change, CancellationToken ct = default)
        {
            Changes.Add(change);
            return Task.CompletedTask;
        }
        public Task<RecordSearchProjectionResult> SearchAsync(RecordCollectionPolicy policy, QueryEnvelope query, CancellationToken ct = default) =>
            Task.FromResult(new RecordSearchProjectionResult());
    }

    private sealed class ProjectionCanonicalStore : IRecordCollectionStore
    {
        private readonly Dictionary<(string PartitionKey, string Id), VyralRecord> _records;
        public ProjectionCanonicalStore(params VyralRecord[] records) =>
            _records = records.ToDictionary(record => (record.PartitionKey, record.Id));
        public RecordCollectionPolicy? Policy { get; init; }
        public Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) =>
            Task.FromResult(_records.GetValueOrDefault((partitionKey, id)));
        public Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult(Policy is not null && string.Equals(Policy.Name, collection, StringComparison.Ordinal) ? Policy : null);
        public Task DeleteCollectionAsync(string collection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CaptureHttpHandler : HttpMessageHandler
    {
        public string RequestUri { get; private set; } = string.Empty;
        public string Authorization { get; private set; } = string.Empty;
        public string AmzDate { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.ToString();
            Authorization = request.Headers.GetValues("Authorization").Single();
            AmzDate = request.Headers.GetValues("x-amz-date").Single();
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
