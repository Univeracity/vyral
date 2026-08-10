# Execution Runtime Plugin Authoring

This guide is for Vyral consumers writing work that should run through the provider-neutral
execution runtime. A plugin should be able to move from local SQLite to Azure Durable Functions or
another future adapter without changing its handler code for normal lifecycle behavior.

## Stable Authoring Surface

Design against these contracts:

- `IExecutionPlugin` groups one or more handlers under a plugin id and version.
- `IExecutionHandler` owns one executable unit of work.
- `ExecutionRunRequest` starts a run with handler id, optional plugin id, payload, idempotency
  key, retry policy, schedule time, correlation id, and tags.
- `IExecutionRunContext` is the only runtime object a handler should use for progress, artifacts,
  trace events, leases, durable waits, timers, external events, and cancellation.
- `ExecutionRunResult` is the terminal result returned by a handler.

Do not design a plugin around a concrete adapter SDK. Local SQLite, Azure Durable Functions, and
future adapters are implementation details behind `IExecutionRuntimeAdapter`.

## Consumer Package Boundary

The stable consumer contract is the `Vyral.Execution` namespace:

- plugin and handler interfaces
- run, request, result, retry, status, trace, artifact, lease, timer, and event models
- descriptor builders and simple delegate/static plugin helpers
- lifecycle, validation, status, failure, capability, JSON, and limit helpers

`Vyral.Tests.Conformance` is test support. It is appropriate for plugin and adapter tests, but
plugins should not reference it at runtime.

Concrete adapters are narrower implementation packages:

- `Vyral.Execution.Local` is the local SQLite adapter for development, tests, and local
  deployments.
- `Vyral.Execution.AzureDurable` is the Azure Durable Functions adapter. Its adapter and options
  are intended for host integration; its dialect, host bridge, status store, scheduler, and driver
  remain provider-specific host components rather than portable plugin dependencies.

Consumers should not reference Vyral server job adapters, retrieval/provider/graph assemblies, or
adapter SDK types unless they are deliberately building those domain surfaces. A portable plugin
should compile against `Vyral.Execution` and its own domain dependencies.

## Portable Baseline

The baseline capabilities for a portable runtime adapter are:

- `durable.runs`
- `cancellation`
- `retries`
- `artifacts`
- `trace.history`
- `idempotency`

Every adapter also reports one dispatch model: `local.dispatch` or `remote.orchestration`.
Branch on capabilities before requiring optional behavior such as `durable.timers`,
`external.events`, `durable.waits`, `leases`, or `restart.resume`.

Use caller-scoped `/execution/runtime/effective` or in-process `GetAdapterStatusAsync` to inspect:

- adapter capabilities
- `operationalPolicy` limits
- `resumePolicy` behavior
- registered plugin and handler descriptors

## Plugin Boundary

The runtime owns:

- run lifecycle and status transitions
- handler registration and dispatch
- retries, cancellation, idempotency, bounded status, traces, artifacts, leases, durable waits,
  timers, and events

The plugin owns:

- payload and result schemas
- domain validation
- side-effect idempotency
- checkpoint interpretation
- artifact meaning
- mapping runtime runs back into domain-specific job DTOs

The runtime persists `JsonNode` payloads, results, and status details as opaque portable data. It
does not interpret domain records, provider requests, embeddings, graphs, or ingestion-specific
state.

## Descriptor Rules

Use `ExecutionDescriptors` when possible. It validates ids, handler ownership, retry ceilings, tag
shape, and size limits before a plugin reaches an adapter.

Handler descriptors should include:

- stable `handlerId`
- owning `pluginId`
- short display name
- optional description
- `MaxAttempts`
- optional `ConcurrencyKey` when two handlers must not run at the same time
- stable tags for operator filtering

Registered handler descriptors are authoritative. A run can omit `pluginId` and let the runtime
derive it from the handler descriptor. A conflicting `pluginId` is rejected as a portable contract
error.

## Payloads And Idempotency

Use payloads as commands, not as handles to live process state. A payload should be serializable,
bounded, and sufficient for a handler to validate what it has been asked to do.

Set an idempotency key when the caller wants one logical request to map to one run. The idempotency
scope is handler id, derived plugin id, and payload hash. Reusing a key with a different logical
request is a contract error.

Handlers must assume at-least-once execution when `restart.resume` is available. If a handler
writes external state, it should protect side effects with one of these:

- domain-side idempotent writes
- run-owned or domain-owned leases
- plugin-owned checkpoints in status details or durable storage
- artifact/result comparison before repeating a publish step

## Progress And Status

Use `ReportAsync` for operator-visible run state:

- `Requested`
- `Attempted`
- `Succeeded`
- `Failed`
- `Progress`
- `CurrentStep`
- `StatusDetails`

