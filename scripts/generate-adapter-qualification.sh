#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

source_file="${VYRAL_ADAPTER_QUALIFICATION_SOURCE:-qualification/adapter-qualification.json}"
output_file="${1:-${VYRAL_ADAPTER_QUALIFICATION_OUTPUT:-adapter-qualification.json}}"

if [[ ! -f "$source_file" ]]; then
  echo "Adapter qualification source does not exist." >&2
  exit 2
fi

release_commit="${VYRAL_QUALIFICATION_RELEASE_COMMIT:-$(git rev-parse HEAD)}"
if [[ ! "$release_commit" =~ ^[0-9a-f]{40}$ ]]; then
  echo "Qualification release commit must be a full lowercase Git object id." >&2
  exit 2
fi

if [[ -n "${SOURCE_DATE_EPOCH:-}" ]]; then
  generated_epoch="$SOURCE_DATE_EPOCH"
else
  generated_epoch="$(git show -s --format=%ct "$release_commit")"
fi
if [[ ! "$generated_epoch" =~ ^[0-9]+$ ]]; then
  echo "SOURCE_DATE_EPOCH must be a non-negative integer." >&2
  exit 2
fi

mkdir -p "$(dirname "$output_file")"

python3 - "$source_file" "$output_file" "$release_commit" "$generated_epoch" <<'PY'
from __future__ import annotations

import json
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

source_path = Path(sys.argv[1])
output_path = Path(sys.argv[2])
release_commit = sys.argv[3]
generated_epoch = int(sys.argv[4])
artifact = json.loads(source_path.read_text(encoding="utf-8"))

levels = ("prototype", "local_conformant", "live_qualified", "consumer_validated")
level_requirements = {
    "prototype": {"unit_gate"},
    "local_conformant": {"shared_conformance", "fault_restart", "public_surface"},
    "live_qualified": {"shared_conformance", "fault_restart", "public_surface", "live_gate", "cleanup"},
    "consumer_validated": {"shared_conformance", "fault_restart", "public_surface", "live_gate", "cleanup", "consumer_validation"},
}
portable_capabilities = {
    "local.dispatch",
    "remote.orchestration",
    "in_process.handlers",
    "durable.runs",
    "durable.timers",
    "external.events",
    "durable.waits",
    "cancellation",
    "retries",
    "restart.resume",
    "leases",
    "artifacts",
    "trace.history",
    "idempotency",
    "external.workers",
}
commit_pattern = re.compile(r"^[0-9a-f]{40}$")
identifier_pattern = re.compile(r"^[a-z0-9]+(?:[._-][a-z0-9]+)*$")
version_pattern = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$")


def fail(message: str) -> None:
    raise SystemExit(f"adapter qualification: {message}")


def parse_time(value: object, field: str) -> datetime:
    if not isinstance(value, str):
        fail(f"{field} must be an RFC 3339 timestamp")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        fail(f"{field} must be an RFC 3339 timestamp")
    if parsed.tzinfo is None:
        fail(f"{field} must include a UTC offset")
    return parsed.astimezone(timezone.utc)


