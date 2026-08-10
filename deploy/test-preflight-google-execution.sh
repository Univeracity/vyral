#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

cat >"$WORK/google-cloud-run.env" <<'EOF'
ExecutionRuntime__Adapter=google-firestore-cloud-tasks
CanonicalStore__Enabled=false
ExecutionRuntime__Google__RequireExplicitWorkerRoutes=true
ExecutionRuntime__Google__ProjectId=test-project
ExecutionRuntime__Google__LocationId=us-central1
ExecutionRuntime__Google__QueueId=execution-queue
ExecutionRuntime__Google__WorkerUrl=https://worker.example/tasks/execution
ExecutionRuntime__Google__FirestoreRootCollection=vyral_execution_test
ExecutionRuntime__Google__ArtifactObjectContainer=execution-artifacts
ExecutionRuntime__Google__ServiceAccountEmail=tasks@test-project.iam.gserviceaccount.com
ExecutionRuntime__Google__OidcAudience=https://worker.example
ExecutionRuntime__Google__WorkerRoutes__0__HandlerId=product.example.job
ExecutionRuntime__Google__WorkerRoutes__0__WorkerUrl=https://worker.example/tasks/execution
ExecutionRuntime__Google__WorkerRoutes__0__OidcAudience=https://worker.example
ExecutionRuntime__ExternalHandlers__0__HandlerId=product.example.job
VYRAL_FIRESTORE_ROOT_COLLECTION=vyral
VYRAL_OBJECT_STORE=google-cloud-storage
VYRAL_GCS_BUCKET=execution-artifacts
Server__ExecutionAccess__AuthenticationMode=google-oidc
Server__ExecutionAccess__AllowedAudiences__0=https://vyral.example
Server__ExecutionAccess__IdentityPolicies__0__Principal=worker@test-project.iam.gserviceaccount.com
Server__ExecutionAccess__IdentityPolicies__0__ProductId=product-example
Server__ExecutionAccess__IdentityPolicies__0__WorkerId=product-worker
Server__ExecutionAccess__IdentityPolicies__0__AllowedOperations__0=worker
Server__ExecutionAccess__IdentityPolicies__0__AllowedHandlerIds__0=product.example.job
ExecutionRuntime__ProductPolicies__0__ProductId=product-example
ExecutionRuntime__ProductPolicies__0__AllowedServiceIdentities__0=product-worker
ExecutionRuntime__ProductPolicies__0__AllowedHandlerIds__0=product.example.job
EOF

mkdir "$WORK/bin"
cat >"$WORK/bin/gcloud" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
arguments=" $* "
if [[ "$arguments" == *" run services describe vyral-server "* ]]; then
  printf '%s\n' '{"status":{"url":"https://vyral.example"},"spec":{"template":{"spec":{"serviceAccountName":"vyral@test-project.iam.gserviceaccount.com","containers":[{"env":[{"name":"CanonicalStore__Enabled","value":"false"}]}]}}}}'
elif [[ "$arguments" == *" run services describe worker "* ]]; then
  printf '%s\n' '{"status":{"url":"https://worker.example"},"spec":{"template":{"spec":{"serviceAccountName":"worker@test-project.iam.gserviceaccount.com"}}}}'
elif [[ "$arguments" == *" run services get-iam-policy vyral-server "* ]]; then
  printf '%s\n' '{"bindings":[{"role":"roles/run.invoker","members":["serviceAccount:worker@test-project.iam.gserviceaccount.com"]}]}'
elif [[ "$arguments" == *" run services get-iam-policy worker "* ]]; then
  printf '%s\n' '{"bindings":[{"role":"roles/run.invoker","members":["serviceAccount:tasks@test-project.iam.gserviceaccount.com"]}]}'
elif [[ "$arguments" == *" projects get-iam-policy "* ]]; then
  printf '%s\n' '{"bindings":[{"role":"roles/datastore.user","members":["serviceAccount:vyral@test-project.iam.gserviceaccount.com"]},{"role":"roles/cloudtasks.enqueuer","members":["serviceAccount:vyral@test-project.iam.gserviceaccount.com"]}]}'
elif [[ "$arguments" == *" iam service-accounts get-iam-policy "* ]]; then
  printf '%s\n' '{"bindings":[{"role":"roles/iam.serviceAccountUser","members":["serviceAccount:vyral@test-project.iam.gserviceaccount.com","serviceAccount:service-12345@gcp-sa-cloudtasks.iam.gserviceaccount.com"]}]}'
elif [[ "$arguments" == *" storage buckets get-iam-policy "* ]]; then
  printf '%s\n' '{"bindings":[{"role":"roles/storage.objectUser","members":["serviceAccount:vyral@test-project.iam.gserviceaccount.com"]}]}'
elif [[ "$arguments" == *" projects describe "* ]]; then
  printf '%s\n' '12345'
elif [[ "$arguments" == *" firestore databases describe "* || "$arguments" == *" tasks queues describe "* || "$arguments" == *" iam service-accounts describe "* || "$arguments" == *" storage buckets describe "* ]]; then
  exit 0
else
  printf 'unexpected read-only gcloud call: %s\n' "$*" >&2
  exit 90
fi
EOF
chmod +x "$WORK/bin/gcloud"

output="$(PATH="$WORK/bin:$PATH" \
  VYRAL_EXECUTION_PROJECT_ID=test-project \
  VYRAL_EXECUTION_SERVER_SERVICE=vyral-server \
  VYRAL_EXECUTION_WORKER_SERVICE=worker \
  VYRAL_EXECUTION_WORKER_ID=product-worker \
  VYRAL_EXECUTION_HANDLER_IDS=product.example.job \
  VYRAL_EXECUTION_CONFIG_FILE="$WORK/google-cloud-run.env" \
  "$ROOT/deploy/preflight-google-execution.sh")"

if [[ "$output" != *"Google execution deployment preflight passed."* ]]; then
  printf 'expected preflight to pass, output follows:\n%s\n' "$output" >&2
  exit 1
fi

sed -i 's/ExecutionRuntime__Google__RequireExplicitWorkerRoutes=true/ExecutionRuntime__Google__RequireExplicitWorkerRoutes=false/' "$WORK/google-cloud-run.env"
if PATH="$WORK/bin:$PATH" \
  VYRAL_EXECUTION_PROJECT_ID=test-project \
  VYRAL_EXECUTION_SERVER_SERVICE=vyral-server \
  VYRAL_EXECUTION_WORKER_SERVICE=worker \
  VYRAL_EXECUTION_WORKER_ID=product-worker \
  VYRAL_EXECUTION_HANDLER_IDS=product.example.job \
  VYRAL_EXECUTION_CONFIG_FILE="$WORK/google-cloud-run.env" \
  "$ROOT/deploy/preflight-google-execution.sh" >"$WORK/failure.txt" 2>&1; then
  printf 'expected preflight to reject disabled explicit worker routes\n' >&2
  exit 1
fi
if ! grep -q 'RequireExplicitWorkerRoutes must be true' "$WORK/failure.txt"; then
  printf 'expected explicit-route failure, output follows:\n' >&2
  sed -n '1,200p' "$WORK/failure.txt" >&2
  exit 1
fi

printf 'google-execution-preflight-test=ok\n'
