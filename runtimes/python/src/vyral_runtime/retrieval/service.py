from __future__ import annotations

from dataclasses import dataclass, field, replace
from datetime import datetime, timezone
import struct
from time import perf_counter
from typing import Any, Mapping, Protocol, Sequence

from ..async_runtime import RuntimeExecutor
from ..embeddings import (
    EmbeddingProvider,
    EmbeddingProviderOptions,
    EmbeddingProviderRegistry,
    prepare_embedding_text,
)
from ..local import (
    LexicalSearchOptions,
    QueryEnvelope,
    RetrievalDiagnostics,
    SQLiteRecordStore,
    TraceRecord,
    VectorSearchOptions,
    VyralRecord,
)
from .models import (
    EmbeddingOptions,
    HybridSearchOptions,
    RerankOptions,
    RetrievalMatch,
    RetrievalRequest,
    RetrievalResultEnvelope,
    RetrievalVectorFieldQuery,
)
from .profiles import apply_retrieval_profile


MAX_VECTOR_FIELDS = 8


def _f32(value: float) -> float:
    return float(struct.unpack("<f", struct.pack("<f", value))[0])


class RerankingService(Protocol):
    provider_id: str

    def rerank(
        self,
        query: str,
        candidates: Sequence[tuple[str, str, float]],
    ) -> Mapping[str, float]: ...


class TraceWriter(Protocol):
    def write_trace(self, trace: TraceRecord) -> None: ...


class TokenOverlapReranker:
    provider_id = "local-token-overlap"

    def rerank(
        self,
        query: str,
        candidates: Sequence[tuple[str, str, float]],
    ) -> Mapping[str, float]:
        query_terms = set(_tokens(query))
        scores: dict[str, float] = {}
        for candidate_id, text, _ in candidates:
            candidate_terms = set(_tokens(text))
            overlap = len(query_terms & candidate_terms)
            scores[candidate_id] = _f32(
                overlap / len(query_terms) if query_terms else 0.0
            )
        return scores


@dataclass(frozen=True)
class _VectorSpec:
    field: str
    distance_function: str
    weight: float
    query: str
    embedding: EmbeddingOptions
    candidate_limit: int
    min_score: float | None


@dataclass(frozen=True)
class _VectorHit:
    field: str
    raw_score: float
    normalized_score: float
    rank: int
    distance_function: str
    weight: float


@dataclass
class _Candidate:
    collection: str
    record: VyralRecord
    vector_hits: dict[str, _VectorHit] = field(default_factory=dict)
    vector_score: float | None = None
    vector_normalized_score: float | None = None
    vector_rank: int | None = None
    lexical_score: float | None = None
    lexical_rank: int | None = None
    lexical_diagnostics: RetrievalDiagnostics | None = None
    candidate_sources: set[str] = field(default_factory=set)
    vector_field_count: int = 0
    score: float = 0.0
    diagnostics: RetrievalDiagnostics | None = None


