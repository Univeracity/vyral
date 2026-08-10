# Vyral documentation

The repository root contains the project overview, community policies, build
entrypoints, and public roadmap. Detailed product and operating material lives
here so each document has a clear audience and owner.

## Use Vyral

- [Consumer handoff](guides/consumer-handoff.md) — local startup, configuration,
  readiness, common workflows, and failure interpretation.
- [CanonicalStore](concepts/canonical-store.md) — strong-storage transactions,
  identity policy, export/restore, and provider qualification.
- [Execution runtime limitations](reference/execution-runtime-limitations.md) —
  portable guarantees and adapter-specific boundaries.
- [Stability and versioning](reference/stability.md) — maturity labels, version
  lines, and compatibility promises.

## Extend and operate Vyral

- [Adapter contributor guide](contributing/adapter-contributor.md) — adapter
  contracts, conformance, capability claims, and qualification evidence.
- [Release process](maintainers/releasing.md) — build evidence, publication
  gates, provenance, and registry requirements.
- [Temporal operator guide](temporal-operator-guide.md) — supported Temporal
  topology and operational boundaries.

## Protocol and design references

- [Design documents](../design) define the public SDK, admission, execution,
  MCP, provider, and runtime architecture.
- [Conformance fixtures](../conformance) provide language-neutral executable
  evidence for portable behavior.
- [ROMAN explainer](roman.md) describes the graph-envelope conventions used by
  Vyral's graph and GraphRAG surfaces.
