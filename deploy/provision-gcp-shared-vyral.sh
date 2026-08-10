#!/usr/bin/env bash
set -euo pipefail

PROJECT_ID="${VYRAL_PROJECT_ID:-}"
PROJECT_NAME="${VYRAL_PROJECT_NAME:-Vyral Shared Services}"
BILLING_ACCOUNT="${VYRAL_BILLING_ACCOUNT:-}"
REGION="${VYRAL_REGION:-us-central1}"
SERVICE="${VYRAL_SERVICE:-vyral-server}"
REPOSITORY="${VYRAL_ARTIFACT_REPOSITORY:-cloud-run-source-deploy}"
BUCKET="${VYRAL_GCS_BUCKET:-${PROJECT_ID}-vyral-artifacts}"
RUNTIME_SA_ID="${VYRAL_RUNTIME_SERVICE_ACCOUNT_ID:-vyral-server}"
RUNTIME_SA="${RUNTIME_SA_ID}@${PROJECT_ID}.iam.gserviceaccount.com"
API_KEY_SECRET="${VYRAL_API_KEY_SECRET:-vyral-api-key}"
SOURCE_API_KEY_PROJECT="${VYRAL_API_KEY_SOURCE_PROJECT:-}"
IMAGE_TAG="${VYRAL_IMAGE_TAG:-$(git rev-parse --short HEAD 2>/dev/null || date +%Y%m%d%H%M%S)}"
IMAGE="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/${SERVICE}:${IMAGE_TAG}"
BUILD_REF="${VYRAL_BUILD_REF:-HEAD}"
EXTRA_API_KEY_SECRETS="${VYRAL_EXTRA_API_KEY_SECRETS:-}"
CONFIRM_DEPLOY="${VYRAL_CONFIRM_DEPLOY:-}"
AUTH_MODE="${VYRAL_CLOUD_RUN_AUTH_MODE:-iam-internal}"
INVOKER_SERVICE_ACCOUNTS="${VYRAL_INVOKER_SERVICE_ACCOUNTS:-}"

info() {
  printf '[vyral-gcp] %s\n' "$*"
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    printf 'Required command not found: %s\n' "$1" >&2
    exit 1
  }
}

confirm_target() {
  if [[ -z "$PROJECT_ID" ]]; then
    printf 'Set VYRAL_PROJECT_ID to the target Google project before provisioning shared Vyral.\n' >&2
    exit 1
  fi

  if [[ "$CONFIRM_DEPLOY" != "$PROJECT_ID" ]]; then
    printf 'Refusing to provision project %s without VYRAL_CONFIRM_DEPLOY=%s.\n' "$PROJECT_ID" "$PROJECT_ID" >&2
    printf 'This script creates IAM bindings, secrets, queues, storage, and deploys Cloud Run.\n' >&2
    exit 1
  fi

  case "$AUTH_MODE" in
    iam-internal|public-api-key)
      ;;
    *)
      printf 'VYRAL_CLOUD_RUN_AUTH_MODE must be iam-internal or public-api-key, not %s.\n' "$AUTH_MODE" >&2
      exit 1
      ;;
  esac

  if [[ "$AUTH_MODE" == "iam-internal" ]]; then
    reject_public_invoker_inputs
  fi
}

ensure_project() {
  if gcloud projects describe "$PROJECT_ID" >/dev/null 2>&1; then
    info "Project exists: ${PROJECT_ID}"
  else
    info "Creating project: ${PROJECT_ID}"
    gcloud projects create "$PROJECT_ID" --name="$PROJECT_NAME"
  fi

  if [[ -n "$BILLING_ACCOUNT" ]]; then
    if [[ "$(gcloud billing projects describe "$PROJECT_ID" --format='value(billingEnabled)' 2>/dev/null || true)" != "True" ]]; then
      info "Linking billing account"
      gcloud billing projects link "$PROJECT_ID" --billing-account="$BILLING_ACCOUNT"
    fi
  fi
}

