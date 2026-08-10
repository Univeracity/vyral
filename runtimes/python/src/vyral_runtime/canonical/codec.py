from __future__ import annotations

from dataclasses import replace
from hashlib import sha256
import json
import re
from typing import Any, Iterable, Mapping, cast

from ..contracts import JSONValue
from ..primitives import canonical_transaction_id, hash_lease_token
from .models import (
    CANONICAL_ARCHIVE_PROFILE,
    DEFAULT_ARCHIVE_CHUNK_BYTES,
    MAX_ARCHIVE_CHUNK_BYTES,
    MAX_DOCUMENT_BYTES,
    MAX_OUTBOX_PAYLOAD_BYTES,
    MAX_QUERY_LIMIT,
    MAX_SNAPSHOT_BYTES,
    MAX_TRANSACTION_FENCES,
    MAX_TRANSACTION_MUTATIONS,
    MAX_TRANSACTION_OUTBOX_EVENTS,
    CanonicalArchiveRestoreRequest,
    CanonicalDocument,
    CanonicalDocumentRevision,
    CanonicalFence,
    CanonicalFenceMutation,
    CanonicalMigration,
    CanonicalMutation,
    CanonicalOutboxEvent,
    CanonicalOutboxLeaseRequest,
    CanonicalOutboxLeaseRenewalRequest,
    CanonicalOutboxNackRequest,
    CanonicalOutboxQuery,
    CanonicalOutboxReplayRequest,
    CanonicalOutboxWrite,
    CanonicalRestoreRequest,
    CanonicalTenantArchive,
    CanonicalTenantArchiveChunk,
    CanonicalTenantSnapshot,
    CanonicalTransactionReceipt,
    CanonicalTransactionRequest,
    CanonicalWritePrecondition,
    parse_timestamp,
    utc_timestamp,
)


class CanonicalStoreError(RuntimeError):
    """Base failure for the strong CanonicalStore profile."""


class CanonicalValidationError(CanonicalStoreError, ValueError):
    pass


class CanonicalConflictError(CanonicalStoreError):
    pass


class CanonicalIntegrityError(CanonicalStoreError):
    pass


def _sha256(value: bytes) -> str:
    return "sha256:" + sha256(value).hexdigest()


def canonical_json_bytes(value: object) -> bytes:
    """Serialize JSON with recursive ordinal property ordering.

    Array ordering and numeric representation remain significant. The compact
    representation is the portable hash material used by CanonicalStore.
    """

    return _system_text_json(value, sort_keys=True).encode("utf-8")


def wire_json_bytes(value: object) -> bytes:
    return _system_text_json(value, sort_keys=False).encode("utf-8")


_UNICODE_ESCAPE = re.compile(r"\\u([0-9a-fA-F]{4})")


def _json_string(value: str) -> str:
    encoded = json.dumps(value, ensure_ascii=True)
    encoded = (
        encoded.replace("<", "\\u003C")
        .replace(">", "\\u003E")
        .replace("&", "\\u0026")
        .replace("'", "\\u0027")
    )
    return _UNICODE_ESCAPE.sub(
        lambda match: "\\u" + match.group(1).upper(),
        encoded,
    )


def _system_text_json(value: object, *, sort_keys: bool) -> str:
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, str):
        return _json_string(value)
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        return json.dumps(value, allow_nan=False)
    if isinstance(value, Mapping):
        items: Iterable[tuple[object, object]] = value.items()
        if sort_keys:
            items = sorted(
                items, key=lambda item: str(item[0])
            )
        material: list[str] = []
        for key, item in items:
            if not isinstance(key, str):
                raise TypeError("Canonical JSON object keys must be strings.")
            material.append(
                _json_string(key)
                + ":"
                + _system_text_json(item, sort_keys=sort_keys)
            )
        return "{" + ",".join(material) + "}"
    if isinstance(value, (list, tuple)):
        return (
            "["
            + ",".join(
                _system_text_json(item, sort_keys=sort_keys)
                for item in value
            )
            + "]"
        )
    raise TypeError(
        f"Canonical JSON value {type(value).__name__!r} is unsupported."
    )


