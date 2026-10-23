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

Native scoring retains one output position, `temperature=-1`, `seed=0`, an empty
configured `samplers` list, pre-sampling probabilities and a bounded top-token
distribution. Empty configured samplers alone do not prove greedy selection or
absence of runtime-added samplers; actual behavior belongs to pinned-build
qualification. Every requested decision label must appear;
missing from top-k means unknown and rejects the answer. The two explicitly
supported native response fields are completion_probabilities and probs; simultaneous
fields are rejected. Backend support for that distribution must be qualified at
the pinned build. SSE transports can implement the injected session contract,
but this native adapter uses bounded nonstreaming completion.

### Optional JSON object chat output

`LlamaCppRuntimeOptions.ChatOutputFormat` accepts only `text` (the omitted default)
or `json-object-v1`. Set the equivalent `Providers:LocalRuntime:ChatOutputFormat`
server option and `chatOutputFormat` in the owned native profile consistently.
Omitted or explicit `text` preserves the previous ten-option factory binding and
completion wire. Selecting JSON adds the format and fixed owner policy digest to
that binding; a consumer must pin the new value rather than reuse a text binding.
The JSON descriptor adds `outputLimits.chatOutputFormat` and
`outputLimits.constraintPolicySha256`. Descriptor metadata is not execution proof.

JSON chat sends a fixed GBNF object grammar, `grammar_lazy=false` and
`backend_sampling=false`, retaining the existing token/context/byte limits,
temperature, seed, empty sampler list, nonstreaming transport and single send.
Callers cannot supply arbitrary grammars or schemas. Native Choice scoring remains
unconstrained and keeps its previous request and response validation in either
chat mode. This option does not change scoring probabilities or claim calibration.

Before returning chat output, JSON mode requires an exact grammar echo plus
nonlazy, empty-sampler and disabled-backend settings in `generation_settings`.
Absent or conflicting metadata holds with `native_output_constraint_unconfirmed`;
it never falls back to text. This is a consistency check in the operator-owned
runtime boundary, not independent proof that a binary enforces the grammar.
The pinned binary, template, model and current sampler behavior must be separately
qualified by an explicitly bounded native trial before adoption. Source and
in-memory transport tests cannot establish native constraint enforcement.

Output must be one complete plain JSON object, with depth at most 64, unique
property names at every level, finite numbers and valid Unicode. Fences, prose,
expressions, trailing commas, extra documents and incomplete objects hold with
`native_json_object_invalid`. The provider preserves accepted text exactly; it
does not normalize, repair, truncate or retry. Valid reported input/output counters
remain visible when the JSON or confirmation check holds. Missing or invalid
counters remain unknown, and cancellation after dispatch remains an unresolved
effect. Worker schema/path/hash/anchor/atomicity checks remain independent.

## Explicit registration and reproduction

Set Providers:LocalRuntime:Enabled=true, separately from remote CLI registration.
Configure Endpoint, ModelId, absolute ModelPath, ModelSha256, BuildInfo,
TemplateSha256, TemplateProbeSha256, ContextTokens and MaxOutputTokens under
Providers:LocalRuntime. Pin only an operator-controlled runtime. The server
registers local-llamacpp and local-logprob-judge; the latter uses mechanics mode.
WorkspaceAgent opt-in is also independent of remote CLI enablement and retains
API-key authentication and its existing source-writing guards.

On Linux, source-writing runs require the checkout to sit below a directory
owned by the workspace-host effective user with no group or other permission
bits, for example a host-owned `0700` work root. Every path below that private
boundary through the checkout root must have the same owner. The host checks
this precondition before staging and again immediately before applying changes;
otherwise it rejects the run with `workspace_not_private`. This excludes path
swaps by other host principals while the path-based apply is in progress. A
process running as the same host user remains inside the trusted host boundary
and can still race the checkout, so untrusted workers must use a distinct OS
identity or a stronger isolation boundary.

Native chat accepts an explicit `Providers:LocalRuntime:MaxExecutionSeconds`
integer from 1 through 3600; omission preserves 120 seconds. The public
`LocalLlamaCppProviderTarget(options, maxExecutionSeconds)` constructor exposes
the same policy through its advisory descriptor `timeoutSeconds`. An explicit
request above the configured target ceiling rejects before runtime access.
An omitted request timeout still selects 120 seconds, attenuated by a smaller
target ceiling or caller cancellation. The server's separate
`Providers:MaxRunTimeoutSeconds` must also admit the requested duration (its
default stays 300); `Providers:DefaultRunTimeoutSeconds` stays 120. Provider
readiness exposes that server ceiling as `operationalLimits.maxTimeoutSeconds`.
These metadata values describe policy, not successful model execution.
The duration setting does not change the ten native runtime options or their
factory binding. Local logprob judging retains its existing policy.

