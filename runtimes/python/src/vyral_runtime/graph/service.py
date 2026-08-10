from __future__ import annotations

from collections import Counter, defaultdict, deque
from datetime import datetime, timezone
from hashlib import sha256
import json
from typing import Any, Callable, Iterable, Mapping, Sequence

from ..async_runtime import RuntimeExecutor
from ..local import MAX_COLLECTION_SNAPSHOT_RECORDS, SQLiteRecordStore, VyralRecord
from ..local.query_engine import encode_continuation_token
from .mapper import (
    create_graph_collection_policy,
    graph_from_records,
    graph_to_records,
    is_graph_collection_policy,
    missing_graph_metadata_indexes,
    validate_graph_envelope,
)
from .models import (
    VyralGraphAssertion,
    VyralGraphEdge,
    VyralGraphEnvelope,
    VyralGraphNode,
    VyralGraphProjection,
    VyralGraphTraversalProfile,
)
from .operations import (
    GraphDoctorRequest,
    GraphDoctorResult,
    GraphExportRequest,
    GraphExportResult,
    GraphImportPreflightResult,
    GraphImportRequest,
    GraphImportResult,
    GraphInspectionAnomaly,
    GraphInspectionRequest,
    GraphInspectionResult,
    GraphSeedCoverage,
    GraphSourceGroundingInspection,
    GraphTraversalRequest,
    GraphTraversalResult,
)


MAX_GRAPH_ANOMALIES = 500


class GraphTraversalTruncatedError(ValueError):
    def __init__(self, result: GraphExportResult) -> None:
        super().__init__(
            "Graph traversal source export was truncated. Increase maxRecords "
            "or set allowPartialGraph to true."
        )
        self.result = result
        self.estimated_required_record_count = result.record_count + 1


