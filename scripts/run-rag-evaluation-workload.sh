#!/usr/bin/env bash
set -euo pipefail

base_url="${VYRAL_BASE_URL:-http://127.0.0.1:5220}"
collection="${VYRAL_EVAL_COLLECTION:-vyral-rag-eval-$(date -u +%Y%m%dT%H%M%SZ)}"
partition_key="${VYRAL_EVAL_PARTITION_KEY:-tenant-eval}"
embedding_field="${VYRAL_EVAL_EMBEDDING_FIELD:-contentEmbedding}"
keep_collection="${VYRAL_EVAL_KEEP_COLLECTION:-0}"
limit="${VYRAL_EVAL_LIMIT:-6}"
k="${VYRAL_EVAL_K:-5}"
include_trace="${VYRAL_EVAL_INCLUDE_TRACE:-false}"
rerank_provider="${VYRAL_EVAL_RERANK_PROVIDER:-}"
rerank_candidate_limit="${VYRAL_EVAL_RERANK_CANDIDATE_LIMIT:-40}"
rerank_candidate_limits="${VYRAL_EVAL_RERANK_CANDIDATE_LIMITS:-$rerank_candidate_limit}"
rerank_max_candidate_chars="${VYRAL_EVAL_RERANK_MAX_CANDIDATE_CHARS:-2000}"
rerank_max_candidate_chars_list="${VYRAL_EVAL_RERANK_MAX_CANDIDATE_CHARS_LIST:-$rerank_max_candidate_chars}"
rerank_timeout_seconds="${VYRAL_EVAL_RERANK_TIMEOUT_SECONDS:-60}"
rerank_content_field="${VYRAL_EVAL_RERANK_CONTENT_FIELD:-text}"
results_dir="$(realpath -m "${VYRAL_EVAL_RESULTS_DIR:-.vyral/benchmarks/retrieval-quality}")"
run_id="${VYRAL_EVAL_RUN_ID:-$(date -u +%Y%m%dT%H%M%SZ)}"
output="$results_dir/rag-eval-$run_id.jsonl"
summary_json="$results_dir/rag-eval-$run_id-summary.json"
summary_csv="$results_dir/rag-eval-$run_id-summary.csv"
summary_md="$results_dir/rag-eval-$run_id-summary.md"

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

json_bool() {
  case "${1,,}" in
    1|true|yes|on)
      printf 'true'
      ;;
    *)
      printf 'false'
      ;;
  esac
}

duration_ms() {
  local start="$1"
  local end="$2"
  printf '%s' "$((end - start))"
}

