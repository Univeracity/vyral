#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "$1 is required." >&2
    exit 2
  }
}

require_command docker
require_command dotnet
require_command git
require_command jq

docker compose version >/dev/null
if ! docker info >/dev/null 2>&1; then
  echo "Temporal container gate requires access to a Docker daemon." >&2
  exit 2
fi

COMPOSE_FILE="$ROOT/deploy/temporal-qualification/compose.yaml"
COMPOSE_PROJECT="vyral-temporal-gate-$$"
TEMPORAL_PORT="${VYRAL_TEMPORAL_GATE_PORT:-37233}"
POSTGRES_PORT="${VYRAL_TEMPORAL_GATE_POSTGRES_PORT:-35432}"
RESULT_PATH="${VYRAL_TEMPORAL_GATE_RESULT_PATH:-$ROOT/artifacts/qualification/temporal-container-gate.json}"
STATE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vyral-temporal-gate.XXXXXX")"
OBJECT_ROOT="$STATE_ROOT/objects"
COMPOSE_ACTIVE="false"
export VYRAL_TEMPORAL_GATE_DATABASE_PASSWORD="VyralGate-${RANDOM}${RANDOM}${RANDOM}a!"

validate_port() {
  local value="$1"
  local name="$2"
  if [[ ! "$value" =~ ^[1-9][0-9]{0,4}$ ]] || (( value > 65535 )); then
    echo "$name must be a TCP port from 1 through 65535." >&2
    exit 2
  fi
}

cleanup_on_exit() {
  local exit_code="$1"
  if [[ "$COMPOSE_ACTIVE" == "true" ]]; then
    docker compose --project-name "$COMPOSE_PROJECT" --file "$COMPOSE_FILE" \
      down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi
  rm -rf -- "$STATE_ROOT"
  return "$exit_code"
}
trap 'cleanup_on_exit "$?"' EXIT

validate_port "$TEMPORAL_PORT" VYRAL_TEMPORAL_GATE_PORT
validate_port "$POSTGRES_PORT" VYRAL_TEMPORAL_GATE_POSTGRES_PORT
if [[ "$TEMPORAL_PORT" == "$POSTGRES_PORT" ]]; then
  echo "Temporal and PostgreSQL gate ports must differ." >&2
  exit 2
fi

mkdir -p "$OBJECT_ROOT" "$(dirname "$RESULT_PATH")"

echo "temporal-container-gate=starting dependencies=temporal,postgres,object-store"
COMPOSE_ACTIVE="true"
STARTUP_RESULT="passed"
set +e
VYRAL_TEMPORAL_GATE_PORT="$TEMPORAL_PORT" \
VYRAL_TEMPORAL_GATE_POSTGRES_PORT="$POSTGRES_PORT" \
docker compose --project-name "$COMPOSE_PROJECT" --file "$COMPOSE_FILE" \
  up --detach --wait --wait-timeout 240 temporal
DEPENDENCY_EXIT="$?"
NAMESPACE_EXIT=1
if (( DEPENDENCY_EXIT == 0 )); then
  docker compose --project-name "$COMPOSE_PROJECT" --file "$COMPOSE_FILE" \
    run --rm --no-deps temporal-namespace
  NAMESPACE_EXIT="$?"
fi
set -e
if (( DEPENDENCY_EXIT != 0 || NAMESPACE_EXIT != 0 )); then
  STARTUP_RESULT="failed"
fi

TEST_EXIT=1
if [[ "$STARTUP_RESULT" == "passed" ]]; then
  set +e
  VYRAL_TEMPORAL_CONTAINER_GATE=1 \
  VYRAL_EXECUTION_TEMPORAL_TARGET_HOST="127.0.0.1:${TEMPORAL_PORT}" \
  VYRAL_EXECUTION_TEMPORAL_NAMESPACE="vyral-qualification" \
  VYRAL_EXECUTION_TEMPORAL_REQUIRE_TLS=false \
  VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=postgres;Username=vyral_gate;Password=${VYRAL_TEMPORAL_GATE_DATABASE_PASSWORD};SSL Mode=Disable" \
  VYRAL_TEMPORAL_POSTGRES_REQUIRE_TLS=false \
  VYRAL_TEMPORAL_API_KEY= \
  VYRAL_TEMPORAL_TLS_ROOT_CA_PATH= \
  VYRAL_TEMPORAL_TLS_CLIENT_CERT_PATH= \
  VYRAL_TEMPORAL_TLS_CLIENT_KEY_PATH= \
  VYRAL_TEMPORAL_GATE_OBJECT_ROOT="$OBJECT_ROOT" \
  dotnet test tests/Vyral.Tests.Temporal/Vyral.Tests.Temporal.csproj --no-restore \
    --filter 'FullyQualifiedName~TemporalContainerExecutionRuntimeConformanceTests|FullyQualifiedName~TemporalWorkerProcessRecoveryTests|FullyQualifiedName~TemporalProjectionBackupRestoreTests|FullyQualifiedName~TemporalCoordinatorReplayTests' \
    --logger 'console;verbosity=minimal'
  TEST_EXIT="$?"
  set -e