class GraphService:
    def __init__(
        self,
        record_store: SQLiteRecordStore,
        *,
        clock: Callable[[], datetime] | None = None,
        executor: RuntimeExecutor | None = None,
    ) -> None:
        self.record_store = record_store
        self._clock = clock or (lambda: datetime.now(timezone.utc))
        self.executor = executor or RuntimeExecutor()
        self._owns_executor = executor is None

    def preflight_import(
        self,
        collection: str,
        request: GraphImportRequest | Mapping[str, Any],
    ) -> GraphImportPreflightResult:
        normalized = GraphImportRequest.from_value(request)
        envelope = normalized.envelope
        errors: list[str] = []
        warnings: list[str] = []
        record_count = 0
        try:
            validate_graph_envelope(envelope)
            record_count = len(graph_to_records(envelope, clock=self._clock))
        except (TypeError, ValueError) as exc:
            errors.append(str(exc))
        existing = self.record_store.get_collection_policy(collection)
        policy_status = ""
        would_create = False
        would_replace = False
        would_allow = False
        if existing is None:
            if normalized.create_collection_if_missing:
                policy_status = "created"
                would_create = True
            else:
                errors.append(
                    f"Collection {collection!r} does not exist and "
                    "createCollectionIfMissing is false."
                )
        elif normalized.replace_existing:
            policy_status = "replaced"
            would_replace = True
            warnings.append(
                "replaceExisting will delete and recreate the target collection."
            )
        elif is_graph_collection_policy(existing):
            policy_status = "existing_graph_policy"
        elif normalized.allow_non_graph_policy:
            policy_status = "existing_non_graph_policy_allowed"
            would_allow = True
            warnings.append(
                "Target collection lacks default graph indexes; graph operations "
                "may be slower."
            )
        else:
            missing = ", ".join(missing_graph_metadata_indexes(existing))
            errors.append(
                f"Collection {collection!r} is missing graph metadata indexes: "
                f"{missing}."
            )
        scope = envelope.scope
        return GraphImportPreflightResult(
            collection=collection,
            generated_at=_utc(self._clock()),
            valid=not errors,
            ready_to_import=not errors,
            graph_id=scope.graph_id,
            namespace=scope.namespace,
            tenant_id=scope.tenant_id,
            partition_key=scope.resolved_partition_key(),
            collection_exists=existing is not None,
            collection_policy_status=policy_status,
            would_create_collection=would_create,
            would_replace_collection=would_replace,
            would_allow_non_graph_policy=would_allow,
            create_collection_if_missing=normalized.create_collection_if_missing,
            replace_existing=normalized.replace_existing,
            allow_non_graph_policy=normalized.allow_non_graph_policy,
            node_count=len(envelope.nodes),
            edge_count=len(envelope.edges),
            assertion_count=len(envelope.assertions),
            review_count=len(envelope.reviews),
            projection_count=len(envelope.projections),
            record_count=record_count,
            max_records=MAX_COLLECTION_SNAPSHOT_RECORDS,
            warning_count=len(warnings),
            warnings=tuple(warnings),
            error_count=len(errors),
            errors=tuple(errors),
        )

    async def apreflight_import(
        self,
        collection: str,
        request: GraphImportRequest | Mapping[str, Any],
    ) -> GraphImportPreflightResult:
        return await self.executor.run(
            lambda: self.preflight_import(collection, request)
        )

    def import_envelope(
        self,
        collection: str,
        request: GraphImportRequest | Mapping[str, Any],
    ) -> GraphImportResult:
        normalized = GraphImportRequest.from_value(request)
        preflight = self.preflight_import(collection, normalized)
        if not preflight.ready_to_import:
            raise ValueError(" ".join(preflight.errors) or "Graph import preflight failed.")
        existing = self.record_store.get_collection_policy(collection)
        if existing is None:
            self.record_store.create_collection(
                create_graph_collection_policy(collection)
            )
        elif normalized.replace_existing:
            self.record_store.delete_collection(collection)
            self.record_store.create_collection(
                create_graph_collection_policy(collection)
            )
        records = graph_to_records(normalized.envelope, clock=self._clock)
        batch = self.record_store.upsert_records(
            collection,
            records,
            continue_on_error=normalized.continue_on_error,
        )
        return GraphImportResult(
            collection=collection,
            graph_id=normalized.envelope.scope.graph_id,
            partition_key=normalized.envelope.scope.resolved_partition_key(),
            policy_status=preflight.collection_policy_status,
            node_count=len(normalized.envelope.nodes),
            edge_count=len(normalized.envelope.edges),
            assertion_count=len(normalized.envelope.assertions),
            review_count=len(normalized.envelope.reviews),
            projection_count=len(normalized.envelope.projections),
            record_count=len(records),
            records=batch,
        )

    async def aimport_envelope(
        self,
        collection: str,
        request: GraphImportRequest | Mapping[str, Any],
    ) -> GraphImportResult:
        return await self.executor.run(
            lambda: self.import_envelope(collection, request)
        )

    def export_envelope(
        self,
        collection: str,
        request: GraphExportRequest | Mapping[str, Any] | None = None,
    ) -> GraphExportResult | None:
        normalized = GraphExportRequest.from_value(request)
        maximum = normalized.max_records or MAX_COLLECTION_SNAPSHOT_RECORDS
        _validate_maximum(maximum, "Graph export maxRecords")
        if self.record_store.get_collection_policy(collection) is None:
            return None
        records = tuple(
            record
            for record in self.record_store.query_all_records(collection)
            if _record_in_scope(record, normalized)
            and (
                normalized.include_projections
                or record.type != "graph.projection"
            )
        )
        records = tuple(sorted(records, key=lambda item: (item.type, item.id)))
        truncated = len(records) > maximum
        if truncated and normalized.fail_on_limit_exceeded:
            raise ValueError(
                f"Graph export exceeded maxRecords ({maximum})."
            )
        selected = records[:maximum]
        return GraphExportResult(
            collection=collection,
            envelope=graph_from_records(
                selected,
                include_projections=normalized.include_projections,
            ),
            record_count=len(selected),
            truncated=truncated,
            continuation_token=(
                encode_continuation_token(maximum) if truncated else None
            ),
            exported_at=_utc(self._clock()),
        )

    async def aexport_envelope(
        self,
        collection: str,
        request: GraphExportRequest | Mapping[str, Any] | None = None,
    ) -> GraphExportResult | None:
        return await self.executor.run(
            lambda: self.export_envelope(collection, request)
        )

    def traverse(
        self,
        collection: str,
        request: GraphTraversalRequest | Mapping[str, Any],
    ) -> GraphTraversalResult | None:
        normalized = GraphTraversalRequest.from_value(request)
        _validate_traversal(normalized)
        maximum = normalized.max_records or MAX_COLLECTION_SNAPSHOT_RECORDS
        exported = self.export_envelope(
            collection,
            GraphExportRequest(
                graph_id=normalized.graph_id,
                namespace=normalized.namespace,
                tenant_id=normalized.tenant_id,
                partition_key=normalized.partition_key,
                include_projections=False,
                max_records=maximum,
                fail_on_limit_exceeded=False,
            ),
        )
        if exported is None:
            return None
        if exported.truncated and not normalized.allow_partial_graph:
            raise GraphTraversalTruncatedError(exported)
        projection = _traverse(
            exported.envelope,
            normalized.start_node_ids,
            normalized.profile,
            exported,
            _utc(self._clock()),
        )
        return GraphTraversalResult(
            collection=collection,
            graph_id=exported.envelope.scope.graph_id,
            projection=projection,
            node_count=len(projection.nodes),
            edge_count=len(projection.edges),
            source_record_count=exported.record_count,
            source_truncated=exported.truncated,
            requested_max_records=maximum,
            exported_record_count=exported.record_count,
            estimated_required_record_count=(
                exported.record_count + 1
                if exported.truncated
                else exported.record_count
            ),
            source_continuation_token=exported.continuation_token,
        )

    async def atraverse(
        self,
        collection: str,
        request: GraphTraversalRequest | Mapping[str, Any],
    ) -> GraphTraversalResult | None:
        return await self.executor.run(lambda: self.traverse(collection, request))

    def inspect(
        self,
        collection: str,
        request: GraphInspectionRequest | Mapping[str, Any] | None = None,
    ) -> GraphInspectionResult | None:
        normalized = GraphInspectionRequest.from_value(request)
        _validate_inspection(normalized)
        exported = self.export_envelope(
            collection,
            GraphExportRequest(
                graph_id=normalized.graph_id,
                namespace=normalized.namespace,
                tenant_id=normalized.tenant_id,
                partition_key=normalized.partition_key,
                max_records=normalized.max_records,
                fail_on_limit_exceeded=False,
            ),
        )
        if exported is None:
            return None
        return _inspect(collection, normalized, exported, _utc(self._clock()))

    async def ainspect(
        self,
        collection: str,
        request: GraphInspectionRequest | Mapping[str, Any] | None = None,
    ) -> GraphInspectionResult | None:
        return await self.executor.run(lambda: self.inspect(collection, request))

    def doctor(
        self,
        collection: str,
        request: GraphDoctorRequest | Mapping[str, Any] | None = None,
    ) -> GraphDoctorResult | None:
        normalized = GraphDoctorRequest.from_value(request)
        if normalized.max_target_records <= 0:
            raise ValueError("Graph doctor maxTargetRecords must be positive.")
        for pointer in normalized.seed_json_pointers:
            if not pointer.startswith("/"):
                raise ValueError(
                    "Graph doctor seedJsonPointers must be JSON pointers."
                )
        inspection = self.inspect(
            collection,
            GraphInspectionRequest(
                graph_id=normalized.graph_id,
                namespace=normalized.namespace,
                tenant_id=normalized.tenant_id,
                partition_key=normalized.partition_key,
                max_records=normalized.max_graph_records,
                allow_partial_graph=normalized.allow_partial_graph,
                include_anomalies=normalized.include_anomalies,
                anomaly_limit=normalized.anomaly_limit,
            ),
        )
        if inspection is None:
            return None
        coverage = (
            self._seed_coverage(collection, normalized)
            if normalized.target_collection
            else None
        )
        actions: list[str] = []
        failure_mode: str | None = None
        if inspection.truncated:
            status = "budget_truncated"
            failure_mode = status
            actions.append(
                "Increase maxGraphRecords or allow an intentional partial graph inspection."
            )
        elif not inspection.traversal_ready:
            status = "graph_not_ready"
            failure_mode = "graph_anomalies"
            actions.append(
                "Inspect graph warnings and anomalies before enabling GraphRAG."
            )
        elif (
            coverage is not None
            and coverage.target_record_count > 0
            and coverage.unique_seed_value_count == 0
        ):
            status = "no_seeds"
            failure_mode = status
            actions.append(
                "Stamp target records with graph seed metadata or configure seedJsonPointers."
            )
        elif (
            coverage is not None
            and coverage.unique_seed_value_count > 0
            and coverage.resolved_seed_node_count == 0
        ):
            status = "seed_node_not_found"
            failure_mode = status
            actions.append("Align target record seed ids with graph node ids.")
        elif inspection.node_count > 0 and inspection.edge_count == 0:
            status = "traversal_empty"
            failure_mode = status
            actions.append(
                "Add graph edges or broaden traversal predicates/depth."
            )
        else:
            status = "ready"
        if coverage is not None and coverage.target_truncated:
            actions.append(
                "Increase maxTargetRecords to inspect full seed coverage."
            )
        ready = status == "ready"
        return GraphDoctorResult(
            collection=collection,
            generated_at=_utc(self._clock()),
            status=status,
            failure_mode=failure_mode,
            ready=ready,
            graph_ready=inspection.traversal_ready,
            graph_record_count=inspection.record_count,
            graph_node_count=inspection.node_count,
            graph_edge_count=inspection.edge_count,
            graph_truncated=inspection.truncated,
            seed_coverage=coverage,
            inspection=inspection,
            recommended_actions=tuple(actions),
        )

    async def adoctor(
        self,
        collection: str,
        request: GraphDoctorRequest | Mapping[str, Any] | None = None,
    ) -> GraphDoctorResult | None:
        return await self.executor.run(lambda: self.doctor(collection, request))

    def close(self) -> None:
        if self._owns_executor:
            self.executor.close()

    def _seed_coverage(
        self,
        graph_collection: str,
        request: GraphDoctorRequest,
    ) -> GraphSeedCoverage:
        target = request.target_collection or ""
        maximum = request.max_target_records
        _validate_maximum(maximum, "Graph doctor maxTargetRecords")
        records = self.record_store.query_all_records(target)
        if request.target_partition_keys:
            allowed = set(request.target_partition_keys)
            records = tuple(
                record for record in records if record.partition_key in allowed
            )
        truncated = len(records) > maximum
        selected = records[:maximum]
        seed_values: list[str] = []
        record_count = 0
        for record in selected:
            found = False
            document = record.to_dict()
            for pointer in request.seed_json_pointers:
                for value in _pointer_values(document, pointer):
                    seed_values.append(value)
                    found = True
            if found:
                record_count += 1
        unique = set(seed_values)
        node_ids: set[str] = set()
        graph_records = self.record_store.query_all_records(graph_collection)
        for record in graph_records:
            if record.type == "graph.node":
                node_value = (record.metadata or {}).get("nodeId")
                if isinstance(node_value, str):
                    node_ids.add(node_value)
        matched = unique.intersection(node_ids)
        missing = tuple(sorted(unique - node_ids))
        return GraphSeedCoverage(
            target_collection=target,
            target_record_count=len(selected),
            target_truncated=truncated,
            seed_json_pointers=request.seed_json_pointers,
            records_with_seed_metadata_count=record_count,
            seed_value_count=len(seed_values),
            unique_seed_value_count=len(unique),
            resolved_seed_node_count=len(matched),
            unresolved_seed_node_count=len(missing),
            seed_coverage=record_count / len(selected) if selected else 1.0,
            resolved_seed_coverage=(
                len(matched) / len(unique) if unique else 1.0
            ),
            resolved_seed_node_ids=tuple(sorted(matched))[:100],
            unresolved_seed_node_ids=missing[:100],
        )


