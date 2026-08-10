# Vyral Execution Runtime

## Purpose

The execution runtime is a provider-neutral runtime boundary for durable, observable work. It
coordinates jobs, runs, steps, handlers, leases, retries, timers, external events, cancellation,
resumability, artifacts, traces, status, quotas, bounded logs, and idempotency.

It does not know about Vyral records, RAG, vectors, graphs, external business systems,
marketplace APIs, or service-specific pipeline internals. Those concerns live in plugins and
adapters.

## Terminology

Use `execution runtime` or `execution layer` for this subsystem in code and docs. Avoid using
`fabric` as the primary noun so future Microsoft Fabric support can be discussed as a provider or
adapter target without ambiguity.

## Requirements From Real-World Fixtures

A production-style Azure Functions fixture is useful because its entry point mixes many execution
concerns in one place. It was still being actively integrated after an initial successful
deployment, so its shapes are evidence that work can run, not canonical runtime shapes to copy:

- Hosted durable and local direct-run paths must be able to execute the same work.
- Timer starts need schedule windows, enable flags, singleton instance checks, and pressure gates.
- Orchestrators need fan-out/fan-in batches with bounded parallelism.
- Long-running polling loops need durable timers instead of process sleeps.
- Activities need structured summaries, partial progress, runtime budgets, and timeout warnings.
- Operators need recent status grouped per handler, not unbounded logs.
- Local development needs a storage-backed runner and restart-safe state.
- Idempotency is required for work items, snapshots, and status writes.
- Pipeline-like work should be testable as neutral windowed batches with bounded chunks,
  duplicate suppression, per-window progress, step events, and summary artifacts.
- Failure should be classified enough to drive retry/backoff without embedding domain policy in
  the runtime.
- Plugins should own fetch/normalize/persist/publish logic; the runtime should own run lifecycle.

Follow-up inspection of pipeline helpers found additional fixture pressure that is not
domain-specific:

- timer entrypoints combine schedule windows, enable flags, singleton instance checks, local/cloud
  mode gates, and storage-pressure deferral before work is scheduled.
- activity wrappers return compact structured summaries with pipeline run id, outcome, elapsed
  seconds, warning flags, per-window counts, error type/code, and sample metadata.
- pipeline runners track elapsed runtime budgets, emit warning thresholds before hard host limits,
  and record advisory artifacts from completed or failed runs.
- heartbeat/status rows are grouped per pipeline so high-frequency work does not crowd out slower
  lanes on operator surfaces.
- autotune state adjusts future window sizes or chunk plans from success/failure/runtime-warning
  observations.
- item progress tracking suppresses duplicate processing across retries and resume, with bounded
  retention.

The existing conformance fixture already covers generic windowed batches, bounded window
parallelism, duplicate suppression within a run, summary artifacts, idempotent replay, progress,
and history. The next ugly fixture should add budget-warning status, advisory artifacts,
heartbeat-like grouped status details, progress checkpoints across a retry/resume path, and
adaptive hints without importing marketplace, listing, or business-system concepts.

## Requirements fed from Vyral

Vyral currently has several async job shapes with similar lifecycle mechanics:

- provider runs
- embedding batch jobs
- RAG ingestion
- retrieval evaluation
- graph import and evaluation

These require:

- queued, running, waiting, succeeded, failed, cancelled, rejected, and timed-out statuses
- cancellation requested separately from terminal cancellation
- progress and item counters
- trace events and artifacts
- bounded list surfaces
- persistence for local runs
- retry and resume hooks without making retry policy provider-specific
- narrow adapters that project runtime runs into existing Vyral response models

## Boundary

The core runtime owns:

- `ExecutionRun` lifecycle and status transitions
- `ExecutionHandler` plugin registration and invocation
- portable request, status, artifact, trace, timer, event, lease, and retry limits
- idempotency keys
- cancellation signals
- retry metadata and attempt counters
- durable history events
- artifacts
- leases
- schedules and timers
- external event inboxes
- status/list/query surfaces

Plugins own:

- payload schema
- domain validation
- handler implementation
- result schema
- artifact meaning
- retry classification details
- Vyral or service-specific model projection

