// execution-smoke-go is an intentionally small Cloud Run worker used to exercise the public
// external-worker protocol end to end. It is not a general-purpose product worker: product
// workers should implement their own handlers using the supported Go client and authenticate
// their calls to Vyral.
package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"os"
	"strings"
	"time"

	vyralexecution "github.com/univeracity/vyral/clients/go"
)

type config struct {
	serverURL      string
	serverAudience string
	serverAuthMode string
	workerID       string
	handlerIDs     []string
	ttlSeconds     float64
	workerClient   *vyralexecution.Client
	tokenSource    vyralexecution.TokenSource
}

type dispatchMessage struct {
	RunID string `json:"runId"`
}

func main() {
	cfg, err := loadConfig()
	if err != nil {
		log.Fatal(err)
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusOK) })
	mux.HandleFunc("/tasks/execution", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			w.WriteHeader(http.StatusMethodNotAllowed)
			return
		}
		defer r.Body.Close()
		var message dispatchMessage
		if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64<<10)).Decode(&message); err != nil || message.RunID == "" {
			http.Error(w, "runId is required", http.StatusBadRequest)
			return
		}
		if err := executeRun(r.Context(), cfg, message.RunID); err != nil {
			log.Printf("run %s: %v", message.RunID, err)
			http.Error(w, "execution worker failed", http.StatusInternalServerError)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	})
	mux.HandleFunc("/smoke/start", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			w.WriteHeader(http.StatusMethodNotAllowed)
			return
		}
		defer r.Body.Close()
		var request map[string]any
		if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64<<10)).Decode(&request); err != nil {
			http.Error(w, "valid execution run JSON is required", http.StatusBadRequest)
			return
		}
		var response map[string]any
		if err := postServerJSON(r.Context(), cfg, "/execution/runs", request, &response); err != nil {
			log.Printf("smoke start: %v", err)
			http.Error(w, "execution run start failed", http.StatusBadGateway)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(response)
	})
	mux.HandleFunc("/smoke/prune", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			w.WriteHeader(http.StatusMethodNotAllowed)
			return
		}
		var request map[string]any
		if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 16<<10)).Decode(&request); err != nil {
			http.Error(w, "valid prune JSON is required", http.StatusBadRequest)
			return
		}
		var response map[string]any
		if err := postServerJSON(r.Context(), cfg, "/execution/runtime/maintenance/prune", request, &response); err != nil {
			log.Printf("smoke prune: %v", err)
			http.Error(w, "execution prune failed", http.StatusBadGateway)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(response)
	})

	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}
	log.Printf("execution smoke worker listening on %s for handlers %s", port, strings.Join(cfg.handlerIDs, ","))
	server := &http.Server{
		Addr:              ":" + port,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       60 * time.Second,
		MaxHeaderBytes:    1 << 20,
	}
	log.Fatal(server.ListenAndServe())
}

func loadConfig() (config, error) {
	serverURL := strings.TrimRight(strings.TrimSpace(os.Getenv("VYRAL_SERVER_URL")), "/")
	serverAudience := strings.TrimSpace(os.Getenv("VYRAL_SERVER_AUDIENCE"))
	if serverAudience == "" {
		serverAudience = serverURL
	}
	serverAuthMode := strings.TrimSpace(os.Getenv("VYRAL_SERVER_AUTH_MODE"))
	if serverAuthMode == "" {
		serverAuthMode = "metadata-oidc"
	}
	workerID := strings.TrimSpace(os.Getenv("VYRAL_WORKER_ID"))
	handlerValues := strings.Split(os.Getenv("VYRAL_HANDLER_IDS"), ",")
	handlerIDs := make([]string, 0, len(handlerValues))
	for _, value := range handlerValues {
		if normalized := strings.TrimSpace(value); normalized != "" {
			handlerIDs = append(handlerIDs, normalized)
		}
	}
	if serverURL == "" || workerID == "" || len(handlerIDs) == 0 {
		return config{}, fmt.Errorf("VYRAL_SERVER_URL, VYRAL_WORKER_ID, and VYRAL_HANDLER_IDS are required")
	}
	if serverAuthMode != "metadata-oidc" && serverAuthMode != "none" {
		return config{}, fmt.Errorf("VYRAL_SERVER_AUTH_MODE must be metadata-oidc or none")
	}
	var tokenSource vyralexecution.TokenSource
	if serverAuthMode == "metadata-oidc" {
		tokenSource = vyralexecution.MetadataOIDCSource{Audience: serverAudience}
	}
	workerClient, err := vyralexecution.NewClient(vyralexecution.Config{
		BaseURL: serverURL, WorkerID: workerID, HandlerIDs: handlerIDs, TokenSource: tokenSource,
		Observe: func(event vyralexecution.Event) {
			if event.Err != nil {
				log.Printf("worker operation=%s run=%s status=%d error=%v", event.Operation, event.RunID, event.StatusCode, event.Err)
			}
		},
	})
	if err != nil {
		return config{}, err
	}
	return config{serverURL: serverURL, serverAudience: serverAudience, serverAuthMode: serverAuthMode, workerID: workerID, handlerIDs: handlerIDs, ttlSeconds: 30, workerClient: workerClient, tokenSource: tokenSource}, nil
}

