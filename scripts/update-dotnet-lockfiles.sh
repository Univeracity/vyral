#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if (( $# > 1 )); then
  echo "usage: scripts/update-dotnet-lockfiles.sh [solution-or-project]" >&2
  exit 2
fi

target="${1:-Vyral.sln}"
dotnet restore "$target" --force-evaluate -p:RestoreLockedMode=false
printf 'dotnet-lockfiles=updated target=%s\n' "$target"
