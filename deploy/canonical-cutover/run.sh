#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="$ROOT/deploy/canonical-cutover/compose.yaml"
PROJECT_NAME="vyral-canonical-cutover-$$"
MYSQL_PORT="${VYRAL_CUTOVER_MYSQL_PORT:-33306}"
POSTGRES_PORT="${VYRAL_CUTOVER_POSTGRES_PORT:-35432}"

cleanup() {
  docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" down --volumes --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" up --detach --wait

export VYRAL_MYSQL_CONNECTION_STRING="Server=127.0.0.1;Port=$MYSQL_PORT;Database=vyral_cutover;User ID=root;Password=vyral-cutover-test;SslMode=None"
export VYRAL_PGVECTOR_CONNECTION_STRING="Host=127.0.0.1;Port=$POSTGRES_PORT;Database=vyral_cutover;Username=postgres;Password=vyral-cutover-test;SSL Mode=Disable"
"$ROOT/scripts/validate-canonical-mysql-postgres-cutover.sh"
