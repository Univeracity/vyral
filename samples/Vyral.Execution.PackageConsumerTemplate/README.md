# Vyral Execution Package Consumer Template

This is the copyable package-only shape for a .NET consumer. It uses NuGet package references,
not Vyral repo project references.

The template:

- creates a local SQLite execution runtime
- registers a portable plugin and handler
- starts an idempotent run
- reports progress
- writes a JSON artifact
- writes and reads a checkpoint
- reads run status, history, artifact summaries, artifacts by id/name, and maintenance dry-run

## Use With Local Packages

From the Vyral repo, create local packages first:

```bash
mkdir -p /tmp/vyral-packages
dotnet pack src/Vyral.Primitives/Vyral.Primitives.csproj --no-restore -o /tmp/vyral-packages
dotnet pack src/Vyral.Execution/Vyral.Execution.csproj --no-restore -o /tmp/vyral-packages
dotnet pack src/Vyral.Execution.Local/Vyral.Execution.Local.csproj --no-restore -o /tmp/vyral-packages
```

Then in a copied consumer project:

```bash
dotnet nuget add source /tmp/vyral-packages --name local-vyral
dotnet restore
dotnet run
```

The automated equivalent is:

```bash
scripts/validate-execution-runtime-package-consumer.sh
```

Plugin code should depend on `Vyral.Execution`; `Vyral.Execution.Local` is a hosting/runtime
choice for local development.

For a full handoff check across .NET package consumption plus Python and JavaScript HTTP
client helper coverage, run:

```bash
scripts/validate-execution-runtime-preview-handoff.sh
```
