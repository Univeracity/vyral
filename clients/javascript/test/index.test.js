import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import {
  TERMINAL_EXECUTION_RUN_STATUSES,
  VyralClient,
  VyralClientError,
  buildEvidenceBriefTransaction,
  buildGraphAssertion,
  buildGraphEdge,
  buildGraphEnvelope,
  buildGraphExpansionOptions,
  buildGraphDoctorRequest,
  buildGraphInspectionRequest,
  buildGraphNode,
  buildGraphReview,
  buildGraphScope,
  buildGraphSourceSpan,
  buildGraphTraversalRequest,
  buildProviderChatRequest,
  buildProviderExtractRequest,
  buildProviderRerankRequest,
  buildProviderReviewRequest,
  buildProviderScaffoldRequest,
  buildProviderToolPlanRequest,
  buildRagCollectionPolicy,
  buildRagContextRequest,
  buildRagTextIngestionRequest,
  buildRetrievalEvaluationCase,
  buildRetrievalEvaluationComparisonRequest,
  buildRetrievalEvaluationExpectedMatch,
  buildRetrievalEvaluationHardNegative,
  buildRetrievalEvaluationRequest,
  buildRetrievalEvaluationVariant,
  buildRetrievalProfileRequest,
  buildRerankOptions,
  buildVerifiedRetrievalRequest,
  compareRagIngestResults,
  getProviderRunRejection,
  isExecutionRunTerminal,
  isProviderRunOutputUsable,
  isProviderRunSucceeded,
  stampGraphNodeMetadata,
  summarizeRagIngestResult
} from "../src/index.js";

const evidenceBriefFixture = () => ({
  schema: "vyral.evidence-brief.v1",
  id: "brief-rates-2026-07-21",
  question: "What rate was published as of 2026-07-21?",
  asOfUtc: "2026-07-21T12:00:00Z",
  factAnchors: [{
    id: "rate-published",
    statement: "The official schedule lists the rate as 4.25 percent.",
    sourceSnapshotIds: ["official-schedule"],
    citationIds: ["official-schedule-page-4"]
  }],
  sourceSnapshots: [{
    id: "official-schedule",
    kind: "web",
    uri: "https://example.test/rates/schedule",
    contentHash: "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    capturedAtUtc: "2026-07-21T11:59:00Z"
  }],
  citations: [{
    id: "official-schedule-page-4",
    sourceSnapshotId: "official-schedule",
    factAnchorIds: ["rate-published"],
    counterEvidenceIds: [],
    displayText: "Official rate schedule, page 4"
  }],
  counterEvidence: [],
  uncertainties: [],
  retrievalTraces: [{
    traceId: "trace-rates-2026-07-21",
    retrievedAtUtc: "2026-07-21T11:58:00Z",
    queryHash: "sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
    matches: [{
      collection: "rates-public",
      recordId: "schedule-2026-07-page-4",
      rank: 1,
      sourceSnapshotIds: ["official-schedule"]
    }]
  }]
});

test("remaining base surface methods use expected routes", async () => {
  const responses = [
    new Response(JSON.stringify(["events"])),
    new Response(JSON.stringify({ name: "events" })),
    new Response(JSON.stringify({ id: "import-1" })),
    new Response(JSON.stringify({ id: "event-1" })),
    new Response(null, { status: 204 }),
    new Response(JSON.stringify({ id: "event-2" })),
    new Response(JSON.stringify({ items: [], continuationToken: null })),
    new Response(JSON.stringify({ container: "artifacts", key: "logs/event 1.json" })),
    new Response("artifact-body"),
    new Response(null, { status: 204 }),
    new Response(JSON.stringify({ matches: [] })),
    new Response(JSON.stringify([{ id: "trace-1" }])),
    new Response(JSON.stringify({ id: "trace-1" }))
  ];
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      requests.push({ url, init });
      return responses.shift();
    }
  });

  assert.deepEqual(await client.listCollections(), ["events"]);
  assert.equal((await client.getCollectionPolicy("event streams")).name, "events");
  assert.equal((await client.startCollectionImportJob(
    "event streams",
    { schema: "vyral.collection-snapshot.v1", records: [] },
    { idempotencyKey: "import-1" }
  )).id, "import-1");
  assert.equal((await client.getRecord("event streams", "tenant/a", "event/1")).id, "event-1");
  await client.deleteRecord("event streams", "tenant/a", "event/1");
  assert.equal((await client.upsertRecord("event streams", {
    id: "event-2",
    partitionKey: "tenant/a",
    type: "consumer.event"
  })).id, "event-2");
  assert.deepEqual((await client.listObjects("artifacts", {
    prefix: "logs/",
    limit: 2,
    continuationToken: "next/1"
  })).items, []);
  assert.equal((await client.putObject("artifacts", "logs/event 1.json", "artifact-body", {
    contentType: "application/json",
    metadata: { source: "sdk" },
    ifNoneMatch: "*"
  })).key, "logs/event 1.json");
  assert.equal(
    new TextDecoder().decode(await client.getObject("artifacts", "logs/event 1.json")),
    "artifact-body"
  );
  await client.deleteObject("artifacts", "logs/event 1.json", { ifMatch: '"etag-1"' });
  assert.deepEqual((await client.retrieve({ collections: ["events"], text: "incident" })).matches, []);
  assert.equal((await client.listTraces({ operation: "search", limit: 1 }))[0].id, "trace-1");
  assert.equal((await client.getTrace("trace/1")).id, "trace-1");

  assert.equal(responses.length, 0);
  assert.deepEqual(
    requests.map(({ url, init }) => [init.method, url]),
    [
      ["GET", "http://vyral.local/collections"],
      ["GET", "http://vyral.local/collections/event%20streams"],
      ["POST", "http://vyral.local/collections/event%20streams/import/jobs"],
      ["GET", "http://vyral.local/collections/event%20streams/records/tenant%2Fa/event%2F1"],
      ["DELETE", "http://vyral.local/collections/event%20streams/records/tenant%2Fa/event%2F1"],
      ["POST", "http://vyral.local/collections/event%20streams/records"],
      ["GET", "http://vyral.local/objects/artifacts?prefix=logs%2F&limit=2&continuationToken=next%2F1"],
      ["PUT", "http://vyral.local/objects/artifacts/logs/event%201.json"],
      ["GET", "http://vyral.local/objects/artifacts/logs/event%201.json"],
      ["DELETE", "http://vyral.local/objects/artifacts/logs/event%201.json"],
      ["POST", "http://vyral.local/search"],
      ["GET", "http://vyral.local/traces?operation=search&limit=1"],
      ["GET", "http://vyral.local/traces/trace%2F1"]
    ]
  );
  assert.equal(requests[2].init.headers.get("Idempotency-Key"), "import-1");
  assert.equal(requests[7].init.headers.get("Content-Type"), "application/json");
  assert.equal(requests[7].init.headers.get("X-Vyral-Meta-source"), "sdk");
  assert.equal(requests[7].init.headers.get("If-None-Match"), "*");
  assert.equal(requests[9].init.headers.get("If-Match"), '"etag-1"');
});

test("record artifact ingest sends manifest and binary multipart parts", async () => {
  const manifest = {
    collection: "events",
    record: { id: "event-1", partitionKey: "tenant-a", type: "consumer.event" },
    artifact: { container: "consumer-artifacts", key: "events/event-1.json" }
  };
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      requests.push({ url, init });
      return new Response(JSON.stringify({ id: "run-artifact-1", status: "queued" }), {
        status: 202
      });
    }
  });

  const result = await client.ingestRecordArtifact(
    manifest,
    new Blob(["artifact-body"], { type: "text/plain" }),
    { fileName: "event.json", contentType: "application/json", idempotencyKey: "artifact-1" }
  );

  assert.equal(result.id, "run-artifact-1");
  assert.equal(requests[0].url, "http://vyral.local/ingest/record-artifact");
  assert.equal(requests[0].init.method, "POST");
  assert.equal(requests[0].init.body.get("manifest"), JSON.stringify(manifest));
  const artifact = requests[0].init.body.get("artifact");
  assert.equal(artifact.name, "event.json");
  assert.equal(artifact.type, "application/json");
  assert.equal(await artifact.text(), "artifact-body");
  assert.equal(requests[0].init.headers.get("Idempotency-Key"), "artifact-1");
  assert.equal(requests[0].init.headers.has("Content-Type"), false, "fetch must add the multipart boundary");
});

test("raiseExecutionEvent uses the encoded run route and rejects mismatches", async () => {
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      requests.push({ url, body: JSON.parse(init.body) });
      return new Response(JSON.stringify({ accepted: true }), { status: 200 });
    }
  });

  const result = await client.raiseExecutionEvent("run/1", {
    name: "approved",
    payload: { by: "operator" }
  });

  assert.equal(result.accepted, true);
  assert.equal(requests[0].url, "http://vyral.local/execution/runs/run%2F1/events");
  assert.deepEqual(requests[0].body, { name: "approved", payload: { by: "operator" } });
  assert.throws(
    () => client.raiseExecutionEvent("run-1", { runId: "run-2", name: "approved" }),
    /must match/
  );
});

test("health and openApiContract use status routes", async () => {
  const responses = [
    { status: "ok", service: "vyral-server" },
    { status: "warning", ready: true, checks: [] },
    [{ provider: "deterministic-hash" }],
    [{ provider: "local-token-hash", realisticForSemanticRetrieval: false }],
    { provider: "local-token-hash", status: "ok", checks: [] },
    { openapi: "3.1.0" },
    { $schema: "https://json-schema.org/draft/2020-12/schema", $defs: {} }
  ];
  const urls = [];

  const client = new VyralClient("http://vyral.local", {
    fetch: async (url) => {
      urls.push(url);
      return new Response(JSON.stringify(responses.shift()), { status: 200 });
    }
  });

  const health = await client.health();
  const readiness = await client.readiness();
  const providers = await client.listEmbeddingProviders();
  const guidance = await client.listEmbeddingProviderGuidance();
  const doctor = await client.getEmbeddingProviderDoctor();
  const contract = await client.openApiContract();
  const schemas = await client.getPublicSchemaContract();

  assert.equal(health.status, "ok");
  assert.equal(readiness.ready, true);
  assert.equal(providers[0].provider, "deterministic-hash");
  assert.equal(guidance[0].provider, "local-token-hash");
  assert.equal(doctor.provider, "local-token-hash");
  assert.equal(contract.openapi, "3.1.0");
  assert.equal(schemas.$schema, "https://json-schema.org/draft/2020-12/schema");
  assert.deepEqual(urls, [
    "http://vyral.local/health",
    "http://vyral.local/readiness",
    "http://vyral.local/embedding-providers",
    "http://vyral.local/embedding-providers/guidance",
    "http://vyral.local/embedding-providers/doctor",
    "http://vyral.local/openapi/vyral.json",
    "http://vyral.local/contracts/schemas/vyral-public.schema.json"
  ]);
});

