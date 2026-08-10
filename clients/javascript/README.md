# @vyral/client

Thin JavaScript client for the Vyral local HTTP server.

The package ships generated TypeScript declarations and OpenAPI-derived interfaces through its `types` export; no separate `@types` package is required.

```js
import {
  TERMINAL_EXECUTION_RUN_STATUSES,
  VyralClient,
  buildProviderExtractRequest,
  buildProviderReviewRequest,
  buildProviderToolPlanRequest,
  buildRagContextRequest,
  buildRagTextIngestionRequest,
  buildRetrievalEvaluationCase,
  buildRetrievalEvaluationExpectedMatch,
  buildRetrievalEvaluationRequest,
  buildVerifiedRetrievalRequest,
  compareRagIngestResults,
  isExecutionRunTerminal
} from "@vyral/client";

const client = new VyralClient("http://localhost:5220");
// const client = new VyralClient("http://localhost:5220", { apiKey: "dev-secret" });
console.log(await client.health());
console.log(await client.readiness());
let artifactRun = await client.ingestRecordArtifact(
  {
    collection: "consumer-events",
    record: {
      id: "event-1",
      partitionKey: "tenant-a",
      type: "consumer.event",
      content: { status: "created" }
    },
    artifact: {
      container: "consumer-artifacts",
      key: "events/event-1.json",
      contentType: "application/json"
    }
  },
  new Blob([JSON.stringify({ event: "created" })], { type: "application/json" }),
  { fileName: "consumer-artifact.json", idempotencyKey: "consumer-event-1" }
);
artifactRun = await client.waitExecutionRun(artifactRun.id);
const artifactReceipt = artifactRun.result;
const canonical = await client.commitCanonicalTransaction("tenant-a", {
  tenantId: "tenant-a",
  idempotencyKey: "claim:claim-42:approve:v1",
  mutations: [{
    operation: "upsert",
    document: {
      tenantId: "tenant-a",
      documentType: "claim",
      id: "claim-42",
      schemaVersion: "v1",
      data: { status: "approved" },
      indexes: { status: "approved" }
    }
  }],
  outbox: [{ topic: "canon.claim.approved", key: "claim-42", payload: { claimId: "claim-42" } }]
});
const canonicalSnapshot = await client.exportCanonicalTenant("tenant-a");
console.log(await client.listCollections());
const embeddingDoctor = await client.getEmbeddingProviderDoctor();
await client.createRagCollection("chunks");
const vector = await client.embedText("retention policy");
const ingestionRequest = buildRagTextIngestionRequest("retention policy", "tenant-a", {
  skipUnchangedChunks: true,
  persistManifest: true
});
const plan = await client.planRagTextIngestion("chunks", ingestionRequest);
const ingestionJob = await client.commitRagTextIngestion(
  "chunks", ingestionRequest, plan, { idempotencyKey: "rag-document-1" }
);
const ingested = (await client.waitRagIngestionJob(ingestionJob.id)).textResult;
const comparison = compareRagIngestResults(plan, ingested);
const batchIngestionJob = await client.ingestRagTexts("chunks", [
  { documentId: "doc-1", partitionKey: "tenant-a", text: "retention policy" },
  { documentId: "doc-2", partitionKey: "tenant-a", text: "travel reimbursement" }
], { continueOnError: true, idempotencyKey: "rag-batch-1" });
const batchIngested = (await client.waitRagIngestionJob(batchIngestionJob.id)).batchResult;
const batchJob = await client.upsertRecords(
  "chunks",
  [{ id: "chunk-1", partitionKey: "tenant-a" }],
  { idempotencyKey: "record-batch-1" }
);
const batch = (await client.waitRecordImportJob(batchJob.id)).batchResult;
const snapshot = await client.exportCollection("chunks");
const records = await client.queryAllRecords("chunks", { limit: 100 });
const context = await client.buildRagContext(buildRagContextRequest("retention policy", ["chunks"]));
console.log(context.contextText);
console.log(context.contextTextHash);
const prompt = await client.buildRagPrompt({
  context: {
    retrieval: {
      query: "retention policy",
      collections: ["chunks"]
    },
    includeTrace: true
  },
  template: { failOnEmptyContext: true }
});
console.log(prompt.promptHash);
const verified = await client.retrieve(buildVerifiedRetrievalRequest(
  "RECORD-000123 update deadline",
  ["chunks"],
  { partitionKeys: ["tenant-a"] }
));
const evaluationCase = buildRetrievalEvaluationCase(
  "retention",
  { query: "retention policy", collections: ["chunks"], limit: 5 },
  buildRetrievalEvaluationExpectedMatch("chunk-1")
);
const evaluation = await client.evaluateRetrieval(buildRetrievalEvaluationRequest([evaluationCase]));
const providers = await client.listProviders();
const matrix = await client.getProviderCapabilityMatrix();
const doctor = await client.getProviderDoctor("local-deterministic-ai");
const models = await client.listProviderModels("local-deterministic-ai");
const readiness = await client.listProviderReadiness();
const quota = await client.getProviderQuota("codex-cli");
const qualified = await client.qualifyProvider("local-deterministic-ai", { capability: "ai.chat" });
const extractJob = await client.runProvider("local-deterministic-ai", buildProviderExtractRequest("source text", {
  schema: { type: "object" },
  instructions: "Return structured fields."
}), { idempotencyKey: "example:extract:1" });
const extract = (await client.waitProviderJob(extractJob.id)).result;
const job = await client.startProviderJob("local-deterministic-ai", {
  capability: "ai.chat",
  operation: "run",
  payload: { messages: [{ role: "user", content: "Summarize asynchronously." }] }
});
const jobStatus = await client.waitProviderJob(job.id);
const runtime = await client.getExecutionRuntime();
const maintenance = await client.getExecutionRuntimeMaintenance();
const prunePreview = await client.pruneExecutionRuntimeMaintenance({ retainTerminalRuns: 100 });
const reconcile = await client.reconcileExecutionRuntimeDispatch({ dryRun: true, limit: 100 });
const runs = await client.listExecutionRuns({ limit: 10, includeResult: true });
const run = await client.waitExecutionRun(job.id);
await client.raiseExecutionEvent(run.id, { name: "approved", payload: { by: "operator" } });
if (isExecutionRunTerminal(run)) {
  const history = await client.getExecutionRunHistory(job.id);
  const artifacts = await client.listExecutionRunArtifacts(job.id);
}
```

