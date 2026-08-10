from __future__ import annotations

from dataclasses import dataclass, fields, is_dataclass
from datetime import datetime, timezone
from typing import Any, Mapping, cast

from ..local import RecordBatchResult
from ..local.models import JSONObject
from .models import (
    GraphModel,
    VyralGraphEnvelope,
    VyralGraphProjection,
    VyralGraphTraversalProfile,
)


class GraphOperationModel:
    def to_dict(self) -> JSONObject:
        return cast(JSONObject, _wire(self))


def _wire(value: Any) -> Any:
    if isinstance(value, GraphModel):
        return value.to_dict()
    if is_dataclass(value) and not isinstance(value, type):
        return {
            _camel(field.name): _wire(getattr(value, field.name))
            for field in fields(value)
        }
    if isinstance(value, datetime):
        return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
    if isinstance(value, Mapping):
        return {str(key): _wire(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [_wire(item) for item in value]
    if hasattr(value, "to_dict"):
        return _wire(value.to_dict())
    return value


def _camel(value: str) -> str:
    head, *tail = value.split("_")
    return head + "".join(item[:1].upper() + item[1:] for item in tail)


def _mapping(value: object, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise TypeError(f"{name} must be an object")
    return value


def _text(value: object, name: str) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise TypeError(f"{name} must be a string")
    return value


def _integer(value: object, name: str) -> int | None:
    if value is None:
        return None
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
        isinstance(item, str) for item in value
    ):
        raise TypeError(f"{name} must be an array of strings")
    return tuple(value)


@dataclass(frozen=True)
class GraphImportRequest(GraphOperationModel):
    envelope: VyralGraphEnvelope
    create_collection_if_missing: bool = True
    replace_existing: bool = False
    continue_on_error: bool = False
    allow_non_graph_policy: bool = False

    @classmethod
    def from_value(
        cls, value: GraphImportRequest | Mapping[str, Any]
    ) -> GraphImportRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "graph import request")
        return cls(
            envelope=VyralGraphEnvelope.from_value(
                _mapping(item.get("envelope"), "graph import envelope")
            ),
            create_collection_if_missing=_boolean(
                item.get("createCollectionIfMissing"),
                "createCollectionIfMissing",
                True,
            ),
            replace_existing=_boolean(
                item.get("replaceExisting"), "replaceExisting", False
            ),
            continue_on_error=_boolean(
                item.get("continueOnError"), "continueOnError", False
            ),
            allow_non_graph_policy=_boolean(
                item.get("allowNonGraphPolicy"), "allowNonGraphPolicy", False
            ),
        )


@dataclass(frozen=True)
class GraphImportPreflightResult(GraphOperationModel):
    collection: str
    generated_at: datetime
    valid: bool
    ready_to_import: bool
    graph_id: str
    namespace: str
    tenant_id: str
    partition_key: str
    collection_exists: bool
    collection_policy_status: str
    would_create_collection: bool
    would_replace_collection: bool
    would_allow_non_graph_policy: bool
    create_collection_if_missing: bool
    replace_existing: bool
    allow_non_graph_policy: bool
    node_count: int
    edge_count: int
    assertion_count: int
    review_count: int
    projection_count: int
    record_count: int
    max_records: int
    warning_count: int
    warnings: tuple[str, ...]
    error_count: int
    errors: tuple[str, ...]


@dataclass(frozen=True)
class GraphImportResult(GraphOperationModel):
    collection: str
    graph_id: str
    partition_key: str
    policy_status: str
    node_count: int
    edge_count: int
    assertion_count: int
    review_count: int
    projection_count: int
    record_count: int
    records: RecordBatchResult


@dataclass(frozen=True)
class GraphExportRequest(GraphOperationModel):
    graph_id: str | None = None
    namespace: str | None = None
    tenant_id: str | None = None
    partition_key: str | None = None
    include_projections: bool = True
    max_records: int | None = None
    fail_on_limit_exceeded: bool = True

    @classmethod
    def from_value(
        cls, value: GraphExportRequest | Mapping[str, Any] | None
    ) -> GraphExportRequest:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        item = _mapping(value, "graph export request")
        return cls(
            graph_id=_text(item.get("graphId"), "graphId"),
            namespace=_text(item.get("namespace"), "namespace"),
            tenant_id=_text(item.get("tenantId"), "tenantId"),
            partition_key=_text(item.get("partitionKey"), "partitionKey"),
            include_projections=_boolean(
                item.get("includeProjections"), "includeProjections", True
            ),
            max_records=_integer(item.get("maxRecords"), "maxRecords"),
            fail_on_limit_exceeded=_boolean(
                item.get("failOnLimitExceeded"), "failOnLimitExceeded", True
            ),
        )


@dataclass(frozen=True)
class GraphExportResult(GraphOperationModel):
    collection: str
    envelope: VyralGraphEnvelope
    record_count: int
    truncated: bool
    continuation_token: str | None
    exported_at: datetime


@dataclass(frozen=True)
class GraphTraversalRequest(GraphOperationModel):
    start_node_ids: tuple[str, ...]
    graph_id: str | None = None
    namespace: str | None = None
    tenant_id: str | None = None
    partition_key: str | None = None
    profile: VyralGraphTraversalProfile = VyralGraphTraversalProfile()
    max_records: int | None = None
    allow_partial_graph: bool = False

    @classmethod
    def from_value(
        cls, value: GraphTraversalRequest | Mapping[str, Any]
    ) -> GraphTraversalRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "graph traversal request")
        return cls(
            graph_id=_text(item.get("graphId"), "graphId"),
            namespace=_text(item.get("namespace"), "namespace"),
            tenant_id=_text(item.get("tenantId"), "tenantId"),
            partition_key=_text(item.get("partitionKey"), "partitionKey"),
            start_node_ids=_strings(item.get("startNodeIds"), "startNodeIds"),
            profile=VyralGraphTraversalProfile.from_value(item.get("profile")),
            max_records=_integer(item.get("maxRecords"), "maxRecords"),
            allow_partial_graph=_boolean(
                item.get("allowPartialGraph"), "allowPartialGraph", False
            ),
        )


