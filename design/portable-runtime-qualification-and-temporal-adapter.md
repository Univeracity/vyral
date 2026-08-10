# Portable Runtime Qualification and Temporal Adapter

**Status:** Proposed  
**Date:** 2026-07-28  
**Audience:** Vyral maintainers, adapter authors, platform operators, and early consumers

## Decision summary

Vyral will narrow its competitive and reliability gaps by becoming the **verified portability
layer** for a deliberately bounded set of application concerns, rather than trying to outbuild a
database, vector database, workflow engine, or general integration framework.

The work has five connected parts:

1. Add a first-class `Vyral.Execution.Temporal` adapter. Temporal remains an adapter behind
   `Vyral.Execution`, never the public plugin programming model.
2. Make provider qualification a release artifact: a versioned capability matrix, shared
   conformance results, and isolated live-environment evidence for every supported adapter.
3. Finish the execution portability envelope around the adapter: durable dispatch reconciliation,
   deterministic status projection, fault/recovery tests, security preflight, and a supported
   worker-host topology.
4. Turn CanonicalStore's aggregate/outbox contract into an operational migration story, while
   preserving its explicit non-goal of portable relational querying.
5. Keep retrieval as a verifiable substrate and integration boundary, not an attempt to compete
   with dedicated vector databases or broad RAG frameworks on index count or model integrations.

The result should give a consumer a defensible answer to: *Can this application keep its durable
work and authoritative aggregate state while moving between local development, Temporal, Azure,
AWS, Google Cloud, or a future adapter? What behavior was actually proved?*

## Problem statement

Vyral's current architecture has a credible but incomplete differentiation. It owns portable
contracts for records, objects, retrieval, canonical aggregate state, and durable execution, and
it already has local, Azure, AWS, and Google execution shapes. The execution contract includes
runs, retries, cancellation, checkpoints, artifacts, traces, leases, timers, external events, and
restart semantics.

That does not by itself create trust or market differentiation:

| Gap | Why it matters | This design's response |
| --- | --- | --- |
| A provider-neutral interface can conceal different behavior | Consumers discover idempotency, waiting, paging, or recovery differences after they are deployed | Require shared behavioral conformance, capability disclosure, and recurring live qualification before a provider is described as supported. |
| Durable workflow engines are more mature than a new execution layer | Temporal and Dapr already have strong orchestration ecosystems | Treat Temporal as a high-quality adapter target and prove that Vyral plugins keep their contract when coordinated by Temporal. |
| Direct databases beat a generic abstraction at arbitrary relational work | Join-heavy, temporal, and high-cardinality reporting workloads need native data models | Keep CanonicalStore bounded to aggregate commits; provide an outbox/projection migration pattern instead of making an inaccurate SQL-portability claim. |
| Dedicated vector systems and RAG frameworks have much larger ecosystems | Index breadth and model/provider integrations are not a realistic primary moat | Qualify a small number of production search projections and make quality, provenance, and reproducibility portable. |
| Consumer-specific code makes a neutral platform look like a private application | It obscures ownership, raises disclosure risk, and weakens the adapter story | Move such behavior to explicit consumer-owned integration packages or generic, schema-driven ingress contracts. |

The desired moat is not proprietary syntax. It is a maintained body of difficult-to-recreate
evidence: contract tests, fault cases, live deployment gates, compatibility history, migration
fixtures, and reusable worker/plugin packages.

## Goals

- Preserve `Vyral.Execution` as the only execution dependency needed by portable plugin code.
- Run ordinary Vyral handlers safely with Temporal without leaking `Temporalio` types into public
  contracts, handler signatures, OpenAPI, or client libraries.
- Support the portable baseline and the coordination capabilities that a Temporal-backed adapter
  can genuinely implement: remote orchestration, in-process worker handlers, durable runs,
  retries, cancellation, artifacts, traces, idempotency, timers, external events, durable waits,
  leases, and restart/resume.
- Ensure the Vyral run/status/history/artifact APIs remain available even when Temporal visibility
  settings, retention, or search attributes differ by deployment.
- Make every provider's maturity and evidence visible without forcing consumers to branch on a
  provider name.
- Provide a credible MySQL-to-PostgreSQL CanonicalStore migration and projection story.
- Establish explicit boundaries where Vyral composes with Temporal, Dapr, OpenDAL, LlamaIndex, or
  a dedicated search engine instead of attempting to replace each of them.

