using Microsoft.Extensions.Configuration;

namespace Vyral.Server;

/// <summary>Server selection for the strong canonical-domain store.</summary>
public sealed class CanonicalStoreOptions
{
    public const string LocalSqlite = "local-sqlite";
    public const string PostgreSql = "postgresql";
    public const string MySql = "mysql";

    /// <summary>
    /// Whether the server exposes CanonicalStore and constructs its backing provider. An
    /// execution-only host must opt out explicitly; canonical access remains fail-closed whenever
    /// this is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;
    public string Provider { get; init; } = LocalSqlite;
    public string DatabasePath { get; init; } = "vyral.canonical.sqlite";
    public string? ConnectionString { get; init; }

    public static CanonicalStoreOptions FromConfiguration(IConfiguration configuration, string defaultDatabasePath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var provider = FirstNonEmpty(
            configuration["CanonicalStore:Provider"],
            configuration["Vyral:CanonicalStore:Provider"],
            configuration["VYRAL_CANONICAL_STORE_PROVIDER"],
            LocalSqlite)!.Trim().ToLowerInvariant();
        provider = provider switch
        {
            "sqlite" or "local" => LocalSqlite,
            "postgres" or "postgresql" or "cloud-sql-postgres" or "alloydb-postgres" => PostgreSql,
            "mysql" or "cloud-sql-mysql" => MySql,
            _ => provider
        };
        var defaultPath = Path.ChangeExtension(defaultDatabasePath, ".canonical.sqlite");
        var options = new CanonicalStoreOptions
        {
            Enabled = ReadEnabled(FirstNonEmpty(
                configuration["CanonicalStore:Enabled"],
                configuration["Vyral:CanonicalStore:Enabled"],
                configuration["VYRAL_CANONICAL_STORE_ENABLED"],
                null)),
            Provider = provider,
            DatabasePath = FirstNonEmpty(
                configuration["CanonicalStore:DatabasePath"],
                configuration["Vyral:CanonicalStore:DatabasePath"],
                configuration["VYRAL_CANONICAL_DATABASE_PATH"],
                defaultPath)!,
            ConnectionString = FirstNonEmpty(
                configuration["CanonicalStore:ConnectionString"],
                configuration["CanonicalStore:Postgres:ConnectionString"],
                configuration["CanonicalStore:MySql:ConnectionString"],
                configuration["Google:CloudSql:ConnectionString"],
                configuration["VYRAL_CANONICAL_CONNECTION_STRING"],
                null)
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!Enabled) return;
        if (Provider is not (LocalSqlite or PostgreSql or MySql))
            throw new InvalidOperationException($"Canonical store provider '{Provider}' is not supported. Use '{LocalSqlite}', '{PostgreSql}', or '{MySql}'.");
        if (Provider == LocalSqlite && string.IsNullOrWhiteSpace(DatabasePath))
            throw new InvalidOperationException("CanonicalStore:DatabasePath is required for the local SQLite provider.");
        if (Provider is PostgreSql or MySql && string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("CanonicalStore:ConnectionString is required for the configured remote SQL provider.");
    }

    public string Describe() => !Enabled
        ? "enabled=false"
        : Provider is PostgreSql or MySql
        ? $"provider={Provider}; connectionString=configured"
        : $"provider=local-sqlite; databasePath={DatabasePath}";

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static bool ReadEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (bool.TryParse(value, out var enabled)) return enabled;
        throw new InvalidOperationException("CanonicalStore:Enabled must be true or false.");
    }
}
