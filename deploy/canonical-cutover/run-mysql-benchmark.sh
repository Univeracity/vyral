#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="$ROOT/deploy/canonical-cutover/compose.yaml"
PROJECT_NAME="vyral-canonical-mysql-benchmark-$$"
MYSQL_PORT="${VYRAL_CUTOVER_MYSQL_PORT:-33306}"

cleanup() {
  docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" down --volumes --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" up --detach --wait mysql
export VYRAL_MYSQL_CONNECTION_STRING="Server=127.0.0.1;Port=$MYSQL_PORT;User ID=root;Password=vyral-cutover-test;SslMode=None"
"$ROOT/scripts/run-canonical-mysql-benchmark.sh" "$@"
