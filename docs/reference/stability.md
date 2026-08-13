# Stability and versioning

Vyral is pre-1.0 software. This document defines what the repository's maturity
labels mean, how the package lines are versioned, and what a consumer may infer
from a package, test, or qualification result.

## Stability labels

| Label | Consumer contract |
| --- | --- |
| `public` | Documented, covered by deterministic tests, and intended for ordinary use. During 0.x, incompatible changes are limited to minor releases and called out in release notes. |
| `preview` | Implemented and testable, but operational evidence or API experience is still incomplete. Shape and behavior may change in a minor release. |
| `prototype` | An evaluation surface with known gaps. It is not a portability or production-readiness claim and may change or be removed. |

Stability and qualification are separate dimensions. A package can expose a
`public` contract while one provider implementation is only `preview`; a
published adapter remains no more qualified than the evidence in
[`qualification/adapter-qualification.json`](../../qualification/adapter-qualification.json).

## 0.x compatibility policy

- Minor releases may contain breaking changes. Release notes identify contract,
  wire-format, migration, and configuration changes.
- Patch releases should remain source and wire compatible within their minor
  line, except when a security or correctness issue makes that unsafe.
- Deprecated public members normally remain for at least one minor release when
  a practical migration path exists.
- OpenAPI and [`contracts/public-sdk-surface.json`](../../contracts/public-sdk-surface.json)
  are the wire and SDK-surface authorities. Python, JavaScript, REST, and MCP do
  not define competing domain models.
- Public mutations either finish at a documented synchronous atomic boundary or return a durable
  `vyral.admission.v1` receipt. A `202` response always includes a polling `Location`; see
  [`design/admission-contract.md`](../../design/admission-contract.md).
- No 0.x package, image, or adapter implies a service-level commitment.

## Current version lines

| Line | Current source version | Scope |
| --- | --- | --- |
| Product and SDK | `0.3.x` | OpenAPI, public SDK catalog, Python and JavaScript clients, MCP, and NuGet projects without a deliberately separate line |
| Execution runtime | `0.2.x` | `Vyral.Primitives`, `Vyral.Execution`, execution adapters, worker client, and their current qualification evidence |
| Python runtime | `0.1.x` | Prototype Python-first full portable-local implementation; profile promotion remains evidence-gated |
| Go worker client | pre-v1 module | Source is under `clients/go`; a release tag must use the submodule form `clients/go/vMAJOR.MINOR.PATCH` |

The mixed 0.x lines are deliberate. Adding the broad SDK and stateless MCP
surface advanced the product line to 0.3 without relabeling execution evidence
that was produced for 0.2. A future execution minor release must regenerate its
qualification evidence and update all related package and descriptor versions
together.

Source versions do not assert registry availability. The first publication
cohort is intentionally small: core contracts, local runtime, local execution,
the server container, and the JavaScript HTTP client. The Python runtime is
designated for publication as `vyral`; the separate lightweight `vyral-client`
remains outside that cohort. Cloud and Temporal adapters must be labeled
`preview` or `prototype` in package and release notes until current live
evidence supports a stronger claim.

## Current surface classification

| Surface | Label |
| --- | --- |
| Local server, SQLite records/objects, retrieval, and RAG | `public` |
| Python and JavaScript HTTP SDK surface catalog | `public` |
| Python-native portable local runtime, REST host, and MCP adapter | `prototype` |
| Python Extropic registered-workload integration | `prototype` |
| Core .NET contracts | `public` |
| Stateless MCP endpoint | `preview` |
| Azure, AWS, Google, pgvector, Cloudflare, and specialized store adapters | `preview` unless the qualification report says otherwise |
| AWS, Google, and Temporal execution adapters | `prototype` |
| Dynamic external adapter discovery | Deferred; host composition is the supported workaround |

External adapters can be shipped in their own package today: reference the
Vyral contract package and wire the implementation at the host composition
boundary. Vyral does not yet scan directories or load arbitrary assemblies at
runtime. See the [adapter contributor guide](../contributing/adapter-contributor.md) for the supported
registration pattern and qualification requirements.

## Promotion requirements

A surface moves from `prototype` to `preview` only after its portable behavior
has deterministic conformance coverage and its limitations are documented. It
moves to `public` only after the contract is represented in the relevant public
catalog/schema, consumer examples exist, release gates exercise it, and the
maintainers are prepared to honor the 0.x compatibility policy.

An adapter qualification level changes only through a regenerated, reviewed
qualification artifact. Package publication, a successful local mock, or a
README statement is not qualification evidence.
