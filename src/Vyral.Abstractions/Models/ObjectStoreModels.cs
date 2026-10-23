using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public static class ObjectStoreLimits
{
    public const int DefaultListLimit = 100;
    public const int MaxListLimit = 5_000;
    public const int MaxContinuationTokenChars = 8_192;
    public const long DefaultMaxContentBytes = 64L * 1024 * 1024;
    public const long MaxContentBytes = 1024L * 1024 * 1024;

    public static int ResolveListLimit(int? limit)
    {
        if (limit is <= 0)
        {
            throw new InvalidOperationException("Object list limit must be greater than zero.");
        }

        var selected = limit ?? DefaultListLimit;
        if (selected > MaxListLimit)
        {
            throw new InvalidOperationException($"Object list limit cannot exceed {MaxListLimit}.");
        }

        return selected;
    }

    public static long ResolveMaxContentBytes(long? maxContentBytes)
    {
        if (maxContentBytes is <= 0)
        {
            throw new InvalidOperationException("Object read maxContentBytes must be greater than zero.");
        }

        var selected = maxContentBytes ?? DefaultMaxContentBytes;
        if (selected > MaxContentBytes)
        {
            throw new InvalidOperationException($"Object read maxContentBytes cannot exceed {MaxContentBytes}.");
        }

        return selected;
    }

    public static void ValidateContinuationToken(string? continuationToken)
    {
        if (continuationToken?.Length > MaxContinuationTokenChars)
        {
            throw new InvalidOperationException(
                $"Object continuationToken cannot exceed {MaxContinuationTokenChars} characters.");
        }
    }

    public static void EnsureContentLength(long contentLength, long maxContentBytes)
    {
        if (contentLength < 0)
        {
            throw new InvalidOperationException("Object content length cannot be negative.");
        }

        if (contentLength > maxContentBytes)
        {
            throw new ObjectContentLimitExceededException(contentLength, maxContentBytes);
        }
    }
}

public sealed class ObjectContentLimitExceededException(long contentLength, long maxContentBytes)
    : InvalidOperationException($"Object content length {contentLength} exceeds the configured read limit of {maxContentBytes} bytes.")
{
    public long ContentLength { get; } = contentLength;

    public long MaxContentBytes { get; } = maxContentBytes;
}

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

    [JsonPropertyName("maxContentBytes")]
    public long? MaxContentBytes { get; set; }
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

public sealed class ContentAddressedObjectReceipt
{
    [JsonPropertyName("object")]
    public ObjectInfo Object { get; set; } = new();

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("replayed")]
    public bool Replayed { get; set; }
}
