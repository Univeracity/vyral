# Vyral.Execution

`Vyral.Execution` is the stable consumer contract for portable durable work in Vyral.

Current version label: `0.2.0`.

Design plugins against this package when handler code should move between local SQLite, Azure
Durable Functions, AWS, Google Cloud, and future provider-backed adapters without depending on
provider SDKs.

## Release Policy

The `0.2.0` package line freezes the reviewed portable consumer contract. Its compatibility
boundary is:

- plugin contracts, handler contracts, run requests/results, retry policy, history events,
  artifacts, leases, timers, external events, durable waits, cancellation, and adapter status models are the
  reviewed consumer surface
- payload and result schemas remain plugin-owned
- adapter-specific persistence, scheduling, host packaging, and cloud resource wiring remain
  provider-specific; consumers must use adapter capabilities and operational policy instead of
  assuming a provider's native behavior
- repository and package metadata declare Apache-2.0; see the repository
  [LICENSE](https://github.com/univeracity/vyral/blob/main/LICENSE)

Package consumers should pin a compatible `0.2.x` version and run the package-consumer and
provider live gates before adopting an update. The public
[execution runtime limitations](https://github.com/univeracity/vyral/blob/main/docs/reference/execution-runtime-limitations.md)
guide describes the provider
and operations boundaries.

## Stable Consumer Surface

- `IExecutionPlugin`, `IExecutionHandler`, `IExecutionRunContext`, `IExecutionRuntime`, and
  `IExecutionRuntimeAdapter`; hosts can compose provider adapters through
  `IExecutionRuntimeAdapterFactory`, and runtimes that support explicit cleanup/recovery implement
  `IExecutionRuntimeMaintenance`. `IExternalExecutionWorkerRuntime` is the opaque-token lease,
  heartbeat, progress, trace-event, artifact, checkpoint-read/write, durable-wait, and replay-safe
  completion protocol for workers implemented outside .NET.
- run, request, result, retry, status, trace, artifact, checkpoint, lease, timer, and
  external-event models
- `ExecutionDescriptors`, descriptor builders, `DelegateExecutionHandler`, and
  `StaticExecutionPlugin`
- `ExecutionContractValidator`, `ExecutionRunLifecycle`, `ExecutionRuntimeLimits`,
  `ExecutionJson`, status/failure/capability constants, and capability catalog helpers

Runtime adapters expose artifact listing plus `GetArtifactAsync(runId, artifactRef)`, where
`artifactRef` may be the artifact id or artifact name.
`ExecutionRunQuery` supports portable filters for handler id, plugin id, status, correlation id,
idempotency key, created/updated ranges, and tags.
Run listing is a bounded operator view rather than an exhaustive reporting or pagination API; use
a run id or a domain-owned projection for correctness-critical discovery.
Handlers can persist plugin-owned resume state with `PutCheckpointAsync`; callers can inspect it
with `GetCheckpointAsync(runId, key)`.
Handlers can record structured, layered inspection entries with `LogAsync`, `LogInfoAsync`, and
`LogWarningAsync`. Adapters persist those as normal execution history events with bounded JSON
details.
Maintenance-capable runtimes expose `GetMaintenanceStatusAsync`, `PruneAsync`, and
`ReconcileDispatchAsync`. Reconciliation redrives durable runnable work after an interrupted
state-write/queue-enqueue boundary; remote deployments should invoke it from a trusted scheduler.
Prune requests default to dry-run so consumers can inspect retained-terminal-run cleanup before
deleting state.

Plugins should compile against `Vyral.Execution` plus their own domain dependencies. They should
not reference local SQLite, Azure Durable Functions, Vyral server job adapters, retrieval,
embedding, graph, provider, or storage assemblies unless they are deliberately implementing those
domain surfaces.

## Runtime Boundary

The runtime owns run lifecycle, handler dispatch, retries, cancellation, idempotency, bounded
status, traces, artifacts, leases, timers, external events, and durable waits. Plugins own payload and result
schemas, validation, side-effect idempotency, checkpoints, and domain projection.

Use `GetAdapterStatusAsync` or caller-scoped `/execution/runtime/effective` to inspect capabilities
and limits before a consumer relies on optional behavior. `/execution/runtime` is the global
operator catalog in shared deployments. Portable code branches on capabilities, not adapter names.
External worker support is advertised with `external.workers`; API hosts register external handler
descriptors, while the adapter does not execute their .NET implementation in the API process.
`in_process.handlers` means registered .NET handler delegates execute in the current host. An
adapter must advertise at least one execution model; consumers must not assume `RegisterHandler`
is executable when only `external.workers` is present.
`Vyral.Execution.WorkerClient.ExecutionPluginWorker` is the supported bridge for loading an
ordinary portable plugin in an external process. It maps the handler's progress, events, artifacts,
checkpoints, durable waits, and result through the worker protocol, so the plugin itself keeps only
its `Vyral.Execution` dependency. The current protocol does not expose handler-side coordination
leases, standalone timer creation, or event raising; those context calls fail explicitly.
Adapters that advertise `durable.waits` implement replay-oriented
`WaitForExternalEventAsync` and `WaitForTimerAsync`: the initial call suspends the current handler
after persisting a wait, and a later replay returns `ExecutionWaitResult`. Persist checkpoints
before waiting and make code before a wait idempotent.

## Failure Classes

Execution run status is the authority for lifecycle decisions. `failureClass` is a portable
diagnostic and retry hint.

Built-in adapter classes:

| Class | Meaning |
| --- | --- |
| `cancelled` | Cancellation was requested and the handler observed it. |
| `handler_missing` | The requested handler was not registered in the adapter. |
| `plugin_mismatch` | The request named a plugin that does not own the handler. |
| `idempotency_conflict` | The idempotency key belongs to a different request shape. |
| `queue_full` | The adapter refused new work because active-run limits were reached. |
| `timeout` | A runtime or handler timeout elapsed. |
| `transient` | A retryable dependency or pressure condition was reported. |
| `validation` | The handler rejected malformed or unsupported payload shape. |
| `platform` | The adapter or host failed outside plugin-controlled work. |
| `unknown` | The adapter could not classify an exception safely. |

Plugins may emit more specific classes in `ExecutionRunResult.Failed(...)`, but they should keep
them stable, lower-case, and domain-owned. Consumers should branch first on terminal status, then on
known failure classes for retry/cancel/escalation behavior, and display unknown plugin classes as
diagnostics rather than provider facts.

## Validation

Consumer confidence checks:

```bash
scripts/validate-execution-runtime-package-consumer.sh
scripts/validate-execution-runtime-preview-handoff.sh
dotnet test tests/Vyral.Tests.Conformance/Vyral.Tests.Conformance.csproj --no-restore
dotnet test tests/Vyral.Tests.Local/Vyral.Tests.Local.csproj --no-restore --filter ExecutionRuntime
dotnet test tests/Vyral.Tests.Azure/Vyral.Tests.Azure.csproj --no-restore --filter AzureDurableExecution
dotnet test tests/Vyral.Tests.Google/Vyral.Tests.Google.csproj --no-restore --filter GoogleCloudExecutionRuntimeAdapterConformanceTests
dotnet run --project samples/Vyral.Execution.LocalSample/Vyral.Execution.LocalSample.csproj -- --once
(cd clients/go && go test ./...)
(cd workers/execution-smoke-go && go test ./...)
deploy/test-preflight-google-execution.sh
```

The shared conformance suite is the portability gate. New adapter behavior should be promoted into
that suite when it becomes part of the provider-neutral contract. Adapters that advertise
`external.workers` must also run `ExternalExecutionWorkerRuntimeConformanceTests`; this verifies
portable claim, progress, artifact, trace, checkpoint, durable-wait, and completion behavior
without requiring a provider SDK or live cloud account.

`GoogleExecutionLiveTests` is an additional opt-in deployment gate for a temporary Firestore
root, Cloud Tasks queue, and Cloud Run target. Run `scripts/validate-google-execution-live.sh` for
its environment contract and cleanup safeguards; `Vyral.Google` documents its deliberate paused
queue and the separate disposable-worker delivery qualification.