def _record_in_scope(record: VyralRecord, request: GraphExportRequest) -> bool:
    if record.type not in {
        "graph.envelope",
        "graph.node",
        "graph.edge",
        "graph.assertion",
        "graph.review",
        "graph.projection",
    }:
        return False
    metadata = record.metadata or {}
    return all(
        not expected or metadata.get(key) == expected
        for key, expected in (
            ("graphId", request.graph_id),
            ("namespace", request.namespace),
            ("tenantId", request.tenant_id),
            ("graphPartitionKey", request.partition_key),
        )
    )


def _traverse(
    envelope: VyralGraphEnvelope,
    start_ids: Sequence[str],
    profile: VyralGraphTraversalProfile,
    source: GraphExportResult,
    now: datetime,
) -> VyralGraphProjection:
    nodes = {node.id: node for node in envelope.nodes}
    outgoing: dict[str, list[VyralGraphEdge]] = defaultdict(list)
    incoming: dict[str, list[VyralGraphEdge]] = defaultdict(list)
    for edge in sorted(envelope.edges, key=lambda item: item.id):
        outgoing[edge.source_id].append(edge)
        incoming[edge.target_id].append(edge)
    assertions_by_subject: dict[str, set[str]] = defaultdict(set)
    assertions = {item.id: item for item in envelope.assertions}
    for assertion in envelope.assertions:
        assertions_by_subject[assertion.subject_id].add(assertion.id)
    reviews: dict[str, set[str]] = defaultdict(set)
    for review in envelope.reviews:
        reviews[review.subject_id].add(review.status)
    included_nodes: dict[str, VyralGraphNode] = {}
    included_edges: dict[str, VyralGraphEdge] = {}
    visited: set[str] = set()
    frontier: deque[tuple[str, int]] = deque()
    missing: list[str] = []
    filtered = Counter(
        {
            "nodeType": 0,
            "predicate": 0,
            "sourceGrounding": 0,
            "score": 0,
            "assertionStatus": 0,
            "reviewStatus": 0,
            "nodeLimit": 0,
            "edgeLimit": 0,
        }
    )
    paths: dict[str, list[dict[str, Any]]] = {}
    for node_id in dict.fromkeys(start_ids):
        node = nodes.get(node_id)
        if node is None:
            missing.append(node_id)
            continue
        visited.add(node_id)
        if profile.include_start and _node_allowed(
            node, profile, assertions_by_subject, assertions, reviews, filtered
        ):
            included_nodes[node_id] = node
            paths[node_id] = []
        frontier.append((node_id, 0))
    edge_truncated = False
    node_limit = False
    while frontier:
        node_id, depth = frontier.popleft()
        if depth >= profile.max_depth:
            continue
        candidates: list[VyralGraphEdge] = []
        if profile.direction in {"outgoing", "both", ""}:
            candidates.extend(outgoing.get(node_id, ()))
        if profile.direction in {"incoming", "both", ""}:
            candidates.extend(incoming.get(node_id, ()))
        for edge in candidates:
            if edge.id in included_edges:
                continue
            if not _edge_allowed(
                edge, profile, assertions_by_subject, assertions, reviews, filtered
            ):
                continue
            if len(included_edges) >= profile.edge_limit:
                filtered["edgeLimit"] += 1
                edge_truncated = True
                break
            next_id = (
                edge.target_id
                if edge.source_id == node_id
                else edge.source_id
                if edge.target_id == node_id
                else None
            )
            node = nodes.get(next_id or "")
            if node is None or not _node_allowed(
                node, profile, assertions_by_subject, assertions, reviews, filtered
            ):
                continue
            if node.id not in included_nodes and len(included_nodes) >= profile.limit:
                filtered["nodeLimit"] += 1
                node_limit = True
                continue
            included_edges[edge.id] = edge
            included_nodes[node.id] = node
            paths.setdefault(node.id, []).append(
                {
                    "edgeId": edge.id,
                    "from": node_id,
                    "to": node.id,
                    "predicate": edge.predicate,
                    "depth": depth + 1,
                }
            )
            if node.id not in visited:
                visited.add(node.id)
                frontier.append((node.id, depth + 1))
        if edge_truncated:
            break
    identity = json.dumps(
        {
            "graphId": envelope.scope.graph_id,
            "starts": sorted(set(start_ids)),
            "profile": profile.to_dict(),
            "nodes": sorted(included_nodes),
            "edges": sorted(included_edges),
        },
        sort_keys=True,
        separators=(",", ":"),
    )
    diagnostics: dict[str, Any] = {
        "sourceRecordCount": source.record_count,
        "sourceTruncated": source.truncated,
        "availableNodeCount": len(envelope.nodes),
        "availableEdgeCount": len(envelope.edges),
        "nodeCount": len(included_nodes),
        "edgeCount": len(included_edges),
        "missingStartNodeIds": missing,
        "edgeTruncated": edge_truncated,
        "nodeLimitReached": node_limit,
        "filtered": dict(sorted(filtered.items())),
    }
    if profile.include_path_explanations:
        diagnostics["pathExplanations"] = paths
    return VyralGraphProjection(
        id="projection:"
        + envelope.scope.graph_id
        + ":"
        + sha256(identity.encode("utf-8")).hexdigest()[:32],
        profile=profile,
        start_node_ids=tuple(dict.fromkeys(start_ids)),
        nodes=tuple(sorted(included_nodes.values(), key=lambda item: item.id)),
        edges=tuple(sorted(included_edges.values(), key=lambda item: item.id)),
        diagnostics=diagnostics,
        created_at=now,
    )


