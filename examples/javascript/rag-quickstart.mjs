import { VyralClient } from "../../clients/javascript/src/index.js";

const collection = process.env.VYRAL_COLLECTION ?? "quickstart-chunks-js";
const partitionKey = "tenant:quickstart";
const vectorField = "contentEmbedding";

const client = new VyralClient(process.env.VYRAL_URL ?? "http://localhost:5220");
const health = await client.health();
const embedding = health.embedding;

const deleteRun = await client.deleteCollection(collection, { idempotencyKey: `quickstart:${collection}:delete` });
await client.waitExecutionRun(deleteRun.id);
const createRun = await client.createRagCollection(collection, {
  dimensions: embedding.dimensions,
  embeddingField: vectorField,
  indexedMetadata: ["/metadata/status", "/metadata/topic"],
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

const ingestion = await client.ingestRagTexts(collection, documents.map((document) => ({
    documentId: document.id,
    partitionKey,
    text: document.text,
    embeddingField: vectorField,
    sourceUri: `memory://quickstart/${document.id}`,
    sourceKind: "example",
    metadata: {
      status: "active",
      topic: document.topic
    }
  })), { idempotencyKey: `quickstart:${collection}:ingest` });
await client.waitRagIngestionJob(ingestion.id);

const context = await client.buildRagContext({
  query: "Retention holds keep protected records from deletion until the hold is released.",
  collections: [collection],
  partitionKeys: [partitionKey],
  embeddingField: vectorField,
  limit: 2,
  maxChars: 2000,
  maxCharsPerChunk: 800,
  includeContextText: true,
  includeTrace: true
});
const prompt = await client.buildRagPrompt({
  context: {
    query: "Retention holds keep protected records from deletion until the hold is released.",
    collections: [collection],
    partitionKeys: [partitionKey],
    embeddingField: vectorField,
    limit: 2,
    maxChars: 2000,
    maxCharsPerChunk: 800,
    includeTrace: true
  },
  template: { failOnEmptyContext: true }
});

console.log(`provider=${embedding.provider} model=${embedding.modelId} dimensions=${embedding.dimensions}`);
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
