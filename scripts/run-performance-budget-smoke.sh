#!/usr/bin/env bash
set -euo pipefail

base_url="${VYRAL_BASE_URL:-http://127.0.0.1:5220}"
collection="${VYRAL_PERF_COLLECTION:-vyral-perf-$(date -u +%Y%m%dT%H%M%SZ)}"
graph_collection="${VYRAL_PERF_GRAPH_COLLECTION:-${collection}-graph}"
partition_key="${VYRAL_PERF_PARTITION_KEY:-tenant-perf}"
embedding_field="${VYRAL_PERF_EMBEDDING_FIELD:-contentEmbedding}"
graph_id="${VYRAL_PERF_GRAPH_ID:-perf-graph}"
rerank_provider="${VYRAL_PERF_RERANK_PROVIDER:-local-token-overlap-reranker}"
rerank_candidate_limits="${VYRAL_PERF_RERANK_CANDIDATE_LIMITS:-4 8}"
rerank_max_candidate_chars="${VYRAL_PERF_RERANK_MAX_CANDIDATE_CHARS:-700}"
keep_collections="${VYRAL_PERF_KEEP_COLLECTIONS:-0}"
results_dir="$(realpath -m "${VYRAL_PERF_RESULTS_DIR:-.vyral/benchmarks/performance-budgets}")"
run_id="${VYRAL_PERF_RUN_ID:-$(date -u +%Y%m%dT%H%M%SZ)}"
output_jsonl="$results_dir/perf-budget-$run_id.jsonl"
summary_json="$results_dir/perf-budget-$run_id-summary.json"
summary_md="$results_dir/perf-budget-$run_id-summary.md"

LAST_RESPONSE=""
LAST_DURATION_MS=0
LAST_RESOURCE_JSON='{"available":false,"reason":"not sampled"}'

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

now_ms() {
  date -u +%s%3N
}

sample_process() {
  if [[ -z "${VYRAL_SERVER_PID:-}" || -z "$(command -v ps || true)" ]]; then
    jq -nc '{available:false, reason:"set VYRAL_SERVER_PID to sample server process usage"}'
    return
  fi

  local pcpu=""
  local pmem=""
  local rss=""
  read -r pcpu pmem rss < <(ps -p "$VYRAL_SERVER_PID" -o pcpu=,pmem=,rss= 2>/dev/null || true) || true
  if [[ -z "$pcpu" || -z "$pmem" || -z "$rss" ]]; then
    jq -nc --arg pid "$VYRAL_SERVER_PID" '{available:false, pid:$pid, reason:"process not found"}'
    return
  fi

  jq -nc \
    --arg pid "$VYRAL_SERVER_PID" \
    --arg pcpu "$pcpu" \
    --arg pmem "$pmem" \
    --arg rss "$rss" \
    '{available:true, pid:($pid|tonumber), cpuPercent:($pcpu|tonumber), memoryPercent:($pmem|tonumber), rssKb:($rss|tonumber)}'
}

run_timed() {
  local method="$1"
  local path="$2"
  local body="${3:-}"
  local started
  local ended

  started="$(now_ms)"
  LAST_RESPONSE="$(request "$method" "$path" "$body")"
  ended="$(now_ms)"
  LAST_DURATION_MS="$((ended - started))"
  LAST_RESOURCE_JSON="$(sample_process)"
}

append_event() {
  local event="$1"
  local payload="$2"
  jq -c \
    --arg event "$event" \
    --arg runId "$run_id" \
    --arg collection "$collection" \
    --arg graphCollection "$graph_collection" \
    --arg partitionKey "$partition_key" \
    '. + {event:$event, runId:$runId, collection:$collection, graphCollection:$graphCollection, partitionKey:$partitionKey}' \
    <<<"$payload" >> "$output_jsonl"
}

append_operation() {
  local name="$1"
  local surface="$2"
  local request_shape="$3"
  local response_shape="$4"

  append_event "operation" "$(jq -nc \
    --arg name "$name" \
    --arg surface "$surface" \
    --argjson durationMs "$LAST_DURATION_MS" \
    --argjson resource "$LAST_RESOURCE_JSON" \
    --argjson requestShape "$request_shape" \
    --argjson responseShape "$response_shape" \
    '{
      name: $name,
      surface: $surface,
      durationMs: $durationMs,
      resource: $resource,
      requestShape: $requestShape,
      responseShape: $responseShape
    }')"
}

