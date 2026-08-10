using System;
using System.Threading.Tasks;
using Amazon.Runtime;
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
}
