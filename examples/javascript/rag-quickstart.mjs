import { VyralClient } from "../../clients/javascript/src/index.js";

const collection = process.env.VYRAL_COLLECTION ?? "quickstart-chunks-js";
const partitionKey = "tenant:quickstart";

const client = new VyralClient(process.env.VYRAL_URL ?? "http://localhost:5220");

const deleteRun = await client.deleteCollection(collection, { idempotencyKey: `quickstart:${collection}:delete` });
await client.waitExecutionRun(deleteRun.id);
const createRun = await client.createCollection({
  name: collection,
  partitionKeyPath: "/partitionKey",
  vectorPolicies: [],
  indexedMetadata: ["/metadata/status", "/metadata/topic"]
}, {
  idempotencyKey: `quickstart:${collection}:create`
});
await client.waitExecutionRun(createRun.id);

const documents = [
  {
    id: "retention",
    topic: "records",
    text: "Retention holds keep protected records from deletion until the hold is released."
  },
  {
    id: "travel",
    topic: "finance",
    text: "Travel reimbursement requires receipts for hotels, flights, and approved meals."
  },
  {
    id: "security",
    topic: "security",
    text: "Compromised credentials should be rotated and reviewed through incident response."
  }
];

await client.upsertRecords(collection, documents.map((document) => ({
  id: document.id,
  partitionKey,
  type: "rag.chunk",
  content: { text: document.text },
  metadata: {
    status: "active",
    topic: document.topic
  },
  sources: [{
    id: document.id,
    kind: "example",
    uri: `memory://quickstart/${document.id}`,
    label: document.id
  }]
})), { idempotencyKey: `quickstart:${collection}:ingest` });

const retrieval = {
  query: "Retention holds keep protected records from deletion until the hold is released.",
  collections: [collection],
  partitionKeys: [partitionKey],
  searchMode: "lexical",
  lexical: { fields: ["/content/text"] },
  limit: 2,
  includeTrace: true
};
const contextRequest = {
  retrieval,
  maxChars: 2000,
  maxCharsPerChunk: 800,
  includeContextText: true,
  includeTrace: true
};
const context = await client.buildRagContext(contextRequest);
const prompt = await client.buildRagPrompt({
  context: contextRequest,
  template: { failOnEmptyContext: true }
});

console.log("retrieval=lexical embeddings=unused");
for (const chunk of context.chunks) {
  const citations = chunk.citationIds?.join(", ") || "none";
  console.log(`${chunk.rank}. ${chunk.id} score=${chunk.score.toFixed(4)} citations=${citations} text=${chunk.text}`);
}

for (const citation of context.citations ?? []) {
  console.log(`[${citation.id}] ${citation.sourceUri ?? citation.recordId}`);
}

console.log("\ncontextText:");
console.log(context.contextText);
console.log(`contextTextHash=${context.contextTextHash}`);
console.log(`promptHash=${prompt.promptHash}`);
