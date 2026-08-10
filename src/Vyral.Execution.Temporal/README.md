# Vyral.Execution.Temporal

This package is the provider adapter boundary between portable `Vyral.Execution` contracts and a
Temporal coordinator. Portable plugins continue to reference only `Vyral.Execution`; this package
contains provider-owned identity, dispatch reconciliation, coordinator envelopes, and validated
Temporal settings.

The adapter follows a strict determinism boundary: ordinary Vyral handlers run only in Temporal
activities, never in workflow methods. Coordinator inputs contain a Vyral run id, projection
revision, generation, portable attempt/transition offsets, and buffered event identities—not a
consumer payload, event body, artifact, checkpoint, secret, or tenant identity.

This work is published as a prototype and does not register itself as a selectable runtime or claim
`local_conformant` or `live_qualified`. `TemporalExecutionRuntimeAdapter` provides the portable
API-side facade, and `Vyral.Execution.Temporal.Hosting` now provides the standard worker-side
handler context, Generic Host registration, durable outbox pump, and object-store composition for
oversized artifact bodies. Production readiness still requires a retained passing receipt from the
checked-in process-restart gate plus the live qualification and operator-recovery evidence
described by the design.

Production topology, security, rollout, backup/restore, and incident procedures are linked from the
[repository's Execution Runtime guide](https://github.com/univeracity/vyral#execution-runtime).

## Foundation surface

- `TemporalExecutionOptions` validates the non-secret adapter namespace, host/port, Temporal
  namespace, task queue, worker deployment/build labels, TLS posture, and dispatcher batch size.
  TLS can be disabled only for a loopback development target. Diagnostic metadata contains hashes
  of provider topology and deployment names rather than their raw values.
- `TemporalSdkCoordinatorClient` is the official-SDK boundary for starting the fixed coordinator,
  querying its Vyral identity after an ambiguous duplicate start, delivering event-id signals, and
  requesting cancellation. SDK errors are reduced to stable failure classes before the outbox
  observes them.
- `TemporalExecutionRuntimeAdapter` validates portable requests, keeps handler and plugin
  registration provider-neutral, persists a run before dispatch, canonicalizes idempotency input,
  and delegates all public reads and coordination state to the projection store. Its status marks
  the implementation `prototype_unqualified` rather than upgrading it from code presence alone.
- `TemporalExecutionDispatchReconciler` and `TemporalExecutionSignalReconciler` drain projection
  outboxes. A duplicate start is delivered only after the existing workflow reports the same
  Vyral run and coordinator generation.
- `TemporalExecutionCancellationReconciler` retries the durable cancellation outbox independently
  of the API request that recorded cancellation intent.
- `TemporalExecutionOutboxReconciler` gives API/worker hosts one safe operation for recurring
  start, signal, and cancellation redrive. PostgreSQL claims make concurrent host loops safe.
- `AddVyralTemporalExecution` composes one shared portable runtime, coordinator, reconciler, and
  preflight surface from host-owned Temporal/store services. `AddHostedWorker` adds the official
  Temporal Generic Host lifecycle and continuously drains all three durable delivery planes.
- `ITemporalExecutionProjectionStore` is the provider-owned seam for transactional start/signal
  outboxes and activity projection. It is not a new portable plugin contract.

## Operational policy and limits

The workflow id is `vyral:{adapterNamespace}:{runId}`. Requests cannot choose a Temporal namespace,
task queue, workflow type, worker endpoint, assembly, or handler type. Coordinator inputs contain
only the run id, projection revision, generation, portable counters, and buffered signal identities;
signals contain only an event id and event revision. Payloads, event bodies, status details,
artifacts, traces, checkpoints, tenant identity, and credentials must remain in Vyral-owned
projection/object storage. The real-server qualification suite proves this with distinct synthetic
sentinels retained in each projection surface and absent from the exact serialized coordinator
history, rather than inferring confidentiality solely from DTO shape.

Each activity carries one logical Vyral attempt number and uses bounded, heartbeating Temporal
transport retries to recover worker loss. A transport replay returns a durable attempt outcome when
one was already projected; otherwise the ordinary handler can re-execute under the explicit
at-least-once side-effect policy. Portable retry classification and delay remain Vyral-owned, and
retry delays are bounded to one day. Suspension ends the activity, and resumption invokes a new
handler attempt after the accepted wait result is projected. Wait results are claimed by the
resumed logical attempt and remain replayable until that attempt completes or registers its next
wait. This is `may_reexecute_handler`, not stack-frame continuation. External-event signals are
deduplicated by event id and revision, can arrive before the wait is registered, and are retained
until the projection store accepts one for the active wait. Projection operations must be
idempotent because activity completion and outbox delivery are at-least-once.

The coordinator continues as new after 32 coordination transitions or an earlier server suggestion.
It rolls only at an idle boundary, carries portable attempt and cumulative transition counters plus
unconsumed signal identities, and retains the logical Vyral run id and generation. A deterministic
Temporal patch marker preserves replay compatibility for workflows created before this branch was
introduced. Temporal run ids are chain-local diagnostics and never become public execution ids.
The normal suite replays a hash-pinned, sanitized successful history from before that marker existed
and includes an incompatible-command negative control, so the legacy branch is enforced offline on
every build rather than left as a code-review convention.

The companion `Vyral.Execution.Temporal.Postgres` package supplies the version-4 transactional
projection/outbox schema and activity/API-side state operations. Preflight and the disposable
Temporal/PostgreSQL shared-conformance gate are checked in; local-conformant promotion still waits
for a retained passing worker-process recovery receipt. An explicit operator-provisioned live gate
can reuse that suite with Temporal/PostgreSQL TLS, API-key or mTLS authentication, random resource
identity, and a second-pass projection cleanup audit; its receipt is scoped honestly to the
process-local object store and never promotes the adapter automatically. Do not use Temporal
visibility/history as the portable run, audit, authorization, or artifact store, and do not operate
this package as a production adapter until the published qualification record advances from
prototype.
