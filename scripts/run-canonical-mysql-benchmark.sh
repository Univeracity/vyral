#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if [[ -z "${VYRAL_MYSQL_CONNECTION_STRING:-}" ]]; then
  echo "VYRAL_MYSQL_CONNECTION_STRING is required." >&2
  exit 2
fi

export VYRAL_BENCHMARK_SOURCE_COMMIT="${VYRAL_BENCHMARK_SOURCE_COMMIT:-$(git rev-parse HEAD)}"
dotnet run --project tools/Vyral.CanonicalStore.Benchmarks/Vyral.CanonicalStore.Benchmarks.csproj -- "$@"