def canonical_request_hash(
    request: CanonicalTransactionRequest | Mapping[str, Any],
) -> str:
    selected = CanonicalTransactionRequest.from_value(request)
    normalized_mutations: list[CanonicalMutation] = []
    for mutation in selected.mutations:
        document = mutation.document
        if document is not None:
            document = replace(
                document,
                tenant_id=document.tenant_id.strip(),
                document_type=document.document_type.strip(),
                id=document.id.strip(),
                schema_version=document.schema_version.strip(),
            )
        normalized_mutations.append(
            replace(
                mutation,
                operation=mutation.operation.strip(),
                document=document,
                document_type=(
                    mutation.document_type.strip()
                    if mutation.document_type is not None
                    else None
                ),
                id=(
                    mutation.id.strip()
                    if mutation.id is not None
                    else None
                ),
            )
        )
    normalized_fences = tuple(
        replace(
            item,
            operation=item.operation.strip(),
            name=item.name.strip(),
            value=item.value.strip(),
            owner_document_type=item.owner_document_type.strip(),
            owner_document_id=item.owner_document_id.strip(),
        )
        for item in selected.fences
    )
    normalized_outbox = tuple(
        replace(
            item,
            id=item.id.strip() if item.id is not None else None,
            topic=item.topic.strip(),
            key=item.key.strip(),
            not_before_utc=(
                utc_timestamp(item.not_before_utc)
                if item.not_before_utc is not None
                else None
            ),
        )
        for item in selected.outbox
    )
    normalized = replace(
        selected,
        tenant_id=selected.tenant_id.strip(),
        idempotency_key=selected.idempotency_key.strip(),
        correlation_id=(
            selected.correlation_id.strip()
            if selected.correlation_id is not None
            else None
        ),
        actor=(
            selected.actor.strip()
            if selected.actor is not None
            else None
        ),
        mutations=tuple(normalized_mutations),
        fences=normalized_fences,
        outbox=normalized_outbox,
    )
    return _sha256(canonical_json_bytes(normalized.to_dict()))


def canonical_snapshot_hash(snapshot: CanonicalTenantSnapshot) -> str:
    material: dict[str, JSONValue] = {
        "tenantId": snapshot.tenant_id,
        "documents": [
            item.to_dict()
            for item in sorted(
                snapshot.documents,
                key=lambda item: (item.document_type, item.id),
            )
        ],
        "revisions": [
            item.to_dict()
            for item in sorted(
                snapshot.revisions,
                key=lambda item: (
                    item.document_type,
                    item.id,
                    item.revision,
                ),
            )
        ],
        "fences": [
            item.to_dict()
            for item in sorted(
                snapshot.fences,
                key=lambda item: (item.name, item.value),
            )
        ],
        "outbox": [
            item.to_dict()
            for item in sorted(
                snapshot.outbox,
                key=lambda item: item.id,
            )
        ],
        "transactions": [
            item.to_dict()
            for item in sorted(
                snapshot.transactions,
                key=lambda item: item.transaction_id,
            )
        ],
    }
    return _sha256(canonical_json_bytes(material))


def canonical_snapshot_byte_count(
    snapshot: CanonicalTenantSnapshot,
) -> int:
    material: dict[str, JSONValue] = {
        "tenantId": snapshot.tenant_id,
        "documents": [
            item.to_dict()
            for item in sorted(
                snapshot.documents,
                key=lambda item: (item.document_type, item.id),
            )
        ],
        "revisions": [
            item.to_dict()
            for item in sorted(
                snapshot.revisions,
                key=lambda item: (
                    item.document_type,
                    item.id,
                    item.revision,
                ),
            )
        ],
        "fences": [
            item.to_dict()
            for item in sorted(
                snapshot.fences,
                key=lambda item: (item.name, item.value),
            )
        ],
        "outbox": [
            item.to_dict()
            for item in sorted(snapshot.outbox, key=lambda item: item.id)
        ],
        "transactions": [
            item.to_dict()
            for item in sorted(
                snapshot.transactions,
                key=lambda item: item.transaction_id,
            )
        ],
    }
    return len(canonical_json_bytes(material))


