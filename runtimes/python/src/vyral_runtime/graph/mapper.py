from __future__ import annotations

from base64 import urlsafe_b64encode
from collections import Counter
from datetime import datetime, timezone
from hashlib import sha256
from typing import Callable, Iterable

from ..local import MAX_COLLECTION_SNAPSHOT_RECORDS, RecordCollectionPolicy, VyralRecord
from ..local.models import JSONObject
from .models import (
    GRAPH_INDEXED_METADATA,
    GRAPH_RECORD_TYPES,
    VyralGraphAssertion,
    VyralGraphEdge,
    VyralGraphEnvelope,
    VyralGraphNode,
    VyralGraphProjection,
    VyralGraphReviewEvent,
    VyralGraphScope,
    VyralGraphSourceSpan,
)


def create_graph_collection_policy(collection: str) -> RecordCollectionPolicy:
    return RecordCollectionPolicy(
        name=collection,
        indexed_metadata=GRAPH_INDEXED_METADATA,
    )


def is_graph_collection_policy(policy: RecordCollectionPolicy) -> bool:
    return set(GRAPH_INDEXED_METADATA).issubset(policy.indexed_metadata)


def missing_graph_metadata_indexes(
    policy: RecordCollectionPolicy,
) -> tuple[str, ...]:
    indexed = set(policy.indexed_metadata)
    return tuple(path for path in GRAPH_INDEXED_METADATA if path not in indexed)


def graph_to_records(
    envelope: VyralGraphEnvelope,
    *,
    clock: Callable[[], datetime] | None = None,
) -> tuple[VyralRecord, ...]:
    normalized = normalize_graph_envelope(envelope)
    validate_graph_envelope(normalized)
    now = (clock or (lambda: datetime.now(timezone.utc)))()
    if now.tzinfo is None:
        raise ValueError("Graph mapper clock must return an offset-aware timestamp.")
    scope_key = "|".join(
        (
            normalized.scope.graph_id,
            normalized.scope.namespace,
            normalized.scope.collection,
            normalized.scope.tenant_id,
            normalized.scope.resolved_partition_key(),
        )
    )
    records = [
        _record(
            normalized,
            "graph.envelope",
            "envelope",
            scope_key,
            {"schema": normalized.schema, "scope": normalized.scope.to_dict(), "metadata": normalized.metadata, "text": f"graph envelope {normalized.scope.graph_id}"},
            (),
            now,
        )
    ]
    for node in normalized.nodes:
        records.append(_entity_record(normalized, "graph.node", "node", node.id, node.to_dict(), "node", node.source_spans, (node.id, node.type, node.label), {"nodeId": node.id, "nodeType": node.type}, now))
    for edge in normalized.edges:
        records.append(_entity_record(normalized, "graph.edge", "edge", edge.id, edge.to_dict(), "edge", edge.source_spans, (edge.id, edge.source_id, edge.predicate, edge.target_id, edge.label), {"edgeId": edge.id, "sourceId": edge.source_id, "targetId": edge.target_id, "predicate": edge.predicate}, now))
    for assertion in normalized.assertions:
        records.append(_entity_record(normalized, "graph.assertion", "assertion", assertion.id, assertion.to_dict(), "assertion", assertion.source_spans, (assertion.id, assertion.subject_kind, assertion.subject_id, assertion.status, assertion.method, assertion.actor), {"assertionId": assertion.id, "subjectId": assertion.subject_id, "subjectKind": assertion.subject_kind, "assertionStatus": assertion.status}, now))
    for review in normalized.reviews:
        records.append(_entity_record(normalized, "graph.review", "review", review.id, review.to_dict(), "review", (), (review.id, review.subject_kind, review.subject_id, review.status, review.reviewer, review.notes), {"reviewId": review.id, "subjectId": review.subject_id, "subjectKind": review.subject_kind, "reviewStatus": review.status}, now))
    for projection in normalized.projections:
        records.append(_entity_record(normalized, "graph.projection", "projection", projection.id, projection.to_dict(), "projection", (), (projection.id, projection.profile.id), {"projectionId": projection.id}, now))
    return tuple(records)


