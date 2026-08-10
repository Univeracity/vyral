#!/usr/bin/env bash
set -euo pipefail
umask 077

# Runs the real Temporal conformance and worker-process recovery suite against an
# operator-provisioned TLS/authenticated Temporal namespace and PostgreSQL database. The namespace
# must be dedicated to qualification because completed workflow histories remain until its
# configured retention removes them. The script creates and removes only random Vyral projection
# schemas and process-local object directories; it never creates or deletes the namespace.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "$1 is required." >&2
    exit 2
  }
}

require_setting() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "$name is required." >&2
    exit 2
  fi
}

require_command dotnet
require_command git
require_command jq
require_setting VYRAL_EXECUTION_TEMPORAL_TARGET_HOST
require_setting VYRAL_EXECUTION_TEMPORAL_NAMESPACE
require_setting VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING
require_setting VYRAL_TEMPORAL_LIVE_PROVIDER_VERSION

if [[ "${VYRAL_TEMPORAL_LIVE_CONFIRM:-}" != "dedicated-qualification-namespace" ]]; then
  echo "Set VYRAL_TEMPORAL_LIVE_CONFIRM=dedicated-qualification-namespace after verifying the namespace is disposable and dedicated to this gate." >&2
  exit 2
fi

ENVIRONMENT_CLASS="${VYRAL_TEMPORAL_LIVE_ENVIRONMENT_CLASS:-}"
if [[ "$ENVIRONMENT_CLASS" != "live_self_hosted" && "$ENVIRONMENT_CLASS" != "live_managed" ]]; then
  echo "VYRAL_TEMPORAL_LIVE_ENVIRONMENT_CLASS must be live_self_hosted or live_managed." >&2
  exit 2
fi

AUTH_MODE="${VYRAL_TEMPORAL_LIVE_AUTH_MODE:-}"
case "$AUTH_MODE" in
  api_key)
    require_setting VYRAL_TEMPORAL_API_KEY
    if [[ -n "${VYRAL_TEMPORAL_TLS_CLIENT_CERT_PATH:-}" || -n "${VYRAL_TEMPORAL_TLS_CLIENT_KEY_PATH:-}" ]]; then
      echo "Do not configure mTLS client files when VYRAL_TEMPORAL_LIVE_AUTH_MODE=api_key." >&2
      exit 2
    fi
    ;;
  mtls)
    require_setting VYRAL_TEMPORAL_TLS_CLIENT_CERT_PATH
    require_setting VYRAL_TEMPORAL_TLS_CLIENT_KEY_PATH
    if [[ -n "${VYRAL_TEMPORAL_API_KEY:-}" ]]; then
      echo "Do not configure an API key when VYRAL_TEMPORAL_LIVE_AUTH_MODE=mtls." >&2
      exit 2
    fi
    ;;
  *)
    echo "VYRAL_TEMPORAL_LIVE_AUTH_MODE must be api_key or mtls." >&2
    exit 2
    ;;
esac

for certificate_setting in \
  VYRAL_TEMPORAL_TLS_ROOT_CA_PATH \
  VYRAL_TEMPORAL_TLS_CLIENT_CERT_PATH \
  VYRAL_TEMPORAL_TLS_CLIENT_KEY_PATH; do
  certificate_path="${!certificate_setting:-}"
  if [[ -n "$certificate_path" && ! -r "$certificate_path" ]]; then
    echo "$certificate_setting must identify a readable file." >&2
    exit 2
  fi
done

if [[ ! "$VYRAL_TEMPORAL_LIVE_PROVIDER_VERSION" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]{0,159}$ ]]; then
  echo "VYRAL_TEMPORAL_LIVE_PROVIDER_VERSION has an invalid format." >&2
  exit 2
fi

RESULT_PATH="${VYRAL_TEMPORAL_LIVE_RESULT_PATH:-$ROOT/artifacts/qualification/temporal-live-gate.json}"
STATE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vyral-temporal-live.XXXXXX")"
OBJECT_ROOT="$STATE_ROOT/objects"
printf -v RESOURCE_SUFFIX '%04x%04x%04x' "$RANDOM" "$RANDOM" "$(( $$ % 65536 ))"
RESOURCE_PREFIX="vyrallive${RESOURCE_SUFFIX}"
mkdir -p "$OBJECT_ROOT" "$(dirname "$RESULT_PATH")"

cleanup_on_exit() {
  rm -rf -- "$STATE_ROOT"
}
trap cleanup_on_exit EXIT

export VYRAL_TEMPORAL_LIVE_GATE=1
export VYRAL_TEMPORAL_GATE_RESOURCE_PREFIX="$RESOURCE_PREFIX"
export VYRAL_TEMPORAL_GATE_OBJECT_ROOT="$OBJECT_ROOT"
export VYRAL_EXECUTION_TEMPORAL_REQUIRE_TLS=true
export VYRAL_TEMPORAL_POSTGRES_REQUIRE_TLS=true