enable_services() {
  info "Enabling Google APIs"
  gcloud services enable \
    run.googleapis.com \
    artifactregistry.googleapis.com \
    cloudbuild.googleapis.com \
    firestore.googleapis.com \
    storage.googleapis.com \
    storage-api.googleapis.com \
    storage-component.googleapis.com \
    cloudtasks.googleapis.com \
    secretmanager.googleapis.com \
    iam.googleapis.com \
    iamcredentials.googleapis.com \
    logging.googleapis.com \
    monitoring.googleapis.com \
    --project "$PROJECT_ID"
}

ensure_artifact_registry() {
  if gcloud artifacts repositories describe "$REPOSITORY" --location "$REGION" --project "$PROJECT_ID" >/dev/null 2>&1; then
    info "Artifact Registry repository exists: ${REPOSITORY}"
  else
    info "Creating Artifact Registry repository: ${REPOSITORY}"
    gcloud artifacts repositories create "$REPOSITORY" \
      --repository-format=docker \
      --location="$REGION" \
      --description="Cloud Run source deploy images" \
      --project "$PROJECT_ID"
  fi
}

ensure_service_account() {
  if gcloud iam service-accounts describe "$RUNTIME_SA" --project "$PROJECT_ID" >/dev/null 2>&1; then
    info "Runtime service account exists: ${RUNTIME_SA}"
  else
    info "Creating runtime service account: ${RUNTIME_SA}"
    gcloud iam service-accounts create "$RUNTIME_SA_ID" \
      --display-name="Vyral Server Runtime" \
      --project "$PROJECT_ID"
  fi

  local project_roles=(
    roles/datastore.user
    roles/cloudtasks.enqueuer
    roles/logging.logWriter
    roles/monitoring.metricWriter
  )

  for role in "${project_roles[@]}"; do
    info "Ensuring runtime project role: ${role}"
    gcloud projects add-iam-policy-binding "$PROJECT_ID" \
      --member="serviceAccount:${RUNTIME_SA}" \
      --role="$role" \
      --quiet >/dev/null
  done

}

ensure_storage() {
  if gcloud storage buckets describe "gs://${BUCKET}" --project "$PROJECT_ID" >/dev/null 2>&1; then
    info "Bucket exists: gs://${BUCKET}"
  else
    info "Creating bucket: gs://${BUCKET}"
    gcloud storage buckets create "gs://${BUCKET}" \
      --project "$PROJECT_ID" \
      --location="$REGION" \
      --uniform-bucket-level-access
  fi

  info "Ensuring runtime bucket object access: gs://${BUCKET}"
  gcloud storage buckets add-iam-policy-binding "gs://${BUCKET}" \
    --member="serviceAccount:${RUNTIME_SA}" \
    --role="roles/storage.objectAdmin" \
    --project "$PROJECT_ID" \
    --quiet >/dev/null
}

ensure_firestore() {
  if gcloud firestore databases describe --database="(default)" --project "$PROJECT_ID" >/dev/null 2>&1; then
    info "Firestore default database exists"
  else
    info "Creating Firestore native default database"
    gcloud firestore databases create \
      --database="(default)" \
      --location="$REGION" \
      --type=firestore-native \
      --project "$PROJECT_ID"
  fi
}

ensure_secret() {
  if gcloud secrets describe "$API_KEY_SECRET" --project "$PROJECT_ID" >/dev/null 2>&1; then
    info "Secret exists: ${API_KEY_SECRET}"
  else
    info "Creating secret: ${API_KEY_SECRET}"
    gcloud secrets create "$API_KEY_SECRET" \
      --replication-policy=automatic \
      --project "$PROJECT_ID"
  fi

  if [[ -n "${VYRAL_API_KEY_VALUE:-}" ]]; then
    info "Adding API key from VYRAL_API_KEY_VALUE"
    printf '%s' "$VYRAL_API_KEY_VALUE" | gcloud secrets versions add "$API_KEY_SECRET" --data-file=- --project "$PROJECT_ID" >/dev/null
  elif [[ -n "$SOURCE_API_KEY_PROJECT" ]]; then
    info "Copying API key secret from ${SOURCE_API_KEY_PROJECT}"
    gcloud secrets versions access latest --secret="$API_KEY_SECRET" --project "$SOURCE_API_KEY_PROJECT" \
      | gcloud secrets versions add "$API_KEY_SECRET" --data-file=- --project "$PROJECT_ID" >/dev/null
  else
    info "No API key value or source project supplied; existing secret version must already be usable"
  fi

  grant_secret_access "$API_KEY_SECRET"
  grant_extra_secret_access
}