Portable plugins should also keep storage and projection I/O behind Vyral contracts when they need
provider mobility. Record, object, retrieval, RAG, graph, provider-run, and execution abstractions
are the consumer-facing boundary. Storage databases, object stores, business systems, analytics
systems, orchestration engines, and similar services are adapter targets behind that boundary. A
plugin may intentionally call a concrete SDK, but then that SDK is part of the plugin's deployment
contract rather than part of the portable execution runtime contract.

Interactive business-system adapters need a documented provider profile instead of new core runtime
concepts. That profile should state the supported query envelope subset,
create/upsert/patch semantics, optimistic concurrency and etag/version mapping, schema/table
mapping, lookup and choice field behavior, row/version metadata, materialized-view or projection
behavior, and authority mode: Vyral as source of truth, write-through, read-through, or
sync/projection coordinator. Analytics or lakehouse adapters should make the same authority and
freshness boundary explicit. None of those provider semantics belong in `Vyral.Execution`; handlers
should see only runtime runs, context, artifacts, traces, checkpoints, and capability/policy status.

Registered handler descriptors are authoritative for plugin ownership. A run may omit
`pluginId` and let the runtime derive it from the handler; a conflicting `pluginId` is rejected
as a portable contract error.

Handlers receive an `IExecutionRunContext` rather than an adapter-specific client. The context
lets plugins report progress, write artifacts, record trace events, acquire/release run-owned
leases, schedule run-owned timers, and raise run-owned events. That keeps plugin code portable:
local SQLite, Azure Durable Functions, or a future Temporal/Kubernetes adapter can implement the
same coordination surface without exposing backend-specific SDK types.

## External workers

`IExternalExecutionWorkerRuntime` is the portable protocol for a process that executes a
registered handler without loading a .NET `IExecutionPlugin`. It is intended for independently
deployed workers such as a Go Cloud Run service. Hosts register handler descriptors at deploy
time; an external worker can only claim handler ids from that host allowlist.

The protocol has nine operations: `LeaseNextRunAsync` atomically claims one due external-handler
run; `HeartbeatExternalLeaseAsync` renews an active lease;
`ReportExternalLeaseAsync` records bounded progress; `RecordExternalLeaseEventAsync` records a
bounded trace event; `PutExternalLeaseArtifactAsync` writes a run artifact;
`CheckpointExternalLeaseAsync` and `GetExternalLeaseCheckpointAsync` persist and recover plugin
state; `WaitExternalLeaseAsync` registers or consumes a durable wait; and
`CompleteExternalLeaseAsync` records a terminal result, including retry scheduling when allowed.
A lease request can specify a `runId` from a queue message or omit it to poll the worker's
allowlisted handlers.

The returned `leaseToken` is an opaque bearer secret. An adapter stores only its hash, must never
put it in trace events or logs, and requires the token and worker id for heartbeat, progress,
trace-event, artifact, checkpoint-read/write, completion, and wait registration. Completion with the same valid token is replay-safe after a terminal write; a
stale token is rejected after lease expiry and reclamation. A cancellation that races a successful
external completion resolves to `cancelled` (except a reported timeout), matching in-process
cancellation semantics.

The default server maps this protocol to authenticated JSON endpoints:

- `POST /execution/workers/leases`
- `POST /execution/workers/leases/heartbeat`
- `POST /execution/workers/leases/reports`
- `POST /execution/workers/leases/events`
- `POST /execution/workers/leases/artifacts`
- `POST /execution/workers/leases/checkpoints`
- `POST /execution/workers/leases/checkpoints/read`
- `POST /execution/workers/leases/wait`
- `POST /execution/workers/leases/complete`

`WaitExternalLeaseAsync` supports `external_event` and `timer`. The first call persists the
wait and returns `suspended=true`, which releases the lease. When the run is leased again, repeat
the same wait request: the response returns `suspended=false` with the one-time durable outcome.
An approval service raises the event through `POST /execution/runs/{runId}/events`. This gives an
external worker the same checkpoint/replay pattern as a .NET handler without holding a Cloud Run
request open.

External handler descriptors are host configuration, not worker-provided code. The server
reads `ExecutionRuntime:ExternalHandlers` entries with `HandlerId`, optional `PluginId`,
`DisplayName`, `Description`, `MaxAttempts`, and `ConcurrencyKey`. Cloud Run deployments should
protect these routes with service-to-service authentication and map the verified service identity
to the protocol worker id; an opaque lease token is not a substitute for authenticating the
caller.

