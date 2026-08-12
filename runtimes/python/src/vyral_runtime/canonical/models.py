from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
import base64
import json
from typing import Any, Mapping, TypeVar, cast

from .._datetime import parse_iso_datetime
from ..contracts import JSONValue


MAX_TRANSACTION_MUTATIONS = 100
MAX_TRANSACTION_FENCES = 100
MAX_TRANSACTION_OUTBOX_EVENTS = 100
MAX_DOCUMENT_BYTES = 1_048_576
MAX_OUTBOX_PAYLOAD_BYTES = 1_048_576
MAX_QUERY_LIMIT = 1_000
MAX_SNAPSHOT_BYTES = 67_108_864
DEFAULT_OUTBOX_RETRY_DELAY_SECONDS = 5.0
DEFAULT_ARCHIVE_CHUNK_BYTES = 8 * 1024 * 1024
MAX_ARCHIVE_CHUNK_BYTES = 16 * 1024 * 1024
MAX_ARCHIVE_CHUNKS = 1_024
CANONICAL_ARCHIVE_PROFILE = "vyral.canonical.archive.v1"
CANONICAL_PREFLIGHT_PROFILE = (
    "vyral.canonical.data-plane-preflight.v1"
)
EPOCH_TIMESTAMP = "0001-01-01T00:00:00Z"

T = TypeVar("T")


def utc_timestamp(value: datetime | str, name: str = "timestamp") -> str:
    """Return portable UTC wire text while preserving imported precision."""

    if isinstance(value, datetime):
        selected = value
        if selected.tzinfo is None:
            selected = selected.replace(tzinfo=timezone.utc)
        selected = selected.astimezone(timezone.utc)
        return selected.isoformat().replace("+00:00", "Z")
    if not isinstance(value, str) or not value.strip():
        raise TypeError(f"{name} must be an RFC 3339 timestamp.")
    text = value.strip()
    try:
        parsed = parse_iso_datetime(text)
    except ValueError:
        if text == EPOCH_TIMESTAMP:
            return text
        raise ValueError(f"{name} must be an RFC 3339 timestamp.") from None
    if parsed.tzinfo is None:
        raise ValueError(f"{name} must include a UTC offset.")
    if parsed.utcoffset() != timezone.utc.utcoffset(parsed):
        return parsed.astimezone(timezone.utc).isoformat().replace(
            "+00:00", "Z"
        )
    return text[:-6] + "Z" if text.endswith("+00:00") else text


def parse_timestamp(value: str) -> datetime:
    text = utc_timestamp(value)
    return parse_iso_datetime(text)


def _mapping(value: object, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise TypeError(f"{name} must be an object.")
    return cast(Mapping[str, Any], value)


def _text(value: object, name: str, *, empty: bool = False) -> str:
    if not isinstance(value, str):
        raise TypeError(f"{name} must be a string.")
    if not empty and not value.strip():
        raise ValueError(f"{name} is required.")
    return value


def _optional_text(value: object, name: str) -> str | None:
    if value is None:
        return None
    return _text(value, name)


def _integer(
    value: object,
    name: str,
    *,
    default: int | None = None,
) -> int:
    if value is None and default is not None:
        return default
    if isinstance(value, bool) or not isinstance(value, int):
        raise TypeError(f"{name} must be an integer.")
    return value


def _number(
    value: object,
    name: str,
    *,
    default: float | None = None,
) -> float:
    if value is None and default is not None:
        return default
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError(f"{name} must be a number.")
    return float(value)


def _boolean(
    value: object,
    name: str,
    *,
    default: bool = False,
) -> bool:
    if value is None:
        return default
    if not isinstance(value, bool):
        raise TypeError(f"{name} must be a boolean.")
    return value


def _json(value: object, name: str) -> JSONValue:
    try:
        encoded = json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
        )
        return cast(JSONValue, json.loads(encoded))
    except (TypeError, ValueError):
        raise TypeError(f"{name} must be a JSON value.") from None


def _string_map(value: object, name: str) -> dict[str, str]:
    if value is None:
        return {}
    item = _mapping(value, name)
    result: dict[str, str] = {}
    for key, raw in item.items():
        if not isinstance(key, str) or not isinstance(raw, str):
            raise TypeError(f"{name} must contain string keys and values.")
        result[key] = raw
    return result


def _sequence(value: object, name: str) -> list[object]:
    if value is None:
        return []
    if not isinstance(value, list):
        raise TypeError(f"{name} must be an array.")
    return cast(list[object], value)