def graph_from_records(
    records: Iterable[VyralRecord],
    *,
    include_projections: bool = True,
) -> VyralGraphEnvelope:
    selected = sorted(
        (record for record in records if record.type.startswith("graph.")),
        key=lambda record: (record.type, record.id),
    )
    envelope_record = next(
        (record for record in selected if record.type == "graph.envelope"),
        None,
    )
    if envelope_record is not None:
        content = envelope_record.content or {}
        schema = str(content.get("schema") or "roman.graph.v1")
        raw_scope = content.get("scope")
        scope = (
            VyralGraphScope.from_value(raw_scope)
            if isinstance(raw_scope, dict)
            else _scope_from_record(envelope_record)
        )
        metadata = content.get("metadata")
        graph_metadata = dict(metadata) if isinstance(metadata, dict) else None
    elif selected:
        schema = selected[0].schema_version or "roman.graph.v1"
        scope = _scope_from_record(selected[0])
        graph_metadata = None
    else:
        return VyralGraphEnvelope(scope=VyralGraphScope())
    nodes: list[VyralGraphNode] = []
    edges: list[VyralGraphEdge] = []
    assertions: list[VyralGraphAssertion] = []
    reviews: list[VyralGraphReviewEvent] = []
    projections: list[VyralGraphProjection] = []
    for record in selected:
        content = record.content or {}
        if record.type == "graph.node" and isinstance(content.get("node"), dict):
            nodes.append(VyralGraphNode.from_value(content["node"]))
        elif record.type == "graph.edge" and isinstance(content.get("edge"), dict):
            edges.append(VyralGraphEdge.from_value(content["edge"]))
        elif record.type == "graph.assertion" and isinstance(content.get("assertion"), dict):
            assertions.append(VyralGraphAssertion.from_value(content["assertion"]))
        elif record.type == "graph.review" and isinstance(content.get("review"), dict):
            reviews.append(VyralGraphReviewEvent.from_value(content["review"]))
        elif include_projections and record.type == "graph.projection" and isinstance(content.get("projection"), dict):
            projections.append(VyralGraphProjection.from_value(content["projection"]))
    return normalize_graph_envelope(
        VyralGraphEnvelope(
            schema=schema,
            scope=scope,
            metadata=graph_metadata,
            nodes=tuple(nodes),
            edges=tuple(edges),
            assertions=tuple(assertions),
            reviews=tuple(reviews),
            projections=tuple(projections),
        )
    )


def normalize_graph_envelope(envelope: VyralGraphEnvelope) -> VyralGraphEnvelope:
    scope = envelope.scope
    return VyralGraphEnvelope(
        schema=envelope.schema.strip() or "roman.graph.v1",
        scope=VyralGraphScope(
            graph_id=scope.graph_id.strip() or "default",
            namespace=scope.namespace.strip() or "default",
            collection=scope.collection.strip() or "default",
            tenant_id=scope.tenant_id.strip(),
            partition_key=scope.partition_key.strip(),
            additional_properties=dict(scope.additional_properties),
        ),
        metadata=envelope.metadata,
        nodes=envelope.nodes,
        edges=envelope.edges,
        assertions=envelope.assertions,
        reviews=envelope.reviews,
        projections=envelope.projections,
    )


