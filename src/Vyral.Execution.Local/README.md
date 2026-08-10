# Vyral.Execution.Local

`Vyral.Execution.Local` is the SQLite-backed execution runtime adapter for local development,
tests, and local deployments.

Current version label: `0.2.0`.

It implements `IExecutionRuntimeAdapter` using local SQLite state and local artifact files. It is
the default adapter for proving plugin behavior before moving to a remote orchestration target.

## Local Setup

```csharp
using Vyral.Execution;
using Vyral.Execution.Local;

IExecutionRuntimeAdapter runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
{
    DatabasePath = ".vyral/execution-runtime.sqlite",
    MaxActiveRuns = 100,
    MaxRetainedTerminalRuns = 500
});

runtime.RegisterPlugin(new YourPlugin());
```

For hosted apps, register the same runtime behind the provider-neutral interfaces:

```csharp
using Vyral.Execution;
using Vyral.Execution.Local;

builder.Services.AddVyralExecutionPlugin<YourPlugin>();
builder.Services.AddVyralLocalExecutionRuntime(new LocalExecutionRuntimeOptions
{
    DatabasePath = ".vyral/execution-runtime.sqlite"
});

// App code can depend on IExecutionRuntime or IExecutionRuntimeAdapter.
```

The adapter reports its active policy through `GetAdapterStatusAsync`, including list/history
limits, payload/result/artifact bounds, retention scope, resume policy, and concurrency-key
behavior.

It also implements `IExecutionRuntimeMaintenance`:

```csharp
var maintenance = (IExecutionRuntimeMaintenance)runtime;
var status = await maintenance.GetMaintenanceStatusAsync();
var preview = await maintenance.PruneAsync(new ExecutionMaintenancePruneRequest
{
    DryRun = true,
    RetainTerminalRuns = 100
});
```

Maintenance status reports local run counts, SQLite row counts, and local artifact directory
metrics. Prune requests default to dry-run; set `DryRun = false` only when intentionally deleting
terminal run-owned state beyond the retained terminal count.

## Runnable Sample

```bash
dotnet run --project samples/Vyral.Execution.LocalSample/Vyral.Execution.LocalSample.csproj -- --once
```

The sample creates a temporary SQLite database, registers a portable plugin, starts an idempotent
run, waits for terminal status, and prints status/history/artifact summaries.

## Notes

- Local SQLite is intended for local-first development and small local deployments.
- Handler code should still target `Vyral.Execution`; this adapter is a host/runtime choice.
- The package-consumer validation script checks the packed local adapter from a temp external app
  and verifies the current SQLite package graph has no known vulnerabilities from NuGet advisory
  sources used by `dotnet list package --vulnerable`.
