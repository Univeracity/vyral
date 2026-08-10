from __future__ import annotations

from dataclasses import replace

from ..local.query_models import LexicalSearchOptions
from .models import (
    EmbeddingOptions,
    HybridSearchOptions,
    RerankOptions,
    RetrievalRequest,
)


EVIDENCE_FIELDS = (
    "/content/text",
    "/metadata/referenceId",
    "/metadata/page",
    "/metadata/title",
    "/metadata/source",
    "/id",
)
EVIDENCE_BOOSTS = {
    "/metadata/referenceId": 3.0,
    "/id": 1.5,
    "/metadata/page": 1.25,
    "/metadata/title": 1.15,
}
RAG_FIELDS = (
    "/content/text",
    "/metadata/title",
    "/metadata/source",
    "/id",
)
PRODUCT_FIELDS = (
    "/content/title",
    "/content/bullets",
    "/content/description",
    "/content/text",
    "/metadata/keywords",
    "/metadata/brand",
    "/metadata/source",
    "/id",
)

_PROFILE_METADATA = (
    (
        "evidence",
        "Evidence lookup",
        "Lexical-first retrieval for exact identifiers, stable source "
        "references, citations, and other verified-reference workflows.",
        ("verified records", "exact identifiers", "source-reference lookup",
         "citation-sensitive QA"),
        ("Use labeled evaluation before adding vectors to verified claim "
         "retrieval.",),
    ),
    (
        "ragBaseline",
        "RAG baseline",
        "Stable lexical RAG baseline for local development and regression "
        "testing before semantic or rerank promotion.",
        ("RAG baseline", "deterministic local testing",
         "citation-markdown context assembly"),
        ("Semantic recall should be characterized separately with a "
         "labeled fixture.",),
    ),
    (
        "rerankPolish",
        "Rerank polish",
        "Lexical retrieval with a conservative rerank pass for modest "
        "top-rank improvement without losing lexical recall.",
        ("rank polishing", "small candidate rerank",
         "evidence retrieval experiments"),
        ("Requires a configured rerank provider for non-token-overlap "
         "behavior.", "Fallback diagnostics should be reviewed during "
         "evaluation."),
    ),
    (
        "deepQuality",
        "Deep quality",
        "Lexical retrieval with larger rerank payloads for quality sweeps "
        "where slower mixed rerank/fallback behavior is acceptable.",
        ("quality characterization", "offline evaluation",
         "candidate sweep testing"),
        ("Slower than baseline retrieval.", "Oversized rerank payloads may "
         "fall back; inspect fallback rates before promotion."),
    ),
    (
        "discovery",
        "Semantic discovery",
        "Hybrid lexical/vector retrieval for exploratory recall and topic "
        "discovery over conventional RAG chunk collections.",
        ("topic discovery", "semantic exploration", "broad recall"),
        ("Assumes a contentEmbedding vector field unless the request "
         "overrides embedding/vectorFields.", "Do not use as the default "
         "for verified evidence until evaluated."),
    ),
    (
        "productOptimization",
        "Product optimization",
        "Hybrid retrieval for product copy work across listings, keywords, "
        "reviews, competitor notes, manuals, and research artifacts.",
        ("product copy optimization", "keyword research",
         "review/OEM/manual synthesis"),
        ("Assumes a contentEmbedding vector field unless the request "
         "overrides embedding/vectorFields.", "Review retrieved claims "
         "before using them in generated copy."),
    ),
)


def _evidence_lexical() -> LexicalSearchOptions:
    return LexicalSearchOptions(
        fields=EVIDENCE_FIELDS,
        field_boosts=EVIDENCE_BOOSTS,
        scan_limit=5000,
        scoring="bm25",
        prefix_matching=True,
        prefix_min_chars=3,
    )


def _rag_lexical() -> LexicalSearchOptions:
    return LexicalSearchOptions(
        fields=RAG_FIELDS,
        scan_limit=5000,
        scoring="bm25",
        prefix_matching=True,
        prefix_min_chars=3,
    )


