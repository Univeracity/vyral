using System;
using Xunit;

namespace Vyral.Tests.Pgvector;

public static class PgvectorLiveSettings
{
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable("VYRAL_PGVECTOR_CONNECTION_STRING");

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

    public static string UniquePrefix(string basePrefix = "vyral-test")
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return $"{basePrefix}-{suffix}";
    }
}

public class PgvectorLiveConformanceStatusTests
{
    [Fact]
    public void LiveConformance_IsOptInByEnvironment()
    {
        Assert.Equal(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_PGVECTOR_CONNECTION_STRING")),
            PgvectorLiveSettings.IsConfigured);
    }
}

public sealed class PgvectorLiveFactAttribute : FactAttribute
{
    public PgvectorLiveFactAttribute()
    {
        if (!PgvectorLiveSettings.IsConfigured)
        {
            Skip = "Set VYRAL_PGVECTOR_CONNECTION_STRING to run pgvector live tests.";
        }
    }
}
