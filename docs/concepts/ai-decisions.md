# Application-owned AI decisions

Vyral can turn one completed `ai.judge` answer into a bounded application
outcome: `accept`, `reject`, or `escalate`. The application supplies the policy;
the provider supplies evidence. Vyral's host removes the policy before calling
the provider adapter, so the provider does not receive or control it.

This layer is for branching. It does not generate the next action, invoke the
escalation route, or claim that a confidence value establishes truth. The
caller still owns the question, option schema, consequences, and execution
authority.

## Local proof

The source launcher can evaluate the language-neutral conformance fixture
without a server, model, network connection, or package installation:

```bash
./scripts/vyral decide \
  --policy conformance/ai-decision/v1/policy.json \
  --result conformance/ai-decision/v1/provider-result.json \
  --evaluated-at 2026-10-23T05:30:00Z
```

The output is a `vyral.ai-decision-receipt.v1` object. The same fixture is
evaluated by .NET and Python conformance tests, including identical policy and
answer hashes.

## Policy contract

A `vyral.ai-decision-policy.v1` policy addresses exactly one question:

```json
{
  "schema": "vyral.ai-decision-policy.v1",
  "id": "catalog-item-reuse",
  "version": "1.0.0",
  "questionId": "reuse",
  "answerKind": "choice",
  "accept": {
    "operator": "in",
    "values": ["same_item"],
    "threshold": null
  },
  "reject": {
    "operator": "in",
    "values": ["different_item"],
    "threshold": null
  },
  "evidence": {
    "requireCalibrated": true,
    "minimumConfidence": 0.8,
    "minimumLabelMassCoverage": null,
    "minimumRotationAgreement": null,
    "allowedValidationStatuses": []
  },
  "escalationRoute": "manual-review"
}
```

Choice policies use disjoint `in` sets. Probability and Score policies use
opposing `at_least` and `at_most` thresholds with a non-overlapping gap. A
value in that gap escalates. This makes abstention explicit rather than hiding
it in caller code.

`requireCalibrated` defaults to `true`. An application may deliberately set it
to `false`, but that choice is captured in the policy hash. Requirements for
confidence, label-mass coverage, or rotation agreement fail closed when the
provider does not report the requested metric.

Attach the policy to a normal provider request as `decisionPolicy`. The server
validates it before reserving provider capacity and writes the resulting
receipt to `ProviderRunResult.decision`. Direct .NET callers can use
`AiDecisionPolicyEvaluator`; Python callers can use
`evaluate_decision_policy`.

## Provider posture

The policy is provider-neutral, while its evidence requirements expose real
differences among implementations:

| Provider path | Typical policy behavior |
| --- | --- |
| Deterministic mechanics fixture | Reports `calibrated=false`; the safe default escalates. A mechanics test may explicitly permit uncalibrated evidence. |
| Local ONNX NLI | Can branch after a fitted, relevant calibration is loaded and the application chooses thresholds from its own labeled cases. |
| Local log-probability judge | Reports useful raw mass and rotation agreement but remains uncalibrated; policies can require those diagnostics and still escalate. |
| Jev | Remains an optional network provider. Vyral reports its answers as uncalibrated because the provider's calibration claim has not been independently verified here. |

No path silently falls through to another provider. An escalation receipt
contains an opaque route label such as `manual-review` or `remote-review`; the
application decides whether and how to execute that route.

## Worked loop: equipment catalog reuse

Consider a fictional equipment-rental catalog. A supplier submits “18 V drill,
two batteries, charger, hard case,” and retrieval finds an existing “18 V bare
drill” record.

1. Deterministic validation checks required fields and exact identifiers.
2. A local judge answers `same_item`, `different_item`, or `insufficient_schema`.
3. The policy accepts only a calibrated, sufficiently confident `same_item`
   result and rejects only `different_item`. Everything else enters manual
   review.
4. The reviewer observes that the catalog schema does not distinguish a bare
   tool from a kit. They revise the schema and policy rather than treating the
   model's confidence as an answer to the missing question.
5. The receipt records the policy version, normalized policy hash, selected
   branch, evidence diagnostics, provider/model identity, trace hashes, and any
   escalation route.

The confidence number answers a bounded classification question under the old
schema. It does not own the catalog identity decision, and a higher number
cannot repair a missing distinction.

## Local receipt ledger

The receipt is privacy-minimized: it carries digests and decision metadata,
not the prompt or source text. An application can append it to an explicit
local SQLite ledger in the same command that evaluates the policy:

```bash
./scripts/vyral decide \
  --policy conformance/ai-decision/v1/policy.json \
  --result conformance/ai-decision/v1/provider-result.json \
  --ledger ./.vyral/decisions.sqlite
```

Vyral does not choose a ledger path implicitly. When `--ledger` is present,
the command emits a `vyral.ai-decision-ledger-entry.v1` object. The ledger
retains the normalized receipt only; it does not retain the policy, provider
result, prompt, source text, or escalation payload.

Inspect and verify the chain locally:

```bash
./scripts/vyral decisions list --ledger ./.vyral/decisions.sqlite
./scripts/vyral decisions show --ledger ./.vyral/decisions.sqlite --sequence 0
./scripts/vyral decisions verify --ledger ./.vyral/decisions.sqlite
```

Each entry contains a canonical receipt hash and the preceding entry hash.
Append, list, show, and replay operations verify the complete existing chain
before returning data or writing another entry. The SQLite adapter also checks
that indexed columns still agree with the stored entry envelope.

The `verify` result includes `entryCount` and `headEntryHash`. Retain both in a
separate trusted location and supply them later when truncation or complete
replacement must be detected:

```bash
./scripts/vyral decisions verify \
  --ledger ./.vyral/decisions.sqlite \
  --expect-count 12 \
  --expect-head sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
```

A chain that verifies without an external checkpoint establishes only internal
consistency. A process with write access can replace the database and recompute
the entire chain. Entries are not signed, do not prove that provider evidence
is true, and do not authorize the selected branch. Applications can bind the
head into separately signed evidence or store receipts in CanonicalStore beside
their domain decisions when that stronger boundary is needed.

Replay requires the caller to supply the original policy and provider result.
Vyral evaluates them at the retained receipt timestamp and compares the exact
normalized receipt hash:

```bash
./scripts/vyral decisions replay \
  --ledger ./.vyral/decisions.sqlite \
  --sequence 0 \
  --policy conformance/ai-decision/v1/policy.json \
  --result conformance/ai-decision/v1/provider-result.json
```

Replay compares evidence; it does not invoke the accepted, rejected, or
escalation path. The local adapter caps entries at 1 MiB and ledgers at 100,000
entries. A larger deployment should move the same entry contract behind an
application-owned durable store and retention policy.

The canonical schemas are `AiDecisionPolicy`, `AiDecisionReceipt`, and
`AiDecisionLedgerEntry` in the public JSON Schema bundle. Cross-language
fixtures are under `conformance/ai-decision/v1` and
`conformance/ai-decision-ledger/v1`.
