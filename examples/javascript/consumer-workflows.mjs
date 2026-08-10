import {
  VyralClient,
  buildGraphEdge,
  buildGraphEnvelope,
  buildGraphExpansionOptions,
  buildGraphNode,
  buildGraphScope,
  buildGraphSourceSpan,
  buildProviderExtractRequest,
  buildRagContextRequest,
  buildRerankOptions
} from "../../clients/javascript/src/index.js";

const baseUrl = process.env.VYRAL_URL ?? "http://127.0.0.1:5220";
const apiKey = process.env.VYRAL_API_KEY;
const collection = process.env.VYRAL_COLLECTION ?? "consumer-workflows-js";
const graphCollection = process.env.VYRAL_GRAPH_COLLECTION ?? "consumer-workflows-graph-js";
const partitionKey = "tenant:consumer-workflows";
const graphId = "consumer-workflows";
const vectorField = "contentEmbedding";

const documents = [
  {
    id: "retention",
    topic: "records",
    graphNodeId: "chunk:retention",
    text: "Retention holds keep protected records from deletion until the hold is released."
  },
  {
    id: "travel",
    topic: "finance",
    graphNodeId: "chunk:travel",
    text: "Travel reimbursement requires receipts for hotels, flights, and approved meals."
  },
  {
    id: "security",
    topic: "security",
    graphNodeId: "chunk:security",
    text: "Compromised credentials should be rotated and reviewed through incident response."
  },
  {
    id: "listing",
    topic: "catalog",
    graphNodeId: "chunk:listing",
    text: "Insulated stainless travel mugs should mention leak-resistant lids, capacity, care instructions, and fit with cup holders."
  }
];

const client = new VyralClient(baseUrl, { apiKey });
const health = await client.health();
const readiness = await client.readiness();
console.log(`server=${baseUrl} ready=${readiness.ready} summary=${readiness.summary}`);

await resetCollections();
await createRagCorpus(health);
await createGraph();

const lexical = await runLexicalRag();
const semantic = await runSemanticVectorRag();
const reranked = await runLexicalRerank();
const graph = await runGraphRag();
const extraction = await runAiExtract();
const providerInfo = await inspectProviders();

printSummary("lexical", lexical);
printSummary("semantic-vector", semantic);
printSummary("lexical-rerank", reranked);
printSummary("graphrag", graph);
printProviderSummary(extraction, providerInfo);

async function resetCollections() {
  const collectionDelete = await client.deleteCollection(collection, { idempotencyKey: `consumer:${collection}:delete` });
  const graphDelete = await client.deleteCollection(graphCollection, { idempotencyKey: `consumer:${graphCollection}:delete` });
  await Promise.all([
    client.waitExecutionRun(collectionDelete.id),
    client.waitExecutionRun(graphDelete.id)
  ]);
}

async function createRagCorpus(healthStatus) {
  const embedding = healthStatus.embedding;
  const createRun = await client.createRagCollection(collection, {
    dimensions: embedding.dimensions,
    embeddingField: vectorField,
    indexedMetadata: [
      "/metadata/documentId",
      "/metadata/topic",
      "/metadata/status",
      "/metadata/graphNodeId",
      "/type"
    ],
    idempotencyKey: `consumer:${collection}:create`
  });
  await client.waitExecutionRun(createRun.id);

  const ingestion = await client.ingestRagTexts(collection, documents.map((document) => ({
    documentId: document.id,
    partitionKey,
    text: document.text,
    embeddingField: vectorField,
    sourceUri: `memory://consumer-workflows/${document.id}`,
    sourceKind: "example",
    metadata: {
      status: "active",
      topic: document.topic,
      graphNodeId: document.graphNodeId
    }
  })), { continueOnError: false, idempotencyKey: `consumer:${collection}:ingest` });
  await client.waitRagIngestionJob(ingestion.id);
}

async function createGraph() {
  const source = buildGraphSourceSpan("memory://consumer-workflows/retention", {
    charStart: 0,
    charEnd: documents[0].text.length
  });
  const scope = buildGraphScope(graphId, {
    namespace: "examples",
    collection,
    tenantId: partitionKey,
    partitionKey
  });
  const envelope = buildGraphEnvelope(scope, {
    nodes: [
      buildGraphNode("chunk:retention", "chunk", { label: "Retention chunk", sourceSpans: [source] }),
      buildGraphNode("concept:retention-hold", "concept", { label: "Retention hold", sourceSpans: [source] }),
      buildGraphNode("concept:protected-record", "concept", { label: "Protected record", sourceSpans: [source] })
    ],
    edges: [
      buildGraphEdge("edge:retention-hold", "chunk:retention", "concept:retention-hold", "supports", {
        sourceSpans: [source]
      }),
      buildGraphEdge("edge:retention-record", "concept:retention-hold", "concept:protected-record", "mentions", {
        sourceSpans: [source]
      })
    ]
  });
  const job = await client.importGraphEnvelope(
    graphCollection,
    envelope,
    { replaceExisting: true, idempotencyKey: `consumer:${graphCollection}:import` }
  );
  await client.waitGraphJob(job.id);
}