Client URLs must use HTTP or HTTPS and cannot contain user credentials. API keys,
bearer tokens, and credential headers require HTTPS except on an exact loopback
address used for local development. Credential-bearing requests do not follow redirects.

Configure transport behavior once for every method, or derive a request-scoped client without mutating the original:

```js
const client = new VyralClient("http://localhost:5220", {
  bearerToken: "gateway-token",
  headers: { "X-Consumer": "orders" },
  timeoutMs: 10_000,
  maxRetries: 2
}).withOptions({ correlationId: "order-sync-42" });

// Retries apply to reads and to writes with an explicit idempotency key only.
for await (const record of client.iterateRecords(
  "orders",
  { limit: 100 },
  { maxPages: 20, maxItems: 1000 }
)) {
  console.log(record.id);
}
```

`iterateCanonicalDocuments(...)`, `iterateRecords(...)`, and `iterateSearchRecords(...)` lazily follow continuation tokens with mandatory page bounds and optional item bounds. Their `queryAll*` / `searchAll*` counterparts retain collecting convenience behavior with the same bounds. Pass an `AbortSignal` at construction or through `withOptions(...)` for cancellation. HTTP failures expose `retryAfter` and `correlationId` alongside normalized problem details.

Use `createRagCollection(...)` to durably admit creation of the common Vyral RAG collection policy with the server's configured embedding dimensions. Poll the returned run with `waitExecutionRun(...)` before writing records. `createCollection(...)` and `deleteCollection(...)` follow the same receipt-bound lifecycle because some cloud providers require multiple remote operations. Use `buildRagCollectionPolicy(...)` when an app wants to inspect or customize the policy before admission.
Use `commitCanonicalTransaction(...)` for authoritative tenant-domain changes that must atomically include documents, fences, and an outbox. `getCanonicalDocument(...)`, `queryCanonicalDocuments(...)`, `queryAllCanonicalDocuments(...)`, `listCanonicalDocumentRevisions(...)`, `leaseCanonicalOutbox(...)`, `acknowledgeCanonicalOutbox(...)`, `releaseCanonicalOutbox(...)`, `exportCanonicalTenant(...)`, and `restoreCanonicalTenant(...)` map directly to the CanonicalStore surface. Lease tokens remain request-body values; do not include them in URLs or logs. `listCanonicalMigrations(...)`, `applyCanonicalMigrations(...)`, `getCanonicalPreflight(...)`, and `probeCanonicalDataPlane(...)` require canonical administrator access when shared tenant policies are enabled. The last method explicitly creates and cleans isolated probe state; the passive preflight remains read-only.
Use `buildEvidenceBriefTransaction(...)`, `storeEvidenceBrief(...)`, and `getEvidenceBrief(...)` for `vyral.evidence-brief.v1`: a dated fact/evidence/citation artifact, not a generated answer or persona prompt. The server validates the full contract before committing it. Preserve source SHA-256 snapshots and credential-free source URIs; the default change outbox event wakes projections but does not prove a projection is complete.
Use `readiness()` before consumer handoff to inspect storage reachability, SQLite diagnostics, embedding provider checks, model-file/runtime state, API-key posture, provider caps, provider readiness counts, warnings, and blockers in one response.
Use `buildVerifiedRetrievalRequest(...)` for source-reference, exact-identifier, and verified-record retrieval paths where lexical matching should be the operational default and vectors should not dominate verified-record lookup.
Use `listRetrievalProfiles()` and `buildRetrievalProfileRequest(...)` for built-in profiles such as `evidence`, `ragBaseline`, `rerankPolish`, `deepQuality`, `discovery`, and `productOptimization`. Use `buildRetrievalEvaluationExpectedMatch(...)`, `buildRetrievalEvaluationHardNegative(...)`, `buildRetrievalEvaluationCase(...)`, `buildRetrievalEvaluationRequest(...)`, `buildRetrievalEvaluationVariant(...)`, and `buildRetrievalEvaluationComparisonRequest(...)` to avoid hand-shaping labeled retrieval quality requests. Use `compareRetrievalEvaluations(...)` to evaluate multiple retrieval variants against the same labeled cases. Use `startRetrievalEvaluationJob(...)`, `startRetrievalEvaluationComparisonJob(...)`, `getRetrievalEvaluationJob(...)`, `waitRetrievalEvaluationJob(...)`, and `cancelRetrievalEvaluationJob(...)` for long evaluation runs that need progress, partial results, polling, or cancellation.
`buildRagContextRequest(...)` accepts `profile`, semantic retrieval options, and `maxCitationsPerChunk` so RAG consumers can reuse retrieval profiles without forcing lexical defaults.
Use `listEmbeddingProviderGuidance()` and `getEmbeddingProviderDoctor()` to inspect embedding provider quality, hardware shape, model-file/runtime state, fallback reason, and suggested evaluation variants before running retrieval workloads.
Use `buildRagTextIngestionRequest(...)` and `ingestRagText(...)` for the default text-to-chunk-record RAG path. Set `options.dryRun: true` to synchronously preview chunk IDs, hashes, planned writes, stale delete records, manifest changes, action summaries, and a stable `planHash` without mutating the store. A mutating call returns a durable `RagIngestionJob`; poll it before reading `textResult`. Use `planRagTextIngestion(...)`, `commitRagTextIngestion(...)`, `summarizeRagIngestResult(...)`, and `compareRagIngestResults(...)` to carry expected plan/manifest hashes into `options.expectedPlanHash` / `options.expectedManifestHash` and detect ingestion-plan drift. Set `options.skipUnchangedChunks`, `options.reuseExistingChunkVectors`, `options.deduplicateExistingChunks`, `options.replaceDocumentChunks`, and `options.persistManifest` for repeatable ingestion loops.
`ingestRagTexts(...)` and `upsertRecords(...)` also return durable jobs; their per-item outcomes are available as `batchResult` after polling. The explicit `startRagTextIngestionJob(...)`, `startRagTextBatchIngestionJob(...)`, and `startRecordBatchUpsertJob(...)` methods are equivalent `/jobs` aliases. Pass an idempotency key whenever submission may be retried.
Use `listRagIngestionJobs(...)`, `getRagIngestionJob(...)`, `waitRagIngestionJob(...)`, and `cancelRagIngestionJob(...)` to manage any RAG job regardless of which start alias admitted it.
Use `exportCollection(...)` to snapshot the collection policy, records, and content hash before destructive local changes or when seeding another environment. Exports default to the 10,000-record snapshot cap. Pass `query`, `maxRecords`, and `failOnLimitExceeded: false` for paged snapshots; truncated responses include `truncated` and `continuationToken`. Use `importCollection(...)` to restore or seed from a snapshot with explicit `replaceExisting`, `continueOnError`, `allowCollectionRename`, `allowPartialSnapshot`, and `expectedContentHash` controls.
Use `inspectCollection(...)` to check record counts, vector policy coverage, embedding provider/model distribution, RAG chunk/manifest counts, and capped vector anomalies before quality runs. The local SQLite adapter uses deterministic exact vector scans; provider adapters own any production index acceleration. Retrieval diagnostics and retrieval-evaluation top results preserve the active search shape.
Use `buildRagContextRequest(...)` and `buildRagContext(...)` with `includeContextText: true` when an app wants a deterministic citation-markdown block plus `contextTextHash`; the structured `chunks` and `citations` fields remain available for custom prompt assembly.
Use `preflightGraphImport(...)`, `importGraphEnvelope(...)`, `inspectGraph(...)`, `doctorGraph(...)`, `traverseGraph(...)`, `buildGraphExpansionOptions(...)`, and `evaluateRagContext(...)` for GraphRAG workflows with import preview, seed-coverage readiness, bounded traversal, compact contribution summaries, and failure-category diagnostics.
Use `startGraphImportJob(...)`, `startGraphInspectionJob(...)`, or `startGraphDoctorJob(...)` with `buildGraphCollectionImportRequest(...)`, `buildGraphInspectionRequest(...)`, or `buildGraphDoctorRequest(...)` respectively for longer graph work. `listGraphJobs(...)`, `getGraphJob(...)`, `waitGraphJob(...)`, and `cancelGraphJob(...)` provide the same durable execution lifecycle; use an idempotency key when retrying submission.
Use `buildRagPrompt(...)` when an app wants Vyral to render deterministic chat-style `messages`, `prompt`, and `promptHash` over a normal context request without calling an answer model.
Use `evaluateRetrieval(...)` to run labeled retrieval cases and compare hit rate, MRR, precision/recall, nDCG, and top-result summaries during local R&D.
Use `embedTexts([...])` to generate ingestion vectors with the server's configured provider, then attach the returned values to records before calling `upsertRecords` when custom ingestion is needed. Pass `{ purpose: "query" }` / `{ purpose: "passage" }` and optional `queryPrefix`, `passagePrefix`, or `symmetricPrefix` when an asymmetric local model needs shaped embedding inputs. Use `startEmbeddingJob(...)`, `getEmbeddingJob(...)`, `waitEmbeddingJob(...)`, and `cancelEmbeddingJob(...)` for large embedding batches that need progress, partial results, polling, or explicit cancellation.
Use `pruneTraces(...)` for constrained local trace cleanup with dry-run support.
Use `listProviderQuotas()` and `getProviderQuota(...)` for advisory provider quota telemetry such as Codex app-server rate-limit buckets when exposed.
Use `buildProviderChatRequest(...)`, `buildProviderExtractRequest(...)`, `buildProviderRerankRequest(...)`, `buildProviderReviewRequest(...)`, `buildProviderScaffoldRequest(...)`, `buildProviderToolPlanRequest(...)`, `runProvider(...)`, and `runProviderExtract(...)` for common AI calls. Provider runs are receipt-bound: both run helpers return `ProviderRunJob`, and `waitProviderJob(...)` yields the terminal `result`. Use `listProviders()`, `getProviderCapabilityMatrix()`, `getProvider(...)`, `listProviderDoctor()`, `getProviderDoctor(...)`, `listProviderModels(...)`, `listProviderReadiness()`, `getProviderReadiness(...)`, `listProviderQuotas()`, `getProviderQuota(...)`, `listProviderQualifications(...)`, `qualifyProvider(...)`, `startProviderJob(...)`, `listProviderJobs({ provider, limit, includeResult })`, `getProviderJob(...)`, and `cancelProviderJob(...)` for broader provider workflows.
Use `getEffectiveExecutionRuntime(...)` for caller-scoped execution discovery; `getExecutionRuntime()` exposes the global catalog and requires a maintenance identity. `startExecutionRun(...)`, `getExecutionRuntimeMaintenance()`, `pruneExecutionRuntimeMaintenance(...)`, `reconcileExecutionRuntimeDispatch(...)`, `listExecutionRuns(...)`, `getExecutionRun(...)`, `waitExecutionRun(...)`, `cancelExecutionRun(...)`, `getExecutionRunHistory(...)`, `listExecutionRunArtifacts(...)`, `getExecutionRunArtifact(...)`, and `getExecutionRunCheckpoint(...)` support the provider-neutral execution view shared by provider jobs, embedding jobs, retrieval evaluations, RAG ingestion, graph work, and custom plugins. External workers use `leaseExternalExecutionRun(...)`, heartbeat/report/event/artifact/checkpoint/read/wait/complete helpers; keep lease tokens out of URLs and logs. `startExecutionRun(...)` starts registered handlers with plugin-owned payload JSON; `listExecutionRuns(...)` supports portable handler/plugin/status/correlation/idempotency/time/tag filters. Maintenance pruning defaults to dry-run and reconciliation is available when the active runtime implements the optional maintenance interface. `startRecordBatchUpsertJob(...)` accepts `productId` and `tenantId` options for shared execution scope, and `waitRecordImportJob(...)` polls its durable result. `TERMINAL_EXECUTION_RUN_STATUSES` and `isExecutionRunTerminal(...)` help consumers write polling code without hard-coding lifecycle strings.