class RetrievalService:
    def __init__(
        self,
        record_store: SQLiteRecordStore,
        embedding_provider: EmbeddingProvider | None = None,
        *,
        embedding_options: EmbeddingProviderOptions | Mapping[str, Any] | None = None,
        reranker: RerankingService | None = None,
        trace_store: TraceWriter | None = None,
        executor: RuntimeExecutor | None = None,
    ) -> None:
        self.record_store = record_store
        self.embedding_options = EmbeddingProviderOptions.from_value(embedding_options)
        self.embedding_provider = embedding_provider or EmbeddingProviderRegistry().create(
            self.embedding_options
        )
        self.reranker = reranker or TokenOverlapReranker()
        self.trace_store = trace_store
        self.executor = executor or RuntimeExecutor()
        self._owns_executor = executor is None

    def search(
        self,
        request: RetrievalRequest | Mapping[str, Any],
    ) -> RetrievalResultEnvelope:
        started_at = datetime.now(timezone.utc)
        started = perf_counter()
        normalized = apply_retrieval_profile(RetrievalRequest.from_value(request))
        _validate_request(normalized)
        search_mode = _search_mode(normalized)
        rerank = _validate_rerank(normalized.rerank)
        candidate_limit = (
            max(
                normalized.limit,
                rerank.candidate_limit or normalized.limit * 4,
            )
            if rerank is not None and rerank.enabled
            else normalized.limit
        )
        all_candidates: list[_Candidate] = []
        pool_summaries: list[dict[str, Any]] = []
        embedding_cache: dict[str, tuple[float, ...]] = {}
        for collection in normalized.collections:
            candidates, pool = self._search_collection(
                collection,
                normalized,
                search_mode,
                candidate_limit,
                embedding_cache,
            )
            all_candidates.extend(candidates)
            pool_summaries.append(pool)

        ranked = sorted(
            all_candidates,
            key=lambda item: (
                -item.score,
                item.collection,
                item.record.partition_key,
                item.record.id,
            ),
        )[:candidate_limit]
        if rerank is not None and rerank.enabled:
            ranked = self._rerank(normalized, rerank, ranked)
        else:
            ranked = ranked[: normalized.limit]

        matches: list[RetrievalMatch] = []
        for index, candidate in enumerate(ranked):
            diagnostics = (
                _stamp_rank(candidate.diagnostics, candidate, index + 1, len(ranked))
                if normalized.include_trace
                else None
            )
            matches.append(
                RetrievalMatch(
                    rank=index + 1,
                    score=candidate.score,
                    collection=candidate.collection,
                    snippet=_snippet(candidate.record),
                    record=candidate.record,
                    diagnostics=diagnostics,
                )
            )
        trace: dict[str, Any] | None = None
        if normalized.include_trace:
            duration_ms = (perf_counter() - started) * 1000.0
            request_summary: dict[str, Any] = {
                "profile": normalized.profile,
                "query": normalized.query,
                "collections": list(normalized.collections),
                "partitionKeys": (
                    list(normalized.partition_keys)
                    if normalized.partition_keys is not None
                    else None
                ),
                "searchMode": search_mode,
                "limit": normalized.limit,
                "minScore": normalized.min_score,
            }
            result_summary: dict[str, Any] = {
                "profile": normalized.profile,
                "embeddingProvider": (
                    self.embedding_provider.provider_id
                    if search_mode in {"vector", "hybrid"}
                    else None
                ),
                "embeddingModel": (
                    self.embedding_provider.model_id
                    if search_mode in {"vector", "hybrid"}
                    else None
                ),
                "candidateCount": len(all_candidates),
                "returnedCount": len(matches),
                "searchMode": search_mode,
                "candidatePools": pool_summaries,
                "rerankEnabled": bool(rerank is not None and rerank.enabled),
                "rerankProvider": (
                    self.reranker.provider_id
                    if rerank is not None and rerank.enabled
                    else None
                ),
            }
            trace_record = TraceRecord(
                operation="retrieval.search",
                adapter=type(self.record_store).__name__,
                request=request_summary,
                result_summary=result_summary,
                started_at=started_at,
                duration_ms=duration_ms,
                created_at=datetime.now(timezone.utc),
            )
            if self.trace_store is not None:
                self.trace_store.write_trace(trace_record)
            trace = {
                "id": trace_record.id,
                "durationMs": duration_ms,
                "tracePersisted": self.trace_store is not None,
                "operation": trace_record.operation,
                "adapter": trace_record.adapter,
                "request": request_summary,
                "resultSummary": result_summary,
            }
        return RetrievalResultEnvelope(
            query=normalized.query,
            results=tuple(matches),
            trace=trace,
        )

    async def asearch(
        self,
        request: RetrievalRequest | Mapping[str, Any],
    ) -> RetrievalResultEnvelope:
        return await self.executor.run(lambda: self.search(request))

    def close(self) -> None:
        if self._owns_executor:
            self.executor.close()

    def _search_collection(
        self,
        collection: str,
        request: RetrievalRequest,
        search_mode: str,
        result_limit: int,
        embedding_cache: dict[str, tuple[float, ...]],
    ) -> tuple[list[_Candidate], dict[str, Any]]:
        policy = self.record_store.get_collection_policy(collection)
        if policy is None:
            raise ValueError(f"Collection {collection!r} does not exist.")
        candidates: dict[tuple[str, str, str], _Candidate] = {}
        specs = (
            _resolve_vector_specs(
                collection,
                policy.vector_policies,
                request,
                search_mode,
                result_limit,
            )
            if search_mode in {"vector", "hybrid"}
            else ()
        )
        vector_count = 0
        vector_counts: dict[str, int] = {}
        for spec in specs:
            if self.embedding_provider.dimensions != next(
                item.dimensions
                for item in policy.vector_policies
                if item.name == spec.field
            ):
                raise ValueError(
                    f"Embedding provider returns {self.embedding_provider.dimensions} "
                    f"dimensions, but collection {collection!r} field {spec.field!r} "
                    "expects a different size."
                )
            prepared = prepare_embedding_text(
                spec.query,
                spec.embedding.purpose or "query",
                spec.embedding.query_prefix or self.embedding_options.query_prefix,
                spec.embedding.passage_prefix or self.embedding_options.passage_prefix,
                spec.embedding.symmetric_prefix or self.embedding_options.symmetric_prefix,
            )
            vector = embedding_cache.get(prepared.prepared_text)
            if vector is None:
                vector = self.embedding_provider.generate_embedding(prepared.prepared_text)
                if len(vector) != self.embedding_provider.dimensions:
                    raise ValueError(
                        f"Embedding provider returned {len(vector)} dimensions, "
                        f"expected {self.embedding_provider.dimensions}."
                    )
                embedding_cache[prepared.prepared_text] = vector
            matches = self.record_store.search_all_records(
                collection,
                QueryEnvelope(
                    partition_keys=request.partition_keys,
                    filter=request.filter,
                    vector=VectorSearchOptions(
                        field=spec.field,
                        value=vector,
                        top=spec.candidate_limit,
                        min_score=spec.min_score,
                    ),
                    limit=spec.candidate_limit,
                ),
            )
            vector_counts[spec.field] = len(matches)
            vector_count += len(matches)
            for rank, match in enumerate(matches, start=1):
                candidate = _candidate(
                    candidates,
                    collection,
                    match.record,
                )
                candidate.vector_hits[spec.field] = _VectorHit(
                    field=spec.field,
                    raw_score=match.score,
                    normalized_score=_normalize_vector_score(
                        spec.distance_function,
                        match.score,
                    ),
                    rank=rank,
                    distance_function=spec.distance_function,
                    weight=spec.weight,
                )
                candidate.candidate_sources.add("vector")

        lexical_count = 0
        if search_mode in {"lexical", "hybrid"}:
            lexical = _lexical_options(request, search_mode, result_limit)
            matches = self.record_store.search_all_records(
                collection,
                QueryEnvelope(
                    partition_keys=request.partition_keys,
                    filter=request.filter,
                    lexical=lexical,
                    limit=lexical.top,
                ),
            )
            lexical_count = len(matches)
            for rank, match in enumerate(matches, start=1):
                candidate = _candidate(candidates, collection, match.record)
                candidate.lexical_score = match.score
                candidate.lexical_rank = rank
                candidate.lexical_diagnostics = match.diagnostics
                candidate.candidate_sources.add("lexical")

        for candidate in candidates.values():
            _finalize_vector(candidate, specs)
            candidate.score = _final_score(candidate, search_mode, request.hybrid)
            if (
                request.min_score is not None
                and candidate.score < request.min_score
            ):
                continue
            if request.include_trace:
                candidate.diagnostics = _diagnostics(
                    candidate,
                    search_mode,
                    request.hybrid,
                    vector_count,
                    vector_counts,
                    lexical_count,
                    len(candidates),
                    result_limit,
                    self.embedding_provider,
                )
        selected = [
            candidate
            for candidate in candidates.values()
            if request.min_score is None or candidate.score >= request.min_score
        ]
        return selected, {
            "collection": collection,
            "vectorCandidates": vector_count,
            "vectorCandidatesByField": vector_counts,
            "lexicalCandidates": lexical_count,
            "mergedCandidates": len(candidates),
            "searchMode": search_mode,
        }

    def _rerank(
        self,
        request: RetrievalRequest,
        options: RerankOptions,
        ranked: list[_Candidate],
    ) -> list[_Candidate]:
        selected = ranked[: options.candidate_limit or len(ranked)]
        payload = [
            (
                _candidate_id(candidate),
                _rerank_text(candidate.record, options)[: options.max_candidate_chars],
                candidate.score,
            )
            for candidate in selected
        ]
        try:
            scores = self.reranker.rerank(request.query, payload)
        except Exception:
            if options.fallback_on_failure:
                return ranked[: request.limit]
            raise
        for candidate in selected:
            rerank_score = scores.get(_candidate_id(candidate), 0.0)
            original = candidate.score
            candidate.score = _f32(
                rerank_score * options.rerank_score_weight
                + original * options.original_score_weight
            )
            if candidate.diagnostics is not None:
                candidate.diagnostics = _with_rerank_diagnostics(
                    candidate.diagnostics,
                    original,
                    rerank_score,
                    self.reranker.provider_id,
                )
        return sorted(
            selected,
            key=lambda item: (
                -item.score,
                item.collection,
                item.record.partition_key,
                item.record.id,
            ),
        )[: request.limit]