For the Google adapter, every external handler also requires an explicit
`ExecutionRuntime:Google:WorkerRoutes` entry. A route supplies the Cloud Tasks queue and Cloud
Run target (with optional per-handler overrides). The adapter rejects a run that has only an
in-process server delegate or an external handler without a route: accepting it would leave work
queued after a Cloud Tasks delivery is acknowledged by the wrong worker. Worker polling uses a
per-handler Firestore runnable-work subcollection rather than scanning all runs.

## Product and tenant boundary

Runs may carry `ExecutionScope` (`productId`, `tenantId`, and an optional service identity).
`ExecutionProductPolicy` is the portable configured boundary shape: per-product handler and tenant
allowlists, tighter payload limits, artifact-name prefixes, external-worker service-identity
allowlists, and named top-level JSON properties redacted before trace persistence. Local and Google
adapters enforce this same shape. Scope values are copied into durable trace context so the run
history is an audit trail. Product policy configuration is opt-in; when any policy is configured,
new runs must include a recognized product and tenant scope.

Service identity values must come from a host's verified authentication layer (for example, a
validated Cloud Run OIDC principal), not from an untrusted JSON body. The server's shared policy
depends on an `IExecutionIdentityAuthenticator`, so Google OIDC, Azure Entra, AWS IAM, mTLS, or a
host-specific verifier can bind a stable identity to `scope.serviceIdentity` without changing product,
tenant, handler, operation, or worker-id policy. The Google OIDC and development-header
authenticators are built in; a trusted deployment may select another parameterless authenticator
with `Server:ExecutionAccess:AuthenticatorType`. The development-header mode exists only for local
Development tests and refuses to run outside that environment.

Plugin authors should use the neutral authoring helpers in `Vyral.Execution`:

- `ExecutionDescriptors` and descriptor builders for validated plugin/handler descriptors.
- `DelegateExecutionHandler` and `StaticExecutionPlugin` for simple plugin composition.
- `ExecutionPluginTestHarness` from `Vyral.Tests.Conformance` for backend-free handler tests.

See `design/execution-runtime-plugin-authoring.md` for versioning, payload/result validation, and
test guidance.

For consumer adoption, treat `Vyral.Execution` as the stable contract surface. `Vyral.Execution`
contains the interfaces, models, descriptors, lifecycle helpers, validators, status/failure ids,
JSON options, and limits that portable plugins should design against. `Vyral.Execution.Local` and
`Vyral.Execution.AzureDurable` are adapter packages. Their runtime adapter/options entrypoints are
usable by hosts, while provider-specific host bridges, dialect helpers, storage implementations,
and future cloud SDK wiring remain adapter-preview details rather than plugin dependencies.

Hosts that need to select a provider at deployment time can configure a trusted,
parameterless `IExecutionRuntimeAdapterFactory` through
`ExecutionRuntime:FactoryType`. The factory receives the selected adapter id and a flattened
settings map; it owns provider SDK construction and validation. This allows an Azure, AWS, or
other adapter package to join the Vyral server without changing its built-in runtime switch.

Adapters own:

- local SQLite execution
- Azure Durable Functions orchestration and activity mapping
- AWS DynamoDB/SQS external-worker execution
- Google Firestore/Cloud Tasks external-worker execution
- future Kubernetes or Temporal mapping
- provider-specific lease, timer, and event implementation details

`GoogleCloudExecutionRuntimeAdapter` uses `FirestoreExecutionStateStore` for portable run,
history, checkpoint, artifact, timer, external-event, and transactional lease documents, plus
`GoogleCloudExecutionDispatcher` for idempotent Cloud Tasks delivery to a Cloud Run worker. Queue
messages contain only a run id and dispatch reason; a worker must lease durable state before doing
work. This keeps duplicate Cloud Task delivery harmless and avoids treating a task payload as the
source of truth. Claim and lease state are written in one Firestore transaction. Each active
external lease also schedules an expiry probe (and heartbeats reschedule it), so an acknowledged
duplicate delivery cannot leave a crashed worker's run `running` indefinitely. Event consumption
uses a transactional per-event marker and does not require a manually provisioned Firestore
composite index.

