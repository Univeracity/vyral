using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Azure;

public class AzureBlobObjectStore : IObjectStore
{
    public const string PortableContentHashMetadataKey = "vyral_content_sha256";

    private readonly BlobServiceClient _serviceClient;

    public AzureBlobObjectStore(BlobServiceClient serviceClient)
    {
        _serviceClient = serviceClient;
    }

    public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
    {
        ObjectMetadataValidator.ValidateUserMetadata(request.Metadata);
        var blobClient = GetBlobClient(request.Container, request.Key);
        var payload = await BufferedPayload.CreateAsync(request.Content, ct);
        try
        {
            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = request.ContentType },
                Metadata = BuildMetadataWithContentHash(request.Metadata, payload.ContentHash),
                Conditions = new BlobRequestConditions()
            };

            if (!string.IsNullOrWhiteSpace(request.IfMatch))
            {
                options.Conditions.IfMatch = new ETag(request.IfMatch);
            }
            if (!string.IsNullOrWhiteSpace(request.IfNoneMatch))
            {
                options.Conditions.IfNoneMatch = request.IfNoneMatch == "*" ? ETag.All : new ETag(request.IfNoneMatch);
            }

            await using var uploadStream = File.OpenRead(payload.Path);
            await blobClient.UploadAsync(uploadStream, options, ct);
        }
        catch (RequestFailedException ex) when (IsPreconditionFailure(ex))
        {
            throw new InvalidOperationException("Object write precondition failed: Azure Blob conditional request failed.", ex);
        }
        finally
        {
            payload.Dispose();
        }

        return await ReadInfoAsync(request.Container, request.Key, ct) ?? throw new InvalidOperationException("Uploaded blob properties could not be read.");
    }

    public async Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default)
    {
        var blobClient = GetBlobClient(request.Container, request.Key);
        try
        {
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);
            var download = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
            return new ObjectResult
            {
                Container = request.Container,
                Key = request.Key,
                ContentType = properties.Value.ContentType,
                ContentLength = properties.Value.ContentLength,
                Etag = properties.Value.ETag.ToString(),
                ContentHash = ResolvePortableContentHash(properties.Value.Metadata, properties.Value.ETag.ToString()),
                Metadata = ToUserMetadata(properties.Value.Metadata),
                UpdatedAt = properties.Value.LastModified.UtcDateTime,
                Content = download.Value.Content
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default)
    {
        var blobClient = GetBlobClient(request.Container, request.Key);
        var conditions = string.IsNullOrWhiteSpace(request.IfMatch)
            ? null
            : new BlobRequestConditions { IfMatch = new ETag(request.IfMatch) };
        try
        {
            await blobClient.DeleteIfExistsAsync(conditions: conditions, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (IsPreconditionFailure(ex))
        {
            throw new InvalidOperationException("Object delete precondition failed: Azure Blob conditional request failed.", ex);
        }
    }

    public async Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default)
    {
        ValidateListLimit(request.Limit);
        ObjectNameValidator.ValidateContainer(request.Container);
        var prefix = string.IsNullOrEmpty(request.Prefix)
            ? null
            : ObjectNameValidator.NormalizeObjectKey(request.Prefix, allowTrailingSlash: true);
        var containerClient = _serviceClient.GetBlobContainerClient(request.Container);
        var items = new List<ObjectInfo>();
        var pageSize = request.Limit.GetValueOrDefault(100);

        await foreach (var page in containerClient
            .GetBlobsAsync(traits: BlobTraits.Metadata, states: BlobStates.None, prefix: prefix, cancellationToken: ct)
            .AsPages(request.ContinuationToken, pageSize))
        {
            foreach (var blob in page.Values)
            {
                items.Add(new ObjectInfo
                {
                    Container = request.Container,
                    Key = blob.Name,
                    ContentType = blob.Properties.ContentType,
                    ContentLength = blob.Properties.ContentLength ?? 0,
                    Etag = blob.Properties.ETag?.ToString() ?? string.Empty,
                    ContentHash = ResolvePortableContentHash(blob.Metadata, blob.Properties.ETag?.ToString() ?? string.Empty),
                    Metadata = ToUserMetadata(blob.Metadata),
                    UpdatedAt = blob.Properties.LastModified?.UtcDateTime ?? DateTime.MinValue
                });
            }

            return new ObjectListResult
            {
                Items = items,
                ContinuationToken = string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken
            };
        }

        return new ObjectListResult();
    }

    private BlobClient GetBlobClient(string container, string key)
    {
        ObjectNameValidator.ValidateContainer(container);
        return _serviceClient.GetBlobContainerClient(container).GetBlobClient(ObjectNameValidator.NormalizeObjectKey(key));
    }

    private static void ValidateListLimit(int? limit)
    {
        if (limit <= 0)
        {
            throw new InvalidOperationException("Object list limit must be greater than zero.");
        }
    }

    private async Task<ObjectInfo?> ReadInfoAsync(string container, string key, CancellationToken ct)
    {
        var result = await GetObjectAsync(new ObjectReadRequest { Container = container, Key = key }, ct);
        if (result == null) return null;

        await result.Content.DisposeAsync();
        return result;
    }

    public static bool IsPreconditionFailure(RequestFailedException exception)
    {
        return exception.Status == 412 ||
            string.Equals(exception.ErrorCode, BlobErrorCode.ConditionNotMet.ToString(), StringComparison.Ordinal) ||
            string.Equals(exception.ErrorCode, BlobErrorCode.BlobAlreadyExists.ToString(), StringComparison.Ordinal);
    }

    public static Dictionary<string, string> BuildMetadataWithContentHash(Dictionary<string, string>? metadata, string contentHash)
    {
        ObjectMetadataValidator.ValidateUserMetadata(metadata);
        var result = metadata == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        result[PortableContentHashMetadataKey] = contentHash;
        return result;
    }

    public static Dictionary<string, string> ToUserMetadata(IDictionary<string, string>? metadata)
    {
        return metadata?
            .Where(kvp => !string.Equals(kvp.Key, PortableContentHashMetadataKey, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal) ?? new Dictionary<string, string>();
    }

    public static string ResolvePortableContentHash(IDictionary<string, string>? metadata, string fallback)
    {
        if (metadata != null &&
            metadata.TryGetValue(PortableContentHashMetadataKey, out var hash) &&
            !string.IsNullOrWhiteSpace(hash))
        {
            return hash;
        }

        return fallback;
    }

    private sealed class BufferedPayload : IDisposable
    {
        private BufferedPayload(string path, string contentHash)
        {
            Path = path;
            ContentHash = contentHash;
        }

        public string Path { get; }

        public string ContentHash { get; }

        public static async Task<BufferedPayload> CreateAsync(Stream content, CancellationToken ct)
        {
            if (content == null) throw new InvalidOperationException("Object content stream is required.");

            var path = System.IO.Path.GetTempFileName();
            try
            {
                await using (var file = File.Create(path))
                {
                    await content.CopyToAsync(file, ct);
                }

                await using var read = File.OpenRead(path);
                var hash = await SHA256.HashDataAsync(read, ct);
                return new BufferedPayload(path, "sha256:" + Convert.ToHexString(hash).ToLowerInvariant());
            }
            catch
            {
                File.Delete(path);
                throw;
            }
        }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }
}
