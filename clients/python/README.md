# vyral-client

Thin Python client for the Vyral local HTTP server.

The wheel ships `py.typed`, generated stubs, and OpenAPI-derived `TypedDict`/`Literal` models in `vyral_client.contracts`. Install `vyral-client[async]` to use the optional httpx-based `AsyncVyralClient`; importing or using the synchronous client keeps the runtime dependency-free.

```python
from vyral_client import (
    TERMINAL_EXECUTION_RUN_STATUSES,
    VyralClient,
    build_provider_extract_request,
    build_provider_review_request,
    build_provider_tool_plan_request,
    build_rag_context_request,
    build_rag_text_ingestion_request,
    build_retrieval_evaluation_case,
    build_retrieval_evaluation_expected_match,
    build_retrieval_evaluation_request,
    build_verified_retrieval_request,
    compare_rag_ingest_results,
    is_execution_run_terminal,
)

client = VyralClient("http://localhost:5220")
# client = VyralClient("http://localhost:5220", api_key="dev-secret")
print(client.health())
print(client.readiness())
with open("consumer-artifact.json", "rb") as artifact:
    artifact_run = client.ingest_record_artifact(
        {
            "collection": "consumer-events",
            "record": {
                "id": "event-1",
                "partitionKey": "tenant-a",
                "type": "consumer.event",
                "content": {"status": "created"},
            },
            "artifact": {
                "container": "consumer-artifacts",
                "key": "events/event-1.json",
                "contentType": "application/json",
            },
        },
        artifact,
        filename="consumer-artifact.json",
        content_type="application/json",
        idempotency_key="consumer-event-1",
    )
artifact_run = client.wait_execution_run(artifact_run["id"])
artifact_receipt = artifact_run["result"]
canonical = client.commit_canonical_transaction("tenant-a", {
    "tenantId": "tenant-a",
    "idempotencyKey": "claim:claim-42:approve:v1",
    "mutations": [{
        "operation": "upsert",
        "document": {
            "tenantId": "tenant-a",
            "documentType": "claim",
            "id": "claim-42",
            "schemaVersion": "v1",
            "data": {"status": "approved"},
            "indexes": {"status": "approved"},
        },
    }],
    "outbox": [{"topic": "canon.claim.approved", "key": "claim-42", "payload": {"claimId": "claim-42"}}],
})
canonical_snapshot = client.export_canonical_tenant("tenant-a")
print(client.list_collections())
embedding_doctor = client.get_embedding_provider_doctor()
client.create_rag_collection("chunks")
vector = client.embed_text("retention policy")
ingestion_request = build_rag_text_ingestion_request(
    "retention policy",
    "tenant-a",
    skip_unchanged_chunks=True,
    persist_manifest=True,
)
plan = client.plan_rag_text_ingestion("chunks", ingestion_request)
ingestion_job = client.commit_rag_text_ingestion(
    "chunks", ingestion_request, plan, idempotency_key="rag-document-1"
)
ingested = client.wait_rag_ingestion_job(ingestion_job["id"])["textResult"]
comparison = compare_rag_ingest_results(plan, ingested)
batch_ingestion_job = client.ingest_rag_texts("chunks", [
    {"documentId": "doc-1", "partitionKey": "tenant-a", "text": "retention policy"},
    {"documentId": "doc-2", "partitionKey": "tenant-a", "text": "travel reimbursement"},
], continue_on_error=True, idempotency_key="rag-batch-1")
batch_ingested = client.wait_rag_ingestion_job(batch_ingestion_job["id"])["batchResult"]
batch_job = client.upsert_records(
    "chunks",
    [{"id": "chunk-1", "partitionKey": "tenant-a"}],
    idempotency_key="record-batch-1",
)
batch = client.wait_record_import_job(batch_job["id"])["batchResult"]
snapshot = client.export_collection("chunks")
records = client.query_all_records("chunks", {"limit": 100})
context = client.build_rag_context(build_rag_context_request("retention policy", ["chunks"]))
print(context["contextText"])
print(context["contextTextHash"])
prompt = client.build_rag_prompt({
    "context": {
        "retrieval": {
            "query": "retention policy",
            "collections": ["chunks"],
        },
        "includeTrace": True,
    },
    "template": {"failOnEmptyContext": True},
})
print(prompt["promptHash"])
verified = client.retrieve(build_verified_retrieval_request(
    "RECORD-000123 update deadline",
    ["chunks"],
    partition_keys=["tenant-a"],
))
evaluation_case = build_retrieval_evaluation_case(
    "retention",
    {"query": "retention policy", "collections": ["chunks"], "limit": 5},
    build_retrieval_evaluation_expected_match("chunk-1"),
)
evaluation = client.evaluate_retrieval(build_retrieval_evaluation_request([evaluation_case]))
providers = client.list_providers()
matrix = client.get_provider_capability_matrix()
doctor = client.get_provider_doctor("local-deterministic-ai")
models = client.list_provider_models("local-deterministic-ai")
readiness = client.list_provider_readiness()
quota = client.get_provider_quota("codex-cli")
qualified = client.qualify_provider("local-deterministic-ai", {"capability": "ai.chat"})
extract_job = client.run_provider("local-deterministic-ai", build_provider_extract_request(
    "source text",
    schema={"type": "object"},
    instructions="Return structured fields.",
), idempotency_key="example:extract:1")
extract = client.wait_provider_job(extract_job["id"])["result"]
job = client.start_provider_job("local-deterministic-ai", {
    "capability": "ai.chat",
    "operation": "run",
    "payload": {"messages": [{"role": "user", "content": "Summarize asynchronously."}]},
})
job_status = client.wait_provider_job(job["id"])
runtime = client.get_execution_runtime()
maintenance = client.get_execution_runtime_maintenance()
prune_preview = client.prune_execution_runtime_maintenance(retain_terminal_runs=100)
reconcile = client.reconcile_execution_runtime_dispatch(dry_run=True, limit=100)
runs = client.list_execution_runs(limit=10, include_result=True)
run = client.wait_execution_run(job["id"])
client.raise_execution_event(run["id"], {"name": "approved", "payload": {"by": "operator"}})
if is_execution_run_terminal(run):
    history = client.get_execution_run_history(job["id"])
    artifacts = client.list_execution_run_artifacts(job["id"])
```

