#!/usr/bin/env python3
"""Compare bounded ripgrep search with Vyral's local lexical record index."""

from __future__ import annotations

import argparse
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import random
import sqlite3
import statistics
import subprocess
import sys
import tempfile
from time import perf_counter
from typing import Any, Callable, Mapping, Sequence


ROOT = Path(__file__).resolve().parent.parent
PYTHON_RUNTIME = ROOT / "runtimes/python/src"
FIXTURE_PATH = ROOT / "benchmarks/retrieval/fixtures/source-native-v1.json"
COLLECTION = "source-native-comparison"
PARTITION = "fixture"
TOP_K = 5
FRESHNESS_CANARY = "FRESHNESS_CANARY_7D91A3"
SAFETY_CANARY = "CREDENTIAL_CANARY_4E82B1"

sys.path.insert(0, str(PYTHON_RUNTIME))

from vyral_runtime import (  # noqa: E402
    RecordCollectionPolicy,
    SQLiteRecordStore,
    VyralRecord,
)
from vyral_runtime.integrations.ripgrep import (  # noqa: E402
    RipgrepAdapterOptions,
    RipgrepSearchAdapter,
    RipgrepSearchRequest,
)


JSONObject = dict[str, Any]


def _timer(operation: Callable[[], Any]) -> tuple[Any, float]:
    started = perf_counter()
    value = operation()
    return value, (perf_counter() - started) * 1000


def _percentile(values: Sequence[float], fraction: float) -> float:
    if not values:
        raise ValueError("A percentile requires at least one measurement.")
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    weight = position - lower
    return ordered[lower] * (1 - weight) + ordered[upper] * weight


def _latency_summary(values: Sequence[float]) -> JSONObject:
    return {
        "count": len(values),
        "min": round(min(values), 3),
        "p50": round(_percentile(values, 0.50), 3),
        "p95": round(_percentile(values, 0.95), 3),
        "max": round(max(values), 3),
        "mean": round(statistics.fmean(values), 3),
    }


def _load_fixture() -> JSONObject:
    value = json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))
    if value.get("schemaVersion") != "vyral.retrieval.source-native-fixture.v1":
        raise RuntimeError("The source-native fixture has an unsupported schema version.")
    if not isinstance(value.get("documents"), list) or not isinstance(
        value.get("qualityCases"), list
    ):
        raise RuntimeError("The source-native fixture is incomplete.")
    return value


def _write_corpus(root: Path, fixture: Mapping[str, Any], noise_count: int) -> list[str]:
    paths: list[str] = []
    for document in fixture["documents"]:
        relative = str(document["path"])
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(str(document["content"]), encoding="utf-8")
        paths.append(relative)

    randomizer = random.Random(20260811)
    vocabulary = (
        "adapter", "artifact", "boundary", "checkpoint", "collection", "contract",
        "dispatch", "evidence", "fixture", "gateway", "manifest", "provider",
        "qualification", "record", "revision", "routing", "runtime", "snapshot",
        "storage", "workflow",
    )
    for index in range(noise_count):
        words = randomizer.sample(vocabulary, 8)
        relative = f"generated/noise-{index:05d}.md"
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(
            "# Generated fixture\n\n"
            f"Noise document {index:05d}: {' '.join(words)}. "
            f"Unique marker NOISE_{index:05d}.\n",
            encoding="utf-8",
        )
        paths.append(relative)

    (root / ".env").write_text(f"TOKEN={SAFETY_CANARY}\n", encoding="utf-8")
    (root / "secrets.md").write_text(
        f"This sensitive fixture contains {SAFETY_CANARY}.\n",
        encoding="utf-8",
    )
    return paths


def _record(relative: str, corpus_root: Path) -> VyralRecord:
    text = (corpus_root / relative).read_text(encoding="utf-8")
    return VyralRecord(
        id="source-" + hashlib.sha256(relative.encode("utf-8")).hexdigest()[:24],
        partition_key=PARTITION,
        type="source.document",
        metadata={"path": relative, "extension": Path(relative).suffix},
        content={"text": text},
        sources=(
            {
                "id": relative,
                "kind": "source-file",
                "uri": f"vyral-source://fixture/{relative}",
                "label": relative,
                "span": None,
            },
        ),
    )