def _archive_hash(archive: CanonicalTenantArchive) -> str:
    material = "\n".join(
        (
            archive.profile,
            archive.tenant_id,
            archive.exported_at_utc,
            archive.snapshot_content_hash,
            *(
                f"{item.index}:{item.length}:{item.content_hash}"
                for item in sorted(
                    archive.chunks, key=lambda chunk: chunk.index
                )
            ),
        )
    )
    return _sha256(material.encode("utf-8"))


def create_canonical_archive(
    snapshot: CanonicalTenantSnapshot,
    chunk_bytes: int = DEFAULT_ARCHIVE_CHUNK_BYTES,
) -> CanonicalTenantArchive:
    validate_snapshot(snapshot, enforce_portable_size=False)
    if chunk_bytes <= 0 or chunk_bytes > MAX_ARCHIVE_CHUNK_BYTES:
        raise CanonicalValidationError(
            "Canonical archive chunk_bytes must be between 1 and "
            f"{MAX_ARCHIVE_CHUNK_BYTES}."
        )
    snapshot_hash = canonical_snapshot_hash(snapshot)
    if (
        not snapshot.content_hash
        or snapshot.content_hash != snapshot_hash
    ):
        raise CanonicalIntegrityError(
            "Canonical archive source snapshot content hash does not "
            "match its contents."
        )
    payload = wire_json_bytes(snapshot.to_dict())
    chunks = tuple(
        CanonicalTenantArchiveChunk(
            index=index,
            content=payload[offset : offset + chunk_bytes],
            length=len(payload[offset : offset + chunk_bytes]),
            content_hash=_sha256(
                payload[offset : offset + chunk_bytes]
            ),
        )
        for index, offset in enumerate(
            range(0, len(payload), chunk_bytes)
        )
    )
    archive = CanonicalTenantArchive(
        tenant_id=snapshot.tenant_id,
        exported_at_utc=snapshot.exported_at_utc,
        snapshot_content_hash=snapshot_hash,
        content_hash="",
        chunks=chunks,
    )
    return replace(archive, content_hash=_archive_hash(archive))


def read_canonical_archive(
    request: CanonicalArchiveRestoreRequest | Mapping[str, Any],
) -> CanonicalTenantSnapshot:
    selected = CanonicalArchiveRestoreRequest.from_value(request)
    archive = selected.archive
    validate_tenant_id(archive.tenant_id)
    if archive.profile != CANONICAL_ARCHIVE_PROFILE:
        raise CanonicalValidationError(
            f"Canonical archive profile {archive.profile!r} is not "
            "supported."
        )
    if not archive.chunks:
        raise CanonicalIntegrityError(
            "Canonical archive contains no chunks."
        )
    expected = (
        selected.expected_content_hash.strip()
        if selected.expected_content_hash is not None
        else archive.content_hash
    )
    if not expected or expected != _archive_hash(archive):
        raise CanonicalIntegrityError(
            "Canonical archive content hash does not match the "
            "requested restore."
        )
    ordered = sorted(archive.chunks, key=lambda item: item.index)
    payload = bytearray()
    for expected_index, chunk in enumerate(ordered):
        if (
            chunk.index != expected_index
            or chunk.length <= 0
            or chunk.length > MAX_ARCHIVE_CHUNK_BYTES
            or len(chunk.content) != chunk.length
            or chunk.content_hash != _sha256(chunk.content)
        ):
            raise CanonicalIntegrityError(
                "Canonical archive contains an invalid chunk."
            )
        payload.extend(chunk.content)
    try:
        raw = json.loads(bytes(payload))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise CanonicalIntegrityError(
            "Canonical archive did not contain a tenant snapshot."
        ) from None
    snapshot = CanonicalTenantSnapshot.from_value(
        cast(Mapping[str, Any], raw)
    )
    validate_snapshot(snapshot, enforce_portable_size=False)
    actual = canonical_snapshot_hash(snapshot)
    if (
        archive.tenant_id != snapshot.tenant_id
        or archive.snapshot_content_hash != actual
        or snapshot.content_hash != actual
    ):
        raise CanonicalIntegrityError(
            "Canonical archive snapshot integrity check failed."
        )
    return snapshot


