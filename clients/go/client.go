// Package vyralexecution is a small, supported Go client for Vyral's external-worker HTTP protocol.
// It keeps lease tokens inside request bodies and never includes credentials, lease tokens, or
// response bodies in its errors or observer events.
package vyralexecution

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"
)

const defaultMetadataIdentityEndpoint = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/identity"

const (
	WaitKindExternalEvent = "external_event"
	WaitKindTimer         = "timer"
)

// TokenSource obtains the bearer token used for calls to the Vyral server.
// Implementations must treat returned values as credentials.
type TokenSource interface {
	Token(context.Context) (string, error)
}

// TokenSourceFunc adapts a function into a TokenSource.
type TokenSourceFunc func(context.Context) (string, error)

func (source TokenSourceFunc) Token(ctx context.Context) (string, error) { return source(ctx) }

// MetadataOIDCSource acquires Cloud Run identity tokens from the Google metadata server.
// Endpoint is overridable only for tests; production callers should leave it empty.
type MetadataOIDCSource struct {
	Audience string
	Endpoint string
	Client   *http.Client
}

func (source MetadataOIDCSource) Token(ctx context.Context) (string, error) {
	audience := strings.TrimSpace(source.Audience)
	if audience == "" {
		return "", errors.New("Vyral metadata OIDC audience is required")
	}
	endpoint := source.Endpoint
	if endpoint == "" {
		endpoint = defaultMetadataIdentityEndpoint
	}
	parsed, err := url.Parse(endpoint)
	if err != nil || !parsed.IsAbs() {
		return "", errors.New("Vyral metadata OIDC endpoint must be absolute")
	}
	query := parsed.Query()
	query.Set("audience", audience)
	parsed.RawQuery = query.Encode()
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, parsed.String(), nil)
	if err != nil {
		return "", fmt.Errorf("create metadata OIDC request: %w", err)
	}
	request.Header.Set("Metadata-Flavor", "Google")
	client := source.Client
	if client == nil {
		client = &http.Client{Timeout: 5 * time.Second}
	}
	response, err := client.Do(request)
	if err != nil {
		return "", fmt.Errorf("obtain Cloud Run identity token: %w", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return "", fmt.Errorf("obtain Cloud Run identity token returned HTTP %d", response.StatusCode)
	}
	value, err := io.ReadAll(io.LimitReader(response.Body, 16<<10))
	if err != nil {
		return "", fmt.Errorf("read Cloud Run identity token: %w", err)
	}
	token := strings.TrimSpace(string(value))
	if token == "" {
		return "", errors.New("Cloud Run identity token was empty")
	}
	return token, nil
}

// Event is intentionally free of bearer credentials, lease tokens, request bodies, and response bodies.
// Supply Observe when an application wants structured, token-safe transport telemetry.
type Event struct {
	Operation  string
	Path       string
	RunID      string
	StatusCode int
	Err        error
}

// Config configures an external worker client. WorkerID and HandlerIDs are host-registered values;
// the client never accepts them from a Cloud Tasks message.
type Config struct {
	BaseURL     string
	WorkerID    string
	HandlerIDs  []string
	HTTPClient  *http.Client
	TokenSource TokenSource
	Observe     func(Event)
}

// Client calls the public Vyral external-worker routes.
type Client struct {
	baseURL     *url.URL
	workerID    string
	handlerIDs  []string
	httpClient  *http.Client
	tokenSource TokenSource
	observe     func(Event)
}