echo "temporal-live-gate=running environment-class=${ENVIRONMENT_CLASS} auth=${AUTH_MODE} topology=redacted"
set +e
dotnet test tests/Vyral.Tests.Temporal/Vyral.Tests.Temporal.csproj --no-restore \
  --filter 'FullyQualifiedName~TemporalContainerExecutionRuntimeConformanceTests|FullyQualifiedName~TemporalWorkerProcessRecoveryTests|FullyQualifiedName~TemporalCoordinatorReplayTests' \
  --logger 'console;verbosity=minimal'
TEST_EXIT="$?"

dotnet test tests/Vyral.Tests.Temporal/Vyral.Tests.Temporal.csproj --no-build \
  --filter 'FullyQualifiedName~TemporalLiveCleanupAuditTests' \
  --logger 'console;verbosity=minimal'
AUDIT_EXIT="$?"
set -e

rm -rf -- "$STATE_ROOT"
OBJECT_CLEANUP="passed"
if [[ -e "$STATE_ROOT" ]]; then
  OBJECT_CLEANUP="failed"
fi

GATE_RESULT="passed"
if (( TEST_EXIT != 0 || AUDIT_EXIT != 0 )) || [[ "$OBJECT_CLEANUP" != "passed" ]]; then
  GATE_RESULT="failed"
fi
SOURCE_TREE_CLEAN="true"
if [[ -n "$(git status --porcelain)" ]]; then
  SOURCE_TREE_CLEAN="false"
fi

jq --null-input \
  --arg result "$GATE_RESULT" \
  --arg environmentClass "$ENVIRONMENT_CLASS" \
  --arg providerVersion "$VYRAL_TEMPORAL_LIVE_PROVIDER_VERSION" \
  --arg authMode "$AUTH_MODE" \
  --arg testedAtUtc "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --arg testCommit "$(git rev-parse HEAD)" \
  --arg objectCleanup "$OBJECT_CLEANUP" \
  --argjson testExitCode "$TEST_EXIT" \
  --argjson cleanupAuditExitCode "$AUDIT_EXIT" \
  --argjson sourceTreeClean "$SOURCE_TREE_CLEAN" \
  '{
    schemaVersion: 1,
    gate: "temporal_live_endpoint",
    result: $result,
    environmentClass: $environmentClass,
    scope: "temporal_postgres_with_process_local_object_store",
    providerVersion: $providerVersion,
    testedAtUtc: $testedAtUtc,
    testCommit: $testCommit,
    sourceTreeClean: $sourceTreeClean,
    authentication: {
      mode: $authMode,
      temporalTlsRequired: true,
      postgresTlsRequired: true,
      secretsRedacted: true
    },
    isolation: {
      operatorConfirmedDedicatedNamespace: true,
      randomAdapterNamespace: true,
      randomTaskQueues: true,
      randomWorkflowIds: true,
      randomProjectionSchemas: true,
      namespaceOwnedByOperator: true
    },
    testExitCode: $testExitCode,
    cleanupAuditExitCode: $cleanupAuditExitCode,
    checks: [
      "shared_execution_conformance",
      "native_timer_wait",
      "signal_before_wait",
      "artifact_object_offload",
      "continue_as_new_portable_identity",
      "continue_as_new_history_bounds",
      "coordinator_legacy_history_replay",
      "coordinator_history_body_redaction",
      "generic_host_composition",
      "preflight_worker_coordinator_readiness",
      "preflight_worker_build_compatibility",
      "worker_process_restart_activity",
      "worker_absent_dispatch_recovery",
      "worker_process_restart_wait"
    ],
    historyRollover: {
      result: (if $testExitCode == 0 then "passed" else "failed" end),
      coordinationTransitionThreshold: 32,
      inducedCoordinationTransitions: 37,
      maximumChainRuns: 8,
      maximumEventsPerTemporalRun: 512,
      maximumEncodedBytesPerTemporalRun: 1048576,
      identifiersRedacted: true
    },
    cleanup: {
      result: (if $cleanupAuditExitCode == 0 and $objectCleanup == "passed" then "passed" else "failed" end),
      projectionSchemasRemoved: ($cleanupAuditExitCode == 0),
      objectDirectoriesRemoved: ($objectCleanup == "passed"),
      namespacePreserved: true,
      workflowHistoriesRetainedByNamespacePolicy: true
    },
    limitations: {
      projectionBackupRestoreCoveredByDisposableGate: false,
      productionObjectStoreQualified: false,
      automaticQualificationPromotion: false
    }
  }' >"$RESULT_PATH"

SCHEMA_CLEANUP="passed"
if (( AUDIT_EXIT != 0 )); then
  SCHEMA_CLEANUP="failed"
fi
echo "temporal-live-cleanup=schemas:${SCHEMA_CLEANUP} objects:${OBJECT_CLEANUP} namespace:preserved evidence=redacted"
if [[ "$GATE_RESULT" != "passed" ]]; then
  echo "temporal-live-gate=failed" >&2
  exit 1
fi
echo "temporal-live-gate=ok"