def _contains_control(value: str) -> bool:
    return any(
        ord(character) < 32
        or 127 <= ord(character) <= 159
        for character in value
    )


def validate_required_id(value: str | None, name: str) -> str:
    normalized = value.strip() if isinstance(value, str) else ""
    if not normalized or len(normalized) > 160:
        raise CanonicalValidationError(
            f"{name} is required and must not exceed 160 characters."
        )
    if _contains_control(normalized):
        raise CanonicalValidationError(
            f"{name} cannot contain control characters."
        )
    return normalized


def validate_tenant_id(value: str) -> str:
    normalized = validate_required_id(value, "Canonical tenant id")
    if any(character in normalized for character in "/\\?#%"):
        raise CanonicalValidationError(
            "Canonical tenant id must be safe for a single HTTP path "
            "segment."
        )
    return normalized


def validate_document_identity(
    tenant_id: str, document_type: str, id: str
) -> tuple[str, str, str]:
    return (
        validate_tenant_id(tenant_id),
        validate_required_id(
            document_type, "Canonical document type"
        ),
        validate_required_id(id, "Canonical document id"),
    )


def _validate_string_map(
    values: Mapping[str, str], name: str
) -> None:
    if len(values) > 64:
        raise CanonicalValidationError(
            f"{name} cannot contain more than 64 values."
        )
    for key, value in values.items():
        validate_required_id(key, f"{name} key")
        if (
            not value.strip()
            or len(value) > 4_096
            or _contains_control(value)
        ):
            raise CanonicalValidationError(
                f"{name} value is required, must not exceed 4096 "
                "characters, and cannot contain controls."
            )


def _json_bytes(value: JSONValue) -> int:
    return len(wire_json_bytes(value))


def validate_document(
    document: CanonicalDocument, *, allow_deleted: bool = True
) -> None:
    validate_document_identity(
        document.tenant_id, document.document_type, document.id
    )
    validate_required_id(
        document.schema_version,
        "Canonical document schema version",
    )
    if not allow_deleted and document.deleted:
        raise CanonicalValidationError(
            "Canonical upsert document cannot be marked deleted."
        )
    if not document.deleted and document.data is None:
        raise CanonicalValidationError(
            "Canonical document data is required for an upsert."
        )
    if _json_bytes(document.data) > MAX_DOCUMENT_BYTES:
        raise CanonicalValidationError(
            f"Canonical document data exceeds {MAX_DOCUMENT_BYTES} "
            "bytes."
        )
    _validate_string_map(
        document.indexes, "Canonical document indexes"
    )


def _validate_precondition(
    precondition: CanonicalWritePrecondition | None,
) -> None:
    if precondition is None:
        return
    if precondition.must_exist and precondition.must_not_exist:
        raise CanonicalValidationError(
            "Canonical write precondition cannot require both "
            "existence and non-existence."
        )
    if (
        precondition.expected_revision is not None
        and precondition.expected_revision < 0
    ):
        raise CanonicalValidationError(
            "Canonical expected revision cannot be negative."
        )


def mutation_key(mutation: CanonicalMutation) -> tuple[str, str]:
    if mutation.operation == "upsert" and mutation.document is not None:
        return (
            mutation.document.document_type.strip(),
            mutation.document.id.strip(),
        )
    return (
        mutation.document_type.strip()
        if mutation.document_type is not None
        else "",
        mutation.id.strip() if mutation.id is not None else "",
    )


def _validate_fence(item: CanonicalFenceMutation) -> None:
    if item.operation not in {"claim", "release"}:
        raise CanonicalValidationError(
            f"Canonical fence operation {item.operation!r} is not "
            "supported."
        )
    validate_required_id(item.name, "Canonical fence name")
    validate_required_id(item.value, "Canonical fence value")
    validate_required_id(
        item.owner_document_type,
        "Canonical fence owner document type",
    )
    validate_required_id(
        item.owner_document_id,
        "Canonical fence owner document id",
    )