func NewClient(config Config) (*Client, error) {
	baseURL, err := url.Parse(strings.TrimSpace(config.BaseURL))
	if err != nil || !baseURL.IsAbs() || baseURL.Host == "" || baseURL.User != nil || baseURL.RawQuery != "" || baseURL.Fragment != "" ||
		(baseURL.Scheme != "http" && baseURL.Scheme != "https") {
		return nil, errors.New("Vyral server URL must be an absolute HTTP(S) URL without user credentials")
	}
	if config.TokenSource != nil && baseURL.Scheme != "https" && !isLoopbackHost(baseURL.Hostname()) {
		return nil, errors.New("Vyral bearer credentials require HTTPS except on loopback")
	}
	workerID := strings.TrimSpace(config.WorkerID)
	if workerID == "" {
		return nil, errors.New("Vyral worker id is required")
	}
	handlerIDs := make([]string, 0, len(config.HandlerIDs))
	seen := make(map[string]struct{}, len(config.HandlerIDs))
	for _, handlerID := range config.HandlerIDs {
		normalized := strings.TrimSpace(handlerID)
		if normalized == "" {
			continue
		}
		if _, exists := seen[normalized]; exists {
			continue
		}
		seen[normalized] = struct{}{}
		handlerIDs = append(handlerIDs, normalized)
	}
	if len(handlerIDs) == 0 {
		return nil, errors.New("at least one Vyral handler id is required")
	}
	client := config.HTTPClient
	if client == nil {
		client = &http.Client{Timeout: 20 * time.Second}
	}
	if config.TokenSource != nil {
		credentialClient := *client
		credentialClient.CheckRedirect = func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		}
		client = &credentialClient
	}
	return &Client{
		baseURL: baseURL, workerID: workerID, handlerIDs: handlerIDs,
		httpClient: client, tokenSource: config.TokenSource, observe: config.Observe,
	}, nil
}

func isLoopbackHost(host string) bool {
	if strings.EqualFold(host, "localhost") {
		return true
	}
	address := net.ParseIP(host)
	return address != nil && address.IsLoopback()
}

// Run is the portable run context returned to a worker. HandlerID is the host-authorized routing
// key for a multi-handler worker; do not infer handler behavior from product payloads.
type Run struct {
	ID                    string          `json:"id"`
	HandlerID             string          `json:"handlerId"`
	PluginID              string          `json:"pluginId"`
	Attempt               int             `json:"attempt"`
	Status                string          `json:"status"`
	CorrelationID         string          `json:"correlationId"`
	Scope                 *Scope          `json:"scope"`
	CancellationRequested bool            `json:"cancellationRequested"`
	Payload               json.RawMessage `json:"payload"`
}

// Lease is an opaque bearer lease. Never log LeaseToken.
type Lease struct {
	LeaseKey   string    `json:"leaseKey"`
	LeaseToken string    `json:"leaseToken"`
	WorkerID   string    `json:"workerId"`
	Run        Run       `json:"run"`
	ExpiresAt  time.Time `json:"expiresAtUtc"`
}

type CheckpointWrite struct {
	Key      string            `json:"key"`
	Content  any               `json:"content,omitempty"`
	Metadata map[string]string `json:"metadata,omitempty"`
}

type Checkpoint struct {
	RunID       string            `json:"runId"`
	Key         string            `json:"key"`
	Content     json.RawMessage   `json:"content"`
	ContentHash string            `json:"contentHash"`
	UpdatedAt   time.Time         `json:"updatedAtUtc"`
	Metadata    map[string]string `json:"metadata"`
}

type WaitRequest struct {
	Kind         string     `json:"kind"`
	Name         string     `json:"name"`
	TimeoutAtUTC *time.Time `json:"timeoutAtUtc,omitempty"`
	FireAtUTC    *time.Time `json:"fireAtUtc,omitempty"`
	Payload      any        `json:"payload,omitempty"`
}

type WaitResponse struct {
	Run       Run             `json:"run"`
	Suspended bool            `json:"suspended"`
	Outcome   json.RawMessage `json:"outcome"`
}

type Completion struct {
	Status        string `json:"status"`
	Result        any    `json:"result,omitempty"`
	FailureClass  string `json:"failureClass,omitempty"`
	Error         string `json:"error,omitempty"`
	StatusDetails any    `json:"statusDetails,omitempty"`
}

// RunUpdate is a durable worker progress update. External workers may only report a running
// status; wait and completion own lifecycle changes.
type RunUpdate struct {
	Status        string   `json:"status,omitempty"`
	Requested     *int     `json:"requested,omitempty"`
	Attempted     *int     `json:"attempted,omitempty"`
	Succeeded     *int     `json:"succeeded,omitempty"`
	Failed        *int     `json:"failed,omitempty"`
	Progress      *float64 `json:"progress,omitempty"`
	CurrentStep   string   `json:"currentStep,omitempty"`
	FailureClass  string   `json:"failureClass,omitempty"`
	Error         string   `json:"error,omitempty"`
	Result        any      `json:"result,omitempty"`
	StatusDetails any      `json:"statusDetails,omitempty"`
}

// TraceEvent is an application-owned trace entry recorded under an active lease.
type TraceEvent struct {
	Type     string `json:"type"`
	Message  string `json:"message,omitempty"`
	Severity string `json:"severity,omitempty"`
	Details  any    `json:"details,omitempty"`
}