cleanup() {
  if [[ "$keep_collections" != "1" ]]; then
    request DELETE "/collections/$collection" >/dev/null 2>&1 || true
    request DELETE "/collections/$graph_collection" >/dev/null 2>&1 || true
  fi
}

build_collection_policy() {
  local name="$1"
  jq -n \
    --arg name "$name" \
    --arg embeddingField "$embedding_field" \
    --argjson dimensions "$dimensions" \
    '{
      name: $name,
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
    }'
}

build_ingest_item() {
  local document_id="$1"
  local topic="$2"
  local text="$3"
  jq -n \
    --arg documentId "$document_id" \
    --arg partitionKey "$partition_key" \
    --arg topic "$topic" \
    --arg text "$text" \
    --arg embeddingField "$embedding_field" \
    '{
      documentId: $documentId,
      partitionKey: $partitionKey,
      text: $text,
      embedding: {
        field: $embeddingField,
        purpose: "passage"
      },
      metadata: {
        documentId: $documentId,
        topic: $topic,
        status: "active"
      },
      sourceId: ("perf://" + $documentId),
      sourceKind: "text",
      sourceLabel: $documentId,
      options: {
        chunkChars: 900,
        chunkOverlapChars: 80,
        replaceDocumentChunks: true,
        skipUnchangedChunks: true,
        reuseExistingChunkVectors: true,
        persistManifest: true,
        includeTrace: true
      }
    }'
}

build_search_request() {
  local mode="$1"
  local query="$2"
  jq -n \
    --arg collection "$collection" \
    --arg partitionKey "$partition_key" \
    --arg embeddingField "$embedding_field" \
    --arg mode "$mode" \
    --arg query "$query" \
    '{
      query: $query,
      collections: [$collection],
      partitionKeys: [$partitionKey],
      searchMode: $mode,
      limit: 5,
      includeTrace: true
    }
    + (if $mode == "vector" or $mode == "hybrid" then {
      embedding: {
        field: $embeddingField,
        purpose: "query"
      }
    } else {} end)
    + (if $mode == "lexical" or $mode == "hybrid" then {
      lexical: {
        fields: ["/content/text", "/metadata/topic"],
        fieldBoosts: { "/content/text": 2.0 }
      }
    } else {} end)
    + (if $mode == "hybrid" then {
      hybrid: {
        fusion: "rrf",
        vectorWeight: 0.45,
        lexicalWeight: 0.55
      }
    } else {} end)'
}

build_rerank_search_request() {
  local candidate_limit="$1"
  jq -n \
    --arg collection "$collection" \
    --arg partitionKey "$partition_key" \
    --arg reranker "$rerank_provider" \
    --argjson candidateLimit "$candidate_limit" \
    --argjson maxCandidateChars "$rerank_max_candidate_chars" \
    '{
      query: "retention deletion approval protected records hold",
      collections: [$collection],
      partitionKeys: [$partitionKey],
      searchMode: "lexical",
      lexical: {
        fields: ["/content/text", "/metadata/topic"],
        fieldBoosts: { "/content/text": 2.0 }
      },
      rerank: {
        enabled: true,
        provider: $reranker,
        candidateLimit: $candidateLimit,
        maxCandidateChars: $maxCandidateChars,
        fallbackOnFailure: true,
        timeoutSeconds: 20
      },
      limit: 5,
      includeTrace: true
    }'
}

