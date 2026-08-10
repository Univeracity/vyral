# Vyral.Execution.Temporal.Hosting

This package hosts the single generic `Vyral.RunCoordinator.v1` workflow and its activities. The
workflow contains only deterministic coordination: activity scheduling, retry delays, native
timers, event-id signals, and portable wait projection. Consumer handler code is invoked by the
activity executor and may perform normal asynchronous I/O there.

Worker hosts own Temporal client construction, TLS/auth material, task queue selection, registered
plugins, and worker identity. None of those settings can be selected by a portable run request.

For a .NET Generic Host, register the host-owned `ITemporalClient`, one
`ITemporalExecutionRuntimeStore`, and the intended `IObjectStore`, then use the supported
composition:

```csharp
var temporal = services
    .AddVyralTemporalExecution(executionOptions)
    .AddPlugin<MyPortablePlugin>();

temporal.AddHostedWorker(new TemporalExecutionWorkerHostOptions
{
    WorkerId = workerIdentity
});
```

`AddVyralTemporalExecution` creates one shared `TemporalExecutionRuntimeAdapter`, coordinator
client, aggregate outbox reconciler, and preflight surface. An API-only host omits
`AddHostedWorker` and does not load handler assemblies. A worker host adds the official Temporal
Generic Host lifecycle, the fixed Vyral workflow/activities, and a cancellation-aware background
pump for start, signal, and cancellation outboxes. The pump logs counts and fixed messages only;
durable row identities and provider exception text are not logged. Reconciliation is safe across
multiple worker replicas because the PostgreSQL store claims bounded batches.
The disposable and live qualification suites also start this exact Generic Host composition,
submit a portable handler through it, and require the hosted outbox pump to deliver the run before
the receipt records `generic_host_composition`.

`TemporalExecutionWorker` registers exactly one versioned workflow and three activities: execute a
Vyral attempt, project a wait resolution, and project cancellation. Activities delegate to the
host-provided `ITemporalExecutionActivityExecutor`. For normal hosts, the overload accepting an
`ITemporalExecutionHandlerResolver`, `ITemporalExecutionRuntimeStore`, options, and a provider-owned
worker id composes the standard `TemporalExecutionAttemptHandler`. That handler supplies the
portable `IExecutionRunContext`: progress, traces, artifacts, checkpoints, fenced leases,
timers, external events, and durable wait suspension/resumption are projected without making the
database responsible for invoking consumer code. The lower-level attempt-handler overload remains
available for custom host composition.

Activities heartbeat every five seconds under a thirty-second heartbeat timeout and have bounded
Temporal transport retries under a fixed ten-minute per-try start-to-close timeout. Every transport
retry carries the same Vyral attempt number; the projection replays an already persisted terminal,
retry, or suspension outcome instead of invoking the handler again. If the prior worker stopped
before persisting an outcome, the handler may re-execute under the documented at-least-once side
effect policy. Portable retry classification, count, and delay remain Vyral-owned.

The coordinator uses only replay-safe workflow APIs for delays, signals, queries, and cancellation.
It never opens a database connection, performs network I/O, reads a secret, invokes consumer code,
or places payload/artifact bodies in workflow history.

The coordinator rolls over after 32 coordination transitions or an earlier Temporal server
suggestion. Continue-as-new occurs only between activities and waits, carries the portable attempt
offset, cumulative transition count, and deduplicated event id/revision signals, and keeps the same
Vyral run id and coordinator generation. A Temporal patch marker protects replay of workflows that
started before rollover support. Temporal run ids may change across the chain and remain diagnostic
metadata only. The dependency-backed qualification gate drives 37 transitions, follows each exact
Temporal run in the chain, and rejects any run above 512 history events or 1 MiB of encoded protobuf
history. It also caps that scenario at eight chained runs. These are conservative Vyral release
guardrails, not Temporal service limits; a coordinator-shape change must re-run the measurement gate
before the threshold is retained. The normal test suite also replays a checked-in successful
coordinator history captured before the continue-as-new patch marker existed. This offline fixture
makes removal of the required legacy patch branch a deterministic test failure.

The dependency-backed history-boundary case writes distinct synthetic sentinels through a run
payload, external-event body, checkpoint, artifact body, trace, status details, and terminal result.
It verifies each body in the Vyral projection and then scans the exact serialized Temporal protobuf
history, failing if any sentinel appears there. Hashes and opaque ids may cross the coordinator
boundary; consumer-owned bodies may not.

Waits resume by re-executing the handler, not by restoring a CLR stack frame. Plugins must use
checkpoints and idempotent external side effects. Artifacts without an existing URI remain inline
up to `MaxArtifactInlineBytes`; with an `IObjectStore` supplied to the standard handler or worker
constructor, larger bodies are written beneath a run-owned prefix and projection metadata carries
only a `vyral-object://` reference, hash, size, and non-secret storage markers. Without that
composition, oversized writes fail before projection rather than silently entering PostgreSQL.

This remains a prototype host surface until its checked-in process-restart gate has produced a
retained passing receipt. Its Generic Host composition deliberately uses Temporal's official
hosting integration, but it does not yet enable Temporal Worker Versioning routing. Workers
advertise non-routing deployment/build metadata, and preflight
classifies current, other unversioned, unattributed, and unexpectedly versioned pollers without
returning raw worker identities or deployment labels. The disposable Temporal/PostgreSQL gate
exercises shared conformance, native timers/signals, object-store offload, bounded-history rollover,
and the real worker-process recovery drill. The opt-in live gate runs the same behavioral and
process-boundary coverage against operator-provisioned TLS/authenticated Temporal and PostgreSQL
endpoints with random identities and a cleanup audit. The neutral console host in
`samples/Vyral.Execution.TemporalSample` is operable example composition, not qualification
evidence by itself.

Use the operator material linked from the
[repository's Execution Runtime guide](https://github.com/univeracity/vyral#execution-runtime)
for production topology, replay-compatible rollout, credential rotation, backup/restore, and
incident recovery.

`TemporalExecutionPreflight` validates the redacted topology, checks Temporal workflow-service
health and namespace access, reports fresh workflow/activity poller and aggregate build-compatibility
counts from a read-only task queue inspection, reads projection schema/backlog state, and verifies
a write/read/delete object-store probe under a random isolated key. It never starts a workflow and returns fixed
blocker/warning codes instead of provider exception text. Missing or uninspectable worker pollers
remain declared warnings rather than inferred successes. For delivered, nonterminal runs it takes
a bounded projection snapshot and compares each included coordinator with the latest Temporal
workflow status. Missing or closed coordinators and partial/failed inspection are warnings; only
counts and coverage are returned, never workflow ids, worker identities, or observed build labels.
