# Adapter Contributor Guide

This guide is for authors who implement **Vyral adapters**: packages that map a
provider or runtime onto Vyral's portable contracts without changing the
application-facing capability model.

It complements [CONTRIBUTING.md](../../CONTRIBUTING.md). Plugin authors (consumers of
the execution runtime, not adapters) should start with
[the execution runtime plugin guide](../../design/execution-runtime-plugin-authoring.md)
and [the execution package README](../../src/Vyral.Execution/README.md).

---

## Why adapters exist

Vyral applications depend on **Vyral-owned contracts** (records, objects, query
envelopes, provider runs, execution runs). Adapters implement those contracts for
a specific backend. The goal is:

> Own the coherence layer. Outsource execution selectively.

A good adapter:

1. Implements the portable interface faithfully where the provider can support it.
2. **Fails closed** with a clear error when the provider cannot express a
   portable operation (do not invent silent semantics).
3. Keeps credentials, SDK clients, and topology at the **composition boundary**.
4. Proves behavior with **shared conformance tests**, not only provider demos.
5. Advertises only capabilities it can defend, and records **qualification level**
   honestly.

A bad adapter silently widens the portable contract, leaks provider types into
application code, or equates “package exists” with “live-qualified.”

---

## Adapter families

| Family | Primary contracts | Reference packages | Conformance base |
| --- | --- | --- | --- |
| **Record collection store** | `IRecordCollectionStore` | `Vyral.Local`, `Vyral.Pgvector`, `Vyral.Azure`, `Vyral.Aws`, `Vyral.Google` | `RecordCollectionStoreConformanceTests` |
| **Object store** | `IObjectStore` | `Vyral.Local`, Azure Blob, GCS, S3, Cloudflare R2 | `ObjectStoreConformanceTests` |
| **Trace store** | `ITraceStore` | Local SQLite, Firestore traces | Local + provider tests |
| **Execution runtime** | `IExecutionRuntimeAdapter` (+ optional `IExternalExecutionWorkerRuntime`, `IExecutionRuntimeMaintenance`) | `Vyral.Execution.Local`, `.AzureDurable`, `.Aws`, Temporal, Google | `ExecutionRuntimeConformanceTests` / `ExternalExecutionWorkerRuntimeConformanceTests` |
| **AI / coding provider target** | `IProviderTarget` | `Vyral.Providers.Local`, `.Cli`, `.Onnx`, `.Jules` | Provider unit/doctor tests |
| **Canonical store** | `ICanonicalStore` | `Vyral.MySql`, Postgres canonical path | `CanonicalStoreConformanceTests` |
| **Search projection** (optional) | `IRecordSearchProjection` | AWS OpenSearch projection (preview) | Projection-specific tests |

Most contributions implement one family. A “profile” (for example GCP records +
GCS objects + Cloud Tasks execution) may span several packages, but each package
should stay focused.

**Not an adapter:** domain plugins, RAG evaluation harnesses, or HTTP clients.
Those consume contracts; they do not redefine them.

---

## Golden rules

### 1. Do not widen the portable contract

New provider features belong in the adapter (or in an explicit capability) until
the portable contract is reviewed and versioned. Do not:

- accept provider-only filter operators as if they were portable;
- change OpenAPI request shapes “because this cloud needs it”;
- return provider SDK types from public adapter APIs that plugins or apps use.

If the provider is more capable than the contract, document the surplus as
**non-portable extension** and keep it out of the shared envelope by default.

### 2. Fail closed on unsupported portable operations

Prefer:

```csharp
throw new NotSupportedException(
    "Firestore record search does not provide lexical ranking. Use QueryRecordsPageAsync …");
```

over approximate behavior that consumers will treat as portable truth.

When approximation is intentional (for example score normalization), document it
in the package README and, when relevant, in diagnostics/traces.

### 3. Credentials stay out of the contract surface

Adapters should take **already-constructed clients** or options that reference
environment/config-owned secrets—not embed keys in records, metadata, or test
fixtures committed to git.

```csharp
// Good: host owns the client and identity
var records = new CosmosRecordCollectionStore(cosmosClient, databaseId);

// Bad: adapter package hardcodes connection strings or commits .env values
```

### 4. Application code depends on abstractions

| Layer | May reference |
| --- | --- |
| Application / portable plugin | `Vyral.Abstractions`, `Vyral.Execution`, `Vyral.Providers.Abstractions`, domain code |
| Host / server composition | Adapter packages + cloud SDKs |
| Adapter implementation | Abstractions + provider SDKs |
| Conformance tests | Abstractions + adapter under test + `Vyral.Tests.Conformance` |

### 5. Availability is not qualification

