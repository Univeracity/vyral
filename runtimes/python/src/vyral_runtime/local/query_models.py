from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass, field
import math
from typing import Any, Mapping, Sequence

from .models import JSONObject, VyralRecord, _integer, _mapping, _optional_text, _text


def _optional_number(value: object, name: str) -> float | None:
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError(f"{name} must be a number")
    result = float(value)
    if not math.isfinite(result):
        raise ValueError(f"{name} must be finite")
    return result


def _number(value: object, name: str, default: float) -> float:
    result = _optional_number(value, name)
    return default if result is None else result


def _boolean(value: object, name: str, default: bool = False) -> bool:
    if value is None:
        return default
    if not isinstance(value, bool):
        raise TypeError(f"{name} must be a boolean")
    return value


def _string_array(value: object, name: str) -> tuple[str, ...] | None:
    if value is None:
        return None
    if not isinstance(value, (list, tuple)) or not all(
        isinstance(item, str) for item in value
    ):
        raise TypeError(f"{name} must be an array of strings")
    return tuple(value)


@dataclass(frozen=True)
class FilterNode:
    path: str | None = None
    op: str | None = None
    value: Any = None
    combine: str | None = None
    children: tuple["FilterNode", ...] | None = None

    @classmethod
    def from_value(cls, value: FilterNode | Mapping[str, Any]) -> FilterNode:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "filter")
        raw_children = item.get("children")
        if raw_children is not None and not isinstance(raw_children, (list, tuple)):
            raise TypeError("filter children must be an array")
        return cls(
            path=_optional_text(item.get("path"), "filter path"),
            op=_optional_text(item.get("op"), "filter op"),
            value=deepcopy(item.get("value")),
            combine=_optional_text(item.get("combine"), "filter combine"),
            children=(
                tuple(cls.from_value(child) for child in raw_children)
                if raw_children is not None
                else None
            ),
        )

    def to_dict(self) -> JSONObject:
        return {
            "path": self.path,
            "op": self.op,
            "value": deepcopy(self.value),
            "combine": self.combine,
            "children": (
                [child.to_dict() for child in self.children]
                if self.children is not None
                else None
            ),
        }


@dataclass(frozen=True)
class OrderExpression:
    path: str
    direction: str = "asc"

    @classmethod
    def from_value(
        cls,
        value: OrderExpression | Mapping[str, Any],
    ) -> OrderExpression:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "order expression")
        return cls(
            path=_text(item.get("path"), "order path"),
            direction=_text(item.get("direction"), "order direction", "asc"),
        )

    def to_dict(self) -> JSONObject:
        return {"path": self.path, "direction": self.direction}


@dataclass(frozen=True)
class VectorSearchOptions:
    field: str
    value: tuple[float, ...]
    top: int = 10
    min_score: float | None = None

    @classmethod
    def from_value(
        cls,
        value: VectorSearchOptions | Mapping[str, Any],
    ) -> VectorSearchOptions:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "vector search")
        raw_vector = item.get("value")
        if not isinstance(raw_vector, (list, tuple)):
            raise TypeError("vector search value must be an array")
        vector: list[float] = []
        for entry in raw_vector:
            if isinstance(entry, bool) or not isinstance(entry, (int, float)):
                raise TypeError("vector search value must contain only numbers")
            vector.append(float(entry))
        return cls(
            field=_text(item.get("field"), "vector search field"),
            value=tuple(vector),
            top=_integer(item.get("top"), "vector search top", 10),
            min_score=_optional_number(item.get("minScore"), "vector search minScore"),
        )

    def to_dict(self) -> JSONObject:
        return {
            "field": self.field,
            "value": list(self.value),
            "top": self.top,
            "minScore": self.min_score,
        }


