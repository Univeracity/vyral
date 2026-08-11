# Execution Adapter Qualification

`adapter-qualification.json` is the checked-in evidence baseline for every execution adapter that
advertises a portable capability profile. `adapter-qualification.schema.json` is the release
contract for that artifact.

Capability and qualification answer different questions:

- `advertisedCapabilities` says what the adapter implements.
- `qualification.level` says how that exact capability set was verified.
- `qualification.status` says whether the evidence is within the 90-day operational cadence.

The levels are `prototype`, `local_conformant`, `live_qualified`, and `consumer_validated`. A
hosted adapter is not `live_qualified` merely because an opt-in test exists or passed once on a
developer workstation. Promotion requires a provider version, isolated live gate, redacted result
artifact, and cleanup evidence. Provider endpoints, account ids, tenant ids, credentials, and
consumer identities do not belong in this artifact.

The manual `Google Live Qualification` workflow emits a commit-bound
`google-live-gate-<commit>` artifact through short-lived workload identity federation. That
operational receipt deliberately leaves this checked-in maturity baseline unchanged. Promotion of
the Google adapter remains a separate review that must validate the receipt, cleanup evidence,
provider scope, and the full level requirements below.

The manual `AWS Live Qualification` workflow follows the same separation of duties. It uses a
short-lived GitHub OIDC session and an isolated least-privilege role to exercise S3, DynamoDB, and
SQS, then uploads only a redacted `aws-live-gate-<commit>` receipt. Raw provider logs, resource
identifiers, account identifiers, and credentials are not published. The receipt proves the live
gate and cleanup for that commit; it does not automatically promote an adapter or claim coverage
for managed OpenSearch or a consumer environment.

Generate the release copy with:

```bash
scripts/generate-adapter-qualification.sh artifacts/release/qualification/adapter-qualification.json
```

The generator binds the copy to the release commit, uses the commit timestamp by default for
reproducibility, recalculates freshness and summary measures, checks level-specific evidence, and
rejects capability profiles not fully covered by the qualification record. Adapter conformance
tests also compare runtime descriptors to this report so capability drift fails CI.