## Direct operation index

The higher-level examples above cover the common paths. These direct methods expose the rest of the server contract without requiring consumers to construct URLs:

- Discovery and embedding: `openApiContract()`, `getPublicSchemaContract()`, `listEmbeddingProviders()`, `listEmbeddingJobs(...)`, `listGraphProviderShapes()`, and `getGraphProviderShape(...)`.
- Canonical outbox recovery: `queryCanonicalOutbox(...)`, `renewCanonicalOutboxLease(...)`, and `replayCanonicalOutbox(...)`.
- Collections and records: `getCollectionPolicy(...)`, `deleteCollection(...)`, `startCollectionImportJob(...)`, `exportGraphEnvelope(...)`, `getRecord(...)`, `deleteRecord(...)`, `queryRecords(...)`, and `searchRecords(...)`.
- Binary objects: `listObjects(...)`, `putObject(...)`, `getObject(...)`, and `deleteObject(...)`.
- Retrieval operations: `retrieve(...)` and `listRetrievalEvaluationJobs(...)`.
- Trace inspection: `listTraces(...)`, `summarizeTraces(...)`, `exportTraces(...)`, and `getTrace(...)`.
- Durable record import administration: `listRecordImportJobs(...)`, `getRecordImportJob(...)`, and `cancelRecordImportJob(...)`.
- External execution workers: `heartbeatExternalExecutionLease(...)`, `reportExternalExecutionLease(...)`, `recordExternalExecutionLeaseEvent(...)`, `putExternalExecutionLeaseArtifact(...)`, `putExternalExecutionLeaseCheckpoint(...)`, `getExternalExecutionLeaseCheckpoint(...)`, `waitExternalExecutionLease(...)`, and `completeExternalExecutionLease(...)`.

Failed HTTP calls throw `VyralClientError` with `status`, raw `body`, parsed problem details (`problem`, `title`, `detail`, `type`, `instance`, `problemStatus`), and helper predicates such as `isMissingCollection()`, `isAuthError()`, `isValidationError()`, and `isTimeout()`.

```bash
npm test
```
