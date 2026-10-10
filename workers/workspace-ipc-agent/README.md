# Bounded workspace IPC agent

This Linux development worker performs one `ai.chat` invocation through an
authenticated private Unix socket and stages a bounded set of file edits for
Vyral's independent workspace validation. It uses Python's standard library.
It has no provider SDK, shell, tool loop, retrieval service, helper calls,
automatic resend, admission engine or signing engine.

The supported entry points, executed by the workspace host at `/workspace`, are:

```text
/usr/bin/python3 -I /opt/vyral/workspace-ipc-agent/agent.py run --profile /run/vyral-worker-ipc/profile.json
/usr/bin/python3 -I /opt/vyral/workspace-ipc-agent/agent.py recover --profile /run/vyral-worker-ipc/profile.json
```

`recover` only reads the original invocation key. A missing invocation is held;
an uncertain POST never authorizes another POST. A fresh Vyral job is also not
authorized by an uncertain workspace job: first inspect that job through the
server's public job lookup. Gateway owner recovery and workspace job recovery
remain separate responsibilities.

## Host profile

[The disabled host example](../../deploy/workspace-ipc/example-host.json) binds
`Providers:WorkspaceAgent`. Use one dedicated server instance and one runner,
with `SerializeRuns=true`, network disabled, a pinned model ID and
`InputFormat=workspace-json-v1`. Source-writing registration still requires
server authentication. No service is started by these examples.

Set `AgentOutcomeFormat=workspace-ipc-v1` to require the worker's bounded
acknowledgment. Public workspace result `agentDelivery` contains only fixed
provenance/state fields and task, profile, request, result and stdout digests.
The host verifies the task digest against exact stdin task bytes; the owner
must compare profile/request/result pins with its immutable preparation and
gateway inventory. Raw task, context, journal and stdout are never copied into
this receipt. It acknowledges delivery, not model attention or independent
metering. Missing/malformed/mismatched success output refuses application;
failed agent execution claims no receipt. A valid acknowledgment is retained
when subsequent host validation fails, without claiming changes were applied.

Install and hash the worker, interpreter, runtime closure and Bubblewrap on the
chosen host. Mount only the necessary read-only Python libraries and dynamic
loader as `RuntimeReadOnlyPaths`. Mount the worker file and owner-private IPC
directory as `AgentReadOnlyPaths`. Agent-only paths are absent from validators.
Do not include host home directories, provider credentials, owner credentials,
model files, the gateway database or the native provider's TCP endpoint.
Environment inheritance is cleared; leave the configured environment empty.

The example limits are a 60-second host execution deadline, 16 KiB host envelope,
8 KiB full UTF-8 model prompt, 512 output tokens, 12800 reserved tokens, 64 KiB
host output, 256 KiB gateway response, eight edits and 32 KiB total edited content.
Limits include serialization wait, preparation, agent execution, validation and
application. Process teardown has an additional two-second grace period and
filesystem rollback/cleanup is best effort. The deadline is not a hard bound on
response return time or termination of an already sent provider invocation.
Serialization is per runner, not a cross-process lock: multiple replicas need
separate workspaces or an independently qualified coordinator.

Worker profiles and stdin `remainingMilliseconds` support up to 600 seconds
(600000 milliseconds). Existing example allocations remain unchanged. A v2
profile may include `invocationDeadlineUtc`, and must include it when
`maxExecutionSeconds` exceeds 120. This is an ISO UTC string ending in `Z` or
`+00:00`, with optional 1–7 fractional digits, at most 40 characters. Freeze it once before the original job POST;
include it in the profile digest and preserve the same bytes for recovery.
The worker uses the minimum of that UTC remainder, the profile maximum and the
host's remaining envelope. An expired deadline refuses before transport.

Create the worker profile only after the owner admits a new immutable session
and operation key. Populate the exact grant and backend digests, clean Git base,
task UTF-8 digest, context reference, model/provider IDs, path scope and frozen
context-file digests. Use a private regular profile and credential file (0600)
and an owner-private socket directory (0700) with a 0600 socket. Keep the profile
immutable for the entire run and any read-only recovery. Do not rotate it while
jobs are queued. Use a distinct host/profile for a different admitted task.

[The worker example](../../deploy/workspace-ipc/example-worker.json) deliberately
has `allowInvoke=false` and placeholder digests. It cannot authorize a call.
An operator must assemble and review the exact joined configuration before
enabling it. These examples are an interface, not production qualification.

## Public wire contracts

The versioned stdin envelope is:

```json
{"schemaVersion":"vyral.workspace-agent-input.v1","task":"Exact governed task","taskSha256":"<lowercase SHA256 of exact task UTF-8>","baseCommit":"<clean Git commit>","allowedPaths":["src"],"avoidedPaths":["checks"],"modelId":"<pinned model ID>","remainingMilliseconds":60000}
```

The worker revalidates these fields against the private profile, reads only the
listed context files and verifies their digests. It sends their UTF-8 contents
as data in the prompt. The request contains `prompt`, `max_output_tokens` and
`kind=main`; a profile with `invocationDeadlineUtc` adds the exact original
string as `deadline_utc`. That value is part of the immutable gateway request
digest. Fresh remaining envelopes never reconstruct or renew it. The gateway
must support this contract before admitting such a profile. The routes are:

```text
GET  /v1/sessions/{sessionId}
GET  /v1/sessions/{sessionId}/calls/{operationId}
POST /v1/sessions/{sessionId}/calls/{operationId}
```

The session must match the grant/backend digests. The retained invocation must
match the session, key, canonical request digest, reservation and response
digest. The response must bind the pinned provider and model, contain a
successful assistant message, and include nonnegative input/output token
counters. Unknown usage, mismatched bindings and exceeded limits hold the
result without applying edits.

By default, the assistant message must contain exactly one raw JSON object,
with optional surrounding whitespace. V2 profiles may explicitly set
`responseFraming` to `raw-json` (the default when omitted) or
`raw-json-or-single-json-fence`. Other values and types refuse; the closed v1
profile does not accept this field.

The opt-in mode also accepts exactly one complete lowercase `json` fence:
an opening line of three backticks followed immediately by `json`, an LF or
CRLF newline, the JSON object, another LF or CRLF newline and a closing line of
three backticks. Only ASCII spaces, tabs, CR and LF may surround that fence.
No triple-backtick sequence may occur inside its body. Prose, extra tags,
malformed, multiple or nested fences, multiple JSON values, duplicate keys,
nonfinite values and unknown fields refuse. The original UTF-8 response,
including all framing and surrounding whitespace, must fit `maxResponseBytes`
before decoding. Raw JSON remains accepted in either mode.

Freeze the selected mode in the profile before the original invocation. It is
part of the profile digest and is advertised in the prompt, binding the request
digest. Recovery retains those original bytes; it does not infer a mode from
the response or switch modes to accept a refused result. A newly selected mode
requires a new admitted operation. Framing does not establish that an anchor
exists or that a proposal meets the task.

V2 profiles may also select `promptFormat`: `structured-json-v1` is the default
when omitted and retains the existing serialized prompt bytes. The explicit
`readable-windowed-v1` option requires at least one nonempty hashed source
window. Invalid values/types, nonwindowed use and any use in the closed v1
profile refuse. The optional field fragment is:

```json
{"promptFormat":"readable-windowed-v1"}
```

Readable mode places the fixed instructions first, including the selected
response framing and anchored v2 constraints. It adds a tiny fictional JSON
illustration and guidance to use double-quoted, escaped JSON string values;
JavaScript backticks never delimit those strings. The original task appears
once as literal data, followed by context/path/hash/window metadata and exact
selected source content with its original line breaks. Mixed full-file context
entries remain included. Content byte lengths distinguish literal data from
section labels; task and source data confer no instruction authority. No task
intent, source text or proposed solution is rewritten or supplied by the worker.

The entire final UTF-8 prompt, including instructions, example and headers,
must fit the existing input and reservation bounds before transport. The mode
is pinned in the original profile and request digests and retained during
GET-only recovery. A changed mode, task, source or original UTC deadline cannot
renew or resend the original invocation. Output framing and every source,
anchor, scope and staging guard remain unchanged. More visible instructions
are a formatting capability; coding quality requires separate operational
evaluation.

For full-file context, the existing v1 whole-content response remains supported:

```json
{"schemaVersion":"vyral.workspace-edits.v1","edits":[{"path":"src/new.txt","previousSha256":null,"content":"new content\n"}]}
```

Existing files require the exact `sha256:<hex>` prior content digest; `content`
is null for deletion. All edits and preconditions are checked before writing
the staged copy. Paths outside scope, avoided paths, `.git`, symlinks, duplicate
or overlapping paths and oversized batches are refused. The host reconciles
the Git change set, runs declared validators without IPC credentials and applies
only a successfully validated change set. Model output is never executed.

