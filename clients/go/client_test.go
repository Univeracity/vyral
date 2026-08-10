package vyralexecution

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestWorkerProtocolAttachesBearerTokenAndUsesOpaqueLease(t *testing.T) {
	t.Helper()
	requests := make([]map[string]any, 0, 9)
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if request.Header.Get("Authorization") != "Bearer test-identity-token" {
			t.Errorf("authorization = %q", request.Header.Get("Authorization"))
		}
		var body map[string]any
		if err := json.NewDecoder(request.Body).Decode(&body); err != nil {
			t.Fatal(err)
		}
		requests = append(requests, body)
		writer.Header().Set("Content-Type", "application/json")
		switch request.URL.Path {
		case "/execution/workers/leases":
			_, _ = writer.Write([]byte(`{"leaseKey":"lease-key","leaseToken":"lease-secret","workerId":"worker-a","run":{"id":"run-a","attempt":1,"status":"running","payload":{"action":"echo"}}}`))
		case "/execution/workers/leases/heartbeat":
			_, _ = writer.Write([]byte(`{"leaseKey":"lease-key","leaseToken":"lease-secret","workerId":"worker-a","run":{"id":"run-a","attempt":1,"status":"running"}}`))
		case "/execution/workers/leases/checkpoints":
			_, _ = writer.Write([]byte(`{"runId":"run-a","key":"progress","content":{"position":1},"contentHash":"sha256:test","updatedAtUtc":"2026-01-01T00:00:00Z"}`))
		case "/execution/workers/leases/checkpoints/read":
			_, _ = writer.Write([]byte(`{"runId":"run-a","key":"progress","content":{"position":1},"contentHash":"sha256:test","updatedAtUtc":"2026-01-01T00:00:00Z"}`))
		case "/execution/workers/leases/reports":
			_, _ = writer.Write([]byte(`{"id":"run-a","attempt":1,"status":"running"}`))
		case "/execution/workers/leases/events":
			writer.WriteHeader(http.StatusNoContent)
		case "/execution/workers/leases/artifacts":
			_, _ = writer.Write([]byte(`{"id":"artifact-a","runId":"run-a","name":"summary","kind":"text","contentHash":"sha256:test","sizeBytes":2,"text":"ok"}`))
		case "/execution/workers/leases/wait":
			_, _ = writer.Write([]byte(`{"run":{"id":"run-a","attempt":1,"status":"waiting"},"suspended":true}`))
		case "/execution/workers/leases/complete":
			_, _ = writer.Write([]byte(`{"id":"run-a","attempt":1,"status":"succeeded"}`))
		default:
			t.Errorf("unexpected path %s", request.URL.Path)
			writer.WriteHeader(http.StatusNotFound)
		}
	}))
	defer server.Close()

	var events []Event
	client, err := NewClient(Config{
		BaseURL: server.URL, WorkerID: "worker-a", HandlerIDs: []string{"handler-a"},
		TokenSource: TokenSourceFunc(func(context.Context) (string, error) { return "test-identity-token", nil }),
		Observe:     func(event Event) { events = append(events, event) },
	})
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	lease, err := client.LeaseNext(ctx, "run-a", 30)
	if err != nil || lease == nil {
		t.Fatalf("LeaseNext() = %v, %v", lease, err)
	}
	if _, err := client.Heartbeat(ctx, lease, 30); err != nil {
		t.Fatal(err)
	}
	if _, err := client.Checkpoint(ctx, lease, CheckpointWrite{Key: "progress", Content: map[string]int{"position": 1}}); err != nil {
		t.Fatal(err)
	}
	if _, err := client.GetCheckpoint(ctx, lease, "progress"); err != nil {
		t.Fatal(err)
	}
	if _, err := client.Report(ctx, lease, RunUpdate{Status: "running", CurrentStep: "prepare"}); err != nil {
		t.Fatal(err)
	}
	if err := client.RecordEvent(ctx, lease, TraceEvent{Type: "log", Message: "safe progress"}); err != nil {
		t.Fatal(err)
	}
	if _, err := client.PutArtifact(ctx, lease, ArtifactWrite{Name: "summary", Text: "ok"}); err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(time.Minute)
	if _, err := client.Wait(ctx, lease, WaitRequest{Kind: WaitKindExternalEvent, Name: "approval", TimeoutAtUTC: &deadline}); err != nil {
		t.Fatal(err)
	}
	if _, err := client.Complete(ctx, lease, Completion{Status: "succeeded", Result: map[string]bool{"ok": true}}); err != nil {
		t.Fatal(err)
	}
	if len(requests) != 9 {
		t.Fatalf("requests = %d, want 9", len(requests))
	}
	for _, request := range requests[1:] {
		if request["leaseToken"] != "lease-secret" || request["workerId"] != "worker-a" {
			t.Fatalf("request does not retain lease identity: %#v", request)
		}
	}
	for _, event := range events {
		if strings.Contains(event.Path, "secret") || strings.Contains(event.RunID, "secret") {
			t.Fatalf("observer event contains a secret: %#v", event)
		}
	}
}

