using Xunit;

namespace Vyral.Tests.MySql;

public static class MySqlLiveSettings
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("VYRAL_MYSQL_CONNECTION_STRING");
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}

public sealed class MySqlLiveFactAttribute : FactAttribute
{
    public MySqlLiveFactAttribute()
    {
        if (!MySqlLiveSettings.IsConfigured) Skip = "Set VYRAL_MYSQL_CONNECTION_STRING to run MySQL 8 CanonicalStore conformance tests.";
    }
}
