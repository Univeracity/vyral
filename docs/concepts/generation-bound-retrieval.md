# Generation-bound retrieval projections

Vyral's optional generation-bound projection contract lets a derived search index prove exactly
which immutable generation it is serving and whether that generation completely covers every
requested logical partition. It is intended for retrieval paths where a healthy endpoint or a
successful query is not enough evidence that the index is complete, current, or internally
consistent.

The baseline `IRecordSearchProjection` remains the simpler eventual-projection seam. Implement
`IGenerationBoundRecordSearchProjection` only when the provider can support the stronger evidence
model.

## Portable boundary

A generation descriptor binds:

- the collection, generation, provider, profile, and strategy;
- source-manifest and record-revision-set digests;
- projection-schema, analyzer, and configuration digests;
- expected item count and logical partitions;
- capabilities and content-addressed artifact declarations; and
- the descriptor's own canonical SHA-256 digest.

Search selects the active generation or an explicitly named retained generation. A caller can also
fence the expected descriptor digest. Incomplete, unavailable, retired, late, or substituted
generations fail closed with no candidates. An adapter that emits a continuation must authenticate
it and bind it to the exact generation, descriptor, invariant request, expiry, and page boundary.

Search results contain candidate identities, canonical record revisions, and scores—not canonical
records or an authorization decision. `SearchGenerationAndHydrateAsync` reads candidates from the
canonical store and discards any whose stored revision no longer matches. The application still
owns tenant authorization, eligibility, final ranking, abstention, and adoption policy.

Provider index names, SDK objects, endpoints, credentials, health checks, and physical shard or
replica topology remain outside the portable descriptor. A provider-private index format may change
without changing the public contract.

## Building a generation

`IRecordSearchProjectionGenerationBuilder` is a build-and-verify seam, not a portable lifecycle
manager. It writes provider-native parts through an `IObjectStore` and returns a compact verified
descriptor receipt. `ExecutionRuntimeProjectionGenerationAdapter` can admit that build as durable,
idempotent Vyral work, retain progress and terminal evidence, and avoid adding a Vyral network hop
to the query path.

Activation, rollback, and retirement remain host-owned. More provider evidence is required before
those operations can be promoted into a credible portable contract.

## Implementations

| Shape | Current evidence boundary |
| --- | --- |
| Local exhaustive projection | Deterministic reference implementation and shared lifecycle conformance |
| OpenSearch exact-index projection | Local OpenSearch data-plane conformance; managed AWS IAM, networking, scaling, and availability remain unqualified |
| Cloudflare Worker/R2 with direct R2 binding | Deterministic Miniflare conformance for the exact Worker source |
| Cloudflare Worker/R2 with isolated service reader | Deterministic Miniflare conformance plus private, opaque consumer validation of the exact Worker and reader components |

The Cloudflare service-reader topology gives the public query Worker no R2 binding. A separate
non-public Worker alone binds the bucket and exposes only an authenticated, allowlisted read
operation over a service binding. The query Worker fails closed when neither or both read modes are
configured. See the [reference implementation and proof harness](../../experiments/worker-r2-generation-projection).

The consumer validation used a small synthetic corpus and does not establish representative
traffic, production capacity, availability, cost, or provider observability. The public evidence
reference is intentionally opaque:
`urn:vyral:private-consumer-evidence:sha256:cdc973c71ac0abba92dd2ec0fa3eb016b4e35233e9a059bf96f6b97b93a197da`.
The exact live-exercised source components are:

- query Worker: `sha256:e9311869f2e671397b6f2a017882002bde33b46b9a78d4367d3e7ea192f0d6fc`
- object reader: `sha256:9fe0cba9c743210314ef6a91d9306a867527a6267068f75108fd5d868bd15a28`

Consumer identities, repositories, commands, deployment identifiers, hostnames, generation names,
and private receipt locations are not public qualification data. Maintainers keep the identity map
outside the repository unless a consumer separately authorizes disclosure.

## API status

The descriptor, request, result, inspection, build, and receipt shapes are covered by JSON Schema
and language-neutral conformance fixtures. The .NET abstraction and local/OpenSearch implementations
are available for host composition. Python reproduces the shared descriptor and lifecycle fixtures.
The generation-aware retrieval-evaluation target remains an in-process host seam and is deliberately
excluded from REST and peer-SDK serialization until a versioned public wire surface is reviewed.

Adapter qualification and generation rollout are separate evidence planes: adapter qualification
says a topology can implement the contract, while each generation receipt proves one exact
generation was built and verified. Neither a healthy process nor an adapter's qualification makes
an individual generation complete.
