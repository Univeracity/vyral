using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Amazon.Runtime;
using Vyral.Abstractions.Models;
using Vyral.Aws;

namespace Vyral.Tests.Aws;

/// <summary>
/// Credential-free OpenSearch data-plane qualification. The local process has its security plugin
/// disabled; the deliberately non-secret placeholder credentials exercise request construction but
/// do not validate AWS authorization.
/// </summary>
public sealed class OpenSearchRecordSearchProjectionLocalTests
{
    [OpenSearchLocalFact]
    public async Task Projection_IndexesSearchesRevisionFencesAndDeletesAgainstLocalOpenSearch()
    {
        var endpoint = OpenSearchLocalSettings.GetEndpoint();
        var policy = OpenSearchRecordSearchProjectionQualification.CreatePolicy(
            AwsLiveSettings.UniquePrefix("vyral-it-opensearch-local"));

        using var transport = new AwsSigV4OpenSearchTransport(
            endpoint,
            region: "us-east-1",
            credentials: new BasicAWSCredentials("local", "local"));
        var projection = new OpenSearchRecordSearchProjection(
            transport,
            new OpenSearchRecordSearchProjectionOptions { MaximumCandidates = 10 });

        await OpenSearchRecordSearchProjectionQualification.RunAsync(projection, policy);
    }

