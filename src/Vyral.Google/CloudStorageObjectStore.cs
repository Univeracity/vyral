using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Storage.v1.Data;
using GoogleApiException = global::Google.GoogleApiException;
using Google.Cloud.Storage.V1;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Object = Google.Apis.Storage.v1.Data.Object;

namespace Vyral.Google;

/// <summary>
/// IObjectStore implementation backed by Google Cloud Storage.
/// Parallel to AzureBlobObjectStore in Vyral.Azure.
///
/// SHA-256 content hashes are stored as GCS object metadata under
/// "vyral_content_sha256", following the same convention as the Azure Blob
/// adapter. User-supplied metadata keys are validated against the portable
/// Vyral metadata key contract.
///
/// Authentication uses Google Application Default Credentials via the injected
/// StorageClient.
/// </summary>
public class CloudStorageObjectStore : IObjectStore
{
    public const string PortableContentHashMetadataKey = "vyral_content_sha256";

    private readonly StorageClient _storageClient;

    public CloudStorageObjectStore(StorageClient storageClient)
    {
        _storageClient = storageClient;
    }

    public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
    {
        ObjectMetadataValidator.ValidateUserMetadata(request.Metadata);
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);
        if (!string.IsNullOrWhiteSpace(request.IfMatch) && !string.IsNullOrWhiteSpace(request.IfNoneMatch))
        {
            throw new InvalidOperationException("Object writes cannot combine If-Match and If-None-Match preconditions.");
        }

        // Buffer content and compute SHA-256
        using var ms = new MemoryStream();
        await request.Content.CopyToAsync(ms, ct);
        ms.Position = 0;
        var bytes = ms.ToArray();
        var hashBytes = SHA256.HashData(bytes);
        var contentHash = "sha256:" + Convert.ToHexString(hashBytes).ToLowerInvariant();

        var metadata = BuildMetadata(request.Metadata, contentHash);

        var gcsObject = new Object
        {
            Bucket = request.Container,
            Name = key,
            ContentType = request.ContentType ?? "application/octet-stream",
            Metadata = metadata
        };

        var uploadOptions = new UploadObjectOptions();
        if (!string.IsNullOrWhiteSpace(request.IfMatch))
        {
            uploadOptions.IfGenerationMatch = ParseGenerationEtag(request.IfMatch, "Object write");
        }
        if (!string.IsNullOrWhiteSpace(request.IfNoneMatch))
        {
            if (!string.Equals(request.IfNoneMatch.Trim(), "*", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Google Cloud Storage object writes only support '*' for If-None-Match.");
            }

            uploadOptions.IfGenerationMatch = 0; // only succeed if object does not exist
        }

        Object uploaded;
        try
        {
            using var uploadStream = new MemoryStream(bytes);
            uploaded = await _storageClient.UploadObjectAsync(gcsObject, uploadStream, uploadOptions, ct);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            throw new InvalidOperationException("Object write precondition failed.", ex);
        }

        return MapToInfo(request.Container, key, uploaded, contentHash);
    }