@dataclass(frozen=True)
class LexicalSearchOptions:
    query: str = ""
    fields: tuple[str, ...] | None = None
    top: int = 50
    scan_limit: int | None = 5000
    min_score: float | None = None
    scoring: str = "bm25"
    match_mode: str = "any"
    field_boosts: Mapping[str, float] | None = None
    bm25_k1: float = 1.2
    bm25_b: float = 0.75
    phrase_boost: float = 0.15
    exact_boost: float = 0.25
    metadata_boost: float = 0.10
    prefix_matching: bool = False
    prefix_min_chars: int = 3
    required_phrase_groups: tuple[tuple[str, ...], ...] | None = None

    @classmethod
    def from_value(
        cls,
        value: LexicalSearchOptions | Mapping[str, Any],
    ) -> LexicalSearchOptions:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "lexical search")
        raw_boosts = item.get("fieldBoosts")
        if raw_boosts is not None and not isinstance(raw_boosts, Mapping):
            raise TypeError("lexical search fieldBoosts must be an object")
        boosts: dict[str, float] | None = None
        if raw_boosts is not None:
            boosts = {}
            for path, raw_boost in raw_boosts.items():
                if not isinstance(path, str):
                    raise TypeError("lexical search fieldBoosts keys must be strings")
                boosts[path] = _number(
                    raw_boost,
                    f"lexical field boost {path}",
                    1.0,
                )
        raw_groups = item.get("requiredPhraseGroups")
        if raw_groups is not None and not isinstance(raw_groups, (list, tuple)):
            raise TypeError("lexical requiredPhraseGroups must be an array")
        groups: tuple[tuple[str, ...], ...] | None = None
        if raw_groups is not None:
            parsed_groups: list[tuple[str, ...]] = []
            for group in raw_groups:
                parsed = _string_array(group, "lexical required phrase group")
                if parsed is None:
                    raise TypeError("lexical required phrase group must be an array")
                parsed_groups.append(parsed)
            groups = tuple(parsed_groups)
        raw_scan_limit = item.get("scanLimit", 5000)
        return cls(
            query=_text(item.get("query"), "lexical query", ""),
            fields=_string_array(item.get("fields"), "lexical fields"),
            top=_integer(item.get("top"), "lexical top", 50),
            scan_limit=(
                None
                if raw_scan_limit is None
                else _integer(raw_scan_limit, "lexical scanLimit")
            ),
            min_score=_optional_number(item.get("minScore"), "lexical minScore"),
            scoring=_text(item.get("scoring"), "lexical scoring", "bm25"),
            match_mode=_text(item.get("matchMode"), "lexical matchMode", "any"),
            field_boosts=boosts,
            bm25_k1=_number(item.get("bm25K1"), "lexical bm25K1", 1.2),
            bm25_b=_number(item.get("bm25B"), "lexical bm25B", 0.75),
            phrase_boost=_number(item.get("phraseBoost"), "lexical phraseBoost", 0.15),
            exact_boost=_number(item.get("exactBoost"), "lexical exactBoost", 0.25),
            metadata_boost=_number(
                item.get("metadataBoost"),
                "lexical metadataBoost",
                0.10,
            ),
            prefix_matching=_boolean(
                item.get("prefixMatching"),
                "lexical prefixMatching",
            ),
            prefix_min_chars=_integer(
                item.get("prefixMinChars"),
                "lexical prefixMinChars",
                3,
            ),
            required_phrase_groups=groups,
        )

    def to_dict(self) -> JSONObject:
        return {
            "query": self.query,
            "fields": list(self.fields) if self.fields is not None else None,
            "top": self.top,
            "scanLimit": self.scan_limit,
            "minScore": self.min_score,
            "scoring": self.scoring,
            "matchMode": self.match_mode,
            "fieldBoosts": dict(self.field_boosts) if self.field_boosts is not None else None,
            "bm25K1": self.bm25_k1,
            "bm25B": self.bm25_b,
            "phraseBoost": self.phrase_boost,
            "exactBoost": self.exact_boost,
            "metadataBoost": self.metadata_boost,
            "prefixMatching": self.prefix_matching,
            "prefixMinChars": self.prefix_min_chars,
            "requiredPhraseGroups": (
                [list(group) for group in self.required_phrase_groups]
                if self.required_phrase_groups is not None
                else None
            ),
        }