def _validate_request(request: RetrievalRequest) -> None:
    if not request.query.strip():
        raise ValueError("Retrieval query is required.")
    if not request.collections:
        raise ValueError("At least one retrieval collection is required.")
    if request.limit <= 0:
        raise ValueError("Retrieval limit must be greater than zero.")
    _search_mode(request)
    if request.vector_fields is not None:
        if len(request.vector_fields) > MAX_VECTOR_FIELDS:
            raise ValueError(
                f"Retrieval supports at most {MAX_VECTOR_FIELDS} vectorFields entries."
            )
        fields: set[str] = set()
        total = 0.0
        for item in request.vector_fields:
            field_name = item.field.strip()
            if not field_name:
                raise ValueError("Retrieval vectorFields field is required.")
            if field_name in fields:
                raise ValueError(
                    f"Retrieval vectorFields contains duplicate field {item.field!r}."
                )
            fields.add(field_name)
            if item.weight < 0:
                raise ValueError(
                    "Retrieval vectorFields weight must be non-negative."
                )
            total += item.weight
            if item.candidate_limit is not None and item.candidate_limit <= 0:
                raise ValueError(
                    "Retrieval vectorFields candidateLimit must be greater than zero."
                )
        if request.vector_fields and total <= 0:
            raise ValueError(
                "Retrieval vectorFields requires at least one positive weight."
            )
    hybrid = request.hybrid
    if hybrid is not None:
        if hybrid.vector_weight < 0 or hybrid.lexical_weight < 0:
            raise ValueError("Hybrid search weights must be non-negative.")
        if hybrid.vector_weight + hybrid.lexical_weight <= 0:
            raise ValueError("Hybrid search requires at least one positive weight.")
        if hybrid.candidate_multiplier <= 0:
            raise ValueError(
                "Hybrid candidateMultiplier must be greater than zero."
            )
        if hybrid.rrf_k <= 0:
            raise ValueError("Hybrid rrfK must be greater than zero.")
        if hybrid.fusion.strip().lower() not in {"weighted", "rrf"}:
            raise ValueError(
                f"Hybrid fusion {hybrid.fusion!r} is not supported."
            )


