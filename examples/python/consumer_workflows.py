from __future__ import annotations

import os
import pathlib
import sys
from typing import Any

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "clients/python/src"))

from vyral_client import (
    VyralClient,
    build_graph_edge,
    build_graph_envelope,
    build_graph_expansion_options,
    build_graph_node,
    build_graph_scope,
    build_graph_source_span,
    build_provider_extract_request,
    build_rag_context_request,
    build_rerank_options,
)


BASE_URL = os.environ.get("VYRAL_URL", "http://127.0.0.1:5220")
API_KEY = os.environ.get("VYRAL_API_KEY")
COLLECTION = os.environ.get("VYRAL_COLLECTION", "consumer-workflows-python")
GRAPH_COLLECTION = os.environ.get("VYRAL_GRAPH_COLLECTION", "consumer-workflows-graph-python")
PARTITION_KEY = "tenant:consumer-workflows"
GRAPH_ID = "consumer-workflows"
VECTOR_FIELD = "contentEmbedding"


DOCUMENTS = [
    {
        "id": "retention",
        "topic": "records",
        "graphNodeId": "chunk:retention",
        "text": "Retention holds keep protected records from deletion until the hold is released.",
    },
    {
        "id": "travel",
        "topic": "finance",
        "graphNodeId": "chunk:travel",
        "text": "Travel reimbursement requires receipts for hotels, flights, and approved meals.",
    },
    {
        "id": "security",
        "topic": "security",
        "graphNodeId": "chunk:security",
        "text": "Compromised credentials should be rotated and reviewed through incident response.",
    },
    {
        "id": "listing",
        "topic": "catalog",
        "graphNodeId": "chunk:listing",
        "text": "Insulated stainless travel mugs should mention leak-resistant lids, capacity, care instructions, and fit with cup holders.",
    },
]


def main() -> None:
    client = VyralClient(BASE_URL, api_key=API_KEY)
    health = client.health()
    readiness = client.readiness()
    print(f"server={BASE_URL} ready={readiness.get('ready')} summary={readiness.get('summary')}")

    reset_collections(client)
    create_rag_corpus(client, health)
    create_graph(client)

    lexical = run_lexical_rag(client)
    semantic = run_semantic_vector_rag(client)
    reranked = run_lexical_rerank(client)
    graph = run_graphrag(client)
    extraction = run_ai_extract(client)
    provider_info = inspect_providers(client)

    print_summary("lexical", lexical)
    print_summary("semantic-vector", semantic)
    print_summary("lexical-rerank", reranked)
    print_summary("graphrag", graph)
    print_provider_summary(extraction, provider_info)


def reset_collections(client: VyralClient) -> None:
    for collection in (COLLECTION, GRAPH_COLLECTION):
        run = client.delete_collection(
            collection,
            idempotency_key=f"consumer:{collection}:delete",
        )
        client.wait_execution_run(run["id"])


def create_rag_corpus(client: VyralClient, health: dict[str, Any]) -> None:
    embedding = health["embedding"]
    create_run = client.create_rag_collection(
        COLLECTION,
        dimensions=embedding["dimensions"],
        embedding_field=VECTOR_FIELD,
        indexed_metadata=[
            "/metadata/documentId",
            "/metadata/topic",
            "/metadata/status",
            "/metadata/graphNodeId",
            "/type",
        ],
        idempotency_key=f"consumer:{COLLECTION}:create",
    )
    client.wait_execution_run(create_run["id"])

    ingestion = client.ingest_rag_texts(
        COLLECTION,
        [
            {
                "documentId": document["id"],
                "partitionKey": PARTITION_KEY,
                "text": document["text"],
                "embeddingField": VECTOR_FIELD,
                "sourceUri": f"memory://consumer-workflows/{document['id']}",
                "sourceKind": "example",
                "metadata": {
                    "status": "active",
                    "topic": document["topic"],
                    "graphNodeId": document["graphNodeId"],
                },
            }
            for document in DOCUMENTS
        ],
        continue_on_error=False,
        idempotency_key=f"consumer:{COLLECTION}:ingest",
    )
    client.wait_rag_ingestion_job(ingestion["id"])


def create_graph(client: VyralClient) -> None:
    source = build_graph_source_span(
        "memory://consumer-workflows/retention",
        char_start=0,
        char_end=len(DOCUMENTS[0]["text"]),
    )
    scope = build_graph_scope(
        GRAPH_ID,
        namespace="examples",
        collection=COLLECTION,
        tenant_id=PARTITION_KEY,
        partition_key=PARTITION_KEY,
    )
    envelope = build_graph_envelope(
        scope,
        nodes=[
            build_graph_node("chunk:retention", "chunk", label="Retention chunk", source_spans=[source]),
            build_graph_node("concept:retention-hold", "concept", label="Retention hold", source_spans=[source]),
            build_graph_node("concept:protected-record", "concept", label="Protected record", source_spans=[source]),
        ],
        edges=[
            build_graph_edge(
                "edge:retention-hold",
                "chunk:retention",
                "concept:retention-hold",
                "supports",
                source_spans=[source],
            ),
            build_graph_edge(
                "edge:retention-record",
                "concept:retention-hold",
                "concept:protected-record",
                "mentions",
                source_spans=[source],
            ),
        ],
    )
    job = client.import_graph_envelope(
        GRAPH_COLLECTION,
        envelope,
        replace_existing=True,
        idempotency_key=f"consumer:{GRAPH_COLLECTION}:import",
    )
    client.wait_graph_job(job["id"])