def _quality_metrics(returned: Sequence[str], relevant: Sequence[str], k: int) -> JSONObject:
    selected = list(dict.fromkeys(returned))[:k]
    expected = set(relevant)
    found = [path for path in selected if path in expected]
    if expected:
        reciprocal_rank = next(
            (1.0 / (index + 1) for index, path in enumerate(selected) if path in expected),
            0.0,
        )
        recall = len(set(found)) / len(expected)
    else:
        reciprocal_rank = None
        recall = 1.0 if not selected else 0.0
    precision = len(found) / len(selected) if selected else (1.0 if not expected else 0.0)
    return {
        "returned": selected,
        "relevant": list(relevant),
        "hit": bool(found) if expected else not selected,
        "recallAtK": round(recall, 6),
        "precisionReturned": round(precision, 6),
        "reciprocalRank": (
            round(reciprocal_rank, 6) if reciprocal_rank is not None else None
        ),
        "falsePositiveCount": len([path for path in selected if path not in expected]),
        "exactSet": set(selected) == expected,
    }


def _aggregate_quality(cases: Sequence[Mapping[str, Any]]) -> JSONObject:
    positive = [case for case in cases if case["metrics"]["relevant"]]
    return {
        "caseCount": len(cases),
        "positiveCaseCount": len(positive),
        "meanRecallAtK": round(
            statistics.fmean(case["metrics"]["recallAtK"] for case in cases), 6
        ),
        "meanPrecisionReturned": round(
            statistics.fmean(
                case["metrics"]["precisionReturned"] for case in cases
            ),
            6,
        ),
        "meanReciprocalRank": (
            round(
                statistics.fmean(
                    case["metrics"]["reciprocalRank"] for case in positive
                ),
                6,
            )
            if positive
            else None
        ),
        "exactSetRate": round(
            statistics.fmean(1.0 if case["metrics"]["exactSet"] else 0.0 for case in cases),
            6,
        ),
    }


def _ripgrep_paths(adapter: RipgrepSearchAdapter, query: str) -> list[str]:
    result = adapter.search(RipgrepSearchRequest(query=query, limit=TOP_K))
    return list(dict.fromkeys(match.relative_path for match in result.matches))


def _vyral_paths(
    store: SQLiteRecordStore,
    query: str,
    *,
    match_mode: str,
    prefix_matching: bool,
) -> list[str]:
    matches = store.search_records(
        COLLECTION,
        {
            "lexical": {
                "query": query,
                "fields": ["/content/text", "/metadata/path"],
                "top": TOP_K,
                "matchMode": match_mode,
                "prefixMatching": prefix_matching,
                "prefixMinChars": 3,
            },
            "limit": TOP_K,
        },
    )
    paths: list[str] = []
    for match in matches:
        path = match.record.metadata.get("path")
        if not isinstance(path, str):
            raise RuntimeError("A benchmark record is missing its source path.")
        paths.append(path)
    return paths


def _variant_operations(
    adapter: RipgrepSearchAdapter,
    store: SQLiteRecordStore,
) -> dict[str, Callable[[str], list[str]]]:
    return {
        "ripgrep-fixed": lambda query: _ripgrep_paths(adapter, query),
        "vyral-lexical-any": lambda query: _vyral_paths(
            store, query, match_mode="any", prefix_matching=False
        ),
        "vyral-lexical-all": lambda query: _vyral_paths(
            store, query, match_mode="all", prefix_matching=False
        ),
        "vyral-lexical-prefix": lambda query: _vyral_paths(
            store, query, match_mode="all", prefix_matching=True
        ),
    }


def _run_quality(
    fixture: Mapping[str, Any],
    operations: Mapping[str, Callable[[str], list[str]]],
) -> JSONObject:
    results: dict[str, list[JSONObject]] = {name: [] for name in operations}
    grouped: dict[str, dict[str, list[JSONObject]]] = {
        name: defaultdict(list) for name in operations
    }
    for raw_case in fixture["qualityCases"]:
        case_id = str(raw_case["id"])
        group = str(raw_case["group"])
        query = str(raw_case["query"])
        relevant = [str(value) for value in raw_case["relevant"]]
        for name, operation in operations.items():
            returned = operation(query)
            result = {
                "id": case_id,
                "group": group,
                "query": query,
                "metrics": _quality_metrics(returned, relevant, TOP_K),
            }
            results[name].append(result)
            grouped[name][group].append(result)
    return {
        "topK": TOP_K,
        "variants": {
            name: {
                "aggregate": _aggregate_quality(cases),
                "groups": {
                    group: _aggregate_quality(group_cases)
                    for group, group_cases in sorted(grouped[name].items())
                },
                "cases": cases,
            }
            for name, cases in results.items()
        },
    }