def _validate_rerank(options: RerankOptions | None) -> RerankOptions | None:
    if options is None or not options.enabled:
        return options
    if options.candidate_limit is not None and options.candidate_limit <= 0:
        raise ValueError("Rerank candidateLimit must be greater than zero.")
    if options.max_candidate_chars <= 0:
        raise ValueError("Rerank maxCandidateChars must be greater than zero.")
    if options.rerank_score_weight < 0 or options.original_score_weight < 0:
        raise ValueError("Rerank score weights must be non-negative.")
    if options.rerank_score_weight + options.original_score_weight <= 0:
        raise ValueError("Rerank requires at least one positive score weight.")
    if options.timeout_seconds is not None and options.timeout_seconds <= 0:
        raise ValueError("Rerank timeoutSeconds must be greater than zero.")
    if options.max_output_bytes is not None and options.max_output_bytes <= 0:
        raise ValueError("Rerank maxOutputBytes must be greater than zero.")
    if options.content_field and any(
        character in options.content_field for character in "/\\."
    ):
        raise ValueError(
            "Rerank contentField must be a simple content property name."
        )
    return options


def _search_mode(request: RetrievalRequest) -> str:
    if request.search_mode is None or not request.search_mode.strip():
        return (
            "vector"
            if request.embedding is not None or request.vector_fields
            else "lexical"
        )
    mode = request.search_mode.strip().lower()
    if mode not in {"vector", "lexical", "hybrid"}:
        raise ValueError(
            f"Retrieval searchMode {request.search_mode!r} is not supported."
        )
    return mode


