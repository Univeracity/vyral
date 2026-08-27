#!/usr/bin/env python3
"""Validate portable runtime fixture schemas, integrity, profiles, and goldens."""

from __future__ import annotations

import json
from pathlib import Path
import sys
from typing import Any

from jsonschema import Draft202012Validator


ROOT = Path(__file__).resolve().parents[1]
RUNTIME_SOURCE = ROOT / "runtimes/python/src"
FIXTURE_ROOT = ROOT / "conformance/runtime/v1"
sys.path.insert(0, str(RUNTIME_SOURCE))

from vyral_runtime import (  # noqa: E402
    CONTRACT_VERSION,
    FIXTURE_VERSION,
    RUNTIME_VERSION,
    RuntimeProfileId,
    run_bundled_canonical_scenario,
    run_bundled_native_execution_scenario,
    run_bundled_external_worker_scenario,
    load_conformance_manifest,
    run_bundled_record_store_scenario,
    run_bundled_record_store_scenarios,
    run_bundled_goldens,
    run_bundled_projection_generation_scenario,
)


def read_object(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise SystemExit(f"{path.relative_to(ROOT)} must contain a JSON object.")
    return value


def validate(schema_path: Path, document_path: Path) -> None:
    schema = read_object(schema_path)
    document = read_object(document_path)
    Draft202012Validator.check_schema(schema)
    errors = sorted(
        Draft202012Validator(schema).iter_errors(document),
        key=lambda error: tuple(str(part) for part in error.absolute_path),
    )
    if errors:
        details = []
        for error in errors:
            location = ".".join(str(part) for part in error.absolute_path) or "<root>"
            details.append(f"{document_path.relative_to(ROOT)}:{location}: {error.message}")
        raise SystemExit("Runtime conformance schema validation failed:\n- " + "\n- ".join(details))


def main() -> int:
    manifest_path = FIXTURE_ROOT / "manifest.json"
    manifest_schema_path = FIXTURE_ROOT / "manifest.schema.json"
    scenario_schema_path = FIXTURE_ROOT / "scenario.schema.json"

    validate(manifest_schema_path, manifest_path)
    manifest_document = read_object(manifest_path)
    for descriptor in manifest_document["scenarios"]:
        validate(scenario_schema_path, FIXTURE_ROOT / descriptor["path"])

    manifest = load_conformance_manifest(FIXTURE_ROOT)
    if manifest.fixture_version != FIXTURE_VERSION:
        raise SystemExit("Runtime fixture and Python package fixture versions differ.")
    if manifest.contract_version != CONTRACT_VERSION:
        raise SystemExit("Runtime fixture and Python package contract versions differ.")
    expected_profiles = {profile.value for profile in RuntimeProfileId}
    actual_profiles = set(manifest.profiles)
    if actual_profiles != expected_profiles:
        missing = sorted(expected_profiles - actual_profiles)
        unexpected = sorted(actual_profiles - expected_profiles)
        raise SystemExit(
            f"Runtime conformance profile drift: missing={missing}, unexpected={unexpected}"
        )

    results = run_bundled_goldens(FIXTURE_ROOT)
    record_results = run_bundled_record_store_scenario(FIXTURE_ROOT)
    record_scenario_results = run_bundled_record_store_scenarios(
        FIXTURE_ROOT
    )
    worker_results = run_bundled_external_worker_scenario(FIXTURE_ROOT)
    canonical_results = run_bundled_canonical_scenario(FIXTURE_ROOT)
    native_results = run_bundled_native_execution_scenario(
        FIXTURE_ROOT
    )
    projection_generation_results = run_bundled_projection_generation_scenario(
        FIXTURE_ROOT
    )
    print(
        f"runtime-conformance=ok fixture={FIXTURE_VERSION} contract={CONTRACT_VERSION} "
        f"runner={RUNTIME_VERSION} minimum-runner={manifest.runner_version} "
        f"profiles={len(actual_profiles)} scenarios={len(manifest.scenarios)} "
        f"goldens={len(results)} record-core-steps={len(record_results)} "
        f"record-steps={len(record_scenario_results)} "
        f"external-worker-steps={len(worker_results)} "
        f"canonical-steps={len(canonical_results)} "
        f"native-execution-steps={len(native_results)} "
        f"projection-generation-steps={len(projection_generation_results)}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