Keep `StatusDetails` compact and machine-readable. It is suitable for current item, checkpoint,
warning, heartbeat, or advisory summaries. It is not a log stream.

Use `RecordEventAsync` for history. Trace events are bounded and should describe transitions or
interesting steps, not every byte of handler output.

## Artifacts

Use artifacts for data that should be retained with the run:

- summaries
- advisory reports
- normalized result envelopes
- checkpoint snapshots intended for inspection
- references to larger external objects

Small JSON artifacts can be inline. Larger artifacts should use adapter-supported artifact storage
and carry a hash, size, URI, and metadata when available.

## Retry And Resume

Return `ExecutionRunResult.Failed(...)` when runtime retry policy should apply. Return an explicit
`ExecutionRunResult` with `Status = ExecutionRunStatuses.TimedOut` for timeout classification.
Return an explicit `ExecutionRunResult` with `Status = ExecutionRunStatuses.Rejected` only for a
request-level rejection that should be terminal. Invalid payloads should usually be validated
before side effects and paired with `MaxAttempts = 1` unless retry is meaningful.

Resume has a narrow runtime meaning: an adapter can recover active persisted work after restart.
The runtime may re-execute the handler. The runtime does not interpret plugin checkpoints or
create follow-up runs.

For an external decision that belongs to one logical run, first check for `durable.waits`, write a
plugin-owned checkpoint/artifact, then call `WaitForExternalEventAsync`. The first call persists
the wait and suspends the run without holding a worker slot. A matching event, timeout, or timer
causes a replay, where the same call returns `ExecutionWaitResult`. Code before the wait can run
again, so it must be idempotent and should restore its checkpoint before branching on the outcome.

Use a linked follow-up run instead when the decision must be a separately auditable domain
transition. Do not keep a handler sleeping while an external system responds.

## Complete Sample

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;

public sealed class SampleBatchPlugin : IExecutionPlugin
{
    public const string PluginId = "sample.batch";
    public const string HandlerId = "sample.batch.count";

    public SampleBatchPlugin()
    {
        Handlers = new[] { new SampleBatchHandler() };
        Descriptor = ExecutionDescriptors.Plugin(
            PluginId,
            "Sample batch",
            "1.0.0",
            plugin => plugin.AddHandler(Handlers[0].Descriptor));
    }

    public ExecutionPluginDescriptor Descriptor { get; }
    public IReadOnlyList<IExecutionHandler> Handlers { get; }

    private sealed class SampleBatchHandler : IExecutionHandler
    {
        public ExecutionHandlerDescriptor Descriptor { get; } = ExecutionDescriptors.Handler(
            HandlerId,
            "Count sample items",
            handler => handler
                .WithPluginId(PluginId)
                .WithDescription("Counts a bounded item list and writes a summary artifact.")
                .WithMaxAttempts(2)
                .WithConcurrencyKey("sample.batch")
                .WithTag("sample.kind", "batch"));

        public async Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default)
        {
            var payload = context.Run.Payload?.Deserialize<SampleBatchPayload>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("Sample batch payload is required.");
            var items = payload.Items ?? new List<string>();

            await context.ReportAsync(new ExecutionRunUpdate
            {
                Requested = items.Count,
                Attempted = 0,
                Succeeded = 0,
                Failed = 0,
                Progress = 0,
                StatusDetails = new JsonObject
                {
                    ["phase"] = "started",
                    ["itemCount"] = items.Count
                }
            }, ct);

            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                unique.Add(items[i]);
                await context.ReportAsync(new ExecutionRunUpdate
                {
                    Attempted = i + 1,
                    Succeeded = i + 1,
                    Progress = items.Count == 0 ? 1 : (i + 1) / (double)items.Count,
                    CurrentStep = $"item:{i}",
                    StatusDetails = new JsonObject
                    {
                        ["phase"] = "running",
                        ["currentIndex"] = i
                    }
                }, ct);
            }

            var result = new JsonObject
            {
                ["itemCount"] = items.Count,
                ["uniqueCount"] = unique.Count
            };

            await context.PutArtifactAsync(new ExecutionArtifactWrite
            {
                Name = "sample-batch-summary",
                Kind = ExecutionArtifactKinds.Json,
                Content = result.DeepClone()
            }, ct);

