from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
import tempfile
from typing import Any, Callable, Mapping, cast

from ..conformance import ConformanceError, load_conformance_manifest
from ..contracts import JSONValue
from .codec import CanonicalStoreError
from .models import (
    CanonicalArchiveRestoreRequest,
    CanonicalDocument,
    CanonicalDocumentIndexRange,
    CanonicalDocumentQuery,
    CanonicalMigration,
    CanonicalMutation,
    CanonicalOutboxLeaseRequest,
    CanonicalOutboxLeaseRenewalRequest,
    CanonicalOutboxNackRequest,
    CanonicalOutboxQuery,
    CanonicalTenantArchive,
    CanonicalTransactionRequest,
)
from .store import SQLiteCanonicalStore


_SCENARIO_ID = "canonical.strong-profile.v1"
_NOW = datetime(2026, 7, 30, 18, 0, tzinfo=timezone.utc)


@dataclass(frozen=True)
class CanonicalConformanceResult:
    scenario_id: str
    step_id: str
    operation: str
    value: JSONValue


def _object(value: object, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise ConformanceError(f"{name} must be an object.")
    return cast(Mapping[str, Any], value)


class _Runner:
    def __init__(self, store: SQLiteCanonicalStore) -> None:
        self.store = store
        self.seed_request: CanonicalTransactionRequest | None = None

    def execute(
        self, operation: str, arguments: Mapping[str, Any]
    ) -> JSONValue:
        handlers: dict[
            str, Callable[[Mapping[str, Any]], JSONValue]
        ] = {
            "canonical.commit-replay": self._commit_replay,
            "canonical.conflicts-and-tombstone": (
                self._conflicts_and_tombstone
            ),
            "canonical.outbox-lifecycle": self._outbox_lifecycle,
            "canonical.restore-roundtrip": self._restore_roundtrip,
            "canonical.migrations-query-isolation": (
                self._migrations_query_isolation
            ),
            "canonical.snapshot-codec": self._snapshot_codec,
        }
        handler = handlers.get(operation)
        if handler is None:
            raise ConformanceError(
                f"Unsupported canonical fixture operation "
                f"{operation!r}."
            )
        return handler(arguments)

    def _snapshot_codec(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        from dataclasses import replace

        from .codec import (
            canonical_snapshot_hash,
            create_canonical_archive,
            read_canonical_archive,
        )
        from .models import CanonicalTenantSnapshot

        source = CanonicalTenantSnapshot.from_value(
            _object(
                arguments.get("snapshot"),
                "arguments.snapshot",
            )
        )
        snapshot_hash = canonical_snapshot_hash(source)
        source = replace(source, content_hash=snapshot_hash)
        raw_chunk_bytes = arguments.get("chunkBytes")
        if (
            isinstance(raw_chunk_bytes, bool)
            or not isinstance(raw_chunk_bytes, int)
        ):
            raise ConformanceError(
                "arguments.chunkBytes must be an integer."
            )
        archive = create_canonical_archive(
            source, raw_chunk_bytes
        )
        round_trip = read_canonical_archive(
            CanonicalArchiveRestoreRequest(
                archive=archive,
                expected_content_hash=archive.content_hash,
            )
        )
        return {
            "snapshotHash": snapshot_hash,
            "archiveHash": archive.content_hash,
            "chunkCount": len(archive.chunks),
            "chunkHashes": [
                item.content_hash for item in archive.chunks
            ],
            "roundTripHash": canonical_snapshot_hash(round_trip),
        }

    def _commit_replay(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        request = CanonicalTransactionRequest.from_value(
            _object(arguments.get("request"), "arguments.request")
        )
        self.seed_request = request
        first = self.store.commit(request)
        replay = self.store.commit(request)
        query = self.store.query_documents(
            CanonicalDocumentQuery(
                tenant_id=request.tenant_id,
                document_type="author",
                indexes={"email": "ada@example.test"},
            )
        )
        snapshot = self.store.export_tenant(request.tenant_id)
        document = _single(first.documents, "committed document")
        outbox = _single(first.outbox, "committed outbox event")
        return {
            "transactionId": first.transaction_id,
            "firstReplayed": first.replayed,
            "replayReplayed": replay.replayed,
            "revision": document.revision,
            "etag": document.etag,
            "outboxId": outbox.id,
            "queryIds": sorted(item.id for item in query.items),
            "snapshotCounts": {
                "documents": len(snapshot.documents),
                "revisions": len(snapshot.revisions),
                "fences": len(snapshot.fences),
                "outbox": len(snapshot.outbox),
                "transactions": len(snapshot.transactions),
            },
        }

    def _conflicts_and_tombstone(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        conflict = CanonicalTransactionRequest.from_value(
            _object(
                arguments.get("conflictingFenceRequest"),
                "arguments.conflictingFenceRequest",
            )
        )
        fence_conflict = _rejects(
            lambda: self.store.commit(conflict)
        )
        conflicting_document = self.store.get_document(
            "tenant-a",
            "author",
            "a-2",
            include_deleted=True,
        )
        updated = _single(
            self.store.commit(
                CanonicalTransactionRequest.from_value(
                    _object(
                        arguments.get("updateRequest"),
                        "arguments.updateRequest",
                    )
                )
            ).documents,
            "updated document",
        )
        stale = _rejects(
            lambda: self.store.commit(
                CanonicalTransactionRequest.from_value(
                    _object(
                        arguments.get("staleRequest"),
                        "arguments.staleRequest",
                    )
                )
            )
        )
        deleted = _single(
            self.store.commit(
                CanonicalTransactionRequest.from_value(
                    _object(
                        arguments.get("deleteRequest"),
                        "arguments.deleteRequest",
                    )
                )
            ).documents,
            "deleted document",
        )
        normal = self.store.get_document(
            "tenant-a", "author", "a-1"
        )
        tombstone = self.store.get_document(
            "tenant-a",
            "author",
            "a-1",
            include_deleted=True,
        )
        revisions = self.store.get_revisions(
            "tenant-a", "author", "a-1"
        )
        return {
            "fenceConflictRejected": fence_conflict,
            "conflictingDocumentAbsent": (
                conflicting_document is None
            ),
            "updatedRevision": updated.revision,
            "staleRevisionRejected": stale,
            "deletedRevision": deleted.revision,
            "normalReadMissing": normal is None,
            "tombstoneDeleted": (
                tombstone is not None and tombstone.deleted
            ),
            "revisionOrder": [
                item.revision for item in revisions
            ],
            "revisionOperations": [
                item.operation for item in revisions
            ],
        }

    def _outbox_lifecycle(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        tenant_id = _text(
            arguments.get("tenantId"), "arguments.tenantId"
        )
        consumer_id = _text(
            arguments.get("consumerId"), "arguments.consumerId"
        )
        not_before = _text(
            arguments.get("releaseNotBeforeUtc"),
            "arguments.releaseNotBeforeUtc",
        )
        first = _single(
            self.store.lease_outbox(
                CanonicalOutboxLeaseRequest(
                    tenant_id=tenant_id,
                    consumer_id=consumer_id,
                )
            ),
            "first outbox lease",
        )
        renewal = self.store.renew_outbox_lease(
            CanonicalOutboxLeaseRenewalRequest(
                tenant_id=tenant_id,
                event_id=first.event.id,
                lease_token=first.lease_token,
                lease_seconds=120,
            )
        )
        self.store.nack_outbox(
            CanonicalOutboxNackRequest(
                tenant_id=tenant_id,
                event_id=first.event.id,
                lease_token=first.lease_token,
                not_before_utc=not_before,
                error="portable retry",
            )
        )
        second = _single(
            self.store.lease_outbox(
                CanonicalOutboxLeaseRequest(
                    tenant_id=tenant_id,
                    consumer_id=consumer_id,
                )
            ),
            "second outbox lease",
        )
        self.store.acknowledge_outbox(
            tenant_id, second.event.id, second.lease_token
        )
        self.store.acknowledge_outbox(
            tenant_id, second.event.id, second.lease_token
        )
        delivered = self.store.query_outbox(
            CanonicalOutboxQuery(
                tenant_id=tenant_id, state="delivered"
            )
        )
        after = self.store.lease_outbox(
            CanonicalOutboxLeaseRequest(
                tenant_id=tenant_id,
                consumer_id=consumer_id,
            )
        )
        return {
            "firstDeliveryCount": first.event.delivery_count,
            "secondDeliveryCount": second.event.delivery_count,
            "renewed": (
                renewal.expires_at_utc > first.expires_at_utc
            ),
            "deliveredCount": len(delivered.items),
            "leaseAfterAckCount": len(after),
        }

    def _restore_roundtrip(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        raw_chunk_bytes = arguments.get("chunkBytes")
        if (
            isinstance(raw_chunk_bytes, bool)
            or not isinstance(raw_chunk_bytes, int)
        ):
            raise ConformanceError(
                "arguments.chunkBytes must be an integer."
            )
        archive = self.store.export_tenant_archive(
            "tenant-a", chunk_bytes=raw_chunk_bytes
        )
        self.store.commit(
            CanonicalTransactionRequest.from_value(
                _object(
                    arguments.get("additionalRequest"),
                    "arguments.additionalRequest",
                )
            )
        )
        self.store.restore_tenant_archive(
            CanonicalArchiveRestoreRequest(
                archive=archive,
                expected_content_hash=archive.content_hash,
            )
        )
        original = self.store.get_document(
            "tenant-a",
            "author",
            "a-1",
            include_deleted=True,
        )
        additional = self.store.get_document(
            "tenant-a",
            "author",
            "a-2",
            include_deleted=True,
        )
        if self.seed_request is None:
            raise ConformanceError(
                "Canonical seed request is unavailable."
            )
        replay = self.store.commit(self.seed_request)
        chunks = list(archive.chunks)
        first = chunks[0]
        corrupted = bytearray(first.content)
        corrupted[0] ^= 0x01
        chunks[0] = type(first)(
            index=first.index,
            content=bytes(corrupted),
            length=first.length,
            content_hash=first.content_hash,
        )
        corrupt_archive = CanonicalTenantArchive(
            profile=archive.profile,
            tenant_id=archive.tenant_id,
            exported_at_utc=archive.exported_at_utc,
            snapshot_content_hash=archive.snapshot_content_hash,
            content_hash=archive.content_hash,
            chunks=tuple(chunks),
        )
        corruption_rejected = _rejects(
            lambda: self.store.restore_tenant_archive(
                CanonicalArchiveRestoreRequest(
                    archive=corrupt_archive,
                    expected_content_hash=(
                        corrupt_archive.content_hash
                    ),
                )
            )
        )
        return {
            "archiveHasMultipleChunks": len(archive.chunks) > 1,
            "chunkIndexesContiguous": all(
                item.index == index
                for index, item in enumerate(archive.chunks)
            ),
            "originalTombstoneRestored": (
                original is not None and original.deleted
            ),
            "additionalDocumentAbsent": additional is None,
            "idempotencyReceiptRestored": replay.replayed,
            "corruptionRejected": corruption_rejected,
        }

    def _migrations_query_isolation(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        migration = CanonicalMigration.from_value(
            _object(
                arguments.get("migration"),
                "arguments.migration",
            )
        )
        self.store.apply_migrations((migration,))
        self.store.apply_migrations((migration,))
        checksum_conflict = _rejects(
            lambda: self.store.apply_migrations(
                (
                    CanonicalMigration(
                        namespace=migration.namespace,
                        id=migration.id,
                        checksum="sha256:different",
                    ),
                )
            )
        )
        self.store.apply_migrations(
            (
                CanonicalMigration(
                    namespace="portable-other",
                    id=migration.id,
                    checksum="sha256:two",
                ),
            )
        )
        for id, rank in (
            ("e-1", "020"),
            ("e-2", "010"),
            ("e-3", "030"),
        ):
            self.store.commit(
                _upsert(
                    "tenant-a",
                    "rank-" + id,
                    "review",
                    id,
                    id,
                    {"rank": rank},
                )
            )
        self.store.commit(
            _upsert(
                "tenant-a",
                "isolation-a",
                "entity",
                "same",
                "A",
            )
        )
        self.store.commit(
            _upsert(
                "tenant-b",
                "isolation-b",
                "entity",
                "same",
                "B",
            )
        )
        first = self._page(None)
        second = self._page(first.continuation_token)
        third = self._page(second.continuation_token)
        tenant_a = self.store.get_document(
            "tenant-a", "entity", "same"
        )
        tenant_b = self.store.get_document(
            "tenant-b", "entity", "same"
        )
        if (
            tenant_a is None
            or tenant_b is None
            or not isinstance(tenant_a.data, Mapping)
            or not isinstance(tenant_b.data, Mapping)
        ):
            raise ConformanceError(
                "Canonical tenant-isolation documents are absent."
            )
        return {
            "migrationCount": len(self.store.list_migrations()),
            "checksumConflictRejected": checksum_conflict,
            "pageIds": [
                _single(first.items, "first page").id,
                _single(second.items, "second page").id,
                _single(third.items, "third page").id,
            ],
            "continuations": [
                first.continuation_token is not None,
                second.continuation_token is not None,
                third.continuation_token is not None,
            ],
            "tenantValues": [
                cast(Mapping[str, Any], tenant_a.data).get("value"),
                cast(Mapping[str, Any], tenant_b.data).get("value"),
            ],
        }

    def _page(self, token: str | None) -> Any:
        return self.store.query_documents(
            CanonicalDocumentQuery(
                tenant_id="tenant-a",
                document_type="review",
                order_by_index="rank",
                order_direction="descending",
                index_range=CanonicalDocumentIndexRange(
                    name="rank",
                    greater_than_or_equal="010",
                    less_than_or_equal="030",
                ),
                limit=1,
                continuation_token=token,
            )
        )


def _upsert(
    tenant_id: str,
    idempotency_key: str,
    document_type: str,
    id: str,
    value: str,
    indexes: Mapping[str, str] | None = None,
) -> CanonicalTransactionRequest:
    return CanonicalTransactionRequest(
        tenant_id=tenant_id,
        idempotency_key=idempotency_key,
        mutations=(
            CanonicalMutation(
                document=CanonicalDocument(
                    tenant_id=tenant_id,
                    document_type=document_type,
                    id=id,
                    schema_version="v1",
                    data={"value": value},
                    indexes=dict(indexes or {}),
                )
            ),
        ),
    )


def _single(value: tuple[Any, ...], name: str) -> Any:
    if len(value) != 1:
        raise ConformanceError(f"{name} did not contain one item.")
    return value[0]


def _rejects(action: Callable[[], object]) -> bool:
    try:
        action()
    except CanonicalStoreError:
        return True
    return False


def _text(value: object, name: str) -> str:
    if not isinstance(value, str) or not value:
        raise ConformanceError(f"{name} must be a non-empty string.")
    return value


def run_bundled_canonical_scenario(
    fixture_root: str | Path | None = None,
) -> tuple[CanonicalConformanceResult, ...]:
    manifest = load_conformance_manifest(fixture_root)
    descriptor = next(
        (
            item
            for item in manifest.scenarios
            if item.scenario_id == _SCENARIO_ID
        ),
        None,
    )
    if descriptor is None:
        raise ConformanceError(
            f"Conformance scenario {_SCENARIO_ID!r} is unavailable."
        )
    if descriptor.kind != "stateful":
        raise ConformanceError(
            f"Conformance scenario {_SCENARIO_ID!r} must be stateful."
        )
    scenario = manifest.scenario(descriptor)
    raw_steps = scenario.get("steps")
    if not isinstance(raw_steps, list):
        raise ConformanceError(
            "Canonical scenario steps must be an array."
        )
    with tempfile.TemporaryDirectory(
        prefix="vyral-canonical-conformance-"
    ) as temporary:
        runner = _Runner(
            SQLiteCanonicalStore(
                Path(temporary) / "canonical.sqlite",
                clock=lambda: _NOW,
                lease_token_factory=lambda: "portable-lease-token",
            )
        )
        results: list[CanonicalConformanceResult] = []
        for raw_step in raw_steps:
            step = _object(raw_step, "scenario.steps[]")
            step_id = _text(step.get("id"), "step.id")
            operation = _text(
                step.get("operation"), "step.operation"
            )
            arguments = _object(
                step.get("arguments"), "step.arguments"
            )
            expectation = _object(
                step.get("expect"), "step.expect"
            )
            if "value" not in expectation:
                raise ConformanceError(
                    "Canonical error expectations are not implemented."
                )
            actual = runner.execute(operation, arguments)
            expected = cast(JSONValue, expectation["value"])
            if actual != expected:
                raise ConformanceError(
                    f"Canonical step {step_id!r} produced {actual!r}, "
                    f"expected {expected!r}."
                )
            results.append(
                CanonicalConformanceResult(
                    scenario_id=descriptor.scenario_id,
                    step_id=step_id,
                    operation=operation,
                    value=actual,
                )
            )
    return tuple(results)


__all__ = [
    "CanonicalConformanceResult",
    "run_bundled_canonical_scenario",
]
