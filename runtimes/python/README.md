# vyral-runtime

Python-first implementation of Vyral's portable local runtime.

Current version: `0.1.1`

Current maturity: `prototype`

Supported Vyral contract: `0.3.0`

This package is implementing the sequence in
[`design/python-runtime.md`](../../design/python-runtime.md). The current
prototype includes:

- the canonical OpenAPI, JSON Schema, and public operation catalog;
- shared Python/.NET conformance fixtures for primitives, snapshots,
  deterministic embeddings, record CRUD, compound filters, ordering,
  continuation, RAG plan/manifest hashes, graph record mapping, and durable
  execution failure/cancellation semantics;
- SQLite collections and records, filtering, lexical and exact-vector search,
  snapshots, filesystem objects, and persisted traces;
- deterministic embedding providers, retrieval profiles and fusion, reranking
  seams, ingestion, context and prompt assembly, citations, and evaluation;
- graph import/export, traversal, inspection, doctor, and bounded GraphRAG
  context expansion;
- the strong transactional SQLite CanonicalStore profile with fences,
  revisions, idempotency, outbox leasing, migrations, preflight, and
  hash-verified archives;
- native durable execution with retries, cancellation, events, timers, waits,
  leases, checkpoints, artifacts, restart recovery, maintenance, policy, and
  thirteen built-in domain job adapters, including staged cross-store artifact ingestion and
  receipt-bound collection lifecycle;
- an OpenAPI-derived ASGI host covering all 133 REST operation IDs;
- parity with the shared `vyral.admission.v1` contract: aggregate mutations return durable,
  idempotent jobs with `Location`, while non-mutating RAG dry-runs remain synchronous;
- a stateless MCP `2026-07-28` endpoint with self-describing routing headers,
  authorization, bounded resources/tools, and durable Tasks;
- synchronous APIs plus bounded-executor asynchronous facades; and
- Python-native handler descriptors, an async run context, a replayable local
  handler harness, and a dependency-free token-safe HTTP external-worker
  transport covering leases, heartbeats, progress, events, artifacts,
  checkpoints, waits, completion, and cancellation.

Every required portable-local profile is implemented and reports
`available: true`. All remain `prototype`, and `fullLocalReady` deliberately
remains false until the manual Python 3.10–3.12 Linux/macOS/Windows matrix and
independent security review are complete. A Linux/Python 3.12 platform receipt,
the combined `0.1.0` → `0.1.1` upgrade/restart rehearsal, and the executable
adversarial security gate pass locally; those results do not stand in for the
remaining platform or human-review evidence.
Unsupported optional providers are disclosed explicitly; the runtime does not
silently delegate embedded behavior to .NET or to the existing HTTP client.

```python
from vyral_runtime import VyralRuntime

runtime = VyralRuntime()
status = runtime.readiness().to_dict()

print(status["runtimeVersion"])
print(status["contract"]["operationCount"])
print(status["fullLocalReady"])
```

```python
from vyral_runtime import VyralRuntime

with VyralRuntime.open_local("./.vyral") as runtime:
    runtime.records.create_collection({"name": "notes"})
    runtime.records.upsert_record(
        "notes",
        {
            "id": "hello",
            "partitionKey": "local",
            "content": {"text": "Hello from the Python runtime"},
        },
    )
```

Python handlers use the same authoring surface in the local harness and over
the remote external-worker protocol. The decorator is deliberately thin: it
builds the existing portable descriptor and handler objects without adding a
workflow graph, scheduler, or alternate retry model.

```python
from vyral_runtime import (
    ExecutionRunContext,
    ExecutionRunResult,
    execution_plugin,
    vyral,
)

@vyral(
    "example.echo",
    plugin="example.plugin",
    max_attempts=3,
)
async def echo(context: ExecutionRunContext) -> ExecutionRunResult:
    await context.record_event("log", message="handler started")
    return ExecutionRunResult.succeeded_result({"received": context.run.payload})

plugin = execution_plugin(
    "example.plugin",
    name="Example",
    version="1.0.0",
    handlers=(echo,),
)
```

The decorated handler remains directly awaitable for focused tests. Explicit
`execution_handler(...)`, `DelegateExecutionHandler`, and
`StaticExecutionPlugin` construction remains available when descriptor-oriented
naming or dynamic registration is clearer. Handler and plugin IDs remain
explicit because they are durable contract identities; renaming or moving a
Python function must not silently create a different operation.

## Choosing a Python package

| Goal | Install |
| --- | --- |
| Talk to a running .NET or Python Vyral host | `vyral-client` |
| Run Vyral in-process without a server or .NET | `vyral-runtime` |
| Run a Python-hosted REST and MCP endpoint | `vyral-runtime[server]` |