## Non-goals

- Replacing Temporal's server, Web UI, worker-versioning model, or operations tooling.
- Making arbitrary .NET code deterministic enough to execute inside a Temporal workflow method.
- Promising exactly-once external side effects. All handler side effects remain at-least-once and
  must use consumer idempotency keys, durable fences, or checkpoints.
- Treating Temporal workflow history as Vyral's public record store, artifact store, audit export,
  or tenant authorization database.
- Adding portable arbitrary SQL joins, graph traversal, temporal analytics, or an unlimited
  transaction size to CanonicalStore.
- Becoming a vector-database control plane or copying every RAG framework integration.

## Portability contract and evidence model

### Three planes

Every adapter must distinguish three planes instead of implying that one provider service performs
all roles:

```text
Consumer plugin / HTTP client
            |
            v
Vyral portable contract and query/status projection
            |
            +-- durable coordination adapter (Temporal, Azure Durable, AWS queue, Google Tasks)
            |
            +-- artifact and execution-projection stores
```

The Vyral projection is the consumer-facing authority for `GetRunAsync`, lists, history,
artifacts, checkpoints, idempotency, leases, and authorization scopes. The coordination engine is
the authoritative scheduler for its own delivery/replay lifecycle. Neither is silently treated as
the other.

This separation prevents an adapter from depending on provider-console queries for portable status
and prevents provider retention settings from deleting application audit evidence. It also makes
the relationship observable: every projected run includes non-secret coordination references such
as adapter id, workflow/instance id, and coordination generation.

### Qualification levels

An adapter must publish a `qualification` record separately from advertised capabilities. A
capability means "the adapter implements this behavior"; qualification says how that claim was
verified. Neither substitutes for the other.

| Level | Meaning | Minimum evidence |
| --- | --- | --- |
| `prototype` | API experiments only; not consumer-ready | Unit tests only; omitted from normal adapter selection. |
| `local_conformant` | Shared fixtures pass against an isolated local dependency | Adapter conformance suite, fault/restart tests, public-surface check. |
| `live_qualified` | The published capability profile passed against a disposable real target | Checked-in isolated gate, date/commit/provider version, redacted result artifact, and cleanup proof. |
| `consumer_validated` | A consumer ran its own reviewed topology gate | Consumer-owned evidence reference; never inferred solely from Vyral CI. |

The initial status endpoint can expose this as adapter metadata plus a machine-readable
`adapter-qualification.json` release artifact. A later additive `ExecutionAdapterQualification`
model may make it a first-class API field once all existing adapters can populate it without
invented data.

### Release rules

- A provider is **implemented** only after the shared conformance fixture passes.
- A hosted provider is **live-qualified** only after its checked-in disposable gate passes against
  real infrastructure. Mocked SDK tests and local emulators do not satisfy this level.
- The release artifact records environment class, adapter version, core contract version, test
  commit, capability set, and timestamp. It contains no endpoint, account, tenant, secret name,
  or consumer identifier.
- Qualification expires operationally after a documented cadence (initially 90 days) or after a
  provider SDK/protocol/contract-major update. Expiry changes the published status to stale; it
  does not falsify previous evidence.
- A capability regression is a contract defect. The adapter must be repaired, the capability
  withdrawn, or the portable contract narrowed before release.

### Current portable workload boundary

The repository now carries one labeled consumer workload,
`portable-consumer-workload/v1`, as a single `IExecutionPlugin` implementation in the shared
conformance assembly. The unchanged compiled plugin runs in-process under Local and Azure, through
the provider-neutral `ExecutionPluginWorker` under the Local, AWS, and Google external-worker
fixtures, and in the isolated Temporal/PostgreSQL container gate. Every case asserts the same
idempotency identity, progress counters, digest result, artifact, and trace shape.

This is deterministic/source portability evidence, not a live-qualification promotion. The
checked-in matrix links each adapter case and continues to show no retained hosted or Temporal
receipt. The external worker protocol maps progress, events, artifacts, checkpoints, completion,
and durable waits, but not handler-side coordination leases, standalone timer creation, or
worker-raised external events; those operations fail explicitly and remain a declared
execution-model boundary.

## Temporal adapter

### Why Temporal is an adapter

