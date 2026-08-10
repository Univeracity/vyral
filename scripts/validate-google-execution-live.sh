#!/usr/bin/env bash
set -euo pipefail
umask 077

# Creates or reuses a paused Cloud Tasks queue and unique Firestore roots, then runs the Google
# execution transport and Firestore external-worker conformance gate. Pausing the queue proves
# task creation and OIDC shape without allowing a test task to reach a shared worker. A named
# database and reusable queue let production-adjacent projects isolate qualification state. A
# separately deployed Go smoke worker remains the end-to-end Cloud Run delivery qualification.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

require() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    printf '%s is required.\n' "$name" >&2
    exit 2
  fi
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    printf 'Required command not found: %s\n' "$1" >&2
    exit 2
  }
}

require_command gcloud
require_command dotnet
require_command jq
require VYRAL_GOOGLE_EXECUTION_PROJECT_ID
require VYRAL_GOOGLE_EXECUTION_WORKER_URL
require VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL

PROJECT_ID="$VYRAL_GOOGLE_EXECUTION_PROJECT_ID"
LOCATION_ID="${VYRAL_GOOGLE_EXECUTION_TASKS_LOCATION:-us-central1}"
DATABASE_ID="${VYRAL_GOOGLE_EXECUTION_DATABASE_ID:-(default)}"
WORKER_URL="${VYRAL_GOOGLE_EXECUTION_WORKER_URL%/}"
SERVICE_ACCOUNT_EMAIL="$VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL"
OIDC_AUDIENCE="${VYRAL_GOOGLE_EXECUTION_OIDC_AUDIENCE:-$WORKER_URL}"
STAMP="$(date -u +%Y%m%d%H%M%S)-$RANDOM"
QUEUE_ID="${VYRAL_GOOGLE_EXECUTION_EXISTING_QUEUE:-vyral-it-google-exec-${STAMP}}"
FIRESTORE_ROOT="vyral_handoff_${STAMP//-/}"
CLEANUP_RECEIPT="pending"
QUEUE_CREATED=false
QUEUE_REUSABLE=false
CLEANUP_DONE=false
QUEUE_EMPTY_OBSERVED=false
TEST_EXIT=1
RESULT_PATH="${VYRAL_GOOGLE_EXECUTION_RESULT_PATH:-}"
PROVIDER_VERSION="${VYRAL_GOOGLE_LIVE_PROVIDER_VERSION:-}"
AUTH_MODE="${VYRAL_GOOGLE_LIVE_AUTH_MODE:-application_default_credentials}"

if [[ ! "$QUEUE_ID" =~ ^vyral-it-google-exec-[a-z0-9-]+$ ]]; then
  printf '%s\n' 'Google execution queues must use the vyral-it-google-exec- prefix.' >&2
  exit 2
fi
if [[ -n "$RESULT_PATH" ]]; then
  if [[ ! "$PROVIDER_VERSION" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]{0,159}$ ]]; then
    printf '%s\n' 'VYRAL_GOOGLE_LIVE_PROVIDER_VERSION is required and has an invalid format.' >&2
    exit 2
  fi
  if [[ "$AUTH_MODE" != "workload_identity_federation" ]]; then
    printf '%s\n' 'Qualification receipts require VYRAL_GOOGLE_LIVE_AUTH_MODE=workload_identity_federation.' >&2
    exit 2
  fi
  mkdir -p "$(dirname "$RESULT_PATH")"
fi

cleanup() {
  if [[ "$CLEANUP_DONE" == true ]]; then
    return
  fi

  if [[ "$QUEUE_CREATED" == true ]]; then
    if gcloud tasks queues delete "$QUEUE_ID" --project "$PROJECT_ID" --location "$LOCATION_ID" --quiet >/dev/null 2>&1; then
      CLEANUP_RECEIPT="queue-deleted"
    else
      CLEANUP_RECEIPT="queue-delete-pending"
    fi
  elif [[ "$QUEUE_REUSABLE" == true ]]; then
    if gcloud tasks queues purge "$QUEUE_ID" --project "$PROJECT_ID" --location "$LOCATION_ID" --quiet >/dev/null 2>&1; then
      CLEANUP_RECEIPT="queue-purge-pending"
      for _ in {1..12}; do
        queued_tasks=""
        if queued_tasks="$(gcloud tasks list --queue "$QUEUE_ID" --project "$PROJECT_ID" \
          --location "$LOCATION_ID" --format='value(name)' 2>/dev/null)" && [[ -z "$queued_tasks" ]]; then
          CLEANUP_RECEIPT="queue-purged"
          QUEUE_EMPTY_OBSERVED=true
          break
        fi
        sleep 5
      done
    else
      CLEANUP_RECEIPT="queue-purge-pending"
    fi
  else
    CLEANUP_RECEIPT="queue-not-owned"
  fi
  CLEANUP_DONE=true
  unset VYRAL_GOOGLE_EXECUTION_ACCESS_TOKEN
  printf 'google-execution-live-cleanup=%s firestore-root=redacted\n' "$CLEANUP_RECEIPT"
}
trap cleanup EXIT

gcloud firestore databases describe --project "$PROJECT_ID" --database="$DATABASE_ID" --format='value(name)' >/dev/null

if [[ -n "${VYRAL_GOOGLE_EXECUTION_EXISTING_QUEUE:-}" ]]; then
  QUEUE_STATE="$(gcloud tasks queues describe "$QUEUE_ID" --project "$PROJECT_ID" \
    --location "$LOCATION_ID" --format='value(state)')"
  if [[ "$QUEUE_STATE" != PAUSED ]]; then
    printf 'Reusable Google execution queue must be PAUSED; observed %s.\n' "$QUEUE_STATE" >&2
    exit 2
  fi
  QUEUE_REUSABLE=true