sample_process() {
  if [[ -z "${VYRAL_SERVER_PID:-}" ]] || ! command -v ps >/dev/null 2>&1; then
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

append_event() {
  local event="$1"
  local payload="$2"
  jq -c \
    --arg event "$event" \
    --arg runId "$run_id" \
    --arg collection "$collection" \
    --arg partitionKey "$partition_key" \
    '. + {event:$event, runId:$runId, collection:$collection, partitionKey:$partitionKey}' \
    <<<"$payload" >> "$output"
}

cleanup() {
  if [[ "$keep_collection" != "1" ]]; then
    request DELETE "/collections/$collection" >/dev/null 2>&1 || true
  fi
}

build_evaluation_request() {
  local mode="$1"
  local reranker="$2"
  local candidate_limit="$3"
  local max_candidate_chars="$4"
  jq -n \
    --arg collection "$collection" \
    --arg partitionKey "$partition_key" \
    --arg embeddingField "$embedding_field" \
    --arg mode "$mode" \
    --arg reranker "$reranker" \
    --arg rerankContentField "$rerank_content_field" \
    --argjson limit "$limit" \
    --argjson k "$k" \
    --argjson includeTrace "$(json_bool "$include_trace")" \
    --argjson rerankCandidateLimit "$candidate_limit" \
    --argjson rerankMaxCandidateChars "$max_candidate_chars" \
    --argjson rerankTimeoutSeconds "$rerank_timeout_seconds" \
    --arg retentionId "$retention_id" \
    --arg travelId "$travel_id" \
    --arg traceId "$trace_id" \
    --arg contextId "$context_id" \
    --arg embeddingId "$embedding_id" \
    --arg incidentId "$incident_id" \
    --arg catalogId "$catalog_id" \
    --arg invoiceId "$invoice_id" \
    '
    def lexical:
      {
        fields: ["/content/text", "/metadata/title", "/metadata/topic", "/metadata/status"],
        fieldBoosts: {
          "/content/text": 2.0,
          "/metadata/title": 1.5,
          "/metadata/topic": 1.2
        },
        scanLimit: 1000,
        scoring: "bm25",
        prefixMatching: true,
        prefixMinChars: 3
      };
    def mode_options:
      if $mode == "lexical" then
        { lexical: lexical }
      elif $mode == "hybrid" then
        {
          lexical: lexical,
          hybrid: {
            fusion: "rrf",
            vectorWeight: 0.45,
            lexicalWeight: 0.55,
            candidateMultiplier: 8,
            rrfK: 60
          }
        }
      else
        {}
      end;
    def rerank_options:
      if $reranker == "" then
        {}
      else
        {
          rerank: {
            enabled: true,
            provider: $reranker,
            candidateLimit: $rerankCandidateLimit,
            contentField: $rerankContentField,
            maxCandidateChars: $rerankMaxCandidateChars,
            timeoutSeconds: $rerankTimeoutSeconds
          }
        }
      end;
    def retrieval($query):
      ({
        query: $query,
        collections: [$collection],
        partitionKeys: [$partitionKey],
        embeddingField: $embeddingField,
        searchMode: $mode,
        limit: $limit,
        filter: {
          path: "/metadata/status",
          op: "eq",
          value: "active"
        },
        includeTrace: $includeTrace
      } + mode_options + rerank_options);
    def expected($id): [{ id: $id, partitionKey: $partitionKey, collection: $collection }];
    def hard_negative($id; $reason): [{ id: $id, partitionKey: $partitionKey, collection: $collection, reason: $reason }];
    {
      defaultK: $k,
      includeTopResults: true,
      continueOnError: true,
      cases: [
        {
          name: "retention-hold-release",
          k: $k,
          request: retrieval("when can protected records under a retention hold be deleted after release"),
          expected: expected($retentionId),
          hardNegatives: hard_negative($invoiceId; "finance policy decoy"),
          metadata: { topic: "records" }
        },
        {
          name: "travel-reimbursement-receipts",
          k: $k,
          request: retrieval("which receipts approvals and cost center are needed for employee travel reimbursement"),
          expected: expected($travelId),
          hardNegatives: hard_negative($invoiceId; "finance policy near-neighbor"),
          metadata: { topic: "finance" }
        },
        {
          name: "provider-trace-evidence",
          k: $k,
          request: retrieval("what should provider run traces store for model calls and failure classification"),
          expected: expected($traceId),
          hardNegatives: hard_negative($contextId; "RAG design near-neighbor"),
          metadata: { topic: "provider-governance" }
        },
        {
          name: "rag-context-budget-citations",
          k: $k,
          request: retrieval("how should retrieved chunks be assembled into citation context under a token budget"),
          expected: expected($contextId),
          hardNegatives: hard_negative($traceId; "provider-governance near-neighbor"),
          metadata: { topic: "rag" }
        },
        {
          name: "embedding-provider-cpu-fallback",
          k: $k,
          request: retrieval("what local embedding provider setup falls back from CUDA to CPU and preserves dimensions"),
          expected: expected($embeddingId),
          hardNegatives: hard_negative($catalogId; "semantic retrieval use-case decoy"),
          metadata: { topic: "embeddings" }
        },
        {
          name: "incident-runbook-escalation",
          k: $k,
          request: retrieval("how should on call handle a severity two incident rollback and escalation"),
          expected: expected($incidentId),
          hardNegatives: hard_negative($embeddingId; "operations runbook near-neighbor"),
          metadata: { topic: "operations" }
        }
      ]
    }'
}

run_mode() {
  local mode="$1"
  local label="$2"
  local reranker="${3:-}"
  local candidate_limit="${4:-$rerank_candidate_limit}"
  local max_candidate_chars="${5:-$rerank_max_candidate_chars}"
  local request_body
  local response
  local start
  local end
  local resource

  request_body="$(build_evaluation_request "$mode" "$reranker" "$candidate_limit" "$max_candidate_chars")"
  start="$(now_ms)"
  response="$(request POST /retrieval/evaluate "$request_body")"
  end="$(now_ms)"
  resource="$(sample_process)"

  append_event "retrieval_evaluation" "$(jq -n \
    --arg mode "$label" \
    --arg searchMode "$mode" \
    --arg reranker "$reranker" \
    --argjson candidateLimit "$candidate_limit" \
    --argjson maxCandidateChars "$max_candidate_chars" \
    --argjson durationMs "$(duration_ms "$start" "$end")" \
    --argjson request "$request_body" \
    --argjson evaluation "$response" \
    --argjson resource "$resource" \
    '{
      mode: $mode,
      searchMode: $searchMode,
      rerankProvider: $reranker,
      rerankCandidateLimit: $candidateLimit,
      rerankMaxCandidateChars: $maxCandidateChars,
      durationMs: $durationMs,
      request: $request,
      evaluation: $evaluation,
      resource: $resource
    }')"
}