def _validate_outbox_write(item: CanonicalOutboxWrite) -> None:
    if item.id is not None:
        validate_outbox_event_id(item.id)
    validate_required_id(item.topic, "Canonical outbox topic")
    validate_required_id(item.key, "Canonical outbox key")
    if _json_bytes(item.payload) > MAX_OUTBOX_PAYLOAD_BYTES:
        raise CanonicalValidationError(
            "Canonical outbox payload exceeds "
            f"{MAX_OUTBOX_PAYLOAD_BYTES} bytes."
        )
    _validate_string_map(
        item.headers, "Canonical outbox headers"
    )
    if (
        item.max_delivery_attempts is not None
        and (
            item.max_delivery_attempts <= 0
            or item.max_delivery_attempts > 100_000
        )
    ):
        raise CanonicalValidationError(
            "Canonical outbox max_delivery_attempts must be between "
            "1 and 100000."
        )


def validate_transaction(
    request: CanonicalTransactionRequest | Mapping[str, Any],
) -> CanonicalTransactionRequest:
    selected = CanonicalTransactionRequest.from_value(request)
    validate_tenant_id(selected.tenant_id)
    validate_required_id(
        selected.idempotency_key, "Canonical idempotency key"
    )
    if selected.correlation_id is not None:
        validate_required_id(
            selected.correlation_id, "Canonical correlation id"
        )
    if selected.actor is not None:
        validate_required_id(selected.actor, "Canonical actor")
    if not (
        selected.mutations or selected.fences or selected.outbox
    ):
        raise CanonicalValidationError(
            "Canonical transaction requires a mutation, fence, or "
            "outbox event."
        )
    if len(selected.mutations) > MAX_TRANSACTION_MUTATIONS:
        raise CanonicalValidationError(
            "Canonical transaction contains too many document "
            "mutations."
        )
    if len(selected.fences) > MAX_TRANSACTION_FENCES:
        raise CanonicalValidationError(
            "Canonical transaction contains too many fence mutations."
        )
    if len(selected.outbox) > MAX_TRANSACTION_OUTBOX_EVENTS:
        raise CanonicalValidationError(
            "Canonical transaction contains too many outbox events."
        )
    document_keys: set[tuple[str, str]] = set()
    for mutation in selected.mutations:
        if mutation.operation == "upsert":
            if mutation.document is None:
                raise CanonicalValidationError(
                    "Canonical upsert mutation requires a document."
                )
            validate_document(
                mutation.document, allow_deleted=False
            )
            if mutation.document.tenant_id != selected.tenant_id:
                raise CanonicalValidationError(
                    "Canonical upsert document tenant must match the "
                    "enclosing transaction tenant."
                )
        elif mutation.operation == "delete":
            validate_required_id(
                mutation.document_type,
                "Canonical delete document type",
            )
            validate_required_id(
                mutation.id, "Canonical delete document id"
            )
            if mutation.document is not None:
                raise CanonicalValidationError(
                    "Canonical delete mutation cannot include a "
                    "document."
                )
        else:
            raise CanonicalValidationError(
                f"Canonical mutation operation {mutation.operation!r} "
                "is not supported."
            )
        _validate_precondition(mutation.precondition)
        key = mutation_key(mutation)
        if key in document_keys:
            raise CanonicalValidationError(
                f"Canonical transaction duplicates document "
                f"{key[0]!r}/{key[1]!r}."
            )
        document_keys.add(key)
    fence_keys: set[tuple[str, str]] = set()
    for fence_item in selected.fences:
        _validate_fence(fence_item)
        fence_key = (
            fence_item.name.strip(),
            fence_item.value.strip(),
        )
        if fence_key in fence_keys:
            raise CanonicalValidationError(
                "Canonical transaction duplicates a fence."
            )
        fence_keys.add(fence_key)
    event_ids: set[str] = set()
    for outbox_item in selected.outbox:
        _validate_outbox_write(outbox_item)
        if outbox_item.id is not None:
            event_id = outbox_item.id.strip()
            if event_id in event_ids:
                raise CanonicalValidationError(
                    "Canonical transaction duplicates an outbox event."
                )
            event_ids.add(event_id)
    return selected