def _run_latency(
    fixture: Mapping[str, Any],
    operations: Mapping[str, Callable[[str], list[str]]],
    iterations: int,
) -> JSONObject:
    queries = [str(case["query"]) for case in fixture["qualityCases"]]
    positive_queries = [
        str(case["query"]) for case in fixture["qualityCases"] if case["relevant"]
    ]
    summaries: dict[str, JSONObject] = {}
    for name, operation in operations.items():
        cold_query = positive_queries[0]
        _, cold_ms = _timer(lambda: operation(cold_query))
        values: list[float] = []
        for _ in range(iterations):
            for query in queries:
                _, duration = _timer(lambda query=query: operation(query))
                values.append(duration)
        summaries[name] = {
            "coldFirstQueryMs": round(cold_ms, 3),
            "warmAllCasesMs": _latency_summary(values),
        }
    return {
        "iterations": iterations,
        "queriesPerIteration": len(queries),
        "variants": summaries,
    }


def _run_freshness(
    corpus_root: Path,
    adapter: RipgrepSearchAdapter,
    store: SQLiteRecordStore,
) -> JSONObject:
    relative = "live/freshness.md"
    path = corpus_root / relative
    before_ripgrep = _ripgrep_paths(adapter, FRESHNESS_CANARY)
    before_vyral = _vyral_paths(
        store, FRESHNESS_CANARY, match_mode="all", prefix_matching=False
    )
    path.write_text(
        path.read_text(encoding="utf-8")
        + f"\nThe committed source now contains {FRESHNESS_CANARY}.\n",
        encoding="utf-8",
    )
    rg_after, rg_after_ms = _timer(lambda: _ripgrep_paths(adapter, FRESHNESS_CANARY))
    stale_vyral, stale_vyral_ms = _timer(
        lambda: _vyral_paths(
            store, FRESHNESS_CANARY, match_mode="all", prefix_matching=False
        )
    )
    _, refresh_ms = _timer(lambda: store.upsert_record(COLLECTION, _record(relative, corpus_root)))
    refreshed_vyral, refreshed_vyral_ms = _timer(
        lambda: _vyral_paths(
            store, FRESHNESS_CANARY, match_mode="all", prefix_matching=False
        )
    )
    return {
        "path": relative,
        "absentBeforeEdit": not before_ripgrep and not before_vyral,
        "ripgrepVisibleWithoutIndexRefresh": relative in rg_after,
        "ripgrepWriteToResultMs": round(rg_after_ms, 3),
        "vyralStaleBeforeRecordRefresh": relative not in stale_vyral,
        "vyralStaleQueryMs": round(stale_vyral_ms, 3),
        "vyralRecordRefreshMs": round(refresh_ms, 3),
        "vyralVisibleAfterRecordRefresh": relative in refreshed_vyral,
        "vyralPostRefreshQueryMs": round(refreshed_vyral_ms, 3),
    }


def _run_safety(adapter: RipgrepSearchAdapter, store: SQLiteRecordStore) -> JSONObject:
    result = adapter.search(RipgrepSearchRequest(SAFETY_CANARY, limit=TOP_K))
    citation_sample = adapter.search(
        RipgrepSearchRequest("ADMISSION_RECEIPT_V1", limit=TOP_K)
    )
    vyral_paths = _vyral_paths(
        store, SAFETY_CANARY, match_mode="all", prefix_matching=False
    )
    serialized = json.dumps(result.to_dict(), sort_keys=True)
    return {
        "ripgrepReturnedPaths": [match.relative_path for match in result.matches],
        "ripgrepFilteredSensitivePaths": result.filtered_sensitive_paths,
        "ripgrepSensitiveCanaryExcluded": not result.matches,
        "vyralSensitiveCanaryExcluded": not vyral_paths,
        "absoluteRootDisclosed": str(adapter.root_path) in serialized,
        "citationsRevisionBound": all(
            match.source_uri.startswith("vyral-source://ripgrep/")
            and "#L" in match.source_uri
            and match.source_revision.startswith("sha256:")
            and match.line_number > 0
            for match in citation_sample.matches
        ),
        "citationSampleCount": len(citation_sample.matches),
    }