require_command curl
require_command date
require_command jq
require_command realpath
trap cleanup EXIT

mkdir -p "$results_dir"

health="$(request GET /health)"
dimensions="$(jq -r '.embedding.dimensions' <<<"$health")"
provider="$(jq -r '.embedding.provider' <<<"$health")"
model="$(jq -r '.embedding.modelId' <<<"$health")"

if [[ -z "$dimensions" || "$dimensions" == "null" || "$dimensions" -le 0 ]]; then
  echo "Server health did not report a usable embedding dimension." >&2
  exit 1
fi

echo "Vyral RAG evaluation target: $base_url"
echo "Embedding provider: $provider ($model), dimensions=$dimensions"
echo "Collection: $collection"
echo "Results: $output"
echo "Summaries: $summary_json, $summary_csv, $summary_md"

append_event "run_started" "$(jq -n \
  --arg baseUrl "$base_url" \
  --arg embeddingField "$embedding_field" \
  --argjson health "$health" \
  '{
    baseUrl: $baseUrl,
    embeddingField: $embeddingField,
    health: $health
  }')"

policy="$(jq -n \
  --arg collection "$collection" \
  --arg embeddingField "$embedding_field" \
  --argjson dimensions "$dimensions" \
  '{
    name: $collection,
    indexedMetadata: [
      "/metadata/documentId",
      "/metadata/title",
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

retention_text=$'Retention hold policy for protected records. While a retention hold is active, archived source material and review notes must not be deleted even if their ordinary retention period has expired. An authorized release is required before deletion. After release, the records team records the approval, verifies no related retention notice remains active, and stores an audit entry.'
travel_text=$'Travel reimbursement policy. Employees submit airfare, lodging, mileage, meals, and conference expenses with receipts, business purpose, trip dates, cost center, and manager approval. Missing receipts require a signed exception. Personal upgrades are not reimbursable. Finance rejects expense reports that omit the project code or approval chain.'
trace_text=$'Provider trace governance. A provider run trace stores provider identity, capability, model id, prompt or input hash, output hash, config hash, mode policy, cost, latency, cancellation status, retry count, and normalized failure class. AI output is treated as proposal evidence until the caller validates and adopts it. Tool execution remains caller-owned.'
context_text=$'RAG context assembly guidance. Retrieved chunks should be deduplicated, grouped by authority or source when useful, clipped to a token or character budget, and returned with citation ids, source labels, source spans, chunk hashes, and retrieval diagnostics. The context package is prompt-ready but does not generate the final answer.'
embedding_text=$'Embedding provider operations. Local semantic embeddings can run through ONNX Runtime with model files under an ignored model directory. CPU profiles should set conservative intra-op threads. CUDA-preferred profiles may use GPU acceleration when native libraries are available, but must cleanly fall back to CPU unless the caller requested CUDA-required behavior. Vector dimensions must match collection policy.'
incident_text=$'Incident runbook. For a severity two service incident, the on-call engineer acknowledges the page, opens the incident channel, captures the start time and impact, checks recent deployments, rolls back the suspected change when blast radius is high, escalates to the owning service team, and posts customer-facing status updates on the agreed cadence.'
catalog_text=$'Product similarity notes. Catalog retrieval can use vectors over titles, descriptions, facets, and customer-visible attributes to find substitute products, duplicate listings, or related accessories. Metadata filters for brand, price band, region, inventory state, and compliance flags keep recommendations within the permitted catalog segment.'
invoice_text=$'Invoice payment policy. Vendor invoices require a purchase order, matching receipt record, tax fields, payment terms, and approval from the budget owner. Standard payment terms are net thirty unless the contract states otherwise. Finance holds invoices with unmatched purchase orders or conflicting remittance details.'

ingest_request="$(jq -n \
  --arg partitionKey "$partition_key" \
  --arg embeddingField "$embedding_field" \
  --arg retentionText "$retention_text" \
  --arg travelText "$travel_text" \
  --arg traceText "$trace_text" \
  --arg contextText "$context_text" \
  --arg embeddingText "$embedding_text" \
  --arg incidentText "$incident_text" \
  --arg catalogText "$catalog_text" \
  --arg invoiceText "$invoice_text" \
  '{
    continueOnError: false,
    items: [
      {
        documentId: "retention-hold-policy",
        idPrefix: "eval-retention-hold-policy",
        partitionKey: $partitionKey,
        text: $retentionText,
        embeddingField: $embeddingField,
        chunkChars: 1600,
        chunkOverlapChars: 120,
        replaceDocumentChunks: true,
        skipUnchangedChunks: true,
        persistManifest: true,
        sourceId: "eval-retention-hold-policy",
        sourceKind: "policy",
        sourceLabel: "Retention hold policy",
        metadata: { title: "Retention hold policy", topic: "records", status: "active" }
      },
      {
        documentId: "travel-reimbursement-policy",
        idPrefix: "eval-travel-reimbursement-policy",
        partitionKey: $partitionKey,
        text: $travelText,
        embeddingField: $embeddingField,
        chunkChars: 1600,
        chunkOverlapChars: 120,
        replaceDocumentChunks: true,
        skipUnchangedChunks: true,
        persistManifest: true,
        sourceId: "eval-travel-reimbursement-policy",
        sourceKind: "policy",
        sourceLabel: "Travel reimbursement policy",
        metadata: { title: "Travel reimbursement policy", topic: "finance", status: "active" }
      },
      {
        documentId: "provider-trace-governance",
        idPrefix: "eval-provider-trace-governance",
        partitionKey: $partitionKey,
        text: $traceText,
        embeddingField: $embeddingField,
        chunkChars: 1600,
        chunkOverlapChars: 120,
        replaceDocumentChunks: true,
        skipUnchangedChunks: true,
        persistManifest: true,
        sourceId: "eval-provider-trace-governance",
        sourceKind: "design",
        sourceLabel: "Provider trace governance",
        metadata: { title: "Provider trace governance", topic: "provider-governance", status: "active" }
      },
      {
        documentId: "rag-context-assembly",
        idPrefix: "eval-rag-context-assembly",
        partitionKey: $partitionKey,
        text: $contextText,
        embeddingField: $embeddingField,
        chunkChars: 1600,
        chunkOverlapChars: 120,
        replaceDocumentChunks: true,
        skipUnchangedChunks: true,
        persistManifest: true,
        sourceId: "eval-rag-context-assembly",
        sourceKind: "design",
        sourceLabel: "RAG context assembly guidance",
        metadata: { title: "RAG context assembly guidance", topic: "rag", status: "active" }
      },
      {
        documentId: "embedding-provider-operations",
        idPrefix: "eval-embedding-provider-operations",
        partitionKey: $partitionKey,
        text: $embeddingText,
        embeddingField: $embeddingField,
        chunkChars: 1600,
        chunkOverlapChars: 120,
        replaceDocumentChunks: true,
        skipUnchangedChunks: true,
        persistManifest: true,
        sourceId: "eval-embedding-provider-operations",
        sourceKind: "runbook",
        sourceLabel: "Embedding provider operations",
        metadata: { title: "Embedding provider operations", topic: "embeddings", status: "active" }
      },
      {
        documentId: "incident-runbook",
        idPrefix: "eval-incident-runbook",
        partitionKey: $partitionKey,
        text: $incidentText,
        embeddingField: $embeddingField,
        chunkChars: 1600,
        chunkOverlapChars: 120,
        replaceDocumentChunks: true,
        skipUnchangedChunks: true,
        persistManifest: true,
        sourceId: "eval-incident-runbook",
        sourceKind: "runbook",
        sourceLabel: "Incident runbook",
        metadata: { title: "Incident runbook", topic: "operations", status: "active" }
      },
      {
        documentId: "product-similarity-notes",
        idPrefix: "eval-product-similarity-notes",
        partitionKey: $partitionKey,
        text: $catalogText,
        embeddingField: $embeddingField,
        chunkChars: 1600,
        chunkOverlapChars: 120,
        replaceDocumentChunks: true,
        skipUnchangedChunks: true,
        persistManifest: true,
        sourceId: "eval-product-similarity-notes",
        sourceKind: "notes",
        sourceLabel: "Product similarity notes",
        metadata: { title: "Product similarity notes", topic: "catalog", status: "active" }
      },
      {
        documentId: "invoice-payment-policy",
        idPrefix: "eval-invoice-payment-policy",
        partitionKey: $partitionKey,
        text: $invoiceText,
        embeddingField: $embeddingField,
        chunkChars: 1600,
        chunkOverlapChars: 120,
        replaceDocumentChunks: true,
        skipUnchangedChunks: true,
        persistManifest: true,
        sourceId: "eval-invoice-payment-policy",
        sourceKind: "policy",
        sourceLabel: "Invoice payment policy",
        metadata: { title: "Invoice payment policy", topic: "finance", status: "active" }
      }
    ]
  }')"