Temporal is a strong coordinator for replay, timers, signals, activity retry, and worker
operations. It is not Vyral's public ontology. A plugin must continue to target
`IExecutionPlugin`, `IExecutionHandler`, and `IExecutionRunContext`; consumers continue to use
`IExecutionRuntime` and portable JSON/HTTP models.

This preserves three important options:

- A plugin can run under local, Azure, AWS, Google, or future Kubernetes adapters without a
  Temporal SDK dependency.
- Temporal can be introduced for a workload that needs it without converting all Vyral state or
  consumer APIs into Temporal types.
- Vyral can use Temporal's reliability while retaining a comparable status and artifact surface
  for non-Temporal adapters.

### The determinism boundary

Vyral handlers are intentionally normal asynchronous application code. Their run context performs
storage I/O, writes artifacts, acquires leases, and can call external systems. Such code cannot
run inside a Temporal workflow method, which must replay deterministically.

The adapter therefore uses a **generic coordinator workflow** plus activities:

```text
StartRunAsync
  -> Vyral execution projection: create run + idempotency record + pending dispatch
  -> Temporal: start Vyral.RunCoordinator.v1(workflowId, runId, generation)

Vyral.RunCoordinator.v1 (deterministic Temporal workflow)
  -> ExecuteAttempt activity
       -> resolves registered Vyral handler in worker host
       -> executes handler with a Temporal-backed Vyral run context
       -> writes Vyral projection updates, artifacts, checkpoints, traces, leases
       -> returns completed / suspended / retryable / terminal outcome
  -> waits natively for a timer, signal, retry delay, or cancellation
  -> invokes the next activity attempt or projects terminal state
```

Only the generic coordinator is a Temporal workflow. It contains no consumer handler code, direct
database call, network request, secret, raw payload, or artifact body. Activities may execute
ordinary Vyral handlers because Temporal activities are allowed to perform side effects.

### Wait and resume semantics

The portable contract already permits restart recovery to re-execute a handler and makes
checkpoint interpretation plugin-owned. Existing local and external-worker shapes persist a wait
and suspend an attempt rather than serializing an arbitrary CLR stack. The Temporal adapter adopts
the same semantics:

1. `WaitForExternalEventAsync` or `WaitForTimerAsync` validates and persists a Vyral wait record
   with a deterministic wait identity.
2. The activity returns a `suspended` outcome; it does not hold a worker slot awaiting a signal.
3. The coordinator waits on a Temporal signal and/or Temporal timer.
4. On completion it stores the portable wait outcome, records history, and invokes a new handler
   attempt.
5. The handler reloads the checkpoint and the recorded wait result as part of its normal
   restart-safe logic.

The adapter must not claim stack-frame continuation. It advertises `restart.resume` and
`durable.waits`; its `resumePolicy` explicitly states `may_reexecute_handler` and
`plugin_owned` checkpoint behavior.

### Package and host layout

The first implementation is split so a web/API process need not run a Temporal worker:

```text
Vyral.Execution.Temporal
  - TemporalExecutionRuntimeAdapter
  - TemporalExecutionOptions and factory
  - coordinator workflow contracts (internal)
  - activity outcome/dispatch reconciliation contracts (internal)
  - ITemporalExecutionProjectionStore seam

Vyral.Execution.Temporal.Hosting
  - worker-host registration helpers
  - generic coordinator workflow and activities
  - handler/plugin registry composition
  - Generic Host lifecycle and bounded aggregate outbox pump
  - health/readiness and safe worker identity diagnostics

Vyral.Execution.Temporal.Postgres (initial production projection store)
  - run, history, wait, lease, checkpoint, idempotency, and dispatch-outbox tables
  - artifact metadata; bodies remain in IObjectStore
```

`Vyral.Execution.Temporal` references `Vyral.Execution` and the official Temporal .NET SDK only.
The core execution package remains free of Temporal references. The worker host owns SDK client,
worker, task-queue, and TLS construction; it accepts a validated provider-owned settings map
through `IExecutionRuntimeAdapterFactory`.

The Postgres projection store is intentionally the first production composition because it gives
transactional, queryable state independent of Temporal visibility configuration and runs on every
major cloud. It is not a new portable public storage contract. Its schema and operations are
internal to the Temporal adapter. A future `ICanonicalStore` projection implementation is allowed
only after benchmark and conformance evidence show that heartbeat, lease, and history write rates
are appropriate; it must not be assumed equivalent merely because both are durable.