If any supplied context file has `windows`, the prompt selects only
`vyral.workspace-edits.v2` and the worker refuses v1, including empty v1 edits.
Each edit must address an existing supplied context file and carry its exact
whole-file prior digest. Each nonempty `oldText` must match uniquely in the whole
source and fit entirely inside one supplied, hashed window (or a supplied full
context file); `newText` replaces only that anchor. Crossing a window boundary,
guessing an anchor outside the supplied context, creating/deleting files,
overlapping replacements or a stale context/window digest refuses the entire
proposal before writing. All other original bytes, including line endings and
UTF-8 encoding, are preserved. These guards apply after either permitted
framing form. The stdin schema is unchanged.

## Reservation and evidence limits

The reservation is `UTF-8 bytes of the full prompt + reservationOverheadTokens
+ maxOutputTokens`. The configured gateway rule must use the identical formula.
For a native provider that independently enforces templated input tokens plus
requested output no greater than a pinned context size of at most 4096 tokens,
an overhead equal to that context size provides a conservative reservation.
Qualification must bind the actual runtime, model, template and configuration;
setting that number in a profile does not establish the bound.

The worker reports one main invocation and zero helper/retry invocations on its
supported route. Input/output counts are provider-reported, and their sum is
explicitly derived. Unknown values never become zero. Scope remains `partial`
and `independentlyIssued=false`: provider-internal attempts, remote effects,
independent metering, signing and cross-service trust need separate evidence.
For a per-attempt native lifetime, the owner can use the supported
[owned runtime entry point](../owned-native-runtime/README.md) around this
separate worker profile. An externally owned native HTTP service alone does
not inherit a workspace job's cancellation or deadline.
Synthetic gateway tests do not establish coding quality, a live native bound,
zero provider charge or production readiness. Electricity and host resources
are outside any provider-charge claim.

## Large sources and fixed native reservations

The additive [v2 profile](../../deploy/workspace-ipc/example-worker-v2.json)
keeps the host envelope and gateway routes unchanged. It replaces the v1
`reservationOverheadTokens` field with a closed `reservationRule`: either
`{"method":"utf8_bytes_plus_output","overhead_tokens":4096}` or
`{"method":"native_context_window","context_tokens":4096,"max_prompt_bytes":8192}`. For the latter,
the reservation is exactly the pinned context size, with an output ceiling
smaller than that size. The independently qualified gateway backend owns its
qualification/profile artifact DTO; the worker binds its exact backend digest
and checks the returned reservation. It does not consume or grant qualification
from a caller's assertion. Its context limit is at most 4096 tokens.

V2 also requires `maxSourceBytes` (at most 4 MiB per existing source) and permits
context entries with bounded `windows`. Each entry binds the whole-file SHA256;
each window contains one-based `startLine`, `lineCount` (at most 200) and the
SHA256 of those exact UTF-8 lines including line terminators. Lines follow
Python `str.splitlines(keepends=True)`. The worker verifies the entire source
and each selected window, then sends only selected window contents. Up to 16
nonoverlapping windows per file are allowed. The complete prompt still fits
`maxInputBytes`. Files without windows use the unchanged bounded v1 behavior.

V2 permits an alternative assistant output:

```json
{"schemaVersion":"vyral.workspace-edits.v2","edits":[{"path":"src/large.ts","previousSha256":"sha256:<whole-file digest>","replacements":[{"oldText":"unique exact old span","newText":"replacement"}]}]}
```

An existing UTF-8 file and its whole-file digest are required. Every nonempty
old span must match exactly once in the original bytes; up to 32 replacements
per file must not overlap. The edit byte budget counts old plus new spans,
while `maxSourceBytes` bounds the resulting file. All replacements across all
files are checked before staging any change. Unselected and unreplaced bytes
remain exact. The v1 full-file output and closed v1 profile retain their meaning;
v1 rejects the new fields and patch output.

Run the no-model transport/binding/recovery tests on Linux with:

```text
python3 -m unittest discover -s workers/workspace-ipc-agent -p test_agent.py -v
```

The framing, window and original-deadline controls use only synthetic files and
in-memory recovery doubles, with no services or model calls:

```text
python3 -m unittest discover -s workers/workspace-ipc-agent -p test_response_framing.py -v
python3 -m unittest discover -s workers/workspace-ipc-agent -p test_readable_prompt.py -v
python3 -m unittest discover -s workers/workspace-ipc-agent -p test_windowed_protocol.py -v
python3 -m unittest discover -s workers/workspace-ipc-agent -p test_deadlines.py -v
```

The provider tests additionally exercise real Bubblewrap isolation, private
agent-only mounts, direct network denial, deadlines and serialized work.
