# Consumer Handoff

This guide is for applications integrating with a local Vyral server through HTTP, Python, JavaScript, or .NET. It covers the stable local-development path: start the server, verify readiness, ingest records or RAG text, retrieve context, run provider-backed AI calls, and inspect failures.

## Start The Server

```bash
scripts/start-local-server.sh
```

Default local paths:

- server URL: `http://127.0.0.1:5220`
- SQLite database: `.vyral/vyral.sqlite`
- object store root: `.vyral/objects`
- provider artifact root: `.vyral/provider-runs`

Useful startup variables:

| Variable | Purpose |
| --- | --- |
| `VYRAL_URLS` | Server listen URL. Defaults to `http://127.0.0.1:5220`. |
| `VYRAL_DATA_DIR` | Base local data directory. Defaults to `.vyral`. |
| `VYRAL_DATABASE_PATH` | SQLite database path. |
| `VYRAL_OBJECTS_PATH` | Filesystem object store path. |
| `VYRAL_RECORD_STORE` | Record backend. Defaults to `sqlite`; use `google-firestore` or `google-alloydb` for GCP deployments. |
| `VYRAL_TRACE_STORE` | Trace backend. Defaults to `sqlite`; use `google-firestore` to avoid ephemeral Cloud Run trace state. |
| `VYRAL_OBJECT_STORE` | Object backend. Defaults to `file`; use `google-cloud-storage` for GCS or `cloudflare-r2` for R2 artifacts. |
| `GOOGLE_CLOUD_PROJECT` / `VYRAL_GCP_PROJECT_ID` | Google project id for Firestore-backed Vyral stores. |
| `VYRAL_GCS_BUCKET` | GCS bucket used by readiness probes and compatibility artifact ingestion when GCS is selected. |
| `VYRAL_ALLOYDB_CONNECTION_STRING` | Optional AlloyDB/pgvector record-store connection string. |
| `VYRAL_CLOUDFLARE_ACCOUNT_ID` | Cloudflare account id for R2 when `VYRAL_OBJECT_STORE=cloudflare-r2`. |
| `VYRAL_R2_ACCESS_KEY_ID` | Cloudflare R2 S3 API access key id. |
| `VYRAL_R2_SECRET_ACCESS_KEY` | Cloudflare R2 S3 API secret access key. |
| `VYRAL_R2_BUCKET` | R2 bucket used by readiness probes and artifact ingestion when R2 is selected. |
| `VYRAL_PROVIDER_ARTIFACT_DIR` | Provider run artifact directory. |
| `VYRAL_API_KEY` | Enables API-key protection when set. Send as bearer token or `X-Vyral-Api-Key`. |
| `VYRAL_ENABLE_LIVE_TARGETS` | Registers live CLI/API providers when true. Leave false for deterministic local-only use. |

Check the server:

```bash
curl http://127.0.0.1:5220/health
curl http://127.0.0.1:5220/readiness
curl http://127.0.0.1:5220/openapi/vyral.json
```

Use `/health` for a quick liveness and configuration summary. Use `/readiness` before consumer handoff; it probes SQLite, object storage, traces, embedding provider state, provider limits, auth posture, and provider readiness.

## GCP And Compatibility Artifact Ingestion

Local-first consumers still use the same HTTP contract when Vyral is deployed on Google Cloud Run.
Select Google-backed storage with configuration, not code changes:

```bash
VYRAL_RECORD_STORE=google-firestore
VYRAL_TRACE_STORE=google-firestore
VYRAL_OBJECT_STORE=google-cloud-storage
GOOGLE_CLOUD_PROJECT=your-gcp-project
VYRAL_GCS_BUCKET=your-vyral-artifact-bucket
```

Cloudflare R2 can be selected for raw artifacts without changing the compatibility HTTP contract:

```bash
VYRAL_OBJECT_STORE=cloudflare-r2
VYRAL_CLOUDFLARE_ACCOUNT_ID=your-account-id
VYRAL_R2_ACCESS_KEY_ID=your-r2-access-key
VYRAL_R2_SECRET_ACCESS_KEY=your-r2-secret
VYRAL_R2_BUCKET=your-vyral-artifact-bucket
```