### Identity and naming

- `runId` remains the stable Vyral identifier and is the only public execution identifier.
- The Temporal workflow id is deterministic from a non-secret adapter namespace and `runId`, for
  example `vyral:{adapterNamespace}:{runId}`. It contains no tenant, user, payload, or idempotency
  key.
- The Temporal run id is diagnostic metadata only because it changes after continue-as-new.
- Task queue names, Temporal namespace, endpoint, mTLS material, and worker identity are adapter
  configuration. They are never placed in a portable run request or plugin descriptor.
- Plugin and handler registration is deployment configuration. A request cannot choose an assembly,
  type, Temporal workflow type, task queue, or worker endpoint.

### State and dispatch protocol

`StartRunAsync` is a two-system operation and must tolerate a process failure between persistence
and `StartWorkflow`:

1. Validate the portable request and handler policy.
2. In one projection-store transaction, create or replay the Vyral run, record the idempotency
   receipt, allocate its workflow id, and create a `dispatch_pending` outbox row.
3. A dispatcher starts the generic workflow using that fixed id. "Already started" is success only
   when the existing workflow belongs to the same Vyral run and generation.
4. Mark the outbox row delivered only after the start result is known. Reconciliation retries
   undelivered rows, describes ambiguous workflow ids, and never creates a second logical run.

Signals use the same pattern: persist an external-event record and signal-outbox row first, then
deliver a signal whose durable payload is only an event id and event revision. The coordinator
loads the durable event through an activity. This prevents raw content from accumulating in
Temporal history and closes the persist-versus-signal crash window.

Cancellation first records `cancellationRequested` in the Vyral projection, then requests
Temporal cancellation. The coordinator and activities observe that durable flag before beginning
new work. An already-running activity may require the normal cooperative cancellation timeout;
the adapter records this as an operational detail rather than claiming immediate cancellation.

### Mapping table

| Vyral concept | Temporal mechanism | Vyral authority and rule |
| --- | --- | --- |
| Durable run | One generic coordinator workflow per logical run | Projection store owns public run status and idempotency; workflow id is a diagnostic reference. |
| Handler invocation | `ExecuteAttempt` activity | Handler runs as ordinary side-effecting activity code; never in workflow code. |
| Retry | Coordinator schedules retry after a Vyral-calculated delay | Temporal activity retry is set to one attempt so Vyral attempt counts, failure classes, and history stay portable. |
| Scheduled start/timer | Coordinator delay/timer | Portable timer record and trace are stored before waiting; due work may be re-driven safely. |
| External event | Workflow signal carrying event id | Event body is in projection/object storage, not workflow history. Name/payload limits are validated by Vyral. |
| Durable wait | Coordinator wait for signal/timer after suspended activity | Resume re-executes handler from a checkpoint, matching the portable restart policy. |
| Lease | Projection-store conditional acquisition | Temporal's workflow serialization is not misrepresented as a lease usable by external consumers. |
| Artifact/checkpoint | IObjectStore plus projection metadata | No artifact body or arbitrary checkpoint JSON is put in Temporal history. |
| History/traces | Projection trace rows; optional Temporal ids in details | `GetHistoryAsync` returns Vyral history consistently across adapters. |
| Cancellation | Durable cancellation flag plus workflow cancellation | Terminal state is projected once; cancellation races remain visible in trace history. |
| Long-lived coordinator | Continue-as-new at a tested history threshold | Logical Vyral run id is stable; generation and Temporal run ids are diagnostic only. |

### Temporal history and deployment evolution

The coordinator must remain small and versioned as `Vyral.RunCoordinator.v1`. Its input is a
stable internal envelope containing `runId`, projection revision, and coordinator generation, not
a consumer payload. It uses explicitly versioned branch changes and a documented continue-as-new
threshold based on coordination transitions and tested history size. A new incompatible coordinator
is a new workflow type/version, with old workers retained until existing runs drain or have a
defined migration path.

Worker deployment versioning is operationally useful but is not exposed as a portable capability.
The adapter status may report a non-secret worker build/version for diagnosis. A deployment must
never route one Vyral run between incompatible handler/plugin implementations merely because a
task queue has multiple worker versions.

### Initial capabilities and exclusions

The first release advertises:

```text
remote.orchestration, in_process.handlers, durable.runs, durable.timers,
external.events, durable.waits, cancellation, retries, restart.resume,
leases, artifacts, trace.history, idempotency
```