The Google adapter implements run-owned maintenance. Its authenticated prune endpoint retains a
configured number of terminal runs and deletes each run's fixed history, checkpoints, artifacts,
timers, external events, and coordination documents. Its authenticated reconcile endpoint scans
the Firestore runnable-work index plus expired external leases and idempotently redrives Cloud
Tasks delivery. Operators should invoke both operations from a dedicated, authenticated scheduler
principal; reconciliation covers an interrupted durable-state/queue-enqueue boundary, while a
Firestore TTL policy may complement but does not replace explicit cleanup.

Adapter descriptors are part of the core contract. Every adapter must report:

- adapter id and runtime kind
- supported capabilities (`durable.runs`, `durable.timers`, `external.events`, `durable.waits`, `retries`,
  `restart.resume`, `leases`, `artifacts`, `trace.history`, `idempotency`)
- configured boundary limits such as payload bytes, inline artifact bytes, and trace message size
- availability/status
- active-run count when the backend can report it
- an `operationalPolicy` snapshot covering quotas, list/history bounds, payload/result/artifact
  limits, retry/lease ceilings, trace severity defaults, retention scope, and concurrency-key
  behavior

Consumers should branch on capabilities, not on runtime kind. Runtime kind is diagnostic and
operational metadata, not an abstraction source. The core contract intentionally does not publish
a provider catalog of runtime-kind constants; concrete adapter assemblies own their diagnostic
kind ids.

The portable baseline for any adapter that claims to implement `IExecutionRuntimeAdapter` is:

- `durable.runs`
- `cancellation`
- `retries`
- `artifacts`
- `trace.history`
- `idempotency`

Every adapter must also advertise at least one dispatch model: `local.dispatch` or
`remote.orchestration`, and at least one execution model: `in_process.handlers` or
`external.workers`. `in_process.handlers` means registered .NET `IExecutionHandler` delegates are
executed by the host; an adapter that advertises only `external.workers` executes only explicitly
registered external descriptors. Coordination features such as `durable.timers`, `external.events`,
`durable.waits`, `leases`, and `restart.resume` are portable capability ids, but consumers must still branch on
their presence before designing a plugin that requires them. Adapters may publish extension
capabilities for diagnostics or provider-specific tooling; those extension ids are not portable
commitments unless they are promoted into the core capability catalog and conformance suite.

The core contract validates portability at the adapter boundary. Oversized payloads, inline
artifacts, status details, trace details, retry policies, tags, and invalid status strings are
rejected before the local runner persists a run or a remote adapter schedules provider-specific
work. Trace messages are bounded so handler output cannot become an unbounded operational log.
Adapters must expose their active policy through `ExecutionRuntimeAdapterStatus` so callers and
operators can inspect backend limits without hard-coding local SQLite, Azure, AWS, Google Cloud,
Kubernetes, or Temporal assumptions.

Run lifecycle is a portable state machine. Runs are created as `queued`, `waiting`, or terminal
`rejected`; dispatch moves `queued` or due `waiting` work to `running`; handlers complete
`running` work as `succeeded`, `failed`, `cancelled`, `timed_out`, or `rejected`; retry may move
`failed` or `timed_out` runs back to `waiting`; restart recovery may move interrupted `running`
runs back to `queued`. Terminal statuses do not resume by ordinary status update. An idempotency
key replays only the same logical request: handler id, derived plugin id, and payload hash must
match the existing run, otherwise the start request is a portable contract error.

Resume has a deliberately narrow meaning in the core contract. `restart.resume` means an adapter
can recover active persisted work after an adapter or process restart. A recovered `running` run
may re-execute the handler, so handlers must make side effects idempotent or protect them with
leases, durable records, or plugin-owned checkpoints. Scheduled `waiting` runs dispatch when due
from the shared backing store. Terminal runs never resume through ordinary recovery, and the core
runtime does not interpret plugin checkpoint artifacts or create linked follow-up runs. Adapters
publish this through `ExecutionRuntimeAdapterStatus.resumePolicy`.

Handlers can suspend with `WaitForExternalEventAsync` or `WaitForTimerAsync` when the adapter
advertises `durable.waits`. The first unmet wait writes durable state, moves the run to `waiting`,
and ends the current handler invocation without retaining a worker slot. When a matching event,
timer, or event-wait timeout occurs, the adapter queues a replay and the same wait call returns an
`ExecutionWaitResult`. The result outcome is `external_event`, `timer`, or `timed_out`.