The existing `vyral-client` distribution remains the supported lightweight
client for a running Vyral server. `vyral-runtime` is a separate distribution
and does not depend on or route its embedded behavior through that client.

## Embedded models and wire JSON

The embedded runtime accepts schema-shaped mappings where convenient, then
returns rich Python models such as `VyralRecord`, readiness receipts, plans,
and execution state. Use their `to_dict()` methods when serializing to JSON or
crossing an HTTP, MCP, queue, or persistence boundary. The REST host and
`vyral-client` expose the canonical JSON wire representation instead; callers
must not depend on Python class names or private object layout as portable
contract.

OpenAPI operation IDs remain the semantic naming authority. Embedded methods
use idiomatic `snake_case` names and mirror those operation IDs where practical;
a convenience name may improve Python composition but cannot add alternate
wire semantics, defaults, validation, or failure behavior. When exact
cross-runtime correspondence matters, start from the operation ID and its
shared schema or conformance fixture.

## Storage portability

The Python runtime's SQLite tables, migration ledger, filesystem layout, and
the corresponding .NET layouts are implementation-private. They are not a
binary compatibility surface, even when the filenames look alike. Do not copy
`vyral.sqlite`, a CanonicalStore database, or an execution database from one
runtime into the other.

Cross-runtime movement uses documented public envelopes: collection and graph
exports, CanonicalStore tenant archives, objects, and execution-owned outputs
where supported. Those envelopes and their shared conformance fixtures—not raw
SQLite bytes—are the portability contract.

## Contract and fixture verification

Every runtime construction validates the bundled OpenAPI, JSON Schema, and SDK
catalog. Embedded `VyralRuntime()` construction does not execute the complete
golden corpus by default, keeping notebook and test composition proportional as
the fixture set grows. Use `VyralRuntime(verify_assets=True)` when construction
itself must fail closed on both the contract bundle and goldens.

Readiness always executes the bundled goldens. The optional REST/MCP host also
constructs its owned runtime with `verify_assets=True`, and release
qualification runs the canonical fixture suite separately. This keeps
construction fast without weakening host startup or qualification evidence.

## Optional server

Install the server extra and choose an explicit durable directory:

```bash
python -m pip install "vyral-runtime[server]"
export VYRAL_API_KEY="replace-with-a-secret"
vyral-runtime --root ./.vyral --host 127.0.0.1 --port 5220
```

REST is available at the public OpenAPI paths and stateless MCP at `/mcp`.
API-key hosts accept `X-Vyral-Api-Key` or a bearer token. Localhost Host/Origin
validation is enabled by default. A non-loopback CLI bind is rejected unless
`VYRAL_API_KEY` is set; wildcard binds also require one or more explicit
`--allowed-host` values. Browser deployments opt into exact origins with
`--allowed-origin`. The SQLite/filesystem composition is a single-node topology
even though each MCP request can be parsed and authorized without session
affinity.

The baseline qualification corpus is 2,000 records with 384-dimensional exact
vectors, a roughly 200-chunk RAG document, and 20 durable jobs. This is a
bounded regression smoke for notebook and small local-service use, not an SLA
or a maximum supported corpus.

## Development checks

From the repository root:

```bash
scripts/verify-python-runtime.sh
python3 scripts/verify-python-external-worker-integration.py
scripts/verify-python-runtime-external-worker.sh path/to/vyral_runtime.whl
scripts/verify-python-runtime-mcp-conformance.sh path/to/vyral_runtime.whl
python3 scripts/verify-python-runtime-security.py path/to/vyral_runtime.whl
python3 scripts/verify-python-runtime-upgrade.py \
  path/to/vyral_runtime-0.1.0.whl path/to/vyral_runtime-0.1.1.whl
python3 scripts/benchmark-python-runtime.py
dotnet test tests/Vyral.Tests.Conformance/Vyral.Tests.Conformance.csproj \
  --filter 'PortableRuntimeGoldenFixtureTests|PortableExternalWorkerLifecycleFixtureTests'
```

The main verification command runs the full unit suite with branch
instrumentation using pinned Coverage.py 7.15.2 and enforces a 77.5% combined
line/branch regression floor. The floor is a regression guard, not a claim
that every dispatch or error branch is exhaustively tested.

The cross-platform qualification workflow is manual-only:
`.github/workflows/python-runtime-qualification.yml`. This preserves the
repository's current GitHub-run gate while keeping the promotion matrix
reproducible. Dispatch requires the HTTPS URL and SHA-256 of a previously
qualified `0.1.x` wheel so the installed upgrade gate cannot silently
self-compare the candidate. The aggregate job then runs
`scripts/verify-python-runtime-platform-matrix.py`; all nine cells must be
clean, refer to one commit, and agree on runtime and contract identity before
the matrix is valid promotion evidence.