build_context_request() {
  local include_graph="$1"
  jq -n \
    --arg collection "$collection" \
    --arg graphCollection "$graph_collection" \
    --arg partitionKey "$partition_key" \
    --arg graphId "$graph_id" \
    --argjson includeGraph "$include_graph" \
    '{
      retrieval: {
        query: "retention deletion approval protected records hold",
        collections: [$collection],
        partitionKeys: [$partitionKey],
        searchMode: "lexical",
        lexical: {
          fields: ["/content/text", "/metadata/topic"]
        },
        limit: 4,
        includeTrace: true
      },
      maxChars: 1000,
      maxCharsPerChunk: 450,
      maxCitationsPerChunk: 2,
      contextAssembly: {
        groupByPath: "/metadata/topic",
        defaultMaxChunksPerGroup: 2,
        defaultMaxCharsPerGroup: 700
      },
      includeCitations: true,
      includeContextText: true,
      includeTrace: true
    }
    + (if $includeGraph then {
      graphExpansion: {
        enabled: true,
        collection: $graphCollection,
        graphId: $graphId,
        namespace: "performance",
        partitionKey: $partitionKey,
        seedJsonPointers: ["/id"],
        profile: {
          id: "grounded-support",
          direction: "outgoing",
          maxDepth: 2,
          predicates: ["supports", "mentions"],
          requireSourceGrounding: true,
          limit: 8,
          edgeLimit: 8
        },
        maxRecords: 100,
        maxGraphContextChars: 700,
        maxGraphProvenanceItems: 12,
        fallbackOnFailure: true
      }
    } else {} end)'
}

require_command curl
require_command jq
mkdir -p "$results_dir"
trap cleanup EXIT

health="$(request GET /health)"
dimensions="$(jq -r '.embedding.dimensions' <<<"$health")"
provider="$(jq -r '.embedding.provider' <<<"$health")"
model="$(jq -r '.embedding.modelId' <<<"$health")"

if [[ -z "$dimensions" || "$dimensions" == "null" || "$dimensions" -le 0 ]]; then
  echo "Server health did not report a usable embedding dimension." >&2
  exit 1
fi

append_event "run_started" "$(jq -n \
  --arg baseUrl "$base_url" \
  --arg provider "$provider" \
  --arg model "$model" \
  --argjson dimensions "$dimensions" \
  '{baseUrl:$baseUrl, embedding:{provider:$provider, model:$model, dimensions:$dimensions}}')"

request POST /collections "$(build_collection_policy "$collection")" >/dev/null

retention_text=$'Retention policy guidance. Protected records under a retention hold must not be deleted until an authorized release. After release, deletion requires documented approval, an audit trail, and confirmation that no retention obligation remains.'
trace_text=$'Provider trace guidance. Provider runs should record model configuration, input hash, output hash, duration, cancellation status, provider status, and normalized failure class so local development can diagnose quota, auth, timeout, and policy failures.'
catalog_text=$'Product listing guidance. Product copy optimization should preserve evidence-backed claims, include search terms naturally, avoid unsupported performance promises, and surface claims needing review before copy is adopted.'
context_text=$'RAG context guidance. Retrieved chunks should be deduplicated, grouped when useful, clipped to character budgets, returned with citation ids and source spans, and kept prompt-ready without treating generated output as authority.'

batch="$(jq -n \
  --argjson retention "$(build_ingest_item "retention-policy" "retention" "$retention_text")" \
  --argjson trace "$(build_ingest_item "provider-traces" "provider" "$trace_text")" \
  --argjson catalog "$(build_ingest_item "product-copy" "catalog" "$catalog_text")" \
  --argjson context "$(build_ingest_item "rag-context" "rag" "$context_text")" \
  '{continueOnError:false, items:[$retention, $trace, $catalog, $context]}')"

run_timed POST "/collections/$collection/rag/ingest-text/batch" "$batch"
ingest="$LAST_RESPONSE"
append_operation "rag-ingest-batch" "rag.ingest" \
  "$(jq -nc '{documents:4, chunkChars:900, reuseExistingChunkVectors:true}')" \
  "$(jq -c '{requested, succeeded, failed, chunkCount, vectorGeneratedCount, vectorReusedCount}' <<<"$ingest")"

retention_id="$(jq -r '.items[] | select(.documentId == "retention-policy") | .result.chunks[0].id // ""' <<<"$ingest")"
if [[ -z "$retention_id" ]]; then
  echo "Could not extract retention chunk id from ingestion result." >&2
  exit 1
fi