This is intentionally replay-oriented: handler code before a wait may run again, so it must make
side effects idempotent and persist progress in checkpoints before waiting. Events raised before a
wait is registered are durable inbox entries and are consumed by the later wait. A matching event
is consumed exactly once. Cancellation of a suspended run is terminal and removes its active wait.
Timers, wait outcomes, and event-consumption markers are run-owned retention state.

Human approval gates can now remain one logical run: write an approval checkpoint/artifact, wait
for `approval`, and branch on the external-event payload or `timed_out` expiry. Reminders are
ordinary durable timer waits, and retryable third-party callbacks can combine a checkpoint,
external event wait, and the normal run retry policy.

## Consumer Stability

The execution runtime contract is stable enough for internal Vyral consumers to author plugins
against `Vyral.Execution` and to use the public `/execution/*` status surfaces. Stability applies
to the provider-neutral concepts, not to a specific backend:

- `IExecutionRuntime`, `IExecutionRuntimeAdapter`, `IExecutionPlugin`, `IExecutionHandler`, and
  `IExecutionRunContext`
- run request, run status, run result, retry policy, artifact, trace, lease, timer, external
  event, operational policy, and resume policy model shapes
- handler/plugin descriptor validation and ownership rules
- baseline capabilities: `durable.runs`, `cancellation`, `retries`, `artifacts`,
  `trace.history`, and `idempotency`
- dispatch capabilities: `local.dispatch` and `remote.orchestration`
- bounded list/history/status/artifact surfaces
- generic registered-handler start over HTTP with plugin-owned payloads

Consumer code should not branch on provider identity for portable behavior. It should inspect
`ExecutionRuntimeAdapterStatus.Adapter.Capabilities`, `operationalPolicy`, and `resumePolicy`.
Optional features are capability-gated:

- `durable.timers`
- `external.events`
- `leases`
- `restart.resume`

Preview areas may still change as adapters harden:

- concrete cloud host packaging and deployment wiring
- provider-specific status store and artifact storage implementations
- extension capability ids not listed in `ExecutionCapabilityCatalog`
- adapter metadata keys used only for diagnostics
- provider-specific AWS and Google deployment details
- future Kubernetes or Temporal adapter details

Azure Durable Functions has a replay-safe adapter and an opt-in isolated live gate. SDK-neutral
and in-memory tests remain the ordinary path; a deployment owner can run the live gate with unique
temporary resources to validate host wiring, durable events, and timers without reusing a shared
deployment name or setting.

Vyral uses a shared `OrderedId` primitive where time-sortable identifiers improve inspection.
The implementation adapts the useful shape of a local sequential id generator without carrying
over service-specific naming. IDs are serialized as fixed-width decimal strings so lexical ordering
matches generation order. The default node source uses deployment instance, host, process, and
network hints, with `VYRAL_ORDERED_ID_NODE` available when an operator wants explicit node
assignment. The execution runtime uses ordered ids for local run-owned objects and trace event
sequence ids; the general Vyral trace model also uses the primitive by default so retrieval, RAG,
provider, and execution traces can be inspected together.

`OrderedId` also keeps the inspection intent in provider-neutral form: callers can parse and
decompose an id into timestamp, node, and sequence parts, and can create before/after timestamp
references for range queries. Jittered creation is opt-in for externally exposed ids where making
range guessing slightly harder is useful. Fallback string creation is also opt-in for best-effort
telemetry or correlation paths; normal execution-runtime ids use the parseable ordered form.

Logging ergonomics are represented as structured execution context, not as a required log
formatter. `ExecutionTraceEvent` carries a `sequenceId` plus a compact context map with adapter,
runtime, worker, run, correlation, handler, and plugin identifiers when available. Adapters may
render that data into logs, dashboards, or provider-native status surfaces, but plugins should not
depend on a particular text prefix format.

Vyral exposes a narrow runtime status surface for operator and client tooling:

- `/execution/runtime/effective` reports caller-scoped adapter status and only the handlers the
  authenticated identity may start. `/execution/runtime` reports the global catalog and is a
  maintenance-identity operation in shared deployments.
- `/execution/runs` lists bounded recent runs, omitting result bodies by default.
- `/execution/runs/{runId}` returns one run and supports cancellation via `DELETE`.
- `/execution/runs/{runId}/history` returns bounded trace history.
- `/execution/runs/{runId}/artifacts` lists run artifacts.

