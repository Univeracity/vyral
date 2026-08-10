from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Any, Mapping

from ..local.models import JSONObject, VyralRecord
from ..local.query_models import (
    FilterNode,
    LexicalSearchOptions,
    RetrievalDiagnostics,
)


def _text(value: object, name: str, default: str | None = None) -> str:
    if value is None and default is not None:
        return default
    if not isinstance(value, str):
        raise TypeError(f"{name} must be a string")
    return value


def _optional_text(value: object, name: str) -> str | None:
    return None if value is None else _text(value, name)


def _integer(value: object, name: str, default: int | None = None) -> int:
    if value is None and default is not None:
        return default
    if isinstance(value, bool) or not isinstance(value, int):
        raise TypeError(f"{name} must be an integer")
    return value


def _optional_integer(value: object, name: str) -> int | None:
    return None if value is None else _integer(value, name)


def _number(value: object, name: str, default: float | None = None) -> float:
    if value is None and default is not None:
        return default
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError(f"{name} must be a number")
    result = float(value)
    if not math.isfinite(result):
        raise ValueError(f"{name} must be finite")
    return result


def _optional_number(value: object, name: str) -> float | None:
    return None if value is None else _number(value, name)


def _boolean(value: object, name: str, default: bool) -> bool:
    if value is None:
        return default
    if not isinstance(value, bool):
        raise TypeError(f"{name} must be a boolean")
    return value


@dataclass(frozen=True)
class EmbeddingOptions:
    field: str | None = None
    purpose: str | None = None
    query_prefix: str | None = None
    passage_prefix: str | None = None
    symmetric_prefix: str | None = None

    @classmethod
    def from_value(
        cls,
        value: EmbeddingOptions | Mapping[str, Any] | None,
    ) -> EmbeddingOptions | None:
        if value is None or isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("retrieval embedding must be an object")
        return cls(
            field=_optional_text(value.get("field"), "retrieval embedding field"),
            purpose=_optional_text(value.get("purpose"), "retrieval embedding purpose"),
            query_prefix=_optional_text(
                value.get("queryPrefix"),
                "retrieval embedding queryPrefix",
            ),
            passage_prefix=_optional_text(
                value.get("passagePrefix"),
                "retrieval embedding passagePrefix",
            ),
            symmetric_prefix=_optional_text(
                value.get("symmetricPrefix"),
                "retrieval embedding symmetricPrefix",
            ),
        )

    def to_dict(self) -> JSONObject:
        return {
            "field": self.field,
            "purpose": self.purpose,
            "queryPrefix": self.query_prefix,
            "passagePrefix": self.passage_prefix,
            "symmetricPrefix": self.symmetric_prefix,
        }


@dataclass(frozen=True)
class HybridSearchOptions:
    vector_weight: float = 0.55
    lexical_weight: float = 0.45
    candidate_multiplier: int = 8
    vector_candidate_limit: int | None = None
    lexical_candidate_limit: int | None = None
    fusion: str = "weighted"
    rrf_k: int = 60

    @classmethod
    def from_value(
        cls,
        value: HybridSearchOptions | Mapping[str, Any] | None,
    ) -> HybridSearchOptions | None:
        if value is None or isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("hybrid search must be an object")
        return cls(
            vector_weight=_number(
                value.get("vectorWeight"),
                "hybrid vectorWeight",
                0.55,
            ),
            lexical_weight=_number(
                value.get("lexicalWeight"),
                "hybrid lexicalWeight",
                0.45,
            ),
            candidate_multiplier=_integer(
                value.get("candidateMultiplier"),
                "hybrid candidateMultiplier",
                8,
            ),
            vector_candidate_limit=_optional_integer(
                value.get("vectorCandidateLimit"),
                "hybrid vectorCandidateLimit",
            ),
            lexical_candidate_limit=_optional_integer(
                value.get("lexicalCandidateLimit"),
                "hybrid lexicalCandidateLimit",
            ),
            fusion=_text(value.get("fusion"), "hybrid fusion", "weighted"),
            rrf_k=_integer(value.get("rrfK"), "hybrid rrfK", 60),
        )

    def to_dict(self) -> JSONObject:
        return {
            "vectorWeight": self.vector_weight,
            "lexicalWeight": self.lexical_weight,
            "candidateMultiplier": self.candidate_multiplier,
            "vectorCandidateLimit": self.vector_candidate_limit,
            "lexicalCandidateLimit": self.lexical_candidate_limit,
            "fusion": self.fusion,
            "rrfK": self.rrf_k,
        }