test("queryAllRecords drains continuation tokens", async () => {
  const responses = [
    { items: [{ id: "a" }], continuationToken: "next" },
    { items: [{ id: "b" }], continuationToken: null }
  ];
  const requests = [];

  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      requests.push({ url, body: JSON.parse(init.body) });
      return new Response(JSON.stringify(responses.shift()), { status: 200 });
    }
  });

  const items = await client.queryAllRecords("chunks", { limit: 1 });

  assert.deepEqual(items.map((item) => item.id), ["a", "b"]);
  assert.deepEqual(requests.map((request) => request.body), [
    { limit: 1 },
    { limit: 1, continuationToken: "next" }
  ]);
});

test("record async iterator enforces page and item bounds", async () => {
  const responses = [
    { items: [{ id: "a" }, { id: "b" }], continuationToken: "next" },
    { items: [{ id: "a" }], continuationToken: "next" }
  ];
  const client = new VyralClient("http://vyral.local", {
    fetch: async () => new Response(JSON.stringify(responses.shift()), { status: 200 })
  });

  const bounded = [];
  for await (const item of client.iterateRecords("chunks", {}, { maxItems: 1 })) bounded.push(item.id);
  assert.deepEqual(bounded, ["a"]);
  await assert.rejects(
    async () => {
      for await (const _item of client.iterateRecords("chunks", {}, { maxPages: 1 })) {
        // Drain the bounded iterator.
      }
    },
    /maxPages=1/
  );
  await assert.rejects(
    async () => {
      for await (const _item of client.iterateRecords("chunks", {}, { maxPages: 0 })) {
        // Validation happens before the first request.
      }
    },
    /maxPages/
  );
  assert.equal(responses.length, 0);
});

test("EvidenceBrief helpers use the validated CanonicalStore transaction and body read routes", async () => {
  const brief = evidenceBriefFixture();
  const responses = [
    { transactionId: "brief-tx", documents: [{ id: brief.id }] },
    {
      tenantId: "tenant-a",
      documentType: "vyral.evidence-brief",
      id: brief.id,
      schemaVersion: "vyral.evidence-brief.v1",
      data: brief
    }
  ];
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({ method: init.method, url, body: init.body ? JSON.parse(init.body) : null });
      return new Response(JSON.stringify(responses.shift()), { status: 200 });
    }
  });

  const stored = await client.storeEvidenceBrief("tenant-a", brief, { idempotencyKey: "brief:rates:v1" });
  const loaded = await client.getEvidenceBrief("tenant-a", brief.id);

  assert.equal(stored.transactionId, "brief-tx");
  assert.deepEqual(loaded.brief, brief);
  assert.deepEqual(requests.map(request => ({ method: request.method, url: request.url })), [
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/transactions" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/documents/read" }
  ]);
  assert.equal(requests[0].body.mutations[0].document.documentType, "vyral.evidence-brief");
  assert.equal(requests[0].body.outbox[0].topic, "vyral.evidence-brief.changed");
  assert.deepEqual(requests[1].body, {
    tenantId: "tenant-a",
    documentType: "vyral.evidence-brief",
    id: brief.id,
    includeDeleted: false
  });
  assert.throws(() => buildEvidenceBriefTransaction("tenant-a", "brief:bad:v1", { ...brief, schema: "wrong" }), /brief\.schema/);
});

test("canonical store helpers use tenant-scoped routes and keep lease tokens in bodies", async () => {
  const responses = [
    [],
    null,
    { transactionId: "ctx-1", replayed: false },
    { id: "claim-1", revision: 1 },
    { items: [{ id: "claim-1" }], continuationToken: "next" },
    { items: [{ id: "claim-2" }], continuationToken: null },
    [{ revision: 1 }],
    [{ event: { id: "evt-1" }, leaseToken: "opaque-token" }],
    { items: [{ id: "evt-1" }], continuationToken: null },
    { expiresAtUtc: "2026-07-12T00:00:00Z" },
    null,
    null,
    null,
    { tenantId: "tenant-a", contentHash: "sha256:snapshot" },
    null
  ];
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({
        method: init.method ?? "GET",
        url,
        body: init.body ? JSON.parse(init.body) : null
      });
      const response = responses.shift();
      return new Response(response === null ? "" : JSON.stringify(response), { status: 200 });
    }
  });

  assert.deepEqual(await client.listCanonicalMigrations(), []);
  assert.equal(await client.applyCanonicalMigrations([{ namespace: "client-test", id: "m1", checksum: "sha256:1" }]), null);
  assert.equal((await client.commitCanonicalTransaction("tenant-a", { tenantId: "tenant-a", idempotencyKey: "claim-1" })).transactionId, "ctx-1");
  assert.equal((await client.getCanonicalDocument("tenant-a", "claim/type", "claim/1", { includeDeleted: true })).id, "claim-1");
  assert.deepEqual((await client.queryAllCanonicalDocuments("tenant-a", { tenantId: "tenant-a", limit: 1 })).map(item => item.id), ["claim-1", "claim-2"]);
  assert.equal((await client.listCanonicalDocumentRevisions("tenant-a", "claim/type", "claim/1", { limit: 2 }))[0].revision, 1);
  assert.equal((await client.leaseCanonicalOutbox("tenant-a", { tenantId: "tenant-a", consumerId: "projector" }))[0].event.id, "evt-1");
  assert.equal((await client.queryCanonicalOutbox("tenant-a", { tenantId: "tenant-a", state: "leased" })).items[0].id, "evt-1");
  assert.equal((await client.renewCanonicalOutboxLease("tenant-a", "evt-1", { tenantId: "tenant-a", eventId: "evt-1", leaseToken: "opaque-token" })).expiresAtUtc, "2026-07-12T00:00:00Z");
  assert.equal(await client.acknowledgeCanonicalOutbox("tenant-a", "evt-1", "opaque-token"), null);
  assert.equal(await client.releaseCanonicalOutbox("tenant-a", "evt-1", { tenantId: "tenant-a", eventId: "evt-1", leaseToken: "opaque-token" }), null);
  assert.equal(await client.replayCanonicalOutbox("tenant-a", "evt-1", { tenantId: "tenant-a", eventId: "evt-1" }), null);
  const snapshot = await client.exportCanonicalTenant("tenant-a");
  assert.equal(snapshot.contentHash, "sha256:snapshot");
  assert.equal(await client.restoreCanonicalTenant("tenant-a", snapshot, snapshot.contentHash), null);

  assert.deepEqual(requests.map(request => ({ method: request.method, url: request.url })), [
    { method: "GET", url: "http://vyral.local/canonical/migrations" },
    { method: "POST", url: "http://vyral.local/canonical/migrations" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/transactions" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/documents/read" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/documents/query" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/documents/query" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/documents/revisions" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/outbox/leases" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/outbox/query" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/outbox/evt-1/renew" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/outbox/evt-1/ack" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/outbox/evt-1/nack" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/outbox/evt-1/replay" },
    { method: "GET", url: "http://vyral.local/canonical/tenants/tenant-a/export" },
    { method: "POST", url: "http://vyral.local/canonical/tenants/tenant-a/restore" }
  ]);
  assert.deepEqual(requests[3].body, { tenantId: "tenant-a", documentType: "claim/type", id: "claim/1", includeDeleted: true });
  assert.deepEqual(requests[6].body, { tenantId: "tenant-a", documentType: "claim/type", id: "claim/1", limit: 2 });
  assert.deepEqual(requests[10].body, { leaseToken: "opaque-token" });
  assert.deepEqual(requests[14].body, { snapshot, expectedContentHash: "sha256:snapshot" });
});

test("execution runtime helpers use execution routes", async () => {
  const responses = [
    { adapter: { adapterId: "local-sqlite" }, plugins: [], handlers: [] },
    { adapterId: "local-sqlite", runtimeKind: "local.sqlite", rowCounts: { runs: 2 } },
    { dryRun: true, retainTerminalRuns: 1, runs: 1, runIds: ["run-old"] },
    { id: "run-1", status: "queued" },
    [{ id: "run-1", status: "running" }],
    { id: "run-1", status: "running" },
    [{ type: "run.created" }],
    [{ id: "artifact-1", name: "summary" }],
    { id: "artifact-1", name: "summary", content: "done" },
    { runId: "run-1", key: "cursor", value: { offset: 2 } },
    { id: "run-1", status: "cancelled" }
  ];
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({ method: init.method, url });
      return new Response(JSON.stringify(responses.shift()), { status: 200 });
    }
  });

  const runtime = await client.getExecutionRuntime();
  const maintenance = await client.getExecutionRuntimeMaintenance();
  const prune = await client.pruneExecutionRuntimeMaintenance({ retainTerminalRuns: 1 });
  const started = await client.startExecutionRun({
    handlerId: "jobs.rag",
    payload: { collection: "chunks" },
    correlationId: "corr-1"
  });
  const runs = await client.listExecutionRuns({
    handlerId: "jobs.rag",
    pluginId: "plugin.rag",
    status: "running",
    correlationId: "corr-1",
    idempotencyKey: "idem-1",
    tags: { projectId: "project-a" },
    limit: 5,
    includeResult: true
  });
  const run = await client.getExecutionRun("run-1", { includeResult: false });
  const history = await client.getExecutionRunHistory("run-1", { limit: 2 });
  const artifacts = await client.listExecutionRunArtifacts("run-1");
  const artifact = await client.getExecutionRunArtifact("run-1", "summary");
  const checkpoint = await client.getExecutionRunCheckpoint("run-1", "cursor");
  const cancelled = await client.cancelExecutionRun("run-1");

  assert.equal(runtime.adapter.adapterId, "local-sqlite");
  assert.equal(maintenance.runtimeKind, "local.sqlite");
  assert.equal(prune.dryRun, true);
  assert.equal(started.status, "queued");
  assert.equal(runs[0].id, "run-1");
  assert.equal(run.status, "running");
  assert.equal(history[0].type, "run.created");
  assert.equal(artifacts[0].id, "artifact-1");
  assert.equal(artifact.content, "done");
  assert.equal(checkpoint.value.offset, 2);
  assert.equal(cancelled.status, "cancelled");
  assert.deepEqual(requests, [
    { method: "GET", url: "http://vyral.local/execution/runtime" },
    { method: "GET", url: "http://vyral.local/execution/runtime/maintenance" },
    { method: "POST", url: "http://vyral.local/execution/runtime/maintenance/prune" },
    { method: "POST", url: "http://vyral.local/execution/runs" },
    {
      method: "GET",
      url: "http://vyral.local/execution/runs?handlerId=jobs.rag&pluginId=plugin.rag&status=running&correlationId=corr-1&idempotencyKey=idem-1&tag.projectId=project-a&limit=5&includeResult=true"
    },
    { method: "GET", url: "http://vyral.local/execution/runs/run-1?includeResult=false" },
    { method: "GET", url: "http://vyral.local/execution/runs/run-1/history?limit=2" },
    { method: "GET", url: "http://vyral.local/execution/runs/run-1/artifacts" },
    { method: "GET", url: "http://vyral.local/execution/runs/run-1/artifacts/summary" },
    { method: "GET", url: "http://vyral.local/execution/runs/run-1/checkpoints/cursor" },
    { method: "DELETE", url: "http://vyral.local/execution/runs/run-1" }
  ]);
});