ingest_start="$(now_ms)"
ingest="$(request POST "/collections/$collection/rag/ingest-text/batch" "$ingest_request")"
ingest_end="$(now_ms)"

if [[ "$(jq -r '.failed' <<<"$ingest")" != "0" ]]; then
  echo "RAG corpus ingestion failed:" >&2
  jq '.items[] | select(.status != "succeeded")' <<<"$ingest" >&2
  exit 1
fi

append_event "corpus_ingested" "$(jq -n \
  --argjson durationMs "$(duration_ms "$ingest_start" "$ingest_end")" \
  --argjson request "$ingest_request" \
  --argjson ingestion "$ingest" \
  --argjson resource "$(sample_process)" \
  '{
    durationMs: $durationMs,
    request: $request,
    ingestion: $ingestion,
    resource: $resource
  }')"

chunk_id_for_doc() {
  local document_id="$1"
  jq -r --arg documentId "$document_id" '
    .items[]
    | select(.documentId == $documentId and .status == "succeeded")
    | .result.chunks[0].id // empty
  ' <<<"$ingest"
}

retention_id="$(chunk_id_for_doc "retention-hold-policy")"
travel_id="$(chunk_id_for_doc "travel-reimbursement-policy")"
trace_id="$(chunk_id_for_doc "provider-trace-governance")"
context_id="$(chunk_id_for_doc "rag-context-assembly")"
embedding_id="$(chunk_id_for_doc "embedding-provider-operations")"
incident_id="$(chunk_id_for_doc "incident-runbook")"
catalog_id="$(chunk_id_for_doc "product-similarity-notes")"
invoice_id="$(chunk_id_for_doc "invoice-payment-policy")"