def _candidate_limit(
    result_limit: int,
    hybrid: HybridSearchOptions | None,
    explicit: int | None,
) -> int:
    if explicit is not None:
        return max(result_limit, explicit)
    multiplier = hybrid.candidate_multiplier if hybrid is not None else 8
    return max(result_limit * multiplier, result_limit, 50)


def _resolve_vector_specs(
    collection: str,
    policies: Sequence[Any],
    request: RetrievalRequest,
    mode: str,
    result_limit: int,
) -> tuple[_VectorSpec, ...]:
    requested = request.vector_fields or (
        RetrievalVectorFieldQuery(
            field=request.embedding.field if request.embedding and request.embedding.field else "",
            embedding=request.embedding,
        ),
    )
    specs: list[_VectorSpec] = []
    for item in requested:
        field_name = item.field.strip()
        if not field_name and policies:
            field_name = str(policies[0].name)
        policy = next(
            (policy for policy in policies if policy.name == field_name),
            None,
        )
        if policy is None:
            if not field_name:
                raise ValueError(
                    f"Collection {collection!r} does not define a vector policy for retrieval."
                )
            raise ValueError(
                f"Vector field {field_name!r} is not defined in policy for "
                f"collection {collection!r}."
            )
        embedding = item.embedding or request.embedding or EmbeddingOptions()
        default_limit = (
            _candidate_limit(
                result_limit,
                request.hybrid,
                request.hybrid.vector_candidate_limit
                if request.hybrid is not None
                else None,
            )
            if mode == "hybrid"
            else result_limit
        )
        specs.append(
            _VectorSpec(
                field=field_name,
                distance_function=str(policy.distance_function),
                weight=item.weight,
                query=item.query if item.query and item.query.strip() else request.query,
                embedding=embedding,
                candidate_limit=max(
                    result_limit,
                    item.candidate_limit
                    if item.candidate_limit is not None
                    else default_limit,
                ),
                min_score=(
                    item.min_score
                    if item.min_score is not None
                    else request.min_score
                    if mode == "vector"
                    else None
                ),
            )
        )
    return tuple(specs)


def _lexical_options(
    request: RetrievalRequest,
    mode: str,
    result_limit: int,
) -> LexicalSearchOptions:
    source = request.lexical or LexicalSearchOptions()
    top = source.top if source.top > 0 else 50
    if mode == "hybrid":
        top = max(
            top,
            _candidate_limit(
                result_limit,
                request.hybrid,
                request.hybrid.lexical_candidate_limit
                if request.hybrid is not None
                else None,
            ),
        )
    else:
        top = max(result_limit, top)
    return replace(
        source,
        query=source.query if source.query.strip() else request.query,
        top=top,
        min_score=(
            (
                request.min_score
                if request.min_score is not None
                else source.min_score
            )
            if mode == "lexical"
            else source.min_score
        ),
    )


def _candidate(
    candidates: dict[tuple[str, str, str], _Candidate],
    collection: str,
    record: VyralRecord,
) -> _Candidate:
    key = (collection, record.partition_key, record.id)
    if key not in candidates:
        candidates[key] = _Candidate(collection=collection, record=record)
    return candidates[key]


def _normalize_vector_score(distance: str, score: float) -> float:
    normalized = distance.lower()
    if normalized == "cosine":
        return _f32(min(1.0, max(0.0, (score + 1.0) / 2.0)))
    if normalized == "euclidean":
        return _f32(min(1.0, max(0.0, score)))
    if normalized == "dotproduct":
        return _f32(0.0 if score <= 0 else score / (1.0 + score))
    return _f32(min(1.0, max(0.0, score)))


def _finalize_vector(
    candidate: _Candidate,
    specs: Sequence[_VectorSpec],
) -> None:
    if not candidate.vector_hits:
        return
    candidate.vector_field_count = len(specs)
    if len(specs) <= 1:
        hit = next(iter(candidate.vector_hits.values()))
        candidate.vector_score = hit.raw_score
        candidate.vector_normalized_score = hit.normalized_score
        candidate.vector_rank = hit.rank
        return
    total_weight = sum(spec.weight for spec in specs)
    if total_weight <= 0:
        return
    weighted = sum(
        candidate.vector_hits[spec.field].normalized_score * spec.weight
        for spec in specs
        if spec.field in candidate.vector_hits
    )
    candidate.vector_score = _f32(weighted / total_weight)
    candidate.vector_normalized_score = candidate.vector_score
    candidate.vector_rank = min(hit.rank for hit in candidate.vector_hits.values())


