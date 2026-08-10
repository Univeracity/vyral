from __future__ import annotations

from contextlib import contextmanager
from dataclasses import replace
from datetime import datetime, timedelta, timezone
import base64
import json
from pathlib import Path
from secrets import token_hex
import sqlite3
from threading import Lock
from time import monotonic
from typing import Any, Callable, Iterator, Mapping, Sequence, cast
from uuid import uuid4

from ..async_runtime import RuntimeExecutor
from ..contracts import JSONValue
from ..primitives import canonical_transaction_id, hash_lease_token
from .codec import (
    CanonicalConflictError,
    CanonicalIntegrityError,
    CanonicalStoreError,
    CanonicalValidationError,
    canonical_request_hash,
    canonical_snapshot_byte_count,
    canonical_snapshot_hash,
    create_canonical_archive,
    mutation_key,
    parse_timestamp,
    read_canonical_archive,
    validate_document_identity,
    validate_migration,
    validate_outbox_acknowledgement,
    validate_outbox_lease,
    validate_outbox_nack,
    validate_outbox_query,
    validate_outbox_renewal,
    validate_outbox_replay,
    validate_query_limit,
    validate_restore,
    validate_tenant_id,
    validate_transaction,
)
from .models import (
    DEFAULT_ARCHIVE_CHUNK_BYTES,
    DEFAULT_OUTBOX_RETRY_DELAY_SECONDS,
    MAX_QUERY_LIMIT,
    CanonicalArchiveRestoreRequest,
    CanonicalDataPlanePreflightCheck,
    CanonicalDataPlanePreflightResult,
    CanonicalDocument,
    CanonicalDocumentQuery,
    CanonicalDocumentQueryResult,
    CanonicalDocumentRevision,
    CanonicalFence,
    CanonicalMigration,
    CanonicalMigrationReceipt,
    CanonicalMutation,
    CanonicalOutboxEvent,
    CanonicalOutboxLease,
    CanonicalOutboxLeaseRequest,
    CanonicalOutboxLeaseRenewal,
    CanonicalOutboxLeaseRenewalRequest,
    CanonicalOutboxNackRequest,
    CanonicalOutboxQuery,
    CanonicalOutboxQueryResult,
    CanonicalOutboxReplayRequest,
    CanonicalRestoreRequest,
    CanonicalTenantArchive,
    CanonicalTenantSnapshot,
    CanonicalTransactionReceipt,
    CanonicalTransactionRequest,
    CanonicalTransactionResult,
    CanonicalWritePrecondition,
    utc_timestamp,
)


