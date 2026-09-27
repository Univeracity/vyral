# Vyral.Providers.Jev

Remote-HTTP `IProviderTarget` for TypeSafe AI's Jev (System One typed-judgment
API), implementing `ai.judge` only. Modeled on `Vyral.Providers.Jules`
(`JulesProviderTarget`) rather than a CLI provider target: stateless HTTP,
api-key auth, no local process.

## Status: verified schema, exercised once against the live endpoint

The wire contract below has been checked against:

- TypeSafe's own published docs (`docs.typesafe.ai/api.md`,
  `/primitives/choice.md`, `/primitives/noul.md`, `/primitives/score.md`).
- A run of `JevProviderTarget` itself against the live endpoint with a real
  credential (2026-09-18), covering all three question types in one exchange
  — not just the wire schema on paper.

**`POST /v1/systemone`** — request `{state, model, questions}` where
`questions` is a **map keyed by question id** (not an array), each question
is `{type, instructions, criteria?}`. Response is `{model, answers, usage}`
where `answers` is a map keyed the same way. Three question types, all
implemented:

- **Choice** — `criteria` is a map (option id → description); answer is
  `{type, choice, probabilities, confidence}`.
- **Noul** — `criteria` is optional; answer is `{type, noul}`. A Noul answer
  has **no provider-side confidence** — this adapter derives one as
  `max(p, 1-p)`, the same convention the local judge targets use, so all
  three `ai.judge` implementers stay comparable.
- **Score** — `criteria` is an **ordered array** of level descriptions (low
  to high), not a map — Jev has no concept of our option ids here, it
  answers by array index. Answer is `{type, score, legend, probabilities,
  confidence}`, with `legend`/`probabilities` keyed by level-index strings
  ("0", "1", ...). This adapter remaps those indices back to the caller's own
  `question.Options[index].Id` (via `RemapIndexKeyedObject`) so all three
  `ai.judge` implementers share one id-keyed convention; if a matching
  question or option can't be found, the raw index-keyed maps pass through
  unchanged rather than being dropped.

What's still true is that this was **one verified exchange at one point in
time, not a repeatable isolated live-qualification gate**. `DiagnoseAsync`'s
`integration.exercise` check reports `Ok` based on that recorded exchange —
it does not itself make a network call, so it cannot detect if TypeSafe
changes the API tomorrow. Re-run the live test (below) periodically or before
a release, not just once, before treating this as more than `prototype`.

## What it implements

- `IProviderTarget` — capability `ai.judge`, operations `judge`/`run`.
- `IProviderDoctor` — checks API key presence, whether `ModelId` is pinned to
  an exact version (not a rolling alias like `jev-latest`), the base URI
  scheme, and `integration.exercise` (whether a live exchange has been
  confirmed — see Status above; the check itself never makes a network call).
- `IProviderQualificationPlanner` — a conservative smoke probe (one `noul`
  question) run in `mechanics` mode.

## Construction

```csharp
var target = new JevProviderTarget(new JevProviderOptions
{
    ApiKey = Environment.GetEnvironmentVariable("JEV_API_KEY"),
    ModelId = "jev-1.13.0" // pin an exact version, not "jev-latest"
});
```

The server wires this from `Providers:Jev:ApiKey` / `JEV_API_KEY`,
`Providers:Jev:BaseUri` (default `https://api.typesafe.ai/`), and
`Providers:Jev:ModelId` (default `jev-1.13.0` — the version `jev-latest`
resolved to in the recorded adapter smoke test), only when `Providers:EnableLiveTargets` is set — the same gate as
the other live (network-calling) provider targets.

## Auth model

API key sent as `Authorization: Bearer <key>` — confirmed against TypeSafe's
raw HTTP API docs: a single bearer token, no key-ID/secret pairing.

## Model pinning

`ModelId` should be an exact version (e.g. `jev-1.13.0`), never `jev-latest`.
A rolling alias can change independently of this adapter. Its `model.pin`
doctor check surfaces an unpinned `ModelId` but cannot prevent drift on the
provider side.

## Batching

`AiJudgeRequest` carries a list of questions against one shared `context` in
a single call; this adapter sends the whole batch as one `questions` map in
one HTTP request. This mirrors the portable `ai.judge` contract, not a
Jev-specific extension — see `Vyral.Providers.Local`'s deterministic judge
target and `Vyral.Providers.Onnx`'s local ONNX judge target for the same
batched shape without a network dependency.

