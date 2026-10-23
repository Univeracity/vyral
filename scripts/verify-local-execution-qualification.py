#!/usr/bin/env python3
"""Verify pinned evidence without launching or retrying native work."""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXPECTED = {
    "workspace-calculator": ("Succeeded", 1, False),
    "schema-array-refusal": ("Succeeded", 1, False),
    "context-refusal": ("Rejected", 0, False),
    "api-crash-after-native-response": ("Failed", 1, True),
    "cancel-after-dispatch": ("Cancelled", 1, True),
    "deadline-after-dispatch": ("TimedOut", 1, True),
    "owner-death-during-native-processing": ("Failed", 1, True),
}


def summaries():
    return [dict(case=name, status=status, completionRequests=count, reconciliationRequired=unresolved)
            for name, (status, count, unresolved) in EXPECTED.items()]


def validate_receipt(receipt):
    if receipt.get("schemaVersion") != "vyral.consumer-local-execution.v1" or receipt.get("status") != "passed":
        raise ValueError("passed consumer receipt required")
    cases = receipt["cases"]
    if len(cases) != len(EXPECTED) or {case["case"] for case in cases} != set(EXPECTED):
        raise ValueError("exact seven-case suite required")
    for case in cases:
        status, count, unresolved = EXPECTED[case["case"]]
        progress = case.get("after", case.get("progress"))
        if (progress["phase"], progress["status"], case["completionRequests"], progress["reconciliationRequired"]) != (
                "terminal", status, count, unresolved):
            raise ValueError("outcome, dispatch or reconciliation mismatch")
        if "before" in case and any(case["before"]["invocation"][field] != progress["invocation"][field]
                for field in ("originalAttempt", "startedAt", "deadlineUtc")):
            raise ValueError("attempt authority changed")
        if case["case"] == "api-crash-after-native-response" and case["sameKeyReplayId"] != case["jobId"]:
            raise ValueError("same-key job changed")
        if case["case"] == "workspace-calculator" and case["acceptedEdit"]["validationCases"] != 4:
            raise ValueError("application validation missing")
        if case["case"] == "owner-death-during-native-processing" and case["nativeProcessingObserved"] is not True:
            raise ValueError("native processing not observed")
    if receipt["completionRequests"] != 6 or not receipt["cleanup"] or any(
            item["liveDescendants"] for item in receipt["cleanup"]):
        raise ValueError("dispatch or cleanup evidence incomplete")
    if not receipt["nativeResourceWitness"] or any(
            (item["addressSpaceBytes"], item["cpuSeconds"]) != (4294967296, 1200)
            for item in receipt["nativeResourceWitness"]):
        raise ValueError("qualified resource limits differ")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--qualification", type=Path, default=ROOT / "qualification/local-execution-qualification.json")
    parser.add_argument("--receipt", type=Path)
    args = parser.parse_args()
    record = json.loads(args.qualification.read_text())
    if record["schemaVersion"] != "vyral.local-execution-qualification.v1" or record["level"] != "bounded-local-native":
        raise ValueError("unknown qualification contract")
    for item in record["implementationFiles"]:
        file = (ROOT / item["path"]).resolve()
        if not file.is_relative_to(ROOT) or hashlib.sha256(file.read_bytes()).hexdigest() != item["sha256"]:
            raise ValueError("stale implementation qualification: " + item["path"])
    if record["cases"] != summaries():
        raise ValueError("qualification summary incomplete")
    if args.receipt:
        receipt = json.loads(args.receipt.read_text())
        validate_receipt(receipt)
        if receipt["modelSha256"] != record["profile"]["modelSha256"] or (
                receipt["contextTokens"], receipt["maxOutputTokens"]) != (2048, 128):
            raise ValueError("receipt profile changed")
    print("local-execution-qualification=ok (bounded mechanics; no semantic accuracy claim)")


if __name__ == "__main__":
    main()
