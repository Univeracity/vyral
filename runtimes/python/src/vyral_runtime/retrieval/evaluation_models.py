from __future__ import annotations

from dataclasses import dataclass, fields, is_dataclass
import math
from typing import Any, Mapping, cast

from ..local import FilterNode, LexicalSearchOptions
from ..local.models import JSONObject
from .models import (
    EmbeddingOptions,
    HybridSearchOptions,
    RerankOptions,
    RetrievalRequest,
    RetrievalVectorFieldQuery,
)


MAX_EVALUATION_CASES = 200
MAX_EXPECTED_MATCHES = 100
MAX_HARD_NEGATIVES = 200
MAX_EVALUATION_VARIANTS = 12
MAX_EVALUATION_K = 100


class _WireModel:
    def to_dict(self) -> JSONObject:
        return cast(JSONObject, _wire(self))


def _wire(value: Any) -> Any:
    if is_dataclass(value) and not isinstance(value, type):
        return {
            _camel(field.name): _wire(getattr(value, field.name))
            for field in fields(value)
        }
    if isinstance(value, Mapping):
        return {str(key): _wire(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [_wire(item) for item in value]
    return value


def _camel(value: str) -> str:
    head, *tail = value.split("_")
    return head + "".join(item[:1].upper() + item[1:] for item in tail)


def _mapping(value: object, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise TypeError(f"{name} must be an object")
    return value


def _text(value: object, name: str, default: str = "") -> str:
    if value is None:
        return default
    if not isinstance(value, str):
        raise TypeError(f"{name} must be a string")
    return value


def _optional_text(value: object, name: str) -> str | None:
    return None if value is None else _text(value, name)


def _optional_integer(value: object, name: str) -> int | None:
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, int):
        raise TypeError(f"{name} must be an integer")
    return value


def _optional_number(value: object, name: str) -> float | None:
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError(f"{name} must be a number")
    selected = float(value)
    if not math.isfinite(selected):
        raise ValueError(f"{name} must be finite")
    return selected


def _boolean(value: object, name: str, default: bool) -> bool:
    if value is None:
        return default
    if not isinstance(value, bool):
        raise TypeError(f"{name} must be a boolean")
    return value


def _strings(value: object, name: str) -> tuple[str, ...]:
    if value is None:
        return ()
    if not isinstance(value, (list, tuple)) or not all(
        isinstance(item, str) for item in value
    ):
        raise TypeError(f"{name} must be an array of strings")
    return tuple(value)


def _sources(value: object, name: str) -> tuple[JSONObject, ...]:
    if value is None:
        return ()
    if not isinstance(value, (list, tuple)):
        raise TypeError(f"{name} must be an array")
    return tuple(dict(_mapping(item, f"{name} item")) for item in value)


@dataclass(frozen=True)
class RetrievalEvaluationExpectedMatch(_WireModel):
    id: str = ""
    partition_key: str | None = None
    collection: str | None = None
    aliases: tuple[str, ...] = ()
    source_ids: tuple[str, ...] = ()
    sources: tuple[JSONObject, ...] = ()
    relevance: float = 1.0

    @classmethod
    def from_value(
        cls,
        value: RetrievalEvaluationExpectedMatch | Mapping[str, Any],
    ) -> RetrievalEvaluationExpectedMatch:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "retrieval evaluation expected match")
        relevance = _optional_number(item.get("relevance"), "expected relevance")
        return cls(
            id=_text(item.get("id"), "expected id"),
            partition_key=_optional_text(
                item.get("partitionKey"), "expected partitionKey"
            ),
            collection=_optional_text(item.get("collection"), "expected collection"),
            aliases=_strings(item.get("aliases"), "expected aliases"),
            source_ids=_strings(item.get("sourceIds"), "expected sourceIds"),
            sources=_sources(item.get("sources"), "expected sources"),
            relevance=1.0 if relevance is None else relevance,
        )


@dataclass(frozen=True)
class RetrievalEvaluationHardNegativeMatch(_WireModel):
    id: str = ""
    partition_key: str | None = None
    collection: str | None = None
    aliases: tuple[str, ...] = ()
    source_ids: tuple[str, ...] = ()
    sources: tuple[JSONObject, ...] = ()
    reason: str | None = None

    @classmethod
    def from_value(
        cls,
        value: RetrievalEvaluationHardNegativeMatch | Mapping[str, Any],
    ) -> RetrievalEvaluationHardNegativeMatch:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "retrieval evaluation hard negative")
        return cls(
            id=_text(item.get("id"), "hard negative id"),
            partition_key=_optional_text(
                item.get("partitionKey"), "hard negative partitionKey"
            ),
            collection=_optional_text(
                item.get("collection"), "hard negative collection"
            ),
            aliases=_strings(item.get("aliases"), "hard negative aliases"),
            source_ids=_strings(item.get("sourceIds"), "hard negative sourceIds"),
            sources=_sources(item.get("sources"), "hard negative sources"),
            reason=_optional_text(item.get("reason"), "hard negative reason"),
        )