@dataclass(frozen=True)
class GraphTraversalResult(GraphOperationModel):
    collection: str
    graph_id: str
    projection: VyralGraphProjection
    node_count: int
    edge_count: int
    source_record_count: int
    source_truncated: bool
    requested_max_records: int
    exported_record_count: int
    estimated_required_record_count: int
    source_continuation_token: str | None


@dataclass(frozen=True)
class GraphInspectionRequest(GraphOperationModel):
    graph_id: str | None = None
    namespace: str | None = None
    tenant_id: str | None = None
    partition_key: str | None = None
    max_records: int | None = None
    allow_partial_graph: bool = False
    include_anomalies: bool = True
    anomaly_limit: int = 50

    @classmethod
    def from_value(
        cls, value: GraphInspectionRequest | Mapping[str, Any] | None
    ) -> GraphInspectionRequest:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        item = _mapping(value, "graph inspection request")
        anomaly_limit = _integer(item.get("anomalyLimit"), "anomalyLimit")
        return cls(
            graph_id=_text(item.get("graphId"), "graphId"),
            namespace=_text(item.get("namespace"), "namespace"),
            tenant_id=_text(item.get("tenantId"), "tenantId"),
            partition_key=_text(item.get("partitionKey"), "partitionKey"),
            max_records=_integer(item.get("maxRecords"), "maxRecords"),
            allow_partial_graph=_boolean(
                item.get("allowPartialGraph"), "allowPartialGraph", False
            ),
            include_anomalies=_boolean(
                item.get("includeAnomalies"), "includeAnomalies", True
            ),
            anomaly_limit=50 if anomaly_limit is None else anomaly_limit,
        )


@dataclass(frozen=True)
class GraphInspectionAnomaly(GraphOperationModel):
    kind: str
    id: str | None
    message: str
    subject_id: str | None = None
    subject_kind: str | None = None
    details: JSONObject | None = None


@dataclass(frozen=True)
class GraphSourceGroundingInspection(GraphOperationModel):
    node_grounded_count: int
    node_ungrounded_count: int
    node_coverage: float
    edge_grounded_count: int
    edge_ungrounded_count: int
    edge_coverage: float
    assertion_grounded_count: int
    assertion_ungrounded_count: int
    assertion_coverage: float


