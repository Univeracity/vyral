#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STATE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vyral-canonical-mysql-conformance.XXXXXX")"
MYSQL_PORT="${VYRAL_MYSQL_CONFORMANCE_PORT:-43308}"

cleanup() {
  if [[ -S "$STATE_ROOT/mysql.sock" ]]; then
    mysqladmin --protocol=SOCKET --socket="$STATE_ROOT/mysql.sock" -uroot shutdown >/dev/null 2>&1 || true
  fi
  rm -rf -- "$STATE_ROOT"
}
trap cleanup EXIT

if ! command -v mysqld >/dev/null 2>&1 || ! command -v mysqladmin >/dev/null 2>&1; then
  echo "mysqld and mysqladmin are required for native disposable MySQL conformance." >&2
  exit 2
fi
if [[ ! "$MYSQL_PORT" =~ ^[0-9]+$ ]] || (( MYSQL_PORT < 1024 || MYSQL_PORT > 65535 )); then
  echo "VYRAL_MYSQL_CONFORMANCE_PORT must be an unprivileged TCP port." >&2
  exit 2
fi
if ss -ltn | awk '{print $4}' | rg -q ":${MYSQL_PORT}$"; then
  echo "MySQL conformance port $MYSQL_PORT is already in use." >&2
  exit 2
fi

mkdir "$STATE_ROOT/data"
mysqld --no-defaults --initialize-insecure \
  --datadir="$STATE_ROOT/data" \
  --log-error="$STATE_ROOT/initialize.log"
mysqld --no-defaults --daemonize \
  --datadir="$STATE_ROOT/data" \
  --socket="$STATE_ROOT/mysql.sock" \
  --port="$MYSQL_PORT" \
  --bind-address=127.0.0.1 \
  --pid-file="$STATE_ROOT/mysql.pid" \
  --log-error="$STATE_ROOT/server.log" \
  --mysqlx=OFF \
  --skip-log-bin \
  --innodb-flush-log-at-trx-commit=1

ready=false
for _ in {1..120}; do
  if mysqladmin --protocol=SOCKET --socket="$STATE_ROOT/mysql.sock" -uroot ping >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 0.25
done
if [[ "$ready" != true ]]; then
  echo "Disposable MySQL did not become ready." >&2
  exit 2
fi

export VYRAL_MYSQL_CONNECTION_STRING="Server=127.0.0.1;Port=$MYSQL_PORT;User ID=root;Password=;SslMode=None;AllowPublicKeyRetrieval=true"
dotnet test "$ROOT/tests/Vyral.Tests.MySql/Vyral.Tests.MySql.csproj" \
  --filter FullyQualifiedName~MySqlCanonicalStoreConformanceTests \
  --logger "console;verbosity=normal"

printf 'canonical-mysql-conformance=ok isolation=disposable-native\n'
