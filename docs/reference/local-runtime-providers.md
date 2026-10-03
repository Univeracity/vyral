# Local runtime providers

The native chat target and Choice-only log-probability judge are opt-in prototypes.
Ordinary CI uses fake sessions and HTTP fixtures; a real model is never downloaded
or launched implicitly. The previous proposal's scoring mechanics are implemented
in Vyral. Host-specific acquisition, renewal and release belong behind the injected
ILocalLogprobSessionFactory, allowing an in-process engine or explicitly configured
local service to implement the same shape.

## Contracts and scoring

AiChatRequest.maxOutputTokens is a positive ceiling on every generated token,
including hidden reasoning. An adapter that cannot enforce it must reject before
inference. The deterministic and CLI adapters reject it; they never approximate it
with characters or bytes. The native target requires an explicit ceiling and sends
that exact value as n_predict. maxOutputChars and MaxOutputBytes remain separate
output controls. Unknown payload fields, tools, attachments and source writes are
unsupported.

LocalLogprobJudgeProviderTarget answers only Choice questions. It acquires one
session per batch, resolves decision labels to verified single token IDs/bytes,
and counts every complete model-templated prompt plus one reserved output token
before any inference. It refuses overflow without truncation. The default scores
three cyclic rotations, capped by option count; options bound question count,
option count and total scoring calls. A host adapter owns bounded cleanup even
after cancellation.

Each answer exposes normalized Probabilities, mean unconditioned
rawLabelProbabilities, minimum labelMassCoverage across rotations, and
rotationAgreement (the share of rotation winners agreeing with the final winner).
Ties use original option order. Missing labels, mismatched bytes, duplicate IDs,
multiple scored positions, nonfinite values or impossible mass reject the batch.
Low positive coverage remains visible. Callers choose their own abstention policy.
These fields are null for providers without this evidence. RawClassProbabilities
keeps its existing native Noul-classifier meaning. Every log-probability answer
has calibrated=false; no relevance or accuracy claim follows from normalization.

## Native transport profile

LlamaCppRuntimeSessionFactory connects only to a literal loopback HTTP origin,
with redirects and proxies disabled. It uses bounded response streaming and one
explicit send per route, with no application retry. It checks configured model
file bytes, the peer's absolute model path, runtime build, original template hash,
one-slot context window and effective template probe against an operator-owned
profile. These are consistency checks inside a trusted local process boundary,
not cryptographic proof against an arbitrary peer impersonating the runtime.

Its closed adapter route inventory is GET /props and POST /apply-template,
/tokenize, /detokenize (judge label verification) and /completion. It calls no
tools, helper processes or remote providers. The profile operator must pin the
actual binary/library closure and model bytes, disable context shifting,
speculative decoding, tools and remote routes, and qualify that launch separately.
The adapter does not manage the process or establish complete host egress denial.

Native scoring requests one output position, negative temperature (greedy choice
with original softmax probabilities), no sampler chain, pre-sampling probabilities,
and a bounded top-token distribution. Every requested decision label must appear;
missing from top-k means unknown and rejects the answer. The two explicitly
supported native response fields are completion_probabilities and probs; simultaneous
fields are rejected. Backend support for that distribution must be qualified at
the pinned build. SSE transports can implement the injected session contract,
but this native adapter uses bounded nonstreaming completion.

## Explicit registration and reproduction

Set Providers:LocalRuntime:Enabled=true, separately from remote CLI registration.
Configure Endpoint, ModelId, absolute ModelPath, ModelSha256, BuildInfo,
TemplateSha256, TemplateProbeSha256, ContextTokens and MaxOutputTokens under
Providers:LocalRuntime. Pin only an operator-controlled runtime. The server
registers local-llamacpp and local-logprob-judge; the latter uses mechanics mode.
WorkspaceAgent opt-in is also independent of remote CLI enablement and retains
API-key authentication and its existing source-writing guards.

For the optional backend gate, set VYRAL_LOCAL_RUNTIME_PROFILE to an ignored JSON
file containing the equivalent camelCase runtime options, then run the provider
tests filtered by LocalNativeRuntimeTests. The fixtures exercise token/byte
ceilings, unknown and negative usage, model/template/window mismatch, preflight
refusals and cancellation. The real smoke verifies chat and Choice response
mechanics for one pinned model/runtime; it establishes no model-quality claim.

Provider-native input/output counters remain reported observations. A total
derived by summing them is explicitly consumer_inference/estimated, and absent
counters stay unknown. Receipts remain partial; a local result or signature
cannot establish complete helper-route accounting or independent production
issuance. Reuse the existing durable provider jobs, public lookup, constrained
workspace runner, signing hooks and independent review contract. Cancellation
after a completion send leaves the native effect unresolved; it does not prove
model execution stopped or consumed zero work. A deployed worker, private IPC,
restart/owner-death containment and protected independent issuer require their
own joined qualification before source-writing or trusted issuance is enabled.

Primary protocol reference: [llama.cpp server](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md).

The [dated mechanics receipt](../../qualification/local-runtime-mechanics-2026-10-03.json)
pins the tested source, binary/library closure, model and effective template.
It records two real-model smoke cases alongside deterministic boundary checks.
