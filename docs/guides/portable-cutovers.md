# Portable cutovers

Vyral makes a provider change an explicit data and behavior migration instead
of an application rewrite. The stable boundary is the contract: provider
schemas, SDKs, deployment resources, and migration systems remain private to
their adapters.

Start with the bundled local proof:

```bash
python3 examples/python/canonical_store_cutover.py
```

It writes one tenant transaction to a source CanonicalStore, exports a chunked
and hash-bound archive, restores that archive into an independent target store,
and proves that the document, revision, outbox event, transaction identity,
tenant boundary, and idempotent replay survived. Both stores are local SQLite
instances, so this is contract evidence—not a qualification claim for a cloud
adapter.

## Cutover sequence

Use the same sequence for a provider-backed migration:

1. Inventory the source tenant scope, required capabilities, data volume,
   retention rules, and adapter-specific features that have no portable
   equivalent.
2. Run the target adapter's conformance suite and data-plane preflight against
   disposable resources. Do not treat construction or a successful health
   check as qualification.
3. Establish a write boundary. Use a maintenance window, durable change log,
   or dual-write policy whose ordering and replay behavior have been tested.
4. Export the canonical tenant archive and retain its archive and snapshot
   hashes outside either provider.
5. Restore into the target, requiring the expected archive hash. Verify
   document and revision counts, fences, outbox state, transaction receipts,
   and tenant isolation.
6. Rebuild disposable indexes and projections from canonical state. Do not
   copy a provider's private index tables and call them portable.
7. Shadow reads and bounded work against both sides, comparing semantic
   results rather than provider-specific response details.
8. Move traffic only after readiness names the intended target adapter and
   topology. Keep the source read-only until the rollback window closes.
9. Rehearse rollback before deleting anything. A rollback must account for
   writes accepted after the export boundary, not merely restore the old
   snapshot.

## Evidence to retain

At minimum, retain:

- source and target adapter identities and versions;
- conformance and preflight receipts from the exact target shape;
- archive profile, chunk count, archive hash, and snapshot hash;
- counts for documents, revisions, fences, outbox events, and transaction
  receipts before and after restore;
- the projection rebuild result and representative shadow-read comparisons;
- the write boundary, cutover time, rollback deadline, and cleanup result; and
- redacted failure evidence for every rejected or retried phase.

Do not retain credentials, lease tokens, customer payloads, or provider
connection strings in migration evidence.

## What portability does not erase

CanonicalStore covers the strong portable data plane. It does not promise
portable arbitrary SQL, provider-native query extensions, IAM policy, network
topology, backup scheduling, or regional durability. Those remain deployment
decisions. A provider adapter is ready for a real cutover only when its
qualification evidence covers the capabilities and operational shape the
application will actually use.

The repository also includes an executable
[source-native to indexed retrieval](source-native-retrieval.md#executable-migration-walkthrough)
walkthrough. Together, the examples demonstrate two different migration
boundaries: introduce governed indexed state only when it earns its cost, and
move canonical data without changing the consumer contract.
