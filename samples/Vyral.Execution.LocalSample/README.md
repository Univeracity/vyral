# Vyral Execution Local Sample

This console sample proves the consumer path for the execution runtime without external
resources.

It:

- creates a local SQLite-backed `LocalExecutionRuntime`
- registers a portable plugin that depends only on `Vyral.Execution`
- starts an idempotent run
- schedules a provider-neutral timer and raises a provider-neutral external event from inside the handler
- waits for terminal status
- prints the run result, history count, timer/event visibility, and artifact names

Run it:

```bash
dotnet run --project samples/Vyral.Execution.LocalSample/Vyral.Execution.LocalSample.csproj -- --once
```

Use an explicit database path when you want to inspect retained state:

```bash
dotnet run --project samples/Vyral.Execution.LocalSample/Vyral.Execution.LocalSample.csproj -- --db .vyral/local-sample.sqlite
```