The owned controller and v2 workspace worker permit an explicitly pinned context
selected by the immutable profile, within a finite allocation ceiling of 1048576
tokens. The worker also refuses a context larger than its selected
`maxReservationTokens` before transport. Existing example allocations and
defaults are unchanged.
Worker input and native prompt byte limits remain at most 65536; a task-sized
profile can select 32768. Output must be strictly smaller than the selected
context, with the existing worker output ceiling of 8192; an explicit
16384 context and 5120 output is one supported configuration. These checks
do not establish fit: the native factory still tokenizes the fully templated
prompt and rejects input tokens plus reserved output above the context before
completion. Changing context or output changes the exact factory binding.
The finite allocation ceiling is a safety bound, not evidence of runtime or
model support at that capacity. Native controller profile, worker context
reservation, gateway profile and whole grant must match the fresh selected
capacity. Actual readiness, tokenizer fit, output/time and cleanup require
separate qualification; earlier 4096/8192 evidence cannot qualify a larger
context.

Worker execution and its remaining envelope support at most 3600 seconds and
3600000 milliseconds. Above 120 seconds the v2 profile requires an original
absolute UTC deadline. The worker attenuates by the profile, envelope and UTC
remainders; the controller retains its original UTC and cancellation checks.
Native target, host, worker, owner task and gateway backend ceilings must each
admit a fresh selected allocation. Configuration acceptance does not establish
actual tokenizer fit, CPU completion time, usage or cleanup, and never renews
an existing invocation.

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

### Safe progress and JSON rejection diagnostics

`GET /provider-jobs/{jobId}/progress` returns a closed lifecycle projection and an optional bound observation. Native observations distinguish `runtime_binding`, `prompt_preparation`, `prompt_prepared`, `completion_pending`, and `completion_validation`. These are observed pipeline boundaries, not generation/prefill sensors. `completion_pending` records intent before dispatch; it does not prove the model executed. No provisional token counters are invented.

The server awaits durable observation recording before allowing native dispatch. Recorder failure before dispatch refuses the operation; failure after dispatch retains unresolved effects and never retries. Observations bind the original job, execution attempt, request payload and provider configuration. Their native start and effective deadline remain immutable; the server deadline attenuates the provider's existing limit. Context, prepared prompt tokens, reserved output and fit are preparation observations, not usage accounting or new allocations.

Only a matching running observation no older than 30 seconds and before its deadline is marked current. Stale or expired observations report `unknown`; terminal jobs report `terminal` and may retain historical observations with `isCurrent=false`. Changed identity, attempt, payload, configuration, or invalid observation shape discards the snapshot. Polling invokes no providers and renews no deadlines. The projection excludes prompts, outputs, errors, paths, credentials and metering. Lifecycle timestamps alone do not establish native deadlines. Providers without the optional observation interface retain unknown native phases.

Provider entry has a durable `vyral.provider-invocation.v1` intent journal. Completion and its result hash are saved together. Restart inspects this retained evidence before any provider call: a matching completed response is reused unchanged; an intent, missing legacy proof, corrupted binding/result, or malformed journal becomes `provider_effect_unresolved` with reconciliation required. Recovery never invokes the provider, renews the original deadline, creates a zero-usage claim, or authorizes a fresh attempt. Reusing the original idempotency key only observes the existing job. A fresh attempt requires explicit caller authority and a distinct admission.

The progress projection includes the original invocation start/deadline and `reconciliationRequired`. A completed response does not by itself prove external effects stopped: known unresolved native effects remain flagged. Per-boundary `provider.progress` history events contain only phase, sequence, timestamps and observed preparation bounds. Observations remain advisory; terminal execution is authoritative.

Seeded SQLite recovery controls qualify the restart dispatcher/plugin path. They do not substitute for actual server/owner death, native containment, or a representative consumer task. Those remain part of the fresh consumer qualification pass.

A rejected JSON-object completion retains `native_json_object_invalid` and exposes only a closed rejection category: `parse_or_depth_invalid`, `non_object_root`, `duplicate_property`, `nonfinite_number`, or `invalid_unicode`. These labels diagnose rejection; they never repair output, retry a call, or disclose property names or exception messages. Grammar enforcement and task-sized native performance still require qualification on the selected runtime/model.