test("waitExecutionRun polls until terminal status", async () => {
  const responses = [
    { id: "run-1", status: "running" },
    { id: "run-1", status: "succeeded", result: { ok: true } }
  ];
  const urls = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url) => {
      urls.push(url);
      return new Response(JSON.stringify(responses.shift()), { status: 200 });
    }
  });

  const run = await client.waitExecutionRun("run-1", { timeoutMs: 1000, pollIntervalMs: 0 });

  assert.equal(isExecutionRunTerminal(run), true);
  assert.equal(TERMINAL_EXECUTION_RUN_STATUSES.includes("succeeded"), true);
  assert.deepEqual(run.result, { ok: true });
  assert.deepEqual(urls, [
    "http://vyral.local/execution/runs/run-1?includeResult=true",
    "http://vyral.local/execution/runs/run-1?includeResult=true"
  ]);
});

test("external execution worker helpers cover the portable protocol", async () => {
  const responses = [
    { dryRun: false, dispatched: 1 },
    { leaseKey: "lease-1", leaseToken: "token-1", workerId: "worker-a", run: { id: "run-1" } },
    { leaseKey: "lease-1", leaseToken: "token-1", workerId: "worker-a", run: { id: "run-1" } },
    { id: "run-1", status: "running" },
    null,
    { id: "artifact-1" },
    { runId: "run-1", key: "cursor" },
    { runId: "run-1", key: "cursor" },
    { run: { id: "run-1" }, suspended: true },
    { id: "run-1", status: "succeeded" }
  ];
  const urls = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url) => {
      urls.push(url);
      const response = responses.shift();
      return new Response(response === null ? "" : JSON.stringify(response), { status: 200 });
    }
  });

  const leaseRequest = { leaseKey: "lease-1", leaseToken: "token-1", workerId: "worker-a" };
  assert.equal((await client.reconcileExecutionRuntimeDispatch({ limit: 10 })).dispatched, 1);
  assert.equal((await client.leaseExternalExecutionRun({ workerId: "worker-a", handlerIds: ["handler-a"] })).leaseKey, "lease-1");
  await client.heartbeatExternalExecutionLease({ ...leaseRequest, ttlSeconds: 30 });
  await client.reportExternalExecutionLease({ ...leaseRequest, update: { progress: 0.5 } });
  assert.equal(await client.recordExternalExecutionLeaseEvent({ ...leaseRequest, type: "log", severity: "info" }), null);
  await client.putExternalExecutionLeaseArtifact({ ...leaseRequest, artifact: { name: "summary", content: {} } });
  await client.putExternalExecutionLeaseCheckpoint({ ...leaseRequest, checkpoint: { key: "cursor", content: {} } });
  assert.equal((await client.getExternalExecutionLeaseCheckpoint({ ...leaseRequest, key: "cursor" })).key, "cursor");
  await client.waitExternalExecutionLease({ ...leaseRequest, kind: "external_event", name: "approval" });
  await client.completeExternalExecutionLease({ ...leaseRequest, result: { status: "succeeded" } });

  assert.deepEqual(urls, [
    "http://vyral.local/execution/runtime/maintenance/reconcile",
    "http://vyral.local/execution/workers/leases",
    "http://vyral.local/execution/workers/leases/heartbeat",
    "http://vyral.local/execution/workers/leases/reports",
    "http://vyral.local/execution/workers/leases/events",
    "http://vyral.local/execution/workers/leases/artifacts",
    "http://vyral.local/execution/workers/leases/checkpoints",
    "http://vyral.local/execution/workers/leases/checkpoints/read",
    "http://vyral.local/execution/workers/leases/wait",
    "http://vyral.local/execution/workers/leases/complete"
  ]);
});

test("collection snapshot export and import helpers use typed routes", async () => {
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({
        method: init.method ?? "GET",
        url,
        body: init.body ? JSON.parse(init.body) : null
      });
      if ((init.method ?? "GET") === "POST" && url.endsWith("/import")) {
        return new Response(JSON.stringify({ collection: "chunks-copy", records: { succeeded: 2 } }), { status: 200 });
      }
      if ((init.method ?? "GET") === "POST") {
        return new Response(JSON.stringify({
          collection: "chunks",
          policy: { name: "chunks" },
          records: [{ id: "a" }],
          recordCount: 1,
          maxRecords: 1,
          truncated: true,
          continuationToken: "next",
          contentHash: "sha256:bounded"
        }), { status: 200 });
      }
      return new Response(JSON.stringify({
        collection: "chunks",
        policy: { name: "chunks" },
        records: [{ id: "a" }, { id: "b" }],
        recordCount: 2,
        contentHash: "sha256:abc"
      }), { status: 200 });
    }
  });

  const envelope = await client.exportCollection("chunks");
  const bounded = await client.exportCollection("chunks", {
    query: { limit: 10 },
    maxRecords: 1,
    failOnLimitExceeded: false
  });
  const imported = await client.importCollection("chunks-copy", envelope, {
    expectedContentHash: "sha256:abc",
    allowCollectionRename: true,
    allowPartialSnapshot: true
  });

  assert.equal(envelope.collection, "chunks");
  assert.deepEqual(envelope.records.map((record) => record.id), ["a", "b"]);
  assert.equal(envelope.recordCount, 2);
  assert.equal(bounded.truncated, true);
  assert.equal(imported.collection, "chunks-copy");
  assert.deepEqual(requests.map((request) => [request.method, request.url]), [
    ["GET", "http://vyral.local/collections/chunks/export"],
    ["POST", "http://vyral.local/collections/chunks/export"],
    ["POST", "http://vyral.local/collections/chunks-copy/import"]
  ]);
  assert.deepEqual(requests[1].body.query, { limit: 10 });
  assert.equal(requests[1].body.maxRecords, 1);
  assert.equal(requests[1].body.failOnLimitExceeded, false);
  assert.equal(requests[2].body.expectedContentHash, "sha256:abc");
  assert.equal(requests[2].body.allowCollectionRename, true);
  assert.equal(requests[2].body.allowPartialSnapshot, true);
});

test("embedTexts posts embedding request", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({
        provider: "deterministic-hash",
        modelId: "deterministic-hash-embedding-v1",
        dimensions: 2,
        items: [
          { index: 0, textLength: 5, values: [1, 0] },
          { index: 1, textLength: 4, values: [0, 1] }
        ]
      }), { status: 200 });
    }
  });

  const response = await client.embedTexts(["alpha", "beta"]);

  assert.deepEqual(request, {
    url: "http://vyral.local/embeddings",
    body: { texts: ["alpha", "beta"] }
  });
  assert.deepEqual(response.items[0].values, [1, 0]);
});

test("embedTexts posts embedding purpose options", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({
        provider: "deterministic-hash",
        modelId: "deterministic-hash-embedding-v1",
        dimensions: 2,
        purpose: "query",
        items: [
          { index: 0, textLength: 5, preparedTextLength: 12, prefixApplied: true, prefixLength: 7, values: [1, 0] }
        ]
      }), { status: 200 });
    }
  });

  const response = await client.embedTexts(["alpha"], {
    purpose: "query",
    queryPrefix: "query: "
  });

  assert.deepEqual(request, {
    url: "http://vyral.local/embeddings",
    body: { texts: ["alpha"], purpose: "query", queryPrefix: "query: " }
  });
  assert.equal(response.items[0].prefixApplied, true);
});

test("embedText returns first embedding values", async () => {
  const client = new VyralClient("http://vyral.local", {
    fetch: async () => new Response(JSON.stringify({
      provider: "deterministic-hash",
      modelId: "deterministic-hash-embedding-v1",
      dimensions: 2,
      items: [
        { index: 0, textLength: 5, values: [0.25, 0.75] }
      ]
    }), { status: 200 })
  });

  assert.deepEqual(await client.embedText("alpha"), [0.25, 0.75]);
});

test("embedding job helpers use job routes", async () => {
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({
        url,
        method: init.method,
        body: init.body ? JSON.parse(init.body) : undefined
      });
      if (init.method === "GET" && url.endsWith("/embeddings/jobs/embed-1")) {
        return new Response(JSON.stringify({ id: "embed-1", status: "succeeded", progress: 1 }), { status: 200 });
      }
      if (init.method === "GET") {
        return new Response(JSON.stringify([{ id: "embed-1", status: "running" }]), { status: 200 });
      }
      return new Response(JSON.stringify({ id: "embed-1", status: "queued", progress: 0 }), { status: 200 });
    }
  });

  const started = await client.startEmbeddingJob({ texts: ["alpha", "beta"], purpose: "passage" });
  const listed = await client.listEmbeddingJobs({ limit: 5, includeResult: true });
  const fetched = await client.getEmbeddingJob("embed-1");
  const cancelled = await client.cancelEmbeddingJob("embed-1");

  assert.equal(started.id, "embed-1");
  assert.equal(listed[0].id, "embed-1");
  assert.equal(fetched.status, "succeeded");
  assert.equal(cancelled.status, "queued");
  assert.deepEqual(requests, [
    {
      url: "http://vyral.local/embeddings/jobs",
      method: "POST",
      body: { texts: ["alpha", "beta"], purpose: "passage" }
    },
    {
      url: "http://vyral.local/embeddings/jobs?limit=5&includeResult=true",
      method: "GET",
      body: undefined
    },
    {
      url: "http://vyral.local/embeddings/jobs/embed-1",
      method: "GET",
      body: undefined
    },
    {
      url: "http://vyral.local/embeddings/jobs/embed-1",
      method: "DELETE",
      body: undefined
    }
  ]);
});

test("waitEmbeddingJob polls until terminal status", async () => {
  const responses = [
    { id: "embed-1", status: "running" },
    { id: "embed-1", status: "succeeded" }
  ];
  const client = new VyralClient("http://vyral.local", {
    fetch: async () => new Response(JSON.stringify(responses.shift()), { status: 200 })
  });

  const result = await client.waitEmbeddingJob("embed-1", { timeoutMs: 1000, pollIntervalMs: 0 });

  assert.equal(result.status, "succeeded");
  assert.deepEqual(responses, []);
});

test("buildRagCollectionPolicy uses Vyral RAG defaults", () => {
  assert.deepEqual(buildRagCollectionPolicy("chunks", { dimensions: 384 }), {
    name: "chunks",
    partitionKeyPath: "/partitionKey",
    indexedMetadata: [
      "/metadata/documentId",
      "/metadata/topic",
      "/metadata/status",
      "/type"
    ],
    vectorPolicies: [
      {
        name: "contentEmbedding",
        path: "/vectors/contentEmbedding/values",
        dimensions: 384,
        datatype: "float32",
        distanceFunction: "cosine",
        indexType: "flat"
      }
    ]
  });
});

test("buildRagCollectionPolicy rejects invalid dimensions", () => {
  assert.throws(
    () => buildRagCollectionPolicy("chunks", { dimensions: 0 }),
    /positive integer/
  );
});

