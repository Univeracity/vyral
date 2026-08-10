using System.Text.Json.Serialization;

namespace Vyral.Local;

public class FileObjectStoreDiagnostics
{
    [JsonPropertyName("healthy")]
    public bool Healthy { get; set; }

    [JsonPropertyName("rootExists")]
    public bool RootExists { get; set; }

    [JsonPropertyName("containerCount")]
    public int ContainerCount { get; set; }

    [JsonPropertyName("objectCount")]
    public int ObjectCount { get; set; }

    [JsonPropertyName("metadataSidecarCount")]
    public int MetadataSidecarCount { get; set; }

    [JsonPropertyName("missingMetadataCount")]
    public int MissingMetadataCount { get; set; }

    [JsonPropertyName("orphanMetadataCount")]
    public int OrphanMetadataCount { get; set; }

    [JsonPropertyName("temporaryFileCount")]
    public int TemporaryFileCount { get; set; }

    [JsonPropertyName("temporaryBytes")]
    public long TemporaryBytes { get; set; }

    [JsonPropertyName("contentBytes")]
    public long ContentBytes { get; set; }
}