@dataclass(frozen=True)
class RetrievalEvaluationCase(_WireModel):
    request: RetrievalRequest
    expected: tuple[RetrievalEvaluationExpectedMatch, ...]
    name: str | None = None
    hard_negatives: tuple[RetrievalEvaluationHardNegativeMatch, ...] = ()
    k: int | None = None
    metadata: JSONObject | None = None

    @classmethod
    def from_value(
        cls,
        value: RetrievalEvaluationCase | Mapping[str, Any],
    ) -> RetrievalEvaluationCase:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "retrieval evaluation case")
        raw_expected = item.get("expected", ())
        raw_negatives = item.get("hardNegatives", ())
        if not isinstance(raw_expected, (list, tuple)):
            raise TypeError("retrieval evaluation expected must be an array")
        if not isinstance(raw_negatives, (list, tuple)):
            raise TypeError("retrieval evaluation hardNegatives must be an array")
        raw_metadata = item.get("metadata")
        return cls(
            request=RetrievalRequest.from_value(
                _mapping(item.get("request"), "retrieval evaluation case request")
            ),
            expected=tuple(
                RetrievalEvaluationExpectedMatch.from_value(entry)
                for entry in raw_expected
            ),
            name=_optional_text(item.get("name"), "retrieval evaluation case name"),
            hard_negatives=tuple(
                RetrievalEvaluationHardNegativeMatch.from_value(entry)
                for entry in raw_negatives
            ),
            k=_optional_integer(item.get("k"), "retrieval evaluation case k"),
            metadata=(
                dict(_mapping(raw_metadata, "retrieval evaluation case metadata"))
                if raw_metadata is not None
                else None
            ),
        )


@dataclass(frozen=True)
class RetrievalEvaluationRequest(_WireModel):
    cases: tuple[RetrievalEvaluationCase, ...]
    continue_on_error: bool = True
    default_k: int | None = None
    include_top_results: bool = True

    @classmethod
    def from_value(
        cls,
        value: RetrievalEvaluationRequest | Mapping[str, Any],
    ) -> RetrievalEvaluationRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "retrieval evaluation request")
        raw_cases = item.get("cases", ())
        if not isinstance(raw_cases, (list, tuple)):
            raise TypeError("retrieval evaluation cases must be an array")
        return cls(
            cases=tuple(RetrievalEvaluationCase.from_value(case) for case in raw_cases),
            continue_on_error=_boolean(
                item.get("continueOnError"), "continueOnError", True
            ),
            default_k=_optional_integer(item.get("defaultK"), "defaultK"),
            include_top_results=_boolean(
                item.get("includeTopResults"), "includeTopResults", True
            ),
        )