def apply_retrieval_profile(request: RetrievalRequest) -> RetrievalRequest:
    if request.profile is None or not request.profile.strip():
        return request
    profile = request.profile.strip().lower()
    defaults: tuple[
        str,
        str,
        LexicalSearchOptions,
        EmbeddingOptions | None,
        HybridSearchOptions | None,
        RerankOptions | None,
        int,
    ]
    if profile == "evidence":
        defaults = ("evidence", "lexical", _evidence_lexical(), None, None, None, 8)
    elif profile == "ragbaseline":
        defaults = ("ragBaseline", "lexical", _rag_lexical(), None, None, None, 8)
    elif profile == "rerankpolish":
        defaults = (
            "rerankPolish",
            "lexical",
            _evidence_lexical(),
            None,
            None,
            RerankOptions(enabled=True, candidate_limit=8, max_candidate_chars=1000),
            8,
        )
    elif profile == "deepquality":
        defaults = (
            "deepQuality",
            "lexical",
            _evidence_lexical(),
            None,
            None,
            RerankOptions(enabled=True, candidate_limit=40, max_candidate_chars=2000),
            8,
        )
    elif profile == "discovery":
        defaults = (
            "discovery",
            "hybrid",
            _rag_lexical(),
            EmbeddingOptions(field="contentEmbedding", purpose="query"),
            HybridSearchOptions(
                vector_weight=0.35,
                lexical_weight=0.65,
                candidate_multiplier=8,
                fusion="rrf",
                rrf_k=60,
            ),
            None,
            10,
        )
    elif profile == "productoptimization":
        defaults = (
            "productOptimization",
            "hybrid",
            LexicalSearchOptions(
                fields=PRODUCT_FIELDS,
                field_boosts={
                    "/metadata/keywords": 2.0,
                    "/content/title": 1.5,
                    "/content/bullets": 1.35,
                    "/metadata/brand": 1.2,
                },
                scan_limit=5000,
                scoring="bm25",
                prefix_matching=True,
                prefix_min_chars=3,
            ),
            EmbeddingOptions(field="contentEmbedding", purpose="query"),
            HybridSearchOptions(
                vector_weight=0.45,
                lexical_weight=0.55,
                candidate_multiplier=8,
                fusion="rrf",
                rrf_k=60,
            ),
            None,
            12,
        )
    else:
        raise ValueError(f"Retrieval profile {request.profile!r} is not supported.")
    (
        profile_id,
        search_mode,
        lexical,
        embedding,
        hybrid,
        rerank,
        limit,
    ) = defaults
    return replace(
        request,
        profile=profile_id,
        search_mode=request.search_mode or search_mode,
        lexical=request.lexical or lexical,
        embedding=request.embedding or embedding,
        hybrid=request.hybrid or hybrid,
        rerank=request.rerank or rerank,
        limit=limit if request.limit == 10 else request.limit,
        include_trace=True,
    )


def get_retrieval_profiles() -> tuple[dict[str, object], ...]:
    """Return the portable retrieval-profile catalog in wire shape."""
    profiles: list[dict[str, object]] = []
    for profile_id, label, description, recommended, cautions in (
        _PROFILE_METADATA
    ):
        defaults = apply_retrieval_profile(
            RetrievalRequest(
                query="", collections=(), profile=profile_id
            )
        )
        profiles.append(
            {
                "id": profile_id,
                "label": label,
                "description": description,
                "searchMode": defaults.search_mode or "lexical",
                "requiresVector": defaults.embedding is not None,
                "usesRerank": bool(
                    defaults.rerank is not None
                    and defaults.rerank.enabled
                ),
                "recommendedFor": list(recommended),
                "cautions": list(cautions),
                "defaults": {
                    "searchMode": defaults.search_mode or "lexical",
                    "embedding": (
                        defaults.embedding.to_dict()
                        if defaults.embedding is not None
                        else None
                    ),
                    "lexical": (
                        defaults.lexical.to_dict()
                        if defaults.lexical is not None
                        else None
                    ),
                    "hybrid": (
                        defaults.hybrid.to_dict()
                        if defaults.hybrid is not None
                        else None
                    ),
                    "rerank": (
                        defaults.rerank.to_dict()
                        if defaults.rerank is not None
                        else None
                    ),
                    "limit": defaults.limit,
                    "includeTrace": defaults.include_trace,
                },
            }
        )
    return tuple(profiles)
