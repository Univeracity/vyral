using System;
using Google.Apis.Auth.OAuth2;
using Xunit;

namespace Vyral.Tests.Google;

public static class GoogleLiveSettings
{
    public static string? AlloyDbConnectionString =>
        Environment.GetEnvironmentVariable("VYRAL_ALLOYDB_CONNECTION_STRING");

    public static string? GcsProjectId =>
        Environment.GetEnvironmentVariable("VYRAL_GCS_PROJECT_ID");

    // Pre-existing GCS bucket used for object store conformance tests.
    // The bucket must already exist and the runtime identity must have
    // storage.objects.create / delete permissions on it.
    public static string? GcsBucket =>
        Environment.GetEnvironmentVariable("VYRAL_GCS_BUCKET");

    public static bool IsAlloyDbConfigured =>
        !string.IsNullOrWhiteSpace(AlloyDbConnectionString);

    public static bool IsGcsConfigured =>
        !string.IsNullOrWhiteSpace(GcsBucket);

    public static string? ExecutionProjectId =>
        Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_PROJECT_ID");

    public static string ExecutionDatabaseId =>
        Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_DATABASE_ID")?.Trim() is { Length: > 0 } databaseId
            ? databaseId
            : "(default)";

    public static string? ExecutionFirestoreRoot =>
        Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_FIRESTORE_ROOT");

    public static string RequireExecutionFirestoreRoot() =>
        !string.IsNullOrWhiteSpace(ExecutionFirestoreRoot)
            ? ExecutionFirestoreRoot.Trim()
            : throw new InvalidOperationException("VYRAL_GOOGLE_EXECUTION_FIRESTORE_ROOT is required for Google execution live tests.");

    public static string? ExecutionTasksLocation =>
        Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_TASKS_LOCATION");

    public static string? ExecutionTasksQueue =>
        Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_TASKS_QUEUE");

    public static string? ExecutionWorkerUrl =>
        Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_WORKER_URL");

    public static string? ExecutionServiceAccountEmail =>
        Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL");

    public static string? ExecutionOidcAudience =>
        Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_OIDC_AUDIENCE");

    /// <summary>
    /// Optional short-lived access token for a local gcloud-authenticated live run. CI and normal
    /// deployments should rely on Application Default Credentials instead.
    /// </summary>
    public static string? ExecutionAccessToken =>
        Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_ACCESS_TOKEN");

    public static string? LiveAccessToken =>
        Environment.GetEnvironmentVariable("VYRAL_GOOGLE_LIVE_ACCESS_TOKEN");

    public static GoogleCredential CreateLiveCredential() =>
        string.IsNullOrWhiteSpace(LiveAccessToken)
            ? GoogleCredential.GetApplicationDefault()
            : GoogleCredential.FromAccessToken(LiveAccessToken);

    public static bool IsExecutionConfigured =>
        !string.IsNullOrWhiteSpace(ExecutionProjectId) &&
        !string.IsNullOrWhiteSpace(ExecutionFirestoreRoot) &&
        !string.IsNullOrWhiteSpace(ExecutionTasksLocation) &&
        !string.IsNullOrWhiteSpace(ExecutionTasksQueue) &&
        !string.IsNullOrWhiteSpace(ExecutionWorkerUrl) &&
        !string.IsNullOrWhiteSpace(ExecutionServiceAccountEmail) &&
        !string.IsNullOrWhiteSpace(ExecutionOidcAudience);

    public static GoogleCredential CreateExecutionCredential() =>
        string.IsNullOrWhiteSpace(ExecutionAccessToken)
            ? GoogleCredential.GetApplicationDefault()
            : GoogleCredential.FromAccessToken(ExecutionAccessToken);

    // Kept for backwards compat; true when both AlloyDB and GCS are set.
    public static bool IsConfigured => IsAlloyDbConfigured && IsGcsConfigured;

    public static string UniquePrefix(string basePrefix = "vyral-test")
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return $"{basePrefix}-{suffix}";
    }
}

public class GoogleLiveConformanceStatusTests
{
    [Fact]
    public void LiveConformance_IsOptInByEnvironment()
    {
        Assert.Equal(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_ALLOYDB_CONNECTION_STRING")),
            GoogleLiveSettings.IsAlloyDbConfigured);
        Assert.Equal(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_GCS_BUCKET")),
            GoogleLiveSettings.IsGcsConfigured);
        Assert.Equal(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_PROJECT_ID")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_FIRESTORE_ROOT")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_TASKS_LOCATION")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_TASKS_QUEUE")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_WORKER_URL")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_GOOGLE_EXECUTION_OIDC_AUDIENCE")),
            GoogleLiveSettings.IsExecutionConfigured);
    }
}

public sealed class GoogleAlloyDbLiveFactAttribute : FactAttribute
{
    public GoogleAlloyDbLiveFactAttribute()
    {
        if (!GoogleLiveSettings.IsAlloyDbConfigured)
            Skip = "Set VYRAL_ALLOYDB_CONNECTION_STRING to run AlloyDB live tests.";
    }
}

public sealed class GoogleGcsLiveFactAttribute : FactAttribute
{
    public GoogleGcsLiveFactAttribute()
    {
        if (!GoogleLiveSettings.IsGcsConfigured)
            Skip = "Set VYRAL_GCS_BUCKET (and optionally VYRAL_GCS_PROJECT_ID) to run GCS live tests.";
    }
}

public sealed class GoogleExecutionLiveFactAttribute : FactAttribute
{
    public GoogleExecutionLiveFactAttribute()
    {
        if (!GoogleLiveSettings.IsExecutionConfigured)
        {
            Skip = "Set VYRAL_GOOGLE_EXECUTION_PROJECT_ID, VYRAL_GOOGLE_EXECUTION_FIRESTORE_ROOT, VYRAL_GOOGLE_EXECUTION_TASKS_LOCATION, VYRAL_GOOGLE_EXECUTION_TASKS_QUEUE, VYRAL_GOOGLE_EXECUTION_WORKER_URL, VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL, and VYRAL_GOOGLE_EXECUTION_OIDC_AUDIENCE to run Google execution live tests.";
        }
    }
}