Any consumer can submit a record and its artifact to:

```bash
POST /ingest/record-artifact
```

The request is `multipart/form-data`: a JSON `manifest` carries a generic
collection, `VyralRecord`, and object descriptor, while an `artifact` file part
carries raw bytes without base64 expansion. Vyral stores the artifact through
`IObjectStore` and upserts the supplied record. The consumer provisions its
collection and owns its partition, artifact-container, field mapping, queue,
and callback configuration.

Consumers decide whether and how to derive pseudonymous identifiers. Vyral
stores only the metadata and content that their manifest supplies.

For Cloud Run, keep specialized work request-bound and let Vyral own generic
durable acceptance of the supplied artifact and record. A producer may select
inline or queue-backed processing without changing the contract. Its integration
owns the queue, callback identity, retry policy, and route.

An upstream may attach a signed external context to its manifest. The host
configures a public-key verifier and expected issuer, audience, and key id.
Vyral verifies that provider-neutral envelope; the consumer owns claim bindings
and provenance projection, and Vyral never needs a raw client address.

## Canonical Domain State

Use CanonicalStore for authoritative tenant state where a document change, durable uniqueness or
command fence, and downstream projection command must commit together. It is separate from
retrieval records and requires a tenant-scoped idempotency key for every transaction.

```bash
curl -X POST http://127.0.0.1:5220/canonical/tenants/tenant-a/transactions \
  -H 'Content-Type: application/json' \
  -d '{"tenantId":"tenant-a","idempotencyKey":"review:42:approve:v1","mutations":[{"operation":"upsert","document":{"tenantId":"tenant-a","documentType":"review","id":"42","schemaVersion":"v1","data":{"decision":"approved"},"indexes":{"decision":"approved"}}}],"outbox":[{"topic":"review.approved","key":"42","payload":{"reviewId":"42"}}]}'
```

The response includes immutable document revisions and the durable outbox event. Lease events from
`POST /canonical/tenants/{tenantId}/outbox/leases`; acknowledge or release them with the returned
lease token in the JSON request body—never a URL or a log. Tenant export/restore and migration
ledger routes are available for operational ownership. See the [CanonicalStore guide](../concepts/canonical-store.md)
for the full invariants, PostgreSQL configuration, local CLI backup/restore, and shared-deployment
tenant authorization policy.

## Evidence Briefs

`vyral.evidence-brief.v1` is an optional CanonicalStore artifact for an application that must
preserve what supported a dated question. It binds a question and `asOfUtc` to fact anchors,
immutable source snapshots with SHA-256 content digests, citation presentation, counterevidence,
uncertainties, and safe retrieval-trace references. It deliberately does **not** contain a generated
answer, a persona prompt, source-acquisition policy, or a truth-adjudication decision.

The JSON Schema and a valid fixture ship with `Vyral.Abstractions` under
`contracts/evidence-brief.v1.schema.json` and `contracts/fixtures/`. C# callers can use
`EvidenceBriefContract` plus `StoreEvidenceBriefAsync` / `GetEvidenceBriefAsync`; the Python and
JavaScript clients expose `build_evidence_brief_transaction` / `buildEvidenceBriefTransaction` and
`store_evidence_brief` / `storeEvidenceBrief`. These helpers use the ordinary tenant-scoped
CanonicalStore routes, so shared-deployment authorization and revisions continue to apply.

Each source URI is stable and credential-free: use a source-local citation `locator` for page or
section presentation, and do not store signed URLs, query strings, tokens, or raw retrieval prompts.
The default `vyral.evidence-brief.changed` outbox event is a projection wake-up only; an immutable
source manifest or equivalent completeness receipt remains necessary before a derived index serves
as authoritative.

## Client Entry Points

Python:

```python
from vyral_client import VyralClient

client = VyralClient("http://127.0.0.1:5220")
print(client.readiness()["summary"])
```

JavaScript:

```js
import { VyralClient } from "vyral-client";

const client = new VyralClient("http://127.0.0.1:5220");
console.log((await client.readiness()).summary);
```

If `VYRAL_API_KEY` is set, pass the same key to the client constructor. Failed calls raise or throw `VyralClientError` with status, raw body, parsed problem details, and helper predicates for auth, validation, timeout, and missing-collection failures.

## Retrieval Defaults

For local or structured text that the application can safely read directly,
test source-native search first. It provides exact matching and current source
state without an index. Use Vyral lexical retrieval when the corpus needs a
governed record boundary, filters, stable snapshots, or provider-portable
retrieval semantics. Use vectors for semantic discovery, recall expansion,
remote or unstructured corpora, and non-greppable modalities when your own
evaluation supports the added index. Treat hybrid and reranking as evaluated
policies rather than defaults. See the
[source-native and indexed retrieval guide](source-native-retrieval.md) for the
bounded ripgrep experiment and a five-variant comparison recipe.

Recommended first path:

1. Create a record-only collection for lexical retrieval, or a RAG collection
   with `create_rag_collection(...)` / `createRagCollection(...)` when vectors
   are justified.
2. Store source-backed records directly for zero-embedding lexical RAG. For
   vector ingestion, use `plan_rag_text_ingestion(...)` then
   `commit_rag_text_ingestion(...)`, or `ingest_rag_text(...)` for simple loops.
3. Build context with `build_rag_context(...)`.
4. Evaluate with `/retrieval/evaluate` or `evaluate_retrieval(...)` before changing retrieval profiles.
5. Snapshot with collection export before destructive corpus changes.

## Embeddings And Reranking

The default embedding provider is deterministic and local. It is reliable for mechanics and tests, but it is not semantic. ONNX providers are available when model and vocabulary files are present under an untracked path such as `.vyral/models`.

Common embedding variables:

| Variable | Purpose |
| --- | --- |
| `VYRAL_EMBEDDING_PROVIDER` | Embedding provider id. |
| `VYRAL_EMBEDDING_MODEL_ID` | Model id reported in responses. |
| `VYRAL_EMBEDDING_DIMENSIONS` | Vector dimension override. |
| `VYRAL_EMBEDDING_MODEL_PATH` | ONNX model file path. |
| `VYRAL_EMBEDDING_VOCAB_PATH` | tokenizer vocabulary file path. |
| `VYRAL_EMBEDDING_EXECUTION_PROVIDER` | `cpu`, `cuda-preferred`, or `cuda-required`. |
| `VYRAL_EMBEDDING_INTRA_OP_THREADS` | ONNX intra-op thread cap. |
| `VYRAL_EMBEDDING_INTER_OP_THREADS` | ONNX inter-op thread cap. |
| `VYRAL_EMBEDDING_CUDA_MEMORY_LIMIT_MB` | Optional CUDA memory cap for GPU-capable hosts. |

Common rerank variables:

| Variable | Purpose |
| --- | --- |
| `VYRAL_RERANK_PROVIDER` | Rerank provider id for retrieval profiles that use provider reranking. |
| `VYRAL_ONNX_RERANK_CPU_MODEL_PATH` | CPU ONNX cross-encoder model path. |
| `VYRAL_ONNX_RERANK_CPU_VOCAB_PATH` | CPU vocabulary file path. |
| `VYRAL_ONNX_RERANK_CPU_MAX_TOKENS` | Candidate payload token budget. |
| `VYRAL_ONNX_RERANK_CPU_BATCH_SIZE` | Cross-encoder batch size. |
| `VYRAL_ONNX_RERANK_GPU_MODEL_PATH` | GPU-preferred ONNX cross-encoder model path. |
| `VYRAL_ONNX_RERANK_GPU_EXECUTION_PROVIDER` | `cuda-preferred` or `cuda-required`. |
| `VYRAL_ONNX_RERANK_GPU_CUDA_MEMORY_LIMIT_MB` | Optional CUDA memory cap. |

Use `/embedding-providers/doctor` and `/embedding-providers/guidance` before running quality tests. Use collection inspection and retrieval-evaluation diagnostics to verify the local exact-search shape or adapter-specific production behavior.

