from __future__ import annotations

import os
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "clients/python/src"))

from vyral_client import VyralClient


COLLECTION = os.environ.get("VYRAL_COLLECTION", "quickstart-chunks-python")
PARTITION_KEY = "tenant:quickstart"


def main() -> None:
    client = VyralClient(os.environ.get("VYRAL_URL", "http://localhost:5220"))

    delete_run = client.delete_collection(
        COLLECTION, idempotency_key=f"quickstart:{COLLECTION}:delete"
    )
    client.wait_execution_run(delete_run["id"])
    create_run = client.create_collection(
        {
            "name": COLLECTION,
            "partitionKeyPath": "/partitionKey",
            "vectorPolicies": [],
            "indexedMetadata": ["/metadata/status", "/metadata/topic"],
        },
        idempotency_key=f"quickstart:{COLLECTION}:create",
    )
    client.wait_execution_run(create_run["id"])

    documents = [
        {
            "id": "retention",
            "topic": "records",
            "text": "Retention holds keep protected records from deletion until the hold is released.",
        },
        {
            "id": "travel",
            "topic": "finance",
            "text": "Travel reimbursement requires receipts for hotels, flights, and approved meals.",
        },
        {
            "id": "security",
            "topic": "security",
            "text": "Compromised credentials should be rotated and reviewed through incident response.",
        },
    ]

    client.upsert_records(
        COLLECTION,
        [
            {
                "id": document["id"],
                "partitionKey": PARTITION_KEY,
                "type": "rag.chunk",
                "content": {"text": document["text"]},
                "metadata": {
                    "status": "active",
                    "topic": document["topic"],
                },
                "sources": [
                    {
                        "id": document["id"],
                        "kind": "example",
                        "uri": f"memory://quickstart/{document['id']}",
                        "label": document["id"],
                    }
                ],
            }
            for document in documents
        ],
        idempotency_key=f"quickstart:{COLLECTION}:ingest",
    )

    retrieval = {
        "query": "Retention holds keep protected records from deletion until the hold is released.",
        "collections": [COLLECTION],
        "partitionKeys": [PARTITION_KEY],
        "searchMode": "lexical",
        "lexical": {"fields": ["/content/text"]},
        "limit": 2,
        "includeTrace": True,
    }
    context_request = {
        "retrieval": retrieval,
        "maxChars": 2000,
        "maxCharsPerChunk": 800,
        "includeContextText": True,
        "includeTrace": True,
    }
    context = client.build_rag_context(context_request)
    prompt = client.build_rag_prompt({
        "context": context_request,
        "template": {"failOnEmptyContext": True},
    })

    print("retrieval=lexical embeddings=unused")
    for chunk in context["chunks"]:
        citations = ", ".join(chunk.get("citationIds", [])) or "none"
        print(f"{chunk['rank']}. {chunk['id']} score={chunk['score']:.4f} citations={citations} text={chunk['text']}")

    for citation in context.get("citations", []):
        print(f"[{citation['id']}] {citation.get('sourceUri') or citation['recordId']}")

    print("\ncontextText:")
    print(context["contextText"])
    print(f"contextTextHash={context['contextTextHash']}")
    print(f"promptHash={prompt['promptHash']}")


if __name__ == "__main__":
    main()
