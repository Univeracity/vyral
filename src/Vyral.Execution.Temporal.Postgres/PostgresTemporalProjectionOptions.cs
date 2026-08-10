using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Vyral.Execution.Temporal.Postgres;

public sealed class PostgresTemporalProjectionOptions
{
    private static readonly Regex SchemaPattern = new(
        "^[a-z][a-z0-9_]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string ConnectionString { get; init; } = string.Empty;
    public string DatabaseSchema { get; init; } = "vyral_temporal";
    public bool RequireTls { get; init; } = true;
    public int DispatchClaimSeconds { get; init; } = 30;
    public int DispatchRetrySeconds { get; init; } = 5;

    public void Validate(bool requireConnectionString = true)
    {
        if (!SchemaPattern.IsMatch(DatabaseSchema))
            throw new InvalidOperationException("Temporal projection database schema must be a 1-63 character lowercase PostgreSQL identifier.");
        if (DispatchClaimSeconds is < 5 or > 600)
            throw new InvalidOperationException("Temporal projection dispatch claim duration must be between 5 and 600 seconds.");
        if (DispatchRetrySeconds is < 1 or > 3600)
            throw new InvalidOperationException("Temporal projection dispatch retry delay must be between 1 and 3600 seconds.");
        if (!requireConnectionString && string.IsNullOrWhiteSpace(ConnectionString)) return;
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("Temporal projection PostgreSQL connection string is required.");

        ValidateConnectionString(ConnectionString);
    }

    internal void ValidateConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Temporal projection PostgreSQL connection string is required.");

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Temporal projection PostgreSQL connection string is invalid.", ex);
        }
        if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
            throw new InvalidOperationException("Temporal projection PostgreSQL host and database are required.");
        var loopback = string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(builder.Host, out var address) && IPAddress.IsLoopback(address);
        if (!RequireTls && !loopback)
            throw new InvalidOperationException("Temporal projection TLS can be disabled only for a loopback development database.");
        if (RequireTls && builder.SslMode is not (SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull))
            throw new InvalidOperationException("Temporal projection PostgreSQL requires sslmode Require, VerifyCA, or VerifyFull.");
    }

    public IReadOnlyDictionary<string, string> ToDiagnosticMetadata()
    {
        Validate();
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hostHash"] = Hash(builder.Host ?? string.Empty),
            ["databaseHash"] = Hash(builder.Database ?? string.Empty),
            ["schemaHash"] = Hash(DatabaseSchema),
            ["tlsRequired"] = RequireTls ? "true" : "false",
            ["dispatchClaimSeconds"] = DispatchClaimSeconds.ToString(CultureInfo.InvariantCulture),
            ["dispatchRetrySeconds"] = DispatchRetrySeconds.ToString(CultureInfo.InvariantCulture)
        };
    }

    internal static string QuoteSchema(string value)
    {
        if (!SchemaPattern.IsMatch(value)) throw new InvalidOperationException("PostgreSQL schema identifier is invalid.");
        return $"\"{value}\"";
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))[..16].ToLowerInvariant();
}
