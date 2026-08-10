# Developer Adoption and Evidence-Gated Growth Design

**Status:** proposed

**Date:** 2026-08-10

**Decision scope:** developer entry experience, MCP positioning, migration
blueprints, qualification presentation, and the sequencing of the next public
adoption phase.

## Decision

Vyral will treat its next phase as an adoption-design problem rather than a
capability-expansion problem.

The repository already contains a broad contract surface, local runtimes,
provider adapters, language-neutral conformance fixtures, and versioned
qualification evidence. The next objective is to turn those assets into a
repeatable path by which an individual developer:

1. gets a useful local result with minimal setup;
2. encounters Vyral through a timely integration surface such as stateless MCP;
3. applies Vyral to a concrete migration or durability problem; and
4. can inspect executable evidence before trusting a provider-backed path.

Four coordinated wedges will serve that objective:

| Wedge | Role |
| --- | --- |
| Local single-player experience | Establish the developer habit |
| Stateless MCP | Create discovery and demonstrate the deployment boundary |
| Executable migration blueprints | Convert acute infrastructure pain into adoption |
| Qualification evidence | Earn trust and sustain ecosystem participation |

These wedges are not independent product categories. They are stages of one
adoption loop. MCP is an entry surface over Vyral services, migration
blueprints demonstrate the value of the contracts, and qualification evidence
limits the resulting claims.

Vyral will not position itself primarily as "Terraform for RAG." That analogy
may explain provider adapters and retained optionality, but Terraform provisions
infrastructure from desired state while Vyral defines application-facing state,
retrieval, admission, and execution behavior across infrastructure. The primary
category remains a provider-portable application substrate.

## Context

Vyral's current architecture already supports the substance of this strategy:

- local SQLite records, traces, canonical state, and durable execution;
- filesystem object storage and deterministic local provider shapes;
- retrieval, RAG, citations, GraphRAG, and evaluation;
- synchronous completion and receipt-bound asynchronous admission contracts;
- peer .NET and Python runtime implementations with shared conformance fixtures;
- Python, JavaScript, and Go consumer surfaces;
- a stateless MCP `2026-07-28` endpoint with durable Tasks and multi-instance
  conformance coverage; and
- a checked-in execution-adapter qualification report with versioned evidence.