func TestClientRejectsUnsafeCredentialTransportAndUnsupportedURLs(t *testing.T) {
	tokenSource := TokenSourceFunc(func(context.Context) (string, error) { return "token", nil })
	tests := []struct {
		name    string
		baseURL string
		token   TokenSource
	}{
		{name: "remote HTTP bearer", baseURL: "http://vyral.example", token: tokenSource},
		{name: "URL user info", baseURL: "https://user:password@vyral.example"},
		{name: "unsupported scheme", baseURL: "file:///tmp/vyral.sock"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			_, err := NewClient(Config{
				BaseURL: test.baseURL, WorkerID: "worker-a", HandlerIDs: []string{"handler-a"}, TokenSource: test.token,
			})
			if err == nil {
				t.Fatalf("NewClient(%q) error = nil", test.baseURL)
			}
		})
	}

	if _, err := NewClient(Config{
		BaseURL: "http://127.0.0.1:5220", WorkerID: "worker-a", HandlerIDs: []string{"handler-a"}, TokenSource: tokenSource,
	}); err != nil {
		t.Fatalf("loopback credential transport was rejected: %v", err)
	}
}

func TestCredentialClientDoesNotFollowRedirects(t *testing.T) {
	redirectTargetCalled := false
	redirectTarget := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
		redirectTargetCalled = true
	}))
	defer redirectTarget.Close()
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, _ *http.Request) {
		writer.Header().Set("Location", redirectTarget.URL)
		writer.WriteHeader(http.StatusTemporaryRedirect)
	}))
	defer server.Close()

	client, err := NewClient(Config{
		BaseURL: server.URL, WorkerID: "worker-a", HandlerIDs: []string{"handler-a"},
		TokenSource: TokenSourceFunc(func(context.Context) (string, error) { return "token", nil }),
	})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := client.LeaseNext(context.Background(), "run-a", 30); err == nil {
		t.Fatal("LeaseNext() followed or accepted a credential-bearing redirect")
	}
	if redirectTargetCalled {
		t.Fatal("credential-bearing redirect reached its target")
	}
}