graph_import="$(jq -n \
  --arg graphId "$graph_id" \
  --arg collection "$collection" \
  --arg graphCollection "$graph_collection" \
  --arg partitionKey "$partition_key" \
  --arg retentionId "$retention_id" \
  '{
    createCollectionIfMissing: true,
    replaceExisting: true,
    envelope: {
      schema: "roman.graph.v1",
      scope: {
        graphId: $graphId,
        namespace: "performance",
        collection: $collection,
        tenantId: $partitionKey,
        partitionKey: $partitionKey
      },
      metadata: {
        purpose: "performance-budget-smoke"
      },
      nodes: [
        {
          id: $retentionId,
          type: "chunk",
          label: "Retention chunk",
          sourceSpans: [{sourceRef:"perf://retention-policy", charStart:0, charEnd:120, unit:"utf16"}]
        },
        {
          id: "concept:retention-hold",
          type: "concept",
          label: "Retention hold",
          sourceSpans: [{sourceRef:"perf://retention-policy", charStart:0, charEnd:120, unit:"utf16"}]
        },
        {
          id: "concept:approval",
          type: "concept",
          label: "Deletion approval",
          sourceSpans: [{sourceRef:"perf://retention-policy", charStart:90, charEnd:190, unit:"utf16"}]
        }
      ],
      edges: [
        {
          id: "edge:retention-hold",
          sourceId: $retentionId,
          targetId: "concept:retention-hold",
          predicate: "supports",
          sourceSpans: [{sourceRef:"perf://retention-policy", charStart:0, charEnd:120, unit:"utf16"}]
        },
        {
          id: "edge:retention-approval",
          sourceId: "concept:retention-hold",
          targetId: "concept:approval",
          predicate: "mentions",
          sourceSpans: [{sourceRef:"perf://retention-policy", charStart:90, charEnd:190, unit:"utf16"}]
        }
      ]
    }
  }')"

run_timed POST "/collections/$graph_collection/graph/import" "$graph_import"
append_operation "graph-import" "graph.import" \
  "$(jq -nc '{nodes:3, edges:2, replaceExisting:true}')" \
  "$(jq -c '{nodeCount, edgeCount, recordCount, policyStatus}' <<<"$LAST_RESPONSE")"

for mode in lexical vector hybrid; do
  run_timed POST /search "$(build_search_request "$mode" "retention deletion approval protected records hold")"
  append_operation "$mode-search" "retrieval.search" \
    "$(jq -nc --arg mode "$mode" '{searchMode:$mode, limit:5}')" \
    "$(jq -c '{resultCount:(.results | length), topId:(.results[0].record.id // null), trace:{durationMs:(.trace.durationMs // 0), candidateCount:(.trace.candidateCount // 0), returnedCount:(.trace.returnedCount // 0), searchMode:(.trace.searchMode // null)}}' <<<"$LAST_RESPONSE")"
done

for candidate_limit in $rerank_candidate_limits; do
  run_timed POST /search "$(build_rerank_search_request "$candidate_limit")"
  append_operation "lexical-rerank-c$candidate_limit" "retrieval.rerank" \
    "$(jq -nc --arg provider "$rerank_provider" --argjson candidateLimit "$candidate_limit" --argjson maxCandidateChars "$rerank_max_candidate_chars" '{provider:$provider, candidateLimit:$candidateLimit, maxCandidateChars:$maxCandidateChars}')" \
    "$(jq -c '{resultCount:(.results | length), topId:(.results[0].record.id // null), rerankFallbackApplied:(.trace.rerankFallbackApplied // false), rerankProviderStatus:(.trace.rerankProviderStatus // null), rerankPayloadBytes:(.trace.rerankProviderPayloadBytes // 0)}' <<<"$LAST_RESPONSE")"
done

graph_traverse="$(jq -n \
  --arg graphId "$graph_id" \
  --arg partitionKey "$partition_key" \
  --arg retentionId "$retention_id" \
  '{
    graphId: $graphId,
    namespace: "performance",
    partitionKey: $partitionKey,
    startNodeIds: [$retentionId],
    maxRecords: 100,
    profile: {
      id: "grounded-support",
      direction: "outgoing",
      maxDepth: 2,
      predicates: ["supports", "mentions"],
      requireSourceGrounding: true,
      limit: 8,
      edgeLimit: 8
    }
  }')"