test("buildVerifiedRetrievalRequest uses lexical defaults", () => {
  const request = buildVerifiedRetrievalRequest("RECORD-000123 update deadline", ["pages"], {
    partitionKeys: ["source-a"],
    filter: { path: "/metadata/status", op: "eq", value: "active" },
    fieldBoosts: { "/metadata/referenceId": 4.0 }
  });

  assert.equal(request.searchMode, "lexical");
  assert.deepEqual(request.collections, ["pages"]);
  assert.deepEqual(request.partitionKeys, ["source-a"]);
  assert.deepEqual(request.filter, { path: "/metadata/status", op: "eq", value: "active" });
  assert.equal(request.limit, 8);
  assert.equal(request.includeTrace, true);
  assert.ok(request.lexical.fields.includes("/content/text"));
  assert.ok(request.lexical.fields.includes("/metadata/referenceId"));
  assert.equal(request.lexical.fieldBoosts["/metadata/referenceId"], 4.0);
  assert.equal(request.lexical.scoring, "bm25");
  assert.equal(request.lexical.prefixMatching, true);
});

test("buildVerifiedRetrievalRequest rejects empty collections", () => {
  assert.throws(
    () => buildVerifiedRetrievalRequest("query", []),
    /collections/
  );
});

test("buildRetrievalProfileRequest carries profile and overrides", () => {
  const request = buildRetrievalProfileRequest("rerankPolish", "retention policy", ["chunks"], {
    partitionKeys: ["tenant-a"],
    filter: { path: "/metadata/status", op: "eq", value: "active" },
    rerank: { enabled: true, candidateLimit: 8 },
    limit: 6,
    includeTrace: true
  });

  assert.deepEqual(request, {
    profile: "rerankPolish",
    query: "retention policy",
    collections: ["chunks"],
    rerank: { enabled: true, candidateLimit: 8 },
    limit: 6,
    includeTrace: true,
    partitionKeys: ["tenant-a"],
    filter: { path: "/metadata/status", op: "eq", value: "active" }
  });
});

test("retrieval evaluation builders shape cases and comparisons", () => {
  const retrieval = buildVerifiedRetrievalRequest("retention hold", ["pages"], {
    partitionKeys: ["source-a"],
    limit: 3
  });
  const expected = buildRetrievalEvaluationExpectedMatch("page-1", {
    partitionKey: "source-a",
    collection: "pages",
    aliases: ["RECORD-000001"],
    relevance: 2
  });
  const hardNegative = buildRetrievalEvaluationHardNegative({ id: "page-2", sourceIds: ["RECORD-000002"] }, {
    reason: "adjacent page"
  });
  const evaluationCase = buildRetrievalEvaluationCase("retention", retrieval, expected, {
    hardNegatives: [hardNegative],
    k: 8,
    metadata: { fixture: "example" }
  });
  const evaluationRequest = buildRetrievalEvaluationRequest([evaluationCase], {
    defaultK: 8,
    includeTopResults: false
  });
  const evidence = buildRetrievalEvaluationVariant("evidence", { profile: "evidence", includeTrace: true });
  const rerank = buildRetrievalEvaluationVariant("rerank", {
    profile: "rerankPolish",
    rerank: buildRerankOptions({ provider: "local-token-overlap-reranker", candidateLimit: 8 })
  });
  const comparison = buildRetrievalEvaluationComparisonRequest([evaluationCase], [evidence, rerank], {
    includeTopResults: true,
    includeCaseResults: true
  });

  assert.deepEqual(evaluationCase, {
    name: "retention",
    request: retrieval,
    expected: [{
      id: "page-1",
      partitionKey: "source-a",
      collection: "pages",
      aliases: ["RECORD-000001"],
      relevance: 2
    }],
    hardNegatives: [{
      id: "page-2",
      sourceIds: ["RECORD-000002"],
      reason: "adjacent page"
    }],
    k: 8,
    metadata: { fixture: "example" }
  });
  assert.deepEqual(evaluationRequest, {
    cases: [evaluationCase],
    continueOnError: true,
    includeTopResults: false,
    defaultK: 8
  });
  assert.deepEqual(comparison, {
    cases: [evaluationCase],
    variants: [evidence, rerank],
    continueOnError: true,
    includeTopResults: true,
    includeCaseResults: true
  });
});

test("RAG request builders use safe defaults", () => {
  const ingestion = buildRagTextIngestionRequest("retention policy", "tenant-a", {
    documentId: "doc-1",
    metadata: { topic: "retention" },
    chunkChars: 1200,
    skipUnchangedChunks: true,
    persistManifest: true
  });
  assert.equal(ingestion.partitionKey, "tenant-a");
  assert.equal(ingestion.documentId, "doc-1");
  assert.deepEqual(ingestion.metadata, { topic: "retention" });
  assert.equal(ingestion.options.chunkChars, 1200);
  assert.equal(ingestion.options.skipUnchangedChunks, true);
  assert.equal(ingestion.options.persistManifest, true);

  const rerank = buildRerankOptions({ provider: "onnx-cross-encoder-reranker", candidateLimit: 8 });
  const context = buildRagContextRequest("retention policy", ["chunks"], {
    partitionKeys: ["tenant-a"],
    rerank
  });
  assert.equal(context.retrieval.searchMode, "lexical");
  assert.deepEqual(context.retrieval.collections, ["chunks"]);
  assert.deepEqual(context.retrieval.partitionKeys, ["tenant-a"]);
  assert.ok(context.retrieval.lexical.fields.includes("/content/text"));
  assert.equal(context.retrieval.rerank.provider, "onnx-cross-encoder-reranker");
  assert.equal(context.includeContextText, true);

  const profiled = buildRagContextRequest("retention policy", ["chunks"], {
    profile: "discovery",
    embedding: { field: "contentEmbedding", purpose: "query" },
    maxCitationsPerChunk: 2
  });
  assert.equal(profiled.retrieval.profile, "discovery");
  assert.equal(profiled.retrieval.searchMode, undefined);
  assert.equal(profiled.retrieval.embedding.field, "contentEmbedding");
  assert.equal(profiled.maxCitationsPerChunk, 2);

  const graphExpansion = buildGraphExpansionOptions("graphs", {
    graphId: "source-graph",
    seedNodeIds: ["passage:introduction"],
    profile: { maxDepth: 1, direction: "outgoing" },
    maxGraphContextChars: 800,
    maxGraphProvenanceItems: 8
  });
  const graphrag = buildRagContextRequest("grace", ["chunks"], {
    graphExpansion
  });
  assert.equal(graphrag.graphExpansion.collection, "graphs");
  assert.equal(graphrag.graphExpansion.graphId, "source-graph");
  assert.deepEqual(graphrag.graphExpansion.seedNodeIds, ["passage:introduction"]);
  assert.equal(graphrag.graphExpansion.maxGraphContextChars, 800);
  assert.equal(graphrag.graphExpansion.includeGraphProvenance, true);
  assert.equal(graphrag.graphExpansion.maxGraphProvenanceItems, 8);
});

test("graph request builders validate shapes", () => {
  const span = buildGraphSourceSpan("record:chunk-1", { charStart: 0, charEnd: 12 });
  const node = buildGraphNode("chunk:1", "chunk", { label: "Chunk 1", sourceSpans: [span] });
  const edge = buildGraphEdge("edge:1", "chunk:1", "topic:retention", "mentions", { sourceSpans: [span] });
  const assertion = buildGraphAssertion("assertion:1", "edge:1", { subjectKind: "edge", status: "accepted" });
  const review = buildGraphReview("review:1", "assertion:1", "accepted", "tester");
  const envelope = buildGraphEnvelope(buildGraphScope("g", {
    namespace: "tests",
    collection: "chunks",
    tenantId: "tenant-a"
  }), {
    nodes: [node],
    edges: [edge],
    assertions: [assertion],
    reviews: [review]
  });
  const stamped = stampGraphNodeMetadata({ id: "chunk-1", metadata: { topic: "retention" } }, "chunk:1");

  assert.equal(envelope.scope.graphId, "g");
  assert.equal(envelope.nodes[0].sourceSpans[0].sourceRef, "record:chunk-1");
  assert.equal(envelope.assertions[0].subjectId, "edge:1");
  assert.equal(stamped.metadata.graphNodeId, "chunk:1");
  assert.equal(stamped.metadata.topic, "retention");

  assert.deepEqual(buildGraphTraversalRequest(["node:a"], {
    graphId: "g",
    profile: { maxDepth: 2 },
    maxRecords: 100
  }), {
    startNodeIds: ["node:a"],
    profile: { maxDepth: 2 },
    allowPartialGraph: false,
    graphId: "g",
    maxRecords: 100
  });

  const inspection = buildGraphInspectionRequest({
    graphId: "g",
    includeAnomalies: false,
    anomalyLimit: 0
  });
  assert.equal(inspection.includeAnomalies, false);
  assert.equal(inspection.anomalyLimit, 0);
  const doctor = buildGraphDoctorRequest({
    graphId: "g",
    targetCollection: "chunks",
    targetPartitionKeys: ["tenant-a"],
    seedJsonPointers: ["/metadata/graphNodeId"],
    maxTargetRecords: 25
  });
  assert.equal(doctor.targetCollection, "chunks");
  assert.deepEqual(doctor.targetPartitionKeys, ["tenant-a"]);
  assert.deepEqual(doctor.seedJsonPointers, ["/metadata/graphNodeId"]);
  assert.equal(doctor.maxTargetRecords, 25);
  assert.throws(() => buildGraphTraversalRequest([]), /startNodeIds/);
  assert.throws(() => buildGraphInspectionRequest({ anomalyLimit: -1 }), /anomalyLimit/);
});

test("provider request builders expose typed AI payloads", () => {
  const chat = buildProviderChatRequest([
    { role: "user", content: "Summarize." }
  ], {
    modelId: "gpt-5.3-codex-spark",
    timeoutSeconds: 30
  });
  assert.equal(chat.capability, "ai.chat");
  assert.equal(chat.modelId, "gpt-5.3-codex-spark");
  assert.equal(chat.payload.messages[0].content, "Summarize.");

  const extract = buildProviderExtractRequest("OEM manual text", {
    schema: { type: "object" },
    instructions: "Return product bullets.",
    provider: "codex-cli",
    maxOutputBytes: 4096
  });
  assert.equal(extract.capability, "ai.extract");
  assert.equal(extract.provider, "codex-cli");
  assert.deepEqual(extract.payload.schema, { type: "object" });
  assert.equal(extract.maxOutputBytes, 4096);

  const rerank = buildProviderRerankRequest("retention policy", [
    { id: "a", text: "travel" },
    { id: "b", text: "retention policy" }
  ], { limit: 1 });
  assert.equal(rerank.capability, "ai.rerank");
  assert.equal(rerank.payload.limit, 1);

  const review = buildProviderReviewRequest({
    prompt: "Review this copy.",
    references: [{ id: "record:1", kind: "record" }],
    maxFindings: 2
  });
  assert.equal(review.capability, "ai.review");
  assert.equal(review.payload.maxFindings, 2);

  const scaffold = buildProviderScaffoldRequest("Propose artifacts.", {
    allowedPaths: ["docs/example.md"],
    maxArtifacts: 1
  });
  assert.equal(scaffold.capability, "ai.scaffold");
  assert.deepEqual(scaffold.payload.allowedPaths, ["docs/example.md"]);

  const toolPlan = buildProviderToolPlanRequest("Should I call search?", {
    tools: [{ name: "search", description: "Search local records." }]
  });
  assert.equal(toolPlan.capability, "ai.toolPlan");
  assert.equal(toolPlan.payload.tools[0].name, "search");
});