def _entity_allowed(
    subject_id: str,
    explicit_assertions: Iterable[str],
    spans: Sequence[object],
    properties: Mapping[str, Any] | None,
    profile: VyralGraphTraversalProfile,
    apply_assertions: bool,
    by_subject: Mapping[str, set[str]],
    assertions: Mapping[str, VyralGraphAssertion],
    reviews: Mapping[str, set[str]],
    filtered: Counter[str],
) -> bool:
    if profile.require_source_grounding and not spans:
        filtered["sourceGrounding"] += 1
        return False
    score = (properties or {}).get("score")
    if (
        profile.min_score is not None
        and isinstance(score, (int, float))
        and not isinstance(score, bool)
        and float(score) < profile.min_score
    ):
        filtered["score"] += 1
        return False
    assertion_ids = set(explicit_assertions) | by_subject.get(subject_id, set())
    if apply_assertions and profile.assertion_statuses and not any(
        item in assertions
        and assertions[item].status in profile.assertion_statuses
        for item in assertion_ids
    ):
        filtered["assertionStatus"] += 1
        return False
    if apply_assertions and profile.review_statuses and not any(
        reviews.get(item, set()).intersection(profile.review_statuses)
        for item in assertion_ids
    ):
        filtered["reviewStatus"] += 1
        return False
    return True