fi

CLEANUP_RESULT="passed"
if ! docker compose --project-name "$COMPOSE_PROJECT" --file "$COMPOSE_FILE" \
  down --volumes --remove-orphans; then
  CLEANUP_RESULT="failed"
fi
if [[ -n "$(docker compose --project-name "$COMPOSE_PROJECT" --file "$COMPOSE_FILE" ps --all --quiet)" ]]; then
  CLEANUP_RESULT="failed"
fi
if [[ -n "$(docker volume ls --quiet --filter "label=com.docker.compose.project=${COMPOSE_PROJECT}")" ]]; then
  CLEANUP_RESULT="failed"
fi
COMPOSE_ACTIVE="false"
rm -rf -- "$STATE_ROOT"
if [[ -e "$STATE_ROOT" ]]; then
  CLEANUP_RESULT="failed"
fi

GATE_RESULT="passed"
if [[ "$STARTUP_RESULT" != "passed" ]] || (( TEST_EXIT != 0 )) || [[ "$CLEANUP_RESULT" != "passed" ]]; then
  GATE_RESULT="failed"
fi
SOURCE_TREE_CLEAN="true"
if [[ -n "$(git status --porcelain)" ]]; then
  SOURCE_TREE_CLEAN="false"
fi

jq --null-input \
  --arg result "$GATE_RESULT" \
  --arg testedAtUtc "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --arg testCommit "$(git rev-parse HEAD)" \
  --arg startup "$STARTUP_RESULT" \
  --arg cleanup "$CLEANUP_RESULT" \
  --argjson testExitCode "$TEST_EXIT" \
  --argjson sourceTreeClean "$SOURCE_TREE_CLEAN" \
  '{
    schemaVersion: 1,
    gate: "temporal_container",
    result: $result,
    environmentClass: "disposable_local_dependencies",
    testedAtUtc: $testedAtUtc,
    testCommit: $testCommit,
    sourceTreeClean: $sourceTreeClean,
    dependencies: {
      temporalServer: "1.31.0",
      postgres: "16",
      objectStore: "Vyral.Local.FileObjectStore"
    },
    startup: {
      result: $startup,
      dependenciesReady: ($startup == "passed"),
      namespaceReady: ($startup == "passed")
    },
    testExitCode: $testExitCode,
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
      "projection_backup_restore_integrity",
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
    workerProcessRecovery: {
      result: (if $testExitCode == 0 then "passed" else "failed" end),
      processBoundary: true,
      scenarios: ["activity", "absent_dispatch", "durable_wait"]
    },
    projectionBackupRestore: {
      result: (if $testExitCode == 0 then "passed" else "failed" end),
      isolatedTargetDatabase: ($testExitCode == 0),
      dumpHashVerified: ($testExitCode == 0),
      portableStateVerified: ($testExitCode == 0),
      resumedStoreOperations: ($testExitCode == 0),
      targetDatabaseRemoved: ($testExitCode == 0)
    },
    cleanup: {
      result: $cleanup,
      containersRemoved: ($cleanup == "passed"),
      volumesRemoved: ($cleanup == "passed"),
      projectionSchemasRemoved: ($cleanup == "passed"),
      objectDirectoriesRemoved: ($cleanup == "passed")
    }
  }' >"$RESULT_PATH"

echo "temporal-container-cleanup=${CLEANUP_RESULT} evidence=redacted"
if [[ "$GATE_RESULT" != "passed" ]]; then
  echo "temporal-container-gate=failed" >&2
  exit 1
fi
echo "temporal-container-gate=ok"