// ArtifactWrite creates a product-owned artifact under an active lease.
type ArtifactWrite struct {
	Name      string            `json:"name"`
	Kind      string            `json:"kind,omitempty"`
	MediaType string            `json:"mediaType,omitempty"`
	Text      string            `json:"text,omitempty"`
	Content   any               `json:"content,omitempty"`
	URI       string            `json:"uri,omitempty"`
	Metadata  map[string]string `json:"metadata,omitempty"`
}

type Artifact struct {
	ID          string            `json:"id"`
	RunID       string            `json:"runId"`
	Name        string            `json:"name"`
	Kind        string            `json:"kind"`
	MediaType   string            `json:"mediaType"`
	ContentHash string            `json:"contentHash"`
	SizeBytes   int64             `json:"sizeBytes"`
	Text        string            `json:"text"`
	Content     json.RawMessage   `json:"content"`
	URI         string            `json:"uri"`
	CreatedAt   time.Time         `json:"createdAtUtc"`
	Metadata    map[string]string `json:"metadata"`
}

// ControlConfig configures a provider-neutral control-plane client. It intentionally has no
// worker id or handler allowlist: authorization and product scope are owned by the server policy.
type ControlConfig struct {
	BaseURL     string
	HTTPClient  *http.Client
	TokenSource TokenSource
	Observe     func(Event)
}

// ControlClient calls registered-handler execution APIs for product services. It does not embed
// handler logic, Cloud Tasks routing, or any cloud-specific deployment behavior.
type ControlClient struct {
	transport *Client
}

// NewControlClient creates a control-plane client with the same token-redacting transport used
// by Client. The private transport is deliberately not exposed as a worker client.
func NewControlClient(config ControlConfig) (*ControlClient, error) {
	transport, err := NewClient(Config{
		BaseURL: config.BaseURL, WorkerID: "control-plane", HandlerIDs: []string{"control-plane"},
		HTTPClient: config.HTTPClient, TokenSource: config.TokenSource, Observe: config.Observe,
	})
	if err != nil {
		return nil, err
	}
	return &ControlClient{transport: transport}, nil
}

type Scope struct {
	ProductID       string `json:"productId"`
	TenantID        string `json:"tenantId"`
	ServiceIdentity string `json:"serviceIdentity,omitempty"`
}

type RetryPolicy struct {
	MaxAttempts         int     `json:"maxAttempts,omitempty"`
	InitialDelaySeconds float64 `json:"initialDelaySeconds,omitempty"`
	MaxDelaySeconds     float64 `json:"maxDelaySeconds,omitempty"`
	BackoffMultiplier   float64 `json:"backoffMultiplier,omitempty"`
}

type StartRunRequest struct {
	HandlerID      string            `json:"handlerId"`
	PluginID       string            `json:"pluginId,omitempty"`
	Payload        any               `json:"payload,omitempty"`
	IdempotencyKey string            `json:"idempotencyKey,omitempty"`
	CorrelationID  string            `json:"correlationId,omitempty"`
	Scope          *Scope            `json:"scope,omitempty"`
	ScheduledAtUTC *time.Time        `json:"scheduledAtUtc,omitempty"`
	RetryPolicy    *RetryPolicy      `json:"retryPolicy,omitempty"`
	Tags           map[string]string `json:"tags,omitempty"`
}

type ExternalEventRequest struct {
	Name    string `json:"name"`
	Payload any    `json:"payload,omitempty"`
}

// RuntimeView is an intentionally flexible representation of the authorized runtime discovery
// document. Its adapter/policy payloads remain server-owned contract JSON.
type RuntimeView struct {
	Status   json.RawMessage `json:"status"`
	Handlers json.RawMessage `json:"handlers"`
}

// APIError identifies an unsuccessful Vyral response without exposing request credentials or
// response text, which can be echoed by reverse proxies or product handlers.
type APIError struct {
	Operation  string
	Path       string
	StatusCode int
}

func (err *APIError) Error() string {
	return fmt.Sprintf("Vyral %s %s returned HTTP %d", err.Operation, err.Path, err.StatusCode)
}