    public async Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);

        Object? gcsObject;
        try
        {
            gcsObject = await _storageClient.GetObjectAsync(request.Container, key, cancellationToken: ct);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        var ms = new MemoryStream();
        await _storageClient.DownloadObjectAsync(gcsObject, ms, cancellationToken: ct);
        ms.Position = 0;

        var contentHash = ResolveContentHash(gcsObject.Metadata, gcsObject.Generation?.ToString() ?? string.Empty);

        return new ObjectResult
        {
            Container = request.Container,
            Key = key,
            ContentType = gcsObject.ContentType,
            ContentLength = (long)(gcsObject.Size ?? 0),
            Etag = FormatEtag(gcsObject.Generation),
            ContentHash = contentHash,
            Metadata = ToUserMetadata(gcsObject.Metadata),
            UpdatedAt = gcsObject.UpdatedDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow,
            Content = ms
        };
    }

    public async Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);

        var options = new DeleteObjectOptions();
        if (!string.IsNullOrWhiteSpace(request.IfMatch))
        {
            options.IfGenerationMatch = ParseGenerationEtag(request.IfMatch, "Object delete");
        }

        try
        {
            await _storageClient.DeleteObjectAsync(request.Container, key, options, ct);
        }
        catch (GoogleApiException ex) when (
            ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound &&
            string.IsNullOrWhiteSpace(request.IfMatch))
        {
            // Idempotent delete — not found is not an error
        }
        catch (GoogleApiException ex) when (
            ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound &&
            !string.IsNullOrWhiteSpace(request.IfMatch))
        {
            throw new InvalidOperationException("Object delete precondition failed.", ex);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            throw new InvalidOperationException("Object delete precondition failed.", ex);
        }
    }

    public async Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default)
    {
        if (request.Limit <= 0 && request.Limit.HasValue)
            throw new InvalidOperationException("Object list limit must be greater than zero.");

        ObjectNameValidator.ValidateContainer(request.Container);
        var pageSize = request.Limit ?? 100;
        var prefix = string.IsNullOrEmpty(request.Prefix)
            ? null
            : ObjectNameValidator.NormalizeObjectKey(request.Prefix, allowTrailingSlash: true);

        var listOptions = new ListObjectsOptions
        {
            PageSize = pageSize,
            PageToken = request.ContinuationToken,
            Projection = Projection.Full
        };

        var page = await _storageClient
            .ListObjectsAsync(request.Container, prefix, listOptions)
            .ReadPageAsync(pageSize, ct);
        var items = new List<ObjectInfo>();
        foreach (var obj in page)
        {
            var contentHash = ResolveContentHash(obj.Metadata, obj.Generation?.ToString() ?? string.Empty);
            items.Add(new ObjectInfo
            {
                Container = request.Container,
                Key = obj.Name,
                ContentType = obj.ContentType,
                ContentLength = (long)(obj.Size ?? 0),
                Etag = FormatEtag(obj.Generation),
                ContentHash = contentHash,
                Metadata = ToUserMetadata(obj.Metadata),
                UpdatedAt = obj.UpdatedDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow
            });
        }

        return new ObjectListResult
        {
            Items = items,
            ContinuationToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken
        };
    }

    private static ObjectInfo MapToInfo(string container, string key, Object obj, string contentHash)
    {
        return new ObjectInfo
        {
            Container = container,
            Key = key,
            ContentType = obj.ContentType,
            ContentLength = (long)(obj.Size ?? 0),
            Etag = FormatEtag(obj.Generation),
            ContentHash = contentHash,
            Metadata = ToUserMetadata(obj.Metadata),
            UpdatedAt = obj.UpdatedDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow
        };
    }

    private static Dictionary<string, string> BuildMetadata(Dictionary<string, string>? userMetadata, string contentHash)
    {
        var result = userMetadata == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(userMetadata, StringComparer.Ordinal);
        result[PortableContentHashMetadataKey] = contentHash;
        return result;
    }

    private static Dictionary<string, string> ToUserMetadata(IDictionary<string, string>? metadata)
    {
        return metadata?
            .Where(kvp => !string.Equals(kvp.Key, PortableContentHashMetadataKey, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>();
    }

    private static string ResolveContentHash(IDictionary<string, string>? metadata, string fallback)
    {
        if (metadata != null &&
            metadata.TryGetValue(PortableContentHashMetadataKey, out var hash) &&
            !string.IsNullOrWhiteSpace(hash))
            return hash;
        return fallback;
    }

    private static string FormatEtag(long? generation) =>
        generation.HasValue ? $"\"{generation}\"" : string.Empty;

    internal static long ParseGenerationEtag(string etag, string operation)
    {
        var candidate = etag.Trim();
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1];
        }

        if (!long.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) || generation <= 0)
        {
            throw new InvalidOperationException($"{operation} precondition requires a Google Cloud Storage generation ETag.");
        }

        return generation;
    }
}