func executeRun(ctx context.Context, cfg config, runID string) error {
	lease, err := cfg.workerClient.LeaseNext(ctx, runID, cfg.ttlSeconds)
	if err != nil {
		return err
	}
	if lease == nil {
		return nil
	}

	if _, err := cfg.workerClient.Heartbeat(ctx, lease, cfg.ttlSeconds); err != nil {
		return err
	}
	if _, err := cfg.workerClient.Checkpoint(ctx, lease, vyralexecution.CheckpointWrite{
		Key: "smoke-worker", Content: map[string]any{"attempt": lease.Run.Attempt}, Metadata: map[string]string{"worker": cfg.workerID},
	}); err != nil {
		return err
	}

	payload := map[string]any{}
	if len(lease.Run.Payload) > 0 {
		if err := json.Unmarshal(lease.Run.Payload, &payload); err != nil {
			return fmt.Errorf("decode run payload: %w", err)
		}
	}
	action, _ := payload["action"].(string)
	if action == "wait-event" || action == "wait-timer" {
		wait := vyralexecution.WaitRequest{Kind: vyralexecution.WaitKindExternalEvent, Name: "smoke-signal"}
		if action == "wait-timer" {
			fireAt := time.Now().UTC().Add(2 * time.Second)
			wait.Kind = vyralexecution.WaitKindTimer
			wait.FireAtUTC = &fireAt
			wait.Payload = map[string]any{"source": "execution-smoke-go"}
		}
		response, err := cfg.workerClient.Wait(ctx, lease, wait)
		if err != nil {
			return err
		}
		if response.Suspended {
			return nil
		}
		var outcome any
		if len(response.Outcome) > 0 {
			if err := json.Unmarshal(response.Outcome, &outcome); err != nil {
				return fmt.Errorf("decode wait outcome: %w", err)
			}
		}
		return complete(ctx, cfg, lease, vyralexecution.Completion{Status: "succeeded", Result: map[string]any{"action": action, "outcome": outcome}})
	}

	if action == "retry-once" && lease.Run.Attempt == 1 {
		return complete(ctx, cfg, lease, vyralexecution.Completion{Status: "failed", FailureClass: "smoke_retry", Error: "intentional first-attempt failure"})
	}
	return complete(ctx, cfg, lease, vyralexecution.Completion{Status: "succeeded", Result: map[string]any{"action": action, "attempt": lease.Run.Attempt, "payload": payload}})
}

func complete(ctx context.Context, cfg config, lease *vyralexecution.Lease, result vyralexecution.Completion) error {
	_, err := cfg.workerClient.Complete(ctx, lease, result)
	return err
}

func postServerJSON(ctx context.Context, cfg config, path string, request any, response any) error {
	body, err := json.Marshal(request)
	if err != nil {
		return err
	}
	httpRequest, err := http.NewRequestWithContext(ctx, http.MethodPost, cfg.serverURL+path, bytes.NewReader(body))
	if err != nil {
		return err
	}
	httpRequest.Header.Set("Content-Type", "application/json")
	if cfg.tokenSource != nil {
		token, err := cfg.tokenSource.Token(ctx)
		if err != nil {
			return fmt.Errorf("obtain Vyral request token")
		}
		httpRequest.Header.Set("Authorization", "Bearer "+token)
	}
	httpResponse, err := (&http.Client{Timeout: 20 * time.Second}).Do(httpRequest)
	if err != nil {
		return err
	}
	defer httpResponse.Body.Close()
	if httpResponse.StatusCode < 200 || httpResponse.StatusCode >= 300 {
		return fmt.Errorf("POST %s returned HTTP %d", path, httpResponse.StatusCode)
	}
	if response != nil && httpResponse.StatusCode != http.StatusNoContent {
		if err := json.NewDecoder(httpResponse.Body).Decode(response); err != nil {
			return err
		}
	}
	return nil
}