@dataclass(frozen=True)
class RetrievalEvaluationVariant(_WireModel):
    id: str
    label: str | None = None
    profile: str | None = None
    collections: tuple[str, ...] | None = None
    partition_keys: tuple[str, ...] | None = None
    filter: FilterNode | None = None
    embedding: EmbeddingOptions | None = None
    vector_fields: tuple[RetrievalVectorFieldQuery, ...] | None = None
    search_mode: str | None = None
    lexical: LexicalSearchOptions | None = None
    hybrid: HybridSearchOptions | None = None
    rerank: RerankOptions | None = None
    limit: int | None = None
    min_score: float | None = None
    include_trace: bool | None = None

    @classmethod
    def from_value(
        cls,
        value: RetrievalEvaluationVariant | Mapping[str, Any],
    ) -> RetrievalEvaluationVariant:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "retrieval evaluation variant")
        raw_collections = item.get("collections")
        raw_partitions = item.get("partitionKeys")
        raw_vectors = item.get("vectorFields")
        if raw_vectors is not None and not isinstance(raw_vectors, (list, tuple)):
            raise TypeError("variant vectorFields must be an array")
        raw_trace = item.get("includeTrace")
        if raw_trace is not None and not isinstance(raw_trace, bool):
            raise TypeError("variant includeTrace must be a boolean")
        return cls(
            id=_text(item.get("id"), "variant id"),
            label=_optional_text(item.get("label"), "variant label"),
            profile=_optional_text(item.get("profile"), "variant profile"),
            collections=(
                _strings(raw_collections, "variant collections")
                if raw_collections is not None
                else None
            ),
            partition_keys=(
                _strings(raw_partitions, "variant partitionKeys")
                if raw_partitions is not None
                else None
            ),
            filter=(
                FilterNode.from_value(item["filter"])
                if item.get("filter") is not None
                else None
            ),
            embedding=EmbeddingOptions.from_value(item.get("embedding")),
            vector_fields=(
                tuple(RetrievalVectorFieldQuery.from_value(field) for field in raw_vectors)
                if raw_vectors is not None
                else None
            ),
            search_mode=_optional_text(item.get("searchMode"), "variant searchMode"),
            lexical=(
                LexicalSearchOptions.from_value(item["lexical"])
                if item.get("lexical") is not None
                else None
            ),
            hybrid=HybridSearchOptions.from_value(item.get("hybrid")),
            rerank=RerankOptions.from_value(item.get("rerank")),
            limit=_optional_integer(item.get("limit"), "variant limit"),
            min_score=_optional_number(item.get("minScore"), "variant minScore"),
            include_trace=raw_trace,
        )


@dataclass(frozen=True)
class RetrievalEvaluationComparisonRequest(_WireModel):
    cases: tuple[RetrievalEvaluationCase, ...]
    variants: tuple[RetrievalEvaluationVariant, ...]
    continue_on_error: bool = True
    default_k: int | None = None
    include_top_results: bool = False
    include_case_results: bool = False

    @classmethod
    def from_value(
        cls,
        value: RetrievalEvaluationComparisonRequest | Mapping[str, Any],
    ) -> RetrievalEvaluationComparisonRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "retrieval evaluation comparison request")
        raw_cases = item.get("cases", ())
        raw_variants = item.get("variants", ())
        if not isinstance(raw_cases, (list, tuple)):
            raise TypeError("comparison cases must be an array")
        if not isinstance(raw_variants, (list, tuple)):
            raise TypeError("comparison variants must be an array")
        return cls(
            cases=tuple(RetrievalEvaluationCase.from_value(case) for case in raw_cases),
            variants=tuple(
                RetrievalEvaluationVariant.from_value(variant)
                for variant in raw_variants
            ),
            continue_on_error=_boolean(
                item.get("continueOnError"), "continueOnError", True
            ),
            default_k=_optional_integer(item.get("defaultK"), "defaultK"),
            include_top_results=_boolean(
                item.get("includeTopResults"), "includeTopResults", False
            ),
            include_case_results=_boolean(
                item.get("includeCaseResults"), "includeCaseResults", False
            ),
        )


@dataclass(frozen=True)
class RetrievalEvaluationExpectedResult(_WireModel):
    id: str
    partition_key: str | None
    collection: str | None
    relevance: float
    rank: int | None = None
    score: float | None = None