It does **not** advertise `external.workers` until the Temporal worker protocol has a separately
implemented and conformance-tested bridge. A Go or other non-.NET worker must use the existing
portable external-worker protocol through a Vyral service boundary; it must not call Temporal
activities with a private, undocumented wire shape.

## Security and operator design

### Required controls

- Temporal endpoints and worker task queues are private. The API host receives an authenticated
  Temporal client credential with the minimum namespace permissions; worker credentials are
  separate where the deployment supports it.
- Use mTLS or the deployment's reviewed Temporal Cloud authentication mechanism. Never place an
  API key, certificate, private key, bearer token, endpoint query string, or connection string in
  status, traces, artifacts, test snapshots, or deployment examples.
- The Vyral API remains the external authorization boundary for run reads, cancellation, events,
  artifacts, and scope. Direct Temporal UI/API access is operator-only and must not bypass tenant
  policy.
- Task queues are configured per approved product/worker topology. Request fields cannot influence
  namespace, queue, workflow type, or identity.
- Temporal payload conversion is allowlisted. Coordinator inputs and signals use the minimal
  internal envelope; large, sensitive, or tenant content is referenced through Vyral stores.
- Artifact and checkpoint references use existing content-size, ownership, and object-store
  controls. Logs redact identifiers that could act as credentials.
- Preflight verifies reachability, namespace, task queue, worker identity, TLS/auth configuration,
  projection store migration level, object store access, and least-privilege policy without
  starting a workflow or reading secret values.

### Operator surfaces

Add a read-only `vyral execution temporal preflight` command and an adapter-specific readiness
section. It reports:

- adapter and core contract versions;
- configured capability profile and qualification level;
- Temporal namespace/task queue hashes or operator-approved display names;
- client and worker reachability/auth result without credentials;
- projection-store schema/migration state;
- object-store write/read/delete probe result in an isolated prefix;
- dispatcher backlog, oldest undelivered item, active coordinator count, and stale-run count;
- blockers versus warnings.

It must not create a long-lived run, mutate queue configuration, deploy workers, or print secrets.

## Conformance and live qualification

### Shared adapter conformance

`TemporalExecutionRuntimeAdapterConformanceTests` derives from the existing execution fixtures and
must pass the portable baseline before the package is consumer-preview ready. It includes:

- descriptor validation, capability disclosure, and policy/status projection;
- plugin/handler registration and invocation;
- immutable idempotent replay and conflicting-key rejection;
- progress, status detail bounds, trace history, artifacts, checkpoints, and bounded lists;
- missing handler, plugin mismatch, validation, transient, and terminal failure mapping;
- Vyral-owned retry count and delay behavior;
- cancellation before start, during a cooperative activity, and during a suspended wait;
- timer, external-event, timeout, and event-before-wait race cases;
- lease acquisition, expiry, fencing, and release from a second execution context;
- restart of the worker host during an activity, between persist and dispatch, and while waiting;
- end-to-end Generic Host composition with the official worker lifecycle and hosted aggregate
  outbox pump, not only direct construction of worker primitives;
- history threshold/continue-as-new behavior while preserving one logical Vyral run;
- exact-run chain inspection that bounds event count and encoded protobuf history per Temporal run,
  rejects broken/cyclic rollover links, and keeps workflow/run identifiers out of receipts;
- offline replay of retained, sanitized coordinator histories from before each compatibility patch,
  so removal of a required deterministic branch fails the normal build;
- redaction assertions proving payload/artifact bodies are absent from coordinator inputs and
  Temporal diagnostic metadata, including a real-history scan with distinct request, event,
  checkpoint, artifact, trace, status, and result sentinels.

### Isolated gates

The qualification sequence is intentionally layered:

1. **Unit gate:** fake Temporal client and projection store prove error mapping and two-system
   dispatch state transitions.
2. **Container gate:** a disposable Temporal server plus disposable Postgres and object store run
   the shared fixture in CI. It must stop/restart the worker process during recovery cases.
3. **Live self-hosted gate:** a temporary namespace/task queue plus isolated Postgres schema/object
   prefix validates real TLS/auth/network policy in a deployment-like environment.
4. **Temporal Cloud gate:** opt-in, operator-provisioned namespace/task queue verifies Cloud
   credentials, worker connection, signal/timer delivery, cancellation, recovery, and cleanup.