async function runLexicalRag() {
  return client.buildRagContext(buildRagContextRequest(
    "retention protected records deletion hold",
    [collection],
    {
      partitionKeys: [partitionKey],
      searchMode: "lexical",
      limit: 3,
      includeContextText: true
    }
  ));
}

async function runSemanticVectorRag() {
  return client.buildRagContext(buildRagContextRequest(
    "how do we prevent protected records from being deleted",
    [collection],
    {
      partitionKeys: [partitionKey],
      searchMode: "vector",
      embedding: { field: vectorField, purpose: "query" },
      limit: 3,
      includeContextText: true
    }
  ));
}

async function runLexicalRerank() {
  return client.buildRagContext(buildRagContextRequest(
    "retention protected records deletion hold",
    [collection],
    {
      partitionKeys: [partitionKey],
      searchMode: "lexical",
      rerank: buildRerankOptions({
        provider: process.env.VYRAL_RECIPE_RERANK_PROVIDER ?? "local-token-overlap-reranker",
        candidateLimit: 8,
        maxCandidateChars: 800,
        fallbackOnFailure: true
      }),
      limit: 3,
      includeContextText: true
    }
  ));
}

async function runGraphRag() {
  const graphExpansion = buildGraphExpansionOptions(graphCollection, {
    graphId,
    partitionKey,
    seedJsonPointers: ["/metadata/graphNodeId"],
    profile: {
      id: "grounded-support",
      direction: "outgoing",
      maxDepth: 2,
      predicates: ["supports", "mentions"],
      requireSourceGrounding: true,
      limit: 8,
      edgeLimit: 8
    },
    maxGraphContextChars: 1000,
    maxGraphProvenanceItems: 16
  });
  const request = buildRagContextRequest(
    "retention protected records deletion hold",
    [collection],
    {
      partitionKeys: [partitionKey],
      searchMode: "lexical",
      graphExpansion,
      limit: 3,
      includeContextText: true
    }
  );
  const context = await client.buildRagContext(request);
  const evaluation = await client.evaluateRagContext({
    cases: [
      {
        name: "retention-graphrag",
        request,
        expectedGraph: {
          nodeIds: ["chunk:retention", "concept:retention-hold"],
          edgeIds: ["edge:retention-hold"],
          provenanceEntityIds: ["edge:retention-hold"],
          requireSourceGroundedProvenance: true,
          requireGraphContextText: true,
          requireContextTextNotTruncated: true
        }
      }
    ],
    includeContext: false
  });
  return { context, evaluation };
}

async function runAiExtract() {
  const provider = process.env.VYRAL_RECIPE_AI_PROVIDER ?? "local-deterministic-ai";
  const job = await client.runProvider(provider, buildProviderExtractRequest(documents[3].text, {
    schema: {
      type: "object",
      properties: {
        draftBullets: { type: "array", items: { type: "string" } },
        reviewNotes: { type: "array", items: { type: "string" } },
        claimsNeedingReview: { type: "array", items: { type: "string" } }
      }
    },
    instructions: "Extract product-listing copy fields and mark review-sensitive claims clearly.",
    maxOutputBytes: 32000
  }), { idempotencyKey: `consumer:${provider}:extract` });
  return (await client.waitProviderJob(job.id))?.result;
}

async function inspectProviders() {
  const provider = process.env.VYRAL_RECIPE_AI_PROVIDER ?? "local-deterministic-ai";
  return {
    providers: await client.listProviders(),
    matrix: await client.getProviderCapabilityMatrix(),
    models: await client.listProviderModels(provider),
    quota: await client.getProviderQuota("codex-cli"),
    readiness: await client.getProviderReadiness(provider)
  };
}

function printSummary(name, result) {
  const context = result.context ?? result;
  const chunks = context.chunks ?? [];
  const first = chunks[0]?.id ?? "none";
  const graphStatus = context.graphContext?.status ?? "n/a";
  console.log(`${name}: chunks=${chunks.length} first=${first} graphStatus=${graphStatus}`);
  if (result.evaluation) {
    console.log(`${name}: eval passRate=${result.evaluation.passRate} passed=${result.evaluation.passedCount}/${result.evaluation.attempted}`);
  }
}

function printProviderSummary(extraction, providerInfo) {
  const providers = providerInfo.providers.map((provider) => provider.id);
  console.log(`providers=${JSON.stringify(providers)}`);
  console.log(`capabilityMatrixItems=${providerInfo.matrix?.items?.length ?? 0}`);
  console.log(`extract status=${extraction.status} provider=${extraction.provider} capability=${extraction.capability}`);
  console.log(`model catalog status=${providerInfo.models?.status}`);
  console.log(`codex quota status=${providerInfo.quota?.status}`);
}