These endpoints expose runtime concepts only. Domain-specific job endpoints remain responsible for
projecting runs into embedding, retrieval, provider, or future Vyral job response shapes.

Current Vyral job adapters follow that boundary:

- provider runs map to the `vyral.providers` plugin and `vyral.provider.run` handler
- embedding batches map to the `vyral.embeddings` plugin and `vyral.embedding.batch` handler
- retrieval evaluation maps to the `vyral.retrieval.evaluation` plugin with evaluation and
  comparison handlers
- RAG ingestion maps to the `vyral.rag.ingestion` plugin with text and batch handlers
- graph import/readiness checks map to the `vyral.graph` plugin with import, inspect, and doctor
  handlers

The adapter DTOs expose Vyral-specific counters and results, but the persisted runtime runs only
store generic handler ids, payloads, progress/status details, artifacts, history, and terminal
state. This is the portability boundary Vyral code should design against when moving from local
SQLite to Azure, AWS, Google Cloud, Kubernetes, or Temporal-backed adapters.

## Migration Checklist For Existing Vyral Jobs

Use this checklist when moving an async Vyral workflow onto the execution runtime:

- Assign a stable plugin id and stable handler ids before wiring HTTP endpoints.
- Keep domain request and result models in the adapter or plugin assembly, not in
  `Vyral.Execution`.
- Start runs through `ExecutionRunRequest` and forward caller idempotency keys when the existing
  endpoint has retry/replay semantics.
- Let handler descriptors declare plugin ownership; reject plugin mismatch through the runtime
  rather than in endpoint-specific code.
- Set retry policy intentionally. Existing Vyral jobs that already own provider retries or batch
  error policy should usually start with `MaxAttempts = 1`.
- Report progress through `Requested`, `Attempted`, `Succeeded`, `Failed`, `Progress`, and
  `CurrentStep`.
- Put compact operator state in `StatusDetails`; use typed domain DTOs to project that state back
  to old job response shapes.
- Write artifacts for summaries, normalized result envelopes, advisory reports, or inspection
  payloads that users should retrieve after terminal state.
- Route cancellation through `CancelRunAsync`; handlers should observe the context cancellation
  token before expensive steps and between batch items.
- Treat resume as at-least-once handler re-execution. Any side effects outside the runtime must be
  idempotent or guarded by domain checkpoints, leases, or durable records.
- List jobs by handler id and bounded limits; omit result bodies by default and require explicit
  `includeResult=true` for result-heavy surfaces.
- Keep provider-specific, retrieval-specific, RAG-specific, graph-specific, or embedding-specific
  concepts out of core runtime models even when those concepts drive status details.
- Add a local SQLite server workflow test for the domain endpoint and a runtime endpoint assertion
  proving the job is visible through `/execution/runtime/effective` or operator-only
  `/execution/runtime`, `/execution/runs`, history, and
  artifacts.
- Add or extend shared conformance only when the behavior is generic enough for non-Vyral plugins.

Current adapter expectations:

- Provider runs keep provider execution behind `IProviderTarget`; the runtime sees a single
  provider-run handler and opaque payload/result JSON.
- Embedding batches call the configured `IEmbeddingProvider` from a handler and project runtime
  progress back into `EmbeddingJob`.
- Retrieval evaluation calls `IRetrievalEvaluationService` and stores variant/case progress in
  status details.
- RAG ingestion calls `IRagIngestionService`, writes run-owned result artifacts, and projects text
  or batch counters back into `RagIngestionJob`.
- Graph jobs call `IRecordCollectionStore` graph methods and expose import, inspect, and doctor
  handlers under one plugin.

External services should adopt the runtime through the same boundary:

- timer and HTTP entrypoints translate requests into `ExecutionRunRequest` values
- each pipeline lane registers as an `IExecutionPlugin` with one or more handlers
- schedule windows, enable flags, singleton checks, and pressure gates are adapter or plugin
  policy before a run starts, not new core concepts
- item snapshots, external API calls, domain state, database writes, and publish steps remain
  handler internals
- pipeline run ids, per-window counts, warning flags, advisory summaries, and autotune hints are
  status details or artifacts owned by the plugin schema
- idempotent item progress and checkpoint interpretation stay in service storage or plugin
  status payloads; the core runtime only persists opaque checkpoint/status JSON
