from __future__ import annotations

import asyncio
from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
from datetime import datetime, timezone
import json
from pathlib import Path
import sys
import tempfile
from typing import Any
import unittest

from jsonschema import Draft202012Validator

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime.canonical import (  # noqa: E402
    AsyncSQLiteCanonicalStore,
    CanonicalArchiveRestoreRequest,
    CanonicalConflictError,
    CanonicalDocument,
    CanonicalDocumentIndexRange,
    CanonicalDocumentQuery,
    CanonicalFenceMutation,
    CanonicalIntegrityError,
    CanonicalMigration,
    CanonicalMutation,
    CanonicalOutboxLeaseRequest,
    CanonicalOutboxNackRequest,
    CanonicalOutboxQuery,
    CanonicalOutboxReplayRequest,
    CanonicalOutboxWrite,
    CanonicalRestoreRequest,
    CanonicalTransactionRequest,
    CanonicalTenantArchive,
    CanonicalValidationError,
    CanonicalWritePrecondition,
    MAX_ARCHIVE_CHUNKS,
    MAX_QUERY_LIMIT,
    SQLiteCanonicalStore,
    canonical_request_hash,
    create_canonical_archive,
)
from vyral_runtime.canonical.conformance import (  # noqa: E402
    run_bundled_canonical_scenario,
)
from vyral_runtime.contracts import load_contract_bundle  # noqa: E402


ROOT = Path(__file__).resolve().parents[3]
FIXTURE_ROOT = ROOT / "conformance/runtime/v1"
NOW = datetime(2026, 7, 30, 18, 0, tzinfo=timezone.utc)


def upsert(
    tenant_id: str,
    key: str,
    document_type: str,
    id: str,
    value: str,
    *,
    indexes: dict[str, str] | None = None,
    outbox: bool = False,
    max_delivery_attempts: int | None = None,
    precondition: CanonicalWritePrecondition | None = None,
) -> CanonicalTransactionRequest:
    return CanonicalTransactionRequest(
        tenant_id=tenant_id,
        idempotency_key=key,
        mutations=(
            CanonicalMutation(
                document=CanonicalDocument(
                    tenant_id=tenant_id,
                    document_type=document_type,
                    id=id,
                    schema_version="v1",
                    data={"value": value},
                    indexes=indexes or {},
                ),
                precondition=precondition,
            ),
        ),
        outbox=(
            (
                CanonicalOutboxWrite(
                    topic="canonical.changed",
                    key=id,
                    payload={"id": id},
                    max_delivery_attempts=max_delivery_attempts,
                ),
            )
            if outbox
            else ()
        ),
    )


class CanonicalStoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(
            prefix="vyral-python-canonical-"
        )
        self.database_path = Path(self.temporary.name) / "canonical.sqlite"
        self.store = SQLiteCanonicalStore(
            self.database_path,
            clock=lambda: NOW,
            lease_token_factory=lambda: "lease-secret",
        )

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_portable_strong_profile_fixture(self) -> None:
        results = run_bundled_canonical_scenario(FIXTURE_ROOT)
        self.assertEqual(
            [
                "atomic-commit-and-idempotent-replay",
                "atomic-fence-and-revision-conflicts",
                "outbox-release-dead-letter-replay-and-ack",
                "hash-verified-snapshot-and-chunked-archive-restore",
                "migrations-range-continuation-and-tenant-isolation",
                "portable-snapshot-and-archive-codec",
            ],
            [item.step_id for item in results],
        )

    def test_concurrent_idempotency_is_one_commit_and_one_replay(
        self,
    ) -> None:
        request = upsert(
            "tenant-a",
            "parallel",
            "entity",
            "e-1",
            "value",
            outbox=True,
        )
        with ThreadPoolExecutor(max_workers=2) as executor:
            futures = [
                executor.submit(self.store.commit, request)
                for _ in range(2)
            ]
            results = [future.result() for future in futures]

        self.assertEqual([False, True], sorted(item.replayed for item in results))
        self.assertEqual(
            results[0].transaction_id, results[1].transaction_id
        )
        self.assertEqual(
            1,
            len(
                self.store.get_revisions(
                    "tenant-a", "entity", "e-1"
                )
            ),
        )
        self.assertEqual(
            1, len(self.store.export_tenant("tenant-a").outbox)
        )

    def test_equivalent_json_and_map_order_replays(self) -> None:
        first = CanonicalTransactionRequest(
            tenant_id="tenant-a",
            idempotency_key="canonical-json ",
            mutations=(
                CanonicalMutation(
                    document=CanonicalDocument(
                        tenant_id="tenant-a",
                        document_type="entity",
                        id="e-1",
                        schema_version="v1",
                        data={
                            "name": "Ada",
                            "profile": {
                                "last": "Lovelace",
                                "first": "Ada",
                            },
                        },
                        indexes={
                            "name": "Ada",
                            "email": "ada@example.test",
                        },
                    )
                ),
            ),
        )
        second = CanonicalTransactionRequest(
            tenant_id="tenant-a",
            idempotency_key="canonical-json",
            mutations=(
                CanonicalMutation(
                    document=CanonicalDocument(
                        tenant_id="tenant-a",
                        document_type="entity",
                        id="e-1",
                        schema_version="v1",
                        data={
                            "profile": {
                                "first": "Ada",
                                "last": "Lovelace",
                            },
                            "name": "Ada",
                        },
                        indexes={
                            "email": "ada@example.test",
                            "name": "Ada",
                        },
                    )
                ),
            ),
        )
        self.assertEqual(
            canonical_request_hash(first),
            canonical_request_hash(second),
        )
        self.store.commit(first)
        self.assertTrue(self.store.commit(second).replayed)

    def test_fence_conflict_rolls_back_document(self) -> None:
        owner = upsert(
            "tenant-a", "owner", "entity", "e-1", "owner"
        )
        owner = replace(
            owner,
            fences=(
                CanonicalFenceMutation(
                    name="email",
                    value="shared@example.test",
                    owner_document_type="entity",
                    owner_document_id="e-1",
                ),
            ),
        )
        self.store.commit(owner)
        conflict = upsert(
            "tenant-a", "conflict", "entity", "e-2", "conflict"
        )
        conflict = replace(
            conflict,
            fences=(
                CanonicalFenceMutation(
                    name="email",
                    value="shared@example.test",
                    owner_document_type="entity",
                    owner_document_id="e-2",
                ),
            ),
        )
        with self.assertRaises(CanonicalConflictError):
            self.store.commit(conflict)
        self.assertIsNone(
            self.store.get_document(
                "tenant-a", "entity", "e-2", include_deleted=True
            )
        )

    def test_tombstones_preconditions_and_revision_order(self) -> None:
        self.store.commit(
            upsert("tenant-a", "create", "entity", "e-1", "one")
        )
        updated = self.store.commit(
            upsert(
                "tenant-a",
                "update",
                "entity",
                "e-1",
                "two",
                precondition=CanonicalWritePrecondition(
                    expected_revision=1, must_exist=True
                ),
            )
        )
        self.assertEqual(2, updated.documents[0].revision)
        with self.assertRaises(CanonicalConflictError):
            self.store.commit(
                upsert(
                    "tenant-a",
                    "stale",
                    "entity",
                    "e-1",
                    "stale",
                    precondition=CanonicalWritePrecondition(
                        expected_revision=1
                    ),
                )
            )
        deleted = self.store.commit(
            CanonicalTransactionRequest(
                tenant_id="tenant-a",
                idempotency_key="delete",
                mutations=(
                    CanonicalMutation(
                        operation="delete",
                        document_type="entity",
                        id="e-1",
                        precondition=CanonicalWritePrecondition(
                            expected_revision=2,
                            must_exist=True,
                        ),
                    ),
                ),
            )
        )
        self.assertTrue(deleted.documents[0].deleted)
        self.assertIsNone(
            self.store.get_document("tenant-a", "entity", "e-1")
        )
        self.assertEqual(
            [3, 2, 1],
            [
                item.revision
                for item in self.store.get_revisions(
                    "tenant-a", "entity", "e-1"
                )
            ],
        )

    def test_dead_letter_replay_and_token_hygiene(self) -> None:
        self.store.commit(
            upsert(
                "tenant-a",
                "event",
                "entity",
                "e-1",
                "one",
                outbox=True,
                max_delivery_attempts=1,
            )
        )
        lease = self.store.lease_outbox(
            CanonicalOutboxLeaseRequest(
                tenant_id="tenant-a", consumer_id="projector"
            )
        )[0]
        self.assertNotIn("lease-secret", repr(lease))
        self.assertNotIn(
            "lease-secret", json.dumps(lease.safe_summary())
        )
        self.store.nack_outbox(
            CanonicalOutboxNackRequest(
                tenant_id="tenant-a",
                event_id=lease.event.id,
                lease_token=lease.lease_token,
                error="permanent failure",
            )
        )
        dead = self.store.query_outbox(
            CanonicalOutboxQuery(
                tenant_id="tenant-a", state="dead-letter"
            )
        )
        self.assertEqual("permanent failure", dead.items[0].last_error)
        self.assertEqual(
            (),
            self.store.lease_outbox(
                CanonicalOutboxLeaseRequest(
                    tenant_id="tenant-a", consumer_id="projector"
                )
            ),
        )
        self.store.replay_outbox(
            CanonicalOutboxReplayRequest(
                tenant_id="tenant-a",
                event_id=lease.event.id,
                reset_delivery_count=True,
            )
        )
        replay = self.store.lease_outbox(
            CanonicalOutboxLeaseRequest(
                tenant_id="tenant-a", consumer_id="projector"
            )
        )[0]
        self.assertEqual(1, replay.event.delivery_count)

    def test_snapshot_restore_clears_ephemeral_lease_without_mutating_input(
        self,
    ) -> None:
        request = upsert(
            "tenant-a",
            "snapshot",
            "entity",
            "e-1",
            "one",
            outbox=True,
        )
        self.store.commit(request)
        lease = self.store.lease_outbox(
            CanonicalOutboxLeaseRequest(
                tenant_id="tenant-a", consumer_id="projector"
            )
        )[0]
        snapshot = self.store.export_tenant("tenant-a")
        self.assertEqual("projector", snapshot.outbox[0].lease_owner)
        original_hash = snapshot.content_hash

        self.store.restore_tenant(
            CanonicalRestoreRequest(
                snapshot=snapshot,
                expected_content_hash=original_hash,
            )
        )
        self.store.restore_tenant(
            CanonicalRestoreRequest(
                snapshot=snapshot,
                expected_content_hash=original_hash,
            )
        )
        self.assertEqual("projector", snapshot.outbox[0].lease_owner)
        self.assertEqual(original_hash, snapshot.content_hash)
        resumed = self.store.lease_outbox(
            CanonicalOutboxLeaseRequest(
                tenant_id="tenant-a", consumer_id="projector"
            )
        )[0]
        self.assertEqual(lease.event.delivery_count + 1, resumed.event.delivery_count)

    def test_archive_rejects_corruption_and_store_survives_restart(
        self,
    ) -> None:
        request = upsert(
            "tenant-a", "archive", "entity", "e-1", "one"
        )
        self.store.commit(request)
        archive = self.store.export_tenant_archive(
            "tenant-a", chunk_bytes=128
        )
        first = archive.chunks[0]
        corrupted = bytearray(first.content)
        corrupted[0] ^= 1
        chunks = list(archive.chunks)
        chunks[0] = type(first)(
            index=first.index,
            content=bytes(corrupted),
            length=first.length,
            content_hash=first.content_hash,
        )
        corrupt_archive = type(archive)(
            profile=archive.profile,
            tenant_id=archive.tenant_id,
            exported_at_utc=archive.exported_at_utc,
            snapshot_content_hash=archive.snapshot_content_hash,
            content_hash=archive.content_hash,
            chunks=tuple(chunks),
        )
        with self.assertRaises(CanonicalIntegrityError):
            self.store.restore_tenant_archive(
                CanonicalArchiveRestoreRequest(
                    archive=corrupt_archive,
                    expected_content_hash=corrupt_archive.content_hash,
                )
            )

        reopened = SQLiteCanonicalStore(
            self.database_path, clock=lambda: NOW
        )
        self.assertEqual(
            "one",
            reopened.get_document(
                "tenant-a", "entity", "e-1"
            ).data["value"],  # type: ignore[index,union-attr]
        )
        self.assertTrue(reopened.commit(request).replayed)

    def test_archive_chunk_count_is_bounded_before_decode_or_hashing(
        self,
    ) -> None:
        self.store.commit(
            upsert("tenant-a", "archive", "entity", "e-1", "one")
        )
        snapshot = self.store.export_tenant("tenant-a")
        with self.assertRaisesRegex(
            CanonicalValidationError, "chunk limit"
        ):
            create_canonical_archive(snapshot, chunk_bytes=1)

        archive = self.store.export_tenant_archive("tenant-a")
        first = archive.chunks[0]
        too_many = replace(
            archive,
            chunks=tuple(
                replace(first, index=index)
                for index in range(MAX_ARCHIVE_CHUNKS + 1)
            ),
        )
        with self.assertRaisesRegex(
            CanonicalIntegrityError, "chunk limit"
        ):
            self.store.restore_tenant_archive(
                CanonicalArchiveRestoreRequest(archive=too_many)
            )

        wire = archive.to_dict()
        wire["chunks"] = [{}] * (MAX_ARCHIVE_CHUNKS + 1)
        with self.assertRaisesRegex(ValueError, "must not contain more"):
            CanonicalTenantArchive.from_value(wire)

    def test_migration_namespace_query_order_and_tenant_isolation(
        self,
    ) -> None:
        migration = CanonicalMigration(
            namespace="consumer-a",
            id="v1",
            checksum="sha256:one",
        )
        self.store.apply_migrations((migration,))
        self.store.apply_migrations((migration,))
        with self.assertRaises(CanonicalConflictError):
            self.store.apply_migrations(
                (
                    CanonicalMigration(
                        namespace="consumer-a",
                        id="v1",
                        checksum="sha256:changed",
                    ),
                )
            )
        self.store.apply_migrations(
            (
                CanonicalMigration(
                    namespace="consumer-b",
                    id="v1",
                    checksum="sha256:two",
                ),
            )
        )
        self.assertEqual(2, len(self.store.list_migrations()))

        for id, rank in (
            ("e-1", "020"),
            ("e-2", "010"),
            ("e-3", "030"),
        ):
            self.store.commit(
                upsert(
                    "tenant-a",
                    "rank-" + id,
                    "review",
                    id,
                    id,
                    indexes={"rank": rank},
                )
            )
        token = None
        ids: list[str] = []
        continuations: list[bool] = []
        for _ in range(3):
            page = self.store.query_documents(
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
            ids.append(page.items[0].id)
            token = page.continuation_token
            continuations.append(token is not None)
        self.assertEqual(["e-3", "e-1", "e-2"], ids)
        self.assertEqual([True, True, False], continuations)

        self.store.commit(
            upsert(
                "tenant-a",
                "shared",
                "entity",
                "same",
                "A",
            )
        )
        self.store.commit(
            upsert(
                "tenant-b",
                "shared",
                "entity",
                "same",
                "B",
            )
        )
        self.assertEqual(
            "A",
            self.store.get_document(
                "tenant-a", "entity", "same"
            ).data["value"],  # type: ignore[index,union-attr]
        )
        self.assertEqual(
            "B",
            self.store.get_document(
                "tenant-b", "entity", "same"
            ).data["value"],  # type: ignore[index,union-attr]
        )

    def test_data_plane_preflight_is_bounded_and_cleans_up(self) -> None:
        result = self.store.run_data_plane_preflight()
        self.assertTrue(result.ready)
        self.assertEqual("passed", result.status)
        self.assertTrue(result.backup_restore_verified)
        self.assertTrue(result.tenant_isolation_verified)
        self.assertTrue(result.cleanup_verified)
        self.assertGreater(result.archive_chunk_count, 1)
        self.assertEqual(3, len(result.checks))
        self.assertTrue(all(item.status == "passed" for item in result.checks))
        self._assert_schema(
            "CanonicalDataPlanePreflightResult", result.to_dict()
        )

    def test_async_facade_runs_complete_operations(self) -> None:
        async_store = AsyncSQLiteCanonicalStore(self.store)

        async def scenario() -> None:
            result = await async_store.commit(
                upsert(
                    "tenant-a",
                    "async",
                    "entity",
                    "e-1",
                    "async",
                )
            )
            self.assertFalse(result.replayed)
            document = await async_store.get_document(
                "tenant-a", "entity", "e-1"
            )
            self.assertIsNotNone(document)
            snapshot = await async_store.export_tenant("tenant-a")
            await async_store.restore_tenant(
                CanonicalRestoreRequest(
                    snapshot=snapshot,
                    expected_content_hash=snapshot.content_hash,
                )
            )

        try:
            asyncio.run(scenario())
        finally:
            async_store.close()

    def test_validation_rejects_unsafe_and_unbounded_shapes(self) -> None:
        with self.assertRaises(CanonicalValidationError):
            self.store.commit(
                upsert(
                    "tenant/escape",
                    "bad",
                    "entity",
                    "e-1",
                    "bad",
                )
            )
        with self.assertRaises(CanonicalValidationError):
            self.store.commit(
                CanonicalTransactionRequest(
                    tenant_id="tenant-a",
                    idempotency_key="empty",
                )
            )
        with self.assertRaises(CanonicalValidationError):
            self.store.query_documents(
                CanonicalDocumentQuery(
                    tenant_id="tenant-a", limit=MAX_QUERY_LIMIT + 1
                )
            )

    def test_public_results_match_canonical_json_schema(self) -> None:
        result = self.store.commit(
            upsert(
                "tenant-a",
                "schema",
                "entity",
                "e-1",
                "schema",
                outbox=True,
            )
        )
        snapshot = self.store.export_tenant("tenant-a")
        self._assert_schema(
            "CanonicalTransactionResult", result.to_dict()
        )
        self._assert_schema(
            "CanonicalDocument", result.documents[0].to_dict()
        )
        self._assert_schema(
            "CanonicalOutboxEvent", result.outbox[0].to_dict()
        )
        self._assert_schema(
            "CanonicalTenantSnapshot", snapshot.to_dict()
        )
        self._assert_schema(
            "CanonicalDocumentRevision",
            snapshot.revisions[0].to_dict(),
        )

    def _assert_schema(self, name: str, value: Any) -> None:
        schema = load_contract_bundle().schema
        validator = Draft202012Validator(
            {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                "$ref": f"#/$defs/{name}",
                "$defs": schema["$defs"],
            }
        )
        errors = sorted(
            validator.iter_errors(value),
            key=lambda error: tuple(str(item) for item in error.path),
        )
        self.assertEqual(
            [],
            [error.message for error in errors],
            f"{name} schema errors",
        )


if __name__ == "__main__":
    unittest.main()