func TestControlClientUsesTokenSafeControlPlaneRoutes(t *testing.T) {
	t.Helper()
	type receivedRequest struct {
		method string
		path   string
		query  string
		body   map[string]any
	}
	var received []receivedRequest
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if request.Header.Get("Authorization") != "Bearer control-identity-token" {
			t.Errorf("authorization = %q", request.Header.Get("Authorization"))
		}
		body := map[string]any{}
		if request.Body != nil && request.Method != http.MethodGet && request.Method != http.MethodDelete {
			if err := json.NewDecoder(request.Body).Decode(&body); err != nil {
				t.Fatal(err)
			}
		}
		received = append(received, receivedRequest{method: request.Method, path: request.URL.Path, query: request.URL.RawQuery, body: body})
		writer.Header().Set("Content-Type", "application/json")
		switch request.URL.Path {
		case "/execution/runs":
			writer.WriteHeader(http.StatusAccepted)
			_, _ = writer.Write([]byte(`{"id":"run-a","attempt":0,"status":"queued"}`))
		case "/execution/runs/run-a":
			_, _ = writer.Write([]byte(`{"id":"run-a","attempt":1,"status":"running"}`))
		case "/execution/runs/run-a/events":
			_, _ = writer.Write([]byte(`{"id":"event-a"}`))
		case "/execution/runtime/effective":
			_, _ = writer.Write([]byte(`{"status":{"available":true},"handlers":[{"handlerId":"handler-a"}]}`))
		default:
			t.Errorf("unexpected path %s", request.URL.Path)
			writer.WriteHeader(http.StatusNotFound)
		}
	}))
	defer server.Close()

	client, err := NewControlClient(ControlConfig{
		BaseURL:     server.URL,
		TokenSource: TokenSourceFunc(func(context.Context) (string, error) { return "control-identity-token", nil }),
	})
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	started, err := client.StartRun(ctx, StartRunRequest{HandlerID: "handler-a", IdempotencyKey: "same-work", Scope: &Scope{ProductID: "product-a", TenantID: "tenant-a"}})
	if err != nil || started.ID != "run-a" {
		t.Fatalf("StartRun() = %#v, %v", started, err)
	}
	if _, err := client.GetRun(ctx, "run-a"); err != nil {
		t.Fatal(err)
	}
	if _, err := client.CancelRun(ctx, "run-a"); err != nil {
		t.Fatal(err)
	}
	if err := client.RaiseEvent(ctx, "run-a", ExternalEventRequest{Name: "approved", Payload: map[string]bool{"ok": true}}); err != nil {
		t.Fatal(err)
	}
	runtime, err := client.GetRuntime(ctx, "product-a", "tenant-a")
	if err != nil || !strings.Contains(string(runtime.Handlers), "handler-a") {
		t.Fatalf("GetRuntime() = %#v, %v", runtime, err)
	}
	if len(received) != 5 {
		t.Fatalf("received = %d, want 5", len(received))
	}
	if received[0].method != http.MethodPost || received[0].body["idempotencyKey"] != "same-work" {
		t.Fatalf("start request = %#v", received[0])
	}
	if received[2].method != http.MethodDelete || received[3].body["name"] != "approved" {
		t.Fatalf("control requests = %#v", received)
	}
	if received[4].query != "productId=product-a&tenantId=tenant-a" {
		t.Fatalf("runtime query = %q", received[4].query)
	}
}

func TestAPIErrorAndEventsDoNotExposeTokensOrResponseBodies(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, _ *http.Request) {
		writer.WriteHeader(http.StatusUnauthorized)
		_, _ = writer.Write([]byte(`{"error":"lease-secret and identity-secret"}`))
	}))
	defer server.Close()
	var observed Event
	client, err := NewClient(Config{
		BaseURL: server.URL, WorkerID: "worker-a", HandlerIDs: []string{"handler-a"},
		TokenSource: TokenSourceFunc(func(context.Context) (string, error) { return "identity-secret", nil }),
		Observe:     func(event Event) { observed = event },
	})
	if err != nil {
		t.Fatal(err)
	}
	_, err = client.Heartbeat(context.Background(), &Lease{LeaseKey: "lease-key", LeaseToken: "lease-secret", Run: Run{ID: "run-a"}}, 30)
	if err == nil {
		t.Fatal("Heartbeat() error = nil")
	}
	var apiError *APIError
	if !errors.As(err, &apiError) {
		t.Fatalf("error type = %T, want *APIError", err)
	}
	if strings.Contains(err.Error(), "secret") || strings.Contains(observed.Path, "secret") || strings.Contains(observed.RunID, "secret") {
		t.Fatalf("unsafe error or event: err=%q event=%#v", err, observed)
	}

	client, err = NewClient(Config{
		BaseURL: server.URL, WorkerID: "worker-a", HandlerIDs: []string{"handler-a"},
		TokenSource: TokenSourceFunc(func(context.Context) (string, error) { return "", errors.New("identity-secret") }),
		Observe:     func(event Event) { observed = event },
	})
	if err != nil {
		t.Fatal(err)
	}
	_, err = client.LeaseNext(context.Background(), "run-a", 30)
	if err == nil || strings.Contains(err.Error(), "secret") || strings.Contains(observed.Err.Error(), "secret") {
		t.Fatalf("token-source failure leaked a secret: err=%v event=%#v", err, observed)
	}
}