test("provider result helpers treat rejections as unusable", () => {
  const rejected = {
    status: "Rejected",
    output: {
      data: { draftCopy: "parsed but not usable" },
      rejection: {
        source: "provider_policy",
        parsedOutputDisposition: "quarantine_for_operator_review",
        contentUsable: false
      }
    }
  };
  const succeeded = { status: "Succeeded", output: { data: { draftCopy: "usable" } } };

  assert.equal(isProviderRunSucceeded(rejected), false);
  assert.equal(isProviderRunOutputUsable(rejected), false);
  assert.equal(getProviderRunRejection(rejected).source, "provider_policy");
  assert.equal(isProviderRunSucceeded(succeeded), true);
  assert.equal(isProviderRunOutputUsable(succeeded), true);
  assert.equal(getProviderRunRejection(succeeded), null);
});

test("createRagCollection discovers embedding dimensions", async () => {
  const responses = [
    { embedding: { dimensions: 384 } },
    { name: "chunks" }
  ];
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({
        url,
        method: init.method,
        body: init.body ? JSON.parse(init.body) : null
      });
      return new Response(JSON.stringify(responses.shift()), { status: 200 });
    }
  });

  const result = await client.createRagCollection("chunks");

  assert.equal(result.name, "chunks");
  assert.equal(requests[0].url, "http://vyral.local/health");
  assert.equal(requests[1].url, "http://vyral.local/collections");
  assert.equal(requests[1].body.vectorPolicies[0].dimensions, 384);
  assert.equal(requests[1].body.vectorPolicies[0].path, "/vectors/contentEmbedding/values");
});

test("graph helpers use graph routes", async () => {
  const responses = [
    [{ providerId: "vyral-collection" }],
    { providerId: "vyral-collection", kind: "vyral_collection" },
    { collection: "graphs", recordCount: 2 },
    { collection: "graphs", readyToImport: true },
    { collection: "graphs", envelope: { scope: { graphId: "g" } } },
    { collection: "graphs", graphId: "g", nodeCount: 1 },
    { collection: "graphs", graphId: "g", traversalReady: true },
    { collection: "graphs", status: "ready", ready: true }
  ];
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({
        method: init.method ?? "GET",
        url,
        body: init.body ? JSON.parse(init.body) : null
      });
      return new Response(JSON.stringify(responses.shift()), { status: 200 });
    }
  });

  const shapes = await client.listGraphProviderShapes();
  const shape = await client.getGraphProviderShape("vyral-collection");
  const imported = await client.importGraphEnvelope("graph/name", {
    scope: { graphId: "g" },
    nodes: []
  }, {
    replaceExisting: true
  });
  const preflight = await client.preflightGraphImport("graph/name", {
    scope: { graphId: "g" },
    nodes: []
  });
  const exported = await client.exportGraphEnvelope("graph/name", {
    graphId: "g",
    includeProjections: false
  });
  const traversed = await client.traverseGraph("graph/name", ["node:a"], {
    graphId: "g",
    profile: { maxDepth: 1 }
  });
  const inspected = await client.inspectGraph("graph/name", {
    graphId: "g",
    includeAnomalies: false,
    anomalyLimit: 5
  });
  const doctor = await client.doctorGraph("graph/name", {
    graphId: "g",
    targetCollection: "chunks",
    targetPartitionKeys: ["tenant-a"]
  });

  assert.equal(shapes[0].providerId, "vyral-collection");
  assert.equal(shape.kind, "vyral_collection");
  assert.equal(imported.recordCount, 2);
  assert.equal(preflight.readyToImport, true);
  assert.equal(exported.envelope.scope.graphId, "g");
  assert.equal(traversed.nodeCount, 1);
  assert.equal(inspected.traversalReady, true);
  assert.equal(doctor.ready, true);
  assert.deepEqual(requests, [
    { method: "GET", url: "http://vyral.local/graph/provider-shapes", body: null },
    { method: "GET", url: "http://vyral.local/graph/provider-shapes/vyral-collection", body: null },
    {
      method: "POST",
      url: "http://vyral.local/collections/graph%2Fname/graph/import",
      body: {
        envelope: { scope: { graphId: "g" }, nodes: [] },
        createCollectionIfMissing: true,
        replaceExisting: true,
        continueOnError: false,
        allowNonGraphPolicy: false
      }
    },
    {
      method: "POST",
      url: "http://vyral.local/collections/graph%2Fname/graph/import/preflight",
      body: {
        envelope: { scope: { graphId: "g" }, nodes: [] },
        createCollectionIfMissing: true,
        replaceExisting: false,
        continueOnError: false,
        allowNonGraphPolicy: false
      }
    },
    {
      method: "POST",
      url: "http://vyral.local/collections/graph%2Fname/graph/export",
      body: {
        includeProjections: false,
        failOnLimitExceeded: true,
        graphId: "g"
      }
    },
    {
      method: "POST",
      url: "http://vyral.local/collections/graph%2Fname/graph/traverse",
      body: {
        startNodeIds: ["node:a"],
        profile: { maxDepth: 1 },
        allowPartialGraph: false,
        graphId: "g"
      }
    },
    {
      method: "POST",
      url: "http://vyral.local/collections/graph%2Fname/graph/inspect",
      body: {
        allowPartialGraph: false,
        includeAnomalies: false,
        anomalyLimit: 5,
        graphId: "g"
      }
    },
    {
      method: "POST",
      url: "http://vyral.local/collections/graph%2Fname/graph/doctor",
      body: {
        targetPartitionKeys: ["tenant-a"],
        maxTargetRecords: 1000,
        allowPartialGraph: false,
        includeAnomalies: true,
        anomalyLimit: 50,
        graphId: "g",
        targetCollection: "chunks"
      }
    }
  ]);
});

test("RAG ingestion helpers plan commit and compare results", async () => {
  const responses = [
    {
      planHash: "sha256:plan",
      manifestHash: "sha256:manifest",
      chunks: [{ id: "chunk-1", action: "created", embeddingAction: "generated" }],
      staleDeletes: [{ id: "stale-1" }]
    },
    {
      planHash: "sha256:plan",
      manifestHash: "sha256:manifest",
      actionSummary: {
        actionCounts: { created: 1 },
        embeddingActionCounts: { generated: 1 },
        createdIds: ["chunk-1"],
        updatedIds: [],
        reusedIds: [],
        deduplicatedIds: [],
        staleDeleteIds: ["stale-1"]
      }
    }
  ];
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({ url, body: JSON.parse(init.body) });
      return new Response(JSON.stringify(responses.shift()), { status: 200 });
    }
  });

  const planned = await client.planRagTextIngestion("chunks", { partitionKey: "tenant-a", text: "alpha" });
  const committed = await client.commitRagTextIngestion("chunks", { partitionKey: "tenant-a", text: "alpha" }, planned);

  assert.equal(requests[0].body.options.dryRun, true);
  assert.equal(requests[1].body.options.dryRun, false);
  assert.equal(requests[1].body.options.expectedPlanHash, "sha256:plan");
  assert.equal(requests[1].body.options.expectedManifestHash, "sha256:manifest");
  assert.deepEqual(summarizeRagIngestResult(planned).createdIds, ["chunk-1"]);

  const comparison = compareRagIngestResults(planned, committed);
  assert.equal(comparison.planHash.status, "matched");
  assert.equal(comparison.planHash.matches, true);
  assert.deepEqual(comparison.committedSummary.staleDeleteIds, ["stale-1"]);
});

test("inspectCollection sends inspection route with options", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      request = { url, method: init.method };
      return new Response(JSON.stringify({ collection: "chunks", recordCount: 2 }), { status: 200 });
    }
  });

  const result = await client.inspectCollection("chunk/name", {
    includeAnomalies: false,
    anomalyLimit: 10
  });

  assert.deepEqual(request, {
    url: "http://vyral.local/collections/chunk%2Fname/inspect?includeAnomalies=false&anomalyLimit=10",
    method: "GET"
  });
  assert.equal(result.recordCount, 2);
});

test("api key is sent on requests", async () => {
  let key;
  let redirect;
  const client = new VyralClient("https://vyral.local", {
    apiKey: "secret",
    fetch: async (url, init) => {
      key = init.headers.get("X-Vyral-Api-Key");
      redirect = init.redirect;
      return new Response(JSON.stringify({ status: "ok" }), { status: 200 });
    }
  });

  await client.health();

  assert.equal(key, "secret");
  assert.equal(redirect, "manual");
});

test("credentials require HTTPS except on loopback", async () => {
  for (const baseUrl of ["http://vyral.example", "ftp://vyral.example", "https://user:password@vyral.example"]) {
    assert.throws(() => new VyralClient(baseUrl, { apiKey: "secret", fetch: async () => new Response("{}") }));
  }

  assert.doesNotThrow(() => new VyralClient("http://127.0.0.1:5220", {
    bearerToken: "token",
    fetch: async () => new Response("{}")
  }));

  const client = new VyralClient("http://vyral.example", {
    fetch: async () => new Response("{}")
  });
  assert.throws(
    () => client.withOptions({ headers: { Authorization: "Bearer token" } }),
    /credentials require HTTPS/u
  );
});

test("base URL normalization removes trailing slashes without regex backtracking", () => {
  const client = new VyralClient("https://vyral.example/api///", {
    fetch: async () => new Response("{}")
  });

  assert.equal(client.baseUrl, "https://vyral.example/api");
});

test("shared transport options retry only safe or idempotent requests", async () => {
  const calls = [];
  const responses = [
    new Response(JSON.stringify({ title: "Unavailable", status: 503 }), {
      status: 503,
      headers: { "Retry-After": "0" }
    }),
    new Response(JSON.stringify({ status: "ok" })),
    new Error("connection reset"),
    new Response(JSON.stringify({ title: "Unavailable", status: 503 }), {
      status: 503,
      headers: { "Retry-After": "0" }
    }),
    new Response(JSON.stringify({ id: "import-1" }))
  ];
  const fetch = async (url, init) => {
    calls.push({ url, init });
    const response = responses.shift();
    if (response instanceof Error) throw response;
    return response;
  };
  const client = new VyralClient("https://vyral.local", {
    fetch,
    bearerToken: "token",
    headers: { "X-Client": "javascript" },
    maxRetries: 1,
    retryBackoffMs: 0
  }).withOptions({ timeoutMs: 4_000, correlationId: "corr-1", headers: { "X-Scope": "sdk" } });

  assert.equal((await client.health()).status, "ok");
  await assert.rejects(
    () => client.startExecutionRun({ handlerId: "consumer" }),
    (error) => error instanceof VyralClientError && error.isTransient()
  );
  assert.equal((await client.startCollectionImportJob(
    "events",
    { schema: "vyral.collection-snapshot.v1", records: [] },
    { idempotencyKey: "import-1" }
  )).id, "import-1");

  assert.equal(responses.length, 0);
  assert.deepEqual(calls.map((call) => call.init.method), ["GET", "GET", "POST", "POST", "POST"]);
  for (const call of calls) {
    assert.equal(call.init.headers.get("Authorization"), "Bearer token");
    assert.equal(call.init.headers.get("X-Client"), "javascript");
    assert.equal(call.init.headers.get("X-Scope"), "sdk");
    assert.equal(call.init.headers.get("X-Correlation-ID"), "corr-1");
  }
});

