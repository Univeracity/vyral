import { VyralClient, VyralClientError } from "vyral-client";

const baseUrl = process.env.VYRAL_TEST_BASE_URL;
const apiKey = process.env.VYRAL_TEST_API_KEY;
const collection = "sdk-real-server-js";
if (!baseUrl || !apiKey) throw new Error("real-server test environment is missing");

await VyralClient.prototype.listCollections.call(new VyralClient(baseUrl)).then(
  () => { throw new Error("private route accepted an unauthenticated JavaScript SDK request"); },
  (error) => {
    if (!(error instanceof VyralClientError) || error.status !== 401) throw error;
  }
);

const client = new VyralClient(baseUrl, {
  apiKey,
  correlationId: "javascript-built-package",
  timeoutMs: 10_000,
  maxRetries: 1
});
if ((await client.health()).status !== "ok") throw new Error("health failed");
if ((await client.openApiContract()).openapi !== "3.1.0") throw new Error("OpenAPI 3.1 contract missing");
if (!(await client.getPublicSchemaContract()).$defs.VyralRecord) throw new Error("public schema missing VyralRecord");

const createRun = await client.createCollection({
  name: collection,
  partitionKeyPath: "/partitionKey",
  indexedMetadata: ["/metadata/source"],
  vectorPolicies: []
}, { idempotencyKey: "javascript-built-package:create" });
if ((await client.waitExecutionRun(createRun.id))?.status !== "succeeded") throw new Error("collection creation failed");
const record = {
  id: "record-1",
  partitionKey: "tenant-a",
  type: "consumer.event",
  metadata: { source: "javascript" },
  content: { message: "packed package" }
};
if ((await client.upsertRecord(collection, record)).id !== "record-1") throw new Error("upsert failed");
if ((await client.getRecord(collection, "tenant-a", "record-1")).content.message !== "packed package") {
  throw new Error("record read failed");
}
const ids = [];
for await (const item of client.iterateRecords(collection, { limit: 10 }, { maxPages: 2 })) ids.push(item.id);
if (ids.join(",") !== "record-1") throw new Error("record iterator failed");

if ((await client.putObject("sdk-artifacts", "javascript/raw.txt", "javascript-object", {
  contentType: "text/plain"
})).key !== "javascript/raw.txt") throw new Error("object write failed");
if (new TextDecoder().decode(await client.getObject("sdk-artifacts", "javascript/raw.txt")) !== "javascript-object") {
  throw new Error("object read failed");
}
await client.deleteObject("sdk-artifacts", "javascript/raw.txt");

const artifactRun = await client.ingestRecordArtifact(
  {
    collection,
    record: {
      id: "artifact-1",
      partitionKey: "tenant-a",
      type: "consumer.artifact",
      content: { source: "javascript-package" }
    },
    artifact: {
      container: "sdk-artifacts",
      key: "javascript/artifact.json",
      contentType: "application/json"
    }
  },
  new Blob(['{"source":"javascript-package"}'], { type: "application/json" }),
  { fileName: "artifact.json", idempotencyKey: "javascript-built-package:artifact" }
);
const receipt = (await client.waitExecutionRun(artifactRun.id))?.result;
if (!receipt.accepted) throw new Error("artifact ingestion failed");
if (new TextDecoder().decode(await client.getObject("sdk-artifacts", "javascript/artifact.json")) !== '{"source":"javascript-package"}') {
  throw new Error("ingested artifact read failed");
}

const event = await client.raiseExecutionEvent("sdk-event-run-js", {
  name: "approved",
  payload: { source: "javascript" }
});
if (event.runId !== "sdk-event-run-js" || event.name !== "approved") throw new Error("execution event failed");

const deleteRun = await client.deleteCollection(collection, { idempotencyKey: "javascript-built-package:delete" });
if ((await client.waitExecutionRun(deleteRun.id))?.status !== "succeeded") throw new Error("collection deletion failed");
console.log("javascript-built-sdk-real-server=ok");
