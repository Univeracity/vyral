using System.Text;
using Azure.Storage.Blobs;
using Vyral.Abstractions.Models;
using Vyral.Azure;

namespace Vyral.Tests.Azure;

public class AzureBlobObjectStoreLiveTests
{
    [AzureBlobLiveFact]
    public async Task BlobObjectStore_RoundTripsListsAndDeletesBlob()
    {
        var settings = AzureLiveSettings.Blob();
        var serviceClient = new BlobServiceClient(settings.ConnectionString);
        var containerName = AzureLiveSettings.UniqueContainerName(settings.ContainerPrefix);
        var containerClient = serviceClient.GetBlobContainerClient(containerName);

        await containerClient.CreateIfNotExistsAsync();
        try
        {
            var store = new AzureBlobObjectStore(serviceClient);
            await store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = containerName,
                Key = "docs/live.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("azure blob live test")),
                ContentType = "text/plain",
                Metadata = new Dictionary<string, string> { ["kind"] = "live" },
                IfNoneMatch = "*"
            });

            var result = await store.GetObjectAsync(new ObjectReadRequest
            {
                Container = containerName,
                Key = "docs/live.txt"
            });

            Assert.NotNull(result);
            Assert.Equal("text/plain", result.ContentType);
            Assert.Equal("live", result.Metadata["kind"]);
            Assert.StartsWith("sha256:", result.ContentHash, StringComparison.Ordinal);
            Assert.False(result.Metadata.ContainsKey(AzureBlobObjectStore.PortableContentHashMetadataKey));
            using (var reader = new StreamReader(result.Content))
            {
                Assert.Equal("azure blob live test", await reader.ReadToEndAsync());
            }

            var list = await store.ListObjectsAsync(new ObjectListRequest
            {
                Container = containerName,
                Prefix = "docs/",
                Limit = 10
            });
            Assert.Contains(list.Items, item => item.Key == "docs/live.txt");
            Assert.All(list.Items.Where(item => item.Key == "docs/live.txt"), item => Assert.StartsWith("sha256:", item.ContentHash, StringComparison.Ordinal));

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutObjectAsync(new ObjectWriteRequest
            {
                Container = containerName,
                Key = "docs/live.txt",
                Content = new MemoryStream(Encoding.UTF8.GetBytes("duplicate")),
                IfNoneMatch = "*"
            }));

            await store.DeleteObjectAsync(new ObjectDeleteRequest
            {
                Container = containerName,
                Key = "docs/live.txt"
            });
            Assert.Null(await store.GetObjectAsync(new ObjectReadRequest { Container = containerName, Key = "docs/live.txt" }));
        }
        finally
        {
            await containerClient.DeleteIfExistsAsync();
        }
    }
}