test("shared transport cancellation fails before network I/O", async () => {
  let called = false;
  const controller = new AbortController();
  controller.abort();
  const client = new VyralClient("http://vyral.local", {
    signal: controller.signal,
    fetch: async () => {
      called = true;
      return new Response("{}");
    }
  });

  await assert.rejects(
    () => client.health(),
    (error) => error instanceof VyralClientError && error.isCancelled()
  );
  assert.equal(called, false);
});

test("problem json errors expose details and predicates", async () => {
  const problem = {
    type: "https://vyral.local/problems/collection-not-found",
    title: "Collection not found",
    status: 404,
    detail: "Collection 'missing' does not exist.",
    instance: "/collections/missing/query"
  };
  const client = new VyralClient("http://vyral.local", {
    fetch: async () => new Response(JSON.stringify(problem), {
      status: 404,
      headers: {
        "Content-Type": "application/problem+json",
        "Retry-After": "3",
        "X-Correlation-ID": "corr-error"
      }
    })
  });

  await assert.rejects(
    () => client.queryRecords("missing", { limit: 1 }),
    (error) => {
      assert.ok(error instanceof VyralClientError);
      assert.equal(error.status, 404);
      assert.deepEqual(error.problem, problem);
      assert.equal(error.title, "Collection not found");
      assert.equal(error.detail, "Collection 'missing' does not exist.");
      assert.equal(error.instance, "/collections/missing/query");
      assert.equal(error.problemStatus, 404);
      assert.equal(error.retryAfter, "3");
      assert.equal(error.correlationId, "corr-error");
      assert.match(error.message, /Collection not found/);
      assert.equal(error.isMissingCollection(), true);
      assert.equal(error.isAuthError(), false);
      assert.equal(error.isValidationError(), false);
      return true;
    }
  );
});

test("client error helpers classify auth validation and timeout", async () => {
  assert.equal(new VyralClientError(401, '{"title":"Unauthorized"}').isAuthError(), true);
  assert.equal(new VyralClientError(403, '{"title":"Forbidden"}').isAuthError(), true);
  assert.equal(new VyralClientError(400, '{"title":"Invalid request"}').isValidationError(), true);
  assert.equal(new VyralClientError(422, '{"title":"Unprocessable"}').isValidationError(), true);

  const client = new VyralClient("http://vyral.local", {
    fetch: async () => {
      const error = new Error("aborted");
      error.name = "AbortError";
      throw error;
    }
  });

  await assert.rejects(
    () => client.health(),
    (error) => {
      assert.ok(error instanceof VyralClientError);
      assert.equal(error.status, 0);
      assert.equal(error.failureClass, "timeout");
      assert.equal(error.isTimeout(), true);
      return true;
    }
  );
});

test("provider helpers use provider routes", async () => {
  const responses = [
    [{ id: "local-deterministic-ai" }],
    { items: [{ provider: "local-deterministic-ai", capabilities: { "ai.chat": { supported: true } } }] },
    { profile: { id: "local-deterministic-ai" }, capabilities: [] },
    [{ provider: "local-deterministic-ai", status: "warning", checks: [] }],
    { provider: "local-deterministic-ai", status: "warning", checks: [] },
    { provider: "local-deterministic-ai", status: "succeeded", items: [{ id: "local-deterministic-ai", default: true }] },
    { items: [{ provider: "local-deterministic-ai", capability: "ai.chat", ready: false }] },
    { items: [{ provider: "local-deterministic-ai", capability: "ai.chat", ready: false }] },
    [{ provider: "local-deterministic-ai", status: "unsupported", items: [] }],
    { provider: "local-deterministic-ai", status: "unsupported", items: [] },
    [{ provider: "local-deterministic-ai", capability: "ai.chat", status: "unvalidated" }],
    [{ provider: "local-deterministic-ai", capability: "ai.chat", status: "validated" }],
    { status: "Succeeded", provider: "local-deterministic-ai" }
  ];
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({
        url,
        method: init.method,
        body: init.body ? JSON.parse(init.body) : null
      });
      return new Response(JSON.stringify(responses.shift()), { status: 200 });
    }
  });

  const providers = await client.listProviders();
  const matrix = await client.getProviderCapabilityMatrix();
  const provider = await client.getProvider("local-deterministic-ai");
  const doctor = await client.listProviderDoctor();
  const providerDoctor = await client.getProviderDoctor("local-deterministic-ai");
  const models = await client.listProviderModels("local-deterministic-ai");
  const readiness = await client.listProviderReadiness();
  const providerReadiness = await client.getProviderReadiness("local-deterministic-ai");
  const quotas = await client.listProviderQuotas();
  const providerQuota = await client.getProviderQuota("local-deterministic-ai");
  const qualifications = await client.listProviderQualifications("local-deterministic-ai");
  const qualified = await client.qualifyProvider("local-deterministic-ai", { capability: "ai.chat" });
  const result = await client.runProvider("local-deterministic-ai", {
    capability: "ai.chat",
    operation: "run",
    payload: { messages: [{ role: "user", content: "hello" }] }
  });

  assert.equal(providers[0].id, "local-deterministic-ai");
  assert.equal(matrix.items[0].capabilities["ai.chat"].supported, true);
  assert.equal(provider.profile.id, "local-deterministic-ai");
  assert.equal(doctor[0].status, "warning");
  assert.equal(providerDoctor.status, "warning");
  assert.equal(models.status, "succeeded");
  assert.equal(models.items[0].id, "local-deterministic-ai");
  assert.equal(readiness.items[0].ready, false);
  assert.equal(providerReadiness.items[0].ready, false);
  assert.equal(quotas[0].status, "unsupported");
  assert.equal(providerQuota.status, "unsupported");
  assert.equal(qualifications[0].status, "unvalidated");
  assert.equal(qualified[0].status, "validated");
  assert.equal(result.status, "Succeeded");
  assert.deepEqual(requests, [
    { url: "http://vyral.local/providers", method: "GET", body: null },
    { url: "http://vyral.local/providers/capabilities", method: "GET", body: null },
    { url: "http://vyral.local/providers/local-deterministic-ai", method: "GET", body: null },
    { url: "http://vyral.local/providers/doctor", method: "GET", body: null },
    { url: "http://vyral.local/providers/local-deterministic-ai/doctor", method: "GET", body: null },
    { url: "http://vyral.local/providers/local-deterministic-ai/models", method: "GET", body: null },
    { url: "http://vyral.local/providers/readiness", method: "GET", body: null },
    { url: "http://vyral.local/providers/local-deterministic-ai/readiness", method: "GET", body: null },
    { url: "http://vyral.local/providers/quotas", method: "GET", body: null },
    { url: "http://vyral.local/providers/local-deterministic-ai/quota", method: "GET", body: null },
    { url: "http://vyral.local/providers/local-deterministic-ai/qualifications", method: "GET", body: null },
    {
      url: "http://vyral.local/providers/local-deterministic-ai/qualify",
      method: "POST",
      body: { capability: "ai.chat" }
    },
    {
      url: "http://vyral.local/providers/local-deterministic-ai/run",
      method: "POST",
      body: {
        capability: "ai.chat",
        operation: "run",
        payload: { messages: [{ role: "user", content: "hello" }] }
      }
    }
  ]);
});

test("upsertRecords posts batch request", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({
        collection: "chunks",
        requested: 2,
        attempted: 2,
        succeeded: 2,
        failed: 0,
        items: [
          { index: 0, id: "a", partitionKey: "tenant-a", status: "succeeded" },
          { index: 1, id: "b", partitionKey: "tenant-a", status: "succeeded" }
        ]
      }), { status: 200 });
    }
  });

  const response = await client.upsertRecords(
    "chunks",
    [{ id: "a", partitionKey: "tenant-a" }, { id: "b", partitionKey: "tenant-a" }],
    { continueOnError: true }
  );

  assert.deepEqual(request, {
    url: "http://vyral.local/collections/chunks/records/batch",
    body: {
      records: [{ id: "a", partitionKey: "tenant-a" }, { id: "b", partitionKey: "tenant-a" }],
      continueOnError: true
    }
  });
  assert.equal(response.succeeded, 2);
});

test("record import jobs preserve raw request and idempotency key", async () => {
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      requests.push({
        url,
        method: init.method,
        body: init.body ? JSON.parse(init.body) : null,
        idempotencyKey: new Headers(init.headers).get("Idempotency-Key")
      });
      if (url.startsWith("http://vyral.local/collections/chunks/records/batch/jobs")) {
        return new Response(JSON.stringify({ id: "job-1", kind: "batch_upsert", status: "queued" }), { status: 202 });
      }
      if (url.endsWith("/record-import/jobs?limit=3&includeResult=true")) {
        return new Response(JSON.stringify([{ id: "job-1", status: "succeeded" }]), { status: 200 });
      }
      return new Response(JSON.stringify({ id: "job-1", status: "succeeded" }), { status: 200 });
    }
  });

  const started = await client.startRecordBatchUpsertJob(
    "chunks",
    [{ id: "a", partitionKey: "tenant-a" }],
    {
      preconditions: [{ ifNoneMatch: "*" }],
      continueOnError: true,
      idempotencyKey: "record-import-1",
      productId: "product-a",
      tenantId: "tenant-a"
    }
  );
  const listed = await client.listRecordImportJobs({ limit: 3, includeResult: true });
  const completed = await client.getRecordImportJob("job-1");
  const cancelled = await client.cancelRecordImportJob("job-1");

  assert.equal(started.id, "job-1");
  assert.equal(listed[0].status, "succeeded");
  assert.equal(completed.status, "succeeded");
  assert.equal(cancelled.status, "succeeded");
  assert.deepEqual(requests[0], {
    url: "http://vyral.local/collections/chunks/records/batch/jobs?productId=product-a&tenantId=tenant-a",
    method: "POST",
    body: {
      records: [{ id: "a", partitionKey: "tenant-a" }],
      continueOnError: true,
      preconditions: [{ ifNoneMatch: "*" }]
    },
    idempotencyKey: "record-import-1"
  });
  assert.equal(requests[1].url, "http://vyral.local/record-import/jobs?limit=3&includeResult=true");
  assert.equal(requests[2].method, "GET");
  assert.equal(requests[3].method, "DELETE");
});

test("searchAllRecords drains continuation tokens", async () => {
  const responses = [
    { items: [{ record: { id: "a" }, score: 1 }], continuationToken: "next" },
    { items: [{ record: { id: "b" }, score: 0.5 }], continuationToken: null }
  ];

  const client = new VyralClient("http://vyral.local", {
    fetch: async () => new Response(JSON.stringify(responses.shift()), { status: 200 })
  });

  const items = await client.searchAllRecords("chunks", { limit: 1 });

  assert.deepEqual(items.map((item) => item.record.id), ["a", "b"]);
});

