#!/usr/bin/env python3
"""Validate retrieval-projection qualification evidence and its source binding."""

from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import re
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker


ROOT = Path(__file__).resolve().parent.parent
SCHEMA = ROOT / "qualification/retrieval-projection-qualification.schema.json"
SOURCE_EVIDENCE_HELPER = ROOT / "scripts/compute-source-tree-evidence.py"
OPAQUE_CONSUMER_REFERENCE = re.compile(
    r"^urn:vyral:private-(?:consumer-)?evidence:sha256:[0-9a-f]{64}$"
)


def _timestamp(value: str, label: str) -> datetime:
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise SystemExit(f"{label} is not an ISO-8601 timestamp.") from error
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise SystemExit(f"{label} must include a UTC offset.")
    return parsed.astimezone(timezone.utc)


def _source_evidence(root: Path, commit: str | None = None) -> dict[str, object]:
    specification = importlib.util.spec_from_file_location(
        "vyral_source_tree_evidence",
        SOURCE_EVIDENCE_HELPER,
    )
    if specification is None or specification.loader is None:
        raise SystemExit("Could not load the source-tree evidence helper.")
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    return module.compute_commit(root, commit) if commit else module.compute(root)


def _schema_errors(artifact: Any) -> list[str]:
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    return [
        f"{'/'.join(str(part) for part in error.absolute_path) or '<root>'}: {error.message}"
        for error in sorted(validator.iter_errors(artifact), key=lambda item: list(item.absolute_path))
    ]


