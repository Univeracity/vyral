using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Cloudflare;
using Vyral.Tests.Conformance;
using Xunit;

namespace Vyral.Tests.Cloudflare;

public class CloudflareR2ObjectStoreConformanceTests : ObjectStoreConformanceTests
{
    [CloudflareR2LiveFact]
    public Task ObjectStore_RoundTripsContentMetadataAndEtag() =>
        RunObjectStore_RoundTripsContentMetadataAndEtag();

    [CloudflareR2LiveFact]
    public Task ObjectStore_EnforcesWritePreconditions() =>
        RunObjectStore_EnforcesWritePreconditions();

    [CloudflareR2LiveFact]
    public Task ObjectStore_RejectsNonPortableMetadataKeys() =>
        RunObjectStore_RejectsNonPortableMetadataKeys();

    [CloudflareR2LiveFact]
    public Task ObjectStore_RejectsNonPortableNames() =>
        RunObjectStore_RejectsNonPortableNames();

    [CloudflareR2LiveFact]
    public async Task ObjectStore_DeletesIdempotentlyAndRefusesUnsupportedConditionalDelete()
    {
        await using var store = (ScopedR2ObjectStore)CreateObjectStore();
        var put = await store.PutObjectAsync(new() { Container = "objects", Key = "docs/delete.txt",
            Content = new System.IO.MemoryStream(new byte[] { 1, 2, 3 }) });
        foreach (var etag in new[] { "\"missing\"", put.Etag })
            await Assert.ThrowsAsync<System.InvalidOperationException>(() => store.DeleteObjectAsync(new() {
                Container = "objects", Key = "docs/delete.txt", IfMatch = etag }));
        var retained = await store.GetObjectAsync(new() { Container = "objects", Key = "docs/delete.txt" });
        Assert.NotNull(retained);
        await retained!.Content.DisposeAsync();
        await store.DeleteObjectAsync(new() { Container = "objects", Key = "docs/delete.txt" });
        await store.DeleteObjectAsync(new() { Container = "objects", Key = "docs/delete.txt" });
        Assert.Null(await store.GetObjectAsync(new() { Container = "objects", Key = "docs/delete.txt" }));
    }

    [CloudflareR2LiveFact]
    public Task ObjectStore_ListsWithContinuationToken() =>
        RunObjectStore_ListsWithContinuationToken();

    [CloudflareR2LiveFact]
    public Task ObjectStore_RejectsInvalidListLimit() =>
        RunObjectStore_RejectsInvalidListLimit();

    [CloudflareR2LiveFact]
    public async Task ObjectStore_PreCancelledWriteLeavesNoObject()
    {
        await using var store = (ScopedR2ObjectStore)CreateObjectStore();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(() => store.PutObjectAsync(new() {
            Container = "objects", Key = "cancelled.txt", Content = new System.IO.MemoryStream(new byte[] { 1 }) }, cancellation.Token));
        Assert.Null(await store.GetObjectAsync(new() { Container = "objects", Key = "cancelled.txt" }));
    }

    protected override IObjectStore CreateObjectStore()
    {
        var bucket = CloudflareR2LiveSettings.Bucket!;
        var keyPrefix = CloudflareR2LiveSettings.UniquePrefix();
        var store = R2ObjectStore.Create(new CloudflareR2Options
        {
            AccountId = CloudflareR2LiveSettings.AccountId,
            AccessKeyId = CloudflareR2LiveSettings.AccessKeyId,
            SecretAccessKey = CloudflareR2LiveSettings.SecretAccessKey,
            SessionToken = CloudflareR2LiveSettings.SessionToken
        });

        return new ScopedR2ObjectStore(store, bucket, keyPrefix);
    }
}

internal sealed class ScopedR2ObjectStore : IObjectStore, IAsyncDisposable
{
    private readonly R2ObjectStore _inner;
    private readonly string _bucket;
    private readonly string _keyPrefix;

    public ScopedR2ObjectStore(R2ObjectStore inner, string bucket, string keyPrefix)
    {
        _inner = inner;
        _bucket = bucket;
        _keyPrefix = keyPrefix;
    }

    public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var result = await _inner.PutObjectAsync(MapWrite(request), ct);
        result.Container = request.Container;
        result.Key = request.Key;
        return result;
    }

    public async Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var result = await _inner.GetObjectAsync(new ObjectReadRequest
        {
            Container = _bucket,
            Key = ScopeKey(request.Key)
        }, ct);
        if (result is not null)
        {
            result.Container = request.Container;
            result.Key = request.Key;
        }

        return result;
    }

    public Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        return _inner.DeleteObjectAsync(new ObjectDeleteRequest
        {
            Container = _bucket,
            Key = ScopeKey(request.Key),
            IfMatch = request.IfMatch
        }, ct);
    }

    public async Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        if (!string.IsNullOrEmpty(request.Prefix))
            ObjectNameValidator.NormalizeObjectKey(request.Prefix, allowTrailingSlash: true);
        var prefix = string.IsNullOrEmpty(request.Prefix)
            ? _keyPrefix + "/"
            : _keyPrefix + "/" + request.Prefix.TrimStart('/');

        var result = await _inner.ListObjectsAsync(new ObjectListRequest
        {
            Container = _bucket,
            Prefix = prefix,
            Limit = request.Limit,
            ContinuationToken = request.ContinuationToken
        }, ct);

        foreach (var item in result.Items)
        {
            item.Container = request.Container;
            item.Key = item.Key[(_keyPrefix.Length + 1)..];
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            string? continuationToken = null;
            do
            {
                var list = await _inner.ListObjectsAsync(new ObjectListRequest
                {
                    Container = _bucket,
                    Prefix = _keyPrefix + "/",
                    ContinuationToken = continuationToken
                });

                foreach (var item in list.Items.ToList())
                {
                    try
                    {
                        await _inner.DeleteObjectAsync(new ObjectDeleteRequest
                        {
                            Container = _bucket,
                            Key = item.Key
                        });
                    }
                    catch
                    {
                        // Best-effort cleanup for live conformance tests.
                    }
                }

                continuationToken = list.ContinuationToken;
            }
            while (continuationToken is not null);
        }
        finally
        {
            _inner.Dispose();
        }
    }

    private string ScopeKey(string key) => $"{_keyPrefix}/{ObjectNameValidator.NormalizeObjectKey(key)}";

    private ObjectWriteRequest MapWrite(ObjectWriteRequest request) =>
        new()
        {
            Container = _bucket,
            Key = ScopeKey(request.Key),
            Content = request.Content,
            ContentType = request.ContentType,
            Metadata = request.Metadata,
            IfMatch = request.IfMatch,
            IfNoneMatch = request.IfNoneMatch
        };
}