Client URLs must use HTTP or HTTPS and cannot contain user credentials. API keys,
bearer tokens, and credential headers require HTTPS except on an exact loopback
address used for local development. Credential headers are not forwarded across redirects.

Configure transport behavior once for every method, or derive a request-scoped client without mutating the original:

```python
client = VyralClient(
    "http://localhost:5220",
    bearer_token="gateway-token",
    default_headers={"X-Consumer": "orders"},
    timeout=10,
    max_retries=2,
).with_options(correlation_id="order-sync-42")

# Retries apply to reads and to writes with an explicit idempotency key only.
records = list(client.iter_records("orders", {"limit": 100}, max_pages=20, max_items=1000))
```

`iter_canonical_documents(...)`, `iter_records(...)`, and `iter_search_records(...)` lazily follow continuation tokens with mandatory page bounds and optional item bounds. Their `query_all_*` / `search_all_*` counterparts retain collecting convenience behavior with the same bounds. A synchronous `cancellation_check` is evaluated before network and streamed-artifact work; `AsyncVyralClient` uses normal task cancellation and exposes `request_json(...)`, `request_bytes(...)`, artifact ingestion, execution-event delivery, health, and readiness over the optional httpx transport.

Use `create_rag_collection(...)` to durably admit creation of the common Vyral RAG collection policy with the server's configured embedding dimensions. Poll the returned run with `wait_execution_run(...)` before writing records. `create_collection(...)` and `delete_collection(...)` follow the same receipt-bound lifecycle because some cloud providers require multiple remote operations. Use `build_rag_collection_policy(...)` when an app wants to inspect or customize the policy before admission.
Use `commit_canonical_transaction(...)` for authoritative tenant-domain changes that must atomically include documents, fences, and an outbox. `get_canonical_document(...)`, `query_canonical_documents(...)`, `query_all_canonical_documents(...)`, `list_canonical_document_revisions(...)`, `lease_canonical_outbox(...)`, `acknowledge_canonical_outbox(...)`, `release_canonical_outbox(...)`, `export_canonical_tenant(...)`, and `restore_canonical_tenant(...)` map directly to CanonicalStore. Lease tokens remain request-body values; do not put them in URLs or logs. `list_canonical_migrations(...)`, `apply_canonical_migrations(...)`, `get_canonical_preflight(...)`, and `probe_canonical_data_plane(...)` require canonical administrator access when shared tenant policies are enabled. The last method explicitly creates and cleans isolated probe state; the passive preflight remains read-only.
Use `build_evidence_brief_transaction(...)`, `store_evidence_brief(...)`, and `get_evidence_brief(...)` for `vyral.evidence-brief.v1`: a dated fact/evidence/citation artifact, not a generated answer or persona prompt. The server validates the full contract before committing it. Preserve source SHA-256 snapshots and credential-free source URIs; the default change outbox event wakes projections but does not prove a projection is complete.
Use `readiness()` before consumer handoff to inspect storage reachability, SQLite diagnostics, embedding provider checks, model-file/runtime state, API-key posture, provider caps, provider readiness counts, warnings, and blockers in one response.
Use `build_verified_retrieval_request(...)` for source-reference, exact-identifier, and verified-record retrieval paths where lexical matching should be the operational default and vectors should not dominate verified-record lookup.
Use `list_retrieval_profiles()` and `build_retrieval_profile_request(...)` for built-in profiles such as `evidence`, `ragBaseline`, `rerankPolish`, `deepQuality`, `discovery`, and `productOptimization`. Use `build_retrieval_evaluation_expected_match(...)`, `build_retrieval_evaluation_hard_negative(...)`, `build_retrieval_evaluation_case(...)`, `build_retrieval_evaluation_request(...)`, `build_retrieval_evaluation_variant(...)`, and `build_retrieval_evaluation_comparison_request(...)` to avoid hand-shaping labeled retrieval quality requests. Use `compare_retrieval_evaluations(...)` to evaluate multiple retrieval variants against the same labeled cases. Use `start_retrieval_evaluation_job(...)`, `start_retrieval_evaluation_comparison_job(...)`, `get_retrieval_evaluation_job(...)`, `wait_retrieval_evaluation_job(...)`, and `cancel_retrieval_evaluation_job(...)` for long evaluation runs that need progress, partial results, polling, or cancellation.
`build_rag_context_request(...)` accepts `profile`, semantic retrieval options, and `max_citations_per_chunk` so RAG consumers can reuse retrieval profiles without forcing lexical defaults.
Use `list_embedding_provider_guidance()` and `get_embedding_provider_doctor()` to inspect embedding provider quality, hardware shape, model-file/runtime state, fallback reason, and suggested evaluation variants before running retrieval workloads.
Use `build_rag_text_ingestion_request(...)` and `ingest_rag_text(...)` for the default text-to-chunk-record RAG path. Set `options.dryRun: True` to synchronously preview chunk IDs, hashes, planned writes, stale delete records, manifest changes, action summaries, and a stable `planHash` without mutating the store. A mutating call returns a durable `RagIngestionJob`; poll it before reading `textResult`. Use `plan_rag_text_ingestion(...)`, `commit_rag_text_ingestion(...)`, `summarize_rag_ingest_result(...)`, and `compare_rag_ingest_results(...)` to carry expected plan/manifest hashes into `options.expectedPlanHash` / `options.expectedManifestHash` and detect ingestion-plan drift. Set `options.skipUnchangedChunks`, `options.reuseExistingChunkVectors`, `options.deduplicateExistingChunks`, `options.replaceDocumentChunks`, and `options.persistManifest` for repeatable ingestion loops.
`ingest_rag_texts(...)` and `upsert_records(...)` also return durable jobs; their per-item outcomes are available as `batchResult` after polling. The explicit `start_rag_text_ingestion_job(...)`, `start_rag_text_batch_ingestion_job(...)`, and `start_record_batch_upsert_job(...)` methods are equivalent `/jobs` aliases. Pass an idempotency key whenever submission may be retried.
Use `list_rag_ingestion_jobs(...)`, `get_rag_ingestion_job(...)`, `wait_rag_ingestion_job(...)`, and `cancel_rag_ingestion_job(...)` to manage any RAG job regardless of which start alias admitted it.
Use `export_collection(...)` to snapshot the collection policy, records, and content hash before destructive local changes or when seeding another environment. Exports default to the 10,000-record snapshot cap. Pass `query`, `max_records`, and `fail_on_limit_exceeded=False` for paged snapshots; truncated responses include `truncated` and `continuationToken`. Use `import_collection(...)` to restore or seed from a snapshot with explicit `replace_existing`, `continue_on_error`, `allow_collection_rename`, `allow_partial_snapshot`, and `expected_content_hash` controls.
Use `inspect_collection(...)` to check record counts, vector policy coverage, embedding provider/model distribution, RAG chunk/manifest counts, and capped vector anomalies before quality runs. The local SQLite adapter uses deterministic exact vector scans; provider adapters own any production index acceleration. Retrieval diagnostics and retrieval-evaluation top results preserve the active search shape.
Use `build_rag_context_request(...)` and `build_rag_context(...)` with `includeContextText: True` when an app wants a deterministic citation-markdown block plus `contextTextHash`; the structured `chunks` and `citations` fields remain available for custom prompt assembly.
Use `preflight_graph_import(...)`, `import_graph_envelope(...)`, `inspect_graph(...)`, `doctor_graph(...)`, `traverse_graph(...)`, `build_graph_expansion_options(...)`, and `evaluate_rag_context(...)` for GraphRAG workflows with import preview, seed-coverage readiness, bounded traversal, compact contribution summaries, and failure-category diagnostics.
Use `start_graph_import_job(...)`, `start_graph_inspection_job(...)`, or `start_graph_doctor_job(...)` with `build_graph_collection_import_request(...)`, `build_graph_inspection_request(...)`, or `build_graph_doctor_request(...)` respectively for longer graph work. `list_graph_jobs(...)`, `get_graph_job(...)`, `wait_graph_job(...)`, and `cancel_graph_job(...)` provide the same durable execution lifecycle; use an idempotency key when retrying submission.
Use `build_rag_prompt(...)` when an app wants Vyral to render deterministic chat-style `messages`, `prompt`, and `promptHash` over a normal context request without calling an answer model.
Use `evaluate_retrieval(...)` to run labeled retrieval cases and compare hit rate, MRR, precision/recall, nDCG, and top-result summaries during local R&D.
Use `embed_texts([...])` to generate ingestion vectors with the server's configured provider, then attach the returned values to records before calling `upsert_records` when custom ingestion is needed. Pass `purpose="query"` / `"passage"` and optional `query_prefix`, `passage_prefix`, or `symmetric_prefix` when an asymmetric local model needs shaped embedding inputs. Use `start_embedding_job(...)`, `get_embedding_job(...)`, `wait_embedding_job(...)`, and `cancel_embedding_job(...)` for large embedding batches that need progress, partial results, polling, or explicit cancellation.
Use `prune_traces(...)` for constrained local trace cleanup with dry-run support.
Use `list_provider_quotas()` and `get_provider_quota(...)` for advisory provider quota telemetry such as Codex app-server rate-limit buckets when exposed.
Use `build_provider_chat_request(...)`, `build_provider_extract_request(...)`, `build_provider_rerank_request(...)`, `build_provider_review_request(...)`, `build_provider_scaffold_request(...)`, `build_provider_tool_plan_request(...)`, `run_provider(...)`, and `run_provider_extract(...)` for common AI calls. Provider runs are receipt-bound: both run helpers return `ProviderRunJob`, and `wait_provider_job(...)` yields the terminal `result`. Use `list_providers()`, `get_provider_capability_matrix()`, `get_provider(...)`, `list_provider_doctor()`, `get_provider_doctor(...)`, `list_provider_models(...)`, `list_provider_readiness()`, `get_provider_readiness(...)`, `list_provider_quotas()`, `get_provider_quota(...)`, `list_provider_qualifications(...)`, `qualify_provider(...)`, `start_provider_job(...)`, `list_provider_jobs(provider=..., limit=..., include_result=...)`, `get_provider_job(...)`, and `cancel_provider_job(...)` for broader provider workflows.
Use `get_effective_execution_runtime(...)` for caller-scoped execution discovery; `get_execution_runtime()` exposes the global catalog and requires a maintenance identity. `start_execution_run(...)`, `get_execution_runtime_maintenance()`, `prune_execution_runtime_maintenance(...)`, `reconcile_execution_runtime_dispatch(...)`, `list_execution_runs(...)`, `get_execution_run(...)`, `wait_execution_run(...)`, `cancel_execution_run(...)`, `get_execution_run_history(...)`, `list_execution_run_artifacts(...)`, `get_execution_run_artifact(...)`, and `get_execution_run_checkpoint(...)` support the provider-neutral execution view shared by provider jobs, embedding jobs, retrieval evaluations, RAG ingestion, graph work, and custom plugins. External workers use `lease_external_execution_run(...)`, heartbeat/report/event/artifact/checkpoint/read/wait/complete helpers; keep lease tokens out of URLs and logs. `start_execution_run(...)` starts registered handlers with plugin-owned payload JSON; `list_execution_runs(...)` supports portable handler/plugin/status/correlation/idempotency/time/tag filters. Maintenance pruning defaults to dry-run and reconciliation is available when the active runtime implements the optional maintenance interface. `start_record_batch_upsert_job(...)` accepts `product_id` and `tenant_id` for shared execution scope, and `wait_record_import_job(...)` polls its durable result. `TERMINAL_EXECUTION_RUN_STATUSES` and `is_execution_run_terminal(...)` help consumers write polling code without hard-coding lifecycle strings.

