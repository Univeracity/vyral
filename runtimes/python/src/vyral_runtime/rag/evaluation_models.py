from __future__ import annotations

from dataclasses import dataclass, fields, is_dataclass
from typing import Any, Mapping, cast

from ..local.models import JSONObject
from .context_models import RagContextEnvelope, RagContextRequest


MAX_RAG_EVALUATION_CASES = 100
MAX_EXPECTED_GRAPH_ITEMS = 200


class RagEvaluationModel:
    def to_dict(self) -> JSONObject:
        return cast(JSONObject, _wire(self))


def _wire(value: Any) -> Any:
    if isinstance(value, RagContextEnvelope):
        return value.to_dict()
    if isinstance(value, RagContextRequest):
        return value.to_dict()
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


def _strings(value: object, name: str) -> tuple[str, ...]:
    if value is None:
        return ()
    if not isinstance(value, (list, tuple)) or not all(
        isinstance(item, str) for item in value
    ):
        raise TypeError(f"{name} must be an array of strings")
    return tuple(value)


def _boolean(value: object, name: str, default: bool) -> bool:
    if value is None:
        return default
    if not isinstance(value, bool):
        raise TypeError(f"{name} must be a boolean")
    return value


@dataclass(frozen=True)
class RagContextExpectedGraph(RagEvaluationModel):
    node_ids: tuple[str, ...] = ()
    edge_ids: tuple[str, ...] = ()
    provenance_entity_ids: tuple[str, ...] = ()
    require_source_grounded_provenance: bool = False
    require_graph_context_text: bool = False
    require_context_text_not_truncated: bool = False

    @classmethod
    def from_value(
        cls,
        value: RagContextExpectedGraph | Mapping[str, Any] | None,
    ) -> RagContextExpectedGraph:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        item = _mapping(value, "RAG expected graph")
        return cls(
            node_ids=_strings(item.get("nodeIds"), "expected graph nodeIds"),
            edge_ids=_strings(item.get("edgeIds"), "expected graph edgeIds"),
            provenance_entity_ids=_strings(
                item.get("provenanceEntityIds"),
                "expected graph provenanceEntityIds",
            ),
            require_source_grounded_provenance=_boolean(
                item.get("requireSourceGroundedProvenance"),
                "requireSourceGroundedProvenance",
                False,
            ),
            require_graph_context_text=_boolean(
                item.get("requireGraphContextText"),
                "requireGraphContextText",
                False,
            ),
            require_context_text_not_truncated=_boolean(
                item.get("requireContextTextNotTruncated"),
                "requireContextTextNotTruncated",
                False,
            ),
        )


@dataclass(frozen=True)
class RagContextEvaluationCase(RagEvaluationModel):
    request: RagContextRequest
    expected_graph: RagContextExpectedGraph = RagContextExpectedGraph()
    name: str | None = None
    metadata: JSONObject | None = None

    @classmethod
    def from_value(
        cls,
        value: RagContextEvaluationCase | Mapping[str, Any],
    ) -> RagContextEvaluationCase:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "RAG context evaluation case")
        name = item.get("name")
        if name is not None and not isinstance(name, str):
            raise TypeError("RAG evaluation case name must be a string")
        metadata = item.get("metadata")
        return cls(
            request=RagContextRequest.from_value(
                _mapping(item.get("request"), "RAG evaluation context request")
            ),
            expected_graph=RagContextExpectedGraph.from_value(
                item.get("expectedGraph")
            ),
            name=name,
            metadata=(
                dict(_mapping(metadata, "RAG evaluation metadata"))
                if metadata is not None
                else None
            ),
        )


@dataclass(frozen=True)
class RagContextEvaluationRequest(RagEvaluationModel):
    cases: tuple[RagContextEvaluationCase, ...]
    continue_on_error: bool = True
    include_context: bool = False

    @classmethod
    def from_value(
        cls,
        value: RagContextEvaluationRequest | Mapping[str, Any],
    ) -> RagContextEvaluationRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "RAG context evaluation request")
        cases = item.get("cases", ())
        if not isinstance(cases, (list, tuple)):
            raise TypeError("RAG context evaluation cases must be an array")
        return cls(
            cases=tuple(
                RagContextEvaluationCase.from_value(case) for case in cases
            ),
            continue_on_error=_boolean(
                item.get("continueOnError"), "continueOnError", True
            ),
            include_context=_boolean(
                item.get("includeContext"), "includeContext", False
            ),
        )


@dataclass(frozen=True)
class RagContextGraphEvaluationFailureModes(RagEvaluationModel):
    retrieval_miss: bool = False
    seed_miss: bool = False
    graph_not_found: bool = False
    traversal_empty: bool = False
    expected_node_missing: bool = False
    expected_edge_missing: bool = False
    expected_provenance_missing: bool = False
    source_grounding_failed: bool = False
    graph_context_text_missing: bool = False
    context_text_truncated: bool = False
    budget_truncated: bool = False


@dataclass(frozen=True)
class RagContextGraphEvaluationResult(RagEvaluationModel):
    status: str | None
    expected_node_count: int
    matched_node_count: int
    missing_node_ids: tuple[str, ...]
    expected_edge_count: int
    matched_edge_count: int
    missing_edge_ids: tuple[str, ...]
    expected_provenance_count: int
    matched_provenance_count: int
    missing_provenance_entity_ids: tuple[str, ...]
    source_grounded_provenance_count: int
    source_grounding_satisfied: bool
    graph_context_text_present: bool
    context_text_truncated: bool
    budget_truncated: bool
    failure_modes: RagContextGraphEvaluationFailureModes
    failure_categories: tuple[str, ...]
    passed: bool


@dataclass(frozen=True)
class RagContextEvaluationCaseResult(RagEvaluationModel):
    index: int
    name: str | None
    status: str
    passed: bool
    duration_ms: float
    query_id: str | None
    profile_name: str | None
    expected_anchor_ids: tuple[str, ...]
    retrieved_record_ids: tuple[str, ...]
    graph_expanded_node_ids: tuple[str, ...]
    graph_expanded_edge_ids: tuple[str, ...]
    lexical_contribution_count: int
    vector_contribution_count: int
    graph_contribution_count: int
    failure_categories: tuple[str, ...]
    limit_reasons: tuple[str, ...]
    graph_contribution: JSONObject | None
    graph: RagContextGraphEvaluationResult
    context: RagContextEnvelope | None
    error: str | None = None


@dataclass(frozen=True)
class RagContextEvaluationResult(RagEvaluationModel):
    requested: int
    attempted: int
    succeeded: int
    failed: int
    stopped_on_error: bool
    passed_count: int
    pass_rate: float
    node_hit_rate: float
    edge_hit_rate: float
    provenance_hit_rate: float
    failure_category_counts: Mapping[str, int]
    limit_reason_counts: Mapping[str, int]
    cases: tuple[RagContextEvaluationCaseResult, ...]