The live gates use random run prefixes and dedicated resource names. They do not run against a
consumer namespace or delete shared task queues. Cleanup failures are surfaced as failures with
operator instructions, not hidden by best-effort deletion.

### Fault model

The tests must explicitly induce and classify these failures: duplicate workflow start response,
duplicate signal delivery, activity timeout after an external side effect, workflow cancellation
race, worker crash after checkpoint, projection write failure, Temporal service unavailable,
outbox redelivery, stale lease, clock skew around a timeout, and history rollover. The expected
outcome is a portable Vyral status/history result, not a provider exception escaped to consumers.

## CanonicalStore and retrieval closure work

### CanonicalStore

CanonicalStore is the right portable profile for authoritative aggregate decisions, domain fences,
transactional outbox messages, migration ledgers, and tenant export/restore. It is not an
application's relational reporting engine.

The current closure work makes this practical without broadening the contract:

1. The checked-in operator-run MySQL-to-PostgreSQL cutover gate exports one tenant archive, verifies
   every chunk/hash, restores to a fresh PostgreSQL target, reruns the shared CanonicalStore suite,
   verifies fences/idempotency/outbox state, and exercises consumer projection replay. It remains
   live-evidence pending until run against the disposable dual-database composition.
2. The relational projection starter demonstrates a transactional-outbox consumer,
   checkpoint/fence discipline, rebuild procedure, dead-letter/replay guidance, and a relational
   read-model example. It is a sample, not a hidden required runtime.
3. The staged-visibility-manifest recipe is the supported answer for imports above the
   100-mutation ceiling, including count/hash verification and cleanup of unpublished staged data.
4. The provider-neutral CanonicalStore data-plane preflight now uses two random ephemeral tenants
   to prove hash-verified archive restore, cross-tenant isolation, and verified cleanup. It is part
   of the SQLite, MySQL, and PostgreSQL conformance fixtures and is exposed only through an explicit
   administrator POST; the passive preflight GET remains write-free. Retained live receipts from
   hosted compositions are still required, and archive retention, managed backup/PITR policy, and
   restore cadence remain explicitly operator-owned.
5. The checked-in MySQL contention benchmark separates hot-tenant write/state-growth pressure from
   idempotent replay lock pressure, compares both with tenant-sharded work, and applies explicit
   p95, throughput, failure, and contention-ratio budgets. The retained local MySQL 8.0.46 receipt
   completed every operation but missed the starter hot-tenant p95 and throughput budgets, so it
   recommends evaluating the granular PostgreSQL layout or a dedicated projection without making
   an unmeasured claim about either. Target-topology reruns remain operator-owned.

Success is a consumer migrating one aggregate at a time while retaining join-heavy, temporal, and
relationship views as rebuildable projections—not a table-for-table conversion.

### Retrieval and RAG

Vyral should compete on trustworthy retrieval integration, not index breadth:

1. Select one production search projection through the existing qualification design (initially
   OpenSearch or PostgreSQL/pgvector, based on measured consumer workloads rather than vendor
   preference).
2. Require a candidate-retrieval boundary followed by Vyral-authoritative filtering, tenant scope,
   revision eligibility, stable tie behavior, citations, and trace diagnostics. Native index scores
   may be diagnostic but do not override portable eligibility rules.
3. Version and publish labeled evaluation corpora that cover access filtering, stale source
   replacement, duplicate suppression, citation fidelity, and hard negatives. Performance claims
   and relevance claims are reported separately.
4. Define a small integration protocol for external RAG frameworks: deterministic ingestion plan,
   record/artifact references, metadata schema, retrieval request, citation result, and evaluation
   case export. Do not embed another framework's object model in Vyral's public contract.
5. Keep experimental accelerators, including local vector quantization, behind an adapter option
   and require exact portable result qualification or explicit approximation disclosure.

This makes Vyral complementary to a RAG framework: the framework can provide parsing, agent
orchestration, and model integrations while Vyral provides reproducible storage, provenance,
policy, and evaluation boundaries.

## Delivery plan and exit criteria

### Phase 0 — Contract hygiene and ownership

- Complete extraction of consumer-specific runtime routes/defaults into consumer-owned packages or
  generic schema-driven ingress.
- Add an ownership check to release review: no sibling repository names, local paths, consumer
  deployment identities, or private-default values in tracked release assets.
