# Consumer-grade local execution example

This Linux example uses the public Python SDK, the Vyral HTTP server and the
existing owned-native runtime entry point. It downloads no model and operates
only on a newly created calculator repository and control repository.

The [qualified profile and scenario summary](../../qualification/local-execution-qualification.json)
records the exact tested model, runtime, template and source digests. This
bounded mechanics qualification is separate from execution adapter maturity.

Install the lightweight client or set `PYTHONPATH=clients/python/src` in a source
checkout. Build `src/Vyral.Server/Vyral.Server.csproj` and
`workers/owned-native-runtime/owned-native-runtime.csproj` into separate output
directories. Supply two explicit, operator-pinned JSON files:

- A native model profile with model bytes/digest, runtime build, effective
  template/probe digests, context/output limits and `chatOutputFormat`.
- A runtime manifest containing `nativeCommand`, `nativeCommandSha256`,
  `runtimeLibraryDirectory`, the complete `runtimeFiles` path/digest list and
  minimal `runtimeReadOnlyPaths`.

See [owned runtime profiles](../../workers/owned-native-runtime/README.md).
The example selects fresh loopback endpoints, binds a separate native listener
through a counting transport, fixes HTTP threads to two and applies pinned
`prlimit` address-space/CPU limits. A profile with an incompatible template
probe refuses readiness; do not replace a pin without independent observation
and a fresh qualification.

```bash
PYTHONPATH=clients/python/src python3 examples/python/local_execution_consumer.py \
  --model-profile /path/to/native-profile.json \
  --runtime-manifest /path/to/runtime-manifest.json \
  --owner-dll /path/to/owner/owned-native-runtime.dll \
  --server-dll /path/to/server/Vyral.Server.dll \
  --output /path/to/retained-receipts \
  --work-root /path/to/expendable-work \
  --seconds 300
```

The owner deadline includes readiness and all scenarios and is never renewed.
Native limits are 4 GiB address space per process and four CPU-seconds per
second of the declared owner allowance. These are native process limits, not
an aggregate host or multi-user resource guarantee. The script checks their
effective kernel values and retains bounded resource observations.

The model proposes a calculator edit. Application policy accepts only one
exact anchored edit to `calculator.py`, a restricted addition AST and four
independent arithmetic checks. Failed proposals never change the file. Model
confidence and self-reported correctness play no role in edit authority.

The harness also exercises an adversarial array request, context overflow,
API death after a native response but before the completion receipt, user
cancellation and deadline expiry after dispatch, and native-owner death while
processing is observed. A counting transport forwards once without retries or
repairs and holds responses to establish specific fault windows. It preserves
chunked request payload bytes. It is test instrumentation, not a replacement
service controller or provider.

Receipts retain original job identity, invocation start/deadline, native
completion counts, reconciliation state, resource limits and cleanup results.
Interrupted work requires reconciliation; same-key admission observes the
original job instead of authorizing another native request. Terminal state
does not imply a cancelled external effect never ran.

When `reconciliationRequired` is true, stop automatic retries and retain the
original job/result/progress. The application or operator decides whether an
effect occurred and whether another attempt is acceptable. An explicitly
approved new attempt uses a new idempotency key and fresh bounded request;
record its relationship to the original job in application state. Changing a
key merely to escape an unresolved outcome is not reconciliation. The original
invocation and deadline remain immutable; polling never grants approval.

Run the application-policy and transport controls without a model:

```bash
PYTHONPATH=clients/python/src:. python3 -m unittest discover \
  -s tests/consumer_local -p 'test_*.py'
```

This example establishes mechanics for an exact Linux/model/runtime/profile
combination. It does not establish general model accuracy, calibrated judgment,
malicious-host protection, or production multi-host custody. API credentials are
fresh local test credentials and are omitted from receipts. Preserve valuable
receipts outside expendable storage.
Use a fresh, empty output directory for each trial; retained receipts are never
overwritten by a new attempt.

Verify the source qualification and independently check a retained receipt:

```bash
python3 scripts/verify-local-execution-qualification.py \
  --receipt /path/to/retained-receipts/receipt.json
```

To prepare portable input files, copy the qualification's `profile` fields
into the model profile and add absolute `modelPath`, a local `modelId`, and a
placeholder loopback `endpoint` (the harness replaces the endpoint). Place the
qualified runtime files together and construct the manifest's `runtimeFiles`
using their basenames, recorded digests and absolute paths. Supply
`nativeCommand` pointing to `llama-server`, its recorded digest,
`runtimeLibraryDirectory`, and the necessary system library mounts. The harness
verifies the complete closure before launch. It fetches no artifacts and never
silently substitutes a model or runtime.