@dataclass(frozen=True)
class QueryEnvelope:
    partition_keys: tuple[str, ...] | None = None
    filter: FilterNode | None = None
    vector: VectorSearchOptions | None = None
    lexical: LexicalSearchOptions | None = None
    order_by: tuple[OrderExpression, ...] | None = None
    limit: int | None = None
    continuation_token: str | None = None

    @classmethod
    def from_value(cls, value: QueryEnvelope | Mapping[str, Any] | None) -> QueryEnvelope:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        item = _mapping(value, "query")
        raw_filter = item.get("filter")
        raw_vector = item.get("vector")
        raw_lexical = item.get("lexical")
        raw_order = item.get("orderBy")
        if raw_order is not None and not isinstance(raw_order, (list, tuple)):
            raise TypeError("query orderBy must be an array")
        raw_limit = item.get("limit")
        return cls(
            partition_keys=_string_array(item.get("partitionKeys"), "query partitionKeys"),
            filter=FilterNode.from_value(raw_filter) if raw_filter is not None else None,
            vector=(
                VectorSearchOptions.from_value(raw_vector)
                if raw_vector is not None
                else None
            ),
            lexical=(
                LexicalSearchOptions.from_value(raw_lexical)
                if raw_lexical is not None
                else None
            ),
            order_by=(
                tuple(OrderExpression.from_value(entry) for entry in raw_order)
                if raw_order is not None
                else None
            ),
            limit=None if raw_limit is None else _integer(raw_limit, "query limit"),
            continuation_token=_optional_text(
                item.get("continuationToken"),
                "query continuationToken",
            ),
        )

    def to_dict(self) -> JSONObject:
        return {
            "partitionKeys": (
                list(self.partition_keys) if self.partition_keys is not None else None
            ),
            "filter": self.filter.to_dict() if self.filter is not None else None,
            "vector": self.vector.to_dict() if self.vector is not None else None,
            "lexical": self.lexical.to_dict() if self.lexical is not None else None,
            "orderBy": (
                [item.to_dict() for item in self.order_by]
                if self.order_by is not None
                else None
            ),
            "limit": self.limit,
            "continuationToken": self.continuation_token,
        }


@dataclass(frozen=True)
class RecordQueryResult:
    items: tuple[VyralRecord, ...]
    continuation_token: str | None = None

    def to_dict(self) -> JSONObject:
        return {
            "items": [record.to_dict() for record in self.items],
            "continuationToken": self.continuation_token,
        }


@dataclass(frozen=True)
class RetrievalDiagnostics:
    result_identity: JSONObject | None = None
    score_components: Mapping[str, float] = field(default_factory=dict)
    score_normalization: JSONObject | None = None
    candidate_sources: tuple[str, ...] = ()
    candidate_counts: Mapping[str, int] = field(default_factory=dict)
    reason_codes: tuple[str, ...] = ()
    matched_fields: tuple[str, ...] = ()
    matched_terms: tuple[str, ...] = ()
    details: JSONObject = field(default_factory=dict)
    schema_version: str = "vyral.retrieval.diagnostics.v1"

    def to_dict(self) -> JSONObject:
        return {
            "schemaVersion": self.schema_version,
            "resultIdentity": deepcopy(self.result_identity),
            "scoreComponents": dict(self.score_components),
            "scoreNormalization": deepcopy(self.score_normalization),
            "candidateSources": list(self.candidate_sources),
            "candidateCounts": dict(self.candidate_counts),
            "reasonCodes": list(self.reason_codes),
            "matchedFields": list(self.matched_fields),
            "matchedTerms": list(self.matched_terms),
            "traceReferences": [],
            "details": deepcopy(self.details),
        }


@dataclass(frozen=True)
class VyralRecordMatch:
    record: VyralRecord
    score: float
    diagnostics: RetrievalDiagnostics | None = None

    def to_dict(self) -> JSONObject:
        return {
            "record": self.record.to_dict(),
            "score": self.score,
            "diagnostics": (
                self.diagnostics.to_dict() if self.diagnostics is not None else None
            ),
        }


@dataclass(frozen=True)
class RecordSearchResult:
    items: tuple[VyralRecordMatch, ...]
    continuation_token: str | None = None

    def to_dict(self) -> JSONObject:
        return {
            "items": [item.to_dict() for item in self.items],
            "continuationToken": self.continuation_token,
        }
