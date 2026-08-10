using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;
using Vyral.Local;
using Xunit;

namespace Vyral.Tests.Local;

public class FileObjectStoreTests
{
    [Fact]
    public async Task PutGetListDelete_RoundTripsObjectDeterministically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-objects-{Guid.NewGuid():N}");
        var store = new FileObjectStore(root);

        await store.PutObjectAsync(new ObjectWriteRequest
        {
            Container = "objects",
            Key = "docs/a.txt",
            Content = new MemoryStream(Encoding.UTF8.GetBytes("hello"))
        });
        await store.PutObjectAsync(new ObjectWriteRequest
        {
            Container = "objects",
            Key = "docs/b.txt",
            Content = new MemoryStream(Encoding.UTF8.GetBytes("world"))
        });

        var loaded = await store.GetObjectAsync(new ObjectReadRequest { Container = "objects", Key = "docs/a.txt" });
        await using var loadedContent = loaded!.Content;
        using var reader = new StreamReader(loadedContent);
        var listed = await store.ListObjectsAsync(new ObjectListRequest { Container = "objects", Prefix = "docs/" });
        var diagnostics = await store.GetStorageDiagnosticsAsync();

        Assert.Equal("hello", await reader.ReadToEndAsync());
        Assert.Equal(new[] { "docs/a.txt", "docs/b.txt" }, listed.Items.Select(i => i.Key));
        Assert.True(diagnostics.Healthy);
        Assert.Equal(2, diagnostics.ObjectCount);
        Assert.Equal(2, diagnostics.MetadataSidecarCount);
        Assert.Equal(0, diagnostics.MissingMetadataCount);
        Assert.Equal(0, diagnostics.OrphanMetadataCount);
        Assert.Equal(0, diagnostics.TemporaryFileCount);