@dataclass(frozen=True)
class RetrievalEvaluationHardNegativeResult(_WireModel):
    id: str
    partition_key: str | None
    collection: str | None
    reason: str | None
    rank: int | None = None
    score: float | None = None


@dataclass(frozen=True)
class RetrievalEvaluationTopResult(_WireModel):
    rank: int
    score: float
    collection: str
    id: str
    partition_key: str
    type: str | None
    matched_expected: bool
    matched_hard_negative: bool
    rerank_fallback_applied: bool = False
    rerank_provider_status: str | None = None
    vector_index_used: bool = False
    vector_index_provider: str | None = None
    vector_index_fields: tuple[str, ...] = ()
    snippet: str | None = None


@dataclass(frozen=True)
class RetrievalEvaluationCaseResult(_WireModel):
    index: int
    name: str | None
    query: str
    status: str
    error: str | None
    duration_ms: float
    k: int
    expected_count: int
    retrieved_count: int = 0
    matched_count: int = 0
    hit: bool = False
    first_relevant_rank: int | None = None
    reciprocal_rank: float = 0.0
    precision_at_k: float = 0.0
    recall_at_k: float = 0.0
    ndcg_at_k: float = 0.0
    hard_negative_count: int = 0
    hard_negative_matched_count: int = 0
    hard_negative_hit: bool = False
    first_hard_negative_rank: int | None = None
    hard_negative_rate_at_k: float = 0.0
    rerank_enabled: bool = False
    rerank_provider: str | None = None
    rerank_trace_id: str | None = None
    rerank_fallback_applied: bool = False
    rerank_failure_class: str | None = None
    rerank_provider_status: str | None = None
    rerank_input_candidate_count: int = 0
    rerank_provider_payload_bytes: int = 0
    rerank_provider_max_input_bytes: int = 0
    expected: tuple[RetrievalEvaluationExpectedResult, ...] = ()
    hard_negatives: tuple[RetrievalEvaluationHardNegativeResult, ...] = ()
    top_results: tuple[RetrievalEvaluationTopResult, ...] = ()


@dataclass(frozen=True)
class RetrievalEvaluationMetrics(_WireModel):
    requested: int = 0
    attempted: int = 0
    succeeded: int = 0
    failed: int = 0
    stopped_on_error: bool = False
    hit_count: int = 0
    hit_rate: float = 0.0
    mean_reciprocal_rank: float = 0.0
    mean_precision_at_k: float = 0.0
    mean_recall_at_k: float = 0.0
    mean_ndcg_at_k: float = 0.0
    hard_negative_case_count: int = 0
    hard_negative_hit_count: int = 0
    hard_negative_hit_rate: float = 0.0
    mean_hard_negative_rate_at_k: float = 0.0
    rerank_case_count: int = 0
    rerank_fallback_case_count: int = 0
    rerank_fallback_rate: float = 0.0


@dataclass(frozen=True)
class RetrievalEvaluationResult(RetrievalEvaluationMetrics):
    cases: tuple[RetrievalEvaluationCaseResult, ...] = ()


@dataclass(frozen=True)
class RetrievalEvaluationMetricDeltas(_WireModel):
    hit_rate: float
    mean_reciprocal_rank: float
    mean_precision_at_k: float
    mean_recall_at_k: float
    mean_ndcg_at_k: float
    hard_negative_hit_rate: float
    mean_hard_negative_rate_at_k: float
    rerank_fallback_rate: float


@dataclass(frozen=True)
class RetrievalEvaluationVariantResult(_WireModel):
    id: str
    label: str | None
    status: str
    error: str | None
    duration_ms: float
    metrics: RetrievalEvaluationMetrics
    delta_from_baseline: RetrievalEvaluationMetricDeltas | None = None
    cases: tuple[RetrievalEvaluationCaseResult, ...] = ()


@dataclass(frozen=True)
class RetrievalEvaluationComparisonResult(_WireModel):
    requested: int
    variants_requested: int
    variants_attempted: int
    variants_succeeded: int
    variants_failed: int
    stopped_on_error: bool
    baseline_variant_id: str | None
    variants: tuple[RetrievalEvaluationVariantResult, ...]
