# Stateless MCP behind a gateway

Vyral implements the MCP `2026-07-28` stateless request/response profile. The
protocol version, method, target name when applicable, client capability, and
authorization context travel with every request. A gateway can therefore
classify and authorize a request before forwarding it, and an ordinary load
balancer can select any compatible healthy instance without MCP session
affinity.

Run the dependency-free, two-instance proof from a source checkout:

```bash
python3 examples/python/stateless_mcp_round_robin.py
```

The example sends discovery and catalog requests to two independent Python
runtime instances. Each method reaches both instances, returns an equivalent
result, and creates no `Mcp-Session-Id`. A deliberately mismatched
`MCP-Method` header and JSON-RPC body is rejected, showing that header-aware
routing does not remove host-side validation.

## Gateway sequence

For each request, the edge should:

1. Terminate TLS and apply bounded header and body limits.
2. Require the supported `MCP-Protocol-Version`.
3. Classify the route from `MCP-Method` and, where required, `MCP-Name`.
4. Authenticate the caller and authorize the semantic Vyral operation. Routing
   headers are selectors, not identity claims.
5. Forward the original routing headers and body to any healthy compatible
   instance.
6. Let Vyral verify that the headers, per-request client metadata, and JSON-RPC
   body agree before dispatch.

Do not log API keys, bearer tokens, task payloads, or raw customer arguments at
the gateway. Keep correlation metadata bounded and redacted.

## State and scaling boundary

Stateless MCP removes protocol session affinity; it does not make application
data ephemeral. Discovery and catalog responses can land on any identically
configured instance. Stateful reads and durable tools require the instances to
share or consistently route to the same qualified Vyral data and execution
planes. Local SQLite is suitable for the example and single-node development,
not a shared multi-instance production data plane.

Long-running tools return durable task envelopes. Vyral's execution runtime is
the run authority, so retries and polls may reach different host instances
without inventing a second task lifecycle at the MCP layer.

## Production checks

Before exposing the endpoint, verify:

- exact Host and browser Origin policy, including rebinding probes;
- authentication and operation-level authorization on every enabled tool;
- the explicit write-tool allowlist—writes are disabled by default;
- request, response, task, and artifact bounds;
- compatibility across every instance in the load-balancer pool;
- failover while requests are active and after one instance disappears;
- shared data-plane and execution readiness for stateful operations; and
- the frozen official conformance profile plus Vyral's multi-instance,
  failover, task, policy, and telemetry fixtures.

Run the complete repository gate with:

```bash
scripts/verify-mcp-conformance.sh
```

The [stateless MCP design](../../design/public-sdk-surface-and-stateless-mcp.md)
defines the full catalog, routing, security, and compatibility contract.