for expected_id in "$retention_id" "$travel_id" "$trace_id" "$context_id" "$embedding_id" "$incident_id" "$catalog_id" "$invoice_id"; do
  if [[ -z "$expected_id" ]]; then
    echo "Could not extract one or more expected or hard-negative chunk ids from ingestion response." >&2
    exit 1
  fi
done

run_mode vector vector
run_mode lexical lexical
run_mode hybrid hybrid

if [[ -n "$rerank_provider" ]]; then
  for candidate_limit in $rerank_candidate_limits; do
    for max_candidate_chars in $rerank_max_candidate_chars_list; do
      run_mode lexical "lexical-rerank-c${candidate_limit}-chars${max_candidate_chars}" "$rerank_provider" "$candidate_limit" "$max_candidate_chars"
      run_mode hybrid "hybrid-rerank-c${candidate_limit}-chars${max_candidate_chars}" "$rerank_provider" "$candidate_limit" "$max_candidate_chars"
    done
  done
fi

append_event "run_finished" "$(jq -n \
  --argjson resource "$(sample_process)" \
  '{resource: $resource}')"

summary="$(jq -s \
  --arg outputFile "$output" \
  --arg provider "$provider" \
  --arg model "$model" \
  --argjson dimensions "$dimensions" \
  '{
    outputFile: $outputFile,
    embedding: {
      provider: $provider,
      model: $model,
      dimensions: $dimensions
    },
    modes: [
      .[]
      | select(.event == "retrieval_evaluation")
      | {
          mode,
          searchMode,
          rerankProvider,
          rerankCandidateLimit,
          rerankMaxCandidateChars,
          durationMs,
          hitRate: .evaluation.hitRate,
          meanReciprocalRank: .evaluation.meanReciprocalRank,
          meanPrecisionAtK: .evaluation.meanPrecisionAtK,
          meanRecallAtK: .evaluation.meanRecallAtK,
          meanNdcgAtK: .evaluation.meanNdcgAtK,
          hardNegativeHitRate: .evaluation.hardNegativeHitRate,
          meanHardNegativeRateAtK: .evaluation.meanHardNegativeRateAtK,
          rerankCaseCount: .evaluation.rerankCaseCount,
          rerankFallbackCaseCount: .evaluation.rerankFallbackCaseCount,
          rerankFallbackRate: .evaluation.rerankFallbackRate,
          failed: .evaluation.failed,
          resource: .resource
        }
    ]
  }' "$output")"

