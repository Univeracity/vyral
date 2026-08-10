from __future__ import annotations

from dataclasses import dataclass, field, fields, is_dataclass
from datetime import datetime, timezone
from typing import Any, Mapping, cast

from .._datetime import parse_iso_datetime
from ..local.models import JSONObject


GRAPH_SCHEMA_VERSION = "roman.graph.v1"
GRAPH_RECORD_TYPES = (
    "graph.envelope",
    "graph.node",
    "graph.edge",
    "graph.assertion",
    "graph.review",
    "graph.projection",
)
GRAPH_INDEXED_METADATA = (
    "/type",
    "/metadata/graphKind",
    "/metadata/graphId",
    "/metadata/namespace",
    "/metadata/scopeCollection",
    "/metadata/tenantId",
    "/metadata/graphPartitionKey",
    "/metadata/subjectId",
    "/metadata/subjectKind",
    "/metadata/nodeId",
    "/metadata/nodeType",
    "/metadata/edgeId",
    "/metadata/sourceId",
    "/metadata/targetId",
    "/metadata/predicate",
    "/metadata/assertionId",
    "/metadata/assertionStatus",
    "/metadata/reviewId",
    "/metadata/reviewStatus",
    "/metadata/projectionId",
)


class GraphModel:
    def to_dict(self) -> JSONObject:
        return cast(JSONObject, _wire(self))


