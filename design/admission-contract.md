# Atomic completion and durable admission

**Status:** accepted public contract

**Contract line:** Vyral `0.3.x`

**Receipt version:** `vyral.admission.v1`

## Decision

A public operation that mutates Vyral state has one of two completion boundaries:

1. It completes synchronously inside the storage adapter's documented atomic unit before the
   response is returned. CanonicalStore transactions are the strongest example: document state,
   revisions, fences, outbox events, and the idempotency result commit together or not at all.
2. It durably creates an execution run before returning. The response contains an
   `AdmissionReceipt`, HTTP `Location` names the polling resource, and an idempotency key can replay
   the same admission without creating new work.

There is no third “accepted in memory” state. HTTP `202` means the run is durably recoverable. A
runtime that cannot establish that boundary must return a non-2xx response and a rejected receipt;
it must not report acceptance.

## Receipt semantics

Every execution-backed REST job, raw execution run, and MCP task carries the same receipt shape.

| Field | Meaning |
| --- | --- |
| `version` | Receipt semantics version, currently `vyral.admission.v1`. |
| `admissionId` | Deterministic SHA-256 identity over receipt version, operation id, and durable resource id. |
| `operationId` | Authoritative public operation that first admitted the work. |
| `status` | `accepted` only after durable creation; otherwise `rejected`. |
| `resourceId` | Execution run or job id. |
| `requestHash` | Runtime hash of the admitted payload. |
| `idempotencyKeyHash` | SHA-256 of the caller key, never the raw key. |
| `replayed` | `true` on this response when an earlier admission was replayed. It is invocation provenance, not part of admission identity. |
| `admittedAtUtc` | Durable creation time of the original admission. |
| `statusUri` | Stable polling URI. |
| `resultUri` | Optional separate result URI; terminal results are normally embedded in the polled resource. |
| `failureClass`, `error` | Bounded rejection details; present only when rejected. |

The same operation id, resource id, and admission id survive polling, list projections, process
restart, execution-adapter projection, MCP task polling, and idempotent replay. Raw idempotency keys
are write-only inputs and are removed from public resources.

If the same key is reused with a different request fingerprint, the runtime rejects the request as
an idempotency conflict. Callers should treat keys as opaque, operation-scoped secrets and retain
the returned resource id or `Location` alongside their own command record.

## HTTP behavior

- `200`, `201`, and `204` mean synchronous work completed at the endpoint's documented atomic
  boundary. They never mean that background work was merely scheduled.
- `202` includes `Location` and a resource containing an accepted `AdmissionReceipt`.
- Immediate admission rejection uses `400` for handler/plugin errors, `429` for capacity, `503` for
  platform unavailability, and `422` for other rejected requests. It does not include `Location`.
- Validation may fail before admission and return ordinary problem details. Once a durable run
  exists, terminal failure is reported by polling that run or its domain job projection.
- `Idempotency-Key` is the canonical header. A legacy body key is accepted only by raw execution
  start and must match the header when both are present.

Idempotency remains optional for compatibility, but it is strongly recommended for every
receipt-bound submission and required for a caller that intends to retry after a lost response.

## Operation classification

The following aggregate writes are execution-backed because their providers cannot promise a
portable all-or-none transaction across every item:

| Public operation | Durable resource |
| --- | --- |
| `createCollection`, `deleteCollection` | `/execution/runs/{id}` |
| `importCollection`, `startCollectionImportJob` | `/record-import/jobs/{id}` |
| `upsertRecords`, `startRecordBatchUpsertJob` | `/record-import/jobs/{id}` |
| `importGraphEnvelope`, `startGraphImportJob` | `/graph/jobs/{id}` |
| `ingestRagText`, `startRagIngestionTextJob` | `/rag/ingestion/jobs/{id}` |
| `ingestRagTextBatch`, `startRagIngestionBatchJob` | `/rag/ingestion/jobs/{id}` |
| `ingestRecordArtifact` | `/execution/runs/{id}` |
| `runProviderCapability`, `startProviderRunJob` | `/provider-jobs/{id}` |
| embedding, provider, retrieval-evaluation, graph inspection/doctor, and raw execution starts | Their documented job or run URI. |

The shorter aggregate routes are compatibility aliases with their own public operation ids, not
synchronous loopholes. Their receipt preserves the route operation id that first admitted the run.
The corresponding `/jobs` routes remain explicit start aliases. A RAG text request with
`options.dryRun=true` is the exception: it performs no mutation and returns its plan synchronously.

Per-item `continueOnError` behavior belongs inside the admitted job and is visible in its terminal
result. It no longer weakens the HTTP admission boundary.

Single record/object changes, execution state transitions, and
CanonicalStore administrative operations remain synchronous only where the active
adapter performs the mutation transactionally before returning. New aggregate or cross-store
writes default to execution-backed admission unless every supported adapter can prove an atomic
implementation.

Collection lifecycle is receipt-bound even though the local relational providers can complete it
transactionally: DynamoDB creation spans table provisioning and catalog publication, while
DynamoDB and Firestore deletion may require multiple batches. Provider runs are also receipt-bound
because a remote provider can finish work before Vyral persists its trace or returns the HTTP
response.

The public SDK catalog records this decision in `completionBoundary` as `non-mutating`, `atomic`,
`receipt-bound`, or `maintenance`. Maintenance identifies bounded administrative reconciliation,
qualification, probe, or pruning over already durable state; these operations default to dry-run
where applicable, return exact affected ids/failures, and are safe to repeat. They do not admit new
application work. Catalog generation rejects any receipt-bound operation whose OpenAPI `202` lacks
the admission resource, `Location`, or idempotency support.

## Cross-store artifact ingestion

Multipart artifact ingestion first writes bytes to a private, content-hashed staging object. Only
then does it create the authoritative execution run. The handler publishes the final object and
record idempotently, returns `ArtifactRecordIngestReceipt` as its terminal execution result, and
best-effort removes staging data. A retry can therefore finish a record write after a completed
object write. Staging data is not a public acceptance receipt and must never be exposed as the
published artifact.

## MCP `2026-07-28`

MCP remains a stateless projection of these semantics. Routing, protocol version, capabilities,
and credentials arrive on every request, so any gateway instance can authorize and dispatch the
call without a server session. Routing headers are inputs to routing, never authorization claims.

Long-running tools require the Tasks capability. The MCP task envelope durably stores the same
`AdmissionReceipt` as soon as the execution start result is available, and restores it on polls
served by another instance. The execution runtime remains the run authority; the task store is the
durable protocol projection. Direct MCP execution start applies the same header/body idempotency
consistency rule as REST.

## Adapter and conformance obligations

An execution adapter is conformant only if durable creation and idempotency lookup occur in its
documented storage boundary, replay provenance is preserved, and an accepted run survives process
restart. Domain job adapters must derive their receipts from the underlying run rather than minting
an unrelated job identity.

Contract gates verify OpenAPI response shapes, `Location`, operation-specific receipt identity,
raw-key redaction, first-admission versus replay provenance, immediate rejection status, terminal
polling, MCP task restoration, and parity between the .NET and Python local runtimes. Cloud and
Temporal qualification must repeat those checks against live durable infrastructure before its
evidence level is promoted.
