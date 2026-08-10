# Go external-worker smoke fixture

This disposable Cloud Run worker exercises Vyral's external-worker HTTP protocol. It is an
integration fixture, not a product-worker template: it recognizes only simple `action` payloads
and has no product-domain logic. It consumes the supported Go client at
`github.com/univeracity/vyral/clients/go`.

It calls the Vyral server to lease, heartbeat, checkpoint, wait, and complete a run. Supported
actions are `echo`, `retry-once`, `wait-event`, and `wait-timer`.

Required environment variables:

- `VYRAL_SERVER_URL`: Vyral server base URL.
- `VYRAL_SERVER_AUDIENCE`: expected Cloud Run OIDC audience; defaults to the server URL.
- `VYRAL_SERVER_AUTH_MODE`: `metadata-oidc` (default) obtains a service-account identity token
  from the Cloud Run metadata server. `none` is for isolated public smoke servers only.
- `VYRAL_WORKER_ID`: immutable worker identity sent to the protocol.
- `VYRAL_HANDLER_IDS`: comma-separated host-registered handler ids.

Cloud Tasks should target `POST /tasks/execution`. Its message may contain only a `runId`; all
mutable state comes from the lease response. A real worker must use Cloud Run IAM or equivalent
service-to-service authentication, bind its configured worker identity to that verified
principal, and implement product-owned handlers.

`POST /smoke/start` and `POST /smoke/prune` are fixture-only helpers for an isolated deployment;
do not include them in a product worker.

Build from the repository root so Docker can include the shared Go client:

```bash
docker build -f workers/execution-smoke-go/Dockerfile .
```
