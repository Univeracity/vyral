# Vyral.Execution.AzureDurable

`Vyral.Execution.AzureDurable` is the Azure Durable Functions adapter target for the execution
runtime.

Current version label: `0.2.0`.

The assembly is SDK-neutral. It provides a Durable Functions dialect, host bridge, registry,
injected scheduler/driver contracts, and an in-memory status store used by tests. The optional
`Vyral.Execution.AzureDurable.Functions` package wraps Microsoft Durable Task bindings outside the
core `Vyral.Execution` contract.

`Vyral.Azure.AzureCosmosExecutionStatusStore` is the available persistent status-store
implementation. It atomically reserves run ids, persists run-owned state in a run partition, and
uses ETag conditions for named leases and status-write fencing, so a terminal or cancellation
transition cannot be overwritten by a stale activity completion. It is independently live-tested
against Cosmos DB, but it
does not itself provide Azure Functions attributes, a task hub, or a deployed worker host.

`samples/Vyral.Execution.AzureDurableFunctionsSmoke` is the concrete Functions composition and
isolated deployment gate. Its replayed orchestrator makes only Durable Task calls: a start
activity reserves or rehydrates the run, and each step activity performs persisted transitions and
handler execution. That separation is required because an orchestration replay must not directly
perform Cosmos I/O or invoke arbitrary handlers.

An idempotent start replay for an active run also re-invokes the scheduler. Concrete schedulers
must treat that as an ensure operation, not a request to create a second provider workflow. The
Functions bridge deduplicates provider instances in `Pending` and `Running` states; this recovers
the otherwise ambiguous case where run reservation committed but the caller did not receive a
successful provider-scheduling response.

## Stable Surface Versus Provider Ownership

Stable for host integration:

- `AzureDurableExecutionRuntimeAdapter`
- `AzureDurableExecutionOptions`
- `AzureDurableLocalHostSmokeOptions`
- SDK-neutral command/result/status DTOs used by the host bridge

Provider-owned composition details:

- dialect helper details
- orchestration scheduler and driver wrapper shape
- status-store implementation choices
- concrete Azure Table/Blob/Cosmos persistence mapping
- real Functions binding, packaging, deployment, and scaling details

Plugins should still depend only on `Vyral.Execution`. Azure concepts must stay behind the adapter
or host app boundary.

The package is safe for local dialect validation. Any cloud deployment remains a host integration
project that supplies handler registration, resource settings, credentials, scaling, and
operational validation. The Functions bridge advertises `durable.waits`: a handler-side timer or
external-event wait atomically records the waiting run, returns from the Durable activity, and is
awaited by the replayed orchestrator with native Durable Task primitives. The step activity stores
the wake outcome before the handler is replayed. A handler must checkpoint every non-idempotent
side effect before calling either wait method, because resumption re-executes the handler from its
start.

## Local-Only Smoke Boundary

Use `AzureDurableLocalHostSmokeOptions` when preparing a local Functions smoke path. It emits and
validates clean defaults only:

- `AzureWebJobsStorage=UseDevelopmentStorage=true`
- `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`
- `AzureFunctionsJobHost__extensions__durableTask__hubName=VyralExecutionLocal`
- `VYRAL_EXECUTION_AZURE_LOCAL_HOST_SMOKE=0` by default

It rejects cloud-style storage connection strings, renamed storage settings, and non-default task
hub names. Unit tests inspect this settings contract without starting Functions Core Tools or
connecting to Azure resources.

See `samples/Vyral.Execution.AzureDurableLocalHost` for the local-host skeleton.
See `samples/Vyral.Execution.AzureDurableFunctionsSmoke` for the concrete cloud gate.