def _node_allowed(
    node: VyralGraphNode,
    profile: VyralGraphTraversalProfile,
    by_subject: Mapping[str, set[str]],
    assertions: Mapping[str, VyralGraphAssertion],
    reviews: Mapping[str, set[str]],
    filtered: Counter[str],
) -> bool:
    if profile.node_types and node.type not in profile.node_types:
        filtered["nodeType"] += 1
        return False
    return _entity_allowed(
        node.id,
        node.assertion_ids,
        node.source_spans,
        node.properties,
        profile,
        False,
        by_subject,
        assertions,
        reviews,
        filtered,
    )


def _edge_allowed(
    edge: VyralGraphEdge,
    profile: VyralGraphTraversalProfile,
    by_subject: Mapping[str, set[str]],
    assertions: Mapping[str, VyralGraphAssertion],
    reviews: Mapping[str, set[str]],
    filtered: Counter[str],
) -> bool:
    if profile.predicates and edge.predicate not in profile.predicates:
        filtered["predicate"] += 1
        return False
    return _entity_allowed(
        edge.id,
        edge.assertion_ids,
        edge.source_spans,
        edge.properties,
        profile,
        True,
        by_subject,
        assertions,
        reviews,
        filtered,
    )


def _inspect(
    collection: str,
    request: GraphInspectionRequest,
    exported: GraphExportResult,
    now: datetime,
) -> GraphInspectionResult:
    envelope = exported.envelope
    entity_record_type_counts = {
        "graph.node": len(envelope.nodes),
        "graph.edge": len(envelope.edges),
        "graph.assertion": len(envelope.assertions),
        "graph.review": len(envelope.reviews),
        "graph.projection": len(envelope.projections),
    }
    entity_record_count = sum(entity_record_type_counts.values())
    # FromRecords intentionally tolerates an export whose bound excludes the
    # envelope row. Infer its presence from the selected record count instead
    # of synthesizing a new envelope row and overstating truncated exports.
    envelope_record_count = min(
        1,
        max(0, exported.record_count - entity_record_count),
    )
    record_type_counts = {
        key: count
        for key, count in (
            ("graph.assertion", entity_record_type_counts["graph.assertion"]),
            ("graph.edge", entity_record_type_counts["graph.edge"]),
            ("graph.envelope", envelope_record_count),
            ("graph.node", entity_record_type_counts["graph.node"]),
            ("graph.projection", entity_record_type_counts["graph.projection"]),
            ("graph.review", entity_record_type_counts["graph.review"]),
        )
        if count
    }
    node_ids = {item.id for item in envelope.nodes}
    edge_ids = {item.id for item in envelope.edges}
    assertion_ids = {item.id for item in envelope.assertions}
    projection_ids = {item.id for item in envelope.projections}
    anomalies: list[GraphInspectionAnomaly] = []
    anomaly_count = 0

    def add(anomaly: GraphInspectionAnomaly) -> None:
        nonlocal anomaly_count
        anomaly_count += 1
        if request.include_anomalies and len(anomalies) < request.anomaly_limit:
            anomalies.append(anomaly)

    duplicate_counts: dict[str, int] = {}
    for name, identifiers in (
        ("node", tuple(item.id for item in envelope.nodes)),
        ("edge", tuple(item.id for item in envelope.edges)),
        ("assertion", tuple(item.id for item in envelope.assertions)),
        ("review", tuple(item.id for item in envelope.reviews)),
        ("projection", tuple(item.id for item in envelope.projections)),
    ):
        duplicates = tuple(
            key for key, count in Counter(identifiers).items() if count > 1
        )
        duplicate_counts[name] = len(duplicates)
        for identifier in duplicates:
            add(
                GraphInspectionAnomaly(
                    kind=f"duplicate{name.title()}Id",
                    id=identifier,
                    subject_kind=name,
                    message=f"{name} id {identifier!r} appears more than once.",
                )
            )
    dangling_edges = 0
    for edge in envelope.edges:
        if edge.source_id not in node_ids or edge.target_id not in node_ids:
            dangling_edges += 1
            add(
                GraphInspectionAnomaly(
                    "danglingEdge",
                    edge.id,
                    f"Edge {edge.id!r} references a missing node.",
                    details={
                        "sourceId": edge.source_id,
                        "sourceExists": edge.source_id in node_ids,
                        "targetId": edge.target_id,
                        "targetExists": edge.target_id in node_ids,
                    },
                )
            )
    subject_sets = {
        "node": node_ids,
        "edge": edge_ids,
        "assertion": assertion_ids,
        "projection": projection_ids,
    }
    orphan_assertions = sum(
        1
        for item in envelope.assertions
        if item.subject_id not in subject_sets.get(item.subject_kind, set())
    )
    orphan_reviews = sum(
        1
        for item in envelope.reviews
        if item.subject_id not in subject_sets.get(item.subject_kind, set())
    )
    for assertion in envelope.assertions:
        if assertion.subject_id not in subject_sets.get(assertion.subject_kind, set()):
            add(GraphInspectionAnomaly("orphanAssertion", assertion.id, f"Assertion {assertion.id!r} has a missing subject.", assertion.subject_id, assertion.subject_kind))
    for review in envelope.reviews:
        if review.subject_id not in subject_sets.get(review.subject_kind, set()):
            add(GraphInspectionAnomaly("orphanReview", review.id, f"Review {review.id!r} has a missing subject.", review.subject_id, review.subject_kind))
    dangling_assertions = 0
    subjects = tuple(
        (subject.id, subject.assertion_ids) for subject in envelope.nodes
    ) + tuple((subject.id, subject.assertion_ids) for subject in envelope.edges)
    for subject_id, referenced_assertions in subjects:
        for assertion_id in set(referenced_assertions):
            if assertion_id not in assertion_ids:
                dangling_assertions += 1
                add(GraphInspectionAnomaly("danglingAssertionReference", assertion_id, f"Subject {subject_id!r} references a missing assertion.", subject_id))
    dangling_starts = 0
    for projection in envelope.projections:
        for node_id in projection.start_node_ids:
            if node_id not in node_ids:
                dangling_starts += 1
                add(GraphInspectionAnomaly("danglingProjectionStartNode", projection.id, f"Projection {projection.id!r} has a missing start node.", node_id, "node"))
    grounding = GraphSourceGroundingInspection(
        node_grounded_count=sum(bool(item.source_spans) for item in envelope.nodes),
        node_ungrounded_count=sum(not item.source_spans for item in envelope.nodes),
        node_coverage=_coverage(sum(bool(item.source_spans) for item in envelope.nodes), len(envelope.nodes)),
        edge_grounded_count=sum(bool(item.source_spans) for item in envelope.edges),
        edge_ungrounded_count=sum(not item.source_spans for item in envelope.edges),
        edge_coverage=_coverage(sum(bool(item.source_spans) for item in envelope.edges), len(envelope.edges)),
        assertion_grounded_count=sum(bool(item.source_spans) for item in envelope.assertions),
        assertion_ungrounded_count=sum(not item.source_spans for item in envelope.assertions),
        assertion_coverage=_coverage(sum(bool(item.source_spans) for item in envelope.assertions), len(envelope.assertions)),
    )
    warnings: list[str] = []
    if exported.truncated and not request.allow_partial_graph:
        warnings.append("Graph inspection source was truncated.")
    if not envelope.nodes:
        warnings.append("Graph has no nodes.")
    if dangling_edges:
        warnings.append("Graph contains dangling edges.")
    if orphan_assertions:
        warnings.append("Graph contains orphan assertions.")
    if orphan_reviews:
        warnings.append("Graph contains orphan reviews.")
    if dangling_assertions:
        warnings.append("Graph contains dangling assertion references.")
    if dangling_starts:
        warnings.append("Graph contains projections with missing start nodes.")
    duplicate_total = sum(duplicate_counts.values())
    if duplicate_total:
        warnings.append("Graph contains duplicate entity ids.")
    ready = (
        bool(envelope.nodes)
        and (request.allow_partial_graph or not exported.truncated)
        and not dangling_edges
        and not orphan_assertions
        and not orphan_reviews
        and not duplicate_total
    )
    return GraphInspectionResult(
        collection=collection,
        generated_at=now,
        graph_id=request.graph_id or envelope.scope.graph_id,
        namespace=request.namespace or envelope.scope.namespace,
        tenant_id=request.tenant_id or envelope.scope.tenant_id,
        partition_key=request.partition_key or envelope.scope.partition_key,
        record_count=exported.record_count,
        truncated=exported.truncated,
        continuation_token=exported.continuation_token,
        traversal_ready=ready,
        node_count=len(envelope.nodes),
        edge_count=len(envelope.edges),
        assertion_count=len(envelope.assertions),
        review_count=len(envelope.reviews),
        projection_count=len(envelope.projections),
        record_type_counts=record_type_counts,
        graph_id_counts={envelope.scope.graph_id: exported.record_count},
        namespace_counts={envelope.scope.namespace: exported.record_count},
        tenant_id_counts={envelope.scope.tenant_id or "(missing)": exported.record_count},
        partition_key_counts={envelope.scope.resolved_partition_key(): exported.record_count},
        node_type_counts=_counts(item.type for item in envelope.nodes),
        predicate_counts=_counts(item.predicate for item in envelope.edges),
        assertion_status_counts=_counts(item.status for item in envelope.assertions),
        review_status_counts=_counts(item.status for item in envelope.reviews),
        source_grounding=grounding,
        dangling_edge_count=dangling_edges,
        orphan_assertion_count=orphan_assertions,
        orphan_review_count=orphan_reviews,
        dangling_assertion_reference_count=dangling_assertions,
        dangling_projection_start_node_count=dangling_starts,
        duplicate_node_id_count=duplicate_counts["node"],
        duplicate_edge_id_count=duplicate_counts["edge"],
        duplicate_assertion_id_count=duplicate_counts["assertion"],
        duplicate_review_id_count=duplicate_counts["review"],
        duplicate_projection_id_count=duplicate_counts["projection"],
        warning_count=len(warnings),
        warnings=tuple(warnings),
        anomaly_count=anomaly_count,
        returned_anomaly_count=len(anomalies),
        anomalies=tuple(anomalies),
    )


