# AI metering receipts

Vyral AI paths can emit portable, privacy-minimized evidence of how long a
provider run was observed and how much work was reported or observed. The
contract keeps provider threads, runner sessions, turns, and Vyral execution
runs correlated without pretending they have a one-to-one relationship.

The public models live in `Vyral.Providers.Abstractions` and the HTTP schema:

- `AiMeteringReceipt` records one observation or terminal summary.
- `AiMeteringReview` is a separate assessment and, when verification succeeds,
  a provider-thread or runner-session aggregate over an ordered receipt set.
- `AiMeteringContext` lets a caller supply opaque provider-thread,
  runner-session, turn, and chain correlation.
- `IAiMeteringReceiptSigner` and `IAiMeteringReviewSigner` keep private keys and
  remote KMS implementations outside provider adapters while permitting
  least-privilege separation. `IAiMeteringSigner` composes both for intentional
  local use.

This division is intentional. Vyral owns the portable evidence vocabulary,
canonicalization, validation, signing hooks, aggregation rules, and automatic
provider-run observation. A provider adapter or consumer-side observer owns
access to provider-native usage events. An independently operated reviewer owns
trusted keys and policy. That keeps consumer-specific log formats and authority
out of the core contract while making their evidence interoperable.

## What the evidence means

A signature authenticates an assertion and detects later mutation. It does not
prove that the assertion was measured independently or that a provider's
reported usage is correct. Every receipt therefore carries an explicit
`attestationLevel`, and every measurement carries its own `source` and
`quality`.

| Evidence | What it supports |
| --- | --- |
| `self_reported` | Unsigned or locally asserted observation |
| `observer_signed` | Signed by the runner, gateway, or another observer |
| `provider_attested` | Backed by provider-origin attestation |
| `reconciled` | Multiple evidence sources agreed under a named review ruleset |
| `hardware_attested` | Backed by a hardware or confidential-compute attestation |

Do not infer a stronger level merely because `integrity` is present. A Vyral
server signing its runner observation produces `observer_signed` evidence. A
reviewer deployed with a separate identity can verify that receipt and issue a
separately signed `AiMeteringReview`.

## Time and work boundaries

The period keeps distinct clocks and meanings:

- `elapsedDurationMs` is observer wall time, measured monotonically where the
  observer supports it.
- `queueDurationMs` is time spent acquiring local admission.
- `activeDurationMs` is time inside the provider invocation boundary.
- `idleDurationMs` is known inactivity and is not inferred when unavailable.
- `providerDurationMs` comes from the provider or provider adapter and is not
  treated as independently measured time.

Portable integer measurements include input/output/cache/reasoning tokens,
model and tool calls, turns, retries, transport bytes/messages, and artifacts.
Provider-specific measurements may use a namespaced metric name. Estimated
values must identify their method and should identify the tokenizer when token
counts are estimated.

Actual CPU, GPU, or model compute is not ordinarily observable through a remote
API. Vyral must report it as unknown unless a provider or hardware attestation
supports the claim. Prices are also intentionally outside the base receipt:
usage can be combined later with a versioned rate card without changing the
historical evidence.

## Integrity and chaining

Receipt and review payloads use a bounded canonical JSON subset: UTF-8, object
keys sorted by ordinal UTF-16 code units, arrays in source order, minimally
escaped strings, and integer numeric values no larger than JavaScript's exact
integer limit (`9,007,199,254,740,991`). `integrity` is omitted from the
signed payload. The initial signature algorithm is ES256 using a NIST P-256 key
and a base64url-encoded 64-byte IEEE P1363 signature.

`payloadHash` identifies the canonical unsigned payload. The signature covers
a canonical `vyral.ai-metering-integrity.v1` statement containing the evidence
schema, algorithm, issuer, key ID, and payload hash, so identity metadata cannot
be substituted without invalidating the signature. A receipt's full envelope
hash includes its integrity block. `previousReceiptHash` points to the
preceding full envelope, allowing a verifier to detect deletion, reordering,
or signature substitution. A review binds the ordered full-envelope hashes and
the versioned ruleset used to assess them.

Producer chaining is optional. A serialized producer can set `sequence` and
`previousReceiptHash`; both are null for a standalone receipt. Concurrent
provider runs should normally emit standalone receipts rather than racing over
a shared chain head. The later signed review fixes the receipt order and makes
deletion or reordering of the reviewed set detectable.

The language-neutral canonicalization fixture is under
`conformance/ai-metering/v1`. It includes signed receipt and review envelopes,
public P-256 JWKs, and expected payload, signing-input, and envelope hashes.

Cryptographic evidence schemas reject unknown properties. .NET consumers
accepting untrusted JSON should use `AiMeteringCryptography.DeserializeReceipt`
or `DeserializeReview` before verification; these helpers also reject duplicate
members, excessive depth, and evidence envelopes larger than 1 MiB. Ordinary
permissive DTO deserialization is not a substitute for evidence parsing.

## Server emission

Provider runs receive a runner-observed receipt automatically. It remains
unsigned and `self_reported` unless signing is configured. To sign at the Vyral
server boundary, mount a protected PEM EC private key and configure:

```text
Providers__Metering__SigningKeyPath=/run/secrets/vyral-metering.pem
Providers__Metering__Issuer=spiffe://example.net/vyral/provider-runner
Providers__Metering__KeyId=provider-runner-2026-09
```

All three settings are required together. An invalid path, identity, or curve
fails startup. The key must use NIST P-256. Vyral emits only the issuer, key ID,
payload hash, and signature; public-key distribution and trust policy remain
operator-owned.