func (client *Client) LeaseNext(ctx context.Context, runID string, ttlSeconds float64) (*Lease, error) {
	request := struct {
		WorkerID   string   `json:"workerId"`
		HandlerIDs []string `json:"handlerIds"`
		RunID      string   `json:"runId,omitempty"`
		TTLSeconds float64  `json:"ttlSeconds"`
	}{
		WorkerID: client.workerID, HandlerIDs: client.handlerIDs, RunID: strings.TrimSpace(runID), TTLSeconds: ttlSeconds,
	}
	var lease Lease
	status, err := client.post(ctx, "lease", "/execution/workers/leases", runID, request, &lease)
	if err != nil {
		return nil, err
	}
	if status == http.StatusNoContent {
		return nil, nil
	}
	return &lease, nil
}

func (client *Client) Heartbeat(ctx context.Context, lease *Lease, ttlSeconds float64) (*Lease, error) {
	identity, err := client.leaseIdentity(lease)
	if err != nil {
		return nil, err
	}
	identity["ttlSeconds"] = ttlSeconds
	var renewed Lease
	if _, err := client.post(ctx, "heartbeat", "/execution/workers/leases/heartbeat", lease.Run.ID, identity, &renewed); err != nil {
		return nil, err
	}
	return &renewed, nil
}

func (client *Client) Checkpoint(ctx context.Context, lease *Lease, checkpoint CheckpointWrite) (*Checkpoint, error) {
	if strings.TrimSpace(checkpoint.Key) == "" {
		return nil, errors.New("Vyral checkpoint key is required")
	}
	identity, err := client.leaseIdentity(lease)
	if err != nil {
		return nil, err
	}
	identity["checkpoint"] = checkpoint
	var persisted Checkpoint
	if _, err := client.post(ctx, "checkpoint", "/execution/workers/leases/checkpoints", lease.Run.ID, identity, &persisted); err != nil {
		return nil, err
	}
	return &persisted, nil
}

// Report persists worker progress under an active lease.
func (client *Client) Report(ctx context.Context, lease *Lease, update RunUpdate) (*Run, error) {
	if update.Status != "" && update.Status != "running" {
		return nil, errors.New("Vyral worker progress status must be running")
	}
	identity, err := client.leaseIdentity(lease)
	if err != nil {
		return nil, err
	}
	identity["update"] = update
	var run Run
	if _, err := client.post(ctx, "report", "/execution/workers/leases/reports", lease.Run.ID, identity, &run); err != nil {
		return nil, err
	}
	return &run, nil
}

// RecordEvent appends a token-safe, product-owned trace event under an active lease.
func (client *Client) RecordEvent(ctx context.Context, lease *Lease, event TraceEvent) error {
	if strings.TrimSpace(event.Type) == "" {
		return errors.New("Vyral trace event type is required")
	}
	identity, err := client.leaseIdentity(lease)
	if err != nil {
		return err
	}
	identity["type"] = strings.TrimSpace(event.Type)
	if event.Message != "" {
		identity["message"] = event.Message
	}
	if event.Severity != "" {
		identity["severity"] = event.Severity
	}
	if event.Details != nil {
		identity["details"] = event.Details
	}
	_, err = client.post(ctx, "record-event", "/execution/workers/leases/events", lease.Run.ID, identity, nil)
	return err
}

// PutArtifact persists an artifact or artifact URI under an active lease.
func (client *Client) PutArtifact(ctx context.Context, lease *Lease, artifact ArtifactWrite) (*Artifact, error) {
	if strings.TrimSpace(artifact.Name) == "" {
		return nil, errors.New("Vyral artifact name is required")
	}
	if artifact.Text == "" && artifact.Content == nil && strings.TrimSpace(artifact.URI) == "" {
		return nil, errors.New("Vyral artifact requires text, content, or uri")
	}
	identity, err := client.leaseIdentity(lease)
	if err != nil {
		return nil, err
	}
	identity["artifact"] = artifact
	var persisted Artifact
	if _, err := client.post(ctx, "put-artifact", "/execution/workers/leases/artifacts", lease.Run.ID, identity, &persisted); err != nil {
		return nil, err
	}
	return &persisted, nil
}

func (client *Client) GetCheckpoint(ctx context.Context, lease *Lease, key string) (*Checkpoint, error) {
	if strings.TrimSpace(key) == "" {
		return nil, errors.New("Vyral checkpoint key is required")
	}
	identity, err := client.leaseIdentity(lease)
	if err != nil {
		return nil, err
	}
	identity["key"] = strings.TrimSpace(key)
	var checkpoint Checkpoint
	status, err := client.post(ctx, "get-checkpoint", "/execution/workers/leases/checkpoints/read", lease.Run.ID, identity, &checkpoint)
	if err != nil {
		if apiError, ok := err.(*APIError); ok && apiError.StatusCode == http.StatusNotFound {
			return nil, nil
		}
		return nil, err
	}
	if status == http.StatusNoContent {
		return nil, nil
	}
	return &checkpoint, nil
}

