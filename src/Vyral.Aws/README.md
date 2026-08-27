# Vyral AWS adapters

`Vyral.Aws` provides canonical DynamoDB record and S3 object adapters.
DynamoDB is the canonical source of truth for records; its portable vector
search is intentionally a scan fallback rather than an implied production ANN
service.

For large vector corpora, the package also contains the optional
`OpenSearchRecordSearchProjection`. It is a DynamoDB-Streams-fed, revision
fenced OpenSearch candidate index. It never changes `IRecordCollectionStore`
semantics and callers hydrate candidates from the canonical store before use.
The default index name encodes the relevant mapping shape; an immutable mapping
change therefore selects a fresh derived generation. For an operator-controlled
naming convention, configure its policy-aware index resolver, backfill the new
derived index, then switch readers rather than mutating a live index in place.
See [the OpenSearch projection guide](https://github.com/univeracity/vyral/blob/main/design/aws-opensearch-record-projection.md)
for setup, supported query shapes, security posture, and migration limits.

For a caller-provisioned disposable data-plane endpoint, run the opt-in
qualification harness. It creates and deletes only a unique derived index:

```bash
VYRAL_AWS_OPENSEARCH_ENDPOINT=https://your-opensearch-data-plane-endpoint \
VYRAL_AWS_LIVE_REGION=us-east-1 \
scripts/validate-aws-opensearch-live.sh
```

For portable projection data-plane validation without an AWS resource, use an
unpacked local OpenSearch distribution with its k-NN plugin. The localhost-only
gate starts a security-disabled single node, runs the same mapping, indexing,
revision-fencing, search, and deletion scenario, then removes its temporary
state:

```bash
VYRAL_OPENSEARCH_HOME=/path/to/opensearch \
scripts/validate-opensearch-local.sh
```

This local gate deliberately does not validate a managed endpoint's SigV4
authorization, IAM policy, VPC/networking, encryption, or stream delivery.

## Generation-bound OpenSearch

`OpenSearchGenerationBoundRecordSearchProjection` implements the optional
`IGenerationBoundRecordSearchProjection` contract for exact, immutable provider indexes. Hosts
register an allowlisted index name and provider-issued UUID together with a portable generation
descriptor. Inspection and search verify that binding, require the index to remain read-only,
disable partial search results, reject timeouts or incomplete shard participation, and accept hits
only from the selected index.

The adapter returns identities, canonical revisions, and scores only. Use
`SearchGenerationAndHydrateAsync` to retrieve matching revisions from the canonical record store;
stale candidates are discarded rather than promoted to authoritative data. This implementation is
first-page-only and rejects continuations rather than claiming unproven paging behavior.

The local OpenSearch gate exercises the exact generation binding and fail-closed writable-index
transition. It remains local conformance evidence: managed AWS identity separation, SigV4, VPC
policy, service scaling, availability, and administrator mutation risks require their own live
qualification.