def _admission(report: Mapping[str, Any]) -> JSONObject:
    variants = report["quality"]["variants"]
    exact = variants["ripgrep-fixed"]["groups"]["exact-literal"]
    terms_rg = variants["ripgrep-fixed"]["groups"]["term-retrieval"]
    terms_vyral = variants["vyral-lexical-all"]["groups"]["term-retrieval"]
    prefix_rg = variants["ripgrep-fixed"]["groups"]["prefix"]
    prefix_vyral = variants["vyral-lexical-prefix"]["groups"]["prefix"]
    latency = report["latencyMs"]["variants"]["ripgrep-fixed"]["warmAllCasesMs"]
    freshness = report["freshness"]
    safety = report["safety"]
    criteria = {
        "sourceTreeClean": not report["sourceDirty"],
        "exactLiteralRecallIsPerfect": exact["meanRecallAtK"] == 1.0,
        "exactLiteralPrecisionIsPerfect": exact["meanPrecisionReturned"] == 1.0,
        "exactLiteralFirstRelevantIsPerfect": exact["meanReciprocalRank"] == 1.0,
        "localP95AtOrBelow100Ms": latency["p95"] <= 100.0,
        "editVisibleWithoutIndexRefresh": freshness["ripgrepVisibleWithoutIndexRefresh"],
        "indexedRecordIsStaleUntilRefresh": freshness["vyralStaleBeforeRecordRefresh"],
        "sensitiveCanaryExcluded": (
            safety["ripgrepSensitiveCanaryExcluded"]
            and safety["vyralSensitiveCanaryExcluded"]
        ),
        "absoluteRootNotDisclosed": not safety["absoluteRootDisclosed"],
        "revisionBoundLineCitations": (
            safety["citationSampleCount"] > 0 and safety["citationsRevisionBound"]
        ),
        "vyralWinsReorderedTerms": (
            terms_vyral["meanRecallAtK"] > terms_rg["meanRecallAtK"]
        ),
        "vyralWinsPrefixQueries": (
            prefix_vyral["meanRecallAtK"] > prefix_rg["meanRecallAtK"]
        ),
    }
    admitted = all(criteria.values())
    return {
        "decision": "admit" if admitted else "reject",
        "criteria": criteria,
        "identifiedUseCase": (
            "Bounded, read-only, zero-index lookup of exact literals in an authorized "
            "local code or Markdown tree, with revision-bound line citations and "
            "immediate visibility of source edits."
        ),
        "userPath": (
            "Construct RipgrepSearchAdapter once with an application-owned root and "
            "static globs, then call search with a fixed-string query and bounded limit."
        ),
        "notFor": [
            "semantic or paraphrase retrieval",
            "governed record filtering or tenant authorization",
            "remote or non-text corpora",
            "regex or caller-selected filesystem traversal",
        ],
    }


def _git_commit() -> str:
    completed = subprocess.run(
        ["git", "-C", str(ROOT), "rev-parse", "HEAD"],
        check=True,
        stdout=subprocess.PIPE,
        text=True,
    )
    return completed.stdout.strip()


def _git_dirty() -> bool:
    completed = subprocess.run(
        [
            "git",
            "-C",
            str(ROOT),
            "status",
            "--porcelain",
            "--untracked-files=all",
        ],
        check=True,
        stdout=subprocess.PIPE,
        text=True,
    )
    return bool(completed.stdout.strip())


def _environment(adapter: RipgrepSearchAdapter) -> JSONObject:
    return {
        "platform": platform.system().lower(),
        "architecture": platform.machine().lower(),
        "logicalCpuCount": os.cpu_count(),
        "pythonVersion": platform.python_version(),
        "sqliteVersion": sqlite3.sqlite_version,
        "ripgrepVersion": adapter.executable_version,
    }


