#!/usr/bin/env bash
set -euo pipefail

# Read-only deployment preflight for the Firestore/Cloud Tasks execution adapter. It does not
# create, update, enqueue, deploy, or delete any Google resource.

usage() {
  cat <<'EOF'
Usage:
  VYRAL_EXECUTION_PROJECT_ID=PROJECT \
  VYRAL_EXECUTION_SERVER_SERVICE=vyral-server \
  VYRAL_EXECUTION_WORKER_SERVICE=product-worker \
  VYRAL_EXECUTION_WORKER_ID=product-worker \
  VYRAL_EXECUTION_HANDLER_IDS=product.example.job \
  VYRAL_EXECUTION_CONFIG_FILE=deploy/google-cloud-run.env \
  deploy/preflight-google-execution.sh

Required variables:
  VYRAL_EXECUTION_PROJECT_ID       Google project hosting Vyral and its execution resources.
  VYRAL_EXECUTION_SERVER_SERVICE   Existing Vyral Cloud Run service name.
  VYRAL_EXECUTION_WORKER_SERVICE   Existing Cloud Run worker service name.
  VYRAL_EXECUTION_WORKER_ID        Verified worker id configured in Vyral's identity policy.
  VYRAL_EXECUTION_HANDLER_IDS      Comma-separated external handler ids owned by this worker.
  VYRAL_EXECUTION_CONFIG_FILE      Candidate Vyral Cloud Run env file; defaults to the example.

Optional variables:
  VYRAL_EXECUTION_REGION                   Cloud Run/Cloud Tasks region (default: us-central1).
  VYRAL_EXECUTION_RUNTIME_SERVICE_ACCOUNT  Expected Vyral server runtime service account.

The preflight verifies a candidate configuration, Firestore, Cloud Tasks, Cloud Run route
targets, service identities, required IAM bindings, and Vyral's product/worker policy shape. It
is deliberately read-only and is intended to run after candidate Cloud Run services exist but
before routing consumer traffic to the execution plane.
EOF
}

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  usage
  exit 0
fi

PROJECT_ID="${VYRAL_EXECUTION_PROJECT_ID:-}"
REGION="${VYRAL_EXECUTION_REGION:-us-central1}"
SERVER_SERVICE="${VYRAL_EXECUTION_SERVER_SERVICE:-}"
WORKER_SERVICE="${VYRAL_EXECUTION_WORKER_SERVICE:-}"
WORKER_ID="${VYRAL_EXECUTION_WORKER_ID:-}"
HANDLER_IDS_RAW="${VYRAL_EXECUTION_HANDLER_IDS:-}"
CONFIG_FILE="${VYRAL_EXECUTION_CONFIG_FILE:-deploy/google-cloud-run.env.example}"
EXPECTED_RUNTIME_SA="${VYRAL_EXECUTION_RUNTIME_SERVICE_ACCOUNT:-}"
FAILURES=0
declare -a ROUTE_TASKS_SERVICE_ACCOUNTS=()
declare -a ROUTE_QUEUES=()

pass() { printf 'PASS  %s\n' "$*"; }
fail() { printf 'FAIL  %s\n' "$*" >&2; FAILURES=$((FAILURES + 1)); }
info() { printf 'INFO  %s\n' "$*"; }

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    fail "required command is not available: $1"
  fi
}

require_value() {
  local name="$1" value="$2"
  if [[ -z "${value//[[:space:]]/}" ]]; then
    fail "$name is required"
    return 1
  fi
  return 0
}

normalize_principal() {
  local value="$1"
  value="${value#serviceAccount:}"
  printf '%s' "$value"
}

normalize_url() {
  local value="$1"
  printf '%s' "${value%/}"
}