The terminal provider job/result returns its receipts directly. Provider-backed
paths embedded in retrieval also retain the privacy-minimized receipts in the
associated trace result summary, with their full-envelope hashes on the typed
provider trace event.

Hosted consumers should normally implement `IAiMeteringReceiptSigner` and
`IAiMeteringReviewSigner` with separate cloud KMS/HSM grants rather than
exporting a private key. If evidence must be independent of the Vyral process,
place the observer and reviewer behind separate workload identities and trust
roots.

Remote implementations use `CreateReceiptSigningRequest` or
`CreateReviewSigningRequest`, ask the KMS/HSM to sign the returned protected
bytes with ECDSA/SHA-256, and apply its raw 64-byte IEEE P1363 result with the
matching `Apply*Signature` method. KMS products that return ASN.1 DER ECDSA
signatures must convert them to fixed-width P1363 before applying them.

When signing is configured, emission fails closed: a signing or receipt
validation failure prevents the provider path from claiming successful
metering. Key rotation should retain the public keys needed to verify historical
receipts by issuer and key ID.

## Independent session review

`AiMeteringReviewer.ReviewReceipts` accepts one terminal runner summary per
provider run, optional separately signed terminal observer receipts, an
explicit `provider_thread` or `runner_session` scope, and a caller-owned
signature verifier. It rejects mixed scopes, duplicate receipts, duplicate or
missing runner summaries, repeated observations from one issuer for a run, and
invalid signatures. Only a review without errors contains an `aggregate`; a
rejected review carries no totals that could be mistaken for verified usage.

The aggregate keeps two meanings separate:

- `wallSpanDurationMs` spans the earliest start through the latest completion,
  including gaps.
- `summedElapsedDurationMs` and the optional queue, active, idle, and provider
  sums represent accumulated per-run time and can include concurrent work.
- `concurrentIntervalsDetected` says when those intervals overlap.
- `summaryReceiptCount` and `observationReceiptCount` disclose which evidence
  streams were reviewed. Time totals come only from runner summaries.
- work measurements remain separated by receipt kind, provider, model, source,
  quality, method, and tokenizer before they are summed. An observer's token
  count is never silently added to the runner summary's copy of the same count.

```csharp
var review = AiMeteringReviewer.ReviewReceipts(
    receipts,
    new AiMeteringScope
    {
        Kind = AiMeteringScopeKinds.RunnerSession,
        Id = runnerSessionId
    },
    AiMeteringRulesets.BasicV1,
    receipt => VerifyAgainstTrustedRunnerKey(receipt));

await independentReviewerSigner.SignReviewAsync(review);

var verifiedBundle = AiMeteringReviewer.VerifyReviewBundle(
    review,
    receipts,
    candidate => VerifyAgainstTrustedReviewerKey(candidate),
    receipt => VerifyAgainstTrustedRunnerKey(receipt));
```

The receipt issuer and review issuer should be different identities when the
review is intended to be independent. Vyral cannot prove organizational
independence from key material alone; deployment policy must establish it.
Bundle verification checks the review signature and then deterministically
replays the named ruleset so a valid signature cannot conceal substituted
receipts or incorrect totals.

Session, thread, turn, and correlation identifiers supplied in
`AiMeteringContext` are labels, not credentials. A runner signature proves that
the runner observed and signed those labels; it does not prove who owns the
named scope. Deployments that rely on scope identity must authenticate the
caller and bind or replace those labels at a trusted ingress before signing.

The basic ruleset proves the exact ordered receipt set named by the review; it
does not by itself prove that the set exhausts every run that occurred in the
named thread or session. A reviewer claiming scope-complete accounting must
independently enumerate admissions from an append-only source, establish a
closed scope boundary, and retain that closure evidence under its own policy.
Until Vyral defines a portable closure manifest, describe basic-v1 totals as
reviewed-receipt totals rather than complete billing totals.

## Privacy and adapter guidance

Receipts contain counters, opaque correlation identifiers, and digests. They do
not contain prompts, outputs, credentials, filenames, or customer records by
default. Evidence references should remain redacted or point to separately
authorized, immutable objects.

A digest is linkable metadata, not anonymization: low-entropy inputs can be
guessed and hashed. Treat receipts and reviews as operational evidence with an
appropriate access and retention policy even when raw content is absent.

In-process adapters should return provider-reported usage through
`ProviderRunResult.MeteringMeasurements`; the runner copies it into the
terminal summary before signing. They must preserve the provider's origin and
quality;
for example, token usage from a response is `provider_response` / `reported`,
while a tokenizer-derived count is `consumer_inference` / `estimated`.
Provider-native values that lack portable semantics remain namespaced and must
not be relabeled as a common metric.

A consumer such as a local runner supervisor can contribute useful independent
evidence by observing process lifetime and reading documented, append-only
provider usage events. It should convert only explicit usage objects, keep raw
transcripts out of receipts, sign under a separate workload identity, and leave
provider-account quota snapshots distinct from per-session work. Vyral's
`AiMeteringUsageNormalizer` handles common token and call fields without
searching arbitrary model output. Consumer-specific readers remain outside
Vyral unless their event shape is itself portable.

For a Vyral-run operation, such a supervisor should emit at most one terminal
`observation` receipt for its issuer and the Vyral-assigned `providerRunId`.
Documented session/thread IDs map to the matching subject fields; completed-turn
usage maps to source-labelled token/call measurements; tunnel bytes, messages,
and monotonic duration map to their corresponding transport measurements and
period. Cumulative account quota or billing snapshots must not be presented as
per-run work. The basic reviewer binds these observations beside the runner
summary while retaining their separate evidence stream.
