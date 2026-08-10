using System.Net;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Execution.WorkerClient;
using WorkerClient = Vyral.Execution.WorkerClient.ExecutionWorkerClient;

namespace Vyral.Tests.ExecutionWorkerClient;

public sealed class ExecutionWorkerClientTests
{
    [Fact]
    public async Task Client_UsesEveryWorkerRouteWithoutLeakingLeaseOrBearerTokens()
    {
        var requests = new List<(string Path, string Authorization, string Body)>();
        var telemetry = new List<ExecutionWorkerClientTelemetry>();
        using var client = new HttpClient(new DelegateHandler(async request =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
            requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString() ?? string.Empty, body));
            return request.RequestUri.AbsolutePath switch
            {
                "/execution/workers/leases" => Json(HttpStatusCode.OK, """{"leaseKey":"lease-a","leaseToken":"lease-secret","workerId":"worker-a","run":{"id":"run-a","handlerId":"handler-a","attempt":1,"status":"running"}}"""),
                "/execution/workers/leases/heartbeat" => Json(HttpStatusCode.OK, """{"leaseKey":"lease-a","leaseToken":"lease-secret","workerId":"worker-a","run":{"id":"run-a","attempt":1,"status":"running"}}"""),
                "/execution/workers/leases/checkpoints" => Json(HttpStatusCode.OK, """{"runId":"run-a","key":"progress","contentHash":"sha256:test","updatedAtUtc":"2026-01-01T00:00:00Z"}"""),
                "/execution/workers/leases/checkpoints/read" => Json(HttpStatusCode.OK, """{"runId":"run-a","key":"progress","contentHash":"sha256:test","updatedAtUtc":"2026-01-01T00:00:00Z"}"""),
                "/execution/workers/leases/reports" => Json(HttpStatusCode.OK, """{"id":"run-a","attempt":1,"status":"running"}"""),
                "/execution/workers/leases/events" => new HttpResponseMessage(HttpStatusCode.NoContent),
                "/execution/workers/leases/artifacts" => Json(HttpStatusCode.OK, """{"id":"artifact-a","runId":"run-a","name":"summary","kind":"text","contentHash":"sha256:test","sizeBytes":2,"createdAtUtc":"2026-01-01T00:00:00Z"}"""),
                "/execution/workers/leases/wait" => Json(HttpStatusCode.OK, """{"run":{"id":"run-a","attempt":1,"status":"waiting"},"suspended":true}"""),
                "/execution/workers/leases/complete" => Json(HttpStatusCode.OK, """{"id":"run-a","attempt":1,"status":"succeeded"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        })) { BaseAddress = new Uri("https://ignored.example/") };
        var worker = new WorkerClient(client, new ExecutionWorkerClientOptions
        {
            BaseUri = new Uri("https://vyral.example/"),
            WorkerId = "worker-a",
            HandlerIds = ["handler-a"],
            TokenSource = new DelegateExecutionWorkerTokenSource(_ => Task.FromResult("identity-secret")),
            Observe = telemetry.Add
        });

        var lease = Assert.IsType<ExecutionExternalWorkerLease>(await worker.LeaseNextAsync("run-a", 60));
        await worker.HeartbeatAsync(lease, 60);
        await worker.CheckpointAsync(lease, new ExecutionCheckpointWrite { Key = "progress", Content = new JsonObject { ["position"] = 1 } });
        await worker.GetCheckpointAsync(lease, "progress");
        await worker.ReportAsync(lease, new ExecutionRunUpdate { CurrentStep = "prepare" });
        var eventRequest = new ExecutionExternalWorkerEventRequest { Type = ExecutionEventTypes.Log, Message = "safe" };
        await worker.RecordEventAsync(lease, eventRequest);
        await worker.PutArtifactAsync(lease, new ExecutionArtifactWrite { Name = "summary", Text = "ok" });
        var waitRequest = new ExecutionExternalWorkerWaitRequest { Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent, Name = "approval", TimeoutAtUtc = DateTime.UtcNow.AddMinutes(1) };
        await worker.WaitAsync(lease, waitRequest);
        await worker.CompleteAsync(lease, ExecutionRunResult.Succeeded());

        Assert.Equal(9, requests.Count);
        Assert.All(requests, request => Assert.Equal("Bearer identity-secret", request.Authorization));
        Assert.DoesNotContain("lease-secret", requests[0].Body, StringComparison.Ordinal);
        Assert.All(requests.Skip(1), request => Assert.Contains("lease-secret", request.Body, StringComparison.Ordinal));
        Assert.All(telemetry, item =>
        {
            Assert.DoesNotContain("secret", item.Path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", item.RunId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", item.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Empty(eventRequest.LeaseToken);
        Assert.Empty(waitRequest.LeaseToken);
    }

    [Fact]
    public async Task Client_RedactsUnsuccessfulResponseBodiesAndMetadataTokens()
    {
        var telemetry = new List<ExecutionWorkerClientTelemetry>();
        using var client = new HttpClient(new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("reverse-proxy echoed lease-secret and identity-secret")
        })));
        var worker = new WorkerClient(client, new ExecutionWorkerClientOptions
        {
            BaseUri = new Uri("https://vyral.example/"),
            WorkerId = "worker-a",
            HandlerIds = ["handler-a"],
            TokenSource = new DelegateExecutionWorkerTokenSource(_ => Task.FromResult("identity-secret")),
            Observe = telemetry.Add
        });

        var exception = await Assert.ThrowsAsync<ExecutionWorkerClientException>(() => worker.LeaseNextAsync("run-a"));
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
        var observed = Assert.Single(telemetry);
        Assert.Equal("http_failure", observed.Error);

        var metadataCalls = new List<(string Query, string Header)>();
        using var metadataClient = new HttpClient(new DelegateHandler(async request =>
        {
            metadataCalls.Add((request.RequestUri!.Query, request.Headers.GetValues("Metadata-Flavor").Single()));
            return Json(HttpStatusCode.OK, "metadata-identity-token");
        }));
        var source = new GoogleMetadataOidcTokenSource("https://vyral.example/", metadataClient, "https://metadata.example/identity?format=full");
        var token = await source.GetTokenAsync();
        Assert.Equal("metadata-identity-token", token);
        var metadataRequest = Assert.Single(metadataCalls);
        Assert.Equal("Google", metadataRequest.Header);
        Assert.Contains("audience=https%3A%2F%2Fvyral.example%2F", metadataRequest.Query, StringComparison.Ordinal);

        using var oversizedClient = new HttpClient(new DelegateHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, new string('x', 16 * 1024 + 1)))));
        var oversizedSource = new GoogleMetadataOidcTokenSource("https://vyral.example/", oversizedClient, "https://metadata.example/identity");
        var oversized = await Assert.ThrowsAsync<InvalidOperationException>(() => oversizedSource.GetTokenAsync());
        Assert.DoesNotContain("xxxxx", oversized.Message, StringComparison.Ordinal);

        var tokenFailureTelemetry = new List<ExecutionWorkerClientTelemetry>();
        using var tokenFailureClient = new HttpClient(new DelegateHandler(_ => throw new InvalidOperationException("HTTP should not be called")));
        var tokenFailureWorker = new WorkerClient(tokenFailureClient, new ExecutionWorkerClientOptions
        {
            BaseUri = new Uri("https://vyral.example/"),
            WorkerId = "worker-a",
            HandlerIds = ["handler-a"],
            TokenSource = new DelegateExecutionWorkerTokenSource(_ => throw new InvalidOperationException("identity-secret")),
            Observe = tokenFailureTelemetry.Add
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokenFailureWorker.LeaseNextAsync("run-a"));
        var tokenFailure = Assert.Single(tokenFailureTelemetry);
        Assert.Equal("transport_failure", tokenFailure.Error);
        Assert.DoesNotContain("secret", tokenFailure.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _send(request);
    }
}