def _final_score(
    candidate: _Candidate,
    mode: str,
    hybrid: HybridSearchOptions | None,
) -> float:
    if mode == "vector":
        return candidate.vector_score or 0.0
    if mode == "lexical":
        return candidate.lexical_score or 0.0
    options = hybrid or HybridSearchOptions()
    if options.fusion.strip().lower() == "rrf":
        score = 0.0
        if candidate.vector_rank is not None:
            score += options.vector_weight / (options.rrf_k + candidate.vector_rank)
        if candidate.lexical_rank is not None:
            score += options.lexical_weight / (options.rrf_k + candidate.lexical_rank)
        return _f32(score)
    total = options.vector_weight + options.lexical_weight
    if total <= 0:
        return 0.0
    return _f32(
        (
            options.vector_weight * (candidate.vector_normalized_score or 0.0)
            + options.lexical_weight * (candidate.lexical_score or 0.0)
        )
        / total
    )


def _diagnostics(
    candidate: _Candidate,
    mode: str,
    hybrid: HybridSearchOptions | None,
    vector_count: int,
    vector_counts: Mapping[str, int],
    lexical_count: int,
    merged_count: int,
    result_limit: int,
    provider: EmbeddingProvider,
) -> RetrievalDiagnostics:
    details: dict[str, Any] = {
        "searchMode": mode,
        "collection": candidate.collection,
        "embeddingProvider": provider.provider_id,
        "embeddingModel": provider.model_id if candidate.vector_score is not None else None,
        "embeddingDimensions": (
            provider.dimensions if candidate.vector_score is not None else None
        ),
        "vectorFieldCount": candidate.vector_field_count,
        "vectorRank": candidate.vector_rank,
        "lexicalRank": candidate.lexical_rank,
        "vectorFields": [
            {
                "field": hit.field,
                "rawScore": hit.raw_score,
                "normalizedScore": hit.normalized_score,
                "rank": hit.rank,
                "weight": hit.weight,
                "distanceFunction": hit.distance_function,
            }
            for hit in sorted(
                candidate.vector_hits.values(),
                key=lambda item: item.field,
            )
        ],
    }
    score_components: dict[str, float] = {"final": candidate.score}
    if candidate.vector_score is not None:
        score_components["vector"] = candidate.vector_score
        score_components["vectorNormalized"] = (
            candidate.vector_normalized_score or 0.0
        )
    if candidate.lexical_diagnostics is not None:
        score_components.update(candidate.lexical_diagnostics.score_components)
        details.update(candidate.lexical_diagnostics.details)
    reasons = ["result.identity.record", f"mode.{mode}"]
    if candidate.vector_score is not None:
        reasons.extend(
            [
                "candidate.source.vector",
                "score.vector.raw_similarity",
                "score.vector.normalized",
            ]
        )
        if candidate.vector_field_count > 1:
            reasons.extend(["fusion.multi_vector", "score.vector.weighted_field_fusion"])
    if candidate.lexical_score is not None:
        reasons.extend(["candidate.source.lexical", "score.lexical"])
    if mode == "hybrid":
        fusion = (hybrid or HybridSearchOptions()).fusion.strip().lower()
        reasons.append("fusion.rrf" if fusion == "rrf" else "fusion.weighted")
        details["hybridFusion"] = fusion
    return RetrievalDiagnostics(
        result_identity={
            "collection": candidate.collection,
            "partitionKey": candidate.record.partition_key,
            "id": candidate.record.id,
            "type": candidate.record.type,
            "etag": candidate.record.etag,
            "revision": candidate.record.revision,
        },
        score_components=score_components,
        score_normalization={
            "finalScoreKind": (
                "lexical.score"
                if mode == "lexical"
                else "hybrid.rrf"
                if mode == "hybrid"
                and (hybrid or HybridSearchOptions()).fusion.strip().lower() == "rrf"
                else "hybrid.weighted_normalized"
                if mode == "hybrid"
                else "vector.multi_field_weighted_normalized"
                if candidate.vector_field_count > 1
                else "vector.raw_similarity"
            ),
            "vectorScoreKind": (
                "vector.multi_field_weighted_normalized"
                if candidate.vector_field_count > 1
                else "vector.raw_similarity"
                if candidate.vector_score is not None
                else None
            ),
            "lexicalScoreKind": (
                "lexical.score" if candidate.lexical_score is not None else None
            ),
            "hybridFusion": (
                (hybrid or HybridSearchOptions()).fusion.strip().lower()
                if mode == "hybrid"
                else None
            ),
            "vectorDistanceFunction": None,
            "vectorNormalization": None,
            "weights": {},
            "parameters": {},
        },
        candidate_sources=tuple(sorted(candidate.candidate_sources)),
        candidate_counts={
            "collectionVectorCandidates": vector_count,
            "collectionLexicalCandidates": lexical_count,
            "collectionMergedCandidates": merged_count,
            "retrievalCandidateLimit": result_limit,
            "collectionVectorCandidateFields": len(vector_counts),
            **{
                f"collectionVectorCandidates.{field}": count
                for field, count in vector_counts.items()
            },
        },
        reason_codes=tuple(dict.fromkeys(reasons)),
        matched_fields=(
            candidate.lexical_diagnostics.matched_fields
            if candidate.lexical_diagnostics is not None
            else ()
        ),
        matched_terms=(
            candidate.lexical_diagnostics.matched_terms
            if candidate.lexical_diagnostics is not None
            else ()
        ),
        details=details,
    )


