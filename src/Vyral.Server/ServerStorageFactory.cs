using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;
using Vyral.Abstractions.Interfaces;
using Vyral.Cloudflare;
using Vyral.Google;
using Vyral.Local;

namespace Vyral.Server;

/// <summary>
/// Builds the storage adapters used by the server and by Vyral-owned hosted workers.
/// Hosts share portable storage behavior but receive separate deployment identities.
/// </summary>
public static class ServerStorageFactory
{
    public static async Task<IRecordCollectionStore> CreateRecordStoreAsync(ServerStorageOptions options)
    {
        switch (options.RecordStore)
        {
            case ServerStorageBackendIds.Sqlite:
            {
                var store = new SqliteRecordCollectionStore(options.DatabasePath);
                await store.InitializeAsync();
                return store;
            }
            case ServerStorageBackendIds.GoogleFirestore:
                return new FirestoreRecordCollectionStore(CreateFirestoreDb(options), options.GoogleFirestoreRootCollection);
            case ServerStorageBackendIds.GoogleAlloyDb:
            {
                if (string.IsNullOrWhiteSpace(options.GoogleAlloyDbConnectionString))
                {
                    throw new InvalidOperationException("Google AlloyDB record store requires Google:AlloyDb:ConnectionString or VYRAL_ALLOYDB_CONNECTION_STRING.");
                }

                var store = new AlloyDbRecordCollectionStore(options.GoogleAlloyDbConnectionString);
                await store.InitializeAsync();
                return store;
            }
            default:
                throw new InvalidOperationException($"Record store backend '{options.RecordStore}' is not supported by this host.");
        }
    }

    public static async Task<ITraceStore> CreateTraceStoreAsync(ServerStorageOptions options)
    {
        switch (options.TraceStore)
        {
            case ServerStorageBackendIds.Sqlite:
            {
                var store = new SqliteTraceStore(options.DatabasePath);
                await store.InitializeAsync();
                return store;
            }
            case ServerStorageBackendIds.GoogleFirestore:
                return new FirestoreTraceStore(CreateFirestoreDb(options), options.GoogleFirestoreRootCollection);
            default:
                throw new InvalidOperationException($"Trace store backend '{options.TraceStore}' is not supported by this host.");
        }
    }

    public static IObjectStore CreateObjectStore(ServerStorageOptions options) => options.ObjectStore switch
    {
        ServerStorageBackendIds.File => new FileObjectStore(options.ObjectsPath),
        ServerStorageBackendIds.GoogleCloudStorage => new CloudStorageObjectStore(StorageClient.Create()),
        ServerStorageBackendIds.CloudflareR2 => R2ObjectStore.Create(new CloudflareR2Options
        {
            AccountId = options.CloudflareAccountId,
            AccessKeyId = options.CloudflareR2AccessKeyId,
            SecretAccessKey = options.CloudflareR2SecretAccessKey,
            ServiceUrl = options.CloudflareR2ServiceUrl
        }),
        _ => throw new InvalidOperationException($"Object store backend '{options.ObjectStore}' is not supported by this host.")
    };

    public static FirestoreDb CreateFirestoreDb(ServerStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.GoogleProjectId))
        {
            throw new InvalidOperationException("Google Firestore store requires Google:ProjectId, Google:Firestore:ProjectId, GOOGLE_CLOUD_PROJECT, or VYRAL_GCP_PROJECT_ID.");
        }

        var builder = new FirestoreDbBuilder { ProjectId = options.GoogleProjectId };
        if (!string.IsNullOrWhiteSpace(options.GoogleFirestoreDatabaseId))
        {
            builder.DatabaseId = options.GoogleFirestoreDatabaseId;
        }

        return builder.Build();
    }
}