grant_secret_access() {
  local secret="$1"
  info "Ensuring runtime secret access: ${secret}"
  gcloud secrets add-iam-policy-binding "$secret" \
    --member="serviceAccount:${RUNTIME_SA}" \
    --role="roles/secretmanager.secretAccessor" \
    --project "$PROJECT_ID" \
    --quiet >/dev/null
}

grant_extra_secret_access() {
  if [[ -z "$EXTRA_API_KEY_SECRETS" ]]; then
    return
  fi

  local secret
  local old_ifs="$IFS"
  IFS=','
  for secret in $EXTRA_API_KEY_SECRETS; do
    secret="${secret//[[:space:]]/}"
    if [[ -n "$secret" ]]; then
      grant_secret_access "$secret"
    fi
  done
  IFS="$old_ifs"
}

build_api_key_secret_bindings() {
  local bindings=""
  local index=1
  if [[ -z "$EXTRA_API_KEY_SECRETS" ]]; then
    printf '%s' "$bindings"
    return
  fi

  local secret
  local old_ifs="$IFS"
  IFS=','
  for secret in $EXTRA_API_KEY_SECRETS; do
    secret="${secret//[[:space:]]/}"
    if [[ -z "$secret" ]]; then
      continue
    fi
    bindings="${bindings},Server__ApiKeys__${index}=${secret}:latest"
    index=$((index + 1))
  done
  IFS="$old_ifs"
  printf '%s' "$bindings"
}

build_image() {
  info "Building image: ${IMAGE}"
  if [[ "$BUILD_REF" == "working-tree" || "$BUILD_REF" == "." ]]; then
    gcloud builds submit --tag "$IMAGE" --project "$PROJECT_ID" .
    return
  fi

  local build_dir
  build_dir="$(mktemp -d)"
  trap '[[ -n "${build_dir:-}" ]] && rm -rf "$build_dir"' RETURN
  git archive "$BUILD_REF" | tar -x -C "$build_dir"
  gcloud builds submit --tag "$IMAGE" --project "$PROJECT_ID" "$build_dir"
}

deploy_service() {
  local extra_api_key_secret_bindings
  extra_api_key_secret_bindings="$(build_api_key_secret_bindings)"
  local env_vars
  env_vars="ASPNETCORE_ENVIRONMENT=Production,CanonicalStore__Enabled=false,GOOGLE_CLOUD_PROJECT=${PROJECT_ID},VYRAL_GCP_PROJECT_ID=${PROJECT_ID},VYRAL_RECORD_STORE=google-firestore,VYRAL_TRACE_STORE=google-firestore,VYRAL_OBJECT_STORE=google-cloud-storage,VYRAL_GCS_BUCKET=${BUCKET},VYRAL_OBJECT_PROBE_CONTAINER=${BUCKET},VYRAL_FIRESTORE_ROOT_COLLECTION=vyral,VYRAL_API_KEY_HEADER=X-Vyral-Api-Key"
  local auth_args=(--no-allow-unauthenticated)
  if [[ "$AUTH_MODE" == "public-api-key" ]]; then
    auth_args=(--allow-unauthenticated)
  fi

  info "Deploying initial Cloud Run revision with auth mode ${AUTH_MODE}"
  gcloud run deploy "$SERVICE" \
    --image "$IMAGE" \
    --region "$REGION" \
    --project "$PROJECT_ID" \
    --service-account "$RUNTIME_SA" \
    "${auth_args[@]}" \
    --port 8080 \
    --cpu 1 \
    --memory 1Gi \
    --concurrency 20 \
    --min-instances 0 \
    --max-instances 3 \
    --timeout 30s \
    --set-env-vars "$env_vars" \
    --set-secrets "VYRAL_API_KEY=${API_KEY_SECRET}:latest${extra_api_key_secret_bindings}"

  local service_url
  service_url="$(gcloud run services describe "$SERVICE" --region "$REGION" --project "$PROJECT_ID" --format='value(status.url)')"
  ensure_run_invokers

  info "Vyral URL: ${service_url}"
}