def validate_query_limit(limit: int | None, name: str) -> None:
    if limit is not None and (limit <= 0 or limit > MAX_QUERY_LIMIT):
        raise CanonicalValidationError(
            f"{name} must be between 1 and {MAX_QUERY_LIMIT}."
        )


def validate_outbox_event_id(value: str) -> str:
    normalized = validate_required_id(
        value, "Canonical outbox event id"
    )
    if any(character in normalized for character in "/\\?#%"):
        raise CanonicalValidationError(
            "Canonical outbox event id must be safe for a single "
            "HTTP path segment."
        )
    return normalized


def validate_outbox_lease(
    value: CanonicalOutboxLeaseRequest | Mapping[str, Any],
) -> CanonicalOutboxLeaseRequest:
    request = CanonicalOutboxLeaseRequest.from_value(value)
    validate_tenant_id(request.tenant_id)
    validate_required_id(
        request.consumer_id, "Canonical outbox consumer id"
    )
    if request.max_items <= 0 or request.max_items > 100:
        raise CanonicalValidationError(
            "Canonical outbox lease max_items must be between 1 and "
            "100."
        )
    if request.lease_seconds <= 0 or request.lease_seconds > 86_400:
        raise CanonicalValidationError(
            "Canonical outbox lease_seconds must be between 0 and "
            "86400."
        )
    return request


def validate_outbox_acknowledgement(
    tenant_id: str, event_id: str, lease_token: str
) -> tuple[str, str, str]:
    return (
        validate_tenant_id(tenant_id),
        validate_outbox_event_id(event_id),
        validate_required_id(
            lease_token, "Canonical outbox lease token"
        ),
    )


def validate_outbox_renewal(
    value: CanonicalOutboxLeaseRenewalRequest | Mapping[str, Any],
) -> CanonicalOutboxLeaseRenewalRequest:
    request = CanonicalOutboxLeaseRenewalRequest.from_value(value)
    validate_outbox_acknowledgement(
        request.tenant_id, request.event_id, request.lease_token
    )
    if request.lease_seconds <= 0 or request.lease_seconds > 86_400:
        raise CanonicalValidationError(
            "Canonical outbox lease_seconds must be between 0 and "
            "86400."
        )
    return request


def validate_outbox_nack(
    value: CanonicalOutboxNackRequest | Mapping[str, Any],
) -> CanonicalOutboxNackRequest:
    request = CanonicalOutboxNackRequest.from_value(value)
    validate_outbox_acknowledgement(
        request.tenant_id, request.event_id, request.lease_token
    )
    if (
        request.not_before_utc is not None
        and request.retry_after_seconds is not None
    ):
        raise CanonicalValidationError(
            "Canonical outbox release cannot specify both "
            "not_before_utc and retry_after_seconds."
        )
    if request.retry_after_seconds is not None and (
        request.retry_after_seconds <= 0
        or request.retry_after_seconds > 86_400
    ):
        raise CanonicalValidationError(
            "Canonical outbox retry_after_seconds must be between 0 "
            "and 86400."
        )
    if request.error is not None and len(request.error) > 4_096:
        raise CanonicalValidationError(
            "Canonical outbox error cannot exceed 4096 characters."
        )
    return request


def validate_outbox_query(
    value: CanonicalOutboxQuery | Mapping[str, Any],
) -> CanonicalOutboxQuery:
    query = CanonicalOutboxQuery.from_value(value)
    validate_tenant_id(query.tenant_id)
    if query.state is not None and query.state not in {
        "ready",
        "leased",
        "scheduled",
        "delivered",
        "dead-letter",
    }:
        raise CanonicalValidationError(
            f"Canonical outbox state {query.state!r} is not supported."
        )
    if query.topic is not None:
        validate_required_id(query.topic, "Canonical outbox topic")
    validate_query_limit(query.limit, "Canonical outbox query limit")
    return query


