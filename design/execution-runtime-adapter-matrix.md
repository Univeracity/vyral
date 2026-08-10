# Execution Runtime Adapter Matrix

This matrix describes how current and future adapters map the provider-neutral execution runtime
into local SQLite, Azure, AWS, Google Cloud, Kubernetes, and Temporal. Capability disclosure is a
design contract; verification maturity is published separately in
[`qualification/adapter-qualification.json`](../qualification/adapter-qualification.json).

The invariant is that plugins keep targeting `Vyral.Execution`: runs, handlers, retries, timers,
events, leases, artifacts, traces, cancellation, idempotency, quotas, retention, and resume policy.
Adapters translate those concepts into provider mechanics and then prove the behavior through the
shared conformance suite.

## Published Qualification Baseline

| Adapter | Highest published level | Evidence boundary | Current live receipt |
| --- | --- | --- | --- |
| Local SQLite | `local_conformant` | Full shared runtime suite, restart fixture, and public-surface gate against an isolated SQLite store | Not applicable; no hosted-provider claim |
| Azure Durable Functions | `local_conformant` | Full SDK-neutral host suite, restart fixture, and public-surface gate | None retained in the baseline |
| AWS DynamoDB/SQS | `prototype` | Deterministic state/dispatch and external-worker unit/conformance fixtures | None retained in the baseline |
| Google Firestore/Cloud Tasks | `prototype` | Deterministic state/dispatch and external-worker unit/conformance fixtures | None retained in the baseline |
| Temporal | `prototype` | Deterministic coordinator, projection, preflight, and rollover fixtures; process-restart gate receipt still pending | None retained in the baseline |

The checked-in report is intentionally conservative. Opt-in live tests do not change a release
claim unless their redacted result and cleanup proof are retained in the schema-governed artifact.
The release generator marks evidence stale after 90 days rather than rewriting historical proof.

## Portable Consumer Workload

`ExternalExecutionConsumerSample` is the labeled `portable-consumer-workload/v1` fixture. Its
plugin source lives only in
[`tests/Vyral.Tests.Conformance/ExternalExecutionConsumerSample.cs`](../tests/Vyral.Tests.Conformance/ExternalExecutionConsumerSample.cs):
three deterministic input items produce one stable digest, progress counters, step history, and a
JSON artifact, while a duplicate start must resolve to the same run. Adapter tests consume that
same compiled plugin; they do not copy or translate its handler.

| Adapter | Execution model used | Deterministic/shared evidence | Live evidence retained |
| --- | --- | --- | --- |
| Local SQLite | in-process and external worker | Local→Azure switch test plus `LocalExternalWorker_RunsPortableConsumerPluginUnchanged` | Not applicable |
| Azure Durable Functions | in-process | [`AzureDurableExecutionConsumerSwitchingTests`](../tests/Vyral.Tests.Azure/AzureDurableExecutionConsumerSwitchingTests.cs) runs the same instance under Local and Azure and compares the digest | None |
| AWS DynamoDB/SQS | external worker | [`AwsExternalWorker_RunsPortableConsumerPluginUnchanged`](../tests/Vyral.Tests.Aws/AwsDynamoExecutionRuntimeAdapterConformanceTests.cs) executes it through `ExecutionPluginWorker` and the deterministic DynamoDB/dispatch fixture | None |
| Google Firestore/Cloud Tasks | external worker | [`GoogleExternalWorker_RunsPortableConsumerPluginUnchanged`](../tests/Vyral.Tests.Google/GoogleCloudExecutionRuntimeAdapterConformanceTests.cs) executes it through the same worker host and deterministic Firestore/dispatch fixture | None |
| Temporal | in-process activity | `TemporalContainer_RunsPortableConsumerPluginUnchanged` is part of the isolated Temporal/PostgreSQL container gate | None; the gate remains unpromoted until a current redacted receipt exists |

This closes the source-portability seam but not Phase 3's live-provider exit criterion. AWS and
Google still have `prototype` qualification, and Temporal's container case being present or locally
skipped is not a live result. The external execution model also deliberately exposes fewer handler
context operations today: progress, trace events, artifacts, checkpoints, results, and durable
waits are mapped; handler-side coordination leases, standalone timer creation, and worker-raised
external events are rejected explicitly. The workload does not depend on those unavailable calls.

## Adapter Rules

- Provider names and SDK types do not appear in core runtime models.
- Consumers branch on portable capabilities and status policy, not on provider identity.
- Each adapter publishes `operationalPolicy` and `resumePolicy` from
  `/execution/runtime/effective` for consumers (or `/execution/runtime` for a maintenance
  identity).
- Run ids, handler ids, plugin ids, correlation ids, and idempotency keys remain portable strings.
- Handler execution is at-least-once after restart recovery; plugins own external side-effect
  idempotency and checkpoint interpretation.
- Artifacts, traces, leases, timers, and external events remain run-owned unless an adapter
  explicitly documents a broader retention scope.

## Concept Matrix

