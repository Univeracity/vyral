"""Exercise catalog reuse policy and ledger replay with synthetic judge answers."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from vyral_runtime import DecisionLedger, evaluate_decision_policy, replay_decision_receipt


POLICY = {
    "schema": "vyral.ai-decision-policy.v1",
    "id": "catalog-item-reuse",
    "version": "1.0.0",
    "questionId": "reuse",
    "answerKind": "choice",
    "accept": {"operator": "in", "values": ["same_item"]},
    "reject": {"operator": "in", "values": ["different_item"]},
    "evidence": {"requireCalibrated": True, "minimumConfidence": 0.8},
    "escalationRoute": "manual-review",
}

# Calibration flags and confidence values are test inputs for the mechanics.
CASES = (
    ("matching-kit", "same_item", True, True),
    ("bare-tool-and-kit", "different_item", True, True),
    ("package-type-missing", "same_item", True, False),
    ("unqualified-judge", "same_item", False, True),
)


def run(ledger_path: Path) -> dict:
    observations = []
    with DecisionLedger(ledger_path) as ledger:
        for case, choice, calibrated, package_type_known in CASES:
            result = {
                "status": "Succeeded",
                "provider": "synthetic-catalog-fixture",
                "capability": "ai.judge",
                "output": {
                    "validationStatus": "fixture",
                    "answers": [{
                        "questionId": "reuse",
                        "choice": choice,
                        "calibrated": calibrated,
                        "confidence": 0.94,
                    }],
                },
            }
            receipt = evaluate_decision_policy(POLICY, result)
            observations.append((case, package_type_known, result, ledger.append(receipt)))
        head = observations[-1][3]

    reports = []
    with DecisionLedger(ledger_path, create=False) as ledger:
        verification = ledger.verify(
            expected_head=head["entryHash"], expected_count=head["sequence"] + 1
        )
        for case, package_type_known, result, saved in observations:
            entry = ledger.get(entry_hash=saved["entryHash"])
            if entry is None:
                raise RuntimeError("Appended receipt could not be reopened.")
            receipt = entry["receipt"]
            replay = replay_decision_receipt(receipt, POLICY, result)
            reports.append({
                "case": case,
                "judge_policy_outcome": receipt["outcome"],
                "application_route": receipt["outcome"] if package_type_known else "escalate",
                "application_hold": None if package_type_known else "package_type_missing",
                "reason": receipt["reason"],
                "entry_hash": entry["entryHash"],
                "replay_matches": replay["matches"],
            })

    return {"evidence": "synthetic", "verification": verification, "cases": reports}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ledger", required=True, type=Path)
    args = parser.parse_args()
    report = run(args.ledger)
    print(json.dumps(report, indent=2))
    return 0 if report["verification"]["valid"] and all(
        case["replay_matches"] for case in report["cases"]
    ) else 1


if __name__ == "__main__":
    raise SystemExit(main())