def _wire(value: Any) -> Any:
    if is_dataclass(value) and not isinstance(value, type):
        return {
            _camel(field.name): _wire(getattr(value, field.name))
            for field in fields(value)
            if field.name != "additional_properties"
        } | {
            str(key): _wire(item)
            for key, item in getattr(value, "additional_properties", {}).items()
        }
    if isinstance(value, datetime):
        return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
    if isinstance(value, Mapping):
        return {str(key): _wire(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [_wire(item) for item in value]
    return value


def _camel(value: str) -> str:
    head, *tail = value.split("_")
    return head + "".join(item[:1].upper() + item[1:] for item in tail)


def _item(value: object, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise TypeError(f"{name} must be an object")
    return value


def _value(item: Mapping[str, Any], camel: str, snake: str) -> object:
    return item[camel] if camel in item else item.get(snake)


def _text(value: object, name: str, default: str = "") -> str:
    if value is None:
        return default
    if not isinstance(value, str):
        raise TypeError(f"{name} must be a string")
    return value


def _optional_text(value: object, name: str) -> str | None:
    return None if value is None else _text(value, name)


def _integer(value: object, name: str, default: int) -> int:
    if value is None:
        return default
    if isinstance(value, bool) or not isinstance(value, int):
        raise TypeError(f"{name} must be an integer")
    return value


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
        isinstance(entry, str) for entry in value
    ):
        raise TypeError(f"{name} must be an array of strings")
    return tuple(value)


def _json(value: object, name: str) -> JSONObject | None:
    return None if value is None else dict(_item(value, name))


def _date(value: object, name: str) -> datetime | None:
    if value is None or isinstance(value, datetime):
        selected = value
    elif isinstance(value, str):
        selected = parse_iso_datetime(value)
    else:
        raise TypeError(f"{name} must be an ISO timestamp")
    if selected is not None and selected.tzinfo is None:
        raise ValueError(f"{name} must include an offset")
    return selected


@dataclass(frozen=True)
class VyralGraphScope(GraphModel):
    graph_id: str = "default"
    namespace: str = "default"
    collection: str = "default"
    tenant_id: str = ""
    partition_key: str = ""
    additional_properties: JSONObject = field(default_factory=dict)

    @classmethod
    def from_value(cls, value: VyralGraphScope | Mapping[str, Any]) -> VyralGraphScope:
        if isinstance(value, cls):
            return value
        item = _item(value, "graph scope")
        known = {"graphId", "graph_id", "namespace", "collection", "tenantId", "tenant_id", "partitionKey", "partition_key"}
        return cls(
            graph_id=_text(_value(item, "graphId", "graph_id"), "graphId", "default"),
            namespace=_text(item.get("namespace"), "namespace", "default"),
            collection=_text(item.get("collection"), "collection", "default"),
            tenant_id=_text(_value(item, "tenantId", "tenant_id"), "tenantId"),
            partition_key=_text(_value(item, "partitionKey", "partition_key"), "partitionKey"),
            additional_properties={str(key): entry for key, entry in item.items() if key not in known},
        )

    def resolved_partition_key(self) -> str:
        if self.partition_key.strip():
            return self.partition_key.strip()
        if self.tenant_id.strip():
            return f"tenant:{self.tenant_id.strip()}"
        return f"graph:{self.graph_id.strip() or 'default'}"


@dataclass(frozen=True)
class VyralGraphSourceSpan(GraphModel):
    source_ref: str
    char_start: int | None = None
    char_end: int | None = None
    unit: str = "utf16"
    locator: str | None = None
    text_hash: str | None = None
    metadata: JSONObject | None = None

    @classmethod
    def from_value(cls, value: VyralGraphSourceSpan | Mapping[str, Any]) -> VyralGraphSourceSpan:
        if isinstance(value, cls):
            return value
        item = _item(value, "graph source span")
        start = _value(item, "charStart", "start")
        end = _value(item, "charEnd", "end")
        return cls(
            source_ref=_text(_value(item, "sourceRef", "source_ref"), "sourceRef"),
            char_start=None if start is None else _integer(start, "charStart", 0),
            char_end=None if end is None else _integer(end, "charEnd", 0),
            unit=_text(item.get("unit"), "unit", "utf16") or "utf16",
            locator=_optional_text(item.get("locator"), "locator"),
            text_hash=_optional_text(_value(item, "textHash", "text_hash"), "textHash"),
            metadata=_json(item.get("metadata"), "span metadata"),
        )


def _spans(value: object) -> tuple[VyralGraphSourceSpan, ...]:
    if value is None:
        return ()
    if not isinstance(value, (list, tuple)):
        raise TypeError("sourceSpans must be an array")
    return tuple(VyralGraphSourceSpan.from_value(entry) for entry in value)


@dataclass(frozen=True)
class VyralGraphNode(GraphModel):
    id: str
    type: str
    label: str | None = None
    properties: JSONObject | None = None
    source_spans: tuple[VyralGraphSourceSpan, ...] = ()
    assertion_ids: tuple[str, ...] = ()
    created_at: datetime | None = None
    updated_at: datetime | None = None

    @classmethod
    def from_value(cls, value: VyralGraphNode | Mapping[str, Any]) -> VyralGraphNode:
        if isinstance(value, cls):
            return value
        item = _item(value, "graph node")
        return cls(
            id=_text(_value(item, "id", "node_id"), "node id"),
            type=_text(_value(item, "type", "node_type"), "node type"),
            label=_optional_text(item.get("label"), "node label"),
            properties=_json(item.get("properties"), "node properties"),
            source_spans=_spans(_value(item, "sourceSpans", "source_spans")),
            assertion_ids=_strings(_value(item, "assertionIds", "assertion_ids"), "assertionIds"),
            created_at=_date(_value(item, "createdAt", "created_at"), "createdAt"),
            updated_at=_date(_value(item, "updatedAt", "updated_at"), "updatedAt"),
        )


@dataclass(frozen=True)
class VyralGraphEdge(GraphModel):
    id: str
    source_id: str
    target_id: str
    predicate: str
    label: str | None = None
    properties: JSONObject | None = None
    source_spans: tuple[VyralGraphSourceSpan, ...] = ()
    assertion_ids: tuple[str, ...] = ()
    created_at: datetime | None = None
    updated_at: datetime | None = None

    @classmethod
    def from_value(cls, value: VyralGraphEdge | Mapping[str, Any]) -> VyralGraphEdge:
        if isinstance(value, cls):
            return value
        item = _item(value, "graph edge")
        return cls(
            id=_text(_value(item, "id", "edge_id"), "edge id"),
            source_id=_text(_value(item, "sourceId", "source_id"), "sourceId"),
            target_id=_text(_value(item, "targetId", "target_id"), "targetId"),
            predicate=_text(item.get("predicate"), "predicate"),
            label=_optional_text(item.get("label"), "edge label"),
            properties=_json(item.get("properties"), "edge properties"),
            source_spans=_spans(_value(item, "sourceSpans", "source_spans")),
            assertion_ids=_strings(_value(item, "assertionIds", "assertion_ids"), "assertionIds"),
            created_at=_date(_value(item, "createdAt", "created_at"), "createdAt"),
            updated_at=_date(_value(item, "updatedAt", "updated_at"), "updatedAt"),
        )


@dataclass(frozen=True)
class VyralGraphAssertion(GraphModel):
    id: str
    subject_id: str
    subject_kind: str = "node"
    status: str = "proposed"
    method: str = "unspecified"
    actor: str = "system"
    confidence: float | None = None
    source_spans: tuple[VyralGraphSourceSpan, ...] = ()
    properties: JSONObject | None = None
    created_at: datetime | None = None

    @classmethod
    def from_value(cls, value: VyralGraphAssertion | Mapping[str, Any]) -> VyralGraphAssertion:
        if isinstance(value, cls):
            return value
        item = _item(value, "graph assertion")
        confidence = item.get("confidence")
        if confidence is not None and (
            isinstance(confidence, bool) or not isinstance(confidence, (int, float))
        ):
            raise TypeError("assertion confidence must be a number")
        return cls(
            id=_text(_value(item, "id", "assertion_id"), "assertion id"),
            subject_id=_text(_value(item, "subjectId", "subject_id"), "subjectId"),
            subject_kind=_text(_value(item, "subjectKind", "subject_kind"), "subjectKind", "node"),
            status=_text(item.get("status"), "assertion status", "proposed"),
            method=_text(item.get("method"), "assertion method", "unspecified"),
            actor=_text(item.get("actor"), "assertion actor", "system"),
            confidence=float(confidence) if confidence is not None else None,
            source_spans=_spans(_value(item, "sourceSpans", "source_spans")),
            properties=_json(item.get("properties"), "assertion properties"),
            created_at=_date(_value(item, "createdAt", "created_at"), "createdAt"),
        )


@dataclass(frozen=True)
class VyralGraphReviewEvent(GraphModel):
    id: str
    subject_id: str
    subject_kind: str = "assertion"
    status: str = ""
    reviewer: str = ""
    notes: str | None = None
    properties: JSONObject | None = None
    created_at: datetime | None = None

    @classmethod
    def from_value(cls, value: VyralGraphReviewEvent | Mapping[str, Any]) -> VyralGraphReviewEvent:
        if isinstance(value, cls):
            return value
        item = _item(value, "graph review")
        return cls(
            id=_text(_value(item, "id", "review_id"), "review id"),
            subject_id=_text(_value(item, "subjectId", "subject_id"), "subjectId"),
            subject_kind=_text(_value(item, "subjectKind", "subject_kind"), "subjectKind", "assertion"),
            status=_text(item.get("status"), "review status"),
            reviewer=_text(item.get("reviewer"), "reviewer"),
            notes=_optional_text(item.get("notes"), "review notes"),
            properties=_json(item.get("properties"), "review properties"),
            created_at=_date(_value(item, "createdAt", "created_at"), "createdAt"),
        )


@dataclass(frozen=True)
class VyralGraphTraversalProfile(GraphModel):
    id: str = "default"
    direction: str = "both"
    max_depth: int = 1
    predicates: tuple[str, ...] = ()
    node_types: tuple[str, ...] = ()
    limit: int = 100
    edge_limit: int = 100
    include_start: bool = True
    review_statuses: tuple[str, ...] = ()
    assertion_statuses: tuple[str, ...] = ()
    require_source_grounding: bool = False
    min_score: float | None = None
    include_path_explanations: bool = True

    @classmethod
    def from_value(cls, value: VyralGraphTraversalProfile | Mapping[str, Any] | None) -> VyralGraphTraversalProfile:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        item = _item(value, "graph traversal profile")
        score = _value(item, "minScore", "min_score")
        if score is not None and (
            isinstance(score, bool) or not isinstance(score, (int, float))
        ):
            raise TypeError("minScore must be a number")
        return cls(
            id=_text(item.get("id"), "profile id", "default"),
            direction=_text(item.get("direction"), "direction", "both"),
            max_depth=_integer(_value(item, "maxDepth", "max_depth"), "maxDepth", 1),
            predicates=_strings(item.get("predicates"), "predicates"),
            node_types=_strings(_value(item, "nodeTypes", "node_types"), "nodeTypes"),
            limit=_integer(item.get("limit"), "limit", 100),
            edge_limit=_integer(_value(item, "edgeLimit", "edge_limit"), "edgeLimit", 100),
            include_start=_boolean(_value(item, "includeStart", "include_start"), "includeStart", True),
            review_statuses=_strings(_value(item, "reviewStatuses", "review_statuses"), "reviewStatuses"),
            assertion_statuses=_strings(_value(item, "assertionStatuses", "assertion_statuses"), "assertionStatuses"),
            require_source_grounding=_boolean(_value(item, "requireSourceGrounding", "require_source_grounding"), "requireSourceGrounding", False),
            min_score=float(score) if score is not None else None,
            include_path_explanations=_boolean(_value(item, "includePathExplanations", "include_path_explanations"), "includePathExplanations", True),
        )


@dataclass(frozen=True)
class VyralGraphProjection(GraphModel):
    id: str
    profile: VyralGraphTraversalProfile = VyralGraphTraversalProfile()
    start_node_ids: tuple[str, ...] = ()
    nodes: tuple[VyralGraphNode, ...] = ()
    edges: tuple[VyralGraphEdge, ...] = ()
    diagnostics: JSONObject | None = None
    created_at: datetime | None = None

    @classmethod
    def from_value(cls, value: VyralGraphProjection | Mapping[str, Any]) -> VyralGraphProjection:
        if isinstance(value, cls):
            return value
        item = _item(value, "graph projection")
        raw_nodes = item.get("nodes", ())
        raw_edges = item.get("edges", ())
        if not isinstance(raw_nodes, (list, tuple)) or not isinstance(raw_edges, (list, tuple)):
            raise TypeError("projection nodes and edges must be arrays")
        return cls(
            id=_text(_value(item, "id", "projection_id"), "projection id"),
            profile=VyralGraphTraversalProfile.from_value(item.get("profile")),
            start_node_ids=_strings(_value(item, "startNodeIds", "start_node_ids"), "startNodeIds"),
            nodes=tuple(VyralGraphNode.from_value(entry) for entry in raw_nodes),
            edges=tuple(VyralGraphEdge.from_value(entry) for entry in raw_edges),
            diagnostics=_json(item.get("diagnostics"), "projection diagnostics"),
            created_at=_date(_value(item, "createdAt", "created_at"), "createdAt"),
        )


@dataclass(frozen=True)
class VyralGraphEnvelope(GraphModel):
    scope: VyralGraphScope
    schema: str = GRAPH_SCHEMA_VERSION
    metadata: JSONObject | None = None
    nodes: tuple[VyralGraphNode, ...] = ()
    edges: tuple[VyralGraphEdge, ...] = ()
    assertions: tuple[VyralGraphAssertion, ...] = ()
    reviews: tuple[VyralGraphReviewEvent, ...] = ()
    projections: tuple[VyralGraphProjection, ...] = ()

    @classmethod
    def from_value(cls, value: VyralGraphEnvelope | Mapping[str, Any]) -> VyralGraphEnvelope:
        if isinstance(value, cls):
            return value
        item = _item(value, "graph envelope")
        def entries(name: str) -> tuple[Any, ...]:
            selected = item.get(name, ())
            if not isinstance(selected, (list, tuple)):
                raise TypeError(f"graph {name} must be an array")
            return tuple(selected)
        return cls(
            schema=_text(item.get("schema"), "graph schema", GRAPH_SCHEMA_VERSION),
            scope=VyralGraphScope.from_value(_item(item.get("scope", {}), "graph scope")),
            metadata=_json(item.get("metadata"), "graph metadata"),
            nodes=tuple(VyralGraphNode.from_value(entry) for entry in entries("nodes")),
            edges=tuple(VyralGraphEdge.from_value(entry) for entry in entries("edges")),
            assertions=tuple(VyralGraphAssertion.from_value(entry) for entry in entries("assertions")),
            reviews=tuple(VyralGraphReviewEvent.from_value(entry) for entry in entries("reviews")),
            projections=tuple(VyralGraphProjection.from_value(entry) for entry in entries("projections")),
        )
