#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

work_root="$(mktemp -d "${TMPDIR:-/tmp}/vyral-qualification-test-XXXXXX")"
trap 'rm -rf "$work_root"' EXIT

SOURCE_DATE_EPOCH=1785230489 scripts/generate-adapter-qualification.sh "$work_root/current.json" >/dev/null
jq -e '
  .schemaVersion == "1.0" and
  .summary.adapterCount == 5 and
  .summary.localConformantAdapterCount == 2 and
  .summary.prototypeAdapterCount == 2 and
  .summary.liveQualifiedAdapterCount == 1 and
  .summary.currentLiveQualifiedCapabilityClaims == 12 and
  .summary.currentLiveQualifiedCapabilityPercentage == 18.75 and
  all(.adapters[]; .qualification.status == "current")
' "$work_root/current.json" >/dev/null

SOURCE_DATE_EPOCH=1893456000 scripts/generate-adapter-qualification.sh "$work_root/stale.json" >/dev/null
jq -e 'all(.adapters[]; .qualification.status == "stale")' "$work_root/stale.json" >/dev/null

python3 - "qualification/adapter-qualification.json" "$work_root/invalid-live.json" <<'PY'
import json
import sys
from pathlib import Path

source = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
source["adapters"][0]["qualification"]["evidence"] = [
    item
    for item in source["adapters"][0]["qualification"]["evidence"]
    if item["kind"] != "cleanup"
]
Path(sys.argv[2]).write_text(json.dumps(source), encoding="utf-8")
PY
if VYRAL_ADAPTER_QUALIFICATION_SOURCE="$work_root/invalid-live.json" \
  scripts/generate-adapter-qualification.sh "$work_root/invalid-output.json" >/dev/null 2>&1; then
  echo "Qualification generation accepted a live claim without live evidence." >&2
  exit 1
fi

python3 - "qualification/adapter-qualification.json" "$work_root/invalid-capability.json" <<'PY'
import json
import sys
from pathlib import Path

source = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
source["adapters"][0]["qualification"]["capabilities"].pop()
Path(sys.argv[2]).write_text(json.dumps(source), encoding="utf-8")
PY
if VYRAL_ADAPTER_QUALIFICATION_SOURCE="$work_root/invalid-capability.json" \
  scripts/generate-adapter-qualification.sh "$work_root/invalid-output.json" >/dev/null 2>&1; then
  echo "Qualification generation accepted a capability profile not covered by its evidence." >&2
  exit 1
fi

printf 'adapter-qualification-generator-test=ok\n'