def _validate_maximum(value: int, name: str) -> None:
    if not 1 <= value <= MAX_COLLECTION_SNAPSHOT_RECORDS:
        raise ValueError(
            f"{name} must be between 1 and {MAX_COLLECTION_SNAPSHOT_RECORDS}."
        )


def _validate_traversal(request: GraphTraversalRequest) -> None:
    if not request.start_node_ids:
        raise ValueError("Graph traversal requires at least one startNodeId.")
    if request.profile.direction not in {"outgoing", "incoming", "both", ""}:
        raise ValueError("Graph traversal direction is invalid.")
    if request.profile.max_depth < 0:
        raise ValueError("Graph traversal maxDepth cannot be negative.")
    if request.profile.limit <= 0 or request.profile.edge_limit <= 0:
        raise ValueError("Graph traversal limits must be greater than zero.")
    _validate_maximum(
        request.max_records or MAX_COLLECTION_SNAPSHOT_RECORDS,
        "Graph traversal maxRecords",
    )


def _validate_inspection(request: GraphInspectionRequest) -> None:
    _validate_maximum(
        request.max_records or MAX_COLLECTION_SNAPSHOT_RECORDS,
        "Graph inspection maxRecords",
    )
    if not 0 <= request.anomaly_limit <= MAX_GRAPH_ANOMALIES:
        raise ValueError(
            f"Graph inspection anomalyLimit must be between 0 and {MAX_GRAPH_ANOMALIES}."
        )


def _coverage(grounded: int, total: int) -> float:
    return grounded / total if total else 1.0


def _counts(values: Iterable[str]) -> Mapping[str, int]:
    return dict(sorted(Counter(value or "(missing)" for value in values).items()))


def _pointer_values(document: Mapping[str, Any], pointer: str) -> tuple[str, ...]:
    if not pointer.startswith("/"):
        raise ValueError("Graph doctor seedJsonPointers must be JSON pointers.")
    value: Any = document
    for raw in pointer.split("/")[1:]:
        key = raw.replace("~1", "/").replace("~0", "~")
        if isinstance(value, Mapping) and key in value:
            value = value[key]
        elif isinstance(value, list) and key.isdigit() and int(key) < len(value):
            value = value[int(key)]
        else:
            return ()
    if isinstance(value, list):
        return tuple(
            str(item).strip()
            for item in value
            if isinstance(item, (str, int, float, bool))
            and str(item).strip()
        )
    return (
        (str(value).strip(),)
        if isinstance(value, (str, int, float, bool)) and str(value).strip()
        else ()
    )


def _utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        raise ValueError("Graph service clock must return an offset-aware datetime.")
    return value.astimezone(timezone.utc)
