# Bounded Cloudflare Queues bridge

Status: design decision; no bridge is deployed or registered. Add a prototype
only for a selected deployment that needs to submit projection jobs from a
hosted Vyral service to a Worker-owned Queue. Reuse Vyral's durable execution
identity, hosted-handler catalog, projection generation checks and object-store
references. Queues is a delivery transport; it grants no execution authority.

## Submission boundary

The Vyral sender builds a versioned envelope with an opaque job ID, audience,
issuer/key ID, issued/expiry times, collection and projection generation, an
allowlisted handler ID, an immutable payload digest and object reference, and
a bounded attempt identifier. It signs canonical bytes with a rotated,
deployment-specific signing key held outside source. The Worker verifies the
signature, audience, lifetime and exact schema before dispatch. The Worker
owns its Queue binding and rejects arbitrary queue names, handler IDs, URLs,
inline credentials, executable commands and oversized envelopes.

Use at most 64 KiB of UTF-8 envelope bytes, including signature and metadata.
Large content stays in object storage behind scoped access; the consumer
verifies its digest. Cloudflare currently limits messages to 128 KB, including
metadata, and batches to 100 messages. The tighter envelope bound leaves room
for deployment metadata. Keep concurrency, batch size, max retries and dispatch
rate explicit in the selected profile.

## Delivery and acknowledgment

Queues delivers at least once, so duplicate and reordered messages are expected.
The Worker consumer validates again and claims the issuer, job ID, generation
and payload digest tuple in durable deduplication state before submitting the
approved Vyral operation. The same identity with a different digest is a conflict.
An expired or obsolete generation is a permanent refusal, never an implicit
request to rewrite a newer projection.

Queue acknowledgment follows a durable acceptance/terminal record, depending
on the selected operation's documented completion boundary. A network timeout
after submission is unresolved: look up the same durable Vyral job before
retrying and never assign a fresh job ID to hide uncertainty. An in-memory map
is insufficient deduplication across restarts. Completion must not depend on
an atomic transaction spanning Queue delivery and an HTTP effect.

Retry only recognized transient errors with bounded delay and a fixed maximum.
Permanent authentication, schema, generation, digest and authorization failures
go to a configured dead-letter queue with a safe reason and opaque identity.
If no DLQ is configured, exhausted messages can be deleted by the service;
the profile must reject that configuration when terminal accounting is required.
DLQ redrive repeats validation and preserves original identity and digest.

## Ownership and acceptance

The Worker deployment owns Queue bindings, secrets, deduplication state,
retry/DLQ settings and observability. Vyral owns its request validation,
handler allowlist, job status, authorization and projection freshness.
Neither successful submission nor Queue acknowledgment establishes successful
projection, model execution or a production qualification.

A future prototype must demonstrate tampered/expired/wrong-audience envelopes,
duplicate/conflicting IDs, stale generations, bounded bytes and concurrency,
lost submission responses, restart/lookup, retry exhaustion, DLQ redrive and
retained terminal inventory. Run fixtures first, then one isolated live profile.
There is no current deployment requirement for a prototype.

Sources: [delivery guarantees](https://developers.cloudflare.com/queues/reference/delivery-guarantees/),
[limits](https://developers.cloudflare.com/queues/platform/limits/),
[retries](https://developers.cloudflare.com/queues/configuration/batching-retries/),
[dead-letter queues](https://developers.cloudflare.com/queues/configuration/dead-letter-queues/).