func TestMetadataOIDCSourceUsesMetadataFlavorAndAudience(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if request.Header.Get("Metadata-Flavor") != "Google" {
			t.Errorf("Metadata-Flavor = %q", request.Header.Get("Metadata-Flavor"))
		}
		if request.URL.Query().Get("audience") != "https://vyral.example" {
			t.Errorf("audience = %q", request.URL.Query().Get("audience"))
		}
		_, _ = writer.Write([]byte("metadata-identity-token"))
	}))
	defer server.Close()
	token, err := (MetadataOIDCSource{Audience: "https://vyral.example", Endpoint: server.URL}).Token(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if token != "metadata-identity-token" {
		t.Fatalf("token = %q", token)
	}
}

func TestLeasePollingOmitsEmptyRunID(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		var body map[string]any
		if err := json.NewDecoder(request.Body).Decode(&body); err != nil {
			t.Fatal(err)
		}
		if _, exists := body["runId"]; exists {
			t.Fatalf("polling request unexpectedly includes runId: %#v", body)
		}
		writer.WriteHeader(http.StatusNoContent)
	}))
	defer server.Close()
	client, err := NewClient(Config{BaseURL: server.URL, WorkerID: "worker-a", HandlerIDs: []string{"handler-a"}})
	if err != nil {
		t.Fatal(err)
	}
	lease, err := client.LeaseNext(context.Background(), "", 30)
	if err != nil || lease != nil {
		t.Fatalf("LeaseNext polling = %v, %v", lease, err)
	}
}

func TestLeaseIncludesHostAuthorizedHandlerRoutingContext(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if request.URL.Path != "/execution/workers/leases" {
			t.Fatalf("path = %s", request.URL.Path)
		}
		_, _ = writer.Write([]byte(`{"leaseKey":"lease-key","leaseToken":"lease-secret","workerId":"worker-a","run":{"id":"run-a","handlerId":"handler-b","pluginId":"plugin-b","attempt":2,"status":"running","correlationId":"source-42","scope":{"productId":"product-b","tenantId":"tenant-b","serviceIdentity":"product-b@example.test"},"cancellationRequested":true,"payload":{"action":"review"}}}`))
	}))
	defer server.Close()
	client, err := NewClient(Config{BaseURL: server.URL, WorkerID: "worker-a", HandlerIDs: []string{"handler-a", "handler-b"}})
	if err != nil {
		t.Fatal(err)
	}
	lease, err := client.LeaseNext(context.Background(), "run-a", 30)
	if err != nil || lease == nil {
		t.Fatalf("LeaseNext() = %#v, %v", lease, err)
	}
	if lease.Run.HandlerID != "handler-b" || lease.Run.PluginID != "plugin-b" || lease.Run.CorrelationID != "source-42" {
		t.Fatalf("lease routing context = %#v", lease.Run)
	}
	if lease.Run.Scope == nil || lease.Run.Scope.ProductID != "product-b" || lease.Run.Scope.TenantID != "tenant-b" || !lease.Run.CancellationRequested {
		t.Fatalf("lease scope/cancellation context = %#v", lease.Run)
	}
}

func TestExternalEventWaitRejectsFireAtUTC(t *testing.T) {
	client, err := NewClient(Config{
		BaseURL: "https://vyral.example", WorkerID: "worker-a", HandlerIDs: []string{"handler-a"},
	})
	if err != nil {
		t.Fatal(err)
	}
	fireAt := time.Now().UTC().Add(time.Minute)
	_, err = client.Wait(context.Background(), &Lease{
		LeaseKey: "lease-key", LeaseToken: "lease-token", Run: Run{ID: "run-a"},
	}, WaitRequest{Kind: WaitKindExternalEvent, Name: "approval", FireAtUTC: &fireAt})
	if err == nil || !strings.Contains(err.Error(), "does not accept fireAtUtc") {
		t.Fatalf("Wait() error = %v, want external-event fireAtUtc rejection", err)
	}
}
