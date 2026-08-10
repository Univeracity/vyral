# Vyral.Mcp

`Vyral.Mcp` is Vyral's stateless HTTP adapter for Model Context Protocol specification
`2026-07-28`. It pins the stable 2.0 C# SDK, exposes the cataloged Vyral read/resource surface,
and can bind MCP Tasks to durable Vyral execution runs.

The adapter is disabled by default. A host explicitly enables it, registers its execution
authorization seam, and maps one POST endpoint. Each request carries its own protocol metadata,
capabilities, routing headers, and credentials; the adapter creates no caller session and does not
require sticky routing.

Core host integration:

```csharp
var options = VyralMcpOptions.FromConfiguration(builder.Configuration);
builder.Services.AddVyralMcp(options, durableTaskStore);

app.UseVyralMcpTelemetry(options);
app.UseVyralMcpDnsRebindingProtection(options);
app.UseVyralMcpRequestLimits(options);
// Authenticate here, before capturing the request context.
app.UseVyralMcpRequestContext(options, requestContextAccessor);
app.MapMcp(options.EndpointPath);
```

The package provides:

- strict stateless Streamable HTTP setup pinned to protocol `2026-07-28`;
- catalog-filtered tools and resources, including deployment allow/deny controls;
- bounded body/header middleware and redaction-safe metric dimensions;
- DNS-rebinding protection with loopback-safe defaults and explicit shared-host allowlists;
- a request-context snapshot for durable background task authorization; and
- an object-store-backed MCP task store associated with Vyral execution-run lifecycle,
  cancellation, and authorization.

Write/task tools remain opt-in. Hosts should keep binary uploads on REST and must treat all MCP
routing headers as untrusted hints. See
[execution runtime limitations](https://github.com/univeracity/vyral/blob/main/docs/reference/execution-runtime-limitations.md)
for durable-runtime portability boundaries.

Loopback hosts and origins are accepted without configuration. A shared deployment must configure
exact host names under `Mcp:AllowedHosts`; browser callers additionally require exact HTTP(S)
origins under `Mcp:AllowedOrigins`. Wildcard hosts and origins are deliberately unsupported.

The release gate uses `@modelcontextprotocol/conformance@0.2.0-alpha.11` with the frozen
`--requirements 2026-07-28` profile through two round-robin server processes. Development-only
fixture tools, prompts, and resources exist solely for that official gate and are removed from
ordinary discovery. Production-container qualification verifies that isolation again.