| Runtime concept | AWS target | Google Cloud target | Kubernetes target | Temporal target |
| --- | --- | --- | --- | --- |
| run record | DynamoDB item or relational row keyed by run id | Firestore/Datastore row or relational row keyed by run id | Custom resource, SQL row, or object-store-backed status row | Workflow execution plus optional visibility/status store |
| dispatch | Step Functions, SQS/Lambda worker, ECS/Fargate task, or batch worker | Workflows, Cloud Tasks/Run worker, Pub/Sub worker, or Batch job | Job, worker Deployment queue consumer, or controller reconciliation | Workflow task scheduling |
| handler activity | Lambda/ECS/Fargate activity or worker callback | Cloud Run/Functions activity or worker callback | Container step or worker handler invocation | Activity |
| durable timers | EventBridge Scheduler, Step Functions wait, or delayed queue | Cloud Tasks schedule time, Workflows sleep, or Scheduler | controller requeue time, CronJob, or delayed queue | Workflow timer |
| external events | EventBridge, SNS/SQS, or API Gateway callback into run store | Pub/Sub, Eventarc, or HTTP callback into run store | Kubernetes Event/custom resource update or queue message | Signal |
| cancellation | Step Functions stop, queue tombstone, worker token cancellation, status flag | workflow cancellation, queue tombstone, worker token cancellation, status flag | Job deletion, pod signal, status flag, controller reconcile | Workflow cancellation |
| retry | provider retry policy plus runtime attempt state | provider retry policy plus runtime attempt state | controller/worker retry plus runtime attempt state | Activity retry policy plus workflow state |
| leases | DynamoDB conditional write, S3 conditional object, or relational compare-and-swap | Firestore transaction, GCS conditional object, or relational compare-and-swap | Kubernetes Lease object or SQL/object-store compare-and-swap | Workflow state or external lock activity |
| artifacts | S3 object plus metadata row | Cloud Storage object plus metadata row | object store, PVC, or artifact sidecar plus metadata row | external object store plus workflow/status reference |
| traces/history | CloudWatch plus runtime trace store | Cloud Logging plus runtime trace store | pod logs/events plus runtime trace store | workflow history plus runtime trace store |
| idempotency | stable key lookup before scheduling; optional deterministic execution name | stable key lookup before scheduling; optional deterministic workflow/task name | stable key lookup before creating Job/work item | workflow id reuse policy plus runtime key lookup |
| quotas/backpressure | queue depth, DynamoDB conditional counters, reserved concurrency | queue depth, Firestore counters, Cloud Run/task concurrency | controller limits, work queue depth, namespace quotas | worker task queue limits and workflow rate limits |
| retention | TTL tables, object lifecycle, log retention | TTL policies, object lifecycle, log retention | garbage collection controller and object lifecycle | workflow retention plus external status/artifact retention |
| restart resume | active run store scan and re-dispatch | active run store scan and re-dispatch | controller reconciliation of active run records | workflow replay and activity redelivery |

## Provider Notes

AWS adapters should choose between orchestration-first and queue-first designs. Step Functions is
natural for explicit run orchestration and timers; SQS/Lambda or ECS/Fargate workers are simpler
for high-volume activity dispatch. Either path still needs a runtime-owned status store for
portable run listing, traces, artifacts, leases, idempotency checks, and retention.

Google Cloud adapters have similar choices. Workflows can model orchestration and timers, while
Cloud Tasks, Pub/Sub, Cloud Run, and Batch can carry worker execution. The adapter should keep
runtime state in a queryable store so `/execution/runs`, history, artifacts, operational policy,
and resume policy do not depend on a provider console.

Kubernetes adapters should prefer reconciliation over one-off imperative scheduling. A controller
can observe durable run records, create Jobs or queue work, requeue due timers, reconcile
cancellation, and garbage collect run-owned state. The runtime contract should not require
clusters to install Temporal or any specific queue, but a Kubernetes adapter may use them behind
the boundary.

Temporal is closest to the execution runtime shape, but it still must remain an adapter. Workflows
map naturally to runs, activities to handlers, timers to timers, signals to external events, and
workflow replay to restart resume. The adapter must still expose portable status, artifact,
trace, lease, idempotency, and policy surfaces instead of leaking Temporal SDK types into plugins.

## Conformance Target

Every future adapter should run the same behavioral fixture used by local SQLite and Azure:

- descriptor validation and capability disclosure
- operational and resume policy disclosure
- plugin registration and handler invocation
- progress, status details, traces, artifacts, and bounded query surfaces
- idempotent replay and idempotency conflict rejection
- missing-handler and plugin-mismatch rejection
- cancellation, retry, scheduled waiting work, and restart recovery
- leases, timers, external event shape, and run-owned retention where supported
- Production-fixture-derived pressure/checkpoint behavior expressed only as neutral status
  details, artifacts, retry, and idempotency semantics

Provider-specific tests can add dialect coverage, but they should not replace the shared
conformance target.

## Historical Draft Supersession

Older local notes may refer to an "execution fabric" abstraction with Temporal as a native portable
runtime. That is not the current portability contract. The current contract is Vyral-first:
consumers target `Vyral.Execution`, adapters target local SQLite, Azure Durable Functions, AWS,
Google Cloud, Kubernetes, Temporal, or later runtimes, and shared conformance determines whether an
adapter preserves the portable behavior. Temporal can still be mined for design ideas and can become
an adapter target, but it is not the ontology for Vyral plugins or consumers.
