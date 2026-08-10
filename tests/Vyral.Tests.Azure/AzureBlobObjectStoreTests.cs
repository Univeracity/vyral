using Azure;
using Azure.Storage.Blobs.Models;
using Vyral.Azure;

namespace Vyral.Tests.Azure;

public class AzureBlobObjectStoreTests
{
    [Fact]
    public void BuildMetadataWithContentHash_StoresPortableHashAndPreservesUserMetadata()
    {
        var metadata = AzureBlobObjectStore.BuildMetadataWithContentHash(
            new Dictionary<string, string> { ["kind"] = "sample" },
            "sha256:abc");

        Assert.Equal("sample", metadata["kind"]);
        Assert.Equal("sha256:abc", metadata[AzureBlobObjectStore.PortableContentHashMetadataKey]);
    }

    [Fact]
    public void ToUserMetadata_RemovesReservedPortableHashMetadata()
    {
        var metadata = AzureBlobObjectStore.ToUserMetadata(new Dictionary<string, string>
        {
            ["kind"] = "sample",
            [AzureBlobObjectStore.PortableContentHashMetadataKey] = "sha256:abc"
        });

        Assert.Equal(new[] { "kind" }, metadata.Keys);
        Assert.Equal("sample", metadata["kind"]);
    }

    [Fact]
    public void ResolvePortableContentHash_UsesReservedMetadataBeforeFallback()
    {
        var hash = AzureBlobObjectStore.ResolvePortableContentHash(new Dictionary<string, string>
        {
            [AzureBlobObjectStore.PortableContentHashMetadataKey] = "sha256:abc"
        }, fallback: "\"etag\"");
        var fallback = AzureBlobObjectStore.ResolvePortableContentHash(new Dictionary<string, string>(), fallback: "\"etag\"");

        Assert.Equal("sha256:abc", hash);
        Assert.Equal("\"etag\"", fallback);
    }

    [Fact]
    public void BuildMetadataWithContentHash_RejectsUserReservedMetadata()
    {
        Assert.Throws<InvalidOperationException>(() => AzureBlobObjectStore.BuildMetadataWithContentHash(
            new Dictionary<string, string> { [AzureBlobObjectStore.PortableContentHashMetadataKey] = "user" },
            "sha256:abc"));
    }

    [Fact]
    public void BuildMetadataWithContentHash_RejectsNonPortableUserMetadata()
    {
        Assert.Throws<InvalidOperationException>(() => AzureBlobObjectStore.BuildMetadataWithContentHash(
            new Dictionary<string, string> { ["bad-key"] = "user" },
            "sha256:abc"));
    }

    [Theory]
    [InlineData(412, "ConditionNotMet", true)]
    [InlineData(409, "BlobAlreadyExists", true)]
    [InlineData(404, "BlobNotFound", false)]
    public void IsPreconditionFailure_RecognizesConditionalFailures(int status, string errorCode, bool expected)
    {
        var exception = new RequestFailedException(status, "failure", errorCode, innerException: null);

        Assert.Equal(expected, AzureBlobObjectStore.IsPreconditionFailure(exception));
    }
}