func (client *Client) Wait(ctx context.Context, lease *Lease, request WaitRequest) (*WaitResponse, error) {
	if strings.TrimSpace(request.Name) == "" {
		return nil, errors.New("Vyral wait name is required")
	}
	if request.Kind != WaitKindExternalEvent && request.Kind != WaitKindTimer {
		return nil, fmt.Errorf("unsupported Vyral wait kind %q", request.Kind)
	}
	if request.Kind == WaitKindTimer && request.FireAtUTC == nil {
		return nil, errors.New("Vyral timer wait requires fireAtUtc")
	}
	if request.Kind == WaitKindExternalEvent && request.FireAtUTC != nil {
		return nil, errors.New("Vyral external-event wait does not accept fireAtUtc")
	}
	identity, err := client.leaseIdentity(lease)
	if err != nil {
		return nil, err
	}
	identity["kind"] = request.Kind
	identity["name"] = strings.TrimSpace(request.Name)
	if request.TimeoutAtUTC != nil {
		identity["timeoutAtUtc"] = request.TimeoutAtUTC.UTC()
	}
	if request.FireAtUTC != nil {
		identity["fireAtUtc"] = request.FireAtUTC.UTC()
	}
	if request.Payload != nil {
		identity["payload"] = request.Payload
	}
	var response WaitResponse
	if _, err := client.post(ctx, "wait", "/execution/workers/leases/wait", lease.Run.ID, identity, &response); err != nil {
		return nil, err
	}
	return &response, nil
}

func (client *Client) Complete(ctx context.Context, lease *Lease, completion Completion) (*Run, error) {
	if strings.TrimSpace(completion.Status) == "" {
		return nil, errors.New("Vyral completion status is required")
	}
	identity, err := client.leaseIdentity(lease)
	if err != nil {
		return nil, err
	}
	identity["result"] = completion
	var run Run
	if _, err := client.post(ctx, "complete", "/execution/workers/leases/complete", lease.Run.ID, identity, &run); err != nil {
		return nil, err
	}
	return &run, nil
}

// StartRun creates or safely replays a registered handler run.
func (client *ControlClient) StartRun(ctx context.Context, request StartRunRequest) (*Run, error) {
	if strings.TrimSpace(request.HandlerID) == "" {
		return nil, errors.New("Vyral handler id is required")
	}
	var run Run
	if _, err := client.transport.post(ctx, "start-run", "/execution/runs", "", request, &run); err != nil {
		return nil, err
	}
	return &run, nil
}