            return ExecutionRunResult.Succeeded(
                result,
                new JsonObject
                {
                    ["phase"] = "completed",
                    ["itemCount"] = items.Count,
                    ["uniqueCount"] = unique.Count
                });
        }
    }

    private sealed class SampleBatchPayload
    {
        public List<string>? Items { get; set; }
    }
}
```

Register the plugin once at host startup:

```csharp
runtime.RegisterPlugin(new SampleBatchPlugin());
```

Start a run through the runtime boundary:

```csharp
var run = await runtime.StartRunAsync(new ExecutionRunRequest
{
    HandlerId = SampleBatchPlugin.HandlerId,
    PluginId = SampleBatchPlugin.PluginId,
    IdempotencyKey = "sample-batch:alpha",
    Payload = new JsonObject
    {
        ["items"] = new JsonArray("a", "b", "b")
    },
    RetryPolicy = new ExecutionRetryPolicy
    {
        MaxAttempts = 2
    },
    Tags =
    {
        ["sample.kind"] = "batch"
    }
}, ct);
```

## Minimal Local-First Adoption

1. Reference `Vyral.Execution` from the plugin assembly.
2. Implement one `IExecutionPlugin` with stable handler ids and bounded payload/result JSON.
3. Unit test the handler with `ExecutionPluginTestHarness`.
4. In local development, create a `LocalExecutionRuntime` with a SQLite database path and register
   the plugin at startup.
5. Start work with `ExecutionRunRequest`, setting `IdempotencyKey` when callers may retry the same
   logical request.
6. Poll `GetRunAsync`, `GetHistoryAsync`, and `ListArtifactsAsync`, or use the server
   `/execution/runtime/effective`, `POST /execution/runs`, `/execution/runs`,
   `/execution/runs/{runId}/history`, and
   `/execution/runs/{runId}/artifacts` endpoints when hosted through Vyral.
7. Route cancellation through `CancelRunAsync`; handlers should observe the cancellation token
   before expensive work and between batch items.
8. Move to Azure or a future adapter by swapping the registered `IExecutionRuntimeAdapter`.
   Handler code should not change if it only used `IExecutionRunContext` and branched on declared
   capabilities.

Local runtime setup:

```csharp
using Vyral.Execution;
using Vyral.Execution.Local;

IExecutionRuntimeAdapter runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
{
    DatabasePath = "var/execution-runtime.sqlite",
    MaxActiveRuns = 100,
    MaxRetainedTerminalRuns = 500
});

runtime.RegisterPlugin(new SampleBatchPlugin());
```

Adapter switching should keep consumer code at this boundary:

```csharp
public static async Task<ExecutionRun> StartPortableRunAsync(
    IExecutionRuntimeAdapter runtime,
    JsonObject payload,
    CancellationToken ct)
{
    var status = await runtime.GetAdapterStatusAsync(ct);
    if (!status.Adapter.Capabilities.Contains(ExecutionCapabilityIds.Artifacts))
    {
        throw new InvalidOperationException("This plugin requires run artifacts.");
    }

    return await runtime.StartRunAsync(new ExecutionRunRequest
    {
        HandlerId = SampleBatchPlugin.HandlerId,
        PluginId = SampleBatchPlugin.PluginId,
        Payload = payload,
        IdempotencyKey = "sample-batch:alpha",
        RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 2 }
    }, ct);
}
```

## Consumer Error Semantics

Consumer-facing failures use two shapes:

| Case | Consumer-visible behavior |
| --- | --- |
| malformed request, oversized payload, invalid retry policy | `StartRunAsync` throws `InvalidOperationException` before scheduling |
| missing handler | terminal `rejected` run with `failureClass=handler_missing` |
| conflicting plugin id | terminal `rejected` run with `failureClass=plugin_mismatch` |
| idempotency key reused for different handler/plugin/payload | `StartRunAsync` throws `InvalidOperationException` |
| unknown run cancellation | `CancelRunAsync` returns `null` |
| active cancellation | terminal `cancelled` run with `failureClass=cancelled` |
| terminal cancellation request | existing terminal run is returned unchanged |
| handler returns failed result and retries remain | run returns to `waiting` and records `retry.scheduled` |
| retry attempts exhausted | terminal `failed` run with the handler-provided failure class |
| handler returns timed-out result | terminal or retryable `timed_out` run according to retry policy |
| unhandled handler exception | terminal `failed` run with `failureClass=unknown` and bounded error text |

## Test Guidance

Use `ExecutionPluginTestHarness` for backend-free handler tests. Use
`ExecutionRuntimeConformanceTests` when implementing or changing adapters. Provider-specific tests
can add dialect coverage, but they should not replace the shared conformance suite.

Minimum plugin tests should cover:

- descriptor validation
- payload validation
- progress and status details
- artifact writes
- cancellation
- idempotent side effects or checkpoint recovery
- retry classification
- durable-wait replay and timeout behavior when the plugin requires `durable.waits`

## Consumer Checklist

Before a plugin is treated as portable:

- The handler does not reference adapter SDK types.
- The payload and result fit `ExecutionRuntimeLimits`.
- The idempotency key strategy is documented.
- External side effects are idempotent or guarded.
- Status details are bounded and useful for operators.
- Artifacts are run-owned and have stable names.
- Optional capabilities are checked before use.
- The plugin passes a backend-free handler test and at least one runtime adapter conformance path.
