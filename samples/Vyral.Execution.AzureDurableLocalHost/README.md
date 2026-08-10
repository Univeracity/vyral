# Vyral Execution Azure Durable Local Host Skeleton

This folder is a local-only skeleton for Azure Functions host settings. It intentionally
does not include deployed Azure resource names, account keys, or service-specific settings.

The production contract remains SDK-neutral in `Vyral.Execution.AzureDurable`. A concrete
Functions app wraps Azure Functions bindings around:

- `AzureDurableExecutionRuntimeAdapter`
- `AzureDurableExecutionClient`
- `IAzureDurableExecutionOrchestrationScheduler`
- `IAzureDurableExecutionOrchestrationDriver`
- `AzureDurableExecutionHost`

Plugin code should still depend only on `Vyral.Execution`.

## Local Defaults

The clean local defaults are pinned by `AzureDurableLocalHostSmokeOptions` and mirrored here:

- `AzureWebJobsStorage=UseDevelopmentStorage=true`
- `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`
- `AzureFunctionsJobHost__extensions__durableTask__hubName=VyralExecutionLocal`
- `VYRAL_EXECUTION_AZURE_LOCAL_HOST_SMOKE=0`

Copy `local.settings.sample.json` to `local.settings.json` only for local development. Set
`VYRAL_EXECUTION_AZURE_LOCAL_HOST_SMOKE=1` when intentionally running a local smoke with Functions
Core Tools and local development storage.

The smoke path should skip cleanly when `func` or local development storage is unavailable. It
should not connect to deployed Azure resources by default.

## Intended Host Shape

```csharp
// HTTP or timer trigger:
// - create an ExecutionRunRequest
// - call AzureDurableExecutionRuntimeAdapter.StartRunAsync(...)

// Orchestrator function:
// - read AzureDurableStartCommand
// - call AzureDurableExecutionHost.OrchestrateAsync(...)
// - provide a replay-safe state driver over the Durable context

// Start activity:
// - call AzureDurableExecutionHost.StartRunWithReservationAsync(...)

// Step activity:
// - call AzureDurableExecutionHost.ExecuteOrchestrationStepAsync(...)
```

This skeleton is deliberately not a live cloud smoke. See
`samples/Vyral.Execution.AzureDurableFunctionsSmoke` for the deployable isolated gate.
