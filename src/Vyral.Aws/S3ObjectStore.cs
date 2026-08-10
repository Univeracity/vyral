using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Aws;

/// <summary>
/// IObjectStore backed by Amazon S3.
/// Parallel to AzureBlobObjectStore (Azure) and CloudStorageObjectStore (Google).
///
/// SHA-256 content hashes are stored as S3 object metadata under
/// "vyral_content_sha256", following the same convention as the other adapters.
///
/// The Vyral ETag is the S3 ETag (quoted string, e.g. "\"abc123...\"").
/// Conditional writes are sent as native S3 request preconditions so durable callers such as
/// the MCP task store receive atomic create/compare-and-swap behavior across instances.
///
/// Authentication uses the default AWS credential chain injected via IAmazonS3.
/// </summary>
public class S3ObjectStore : IObjectStore
{
    public const string PortableContentHashMetadataKey = "vyral_content_sha256";

    private readonly IAmazonS3 _client;
    private readonly S3ObjectStoreOptions _options;

    public S3ObjectStore(IAmazonS3 client, S3ObjectStoreOptions? options = null)
    {
        _client = client;
        _options = options ?? new S3ObjectStoreOptions();
    }

    public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
    {
        ObjectMetadataValidator.ValidateUserMetadata(request.Metadata);
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);

        // Buffer content and compute SHA-256
        using var buffer = new MemoryStream();
        await request.Content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var bytes = buffer.ToArray();
        var hashBytes = SHA256.HashData(bytes);
        var contentHash = "sha256:" + Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Build metadata
        var s3Metadata = BuildMetadata(request.Metadata, contentHash);

        var putRequest = new PutObjectRequest
        {
            BucketName = request.Container,
            Key = key,
            InputStream = new MemoryStream(bytes),
            ContentType = request.ContentType ?? "application/octet-stream",
            IfMatch = string.IsNullOrWhiteSpace(request.IfMatch) ? null : request.IfMatch,
            IfNoneMatch = string.IsNullOrWhiteSpace(request.IfNoneMatch) ? null : request.IfNoneMatch,
            DisablePayloadSigning = _options.DisablePutObjectPayloadSigning,
            DisableDefaultChecksumValidation = _options.DisablePutObjectDefaultChecksumValidation
        };

        foreach (var kvp in s3Metadata)
        {
            putRequest.Metadata[kvp.Key] = kvp.Value;
        }

        try
        {
            await _client.PutObjectAsync(putRequest, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new InvalidOperationException("Object write precondition failed.", ex);
        }

        // Re-read ETag from the just-uploaded object
        var info = await HeadObjectAsync(request.Container, key, ct)
            ?? throw new InvalidOperationException("Uploaded object metadata could not be read.");

        return new ObjectInfo
        {
            Container = request.Container,
            Key = request.Key,
            ContentType = info.Headers.ContentType,
            ContentLength = info.ContentLength,
            Etag = info.ETag,
            ContentHash = contentHash,
            Metadata = ToUserMetadata(info.Metadata),
            UpdatedAt = info.LastModified?.ToUniversalTime() ?? DateTime.UtcNow
        };
    }

