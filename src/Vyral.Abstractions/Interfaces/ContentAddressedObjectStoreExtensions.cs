using System.Security.Cryptography;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public static class ContentAddressedObjectStoreExtensions
{
    /// <summary>
    /// Stores immutable bytes under a SHA-256-derived key. An existing object is accepted only when
    /// its provider-independent content hash and length match. The method never overwrites a
    /// different object and uses a conditional create to make retries safe.
    /// </summary>
    public static async Task<ContentAddressedObjectReceipt> PutContentAddressedAsync(
        this IObjectStore store,
        string container,
        string keyPrefix,
        ReadOnlyMemory<byte> content,
        string? contentType = null,
        Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        return await store.PutContentAddressedAsync(
            container,
            keyPrefix,
            stream,
            content.Length,
            hash,
            contentType,
            metadata,
            ct);
    }

    /// <summary>
    /// Streaming content-addressed write for large immutable artifacts. The caller supplies the
    /// digest and length produced while building the artifact; the object store's independently
    /// reported identity must match both before the receipt is accepted.
    /// </summary>
    public static async Task<ContentAddressedObjectReceipt> PutContentAddressedAsync(
        this IObjectStore store,
        string container,
        string keyPrefix,
        Stream content,
        long contentLength,
        string contentHash,
        string? contentType = null,
        Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new InvalidOperationException("A content-addressed object stream must be readable.");
        }
        if (contentLength < 0)
        {
            throw new InvalidOperationException("A content-addressed object length cannot be negative.");
        }
        RequireSha256(contentHash);
        if (string.IsNullOrWhiteSpace(container))
        {
            throw new InvalidOperationException("A content-addressed object container is required.");
        }
        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            throw new InvalidOperationException("A content-addressed object key prefix is required.");
        }
        ObjectMetadataValidator.ValidateUserMetadata(metadata);

        var normalizedPrefix = keyPrefix.Trim().Trim('/');
        if (normalizedPrefix.Length == 0 || normalizedPrefix.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidOperationException("A content-addressed object key prefix must be a safe relative path.");
        }
        var key = normalizedPrefix + "/sha256/" + contentHash[7..];
        var read = new ObjectReadRequest { Container = container, Key = key };
        var existing = await store.GetObjectAsync(read, ct);
        if (existing is not null)
        {
            await existing.Content.DisposeAsync();
            return Verify(existing, contentHash, contentLength, replayed: true);
        }

        ObjectInfo written;
        try
        {
            written = await store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = container,
                Key = key,
                Content = content,
                ContentType = contentType,
                Metadata = metadata is null
                    ? null
                    : new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                IfNoneMatch = "*"
            }, ct);
        }
        catch (InvalidOperationException)
        {
            // A concurrent writer may have won the conditional create. Re-read and accept only the
            // exact immutable object; otherwise preserve the original failure.
            existing = await store.GetObjectAsync(read, ct);
            if (existing is null)
            {
                throw;
            }
            await existing.Content.DisposeAsync();
            return Verify(existing, contentHash, contentLength, replayed: true);
        }

        try
        {
            return Verify(written, contentHash, contentLength, replayed: false);
        }
        catch
        {
            try
            {
                await store.DeleteObjectAsync(new ObjectDeleteRequest
                {
                    Container = written.Container,
                    Key = written.Key,
                    IfMatch = written.Etag
                }, CancellationToken.None);
            }
            catch
            {
                // Preserve the integrity failure. Cleanup is best-effort and must not disguise it.
            }
            throw;
        }
    }

    private static ContentAddressedObjectReceipt Verify(
        ObjectInfo info,
        string expectedHash,
        long expectedLength,
        bool replayed)
    {
        if (!string.Equals(info.ContentHash, expectedHash, StringComparison.Ordinal) ||
            info.ContentLength != expectedLength)
        {
            throw new InvalidOperationException(
                "The content-addressed object identity does not match its expected SHA-256 and length.");
        }
        return new ContentAddressedObjectReceipt
        {
            Object = info,
            ContentHash = expectedHash,
            Replayed = replayed
        };
    }

    private static void RequireSha256(string value)
    {
        var valid = value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal);
        if (valid)
        {
            foreach (var character in value.AsSpan(7))
            {
                if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                {
                    valid = false;
                    break;
                }
            }
        }
        if (!valid)
        {
            throw new InvalidOperationException("A content-addressed object requires a lowercase SHA-256 digest.");
        }
    }
}