@dataclass(frozen=True)
class RetrievalVectorFieldQuery:
    field: str
    weight: float = 1.0
    query: str | None = None
    embedding: EmbeddingOptions | None = None
    candidate_limit: int | None = None
    min_score: float | None = None

    @classmethod
    def from_value(
        cls,
        value: RetrievalVectorFieldQuery | Mapping[str, Any],
    ) -> RetrievalVectorFieldQuery:
        if isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("retrieval vector field must be an object")
        return cls(
            field=_text(value.get("field"), "retrieval vector field"),
            weight=_number(value.get("weight"), "retrieval vector field weight", 1.0),
            query=_optional_text(value.get("query"), "retrieval vector field query"),
            embedding=EmbeddingOptions.from_value(value.get("embedding")),
            candidate_limit=_optional_integer(
                value.get("candidateLimit"),
                "retrieval vector field candidateLimit",
            ),
            min_score=_optional_number(
                value.get("minScore"),
                "retrieval vector field minScore",
            ),
        )

    def to_dict(self) -> JSONObject:
        return {
            "field": self.field,
            "weight": self.weight,
            "query": self.query,
            "embedding": (
                self.embedding.to_dict() if self.embedding is not None else None
            ),
            "candidateLimit": self.candidate_limit,
            "minScore": self.min_score,
        }


@dataclass(frozen=True)
class RerankOptions:
    enabled: bool = True
    provider: str | None = None
    mode: str = "advisory"
    candidate_limit: int | None = None
    max_candidate_chars: int = 2000
    content_field: str | None = None
    rerank_score_weight: float = 1.0
    original_score_weight: float = 0.0
    timeout_seconds: int | None = None
    max_output_bytes: int | None = None
    fallback_on_failure: bool = True

    @classmethod
    def from_value(
        cls,
        value: RerankOptions | Mapping[str, Any] | None,
    ) -> RerankOptions | None:
        if value is None or isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("rerank options must be an object")
        return cls(
            enabled=_boolean(value.get("enabled"), "rerank enabled", True),
            provider=_optional_text(value.get("provider"), "rerank provider"),
            mode=_text(value.get("mode"), "rerank mode", "advisory"),
            candidate_limit=_optional_integer(
                value.get("candidateLimit"),
                "rerank candidateLimit",
            ),
            max_candidate_chars=_integer(
                value.get("maxCandidateChars"),
                "rerank maxCandidateChars",
                2000,
            ),
            content_field=_optional_text(
                value.get("contentField"),
                "rerank contentField",
            ),
            rerank_score_weight=_number(
                value.get("rerankScoreWeight"),
                "rerank score weight",
                1.0,
            ),
            original_score_weight=_number(
                value.get("originalScoreWeight"),
                "rerank original score weight",
                0.0,
            ),
            timeout_seconds=_optional_integer(
                value.get("timeoutSeconds"),
                "rerank timeoutSeconds",
            ),
            max_output_bytes=_optional_integer(
                value.get("maxOutputBytes"),
                "rerank maxOutputBytes",
            ),
            fallback_on_failure=_boolean(
                value.get("fallbackOnFailure"),
                "rerank fallbackOnFailure",
                True,
            ),
        )

    def to_dict(self) -> JSONObject:
        return {
            "enabled": self.enabled,
            "provider": self.provider,
            "mode": self.mode,
            "candidateLimit": self.candidate_limit,
            "maxCandidateChars": self.max_candidate_chars,
            "contentField": self.content_field,
            "rerankScoreWeight": self.rerank_score_weight,
            "originalScoreWeight": self.original_score_weight,
            "timeoutSeconds": self.timeout_seconds,
            "maxOutputBytes": self.max_output_bytes,
            "fallbackOnFailure": self.fallback_on_failure,
        }