def validate(
    artifact: dict[str, Any],
    *,
    as_of: datetime,
    source: dict[str, object] | None,
    allow_dirty: bool,
    public_disclosure: bool = False,
    source_root: Path | None = None,
) -> None:
    errors = _schema_errors(artifact)
    if errors:
        raise SystemExit("Retrieval qualification schema validation failed:\n" + "\n".join(errors))

    generated = _timestamp(artifact["generatedAtUtc"], "generatedAtUtc")
    if generated > as_of + timedelta(minutes=5):
        raise SystemExit("generatedAtUtc cannot be in the future.")
    maximum_age = timedelta(days=artifact["qualificationExpiresAfterDays"])
    adapter_ids: set[str] = set()
    historical_sources: dict[str, dict[str, object]] = {}

    required_kinds = {
        "prototype": {"unit_gate"},
        "local_conformant": {"unit_gate", "local_gate"},
        "live_qualified": {"unit_gate", "local_gate", "live_gate", "cleanup"},
        "consumer_validated": {
            "unit_gate",
            "local_gate",
            "live_gate",
            "cleanup",
            "consumer_validation",
        },
    }
    forbidden_reference = re.compile(
        r"(?:https?://|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+|access[_-]?key|secret|token)",
        re.IGNORECASE,
    )

    for adapter in artifact["adapters"]:
        adapter_id = adapter["adapterId"]
        if adapter_id in adapter_ids:
            raise SystemExit(f"Duplicate retrieval adapter ID: {adapter_id}")
        adapter_ids.add(adapter_id)
        artifact_paths: set[str] = set()
        for implementation_artifact in adapter["implementationArtifacts"]:
            relative = implementation_artifact["path"]
            parts = Path(relative).parts
            if Path(relative).is_absolute() or ".." in parts or relative in artifact_paths:
                raise SystemExit(
                    f"Adapter {adapter_id} has an invalid or duplicate implementation artifact path."
                )
            artifact_paths.add(relative)
            if source_root is not None:
                path = source_root.resolve() / relative
                if not path.is_file() or path.is_symlink():
                    raise SystemExit(
                        f"Adapter {adapter_id} implementation artifact is unavailable: {relative}"
                    )
                observed_digest = "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()
                if observed_digest != implementation_artifact["sha256"]:
                    raise SystemExit(
                        f"Adapter {adapter_id} implementation artifact digest does not match: {relative}"
                    )
        evidence_kinds = {evidence["kind"] for evidence in adapter["evidence"]}
        missing_kinds = required_kinds[adapter["qualification"]] - evidence_kinds
        if missing_kinds:
            raise SystemExit(
                f"Adapter {adapter_id} lacks evidence required for {adapter['qualification']}: "
                + ", ".join(sorted(missing_kinds))
            )

        stale_evidence = False
        for evidence in adapter["evidence"]:
            observed = _timestamp(evidence["observedAtUtc"], f"{adapter_id} observedAtUtc")
            expires = _timestamp(evidence["expiresAtUtc"], f"{adapter_id} expiresAtUtc")
            if observed > generated:
                raise SystemExit(f"Adapter {adapter_id} evidence was observed after artifact generation.")
            if expires <= observed or expires - observed > maximum_age:
                raise SystemExit(f"Adapter {adapter_id} evidence has an invalid qualification lifetime.")
            stale_evidence = stale_evidence or expires <= as_of
            if len(evidence["generationIds"]) != len(evidence["descriptorDigests"]):
                raise SystemExit(
                    f"Adapter {adapter_id} generation IDs and descriptor digests must be paired."
                )
            if forbidden_reference.search(evidence["reference"]):
                raise SystemExit(f"Adapter {adapter_id} evidence reference exposes non-portable or sensitive material.")
            disclosure = evidence.get("disclosure")
            if public_disclosure and disclosure not in {"public", "private_opaque"}:
                raise SystemExit(
                    f"Adapter {adapter_id} public evidence must declare its disclosure boundary."
                )
            if disclosure == "private_opaque":
                if not OPAQUE_CONSUMER_REFERENCE.fullmatch(evidence["reference"]):
                    raise SystemExit(
                        f"Adapter {adapter_id} private evidence must use an opaque consumer-evidence reference."
                    )
                if evidence["generationIds"] or evidence["descriptorDigests"]:
                    raise SystemExit(
                        f"Adapter {adapter_id} private evidence cannot expose consumer generation identifiers."
                    )
            elif OPAQUE_CONSUMER_REFERENCE.fullmatch(evidence["reference"]):
                raise SystemExit(
                    f"Adapter {adapter_id} opaque evidence must declare private_opaque disclosure."
                )
            if evidence["kind"] == "consumer_validation" and disclosure != "private_opaque":
                raise SystemExit(
                    f"Adapter {adapter_id} consumer validation must remain private_opaque."
                )
            if evidence["sourceDirty"] and not allow_dirty:
                raise SystemExit(
                    f"Adapter {adapter_id} evidence is bound to a dirty source tree; "
                    "use --allow-dirty only for private rehearsal."
                )
            if source_root is not None and disclosure != "private_opaque":
                commit = evidence["sourceCommit"]
                if commit not in historical_sources:
                    historical_sources[commit] = _source_evidence(source_root, commit)
                historical = historical_sources[commit]
                for field in ("sourceCommit", "sourceTreeDigest", "sourceDirty"):
                    if evidence[field] != historical[field]:
                        raise SystemExit(
                            f"Adapter {adapter_id} evidence {field} does not match its source commit."
                        )
            elif source is not None and disclosure != "private_opaque":
                for field in ("sourceCommit", "sourceTreeDigest", "sourceDirty"):
                    if evidence[field] != source[field]:
                        raise SystemExit(
                            f"Adapter {adapter_id} evidence {field} does not match the selected source tree."
                        )

        expected_status = "stale" if stale_evidence else "current"
        if adapter["status"] != expected_status:
            raise SystemExit(
                f"Adapter {adapter_id} status must be {expected_status} at the selected as-of time."
            )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact", type=Path)
    parser.add_argument("--source-root", type=Path)
    parser.add_argument("--as-of", help="ISO-8601 verification time; defaults to now")
    parser.add_argument("--allow-dirty", action="store_true")
    parser.add_argument(
        "--public-disclosure",
        action="store_true",
        help="require explicit disclosure labels and opaque consumer-validation evidence",
    )
    arguments = parser.parse_args()

    artifact = json.loads(arguments.artifact.read_text(encoding="utf-8"))
    as_of = _timestamp(arguments.as_of, "--as-of") if arguments.as_of else datetime.now(timezone.utc)
    validate(
        artifact,
        as_of=as_of,
        source=None,
        allow_dirty=arguments.allow_dirty,
        public_disclosure=arguments.public_disclosure,
        source_root=arguments.source_root,
    )
    print(
        "retrieval-projection-qualification=ok "
        f"adapters={len(artifact['adapters'])} dirty-allowed={str(arguments.allow_dirty).lower()} "
        f"public-disclosure={str(arguments.public_disclosure).lower()}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
