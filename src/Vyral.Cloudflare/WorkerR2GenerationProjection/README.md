# Worker/R2 generation-bound projection

This reference integration implements Vyral's optional generation-bound candidate contract on
Cloudflare Workers and R2. It is consumer-neutral reference code, not a published package or a
portable index file format.

The query Worker:

- resolves active or explicitly selected generations from R2-backed catalog records;
- verifies the descriptor, manifest, and every required shard before returning candidates;
- applies bounded lexical scoring and filters inside the Worker;
- fails closed on missing, corrupt, stale, retired, late, or incomplete generations; and
- signs continuations that remain bound to the retained generation and exact request.

The JSON manifest and shard schemas beginning with `vyral.private.worker-r2` are deliberately
adapter-private. The portable boundary is the public Vyral generation descriptor, request, result,
coverage, failure, and continuation behavior.

## Immutable-object access modes

The same query Worker supports two mutually exclusive deployment shapes:

1. `direct-r2` binds `INDEX` directly to the query Worker. This is the simplest single-Worker
   shape, but the query Worker receives the R2 binding's full technical authority.
2. `service-reader` gives the query Worker no R2 binding. Its `OBJECT_READER` service binding points
   to `src/object-reader.mjs`, which alone binds `INDEX`. The reader accepts only authenticated
   `POST /read` requests for allowlisted generation keys, exposes no mutation method, and should
   have neither a public route nor a `workers.dev` address.

The query Worker fails closed unless exactly one of `INDEX` and `OBJECT_READER` is present. Do not
configure both as a fallback: ambiguity is treated as provider unavailability.

Query Worker configuration requires independently generated values of at least 32 bytes for
`AUTHORIZATION_SECRET` and `CONTINUATION_SECRET`. Service-reader mode additionally requires the same
independently generated `OBJECT_READER_SECRET` in both Workers and a service binding shaped like:

```json
{
  "services": [
    { "binding": "OBJECT_READER", "service": "<non-public-reader-worker>" }
  ]
}
```

Bind the reader Worker to the R2 bucket as `INDEX`, set `workers_dev = false`, and declare no
routes. Put the query Worker behind the intended authenticated ingress, rate limits, and
request-size controls. Bearer checks are defense in depth, not substitutes for route isolation or
service identity.

## Local proof

Install the exact locked dependency graph and exercise both topologies against a deterministic,
consumer-neutral fixture:

```shell
npm ci --ignore-scripts --prefix src/Vyral.Cloudflare/WorkerR2GenerationProjection
python3 scripts/verify-worker-r2-generation-projection.py \
  --output /tmp/vyral-worker-r2-proof.json
```

The harness proves exact candidate/revision/score parity, generation lifecycle and continuation
behavior, content verification, authentication and body bounds, reader non-mutation guards, and
fail-closed handling for missing, ambiguous, corrupt, or incomplete configuration and artifacts.

Public qualification is scoped to the exact source and evidence named in the qualification
materials. A local proof does not establish live Cloudflare IAM, latency, cache eviction, billing,
or production capacity.
