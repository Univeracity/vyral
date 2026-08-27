#!/usr/bin/env node

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";
import { Miniflare } from "miniflare";

const root = path.dirname(fileURLToPath(import.meta.url));
const workerPath = path.join(root, "src", "worker.mjs");
const objectReaderPath = path.join(root, "src", "object-reader.mjs");
const secret = "worker-r2-proof-continuation-secret-20260827";
const authorizationSecret = "worker-r2-proof-authorization-secret-20260827";
const objectReaderSecret = "worker-r2-proof-object-reader-secret-20260827";
let bindingMode = "direct-r2";

function progress(message) {
  process.stderr.write(`[worker-r2-proof] ${message}\n`);
}

function scoreHex(score) {
  const bytes = new Uint8Array(8);
  new DataView(bytes.buffer).setFloat64(0, score, false);
  return [...bytes].map((value) => value.toString(16).padStart(2, "0")).join("");
}

function requireArguments() {
  const argumentsByName = new Map();
  for (let index = 2; index < process.argv.length; index += 2) {
    const name = process.argv[index];
    const value = process.argv[index + 1];
    if (!name?.startsWith("--") || value === undefined) throw new Error("Usage: verify.mjs --bundle <path> [--mode direct-r2|service-reader]");
    argumentsByName.set(name, value);
  }
  const bundle = argumentsByName.get("--bundle");
  const mode = argumentsByName.get("--mode") ?? "direct-r2";
  if (bundle === undefined || argumentsByName.size > 2 || [...argumentsByName.keys()].some((name) => !["--bundle", "--mode"].includes(name)) || !["direct-r2", "service-reader"].includes(mode)) {
    throw new Error("Usage: verify.mjs --bundle <path> [--mode direct-r2|service-reader]");
  }
  return { bundle, mode };
}

async function createRuntime(objects, omittedKeys = new Set()) {
  progress(`creating ${bindingMode} runtime with ${Object.keys(objects).length - omittedKeys.size} R2 objects`);
  const runtime = bindingMode === "service-reader"
    ? new Miniflare({
      workers: [
        {
          name: "query",
          modules: [{ type: "ESModule", path: workerPath }],
          compatibilityDate: "2026-08-06",
          compatibilityFlags: ["nodejs_compat"],
          serviceBindings: { OBJECT_READER: "reader" },
          bindings: {
            CONTINUATION_SECRET: secret,
            AUTHORIZATION_SECRET: authorizationSecret,
            OBJECT_READER_SECRET: objectReaderSecret,
          },
        },
        {
          name: "reader",
          modules: [{ type: "ESModule", path: objectReaderPath }],
          compatibilityDate: "2026-08-06",
          compatibilityFlags: ["nodejs_compat"],
          r2Buckets: { INDEX: "vyral-worker-r2-proof" },
          bindings: { OBJECT_READER_SECRET: objectReaderSecret },
        },
      ],
    })
    : new Miniflare({
      modules: [{ type: "ESModule", path: workerPath }],
      compatibilityDate: "2026-08-06",
      compatibilityFlags: ["nodejs_compat"],
      r2Buckets: { INDEX: "vyral-worker-r2-proof" },
      bindings: { CONTINUATION_SECRET: secret, AUTHORIZATION_SECRET: authorizationSecret },
    });
  try {
    const bucket = await runtime.getR2Bucket("INDEX", bindingMode === "service-reader" ? "reader" : undefined);
    for (const [key, value] of Object.entries(objects)) {
      if (!omittedKeys.has(key)) await bucket.put(key, value);
    }
    progress("runtime and R2 fixture ready");
    return {
      runtime,
      bucket,
      worker: await runtime.getWorker(bindingMode === "service-reader" ? "query" : undefined),
      reader: bindingMode === "service-reader" ? await runtime.getWorker("reader") : null,
    };
  } catch (error) {
    await runtime.dispose();
    throw error;
  }
}