@dataclass(frozen=True)
class CanonicalDocument:
    tenant_id: str
    document_type: str
    id: str
    schema_version: str
    data: JSONValue = None
    indexes: Mapping[str, str] = field(default_factory=dict)
    revision: int = 0
    etag: str = ""
    deleted: bool = False
    created_at_utc: str = EPOCH_TIMESTAMP
    updated_at_utc: str = EPOCH_TIMESTAMP

    @classmethod
    def from_value(
        cls, value: CanonicalDocument | Mapping[str, Any]
    ) -> CanonicalDocument:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical document")
        return cls(
            tenant_id=_text(item.get("tenantId"), "document.tenantId"),
            document_type=_text(
                item.get("documentType"), "document.documentType"
            ),
            id=_text(item.get("id"), "document.id"),
            schema_version=_text(
                item.get("schemaVersion"), "document.schemaVersion"
            ),
            data=_json(item.get("data"), "document.data"),
            indexes=_string_map(
                item.get("indexes"), "document.indexes"
            ),
            revision=_integer(
                item.get("revision"), "document.revision", default=0
            ),
            etag=_text(
                item.get("etag", ""), "document.etag", empty=True
            ),
            deleted=_boolean(
                item.get("deleted"), "document.deleted"
            ),
            created_at_utc=utc_timestamp(
                cast(
                    str,
                    item.get("createdAtUtc", EPOCH_TIMESTAMP),
                ),
                "document.createdAtUtc",
            ),
            updated_at_utc=utc_timestamp(
                cast(
                    str,
                    item.get("updatedAtUtc", EPOCH_TIMESTAMP),
                ),
                "document.updatedAtUtc",
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "tenantId": self.tenant_id,
            "documentType": self.document_type,
            "id": self.id,
            "schemaVersion": self.schema_version,
            "data": _json(self.data, "document.data"),
            "indexes": dict(self.indexes),
            "revision": self.revision,
            "etag": self.etag,
            "deleted": self.deleted,
            "createdAtUtc": self.created_at_utc,
            "updatedAtUtc": self.updated_at_utc,
        }


@dataclass(frozen=True)
class CanonicalWritePrecondition:
    expected_revision: int | None = None
    must_not_exist: bool = False
    must_exist: bool = False

    @classmethod
    def from_value(
        cls,
        value: CanonicalWritePrecondition | Mapping[str, Any] | None,
    ) -> CanonicalWritePrecondition | None:
        if value is None or isinstance(value, cls):
            return value
        item = _mapping(value, "canonical write precondition")
        raw_revision = item.get("expectedRevision")
        return cls(
            expected_revision=(
                _integer(
                    raw_revision, "precondition.expectedRevision"
                )
                if raw_revision is not None
                else None
            ),
            must_not_exist=_boolean(
                item.get("mustNotExist"),
                "precondition.mustNotExist",
            ),
            must_exist=_boolean(
                item.get("mustExist"), "precondition.mustExist"
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "expectedRevision": self.expected_revision,
            "mustNotExist": self.must_not_exist,
            "mustExist": self.must_exist,
        }


@dataclass(frozen=True)
class CanonicalMutation:
    operation: str = "upsert"
    document: CanonicalDocument | None = None
    document_type: str | None = None
    id: str | None = None
    precondition: CanonicalWritePrecondition | None = None

    @classmethod
    def from_value(
        cls, value: CanonicalMutation | Mapping[str, Any]
    ) -> CanonicalMutation:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical mutation")
        raw_document = item.get("document")
        return cls(
            operation=_text(
                item.get("operation", "upsert"),
                "mutation.operation",
            ),
            document=(
                CanonicalDocument.from_value(
                    _mapping(raw_document, "mutation.document")
                )
                if raw_document is not None
                else None
            ),
            document_type=_optional_text(
                item.get("documentType"), "mutation.documentType"
            ),
            id=_optional_text(item.get("id"), "mutation.id"),
            precondition=CanonicalWritePrecondition.from_value(
                cast(
                    Mapping[str, Any] | None,
                    item.get("precondition"),
                )
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "operation": self.operation,
            "document": (
                self.document.to_dict()
                if self.document is not None
                else None
            ),
            "documentType": self.document_type,
            "id": self.id,
            "precondition": (
                self.precondition.to_dict()
                if self.precondition is not None
                else None
            ),
        }


@dataclass(frozen=True)
class CanonicalFenceMutation:
    name: str
    value: str
    owner_document_type: str
    owner_document_id: str
    operation: str = "claim"

    @classmethod
    def from_value(
        cls, value: CanonicalFenceMutation | Mapping[str, Any]
    ) -> CanonicalFenceMutation:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical fence mutation")
        return cls(
            operation=_text(
                item.get("operation", "claim"),
                "fence.operation",
            ),
            name=_text(item.get("name"), "fence.name"),
            value=_text(item.get("value"), "fence.value"),
            owner_document_type=_text(
                item.get("ownerDocumentType"),
                "fence.ownerDocumentType",
            ),
            owner_document_id=_text(
                item.get("ownerDocumentId"),
                "fence.ownerDocumentId",
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "operation": self.operation,
            "name": self.name,
            "value": self.value,
            "ownerDocumentType": self.owner_document_type,
            "ownerDocumentId": self.owner_document_id,
        }


@dataclass(frozen=True)
class CanonicalFence:
    tenant_id: str
    name: str
    value: str
    owner_document_type: str
    owner_document_id: str
    created_at_utc: str
    updated_at_utc: str

    @classmethod
    def from_value(
        cls, value: CanonicalFence | Mapping[str, Any]
    ) -> CanonicalFence:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical fence")
        return cls(
            tenant_id=_text(item.get("tenantId"), "fence.tenantId"),
            name=_text(item.get("name"), "fence.name"),
            value=_text(item.get("value"), "fence.value"),
            owner_document_type=_text(
                item.get("ownerDocumentType"),
                "fence.ownerDocumentType",
            ),
            owner_document_id=_text(
                item.get("ownerDocumentId"),
                "fence.ownerDocumentId",
            ),
            created_at_utc=utc_timestamp(
                cast(str, item.get("createdAtUtc")),
                "fence.createdAtUtc",
            ),
            updated_at_utc=utc_timestamp(
                cast(str, item.get("updatedAtUtc")),
                "fence.updatedAtUtc",
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "tenantId": self.tenant_id,
            "name": self.name,
            "value": self.value,
            "ownerDocumentType": self.owner_document_type,
            "ownerDocumentId": self.owner_document_id,
            "createdAtUtc": self.created_at_utc,
            "updatedAtUtc": self.updated_at_utc,
        }


@dataclass(frozen=True)
class CanonicalOutboxWrite:
    topic: str
    key: str
    id: str | None = None
    payload: JSONValue = None
    headers: Mapping[str, str] = field(default_factory=dict)
    not_before_utc: str | None = None
    max_delivery_attempts: int | None = None

    @classmethod
    def from_value(
        cls, value: CanonicalOutboxWrite | Mapping[str, Any]
    ) -> CanonicalOutboxWrite:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical outbox write")
        raw_not_before = item.get("notBeforeUtc")
        raw_attempts = item.get("maxDeliveryAttempts")
        return cls(
            id=_optional_text(item.get("id"), "outbox.id"),
            topic=_text(item.get("topic"), "outbox.topic"),
            key=_text(item.get("key"), "outbox.key"),
            payload=_json(item.get("payload"), "outbox.payload"),
            headers=_string_map(
                item.get("headers"), "outbox.headers"
            ),
            not_before_utc=(
                utc_timestamp(
                    cast(str, raw_not_before),
                    "outbox.notBeforeUtc",
                )
                if raw_not_before is not None
                else None
            ),
            max_delivery_attempts=(
                _integer(
                    raw_attempts, "outbox.maxDeliveryAttempts"
                )
                if raw_attempts is not None
                else None
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "id": self.id,
            "topic": self.topic,
            "key": self.key,
            "payload": _json(self.payload, "outbox.payload"),
            "headers": dict(self.headers),
            "notBeforeUtc": self.not_before_utc,
            "maxDeliveryAttempts": self.max_delivery_attempts,
        }


@dataclass(frozen=True)
class CanonicalOutboxEvent:
    id: str
    tenant_id: str
    transaction_id: str
    topic: str
    key: str
    payload: JSONValue = None
    headers: Mapping[str, str] = field(default_factory=dict)
    not_before_utc: str | None = None
    delivery_count: int = 0
    delivered_at_utc: str | None = None
    lease_owner: str | None = None
    lease_expires_at_utc: str | None = None
    max_delivery_attempts: int | None = None
    dead_lettered_at_utc: str | None = None
    last_error: str | None = None

    @classmethod
    def from_value(
        cls, value: CanonicalOutboxEvent | Mapping[str, Any]
    ) -> CanonicalOutboxEvent:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical outbox event")

        def timestamp(name: str) -> str | None:
            raw = item.get(name)
            return (
                utc_timestamp(cast(str, raw), f"outbox.{name}")
                if raw is not None
                else None
            )

        raw_attempts = item.get("maxDeliveryAttempts")
        return cls(
            id=_text(item.get("id"), "outbox.id"),
            tenant_id=_text(
                item.get("tenantId"), "outbox.tenantId"
            ),
            transaction_id=_text(
                item.get("transactionId"), "outbox.transactionId"
            ),
            topic=_text(item.get("topic"), "outbox.topic"),
            key=_text(item.get("key"), "outbox.key"),
            payload=_json(item.get("payload"), "outbox.payload"),
            headers=_string_map(
                item.get("headers"), "outbox.headers"
            ),
            not_before_utc=timestamp("notBeforeUtc"),
            delivery_count=_integer(
                item.get("deliveryCount"),
                "outbox.deliveryCount",
                default=0,
            ),
            delivered_at_utc=timestamp("deliveredAtUtc"),
            lease_owner=_optional_text(
                item.get("leaseOwner"), "outbox.leaseOwner"
            ),
            lease_expires_at_utc=timestamp("leaseExpiresAtUtc"),
            max_delivery_attempts=(
                _integer(
                    raw_attempts, "outbox.maxDeliveryAttempts"
                )
                if raw_attempts is not None
                else None
            ),
            dead_lettered_at_utc=timestamp(
                "deadLetteredAtUtc"
            ),
            last_error=_optional_text(
                item.get("lastError"), "outbox.lastError"
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "id": self.id,
            "tenantId": self.tenant_id,
            "transactionId": self.transaction_id,
            "topic": self.topic,
            "key": self.key,
            "payload": _json(self.payload, "outbox.payload"),
            "headers": dict(self.headers),
            "notBeforeUtc": self.not_before_utc,
            "deliveryCount": self.delivery_count,
            "deliveredAtUtc": self.delivered_at_utc,
            "leaseOwner": self.lease_owner,
            "leaseExpiresAtUtc": self.lease_expires_at_utc,
            "maxDeliveryAttempts": self.max_delivery_attempts,
            "deadLetteredAtUtc": self.dead_lettered_at_utc,
            "lastError": self.last_error,
        }


@dataclass(frozen=True)
class CanonicalTransactionRequest:
    tenant_id: str
    idempotency_key: str
    correlation_id: str | None = None
    actor: str | None = None
    mutations: tuple[CanonicalMutation, ...] = ()
    fences: tuple[CanonicalFenceMutation, ...] = ()
    outbox: tuple[CanonicalOutboxWrite, ...] = ()

    @classmethod
    def from_value(
        cls,
        value: CanonicalTransactionRequest | Mapping[str, Any],
    ) -> CanonicalTransactionRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical transaction request")
        return cls(
            tenant_id=_text(
                item.get("tenantId"), "transaction.tenantId"
            ),
            idempotency_key=_text(
                item.get("idempotencyKey"),
                "transaction.idempotencyKey",
            ),
            correlation_id=_optional_text(
                item.get("correlationId"),
                "transaction.correlationId",
            ),
            actor=_optional_text(
                item.get("actor"), "transaction.actor"
            ),
            mutations=tuple(
                CanonicalMutation.from_value(
                    _mapping(raw, "transaction.mutations[]")
                )
                for raw in _sequence(
                    item.get("mutations"), "transaction.mutations"
                )
            ),
            fences=tuple(
                CanonicalFenceMutation.from_value(
                    _mapping(raw, "transaction.fences[]")
                )
                for raw in _sequence(
                    item.get("fences"), "transaction.fences"
                )
            ),
            outbox=tuple(
                CanonicalOutboxWrite.from_value(
                    _mapping(raw, "transaction.outbox[]")
                )
                for raw in _sequence(
                    item.get("outbox"), "transaction.outbox"
                )
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "tenantId": self.tenant_id,
            "idempotencyKey": self.idempotency_key,
            "correlationId": self.correlation_id,
            "actor": self.actor,
            "mutations": [
                item.to_dict() for item in self.mutations
            ],
            "fences": [item.to_dict() for item in self.fences],
            "outbox": [item.to_dict() for item in self.outbox],
        }


@dataclass(frozen=True)
class CanonicalTransactionResult:
    transaction_id: str
    tenant_id: str
    idempotency_key: str
    replayed: bool
    committed_at_utc: str
    correlation_id: str | None = None
    actor: str | None = None
    documents: tuple[CanonicalDocument, ...] = ()
    outbox: tuple[CanonicalOutboxEvent, ...] = ()

    @classmethod
    def from_value(
        cls, value: CanonicalTransactionResult | Mapping[str, Any]
    ) -> CanonicalTransactionResult:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical transaction result")
        return cls(
            transaction_id=_text(
                item.get("transactionId"), "result.transactionId"
            ),
            tenant_id=_text(
                item.get("tenantId"), "result.tenantId"
            ),
            idempotency_key=_text(
                item.get("idempotencyKey"),
                "result.idempotencyKey",
            ),
            correlation_id=_optional_text(
                item.get("correlationId"), "result.correlationId"
            ),
            actor=_optional_text(
                item.get("actor"), "result.actor"
            ),
            replayed=_boolean(
                item.get("replayed"), "result.replayed"
            ),
            committed_at_utc=utc_timestamp(
                cast(str, item.get("committedAtUtc")),
                "result.committedAtUtc",
            ),
            documents=tuple(
                CanonicalDocument.from_value(
                    _mapping(raw, "result.documents[]")
                )
                for raw in _sequence(
                    item.get("documents"), "result.documents"
                )
            ),
            outbox=tuple(
                CanonicalOutboxEvent.from_value(
                    _mapping(raw, "result.outbox[]")
                )
                for raw in _sequence(
                    item.get("outbox"), "result.outbox"
                )
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "transactionId": self.transaction_id,
            "tenantId": self.tenant_id,
            "idempotencyKey": self.idempotency_key,
            "correlationId": self.correlation_id,
            "actor": self.actor,
            "replayed": self.replayed,
            "committedAtUtc": self.committed_at_utc,
            "documents": [
                item.to_dict() for item in self.documents
            ],
            "outbox": [item.to_dict() for item in self.outbox],
        }


@dataclass(frozen=True)
class CanonicalDocumentIndexRange:
    name: str
    greater_than_or_equal: str | None = None
    less_than_or_equal: str | None = None

    @classmethod
    def from_value(
        cls,
        value: CanonicalDocumentIndexRange | Mapping[str, Any] | None,
    ) -> CanonicalDocumentIndexRange | None:
        if value is None or isinstance(value, cls):
            return value
        item = _mapping(value, "canonical document index range")
        return cls(
            name=_text(item.get("name"), "range.name"),
            greater_than_or_equal=_optional_text(
                item.get("greaterThanOrEqual"),
                "range.greaterThanOrEqual",
            ),
            less_than_or_equal=_optional_text(
                item.get("lessThanOrEqual"),
                "range.lessThanOrEqual",
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "name": self.name,
            "greaterThanOrEqual": self.greater_than_or_equal,
            "lessThanOrEqual": self.less_than_or_equal,
        }


@dataclass(frozen=True)
class CanonicalDocumentQuery:
    tenant_id: str
    document_type: str | None = None
    indexes: Mapping[str, str] = field(default_factory=dict)
    index_range: CanonicalDocumentIndexRange | None = None
    order_by_index: str | None = None
    order_direction: str = "ascending"
    include_deleted: bool = False
    limit: int | None = None
    continuation_token: str | None = None

    @classmethod
    def from_value(
        cls, value: CanonicalDocumentQuery | Mapping[str, Any]
    ) -> CanonicalDocumentQuery:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical document query")
        raw_limit = item.get("limit")
        return cls(
            tenant_id=_text(item.get("tenantId"), "query.tenantId"),
            document_type=_optional_text(
                item.get("documentType"), "query.documentType"
            ),
            indexes=_string_map(
                item.get("indexes"), "query.indexes"
            ),
            index_range=CanonicalDocumentIndexRange.from_value(
                cast(
                    Mapping[str, Any] | None,
                    item.get("indexRange"),
                )
            ),
            order_by_index=_optional_text(
                item.get("orderByIndex"), "query.orderByIndex"
            ),
            order_direction=_text(
                item.get("orderDirection", "ascending"),
                "query.orderDirection",
            ),
            include_deleted=_boolean(
                item.get("includeDeleted"), "query.includeDeleted"
            ),
            limit=(
                _integer(raw_limit, "query.limit")
                if raw_limit is not None
                else None
            ),
            continuation_token=_optional_text(
                item.get("continuationToken"),
                "query.continuationToken",
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "tenantId": self.tenant_id,
            "documentType": self.document_type,
            "indexes": dict(self.indexes),
            "indexRange": (
                self.index_range.to_dict()
                if self.index_range is not None
                else None
            ),
            "orderByIndex": self.order_by_index,
            "orderDirection": self.order_direction,
            "includeDeleted": self.include_deleted,
            "limit": self.limit,
            "continuationToken": self.continuation_token,
        }


@dataclass(frozen=True)
class CanonicalDocumentQueryResult:
    items: tuple[CanonicalDocument, ...]
    continuation_token: str | None = None

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "items": [item.to_dict() for item in self.items],
            "continuationToken": self.continuation_token,
        }


@dataclass(frozen=True)
class CanonicalDocumentRevision:
    tenant_id: str
    document_type: str
    id: str
    revision: int
    transaction_id: str
    operation: str
    document: CanonicalDocument
    recorded_at_utc: str

    @classmethod
    def from_value(
        cls, value: CanonicalDocumentRevision | Mapping[str, Any]
    ) -> CanonicalDocumentRevision:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical document revision")
        return cls(
            tenant_id=_text(
                item.get("tenantId"), "revision.tenantId"
            ),
            document_type=_text(
                item.get("documentType"), "revision.documentType"
            ),
            id=_text(item.get("id"), "revision.id"),
            revision=_integer(
                item.get("revision"), "revision.revision"
            ),
            transaction_id=_text(
                item.get("transactionId"),
                "revision.transactionId",
            ),
            operation=_text(
                item.get("operation"), "revision.operation"
            ),
            document=CanonicalDocument.from_value(
                _mapping(
                    item.get("document"), "revision.document"
                )
            ),
            recorded_at_utc=utc_timestamp(
                cast(str, item.get("recordedAtUtc")),
                "revision.recordedAtUtc",
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "tenantId": self.tenant_id,
            "documentType": self.document_type,
            "id": self.id,
            "revision": self.revision,
            "transactionId": self.transaction_id,
            "operation": self.operation,
            "document": self.document.to_dict(),
            "recordedAtUtc": self.recorded_at_utc,
        }


@dataclass(frozen=True)
class CanonicalOutboxLeaseRequest:
    tenant_id: str
    consumer_id: str
    max_items: int = 10
    lease_seconds: float = 60.0

    @classmethod
    def from_value(
        cls,
        value: CanonicalOutboxLeaseRequest | Mapping[str, Any],
    ) -> CanonicalOutboxLeaseRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical outbox lease request")
        return cls(
            tenant_id=_text(
                item.get("tenantId"), "lease.tenantId"
            ),
            consumer_id=_text(
                item.get("consumerId"), "lease.consumerId"
            ),
            max_items=_integer(
                item.get("maxItems"),
                "lease.maxItems",
                default=10,
            ),
            lease_seconds=_number(
                item.get("leaseSeconds"),
                "lease.leaseSeconds",
                default=60.0,
            ),
        )


@dataclass(frozen=True)
class CanonicalOutboxLease:
    event: CanonicalOutboxEvent
    lease_token: str = field(repr=False)
    expires_at_utc: str

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "event": self.event.to_dict(),
            "leaseToken": self.lease_token,
            "expiresAtUtc": self.expires_at_utc,
        }

    def safe_summary(self) -> dict[str, JSONValue]:
        return {
            "eventId": self.event.id,
            "tenantId": self.event.tenant_id,
            "expiresAtUtc": self.expires_at_utc,
        }


@dataclass(frozen=True)
class CanonicalOutboxLeaseRenewalRequest:
    tenant_id: str
    event_id: str
    lease_token: str = field(repr=False)
    lease_seconds: float = 60.0

    @classmethod
    def from_value(
        cls,
        value: CanonicalOutboxLeaseRenewalRequest
        | Mapping[str, Any],
    ) -> CanonicalOutboxLeaseRenewalRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical outbox renewal request")
        return cls(
            tenant_id=_text(
                item.get("tenantId"), "renewal.tenantId"
            ),
            event_id=_text(
                item.get("eventId"), "renewal.eventId"
            ),
            lease_token=_text(
                item.get("leaseToken"), "renewal.leaseToken"
            ),
            lease_seconds=_number(
                item.get("leaseSeconds"),
                "renewal.leaseSeconds",
                default=60.0,
            ),
        )


@dataclass(frozen=True)
class CanonicalOutboxLeaseRenewal:
    expires_at_utc: str

    def to_dict(self) -> dict[str, JSONValue]:
        return {"expiresAtUtc": self.expires_at_utc}


@dataclass(frozen=True)
class CanonicalOutboxNackRequest:
    tenant_id: str
    event_id: str
    lease_token: str = field(repr=False)
    not_before_utc: str | None = None
    retry_after_seconds: float | None = None
    error: str | None = None

    @classmethod
    def from_value(
        cls,
        value: CanonicalOutboxNackRequest | Mapping[str, Any],
    ) -> CanonicalOutboxNackRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical outbox nack request")
        raw_not_before = item.get("notBeforeUtc")
        raw_retry = item.get("retryAfterSeconds")
        return cls(
            tenant_id=_text(
                item.get("tenantId"), "nack.tenantId"
            ),
            event_id=_text(
                item.get("eventId"), "nack.eventId"
            ),
            lease_token=_text(
                item.get("leaseToken"), "nack.leaseToken"
            ),
            not_before_utc=(
                utc_timestamp(
                    cast(str, raw_not_before),
                    "nack.notBeforeUtc",
                )
                if raw_not_before is not None
                else None
            ),
            retry_after_seconds=(
                _number(raw_retry, "nack.retryAfterSeconds")
                if raw_retry is not None
                else None
            ),
            error=_optional_text(item.get("error"), "nack.error"),
        )


@dataclass(frozen=True)
class CanonicalOutboxQuery:
    tenant_id: str
    state: str | None = None
    topic: str | None = None
    limit: int | None = None
    continuation_token: str | None = None

    @classmethod
    def from_value(
        cls, value: CanonicalOutboxQuery | Mapping[str, Any]
    ) -> CanonicalOutboxQuery:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical outbox query")
        raw_limit = item.get("limit")
        return cls(
            tenant_id=_text(
                item.get("tenantId"), "outbox query.tenantId"
            ),
            state=_optional_text(
                item.get("state"), "outbox query.state"
            ),
            topic=_optional_text(
                item.get("topic"), "outbox query.topic"
            ),
            limit=(
                _integer(raw_limit, "outbox query.limit")
                if raw_limit is not None
                else None
            ),
            continuation_token=_optional_text(
                item.get("continuationToken"),
                "outbox query.continuationToken",
            ),
        )


@dataclass(frozen=True)
class CanonicalOutboxQueryResult:
    items: tuple[CanonicalOutboxEvent, ...]
    continuation_token: str | None = None

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "items": [item.to_dict() for item in self.items],
            "continuationToken": self.continuation_token,
        }


@dataclass(frozen=True)
class CanonicalOutboxReplayRequest:
    tenant_id: str
    event_id: str
    reset_delivery_count: bool = False

    @classmethod
    def from_value(
        cls,
        value: CanonicalOutboxReplayRequest | Mapping[str, Any],
    ) -> CanonicalOutboxReplayRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical outbox replay request")
        return cls(
            tenant_id=_text(
                item.get("tenantId"), "replay.tenantId"
            ),
            event_id=_text(
                item.get("eventId"), "replay.eventId"
            ),
            reset_delivery_count=_boolean(
                item.get("resetDeliveryCount"),
                "replay.resetDeliveryCount",
            ),
        )


@dataclass(frozen=True)
class CanonicalMigration:
    namespace: str
    id: str
    checksum: str
    description: str | None = None

    @classmethod
    def from_value(
        cls, value: CanonicalMigration | Mapping[str, Any]
    ) -> CanonicalMigration:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical migration")
        return cls(
            namespace=_text(
                item.get("namespace"), "migration.namespace"
            ),
            id=_text(item.get("id"), "migration.id"),
            checksum=_text(
                item.get("checksum"), "migration.checksum"
            ),
            description=_optional_text(
                item.get("description"), "migration.description"
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "namespace": self.namespace,
            "id": self.id,
            "checksum": self.checksum,
            "description": self.description,
        }


@dataclass(frozen=True)
class CanonicalMigrationReceipt(CanonicalMigration):
    applied_at_utc: str = EPOCH_TIMESTAMP

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            **super().to_dict(),
            "appliedAtUtc": self.applied_at_utc,
        }


@dataclass(frozen=True)
class CanonicalTransactionReceipt:
    transaction_id: str
    tenant_id: str
    idempotency_key: str
    request_hash: str
    result: CanonicalTransactionResult
    committed_at_utc: str

    @classmethod
    def from_value(
        cls, value: CanonicalTransactionReceipt | Mapping[str, Any]
    ) -> CanonicalTransactionReceipt:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical transaction receipt")
        return cls(
            transaction_id=_text(
                item.get("transactionId"), "receipt.transactionId"
            ),
            tenant_id=_text(
                item.get("tenantId"), "receipt.tenantId"
            ),
            idempotency_key=_text(
                item.get("idempotencyKey"),
                "receipt.idempotencyKey",
            ),
            request_hash=_text(
                item.get("requestHash"), "receipt.requestHash"
            ),
            result=CanonicalTransactionResult.from_value(
                _mapping(item.get("result"), "receipt.result")
            ),
            committed_at_utc=utc_timestamp(
                cast(str, item.get("committedAtUtc")),
                "receipt.committedAtUtc",
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "transactionId": self.transaction_id,
            "tenantId": self.tenant_id,
            "idempotencyKey": self.idempotency_key,
            "requestHash": self.request_hash,
            "result": self.result.to_dict(),
            "committedAtUtc": self.committed_at_utc,
        }


@dataclass(frozen=True)
class CanonicalTenantSnapshot:
    tenant_id: str
    documents: tuple[CanonicalDocument, ...]
    revisions: tuple[CanonicalDocumentRevision, ...]
    fences: tuple[CanonicalFence, ...]
    outbox: tuple[CanonicalOutboxEvent, ...]
    transactions: tuple[CanonicalTransactionReceipt, ...]
    exported_at_utc: str
    content_hash: str

    @classmethod
    def from_value(
        cls, value: CanonicalTenantSnapshot | Mapping[str, Any]
    ) -> CanonicalTenantSnapshot:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical tenant snapshot")
        return cls(
            tenant_id=_text(
                item.get("tenantId"), "snapshot.tenantId"
            ),
            documents=tuple(
                CanonicalDocument.from_value(
                    _mapping(raw, "snapshot.documents[]")
                )
                for raw in _sequence(
                    item.get("documents"), "snapshot.documents"
                )
            ),
            revisions=tuple(
                CanonicalDocumentRevision.from_value(
                    _mapping(raw, "snapshot.revisions[]")
                )
                for raw in _sequence(
                    item.get("revisions"), "snapshot.revisions"
                )
            ),
            fences=tuple(
                CanonicalFence.from_value(
                    _mapping(raw, "snapshot.fences[]")
                )
                for raw in _sequence(
                    item.get("fences"), "snapshot.fences"
                )
            ),
            outbox=tuple(
                CanonicalOutboxEvent.from_value(
                    _mapping(raw, "snapshot.outbox[]")
                )
                for raw in _sequence(
                    item.get("outbox"), "snapshot.outbox"
                )
            ),
            transactions=tuple(
                CanonicalTransactionReceipt.from_value(
                    _mapping(raw, "snapshot.transactions[]")
                )
                for raw in _sequence(
                    item.get("transactions"),
                    "snapshot.transactions",
                )
            ),
            exported_at_utc=utc_timestamp(
                cast(str, item.get("exportedAtUtc")),
                "snapshot.exportedAtUtc",
            ),
            content_hash=_text(
                item.get("contentHash"),
                "snapshot.contentHash",
                empty=True,
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "tenantId": self.tenant_id,
            "documents": [
                item.to_dict() for item in self.documents
            ],
            "revisions": [
                item.to_dict() for item in self.revisions
            ],
            "fences": [item.to_dict() for item in self.fences],
            "outbox": [item.to_dict() for item in self.outbox],
            "transactions": [
                item.to_dict() for item in self.transactions
            ],
            "exportedAtUtc": self.exported_at_utc,
            "contentHash": self.content_hash,
        }


@dataclass(frozen=True)
class CanonicalRestoreRequest:
    snapshot: CanonicalTenantSnapshot
    expected_content_hash: str | None = None

    @classmethod
    def from_value(
        cls, value: CanonicalRestoreRequest | Mapping[str, Any]
    ) -> CanonicalRestoreRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical restore request")
        return cls(
            snapshot=CanonicalTenantSnapshot.from_value(
                _mapping(item.get("snapshot"), "restore.snapshot")
            ),
            expected_content_hash=_optional_text(
                item.get("expectedContentHash"),
                "restore.expectedContentHash",
            ),
        )


@dataclass(frozen=True)
class CanonicalTenantArchiveChunk:
    index: int
    content: bytes
    length: int
    content_hash: str

    @classmethod
    def from_value(
        cls,
        value: CanonicalTenantArchiveChunk | Mapping[str, Any],
    ) -> CanonicalTenantArchiveChunk:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical archive chunk")
        raw = item.get("content")
        if isinstance(raw, str):
            try:
                content = base64.b64decode(raw, validate=True)
            except ValueError:
                raise ValueError(
                    "archive chunk content is not valid base64."
                ) from None
        elif isinstance(raw, (bytes, bytearray)):
            content = bytes(raw)
        else:
            raise TypeError(
                "archive chunk content must be bytes or base64 text."
            )
        return cls(
            index=_integer(item.get("index"), "chunk.index"),
            content=content,
            length=_integer(item.get("length"), "chunk.length"),
            content_hash=_text(
                item.get("contentHash"), "chunk.contentHash"
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "index": self.index,
            "content": base64.b64encode(self.content).decode("ascii"),
            "length": self.length,
            "contentHash": self.content_hash,
        }


@dataclass(frozen=True)
class CanonicalTenantArchive:
    tenant_id: str
    exported_at_utc: str
    snapshot_content_hash: str
    content_hash: str
    chunks: tuple[CanonicalTenantArchiveChunk, ...]
    profile: str = CANONICAL_ARCHIVE_PROFILE

    @classmethod
    def from_value(
        cls, value: CanonicalTenantArchive | Mapping[str, Any]
    ) -> CanonicalTenantArchive:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical tenant archive")
        raw_chunks = _sequence(
            item.get("chunks"), "archive.chunks"
        )
        if len(raw_chunks) > MAX_ARCHIVE_CHUNKS:
            raise ValueError(
                "archive.chunks must not contain more than "
                f"{MAX_ARCHIVE_CHUNKS} chunks."
            )
        return cls(
            profile=_text(
                item.get("profile", CANONICAL_ARCHIVE_PROFILE),
                "archive.profile",
            ),
            tenant_id=_text(
                item.get("tenantId"), "archive.tenantId"
            ),
            exported_at_utc=utc_timestamp(
                cast(str, item.get("exportedAtUtc")),
                "archive.exportedAtUtc",
            ),
            snapshot_content_hash=_text(
                item.get("snapshotContentHash"),
                "archive.snapshotContentHash",
            ),
            content_hash=_text(
                item.get("contentHash"), "archive.contentHash"
            ),
            chunks=tuple(
                CanonicalTenantArchiveChunk.from_value(
                    _mapping(raw, "archive.chunks[]")
                )
                for raw in raw_chunks
            ),
        )

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "profile": self.profile,
            "tenantId": self.tenant_id,
            "exportedAtUtc": self.exported_at_utc,
            "snapshotContentHash": self.snapshot_content_hash,
            "contentHash": self.content_hash,
            "chunks": [item.to_dict() for item in self.chunks],
        }


@dataclass(frozen=True)
class CanonicalArchiveRestoreRequest:
    archive: CanonicalTenantArchive
    expected_content_hash: str | None = None

    @classmethod
    def from_value(
        cls, value: CanonicalArchiveRestoreRequest | Mapping[str, Any]
    ) -> CanonicalArchiveRestoreRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "canonical archive restore request")
        return cls(
            archive=CanonicalTenantArchive.from_value(
                _mapping(item.get("archive"), "restore.archive")
            ),
            expected_content_hash=_optional_text(
                item.get("expectedContentHash"),
                "restore.expectedContentHash",
            ),
        )


@dataclass(frozen=True)
class CanonicalDataPlanePreflightCheck:
    id: str
    status: str
    message: str

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "id": self.id,
            "status": self.status,
            "message": self.message,
        }


@dataclass(frozen=True)
class CanonicalDataPlanePreflightResult:
    ready: bool
    status: str
    checked_at_utc: str
    duration_ms: int
    archive_chunk_count: int
    backup_restore_verified: bool
    tenant_isolation_verified: bool
    cleanup_verified: bool
    checks: tuple[CanonicalDataPlanePreflightCheck, ...]
    profile: str = CANONICAL_PREFLIGHT_PROFILE
    scope: str = "isolated_ephemeral_tenants"

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "profile": self.profile,
            "ready": self.ready,
            "status": self.status,
            "scope": self.scope,
            "checkedAtUtc": self.checked_at_utc,
            "durationMs": self.duration_ms,
            "archiveChunkCount": self.archive_chunk_count,
            "backupRestoreVerified": self.backup_restore_verified,
            "tenantIsolationVerified": self.tenant_isolation_verified,
            "cleanupVerified": self.cleanup_verified,
            "checks": [item.to_dict() for item in self.checks],
        }