@dataclass(frozen=True)
class RetrievalRequest:
    query: str
    collections: tuple[str, ...]
    profile: str | None = None
    partition_keys: tuple[str, ...] | None = None
    filter: FilterNode | None = None
    embedding: EmbeddingOptions | None = None
    vector_fields: tuple[RetrievalVectorFieldQuery, ...] | None = None
    search_mode: str | None = None
    lexical: LexicalSearchOptions | None = None
    hybrid: HybridSearchOptions | None = None
    rerank: RerankOptions | None = None
    limit: int = 10
    min_score: float | None = None
    include_trace: bool = False

    @classmethod
    def from_value(
        cls,
        value: RetrievalRequest | Mapping[str, Any],
    ) -> RetrievalRequest:
        if isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("retrieval request must be an object")
        raw_collections = value.get("collections", [])
        if not isinstance(raw_collections, (list, tuple)) or not all(
            isinstance(item, str) for item in raw_collections
        ):
            raise TypeError("retrieval collections must be an array of strings")
        raw_partitions = value.get("partitionKeys")
        if raw_partitions is not None and (
            not isinstance(raw_partitions, (list, tuple))
            or not all(isinstance(item, str) for item in raw_partitions)
        ):
            raise TypeError("retrieval partitionKeys must be an array of strings")
        raw_filter = value.get("filter")
        raw_vectors = value.get("vectorFields")
        if raw_vectors is not None and not isinstance(raw_vectors, (list, tuple)):
            raise TypeError("retrieval vectorFields must be an array")
        raw_lexical = value.get("lexical")
        return cls(
            profile=_optional_text(value.get("profile"), "retrieval profile"),
            query=_text(value.get("query"), "retrieval query", ""),
            collections=tuple(raw_collections),
            partition_keys=(
                tuple(raw_partitions) if raw_partitions is not None else None
            ),
            filter=FilterNode.from_value(raw_filter) if raw_filter is not None else None,
            embedding=EmbeddingOptions.from_value(value.get("embedding")),
            vector_fields=(
                tuple(RetrievalVectorFieldQuery.from_value(item) for item in raw_vectors)
                if raw_vectors is not None
                else None
            ),
            search_mode=_optional_text(
                value.get("searchMode"),
                "retrieval searchMode",
            ),
            lexical=(
                LexicalSearchOptions.from_value(raw_lexical)
                if raw_lexical is not None
                else None
            ),
            hybrid=HybridSearchOptions.from_value(value.get("hybrid")),
            rerank=RerankOptions.from_value(value.get("rerank")),
            limit=_integer(value.get("limit"), "retrieval limit", 10),
            min_score=_optional_number(value.get("minScore"), "retrieval minScore"),
            include_trace=_boolean(
                value.get("includeTrace"),
                "retrieval includeTrace",
                False,
            ),
        )

    def to_dict(self) -> JSONObject:
        output: JSONObject = {
            "profile": self.profile,
            "query": self.query,
            "collections": list(self.collections),
            "partitionKeys": (
                list(self.partition_keys)
                if self.partition_keys is not None
                else None
            ),
            "vectorFields": (
                [field.to_dict() for field in self.vector_fields]
                if self.vector_fields is not None
                else None
            ),
            "limit": self.limit,
            "minScore": self.min_score,
            "includeTrace": self.include_trace,
        }
        if self.filter is not None:
            output["filter"] = self.filter.to_dict()
        if self.embedding is not None:
            output["embedding"] = self.embedding.to_dict()
        if self.search_mode is not None:
            output["searchMode"] = self.search_mode
        if self.lexical is not None:
            output["lexical"] = self.lexical.to_dict()
        if self.hybrid is not None:
            output["hybrid"] = self.hybrid.to_dict()
        if self.rerank is not None:
            output["rerank"] = self.rerank.to_dict()
        return output


@dataclass(frozen=True)
class RetrievalMatch:
    rank: int
    score: float
    collection: str
    snippet: str | None
    record: VyralRecord
    diagnostics: RetrievalDiagnostics | None = None

    def to_dict(self) -> JSONObject:
        return {
            "rank": self.rank,
            "score": self.score,
            "collection": self.collection,
            "snippet": self.snippet,
            "record": self.record.to_dict(),
            "diagnostics": (
                self.diagnostics.to_dict() if self.diagnostics is not None else None
            ),
        }


@dataclass(frozen=True)
class RetrievalResultEnvelope:
    query: str
    results: tuple[RetrievalMatch, ...]
    trace: JSONObject | None = None

    def to_dict(self) -> JSONObject:
        return {
            "query": self.query,
            "results": [result.to_dict() for result in self.results],
            "trace": self.trace,
        }