def _stamp_rank(
    diagnostics: RetrievalDiagnostics | None,
    candidate: _Candidate,
    rank: int,
    returned: int,
) -> RetrievalDiagnostics | None:
    if diagnostics is None:
        return None
    details = dict(diagnostics.details)
    details.update(
        {
            "rank": rank,
            "tieBreakOrder": "score desc, collection asc, partitionKey asc, id asc",
            "tieBreakKey": {
                "score": candidate.score,
                "collection": candidate.collection,
                "partitionKey": candidate.record.partition_key,
                "id": candidate.record.id,
            },
        }
    )
    counts = dict(diagnostics.candidate_counts)
    counts["returnedCandidates"] = returned
    reasons = tuple(
        dict.fromkeys(
            (
                *diagnostics.reason_codes,
                "rank.final.assigned",
                "rank.tie_break.applied",
            )
        )
    )
    return replace(
        diagnostics,
        details=details,
        candidate_counts=counts,
        reason_codes=reasons,
    )


def _with_rerank_diagnostics(
    diagnostics: RetrievalDiagnostics,
    original: float,
    rerank_score: float,
    provider: str,
) -> RetrievalDiagnostics:
    components = dict(diagnostics.score_components)
    components.update(
        {
            "preRerank": original,
            "rerank": rerank_score,
        }
    )
    details = dict(diagnostics.details)
    details.update(
        {
            "rerankProvider": provider,
            "rerankApplied": True,
        }
    )
    return replace(
        diagnostics,
        score_components=components,
        details=details,
        reason_codes=tuple(
            dict.fromkeys((*diagnostics.reason_codes, "rerank.applied"))
        ),
    )


def _candidate_id(candidate: _Candidate) -> str:
    return (
        candidate.collection
        + "\x1f"
        + candidate.record.partition_key
        + "\x1f"
        + candidate.record.id
    )


def _rerank_text(record: VyralRecord, options: RerankOptions) -> str:
    content = record.content or {}
    if options.content_field:
        selected = content.get(options.content_field)
        if isinstance(selected, str):
            return selected
    text = content.get("text")
    if isinstance(text, str):
        return text
    return record.id


def _snippet(record: VyralRecord) -> str:
    text = (record.content or {}).get("text")
    if not isinstance(text, str):
        return ""
    return text if len(text) <= 200 else text[:197] + "..."


def _tokens(text: str) -> tuple[str, ...]:
    output: list[str] = []
    builder: list[str] = []
    for character in text.lower():
        if character.isalnum():
            builder.append(character)
        elif builder:
            output.append("".join(builder))
            builder.clear()
    if builder:
        output.append("".join(builder))
    return tuple(output)