        await store.DeleteObjectAsync(new ObjectDeleteRequest { Container = "objects", Key = "docs/a.txt" });
        Assert.Null(await store.GetObjectAsync(new ObjectReadRequest { Container = "objects", Key = "docs/a.txt" }));
    }

    [Fact]
    public async Task ListAndConditionalOperations_RecoverWhenMetadataSidecarIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-objects-{Guid.NewGuid():N}");
        var store = new FileObjectStore(root);
        var objectPath = Path.Combine(root, "objects", "docs", "orphan.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        await File.WriteAllTextAsync(objectPath, "orphan");

        var listed = await store.ListObjectsAsync(new ObjectListRequest
        {
            Container = "objects",
            Prefix = "docs/"
        });
        var item = Assert.Single(listed.Items);
        var diagnostics = await store.GetStorageDiagnosticsAsync();
        Assert.Equal("docs/orphan.txt", item.Key);
        Assert.Equal(6, item.ContentLength);
        Assert.StartsWith("sha256:", item.ContentHash);
        Assert.False(diagnostics.Healthy);
        Assert.Equal(1, diagnostics.ObjectCount);
        Assert.Equal(1, diagnostics.MissingMetadataCount);
        Assert.Equal(0, diagnostics.TemporaryFileCount);

        var loaded = await store.GetObjectAsync(new ObjectReadRequest
        {
            Container = "objects",
            Key = "docs/orphan.txt"
        });
        Assert.NotNull(loaded);
        await using (loaded!.Content)
        using (var reader = new StreamReader(loaded.Content))
        {
            Assert.Equal("orphan", await reader.ReadToEndAsync());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = "docs/orphan.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("replacement")),
                IfNoneMatch = "*"
            }));

        await store.DeleteObjectAsync(new ObjectDeleteRequest
        {
            Container = "objects",
            Key = "docs/orphan.txt",
            IfMatch = item.Etag
        });
        Assert.False(File.Exists(objectPath));
    }

    [Fact]
    public async Task ListObjects_AppliesDefaultAndMaximumLimits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-objects-{Guid.NewGuid():N}");
        var store = new FileObjectStore(root);

        for (var i = 0; i < 105; i++)
        {
            await store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = $"docs/{i:D3}.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("x"))
            });
        }

        var firstPage = await store.ListObjectsAsync(new ObjectListRequest
        {
            Container = "objects",
            Prefix = "docs/"
        });
        var secondPage = await store.ListObjectsAsync(new ObjectListRequest
        {
            Container = "objects",
            Prefix = "docs/",
            ContinuationToken = firstPage.ContinuationToken
        });
        var tooLarge = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListObjectsAsync(new ObjectListRequest
        {
            Container = "objects",
            Prefix = "docs/",
            Limit = 5001
        }));

        Assert.Equal(100, firstPage.Items.Count);
        Assert.Equal("docs/000.txt", firstPage.Items[0].Key);
        Assert.NotNull(firstPage.ContinuationToken);
        Assert.Equal(5, secondPage.Items.Count);
        Assert.Equal("docs/100.txt", secondPage.Items[0].Key);
        Assert.Null(secondPage.ContinuationToken);
        Assert.Contains("cannot exceed 5000", tooLarge.Message);
    }

    [Fact]
    public async Task ListObjects_DoesNotTreatMetadataSuffixObjectAsSidecar()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-objects-{Guid.NewGuid():N}");
        var store = new FileObjectStore(root);

        await store.PutObjectAsync(new ObjectWriteRequest
        {
            Container = "objects",
            Key = "docs/report.metadata.json",
            Content = new MemoryStream(Encoding.UTF8.GetBytes("content"))
        });

        var listed = await store.ListObjectsAsync(new ObjectListRequest
        {
            Container = "objects",
            Prefix = "docs/"
        });

        var item = Assert.Single(listed.Items);
        Assert.Equal("docs/report.metadata.json", item.Key);
    }

    [Fact]
    public async Task Diagnostics_ReportStaleTemporaryFilesWithoutListingThemAsObjects()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-objects-{Guid.NewGuid():N}");
        var store = new FileObjectStore(root);
        var tempDirectory = Path.Combine(root, ".vyral-tmp");
        Directory.CreateDirectory(tempDirectory);
        await File.WriteAllTextAsync(Path.Combine(tempDirectory, "stale.tmp"), "stale");

        await store.PutObjectAsync(new ObjectWriteRequest
        {
            Container = "objects",
            Key = "docs/a.txt",
            Content = new MemoryStream(Encoding.UTF8.GetBytes("hello"))
        });

        var listed = await store.ListObjectsAsync(new ObjectListRequest
        {
            Container = "objects",
            Prefix = "docs/"
        });
        var diagnostics = await store.GetStorageDiagnosticsAsync();

        var item = Assert.Single(listed.Items);
        Assert.Equal("docs/a.txt", item.Key);
        Assert.False(diagnostics.Healthy);
        Assert.Equal(1, diagnostics.ObjectCount);
        Assert.Equal(1, diagnostics.MetadataSidecarCount);
        Assert.Equal(1, diagnostics.TemporaryFileCount);
        Assert.Equal(5, diagnostics.TemporaryBytes);
    }

    [Fact]
    public async Task PutObject_RejectsPathTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-objects-{Guid.NewGuid():N}");
        var store = new FileObjectStore(root);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = "objects",
                Key = "../escape.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("bad"))
            }));
    }

    [Fact]
    public async Task ConcurrentConditionalCreate_AcrossStoreInstances_HasSingleWinner()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-objects-{Guid.NewGuid():N}");
        var firstStore = new FileObjectStore(root);
        var secondStore = new FileObjectStore(root);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> TryCreateAsync(FileObjectStore store, string value)
        {
            await start.Task;
            try
            {
                await store.PutObjectAsync(new ObjectWriteRequest
                {
                    Container = "objects",
                    Key = "shared.txt",
                    Content = new MemoryStream(Encoding.UTF8.GetBytes(value)),
                    IfNoneMatch = "*"
                });
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var writes = new[]
        {
            TryCreateAsync(firstStore, "first"),
            TryCreateAsync(secondStore, "second")
        };
        start.SetResult();
        var results = await Task.WhenAll(writes);

        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);
        var loaded = await firstStore.GetObjectAsync(new ObjectReadRequest
        {
            Container = "objects",
            Key = "shared.txt"
        });
        Assert.NotNull(loaded);
        await using (loaded!.Content)
        using (var reader = new StreamReader(loaded.Content))
        {
            Assert.Contains(await reader.ReadToEndAsync(), new[] { "first", "second" });
        }
    }
}