def validate_outbox_replay(
    value: CanonicalOutboxReplayRequest | Mapping[str, Any],
) -> CanonicalOutboxReplayRequest:
    request = CanonicalOutboxReplayRequest.from_value(value)
    validate_tenant_id(request.tenant_id)
    validate_outbox_event_id(request.event_id)
    return request


def validate_migration(
    value: CanonicalMigration | Mapping[str, Any],
) -> CanonicalMigration:
    migration = CanonicalMigration.from_value(value)
    validate_required_id(
        migration.namespace, "Canonical migration namespace"
    )
    validate_required_id(migration.id, "Canonical migration id")
    validate_required_id(
        migration.checksum, "Canonical migration checksum"
    )
    return migration


def _validate_fence_state(
    fence: CanonicalFence, tenant_id: str
) -> None:
    validate_tenant_id(fence.tenant_id)
    _validate_fence(
        CanonicalFenceMutation(
            name=fence.name,
            value=fence.value,
            owner_document_type=fence.owner_document_type,
            owner_document_id=fence.owner_document_id,
        )
    )
    if fence.tenant_id != tenant_id:
        raise CanonicalValidationError(
            "Canonical snapshot fence tenant does not match snapshot "
            "tenant."
        )


def _validate_outbox_event(
    item: CanonicalOutboxEvent, tenant_id: str
) -> None:
    validate_tenant_id(item.tenant_id)
    validate_outbox_event_id(item.id)
    validate_required_id(
        item.transaction_id, "Canonical outbox transaction id"
    )
    _validate_outbox_write(
        CanonicalOutboxWrite(
            id=item.id,
            topic=item.topic,
            key=item.key,
            payload=item.payload,
            headers=item.headers,
            not_before_utc=item.not_before_utc,
            max_delivery_attempts=item.max_delivery_attempts,
        )
    )
    if item.tenant_id != tenant_id:
        raise CanonicalValidationError(
            "Canonical snapshot outbox tenant does not match snapshot "
            "tenant."
        )


def _validate_revision(
    item: CanonicalDocumentRevision, tenant_id: str
) -> None:
    validate_document_identity(
        item.tenant_id, item.document_type, item.id
    )
    validate_required_id(
        item.transaction_id, "Canonical revision transaction id"
    )
    if item.revision <= 0:
        raise CanonicalValidationError(
            "Canonical revision must be positive."
        )
    if item.operation not in {"upsert", "delete"}:
        raise CanonicalValidationError(
            "Canonical revision operation is not supported."
        )
    validate_document(item.document)
    if (
        item.tenant_id != tenant_id
        or item.document.tenant_id != tenant_id
        or item.document_type != item.document.document_type
        or item.id != item.document.id
        or item.revision != item.document.revision
    ):
        raise CanonicalValidationError(
            "Canonical revision does not match its document state."
        )


def _validate_receipt(
    item: CanonicalTransactionReceipt, tenant_id: str
) -> None:
    validate_tenant_id(item.tenant_id)
    validate_required_id(
        item.transaction_id, "Canonical receipt transaction id"
    )
    validate_required_id(
        item.idempotency_key, "Canonical receipt idempotency key"
    )
    validate_required_id(
        item.request_hash, "Canonical receipt request hash"
    )
    if (
        item.tenant_id != tenant_id
        or item.result.tenant_id != tenant_id
        or item.result.transaction_id != item.transaction_id
        or item.result.idempotency_key != item.idempotency_key
    ):
        raise CanonicalValidationError(
            "Canonical receipt result does not match its receipt "
            "identity."
        )
    for document in item.result.documents:
        validate_document(document)
        if document.tenant_id != tenant_id:
            raise CanonicalValidationError(
                "Canonical receipt document tenant does not match."
            )
    for event in item.result.outbox:
        _validate_outbox_event(event, tenant_id)
        if event.transaction_id != item.transaction_id:
            raise CanonicalValidationError(
                "Canonical receipt outbox transaction does not match."
            )


