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
remains false pending independent security review and an explicit promotion
decision. The manual Python 3.10–3.12 Linux/macOS/Windows matrix now passes:
every cell builds and installs clean wheel and source artifacts, completes the
cited restart/replay quickstart, verifies the server extra, and contributes to
one consistent aggregate receipt. The combined `0.1.0` → `0.1.1`
upgrade/restart rehearsal and the executable adversarial security gate also
pass locally; automated evidence does not substitute for independent review.
Unsupported optional providers are disclosed explicitly; the runtime does not
silently delegate embedded behavior to .NET or to the existing HTTP client.

## Local single-player experience

From the repository root, the shortest path runs directly from source:

```bash
./scripts/vyral
```

It performs the connected retrieval-and-execution proof described below. No
installation or third-party service is required. Use `python scripts/vyral` on
Windows.

To install the command into an isolated environment instead:

```bash
python3 -m venv .venv
. .venv/bin/activate
python -m pip install --editable runtimes/python
```

The installed command is `vyral`, and running it without arguments performs the
same proof:

```bash
vyral
```

The quickstart creates a three-document, source-backed record corpus without
vectors, returns cited lexical context, admits a decorated handler with a
stable idempotency key,
closes the runtime before dispatch, reopens the same SQLite/filesystem state,
and completes the preserved run identity. It reports the queued receipt before
the close/reopen boundary so acceptance is not confused with completion.

The quickstart does not invoke an embedding provider. The runtime's available
default `local-token-hash` provider remains CPU-only, model-free, and requires
no network or downloaded assets; when explicitly selected for vector mechanics,
its lexical-overlap ranking is not a semantic-model quality claim.

Inspect the state and its material limitations independently:

```bash
vyral inspect
```

The quickstart records an ownership marker and will reset only a dedicated
directory bearing that marker:

```bash
vyral quickstart --reset
```

Generate one editable application when you are ready to own the code:

```bash
vyral init
python ./vyral_app.py
```

The generated file uses `@vyral(...)`, admits work with a stable idempotency
key, prints the durable receipt, closes the first runtime before dispatch,
reopens its own `.vyral/vyral_app` directory, and completes the preserved run.
Running it again reports `replayed=true` and dispatches no duplicate work. The
generator refuses to overwrite an existing path; the result is ordinary Python
source intended to be edited or absorbed into an application. Leave the visible
`RUN_VERSION` unchanged to prove replay, then increment it after changing the
work to admit a new run. Use `--path` or `--root` when you need non-default code
or state locations.

Use `--json` with `init`, `quickstart`, or `inspect` for machine-readable
output. `vyral-runtime` remains a compatibility alias. Package publication is
withheld, so direct source use or the editable install is the public pre-release
path. Once a qualified wheel is published, the install command can be replaced
with `python -m pip install vyral-runtime` without changing the local commands.

The quickstart JSON includes measured `firstCitationMs`, `durableReceiptMs`,
`restartRecoveryMs`, and `completedMs` milestones. Artifact qualification runs
the installed wheel and sdist through the generated editable application, its
idempotent rerun, the connected quickstart, a second-process quickstart replay,
independent inspection, and marker-bounded reset. Each supported platform cell
rejects either installed path if package installation through its first useful
result takes more than five minutes.

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

### Experimental source-native retrieval

`vyral_runtime.integrations.ripgrep` provides a bounded, read-only experiment
for code, Markdown, and other safely accessible text sources. It uses a static
root and glob allowlist, fixed-string queries over standard input, source
revision citations, and strict resource limits. It is not part of the stable
wire contract and is not automatically exposed through REST or MCP. Retained
comparison evidence supports it for exact identifiers and phrases in current
local sources when maintaining a duplicate index is not worthwhile. Use Vyral
lexical retrieval for reordered terms, prefixes, record filters, tenant
boundaries, snapshots, and lower post-index query latency. See the
[source-native retrieval guide](../../docs/guides/source-native-retrieval.md)
and [comparison receipt](../../benchmarks/retrieval/README.md).

### Experimental Extropic execution

Install the optional `extropic` extra to place a registered Python workload
behind the same Vyral execution lifecycle:

```bash
python -m pip install --editable "runtimes/python[extropic]"
```

```python
from vyral_runtime import ExecutionRunContext, ExecutionRunResult, vyral
from vyral_runtime.integrations.extropic import (
    ExtropicAdapterOptions,
    ExtropicExecutionAdapter,
)


def simulate(payload):
    return {"seed": payload["seed"], "samples": payload["samples"]}


extropic = ExtropicExecutionAdapter(
    "example.simulation.v1",
    simulate,
    options=ExtropicAdapterOptions(tier="l4", require_seed=True),
)


@vyral("example.simulate", plugin="example.extropic", max_attempts=3)
async def run_simulation(
    context: ExecutionRunContext,
) -> ExecutionRunResult:
    return await extropic.execute(context)
```

Vyral retains the provider job id, safe status, bounded logs, and replay state;
it never persists Extropic credentials or upload grants. Because Extropic 0.5
does not accept an idempotency key during job creation, a lost create response
fails closed and is not resubmitted automatically. Known provider jobs are
reconnected and retried by id. The integration is a prototype, remains outside
the adapter qualification matrix, and makes no Z1 support claim. Registered
workloads should be self-contained plain Python functions; Vyral serializes
those functions by value, while third-party imports must exist in Extropic's
sandbox. A pinned `extropic-torx` extra and
[`examples/python/extropic_torx_workload.py`](../../examples/python/extropic_torx_workload.py)
provide a real, credit-free Torx packaging proof on Python 3.11 or newer. See the
[Extropic execution guide](../../docs/guides/extropic-execution.md) for the
complete lifecycle and current boundaries.

## Choosing a Python package

| Goal | Install |
| --- | --- |
| Talk to a running .NET or Python Vyral host | `vyral-client` |
| Run Vyral in-process without a server or .NET | `vyral-runtime` |
| Run a Python-hosted REST and MCP endpoint | `vyral-runtime[server]` |
| Rehearse the current Extropic/Torx proof locally | `vyral-runtime[extropic-torx]` (Python 3.11+) |

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
vyral serve --root ./.vyral --host 127.0.0.1 --port 5220
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
instrumentation using pinned Coverage.py 7.15.4 and enforces a 77.5% combined
line/branch regression floor. The floor is a regression guard, not a claim
that every dispatch or error branch is exhaustively tested.

The cross-platform qualification workflow is manual-only:
`.github/workflows/python-runtime-qualification.yml`. This preserves the
repository's current GitHub-run gate while keeping the promotion matrix
reproducible. Every cell builds both artifacts, installs each into a clean
environment, completes the real cited/restart quickstart, replays it from a
second process, inspects its limitations, and safely resets its owned state.
The HTTPS URL and SHA-256 of a previously qualified `0.1.x` wheel are optional
paired inputs: when supplied, the workflow also runs the installed upgrade
gate, which cannot silently self-compare the candidate. The aggregate job then runs
`scripts/verify-python-runtime-platform-matrix.py`; all nine cells must be
clean, refer to one commit, agree on runtime and contract identity, and carry
passing measured local-experience evidence before the matrix is valid. A run
without a baseline proves the platform matrix but is not upgrade evidence and
does not by itself authorize maturity promotion.