def iso_z(value: datetime) -> str:
    return value.astimezone(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def require_string(value: object, field: str, maximum: int = 500) -> str:
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        fail(f"{field} must be a non-empty string no longer than {maximum} characters")
    return value


def check_keys(value: dict, field: str, required: set[str], optional: set[str] | None = None) -> None:
    optional = optional or set()
    missing = required - set(value)
    unexpected = set(value) - required - optional
    if missing:
        fail(f"{field} is missing required properties: {', '.join(sorted(missing))}")
    if unexpected:
        fail(f"{field} contains unknown properties: {', '.join(sorted(unexpected))}")


check_keys(
    artifact,
    "artifact",
    {
        "schemaVersion", "coreContractVersion", "generatedAtUtc", "releaseCommit",
        "qualificationExpiresAfterDays", "summary", "adapters",
    },
    {"$schema"},
)
if artifact.get("schemaVersion") != "1.0":
    fail("schemaVersion must be 1.0")
core_version = require_string(artifact.get("coreContractVersion"), "coreContractVersion", 100)
if not version_pattern.fullmatch(core_version):
    fail("coreContractVersion is not a semantic version")
expiry_days = artifact.get("qualificationExpiresAfterDays")
if not isinstance(expiry_days, int) or isinstance(expiry_days, bool) or not 1 <= expiry_days <= 365:
    fail("qualificationExpiresAfterDays must be an integer from 1 through 365")
adapters = artifact.get("adapters")
if not isinstance(adapters, list) or not adapters:
    fail("adapters must be a non-empty array")
if not isinstance(artifact.get("summary"), dict):
    fail("summary must be an object")

generated_at = datetime.fromtimestamp(generated_epoch, timezone.utc)
adapter_ids: set[str] = set()
runtime_kinds: set[str] = set()

for index, adapter in enumerate(adapters):
    where = f"adapters[{index}]"
    if not isinstance(adapter, dict):
        fail(f"{where} must be an object")
    check_keys(
        adapter,
        where,
        {
            "adapterId", "runtimeKind", "displayName", "adapterVersion", "package",
            "advertisedCapabilities", "qualification",
        },
    )
    adapter_id = require_string(adapter.get("adapterId"), f"{where}.adapterId", 100)
    runtime_kind = require_string(adapter.get("runtimeKind"), f"{where}.runtimeKind", 100)
    if not identifier_pattern.fullmatch(adapter_id) or not identifier_pattern.fullmatch(runtime_kind):
        fail(f"{where} adapterId and runtimeKind must be portable identifiers")
    if adapter_id in adapter_ids or runtime_kind in runtime_kinds:
        fail(f"{where} duplicates an adapterId or runtimeKind")
    adapter_ids.add(adapter_id)
    runtime_kinds.add(runtime_kind)
    require_string(adapter.get("displayName"), f"{where}.displayName", 200)
    package = require_string(adapter.get("package"), f"{where}.package", 160)
    if not re.fullmatch(r"Vyral(?:\.[A-Za-z0-9]+)+", package):
        fail(f"{where}.package must be a Vyral package id")
    adapter_version = require_string(adapter.get("adapterVersion"), f"{where}.adapterVersion", 100)
    if not version_pattern.fullmatch(adapter_version):
        fail(f"{where}.adapterVersion is not a semantic version")

    advertised = adapter.get("advertisedCapabilities")
    if not isinstance(advertised, list) or not advertised or len(set(advertised)) != len(advertised):
        fail(f"{where}.advertisedCapabilities must be a non-empty unique array")
    unknown = set(advertised) - portable_capabilities
    if unknown:
        fail(f"{where} advertises unknown portable capabilities: {', '.join(sorted(unknown))}")

    qualification = adapter.get("qualification")
    if not isinstance(qualification, dict):
        fail(f"{where}.qualification must be an object")
    check_keys(
        qualification,
        f"{where}.qualification",
        {
            "level", "status", "environmentClass", "testedAtUtc", "expiresAtUtc",
            "testCommit", "adapterVersion", "coreContractVersion", "capabilities", "evidence",
        },
        {"providerVersion"},
    )
    level = qualification.get("level")
    if level not in levels:
        fail(f"{where}.qualification.level is invalid")
    environment_class = qualification.get("environmentClass")
    allowed_environments = {
        "unit_fixture", "deterministic_fixture", "local_dependency",
        "live_self_hosted", "live_managed", "consumer_environment",
    }
    if environment_class not in allowed_environments:
        fail(f"{where}.qualification.environmentClass is invalid")
    tested_at = parse_time(qualification.get("testedAtUtc"), f"{where}.qualification.testedAtUtc")
    expected_expiry = tested_at + timedelta(days=expiry_days)
    qualification["expiresAtUtc"] = iso_z(expected_expiry)
    qualification["status"] = "stale" if generated_at > expected_expiry else "current"
    test_commit = qualification.get("testCommit")
    if not isinstance(test_commit, str) or not commit_pattern.fullmatch(test_commit):
        fail(f"{where}.qualification.testCommit must be a full lowercase Git object id")
    if qualification.get("adapterVersion") != adapter_version:
        fail(f"{where} adapter and qualification versions differ")
    if qualification.get("coreContractVersion") != core_version:
        fail(f"{where} qualification core contract version differs from the artifact")
    qualified_capabilities = qualification.get("capabilities")
    if not isinstance(qualified_capabilities, list) or set(qualified_capabilities) != set(advertised):
        fail(f"{where} qualification must cover exactly the advertised capability profile")

    evidence = qualification.get("evidence")
    if not isinstance(evidence, list) or not evidence:
        fail(f"{where}.qualification.evidence must be a non-empty array")
    evidence_kinds: set[str] = set()
    for evidence_index, item in enumerate(evidence):
        item_where = f"{where}.qualification.evidence[{evidence_index}]"
        if not isinstance(item, dict):
            fail(f"{item_where} must be an object")
        check_keys(item, item_where, {"kind", "result", "reference", "command"}, {"resultArtifact"})
        kind = require_string(item.get("kind"), f"{item_where}.kind", 100)
        if kind not in {"unit_gate", "shared_conformance", "fault_restart", "public_surface", "live_gate", "cleanup", "consumer_validation"}:
            fail(f"{item_where}.kind is invalid")
        evidence_kinds.add(kind)
        if item.get("result") != "passed":
            fail(f"{item_where}.result must be passed")
        require_string(item.get("reference"), f"{item_where}.reference", 300)
        require_string(item.get("command"), f"{item_where}.command", 500)
    missing_evidence = level_requirements[level] - evidence_kinds
    if missing_evidence:
        fail(f"{where} lacks required {level} evidence: {', '.join(sorted(missing_evidence))}")
    if level in {"live_qualified", "consumer_validated"}:
        provider_version = require_string(qualification.get("providerVersion"), f"{where}.qualification.providerVersion", 160)
        if environment_class not in {"live_self_hosted", "live_managed", "consumer_environment"}:
            fail(f"{where} live qualification requires a live environment class")
        live_items = [item for item in evidence if item.get("kind") == "live_gate"]
        if not any(isinstance(item.get("resultArtifact"), str) and item["resultArtifact"] for item in live_items):
            fail(f"{where} live qualification requires a redacted resultArtifact")

if [item["adapterId"] for item in adapters] != sorted(adapter_ids):
    fail("adapters must be sorted by adapterId")

def strings(value: object):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for nested in value.values():
            yield from strings(nested)
    elif isinstance(value, list):
        for nested in value:
            yield from strings(nested)

for value in strings(adapters):
    lowered = value.lower()
    if "://" in value or "connectionstring=" in lowered or "accountkey=" in lowered or "private key" in lowered:
        fail("adapter evidence contains an endpoint or credential-shaped value")

counts = {level: sum(1 for adapter in adapters if adapter["qualification"]["level"] == level) for level in levels}
current = [adapter for adapter in adapters if adapter["qualification"]["status"] == "current"]
current_live = [
    adapter for adapter in current
    if levels.index(adapter["qualification"]["level"]) >= levels.index("live_qualified")
]
advertised_claims = sum(len(adapter["advertisedCapabilities"]) for adapter in adapters)
live_claims = sum(len(adapter["qualification"]["capabilities"]) for adapter in current_live)
artifact["generatedAtUtc"] = iso_z(generated_at)
artifact["releaseCommit"] = release_commit
artifact["summary"] = {
    "adapterCount": len(adapters),
    "currentAdapterCount": len(current),
    "staleAdapterCount": len(adapters) - len(current),
    "prototypeAdapterCount": counts["prototype"],
    "localConformantAdapterCount": counts["local_conformant"],
    "liveQualifiedAdapterCount": counts["live_qualified"],
    "consumerValidatedAdapterCount": counts["consumer_validated"],
    "advertisedCapabilityClaims": advertised_claims,
    "currentLiveQualifiedCapabilityClaims": live_claims,
    "currentLiveQualifiedCapabilityPercentage": round((live_claims / advertised_claims * 100) if advertised_claims else 0, 2),
}

output_path.write_text(json.dumps(artifact, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
PY

printf 'adapter-qualification-artifact=ok output=%s\n' "$output_file"
