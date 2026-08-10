using System.Text.Json.Serialization;

namespace Vyral.Local;

public class SqliteStorageDiagnostics
{
    [JsonPropertyName("healthy")]
    public bool Healthy { get; set; }

    [JsonPropertyName("quickCheck")]
    public string QuickCheck { get; set; } = string.Empty;

    [JsonPropertyName("foreignKeyViolationCount")]
    public int ForeignKeyViolationCount { get; set; }

    [JsonPropertyName("journalMode")]
    public string JournalMode { get; set; } = string.Empty;

    [JsonPropertyName("pageSize")]
    public long PageSize { get; set; }

    [JsonPropertyName("pageCount")]
    public long PageCount { get; set; }

    [JsonPropertyName("freelistCount")]
    public long FreelistCount { get; set; }

    [JsonPropertyName("databaseExists")]
    public bool? DatabaseExists { get; set; }

    [JsonPropertyName("databaseBytes")]
    public long? DatabaseBytes { get; set; }

    [JsonPropertyName("walBytes")]
    public long? WalBytes { get; set; }

    [JsonPropertyName("shmBytes")]
    public long? ShmBytes { get; set; }
}
