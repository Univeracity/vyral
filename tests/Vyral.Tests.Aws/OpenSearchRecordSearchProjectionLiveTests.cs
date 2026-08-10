using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime.Credentials;
using Vyral.Abstractions.Models;
using Vyral.Aws;

namespace Vyral.Tests.Aws;

/// <summary>
/// Opt-in data-plane qualification for a caller-provisioned, disposable OpenSearch resource. The
/// test owns only its uniquely named derived index and always attempts to remove it. It is not a
/// domain-provisioning test: VPC placement, encryption, IAM role attachment, and cost ownership
/// remain explicit deployment-owner decisions.
/// </summary>
public sealed class OpenSearchRecordSearchProjectionLiveTests
{
    [AwsOpenSearchLiveFact]
    public async Task Projection_IndexesSearchesRevisionFencesAndDeletesAgainstOpenSearch()
    {
        var policy = OpenSearchRecordSearchProjectionQualification.CreatePolicy(
            AwsLiveSettings.UniquePrefix("vyral-it-opensearch"));
        var endpoint = new Uri(AwsLiveSettings.OpenSearchEndpoint!, UriKind.Absolute);

        using var transport = new AwsSigV4OpenSearchTransport(
            endpoint,
            AwsLiveSettings.AwsRegion!,
            DefaultAWSCredentialsIdentityResolver.GetCredentials(new AmazonDynamoDBConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(AwsLiveSettings.AwsRegion!)
            }),
            AwsLiveSettings.OpenSearchSigningService);
        var projection = new OpenSearchRecordSearchProjection(
            transport,
            new OpenSearchRecordSearchProjectionOptions { MaximumCandidates = 10 });

        await OpenSearchRecordSearchProjectionQualification.RunAsync(projection, policy);
    }
}

internal static class OpenSearchRecordSearchProjectionQualification
{
    public static RecordCollectionPolicy CreatePolicy(string name) => new()
    {
        Name = name,
        VectorPolicies =
        [
            new VectorFieldPolicy
            {
                Name = "embedding",
                Path = "/vectors/embedding/values",
                Dimensions = 3,
                DistanceFunction = DistanceFunctions.Cosine,
                IndexType = IndexTypes.DiskAnn
            }
        ],
        IndexedMetadata = ["/metadata/status"]
    };

    public static async Task RunAsync(
        OpenSearchRecordSearchProjection projection,
        RecordCollectionPolicy policy)
    {
        var record = CreateRecord(revision: 1);

        try
        {
            await projection.EnsureCollectionAsync(policy);
            await projection.ProjectAsync(RecordSearchProjectionChange.Upsert(policy.Name, record));

            record.Revision = 2;
            record.Etag = "rev:2";
            record.Metadata!["status"] = "current";
            await projection.ProjectAsync(RecordSearchProjectionChange.Upsert(policy.Name, record));

            // An out-of-order stream delivery must not overwrite the newer document.
            await projection.ProjectAsync(RecordSearchProjectionChange.Upsert(policy.Name, CreateRecord(revision: 1)));

            var current = await EventuallySearchAsync(projection, policy, "current", expectAny: true);
            var candidate = Assert.Single(current.Items);
            Assert.Equal("record-a", candidate.Id);
            Assert.Equal(2, candidate.Revision);

            await projection.ProjectAsync(RecordSearchProjectionChange.Delete(policy.Name, "tenant-a", "record-a", 3));
            var deleted = await EventuallySearchAsync(projection, policy, "current", expectAny: false);
            Assert.Empty(deleted.Items);
        }
        finally
        {
            await projection.DeleteCollectionAsync(policy);
        }
    }

    private static async Task<RecordSearchProjectionResult> EventuallySearchAsync(
        OpenSearchRecordSearchProjection projection,
        RecordCollectionPolicy policy,
        string status,
        bool expectAny)
    {
        var stopwatch = Stopwatch.StartNew();
        RecordSearchProjectionResult result = new();
        do
        {
            result = await projection.SearchAsync(policy, new QueryEnvelope
            {
                Vector = new VectorSearchOptions { Field = "embedding", Value = [0.1f, 0.2f, 0.3f], Top = 1 },
                Filter = FilterNode.Eq("/metadata/status", status)
            });
            if (result.Items.Count > 0 == expectAny) return result;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30));

        return result;
    }

    private static VyralRecord CreateRecord(int revision) => new()
    {
        Id = "record-a",
        PartitionKey = "tenant-a",
        Type = "record",
        Revision = revision,
        Etag = $"rev:{revision}",
        Metadata = new JsonObject { ["status"] = "current" },
        Content = new JsonObject { ["text"] = "must-not-be-projected" },
        Vectors = new Dictionary<string, VyralVector>
        {
            ["embedding"] = new() { Values = [0.1f, 0.2f, 0.3f], Dimensions = 3 }
        }
    };
}