    [OpenSearchLocalFact]
    public async Task GenerationBoundProjection_BindsAnImmutableIndexAndFailsClosedWhenItBecomesWritable()
    {
        var endpoint = OpenSearchLocalSettings.GetEndpoint();
        var policy = OpenSearchRecordSearchProjectionQualification.CreatePolicy(
            AwsLiveSettings.UniquePrefix("vyral-it-opensearch-generation"));
        var indexName = policy.Name;
        var projectionOptions = new OpenSearchRecordSearchProjectionOptions
        {
            PolicyIndexNameFactory = _ => indexName,
            MaximumCandidates = 10
        };

        using var transport = new AwsSigV4OpenSearchTransport(
            endpoint,
            region: "us-east-1",
            credentials: new BasicAWSCredentials("local", "local"));
        var writer = new OpenSearchRecordSearchProjection(transport, projectionOptions);
        var created = false;

        try
        {
            await writer.EnsureCollectionAsync(policy);
            created = true;
            await writer.ProjectAsync(RecordSearchProjectionChange.Upsert(policy.Name, CreateRecord()));
            RequireSuccess(await transport.SendAsync(HttpMethod.Post, $"/{indexName}/_refresh", null));

            var indexUuid = await ReadIndexUuidAsync(transport, indexName);
            await SetReadOnlyAsync(transport, indexName, readOnly: true);
            var descriptor = CreateDescriptor(policy, indexName, indexUuid);
            var projection = new OpenSearchGenerationBoundRecordSearchProjection(
                transport,
                projectionOptions,
                [
                    new OpenSearchRecordSearchProjectionGeneration
                    {
                        Descriptor = descriptor,
                        IndexName = indexName,
                        IndexUuid = indexUuid,
                        State = RecordSearchProjectionGenerationStates.Active,
                        AvailablePartitions = ["tenant-a"]
                    }
                ]);

            var inspection = await projection.InspectGenerationAsync(policy, descriptor.GenerationId);
            Assert.NotNull(inspection);
            Assert.Equal(RecordSearchProjectionCoverageStatuses.Complete, inspection!.CoverageStatus);
            Assert.Equal(["tenant-a"], inspection.AvailablePartitions);

            var result = await projection.SearchGenerationAsync(policy, CreateRequest(descriptor));
            Assert.Equal(RecordSearchProjectionResultStatuses.Succeeded, result.Status);
            Assert.Equal(descriptor.GenerationId, result.GenerationId);
            Assert.Equal(descriptor.DescriptorDigest, result.GenerationDescriptorDigest);
            var candidate = Assert.Single(result.Items);
            Assert.Equal("tenant-a", candidate.PartitionKey);
            Assert.Equal("record-a", candidate.Id);
            Assert.Equal(1, candidate.Revision);

            var blockedRecord = CreateRecord();
            blockedRecord.Revision = 2;
            blockedRecord.Etag = "rev:2";
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                writer.ProjectAsync(RecordSearchProjectionChange.Upsert(policy.Name, blockedRecord)));

            await SetReadOnlyAsync(transport, indexName, readOnly: false);
            var writable = await projection.SearchGenerationAsync(policy, CreateRequest(descriptor));
            Assert.Equal(RecordSearchProjectionResultStatuses.Failed, writable.Status);
            Assert.Equal(RecordSearchProjectionFailureCodes.CoverageIncomplete, writable.Failure!.Code);
            Assert.Empty(writable.Items);
            Assert.Null(writable.ContinuationToken);
        }
        finally
        {
            if (created)
            {
                await SetReadOnlyAsync(transport, indexName, readOnly: false, allowMissing: true);
                var deleted = await transport.SendAsync(HttpMethod.Delete, $"/{indexName}", null);
                Assert.True(deleted.StatusCode is >= 200 and < 300 or 404);
            }
        }
    }

    private static VyralRecord CreateRecord() => new()
    {
        Id = "record-a",
        PartitionKey = "tenant-a",
        Type = "record",
        Revision = 1,
        Etag = "rev:1",
        Metadata = new JsonObject { ["status"] = "current" },
        Vectors = new Dictionary<string, VyralVector>
        {
            ["embedding"] = new() { Values = [0.1f, 0.2f, 0.3f], Dimensions = 3 }
        }
    };

    private static RecordSearchProjectionGenerationDescriptor CreateDescriptor(
        RecordCollectionPolicy policy,
        string indexName,
        string indexUuid)
    {
        var descriptor = new RecordSearchProjectionGenerationDescriptor
        {
            Collection = policy.Name,
            GenerationId = "generation-a",
            ProviderId = OpenSearchGenerationBoundRecordSearchProjectionOptions.DefaultProviderId,
            ProfileId = "vector-v1",
            StrategyVersion = "opensearch-3.8",
            SourceManifestDigest = Hash("source-manifest"),
            RecordRevisionSetDigest = Hash("record-revisions"),
            ProjectionSchemaDigest = OpenSearchProjectionGenerationBinding.ComputeProjectionSchemaDigest(policy),
            AnalyzerDigest = Hash("analyzer"),
            ConfigurationDigest = Hash("configuration"),
            ExpectedItemCount = 1,
            ExpectedPartitions = ["tenant-a"],
            Capabilities =
            [
                RecordSearchProjectionGenerationCapabilities.CompleteCoverage,
                RecordSearchProjectionGenerationCapabilities.GenerationPinnedContinuation,
                RecordSearchProjectionGenerationCapabilities.Vector
            ],
            Artifacts =
            [
                new RecordSearchProjectionGenerationArtifact
                {
                    Id = OpenSearchProjectionGenerationBinding.ArtifactId,
                    Kind = OpenSearchProjectionGenerationBinding.ArtifactKind,
                    ContentHash = OpenSearchProjectionGenerationBinding.ComputeContentHash(indexName, indexUuid),
                    SizeBytes = 0
                }
            ],
            CreatedAtUtc = DateTime.UtcNow
        };
        RecordSearchProjectionGenerationContract.SealDescriptor(descriptor);
        return descriptor;
    }

    private static GenerationBoundRecordSearchProjectionRequest CreateRequest(
        RecordSearchProjectionGenerationDescriptor descriptor) => new()
        {
            GenerationId = descriptor.GenerationId,
            ExpectedDescriptorDigest = descriptor.DescriptorDigest,
            Query = new QueryEnvelope
            {
                PartitionKeys = ["tenant-a"],
                Vector = new VectorSearchOptions
                {
                    Field = "embedding",
                    Value = [0.1f, 0.2f, 0.3f],
                    Top = 1
                },
                Limit = 1
            }
        };

    private static async Task<string> ReadIndexUuidAsync(IOpenSearchTransport transport, string indexName)
    {
        var response = await transport.SendAsync(
            HttpMethod.Get,
            $"/{indexName}/_settings?flat_settings=true",
            null);
        RequireSuccess(response);
        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.GetProperty(indexName)
            .GetProperty("settings")
            .GetProperty("index.uuid")
            .GetString()
            ?? throw new InvalidOperationException("The local OpenSearch index did not report its UUID.");
    }

    private static async Task SetReadOnlyAsync(
        IOpenSearchTransport transport,
        string indexName,
        bool readOnly,
        bool allowMissing = false)
    {
        var response = await transport.SendAsync(
            HttpMethod.Put,
            $"/{indexName}/_settings",
            new JsonObject
            {
                ["index"] = new JsonObject
                {
                    ["blocks"] = new JsonObject { ["read_only"] = readOnly }
                }
            }.ToJsonString());
        if (allowMissing && response.StatusCode == 404)
        {
            return;
        }
        RequireSuccess(response);
    }

    private static void RequireSuccess(OpenSearchTransportResponse response) =>
        Assert.True(
            response.StatusCode is >= 200 and < 300,
            $"The local OpenSearch operation failed with HTTP {response.StatusCode}.");

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
