# Extropic execution

Vyral can manage durable admission and lifecycle state around a registered
Python workload dispatched to Extropic's thermodynamic compute cloud. This is
an experimental integration for prototyping. It is not a Vyral execution
runtime qualification claim, and it does not claim support for future Extropic
hardware such as Z1.

The boundary is intentionally narrow:

- Vyral owns the durable run identity, admitted payload, checkpoints,
  cancellation request, progress projection, artifacts, and public result.
- Extropic owns the remote sandbox and compute job.
- The application registers the Python workload at startup. An HTTP, MCP, or
  queue payload supplies data only; it cannot supply executable source.
- The workload must return a JSON-compatible value at the Vyral boundary.

This shape lets a project test Torx, THRML, and other Extropic-hosted workloads
without making provider-specific job objects part of its application contract.

## Install from the repository

Package publication remains withheld. From a Vyral checkout, install the
Python runtime and the pinned Extropic SDK compatibility line into an isolated
environment:

```bash
python3 -m venv .venv
. .venv/bin/activate
python -m pip install --editable "runtimes/python[extropic]"
```

Authenticate using Extropic's supported `EXTROPIC_TOKEN` environment variable
or its local browser-login flow. Do not put provider credentials in Vyral run
payloads, checkpoints, source control, or artifacts.

## Register a workload

The Extropic adapter composes with the existing `@vyral(...)` surface. It does
not add another workflow decorator or change the portable execution contract.

```python
from vyral_runtime import (
    ExecutionRunContext,
    ExecutionRunResult,
    execution_plugin,
    vyral,
)
from vyral_runtime.integrations.extropic import (
    ExtropicAdapterOptions,
    ExtropicExecutionAdapter,
)


def run_thermodynamic_model(payload):
    # Torx/THRML imports used here must exist in Extropic's sandbox image.
    seed = payload["seed"]
    samples = payload["samples"]
    return {"seed": seed, "samples": samples}


extropic = ExtropicExecutionAdapter(
    "example.thermodynamic-model.v1",
    run_thermodynamic_model,
    options=ExtropicAdapterOptions(
        tier="l4",
        timeout_seconds=300,
        require_seed=True,
    ),
)


@vyral("example.run-model", plugin="example.extropic", max_attempts=3)
async def run_model(context: ExecutionRunContext) -> ExecutionRunResult:
    return await extropic.execute(context)


plugin = execution_plugin(
    "example.extropic",
    name="Example Extropic workloads",
    version="1.0.0",
    handlers=(run_model,),
)
```

The stable workload id is included in the durable request fingerprint. Treat
renaming it like changing any durable handler identity. `require_seed=True`
is useful for stochastic workloads whose results need an explicit replay and
evaluation input; it accepts an integer or string in the configured seed
field.

## Lifecycle and recovery

Extropic 0.5 submission has three provider operations: create, upload, and
start. The adapter checkpoints each known boundary and retains only the
provider job id and safe status metadata. Upload tokens, artifact grants,
runner URLs, credentials, raw provider exceptions, and arbitrary provider
records are not persisted or returned.

Extropic's create operation currently has no client request id or idempotency
key. A lost create response is therefore ambiguous: the provider may have
created a billable job even though the client did not receive its id. Vyral
records the intent before the call and fails closed if no id returns. It will
not create a replacement automatically. Reconcile the account's job list with
the Vyral run before deciding whether to submit new work.

Once a provider job id is known, retry and replay address only that job:

| Provider boundary | Vyral behavior |
| --- | --- |
| Create response lost | Mark the submission ambiguous; do not resubmit |
| Job created, upload uncertain | Retain the job id; attempt only a safe start of that job on replay |
| Start response lost | Inspect and start the same uploaded job; never create another |
| Capacity bounce | Retry start for the same uploaded job within configured bounds |
| Worker cancellation | Request provider cancellation, including while settling an in-flight create response |
| Process restart after start | Reconnect by provider job id and resume status supervision |

Provider statuses are projected into Vyral's portable lifecycle. `stdout`,
`stderr`, and failure tracebacks can be retained as bounded text artifacts.
Extropic's `timeout` maps to Vyral `timed_out`; preemption and capacity map to
transient failures; credit exhaustion and internal provider endings map to
platform failures. Provider error detail is kept out of public results.

## Current boundaries

- Compatibility is pinned to `extro-sim >=0.5,<0.6`. The SDK and provider are
  new enough that minor contract changes should be expected and reviewed.
- Extropic functions use cloudpickle and must match the sandbox's Python and
  cloudpickle versions. Imported packages must already exist in that image.
- Vyral accepts only JSON-compatible run payloads and results even though the
  Extropic SDK can transport arbitrary pickled Python values.
- Serialized workload input is capped at 16 MiB by default. Retained text
  artifacts are capped at 512 KiB each by default and never exceed Vyral's
  1 MiB inline artifact limit.
- Live testing can consume credits and must use an isolated account or budget,
  an explicit credential, a uniquely identifiable test job, and post-run
  reconciliation. No live evidence is recorded yet.
- The integration remains outside
  `qualification/adapter-qualification.json` until it has a provider-owned
  isolation shape, cleanup proof, versioned receipts, and an explicit
  promotion decision.

Run the deterministic integration suite without credentials or remote work:

```bash
PYTHONPATH=runtimes/python/src \
  python -m unittest -v runtimes.python.tests.test_extropic_integration
```

An operator-authorized live smoke is separate and never runs in ordinary CI.
It requires an explicit non-interactive token and a second opt-in, uses only
the CPU tier, caps remote execution at 30 seconds, verifies the exact result,
and prints a redacted receipt:

```bash
VYRAL_EXTROPIC_LIVE=1 EXTROPIC_TOKEN="..." \
  python scripts/verify-python-extropic-live.py
```

The command consumes provider credits and leaves the terminal provider job in
the account's history. Review the receipt and account job list after every run.
