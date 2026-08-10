# Vyral — The Conformance Invariant

## The Rule

> Conformance is a gate, not a feature.

No provider, capability, transport, or execution target ships as part of Vyral without a passing qualification suite.

This is not a quality bar. It is the definition of what "ships as part of Vyral" means.

---

## Why This Rule Exists

Vyral's primary value is not abstraction. Abstraction can be replicated. What cannot easily be replicated is a continuously executable corpus of proof that the abstraction remains valid as both the implementation and the ecosystem evolve.

An unqualified capability is anti-coherence. It claims the Vyral contract while providing no evidence that the contract holds. Over time, unqualified capabilities do not merely weaken individual providers — they degrade trust in the system as a whole. The project can become worse by growing.

The rule exists because the moat is the evidence, and evidence requires execution.

---

## What Qualification Means

Qualification is not documentation. It is not a test that was written and ran green once. It is:

1. **A conformance suite** — an executable set of behavioral assertions that any correct implementation of the interface must satisfy, written against the portable abstraction, not against any specific backend.

2. **A passing run** — the suite must pass against the implementation before the capability is considered shipped. For live-infrastructure providers (cloud storage, cloud databases, cloud workflow engines), this means the suite must have run against real infrastructure, not mocks.

3. **A repeatable signal** — the suite must be structured so that regressions are detectable. A conformance test that cannot be re-run against a real target is not a conformance test.

---

## What This Looks Like in Practice

**Storage providers** follow the `RecordCollectionStoreConformanceTests` and `ObjectStoreConformanceTests` pattern. SQLite, Pgvector, Cosmos, AlloyDB, and future providers each run the full suite. Skips are permitted only for infrastructure availability (e.g., `[CosmosLiveFact]` skips when no connection string is configured), not for behavioral gaps.

**Provider targets** (CLI, API, agent) follow the `ProviderQualificationPlanner` pattern. Qualification smoke requests are defined per capability and mode. A provider that cannot produce passing qualification probes for a claimed capability is not a provider — it is a stub.

**Execution fabric adapters** will follow a `WorkflowConformanceTests` pattern analogous to storage conformance. A Temporal adapter, a Durable Functions adapter, and a Step Functions adapter each must prove they can execute the portable workflow definitions the abstraction describes.

---

## The Scale Question

> Can conformance scale at the same rate that capability scales?

Yes. We will see to it that it does, or the capability does not ship.

This commitment has a practical corollary: the cost of adding a new provider is not the adapter code. The adapter code is straightforward. The cost is writing and running the conformance suite. Any roadmap estimate that does not account for this cost is wrong.

---

## What This Means for Contributors

When adding a new backend, provider, capability, or transport:

1. Write the conformance suite before or alongside the implementation, not after.
2. The suite must be runnable by anyone with access to the target infrastructure. Infrastructure-gated tests use the established skip-attribute pattern; they do not simply not exist.
3. Behavioral gaps between the new implementation and the portable contract are bugs, not configuration notes. Either fix the gap or narrow the contract claim.
4. A PR that adds a new provider without a conformance suite will not be merged regardless of how complete the adapter code is.

---

## Relationship to the Broader Thesis

Vyral's invariant is coherence. The conformance system is how that invariant is enforced mechanically.

Without conformance gates, the abstraction layer accumulates unverified promises. Promises are fragile under ecosystem change. Providers update APIs, deprecate behaviors, and diverge from documentation. An abstraction layer that relies on documentation rather than execution to maintain its contract will eventually fail in production, silently, in ways that are hard to attribute.

The conformance invariant is what separates Vyral from an abstraction layer that merely exists from one that can be trusted.

Trusted infrastructure does not ask you to believe the contract holds.

It shows you.
