using System.Text;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Tests.Conformance;

public abstract class ObjectStoreConformanceTests
{
    protected abstract IObjectStore CreateObjectStore();

    protected async Task RunObjectStore_RoundTripsContentMetadataAndEtag()
    {
        await WithStoreAsync(async store =>
        {
            var put = await store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = "docs/a.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("hello")),
                ContentType = "text/plain",
                Metadata = new Dictionary<string, string> { ["kind"] = "sample" }
            });

            var result = await store.GetObjectAsync(new ObjectReadRequest { Container = "objects", Key = "docs/a.txt" });

            Assert.NotNull(result);
            Assert.Equal("text/plain", result.ContentType);
            Assert.Equal("sample", result.Metadata["kind"]);
            Assert.False(string.IsNullOrWhiteSpace(put.Etag));
            Assert.Equal(put.Etag, result.Etag);
            Assert.Equal("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", put.ContentHash);
            Assert.Equal(put.ContentHash, result.ContentHash);

            using var reader = new StreamReader(result.Content);
            Assert.Equal("hello", await reader.ReadToEndAsync());
        });
    }

    protected async Task RunObjectStore_EnforcesWritePreconditions()
    {
        await WithStoreAsync(async store =>
        {
            var put = await store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = "docs/a.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("one"))
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = "docs/a.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("two")),
                IfNoneMatch = "*"
            }));

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = "docs/a.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("two")),
                IfMatch = "\"missing\""
            }));

            var updated = await store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = "docs/a.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("two")),
                IfMatch = put.Etag
            });

            Assert.NotEqual(put.Etag, updated.Etag);
        });
    }

    protected async Task RunObjectStore_RejectsNonPortableMetadataKeys()
    {
        await WithStoreAsync(async store =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = "docs/a.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("bad")),
                Metadata = new Dictionary<string, string> { ["bad-key"] = "value" }
            }));

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = "docs/a.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("bad")),
                Metadata = new Dictionary<string, string> { ["vyral_content_sha256"] = "user" }
            }));
        });
    }

    protected async Task RunObjectStore_RejectsNonPortableNames()
    {
        await WithStoreAsync(async store =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "Objects",
                Key = "docs/a.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("bad"))
            }));

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = "../escape.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("bad"))
            }));
        });
    }

    protected async Task RunObjectStore_DeletesObjectsIdempotentlyAndEnforcesPreconditions()
    {
        await WithStoreAsync(async store =>
        {
            var put = await store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = "docs/delete.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("delete me"))
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteObjectAsync(new ObjectDeleteRequest
            {
                Container = "objects",
                Key = "docs/delete.txt",
                IfMatch = "\"missing\""
            }));

            await store.DeleteObjectAsync(new ObjectDeleteRequest
            {
                Container = "objects",
                Key = "docs/delete.txt",
                IfMatch = put.Etag
            });
            await store.DeleteObjectAsync(new ObjectDeleteRequest
            {
                Container = "objects",
                Key = "docs/delete.txt"
            });

            Assert.Null(await store.GetObjectAsync(new ObjectReadRequest { Container = "objects", Key = "docs/delete.txt" }));
        });
    }

    protected async Task RunObjectStore_ListsWithContinuationToken()
    {
        await WithStoreAsync(async store =>
        {
            await store.PutObjectAsync(new ObjectWriteRequest { Container = "objects", Key = "docs/a.txt", Content = new MemoryStream(Encoding.UTF8.GetBytes("a")) });
            await store.PutObjectAsync(new ObjectWriteRequest { Container = "objects", Key = "docs/b.txt", Content = new MemoryStream(Encoding.UTF8.GetBytes("b")) });
            await store.PutObjectAsync(new ObjectWriteRequest { Container = "objects", Key = "docs/c.txt", Content = new MemoryStream(Encoding.UTF8.GetBytes("c")) });

            var first = await store.ListObjectsAsync(new ObjectListRequest { Container = "objects", Prefix = "docs/", Limit = 2 });
            var second = await store.ListObjectsAsync(new ObjectListRequest { Container = "objects", Prefix = "docs/", ContinuationToken = first.ContinuationToken });

            Assert.Equal(new[] { "docs/a.txt", "docs/b.txt" }, first.Items.Select(i => i.Key));
            Assert.NotNull(first.ContinuationToken);
            Assert.Equal(new[] { "docs/c.txt" }, second.Items.Select(i => i.Key));
            Assert.Null(second.ContinuationToken);
        });
    }

    protected async Task RunObjectStore_RejectsInvalidListLimit()
    {
        await WithStoreAsync(async store =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ListObjectsAsync(new ObjectListRequest { Container = "objects", Limit = 0 }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ListObjectsAsync(new ObjectListRequest { Container = "objects", Limit = -1 }));
        });
    }

    private async Task WithStoreAsync(Func<IObjectStore, Task> scenario)
    {
        var store = CreateObjectStore();
        try
        {
            await scenario(store);
        }
        finally
        {
            if (store is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (store is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
