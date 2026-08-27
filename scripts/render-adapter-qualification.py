#!/usr/bin/env python3
"""Render the canonical adapter qualification artifact as concise Markdown."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
from typing import Any


LEVELS = (
    "prototype",
    "local_conformant",
    "live_qualified",
    "consumer_validated",
)
LEVEL_LABELS = {
    "prototype": "Prototype",
    "local_conformant": "Local conformant",
    "live_qualified": "Live qualified",
    "consumer_validated": "Consumer validated",
}
ENVIRONMENT_LABELS = {
    "unit_fixture": "Unit fixture",
    "deterministic_fixture": "Deterministic fixture",
    "local_dependency": "Local dependency",
    "live_self_hosted": "Live self-hosted",
    "live_managed": "Live managed",
    "consumer_environment": "Consumer environment",
}


def fail(message: str) -> None:
    raise ValueError(f"adapter qualification view: {message}")


def mapping(value: object, field: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{field} must be an object")
    return value


def sequence(value: object, field: str) -> list[Any]:
    if not isinstance(value, list):
        fail(f"{field} must be an array")
    return value


def text(value: object, field: str, *, maximum: int = 300) -> str:
    if (
        not isinstance(value, str)
        or not value.strip()
        or len(value) > maximum
        or "\n" in value
        or "\r" in value
    ):
        fail(f"{field} must be bounded single-line text")
    return value


def integer(value: object, field: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        fail(f"{field} must be a non-negative integer")
    return value


def markdown(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace("|", "\\|")
        .replace("`", "\\`")
    )


def render(artifact: object) -> str:
    root = mapping(artifact, "artifact")
    summary = mapping(root.get("summary"), "summary")
    adapters = sequence(root.get("adapters"), "adapters")
    if not adapters:
        fail("adapters must not be empty")

    rows: list[dict[str, object]] = []
    counts = {level: 0 for level in LEVELS}
    current_count = 0
    stale_count = 0
    adapter_ids: set[str] = set()
    for index, raw_adapter in enumerate(adapters):
        adapter = mapping(raw_adapter, f"adapters[{index}]")
        adapter_id = text(
            adapter.get("adapterId"), f"adapters[{index}].adapterId", maximum=100
        )
        if adapter_id in adapter_ids:
            fail(f"duplicate adapter id {adapter_id!r}")
        adapter_ids.add(adapter_id)
        display_name = text(
            adapter.get("displayName"),
            f"adapters[{index}].displayName",
            maximum=200,
        )
        package = text(
            adapter.get("package"), f"adapters[{index}].package", maximum=160
        )
        capabilities = sequence(
            adapter.get("advertisedCapabilities"),
            f"adapters[{index}].advertisedCapabilities",
        )
        if not capabilities or any(
            not isinstance(item, str) or not item for item in capabilities
        ):
            fail(f"adapters[{index}].advertisedCapabilities is invalid")

        qualification = mapping(
            adapter.get("qualification"), f"adapters[{index}].qualification"
        )
        level = qualification.get("level")
        if level not in LEVELS:
            fail(f"adapters[{index}].qualification.level is invalid")
        assert isinstance(level, str)
        counts[level] += 1
        status = qualification.get("status")
        if status == "current":
            current_count += 1
        elif status == "stale":
            stale_count += 1
        else:
            fail(f"adapters[{index}].qualification.status is invalid")
        environment = qualification.get("environmentClass")
        if environment not in ENVIRONMENT_LABELS:
            fail(
                f"adapters[{index}].qualification.environmentClass is invalid"
            )
        assert isinstance(environment, str)
        evidence = sequence(
            qualification.get("evidence"),
            f"adapters[{index}].qualification.evidence",
        )
        if not evidence:
            fail(f"adapters[{index}].qualification.evidence must not be empty")
        if any(
            not isinstance(item, dict) or item.get("result") != "passed"
            for item in evidence
        ):
            fail(f"adapters[{index}] contains non-passing evidence")
        tested_at = text(
            qualification.get("testedAtUtc"),
            f"adapters[{index}].qualification.testedAtUtc",
            maximum=40,
        )
        rows.append(
            {
                "id": adapter_id,
                "name": display_name,
                "package": package,
                "level": level,
                "environment": environment,
                "capabilities": len(capabilities),
                "evidence": len(evidence),
                "tested": tested_at[:10],
                "status": status,
            }
        )

    expected = {
        "adapterCount": len(rows),
        "currentAdapterCount": current_count,
        "staleAdapterCount": stale_count,
        "prototypeAdapterCount": counts["prototype"],
        "localConformantAdapterCount": counts["local_conformant"],
        "liveQualifiedAdapterCount": counts["live_qualified"],
        "consumerValidatedAdapterCount": counts["consumer_validated"],
    }
    for field, value in expected.items():
        if integer(summary.get(field), f"summary.{field}") != value:
            fail(f"summary.{field} does not match the adapter records")

    generated_at = text(
        root.get("generatedAtUtc"), "generatedAtUtc", maximum=40
    )
    cadence = integer(
        root.get("qualificationExpiresAfterDays"),
        "qualificationExpiresAfterDays",
    )
    lines = [
        "<!-- Generated by scripts/render-adapter-qualification.py. -->",
        "# Execution adapter qualification",
        "",
        (
            "Claims must be supported by evidence. This snapshot reports "
            f"**{expected['localConformantAdapterCount']} locally conformant**, "
            f"**{expected['prototypeAdapterCount']} prototype**, and "
            f"**{expected['liveQualifiedAdapterCount']} live-qualified** "
            f"execution adapters across **{expected['adapterCount']} tracked**."
        ),
        "",
        (
            f"The checked evidence baseline was generated on `{generated_at[:10]}`. "
            f"Evidence is current for {cadence} days after its test date; a stale "
            "record remains visible but does not support a current readiness claim."
        ),
        "",
        "## Current matrix",
        "",
        "| Adapter | Package | Qualification | Environment | Capabilities | Evidence | Tested | Freshness |",
        "| --- | --- | --- | --- | ---: | ---: | --- | --- |",
    ]
    for row in rows:
        level = str(row["level"])
        environment = str(row["environment"])
        status = str(row["status"])
        lines.append(
            "| "
            + " | ".join(
                (
                    markdown(str(row["name"])),
                    f"`{markdown(str(row['package']))}`",
                    LEVEL_LABELS[level],
                    ENVIRONMENT_LABELS[environment],
                    str(row["capabilities"]),
                    str(row["evidence"]),
                    f"`{row['tested']}`",
                    "Current" if status == "current" else "Stale",
                )
            )
            + " |"
        )

    lines.extend(
        (
            "",
            "## How to read the evidence",
            "",
            "- **Capabilities** are behaviors the adapter advertises. They are not, by themselves, a readiness claim.",
            "- **Qualification** states how the exact advertised capability set was exercised.",
            "- **Freshness** states whether that evidence remains inside the declared operational cadence.",
            "- The [canonical JSON](adapter-qualification.json) contains capability names, test commits, evidence kinds, references, and reproduction commands. Its [schema](adapter-qualification.schema.json) is the release contract.",
            "",
            "| Level | Evidence boundary |",
            "| --- | --- |",
            "| Prototype | Deterministic unit or fixture proof. No portability or production-readiness claim. |",
            "| Local conformant | Shared conformance, restart/fault behavior, and public-surface proof against a local or deterministic dependency shape. |",
            "| Live qualified | Current provider version, isolated live gate, redacted result artifact, and cleanup evidence. |",
            "| Consumer validated | Live qualification plus evidence from a representative consumer environment. |",
            "",
            "Provider endpoints, account or tenant identifiers, credentials, and consumer identities do not belong in this public artifact. Consumer-validation evidence is represented by an opaque content digest; the private identity-to-receipt mapping remains outside the repository unless the consumer separately authorizes disclosure. A workflow run or package presence does not promote an adapter automatically; the checked qualification record is the claim boundary.",
            "",
        )
    )
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Render adapter qualification JSON as deterministic Markdown."
    )
    parser.add_argument(
        "source",
        nargs="?",
        type=Path,
        default=Path("qualification/adapter-qualification.json"),
    )
    parser.add_argument(
        "output",
        nargs="?",
        type=Path,
        default=Path("qualification/README.md"),
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Fail if output differs instead of writing it.",
    )
    arguments = parser.parse_args()
    try:
        artifact = json.loads(arguments.source.read_text(encoding="utf-8"))
        rendered = render(artifact)
        if arguments.check:
            current = arguments.output.read_text(encoding="utf-8")
            if current != rendered:
                raise ValueError(
                    f"{arguments.output} is stale; rerun this renderer"
                )
            print(f"adapter-qualification-view=current output={arguments.output}")
            return
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
        print(f"adapter-qualification-view=ok output={arguments.output}")
    except (OSError, json.JSONDecodeError, ValueError) as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(1) from None


if __name__ == "__main__":
    main()