## Direct operation index

The higher-level examples above cover the common paths. These direct methods expose the rest of the server contract without requiring consumers to construct URLs:

- Discovery and embedding: `openapi_contract()`, `get_public_schema_contract()`, `list_embedding_providers()`, `list_embedding_jobs(...)`, `list_graph_provider_shapes()`, and `get_graph_provider_shape(...)`.
- Canonical outbox recovery: `query_canonical_outbox(...)`, `renew_canonical_outbox_lease(...)`, and `replay_canonical_outbox(...)`.
- Collections and records: `get_collection_policy(...)`, `delete_collection(...)`, `start_collection_import_job(...)`, `export_graph_envelope(...)`, `get_record(...)`, `delete_record(...)`, `query_records(...)`, and `search_records(...)`.
- Binary objects: `list_objects(...)`, `put_object(...)`, `get_object(...)`, and `delete_object(...)`.
- Retrieval operations: `retrieve(...)` and `list_retrieval_evaluation_jobs(...)`.
- Trace inspection: `list_traces(...)`, `summarize_traces(...)`, `export_traces(...)`, and `get_trace(...)`.
- Durable record import administration: `list_record_import_jobs(...)`, `get_record_import_job(...)`, and `cancel_record_import_job(...)`.
- External execution workers: `heartbeat_external_execution_lease(...)`, `report_external_execution_lease(...)`, `record_external_execution_lease_event(...)`, `put_external_execution_lease_artifact(...)`, `put_external_execution_lease_checkpoint(...)`, `get_external_execution_lease_checkpoint(...)`, `wait_external_execution_lease(...)`, and `complete_external_execution_lease(...)`.

Failed HTTP calls raise `VyralClientError` with `status`, raw `body`, parsed problem details (`problem`, `title`, `detail`, `type`, `instance`, `problem_status`), and helper predicates such as `is_missing_collection()`, `is_auth_error()`, `is_validation_error()`, and `is_timeout()`.

```bash
python -m unittest discover -s tests
```