- Publish the qualification schema and generate a baseline report for existing adapters.

**Exit:** public contracts and deployment assets have clear neutral ownership; the report marks
unknown/stale evidence honestly rather than upgrading it by assumption.

### Phase 1 — Temporal local-conformant adapter

- Create the three Temporal packages and generic coordinator/activity model.
- Implement the Postgres projection store and object-store composition.
- Pass the shared suite against disposable Temporal and Postgres dependencies.
- Ship worker-host and API-host samples with no customer-specific settings.
- Document the wait/re-execution, retry, payload, and Temporal-history limits as part of the
  adapter's operational policy.

**Exit:** one unchanged sample Vyral plugin passes the local adapter suite and the Temporal suite,
including worker restart, timer, signal, checkpoint, and idempotency cases.

### Phase 2 — Temporal live qualification and operations

- Add preflight, redacted diagnostics, dispatch reconciliation, and maintenance metrics.
- Run the self-hosted and Temporal Cloud isolated gates.
- Publish an operator guide covering namespace/task-queue policy, mTLS/auth, worker rollout,
  history rollover, projection backup, and incident recovery.

**Exit:** reproducible live evidence is attached to a release, no secret/tenant data appears in
artifacts, and recovery drills meet the documented portable results.

### Phase 3 — Cross-adapter proof

- Run the same plugin and labeled execution workload under local, Azure, AWS, Google, and
  Temporal where each advertised capability exists.
- Add provider-specific divergence tests only where the portable contract deliberately exposes a
  capability or operational-policy difference.
- Exercise CanonicalStore MySQL-to-PostgreSQL archive migration and projection rebuild alongside
  the workload.

**Exit:** a published compatibility matrix links every claim to a shared-test and live-gate result;
the consumer guide gives a concrete migration/recovery procedure rather than a conceptual promise.

### Phase 4 — Ecosystem leverage

- Evaluate a Temporal external-worker bridge, a Kubernetes adapter, and one framework-neutral RAG
  integration protocol only after Phase 3 proves the contract under real use.
- Add adapters only when they can satisfy the same qualification bar; do not accept feature-only
  integrations that create an untested portability escape hatch.

**Exit:** at least two independent consumers use portable plugins and the compatibility corpus is
maintained as part of normal releases.

## Measures of success

The program should be judged with evidence, not package count:

- percentage of advertised capabilities with a current live qualification record;
- cross-adapter conformance pass rate and number of declared divergences;
- mean time to detect and reconcile an undelivered start/signal dispatch;
- recovery drill success for worker restart, duplicate delivery, timer/event race, and lease expiry;
- consumer provider-switch exercise time and number of consumer source changes outside configuration;
- archive migration verification time, restored fence/outbox correctness, and projection rebuild success;
- retrieval quality and latency deltas on labeled cases, reported separately from index cost;
- zero secret, endpoint, customer, or adjacent-repository findings in release scans.

## Decisions deferred

- Whether the first public Temporal qualification target is Temporal Cloud, self-hosted Temporal,
  or both. The adapter design supports both; the initial choice should follow operator access and
  cost review.
- Exact Postgres schema and migration tool. The design requires transactional projection/outbox
  semantics and isolated schema tests, not a specific migration library.
- The history rollover threshold. The current 32-transition policy is guarded by a 37-transition
  container/live scenario that caps each exact run at 512 events and 1 MiB encoded protobuf history
  and caps that scenario at eight chained runs. These are Vyral qualification guardrails, not
  Temporal service limits; coordinator-shape changes must re-run and review the measurements rather
  than inherit the threshold mechanically.
- Whether a CanonicalStore-backed Temporal projection is worthwhile after throughput and lock
  contention benchmarks.
- The first production retrieval projection. Selection remains evidence-driven and separate from
  the Temporal delivery path.

## References

- [Execution runtime design](execution-runtime.md)
- [Execution adapter matrix](execution-runtime-adapter-matrix.md)
- [Execution plugin authoring](execution-runtime-plugin-authoring.md)
- [Vyral conformance invariant](../conformance/invariant.md)
- [CanonicalStore contract](../docs/concepts/canonical-store.md)
- [Production retrieval adapter qualification](production-retrieval-adapter.md)
- [Temporal workflow documentation](https://docs.temporal.io/workflows)
