from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
import tempfile


sys.path.insert(
    0,
    str(Path(__file__).resolve().parents[2] / "runtimes/python/src"),
)

from vyral_runtime import VyralRuntime  # noqa: E402
from vyral_runtime.integrations.ripgrep import (  # noqa: E402
    RipgrepAdapterOptions,
    RipgrepSearchAdapter,
    RipgrepSearchRequest,
)


_COLLECTION = "retrieval-migration"
_PARTITION = "walkthrough"
_EXACT_QUERY = "accepted work survives restart"
_REORDERED_QUERY = "restart accepted work"
_DOCUMENTS = {
    "execution.md": (
        "A durable receipt proves that accepted work survives restart before "
        "a worker dispatches the handler."
    ),
    "retrieval.md": (
        "Lexical retrieval supports governed records, filters, stable "
        "snapshots, and term-order tolerance."
    ),
    "operations.md": (
        "Source-native fixed-string search sees authorized text edits without "
        "waiting for an index refresh."
    ),
}


def _walkthrough() -> dict[str, object]:
    with tempfile.TemporaryDirectory(
        prefix="vyral-retrieval-migration-"
    ) as temporary:
        root = Path(temporary)
        source_root = root / "knowledge"
        source_root.mkdir()
        for name, content in _DOCUMENTS.items():
            (source_root / name).write_text(content + "\n", encoding="utf-8")

        source = RipgrepSearchAdapter(
            source_root,
            RipgrepAdapterOptions(include_globs=("*.md",)),
        )
        exact = source.search(
            RipgrepSearchRequest(_EXACT_QUERY, limit=5)
        )
        reordered = source.search(
            RipgrepSearchRequest(_REORDERED_QUERY, limit=5)
        )

        with VyralRuntime.open_local(root / "indexed") as runtime:
            runtime.records.create_collection({"name": _COLLECTION})
            for name, content in _DOCUMENTS.items():
                runtime.records.upsert_record(
                    _COLLECTION,
                    {
                        "id": name,
                        "partitionKey": _PARTITION,
                        "type": "source.document",
                        "metadata": {"path": name},
                        "content": {"text": content},
                        "sources": [
                            {
                                "id": name,
                                "kind": "walkthrough",
                                "uri": f"vyral-example://retrieval/{name}",
                                "label": name,
                            }
                        ],
                    },
                )
            indexed = runtime.retrieval.search(
                {
                    "query": _REORDERED_QUERY,
                    "collections": [_COLLECTION],
                    "partitionKeys": [_PARTITION],
                    "searchMode": "lexical",
                    "lexical": {
                        "fields": ["/content/text"],
                        "matchMode": "all",
                    },
                    "limit": 5,
                    "includeTrace": True,
                }
            )

        return {
            "schemaVersion": "vyral.retrieval-migration-example.v1",
            "sourceNative": {
                "mode": "fixed-string",
                "query": _EXACT_QUERY,
                "matches": [
                    {
                        "sourceUri": match.source_uri,
                        "sourceRevision": match.source_revision,
                    }
                    for match in exact.matches
                ],
                "reorderedQuery": _REORDERED_QUERY,
                "reorderedMatchCount": len(reordered.matches),
                "indexRequired": False,
            },
            "indexed": {
                "mode": "lexical-all",
                "query": _REORDERED_QUERY,
                "results": [
                    {
                        "recordId": match.record.id,
                        "score": match.score,
                        "sourceUri": (
                            match.record.sources[0].get("uri")
                            if match.record.sources
                            else None
                        ),
                    }
                    for match in indexed.results
                ],
                "embeddingUsed": False,
                "governedPartition": _PARTITION,
            },
            "decision": (
                "Keep source-native search while exactness and immediate "
                "freshness are sufficient; copy intentionally selected "
                "documents into Vyral when governed records, stable snapshots, "
                "filters, or term-order tolerance justify an index."
            ),
        }


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Run the source-native to indexed-lexical migration walkthrough."
        )
    )
    parser.add_argument("--json", action="store_true")
    arguments = parser.parse_args()
    result = _walkthrough()
    if arguments.json:
        print(json.dumps(result, indent=2, sort_keys=True))
        return
    source = result["sourceNative"]
    indexed = result["indexed"]
    assert isinstance(source, dict)
    assert isinstance(indexed, dict)
    print(
        "source-native: "
        f"exact={len(source['matches'])} "
        f"reordered={source['reorderedMatchCount']} index=none"
    )
    print(
        "indexed lexical: "
        f"reordered={len(indexed['results'])} embeddings=unused"
    )
    print(result["decision"])


if __name__ == "__main__":
    main()