The current friction is not an absence of capability. It is the amount of
context a new developer must absorb before reaching the first compelling
result. The root [quick start](../README.md#quick-start) asks the developer to
start the .NET server from a source checkout and then run a client example. The
[Python runtime](../runtimes/python) can remove that server and toolchain
dependency, but it remains a prototype until its promotion evidence and review
are complete. Package publication is also intentionally withheld.

The qualification system has a related presentation gap. Its evidence is
machine-readable and release-gated, but the public summary currently covers
execution adapters specifically. It must not be presented as universal
qualification for every storage, retrieval, runtime, and protocol surface.

The MCP `2026-07-28` revision makes the timing favorable. Protocol requests are
self-describing, carry standardized routing headers, and can land on any healthy
instance without a protocol session. That does not remove application state.
It makes application state and durable task identity explicit, which is the
boundary Vyral already owns.

## Goals

- Deliver a useful local result from one supported-language installation path
  in five minutes or less on a clean supported workstation.
- Make Python the primary embedded, single-player entry path without demoting
  the .NET server as the reference provider-capable host.
- Demonstrate why durable application state matters behind stateless MCP.
- Publish migration guidance as executable, reversible rehearsals rather than
  configuration-only claims.
- Generate public qualification views from checked-in evidence without
  broadening the scope of any recorded claim.
- Preserve OpenAPI, public schema, operation catalog, admission contracts, and
  conformance fixtures as the behavioral authorities.
- Use external developer feedback to choose subsequent capability work.

## Non-goals

- Provisioning cloud infrastructure or competing with infrastructure-as-code
  systems.
- Becoming a generic MCP server framework or exposing every REST operation as
  a tool.
- Promoting the Python runtime, an adapter, or a package merely to improve the
  quick-start story.
- Claiming that an already provider-coupled application can migrate through a
  configuration edit alone.
- Bundling a large model or creating a hidden network dependency in the
  deterministic local path.
- Flattening distinct storage, retrieval, execution, runtime, and MCP evidence
  into one misleading readiness score.
- Adding adapters before the existing local and migration experiences establish
  a demonstrated consumer need.

## Adoption model

### Stage 1: establish a local habit

The first experience must be useful to one developer on one machine. It must
not require a cloud account, external service, .NET installation, Docker, or a
Temporal cluster when the Python embedded path is selected.

The supported golden path will require no more than:

1. one package installation;
2. one entry command or one short Python file;
3. one explicit durable data directory; and
4. one command to inspect readiness and explain the active local providers.

Exact command naming remains a packaging decision. The design does not require
the package name, module name, and console command to be identical. Whatever
names are selected must make the distinction between `vyral-client` and the
embedded `vyral-runtime` obvious.

The golden path will contain two connected results.

#### Retrieval result

The developer creates a collection, ingests a small but non-trivial corpus,
retrieves bounded context, and prints citations and diagnostics. The example
must distinguish mechanical determinism from semantic quality. A deterministic
embedding fixture may prove contract behavior, but its output must not be
marketed as meaningful semantic retrieval.

An optional local semantic profile may use an explicitly installed and cached
model. The deterministic path must remain functional after installation with no
network and must not silently download model assets.

#### Durable-execution result

The developer defines a small `@vyral(...)` handler, starts it with an
idempotency key, receives a durable run receipt, stops the process, reopens the
same local data directory, and observes the same run identity and terminal
result. The demonstration should make the acceptance boundary visible rather
than merely printing a successful function return.

Both results must leave inspectable state beneath one documented directory and
must provide a safe reset instruction that names the exact directory it affects.

### Stage 2: attach discovery to stateless MCP

The MCP message is:

> Durable state and execution behind stateless MCP.

Vyral is not differentiated by parsing MCP requests. It is differentiated by
preserving canonical state, authorization policy, durable admission, task
identity, and provider portability after transport-level session state is
removed.

The reference deployment will include:

- two identical Vyral instances behind a non-sticky round-robin proxy;
- gateway-visible `Mcp-Method` and `Mcp-Name` routing fields;
- authentication and coarse gateway policy followed by authoritative Vyral
  authorization;
- a bounded read tool and an opt-in durable mutation tool;
- a durable task handle returned from the mutation;
- status or input continuation reaching a different instance;
- removal and rejoining of an instance without loss of task identity; and
- the frozen official profile plus Vyral's policy, failover, task, and telemetry
  fixtures.

The demonstration must use the production container shape and emit a redacted,
commit-bound receipt. It must not depend on hidden process-local state or a
sticky-session exception.

Public language will use `conformant`, `qualified`, or the exact recorded level.
`Battle-tested`, `production-ready`, and similar claims require consumer and
live evidence that the current repository does not yet record.

### Stage 3: convert pain through executable migration blueprints

A migration blueprint is a versioned rehearsal with runnable preflight and
validation commands. It is not an article that ends after changing an adapter
name.

Every blueprint will contain:

1. **Starting boundary** — the source of truth, provider assumptions, and the
   behavior currently coupled to the application.
2. **Portable target** — the Vyral operations and envelopes the application
   will own after adoption.
3. **Preflight** — identity, schema, capability, volume, and unsupported-shape
   checks that do not mutate either side.
4. **Transfer or rebuild** — export/import, re-ingestion, projection rebuild, or
   dual-write mechanics with explicit authority.
5. **Shadow validation** — deterministic comparisons, retrieval evaluation,
   receipts, or lifecycle scenarios appropriate to the surface.
6. **Cutover** — the smallest reviewed change that transfers live authority.
7. **Rollback** — the preserved source state, trigger, and bounded reversal
   procedure.
8. **Evidence** — redacted outputs, versions, commit identity, and cleanup.

The first blueprint cohort will be:

1. local SQLite records and retrieval to PostgreSQL/pgvector;
2. an in-process or best-effort background task to receipt-bound local durable
   execution; and
3. a session-dependent MCP deployment to stateless MCP with durable Tasks and
   non-sticky routing.

A managed-vector-provider-to-pgvector blueprint should follow only after a
specific source integration can support an honest export, normalization,
comparison, and rollback story.

The documentation will distinguish two costs:

- **adopting Vyral** around a provider-coupled application is a controlled
  migration; and
- **re-homing an application already using Vyral contracts** may reduce to an
  adapter change plus a rehearsed data or execution cutover.

This distinction prevents the phrase "configuration swap" from concealing the
initial mapping and authority-transfer work.

### Stage 4: turn qualification into inspectable trust

Qualification will be surfaced as an evidence ledger, not a marketing score.
The public view must be generated from the checked-in artifact and show, for
each claim:

- adapter or implementation identity;
- capability set and explicit omissions;
- qualification level and environment class;
- status and freshness window;
- adapter and contract versions;
- tested commit;
- evidence type and reproduction command; and
- failures, expiration, or unsupported behavior without visual suppression.

The existing report remains explicitly an execution-adapter report. Storage,
retrieval, runtime, MCP, and package evidence may gain parallel views that share
presentation conventions, but they retain their distinct schemas and promotion
requirements until a reviewed common evidence envelope can represent them
without losing meaning.

Public site summaries, documentation tables, adapter READMEs, and release notes
must be derived from the same source artifact. Hand-edited maturity summaries
must fail repository or site verification when they diverge.

Adapter contributors should be able to run the relevant conformance profile,
generate a candidate receipt, and see why a requested qualification level was
rejected before opening a pull request. Ecosystem trust depends on the
contributor experience as much as the visual matrix.

## Positioning system

### Primary category

> Provider-portable application substrate.

Supporting explanation:

> Build stateful AI applications locally. Expose selected capabilities through
> stateless MCP. Change infrastructure without changing the application
> contract.

### Entry-specific messages

| Entry | Message |
| --- | --- |
| Local development | Exercise the real contract before choosing the provider |
| MCP | Durable state and execution behind stateless MCP |
| Migration | Adopt once; rehearse future cutovers |
| Qualification | Portability claims backed by versioned evidence |

### Analogy boundary

"Terraform for RAG and durable execution" may be used as an informal analogy
when immediately qualified: providers implement a stable abstraction and
conformance limits compatibility claims. It must not appear as the product
category, tagline, package description, or an implication that Vyral provisions
cloud resources.

### Claim discipline

The following language requires recorded evidence:

- production-ready;
- battle-tested;
- provider-interchangeable;
- configuration-only migration;
- multi-cloud parity; and
- complete MCP support.

When evidence is narrower, copy must name the implemented surface, environment,
and qualification level.

## Delivery sequence

### Phase A: package-quality local path

- Complete the Python 3.10–3.12 Linux, macOS, and Windows qualification matrix.
- Complete independent Python runtime security review and resolve promotion
  findings.
- Verify clean installation, import, package contents, upgrade, and uninstall
  behavior from built artifacts.
- Produce the retrieval and durable-restart golden examples.
- Measure first-use latency on supported clean environments.
- Publish only after the existing trusted-publication and provenance gates are
  authorized.

### Phase B: stateless MCP reference deployment

- Package the two-instance proxy demonstration.
- Run official, Vyral-specific, and production-container conformance.
- Publish the architecture, exact policy boundary, and redacted receipt.
- Link MCP discovery material to the local golden path rather than to the full
  repository map.

### Phase C: migration blueprint cohort

- Implement the three initial blueprints from disposable starting state.
- Run each blueprint in CI where deterministic and as an explicit live gate
  where credentials or managed infrastructure are required.
- Verify cutover and rollback independently.
- Record unsupported starting shapes rather than approximating them.

### Phase D: evidence presentation and feedback loop

- Generate the public qualification view directly from repository evidence.
- Add evidence links to adapter documentation and release notes.
- Conduct clean-room quick-start and blueprint sessions with external
  developers.
- Use observed friction and real integration requests to choose the next
  adapter or contract investment.

Phases may overlap, but Phase A owns the default call to action. MCP interest
that lands before the local path is consumable is likely to produce repository
traffic rather than durable adoption.

## Acceptance criteria

### Local experience

- A clean supported Python environment reaches its first citation in at most
  five minutes, excluding an explicitly chosen semantic-model download.
- The embedded path requires no .NET SDK, container runtime, cloud account, or
  external service.
- The durable example returns a receipt before work is described as accepted.
- Restarting against the same data directory preserves run identity and result.
- Readiness explains active local providers and material limitations.

### MCP demonstration

- The official frozen profile passes with no unreviewed expected failures.
- Requests are distributed across at least two instances without session
  affinity.
- Task continuation, cancellation, and input resumption succeed after the
  admitting instance is removed.
- Gateway header/body disagreement is rejected.
- Vyral authorization remains authoritative after gateway policy.
- The production-container receipt is commit-bound and redacted.

### Migration blueprints

- Every blueprint starts from disposable documented state and ends with no
  unreported resources.
- Preflight is non-mutating.
- Shadow validation contains a surface-appropriate correctness threshold.
- Cutover and rollback are independently executable.
- The blueprint names data authority before, during, and after transfer.

### Evidence presentation

- The rendered view can be reproduced from checked-in evidence without manual
  maturity edits.
- Expired and failed evidence remains visible.
- Execution evidence is not represented as storage, retrieval, runtime, or MCP
  qualification.
- Every displayed count and level is covered by site and repository drift tests.

## Measures

Vyral will prefer outcome measures over repository attention metrics:

- clean-install success across supported platforms;
- median time to first citation;
- median time to first durable receipt and restart recovery;
- blueprint completion and rollback success;
- number of external applications completing the golden path;
- number of externally reported unsupported shapes resolved explicitly; and
- number of independently maintained adapters producing current conformance
  evidence.

Stars, impressions, and raw package downloads may describe reach, but do not
establish adoption or trust. The default local runtime will not add behavioral
telemetry merely to measure these outcomes. Clean-room sessions, opt-in surveys,
issues, package statistics, and public conformance receipts are sufficient for
the initial phase.

## Risks and mitigations

### The local path looks capable but produces low-quality retrieval

Deterministic embeddings validate mechanics, not semantic usefulness. The
quickstart must say so and provide an explicit optional semantic profile without
making a model download implicit.

### MCP attention narrows the perceived product

MCP material will describe a transport and distribution wedge over the same
REST-authoritative services. Canonical state, admission, and execution remain
the product boundary.

### Migration copy overpromises

Blueprints separate adoption from later re-homing and require authority,
validation, and rollback sections. A configuration-only claim cannot be made
without an executable rehearsal supporting it.

### Qualification presentation becomes decorative

All public views are generated from versioned evidence. Unsupported, stale, and
failed states receive first-class rendering and cannot be manually hidden.

### Additional adapters consume the adoption cycle

New adapter work requires either a blueprint dependency, a concrete external
consumer, or a qualification gap that blocks an existing claim.

### Python promotion is rushed to satisfy the strategy

The strategy depends on the existing platform, upgrade, packaging, security,
and independent-review gates. Adoption pressure does not waive them.

## Rejected alternatives

### Lead with enterprise portability

Enterprise architecture explains the long-term value but asks a new developer
to trust the abstraction before experiencing it. The local path must establish
the habit first.

### Lead with a universal multi-cloud matrix

Current evidence is intentionally surface- and environment-specific. A broad
matrix would make the presentation simpler by making the claim less true.

### Become an MCP-only framework

MCP server frameworks will be numerous and easy to substitute. Vyral's durable
state semantics, admission contract, and conformance corpus are harder to
replace and remain authoritative.

### Add provider breadth before developer compression

Additional prototypes do not shorten the distance to the first useful result.
Existing adapters are sufficient to demonstrate the contract and identify the
next consumer-backed gap.

## Consequences

- Python runtime promotion becomes the principal adoption dependency.
- The next public examples must demonstrate outcomes and failure recovery, not
  merely enumerate operations.
- MCP receives prominent but bounded positioning.
- Migration documentation becomes executable release material.
- Qualification presentation becomes a generated product surface.
- Capability work without a consumer, blueprint, or evidence dependency is
  deprioritized during this phase.
- The project can measure progress without weakening its claim discipline or
  turning provider presence into provider readiness.
