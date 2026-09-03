#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

current="$(git config --local --get core.hooksPath || true)"
if [[ -n "$current" && "$current" != ".githooks" ]]; then
  echo "Refusing to replace existing core.hooksPath: $current" >&2
  exit 1
fi

git config --local core.hooksPath .githooks
printf 'git-hooks=installed path=.githooks\n'
