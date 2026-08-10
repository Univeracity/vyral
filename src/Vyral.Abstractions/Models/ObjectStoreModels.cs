using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public class ObjectWriteRequest
{
    [JsonPropertyName("container")]
    public string Container { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonIgnore]
    public Stream Content { get; set; } = Stream.Null;

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }

    [JsonPropertyName("ifMatch")]
    public string? IfMatch { get; set; }

    [JsonPropertyName("ifNoneMatch")]
    public string? IfNoneMatch { get; set; }
}

public class ObjectReadRequest
{
    [JsonPropertyName("container")]
    public string Container { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;
}

public class ObjectDeleteRequest
{
    [JsonPropertyName("container")]
    public string Container { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("ifMatch")]
    public string? IfMatch { get; set; }
}

public class ObjectListRequest
{
    [JsonPropertyName("container")]
    public string Container { get; set; } = string.Empty;

    [JsonPropertyName("prefix")]
    public string? Prefix { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}

public class ObjectInfo
{
    [JsonPropertyName("container")]
    public string Container { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("contentLength")]
    public long ContentLength { get; set; }

    [JsonPropertyName("etag")]
    public string Etag { get; set; } = string.Empty;

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new();

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

public class ObjectResult : ObjectInfo
{
    [JsonIgnore]
    public Stream Content { get; set; } = Stream.Null;
}

public class ObjectListResult
{
    [JsonPropertyName("items")]
    public List<ObjectInfo> Items { get; set; } = new();

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}
