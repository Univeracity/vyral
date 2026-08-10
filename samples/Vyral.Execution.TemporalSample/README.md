# Vyral Execution Temporal Sample

This console host is a neutral consumer example for the prototype Temporal adapter. The same
executable can host a worker, submit a run, inspect the redacted projection status, or run
preflight. Its worker also runs the provider-owned aggregate reconciler so scheduled starts and
failed start, signal, and cancellation deliveries are retried without API traffic.
The program uses `AddVyralTemporalExecution` for both API-side modes and the worker; only worker
mode adds `AddHostedWorker`, demonstrating that handler assemblies and polling do not belong in an
API-only process. Temporal client construction remains explicit so credential lifecycle stays with
the host.

The sample requires a reachable Temporal namespace and PostgreSQL database. Configure both
processes with the same values:

```bash
export VYRAL_EXECUTION_TEMPORAL_TARGET_HOST=127.0.0.1:7233
export VYRAL_EXECUTION_TEMPORAL_NAMESPACE=default
export VYRAL_EXECUTION_TEMPORAL_TASK_QUEUE=vyral-execution-sample
export VYRAL_EXECUTION_TEMPORAL_ADAPTER_NAMESPACE=sample
export VYRAL_EXECUTION_TEMPORAL_WORKER_DEPLOYMENT_NAME=vyral-execution
export VYRAL_EXECUTION_TEMPORAL_WORKER_BUILD_ID=sample-release-1
export VYRAL_EXECUTION_TEMPORAL_REQUIRE_TLS=false
export VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=vyral;Username=vyral;Password=local-only;SSL Mode=Disable'
export VYRAL_TEMPORAL_POSTGRES_REQUIRE_TLS=false
```

TLS can be disabled only for loopback development services. For a remote Temporal deployment,
leave both TLS flags at their default `true`, supply a PostgreSQL connection string with an
approved SSL mode, and set `VYRAL_TEMPORAL_API_KEY` when the namespace requires one. The sample
reads credentials but never prints them, endpoint values, or the PostgreSQL connection string.

Start the long-running worker in one terminal:

```bash
dotnet run --project samples/Vyral.Execution.TemporalSample/Vyral.Execution.TemporalSample.csproj -- worker
```

Submit and observe a run from another terminal:

```bash
dotnet run --project samples/Vyral.Execution.TemporalSample/Vyral.Execution.TemporalSample.csproj -- submit
```

The handler writes progress, a checkpoint, a terminal result, and an artifact larger than the
sample's inline threshold. The worker stores that artifact under
`.vyral/temporal-sample-objects` by default; set `VYRAL_TEMPORAL_SAMPLE_OBJECT_ROOT` to use a
different local path. This file store is for a disposable/local proof only—compose a durable
`IObjectStore` implementation in a real worker deployment.

Inspect projection availability and outbox counts without exposing topology:

```bash
dotnet run --project samples/Vyral.Execution.TemporalSample/Vyral.Execution.TemporalSample.csproj -- status
```

Run the non-workflow preflight while the intended worker is active to include its task-queue
pollers in the readiness result:

```bash
dotnet run --project samples/Vyral.Execution.TemporalSample/Vyral.Execution.TemporalSample.csproj -- preflight
```

Preflight validates configuration, checks Temporal workflow-service health and namespace access,
counts recently active workflow and activity pollers through a read-only task-queue inspection,
classifies aggregate current/other/unattributed/versioned build presence, reads
projection schema and backlog state, and writes, reads, deletes, and verifies cleanup of one random
object under the `_preflight/` prefix. It reports only fixed result codes, counts, and hashed
topology and deployment metadata; it does not start a workflow or print exception text. It can still run before
the worker starts, but missing pollers are then an explicit warning. A bounded consistency check
also compares active projection coordinators with their latest Temporal workflow status and reports
only the examined, stale, and total counts. Missing, closed, uninspectable, or partially inspected
coordinators are explicit warnings.

This is an operability sample, not qualification evidence. It does not change the adapter's
`prototype_unqualified` status; the disposable dependency and fault/restart gate remains the
authority for that promotion.

See the [Temporal adapter operator guide](../../docs/temporal-operator-guide.md) before adapting
this sample to a production topology.

For the isolated real-server conformance path, run `scripts/validate-temporal-container.sh` from
the repository root. That gate provisions disposable Temporal/PostgreSQL dependencies, composes a
per-run file-backed object store, runs the shared execution fixture plus native timer/signal and
artifact-offload cases, worker-process recovery, and a hash-verified projection restore into a
fresh database, emits a redacted result, and removes the created resources. It requires an
accessible Docker daemon; merely checking in or skipping the gate does not qualify the adapter.