def validate_snapshot(
    snapshot: CanonicalTenantSnapshot,
    *,
    enforce_portable_size: bool = True,
) -> None:
    tenant_id = validate_tenant_id(snapshot.tenant_id)
    document_keys: set[tuple[str, str]] = set()
    for document in snapshot.documents:
        validate_document(document)
        if (
            document.tenant_id != tenant_id
            or document.revision <= 0
            or not document.etag
        ):
            raise CanonicalValidationError(
                "Canonical snapshot document identity or revision is "
                "invalid."
            )
        document_key = (document.document_type, document.id)
        if document_key in document_keys:
            raise CanonicalValidationError(
                "Canonical snapshot duplicates a document."
            )
        document_keys.add(document_key)
    revision_keys: set[tuple[str, str, int]] = set()
    for revision in snapshot.revisions:
        _validate_revision(revision, tenant_id)
        revision_key = (
            revision.document_type,
            revision.id,
            revision.revision,
        )
        if revision_key in revision_keys:
            raise CanonicalValidationError(
                "Canonical snapshot duplicates a document revision."
            )
        revision_keys.add(revision_key)
    fence_keys: set[tuple[str, str]] = set()
    for fence in snapshot.fences:
        _validate_fence_state(fence, tenant_id)
        fence_key = (fence.name, fence.value)
        if fence_key in fence_keys:
            raise CanonicalValidationError(
                "Canonical snapshot duplicates a fence."
            )
        fence_keys.add(fence_key)
    event_ids: set[str] = set()
    for event in snapshot.outbox:
        _validate_outbox_event(event, tenant_id)
        if event.id in event_ids:
            raise CanonicalValidationError(
                "Canonical snapshot duplicates an outbox event."
            )
        event_ids.add(event.id)
    receipt_keys: set[str] = set()
    for receipt in snapshot.transactions:
        _validate_receipt(receipt, tenant_id)
        if receipt.idempotency_key in receipt_keys:
            raise CanonicalValidationError(
                "Canonical snapshot duplicates an idempotency "
                "receipt."
            )
        receipt_keys.add(receipt.idempotency_key)
    if (
        enforce_portable_size
        and canonical_snapshot_byte_count(snapshot)
        > MAX_SNAPSHOT_BYTES
    ):
        raise CanonicalValidationError(
            f"Canonical snapshot exceeds the {MAX_SNAPSHOT_BYTES}-byte "
            "portable limit."
        )


def validate_restore(
    value: CanonicalRestoreRequest | Mapping[str, Any],
    *,
    enforce_portable_size: bool = True,
) -> CanonicalRestoreRequest:
    request = CanonicalRestoreRequest.from_value(value)
    validate_snapshot(
        request.snapshot,
        enforce_portable_size=enforce_portable_size,
    )
    expected = (
        request.expected_content_hash.strip()
        if request.expected_content_hash is not None
        else request.snapshot.content_hash
    )
    actual = canonical_snapshot_hash(request.snapshot)
    if not expected or expected != actual:
        raise CanonicalIntegrityError(
            "Canonical snapshot content hash does not match the "
            "requested restore."
        )
    return request


__all__ = [
    "CanonicalConflictError",
    "CanonicalIntegrityError",
    "CanonicalStoreError",
    "CanonicalValidationError",
    "canonical_json_bytes",
    "canonical_request_hash",
    "canonical_snapshot_byte_count",
    "canonical_snapshot_hash",
    "canonical_transaction_id",
    "create_canonical_archive",
    "hash_lease_token",
    "parse_timestamp",
    "read_canonical_archive",
    "validate_document",
    "validate_document_identity",
    "validate_migration",
    "validate_outbox_acknowledgement",
    "validate_outbox_lease",
    "validate_outbox_nack",
    "validate_outbox_query",
    "validate_outbox_renewal",
    "validate_outbox_replay",
    "validate_query_limit",
    "validate_restore",
    "validate_snapshot",
    "validate_tenant_id",
    "validate_transaction",
]
