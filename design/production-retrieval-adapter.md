# Production Retrieval Adapter Qualification

## Status

This is the design gate for a future corpus-scale retrieval adapter. It does **not** claim that the
local SQLite record store, its continuation tokens, or any current cloud record store provides
distributed search semantics. A provider is selected only after it can prove this document's
requirements through the existing Vyral retrieval/evaluation surfaces plus adapter-specific
conformance evidence.

The portable boundary remains `IRecordCollectionStore`, `IRetrievalService`, `QueryEnvelope`, and
retrieval diagnostics. The provider may use a search engine, database, vector service, or a
coordinator, but must not leak its SDK types into consumer request shapes.

`vyral.evidence-brief.v1` is an optional CanonicalStore input artifact for fact-grounded
applications. Its immutable source snapshots, citations, counterevidence, uncertainty, and safe
retrieval-trace references can seed a derived retrieval projection, but it is not an answer model or
a truth-adjudication layer. Its canonical outbox event is a projection wake-up, never source-set
completeness evidence; use the immutable source-manifest and generation gates below before serving
any derived projection as authoritative.

## Required production shape

A candidate adapter must support, or explicitly capability-gate, all of the following:

- fielded lexical/BM25 retrieval and ANN vector candidate generation;
- pre-score partition, tenant, visibility, metadata-filter, and controlled-term phrase filtering;
- weighted or reciprocal-rank fusion with a versioned profile;
- bulk idempotent projection writes, immutable generation/alias switching, and freshness/lag
  diagnostics;
- bounded search-after continuation, evaluation, trace, and result payloads;
- provider-neutral retrieval profiles and the same result/diagnostic separation used locally.

Controlled-term phrase plans are request data, not query-string concatenation. An adapter resolves
known phrase alternatives, intersects required groups before scoring, records matched/resolved
terms in safe diagnostics, and deliberately takes the configured broad lexical fallback when a
group is unknown. It must apply visibility and tenant restrictions before both lexical and vector
candidate generation.

## Distributed candidate and score contract

For a fan-out request, the adapter publishes (in a capability/evaluation receipt) the local
per-shard/source candidate cap, transport cap, coordinator cap, final re-score cap, selected-shard
count, profile version, and comparison method. A coordinator must not treat raw shard-local BM25
or vector scores as a globally comparable cutoff unless the provider proves global statistics and
score semantics.

One of these qualification paths is required for each source:

1. provider-global term statistics and comparable scores;
2. a versioned coordinator calibration or exact re-score over the bounded candidate union; or
3. a separately evaluated rank-fusion path.

Candidate inclusion is measured before calibration/re-score, because later work cannot restore a
document dropped by a local cap. Receipts report mean and minimum inclusion against a global
reference, final rank overlap/order agreement, all candidate bounds, and a representative
filter/ACL/tenant slice. A profile that uses raw local score comparison without this evidence is
invalid for global cutoff decisions.

### Hybrid RRF

For distributed hybrid retrieval, retain each source's candidate list across the selected shard
set. Apply RRF only from provider-global source ranks or an evaluated coordinator stage that
reconstructs those ranks. Shard-local rank resets are an invalid diagnostic, not an approximate
global RRF implementation. The qualification descriptor binds source ids, profile/weight versions,
`rrfK`, local/source/transport/global caps, and the comparison receipt.

## Immutable projection and replica eligibility

Every routable projection generation has an immutable descriptor containing:

- provider/shard topology and normalized retrieval profile identity;
- source-manifest identity and digest, release-set identity, and source record/revision-set
  completeness evidence;
- expected document count and ID-set (or equivalent cryptographic completeness) digest;
- index schema/configuration and build receipt digests; and
- creation time and generation identifier.

A replica is eligible for a pinned generation only when its immutable evidence matches that
descriptor. Health, process liveness, or an open index file are mutable observations and cannot
substitute for completeness. A stale, wrong-generation, or incomplete replica is bypassed. If no
eligible replica covers a required shard, the request fails with a coverage error or takes a
separately documented authoritative fallback; it must not silently return a partial corpus result.

This is an adapter/coordinator diagnostic shape, not a new required portable index-file format.
The local SQLite projection journal remains an adapter-local recovery identity; it is not a
cross-store completeness fence, and an at-least-once CanonicalStore outbox high-water is not used
as one.

## Generation-pinned continuation

A distributed adapter that advertises stable continuation uses an opaque, authenticated cursor
that binds:

- a privacy-safe invariant request fingerprint (including filter, partition/visibility, profile,
  sort, and selected shard set);
- immutable generation/profile identities;
- the bounded provider/coordinator search-after tuple; and
- an expiry and cursor-format key/version.

The cursor is integrity protected; mutation, tampering, or an incompatible request returns an
explicit invalid-continuation response. After an alias switch, a valid unexpired cursor continues
only against its retained old generation/route namespace. If the generation is retired or the
cursor expires, the adapter returns an explicit generation-unavailable/expired-continuation result
and the consumer starts a fresh first page. It never mixes pages from generations or route tables.

Current local continuation tokens remain adapter-local paging mechanics. They must not be presented
as these mutation/rebalance-safe portable cursors.

## Qualification and rollout gate

Before a provider becomes selectable for production profiles, its implementation must supply:

1. exact global reference evaluations across the intended shard counts, filters, tenant/ACL slices,
   lexical, vector, and hybrid profiles;
2. low-candidate-cap failure tests proving the receipt detects missing candidates, plus successful
   caps that preserve the stated candidate-inclusion target;
3. score-calibration and global-RRF tests that reject local-score merges and shard-local rank
   resets;
4. dual-write, alias switch, shard-count transition, duplicate/revision-conflict, and
   generation-pinned continuation tests;
5. stale/wrong/incomplete replica routing and no-eligible-replica coverage tests; and
6. operational load, failure, permission, retention, backup, and restore rehearsals.

The adapter reports the chosen qualification path and receipt ids in readiness/evaluation
diagnostics. It is disabled or falls back to an authoritative safe path when evidence is missing.

## Implementation sequencing

1. Select a provider and add a narrow adapter package with no new public provider-specific request
   types.
2. Implement projection lifecycle and a read-only capability/readiness report.
3. Implement single-shard fielded lexical/vector/hybrid conformance.
4. Add a coordinator only with the calibration, RRF, replica-evidence, and cursor gates above.
5. Run consumer corpus qualification before enabling a production profile by default.

Until step 1 names and provisions a concrete provider, this design intentionally does not expand
the core public retrieval contract with unimplementable distributed promises.
