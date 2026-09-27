#!/usr/bin/env python3
"""Bounded local retrieval preparation qualification; no model/service required."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import platform
import random
import sqlite3
import statistics
import struct
import sys
from tempfile import TemporaryDirectory
from time import perf_counter

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "runtimes/python/src"
sys.path.insert(0, str(SOURCE))

from vyral_runtime import RUNTIME_VERSION, SQLiteRecordStore, VyralRecord  # noqa: E402
from vyral_runtime.local.lexical import (  # noqa: E402
    FTS_CANDIDATE_ANALYZER_ID, LEXICAL_ANALYZER_ID,
)
from vyral_runtime.local.record_store import _PreparedSimilarity, _similarity_score  # noqa: E402


def summary(values: list[float]) -> dict[str, float]:
    ordered = sorted(values)
    return {"p50Ms": statistics.median(ordered),
            "p95Ms": ordered[min(len(ordered) - 1, int(len(ordered) * 0.95))]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--records", type=int, default=200)
    parser.add_argument("--dimensions", type=int, default=384)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    if not 100 <= arguments.records <= 2000:
        parser.error("--records must be between 100 and 2000")
    if not 8 <= arguments.dimensions <= 4096:
        parser.error("--dimensions must be between 8 and 4096")
    if not 1 <= arguments.repeats <= 10:
        parser.error("--repeats must be between 1 and 10")
    clock = lambda: datetime(2026, 1, 1, tzinfo=timezone.utc)
    documents = [VyralRecord(
        id=f"record-{index:04d}", partition_key="fixture",
        content={"text": ("browser network diagnostics latency " * 20) + f"category{index % 8}"},
        metadata={"title": f"network category{index % 8}"},
    ) for index in range(arguments.records)]
    requests = [{"lexical": {
        "query": f"browser network category{index}",
        "fields": ["/content/text", "/metadata/title"],
        "scanLimit": 100, "top": 10,
    }} for index in range(8)]
    with TemporaryDirectory(prefix="vyral-retrieval-qualification-") as temporary:
        path = Path(temporary) / "records.sqlite"
        baseline = SQLiteRecordStore(path, clock=clock, lexical_cache_max_entries=0)
        baseline.create_collection({"name": "items"})
        for record in documents:
            baseline.upsert_record("items", record)
        cached = SQLiteRecordStore(path, clock=clock)
        expected = [baseline.search_records_page("items", request).to_dict() for request in requests]
        cold = [cached.search_records_page("items", request).to_dict() for request in requests]
        if expected != cold:
            raise RuntimeError("Cold cache changed records, scores, diagnostics or continuation")
        timings: dict[str, list[float]] = {"uncached": [], "warmCached": []}
        for _ in range(arguments.repeats):
            for name, store in (("uncached", baseline), ("warmCached", cached)):
                for index, request in enumerate(requests):
                    start = perf_counter()
                    result = store.search_records_page("items", request).to_dict()
                    timings[name].append((perf_counter() - start) * 1000)
                    if result != expected[index]:
                        raise RuntimeError("Warm cache changed records, scores, diagnostics or continuation")
        cache_receipt = {"entries": len(cached._lexical_cache._entries),
                         "estimatedRetainedBytes": cached._lexical_cache._bytes,
                         "maxEntries": cached._lexical_cache.max_entries,
                         "maxEstimatedBytes": cached._lexical_cache.max_bytes}
        cached.clear_lexical_cache()
    randomizer = random.Random(381)

    def vector() -> tuple[float, ...]:
        return tuple(struct.unpack("<f", struct.pack("<f", randomizer.uniform(-1, 1)))[0]
                     for _ in range(arguments.dimensions))

    query = vector()
    vectors = [vector() for _ in range(arguments.records)]
    vector_timings: dict[str, dict[str, float]] = {}
    exact_checks = 0
    for metric in ("cosine", "dotproduct", "euclidean"):
        durations: dict[str, list[float]] = {"perCandidatePreparation": [], "perQueryPreparation": []}
        for _ in range(arguments.repeats):
            start = perf_counter()
            reference = [_similarity_score(metric, query, stored) for stored in vectors]
            durations["perCandidatePreparation"].append((perf_counter() - start) * 1000)
            prepared = _PreparedSimilarity(metric, query)
            start = perf_counter()
            actual = [prepared.score(stored) for stored in vectors]
            durations["perQueryPreparation"].append((perf_counter() - start) * 1000)
            if any(struct.pack("<f", left) != struct.pack("<f", right)
                   for left, right in zip(reference, actual)):
                raise RuntimeError("Query preparation changed float32 score bits")
            exact_checks += len(vectors)
        vector_timings[metric] = {f"{name}P50Ms": statistics.median(values)
                                  for name, values in durations.items()}
    receipt = {
        "schemaVersion": "vyral.python-retrieval-preparation.v1",
        "runtimeVersion": RUNTIME_VERSION,
        "environment": {"python": platform.python_version(), "platform": platform.platform(),
                        "sqlite": sqlite3.sqlite_version},
        "sourceSha256": {str(path.relative_to(SOURCE)): hashlib.sha256(path.read_bytes()).hexdigest()
                          for path in sorted((SOURCE / "vyral_runtime").rglob("*.py"))},
        "inputSha256": hashlib.sha256(json.dumps([record.to_dict() for record in documents],
                                                sort_keys=True).encode()).hexdigest(),
        "parameters": vars(arguments) | {"output": str(arguments.output) if arguments.output else None},
        "analyzers": {"candidate": FTS_CANDIDATE_ANALYZER_ID, "scorer": LEXICAL_ANALYZER_ID},
        "lexical": {"exactResultParity": True, "queries": len(requests), "candidateLimit": 100,
                    "timings": {name: summary(values) for name, values in timings.items()},
                    "cache": cache_receipt},
        "vectorKernel": {"exactFloat32Checks": exact_checks, "timings": vector_timings},
        "qualification": "Synthetic single-host preparation probe; no relevance/model-quality or full-runtime promotion claim. Vector timings exclude persistence/hydration.",
    }
    encoded = json.dumps(receipt, indent=2, sort_keys=True) + "\n"
    if arguments.output:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(encoded)
    print(json.dumps({key: value for key, value in receipt.items() if key != "sourceSha256"}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
