#!/usr/bin/env python3
"""Validate a retained Vyral-versus-ripgrep comparison receipt."""

from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
from typing import Any, Mapping, Sequence


ROOT = Path(__file__).resolve().parent.parent
COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$")


def _object(value: Any, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise ValueError(f"{name} must be an object")
    return value


def _number(value: Any, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{name} must be a number")
    return float(value)


def _integer(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise ValueError(f"{name} must be an integer")
    return value


def _boolean(value: Any, name: str) -> bool:
    if not isinstance(value, bool):
        raise ValueError(f"{name} must be a boolean")
    return value


def _variant(report: Mapping[str, Any], name: str) -> Mapping[str, Any]:
    quality = _object(report.get("quality"), "quality")
    variants = _object(quality.get("variants"), "quality.variants")
    return _object(variants.get(name), f"quality.variants.{name}")


def _group(
    report: Mapping[str, Any], variant: str, group: str
) -> Mapping[str, Any]:
    groups = _object(_variant(report, variant).get("groups"), f"{variant}.groups")
    return _object(groups.get(group), f"{variant}.groups.{group}")


def _recomputed_aggregate(cases: Sequence[Mapping[str, Any]]) -> dict[str, float | int | None]:
    if not cases:
        raise ValueError("A quality aggregate must contain cases")
    metrics = [_object(case.get("metrics"), "case.metrics") for case in cases]
    positive = [metric for metric in metrics if metric.get("relevant")]
    return {
        "caseCount": len(cases),
        "positiveCaseCount": len(positive),
        "meanRecallAtK": round(
            sum(_number(metric.get("recallAtK"), "recallAtK") for metric in metrics)
            / len(metrics),
            6,
        ),
        "meanPrecisionReturned": round(
            sum(
                _number(metric.get("precisionReturned"), "precisionReturned")
                for metric in metrics
            )
            / len(metrics),
            6,
        ),
        "meanReciprocalRank": (
            round(
                sum(
                    _number(metric.get("reciprocalRank"), "reciprocalRank")
                    for metric in positive
                )
                / len(positive),
                6,
            )
            if positive
            else None
        ),
        "exactSetRate": round(
            sum(1.0 if _boolean(metric.get("exactSet"), "exactSet") else 0.0 for metric in metrics)
            / len(metrics),
            6,
        ),
    }


def _string_array(value: Any, name: str) -> list[str]:
    if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
        raise ValueError(f"{name} must be an array of strings")
    return value


def _recomputed_metrics(metrics: Mapping[str, Any], top_k: int) -> dict[str, Any]:
    returned = list(dict.fromkeys(_string_array(metrics.get("returned"), "returned")))[:top_k]
    relevant = _string_array(metrics.get("relevant"), "relevant")
    expected = set(relevant)
    found = [path for path in returned if path in expected]
    if expected:
        reciprocal_rank = next(
            (1.0 / (index + 1) for index, path in enumerate(returned) if path in expected),
            0.0,
        )
        recall = len(set(found)) / len(expected)
    else:
        reciprocal_rank = None
        recall = 1.0 if not returned else 0.0
    precision = len(found) / len(returned) if returned else (1.0 if not expected else 0.0)
    return {
        "returned": returned,
        "relevant": relevant,
        "hit": bool(found) if expected else not returned,
        "recallAtK": round(recall, 6),
        "precisionReturned": round(precision, 6),
        "reciprocalRank": (
            round(reciprocal_rank, 6) if reciprocal_rank is not None else None
        ),
        "falsePositiveCount": len([path for path in returned if path not in expected]),
        "exactSet": set(returned) == expected,
    }


def _verify_quality(report: Mapping[str, Any]) -> None:
    quality = _object(report.get("quality"), "quality")
    top_k = _integer(quality.get("topK"), "quality.topK")
    fixture = json.loads(
        (ROOT / "benchmarks/retrieval/fixtures/source-native-v1.json").read_text(
            encoding="utf-8"
        )
    )
    fixture_cases = {
        case["id"]: {
            "group": case["group"],
            "query": case["query"],
            "relevant": case["relevant"],
        }
        for case in fixture["qualityCases"]
    }
    for name in (
        "ripgrep-fixed",
        "vyral-lexical-any",
        "vyral-lexical-all",
        "vyral-lexical-prefix",
    ):
        variant = _variant(report, name)
        raw_cases = variant.get("cases")
        if not isinstance(raw_cases, list):
            raise ValueError(f"{name}.cases must be an array")
        cases = [_object(case, f"{name}.case") for case in raw_cases]
        if len(cases) != len(fixture_cases):
            raise ValueError(f"{name} does not contain the complete labeled query set")
        seen: set[str] = set()
        for case in cases:
            case_id = case.get("id")
            if not isinstance(case_id, str) or case_id in seen or case_id not in fixture_cases:
                raise ValueError(f"{name} contains an invalid or duplicate case id")
            seen.add(case_id)
            expected_case = fixture_cases[case_id]
            if (
                case.get("group") != expected_case["group"]
                or case.get("query") != expected_case["query"]
            ):
                raise ValueError(f"{name}.{case_id} does not match the labeled fixture")
            metrics = _object(case.get("metrics"), f"{name}.{case_id}.metrics")
            if metrics.get("relevant") != expected_case["relevant"]:
                raise ValueError(f"{name}.{case_id} changes the relevance labels")
            if dict(metrics) != _recomputed_metrics(metrics, top_k):
                raise ValueError(f"{name}.{case_id} metrics do not match its results")
        aggregate = _object(variant.get("aggregate"), f"{name}.aggregate")
        if dict(aggregate) != _recomputed_aggregate(cases):
            raise ValueError(f"{name} aggregate does not match its cases")
        by_group: dict[str, list[Mapping[str, Any]]] = {}
        for case in cases:
            group = case.get("group")
            if not isinstance(group, str) or not group:
                raise ValueError(f"{name} case has an invalid group")
            by_group.setdefault(group, []).append(case)
        groups = _object(variant.get("groups"), f"{name}.groups")
        if set(groups) != set(by_group):
            raise ValueError(f"{name} group set does not match its cases")
        for group, group_cases in by_group.items():
            if dict(_object(groups[group], f"{name}.{group}")) != _recomputed_aggregate(
                group_cases
            ):
                raise ValueError(f"{name}.{group} does not match its cases")


def _expected_criteria(report: Mapping[str, Any]) -> dict[str, bool]:
    exact = _group(report, "ripgrep-fixed", "exact-literal")
    terms_rg = _group(report, "ripgrep-fixed", "term-retrieval")
    terms_vyral = _group(report, "vyral-lexical-all", "term-retrieval")
    prefix_rg = _group(report, "ripgrep-fixed", "prefix")
    prefix_vyral = _group(report, "vyral-lexical-prefix", "prefix")
    latency = _object(
        _object(
            _object(report.get("latencyMs"), "latencyMs").get("variants"),
            "latencyMs.variants",
        ).get("ripgrep-fixed"),
        "latencyMs.variants.ripgrep-fixed",
    )
    warm_latency = _object(latency.get("warmAllCasesMs"), "ripgrep warm latency")
    freshness = _object(report.get("freshness"), "freshness")
    safety = _object(report.get("safety"), "safety")
    return {
        "sourceTreeClean": not _boolean(report.get("sourceDirty"), "sourceDirty"),
        "exactLiteralRecallIsPerfect": _number(
            exact.get("meanRecallAtK"), "exact meanRecallAtK"
        )
        == 1.0,
        "exactLiteralPrecisionIsPerfect": _number(
            exact.get("meanPrecisionReturned"), "exact meanPrecisionReturned"
        )
        == 1.0,
        "exactLiteralFirstRelevantIsPerfect": _number(
            exact.get("meanReciprocalRank"), "exact meanReciprocalRank"
        )
        == 1.0,
        "localP95AtOrBelow100Ms": _number(warm_latency.get("p95"), "ripgrep p95")
        <= 100.0,
        "editVisibleWithoutIndexRefresh": _boolean(
            freshness.get("ripgrepVisibleWithoutIndexRefresh"),
            "ripgrep freshness",
        ),
        "indexedRecordIsStaleUntilRefresh": _boolean(
            freshness.get("vyralStaleBeforeRecordRefresh"),
            "Vyral stale-before-refresh",
        ),
        "sensitiveCanaryExcluded": _boolean(
            safety.get("ripgrepSensitiveCanaryExcluded"), "ripgrep sensitive exclusion"
        )
        and _boolean(
            safety.get("vyralSensitiveCanaryExcluded"), "Vyral sensitive exclusion"
        ),
        "absoluteRootNotDisclosed": not _boolean(
            safety.get("absoluteRootDisclosed"), "absoluteRootDisclosed"
        ),
        "revisionBoundLineCitations": (
            _integer(safety.get("citationSampleCount"), "citationSampleCount") > 0
            and _boolean(safety.get("citationsRevisionBound"), "citationsRevisionBound")
        ),
        "vyralWinsReorderedTerms": _number(
            terms_vyral.get("meanRecallAtK"), "Vyral term recall"
        )
        > _number(terms_rg.get("meanRecallAtK"), "ripgrep term recall"),
        "vyralWinsPrefixQueries": _number(
            prefix_vyral.get("meanRecallAtK"), "Vyral prefix recall"
        )
        > _number(prefix_rg.get("meanRecallAtK"), "ripgrep prefix recall"),
    }


def _verify_source_commit(value: Any) -> None:
    if not isinstance(value, str) or not COMMIT_PATTERN.fullmatch(value):
        raise ValueError("sourceCommit must be a full Git commit")
    completed = subprocess.run(
        ["git", "-C", str(ROOT), "merge-base", "--is-ancestor", value, "HEAD"],
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    if completed.returncode != 0:
        raise ValueError("sourceCommit is not an ancestor of the current checkout")


def _scan_private_paths(value: Any) -> None:
    if isinstance(value, str):
        if any(prefix in value for prefix in ("/home/", "/projects/", "C:\\Users\\")):
            raise ValueError("report contains a host-specific absolute path")
    elif isinstance(value, Mapping):
        for key, child in value.items():
            _scan_private_paths(key)
            _scan_private_paths(child)
    elif isinstance(value, list):
        for child in value:
            _scan_private_paths(child)


def verify(
    report: Mapping[str, Any],
    *,
    minimum_noise: int,
    minimum_iterations: int,
    require_admission: bool,
) -> None:
    if report.get("schemaVersion") != "vyral.retrieval.ripgrep-comparison.v1":
        raise ValueError("report schemaVersion is unsupported")
    _verify_source_commit(report.get("sourceCommit"))
    generated = report.get("generatedAtUtc")
    if not isinstance(generated, str):
        raise ValueError("generatedAtUtc must be a string")
    datetime.fromisoformat(generated.replace("Z", "+00:00"))
    parameters = _object(report.get("parameters"), "parameters")
    fixture_path = parameters.get("fixture")
    if fixture_path != "benchmarks/retrieval/fixtures/source-native-v1.json":
        raise ValueError("report references an unexpected fixture")
    fixture_sha256 = parameters.get("fixtureSha256")
    expected_fixture_sha256 = hashlib.sha256(
        (ROOT / fixture_path).read_bytes()
    ).hexdigest()
    if fixture_sha256 != expected_fixture_sha256:
        raise ValueError("report fixture digest does not match the current fixture")
    if _integer(parameters.get("generatedNoiseDocuments"), "generatedNoiseDocuments") < minimum_noise:
        raise ValueError("report does not meet the minimum noise-document count")
    if _integer(parameters.get("iterations"), "iterations") < minimum_iterations:
        raise ValueError("report does not meet the minimum iteration count")
    if _integer(parameters.get("authoredDocuments"), "authoredDocuments") < 16:
        raise ValueError("report does not contain the full authored corpus")
    if _integer(parameters.get("topK"), "topK") != 5:
        raise ValueError("report topK must remain 5")
    _verify_quality(report)
    expected_criteria = _expected_criteria(report)
    admission = _object(report.get("admission"), "admission")
    criteria = _object(admission.get("criteria"), "admission.criteria")
    if dict(criteria) != expected_criteria:
        raise ValueError("admission criteria do not match the report evidence")
    expected_decision = "admit" if all(expected_criteria.values()) else "reject"
    if admission.get("decision") != expected_decision:
        raise ValueError("admission decision does not match its criteria")
    if require_admission and expected_decision != "admit":
        failed = [name for name, passed in expected_criteria.items() if not passed]
        raise ValueError("adapter admission failed: " + ", ".join(failed))
    _scan_private_paths(report)


def _arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--minimum-noise", type=int, default=2_000)
    parser.add_argument("--minimum-iterations", type=int, default=30)
    parser.add_argument("--require-admission", action="store_true")
    return parser.parse_args()


def main() -> int:
    arguments = _arguments()
    try:
        raw = json.loads(arguments.report.read_text(encoding="utf-8"))
        report = _object(raw, "report")
        verify(
            report,
            minimum_noise=arguments.minimum_noise,
            minimum_iterations=arguments.minimum_iterations,
            require_admission=arguments.require_admission,
        )
    except (OSError, json.JSONDecodeError, TypeError, ValueError) as error:
        print(f"ripgrep retrieval report invalid: {error}", file=sys.stderr)
        return 1
    print(
        "ripgrep-retrieval-report=ok "
        f"decision={report['admission']['decision']} "
        f"documents={report['parameters']['totalIndexedDocuments']} "
        f"report={arguments.report}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
