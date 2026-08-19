#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

export_args=()
if [[ "${1:-}" == "--allow-dirty" ]]; then
  export_args+=(--allow-dirty)
  shift
fi
if (( $# != 0 )); then
  echo "usage: scripts/verify-pr-release-boundary.sh [--allow-dirty]" >&2
  exit 2
fi

# Pull requests prove that public/release policy and the deterministic source
# export remain coherent. The focused container-security workflow scans image-affecting changes
# before merge; package-consumer, SBOM, qualification, and full-regression evidence is rebuilt from
# canonical main, tags, and explicit release rehearsals.
python3 scripts/verify-publication-policy.py
python3 scripts/verify-publication-cohort.py
python3 scripts/verify-container-security-release.py
python3 scripts/verify-worker-container-release.py
python3 scripts/verify-version-policy.py
python3 scripts/verify-public-sdk-surface.py
python3 scripts/verify-public-contract-schemas.py
python3 scripts/test-classify-ci-changes.py
scripts/scan-release-secrets.sh
scripts/test-scan-release-secrets.sh
scripts/scan-release-ownership.sh
scripts/verify-public-export.sh "${export_args[@]}"

printf 'pr-release-boundary=ok full-release-evidence=canonical-main\n'
