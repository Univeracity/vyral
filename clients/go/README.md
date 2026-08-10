# Vyral Go external-worker client

`github.com/univeracity/vyral/clients/go` is the supported Go client for Vyral's portable
external-worker and control-plane HTTP protocols.

The worker client supports lease acquisition, heartbeat, progress reports, trace events, artifacts,
checkpoint read/write, durable waits, and completion. The separate control client starts registered
handlers, reads/cancels runs, raises external events, and reads the authorized effective runtime
view. Both use Cloud Run metadata-OIDC bearer tokens. Their `Event` observer and `APIError`
deliberately exclude bearer tokens, lease tokens, request bodies, and response bodies.
Go 1.25 or newer is required. Credential-bearing clients require HTTPS except when the
server URL is an exact loopback address used for local development, and they do not follow
HTTP redirects.

```go
client, err := vyralexecution.NewClient(vyralexecution.Config{
    BaseURL: "https://vyral.example",
    WorkerID: "product-worker",
    HandlerIDs: []string{"product.example.job"},
    TokenSource: vyralexecution.MetadataOIDCSource{Audience: "https://vyral.example"},
})
lease, err := client.LeaseNext(ctx, task.RunID, 30)
if lease == nil { return nil }
_, err = client.Checkpoint(ctx, lease, vyralexecution.CheckpointWrite{Key: "progress"})
_, err = client.PutArtifact(ctx, lease, vyralexecution.ArtifactWrite{Name: "summary", Text: "done"})
```

```go
control, err := vyralexecution.NewControlClient(vyralexecution.ControlConfig{
    BaseURL: "https://vyral.example",
    TokenSource: vyralexecution.MetadataOIDCSource{Audience: "https://vyral.example"},
})
run, err := control.StartRun(ctx, vyralexecution.StartRunRequest{
    HandlerID: "product.example.job",
    IdempotencyKey: "source-record-42",
    Scope: &vyralexecution.Scope{ProductID: "product-example", TenantID: "tenant-a"},
})
```

Treat `LeaseToken` as a bearer secret: do not add it to logs, URLs, metrics, or error messages.
The clients do not implement product handlers or Cloud Tasks HTTP routing; those remain the
consumer application's responsibility. In a shared execution plane, use `GetRuntime` with the
intended product and tenant before presenting a handler choice; Vyral returns only handlers that
the verified caller may start.