- local direct-run and Azure Durable hosted modes use the same handler/context surface, so moving
  between them does not require rewriting pipeline code around a provider SDK

The extracted runtime must keep treating external service workloads as requirements sources and
fixtures. When a service behavior looks useful elsewhere, promote only the runtime-shaped part
into the core contract or conformance suite. Domain names, provider APIs, listing models, and
pipeline record schemas stay outside the core even when they motivated a feature.

Local retention treats traces, artifacts, leases, timers, and external events as run-owned state:
when a terminal run falls outside the configured retained-run window, the SQLite adapter removes
its dependent rows with the run instead of leaving orphaned operational data behind.

The local SQLite adapter also owns local durability mechanics without changing the portable runtime
model. It initializes explicit schema metadata (`user_version` plus a metadata table), uses WAL
mode with a busy timeout and normal synchronous mode, and advertises these choices in adapter
metadata. Artifact payload limits distinguish accepted artifact size from inline artifact size:
small artifacts stay in SQLite, while larger local artifacts are written to the adapter artifact
directory and represented through the same artifact hash, size, URI, and metadata fields that a
cloud adapter could map to blob/object storage.

Local operational controls are enforced in runtime terms: `MaxActiveRuns` rejects new work before
it is persisted when queue pressure is already at the configured ceiling, and handler
`ConcurrencyKey` values serialize `running` runs across all registered handlers that share the
same key. Waiting runs are retried after the adapter's configured concurrency retry delay. Other
adapters may enforce equivalent behavior differently, but they must publish their policy through
the same status surface.

## Conformance expectations

The shared conformance suite is the portability gate for runtime adapters. It should prove that a
consumer can design against the core contracts and move between local and provider-backed runtimes
without changing handler/plugin code for normal lifecycle behavior.

Current conformance coverage includes:

- adapter descriptors advertise the portable baseline and one dispatch model
- adapter status advertises operational policy limits and concurrency-key behavior
- adapter status advertises resume policy, including handler re-execution risk and terminal
  non-resume behavior
- plugins register handlers and can be discovered through the adapter
- run starts, progress, artifacts, history, status counters, result bodies, and idempotent replay
- idempotency-key reuse with a different handler/plugin/payload shape is rejected as a contract
  error
- missing handlers and plugin mismatches produce terminal `rejected` runs rather than leaking
  provider scheduling details
- active cancellation reaches terminal `cancelled`, while cancelling a terminal run is stable
- failed and `timed_out` runs retry according to the portable retry policy, then stop at
  `maxAttempts`
- scheduled `waiting` work can be resumed from a fresh adapter instance over the same backing
  store
- leases enforce owner conflicts, owner release, and expiration
- run-owned timers and external events round-trip portable shape and trace entries
- run listing and history queries are bounded
- a windowed batch fixture exercises ugly real-world plugin mechanics without making its domain
  part of the runtime contract
- a pressure-aware pipeline fixture exercises runtime warnings, grouped heartbeat/status details,
  advisory artifacts, adaptive hints, retry after checkpoint, and duplicate suppression without
  importing service-specific pipeline vocabulary

## First implementation arc

1. Define core contract models in `Vyral.Execution`.
2. Implement a local SQLite runner in `Vyral.Execution.Local`.
3. Prove plugin execution with conformance-style tests.
4. Map existing Vyral job shapes through narrow adapters:
   - provider run jobs
   - embedding batch jobs
   - retrieval evaluation and comparison jobs
   - RAG ingestion jobs
   - graph import, inspection, and readiness jobs
5. Add shared contract validation and limits so local and remote adapters reject non-portable
   work consistently.
6. Expose runtime status/history/artifact surfaces without domain-specific concepts.
7. Keep Azure Durable Functions as an adapter target, not as the ontology.

## Azure Durable Functions adapter

The Azure adapter translates runtime concepts into Durable Functions concepts:

| Runtime concept | Azure Durable Functions target |
| --- | --- |
| run | orchestration instance |
| handler step | activity |
| timer | durable timer |
| external event | `wait_for_external_event` / raise event |
| singleton run | deterministic instance id plus status check |
| retry policy | Durable retry options around activity calls |
| artifact | blob/table/object store reference |
| status/history | custom status plus runtime trace store |
| lease | table/blob lease or storage-backed compare-and-swap |

