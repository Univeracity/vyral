from __future__ import annotations

import os
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "clients/python/src"))

from vyral_client import VyralClient


COLLECTION = os.environ.get("VYRAL_COLLECTION", "quickstart-chunks-python")
PARTITION_KEY = "tenant:quickstart"
VECTOR_FIELD = "contentEmbedding"


def main() -> None:
    client = VyralClient(os.environ.get("VYRAL_URL", "http://localhost:5220"))
    health = client.health()
    embedding = health["embedding"]

    delete_run = client.delete_collection(
        COLLECTION, idempotency_key=f"quickstart:{COLLECTION}:delete"
    )
    client.wait_execution_run(delete_run["id"])
    create_run = client.create_rag_collection(
        COLLECTION,
        dimensions=embedding["dimensions"],
        embedding_field=VECTOR_FIELD,
        indexed_metadata=["/metadata/status", "/metadata/topic"],
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

    ingestion = client.ingest_rag_texts(COLLECTION, [
        {
            "documentId": document["id"],
            "partitionKey": PARTITION_KEY,
            "text": document["text"],
            "embeddingField": VECTOR_FIELD,
            "sourceUri": f"memory://quickstart/{document['id']}",
            "sourceKind": "example",
            "metadata": {
                "status": "active",
                "topic": document["topic"],
            },
        }
        for document in documents
    ], idempotency_key=f"quickstart:{COLLECTION}:ingest")
    client.wait_rag_ingestion_job(ingestion["id"])

    context = client.build_rag_context({
        "query": "Retention holds keep protected records from deletion until the hold is released.",
        "collections": [COLLECTION],
        "partitionKeys": [PARTITION_KEY],
        "embeddingField": VECTOR_FIELD,
        "limit": 2,
        "maxChars": 2000,
        "maxCharsPerChunk": 800,
        "includeContextText": True,
        "includeTrace": True,
    })
    prompt = client.build_rag_prompt({
        "context": {
            "query": "Retention holds keep protected records from deletion until the hold is released.",
            "collections": [COLLECTION],
            "partitionKeys": [PARTITION_KEY],
            "embeddingField": VECTOR_FIELD,
            "limit": 2,
            "maxChars": 2000,
            "maxCharsPerChunk": 800,
            "includeTrace": True,
        },
        "template": {"failOnEmptyContext": True},
    })

    print(f"provider={embedding['provider']} model={embedding['modelId']} dimensions={embedding['dimensions']}")
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