@dataclass(frozen=True)
class GraphInspectionResult(GraphOperationModel):
    collection: str
    generated_at: datetime
    graph_id: str
    namespace: str
    tenant_id: str
    partition_key: str
    record_count: int
    truncated: bool
    continuation_token: str | None
    traversal_ready: bool
    node_count: int
    edge_count: int
    assertion_count: int
    review_count: int
    projection_count: int
    record_type_counts: Mapping[str, int]
    graph_id_counts: Mapping[str, int]
    namespace_counts: Mapping[str, int]
    tenant_id_counts: Mapping[str, int]
    partition_key_counts: Mapping[str, int]
    node_type_counts: Mapping[str, int]
    predicate_counts: Mapping[str, int]
    assertion_status_counts: Mapping[str, int]
    review_status_counts: Mapping[str, int]
    source_grounding: GraphSourceGroundingInspection
    dangling_edge_count: int
    orphan_assertion_count: int
    orphan_review_count: int
    dangling_assertion_reference_count: int
    dangling_projection_start_node_count: int
    duplicate_node_id_count: int
    duplicate_edge_id_count: int
    duplicate_assertion_id_count: int
    duplicate_review_id_count: int
    duplicate_projection_id_count: int
    warning_count: int
    warnings: tuple[str, ...]
    anomaly_count: int
    returned_anomaly_count: int
    anomalies: tuple[GraphInspectionAnomaly, ...]


@dataclass(frozen=True)
class GraphDoctorRequest(GraphOperationModel):
    graph_id: str | None = None
    namespace: str | None = None
    tenant_id: str | None = None
    partition_key: str | None = None
    target_collection: str | None = None
    target_partition_keys: tuple[str, ...] = ()
    seed_json_pointers: tuple[str, ...] = (
        "/metadata/graphNodeId",
        "/metadata/nodeId",
        "/metadata/graphNodeIds",
        "/id",
    )
    max_graph_records: int | None = None
    max_target_records: int = 1000
    allow_partial_graph: bool = False
    include_anomalies: bool = True
    anomaly_limit: int = 50

    @classmethod
    def from_value(
        cls, value: GraphDoctorRequest | Mapping[str, Any] | None
    ) -> GraphDoctorRequest:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        item = _mapping(value, "graph doctor request")
        max_target = _integer(item.get("maxTargetRecords"), "maxTargetRecords")
        anomaly = _integer(item.get("anomalyLimit"), "anomalyLimit")
        pointers = item.get("seedJsonPointers")
        return cls(
            graph_id=_text(item.get("graphId"), "graphId"),
            namespace=_text(item.get("namespace"), "namespace"),
            tenant_id=_text(item.get("tenantId"), "tenantId"),
            partition_key=_text(item.get("partitionKey"), "partitionKey"),
            target_collection=_text(
                item.get("targetCollection"), "targetCollection"
            ),
            target_partition_keys=_strings(
                item.get("targetPartitionKeys"), "targetPartitionKeys"
            ),
            seed_json_pointers=(
                _strings(pointers, "seedJsonPointers")
                if pointers is not None
                else cls().seed_json_pointers
            ),
            max_graph_records=_integer(
                item.get("maxGraphRecords"), "maxGraphRecords"
            ),
            max_target_records=1000 if max_target is None else max_target,
            allow_partial_graph=_boolean(
                item.get("allowPartialGraph"), "allowPartialGraph", False
            ),
            include_anomalies=_boolean(
                item.get("includeAnomalies"), "includeAnomalies", True
            ),
            anomaly_limit=50 if anomaly is None else anomaly,
        )


@dataclass(frozen=True)
class GraphSeedCoverage(GraphOperationModel):
    target_collection: str
    target_record_count: int
    target_truncated: bool
    seed_json_pointers: tuple[str, ...]
    records_with_seed_metadata_count: int
    seed_value_count: int
    unique_seed_value_count: int
    resolved_seed_node_count: int
    unresolved_seed_node_count: int
    seed_coverage: float
    resolved_seed_coverage: float
    resolved_seed_node_ids: tuple[str, ...]
    unresolved_seed_node_ids: tuple[str, ...]


@dataclass(frozen=True)
class GraphDoctorResult(GraphOperationModel):
    collection: str
    generated_at: datetime
    status: str
    failure_mode: str | None
    ready: bool
    graph_ready: bool
    graph_record_count: int
    graph_node_count: int
    graph_edge_count: int
    graph_truncated: bool
    seed_coverage: GraphSeedCoverage | None
    inspection: GraphInspectionResult
    recommended_actions: tuple[str, ...]