run_timed POST "/collections/$graph_collection/graph/traverse" "$graph_traverse"
append_operation "graph-traverse-depth2-edge8" "graph.traverse" \
  "$(jq -nc '{maxDepth:2, edgeLimit:8, limit:8, maxRecords:100, requireSourceGrounding:true}')" \
  "$(jq -c '{nodeCount, edgeCount, sourceRecordCount, sourceTruncated}' <<<"$LAST_RESPONSE")"

run_timed POST /rag/context "$(build_context_request false)"
append_operation "rag-context-compact" "rag.context" \
  "$(jq -nc '{maxChars:1000, maxCharsPerChunk:450, maxCitationsPerChunk:2, grouped:true}')" \
  "$(jq -c '{chunkCount:(.chunks | length), citationCount:(.citations | length), contextTextLength:(.contextText | length), trace:{retrievalDurationMs:(.trace.retrieval.durationMs // 0), budgetExhausted:(.trace.budgetExhausted // false), droppedForBudget:(.trace.droppedForBudget // 0), groupCount:(.trace.groupCount // 0)}}' <<<"$LAST_RESPONSE")"

run_timed POST /rag/context "$(build_context_request true)"
append_operation "graphrag-context-compact" "rag.context.graph" \
  "$(jq -nc '{maxChars:1000, maxCharsPerChunk:450, graphMaxChars:700, graphMaxProvenanceItems:12, traversal:{maxDepth:2, edgeLimit:8}}')" \
  "$(jq -c '{chunkCount:(.chunks | length), citationCount:(.citations | length), contextTextLength:(.contextText | length), graphNodeCount:(.graphContext.nodeCount // 0), graphEdgeCount:(.graphContext.edgeCount // 0), graphContextTextLength:(.graphContext.contextText | length // 0), trace:{retrievalDurationMs:(.trace.retrieval.durationMs // 0), budgetExhausted:(.trace.budgetExhausted // false), graphExpansion:{status:(.trace.graphExpansion.status // null), seedCount:(.trace.graphExpansion.seedCount // 0), nodeCount:(.trace.graphExpansion.nodeCount // 0), edgeCount:(.trace.graphExpansion.edgeCount // 0), contextTextChars:(.trace.graphExpansion.contextTextChars // 0), contextTextTruncated:(.trace.graphExpansion.contextTextTruncated // false), provenanceCount:(.trace.graphExpansion.provenanceCount // 0)}}}' <<<"$LAST_RESPONSE")"

append_event "run_finished" "$(jq -n --argjson resource "$(sample_process)" '{resource:$resource}')"

summary="$(jq -s \
  --arg outputFile "$output_jsonl" \
  --arg summaryMarkdown "$summary_md" \
  --arg provider "$provider" \
  --arg model "$model" \
  --argjson dimensions "$dimensions" \
  '{
    outputFile: $outputFile,
    summaryMarkdown: $summaryMarkdown,
    embedding: {
      provider: $provider,
      model: $model,
      dimensions: $dimensions
    },
    operations: [
      .[]
      | select(.event == "operation")
      | {
          name,
          surface,
          durationMs,
          requestShape,
          responseShape,
          resource
        }
    ]
  }' "$output_jsonl")"

printf '%s\n' "$summary"
printf '%s\n' "$summary" > "$summary_json"

{
  echo "# Vyral Performance Budget Smoke $run_id"
  echo
  echo "- Base URL: $base_url"
  echo "- Collection: $collection"
  echo "- Graph collection: $graph_collection"
  echo "- Embedding: $provider ($model), dimensions=$dimensions"
  echo "- Raw events: $output_jsonl"
  echo
  echo "| Operation | Surface | Time ms | Result Count | Top Id | RSS KB |"
  echo "| --- | --- | ---: | ---: | --- | ---: |"
  jq -r '
    .operations[]
    | "| \(.name) | \(.surface) | \(.durationMs) | \(.responseShape.resultCount // .responseShape.chunkCount // .responseShape.nodeCount // "") | \(.responseShape.topId // "") | \(.resource.rssKb // "") |"
  ' <<<"$summary"
  echo
  echo "Use this as a smoke budget, not a quality benchmark. For retrieval quality and rerank comparisons, run scripts/run-rag-evaluation-workload.sh."
} > "$summary_md"