    public async Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);

        try
        {
            var response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = request.Container,
                Key = key
            }, ct);

            var contentHash = ResolveContentHash(response.Metadata, response.ETag);
            var content = new MemoryStream();
            await response.ResponseStream.CopyToAsync(content, ct);
            content.Position = 0;

            return new ObjectResult
            {
                Container = request.Container,
                Key = request.Key,
                ContentType = response.Headers.ContentType,
                ContentLength = response.ContentLength,
                Etag = response.ETag,
                ContentHash = contentHash,
                Metadata = ToUserMetadata(response.Metadata),
                UpdatedAt = response.LastModified?.ToUniversalTime() ?? DateTime.UtcNow,
                Content = content
            };
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);

        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = request.Container,
                Key = key,
                IfMatch = string.IsNullOrWhiteSpace(request.IfMatch) ? null : request.IfMatch
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new InvalidOperationException("Object delete precondition failed.", ex);
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == HttpStatusCode.NotFound && string.IsNullOrWhiteSpace(request.IfMatch))
        {
            // Idempotent delete — not found is not an error (only when no IfMatch)
        }
    }

    public async Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default)
    {
        if (request.Limit <= 0 && request.Limit.HasValue)
            throw new InvalidOperationException("Object list limit must be greater than zero.");

        ObjectNameValidator.ValidateContainer(request.Container);

        var prefix = string.IsNullOrEmpty(request.Prefix)
            ? null
            : ObjectNameValidator.NormalizeObjectKey(request.Prefix, allowTrailingSlash: true);

        var pageSize = request.Limit ?? 100;

        var listRequest = new ListObjectsV2Request
        {
            BucketName = request.Container,
            Prefix = prefix,
            MaxKeys = pageSize,
            ContinuationToken = request.ContinuationToken
        };

        var response = await _client.ListObjectsV2Async(listRequest, ct);

        var items = new List<ObjectInfo>();
        foreach (var obj in response.S3Objects)
        {
            // S3 ListObjectsV2 does not return metadata per object — would need individual HEAD
            // We return empty metadata and the S3 ETag; content hash requires a separate HEAD.
            items.Add(new ObjectInfo
            {
                Container = request.Container,
                Key = obj.Key,
                ContentType = null,
                ContentLength = obj.Size ?? 0,
                Etag = obj.ETag,
                ContentHash = obj.ETag,
                Metadata = new Dictionary<string, string>(),
                UpdatedAt = obj.LastModified?.ToUniversalTime() ?? DateTime.UtcNow
            });
        }

        return new ObjectListResult
        {
            Items = items,
            ContinuationToken = response.IsTruncated == true
                ? response.NextContinuationToken
                : null
        };
    }

    // ---------------------------------------------------------------------------
    // Internal helpers
    // ---------------------------------------------------------------------------

    private async Task<GetObjectMetadataResponse?> HeadObjectAsync(
        string bucket,
        string key,
        CancellationToken ct)
    {
        try
        {
            return await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static Dictionary<string, string> BuildMetadata(
        Dictionary<string, string>? userMetadata,
        string contentHash)
    {
        ObjectMetadataValidator.ValidateUserMetadata(userMetadata);
        var result = userMetadata == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(userMetadata, StringComparer.Ordinal);

        result[PortableContentHashMetadataKey] = contentHash;
        return result;
    }

    private static Dictionary<string, string> ToUserMetadata(
        MetadataCollection? metadata)
    {
        if (metadata == null) return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in metadata.Keys)
        {
            // S3 SDK strips the "x-amz-meta-" prefix from metadata keys
            var normalizedKey = key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                ? key["x-amz-meta-".Length..]
                : key;

            if (!string.Equals(normalizedKey, PortableContentHashMetadataKey,
                StringComparison.OrdinalIgnoreCase))
            {
                result[normalizedKey] = metadata[key];
            }
        }

        return result;
    }

    private static string ResolveContentHash(MetadataCollection? metadata, string fallback)
    {
        if (metadata != null)
        {
            foreach (var key in metadata.Keys)
            {
                var normalizedKey = key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                    ? key["x-amz-meta-".Length..]
                    : key;

                if (string.Equals(normalizedKey, PortableContentHashMetadataKey,
                    StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(metadata[key]))
                {
                    return metadata[key];
                }
            }
        }

        return fallback;
    }

    private static bool EtagsMatch(string? actual, string? expected)
    {
        if (actual == null || expected == null) return false;
        // Strip quotes for comparison
        var a = actual.Trim('"');
        var b = expected.Trim('"');
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class S3ObjectStoreOptions
{
    public bool DisablePutObjectPayloadSigning { get; init; }
    public bool DisablePutObjectDefaultChecksumValidation { get; init; }
}
