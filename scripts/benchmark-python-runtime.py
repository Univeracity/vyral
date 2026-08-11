#!/usr/bin/env python3
"""Run the bounded baseline Python-runtime performance smoke."""

from __future__ import annotations

import argparse
import asyncio
from datetime import datetime, timezone
import json
from pathlib import Path
import platform
import sqlite3
import sys
import tempfile
from time import monotonic
import tracemalloc


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "runtimes/python/src"))

from vyral_runtime import (  # noqa: E402
    BUILTIN_JOB_PLUGIN_ID,
    EmbeddingOptions,
    ExecutionRunRequest,
    RagIngestTextRequest,
    RagIngestionOptions,
    RecordCollectionPolicy,
    RetrievalRequest,
    RuntimeJobHandlerIds,
    VectorFieldPolicy,
    VyralRecord,
    VyralRuntime,
    VyralVector,
)


def _elapsed(started: float) -> float:
    return round(monotonic() - started, 6)


async def _execution_smoke(
    runtime: VyralRuntime, job_count: int
) -> float:
    started = monotonic()
    runs = [
        await runtime.execution.start_run(
            ExecutionRunRequest(
                RuntimeJobHandlerIds.EMBEDDINGS,
                plugin_id=BUILTIN_JOB_PLUGIN_ID,
                payload={
                    "request": {
                        "texts": [f"durable job {index}"],
                        "purpose": "symmetric",
                    }
                },
            )
        )
        for index in range(job_count)
    ]
    dispatched = 0
    for _ in range(job_count + 1):
        current = await runtime.execution.dispatch_ready_runs()
        dispatched += current
        if current == 0:
            break
    if dispatched != job_count:
        raise RuntimeError(
            f"Expected {job_count} durable jobs, dispatched {dispatched}."
        )
    completed = [
        await runtime.execution.get_run(run.id) for run in runs
    ]
    if any(run is None or run.status != "succeeded" for run in completed):
        raise RuntimeError("A durable benchmark job did not succeed.")
    return _elapsed(started)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--records", type=int, default=2_000)
    parser.add_argument("--dimensions", type=int, default=384)
    parser.add_argument("--jobs", type=int, default=20)
    parser.add_argument(
        "--max-seconds",
        type=float,
        help=(
            "Optional wall-clock guard for a controlled runner. The benchmark "
            "does not impose a cross-runner performance SLA by default."
        ),
    )
    parser.add_argument(
        "--output",
        type=Path,
        help="Optional JSON evidence output path.",
    )
    arguments = parser.parse_args()
    if not 100 <= arguments.records <= 20_000:
        parser.error("--records must be between 100 and 20000")
    if not 8 <= arguments.dimensions <= 4_096:
        parser.error("--dimensions must be between 8 and 4096")
    if not 1 <= arguments.jobs <= 100:
        parser.error("--jobs must be between 1 and 100")
    if arguments.max_seconds is not None and arguments.max_seconds <= 0:
        parser.error("--max-seconds must be positive")

    tracemalloc.start()
    overall = monotonic()
    with tempfile.TemporaryDirectory(
        prefix="vyral-python-benchmark-"
    ) as temporary:
        with VyralRuntime(
            {
                "rootPath": temporary,
                "embedding": {
                    "dimensions": arguments.dimensions,
                },
            }
        ) as runtime:
            collection = "benchmark"
            runtime.records.create_collection(
                RecordCollectionPolicy(
                    name=collection,
                    indexed_metadata=("/metadata/topic",),
                    vector_policies=(
                        VectorFieldPolicy(
                            name="contentEmbedding",
                            path=(
                                "/vectors/contentEmbedding/values"
                            ),
                            dimensions=arguments.dimensions,
                        ),
                    ),
                )
            )
            provider = runtime.embeddings.provider
            retrieval = runtime.retrieval

            started = monotonic()
            for index in range(arguments.records):
                text = (
                    "Vyral durable Python runtime stateless MCP routing "
                    f"record {index} topic {index % 17}"
                )
                runtime.records.upsert_record(
                    collection,
                    VyralRecord(
                        id=f"record-{index:06d}",
                        partition_key=f"tenant-{index % 8}",
                        type="benchmark.document",
                        metadata={"topic": f"topic-{index % 17}"},
                        content={"text": text},
                        vectors={
                            "contentEmbedding": VyralVector(
                                values=provider.generate_embedding(text),
                                model=provider.model_id,
                                source_field="/content/text",
                            )
                        },
                    ),
                )
            ingest_seconds = _elapsed(started)

            started = monotonic()
            lexical = retrieval.search(
                RetrievalRequest.from_value(
                    {
                        "query": "durable Python runtime routing",
                        "collections": [collection],
                        "searchMode": "lexical",
                        "lexical": {"fields": ["/content/text"]},
                        "limit": 10,
                    }
                )
            )
            lexical_seconds = _elapsed(started)
            started = monotonic()
            hybrid = retrieval.search(
                RetrievalRequest.from_value(
                    {
                        "query": "stateless MCP routing",
                        "collections": [collection],
                        "searchMode": "hybrid",
                        "embedding": {
                            "field": "contentEmbedding",
                            "purpose": "query",
                        },
                        "lexical": {"fields": ["/content/text"]},
                        "limit": 10,
                    }
                )
            )
            hybrid_seconds = _elapsed(started)
            if len(lexical.results) != 10 or len(hybrid.results) != 10:
                raise RuntimeError("Retrieval smoke returned too few results.")

            rag_collection = "rag-benchmark"
            runtime.records.create_collection(
                RecordCollectionPolicy(
                    name=rag_collection,
                    indexed_metadata=(
                        "/metadata/documentId",
                        "/metadata/textHash",
                        "/metadata/embeddingTextHash",
                    ),
                    vector_policies=(
                        VectorFieldPolicy(
                            name="contentEmbedding",
                            path=(
                                "/vectors/contentEmbedding/values"
                            ),
                            dimensions=provider.dimensions,
                        ),
                    ),
                )
            )
            started = monotonic()
            rag = runtime.rag_ingestion.ingest_text(
                rag_collection,
                RagIngestTextRequest(
                    document_id="benchmark-guide",
                    partition_key="tenant-a",
                    text=(
                        "Python runtime durable retrieval and MCP gateway "
                        "routing. "
                    )
                    * 2_000,
                    embedding=EmbeddingOptions(
                        field="contentEmbedding",
                        purpose="passage",
                    ),
                    options=RagIngestionOptions(
                        chunk_chars=1_000,
                        chunk_overlap_chars=100,
                    ),
                ),
            )
            rag_seconds = _elapsed(started)
            if rag.chunk_count < 50:
                raise RuntimeError("RAG smoke did not create enough chunks.")

            execution_seconds = asyncio.run(
                _execution_smoke(runtime, arguments.jobs)
            )
            diagnostics = runtime.records.diagnostics()
            total_seconds = _elapsed(overall)
            _, peak_bytes = tracemalloc.get_traced_memory()
            evidence = {
                "schemaVersion": 1,
                "generatedAtUtc": datetime.now(timezone.utc)
                .isoformat()
                .replace("+00:00", "Z"),
                "runtime": "python",
                "pythonVersion": platform.python_version(),
                "platform": platform.platform(),
                "sqliteVersion": sqlite3.sqlite_version,
                "fts5Available": diagnostics.fts5_available,
                "parameters": {
                    "records": arguments.records,
                    "dimensions": arguments.dimensions,
                    "durableJobs": arguments.jobs,
                },
                "durationsSeconds": {
                    "recordIngest": ingest_seconds,
                    "lexicalSearch": lexical_seconds,
                    "hybridExactVectorSearch": hybrid_seconds,
                    "ragIngest": rag_seconds,
                    "durableJobs": execution_seconds,
                    "total": total_seconds,
                },
                "ragChunkCount": rag.chunk_count,
                "peakTracedBytes": peak_bytes,
                "limits": {
                    "qualificationTimeoutSeconds": arguments.max_seconds,
                    "performanceSla": False,
                },
            }
            if arguments.output is not None:
                output = arguments.output.resolve()
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_text(
                    json.dumps(evidence, indent=2, sort_keys=True) + "\n",
                    encoding="utf-8",
                )
            print(json.dumps(evidence, sort_keys=True))
            if (
                arguments.max_seconds is not None
                and total_seconds > arguments.max_seconds
            ):
                raise RuntimeError(
                    "Python runtime performance smoke exceeded the explicit "
                    f"{arguments.max_seconds:g}-second runner guard: "
                    f"{total_seconds:g}."
                )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
