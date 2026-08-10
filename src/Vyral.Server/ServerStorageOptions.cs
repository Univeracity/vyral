using Microsoft.Extensions.Configuration;

namespace Vyral.Server;

public sealed class ServerStorageOptions
{
    public string RecordStore { get; init; } = ServerStorageBackendIds.Sqlite;
    public string TraceStore { get; init; } = ServerStorageBackendIds.Sqlite;
    public string ObjectStore { get; init; } = ServerStorageBackendIds.File;
    public string DatabasePath { get; init; } = "vyral.sqlite";
    public string ObjectsPath { get; init; } = ".vyral/objects";
    public string? GoogleProjectId { get; init; }
    public string? GoogleFirestoreDatabaseId { get; init; }
    public string GoogleFirestoreRootCollection { get; init; } = "vyral";
    public string? GoogleAlloyDbConnectionString { get; init; }
    public string? CloudflareAccountId { get; init; }
    public string? CloudflareR2AccessKeyId { get; init; }
    public string? CloudflareR2SecretAccessKey { get; init; }
    public string? CloudflareR2ServiceUrl { get; init; }
    public string ObjectProbeContainer { get; init; } = "vyral-readiness";

    public static ServerStorageOptions FromConfiguration(IConfiguration configuration)
    {
        var recordStore = NormalizeBackendId(FirstNonEmpty(
            configuration["Storage:RecordStore"],
            configuration["Vyral:Storage:RecordStore"],
            configuration["VYRAL_RECORD_STORE"],
            ServerStorageBackendIds.Sqlite));
        var traceStore = NormalizeBackendId(FirstNonEmpty(
            configuration["Storage:TraceStore"],
            configuration["Vyral:Storage:TraceStore"],
            configuration["VYRAL_TRACE_STORE"],
            recordStore == ServerStorageBackendIds.GoogleFirestore ? ServerStorageBackendIds.GoogleFirestore : ServerStorageBackendIds.Sqlite));
        var objectStore = NormalizeBackendId(FirstNonEmpty(
            configuration["Storage:ObjectStore"],
            configuration["Vyral:Storage:ObjectStore"],
            configuration["VYRAL_OBJECT_STORE"],
            ServerStorageBackendIds.File));
        var googleBucket = FirstNonEmpty(
            configuration["Google:CloudStorage:Bucket"],
            configuration["Gcp:CloudStorage:Bucket"],
            configuration["VYRAL_GCS_BUCKET"],
            null);
        var cloudflareR2Bucket = FirstNonEmpty(
            configuration["Cloudflare:R2:Bucket"],
            configuration["VYRAL_R2_BUCKET"],
            null);
        var configuredObjectProbeContainer = FirstNonEmpty(
            configuration["Storage:ObjectProbeContainer"],
            configuration["ObjectStore:ProbeContainer"],
            configuration["VYRAL_OBJECT_PROBE_CONTAINER"]);
        var objectProbeContainer = configuredObjectProbeContainer
            ?? (objectStore == ServerStorageBackendIds.CloudflareR2 ? cloudflareR2Bucket : googleBucket)
            ?? "vyral-readiness";

        return new ServerStorageOptions
        {
            RecordStore = recordStore,
            TraceStore = traceStore,
            ObjectStore = objectStore,
            DatabasePath = FirstNonEmpty(configuration["DatabasePath"], configuration["Storage:DatabasePath"], configuration["VYRAL_DATABASE_PATH"], "vyral.sqlite")!,
            ObjectsPath = FirstNonEmpty(configuration["ObjectsPath"], configuration["Storage:ObjectsPath"], configuration["VYRAL_OBJECTS_PATH"], ".vyral/objects")!,
            GoogleProjectId = FirstNonEmpty(
                configuration["Google:Firestore:ProjectId"],
                configuration["Google:ProjectId"],
                configuration["Gcp:ProjectId"],
                configuration["GOOGLE_CLOUD_PROJECT"],
                configuration["GCLOUD_PROJECT"],
                configuration["VYRAL_GCP_PROJECT_ID"],
                null),
            GoogleFirestoreDatabaseId = FirstNonEmpty(
                configuration["Google:Firestore:DatabaseId"],
                configuration["Gcp:Firestore:DatabaseId"],
                configuration["VYRAL_FIRESTORE_DATABASE_ID"],
                null),
            GoogleFirestoreRootCollection = FirstNonEmpty(
                configuration["Google:Firestore:RootCollection"],
                configuration["Gcp:Firestore:RootCollection"],
                configuration["VYRAL_FIRESTORE_ROOT_COLLECTION"],
                "vyral")!,
            GoogleAlloyDbConnectionString = FirstNonEmpty(
                configuration["Google:AlloyDb:ConnectionString"],
                configuration["Gcp:AlloyDb:ConnectionString"],
                configuration["VYRAL_ALLOYDB_CONNECTION_STRING"],
                null),
            CloudflareAccountId = FirstNonEmpty(
                configuration["Cloudflare:R2:AccountId"],
                configuration["Cloudflare:AccountId"],
                configuration["VYRAL_CLOUDFLARE_ACCOUNT_ID"],
                null),
            CloudflareR2AccessKeyId = FirstNonEmpty(
                configuration["Cloudflare:R2:AccessKeyId"],
                configuration["VYRAL_R2_ACCESS_KEY_ID"],
                null),
            CloudflareR2SecretAccessKey = FirstNonEmpty(
                configuration["Cloudflare:R2:SecretAccessKey"],
                configuration["VYRAL_R2_SECRET_ACCESS_KEY"],
                null),
            CloudflareR2ServiceUrl = FirstNonEmpty(
                configuration["Cloudflare:R2:ServiceUrl"],
                configuration["VYRAL_R2_SERVICE_URL"],
                null),
            ObjectProbeContainer = objectProbeContainer
        };
    }

