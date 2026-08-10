# Vyral public roadmap

Vyral is developing in evidence-gated 0.x lines. This roadmap describes the
next public outcomes without promising dates or upgrading a surface merely
because an implementation exists. Current maturity and compatibility rules
remain authoritative in the [stability policy](docs/reference/stability.md).

## Now — source launch and distribution

- Publish a clean, reproducible public source tree with required baseline CI,
  protected release controls, private vulnerability reporting, and provenance.
- Publish the first small artifact cohort: core contracts, local execution,
  the local server container, and Python/JavaScript HTTP clients.
- Keep cloud adapters and the Python-native runtime at their current honest
  maturity until their promotion evidence is complete.
- Expand language-neutral admission, query, RAG, graph, and recovery fixtures
  whenever observable contract behavior changes.

## Next — operational evidence

- Establish at least one recurring, operator-owned live adapter qualification
  path and publish only redacted receipts.
- Complete Python 3.10–3.12 qualification on Linux, macOS, and Windows, plus an
  independent security sign-off, before promoting `vyral-runtime` from
  `prototype`.
- Strengthen upgrade, receipt replay, cancellation, lease-expiry, concurrent
  write, and disaster-recovery scenarios across maintained runtime dialects.
- Improve package installation, examples, diagnostics, and first-use latency
  based on public consumer feedback.

## Later — extensibility and scale

- Evaluate dynamic adapter discovery after the explicit host-composition model
  has enough real contributor evidence to justify a stable loading contract.
- Qualify distributed retrieval and execution topologies without weakening the
  distinction between implemented, locally conformant, and live qualified.
- Add performance tiers only when reproducible workloads and published evidence
  can support meaningful claims.

## Decision rules

- OpenAPI, the SDK catalog, public schema, and shared conformance fixtures are
  contract authorities; runtime-specific conveniences do not create new wire
  semantics.
- A mutation either completes at its documented atomic boundary or returns a
  durable, idempotent admission receipt.
- New adapters begin as `prototype` or `preview`. Availability, packaging, and
  local mocks never imply production qualification.
- Cross-runtime portability uses public envelopes, not private database files.

Concrete proposals and pull requests are welcome through
[CONTRIBUTING.md](CONTRIBUTING.md) and
[adapter contributor guide](docs/contributing/adapter-contributor.md).