def run_benchmark(noise_documents: int, iterations: int) -> JSONObject:
    fixture = _load_fixture()
    with tempfile.TemporaryDirectory(prefix="vyral-ripgrep-comparison-") as temporary:
        root = Path(temporary)
        corpus_root = root / "corpus"
        corpus_root.mkdir()
        paths, corpus_ms = _timer(lambda: _write_corpus(corpus_root, fixture, noise_documents))

        adapter, adapter_ms = _timer(
            lambda: RipgrepSearchAdapter(
                corpus_root,
                RipgrepAdapterOptions(
                    include_globs=("*.py", "*.md", ".env"),
                    max_results=TOP_K,
                    timeout_seconds=10.0,
                ),
            )
        )
        store, store_ms = _timer(lambda: SQLiteRecordStore(root / "vyral.sqlite"))
        store.create_collection(
            RecordCollectionPolicy(
                name=COLLECTION,
                indexed_metadata=("/metadata/path", "/metadata/extension"),
            )
        )
        ingest_result, ingest_ms = _timer(
            lambda: store.upsert_records(
                COLLECTION,
                [_record(relative, corpus_root) for relative in paths],
            )
        )
        if ingest_result.failed != 0 or ingest_result.succeeded != len(paths):
            raise RuntimeError("The Vyral comparison corpus did not ingest completely.")
        operations = _variant_operations(adapter, store)
        report: JSONObject = {
            "schemaVersion": "vyral.retrieval.ripgrep-comparison.v1",
            "sourceCommit": _git_commit(),
            "sourceDirty": _git_dirty(),
            "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "environment": _environment(adapter),
            "parameters": {
                "fixture": FIXTURE_PATH.relative_to(ROOT).as_posix(),
                "fixtureSha256": hashlib.sha256(FIXTURE_PATH.read_bytes()).hexdigest(),
                "authoredDocuments": len(fixture["documents"]),
                "generatedNoiseDocuments": noise_documents,
                "totalIndexedDocuments": len(paths),
                "iterations": iterations,
                "topK": TOP_K,
                "randomSeed": 20260811,
            },
            "setupMs": {
                "writeCorpus": round(corpus_ms, 3),
                "ripgrepAdapterInitialize": round(adapter_ms, 3),
                "vyralStoreInitialize": round(store_ms, 3),
                "vyralRecordIngest": round(ingest_ms, 3),
            },
            "storageBytes": {
                "sourceCorpus": sum((corpus_root / relative).stat().st_size for relative in paths),
                "vyralDatabase": store.diagnostics().database_bytes,
            },
            "quality": _run_quality(fixture, operations),
            "latencyMs": _run_latency(fixture, operations, iterations),
            "freshness": _run_freshness(corpus_root, adapter, store),
            "safety": _run_safety(adapter, store),
        }
        report["admission"] = _admission(report)
        return report


def _arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--noise-documents", type=int, default=2_000)
    parser.add_argument("--iterations", type=int, default=30)
    parser.add_argument("--require-admission", action="store_true")
    arguments = parser.parse_args()
    if arguments.noise_documents < 0:
        parser.error("--noise-documents must be non-negative")
    if arguments.iterations <= 0:
        parser.error("--iterations must be greater than zero")
    return arguments


def main() -> int:
    arguments = _arguments()
    report = run_benchmark(arguments.noise_documents, arguments.iterations)
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(
        json.dumps(report, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    admission = report["admission"]
    quality = report["quality"]["variants"]
    print(
        "ripgrep-retrieval-comparison="
        f"{admission['decision']} "
        f"documents={report['parameters']['totalIndexedDocuments']} "
        f"rg-exact-recall={quality['ripgrep-fixed']['groups']['exact-literal']['meanRecallAtK']} "
        f"vyral-term-recall={quality['vyral-lexical-all']['groups']['term-retrieval']['meanRecallAtK']} "
        f"report={arguments.output}"
    )
    if arguments.require_admission and admission["decision"] != "admit":
        failed = [name for name, passed in admission["criteria"].items() if not passed]
        print("ripgrep admission criteria failed: " + ", ".join(failed), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
