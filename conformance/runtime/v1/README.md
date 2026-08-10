# Portable runtime conformance v1

This directory is the language-neutral behavioral evidence shared by Vyral
runtime implementations. OpenAPI and JSON Schema define wire shape; these
fixtures define deterministic observable examples where two correct-looking
implementations could otherwise drift.

Admission fixtures pin deterministic receipt identity, replay provenance,
one-way idempotency-key correlation, and rejected-admission details. Stateful
execution fixtures separately prove that replay resolves to the same durable
run and that conflicting reuse is rejected.

`manifest.json` is the authority for the bundle. Every scenario is identified
by profile and kind and is protected by a SHA-256 digest. Runtime packages may
embed an exact copy, but must not edit the packaged copy independently.

## Changing portable behavior

A change to accepted values, defaults, ordering, continuation, hashing, failure
classification, lifecycle transitions, authorization classes, or public
import/export behavior must:

1. add or update a language-neutral scenario, or cite an existing scenario
   that already proves the behavior;
2. run the scenario against every affected maintained runtime dialect;
3. update `manifest.json` and its digest;
4. synchronize embedded runtime copies and artifact-layout checks; and
5. version the fixture contract when mutating previously released semantics.

An implementation-specific exception must be reviewed, documented in
capability/readiness disclosure, and must not masquerade as portable behavior.

Scenarios contain no Python or .NET type names. Exact adapter-owned tokens,
timestamps, and diagnostics are asserted by portable invariants unless their
bytes are themselves public contract. Stateful scenarios use isolated stores
unless persistence or restart is the behavior being tested.

Run the canonical validation and both current record dialects with:

```bash
python3 scripts/verify-runtime-conformance.py
dotnet test tests/Vyral.Tests.Local/Vyral.Tests.Local.csproj \
  --filter FullyQualifiedName~PortableRuntimeRecordFixtureTests
```

The current high-churn set includes compound filters and continuations, RAG
chunk/manifest/plan hashes, graph record mapping, admission receipts, and execution
rejection/failure/retry/cancellation/restart semantics. Next targets are prompt
and context hashes, graph truncation, lease expiry, concurrent write conflicts,
and additional recovery boundaries.
