#!/usr/bin/env bash
set -euo pipefail

base_url="${VYRAL_BASE_URL:-http://127.0.0.1:5220}"
collection="${VYRAL_SMOKE_COLLECTION:-vyral-beta-smoke-$(date -u +%Y%m%dT%H%M%SZ)}"
partition_key="${VYRAL_SMOKE_PARTITION_KEY:-tenant-smoke}"
embedding_field="${VYRAL_SMOKE_EMBEDDING_FIELD:-contentEmbedding}"
keep_collection="${VYRAL_SMOKE_KEEP_COLLECTION:-0}"

require_command() {
  local name="$1"
  if ! command -v "$name" >/dev/null 2>&1; then
    echo "Missing required command: $name" >&2
    exit 127
  fi
}

auth_args() {
  if [[ -n "${VYRAL_API_KEY:-}" ]]; then
    printf '%s\n' -H "X-Vyral-Api-Key: ${VYRAL_API_KEY}"
  fi
}

request() {
  local method="$1"
  local path="$2"
  local body="${3:-}"
  local args=(-fsS -X "$method" "$base_url$path")

  while IFS= read -r arg; do
    [[ -n "$arg" ]] && args+=("$arg")
  done < <(auth_args)

  if [[ -n "$body" ]]; then
    args+=(-H "Content-Type: application/json" --data-binary "$body")
  fi

  curl "${args[@]}"
}

cleanup() {
  if [[ "$keep_collection" != "1" ]]; then
    request DELETE "/collections/$collection" >/dev/null 2>&1 || true
  fi
}

require_command curl
require_command jq
trap cleanup EXIT

health="$(request GET /health)"
dimensions="$(jq -r '.embedding.dimensions' <<<"$health")"
provider="$(jq -r '.embedding.provider' <<<"$health")"
model="$(jq -r '.embedding.modelId' <<<"$health")"

if [[ -z "$dimensions" || "$dimensions" == "null" || "$dimensions" -le 0 ]]; then
  echo "Server health did not report a usable embedding dimension." >&2
  exit 1
fi

echo "Vyral smoke target: $base_url"
echo "Embedding provider: $provider ($model), dimensions=$dimensions"
echo "Collection: $collection"

policy="$(jq -n \
  --arg collection "$collection" \
  --arg embeddingField "$embedding_field" \
  --argjson dimensions "$dimensions" \
  '{
    name: $collection,
    indexedMetadata: [
      "/metadata/documentId",
      "/metadata/topic",
      "/metadata/status",
      "/type"
    ],
    vectorPolicies: [
      {
        name: $embeddingField,
        path: ("/vectors/" + $embeddingField + "/values"),
        dimensions: $dimensions,
        datatype: "float32",
        distanceFunction: "cosine",
        indexType: "flat"
      }
    ]
  }')"
request POST /collections "$policy" >/dev/null

smoke_text=$'Retention policy guidance: protected records remain under a retention hold until an authorized release. After release, deletion requires recorded approval and an audit record.\n\nTravel reimbursement guidance: business trip expenses require receipts, business purpose, manager approval, and cost center assignment.\n\nProvider trace guidance: provider runs record model configuration, input hash, output hash, latency, cancellation status, and normalized failure class.\n\nRAG context guidance: retrieved chunks should be deduplicated, bounded by token budget, and returned with source labels and citation spans.'

ingest_request="$(jq -n \
  --arg documentId "smoke-rag-doc" \
  --arg partitionKey "$partition_key" \
  --arg text "$smoke_text" \
  --arg embeddingField "$embedding_field" \
  '{
    documentId: $documentId,
    partitionKey: $partitionKey,
    text: $text,
    embeddingField: $embeddingField,
    metadata: {
      status: "active",
      topic: "smoke"
    },
    sourceId: "smoke-source",
    sourceKind: "text",
    sourceLabel: "Smoke test source",
    chunkChars: 260,
    chunkOverlapChars: 40,
    replaceDocumentChunks: true,
    skipUnchangedChunks: true,
    persistManifest: true,
    includeTrace: true
  }')"
ingest="$(request POST "/collections/$collection/rag/ingest-text" "$ingest_request")"

search_request="$(jq -n \
  --arg collection "$collection" \
  --arg partitionKey "$partition_key" \
  --arg embeddingField "$embedding_field" \
  '{
    query: "when can protected records be deleted after a retention hold",
    collections: [$collection],
    partitionKeys: [$partitionKey],
    embeddingField: $embeddingField,
    searchMode: "hybrid",
    lexical: {
      fields: ["/content/text", "/metadata/topic", "/metadata/status"],
      fieldBoosts: { "/content/text": 2.0 }
    },
    hybrid: {
      fusion: "rrf",
      vectorWeight: 0.45,
      lexicalWeight: 0.55
    },
    limit: 3,
    includeTrace: true
  }')"
search="$(request POST /search "$search_request")"

context_request="$(jq -n \
  --arg collection "$collection" \
  --arg partitionKey "$partition_key" \
  --arg embeddingField "$embedding_field" \
  '{
    query: "assemble RAG context for retention deletion approval",
    collections: [$collection],
    partitionKeys: [$partitionKey],
    embeddingField: $embeddingField,
    searchMode: "hybrid",
    lexical: {
      fields: ["/content/text", "/metadata/topic"]
    },
    hybrid: {
      fusion: "rrf",
      vectorWeight: 0.45,
      lexicalWeight: 0.55
    },
    limit: 3,
    maxChars: 1200,
    maxCharsPerChunk: 500,
    includeCitations: true,
    includeContextText: true,
    includeTrace: true
  }')"
context="$(request POST /rag/context "$context_request")"

search_count="$(jq '.results | length' <<<"$search")"
context_count="$(jq '.chunks | length' <<<"$context")"
top_id="$(jq -r '.results[0].record.id // ""' <<<"$search")"
top_score="$(jq -r '.results[0].score // 0' <<<"$search")"
context_hash="$(jq -r '.contextTextHash // ""' <<<"$context")"

if [[ "$search_count" -lt 1 ]]; then
  echo "Smoke search returned no results." >&2
  exit 1
fi

if [[ "$context_count" -lt 1 ]]; then
  echo "Smoke context assembly returned no chunks." >&2
  exit 1
fi

jq -n \
  --arg collection "$collection" \
  --arg provider "$provider" \
  --arg model "$model" \
  --arg topId "$top_id" \
  --argjson topScore "$top_score" \
  --arg contextHash "$context_hash" \
  --argjson ingest "$ingest" \
  --argjson searchCount "$search_count" \
  --argjson contextCount "$context_count" \
  '{
    status: "ok",
    collection: $collection,
    embedding: {
      provider: $provider,
      model: $model
    },
    ingestion: {
      chunkCount: $ingest.chunkCount,
      createdCount: $ingest.createdCount,
      reusedCount: $ingest.reusedCount,
      vectorGeneratedCount: $ingest.vectorGeneratedCount,
      manifestAction: $ingest.manifestAction,
      planHash: $ingest.planHash
    },
    retrieval: {
      resultCount: $searchCount,
      topId: $topId,
      topScore: $topScore
    },
    context: {
      chunkCount: $contextCount,
      contextTextHash: $contextHash
    }
  }'
