# Vyral documentation

The repository root contains the project overview, community policies, build
entrypoints, and public roadmap. Detailed product and operating material lives
here so each document has a clear audience and owner.

## Use Vyral

- [Consumer handoff](guides/consumer-handoff.md) — local startup, configuration,
  readiness, common workflows, and failure interpretation.
- [CanonicalStore](concepts/canonical-store.md) — strong-storage transactions,
  identity policy, export/restore, and provider qualification.
- [Generation-bound retrieval](concepts/generation-bound-retrieval.md) —
  immutable index generations, complete logical coverage, candidate hydration,
  and provider-specific evidence boundaries.
- [AI metering receipts](concepts/ai-metering.md) — signed runner/session
  observations, per-measurement provenance, receipt chains, and independent
  review boundaries.
- [Execution runtime limitations](reference/execution-runtime-limitations.md) —
  portable guarantees and adapter-specific boundaries.
- [Extropic execution](guides/extropic-execution.md) — experimental,
  fail-closed dispatch of registered Python workloads to Extropic compute.
- [Source-native and indexed retrieval](guides/source-native-retrieval.md) —
  choose and evaluate direct, lexical, vector, hybrid, and reranked retrieval.
- [Portable cutovers](guides/portable-cutovers.md) — hash-verified canonical
  migration, shadowing, cutover, rollback, and retained evidence.
- [Python host security](guides/python-host-security.md) — local and
  browser-facing host trust boundaries, strict options, and gateway controls.
- [Stateless MCP behind a gateway](guides/stateless-mcp.md) — two-instance local
  proof, header-aware routing, authorization, and production checks.
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