    public string Describe()
    {
        return $"recordStore={RecordStore}; traceStore={TraceStore}; objectStore={ObjectStore}; databasePath={DatabasePath}; objectsPath={ObjectsPath}; firestoreRoot={GoogleFirestoreRootCollection}; objectProbeContainer={ObjectProbeContainer}";
    }

    private static string NormalizeBackendId(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "" => ServerStorageBackendIds.Sqlite,
            "local" => ServerStorageBackendIds.Sqlite,
            "local-sqlite" => ServerStorageBackendIds.Sqlite,
            "sqlite" => ServerStorageBackendIds.Sqlite,
            "file" => ServerStorageBackendIds.File,
            "local-file" => ServerStorageBackendIds.File,
            "gcs" => ServerStorageBackendIds.GoogleCloudStorage,
            "google-cloud-storage" => ServerStorageBackendIds.GoogleCloudStorage,
            "cloud-storage" => ServerStorageBackendIds.GoogleCloudStorage,
            "r2" => ServerStorageBackendIds.CloudflareR2,
            "cloudflare-r2" => ServerStorageBackendIds.CloudflareR2,
            "cloudflare-r2-object-storage" => ServerStorageBackendIds.CloudflareR2,
            "firestore" => ServerStorageBackendIds.GoogleFirestore,
            "google-firestore" => ServerStorageBackendIds.GoogleFirestore,
            "google-cloud-firestore" => ServerStorageBackendIds.GoogleFirestore,
            "alloydb" => ServerStorageBackendIds.GoogleAlloyDb,
            "google-alloydb" => ServerStorageBackendIds.GoogleAlloyDb,
            _ => normalized
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}

public static class ServerStorageBackendIds
{
    public const string Sqlite = "sqlite";
    public const string File = "file";
    public const string GoogleFirestore = "google-firestore";
    public const string GoogleCloudStorage = "google-cloud-storage";
    public const string GoogleAlloyDb = "google-alloydb";
    public const string CloudflareR2 = "cloudflare-r2";
}