config_value() {
  local key="$1"
  awk -v key="$key" '
    {
      sub(/\r$/, "")
      if ($0 ~ /^[[:space:]]*#/ || $0 ~ /^[[:space:]]*$/) next
      separator = index($0, "=")
      if (separator == 0) next
      candidate = substr($0, 1, separator - 1)
      if (candidate == key) {
        print substr($0, separator + 1)
        exit
      }
    }
  ' "$CONFIG_FILE"
}

config_array_contains() {
  local prefix="$1" expected="$2"
  awk -v prefix="$prefix" -v expected="$expected" '
    {
      sub(/\r$/, "")
      if ($0 ~ /^[[:space:]]*#/ || $0 ~ /^[[:space:]]*$/) next
      separator = index($0, "=")
      if (separator == 0) next
      key = substr($0, 1, separator - 1)
      value = substr($0, separator + 1)
      if (index(key, prefix) == 1 && value == expected) found = 1
    }
    END { exit (found ? 0 : 1) }
  ' "$CONFIG_FILE"
}

config_value_is_real() {
  local name="$1" value="$2"
  if [[ -z "$value" || "$value" == *replace-with* || "$value" == *example.com* ]]; then
    fail "$name must be set to a deployment value, not a placeholder"
    return 1
  fi
  return 0
}

has_run_invoker() {
  local service="$1" principal="$2"
  gcloud run services get-iam-policy "$service" --project "$PROJECT_ID" --region "$REGION" --format=json \
    | jq -e --arg principal "serviceAccount:${principal}" '
      any(.bindings[]?; .role == "roles/run.invoker" and any(.members[]?; . == $principal))
    ' >/dev/null
}

has_project_role() {
  local principal="$1" role="$2"
  gcloud projects get-iam-policy "$PROJECT_ID" --format=json \
    | jq -e --arg principal "serviceAccount:${principal}" --arg role "$role" '
      any(.bindings[]?; .role == $role and any(.members[]?; . == $principal))
    ' >/dev/null
}

has_service_account_user() {
  local target_service_account="$1" principal="$2"
  gcloud iam service-accounts get-iam-policy "$target_service_account" --project "$PROJECT_ID" --format=json \
    | jq -e --arg principal "serviceAccount:${principal}" '
      any(.bindings[]?; .role == "roles/iam.serviceAccountUser" and any(.members[]?; . == $principal))
    ' >/dev/null
}

has_bucket_object_access() {
  local bucket="$1" principal="$2"
  gcloud storage buckets get-iam-policy "gs://${bucket}" --format=json \
    | jq -e --arg principal "serviceAccount:${principal}" '
      any(.bindings[]?; (.role == "roles/storage.objectUser" or .role == "roles/storage.objectAdmin" or .role == "roles/storage.admin") and any(.members[]?; . == $principal))
    ' >/dev/null
}

find_identity_policy() {
  local principal="$1" index policy_principal
  for index in $(seq 0 99); do
    policy_principal="$(normalize_principal "$(config_value "Server__ExecutionAccess__IdentityPolicies__${index}__Principal")")"
    if [[ "$policy_principal" == "$principal" ]]; then
      printf '%s' "$index"
      return 0
    fi
  done
  return 1
}

find_product_policy() {
  local product_id="$1" index candidate
  for index in $(seq 0 99); do
    candidate="$(config_value "ExecutionRuntime__ProductPolicies__${index}__ProductId")"
    if [[ "$candidate" == "$product_id" ]]; then
      printf '%s' "$index"
      return 0
    fi
  done
  return 1
}

check_external_handler() {
  local handler_id="$1" index candidate
  for index in $(seq 0 99); do
    candidate="$(config_value "ExecutionRuntime__ExternalHandlers__${index}__HandlerId")"
    if [[ "$candidate" == "$handler_id" ]]; then
      pass "external handler is registered: $handler_id"
      return 0
    fi
  done
  fail "external handler is not registered in candidate config: $handler_id"
  return 1
}

check_worker_route() {
  local handler_id="$1" worker_url="$2" default_project="$3" default_location="$4" default_queue="$5" default_service_account="$6" default_url="$7" default_audience="$8"
  local index candidate route_project route_location route_queue route_service_account route_url route_audience
  for index in $(seq 0 99); do
    candidate="$(config_value "ExecutionRuntime__Google__WorkerRoutes__${index}__HandlerId")"
    if [[ "$candidate" != "$handler_id" ]]; then
      continue
    fi
    route_project="$(config_value "ExecutionRuntime__Google__WorkerRoutes__${index}__ProjectId")"
    route_location="$(config_value "ExecutionRuntime__Google__WorkerRoutes__${index}__LocationId")"
    route_queue="$(config_value "ExecutionRuntime__Google__WorkerRoutes__${index}__QueueId")"
    route_service_account="$(config_value "ExecutionRuntime__Google__WorkerRoutes__${index}__ServiceAccountEmail")"
    route_url="$(config_value "ExecutionRuntime__Google__WorkerRoutes__${index}__WorkerUrl")"
    route_audience="$(config_value "ExecutionRuntime__Google__WorkerRoutes__${index}__OidcAudience")"
    route_project="${route_project:-$default_project}"
    route_location="${route_location:-$default_location}"
    route_queue="${route_queue:-$default_queue}"
    route_service_account="${route_service_account:-$default_service_account}"
    route_url="${route_url:-$default_url}"
    route_audience="${route_audience:-$default_audience}"
    if [[ "$route_project" == "$PROJECT_ID" ]]; then pass "worker route project for $handler_id matches target project"; else fail "worker route project for $handler_id must equal $PROJECT_ID"; fi
    if [[ "$route_location" == "$REGION" ]]; then pass "worker route location for $handler_id matches target region"; else fail "worker route location for $handler_id must equal $REGION"; fi
    if gcloud tasks queues describe "$route_queue" --project "$PROJECT_ID" --location "$route_location" >/dev/null 2>&1; then pass "worker route queue for $handler_id is available: $route_queue"; else fail "worker route queue for $handler_id is not available: $route_queue"; fi
    if gcloud iam service-accounts describe "$route_service_account" --project "$PROJECT_ID" >/dev/null 2>&1; then pass "worker route OIDC service account for $handler_id exists"; else fail "worker route OIDC service account for $handler_id is not visible: $route_service_account"; fi
    ROUTE_TASKS_SERVICE_ACCOUNTS+=("$route_service_account")
    ROUTE_QUEUES+=("$route_queue")
    if [[ "$(normalize_url "$route_url")" == "$(normalize_url "$worker_url")" || "$route_url" == "$(normalize_url "$worker_url")"/* ]]; then
      pass "worker route for $handler_id targets $route_url"
    else
      fail "worker route for $handler_id targets $route_url, not worker service $worker_url"
    fi
    if [[ "$(normalize_url "$route_audience")" == "$(normalize_url "$worker_url")" ]]; then
      pass "worker route OIDC audience for $handler_id matches worker service"
    else
      fail "worker route OIDC audience for $handler_id must equal worker service URL"
    fi
    return 0
  done
  fail "no explicit WorkerRoutes entry for handler $handler_id"
  return 1
}

require_command gcloud
require_command jq
if [[ "$FAILURES" -gt 0 ]]; then
  exit 2
fi

require_value VYRAL_EXECUTION_PROJECT_ID "$PROJECT_ID" || true
require_value VYRAL_EXECUTION_SERVER_SERVICE "$SERVER_SERVICE" || true
require_value VYRAL_EXECUTION_WORKER_SERVICE "$WORKER_SERVICE" || true
require_value VYRAL_EXECUTION_WORKER_ID "$WORKER_ID" || true
require_value VYRAL_EXECUTION_HANDLER_IDS "$HANDLER_IDS_RAW" || true
if [[ ! -f "$CONFIG_FILE" ]]; then
  fail "candidate config file does not exist: $CONFIG_FILE"
fi
if [[ "$FAILURES" -gt 0 ]]; then
  exit 2
fi

IFS=',' read -r -a HANDLER_IDS <<< "$HANDLER_IDS_RAW"
for index in "${!HANDLER_IDS[@]}"; do
  HANDLER_IDS[$index]="${HANDLER_IDS[$index]//[[:space:]]/}"
  if [[ -z "${HANDLER_IDS[$index]}" ]]; then
    fail "VYRAL_EXECUTION_HANDLER_IDS contains an empty value"
  fi
done
if [[ "$FAILURES" -gt 0 ]]; then
  exit 2
fi

info "read-only preflight project=$PROJECT_ID region=$REGION server=$SERVER_SERVICE worker=$WORKER_SERVICE"

SERVER_JSON="$(gcloud run services describe "$SERVER_SERVICE" --project "$PROJECT_ID" --region "$REGION" --format=json 2>/dev/null || true)"
if [[ -z "$SERVER_JSON" ]]; then
  fail "Vyral server Cloud Run service is not visible: $SERVER_SERVICE"
  exit 1
fi
SERVER_URL="$(jq -r '.status.url // empty' <<< "$SERVER_JSON")"
SERVER_SERVICE_ACCOUNT="$(jq -r '.spec.template.spec.serviceAccountName // empty' <<< "$SERVER_JSON")"
config_value_is_real "Vyral server URL" "$SERVER_URL" || true
config_value_is_real "Vyral server service account" "$SERVER_SERVICE_ACCOUNT" || true
if [[ -n "$EXPECTED_RUNTIME_SA" && "$EXPECTED_RUNTIME_SA" != "$SERVER_SERVICE_ACCOUNT" ]]; then
  fail "Vyral server runs as $SERVER_SERVICE_ACCOUNT, expected $EXPECTED_RUNTIME_SA"
else
  pass "Vyral server runtime service account is $SERVER_SERVICE_ACCOUNT"
fi

WORKER_JSON="$(gcloud run services describe "$WORKER_SERVICE" --project "$PROJECT_ID" --region "$REGION" --format=json 2>/dev/null || true)"
if [[ -z "$WORKER_JSON" ]]; then
  fail "execution worker Cloud Run service is not visible: $WORKER_SERVICE"
  exit 1
fi
WORKER_URL="$(jq -r '.status.url // empty' <<< "$WORKER_JSON")"
WORKER_SERVICE_ACCOUNT="$(jq -r '.spec.template.spec.serviceAccountName // empty' <<< "$WORKER_JSON")"
config_value_is_real "execution worker URL" "$WORKER_URL" || true
config_value_is_real "execution worker service account" "$WORKER_SERVICE_ACCOUNT" || true

GOOGLE_PROJECT="$(config_value "ExecutionRuntime__Google__ProjectId")"
GOOGLE_LOCATION="$(config_value "ExecutionRuntime__Google__LocationId")"
QUEUE_ID="$(config_value "ExecutionRuntime__Google__QueueId")"
FIRESTORE_ROOT="$(config_value "ExecutionRuntime__Google__FirestoreRootCollection")"
TASKS_SERVICE_ACCOUNT="$(config_value "ExecutionRuntime__Google__ServiceAccountEmail")"
DEFAULT_WORKER_URL="$(config_value "ExecutionRuntime__Google__WorkerUrl")"
DEFAULT_OIDC_AUDIENCE="$(config_value "ExecutionRuntime__Google__OidcAudience")"
ARTIFACT_OBJECT_CONTAINER="$(config_value "ExecutionRuntime__Google__ArtifactObjectContainer")"
OBJECT_STORE="$(config_value "VYRAL_OBJECT_STORE")"
GCS_BUCKET="$(config_value "VYRAL_GCS_BUCKET")"
INGEST_STAGING_CONTAINER="$(config_value "VYRAL_INGEST_STAGING_CONTAINER")"
AUTH_MODE="$(config_value "Server__ExecutionAccess__AuthenticationMode")"
RECORD_ROOT="$(config_value "VYRAL_FIRESTORE_ROOT_COLLECTION")"
RUNTIME_ADAPTER="$(config_value "ExecutionRuntime__Adapter")"
EXPLICIT_ROUTES="$(config_value "ExecutionRuntime__Google__RequireExplicitWorkerRoutes")"
CANONICAL_STORE_ENABLED="$(config_value "CanonicalStore__Enabled")"
DEPLOYED_CANONICAL_STORE_ENABLED="$(jq -r '[.spec.template.spec.containers[]?.env[]? | select(.name == "CanonicalStore__Enabled") | .value] | first // empty' <<< "$SERVER_JSON")"

if [[ "$RUNTIME_ADAPTER" == "google-firestore-cloud-tasks" || "$RUNTIME_ADAPTER" == "google" || "$RUNTIME_ADAPTER" == "firestore-cloud-tasks" ]]; then pass "candidate config selects the Google execution adapter"; else fail "ExecutionRuntime__Adapter must select google-firestore-cloud-tasks"; fi
if [[ "$EXPLICIT_ROUTES" == "true" ]]; then pass "candidate config requires explicit worker routes"; else fail "ExecutionRuntime__Google__RequireExplicitWorkerRoutes must be true"; fi
if [[ "$CANONICAL_STORE_ENABLED" == "false" ]]; then pass "candidate config explicitly disables unused CanonicalStore routes"; else fail "execution-only deployments must set CanonicalStore__Enabled=false; a canonical deployment needs its own CanonicalAccess policy configuration"; fi
if [[ "$DEPLOYED_CANONICAL_STORE_ENABLED" == "false" ]]; then pass "deployed server explicitly disables unused CanonicalStore routes"; else fail "deployed server must set CanonicalStore__Enabled=false before this execution-only preflight can pass"; fi
if [[ "$GOOGLE_PROJECT" == "$PROJECT_ID" ]]; then pass "execution project matches target project"; else fail "ExecutionRuntime__Google__ProjectId must equal $PROJECT_ID"; fi
if [[ "$GOOGLE_LOCATION" == "$REGION" ]]; then pass "execution location matches target region"; else fail "ExecutionRuntime__Google__LocationId must equal $REGION"; fi
config_value_is_real "ExecutionRuntime__Google__QueueId" "$QUEUE_ID" || true
config_value_is_real "ExecutionRuntime__Google__FirestoreRootCollection" "$FIRESTORE_ROOT" || true
config_value_is_real "ExecutionRuntime__Google__ServiceAccountEmail" "$TASKS_SERVICE_ACCOUNT" || true
if [[ -n "$RECORD_ROOT" && "$FIRESTORE_ROOT" == "$RECORD_ROOT" ]]; then
  fail "execution Firestore root must differ from VYRAL_FIRESTORE_ROOT_COLLECTION"
else
  pass "execution Firestore root is dedicated: $FIRESTORE_ROOT"
fi
if [[ "$AUTH_MODE" == "google-oidc" ]]; then pass "execution access uses google-oidc"; else fail "Server__ExecutionAccess__AuthenticationMode must be google-oidc"; fi
if config_array_contains "Server__ExecutionAccess__AllowedAudiences__" "$SERVER_URL"; then pass "Vyral server URL is an allowed OIDC audience"; else fail "Server__ExecutionAccess__AllowedAudiences must include $SERVER_URL"; fi

if [[ -n "$ARTIFACT_OBJECT_CONTAINER" ]]; then
  config_value_is_real "ExecutionRuntime__Google__ArtifactObjectContainer" "$ARTIFACT_OBJECT_CONTAINER" || true
  if [[ "$OBJECT_STORE" == "google-cloud-storage" ]]; then pass "execution artifact offload uses google-cloud-storage"; else fail "execution artifact offload requires VYRAL_OBJECT_STORE=google-cloud-storage"; fi
  if [[ "$GCS_BUCKET" == "$ARTIFACT_OBJECT_CONTAINER" ]]; then pass "execution artifact container matches VYRAL_GCS_BUCKET"; else fail "execution artifact container must match VYRAL_GCS_BUCKET"; fi
  if gcloud storage buckets describe "gs://${ARTIFACT_OBJECT_CONTAINER}" >/dev/null 2>&1; then pass "execution artifact bucket is available: $ARTIFACT_OBJECT_CONTAINER"; else fail "execution artifact bucket is not available: $ARTIFACT_OBJECT_CONTAINER"; fi
  if has_bucket_object_access "$ARTIFACT_OBJECT_CONTAINER" "$SERVER_SERVICE_ACCOUNT"; then pass "Vyral runtime can read and write execution artifacts"; else fail "Vyral runtime lacks object access on execution artifact bucket"; fi
fi

if gcloud firestore databases describe --database="(default)" --project "$PROJECT_ID" >/dev/null 2>&1; then pass "Firestore default database is available"; else fail "Firestore default database is not available"; fi
if gcloud tasks queues describe "$QUEUE_ID" --project "$PROJECT_ID" --location "$REGION" >/dev/null 2>&1; then pass "Cloud Tasks queue is available: $QUEUE_ID"; else fail "Cloud Tasks queue is not available: $QUEUE_ID"; fi
if gcloud iam service-accounts describe "$TASKS_SERVICE_ACCOUNT" --project "$PROJECT_ID" >/dev/null 2>&1; then pass "Cloud Tasks OIDC service account exists"; else fail "Cloud Tasks OIDC service account is not visible: $TASKS_SERVICE_ACCOUNT"; fi

for handler_id in "${HANDLER_IDS[@]}"; do
  check_external_handler "$handler_id" || true
  check_worker_route "$handler_id" "$WORKER_URL" "$GOOGLE_PROJECT" "$GOOGLE_LOCATION" "$QUEUE_ID" "$TASKS_SERVICE_ACCOUNT" "$DEFAULT_WORKER_URL" "$DEFAULT_OIDC_AUDIENCE" || true
done

WORKER_POLICY_INDEX="$(find_identity_policy "$WORKER_SERVICE_ACCOUNT" || true)"
if [[ -z "$WORKER_POLICY_INDEX" ]]; then
  fail "no execution identity policy grants the worker principal $WORKER_SERVICE_ACCOUNT"
else
  pass "worker identity policy exists for $WORKER_SERVICE_ACCOUNT"
  POLICY_WORKER_ID="$(config_value "Server__ExecutionAccess__IdentityPolicies__${WORKER_POLICY_INDEX}__WorkerId")"
  POLICY_PRODUCT_ID="$(config_value "Server__ExecutionAccess__IdentityPolicies__${WORKER_POLICY_INDEX}__ProductId")"
  if [[ "$POLICY_WORKER_ID" == "$WORKER_ID" ]]; then pass "worker identity policy binds worker id $WORKER_ID"; else fail "worker identity policy must bind worker id $WORKER_ID"; fi
  if config_array_contains "Server__ExecutionAccess__IdentityPolicies__${WORKER_POLICY_INDEX}__AllowedOperations__" "worker"; then pass "worker identity policy permits worker operation"; else fail "worker identity policy must permit worker operation"; fi
  for handler_id in "${HANDLER_IDS[@]}"; do
    if config_array_contains "Server__ExecutionAccess__IdentityPolicies__${WORKER_POLICY_INDEX}__AllowedHandlerIds__" "$handler_id"; then pass "worker identity policy permits handler $handler_id"; else fail "worker identity policy must permit handler $handler_id"; fi
  done
  PRODUCT_POLICY_INDEX="$(find_product_policy "$POLICY_PRODUCT_ID" || true)"
  if [[ -z "$PRODUCT_POLICY_INDEX" ]]; then
    fail "no execution product policy exists for worker product $POLICY_PRODUCT_ID"
  else
    if config_array_contains "ExecutionRuntime__ProductPolicies__${PRODUCT_POLICY_INDEX}__AllowedServiceIdentities__" "$WORKER_ID"; then pass "product policy permits worker id $WORKER_ID"; else fail "product policy must permit worker id $WORKER_ID"; fi
    for handler_id in "${HANDLER_IDS[@]}"; do
      if config_array_contains "ExecutionRuntime__ProductPolicies__${PRODUCT_POLICY_INDEX}__AllowedHandlerIds__" "$handler_id"; then pass "product policy permits handler $handler_id"; else fail "product policy must permit handler $handler_id"; fi
    done
  fi
fi

if has_run_invoker "$SERVER_SERVICE" "$WORKER_SERVICE_ACCOUNT"; then pass "worker service account can invoke Vyral server"; else fail "worker service account lacks roles/run.invoker on Vyral server"; fi
if has_project_role "$SERVER_SERVICE_ACCOUNT" "roles/datastore.user"; then pass "Vyral runtime can use Firestore"; else fail "Vyral runtime lacks roles/datastore.user"; fi
if has_project_role "$SERVER_SERVICE_ACCOUNT" "roles/cloudtasks.enqueuer"; then pass "Vyral runtime can enqueue Cloud Tasks"; else fail "Vyral runtime lacks roles/cloudtasks.enqueuer"; fi
for handler_id in "${HANDLER_IDS[@]}"; do
  if [[ "$handler_id" != "vyral.artifacts.record-ingest" ]]; then
    continue
  fi
  config_value_is_real "VYRAL_INGEST_STAGING_CONTAINER" "$INGEST_STAGING_CONTAINER" || true
  if [[ "$INGEST_STAGING_CONTAINER" == "$GCS_BUCKET" ]]; then pass "generic ingestion staging uses the Vyral object bucket"; else fail "VYRAL_INGEST_STAGING_CONTAINER must equal VYRAL_GCS_BUCKET for least-privilege hosted ingestion"; fi
  if has_project_role "$WORKER_SERVICE_ACCOUNT" "roles/datastore.user"; then pass "Vyral hosted artifact worker can use Firestore records"; else fail "Vyral hosted artifact worker lacks roles/datastore.user"; fi
  if [[ -n "$GCS_BUCKET" ]] && has_bucket_object_access "$GCS_BUCKET" "$WORKER_SERVICE_ACCOUNT"; then pass "Vyral hosted artifact worker can read and write generic objects"; else fail "Vyral hosted artifact worker lacks object access on $GCS_BUCKET"; fi
done
PROJECT_NUMBER="$(gcloud projects describe "$PROJECT_ID" --format='value(projectNumber)' 2>/dev/null || true)"
TASKS_AGENT="service-${PROJECT_NUMBER}@gcp-sa-cloudtasks.iam.gserviceaccount.com"
for tasks_service_account in $(printf '%s\n' "${ROUTE_TASKS_SERVICE_ACCOUNTS[@]}" | sort -u); do
  if has_run_invoker "$WORKER_SERVICE" "$tasks_service_account"; then pass "Cloud Tasks OIDC service account can invoke worker"; else fail "Cloud Tasks OIDC service account lacks roles/run.invoker on worker"; fi
  if has_service_account_user "$tasks_service_account" "$SERVER_SERVICE_ACCOUNT"; then pass "Vyral runtime can attach Cloud Tasks OIDC identity"; else fail "Vyral runtime lacks roles/iam.serviceAccountUser on Cloud Tasks OIDC service account"; fi
  if [[ -n "$PROJECT_NUMBER" ]] && has_service_account_user "$tasks_service_account" "$TASKS_AGENT"; then pass "Cloud Tasks service agent can mint OIDC identity"; else fail "Cloud Tasks service agent lacks roles/iam.serviceAccountUser on Cloud Tasks OIDC service account"; fi
done

if [[ "$FAILURES" -gt 0 ]]; then
  printf 'Google execution deployment preflight failed with %d issue(s). No resources were changed.\n' "$FAILURES" >&2
  exit 1
fi

printf 'Google execution deployment preflight passed. No resources were changed.\n'
