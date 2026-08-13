# Python host security

The Python runtime's optional REST and stateless MCP host is designed for a
single trusted local operator by default. A network deployment needs an
explicit boundary: credentials, TLS, rate limiting, and an operator-controlled
runtime directory.

## Choose the trust boundary

`create_host_application(root_path)` deliberately supports an unauthenticated
embedded local host. Use it only when the process and its runtime directory are
already the security boundary, such as a notebook, a local desktop application,
or an in-process test.

For an embedded host that must never expose its data plane by accident, provide
a key and require it at construction:

```python
from vyral_runtime.host import create_host_application

app = create_host_application(
    "./.vyral",
    api_key="load-this-from-your-secret-store",
    require_api_key=True,
)
```

Protected REST operations and every MCP operation then accept either
`X-Vyral-Api-Key` or `Authorization: Bearer …`. The REST health endpoint stays
anonymous so a load balancer can check liveness without holding a secret.

The bundled API-key authorizer is one shared-host secret, not multi-tenant IAM.
For per-user permissions, tenant isolation, token rotation, or audit identity,
authorize at a gateway before Vyral or provide custom REST and MCP authorizers.
Stateless MCP is suitable for this: every request brings its routing and
authorization context in headers.

## Run a deliberate server

The CLI rejects a non-loopback bind without `VYRAL_API_KEY`. Require the same
guard on a loopback bind when another local principal, CI job, or forwarding
process could reach it:

```bash
export VYRAL_API_KEY="load-from-a-secret-store"
vyral serve --root ./.vyral --host 127.0.0.1 --require-api-key
```

For a network deployment, terminate TLS and apply request-rate and connection
limits at a reverse proxy or gateway. Keep the runtime directory writable only
by the host identity; filesystem permissions are the security boundary for its
SQLite data, objects, and local secrets. The SQLite/filesystem composition is
a single-node deployment, not a multi-instance shared state store.

The CLI disables Uvicorn request access logs by default. Vyral host code does
not log request bodies, authorization headers, or worker lease tokens. If
`--access-log` is useful during an incident, Uvicorn emits request metadata;
keep sensitive values out of URLs and use your gateway's redaction controls.

## Browser origins and MCP routing

REST accepts a browser `Origin` only when it exactly matches an
`--allowed-origin` value. MCP provides the same allowlist and, for local
same-host development, normally accepts an HTTP(S) origin whose host exactly
matches the permitted request host.

Use allowlist-only browser routing for an exposed console or gateway:

```bash
vyral serve --root ./.vyral --host 0.0.0.0 \
  --allowed-host runtime.example \
  --allowed-origin https://console.example \
  --require-explicit-origin
```

`--require-explicit-origin` removes MCP's same-host fallback. Requests without
an `Origin`, such as service-to-service clients, remain valid and still require
their normal authorization.

## Limits, errors, and diagnostics

The REST host bounds request bodies at 64 MiB; the MCP endpoint uses a 1 MiB
body limit. Both bound headers and reject untrusted Host values. Apply stricter
gateway limits when the workload needs them.

REST failures use stable `https://openvyral.com/problems/...` type URIs and
generic public details rather than exception text. MCP request and
authentication failures likewise use stable JSON-RPC codes and safe error
data. Treat these as client-facing contracts, not a replacement for operator
logs.

`--mcp-conformance-diagnostics` and
`VYRAL_MCP_CONFORMANCE_DIAGNOSTICS=true` are isolated qualification controls.
They are loopback-only and must remain off in ordinary deployments. Health and
readiness resources report stable identity and status, not runtime roots,
storage diagnostics, credentials, or request data. Process-local runtime
inspection retains the richer diagnostics for the operator who already holds
the filesystem boundary.
