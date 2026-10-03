using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Vyral.Abstractions.Models;
using Vyral.Aws;
using Vyral.Cloudflare;
using Xunit;

namespace Vyral.Tests.Cloudflare;

public class R2TransportTests
{
    [Fact]
    public async Task EmptySdkV4CollectionIsAnEmptyPageWithNoContinuation()
    {
        using var client = new EmptyClient();
        var result = await new S3ObjectStore(client).ListObjectsAsync(new() { Container = "objects" });
        Assert.Empty(result.Items); Assert.Null(result.ContinuationToken);
    }

    [Fact]
    public async Task DefaultTransportDoesNotRetryAServiceUnavailableResponse()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var store = R2ObjectStore.Create(new() { ServiceUrl = $"http://127.0.0.1:{port}", AccessKeyId = "fixture", SecretAccessKey = "fixture" });
        var call = store.ListObjectsAsync(new() { Container = "objects" });
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
        context.Response.StatusCode = 503;
        context.Response.Close();
        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() => call);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        var secondRequest = listener.GetContextAsync();
        Assert.NotSame(secondRequest, await Task.WhenAny(secondRequest, Task.Delay(100)));
    }

    [Fact]
    public async Task PrecancelledPutPreservesCancellationAndSendsNoObject()
    {
        using var store = R2ObjectStore.Create(new() { ServiceUrl = "http://127.0.0.1:1", AccessKeyId = "fixture", SecretAccessKey = "fixture" });
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutObjectAsync(new() {
            Container = "objects", Key = "cancelled.txt", Content = new MemoryStream(new byte[] { 1,2,3 }) }, ct.Token));
    }

    [Theory]
    [InlineData(-1)] [InlineData(4)]
    public void RetryBudgetIsExplicitAndBounded(int retries) =>
        Assert.Throws<InvalidOperationException>(() => new CloudflareR2Options { AccessKeyId = "fixture", SecretAccessKey = "fixture", MaxErrorRetry = retries }.ValidateCredentials());

    private sealed class EmptyClient() : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" })
    {
        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken ct = default) =>
            Task.FromResult(new ListObjectsV2Response { S3Objects = null, IsTruncated = false });
    }
}