class SQLiteCanonicalStore:
    """SQLite reference implementation of the strong CanonicalStore profile."""

    def __init__(
        self,
        database_path: str | Path,
        *,
        busy_timeout_ms: int = 5_000,
        clock: Callable[[], datetime] | None = None,
        lease_token_factory: Callable[[], str] | None = None,
    ) -> None:
        path = Path(database_path).expanduser().resolve()
        if not path.name:
            raise ValueError("Canonical SQLite database path is required.")
        if (
            isinstance(busy_timeout_ms, bool)
            or not isinstance(busy_timeout_ms, int)
            or busy_timeout_ms < 0
        ):
            raise ValueError(
                "Canonical SQLite busy timeout must be non-negative."
            )
        path.parent.mkdir(parents=True, exist_ok=True)
        self.database_path = path
        self.busy_timeout_ms = busy_timeout_ms
        self._clock = clock or (lambda: datetime.now(timezone.utc))
        self._lease_token_factory = lease_token_factory or (
            lambda: token_hex(32)
        )
        self._initialization_lock = Lock()
        self._initialized = False
        self._ensure_initialized()

    def apply_migrations(
        self,
        migrations: Sequence[CanonicalMigration | Mapping[str, Any]],
    ) -> None:
        selected = tuple(
            validate_migration(item) for item in migrations
        )
        with self._transaction() as connection:
            for migration in sorted(
                selected,
                key=lambda item: (item.namespace, item.id),
            ):
                storage_id = _migration_identity(
                    migration.namespace, migration.id
                )
                row = connection.execute(
                    """
                    SELECT checksum
                    FROM vyral_canonical_migrations
                    WHERE id = ?
                    """,
                    (storage_id,),
                ).fetchone()
                if row is not None:
                    if str(row["checksum"]) != migration.checksum:
                        raise CanonicalConflictError(
                            f"Canonical migration "
                            f"{migration.namespace!r}/{migration.id!r} "
                            "was already applied with a different "
                            "checksum."
                        )
                    continue
                connection.execute(
                    """
                    INSERT INTO vyral_canonical_migrations
                        (id, checksum, description, applied_at_utc)
                    VALUES (?, ?, ?, ?)
                    """,
                    (
                        storage_id,
                        migration.checksum.strip(),
                        migration.description,
                        self._now(),
                    ),
                )

    def list_migrations(
        self,
    ) -> tuple[CanonicalMigrationReceipt, ...]:
        with self._connection() as connection:
            rows = connection.execute(
                """
                SELECT id, checksum, description, applied_at_utc
                FROM vyral_canonical_migrations
                ORDER BY id
                """
            ).fetchall()
        results: list[CanonicalMigrationReceipt] = []
        for row in rows:
            namespace, id = _parse_migration_identity(str(row["id"]))
            results.append(
                CanonicalMigrationReceipt(
                    namespace=namespace,
                    id=id,
                    checksum=str(row["checksum"]),
                    description=cast(
                        str | None, row["description"]
                    ),
                    applied_at_utc=str(row["applied_at_utc"]),
                )
            )
        return tuple(results)

    def commit(
        self,
        request: CanonicalTransactionRequest | Mapping[str, Any],
    ) -> CanonicalTransactionResult:
        selected = validate_transaction(request)
        tenant_id = selected.tenant_id.strip()
        idempotency_key = selected.idempotency_key.strip()
        request_hash = canonical_request_hash(selected)
        transaction_id = canonical_transaction_id(
            tenant_id, idempotency_key
        )
        with self._transaction() as connection:
            existing = self._get_receipt(
                connection, tenant_id, idempotency_key
            )
            if existing is not None:
                if existing.request_hash != request_hash:
                    raise CanonicalConflictError(
                        f"Canonical idempotency key "
                        f"{idempotency_key!r} already belongs to a "
                        "different transaction request."
                    )
                return replace(existing.result, replayed=True)

            now = self._now()
            documents: list[CanonicalDocument] = []
            for mutation in selected.mutations:
                if mutation.operation == "upsert":
                    document = self._apply_upsert(
                        connection,
                        tenant_id,
                        transaction_id,
                        mutation,
                        now,
                    )
                else:
                    document = self._apply_delete(
                        connection,
                        tenant_id,
                        transaction_id,
                        mutation,
                        now,
                    )
                documents.append(document)

            for fence in selected.fences:
                self._apply_fence(
                    connection, tenant_id, fence, now
                )

            events: list[CanonicalOutboxEvent] = []
            for index, item in enumerate(selected.outbox):
                event_id = (
                    f"{transaction_id}:{index:03d}"
                    if item.id is None
                    else item.id.strip()
                )
                event = CanonicalOutboxEvent(
                    id=event_id,
                    tenant_id=tenant_id,
                    transaction_id=transaction_id,
                    topic=item.topic.strip(),
                    key=item.key.strip(),
                    payload=_clone_json(item.payload),
                    headers=dict(item.headers),
                    not_before_utc=item.not_before_utc,
                    max_delivery_attempts=item.max_delivery_attempts,
                )
                self._insert_outbox(connection, event)
                events.append(event)

            result = CanonicalTransactionResult(
                transaction_id=transaction_id,
                tenant_id=tenant_id,
                idempotency_key=idempotency_key,
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
                replayed=False,
                committed_at_utc=now,
                documents=tuple(documents),
                outbox=tuple(events),
            )
            receipt = CanonicalTransactionReceipt(
                transaction_id=transaction_id,
                tenant_id=tenant_id,
                idempotency_key=idempotency_key,
                request_hash=request_hash,
                result=result,
                committed_at_utc=now,
            )
            connection.execute(
                """
                INSERT INTO vyral_canonical_transactions
                    (tenant_id, idempotency_key, transaction_id,
                     request_hash, result_json, committed_at_utc)
                VALUES (?, ?, ?, ?, ?, ?)
                """,
                (
                    receipt.tenant_id,
                    receipt.idempotency_key,
                    receipt.transaction_id,
                    receipt.request_hash,
                    _dump(receipt.result.to_dict()),
                    receipt.committed_at_utc,
                ),
            )
            return result

    def get_document(
        self,
        tenant_id: str,
        document_type: str,
        id: str,
        *,
        include_deleted: bool = False,
    ) -> CanonicalDocument | None:
        tenant, kind, document_id = validate_document_identity(
            tenant_id, document_type, id
        )
        with self._connection() as connection:
            document = self._get_document(
                connection, tenant, kind, document_id
            )
        if (
            document is not None
            and (not document.deleted or include_deleted)
        ):
            return document
        return None

    def query_documents(
        self,
        query: CanonicalDocumentQuery | Mapping[str, Any],
    ) -> CanonicalDocumentQueryResult:
        selected = CanonicalDocumentQuery.from_value(query)
        tenant_id = validate_tenant_id(selected.tenant_id)
        validate_query_limit(selected.limit, "Canonical query limit")
        if (
            selected.order_direction
            not in {"ascending", "descending"}
        ):
            raise CanonicalValidationError(
                f"Canonical document order direction "
                f"{selected.order_direction!r} is not supported."
            )
        if selected.document_type is not None:
            _validate_short_text(
                selected.document_type,
                "Canonical document type",
            )
        _validate_query_indexes(selected.indexes)
        if selected.order_by_index is not None:
            _validate_short_text(
                selected.order_by_index,
                "Canonical document order index",
            )
        if selected.index_range is not None:
            _validate_short_text(
                selected.index_range.name,
                "Canonical document range index",
            )
            if (
                selected.index_range.greater_than_or_equal is None
                and selected.index_range.less_than_or_equal is None
            ):
                raise CanonicalValidationError(
                    "Canonical document range requires a lower or "
                    "upper bound."
                )
            for value in (
                selected.index_range.greater_than_or_equal,
                selected.index_range.less_than_or_equal,
            ):
                if value is not None and len(value) > 4_096:
                    raise CanonicalValidationError(
                        "Canonical document range bound is too long."
                    )

        limit = selected.limit or 100
        has_index_order = selected.order_by_index is not None
        if has_index_order:
            last_order, last_type, last_id = (
                _decode_ordered_continuation(
                    selected.continuation_token
                )
            )
        else:
            last_type, last_id = _decode_document_continuation(
                selected.continuation_token
            )
            last_order = None
        predicates = ["d.tenant_id = ?"]
        parameters: list[object] = [tenant_id]
        if not selected.include_deleted:
            predicates.append("d.deleted = 0")
        if selected.document_type is not None:
            predicates.append("d.document_type = ?")
            parameters.append(selected.document_type.strip())
        direction_operator = (
            "<"
            if selected.order_direction == "descending"
            else ">"
        )
        if has_index_order and last_order is not None:
            predicates.append(
                f"(oi.index_value {direction_operator} ? OR "
                f"(oi.index_value = ? AND "
                f"(d.document_type {direction_operator} ? OR "
                f"(d.document_type = ? AND "
                f"d.document_id {direction_operator} ?))))"
            )
            parameters.extend(
                [
                    last_order,
                    last_order,
                    last_type,
                    last_type,
                    last_id,
                ]
            )
        elif last_type is not None:
            predicates.append(
                "(d.document_type > ? OR "
                "(d.document_type = ? AND d.document_id > ?))"
            )
            parameters.extend([last_type, last_type, last_id])
        for index, (name, value) in enumerate(
            sorted(selected.indexes.items())
        ):
            alias = f"ci{index}"
            predicates.append(
                f"EXISTS (SELECT 1 FROM "
                f"vyral_canonical_document_indexes {alias} "
                f"WHERE {alias}.tenant_id = d.tenant_id "
                f"AND {alias}.document_type = d.document_type "
                f"AND {alias}.document_id = d.document_id "
                f"AND {alias}.index_name = ? "
                f"AND {alias}.index_value = ?)"
            )
            parameters.extend([name, value])
        if selected.index_range is not None:
            range_predicates = [
                "cir.tenant_id = d.tenant_id",
                "cir.document_type = d.document_type",
                "cir.document_id = d.document_id",
                "cir.index_name = ?",
            ]
            parameters.append(selected.index_range.name.strip())
            if (
                selected.index_range.greater_than_or_equal
                is not None
            ):
                range_predicates.append("cir.index_value >= ?")
                parameters.append(
                    selected.index_range.greater_than_or_equal
                )
            if (
                selected.index_range.less_than_or_equal is not None
            ):
                range_predicates.append("cir.index_value <= ?")
                parameters.append(
                    selected.index_range.less_than_or_equal
                )
            predicates.append(
                "EXISTS (SELECT 1 FROM "
                "vyral_canonical_document_indexes cir WHERE "
                + " AND ".join(range_predicates)
                + ")"
            )
        join = ""
        selection = "d.document_json"
        order = "d.document_type, d.document_id"
        if has_index_order:
            order_by_index = selected.order_by_index
            if order_by_index is None:
                raise AssertionError(
                    "Index ordering requires an index name."
                )
            join = (
                " INNER JOIN vyral_canonical_document_indexes oi "
                "ON oi.tenant_id = d.tenant_id "
                "AND oi.document_type = d.document_type "
                "AND oi.document_id = d.document_id "
                "AND oi.index_name = ?"
            )
            # The JOIN placeholder precedes all WHERE placeholders.
            parameters.insert(0, order_by_index.strip())
            direction = (
                "DESC"
                if selected.order_direction == "descending"
                else "ASC"
            )
            order = (
                f"oi.index_value {direction}, "
                f"d.document_type {direction}, "
                f"d.document_id {direction}"
            )
            selection = "d.document_json, oi.index_value"
        parameters.append(limit + 1)
        sql = (
            f"SELECT {selection} "
            f"FROM vyral_canonical_documents d{join} "
            f"WHERE {' AND '.join(predicates)} "
            f"ORDER BY {order} LIMIT ?"
        )
        with self._connection() as connection:
            rows = connection.execute(sql, parameters).fetchall()
        materialized = [
            (
                CanonicalDocument.from_value(
                    _load_object(str(row[0]))
                ),
                str(row[1]) if has_index_order else None,
            )
            for row in rows
        ]
        continuation: str | None = None
        if len(materialized) > limit:
            last_document, order_value = materialized[limit - 1]
            continuation = (
                _encode_ordered_continuation(
                    cast(str, order_value),
                    last_document.document_type,
                    last_document.id,
                )
                if has_index_order
                else _encode_document_continuation(
                    last_document.document_type, last_document.id
                )
            )
            materialized.pop()
        return CanonicalDocumentQueryResult(
            items=tuple(item[0] for item in materialized),
            continuation_token=continuation,
        )

    def get_revisions(
        self,
        tenant_id: str,
        document_type: str,
        id: str,
        *,
        limit: int = 100,
    ) -> tuple[CanonicalDocumentRevision, ...]:
        tenant, kind, document_id = validate_document_identity(
            tenant_id, document_type, id
        )
        validate_query_limit(limit, "Canonical revision limit")
        with self._connection() as connection:
            rows = connection.execute(
                """
                SELECT revision_json
                FROM vyral_canonical_revisions
                WHERE tenant_id = ?
                  AND document_type = ?
                  AND document_id = ?
                ORDER BY revision DESC
                LIMIT ?
                """,
                (tenant, kind, document_id, limit),
            ).fetchall()
        return tuple(
            CanonicalDocumentRevision.from_value(
                _load_object(str(row["revision_json"]))
            )
            for row in rows
        )

    def lease_outbox(
        self,
        request: CanonicalOutboxLeaseRequest | Mapping[str, Any],
    ) -> tuple[CanonicalOutboxLease, ...]:
        selected = validate_outbox_lease(request)
        now = self._now()
        expires = _add_seconds(now, selected.lease_seconds)
        leases: list[CanonicalOutboxLease] = []
        with self._transaction() as connection:
            rows = connection.execute(
                """
                SELECT event_json, delivery_count, delivered_at_utc,
                       lease_owner, lease_expires_at_utc, last_error,
                       max_delivery_attempts, dead_lettered_at_utc
                FROM vyral_canonical_outbox
                WHERE tenant_id = ?
                  AND delivered_at_utc IS NULL
                  AND dead_lettered_at_utc IS NULL
                  AND (not_before_utc IS NULL OR not_before_utc <= ?)
                  AND (lease_expires_at_utc IS NULL
                       OR lease_expires_at_utc <= ?)
                ORDER BY COALESCE(not_before_utc, ''), event_id
                LIMIT ?
                """,
                (
                    selected.tenant_id.strip(),
                    now,
                    now,
                    selected.max_items,
                ),
            ).fetchall()
            for row in rows:
                event = _read_outbox_event(row)
                token = self._lease_token_factory()
                if not token.strip():
                    raise CanonicalStoreError(
                        "Canonical lease token factory returned an "
                        "empty token."
                    )
                cursor = connection.execute(
                    """
                    UPDATE vyral_canonical_outbox
                    SET lease_owner = ?,
                        lease_token_hash = ?,
                        lease_expires_at_utc = ?,
                        delivery_count = delivery_count + 1
                    WHERE tenant_id = ?
                      AND event_id = ?
                      AND delivered_at_utc IS NULL
                      AND (lease_expires_at_utc IS NULL
                           OR lease_expires_at_utc <= ?)
                    """,
                    (
                        selected.consumer_id.strip(),
                        hash_lease_token(token),
                        expires,
                        selected.tenant_id.strip(),
                        event.id,
                        now,
                    ),
                )
                if cursor.rowcount == 0:
                    continue
                leased = replace(
                    event,
                    lease_owner=selected.consumer_id.strip(),
                    lease_expires_at_utc=expires,
                    delivery_count=event.delivery_count + 1,
                )
                leases.append(
                    CanonicalOutboxLease(
                        event=leased,
                        lease_token=token,
                        expires_at_utc=expires,
                    )
                )
        return tuple(leases)

    def query_outbox(
        self,
        query: CanonicalOutboxQuery | Mapping[str, Any],
    ) -> CanonicalOutboxQueryResult:
        selected = validate_outbox_query(query)
        tenant_id = selected.tenant_id.strip()
        now = self._now()
        limit = selected.limit or 100
        last_event_id = _decode_outbox_continuation(
            selected.continuation_token
        )
        predicates = ["tenant_id = ?"]
        parameters: list[object] = [tenant_id]
        if last_event_id is not None:
            predicates.append("event_id > ?")
            parameters.append(last_event_id)
        if selected.topic is not None:
            predicates.append(
                "json_extract(event_json, '$.topic') = ?"
            )
            parameters.append(selected.topic.strip())
        states = {
            "ready": (
                "delivered_at_utc IS NULL AND "
                "dead_lettered_at_utc IS NULL AND "
                "(not_before_utc IS NULL OR not_before_utc <= ?) "
                "AND (lease_expires_at_utc IS NULL OR "
                "lease_expires_at_utc <= ?)"
            ),
            "leased": (
                "delivered_at_utc IS NULL AND "
                "dead_lettered_at_utc IS NULL AND "
                "lease_expires_at_utc > ?"
            ),
            "scheduled": (
                "delivered_at_utc IS NULL AND "
                "dead_lettered_at_utc IS NULL AND "
                "not_before_utc > ? AND "
                "(lease_expires_at_utc IS NULL OR "
                "lease_expires_at_utc <= ?)"
            ),
            "delivered": "delivered_at_utc IS NOT NULL",
            "dead-letter": "dead_lettered_at_utc IS NOT NULL",
        }
        if selected.state is not None:
            predicates.append(states[selected.state])
            if selected.state in {"ready", "scheduled"}:
                parameters.extend([now, now])
            elif selected.state == "leased":
                parameters.append(now)
        parameters.append(limit + 1)
        with self._connection() as connection:
            rows = connection.execute(
                """
                SELECT event_json, delivery_count, delivered_at_utc,
                       lease_owner, lease_expires_at_utc, last_error,
                       max_delivery_attempts, dead_lettered_at_utc
                FROM vyral_canonical_outbox
                WHERE """
                + " AND ".join(predicates)
                + " ORDER BY event_id LIMIT ?",
                parameters,
            ).fetchall()
        items = [_read_outbox_event(row) for row in rows]
        continuation = (
            _encode_outbox_continuation(items[limit - 1].id)
            if len(items) > limit
            else None
        )
        if len(items) > limit:
            items.pop()
        return CanonicalOutboxQueryResult(
            items=tuple(items),
            continuation_token=continuation,
        )

    def renew_outbox_lease(
        self,
        request: CanonicalOutboxLeaseRenewalRequest
        | Mapping[str, Any],
    ) -> CanonicalOutboxLeaseRenewal:
        selected = validate_outbox_renewal(request)
        now = self._now()
        expires = _add_seconds(now, selected.lease_seconds)
        with self._connection() as connection:
            cursor = connection.execute(
                """
                UPDATE vyral_canonical_outbox
                SET lease_expires_at_utc = ?
                WHERE tenant_id = ?
                  AND event_id = ?
                  AND delivered_at_utc IS NULL
                  AND lease_token_hash = ?
                  AND lease_expires_at_utc > ?
                """,
                (
                    expires,
                    selected.tenant_id.strip(),
                    selected.event_id.strip(),
                    hash_lease_token(selected.lease_token),
                    now,
                ),
            )
        if cursor.rowcount == 0:
            raise CanonicalConflictError(
                "Canonical outbox lease is not active for this "
                "renewal."
            )
        return CanonicalOutboxLeaseRenewal(expires)

    def acknowledge_outbox(
        self, tenant_id: str, event_id: str, lease_token: str
    ) -> None:
        tenant, event, token = validate_outbox_acknowledgement(
            tenant_id, event_id, lease_token
        )
        now = self._now()
        with self._connection() as connection:
            cursor = connection.execute(
                """
                UPDATE vyral_canonical_outbox
                SET delivered_at_utc =
                        COALESCE(delivered_at_utc, ?),
                    lease_owner = NULL,
                    lease_expires_at_utc = NULL
                WHERE tenant_id = ?
                  AND event_id = ?
                  AND lease_token_hash = ?
                  AND (delivered_at_utc IS NOT NULL
                       OR lease_expires_at_utc > ?)
                """,
                (
                    now,
                    tenant,
                    event,
                    hash_lease_token(token),
                    now,
                ),
            )
        if cursor.rowcount == 0:
            raise CanonicalConflictError(
                "Canonical outbox lease is not active for this "
                "acknowledgement."
            )

    def nack_outbox(
        self,
        request: CanonicalOutboxNackRequest | Mapping[str, Any],
    ) -> None:
        selected = validate_outbox_nack(request)
        now = self._now()
        not_before = (
            selected.not_before_utc
            if selected.not_before_utc is not None
            else _add_seconds(
                now,
                selected.retry_after_seconds
                or DEFAULT_OUTBOX_RETRY_DELAY_SECONDS,
            )
        )
        with self._connection() as connection:
            cursor = connection.execute(
                """
                UPDATE vyral_canonical_outbox
                SET lease_owner = NULL,
                    lease_token_hash = NULL,
                    lease_expires_at_utc = NULL,
                    not_before_utc =
                        CASE
                            WHEN max_delivery_attempts IS NOT NULL
                                 AND delivery_count >=
                                     max_delivery_attempts
                            THEN NULL
                            ELSE ?
                        END,
                    dead_lettered_at_utc =
                        CASE
                            WHEN max_delivery_attempts IS NOT NULL
                                 AND delivery_count >=
                                     max_delivery_attempts
                            THEN ?
                            ELSE dead_lettered_at_utc
                        END,
                    last_error = ?
                WHERE tenant_id = ?
                  AND event_id = ?
                  AND delivered_at_utc IS NULL
                  AND lease_token_hash = ?
                  AND lease_expires_at_utc > ?
                """,
                (
                    not_before,
                    now,
                    _trim_error(selected.error),
                    selected.tenant_id.strip(),
                    selected.event_id.strip(),
                    hash_lease_token(selected.lease_token),
                    now,
                ),
            )
        if cursor.rowcount == 0:
            raise CanonicalConflictError(
                "Canonical outbox lease is not active for this release."
            )

    def replay_outbox(
        self,
        request: CanonicalOutboxReplayRequest | Mapping[str, Any],
    ) -> None:
        selected = validate_outbox_replay(request)
        with self._connection() as connection:
            cursor = connection.execute(
                """
                UPDATE vyral_canonical_outbox
                SET lease_owner = NULL,
                    lease_token_hash = NULL,
                    lease_expires_at_utc = NULL,
                    not_before_utc = ?,
                    dead_lettered_at_utc = NULL,
                    last_error = NULL,
                    delivery_count =
                        CASE WHEN ? THEN 0 ELSE delivery_count END
                WHERE tenant_id = ?
                  AND event_id = ?
                  AND dead_lettered_at_utc IS NOT NULL
                """,
                (
                    self._now(),
                    1 if selected.reset_delivery_count else 0,
                    selected.tenant_id.strip(),
                    selected.event_id.strip(),
                ),
            )
        if cursor.rowcount == 0:
            raise CanonicalConflictError(
                "Canonical outbox event is not dead-lettered for this "
                "replay."
            )

    def export_tenant(
        self, tenant_id: str
    ) -> CanonicalTenantSnapshot:
        snapshot = self._export_tenant_snapshot(tenant_id)
        if canonical_snapshot_byte_count(snapshot) > 67_108_864:
            raise CanonicalValidationError(
                "Canonical snapshot exceeds the portable size limit."
            )
        return snapshot

    def export_tenant_archive(
        self,
        tenant_id: str,
        *,
        chunk_bytes: int = DEFAULT_ARCHIVE_CHUNK_BYTES,
    ) -> CanonicalTenantArchive:
        return create_canonical_archive(
            self._export_tenant_snapshot(tenant_id),
            chunk_bytes,
        )

    def restore_tenant(
        self,
        request: CanonicalRestoreRequest | Mapping[str, Any],
    ) -> None:
        selected = validate_restore(request)
        self._restore_snapshot(selected.snapshot)

    def restore_tenant_archive(
        self,
        request: CanonicalArchiveRestoreRequest | Mapping[str, Any],
    ) -> None:
        snapshot = read_canonical_archive(request)
        self._restore_snapshot(snapshot)

    def run_data_plane_preflight(
        self,
    ) -> CanonicalDataPlanePreflightResult:
        started = monotonic()
        checked_at = self._now()
        suffix = uuid4().hex
        tenant_a = f"preflight-a-{suffix}"
        tenant_b = f"preflight-b-{suffix}"
        backup = False
        isolation = False
        cleanup = False
        archive_chunks = 0
        failure = False
        try:
            self.commit(
                _preflight_upsert(
                    tenant_a, "seed-a", "A", "marker"
                )
            )
            self.commit(
                _preflight_upsert(
                    tenant_b, "seed-b", "B", "marker"
                )
            )
            archive = self.export_tenant_archive(
                tenant_a, chunk_bytes=128
            )
            archive_chunks = len(archive.chunks)
            self.commit(
                _preflight_upsert(
                    tenant_a, "temporary", "temporary", "extra"
                )
            )
            self.restore_tenant_archive(
                CanonicalArchiveRestoreRequest(
                    archive=archive,
                    expected_content_hash=archive.content_hash,
                )
            )
            backup = (
                self.get_document(
                    tenant_a, "probe", "marker"
                )
                is not None
                and self.get_document(
                    tenant_a, "probe", "extra"
                )
                is None
            )
            document_a = self.get_document(
                tenant_a, "probe", "marker"
            )
            document_b = self.get_document(
                tenant_b, "probe", "marker"
            )
            isolation = (
                document_a is not None
                and document_b is not None
                and isinstance(document_a.data, Mapping)
                and isinstance(document_b.data, Mapping)
                and document_a.data.get("value") == "A"
                and document_b.data.get("value") == "B"
            )
        except Exception:
            failure = True
        finally:
            try:
                with self._transaction() as connection:
                    self._delete_tenant(connection, tenant_a)
                    self._delete_tenant(connection, tenant_b)
                cleanup = (
                    self.get_document(
                        tenant_a,
                        "probe",
                        "marker",
                        include_deleted=True,
                    )
                    is None
                    and self.get_document(
                        tenant_b,
                        "probe",
                        "marker",
                        include_deleted=True,
                    )
                    is None
                )
            except Exception:
                cleanup = False
                failure = True
        checks = (
            CanonicalDataPlanePreflightCheck(
                id="backup-restore",
                status="passed" if backup else "failed",
                message=(
                    "Isolated archive restore passed."
                    if backup
                    else "Isolated archive restore failed."
                ),
            ),
            CanonicalDataPlanePreflightCheck(
                id="tenant-isolation",
                status="passed" if isolation else "failed",
                message=(
                    "Ephemeral tenant isolation passed."
                    if isolation
                    else "Ephemeral tenant isolation failed."
                ),
            ),
            CanonicalDataPlanePreflightCheck(
                id="cleanup",
                status="passed" if cleanup else "failed",
                message=(
                    "Ephemeral tenant cleanup passed."
                    if cleanup
                    else "Ephemeral tenant cleanup failed."
                ),
            ),
        )
        ready = (
            not failure and backup and isolation and cleanup
        )
        return CanonicalDataPlanePreflightResult(
            ready=ready,
            status="passed" if ready else "failed",
            checked_at_utc=checked_at,
            duration_ms=max(
                0, int((monotonic() - started) * 1000)
            ),
            archive_chunk_count=archive_chunks,
            backup_restore_verified=backup,
            tenant_isolation_verified=isolation,
            cleanup_verified=cleanup,
            checks=checks,
        )

    def diagnostics(self) -> dict[str, JSONValue]:
        with self._connection() as connection:
            quick_check = str(
                connection.execute("PRAGMA quick_check").fetchone()[0]
            )
            journal_mode = str(
                connection.execute("PRAGMA journal_mode").fetchone()[0]
            )
            foreign_keys = bool(
                connection.execute("PRAGMA foreign_keys").fetchone()[0]
            )
        return {
            "store": "SQLiteCanonicalStore",
            "databasePath": str(self.database_path),
            "healthy": (
                quick_check.lower() == "ok"
                and foreign_keys
                and journal_mode.lower() == "wal"
            ),
            "quickCheck": quick_check,
            "foreignKeys": foreign_keys,
            "journalMode": journal_mode,
        }

    def _export_tenant_snapshot(
        self, tenant_id: str
    ) -> CanonicalTenantSnapshot:
        tenant = validate_tenant_id(tenant_id)
        with self._transaction(immediate=False) as connection:
            documents = tuple(
                CanonicalDocument.from_value(
                    _load_object(str(row["document_json"]))
                )
                for row in connection.execute(
                    """
                    SELECT document_json
                    FROM vyral_canonical_documents
                    WHERE tenant_id = ?
                    ORDER BY document_type, document_id
                    """,
                    (tenant,),
                ).fetchall()
            )
            revisions = tuple(
                CanonicalDocumentRevision.from_value(
                    _load_object(str(row["revision_json"]))
                )
                for row in connection.execute(
                    """
                    SELECT revision_json
                    FROM vyral_canonical_revisions
                    WHERE tenant_id = ?
                    ORDER BY document_type, document_id, revision
                    """,
                    (tenant,),
                ).fetchall()
            )
            fences = tuple(
                CanonicalFence(
                    tenant_id=tenant,
                    name=str(row["name"]),
                    value=str(row["value"]),
                    owner_document_type=str(
                        row["owner_document_type"]
                    ),
                    owner_document_id=str(
                        row["owner_document_id"]
                    ),
                    created_at_utc=str(row["created_at_utc"]),
                    updated_at_utc=str(row["updated_at_utc"]),
                )
                for row in connection.execute(
                    """
                    SELECT name, value, owner_document_type,
                           owner_document_id, created_at_utc,
                           updated_at_utc
                    FROM vyral_canonical_fences
                    WHERE tenant_id = ?
                    ORDER BY name, value
                    """,
                    (tenant,),
                ).fetchall()
            )
            outbox = tuple(
                _read_outbox_event(row)
                for row in connection.execute(
                    """
                    SELECT event_json, delivery_count,
                           delivered_at_utc, lease_owner,
                           lease_expires_at_utc, last_error,
                           max_delivery_attempts,
                           dead_lettered_at_utc
                    FROM vyral_canonical_outbox
                    WHERE tenant_id = ?
                    ORDER BY event_id
                    """,
                    (tenant,),
                ).fetchall()
            )
            transactions = tuple(
                CanonicalTransactionReceipt(
                    tenant_id=tenant,
                    transaction_id=str(row["transaction_id"]),
                    idempotency_key=str(row["idempotency_key"]),
                    request_hash=str(row["request_hash"]),
                    result=CanonicalTransactionResult.from_value(
                        _load_object(str(row["result_json"]))
                    ),
                    committed_at_utc=str(row["committed_at_utc"]),
                )
                for row in connection.execute(
                    """
                    SELECT transaction_id, idempotency_key,
                           request_hash, result_json, committed_at_utc
                    FROM vyral_canonical_transactions
                    WHERE tenant_id = ?
                    ORDER BY transaction_id
                    """,
                    (tenant,),
                ).fetchall()
            )
            snapshot = CanonicalTenantSnapshot(
                tenant_id=tenant,
                documents=documents,
                revisions=revisions,
                fences=fences,
                outbox=outbox,
                transactions=transactions,
                exported_at_utc=self._now(),
                content_hash="",
            )
            return replace(
                snapshot,
                content_hash=canonical_snapshot_hash(snapshot),
            )

    def _restore_snapshot(
        self, snapshot: CanonicalTenantSnapshot
    ) -> None:
        # Archive restore already performed full validation; this second
        # integrity check is deliberately cheap compared with mutation.
        actual = canonical_snapshot_hash(snapshot)
        if not snapshot.content_hash or snapshot.content_hash != actual:
            raise CanonicalIntegrityError(
                "Canonical snapshot content hash does not match the "
                "requested restore."
            )
        tenant = snapshot.tenant_id.strip()
        with self._transaction() as connection:
            self._delete_tenant(connection, tenant)
            for document in snapshot.documents:
                self._upsert_document(connection, document)
                self._replace_indexes(connection, document)
            for revision in snapshot.revisions:
                self._insert_revision(connection, revision)
            for fence in snapshot.fences:
                connection.execute(
                    """
                    INSERT INTO vyral_canonical_fences
                        (tenant_id, name, value, owner_document_type,
                         owner_document_id, created_at_utc,
                         updated_at_utc)
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        fence.tenant_id,
                        fence.name,
                        fence.value,
                        fence.owner_document_type,
                        fence.owner_document_id,
                        fence.created_at_utc,
                        fence.updated_at_utc,
                    ),
                )
            for event in snapshot.outbox:
                restored = replace(
                    event,
                    lease_owner=None,
                    lease_expires_at_utc=None,
                )
                self._insert_outbox(connection, restored)
            for receipt in snapshot.transactions:
                connection.execute(
                    """
                    INSERT INTO vyral_canonical_transactions
                        (tenant_id, idempotency_key, transaction_id,
                         request_hash, result_json, committed_at_utc)
                    VALUES (?, ?, ?, ?, ?, ?)
                    """,
                    (
                        receipt.tenant_id,
                        receipt.idempotency_key,
                        receipt.transaction_id,
                        receipt.request_hash,
                        _dump(receipt.result.to_dict()),
                        receipt.committed_at_utc,
                    ),
                )

    def _apply_upsert(
        self,
        connection: sqlite3.Connection,
        tenant_id: str,
        transaction_id: str,
        mutation: CanonicalMutation,
        now: str,
    ) -> CanonicalDocument:
        source = cast(CanonicalDocument, mutation.document)
        document_type = source.document_type.strip()
        document_id = source.id.strip()
        existing = self._get_document(
            connection, tenant_id, document_type, document_id
        )
        _ensure_precondition(
            existing,
            mutation.precondition,
            document_type,
            document_id,
        )
        document = CanonicalDocument(
            tenant_id=tenant_id,
            document_type=document_type,
            id=document_id,
            schema_version=source.schema_version.strip(),
            data=_clone_json(source.data),
            indexes=dict(source.indexes),
            revision=(existing.revision if existing else 0) + 1,
            etag=(
                f"rev:{(existing.revision if existing else 0) + 1}"
            ),
            deleted=False,
            created_at_utc=(
                existing.created_at_utc if existing else now
            ),
            updated_at_utc=now,
        )
        self._upsert_document(connection, document)
        self._replace_indexes(connection, document)
        self._insert_revision(
            connection,
            CanonicalDocumentRevision(
                tenant_id=tenant_id,
                document_type=document.document_type,
                id=document.id,
                revision=document.revision,
                transaction_id=transaction_id,
                operation="upsert",
                document=document,
                recorded_at_utc=now,
            ),
        )
        return document

    def _apply_delete(
        self,
        connection: sqlite3.Connection,
        tenant_id: str,
        transaction_id: str,
        mutation: CanonicalMutation,
        now: str,
    ) -> CanonicalDocument:
        document_type, document_id = mutation_key(mutation)
        existing = self._get_document(
            connection, tenant_id, document_type, document_id
        )
        _ensure_precondition(
            existing,
            mutation.precondition,
            document_type,
            document_id,
        )
        if existing is None or existing.deleted:
            raise CanonicalConflictError(
                f"Canonical document {document_type!r}/{document_id!r} "
                "cannot be deleted because it does not exist."
            )
        revision = existing.revision + 1
        document = replace(
            existing,
            revision=revision,
            etag=f"rev:{revision}",
            deleted=True,
            data=None,
            indexes={},
            updated_at_utc=now,
        )
        self._upsert_document(connection, document)
        self._replace_indexes(connection, document)
        self._insert_revision(
            connection,
            CanonicalDocumentRevision(
                tenant_id=tenant_id,
                document_type=document.document_type,
                id=document.id,
                revision=document.revision,
                transaction_id=transaction_id,
                operation="delete",
                document=document,
                recorded_at_utc=now,
            ),
        )
        return document

    def _apply_fence(
        self,
        connection: sqlite3.Connection,
        tenant_id: str,
        mutation: Any,
        now: str,
    ) -> None:
        name = mutation.name.strip()
        value = mutation.value.strip()
        owner_type = mutation.owner_document_type.strip()
        owner_id = mutation.owner_document_id.strip()
        row = connection.execute(
            """
            SELECT owner_document_type, owner_document_id
            FROM vyral_canonical_fences
            WHERE tenant_id = ? AND name = ? AND value = ?
            """,
            (tenant_id, name, value),
        ).fetchone()
        if mutation.operation == "claim":
            if row is not None and (
                str(row["owner_document_type"]) != owner_type
                or str(row["owner_document_id"]) != owner_id
            ):
                raise CanonicalConflictError(
                    f"Canonical fence {name!r}/{value!r} is already "
                    "owned by another document."
                )
            if row is None:
                connection.execute(
                    """
                    INSERT INTO vyral_canonical_fences
                        (tenant_id, name, value, owner_document_type,
                         owner_document_id, created_at_utc,
                         updated_at_utc)
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        tenant_id,
                        name,
                        value,
                        owner_type,
                        owner_id,
                        now,
                        now,
                    ),
                )
            return
        if row is None or (
            str(row["owner_document_type"]) != owner_type
            or str(row["owner_document_id"]) != owner_id
        ):
            raise CanonicalConflictError(
                f"Canonical fence {name!r}/{value!r} cannot be "
                "released by this owner."
            )
        connection.execute(
            """
            DELETE FROM vyral_canonical_fences
            WHERE tenant_id = ? AND name = ? AND value = ?
            """,
            (tenant_id, name, value),
        )

    def _get_document(
        self,
        connection: sqlite3.Connection,
        tenant_id: str,
        document_type: str,
        document_id: str,
    ) -> CanonicalDocument | None:
        row = connection.execute(
            """
            SELECT document_json
            FROM vyral_canonical_documents
            WHERE tenant_id = ?
              AND document_type = ?
              AND document_id = ?
            """,
            (tenant_id, document_type, document_id),
        ).fetchone()
        return (
            CanonicalDocument.from_value(
                _load_object(str(row["document_json"]))
            )
            if row is not None
            else None
        )

    def _upsert_document(
        self,
        connection: sqlite3.Connection,
        document: CanonicalDocument,
    ) -> None:
        connection.execute(
            """
            INSERT INTO vyral_canonical_documents
                (tenant_id, document_type, document_id, revision,
                 etag, deleted, document_json, created_at_utc,
                 updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(tenant_id, document_type, document_id)
            DO UPDATE SET
                revision = excluded.revision,
                etag = excluded.etag,
                deleted = excluded.deleted,
                document_json = excluded.document_json,
                updated_at_utc = excluded.updated_at_utc
            """,
            (
                document.tenant_id,
                document.document_type,
                document.id,
                document.revision,
                document.etag,
                1 if document.deleted else 0,
                _dump(document.to_dict()),
                document.created_at_utc,
                document.updated_at_utc,
            ),
        )

    def _replace_indexes(
        self,
        connection: sqlite3.Connection,
        document: CanonicalDocument,
    ) -> None:
        connection.execute(
            """
            DELETE FROM vyral_canonical_document_indexes
            WHERE tenant_id = ?
              AND document_type = ?
              AND document_id = ?
            """,
            (
                document.tenant_id,
                document.document_type,
                document.id,
            ),
        )
        if document.deleted:
            return
        connection.executemany(
            """
            INSERT INTO vyral_canonical_document_indexes
                (tenant_id, document_type, document_id,
                 index_name, index_value)
            VALUES (?, ?, ?, ?, ?)
            """,
            (
                (
                    document.tenant_id,
                    document.document_type,
                    document.id,
                    name,
                    value,
                )
                for name, value in document.indexes.items()
            ),
        )

    def _insert_revision(
        self,
        connection: sqlite3.Connection,
        revision: CanonicalDocumentRevision,
    ) -> None:
        connection.execute(
            """
            INSERT INTO vyral_canonical_revisions
                (tenant_id, document_type, document_id, revision,
                 transaction_id, revision_json, recorded_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            (
                revision.tenant_id,
                revision.document_type,
                revision.id,
                revision.revision,
                revision.transaction_id,
                _dump(revision.to_dict()),
                revision.recorded_at_utc,
            ),
        )

    def _insert_outbox(
        self,
        connection: sqlite3.Connection,
        event: CanonicalOutboxEvent,
    ) -> None:
        connection.execute(
            """
            INSERT INTO vyral_canonical_outbox
                (tenant_id, event_id, transaction_id, event_json,
                 not_before_utc, delivery_count, delivered_at_utc,
                 lease_owner, lease_token_hash,
                 lease_expires_at_utc, last_error,
                 max_delivery_attempts, dead_lettered_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, NULL, ?, ?, ?, ?)
            """,
            (
                event.tenant_id,
                event.id,
                event.transaction_id,
                _dump(event.to_dict()),
                event.not_before_utc,
                event.delivery_count,
                event.delivered_at_utc,
                event.lease_owner,
                event.lease_expires_at_utc,
                _trim_error(event.last_error),
                event.max_delivery_attempts,
                event.dead_lettered_at_utc,
            ),
        )

    def _get_receipt(
        self,
        connection: sqlite3.Connection,
        tenant_id: str,
        idempotency_key: str,
    ) -> CanonicalTransactionReceipt | None:
        row = connection.execute(
            """
            SELECT transaction_id, request_hash, result_json,
                   committed_at_utc
            FROM vyral_canonical_transactions
            WHERE tenant_id = ? AND idempotency_key = ?
            """,
            (tenant_id, idempotency_key),
        ).fetchone()
        if row is None:
            return None
        return CanonicalTransactionReceipt(
            tenant_id=tenant_id,
            idempotency_key=idempotency_key,
            transaction_id=str(row["transaction_id"]),
            request_hash=str(row["request_hash"]),
            result=CanonicalTransactionResult.from_value(
                _load_object(str(row["result_json"]))
            ),
            committed_at_utc=str(row["committed_at_utc"]),
        )

    def _delete_tenant(
        self, connection: sqlite3.Connection, tenant_id: str
    ) -> None:
        for table in (
            "vyral_canonical_document_indexes",
            "vyral_canonical_revisions",
            "vyral_canonical_fences",
            "vyral_canonical_outbox",
            "vyral_canonical_transactions",
            "vyral_canonical_documents",
        ):
            connection.execute(
                f"DELETE FROM {table} WHERE tenant_id = ?",
                (tenant_id,),
            )

    def _now(self) -> str:
        return utc_timestamp(self._clock(), "canonical clock")

    @contextmanager
    def _connection(self) -> Iterator[sqlite3.Connection]:
        connection = sqlite3.connect(
            self.database_path,
            timeout=self.busy_timeout_ms / 1000,
            isolation_level=None,
        )
        connection.row_factory = sqlite3.Row
        try:
            connection.execute("PRAGMA foreign_keys = ON")
            connection.execute(
                f"PRAGMA busy_timeout = {self.busy_timeout_ms}"
            )
            yield connection
        finally:
            connection.close()

    @contextmanager
    def _transaction(
        self, *, immediate: bool = True
    ) -> Iterator[sqlite3.Connection]:
        with self._connection() as connection:
            connection.execute(
                "BEGIN IMMEDIATE" if immediate else "BEGIN"
            )
            try:
                yield connection
                connection.commit()
            except BaseException:
                connection.rollback()
                raise

    def _ensure_initialized(self) -> None:
        if self._initialized:
            return
        with self._initialization_lock:
            if self._initialized:
                return
            with self._connection() as connection:
                connection.execute("PRAGMA journal_mode = WAL")
                connection.executescript(_SCHEMA)
            self._initialized = True


class AsyncSQLiteCanonicalStore:
    """Bounded asynchronous facade over complete canonical operations."""

    def __init__(
        self,
        store: SQLiteCanonicalStore,
        *,
        executor: RuntimeExecutor | None = None,
    ) -> None:
        self.store = store
        self.executor = executor or RuntimeExecutor()
        self._owns_executor = executor is None

    async def apply_migrations(
        self,
        migrations: Sequence[CanonicalMigration | Mapping[str, Any]],
    ) -> None:
        await self.executor.run(
            lambda: self.store.apply_migrations(migrations)
        )

    async def list_migrations(
        self,
    ) -> tuple[CanonicalMigrationReceipt, ...]:
        return await self.executor.run(self.store.list_migrations)

    async def commit(
        self,
        request: CanonicalTransactionRequest | Mapping[str, Any],
    ) -> CanonicalTransactionResult:
        return await self.executor.run(
            lambda: self.store.commit(request)
        )

    async def get_document(
        self,
        tenant_id: str,
        document_type: str,
        id: str,
        *,
        include_deleted: bool = False,
    ) -> CanonicalDocument | None:
        return await self.executor.run(
            lambda: self.store.get_document(
                tenant_id,
                document_type,
                id,
                include_deleted=include_deleted,
            )
        )

    async def query_documents(
        self,
        query: CanonicalDocumentQuery | Mapping[str, Any],
    ) -> CanonicalDocumentQueryResult:
        return await self.executor.run(
            lambda: self.store.query_documents(query)
        )

    async def get_revisions(
        self,
        tenant_id: str,
        document_type: str,
        id: str,
        *,
        limit: int = 100,
    ) -> tuple[CanonicalDocumentRevision, ...]:
        return await self.executor.run(
            lambda: self.store.get_revisions(
                tenant_id,
                document_type,
                id,
                limit=limit,
            )
        )

    async def lease_outbox(
        self,
        request: CanonicalOutboxLeaseRequest | Mapping[str, Any],
    ) -> tuple[CanonicalOutboxLease, ...]:
        return await self.executor.run(
            lambda: self.store.lease_outbox(request)
        )

    async def query_outbox(
        self,
        query: CanonicalOutboxQuery | Mapping[str, Any],
    ) -> CanonicalOutboxQueryResult:
        return await self.executor.run(
            lambda: self.store.query_outbox(query)
        )

    async def renew_outbox_lease(
        self,
        request: CanonicalOutboxLeaseRenewalRequest
        | Mapping[str, Any],
    ) -> CanonicalOutboxLeaseRenewal:
        return await self.executor.run(
            lambda: self.store.renew_outbox_lease(request)
        )

    async def acknowledge_outbox(
        self, tenant_id: str, event_id: str, lease_token: str
    ) -> None:
        await self.executor.run(
            lambda: self.store.acknowledge_outbox(
                tenant_id, event_id, lease_token
            )
        )

    async def nack_outbox(
        self,
        request: CanonicalOutboxNackRequest | Mapping[str, Any],
    ) -> None:
        await self.executor.run(
            lambda: self.store.nack_outbox(request)
        )

    async def replay_outbox(
        self,
        request: CanonicalOutboxReplayRequest | Mapping[str, Any],
    ) -> None:
        await self.executor.run(
            lambda: self.store.replay_outbox(request)
        )

    async def export_tenant(
        self, tenant_id: str
    ) -> CanonicalTenantSnapshot:
        return await self.executor.run(
            lambda: self.store.export_tenant(tenant_id)
        )

    async def restore_tenant(
        self,
        request: CanonicalRestoreRequest | Mapping[str, Any],
    ) -> None:
        await self.executor.run(
            lambda: self.store.restore_tenant(request)
        )

    async def export_tenant_archive(
        self,
        tenant_id: str,
        *,
        chunk_bytes: int = DEFAULT_ARCHIVE_CHUNK_BYTES,
    ) -> CanonicalTenantArchive:
        return await self.executor.run(
            lambda: self.store.export_tenant_archive(
                tenant_id, chunk_bytes=chunk_bytes
            )
        )

    async def restore_tenant_archive(
        self,
        request: CanonicalArchiveRestoreRequest | Mapping[str, Any],
    ) -> None:
        await self.executor.run(
            lambda: self.store.restore_tenant_archive(request)
        )

    async def run_data_plane_preflight(
        self,
    ) -> CanonicalDataPlanePreflightResult:
        return await self.executor.run(
            self.store.run_data_plane_preflight
        )

    def close(self) -> None:
        if self._owns_executor:
            self.executor.close()


def _preflight_upsert(
    tenant_id: str,
    idempotency_key: str,
    value: str,
    id: str,
) -> CanonicalTransactionRequest:
    return CanonicalTransactionRequest(
        tenant_id=tenant_id,
        idempotency_key=idempotency_key,
        mutations=(
            CanonicalMutation(
                document=CanonicalDocument(
                    tenant_id=tenant_id,
                    document_type="probe",
                    id=id,
                    schema_version="v1",
                    data={"value": value},
                )
            ),
        ),
    )


def _ensure_precondition(
    existing: CanonicalDocument | None,
    precondition: CanonicalWritePrecondition | None,
    document_type: str,
    id: str,
) -> None:
    if precondition is None:
        return
    if precondition.must_not_exist and existing is not None:
        raise CanonicalConflictError(
            f"Canonical write precondition failed: document "
            f"{document_type!r}/{id!r} already exists."
        )
    if precondition.must_exist and existing is None:
        raise CanonicalConflictError(
            f"Canonical write precondition failed: document "
            f"{document_type!r}/{id!r} does not exist."
        )
    if (
        precondition.expected_revision is not None
        and (
            existing is None
            or existing.revision
            != precondition.expected_revision
        )
    ):
        raise CanonicalConflictError(
            f"Canonical write precondition failed: document "
            f"{document_type!r}/{id!r} revision does not match."
        )


def _read_outbox_event(row: sqlite3.Row) -> CanonicalOutboxEvent:
    event = CanonicalOutboxEvent.from_value(
        _load_object(str(row["event_json"]))
    )
    return replace(
        event,
        delivery_count=int(row["delivery_count"]),
        delivered_at_utc=cast(str | None, row["delivered_at_utc"]),
        lease_owner=cast(str | None, row["lease_owner"]),
        lease_expires_at_utc=cast(
            str | None, row["lease_expires_at_utc"]
        ),
        last_error=cast(str | None, row["last_error"]),
        max_delivery_attempts=(
            int(row["max_delivery_attempts"])
            if row["max_delivery_attempts"] is not None
            else None
        ),
        dead_lettered_at_utc=cast(
            str | None, row["dead_lettered_at_utc"]
        ),
    )


def _clone_json(value: JSONValue) -> JSONValue:
    return cast(JSONValue, json.loads(_dump(value)))


def _dump(value: object) -> str:
    return json.dumps(
        value,
        ensure_ascii=False,
        allow_nan=False,
        separators=(",", ":"),
    )


def _load_object(value: str) -> Mapping[str, Any]:
    raw = json.loads(value)
    if not isinstance(raw, Mapping):
        raise CanonicalIntegrityError(
            "Canonical store JSON did not contain an object."
        )
    return cast(Mapping[str, Any], raw)


def _trim_error(value: str | None) -> str | None:
    if value is None or not value.strip():
        return None
    return value.strip()[:4096]


def _add_seconds(value: str, seconds: float) -> str:
    return utc_timestamp(
        parse_timestamp(value) + timedelta(seconds=seconds)
    )


def _validate_short_text(value: str, name: str) -> None:
    if not value.strip() or len(value.strip()) > 160:
        raise CanonicalValidationError(
            f"{name} is required and must not exceed 160 characters."
        )


def _validate_query_indexes(values: Mapping[str, str]) -> None:
    if len(values) > 64:
        raise CanonicalValidationError(
            "Canonical query indexes cannot contain more than 64 "
            "values."
        )
    for key, value in values.items():
        _validate_short_text(key, "Canonical query index key")
        if not value.strip() or len(value) > 4_096:
            raise CanonicalValidationError(
                "Canonical query index value is invalid."
            )


def _encode_parts(*values: str) -> str:
    return base64.b64encode("\n".join(values).encode("utf-8")).decode(
        "ascii"
    )


def _decode_parts(
    token: str | None,
    count: int,
    name: str,
) -> tuple[str, ...] | None:
    if token is None or not token.strip():
        return None
    try:
        raw = base64.b64decode(token, validate=True).decode("utf-8")
    except (ValueError, UnicodeDecodeError):
        raise CanonicalValidationError(f"{name} is not valid.") from None
    parts = tuple(raw.split("\n", count - 1))
    if (
        len(parts) != count
        or any(not part or any(ord(ch) < 32 for ch in part) for part in parts)
    ):
        raise CanonicalValidationError(f"{name} is not valid.")
    return parts


def _encode_document_continuation(
    document_type: str, id: str
) -> str:
    return _encode_parts(document_type, id)


def _decode_document_continuation(
    token: str | None,
) -> tuple[str | None, str | None]:
    parts = _decode_parts(
        token, 2, "Canonical document continuation token"
    )
    return (None, None) if parts is None else (parts[0], parts[1])


def _encode_ordered_continuation(
    order_value: str, document_type: str, id: str
) -> str:
    return _encode_parts(order_value, document_type, id)


def _decode_ordered_continuation(
    token: str | None,
) -> tuple[str | None, str | None, str | None]:
    parts = _decode_parts(
        token, 3, "Canonical ordered document continuation token"
    )
    return (
        (None, None, None)
        if parts is None
        else (parts[0], parts[1], parts[2])
    )


def _encode_outbox_continuation(event_id: str) -> str:
    return base64.b64encode(event_id.encode("utf-8")).decode("ascii")


def _decode_outbox_continuation(
    token: str | None,
) -> str | None:
    if token is None or not token.strip():
        return None
    try:
        event_id = base64.b64decode(token, validate=True).decode(
            "utf-8"
        )
    except (ValueError, UnicodeDecodeError):
        raise CanonicalValidationError(
            "Canonical outbox continuation token is invalid."
        ) from None
    if not event_id or any(ord(ch) < 32 for ch in event_id):
        raise CanonicalValidationError(
            "Canonical outbox continuation token is invalid."
        )
    return event_id


def _migration_identity(namespace: str, id: str) -> str:
    def encode(value: str) -> str:
        return (
            base64.urlsafe_b64encode(value.strip().encode("utf-8"))
            .decode("ascii")
            .rstrip("=")
        )

    return f"canon-v1.{encode(namespace)}.{encode(id)}"


def _parse_migration_identity(value: str) -> tuple[str, str]:
    prefix = "canon-v1."
    if not value.startswith(prefix):
        return ("legacy", value)
    parts = value[len(prefix) :].split(".", 1)
    if len(parts) != 2:
        raise CanonicalIntegrityError(
            "Canonical migration storage identity is invalid."
        )

    def decode(item: str) -> str:
        padding = "=" * (-len(item) % 4)
        return base64.urlsafe_b64decode(item + padding).decode("utf-8")

    try:
        return decode(parts[0]), decode(parts[1])
    except (ValueError, UnicodeDecodeError):
        raise CanonicalIntegrityError(
            "Canonical migration storage identity is invalid."
        ) from None


_SCHEMA = """
CREATE TABLE IF NOT EXISTS vyral_canonical_migrations (
    id TEXT PRIMARY KEY,
    checksum TEXT NOT NULL,
    description TEXT NULL,
    applied_at_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS vyral_canonical_documents (
    tenant_id TEXT NOT NULL,
    document_type TEXT NOT NULL,
    document_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    etag TEXT NOT NULL,
    deleted INTEGER NOT NULL,
    document_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (tenant_id, document_type, document_id)
);
CREATE TABLE IF NOT EXISTS vyral_canonical_document_indexes (
    tenant_id TEXT NOT NULL,
    document_type TEXT NOT NULL,
    document_id TEXT NOT NULL,
    index_name TEXT NOT NULL,
    index_value TEXT NOT NULL,
    PRIMARY KEY (
        tenant_id, document_type, document_id, index_name
    ),
    FOREIGN KEY (tenant_id, document_type, document_id)
        REFERENCES vyral_canonical_documents(
            tenant_id, document_type, document_id
        ) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS
    ix_vyral_canonical_document_indexes_lookup
ON vyral_canonical_document_indexes(
    tenant_id, document_type, index_name, index_value, document_id
);
CREATE TABLE IF NOT EXISTS vyral_canonical_revisions (
    tenant_id TEXT NOT NULL,
    document_type TEXT NOT NULL,
    document_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    transaction_id TEXT NOT NULL,
    revision_json TEXT NOT NULL,
    recorded_at_utc TEXT NOT NULL,
    PRIMARY KEY (
        tenant_id, document_type, document_id, revision
    )
);
CREATE TABLE IF NOT EXISTS vyral_canonical_fences (
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    value TEXT NOT NULL,
    owner_document_type TEXT NOT NULL,
    owner_document_id TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (tenant_id, name, value)
);
CREATE TABLE IF NOT EXISTS vyral_canonical_outbox (
    tenant_id TEXT NOT NULL,
    event_id TEXT NOT NULL,
    transaction_id TEXT NOT NULL,
    event_json TEXT NOT NULL,
    not_before_utc TEXT NULL,
    delivery_count INTEGER NOT NULL,
    delivered_at_utc TEXT NULL,
    lease_owner TEXT NULL,
    lease_token_hash TEXT NULL,
    lease_expires_at_utc TEXT NULL,
    last_error TEXT NULL,
    max_delivery_attempts INTEGER NULL,
    dead_lettered_at_utc TEXT NULL,
    PRIMARY KEY (tenant_id, event_id)
);
CREATE INDEX IF NOT EXISTS ix_vyral_canonical_outbox_due
ON vyral_canonical_outbox(
    tenant_id, delivered_at_utc, not_before_utc,
    lease_expires_at_utc, event_id
);
CREATE TABLE IF NOT EXISTS vyral_canonical_transactions (
    tenant_id TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    transaction_id TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    result_json TEXT NOT NULL,
    committed_at_utc TEXT NOT NULL,
    PRIMARY KEY (tenant_id, idempotency_key),
    UNIQUE (tenant_id, transaction_id)
);
"""
