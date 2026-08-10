using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

/// <summary>
/// Consumer-supplied manifest for one durable artifact and its queryable record.
/// The manifest is carried in the <c>manifest</c> part of the generic multipart
/// ingest contract; the artifact bytes are carried separately in the
/// <c>artifact</c> part so consumers do not need to base64-expand large payloads.
/// </summary>
public sealed class ArtifactRecordIngestManifest
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("record")]
    public VyralRecord Record { get; set; } = new();

    [JsonPropertyName("artifact")]
    public ArtifactRecordDescriptor Artifact { get; set; } = new();

    /// <summary>
    /// Optional compact signed proof. Vyral validates only the configured
    /// cryptographic envelope; consumers own proof claims and projections.
    /// </summary>
    [JsonPropertyName("externalContext")]
    public ExternalContextProof? ExternalContext { get; set; }
}

public sealed class ArtifactRecordDescriptor
{
    [JsonPropertyName("container")]
    public string Container { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

public sealed class ExternalContextProof
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

public sealed class ArtifactRecordIngestReceipt
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("recordId")]
    public string RecordId { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("recordUri")]
    public string RecordUri { get; set; } = string.Empty;

    [JsonPropertyName("artifact")]
    public ObjectInfo Artifact { get; set; } = new();

    [JsonPropertyName("externalContextVerified")]
    public bool ExternalContextVerified { get; set; }

    [JsonPropertyName("receivedAt")]
    public DateTime ReceivedAt { get; set; }
}
