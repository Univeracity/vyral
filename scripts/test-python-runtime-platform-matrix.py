#!/usr/bin/env python3
"""Unit checks for Python runtime platform matrix aggregation."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "scripts/verify-python-runtime-platform-matrix.py"
SPEC = importlib.util.spec_from_file_location(
    "vyral_python_runtime_platform_matrix", MODULE_PATH
)
if SPEC is None or SPEC.loader is None:
    raise SystemExit("Unable to load the platform matrix verifier.")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def _receipt(system: str, python: str) -> dict[str, object]:
    return {
        "schemaVersion": "vyral.python-runtime-platform.v1",
        "status": "passed",
        "scope": "portable-matrix",
        "source": {"commit": "a" * 40, "dirty": False},
        "environment": {
            "system": system,
            "pythonVersion": python + ".9",
            "platform": f"{system}-{python}",
            "sqliteVersion": "3.45.0",
            "fts5Available": True,
        },
        "runtime": {
            "version": "0.1.2",
            "contractVersion": "0.3.0",
            "fixtureVersion": "1.0.0",
            "profiles": [{"id": "portable", "available": True}],
        },
        "localExperience": {
            "schemaVersion": "vyral.python-runtime-clean-install.v2",
            "status": "passed",
            "serverExtraVerified": True,
            "firstCitationBudgetMs": 300_000,
            "firstUseBudgetMs": 300_000,
            "artifacts": [
                {
                    "artifactKind": artifact_kind,
                    "firstCommandMs": 1_200,
                    "firstCitationMs": 900,
                    "installToQuickstartCompleteMs": 2_000,
                    "installToEditableResultMs": 2_300,
                    "starter": {
                        "status": "passed",
                        "receiptBeforeDispatch": True,
                        "restartPreservedRunIdentity": True,
                        "secondProcessReplayed": True,
                        "versionedNewRun": True,
                        "inspectableState": True,
                        "createCommandMs": 100,
                        "firstRunMs": 800,
                        "replayRunMs": 500,
                        "versionedRunMs": 850,
                    },
                }
                for artifact_kind in ("wheel", "sdist")
            ],
        },
        "gates": sorted(MODULE.REQUIRED_GATES),
    }


def main() -> int:
    with tempfile.TemporaryDirectory(
        prefix="vyral-platform-matrix-test-"
    ) as temporary:
        root = Path(temporary)
        receipts: list[Path] = []
        for system, python in sorted(MODULE.EXPECTED_MATRIX):
            path = root / system / python / "platform.json"
            path.parent.mkdir(parents=True)
            path.write_text(
                json.dumps(_receipt(system, python)),
                encoding="utf-8",
            )
            receipts.append(path)
        result = MODULE.verify(receipts)
        if result["status"] != "passed" or result["cellCount"] != 9:
            raise SystemExit("A complete platform matrix did not pass.")

        duplicate = root / "duplicate" / "platform.json"
        duplicate.parent.mkdir()
        duplicate.write_text(
            json.dumps(_receipt("Linux", "3.10")),
            encoding="utf-8",
        )
        try:
            MODULE.verify([*receipts[:-1], duplicate])
        except MODULE.MatrixError:
            pass
        else:
            raise SystemExit("A duplicate/incomplete matrix was accepted.")

        over_budget = _receipt("Linux", "3.10")
        local_experience = over_budget["localExperience"]
        assert isinstance(local_experience, dict)
        artifacts = local_experience["artifacts"]
        assert isinstance(artifacts, list)
        first_artifact = artifacts[0]
        assert isinstance(first_artifact, dict)
        first_artifact["firstCitationMs"] = 300_001
        over_budget_path = root / "over-budget.json"
        over_budget_path.write_text(
            json.dumps(over_budget),
            encoding="utf-8",
        )
        try:
            replaced = root / "Linux" / "3.10" / "platform.json"
            MODULE.verify(
                [over_budget_path, *(path for path in receipts if path != replaced)]
            )
        except MODULE.MatrixError:
            pass
        else:
            raise SystemExit("An over-budget local experience was accepted.")

        incomplete_starter = _receipt("Linux", "3.10")
        local_experience = incomplete_starter["localExperience"]
        assert isinstance(local_experience, dict)
        artifacts = local_experience["artifacts"]
        assert isinstance(artifacts, list)
        first_artifact = artifacts[0]
        assert isinstance(first_artifact, dict)
        starter = first_artifact["starter"]
        assert isinstance(starter, dict)
        starter["inspectableState"] = False
        incomplete_starter_path = root / "incomplete-starter.json"
        incomplete_starter_path.write_text(
            json.dumps(incomplete_starter),
            encoding="utf-8",
        )
        try:
            replaced = root / "Linux" / "3.10" / "platform.json"
            MODULE.verify(
                [
                    incomplete_starter_path,
                    *(path for path in receipts if path != replaced),
                ]
            )
        except MODULE.MatrixError:
            pass
        else:
            raise SystemExit("An incomplete generated starter was accepted.")
    print("python-runtime-platform-matrix-test=ok cells=9")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
