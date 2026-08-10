using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

namespace Vyral.Tests.Google;

public sealed class CloudStorageObjectStoreTests
{
    [Theory]
    [InlineData("\"123\"", 123)]
    [InlineData("456", 456)]
    public void ParseGenerationEtag_AcceptsReturnedAndUnquotedGenerations(string etag, long expected)
    {
        Assert.Equal(expected, CloudStorageObjectStore.ParseGenerationEtag(etag, "Object write"));
    }

    [Theory]
    [InlineData("\"missing\"")]
    [InlineData("W/\"123\"")]
    [InlineData("\"0\"")]
    [InlineData("\"-1\"")]
    public void ParseGenerationEtag_RejectsValuesThatCannotBeAppliedAtomically(string etag)
    {
        Assert.Throws<InvalidOperationException>(() =>
            CloudStorageObjectStore.ParseGenerationEtag(etag, "Object write"));
    }

    [Fact]
    public async Task PutObjectAsync_RejectsUnsupportedIfNoneMatchBeforeCallingGoogle()
    {
        var client = StorageClient.Create(GoogleCredential.FromAccessToken("unused-test-token"));
        var store = new CloudStorageObjectStore(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutObjectAsync(new ObjectWriteRequest
        {
            Container = "objects",
            Key = "docs/a.txt",
            Content = new MemoryStream(Encoding.UTF8.GetBytes("test")),
            IfNoneMatch = "\"some-etag\""
        }));
    }

    [Fact]
    public async Task PutObjectAsync_RejectsConflictingPortablePreconditions()
    {
        var client = StorageClient.Create(GoogleCredential.FromAccessToken("unused-test-token"));
        var store = new CloudStorageObjectStore(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutObjectAsync(new ObjectWriteRequest
        {
            Container = "objects",
            Key = "docs/a.txt",
            Content = new MemoryStream(Encoding.UTF8.GetBytes("test")),
            IfMatch = "\"123\"",
            IfNoneMatch = "*"
        }));
    }

    [Fact]
    public async Task DeleteObjectAsync_RejectsInvalidGenerationBeforeCallingGoogle()
    {
        var client = StorageClient.Create(GoogleCredential.FromAccessToken("unused-test-token"));
        var store = new CloudStorageObjectStore(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteObjectAsync(new ObjectDeleteRequest
        {
            Container = "objects",
            Key = "docs/a.txt",
            IfMatch = "\"missing\""
        }));
    }
}