Publishing `Vyral.Something` does not make it `live_qualified`. See
[Qualification](#qualification-levels).

---

## Contract packages (what to implement)

### Records and objects — `Vyral.Abstractions`

| Type | Responsibility |
| --- | --- |
| `IRecordCollectionStore` | Collections, policies, upsert/get/delete, query, search |
| `IObjectStore` | Put/get/delete/list with metadata, etag, content hash |
| `QueryEnvelope` / filters | Portable query shape; adapter translates to provider dialect |
| `VyralRecord`, `RecordCollectionPolicy`, `VectorFieldPolicy` | Portable document model |

Start from:

- `src/Vyral.Abstractions/Interfaces/IRecordCollectionStore.cs`
- `src/Vyral.Abstractions/Interfaces/IObjectStore.cs`
- Reference implementation: `src/Vyral.Local/`
- Mature cloud examples: `src/Vyral.Azure/README.md`, `src/Vyral.Pgvector/`

Typical record-store implementation pieces:

1. Store class implementing `IRecordCollectionStore`
2. Query builder (portable filters → parameterized provider queries)
3. Vector policy mapper (when the provider has native vector indexes)
4. Migration / schema bootstrap (SQL providers)
5. Optional scoping wrapper for tests (unique prefix per run)

### Execution — `Vyral.Execution`

| Type | Responsibility |
| --- | --- |
| `IExecutionRuntimeAdapter` | Register plugins/handlers, start/cancel runs, history, artifacts, capabilities |
| `IExecutionRunContext` | Handler-facing progress, waits, events, checkpoints (adapter supplies) |
| `IExternalExecutionWorkerRuntime` | Opaque-token lease protocol for non-.NET workers |
| `IExecutionRuntimeAdapterFactory` | Host selection of adapter by configuration |

Portable baseline capabilities (every adapter):

- `durable.runs`, `cancellation`, `retries`, `artifacts`, `trace.history`, `idempotency`
- exactly one dispatch model: `local.dispatch` **or** `remote.orchestration`
- at least one execution model: `in_process.handlers` and/or `external.workers`

Optional capabilities (`durable.timers`, `external.events`, `durable.waits`,
`leases`, `restart.resume`, …) must be advertised only when implemented.
Consumers branch on **capabilities**, not adapter names.

See [the execution package README](../../src/Vyral.Execution/README.md) and the
[execution runtime limitations](../reference/execution-runtime-limitations.md).

### AI provider targets — `Vyral.Providers.Abstractions`

| Type | Responsibility |
| --- | --- |
| `IProviderTarget` | `Profile`, `Capabilities`, `RunAsync` |
| `ProviderProfile` | Stable id, family, versions, auth shape, local/network flags |
| `ProviderCapabilityDescriptor` | Capability ids and operation metadata |
| `ProviderRunRequest` / `ProviderRunResult` | Portable run envelope |
| Doctor / readiness / quota / qualification helpers | Operational honesty |

```csharp
public interface IProviderTarget
{
    ProviderProfile Profile { get; }
    IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; }
    Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default);
}
```

Normalize failures into stable failure classes where possible; do not leak raw
CLI stderr as the only consumer contract.

---

## Recommended project layout

For an in-tree adapter:

```text
src/Vyral.<Provider>/
  Vyral.<Provider>.csproj
  README.md                 # credentials, limits, non-portable notes
  <Name>RecordCollectionStore.cs
  <Name>ObjectStore.cs      # if applicable
  <Name>QueryBuilder.cs
  ...

tests/Vyral.Tests.<Provider>/
  Vyral.Tests.<Provider>.csproj
  <Name>ConformanceTests.cs # subclasses shared base fixtures
  <Name>LiveSettings.cs     # opt-in env-gated live resources
  unit / query builder tests
```

For an **external** community adapter (out-of-repo package):

```text
YourOrg.Vyral.<Provider>/
  → PackageReference Vyral.Abstractions (and/or Vyral.Execution / Providers.Abstractions)
  → same interfaces and conformance approach
  → document host wiring (see Hosting)
```

Target framework: match repository packages (currently `net10.0`). Prefer
multi-targeting `net8.0;net10.0` when practical so consumers are not blocked.

Package metadata should use Apache-2.0, repository URL, and a package README that
states **supported capabilities and known limits**.

---

## Conformance: how to prove an adapter

Shared fixtures live in `tests/Vyral.Tests.Conformance/`. Adapter test projects
**subclass** the abstract base and call the `Run…` methods from `[Fact]` methods
(or live facts).

### Record store pattern

```csharp
public class MyRecordStoreConformanceTests : RecordCollectionStoreConformanceTests
{
    protected override async Task<IRecordCollectionStore> CreateStoreAsync()
    {
        // Build isolated store (unique prefix/table/container per run when live)
        ...
    }

    [Fact] // or [MyProviderLiveFact] for managed backends
    public Task RecordStore_RoundTripsCollectionPolicyAndListsDeterministically() =>
        RunRecordStore_RoundTripsCollectionPolicyAndListsDeterministically();

    // Wire each RunRecordStore_* case the base class provides
}
```

Reference: `tests/Vyral.Tests.Pgvector/PgvectorConformanceTests.cs`,
`tests/Vyral.Tests.Aws/AwsConformanceTests.cs`.

### Object store pattern

Subclass `ObjectStoreConformanceTests`, implement `CreateObjectStore()`, and
expose each `RunObjectStore_*` case. Reference:
`tests/Vyral.Tests.Cloudflare/CloudflareR2ConformanceTests.cs`.

### Execution runtime pattern

**In-process / local-style adapters** subclass
`ExecutionRuntimeConformanceTests`:

- implement `CreateRuntimeAsync()`
- override `CreateRestartableRuntimePairAsync()` when `restart.resume` is claimed
- override `DispatchReadyRunsAsync` if the adapter needs an explicit pump
- add `ExecutionAdapterQualificationAssertions.AssertMatchesPublishedProfile(...)`
  once the adapter is listed in `qualification/adapter-qualification.json`

Reference: `tests/Vyral.Tests.Local/LocalExecutionRuntimeAdapterConformanceTests.cs`.

**External-worker adapters** subclass
`ExternalExecutionWorkerRuntimeConformanceTests` and supply a fixture with
adapter + worker protocol surface. Use deterministic in-memory state for default
CI; keep real DynamoDB/SQS (or equivalent) behind opt-in live tests.

Reference: `tests/Vyral.Tests.Aws/AwsDynamoExecutionRuntimeAdapterConformanceTests.cs`.

### Deterministic vs live

| Gate | When it runs | Purpose |
| --- | --- | --- |
| **Default unit / fixture** | Always in CI | Portable contract against fakes or local deps |
| **Local dependency** | CI or dev with Docker/SQLite | Real engine, no cloud account |
| **Live (opt-in)** | Env vars + credentials | Isolated provider resources, create + cleanup |

Live tests must:

1. Use **unique prefixes** / temporary resources per run.
2. **Delete** resources on success and failure.
3. Never commit credentials, account IDs, or production names.
4. Stay skipped when env is unset (custom `FactAttribute` or skip helper).

Examples of live validators:

- `scripts/validate-aws-storage-live.sh`
- `scripts/validate-aws-execution-live.sh`
- `scripts/validate-google-execution-live.sh`
- `scripts/validate-azure-durable-functions-live.sh`
- `scripts/validate-temporal-container.sh`

Mirror that isolation style for new providers.

---

## Qualification levels

Execution adapters publish evidence in
[qualification/adapter-qualification.json](../../qualification/adapter-qualification.json)
(schema: `adapter-qualification.schema.json`). See
[qualification/README.md](../../qualification/README.md).

| Level | Meaning |
| --- | --- |
| `prototype` | Deterministic unit/fixture proof only |
| `local_conformant` | Shared conformance + local/real dependency evidence |
| `live_qualified` | Isolated live gate with provider version, redacted receipt, cleanup proof |
| `consumer_validated` | Additional consumer package validation beyond live |

Rules:

- `advertisedCapabilities` must match runtime descriptor capabilities.
- Every advertised capability must appear in the qualification capability list.
- Evidence ages out (90-day freshness by default).
- **Do not** mark `live_qualified` because a script exists or passed once on a laptop.
- Release generation:

  ```bash
  scripts/generate-adapter-qualification.sh artifacts/release/qualification/adapter-qualification.json
  ```

Storage and AI provider adapters may not use the same JSON artifact yet; still
document maturity in the package README and avoid implying live qualification
without an isolated gate.

---

## Hosting and discovery

### Current server composition

`Vyral.Server` selects storage and execution backends at startup from
configuration (for example `Storage:RecordStore` / `VYRAL_RECORD_STORE`,
execution factory type). Built-in providers are registered in
`CreateProviderTargetRegistry`.

Today, first-party adapters are **compiled into or referenced by** the host.
There is no general “drop a DLL in a folder” discovery path for arbitrary
community assemblies yet. Community adapters should:

1. Ship as a NuGet package implementing the portable interfaces.
2. Document how a host wires them (DI registration, options, factory).
3. Optionally open an issue/PR to add a first-party config backend id if the
   adapter is maintained in this repository.

Dynamic adapter loading remains a planned contribution unlock; until it lands,
**in-repo adapters** or **host-owned composition** are the supported paths.

### Execution factory pattern

Hosts can resolve `IExecutionRuntimeAdapterFactory` implementations for
configured runtime kinds. Prefer factories over hard-coding provider SDKs in
application projects.

### Configuration hygiene

- Env examples only: `deploy/*.env.example`
- No secrets in `appsettings` committed to git
- Document required IAM roles / minimal permissions in the adapter README

---

## Documentation expected with every adapter

In the package `README.md`:

1. **What it implements** (interfaces + capability list)
2. **How to construct** it (clients, options)
3. **Auth model** (ADC, connection string for tests only, etc.)
4. **Portable coverage** (filters, vector modes, lexical support)
5. **Explicit non-support** (fail-closed cases)
6. **Operational limits** (paging, size caps, eventual consistency)
7. **How to run tests** (default + live env vars)
8. **Qualification posture** (`prototype` / `local_conformant` / …)

Update consumer-facing docs when behavior changes
([consumer handoff](../guides/consumer-handoff.md), root README adapter tables, design
notes only when the contract itself changes).

---

## PR checklist

Before opening a pull request:

- [ ] Issue or proposal states portable vs provider-specific behavior
- [ ] Implementation lives in an adapter package; portable contracts unchanged
  unless the PR is explicitly a contract change with versioning notes
- [ ] Shared conformance cases wired (or new cases added to the shared base when
  portable behavior is clarified)
- [ ] Default CI tests pass without cloud credentials
- [ ] Live tests are opt-in, isolated, and clean up resources
- [ ] No secrets, absolute personal paths, or private project IDs
- [ ] Package README documents limits and construction
- [ ] Execution adapters: descriptor matches `qualification/adapter-qualification.json`
  (update report + generator inputs as required)
- [ ] `dotnet test` for the new test project and any touched shared conformance
- [ ] If packaging/release paths change: `scripts/verify-release-artifacts.sh`
- [ ] License: contribution under Apache-2.0; third-party notices if needed

### Contract changes (special)

If you must change `Vyral.Abstractions`, `Vyral.Execution`, or
`Vyral.Providers.Abstractions`:

1. Open a design note or issue first.
2. Prefer additive, versioned fields over breaking renames in 0.x only with
   clear migration notes.
3. Update OpenAPI / clients when the HTTP surface is affected.
4. Expand conformance so every first-party adapter either passes or explicitly
   fails closed with tests.

---

## Suggested learning path

1. Run the local server and a RAG quickstart ([README.md](../../README.md)).
2. Read `IRecordCollectionStore` and `SqliteRecordCollectionStore` (local).
3. Skim `RecordCollectionStoreConformanceTests` end-to-end.
4. Compare one SQL and one cloud adapter (`Vyral.Pgvector` vs `Vyral.Azure`).
5. For execution: read `Vyral.Execution` README, then
   `LocalExecutionRuntimeAdapterConformanceTests`.
6. For AI targets: read `IProviderTarget` and `Vyral.Providers.Local`.
7. Implement the smallest vertical slice: object store **or** record store **or**
   execution adapter—not all three at once.

---

## Reference map

| Topic | Location |
| --- | --- |
| Contribution norms | [CONTRIBUTING.md](../../CONTRIBUTING.md) |
| Security reporting | [SECURITY.md](../../SECURITY.md) |
| Execution plugin authoring | [execution runtime plugin guide](../../design/execution-runtime-plugin-authoring.md) |
| Execution consumer contract | [execution package README](../../src/Vyral.Execution/README.md) |
| Execution limits | [execution runtime limitations](../reference/execution-runtime-limitations.md) |
| Qualification artifact | [qualification README](../../qualification/README.md) |
| Azure storage adapter notes | [Azure adapter README](../../src/Vyral.Azure/README.md) |
| Conformance suite | [Vyral.Tests.Conformance](../../tests/Vyral.Tests.Conformance/) |
| Local record/object/execution | [Vyral.Local](../../src/Vyral.Local/), [Vyral.Execution.Local](../../src/Vyral.Execution.Local/) |
| Consumer ops handoff | [consumer handoff](../guides/consumer-handoff.md) |

---

## Summary

| Do | Don't |
| --- | --- |
| Implement portable interfaces | Teach apps to take a dependency on your cloud SDK |
| Fail closed with clear errors | Pretend every provider is identical |
| Shared conformance + opt-in live | Only screenshot a console happy path |
| Honest qualification levels | Claim live-qualified without isolated proof |
| Document limits in README | Leave unsupported operators undocumented |
| Keep secrets in the host | Commit keys, account IDs, or dogfood names |

If you are unsure whether a behavior is portable, open an issue **before**
encoding it into the shared contract. Adapter diversity is welcome; contract
drift is not.