test("evaluateRetrieval posts evaluation request", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({
        requested: 1,
        attempted: 1,
        succeeded: 1,
        failed: 0,
        hitCount: 1,
        hitRate: 1,
        cases: [{ index: 0, status: "succeeded", hit: true }]
      }), { status: 200 });
    }
  });

  const result = await client.evaluateRetrieval({
    cases: [
      {
        name: "retention",
        request: { query: "retention", collections: ["chunks"], limit: 3 },
        expected: [{ id: "chunk-1" }]
      }
    ]
  });

  assert.deepEqual(request, {
    url: "http://vyral.local/retrieval/evaluate",
    body: {
      cases: [
        {
          name: "retention",
          request: { query: "retention", collections: ["chunks"], limit: 3 },
          expected: [{ id: "chunk-1" }]
        }
      ]
    }
  });
  assert.equal(result.hitCount, 1);
});

test("compareRetrievalEvaluations posts comparison request", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({
        requested: 1,
        variantsRequested: 2,
        variantsAttempted: 2,
        variantsSucceeded: 2,
        variantsFailed: 0,
        baselineVariantId: "evidence",
        variants: [
          { id: "evidence", status: "succeeded", metrics: { hitRate: 1 } },
          { id: "rerank", status: "succeeded", metrics: { hitRate: 1 }, deltaFromBaseline: { hitRate: 0 } }
        ]
      }), { status: 200 });
    }
  });

  const result = await client.compareRetrievalEvaluations({
    cases: [
      {
        name: "retention",
        request: { query: "retention", collections: ["chunks"], limit: 3 },
        expected: [{ id: "chunk-1" }]
      }
    ],
    variants: [
      { id: "evidence", profile: "evidence" },
      { id: "rerank", profile: "rerankPolish" }
    ]
  });

  assert.deepEqual(request, {
    url: "http://vyral.local/retrieval/evaluate/compare",
    body: {
      cases: [
        {
          name: "retention",
          request: { query: "retention", collections: ["chunks"], limit: 3 },
          expected: [{ id: "chunk-1" }]
        }
      ],
      variants: [
        { id: "evidence", profile: "evidence" },
        { id: "rerank", profile: "rerankPolish" }
      ]
    }
  });
  assert.equal(result.variantsRequested, 2);
});

test("retrieval evaluation job helpers use job routes", async () => {
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({
        url,
        method: init.method,
        body: init.body ? JSON.parse(init.body) : undefined
      });
      if (init.method === "GET" && url.endsWith("/retrieval/evaluate/jobs/eval-1")) {
        return new Response(JSON.stringify({ id: "eval-1", status: "succeeded", progress: 1 }), { status: 200 });
      }
      if (init.method === "GET") {
        return new Response(JSON.stringify([{ id: "eval-1", status: "running" }]), { status: 200 });
      }
      return new Response(JSON.stringify({ id: "eval-1", status: "queued", progress: 0 }), { status: 200 });
    }
  });

  const startedEval = await client.startRetrievalEvaluationJob({ cases: [] });
  const started = await client.startRetrievalEvaluationComparisonJob({ cases: [], variants: [] });
  const listed = await client.listRetrievalEvaluationJobs({ limit: 5, includeResult: true });
  const fetched = await client.getRetrievalEvaluationJob("eval-1");
  const cancelled = await client.cancelRetrievalEvaluationJob("eval-1");

  assert.equal(startedEval.id, "eval-1");
  assert.equal(started.id, "eval-1");
  assert.equal(listed[0].id, "eval-1");
  assert.equal(fetched.status, "succeeded");
  assert.equal(cancelled.status, "queued");
  assert.deepEqual(requests, [
    {
      url: "http://vyral.local/retrieval/evaluate/jobs",
      method: "POST",
      body: { cases: [] }
    },
    {
      url: "http://vyral.local/retrieval/evaluate/compare/jobs",
      method: "POST",
      body: { cases: [], variants: [] }
    },
    {
      url: "http://vyral.local/retrieval/evaluate/jobs?limit=5&includeResult=true",
      method: "GET",
      body: undefined
    },
    {
      url: "http://vyral.local/retrieval/evaluate/jobs/eval-1",
      method: "GET",
      body: undefined
    },
    {
      url: "http://vyral.local/retrieval/evaluate/jobs/eval-1",
      method: "DELETE",
      body: undefined
    }
  ]);
});

test("waitRetrievalEvaluationJob polls until terminal status", async () => {
  const responses = [
    { id: "eval-1", status: "running" },
    { id: "eval-1", status: "succeeded" }
  ];
  const client = new VyralClient("http://vyral.local", {
    fetch: async () => new Response(JSON.stringify(responses.shift()), { status: 200 })
  });

  const result = await client.waitRetrievalEvaluationJob("eval-1", { timeoutMs: 1000, pollIntervalMs: 0 });

  assert.equal(result.status, "succeeded");
  assert.deepEqual(responses, []);
});

test("listRetrievalProfiles gets profile catalog", async () => {
  let requestUrl;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url) => {
      requestUrl = url;
      return new Response(JSON.stringify([{ id: "evidence" }]), { status: 200 });
    }
  });

  const result = await client.listRetrievalProfiles();

  assert.equal(requestUrl, "http://vyral.local/retrieval/profiles");
  assert.equal(result[0].id, "evidence");
});

test("buildRagContext posts context request", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({ chunks: [{ id: "chunk-1" }] }), { status: 200 });
    }
  });

  const result = await client.buildRagContext({ query: "retention", collections: ["chunks"] });

  assert.deepEqual(request, {
    url: "http://vyral.local/rag/context",
    body: { query: "retention", collections: ["chunks"] }
  });
  assert.equal(result.chunks[0].id, "chunk-1");
});

test("evaluateRagContext posts evaluation request", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({
        requested: 1,
        attempted: 1,
        succeeded: 1,
        passedCount: 1,
        passRate: 1,
        cases: [{ index: 0, status: "succeeded", passed: true }]
      }), { status: 200 });
    }
  });

  const result = await client.evaluateRagContext({
    cases: [
      {
        name: "retention",
        request: { retrieval: { query: "retention", collections: ["chunks"] } },
        expectedGraph: { nodeIds: ["node:a"] }
      }
    ]
  });

  assert.deepEqual(request, {
    url: "http://vyral.local/rag/context/evaluate",
    body: {
      cases: [
        {
          name: "retention",
          request: { retrieval: { query: "retention", collections: ["chunks"] } },
          expectedGraph: { nodeIds: ["node:a"] }
        }
      ]
    }
  });
  assert.equal(result.passedCount, 1);
});

test("buildRagPrompt posts prompt request", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({ prompt: "SYSTEM:\n...", promptHash: "sha256:abc" }), { status: 200 });
    }
  });

  const result = await client.buildRagPrompt({
    context: { query: "retention", collections: ["chunks"] },
    template: { failOnEmptyContext: true }
  });

  assert.deepEqual(request, {
    url: "http://vyral.local/rag/prompt",
    body: {
      context: { query: "retention", collections: ["chunks"] },
      template: { failOnEmptyContext: true }
    }
  });
  assert.equal(result.promptHash, "sha256:abc");
});

test("ingestRagText posts collection ingestion request", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({ documentId: "doc-1", chunkCount: 2 }), { status: 200 });
    }
  });

  const result = await client.ingestRagText("chunks", {
    documentId: "doc-1",
    partitionKey: "tenant-a",
    text: "alpha beta gamma"
  });

  assert.deepEqual(request, {
    url: "http://vyral.local/collections/chunks/rag/ingest-text",
    body: {
      documentId: "doc-1",
      partitionKey: "tenant-a",
      text: "alpha beta gamma"
    }
  });
  assert.equal(result.chunkCount, 2);
});

test("ingestRagTexts posts collection batch ingestion request", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({
        collection: "chunks",
        requested: 2,
        attempted: 2,
        succeeded: 2,
        failed: 0,
        items: []
      }), { status: 200 });
    }
  });

  const result = await client.ingestRagTexts(
    "chunks",
    [
      { documentId: "doc-1", partitionKey: "tenant-a", text: "alpha" },
      { documentId: "doc-2", partitionKey: "tenant-a", text: "beta" }
    ],
    { continueOnError: true }
  );

  assert.deepEqual(request, {
    url: "http://vyral.local/collections/chunks/rag/ingest-text/batch",
    body: {
      items: [
        { documentId: "doc-1", partitionKey: "tenant-a", text: "alpha" },
        { documentId: "doc-2", partitionKey: "tenant-a", text: "beta" }
      ],
      continueOnError: true
    }
  });
  assert.equal(result.succeeded, 2);
});

test("pruneTraces posts prune request", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({ matchedCount: 3, deletedCount: 0 }), { status: 200 });
    }
  });

  const result = await client.pruneTraces({
    operation: "provider.run",
    keepLatest: 10,
    dryRun: true
  });

  assert.deepEqual(request, {
    url: "http://vyral.local/traces/prune",
    body: {
      operation: "provider.run",
      keepLatest: 10,
      dryRun: true
    }
  });
  assert.equal(result.matchedCount, 3);
});

test("exportTraces posts export request", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, body: JSON.parse(init.body) };
      return new Response(JSON.stringify({
        formatVersion: "vyral.trace-export.v1",
        traceCount: 1,
        contentHash: "sha256:abc"
      }), { status: 200 });
    }
  });

  const result = await client.exportTraces({
    operation: "provider.run",
    limit: 10,
    failOnUnsafeContent: true
  });

  assert.deepEqual(request, {
    url: "http://vyral.local/traces/export",
    body: {
      operation: "provider.run",
      limit: 10,
      failOnUnsafeContent: true
    }
  });
  assert.equal(result.traceCount, 1);
});

test("summarizeTraces gets trace summary", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, method: init.method };
      return new Response(JSON.stringify({
        totalCount: 1,
        operations: [{ operation: "provider.run", count: 1 }]
      }), { status: 200 });
    }
  });

  const result = await client.summarizeTraces({ operation: "provider.run" });

  assert.deepEqual(request, {
    url: "http://vyral.local/traces/summary?operation=provider.run",
    method: "GET"
  });
  assert.equal(result.totalCount, 1);
});

test("provider job helpers use job routes", async () => {
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({
        url,
        method: init.method,
        body: init.body ? JSON.parse(init.body) : undefined
      });
      if (init.method === "GET" && url.endsWith("/provider-jobs/job-1")) {
        return new Response(JSON.stringify({ id: "job-1", status: "succeeded" }), { status: 200 });
      }
      if (init.method === "GET") {
        return new Response(JSON.stringify([{ id: "job-1" }]), { status: 200 });
      }
      return new Response(JSON.stringify({ id: "job-1", status: "queued" }), { status: 200 });
    }
  });

  const started = await client.startProviderJob("local-deterministic-ai", { capability: "ai.chat" });
  const listed = await client.listProviderJobs({ provider: "local-deterministic-ai", limit: 5, includeResult: true });
  const fetched = await client.getProviderJob("job-1");
  const cancelled = await client.cancelProviderJob("job-1");

  assert.equal(started.id, "job-1");
  assert.equal(listed[0].id, "job-1");
  assert.equal(fetched.status, "succeeded");
  assert.equal(cancelled.status, "queued");
  assert.deepEqual(requests, [
    {
      url: "http://vyral.local/providers/local-deterministic-ai/jobs",
      method: "POST",
      body: { capability: "ai.chat" }
    },
    {
      url: "http://vyral.local/provider-jobs?provider=local-deterministic-ai&limit=5&includeResult=true",
      method: "GET",
      body: undefined
    },
    {
      url: "http://vyral.local/provider-jobs/job-1",
      method: "GET",
      body: undefined
    },
    {
      url: "http://vyral.local/provider-jobs/job-1",
      method: "DELETE",
      body: undefined
    }
  ]);
});

