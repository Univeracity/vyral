using System;
using Xunit;

namespace Vyral.Tests.Aws;

public static class AwsLiveSettings
{
    // DynamoDB settings
    // Set VYRAL_AWS_DYNAMODB_TABLE_PREFIX to run DynamoDB live tests.
    // Uses the default AWS credential chain (env vars, ~/.aws/credentials, IMDS).
    public static string? DynamoDbTablePrefix =>
        Environment.GetEnvironmentVariable("VYRAL_AWS_DYNAMODB_TABLE_PREFIX");

    // S3 settings
    // Set VYRAL_AWS_S3_BUCKET to run S3 live tests.
    // The bucket must already exist. Uses the default AWS credential chain.
    public static string? S3Bucket =>
        Environment.GetEnvironmentVariable("VYRAL_AWS_S3_BUCKET");

    // Execution settings. The table must be unique to this gate and the queue is temporary.
    public static string? ExecutionDynamoDbTable =>
        Environment.GetEnvironmentVariable("VYRAL_AWS_EXECUTION_DYNAMODB_TABLE");

    public static string? ExecutionSqsQueueUrl =>
        Environment.GetEnvironmentVariable("VYRAL_AWS_EXECUTION_SQS_QUEUE_URL");

    // OpenSearch projection settings. The endpoint must be a caller-provisioned, disposable
    // data-plane endpoint reachable from the test process. The live test creates and removes only
    // a unique derived index; it never creates or destroys the surrounding domain or collection.
    public static string? OpenSearchEndpoint =>
        Environment.GetEnvironmentVariable("VYRAL_AWS_OPENSEARCH_ENDPOINT");

    public static string OpenSearchSigningService =>
        Environment.GetEnvironmentVariable("VYRAL_AWS_OPENSEARCH_SIGNING_SERVICE") ?? "es";

    // Local OpenSearch qualification uses a security-disabled, loopback-only test process. It is
    // intentionally separate from the managed AWS gate: it proves projection data-plane behavior
    // but cannot prove a managed endpoint's SigV4 authorization or network policy.
    public static string? OpenSearchLocalEndpoint =>
        Environment.GetEnvironmentVariable("VYRAL_OPENSEARCH_LOCAL_ENDPOINT");

    public static string? AwsRegion =>
        Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")
        ?? Environment.GetEnvironmentVariable("AWS_REGION");

    public static bool IsDynamoDbConfigured =>
        !string.IsNullOrWhiteSpace(DynamoDbTablePrefix);

    public static bool IsS3Configured =>
        !string.IsNullOrWhiteSpace(S3Bucket);

    public static bool IsExecutionConfigured =>
        !string.IsNullOrWhiteSpace(ExecutionDynamoDbTable) &&
        !string.IsNullOrWhiteSpace(ExecutionSqsQueueUrl);

    public static bool IsOpenSearchConfigured =>
        !string.IsNullOrWhiteSpace(OpenSearchEndpoint) &&
        !string.IsNullOrWhiteSpace(AwsRegion);

    public static string UniquePrefix(string basePrefix = "vyral-test")
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return $"{basePrefix}-{suffix}";
    }
}

public static class OpenSearchLocalSettings
{
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(AwsLiveSettings.OpenSearchLocalEndpoint);

    public static Uri GetEndpoint()
    {
        var value = AwsLiveSettings.OpenSearchLocalEndpoint;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 ||
            endpoint.AbsolutePath != "/")
        {
            throw new InvalidOperationException(
                "VYRAL_OPENSEARCH_LOCAL_ENDPOINT must be an http://localhost[:port]/ URI without credentials, path, query, or fragment.");
        }

        return endpoint;
    }
}

public class AwsLiveConformanceStatusTests
{
    [Fact]
    public void LiveConformance_IsOptInByEnvironment()
    {
        Assert.Equal(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AWS_DYNAMODB_TABLE_PREFIX")),
            AwsLiveSettings.IsDynamoDbConfigured);
        Assert.Equal(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AWS_S3_BUCKET")),
            AwsLiveSettings.IsS3Configured);
        Assert.Equal(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AWS_EXECUTION_DYNAMODB_TABLE")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AWS_EXECUTION_SQS_QUEUE_URL")),
            AwsLiveSettings.IsExecutionConfigured);
        Assert.Equal(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AWS_OPENSEARCH_ENDPOINT")) &&
            !string.IsNullOrWhiteSpace(AwsLiveSettings.AwsRegion),
            AwsLiveSettings.IsOpenSearchConfigured);
        Assert.Equal(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_OPENSEARCH_LOCAL_ENDPOINT")),
            OpenSearchLocalSettings.IsConfigured);
    }
}

public sealed class AwsDynamoDbLiveFactAttribute : FactAttribute
{
    public AwsDynamoDbLiveFactAttribute()
    {
        if (!AwsLiveSettings.IsDynamoDbConfigured)
        {
            Skip = "Set VYRAL_AWS_DYNAMODB_TABLE_PREFIX (and AWS credentials) to run DynamoDB live tests.";
        }
    }
}

public sealed class AwsS3LiveFactAttribute : FactAttribute
{
    public AwsS3LiveFactAttribute()
    {
        if (!AwsLiveSettings.IsS3Configured)
        {
            Skip = "Set VYRAL_AWS_S3_BUCKET (and AWS credentials) to run S3 live tests.";
        }
    }
}

public sealed class AwsExecutionLiveFactAttribute : FactAttribute
{
    public AwsExecutionLiveFactAttribute()
    {
        if (!AwsLiveSettings.IsExecutionConfigured)
        {
            Skip = "Set VYRAL_AWS_EXECUTION_DYNAMODB_TABLE and VYRAL_AWS_EXECUTION_SQS_QUEUE_URL (and AWS credentials) to run AWS execution live tests.";
        }
    }
}

public sealed class AwsOpenSearchLiveFactAttribute : FactAttribute
{
    public AwsOpenSearchLiveFactAttribute()
    {
        if (!AwsLiveSettings.IsOpenSearchConfigured)
        {
            Skip = "Set VYRAL_AWS_OPENSEARCH_ENDPOINT, AWS_DEFAULT_REGION/AWS_REGION, and AWS credentials to run OpenSearch projection live tests.";
        }
    }
}

public sealed class OpenSearchLocalFactAttribute : FactAttribute
{
    public OpenSearchLocalFactAttribute()
    {
        if (!OpenSearchLocalSettings.IsConfigured)
        {
            Skip = "Set VYRAL_OPENSEARCH_LOCAL_ENDPOINT to a security-disabled http://localhost OpenSearch test endpoint to run local OpenSearch projection qualification.";
        }
    }
}
