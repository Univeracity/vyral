using Npgsql;
using Vyral.Execution.Temporal.Postgres;

namespace Vyral.Tests.Temporal;

public sealed class TemporalLiveCleanupAuditTests
{
    [TemporalLiveFact]
    public async Task LiveGate_RemovesEveryPrefixedProjectionSchema()
    {
        var prefix = TemporalGateSettings.ResourcePrefix ??
            throw new InvalidOperationException("Temporal live gate resource prefix is required.");
        var connectionString = Environment.GetEnvironmentVariable("VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING") ??
            throw new InvalidOperationException("Temporal live gate projection connection is required.");
        new PostgresTemporalProjectionOptions
        {
            ConnectionString = connectionString,
            DatabaseSchema = $"{prefix}_audit",
            RequireTls = TemporalGateSettings.PostgresTlsRequired
        }.Validate();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM pg_namespace
            WHERE nspname LIKE @schema_pattern ESCAPE '\';
            """;
        command.Parameters.AddWithValue("schema_pattern", $"{prefix}\\_%");
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? -1L));
    }
}

public sealed class TemporalLiveFactAttribute : FactAttribute
{
    public TemporalLiveFactAttribute()
    {
        if (!TemporalGateSettings.LiveGateEnabled)
            Skip = "Run scripts/validate-temporal-live.sh to enable the operator-provisioned live gate.";
    }
}