else
  gcloud tasks queues create "$QUEUE_ID" --project "$PROJECT_ID" --location "$LOCATION_ID" \
    --max-dispatches-per-second=5 --max-concurrent-dispatches=5 --max-attempts=3 >/dev/null
  QUEUE_CREATED=true
  gcloud tasks queues pause "$QUEUE_ID" --project "$PROJECT_ID" --location "$LOCATION_ID" >/dev/null
fi

ACCESS_TOKEN="${VYRAL_GOOGLE_EXECUTION_ACCESS_TOKEN:-$(gcloud auth print-access-token)}"
set +e
VYRAL_GOOGLE_EXECUTION_PROJECT_ID="$PROJECT_ID" \
VYRAL_GOOGLE_EXECUTION_DATABASE_ID="$DATABASE_ID" \
VYRAL_GOOGLE_EXECUTION_FIRESTORE_ROOT="$FIRESTORE_ROOT" \
VYRAL_GOOGLE_EXECUTION_TASKS_LOCATION="$LOCATION_ID" \
VYRAL_GOOGLE_EXECUTION_TASKS_QUEUE="$QUEUE_ID" \
VYRAL_GOOGLE_EXECUTION_WORKER_URL="$WORKER_URL" \
VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL="$SERVICE_ACCOUNT_EMAIL" \
VYRAL_GOOGLE_EXECUTION_OIDC_AUDIENCE="$OIDC_AUDIENCE" \
VYRAL_GOOGLE_EXECUTION_ACCESS_TOKEN="$ACCESS_TOKEN" \
dotnet test tests/Vyral.Tests.Google/Vyral.Tests.Google.csproj --no-restore \
  --filter 'FullyQualifiedName~GoogleExecutionLive' --logger 'console;verbosity=minimal'
TEST_EXIT="$?"
set -e

cleanup
trap - EXIT

CLEANUP_EXIT=1
if [[ "$CLEANUP_RECEIPT" == "queue-deleted" || "$CLEANUP_RECEIPT" == "queue-purged" ]]; then
  CLEANUP_EXIT=0
fi
GATE_RESULT="passed"
if (( TEST_EXIT != 0 || CLEANUP_EXIT != 0 )); then
  GATE_RESULT="failed"
fi

if [[ -n "$RESULT_PATH" ]]; then
  SOURCE_TREE_CLEAN=true
  if [[ -n "$(git status --porcelain --untracked-files=all)" ]]; then
    SOURCE_TREE_CLEAN=false
  fi
  NAMED_DATABASE=false
  if [[ "$DATABASE_ID" != "(default)" ]]; then
    NAMED_DATABASE=true
  fi
  REUSABLE_QUEUE=false
  if [[ -n "${VYRAL_GOOGLE_EXECUTION_EXISTING_QUEUE:-}" ]]; then
    REUSABLE_QUEUE=true
  fi

  jq --null-input \
    --arg result "$GATE_RESULT" \
    --arg providerVersion "$PROVIDER_VERSION" \
    --arg authMode "$AUTH_MODE" \
    --arg testedAtUtc "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    --arg testCommit "$(git rev-parse HEAD)" \
    --arg queueCleanup "$CLEANUP_RECEIPT" \
    --argjson testExitCode "$TEST_EXIT" \
    --argjson cleanupExitCode "$CLEANUP_EXIT" \
    --argjson sourceTreeClean "$SOURCE_TREE_CLEAN" \
    --argjson namedDatabase "$NAMED_DATABASE" \
    --argjson reusableQueue "$REUSABLE_QUEUE" \
    --argjson queueEmptyObserved "$QUEUE_EMPTY_OBSERVED" \
    '{
      schemaVersion: 1,
      gate: "google_firestore_cloud_tasks_live",
      result: $result,
      environmentClass: "live_managed",
      scope: "firestore_cloud_tasks_paused_queue",
      providerVersion: $providerVersion,
      testedAtUtc: $testedAtUtc,
      testCommit: $testCommit,
      sourceTreeClean: $sourceTreeClean,
      authentication: {
        mode: $authMode,
        shortLivedAccessToken: true,
        serviceAccountKeyFile: false,
        secretsRedacted: true
      },
      isolation: {
        namedDatabase: $namedDatabase,
        runScopedFirestoreRoots: true,
        reusablePausedQueue: $reusableQueue,
        identifiersRedacted: true
      },
      testExitCode: $testExitCode,
      checks: [
        "cloud_tasks_oidc_shape",
        "bounded_admission_contention",
        "competitive_lease_claim",
        "shared_external_worker_conformance",
        "durable_event_wait_resume",
        "stale_lease_rejection"
      ],
      cleanup: {
        result: (if $cleanupExitCode == 0 and $testExitCode == 0 then "passed" else "failed" end),
        queueAction: $queueCleanup,
        queueEmptyObserved: $queueEmptyObserved,
        runOwnedFirestoreStateRemovedByFixture: ($testExitCode == 0),
        reusableProviderFixturesPreserved: $reusableQueue,
        identifiersRedacted: true
      },
      limitations: {
        endToEndWorkerDeliveryCovered: false,
        objectStoreCovered: false,
        automaticQualificationPromotion: false
      }
    }' >"$RESULT_PATH"
  printf 'google-execution-live-evidence=redacted result=%s\n' "$GATE_RESULT"
fi

if [[ "$GATE_RESULT" != "passed" ]]; then
  printf '%s\n' 'google-execution-live-gate=failed' >&2
  exit 1
fi
printf '%s\n' 'google-execution-live-gate=ok queue=redacted firestore-root=redacted'