def run_lexical_rag(client: VyralClient) -> dict[str, Any]:
    return client.build_rag_context(
        build_rag_context_request(
            "retention protected records deletion hold",
            [COLLECTION],
            partition_keys=[PARTITION_KEY],
            search_mode="lexical",
            limit=3,
            include_context_text=True,
        )
    )


def run_semantic_vector_rag(client: VyralClient) -> dict[str, Any]:
    return client.build_rag_context(
        build_rag_context_request(
            "how do we prevent protected records from being deleted",
            [COLLECTION],
            partition_keys=[PARTITION_KEY],
            search_mode="vector",
            embedding={"field": VECTOR_FIELD, "purpose": "query"},
            limit=3,
            include_context_text=True,
        )
    )


def run_lexical_rerank(client: VyralClient) -> dict[str, Any]:
    return client.build_rag_context(
        build_rag_context_request(
            "retention protected records deletion hold",
            [COLLECTION],
            partition_keys=[PARTITION_KEY],
            search_mode="lexical",
            rerank=build_rerank_options(
                provider=os.environ.get("VYRAL_RECIPE_RERANK_PROVIDER", "local-token-overlap-reranker"),
                candidate_limit=8,
                max_candidate_chars=800,
                fallback_on_failure=True,
            ),
            limit=3,
            include_context_text=True,
        )
    )


def run_graphrag(client: VyralClient) -> dict[str, Any]:
    graph_expansion = build_graph_expansion_options(
        GRAPH_COLLECTION,
        graph_id=GRAPH_ID,
        partition_key=PARTITION_KEY,
        seed_json_pointers=["/metadata/graphNodeId"],
        profile={
            "id": "grounded-support",
            "direction": "outgoing",
            "maxDepth": 2,
            "predicates": ["supports", "mentions"],
            "requireSourceGrounding": True,
            "limit": 8,
            "edgeLimit": 8,
        },
        max_graph_context_chars=1000,
        max_graph_provenance_items=16,
    )
    request = build_rag_context_request(
        "retention protected records deletion hold",
        [COLLECTION],
        partition_keys=[PARTITION_KEY],
        search_mode="lexical",
        graph_expansion=graph_expansion,
        limit=3,
        include_context_text=True,
    )
    context = client.build_rag_context(request)
    evaluation = client.evaluate_rag_context(
        {
            "cases": [
                {
                    "name": "retention-graphrag",
                    "request": request,
                    "expectedGraph": {
                        "nodeIds": ["chunk:retention", "concept:retention-hold"],
                        "edgeIds": ["edge:retention-hold"],
                        "provenanceEntityIds": ["edge:retention-hold"],
                        "requireSourceGroundedProvenance": True,
                        "requireGraphContextText": True,
                        "requireContextTextNotTruncated": True,
                    },
                }
            ],
            "includeContext": False,
        }
    )
    return {"context": context, "evaluation": evaluation}


def run_ai_extract(client: VyralClient) -> dict[str, Any]:
    provider = os.environ.get("VYRAL_RECIPE_AI_PROVIDER", "local-deterministic-ai")
    job = client.run_provider(
        provider,
        build_provider_extract_request(
            DOCUMENTS[3]["text"],
            schema={
                "type": "object",
                "properties": {
                    "draftBullets": {"type": "array", "items": {"type": "string"}},
                    "reviewNotes": {"type": "array", "items": {"type": "string"}},
                    "claimsNeedingReview": {"type": "array", "items": {"type": "string"}},
                },
            },
            instructions="Extract product-listing copy fields and mark review-sensitive claims clearly.",
            max_output_bytes=32_000,
        ),
        idempotency_key=f"consumer:{provider}:extract",
    )
    return (client.wait_provider_job(job["id"]) or {}).get("result", {})


def inspect_providers(client: VyralClient) -> dict[str, Any]:
    provider = os.environ.get("VYRAL_RECIPE_AI_PROVIDER", "local-deterministic-ai")
    return {
        "providers": client.list_providers(),
        "matrix": client.get_provider_capability_matrix(),
        "models": client.list_provider_models(provider),
        "quota": client.get_provider_quota("codex-cli"),
        "readiness": client.get_provider_readiness(provider),
    }


def print_summary(name: str, result: dict[str, Any]) -> None:
    context = result.get("context", result)
    chunks = context.get("chunks") or []
    first = chunks[0]["id"] if chunks else "none"
    graph_status = ((context.get("graphContext") or {}).get("status")) if isinstance(context, dict) else None
    print(f"{name}: chunks={len(chunks)} first={first} graphStatus={graph_status or 'n/a'}")
    if "evaluation" in result:
        evaluation = result["evaluation"]
        print(f"{name}: eval passRate={evaluation.get('passRate')} passed={evaluation.get('passedCount')}/{evaluation.get('attempted')}")


def print_provider_summary(extraction: dict[str, Any], provider_info: dict[str, Any]) -> None:
    providers = [provider.get("id") for provider in provider_info.get("providers", [])]
    matrix_items = provider_info.get("matrix", {}).get("items", [])
    print(f"providers={providers}")
    print(f"capabilityMatrixItems={len(matrix_items)}")
    print(f"extract status={extraction.get('status')} provider={extraction.get('provider')} capability={extraction.get('capability')}")
    print(f"model catalog status={(provider_info.get('models') or {}).get('status')}")
    print(f"codex quota status={(provider_info.get('quota') or {}).get('status')}")


if __name__ == "__main__":
    main()
