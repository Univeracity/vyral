# CanonicalStore Relational Projection Starter

This sample is a consumer-owned SQLite read model built from CanonicalStore. It demonstrates the
supported boundary for joins/reporting views without making relational schema part of
`ICanonicalStore`:

- a full rebuild exports a chunked tenant archive, verifies its manifest/chunks/snapshot hash, and
  rebuilds from the authoritative snapshot (an already exported snapshot can also be supplied);
- the read model, rebuild hash fence, and every enclosed outbox event id are committed together;
- live outbox application commits the read-model mutation and event-id checkpoint in one SQLite
  transaction, making redelivery after a lost acknowledgement harmless;
- only after that transaction commits does the pump acknowledge the CanonicalStore lease;
- failures are nacked with a fixed diagnostic, leaving retry/dead-letter/replay visible in
  CanonicalStore.

Run the local walkthrough with:

```bash
dotnet run --project samples/Vyral.CanonicalProjectionStarter/Vyral.CanonicalProjectionStarter.csproj
```

The example topic requires `documentType`, `documentId`, and `revision` in the outbox payload.
Change that schema and the SQLite tables in consumer code; keep the snapshot hash check,
transactional event checkpoint, monotonic revision write, and acknowledge-after-commit order.

`LeaseOutboxAsync` is a tenant-wide dispatch queue rather than a per-topic subscription. Therefore,
run `PumpOnceAsync` only when this component owns all outbox dispatch for the tenant and all emitted
events use its configured topic. In an application with multiple topics, lease once in a single
dispatcher, route each event to the appropriate handler (including `ApplyAsync` here), and perform
the acknowledge/nack operation centrally. Independent topic consumers must not race to lease the
same tenant queue.

During a provider cutover, quiesce source writes, export and verify the tenant archive, restore it
to the target, rebuild this projection from the target, verify the rebuild fence and consumer
queries, then enable target writes. The rebuild checkpoints every outbox event contained in the
snapshot, so restored ready events are acknowledged as duplicates rather than reapplying state the
snapshot already represents.
