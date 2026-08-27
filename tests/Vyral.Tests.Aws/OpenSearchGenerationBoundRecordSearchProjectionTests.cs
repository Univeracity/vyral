using System.Security.Cryptography;
using System.Text;
using Vyral.Abstractions.Models;
using Vyral.Aws;

namespace Vyral.Tests.Aws;

public sealed class OpenSearchGenerationBoundRecordSearchProjectionTests
{
    private const string IndexName = "vyral-evidence-generation-a";
    private const string IndexUuid = "uuid-generation-a";

    [Fact]
    public async Task Search_UsesTheDescriptorBoundExactReadOnlyIndexAndReturnsCompleteCandidates()
    {
        var transport = new QueueTransport();
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: true);
        transport.EnqueueSearch(IndexName, totalShards: 2, successfulShards: 2, failedShards: 0);
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: true);
        var projection = CreateProjection(transport);
        var request = CreateRequest();

        var result = await projection.SearchGenerationAsync(CreatePolicy(), request);

        Assert.Equal(RecordSearchProjectionResultStatuses.Succeeded, result.Status);
        Assert.Equal("generation-a", result.GenerationId);
        Assert.Single(result.Items);
        Assert.Equal("record-a", result.Items[0].Id);
        Assert.Equal(RecordSearchProjectionCoverageStatuses.Complete, result.Coverage.Status);
        Assert.Collection(
            transport.Requests,
            settings => Assert.Equal($"/{IndexName}/_settings?flat_settings=true", settings.PathAndQuery),
            search =>
            {
                Assert.Equal($"/{IndexName}/_search?allow_partial_search_results=false", search.PathAndQuery);
                Assert.DoesNotContain("alias", search.PathAndQuery, StringComparison.OrdinalIgnoreCase);
            },
            finalSettings => Assert.Equal($"/{IndexName}/_settings?flat_settings=true", finalSettings.PathAndQuery));
    }

    [Theory]
    [InlineData(2, 1, 1, false)]
    [InlineData(2, 2, 0, true)]
    public async Task Search_FailsClosedOnPartialOrTimedOutResponses(
        int totalShards,
        int successfulShards,
        int failedShards,
        bool timedOut)
    {
        var transport = new QueueTransport();
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: true);
        transport.EnqueueSearch(IndexName, totalShards, successfulShards, failedShards, timedOut);
        var projection = CreateProjection(transport);

        var result = await projection.SearchGenerationAsync(CreatePolicy(), CreateRequest());

        Assert.Equal(RecordSearchProjectionResultStatuses.Failed, result.Status);
        Assert.Equal(RecordSearchProjectionFailureCodes.CoverageIncomplete, result.Failure!.Code);
        Assert.Empty(result.Items);
        Assert.Null(result.ContinuationToken);
        Assert.Equal(new[] { "tenant-a" }, result.Coverage.MissingPartitions);
    }

    [Fact]
    public async Task Search_FailsClosedWhenTheRemoteIndexUuidDoesNotMatchTheDescriptorBinding()
    {
        var transport = new QueueTransport();
        transport.EnqueueSettings(IndexName, "substituted-index-uuid", readOnly: true);
        var projection = CreateProjection(transport);

        var result = await projection.SearchGenerationAsync(CreatePolicy(), CreateRequest());

        Assert.Equal(RecordSearchProjectionResultStatuses.Failed, result.Status);
        Assert.Equal(RecordSearchProjectionFailureCodes.CoverageIncomplete, result.Failure!.Code);
        Assert.Empty(result.Items);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Search_FailsClosedWhenTheGenerationIndexIsStillWritable()
    {
        var transport = new QueueTransport();
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: false);
        var projection = CreateProjection(transport);

        var result = await projection.SearchGenerationAsync(CreatePolicy(), CreateRequest());

        Assert.Equal(RecordSearchProjectionResultStatuses.Failed, result.Status);
        Assert.Equal(RecordSearchProjectionFailureCodes.CoverageIncomplete, result.Failure!.Code);
        Assert.Empty(result.Items);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Search_ConvertsProviderTransportFailureIntoAnEmptyRetryableResult()
    {
        var projection = CreateProjection(new ThrowingTransport());

        var result = await projection.SearchGenerationAsync(CreatePolicy(), CreateRequest());

        Assert.Equal(RecordSearchProjectionResultStatuses.Failed, result.Status);
        Assert.Equal(RecordSearchProjectionFailureCodes.CoverageIncomplete, result.Failure!.Code);
        Assert.True(result.Failure.Retryable);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Search_FailsClosedWhenAHitComesFromOutsideTheSelectedIndex()
    {
        var transport = new QueueTransport();
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: true);
        transport.EnqueueSearch("vyral-evidence-other", totalShards: 1, successfulShards: 1, failedShards: 0);
        var projection = CreateProjection(transport);

        var result = await projection.SearchGenerationAsync(CreatePolicy(), CreateRequest());

        Assert.Equal(RecordSearchProjectionResultStatuses.Failed, result.Status);
        Assert.Equal(RecordSearchProjectionFailureCodes.CoverageIncomplete, result.Failure!.Code);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Search_FailsClosedWhenAHitEscapesTheRequestedLogicalPartition()
    {
        var transport = new QueueTransport();
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: true);
        transport.EnqueueSearch(
            IndexName,
            totalShards: 1,
            successfulShards: 1,
            failedShards: 0,
            hitPartition: "tenant-b");
        var projection = CreateProjection(transport);

        var result = await projection.SearchGenerationAsync(CreatePolicy(), CreateRequest());

        Assert.Equal(RecordSearchProjectionFailureCodes.CoverageIncomplete, result.Failure!.Code);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Search_FailsClosedIfTheIndexBecomesWritableBeforeSearchCompletion()
    {
        var transport = new QueueTransport();
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: true);
        transport.EnqueueSearch(IndexName, totalShards: 1, successfulShards: 1, failedShards: 0);
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: false);
        var projection = CreateProjection(transport);

        var result = await projection.SearchGenerationAsync(CreatePolicy(), CreateRequest());

        Assert.Equal(RecordSearchProjectionResultStatuses.Failed, result.Status);
        Assert.Equal(RecordSearchProjectionFailureCodes.CoverageIncomplete, result.Failure!.Code);
        Assert.True(result.Failure.Retryable);
        Assert.Empty(result.Items);
        Assert.Null(result.ContinuationToken);
    }

    [Fact]
    public async Task Search_FailsWhenTheDeadlineElapsesEvenIfTheTransportIgnoresCancellation()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var transport = new QueueTransport
        {
            OnRequest = requestNumber =>
            {
                if (requestNumber == 2)
                {
                    clock.UtcNow = clock.UtcNow.AddMinutes(1);
                }
            }
        };
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: true);
        transport.EnqueueSearch(IndexName, totalShards: 1, successfulShards: 1, failedShards: 0);
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: true);
        var projection = CreateProjection(transport, timeProvider: clock);
        var request = CreateRequest();
        request.DeadlineUtc = clock.UtcNow.AddSeconds(30).UtcDateTime;

        var result = await projection.SearchGenerationAsync(CreatePolicy(), request);

        Assert.Equal(RecordSearchProjectionResultStatuses.Failed, result.Status);
        Assert.Equal(RecordSearchProjectionFailureCodes.DeadlineExceeded, result.Failure!.Code);
        Assert.Empty(result.Items);
        Assert.Null(result.ContinuationToken);
    }

    [Fact]
    public void RegistrationRequiresVectorCapabilityInAdditionToPortableSafetyCapabilities()
    {
        var descriptor = CreateDescriptor();
        descriptor.Capabilities.Remove(RecordSearchProjectionGenerationCapabilities.Vector);
        RecordSearchProjectionGenerationContract.SealDescriptor(descriptor);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new OpenSearchGenerationBoundRecordSearchProjection(
                new QueueTransport(),
                new OpenSearchRecordSearchProjectionOptions { MaximumCandidates = 100 },
                [
                    new OpenSearchRecordSearchProjectionGeneration
                    {
                        Descriptor = descriptor,
                        IndexName = IndexName,
                        IndexUuid = IndexUuid,
                        State = RecordSearchProjectionGenerationStates.Active,
                        AvailablePartitions = ["tenant-a"]
                    }
                ]));

        Assert.Contains("vector capability", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_RejectsDescriptorSubstitutionBeforeCallingOpenSearch()
    {
        var transport = new QueueTransport();
        var projection = CreateProjection(transport);
        var request = CreateRequest();
        request.ExpectedDescriptorDigest = Hash("substituted-descriptor");

        var result = await projection.SearchGenerationAsync(CreatePolicy(), request);

        Assert.Equal(RecordSearchProjectionFailureCodes.GenerationDescriptorMismatch, result.Failure!.Code);
        Assert.Empty(result.Items);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Search_RejectsCollectionPolicyDriftBeforeCallingOpenSearch()
    {
        var transport = new QueueTransport();
        var projection = CreateProjection(transport);
        var changedPolicy = CreatePolicy();
        changedPolicy.IndexedMetadata.Add("/metadata/new-field");

        var result = await projection.SearchGenerationAsync(changedPolicy, CreateRequest());

        Assert.Equal(RecordSearchProjectionFailureCodes.GenerationDescriptorMismatch, result.Failure!.Code);
        Assert.Empty(result.Items);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Search_RejectsIncompleteLogicalCoverageBeforeCallingOpenSearch()
    {
        var transport = new QueueTransport();
        var projection = CreateProjection(transport, availablePartitions: Array.Empty<string>(), state: RecordSearchProjectionGenerationStates.Retained);

        var result = await projection.SearchGenerationAsync(CreatePolicy(), CreateRequest());

        Assert.Equal(RecordSearchProjectionFailureCodes.CoverageIncomplete, result.Failure!.Code);
        Assert.Equal(new[] { "tenant-a" }, result.Coverage.MissingPartitions);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Search_DoesNotAcceptAContinuationForTheNonPageableVectorShape()
    {
        var transport = new QueueTransport();
        var projection = CreateProjection(transport);
        var request = CreateRequest();
        request.Query.ContinuationToken = "opaque-provider-token";

        var result = await projection.SearchGenerationAsync(CreatePolicy(), request);

        Assert.Equal(RecordSearchProjectionFailureCodes.InvalidContinuation, result.Failure!.Code);
        Assert.Empty(result.Items);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Inspection_RequiresTheSameImmutableRemoteBinding()
    {
        var transport = new QueueTransport();
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: true);
        transport.EnqueueSettings(IndexName, IndexUuid, readOnly: false);
        var projection = CreateProjection(transport);

        var complete = await projection.InspectGenerationAsync(CreatePolicy(), "generation-a");
        var incomplete = await projection.InspectGenerationAsync(CreatePolicy(), "generation-a");

        Assert.Equal(RecordSearchProjectionCoverageStatuses.Complete, complete!.CoverageStatus);
        Assert.Equal(new[] { "tenant-a" }, complete.AvailablePartitions);
        Assert.Equal(RecordSearchProjectionCoverageStatuses.Incomplete, incomplete!.CoverageStatus);
        Assert.Empty(incomplete.AvailablePartitions);
    }

    private static OpenSearchGenerationBoundRecordSearchProjection CreateProjection(
        IOpenSearchTransport transport,
        IEnumerable<string>? availablePartitions = null,
        string state = RecordSearchProjectionGenerationStates.Active,
        TimeProvider? timeProvider = null)
    {
        var descriptor = CreateDescriptor();
        return new OpenSearchGenerationBoundRecordSearchProjection(
            transport,
            new OpenSearchRecordSearchProjectionOptions { MaximumCandidates = 100 },
            new[]
            {
                new OpenSearchRecordSearchProjectionGeneration
                {
                    Descriptor = descriptor,
                    IndexName = IndexName,
                    IndexUuid = IndexUuid,
                    State = state,
                    AvailablePartitions = availablePartitions?.ToList()
                }
            },
            new OpenSearchGenerationBoundRecordSearchProjectionOptions
            {
                TimeProvider = timeProvider ?? TimeProvider.System
            });
    }

    private static RecordSearchProjectionGenerationDescriptor CreateDescriptor()
    {
        var descriptor = new RecordSearchProjectionGenerationDescriptor
        {
            Collection = "evidence",
            GenerationId = "generation-a",
            ProviderId = OpenSearchGenerationBoundRecordSearchProjectionOptions.DefaultProviderId,
            ProfileId = "vector-v1",
            StrategyVersion = "1",
            SourceManifestDigest = Hash("source"),
            RecordRevisionSetDigest = Hash("revisions"),
            ProjectionSchemaDigest = OpenSearchProjectionGenerationBinding.ComputeProjectionSchemaDigest(CreatePolicy()),
            AnalyzerDigest = Hash("analyzer"),
            ConfigurationDigest = Hash("configuration"),
            ExpectedItemCount = 1,
            ExpectedPartitions = new List<string> { "tenant-a" },
            Capabilities = new List<string>
            {
                RecordSearchProjectionGenerationCapabilities.CompleteCoverage,
                RecordSearchProjectionGenerationCapabilities.GenerationPinnedContinuation,
                RecordSearchProjectionGenerationCapabilities.Vector
            },
            Artifacts = new List<RecordSearchProjectionGenerationArtifact>
            {
                new()
                {
                    Id = OpenSearchProjectionGenerationBinding.ArtifactId,
                    Kind = OpenSearchProjectionGenerationBinding.ArtifactKind,
                    ContentHash = OpenSearchProjectionGenerationBinding.ComputeContentHash(IndexName, IndexUuid),
                    SizeBytes = 0
                }
            },
            CreatedAtUtc = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc)
        };
        RecordSearchProjectionGenerationContract.SealDescriptor(descriptor);
        return descriptor;
    }

    private static GenerationBoundRecordSearchProjectionRequest CreateRequest()
    {
        var descriptor = CreateDescriptor();
        return new GenerationBoundRecordSearchProjectionRequest
        {
            GenerationId = descriptor.GenerationId,
            ExpectedDescriptorDigest = descriptor.DescriptorDigest,
            Query = new QueryEnvelope
            {
                PartitionKeys = new List<string> { "tenant-a" },
                Vector = new VectorSearchOptions
                {
                    Field = "embedding",
                    Value = new[] { 0.1f, 0.2f, 0.3f },
                    Top = 10
                },
                Limit = 10
            }
        };
    }

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
        }
    };

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class QueueTransport : IOpenSearchTransport
    {
        private readonly Queue<OpenSearchTransportResponse> _responses = new();
        public List<(HttpMethod Method, string PathAndQuery, string JsonBody)> Requests { get; } = new();
        public Action<int>? OnRequest { get; init; }

        public void EnqueueSettings(string indexName, string indexUuid, bool readOnly) =>
            _responses.Enqueue(new OpenSearchTransportResponse
            {
                StatusCode = 200,
                Body = $$"""
                    {
                      "{{indexName}}": {
                        "settings": {
                          "index.uuid": "{{indexUuid}}",
                          "index.blocks.read_only": "{{readOnly.ToString().ToLowerInvariant()}}"
                        }
                      }
                    }
                    """
            });

        public void EnqueueSearch(
            string hitIndex,
            int totalShards,
            int successfulShards,
            int failedShards,
            bool timedOut = false,
            string hitPartition = "tenant-a") =>
            _responses.Enqueue(new OpenSearchTransportResponse
            {
                StatusCode = 200,
                Body = $$"""
                    {
                      "timed_out": {{timedOut.ToString().ToLowerInvariant()}},
                      "_shards": {
                        "total": {{totalShards}},
                        "successful": {{successfulShards}},
                        "skipped": 0,
                        "failed": {{failedShards}}
                      },
                      "hits": {
                        "hits": [
                          {
                            "_index": "{{hitIndex}}",
                            "_score": 0.9,
                            "_source": {
                              "partitionKey": "{{hitPartition}}",
                              "id": "record-a",
                              "revision": 4
                            }
                          }
                        ]
                      }
                    }
                    """
            });

        public Task<OpenSearchTransportResponse> SendAsync(
            HttpMethod method,
            string pathAndQuery,
            string? jsonBody,
            CancellationToken ct = default)
        {
            Requests.Add((method, pathAndQuery, jsonBody ?? string.Empty));
            OnRequest?.Invoke(Requests.Count);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class ThrowingTransport : IOpenSearchTransport
    {
        public Task<OpenSearchTransportResponse> SendAsync(
            HttpMethod method,
            string pathAndQuery,
            string? jsonBody,
            CancellationToken ct = default) =>
            throw new HttpRequestException("provider endpoint unavailable");
    }
}