test("waitProviderJob polls until terminal status", async () => {
  const responses = [
    { id: "job-1", status: "running" },
    { id: "job-1", status: "succeeded" }
  ];
  const client = new VyralClient("http://vyral.local", {
    fetch: async () => new Response(JSON.stringify(responses.shift()), { status: 200 })
  });

  const result = await client.waitProviderJob("job-1", { timeoutMs: 1000, pollIntervalMs: 0 });

  assert.equal(result.status, "succeeded");
  assert.deepEqual(responses, []);
});

test("canonical preflight and effective execution discovery use their safe routes", async () => {
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({ url, method: init.method });
      return new Response(JSON.stringify({ ok: true }), { status: 200 });
    }
  });

  const preflight = await client.getCanonicalPreflight();
  const probe = await client.probeCanonicalDataPlane();
  const runtime = await client.getEffectiveExecutionRuntime({ productId: "product/a", tenantId: "tenant a" });

  assert.equal(preflight.ok, true);
  assert.equal(probe.ok, true);
  assert.equal(runtime.ok, true);
  assert.deepEqual(requests, [
    { method: "GET", url: "http://vyral.local/canonical/preflight" },
    { method: "POST", url: "http://vyral.local/canonical/preflight/probe" },
    { method: "GET", url: "http://vyral.local/execution/runtime/effective?productId=product%2Fa&tenantId=tenant+a" }
  ]);
});

test("graph and RAG ingestion job helpers preserve payloads, idempotency, and polling routes", async () => {
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      const request = {
        url,
        method: init.method,
        body: init.body ? JSON.parse(init.body) : undefined,
        idempotencyKey: new Headers(init.headers).get("Idempotency-Key")
      };
      requests.push(request);
      if (url.endsWith("/graph/jobs/graph-1") && init.method === "GET") {
        return new Response(JSON.stringify({ id: "graph-1", status: requests.filter((item) => item.url === url).length === 1 ? "running" : "succeeded" }), { status: 200 });
      }
      if (url.endsWith("/rag/ingestion/jobs/rag-1") && init.method === "GET") {
        return new Response(JSON.stringify({ id: "rag-1", status: "succeeded" }), { status: 200 });
      }
      if (url.endsWith("/record-import/jobs/record-1") && init.method === "GET") {
        return new Response(JSON.stringify({ id: "record-1", status: "succeeded" }), { status: 200 });
      }
      if (init.method === "GET") return new Response(JSON.stringify([{ id: "job-1", status: "queued" }]), { status: 200 });
      return new Response(JSON.stringify({ id: "job-1", status: "queued" }), { status: 202 });
    }
  });

  await client.startGraphImportJob("graphs", { envelope: { graph: { id: "g" } } }, { idempotencyKey: "graph-import-1" });
  await client.startGraphInspectionJob("graphs", { graphId: "g" }, { idempotencyKey: "graph-inspect-1" });
  await client.startGraphDoctorJob("graphs", { graphId: "g" }, { idempotencyKey: "graph-doctor-1" });
  await client.listGraphJobs({ limit: 3, includeResult: true });
  const graph = await client.waitGraphJob("graph-1", { timeoutMs: 1000, pollIntervalMs: 0 });
  await client.cancelGraphJob("graph-1");

  await client.startRagTextIngestionJob("chunks", { documentId: "doc-1", partitionKey: "tenant-a", text: "alpha" }, { idempotencyKey: "rag-text-1" });
  await client.startRagTextBatchIngestionJob("chunks", { items: [], continueOnError: true }, { idempotencyKey: "rag-batch-1" });
  await client.listRagIngestionJobs({ limit: 2, includeResult: false });
  const rag = await client.waitRagIngestionJob("rag-1", { timeoutMs: 1000, pollIntervalMs: 0 });
  await client.cancelRagIngestionJob("rag-1");
  const record = await client.waitRecordImportJob("record-1", { timeoutMs: 1000, pollIntervalMs: 0 });

  assert.equal(graph.status, "succeeded");
  assert.equal(rag.status, "succeeded");
  assert.equal(record.status, "succeeded");
  assert.deepEqual(requests.slice(0, 4), [
    {
      url: "http://vyral.local/collections/graphs/graph/import/jobs",
      method: "POST",
      body: { envelope: { graph: { id: "g" } } },
      idempotencyKey: "graph-import-1"
    },
    {
      url: "http://vyral.local/collections/graphs/graph/inspect/jobs",
      method: "POST",
      body: { graphId: "g" },
      idempotencyKey: "graph-inspect-1"
    },
    {
      url: "http://vyral.local/collections/graphs/graph/doctor/jobs",
      method: "POST",
      body: { graphId: "g" },
      idempotencyKey: "graph-doctor-1"
    },
    {
      url: "http://vyral.local/graph/jobs?limit=3&includeResult=true",
      method: "GET",
      body: undefined,
      idempotencyKey: null
    }
  ]);
  assert.equal(requests.some((item) => item.url === "http://vyral.local/collections/chunks/rag/ingest-text/jobs" && item.idempotencyKey === "rag-text-1"), true);
  assert.equal(requests.some((item) => item.url === "http://vyral.local/collections/chunks/rag/ingest-text/batch/jobs" && item.idempotencyKey === "rag-batch-1"), true);
  assert.equal(requests.some((item) => item.url === "http://vyral.local/rag/ingestion/jobs?limit=2&includeResult=false"), true);
  assert.equal(requests.filter((item) => item.url === "http://vyral.local/graph/jobs/graph-1" && item.method === "GET").length, 2);
  assert.equal(requests.some((item) => item.url === "http://vyral.local/graph/jobs/graph-1" && item.method === "DELETE"), true);
  assert.equal(requests.some((item) => item.url === "http://vyral.local/rag/ingestion/jobs/rag-1" && item.method === "DELETE"), true);
});

test("deleteCollection uses DELETE collection route", async () => {
  let request;
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init) => {
      request = { url, method: init.method };
      return new Response(null, { status: 204 });
    }
  });

  await client.deleteCollection("chunk/name");

  assert.deepEqual(request, {
    url: "http://vyral.local/collections/chunk%2Fname",
    method: "DELETE"
  });
});

test("all receipt-bound start helpers forward idempotency headers", async () => {
  const requests = [];
  const client = new VyralClient("http://vyral.local", {
    fetch: async (url, init = {}) => {
      requests.push({ url, idempotencyKey: new Headers(init.headers).get("Idempotency-Key") });
      return new Response('{"id":"admitted-1"}', { status: 202, headers: { "Content-Type": "application/json" } });
    }
  });

  await client.startExecutionRun({ handlerId: "test.handler" }, { idempotencyKey: "execution-1" });
  await client.startEmbeddingJob({ texts: ["alpha"] }, { idempotencyKey: "embedding-1" });
  await client.startProviderJob("provider-1", { capability: "ai.chat" }, { idempotencyKey: "provider-1" });
  await client.runProvider("provider-1", { capability: "ai.chat" }, { idempotencyKey: "provider-alias-1" });
  await client.startRetrievalEvaluationJob({ cases: [] }, { idempotencyKey: "evaluation-1" });
  await client.startRetrievalEvaluationComparisonJob(
    { cases: [], variants: [] },
    { idempotencyKey: "comparison-1" }
  );
  await client.importCollection("records", {}, { idempotencyKey: "import-1" });
  await client.importGraphEnvelope("graph", {}, { idempotencyKey: "graph-1" });
  await client.upsertRecords("records", [], { idempotencyKey: "batch-1" });
  await client.ingestRagText("chunks", {}, { idempotencyKey: "rag-text-1" });
  await client.ingestRagTexts("chunks", [], { idempotencyKey: "rag-batch-1" });
  await client.createCollection({ name: "records" }, { idempotencyKey: "collection-create-1" });
  await client.deleteCollection("records", { idempotencyKey: "collection-delete-1" });

  assert.deepEqual(
    requests.map((request) => request.idempotencyKey),
    [
      "execution-1", "embedding-1", "provider-1", "provider-alias-1", "evaluation-1", "comparison-1",
      "import-1", "graph-1", "batch-1", "rag-text-1", "rag-batch-1",
      "collection-create-1", "collection-delete-1"
    ]
  );
});

test("rejected admission is available on client errors", () => {
  const error = new VyralClientError(429, JSON.stringify({
    admission: {
      status: "rejected",
      failureClass: "queue_full",
      resourceId: "run-1"
    }
  }));

  assert.equal(error.admission.resourceId, "run-1");
  assert.equal(error.failureClass, "queue_full");
});

test("AI metering fixture has portable canonical hashes", () => {
  const receipt = JSON.parse(readFileSync(new URL("../../../conformance/ai-metering/v1/receipt.json", import.meta.url), "utf8"));
  const review = JSON.parse(readFileSync(new URL("../../../conformance/ai-metering/v1/review.json", import.meta.url), "utf8"));
  const manifest = JSON.parse(readFileSync(new URL("../../../conformance/ai-metering/v1/manifest.json", import.meta.url), "utf8"));
  const canonicalize = (value) => {
    if (Array.isArray(value)) return value.map(canonicalize);
    if (value !== null && typeof value === "object") {
      return Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonicalize(value[key])]));
    }
    return value;
  };
  const canonicalHash = (value) => `sha256:${createHash("sha256").update(JSON.stringify(canonicalize(value)), "utf8").digest("hex")}`;
  const { integrity: _integrity, ...payload } = receipt;
  const { integrity: _reviewIntegrity, ...reviewPayload } = review;

  assert.equal(canonicalHash(payload), manifest.expectedPayloadHash);
  assert.equal(canonicalHash(receipt), manifest.expectedEnvelopeHash);
  const signatureFixture = manifest.signature.fixture;
  assert.equal(canonicalHash({
    schema: "vyral.ai-metering-integrity.v1",
    algorithm: "ES256",
    evidenceSchema: receipt.schema,
    issuer: signatureFixture.issuer,
    keyId: signatureFixture.keyId,
    payloadHash: manifest.expectedPayloadHash,
  }), signatureFixture.expectedInputHash);
  assert.equal(canonicalHash(reviewPayload), manifest.expectedReviewPayloadHash);
  const reviewSignatureFixture = manifest.reviewSignatureFixture;
  assert.equal(canonicalHash({
    schema: "vyral.ai-metering-integrity.v1",
    algorithm: "ES256",
    evidenceSchema: review.schema,
    issuer: reviewSignatureFixture.issuer,
    keyId: reviewSignatureFixture.keyId,
    payloadHash: manifest.expectedReviewPayloadHash,
  }), reviewSignatureFixture.expectedInputHash);
  assert.equal(canonicalHash(review), manifest.expectedReviewEnvelopeHash);
});