## Explicit non-support

- No local execution; every run is a network call.
- No source writes or tool execution.
- `calibrated` is always reported `false` on every answer from this adapter.
  TypeSafe's own calibration claim for Jev is the provider's own and has not
  been independently verified by Vyral, so this adapter does not assert it as
  fact in its output. (Vyral's separate local ONNX judge target computes and
  reports its own `calibrated` value from a fit Vyral performed itself — see
  `Vyral.Providers.Onnx`'s README. The two are unrelated: the local model was
  never calibrated against Jev's outputs.)

## Terms-of-service boundary

TypeSafe's Master Customer Agreement (`typesafe.ai/legal/mca`) governs use of
the live API; whoever configures `JevProviderOptions.ApiKey` is the
"Customer" under that agreement, not Vyral the project. Two clauses are
directly relevant to how this adapter gets deployed, and are **operator**
responsibilities this adapter cannot enforce in code:

- **§2.3(a)** prohibits making the Services available "as a standalone
  service," and **§2.4** restricts API credential use to the Customer's own
  employees/contractors. A Vyral deployment that lets arbitrary unrelated
  third parties submit `ai.judge` requests through one shared configured Jev
  API key — i.e. reselling or multi-tenanting access to Jev through Vyral —
  is very plausibly what those clauses prohibit. This mirrors the boundary
  already stated for other adapters (e.g. the R2 adapter's edge/delivery
  boundary notes): **this adapter owns HTTP/auth/batching mechanics only; who
  is authorized to trigger a call through the operator's own configured
  credential is the operator's compliance question, not something this
  adapter decides or enforces.**
- **§2.3(b)** prohibits using Jev's output "to perform model distillation,
  train a model to imitate the output of the Services, or develop... a
  similar or competing product." Do not feed this adapter's `AiJudgeResult`
  output into `Vyral.Providers.Onnx`'s calibration tool or any other local
  model training/fitting process.

This is not legal advice and is not exhaustive; an operator relying on this
adapter for anything beyond local experimentation should read the current
agreement themselves.

## How to run tests

`JevJudgeConformanceTests.cs` subclasses the shared
`AiJudgeProviderConformanceTests` (`Vyral.Tests.Conformance`) — the same
structural contract (probability ranges, batching, rejection behavior) that
`DeterministicAiJudgeConformanceTests` and `OnnxNliJudgeConformanceTests`
also prove for the other two implementers, so a caller can trust the three
`ai.judge` providers actually behave the same, not just that each one demos
well in isolation. Gated on `JEV_API_KEY`/`TYPESAFE_API_TOKEN_KEY` like the
smoke test below (its "rejects X before any provider work" cases spend no
API call, but still construct the target with a real key configured).

`tests/Vyral.Tests.Providers/JevProviderTargetTests.cs` covers doctor checks,
request shaping (the `criteria` map for Choice, the ordered array for Score),
Noul/Choice/Score response mapping, and failure classification against a
fake `HttpMessageHandler` — no live credentials or network access required
for these.

`JevApi_LiveSmokeTestCoversAllThreeQuestionTypesAgainstTheRealEndpoint` is
gated behind `[JevLiveFact]` and skips unless `JEV_API_KEY` or
`TYPESAFE_API_TOKEN_KEY` is set:

```sh
JEV_API_KEY="$TYPESAFE_API_TOKEN_KEY" dotnet test tests/Vyral.Tests.Providers \
  --filter FullyQualifiedName~JevApi_LiveSmokeTest
```

It sends one real request covering all three question types against generic,
non-sensitive synthetic content (a sentence about this repo) — deliberately
minimal. Each run incurs a small, real API charge; it is not part of the default `dotnet test` run
and won't run in CI without the credential configured there.

## Qualification posture

`prototype`, with one confirmed live exchange (2026-09-18, all three question
types) rather than none — see Status above. That is evidence, not a
qualification result: it is not a repeatable, isolated, credential-rotated
live gate with redacted receipts (the `live_qualified` bar in
`docs/contributing/adapter-contributor.md`). Do not advertise
`live_qualified` until such a gate exists and has run.
