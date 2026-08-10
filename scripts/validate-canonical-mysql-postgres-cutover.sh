#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if [[ -z "${VYRAL_MYSQL_CONNECTION_STRING:-}" ]]; then
  echo "VYRAL_MYSQL_CONNECTION_STRING is required." >&2
  exit 2
fi
if [[ -z "${VYRAL_PGVECTOR_CONNECTION_STRING:-}" ]]; then
  echo "VYRAL_PGVECTOR_CONNECTION_STRING is required." >&2
  exit 2
fi

dotnet test tests/Vyral.Tests.MySql/Vyral.Tests.MySql.csproj \
  --filter 'FullyQualifiedName~MySqlCanonicalStoreConformanceTests|FullyQualifiedName~MySqlToPostgresCanonicalCutoverTests' \
  --logger "console;verbosity=normal"

dotnet test tests/Vyral.Tests.Pgvector/Vyral.Tests.Pgvector.csproj \
  --filter FullyQualifiedName~PostgresCanonicalStoreConformanceTests \
  --logger "console;verbosity=normal"

printf 'canonical-cutover-gate=ok source=mysql target=postgresql projection=sqlite\n'