ensure_run_invokers() {
  if [[ "$AUTH_MODE" != "iam-internal" ]]; then
    return
  fi

  info "Ensuring Cloud Run is not publicly invokable"
  gcloud run services remove-iam-policy-binding "$SERVICE" \
    --region "$REGION" \
    --project "$PROJECT_ID" \
    --member="allUsers" \
    --role="roles/run.invoker" \
    --quiet >/dev/null 2>&1 || true
  gcloud run services remove-iam-policy-binding "$SERVICE" \
    --region "$REGION" \
    --project "$PROJECT_ID" \
    --member="allAuthenticatedUsers" \
    --role="roles/run.invoker" \
    --quiet >/dev/null 2>&1 || true

  if [[ -z "$INVOKER_SERVICE_ACCOUNTS" ]]; then
    return
  fi

  local account
  local old_ifs="$IFS"
  IFS=','
  for account in $INVOKER_SERVICE_ACCOUNTS; do
    account="${account//[[:space:]]/}"
    if [[ -n "$account" ]]; then
      ensure_run_invoker_member "$(normalize_run_invoker_member "$account")"
    fi
  done
  IFS="$old_ifs"
}

normalize_run_invoker_member() {
  local value="$1"
  case "$value" in
    allUsers|allAuthenticatedUsers)
      if [[ "$AUTH_MODE" == "iam-internal" ]]; then
        printf 'Public invoker principal %s is not allowed when VYRAL_CLOUD_RUN_AUTH_MODE=iam-internal.\n' "$value" >&2
        printf 'Use VYRAL_CLOUD_RUN_AUTH_MODE=public-api-key for an intentionally public deployment.\n' >&2
        exit 1
      fi
      printf '%s' "$value"
      ;;
    serviceAccount:*|user:*|group:*|domain:*|allUsers|allAuthenticatedUsers)
      printf '%s' "$value"
      ;;
    *)
      printf 'serviceAccount:%s' "$value"
      ;;
  esac
}

reject_public_invoker_inputs() {
  if [[ -z "$INVOKER_SERVICE_ACCOUNTS" ]]; then
    return
  fi

  local account
  local old_ifs="$IFS"
  IFS=','
  for account in $INVOKER_SERVICE_ACCOUNTS; do
    account="${account//[[:space:]]/}"
    if [[ "$account" == "allUsers" || "$account" == "allAuthenticatedUsers" ]]; then
      printf 'Public invoker principal %s is not allowed when VYRAL_CLOUD_RUN_AUTH_MODE=iam-internal.\n' "$account" >&2
      printf 'Use VYRAL_CLOUD_RUN_AUTH_MODE=public-api-key for an intentionally public deployment.\n' >&2
      exit 1
    fi
  done
  IFS="$old_ifs"
}

ensure_run_invoker_member() {
  local member="$1"
  info "Ensuring Cloud Run invoker binding: ${member}"
  gcloud run services add-iam-policy-binding "$SERVICE" \
    --region "$REGION" \
    --project "$PROJECT_ID" \
    --member="$member" \
    --role="roles/run.invoker" \
    --quiet >/dev/null
}

main() {
  require_command gcloud
  require_command git
  confirm_target
  ensure_project
  enable_services
  ensure_artifact_registry
  ensure_service_account
  ensure_storage
  ensure_firestore
  ensure_secret
  build_image
  deploy_service
}

main "$@"
