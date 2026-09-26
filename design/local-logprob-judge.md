# Local log-probability judgment

Status: proposal. No new provider is registered by this document, and no
backend has been qualified. The existing in-process ONNX judge remains the
available local implementation. This proposal addresses fixed-option Choice
scoring with a generative model without coupling Vyral to a particular host,
service, model package, deployment, or consumer task.

## Boundary

Keep `ai.judge` and its shared-context batch request as the caller-facing
contract. A proposed `LocalLogprobJudgeProviderTarget` would own prompt
construction, option rotations, probability accounting, validation, and
mapping results back to caller option IDs. It would initially advertise only
Choice questions; Noul and Score must return `unsupported` until implemented
and tested explicitly.

Inject a runtime session factory into that target. One asynchronous session
spans the entire batch, including all rotations. Its responsibilities are:

- Pin the model and tokenizer identity, chat template, and context limit.
- Resolve decision labels to token IDs and exact UTF-8 bytes.
- Count the complete templated prompt and reserved output token exactly.
- Return first-token log probabilities for every requested decision token,
  with token IDs and bytes sufficient to verify the mapping.
- Dispose resources on completion, refusal, cancellation, timeout, or error.

The default implementation direction is an in-process runtime or an
explicitly configured loopback endpoint. No model download, process launch,
credential discovery, or network service is required implicitly. Locality
and network requirements must describe the actual transport: loopback HTTP
still uses a network connection; an external endpoint must not advertise
network-free execution. A deterministic fake session supports ordinary tests.

A host-managed adapter may acquire and renew a lease, load a host-approved
package once, and route requests through a lease-scoped proxy. Those details
stay behind the session factory. Disposal attempts release with a separate,
bounded cleanup token even when the caller token is cancelled. Renewal
failure terminates the batch. Credentials and lease secrets stay in memory
and are excluded from traces, config hashes, errors, and raw output.

## HTTP compatibility

An optional HTTP session can use an OpenAI-compatible chat-completions
response shape. That shape alone is insufficient evidence of scoring support.
The session must demonstrate the tokenizer mapping, exact prompt budget, and
complete requested-label log probabilities before it is usable for judgment.

For a streaming backend the scoring request uses `stream=true`,
`max_tokens=1`, `temperature=0`, `logprobs=true`, and a supported
`top_logprobs` count. A backend extension such as `logprob_token_ids` may
request specific decision tokens. Such fields belong in the transport
adapter, not `AiJudgeRequest`. Qualification must establish that the returned
log probabilities represent the original full-vocabulary distribution, not
temperature-scaled, grammar-constrained, biased, or top-k-normalized values.

Read bounded SSE events across arbitrary HTTP chunk boundaries. Ignore role
and usage-only events; require exactly one scored output position and a valid
stream termination. Reject malformed JSON, duplicate or inconsistent token
entries, truncated streams, nonfinite values, and responses above the byte
limit. Do not retry after a partial stream or return a successful batch with
missing questions. Disposal still runs after parser failure.

## Probability accounting

Use single-token labels only after checking the actual tokenizer; letters
are candidates, not assumed token IDs. Verify IDs against returned token
bytes on every scoring result. A missing requested label is unknown, never
zero, and invalidates the answer even if the other labels look decisive.

For each rotation r, let p(r, i) be the full-vocabulary probability of the
verified decision token mapped to original option i. Compute:

```
coverage(r) = sum_i p(r, i)
conditional(r, i) = p(r, i) / coverage(r)
probabilities(i) = mean_r conditional(r, i)
rawLabelProbabilities(i) = mean_r p(r, i)
labelMassCoverage = min_r coverage(r)
```

Require positive coverage and reject impossible mass above one, allowing
only a documented floating-point tolerance. Default to three distinct cyclic
rotations, capped by option count; permit an explicit count from one through
option count. Rotate the option ordering, assign the verified labels in
position order, and map every result back before averaging. Use a stable
tie-break rule. Bound question count, option count, and total scoring calls.

`AiJudgeAnswer.Probabilities` currently sums to approximately one, so it
should retain the conditional distribution above. The proposed nullable
`labelMassCoverage` and `rawLabelProbabilities` fields would carry the
additional evidence. Do not overload `RawClassProbabilities`: its current
documented meaning is a native classifier's class breakdown for Noul.
Existing providers would leave the new fields null, meaning unavailable,
not zero or perfect coverage. Add these fields only with an implementation
and serialization/conformance coverage.

Choose the winning option from the averaged conditional probabilities and
report `calibrated=false`. Normalized decision scores are not validated
likelihoods. Surface low coverage explicitly; never let normalization hide
it. Consumers choose a minimum coverage, qualification data, escalation
provider, or abstention rule. A threshold such as 0.90 is a consumer policy,
not a universal accuracy guarantee. Domain question construction and
consumer evaluation results remain outside the provider.

## Refusals and cancellation

Before inference, count shared context, question, all option labels, chat
template overhead, and the reserved output token for every rotation. Reject
overflow with a typed `policy` failure and `context_limit_exceeded` reason;
never silently truncate. If exact budgeting cannot be established, report
`unsupported`. Use the session's configured limit, not a service-specific
constant. Serialize scoring within a session unless it explicitly supports
and has been qualified for multiple concurrent sequences.

Map resource-admission refusals to `provider_unavailable`, context-budget
refusals to `policy`, unsupported scoring features to `unsupported`, and
invalid probability evidence to `schema`. Distinguish HTTP authentication,
rate limits, and transport failures using existing failure classes; do not
classify every HTTP 409 as a GPU admission refusal without a recognized
backend error code. Preserve the original failure if cleanup also fails.

## Implementation gate

Before registering a target, require offline tests for rotation invariance,
complete and missing label sets, byte/ID mismatch, full-vocabulary mass,
low coverage, unsupported question types, exact context boundaries,
cancellation, split/truncated SSE frames, and disposal on every exit path.
Exercise the batch with a counting session to prove one acquisition/load and
one release, including failures partway through a batch. Test serialization
of the additive answer evidence and keep existing judge conformance passing.

Add an opt-in local backend smoke gate using synthetic, domain-neutral input,
a pinned model/tokenizer, and actual scoring metadata. Ordinary CI must not
depend on an available host GPU or proprietary service. Record backend
capabilities and qualification limits without publishing private host paths,
tokens, consumer identities, or consumer-specific accuracy claims.