def validate_graph_envelope(envelope: VyralGraphEnvelope) -> None:
    total = 1 + sum(
        len(entries)
        for entries in (
            envelope.nodes,
            envelope.edges,
            envelope.assertions,
            envelope.reviews,
            envelope.projections,
        )
    )
    if total > MAX_COLLECTION_SNAPSHOT_RECORDS:
        raise ValueError(
            f"Graph envelope supports at most {MAX_COLLECTION_SNAPSHOT_RECORDS} records."
        )
    for name, identifiers in (
        ("node", (item.id for item in envelope.nodes)),
        ("edge", (item.id for item in envelope.edges)),
        ("assertion", (item.id for item in envelope.assertions)),
        ("review", (item.id for item in envelope.reviews)),
        ("projection", (item.id for item in envelope.projections)),
    ):
        values = tuple(identifiers)
        if any(not value.strip() for value in values):
            raise ValueError(f"Graph {name} id is required.")
        duplicate = next((key for key, count in Counter(values).items() if count > 1), None)
        if duplicate is not None:
            raise ValueError(f"Graph {name} id {duplicate!r} appears more than once.")


def graph_record_id(record_type: str, subject_id: str) -> str:
    if record_type not in GRAPH_RECORD_TYPES:
        raise ValueError(f"Unsupported graph record type {record_type!r}.")
    selected = subject_id if subject_id.strip() else "default"
    encoded = urlsafe_b64encode(selected.encode("utf-8")).decode("ascii").rstrip("=")
    if len(encoded) > 900:
        encoded = "sha256-" + sha256(subject_id.encode("utf-8")).hexdigest()
    return f"g:{record_type.removeprefix('graph.')}:{encoded}"


def _entity_record(
    envelope: VyralGraphEnvelope,
    record_type: str,
    kind: str,
    subject_id: str,
    entity: JSONObject,
    content_key: str,
    spans: Iterable[VyralGraphSourceSpan],
    text_parts: Iterable[str | None],
    metadata: JSONObject,
    now: datetime,
) -> VyralRecord:
    content: JSONObject = {
        content_key: entity,
        "text": " ".join(value for value in text_parts if value and value.strip()),
    }
    return _record(envelope, record_type, kind, subject_id, content, spans, now, metadata)


def _record(
    envelope: VyralGraphEnvelope,
    record_type: str,
    kind: str,
    subject_id: str,
    content: JSONObject,
    spans: Iterable[VyralGraphSourceSpan],
    now: datetime,
    specific_metadata: JSONObject | None = None,
) -> VyralRecord:
    scope = envelope.scope
    partition = scope.resolved_partition_key()
    metadata: JSONObject = {
        "graphKind": kind,
        "graphId": scope.graph_id,
        "namespace": scope.namespace,
        "scopeCollection": scope.collection,
        "tenantId": scope.tenant_id,
        "graphPartitionKey": partition,
        "subjectId": subject_id,
    }
    metadata.update(specific_metadata or {})
    content = dict(content)
    content.update({"kind": kind, "scope": scope.to_dict()})
    sources = tuple(_source(span) for span in spans)
    return VyralRecord(
        id=graph_record_id(record_type, subject_id),
        partition_key=partition,
        type=record_type,
        schema_version=envelope.schema,
        metadata=metadata,
        content=content,
        sources=sources,
        created_at=now,
        updated_at=now,
    )


def _source(span: VyralGraphSourceSpan) -> JSONObject:
    source_ref = span.source_ref.strip()
    return {
        "id": source_ref or "graph-source",
        "kind": "graphSourceSpan",
        "uri": source_ref or "graph://source",
        "label": span.locator,
        "span": {
            "charStart": span.char_start,
            "charEnd": span.char_end,
            "anchor": span.locator,
            "unit": span.unit,
            "textHash": span.text_hash,
        },
    }


def _scope_from_record(record: VyralRecord) -> VyralGraphScope:
    metadata = record.metadata or {}
    return VyralGraphScope(
        graph_id=str(metadata.get("graphId") or "default"),
        namespace=str(metadata.get("namespace") or "default"),
        collection=str(metadata.get("scopeCollection") or "default"),
        tenant_id=str(metadata.get("tenantId") or ""),
        partition_key=str(metadata.get("graphPartitionKey") or record.partition_key),
    )
