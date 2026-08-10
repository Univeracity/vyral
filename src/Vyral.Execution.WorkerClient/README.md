# Vyral.Execution.WorkerClient

`Vyral.Execution.WorkerClient` is the supported .NET transport and plugin host for Vyral's
portable external-worker contract. It covers leasing, heartbeats, checkpoints, progress reports,
trace events, artifacts, durable waits, completion, and execution of ordinary consumer-owned
`IExecutionPlugin` handlers. It does not poll a provider queue or prescribe a Lambda, Cloud Run,
container, or process host.

```csharp
IExecutionWorkerTransport transport = new ExecutionWorkerClient(
    httpClient,
    new ExecutionWorkerClientOptions
{
    BaseUri = new Uri("https://vyral.example/"),
    WorkerId = "product-worker",
    HandlerIds = ["product.example.job"],
    TokenSource = new GoogleMetadataOidcTokenSource("https://vyral.example/")
});

IExecutionPlugin plugin = ProductExecutionPlugin.Create();
var worker = new ExecutionPluginWorker(transport, [plugin]);

// Long-running polling host:
await worker.RunAsync(ct);

// Queue-triggered/serverless host instead supplies the opaque run id:
// await worker.RunOnceAsync(deliveredRunId, ct);
```

The API host registers each plugin's handler descriptors with its external-worker adapter. The
worker loads the same plugin package, while `ExecutionPluginWorker` adapts its normal
`IExecutionRunContext` calls to the external protocol. `InProcessExecutionWorkerTransport` offers
the same boundary for an embedded worker or deterministic test; remote workers use
`ExecutionWorkerClient` over HTTP.

The host heartbeats while a handler runs. A cancellation observed in a heartbeat cancels the
handler and completes the run as cancelled. The first durable wait call persists and suspends the
run without completing it; after wake-up, the handler is replayed and receives the stored wait
outcome. Plugin work before a wait must therefore remain idempotent and should checkpoint before
suspension. Unexpected handler exception text is not sent to Vyral; the run receives a fixed
portable failure diagnostic.

The external protocol currently maps progress, events, artifacts, checkpoints, and durable waits.
It does not expose handler-side coordination leases, standalone timer creation, or raising an
external event from the worker context; those calls fail explicitly. A plugin that requires those
operations needs an in-process adapter until the external protocol advertises and qualifies them.

Lease tokens are bearer credentials. The client sends them only in request bodies; they never
appear in request URLs, exception messages, or `ExecutionWorkerClientTelemetry`. Telemetry exposes
only a sanitized error category, never a raw exception or response body, and the client does not
mutate caller-owned event or wait request objects. Use a token source appropriate to the deployment
(Google metadata OIDC is included as one option); the worker protocol and plugin host themselves
are provider-agnostic.
