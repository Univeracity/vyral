#!/usr/bin/env python3
"""Qualify high-dimensional flat-vector arithmetic and complete synthetic retrieval."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import platform
from pathlib import Path
import random
import sqlite3
import statistics
import struct
import subprocess
import sys
from tempfile import TemporaryDirectory
from time import perf_counter

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "runtimes/python/src"))
from vyral_runtime.local import record_store  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-ref", default="bc2739023d1cf1f0b39aa1c20910b7c4de9738ba")
    parser.add_argument("--records", type=int, default=1000)
    parser.add_argument("--dimensions", type=int, default=384)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not 100 <= args.records <= 4000 or not 8 <= args.dimensions <= 4096 or not 1 <= args.repeats <= 10:
        parser.error("Bound records to 100..4000, dimensions to 8..4096 and repeats to 1..10")
    source_path = "runtimes/python/src/vyral_runtime/local/record_store.py"
    baseline_bytes = subprocess.check_output(["git", "show", f"{args.baseline_ref}:{source_path}"], cwd=ROOT)
    rng = random.Random(381)

    def vector() -> list[float]:
        return [struct.unpack("<f", struct.pack("<f", rng.uniform(-1, 1)))[0] for _ in range(args.dimensions)]

    inputs = [vector() for _ in range(4)]
    controls = 0
    with TemporaryDirectory(prefix="vyral-vector-qualification-") as directory:
        temporary = Path(directory)
        baseline_path = temporary / "reference.py"; baseline_path.write_bytes(baseline_bytes)
        spec = importlib.util.spec_from_file_location("vyral_runtime.local._vector_reference", baseline_path)
        assert spec and spec.loader
        reference = importlib.util.module_from_spec(spec); sys.modules[spec.name] = reference; spec.loader.exec_module(reference)
        for metric in ("cosine", "dotproduct", "euclidean"):
            for left, right in [(a, b) for a in inputs for b in inputs] + [
                ([], []), ([0.0], [0.0]), ([1.0], [2.0, 3.0]), ([float("nan")], [1.0]),
                ([float("inf")], [1.0]), ([3.4e38], [3.4e38]), ([1e-40], [1e-40]),
                ([-0.0], [1.0]), ([1e20, 1e20], [1e20, 1e20]),
            ]:
                outcomes = []
                for module in (reference, record_store):
                    try: outcomes.append(("bits", struct.pack("<f", module._similarity_score(metric, left, right)).hex()))
                    except Exception as exc: outcomes.append((type(exc).__name__, str(exc)))
                if outcomes[0] != outcomes[1]: raise RuntimeError(f"Numeric/error parity failed for {metric}")
                controls += 1
        for _ in range(2000):
            value = struct.unpack("<d", rng.randbytes(8))[0]
            outcomes = []
            for module in (reference, record_store):
                try: outcomes.append(("bits", struct.pack("<f", module._float32(value, "Vector score")).hex()))
                except Exception as exc: outcomes.append((type(exc).__name__, str(exc)))
            if outcomes[0] != outcomes[1]: raise RuntimeError("Scalar numeric/error parity failed")
            controls += 1

        clock = lambda: datetime(2026, 1, 1, tzinfo=timezone.utc)
        database = temporary / "records.sqlite"
        candidate = record_store.SQLiteRecordStore(database, clock=clock)
        candidate.create_collection({"name": "items", "indexedMetadata": ["/metadata/component"], "vectorPolicies": [
            {"name": metric, "path": f"/vectors/{metric}/values", "dimensions": args.dimensions, "distanceFunction": metric}
            for metric in ("cosine", "dotproduct", "euclidean")
        ]})
        input_hash = hashlib.sha256()
        for index in range(args.records):
            values = vector()
            record = {"id": f"{index:05d}", "partitionKey": f"p{index % 2}",
                      "content": {"text": "Generic source passage " * 30},
                      "metadata": {"component": str(index % 4), "item": f"item-{index // 3:05d}"},
                      "vectors": {metric: {"values": values, "distanceFunction": metric}
                                  for metric in ("cosine", "dotproduct", "euclidean")}}
            input_hash.update(json.dumps(record, sort_keys=True).encode())
            candidate.upsert_record("items", record)
        baseline = reference.SQLiteRecordStore(database, clock=clock)
        queries = [{"vector": {"field": metric, "value": inputs[0], "top": 30}, "limit": 10}
                   for metric in ("cosine", "dotproduct", "euclidean")]
        timings: dict[str, list[float]] = {"baseline": [], "candidate": []}
        responses = 0; grouped = []
        for repeat in range(args.repeats):
            for query in queries:
                outputs = []
                for name, store in (("baseline", baseline), ("candidate", candidate)):
                    start = perf_counter()
                    response = store.search_records_page("items", query).to_dict()
                    json.dumps(response, sort_keys=True)
                    timings[name].append((perf_counter() - start) * 1000)
                    outputs.append(response)
                if outputs[0] != outputs[1]: raise RuntimeError("Complete retrieval/serialization parity failed")
                responses += 1
        # Distinct-item budgets are a caller-owned intervention. Compare complete
        # responses at each candidate pool, then cap grouped output at ten items.
        budget_responses = 0
        for query in queries:
            for pool in (10, 30, 100):
                pooled_query = {**query, "limit": pool,
                                "vector": {**query["vector"], "top": pool}}
                before = baseline.search_records_page("items", pooled_query).to_dict()
                after = candidate.search_records_page("items", pooled_query).to_dict()
                if before != after:
                    raise RuntimeError("Budgeted retrieval parity failed")
                budget_responses += 1
                seen: set[str] = set()
                selected: list[str] = []
                consumed = 0
                for match in after["items"]:
                    consumed += 1
                    identity = match["record"]["metadata"]["item"]
                    if identity not in seen:
                        seen.add(identity); selected.append(identity)
                    if len(selected) == 10:
                        break
                grouped.append({"metric": query["vector"]["field"], "candidatePool": pool,
                                "returnedBeforeGrouping": len(after["items"]),
                                "passagesConsumed": consumed, "outputItemBudget": 10,
                                "distinctItemsReturned": len(selected)})
        output = {
            "schemaVersion": "vyral.python-vector-qualification.v1",
            "sourceSha256": hashlib.sha256((ROOT / source_path).read_bytes()).hexdigest(),
            "referenceSha256": hashlib.sha256(baseline_bytes).hexdigest(),
            "inputSha256": input_hash.hexdigest(), "python": platform.python_version(),
            "platform": platform.platform(), "sqlite": sqlite3.sqlite_version,
            "records": args.records, "dimensions": args.dimensions, "numericErrorControls": controls,
            "completeResponses": responses, "completeBudgetResponses": budget_responses, "completeParity": True, "timingScope": "retrieval and serialization; no encoder/model",
            "timings": {name: {"p50Ms": statistics.median(values), "samples": len(values)} for name, values in timings.items()},
            "grouping": grouped,
            "limits": ["one synthetic host fixture; no SLA or relevance claim",
                       "full eligible record validation retained; no rank-before-hydration",
                       "distinct-item grouping is caller-owned and evaluated separately"],
        }
        args.output.parent.mkdir(parents=True, exist_ok=True); args.output.write_text(json.dumps(output, indent=2) + "\n")
        print(json.dumps({key: output[key] for key in ("numericErrorControls", "completeResponses", "timings")}))
        candidate.clear_lexical_cache(); baseline.clear_lexical_cache()


if __name__ == "__main__":
    main()
