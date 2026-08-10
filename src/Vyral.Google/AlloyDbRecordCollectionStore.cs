using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Npgsql;
using Vyral.Pgvector;

namespace Vyral.Google;

/// <summary>
/// IRecordCollectionStore implementation backed by Google AlloyDB for PostgreSQL.
/// AlloyDB is Google's managed Postgres service with native pgvector support.
///
/// Two connection modes are supported:
///
///   1. Auth Proxy — run the AlloyDB Auth Proxy locally and pass a standard
///      Postgres connection string pointing at the proxy socket. This is the
///      simplest production path and handles IAM auth via the proxy process.
///
///   2. Direct IAM — call CreateWithIamAuthAsync() to connect without the
///      proxy. Uses Google Application Default Credentials (ADC) to obtain and
///      periodically refresh an access token, which Npgsql uses as the
///      password. Suitable for workloads running on GCE, GKE, or Cloud Run
///      where a service account identity is attached to the runtime environment.
///
/// Schema and query logic are fully inherited from PgvectorRecordCollectionStore.
/// AlloyDB-specific behaviour is limited to auth and connection construction.
/// </summary>
public class AlloyDbRecordCollectionStore : PgvectorRecordCollectionStore
{
    /// <summary>Auth Proxy mode: the caller supplies a standard connection string.</summary>
    public AlloyDbRecordCollectionStore(string connectionString)
        : base(connectionString)
    {
    }

    /// <summary>Direct connector mode: caller supplies a pre-built NpgsqlDataSource.</summary>
    private AlloyDbRecordCollectionStore(NpgsqlDataSource dataSource)
        : base(dataSource)
    {
    }

    /// <summary>
    /// Creates an AlloyDbRecordCollectionStore that authenticates via Google
    /// Application Default Credentials and refreshes IAM access tokens automatically.
    ///
    /// Suitable for environments where a service account is attached to the runtime
    /// (GCE, GKE, Cloud Run). The caller must still ensure the service account has
    /// the `alloydb.instances.connect` IAM permission.
    /// </summary>
    /// <param name="host">AlloyDB instance IP or hostname (direct connectivity or PSC endpoint).</param>
    /// <param name="port">Postgres port (default 5432).</param>
    /// <param name="database">Database name.</param>
    /// <param name="user">IAM service account email (e.g. sa@project.iam.gserviceaccount.com).</param>
    /// <param name="ct">Cancellation token used during the initial credential fetch.</param>
    public static async Task<AlloyDbRecordCollectionStore> CreateWithIamAuthAsync(
        string host,
        int port,
        string database,
        string user,
        CancellationToken ct = default)
    {
        // Verify ADC is reachable before building the data source.
        var credential = await GoogleCredential.GetApplicationDefaultAsync(ct);

        var connString = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = user,
            SslMode = SslMode.Require,
        }.ToString();

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connString);

        // Periodically refresh the IAM access token used as the Postgres password.
        dataSourceBuilder.UsePeriodicPasswordProvider(
            async (_, cancellationToken) =>
            {
                var cred = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken);
                return await cred.UnderlyingCredential
                    .GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
            },
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(55));

        _ = credential; // ADC confirmed reachable
        return new AlloyDbRecordCollectionStore(dataSourceBuilder.Build());
    }

    /// <summary>
    /// Builds a standard Npgsql connection string for Auth Proxy mode.
    /// Run the AlloyDB Auth Proxy alongside the application process and
    /// point this at the proxy's local socket.
    /// </summary>
    public static string BuildAuthProxyConnectionString(
        string host,
        int port,
        string database,
        string user,
        string password)
    {
        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = user,
            Password = password,
        }.ToString();
    }
}