printf '%s\n' "$summary"
printf '%s\n' "$summary" > "$summary_json"

jq -r '
  ["mode","searchMode","rerankProvider","rerankCandidateLimit","rerankMaxCandidateChars","hitRate","meanReciprocalRank","meanPrecisionAtK","meanRecallAtK","meanNdcgAtK","hardNegativeHitRate","meanHardNegativeRateAtK","rerankCaseCount","rerankFallbackCaseCount","rerankFallbackRate","failed","durationMs","rssKb"] | @csv,
  (.modes[] | [
    .mode,
    .searchMode,
    .rerankProvider,
    (.rerankCandidateLimit // ""),
    (.rerankMaxCandidateChars // ""),
    .hitRate,
    .meanReciprocalRank,
    .meanPrecisionAtK,
    .meanRecallAtK,
    .meanNdcgAtK,
    .hardNegativeHitRate,
    .meanHardNegativeRateAtK,
    .rerankCaseCount,
    .rerankFallbackCaseCount,
    .rerankFallbackRate,
    .failed,
    .durationMs,
    (.resource.rssKb // "")
  ] | @csv)
' <<<"$summary" > "$summary_csv"

{
  echo "# Vyral Retrieval Evaluation $run_id"
  echo
  echo "- Base URL: $base_url"
  echo "- Embedding: $provider ($model), dimensions=$dimensions"
  echo "- Collection: $collection"
  echo "- Raw events: $output"
  echo
  echo "| Mode | Search | Reranker | Candidates | Chars | Hit | MRR | NDCG | Hard-neg | Fallback | Failed | Time ms | RSS KB |"
  echo "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"
  jq -r '
    def fmt: if . == null then "" elif type == "number" then (. * 1000 | round | . / 1000 | tostring) else tostring end;
    .modes[]
    | "| \(.mode) | \(.searchMode) | \(.rerankProvider // "") | \(.rerankCandidateLimit // "") | \(.rerankMaxCandidateChars // "") | \(.hitRate | fmt) | \(.meanReciprocalRank | fmt) | \(.meanNdcgAtK | fmt) | \(.hardNegativeHitRate | fmt) | \(.rerankFallbackCaseCount // 0)/\(.rerankCaseCount // 0) | \(.failed) | \(.durationMs) | \(.resource.rssKb // "") |"
  ' <<<"$summary"
} > "$summary_md"

if [[ -n "${VYRAL_EVAL_MIN_HIT_RATE:-}" ]]; then
  below_threshold="$(jq --argjson minHitRate "$VYRAL_EVAL_MIN_HIT_RATE" '
    [.modes[] | select(.hitRate < $minHitRate)] | length
  ' <<<"$summary")"
  if [[ "$below_threshold" -gt 0 ]]; then
    echo "One or more retrieval modes fell below VYRAL_EVAL_MIN_HIT_RATE=$VYRAL_EVAL_MIN_HIT_RATE." >&2
    exit 1
  fi
fi

if [[ "${VYRAL_EVAL_REQUIRE_HITS:-0}" == "1" ]]; then
  zero_hit_modes="$(jq '[.modes[] | select(.hitRate <= 0)] | length' <<<"$summary")"
  if [[ "$zero_hit_modes" -gt 0 ]]; then
    echo "One or more retrieval modes returned no expected hits." >&2
    exit 1
  fi
fi