async function post(worker, route, body) {
  const response = await worker.fetch(`https://worker.invalid${route}`, {
    method: "POST",
    headers: { "authorization": `Bearer ${authorizationSecret}`, "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  const value = await response.json();
  assert.equal(response.status, 200, `unexpected ${route} status: ${response.status} ${JSON.stringify(value)}`);
  return value;
}

function compareCandidates(actual, expected, label) {
  assert.deepEqual(actual.map((item) => [item.partitionKey, item.id, item.revision]), expected.map((item) => [item.partitionKey, item.id, item.revision]), `${label}: candidate identity/revision divergence`);
  assert.deepEqual(actual.map((item) => scoreHex(item.score)), expected.map((item) => item.scoreHex), `${label}: candidate score divergence`);
}

async function withRuntime(objects, action, omittedKeys = new Set()) {
  const context = await createRuntime(objects, omittedKeys);
  try {
    return await action(context);
  } finally {
    progress("disposing runtime");
    await context.runtime.dispose();
    progress("runtime disposed");
  }
}

async function proveReaderConfigurationFailsClosed(bundle) {
  for (const configuration of ["missing", "ambiguous", "query-secret-missing", "reader-secret-missing"]) {
    const query = {
      name: "query",
      modules: [{ type: "ESModule", path: workerPath }],
      compatibilityDate: "2026-08-06",
      compatibilityFlags: ["nodejs_compat"],
      bindings: { CONTINUATION_SECRET: secret, AUTHORIZATION_SECRET: authorizationSecret },
    };
    const workers = [query];
    if (configuration !== "missing") {
      query.serviceBindings = { OBJECT_READER: "reader" };
      if (configuration !== "query-secret-missing") query.bindings.OBJECT_READER_SECRET = objectReaderSecret;
      if (configuration === "ambiguous") query.r2Buckets = { INDEX: "vyral-worker-r2-proof" };
      workers.push({
        name: "reader",
        modules: [{ type: "ESModule", path: objectReaderPath }],
        compatibilityDate: "2026-08-06",
        compatibilityFlags: ["nodejs_compat"],
        r2Buckets: { INDEX: "vyral-worker-r2-proof" },
        bindings: configuration === "reader-secret-missing" ? {} : { OBJECT_READER_SECRET: objectReaderSecret },
      });
    }
    const runtime = new Miniflare({ workers });
    try {
      const worker = await runtime.getWorker("query");
      const result = await post(worker, "/search", {
        collection: bundle.collection,
        request: bundle.queries[0].request,
      });
      assert.equal(result.status, "failed");
      assert.equal(result.failure.code, "providerUnavailable");
      assert.deepEqual(result.items, []);
    } finally {
      await runtime.dispose();
    }
  }
  return true;
}

function clone(value) {
  return structuredClone(value);
}

async function main() {
  const argumentsValue = requireArguments();
  const bundlePath = argumentsValue.bundle;
  bindingMode = argumentsValue.mode;
  const bundle = JSON.parse(await readFile(bundlePath, "utf8"));
  assert.equal(bundle.schemaVersion, "vyral.worker-r2-proof-bundle.v1");
  assert.ok(Array.isArray(bundle.queries) && bundle.queries.length >= 4);

  const sampleResults = [];
  progress("starting primary lifecycle and parity proof");
  const mainProof = await withRuntime(bundle.objects, async ({ worker, bucket, reader }) => {
    let serviceReaderGuards = null;
    if (reader !== null) {
      const missingReaderAuthorization = await reader.fetch("https://objects.internal/read", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ key: bundle.activeKey }),
      });
      assert.equal(missingReaderAuthorization.status, 401);
      const wrongReaderAuthorization = await reader.fetch("https://objects.internal/read", {
        method: "POST",
        headers: { "authorization": "Bearer wrong", "content-type": "application/json" },
        body: JSON.stringify({ key: bundle.activeKey }),
      });
      assert.equal(wrongReaderAuthorization.status, 401);
      const readerMutation = await reader.fetch("https://objects.internal/read", {
        method: "PUT",
        headers: { "authorization": `Bearer ${objectReaderSecret}`, "content-type": "application/json" },
        body: JSON.stringify({ key: bundle.activeKey }),
      });
      assert.equal(readerMutation.status, 404);
      const invalidReaderKey = await reader.fetch("https://objects.internal/read", {
        method: "POST",
        headers: { "authorization": `Bearer ${objectReaderSecret}`, "content-type": "application/json" },
        body: JSON.stringify({ key: "../outside" }),
      });
      assert.equal(invalidReaderKey.status, 400);
      serviceReaderGuards = true;
    }
    const unauthorized = await worker.fetch("https://worker.invalid/search", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ collection: bundle.collection, request: bundle.queries[0].request }),
    });
    assert.equal(unauthorized.status, 401);
    assert.equal((await unauthorized.json()).code, "unauthorized");
    const wrongAuthorization = await worker.fetch("https://worker.invalid/search", {
      method: "POST",
      headers: { "authorization": "Bearer wrong", "content-type": "application/json" },
      body: JSON.stringify({ collection: bundle.collection, request: bundle.queries[0].request }),
    });
    assert.equal(wrongAuthorization.status, 401);
    assert.equal((await wrongAuthorization.json()).code, "unauthorized");
    const wrongMediaType = await worker.fetch("https://worker.invalid/search", {
      method: "POST",
      headers: { "authorization": `Bearer ${authorizationSecret}`, "content-type": "text/plain" },
      body: "{}",
    });
    assert.equal(wrongMediaType.status, 415);
    assert.equal((await wrongMediaType.json()).code, "invalidRequest");
    const oversizedBody = await worker.fetch("https://worker.invalid/search", {
      method: "POST",
      headers: { "authorization": `Bearer ${authorizationSecret}`, "content-type": "application/json" },
      body: JSON.stringify({ padding: "x".repeat(131_072) }),
    });
    assert.equal(oversizedBody.status, 400);
    assert.equal((await oversizedBody.json()).code, "invalidRequest");

    const inspection = await post(worker, "/inspect", {
      collection: bundle.collection,
      generationId: null,
      expectedDescriptorDigest: bundle.descriptor.descriptorDigest,
    });
    assert.equal(inspection.schema, "vyral.record-search-projection-inspection.v1");
    assert.equal(inspection.state, "active");
    assert.equal(inspection.coverageStatus, "complete");
    assert.deepEqual(inspection.availablePartitions, bundle.descriptor.expectedPartitions);
    const namedInspection = await post(worker, "/inspect", {
      collection: bundle.collection,
      generationId: bundle.generationId,
      expectedDescriptorDigest: bundle.descriptor.descriptorDigest,
    });
    assert.equal(namedInspection.descriptor.descriptorDigest, bundle.descriptor.descriptorDigest);
    assert.equal(namedInspection.coverageStatus, "complete");

    let parity = 0;
    for (const query of bundle.queries) {
      progress(`running parity query ${query.id}`);
      const result = await post(worker, "/search", { collection: bundle.collection, request: query.request });
      assert.equal(result.status, "succeeded", `${query.id}: ${JSON.stringify(result.failure)}`);
      assert.equal(result.generationId, bundle.generationId);
      assert.equal(result.generationDescriptorDigest, bundle.descriptor.descriptorDigest);
      assert.equal(result.coverage.status, "complete");
      assert.equal(result.diagnostics.details.immutableReaderMode, bindingMode);
      compareCandidates(result.items, query.expected, query.id);
      parity += 1;
      if (sampleResults.length < 2) sampleResults.push(result);
    }

    const exactRequest = clone(bundle.queries[0].request);
    exactRequest.generationId = bundle.generationId;
    exactRequest.expectedDescriptorDigest = bundle.descriptor.descriptorDigest;
    const exact = await post(worker, "/search", { collection: bundle.collection, request: exactRequest });
    assert.equal(exact.status, "succeeded");
    compareCandidates(exact.items, bundle.queries[0].expected, "exact generation selection");

    const malformedDigestRequest = clone(bundle.queries[0].request);
    malformedDigestRequest.expectedDescriptorDigest = "sha256:not-a-digest";
    const malformedDigest = await post(worker, "/search", { collection: bundle.collection, request: malformedDigestRequest });
    assert.equal(malformedDigest.status, "failed");
    assert.equal(malformedDigest.failure.code, "invalidRequest");
    assert.deepEqual(malformedDigest.items, []);
    sampleResults.push(malformedDigest);

    const paged = bundle.pagedQuery;
    const firstRequest = clone(paged.request);
    firstRequest.query.limit = 5;
    firstRequest.query.lexical.top = 5;
    const first = await post(worker, "/search", { collection: bundle.collection, request: firstRequest });
    assert.equal(first.status, "succeeded");
    assert.equal(typeof first.continuationToken, "string");
    compareCandidates(first.items, paged.expected.slice(0, 5), "first page");

    const retainedCatalog = JSON.parse(bundle.objects[bundle.catalogKey]);
    retainedCatalog.state = "retained";
    await bucket.put(bundle.catalogKey, JSON.stringify(retainedCatalog));
    const replacement = {
      schemaVersion: "vyral.private.worker-r2-active.v1",
      collection: bundle.collection,
      generationId: "replacement-generation",
      descriptorDigest: `sha256:${"9".repeat(64)}`,
    };
    await bucket.put(bundle.activeKey, JSON.stringify(replacement));

    const secondRequest = clone(firstRequest);
    secondRequest.query.continuationToken = first.continuationToken;
    const second = await post(worker, "/search", { collection: bundle.collection, request: secondRequest });
    assert.equal(second.status, "succeeded");
    assert.equal(second.generationId, bundle.generationId);
    compareCandidates(second.items, paged.expected.slice(5, 10), "retained second page");

    const changedRequest = clone(secondRequest);
    changedRequest.query.lexical.query += " changed";
    const changed = await post(worker, "/search", { collection: bundle.collection, request: changedRequest });
    assert.equal(changed.status, "failed");
    assert.equal(changed.failure.code, "invalidContinuation");
    assert.deepEqual(changed.items, []);

    const tamperedRequest = clone(secondRequest);
    const token = tamperedRequest.query.continuationToken;
    tamperedRequest.query.continuationToken = `${token[0] === "A" ? "B" : "A"}${token.slice(1)}`;
    const tampered = await post(worker, "/search", { collection: bundle.collection, request: tamperedRequest });
    assert.equal(tampered.status, "failed");
    assert.equal(tampered.failure.code, "invalidContinuation");
    assert.deepEqual(tampered.items, []);

    retainedCatalog.state = "retired";
    retainedCatalog.availablePartitions = [];
    await bucket.put(bundle.catalogKey, JSON.stringify(retainedCatalog));
    const retired = await post(worker, "/search", { collection: bundle.collection, request: secondRequest });
    assert.equal(retired.status, "failed");
    assert.equal(retired.failure.code, "generationRetired");
    assert.deepEqual(retired.items, []);
    sampleResults.push(changed, tampered, retired);

    return {
      parity,
      authenticationRejected: true,
      wrongAuthenticationRejected: true,
      nonJsonRejected: true,
      oversizedBodyRejected: true,
      malformedDescriptorDigestRejected: true,
      exactGenerationSelection: true,
      serviceReaderGuards,
      retainedContinuation: true,
      tamperRejected: true,
      changedRequestRejected: true,
      retirementRejected: true,
    };
  });

  progress("starting verified-content cache proof");
  const cacheProof = await withRuntime(bundle.objects, async ({ worker }) => {
    const first = await post(worker, "/search", { collection: bundle.collection, request: bundle.queries[0].request });
    const second = await post(worker, "/search", { collection: bundle.collection, request: bundle.queries[0].request });
    assert.equal(first.status, "succeeded");
    assert.equal(second.status, "succeeded");
    assert.equal(second.diagnostics.cacheStatus, "hit");
    return true;
  });

  progress("starting missing-shard proof");
  const missingShard = await withRuntime(bundle.objects, async ({ worker }) => {
    const result = await post(worker, "/search", { collection: bundle.collection, request: bundle.queries[0].request });
    assert.equal(result.status, "failed");
    assert.equal(result.failure.code, "generationUnavailable");
    assert.deepEqual(result.items, []);
    sampleResults.push(result);
    return true;
  }, new Set([bundle.shardKeys[0]]));

  progress("starting corrupt-shard proof");
  const corruptShard = await withRuntime(bundle.objects, async ({ worker, bucket }) => {
    await bucket.put(bundle.shardKeys[0], "{\"corrupt\":true}");
    const result = await post(worker, "/search", { collection: bundle.collection, request: bundle.queries[0].request });
    assert.equal(result.status, "failed");
    assert.equal(result.failure.code, "artifactDigestMismatch");
    assert.deepEqual(result.items, []);
    sampleResults.push(result);
    return true;
  });

  const incompleteCoverage = await withRuntime(bundle.objects, async ({ worker, bucket }) => {
    const catalog = JSON.parse(bundle.objects[bundle.catalogKey]);
    catalog.availablePartitions = [];
    await bucket.put(bundle.catalogKey, JSON.stringify(catalog));
    const result = await post(worker, "/search", { collection: bundle.collection, request: bundle.queries[0].request });
    assert.equal(result.status, "failed");
    assert.equal(result.failure.code, "coverageIncomplete");
    assert.equal(result.coverage.status, "incomplete");
    assert.deepEqual(result.items, []);
    sampleResults.push(result);
    return true;
  });

  const descriptorFence = await withRuntime(bundle.objects, async ({ worker }) => {
    const request = clone(bundle.queries[0].request);
    request.expectedDescriptorDigest = `sha256:${"8".repeat(64)}`;
    const result = await post(worker, "/search", { collection: bundle.collection, request });
    assert.equal(result.status, "failed");
    assert.equal(result.failure.code, "generationDescriptorMismatch");
    assert.deepEqual(result.items, []);
    sampleResults.push(result);
    return true;
  });

  const deadlineFence = await withRuntime(bundle.objects, async ({ worker }) => {
    const request = clone(bundle.queries[0].request);
    request.deadlineUtc = "2000-01-01T00:00:00Z";
    const result = await post(worker, "/search", { collection: bundle.collection, request });
    assert.equal(result.status, "failed");
    assert.equal(result.failure.code, "deadlineExceeded");
    assert.deepEqual(result.items, []);
    sampleResults.push(result);
    return true;
  });

  const workFence = await withRuntime(bundle.objects, async ({ worker }) => {
    const request = clone(bundle.workBoundQuery.request);
    request.query.lexical.scanLimit = 1;
    const result = await post(worker, "/search", { collection: bundle.collection, request });
    assert.equal(result.status, "failed");
    assert.equal(result.failure.code, "workLimitExceeded");
    assert.equal(result.coverage.status, "complete");
    assert.deepEqual(result.items, []);
    sampleResults.push(result);
    return true;
  });

  const missingInspection = await withRuntime(bundle.objects, async ({ worker }) => {
    const inspection = await post(worker, "/inspect", { collection: bundle.collection, generationId: null });
    assert.equal(inspection.coverageStatus, "unavailable");
    assert.deepEqual(inspection.availablePartitions, []);
    return true;
  }, new Set([bundle.shardKeys[0]]));

  const readerConfigurationFence = await proveReaderConfigurationFailsClosed(bundle);

  process.stdout.write(`${JSON.stringify({
    schemaVersion: "vyral.worker-r2-proof-report.v1",
    status: "complete",
    bindingMode,
    queryCount: bundle.queries.length,
    exactCandidateAndScoreParityCount: mainProof.parity,
    unauthenticatedRequestRejected: mainProof.authenticationRejected,
    wrongAuthenticationRejected: mainProof.wrongAuthenticationRejected,
    nonJsonContentRejected: mainProof.nonJsonRejected,
    oversizedBodyRejected: mainProof.oversizedBodyRejected,
    malformedDescriptorDigestRejected: mainProof.malformedDescriptorDigestRejected,
    exactGenerationSelectionPassed: mainProof.exactGenerationSelection,
    serviceReaderGuardsPassed: mainProof.serviceReaderGuards,
    readerBindingConfigurationFailedClosed: readerConfigurationFence,
    retainedGenerationContinuationPassed: mainProof.retainedContinuation,
    continuationTamperRejected: mainProof.tamperRejected,
    continuationRequestSubstitutionRejected: mainProof.changedRequestRejected,
    retiredGenerationRejected: mainProof.retirementRejected,
    verifiedContentCacheHit: cacheProof,
    missingShardFailedClosed: missingShard,
    corruptShardFailedClosed: corruptShard,
    incompleteCoverageFailedClosed: incompleteCoverage,
    descriptorFenceRejected: descriptorFence,
    expiredDeadlineRejected: deadlineFence,
    workLimitFailedClosed: workFence,
    inspectionVerifiesArtifacts: missingInspection,
    sampleResults,
  })}\n`);
}

main().catch((error) => {
  process.stderr.write(`${error.stack ?? error}\n`);
  process.exitCode = 1;
});