// GetRun reads a run by id. It returns nil for HTTP 404.
func (client *ControlClient) GetRun(ctx context.Context, runID string) (*Run, error) {
	path, err := runPath(runID)
	if err != nil {
		return nil, err
	}
	var run Run
	_, err = client.transport.request(ctx, http.MethodGet, "get-run", path, runID, nil, &run)
	if apiError, ok := err.(*APIError); ok && apiError.StatusCode == http.StatusNotFound {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &run, nil
}

// CancelRun requests cancellation and returns the updated run. It returns nil for HTTP 404.
func (client *ControlClient) CancelRun(ctx context.Context, runID string) (*Run, error) {
	path, err := runPath(runID)
	if err != nil {
		return nil, err
	}
	var run Run
	_, err = client.transport.request(ctx, http.MethodDelete, "cancel-run", path, runID, nil, &run)
	if apiError, ok := err.(*APIError); ok && apiError.StatusCode == http.StatusNotFound {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &run, nil
}

// RaiseEvent sends a scoped external event to a run.
func (client *ControlClient) RaiseEvent(ctx context.Context, runID string, event ExternalEventRequest) error {
	if strings.TrimSpace(event.Name) == "" {
		return errors.New("Vyral external event name is required")
	}
	path, err := runPath(runID)
	if err != nil {
		return err
	}
	_, err = client.transport.post(ctx, "raise-event", path+"/events", runID, event, nil)
	return err
}

// GetRuntime returns the policy-filtered runtime discovery view for this caller.
func (client *ControlClient) GetRuntime(ctx context.Context, productID, tenantID string) (*RuntimeView, error) {
	path := "/execution/runtime/effective"
	query := url.Values{}
	if strings.TrimSpace(productID) != "" {
		query.Set("productId", strings.TrimSpace(productID))
	}
	if strings.TrimSpace(tenantID) != "" {
		query.Set("tenantId", strings.TrimSpace(tenantID))
	}
	if len(query) > 0 {
		path += "?" + query.Encode()
	}
	var view RuntimeView
	if _, err := client.transport.request(ctx, http.MethodGet, "get-runtime", path, "", nil, &view); err != nil {
		return nil, err
	}
	return &view, nil
}

func (client *Client) leaseIdentity(lease *Lease) (map[string]any, error) {
	if lease == nil || strings.TrimSpace(lease.LeaseKey) == "" || strings.TrimSpace(lease.LeaseToken) == "" {
		return nil, errors.New("an active Vyral lease is required")
	}
	return map[string]any{"leaseKey": lease.LeaseKey, "leaseToken": lease.LeaseToken, "workerId": client.workerID}, nil
}

func (client *Client) post(ctx context.Context, operation, path, runID string, payload any, destination any) (int, error) {
	return client.request(ctx, http.MethodPost, operation, path, runID, payload, destination)
}

func (client *Client) request(ctx context.Context, method, operation, path, runID string, payload any, destination any) (int, error) {
	var body io.Reader
	if payload != nil {
		encoded, err := json.Marshal(payload)
		if err != nil {
			return 0, fmt.Errorf("encode Vyral %s request: %w", operation, err)
		}
		body = bytes.NewReader(encoded)
	}
	endpoint, err := client.endpoint(path)
	if err != nil {
		return 0, err
	}
	request, err := http.NewRequestWithContext(ctx, method, endpoint.String(), body)
	if err != nil {
		return 0, fmt.Errorf("create Vyral %s request: %w", operation, err)
	}
	if payload != nil {
		request.Header.Set("Content-Type", "application/json")
	}
	if client.tokenSource != nil {
		token, err := client.tokenSource.Token(ctx)
		if err != nil {
			safeError := errors.New("obtain Vyral request token")
			client.notify(Event{Operation: operation, Path: path, RunID: runID, Err: safeError})
			return 0, safeError
		}
		request.Header.Set("Authorization", "Bearer "+token)
	}
	response, err := client.httpClient.Do(request)
	if err != nil {
		client.notify(Event{Operation: operation, Path: path, RunID: runID, Err: err})
		return 0, fmt.Errorf("call Vyral %s: %w", operation, err)
	}
	defer response.Body.Close()
	if response.StatusCode < http.StatusOK || response.StatusCode >= http.StatusMultipleChoices {
		_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 4<<10))
		apiError := &APIError{Operation: operation, Path: path, StatusCode: response.StatusCode}
		client.notify(Event{Operation: operation, Path: path, RunID: runID, StatusCode: response.StatusCode, Err: apiError})
		return response.StatusCode, apiError
	}
	if destination != nil && response.StatusCode != http.StatusNoContent {
		if err := json.NewDecoder(response.Body).Decode(destination); err != nil {
			client.notify(Event{Operation: operation, Path: path, RunID: runID, StatusCode: response.StatusCode, Err: err})
			return response.StatusCode, fmt.Errorf("decode Vyral %s response: %w", operation, err)
		}
	}
	client.notify(Event{Operation: operation, Path: path, RunID: runID, StatusCode: response.StatusCode})
	return response.StatusCode, nil
}

func (client *Client) endpoint(path string) (*url.URL, error) {
	parsed, err := url.Parse(path)
	if err != nil || parsed.IsAbs() || parsed.Host != "" || !strings.HasPrefix(parsed.Path, "/") {
		return nil, errors.New("Vyral request path must be an absolute server path")
	}
	return client.baseURL.ResolveReference(parsed), nil
}

func runPath(runID string) (string, error) {
	normalized := strings.TrimSpace(runID)
	if normalized == "" || strings.ContainsAny(normalized, "/?#") {
		return "", errors.New("Vyral run id is required and cannot contain URL separators")
	}
	return "/execution/runs/" + normalized, nil
}

func (client *Client) notify(event Event) {
	if client.observe != nil {
		client.observe(event)
	}
}