The adapter should not expose Azure terms through the core contracts.

The first Azure adapter is a thin dialect layer over the existing core in
`Vyral.Execution.AzureDurable`:

- A startup/registration surface maps runtime handlers to Durable activities.
- An idempotency key maps to a stable Durable orchestration instance id.
- A host-provided client boundary keeps Azure Functions SDK types out of the core contracts.
- The host bridge is split into SDK-neutral pieces:
  - `AzureDurableExecutionRegistry` shares registered plugins/handlers between the control-plane
    adapter and Function entrypoints.
  - `IAzureDurableExecutionStatusStore` owns run/status/history/artifact/lease/timer/event
    persistence. The in-memory implementation is for conformance and local adapter tests; a real
    Azure host should implement this with Table/Blob/Cosmos or another durable Azure store.
  - `IAzureDurableExecutionOrchestrationScheduler` wraps Durable client operations such as
    schedule instance, terminate, and raise event.
  - `IAzureDurableExecutionOrchestrationDriver` wraps the orchestrator context operations used by
    the host algorithm: durable timer, activity call, and custom status.
  - `AzureDurableExecutionHost` contains the actual orchestration/activity dispatch algorithm and
    invokes only neutral `IExecutionHandler` plugins.
- Pure dialect helpers create queued/waiting runs, start activity attempts, build activity
  commands, apply activity results, calculate retry delays, and produce status snapshots.
- The adapter writes runtime `ExecutionRun`, `ExecutionTraceEvent`, and `ExecutionArtifact`
  snapshots to a runtime status store, even when Durable Functions owns orchestration history.
- Azure Durable timers/events remain adapter internals.
- Service-specific or Vyral-specific payloads stay in plugins.
- The current SDK-neutral Azure dialect tests pin command mapping, lifecycle transitions, retry
  calculation, descriptor shape, and portable rejection semantics. The host-backed Azure adapter
  runs the same `ExecutionRuntimeConformanceTests` harness used by the local SQLite adapter through
  an in-memory Durable scheduler/driver fixture.

A concrete Azure Functions app can remain thin:

- HTTP/timer triggers call `AzureDurableExecutionRuntimeAdapter.StartRunAsync`.
- The adapter uses `AzureDurableExecutionClient`, whose scheduler implementation wraps the actual
  Durable Functions client binding.
- The orchestrator Function reads `AzureDurableStartCommand` and calls
  `AzureDurableExecutionHost.OrchestrateAsync` with a driver that wraps the Durable orchestration
  context.
- The activity Function reads `AzureDurableActivityCommand` and calls
  `AzureDurableExecutionHost.DispatchActivityAsync`.

No core execution models depend on Azure SDK types, Durable Functions method names, Azure storage
record shapes, or service/Vyral domain payloads.

Local Functions host smoke validation remains opt-in and local-only:

- `AzureDurableLocalHostSmokeOptions` emits clean default host settings for a future local smoke:
  `AzureWebJobsStorage=UseDevelopmentStorage=true`,
  `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`, and
  `AzureFunctionsJobHost__extensions__durableTask__hubName=VyralExecutionLocal`.
- The opt-in flag is `VYRAL_EXECUTION_AZURE_LOCAL_HOST_SMOKE=1`; the default emitted value is `0`.
- The helper rejects cloud-style storage connection strings, renamed storage settings, and
  non-default task hub names.
- Unit tests inspect this settings contract only. They do not start Functions Core Tools, connect
  to storage, or require deployed resources. A future live local-host smoke should skip cleanly
  when `func` or a local storage emulator is unavailable.

## Future adapter targets

AWS, Google Cloud, Kubernetes, and Temporal should be treated as additional adapter targets over
the same contract, not new abstractions. See `design/execution-runtime-adapter-matrix.md` for the
concept mapping and conformance expectations for runs, leases, timers, events, artifacts, traces,
idempotency, cancellation, quotas, retention, and restart recovery.

Temporal remains useful as a design mine because it has mature answers for workflow replay,
signals, activity retries, timers, visibility, worker task queues, and local development. Those
ideas should inform Vyral conformance and adapter design only after they are translated into
provider-neutral Vyral terms. Temporal must not become the consumer portability boundary unless
Vyral explicitly introduces a separate Vyral-owned workflow contract in the future.