## Provider-Backed AI

Local deterministic providers are always registered. Live Codex, Claude, Gemini, Antigravity, Grok Build, and Jules targets are registered only when `VYRAL_ENABLE_LIVE_TARGETS=true`.

Provider endpoints:

- `GET /providers`
- `GET /providers/capabilities`
- `GET /providers/{provider}`
- `GET /providers/{provider}/models`
- `GET /providers/{provider}/quota`
- `GET /providers/{provider}/readiness`
- `GET /providers/{provider}/doctor`
- `POST /providers/{provider}/run`
- `POST /providers/{provider}/jobs`

The built-in live CLI defaults are:

| Provider | Default model | Notes |
| --- | --- | --- |
| `codex-cli` | `gpt-5.3-codex-spark` | Supports advisory provider runs and Codex quota telemetry when the local Codex app-server is available. |
| `gemini-cli` | `gemini-2.5-flash-lite` | Use `GET /providers/gemini-cli/models` for known model ids. |
| `claude-cli` | `claude-sonnet-4-5` | Uses the local Claude CLI command. |
| `grok-build-cli` | CLI configured | Advisory-only Grok target. It is fail-closed until an operator configures its isolated work directory, scrubbed `HOME`, prompt-file directory, Grok sandbox profile, and one or more Grok `--deny` rules. Prompts are passed with `--prompt-file`, never in process arguments. Set `Providers:GrokBuild:ModelId` and optional `KnownModels` when a specific Grok model is required. |
| `jules-api` | provider configured | Requires `JULES_API_KEY` or `Providers:Jules:ApiKey`. |

Provider run guardrails are visible in `/health`, `/readiness`, and provider readiness responses. The default shared limits are one concurrent provider run, five-second queue timeout, sixty runs per sixty seconds, 120-second default run timeout, 300-second max timeout, and 128 KiB max output.

The opt-in `agent.workspace` target inherits those shared limits. It has no target-specific
timeout/concurrency profile and intentionally emits no draft patch if validation, cancellation, or
timeout prevents a successful apply. Consumers should split longer work or retain recovery state in
their own workflow; partial staging is never applied automatically.

Use `/providers/capabilities` or the client capability-matrix helper first when a consumer needs to choose between `ai.extract`, `ai.review`, `ai.scaffold`, `ai.rerank`, model listing, quota telemetry, jobs, or artifact-producing workflows.

For `grok-build-cli`, configure these values before qualification. The target clears the inherited environment and rejects calls when any are absent or invalid. `WorkingDirectory` must be a dedicated empty directory; its only permitted entry is the empty prompt-file directory.

```text
Providers:GrokBuild:WorkingDirectory=/srv/vyral/grok-advisory
Providers:GrokBuild:PromptFileDirectory=/srv/vyral/grok-advisory/prompts
Providers:GrokBuild:Environment:HOME=/srv/vyral/grok-home
Providers:GrokBuild:SandboxProfile=vyral-advisory
Providers:GrokBuild:ToolDenyRules:0=shell:*
```

`SandboxProfile` and `ToolDenyRules` use Grok Build CLI syntax. Provider doctor verifies the executable digest, bounded `--version`, and `grok --sandbox <profile> inspect --json`; replacing the CLI invalidates readiness and prior qualification until re-qualified. The Grok sandbox profile remains the CLI's containment mechanism, so operators must define it to expose only the intended advisory working area.

## Execution Runtime

Use the execution runtime when a consumer needs durable, observable work instead of a synchronous
request/response operation. It supports provider runs, embedding batches, retrieval evaluations,
RAG ingestion, graph jobs, and custom plugins through one run/status/history/artifact/checkpoint
surface.

Core HTTP endpoints:

```bash
curl 'http://127.0.0.1:5220/execution/runtime/effective?productId=your-product&tenantId=your-tenant'
curl http://127.0.0.1:5220/execution/runs
curl -X POST http://127.0.0.1:5220/execution/runs \
  -H 'Content-Type: application/json' \
  -d '{"handlerId":"your.handler","pluginId":"your.plugin","payload":{},"idempotencyKey":"consumer:job:1"}'
curl http://127.0.0.1:5220/execution/runs/{runId}
curl http://127.0.0.1:5220/execution/runs/{runId}/history
curl http://127.0.0.1:5220/execution/runs/{runId}/artifacts
curl http://127.0.0.1:5220/execution/runs/{runId}/artifacts/{artifactIdOrName}
curl http://127.0.0.1:5220/execution/runs/{runId}/checkpoints/{checkpointKey}
```

Use `/execution/runtime/effective` for consumer discovery. It returns only the safe runtime status
and handlers authorized for the caller and optional product/tenant scope. The global
`/execution/runtime` catalog and maintenance routes require a maintenance identity when shared
execution access is enabled.

.NET plugin authors should target `Vyral.Execution`:

- implement `IExecutionPlugin` and `IExecutionHandler`
- use `IExecutionRunContext` for progress, status details, artifacts, events, leases, timers, and
  cancellation
- write plugin-owned resume state with `PutCheckpointAsync` and read it with `GetCheckpointAsync`
- record layered inspection events with `LogInfoAsync`, `LogWarningAsync`, or `LogAsync`
- start work with `ExecutionRunRequest`
- branch on adapter capabilities from `GetAdapterStatusAsync`, not on provider names

Python, JavaScript, or other HTTP consumers can start any registered handler with
`POST /execution/runs`. The endpoint is generic over the execution contract, not over domain
semantics: the caller must know the handler id, optional plugin id, payload schema, idempotency
strategy, and tag/correlation conventions for the plugin it is invoking. Run listing supports
portable filters for handler id, plugin id, status, correlation id, idempotency key,
created/updated ranges, and tag filters such as `tag.projectId=project-a`.
For human review gates, use `durable.waits` only after checking the adapter capability and persist
plugin-owned checkpoint/artifact state before waiting: resumption re-executes the handler body.
Linked follow-up runs remain a useful domain choice where approval must create an explicit audit
transition rather than suspend a run.

The released package set is `0.2.0`: `Vyral.Primitives`, `Vyral.Execution`,
`Vyral.Execution.Local`, `Vyral.Execution.AzureDurable`, and the optional
`Vyral.Execution.AzureDurable.Functions` bridge.

For portability, treat Vyral contracts as the application boundary and provider adapters as
replaceable implementations. Plugin code should route storage, object, retrieval, graph, and
execution interactions through Vyral abstractions when it needs to move from local SQLite to cloud
or business-system providers. Direct calls to analytics, storage, business-system, or orchestration
SDKs are adapter code or an intentional provider-specific plugin choice, not portable runtime code.

Release policy:

- local SQLite execution is ready for local-first consumer adoption
- consumers should pin a compatible `0.2.x` version and run the handoff smoke before upgrading
- packages declare Apache-2.0 metadata; consumers should review the repository [LICENSE](../../LICENSE)
- Azure Durable Functions is live qualified with a replay-safe bridge; each host still validates
  its resources, identity, and operational policy
- Google execution is live qualified with Firestore-backed durable state and Cloud Tasks dispatch;
  each host still validates its resources, identity, and operational policy

Known provider boundaries and portability rules are documented in
[execution runtime limitations](../reference/execution-runtime-limitations.md).

Local-first adapter:

```csharp
IExecutionRuntimeAdapter runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
{
    DatabasePath = ".vyral/execution-runtime.sqlite"
});
```

Runnable local sample:

```bash
dotnet run --project samples/Vyral.Execution.LocalSample/Vyral.Execution.LocalSample.csproj -- --once
```

Copyable package-only template:

```bash
samples/Vyral.Execution.PackageConsumerTemplate/
```

Validation set:

```bash
scripts/validate-execution-runtime-consumer.sh
scripts/validate-execution-runtime-package-consumer.sh
scripts/validate-execution-runtime-preview-handoff.sh
```

Consumer-visible runtime failures:

| Case | Behavior |
| --- | --- |
| malformed request or invalid retry policy | `StartRunAsync` throws before scheduling |
| missing handler | terminal `rejected` run with `failureClass=handler_missing` |
| conflicting plugin id | terminal `rejected` run with `failureClass=plugin_mismatch` |
| idempotency conflict | `StartRunAsync` throws |
| active cancellation | terminal `cancelled` run with `failureClass=cancelled` |
| adapter active-run limit reached | terminal `rejected` run with `failureClass=queue_full` |
| timeout | terminal `timed_out` or `failed` run with `failureClass=timeout` |
| retryable pressure/dependency failure | handler may return `failureClass=transient`; retry policy controls follow-up attempts |
| malformed plugin payload | handler may return `failureClass=validation` |
| host/runtime failure outside plugin control | adapter may return `failureClass=platform` |
| retry exhaustion | terminal `failed` run with the last handler-provided failure class |
| handler exception | terminal `failed` run with `failureClass=unknown` and bounded error text |

Treat run `status` as the lifecycle authority. Treat `failureClass` as a retry, escalation, and
diagnostic hint; plugins may add stable domain-owned classes, but portable consumers should handle
unknown values conservatively.

Maintenance prune defaults to dry-run. Use `dryRun:false` only for local runtime cleanup when the
operator intentionally wants to delete terminal run-owned state beyond the retained terminal count.

The local SQLite adapter is ready for local-first consumer use. The Azure Durable Functions
adapter target is `0.2.0`; local-host smoke settings use only `UseDevelopmentStorage=true` and the
clean `VyralExecutionLocal` task hub by default.

## GraphRAG

Use GraphRAG when retrieved records need bounded relationship expansion, provenance, review state, or source-grounded graph context.

Recommended first path:

1. Import a ROMAN-compatible graph envelope with `/collections/{collection}/graph/import`.
2. Inspect it with `/collections/{collection}/graph/inspect`.
3. Stamp RAG records with `metadata.graphNodeId`.
4. Add `graphExpansion` to `/rag/context` using seed pointers such as `/metadata/graphNodeId`.
5. Evaluate expected graph nodes, edges, provenance, and grounding with `/rag/context/evaluate`.

Traversal diagnostics include source scan mode, max-record budget, scope filters, export duration, traversal duration, source truncation, node/edge truncation, missing start nodes, and filtered-item counts.

## Failure Interpretation

Provider and retrieval responses are designed to fail closed. Consumers should treat only `status: "Succeeded"` as successful provider output. Non-success provider results should be displayed, logged, retried, or escalated according to `failureClass`, `providerStatus`, and `trace.traceId`; they should not be parsed as copy, evidence, or executable instructions.

Common status signals:

| Signal | Meaning |
| --- | --- |
| `Rejected` with `failureClass: "policy"` | Request exceeded timeout, output, input, mode, or capability policy. |
| `Rejected` with `failureClass: "rate_limit"` | Shared provider guard rejected due to queue or rate limit. |
| `TimedOut` | Server-side provider timeout elapsed. |
| `Failed` with `failureClass: "auth"` | Provider credentials or local login are missing or invalid. |
| `Failed` with `failureClass: "network"` | Transient provider transport or stream failure. |
| `fallbackApplied: true` | A configured fallback path preserved the broader workflow after a failed optional stage. |

Capture trace ids and provider job ids in consumer logs. They are the fastest path from an application failure back to Vyral traces and provider artifacts.

## Handoff Checklist

- `GET /readiness` returns `ready: true`, or warnings are understood and accepted.
- The consumer can create or reuse a collection.
- The consumer can ingest or upsert a small fixture.
- Retrieval returns expected records for at least one labeled query.
- RAG context returns citations and, when requested, deterministic `contextText`.
- Provider runs are either deterministic local-only or live targets are explicitly enabled and checked with `/providers/{provider}/readiness`.
- Execution runtime status is visible at `/execution/runtime/effective` for the intended caller scope, and any async job path in use can be listed through `/execution/runs`.
- Any ONNX semantic embedding or rerank model is characterized on the target host before it becomes a default.
- Collection export works before destructive local experiments.
