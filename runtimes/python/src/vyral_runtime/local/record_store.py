from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import re
import sqlite3
import struct
from typing import Any, Callable, Iterator, Mapping, Sequence

from .._datetime import parse_iso_datetime
from .models import (
    JSONObject,
    RecordBatchItemResult,
    RecordBatchResult,
    RecordCollectionPolicy,
    RecordWritePrecondition,
    VectorFieldPolicy,
    VyralRecord,
    VyralVector,
)
from .query_engine import (
    QueryValidationError,
    build_lexical_candidate_plan,
    build_query_plan,
    build_vector_candidate_plan,
    decode_continuation_token,
    encode_continuation_token,
    validate_page_limit,
)
from .lexical import build_fts_expression, score_many
from .query_models import (
    LexicalSearchOptions,
    QueryEnvelope,
    RecordQueryResult,
    RecordSearchResult,
    RetrievalDiagnostics,
    VectorSearchOptions,
    VyralRecordMatch,
)
from .snapshots import (
    MAX_COLLECTION_SNAPSHOT_RECORDS,
    CollectionExportEnvelope,
    CollectionExportRequest,
    CollectionImportRequest,
    CollectionImportResult,
    build_hash_comparison,
    compute_collection_snapshot_hash,
    renamed_policy,
)


_COLLECTION_PATTERN = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,253}[A-Za-z0-9])?$")
_MAX_RECORD_ID_BYTES = 1023
_MAX_PARTITION_KEY_BYTES = 2048
_SUPPORTED_DATATYPES = frozenset({"float32"})
_SUPPORTED_DISTANCES = frozenset({"cosine", "dotproduct", "euclidean"})
_SUPPORTED_INDEX_TYPES = frozenset({"flat", "quantizedflat", "diskann"})
_VALUE_BOUNDARY_TOKEN = "x" * 257


class RecordStoreError(RuntimeError):
    pass


class RecordValidationError(RecordStoreError, ValueError):
    pass


class CollectionNotFoundError(RecordStoreError, LookupError):
    pass


class CollectionPolicyConflictError(RecordStoreError):
    pass


class RecordPreconditionFailedError(RecordStoreError):
    pass


@dataclass(frozen=True)
class SQLiteStorageDiagnostics:
    healthy: bool
    quick_check: str
    foreign_key_violation_count: int
    journal_mode: str
    fts5_available: bool
    database_exists: bool
    database_bytes: int

    def to_dict(self) -> JSONObject:
        return {
            "healthy": self.healthy,
            "quickCheck": self.quick_check,
            "foreignKeyViolationCount": self.foreign_key_violation_count,
            "journalMode": self.journal_mode,
            "fts5Available": self.fts5_available,
            "databaseExists": self.database_exists,
            "databaseBytes": self.database_bytes,
        }


class SQLiteRecordStore:
    """Synchronous SQLite collections, records, query, search, and snapshots."""

    schema_version = 1

    def __init__(
        self,
        database_path: str | Path,
        *,
        busy_timeout_ms: int = 5000,
        clock: Callable[[], datetime] | None = None,
    ) -> None:
        if isinstance(busy_timeout_ms, bool) or busy_timeout_ms < 0:
            raise ValueError("busy_timeout_ms must be a non-negative integer")
        self.database_path = Path(database_path).expanduser().resolve()
        self.busy_timeout_ms = busy_timeout_ms
        self._clock = clock or (lambda: datetime.now(timezone.utc))
        self.database_path.parent.mkdir(parents=True, exist_ok=True)
        self._initialize()

    @contextmanager
    def _connection(self, *, write: bool = False) -> Iterator[sqlite3.Connection]:
        connection = sqlite3.connect(
            self.database_path,
            timeout=self.busy_timeout_ms / 1000,
            isolation_level=None,
        )
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA foreign_keys=ON")
        connection.execute(f"PRAGMA busy_timeout={self.busy_timeout_ms}")
        try:
            if write:
                connection.execute("BEGIN IMMEDIATE")
            yield connection
            if write:
                connection.commit()
        except Exception:
            if write and connection.in_transaction:
                connection.rollback()
            raise
        finally:
            connection.close()

    def _initialize(self) -> None:
        with self._connection() as connection:
            connection.execute("PRAGMA journal_mode=WAL")
            try:
                connection.execute(
                    """
                    CREATE VIRTUAL TABLE IF NOT EXISTS vyral_py_record_fts
                    USING fts5(
                        collection UNINDEXED,
                        partition_key UNINDEXED,
                        record_id UNINDEXED,
                        text,
                        tokenize='unicode61'
                    )
                    """
                )
            except sqlite3.OperationalError as exc:
                raise RecordStoreError("The local Python runtime requires SQLite FTS5.") from exc

            connection.executescript(
                """
                CREATE TABLE IF NOT EXISTS vyral_py_metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS vyral_py_collections (
                    name TEXT PRIMARY KEY,
                    policy_json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS vyral_py_records (
                    collection TEXT NOT NULL,
                    partition_key TEXT NOT NULL,
                    id TEXT NOT NULL,
                    record_json TEXT NOT NULL,
                    etag TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (collection, partition_key, id),
                    FOREIGN KEY (collection) REFERENCES vyral_py_collections(name) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS vyral_py_vectors (
                    collection TEXT NOT NULL,
                    partition_key TEXT NOT NULL,
                    record_id TEXT NOT NULL,
                    vector_name TEXT NOT NULL,
                    vector_data BLOB NOT NULL,
                    dimensions INTEGER NOT NULL,
                    PRIMARY KEY (collection, partition_key, record_id, vector_name),
                    FOREIGN KEY (collection, partition_key, record_id)
                        REFERENCES vyral_py_records(collection, partition_key, id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS vyral_py_metadata_index (
                    collection TEXT NOT NULL,
                    partition_key TEXT NOT NULL,
                    record_id TEXT NOT NULL,
                    path TEXT NOT NULL,
                    value_text TEXT,
                    value_number REAL,
                    value_bool INTEGER,
                    value_json TEXT NOT NULL,
                    PRIMARY KEY (collection, partition_key, record_id, path),
                    FOREIGN KEY (collection, partition_key, record_id)
                        REFERENCES vyral_py_records(collection, partition_key, id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_vyral_py_metadata_text
                    ON vyral_py_metadata_index(collection, path, value_text);
                CREATE INDEX IF NOT EXISTS idx_vyral_py_metadata_number
                    ON vyral_py_metadata_index(collection, path, value_number);
                """
            )
            connection.execute(
                """
                INSERT INTO vyral_py_metadata(key, value)
                VALUES ('schema_version', ?)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """,
                (str(self.schema_version),),
            )

    def diagnostics(self) -> SQLiteStorageDiagnostics:
        with self._connection() as connection:
            quick_check = str(connection.execute("PRAGMA quick_check").fetchone()[0])
            foreign_keys = tuple(connection.execute("PRAGMA foreign_key_check"))
            journal_mode = str(connection.execute("PRAGMA journal_mode").fetchone()[0])
            fts5_available = (
                connection.execute(
                    "SELECT 1 FROM sqlite_master WHERE type='table' AND name='vyral_py_record_fts'"
                ).fetchone()
                is not None
            )
        exists = self.database_path.is_file()
        size = self.database_path.stat().st_size if exists else 0
        return SQLiteStorageDiagnostics(
            healthy=(
                quick_check.lower() == "ok"
                and not foreign_keys
                and fts5_available
                and journal_mode.lower() == "wal"
            ),
            quick_check=quick_check,
            foreign_key_violation_count=len(foreign_keys),
            journal_mode=journal_mode,
            fts5_available=fts5_available,
            database_exists=exists,
            database_bytes=size,
        )

    def create_collection(
        self,
        policy: RecordCollectionPolicy | Mapping[str, Any],
    ) -> None:
        normalized = RecordCollectionPolicy.from_value(policy)
        _validate_policy(normalized)
        policy_json = _json(normalized.to_dict())
        with self._connection(write=True) as connection:
            row = connection.execute(
                "SELECT policy_json FROM vyral_py_collections WHERE name=?",
                (normalized.name,),
            ).fetchone()
            if row is not None:
                existing = RecordCollectionPolicy.from_value(json.loads(row["policy_json"]))
                if not _policies_equivalent(existing, normalized):
                    raise CollectionPolicyConflictError(
                        f"Collection {normalized.name!r} already exists with a different policy."
                    )
                return
            connection.execute(
                "INSERT INTO vyral_py_collections(name, policy_json) VALUES (?, ?)",
                (normalized.name, policy_json),
            )

    def list_collections(self) -> tuple[str, ...]:
        with self._connection() as connection:
            return tuple(
                row["name"]
                for row in connection.execute(
                    "SELECT name FROM vyral_py_collections ORDER BY name"
                )
            )

    def get_collection_policy(self, collection: str) -> RecordCollectionPolicy | None:
        _validate_collection_name(collection)
        with self._connection() as connection:
            row = connection.execute(
                "SELECT policy_json FROM vyral_py_collections WHERE name=?",
                (collection,),
            ).fetchone()
        return (
            RecordCollectionPolicy.from_value(json.loads(row["policy_json"]))
            if row is not None
            else None
        )

    def delete_collection(self, collection: str) -> None:
        _validate_collection_name(collection)
        with self._connection(write=True) as connection:
            connection.execute(
                "DELETE FROM vyral_py_record_fts WHERE collection=?",
                (collection,),
            )
            connection.execute("DELETE FROM vyral_py_collections WHERE name=?", (collection,))

    def upsert_record(
        self,
        collection: str,
        record: VyralRecord | Mapping[str, Any],
        precondition: RecordWritePrecondition | Mapping[str, Any] | None = None,
    ) -> VyralRecord:
        _validate_collection_name(collection)
        normalized = VyralRecord.from_value(record)
        _validate_record_identity(normalized)
        condition = RecordWritePrecondition.from_value(precondition)

        with self._connection(write=True) as connection:
            policy = self._get_policy(connection, collection)
            vectors = _validate_vectors(collection, policy, normalized.vectors)
            row = connection.execute(
                """
                SELECT record_json, etag, revision, created_at_utc
                FROM vyral_py_records
                WHERE collection=? AND partition_key=? AND id=?
                """,
                (collection, normalized.partition_key, normalized.id),
            ).fetchone()
            exists = row is not None
            existing_revision = int(row["revision"]) if row is not None else 0
            existing_etag = str(row["etag"]) if row is not None else None
            existing_created_at = (
                _stored_datetime(str(row["created_at_utc"])) if row is not None else None
            )
            _ensure_precondition(
                condition,
                exists=exists,
                current_etag=existing_etag,
                current_revision=existing_revision if exists else None,
            )

            now = _normalize_clock_value(self._clock())
            created_at = existing_created_at or normalized.created_at or now
            revision = existing_revision + 1
            stored = normalized.stored(
                revision=revision,
                created_at=created_at,
                updated_at=now,
                vectors=vectors,
            )
            connection.execute(
                """
                INSERT INTO vyral_py_records(
                    collection, partition_key, id, record_json, etag, revision,
                    created_at_utc, updated_at_utc
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(collection, partition_key, id) DO UPDATE SET
                    record_json=excluded.record_json,
                    etag=excluded.etag,
                    revision=excluded.revision,
                    updated_at_utc=excluded.updated_at_utc
                """,
                (
                    collection,
                    stored.partition_key,
                    stored.id,
                    _json(stored.to_dict()),
                    stored.etag,
                    stored.revision,
                    _database_datetime(stored.created_at),
                    _database_datetime(stored.updated_at),
                ),
            )
            self._replace_vectors(connection, collection, stored)
            self._replace_metadata_index(connection, collection, stored, policy)
            self._replace_lexical_index(connection, collection, stored)
            return stored

    def upsert_records(
        self,
        collection: str,
        records: Sequence[VyralRecord | Mapping[str, Any]],
        *,
        preconditions: Sequence[RecordWritePrecondition | Mapping[str, Any] | None] = (),
        continue_on_error: bool = False,
    ) -> RecordBatchResult:
        _validate_collection_name(collection)
        if preconditions and len(preconditions) != len(records):
            raise RecordValidationError(
                "Batch upsert preconditions must be omitted or match the records count."
            )

        items: list[RecordBatchItemResult] = []
        succeeded = 0
        failed = 0
        stopped = False
        for index, raw_record in enumerate(records):
            record_id, partition_key = _record_identity_for_error(raw_record)
            try:
                stored = self.upsert_record(
                    collection,
                    raw_record,
                    preconditions[index] if preconditions else None,
                )
                items.append(
                    RecordBatchItemResult(
                        index=index,
                        id=stored.id,
                        partition_key=stored.partition_key,
                        status="succeeded",
                        etag=stored.etag,
                        revision=stored.revision,
                    )
                )
                succeeded += 1
            except (RecordStoreError, TypeError, ValueError) as exc:
                items.append(
                    RecordBatchItemResult(
                        index=index,
                        id=record_id,
                        partition_key=partition_key,
                        status="failed",
                        error=str(exc),
                    )
                )
                failed += 1
                if not continue_on_error:
                    stopped = index + 1 < len(records)
                    break
        return RecordBatchResult(
            collection=collection,
            requested=len(records),
            attempted=succeeded + failed,
            succeeded=succeeded,
            failed=failed,
            stopped_on_error=stopped,
            items=tuple(items),
        )

    def get_record(self, collection: str, partition_key: str, record_id: str) -> VyralRecord | None:
        _validate_collection_name(collection)
        _validate_partition_key(partition_key)
        _validate_record_id(record_id)
        with self._connection() as connection:
            row = connection.execute(
                """
                SELECT record_json FROM vyral_py_records
                WHERE collection=? AND partition_key=? AND id=?
                """,
                (collection, partition_key, record_id),
            ).fetchone()
        return VyralRecord.from_value(json.loads(row["record_json"])) if row is not None else None

    def delete_record(self, collection: str, partition_key: str, record_id: str) -> None:
        _validate_collection_name(collection)
        _validate_partition_key(partition_key)
        _validate_record_id(record_id)
        with self._connection(write=True) as connection:
            connection.execute(
                """
                DELETE FROM vyral_py_record_fts
                WHERE collection=? AND partition_key=? AND record_id=?
                """,
                (collection, partition_key, record_id),
            )
            connection.execute(
                """
                DELETE FROM vyral_py_records
                WHERE collection=? AND partition_key=? AND id=?
                """,
                (collection, partition_key, record_id),
            )

    def query_records_page(
        self,
        collection: str,
        query: QueryEnvelope | Mapping[str, Any] | None = None,
    ) -> RecordQueryResult:
        _validate_collection_name(collection)
        normalized = QueryEnvelope.from_value(query)
        with self._connection() as connection:
            policy = self._get_policy(connection, collection)
            plan = build_query_plan(
                collection,
                normalized,
                policy.indexed_metadata,
            )
            records = tuple(
                VyralRecord.from_value(json.loads(row["record_json"]))
                for row in connection.execute(plan.sql, plan.parameters)
            )
        if normalized.limit is None:
            return RecordQueryResult(items=records)
        offset = decode_continuation_token(normalized.continuation_token)
        items = records[: normalized.limit]
        continuation = (
            encode_continuation_token(offset + normalized.limit)
            if len(records) > normalized.limit
            else None
        )
        return RecordQueryResult(items=items, continuation_token=continuation)

    def query_records(
        self,
        collection: str,
        query: QueryEnvelope | Mapping[str, Any] | None = None,
    ) -> tuple[VyralRecord, ...]:
        return self.query_records_page(collection, query).items

    def query_all_records(
        self,
        collection: str,
        query: QueryEnvelope | Mapping[str, Any] | None = None,
    ) -> tuple[VyralRecord, ...]:
        normalized = QueryEnvelope.from_value(query)
        results: list[VyralRecord] = []
        token = normalized.continuation_token
        while True:
            page = self.query_records_page(
                collection,
                QueryEnvelope(
                    partition_keys=normalized.partition_keys,
                    filter=normalized.filter,
                    vector=normalized.vector,
                    lexical=normalized.lexical,
                    order_by=normalized.order_by,
                    limit=normalized.limit,
                    continuation_token=token,
                ),
            )
            results.extend(page.items)
            token = page.continuation_token
            if token is None:
                return tuple(results)

    def search_records_page(
        self,
        collection: str,
        query: QueryEnvelope | Mapping[str, Any],
    ) -> RecordSearchResult:
        _validate_collection_name(collection)
        normalized = QueryEnvelope.from_value(query)
        if normalized.vector is None:
            if normalized.lexical is not None:
                return self._search_lexical_records_page(
                    collection,
                    normalized,
                    normalized.lexical,
                )
            page = self.query_records_page(collection, normalized)
            return RecordSearchResult(
                items=tuple(
                    VyralRecordMatch(record=record, score=1.0) for record in page.items
                ),
                continuation_token=page.continuation_token,
            )
        if normalized.lexical is not None:
            raise QueryValidationError(
                "Combined vector and lexical search is provided by the retrieval service; "
                "collection search accepts one search mode at a time."
            )
        return self._search_vector_records_page(collection, normalized, normalized.vector)

    def _search_lexical_records_page(
        self,
        collection: str,
        query: QueryEnvelope,
        lexical: LexicalSearchOptions,
    ) -> RecordSearchResult:
        if not lexical.query.strip():
            raise QueryValidationError("Lexical search query is required.")
        validate_page_limit(lexical.top, "Lexical top")
        validate_page_limit(lexical.scan_limit, "Lexical scan limit")
        validate_page_limit(query.limit, "Search page size")
        fts_expression, required_groups = build_fts_expression(
            lexical.query,
            lexical,
        )

        candidate_query = QueryEnvelope(
            partition_keys=query.partition_keys,
            filter=query.filter,
        )
        if fts_expression:
            with self._connection() as connection:
                policy = self._get_policy(connection, collection)
                plan = build_lexical_candidate_plan(
                    collection,
                    candidate_query,
                    policy.indexed_metadata,
                    fts_expression,
                    lexical.scan_limit,
                )
                records = tuple(
                    VyralRecord.from_value(json.loads(row["record_json"]))
                    for row in connection.execute(plan.sql, plan.parameters)
                )
            candidate_source = "sqlite_fts5"
        else:
            records = self.query_records(
                collection,
                QueryEnvelope(
                    partition_keys=query.partition_keys,
                    filter=query.filter,
                    limit=lexical.scan_limit,
                ),
            )
            candidate_source = "scan"

        scores = score_many(records, lexical.query, lexical)
        matches: list[VyralRecordMatch] = []
        for scored in scores:
            if scored.score <= 0:
                continue
            if lexical.min_score is not None and scored.score < lexical.min_score:
                continue
            matches.append(
                VyralRecordMatch(
                    record=scored.record,
                    score=scored.score,
                    diagnostics=scored.diagnostics(
                        collection=collection,
                        candidate_source=candidate_source,
                        candidate_count=len(records),
                        fts_expression=fts_expression or None,
                        required_phrase_groups=required_groups,
                    ),
                )
            )
        matches.sort(
            key=lambda match: (
                -match.score,
                match.record.partition_key,
                match.record.id,
            )
        )
        ranked = matches[: lexical.top]
        candidate_count = len(records)
        ranked = [
            VyralRecordMatch(
                record=match.record,
                score=match.score,
                diagnostics=_ranked_diagnostics(
                    match.diagnostics,
                    rank=index + 1,
                    candidate_count=candidate_count,
                    returned_count=len(ranked),
                    mode="lexical",
                    match=match,
                ),
            )
            for index, match in enumerate(ranked)
        ]
        offset = decode_continuation_token(query.continuation_token)
        page_size = query.limit or lexical.top
        page_items = tuple(ranked[offset : offset + page_size])
        continuation = (
            encode_continuation_token(offset + len(page_items))
            if offset + len(page_items) < len(ranked)
            else None
        )
        return RecordSearchResult(
            items=page_items,
            continuation_token=continuation,
        )

    def search_records(
        self,
        collection: str,
        query: QueryEnvelope | Mapping[str, Any],
    ) -> tuple[VyralRecordMatch, ...]:
        return self.search_records_page(collection, query).items

    def search_all_records(
        self,
        collection: str,
        query: QueryEnvelope | Mapping[str, Any],
    ) -> tuple[VyralRecordMatch, ...]:
        normalized = QueryEnvelope.from_value(query)
        results: list[VyralRecordMatch] = []
        token = normalized.continuation_token
        while True:
            page = self.search_records_page(
                collection,
                QueryEnvelope(
                    partition_keys=normalized.partition_keys,
                    filter=normalized.filter,
                    vector=normalized.vector,
                    lexical=normalized.lexical,
                    order_by=normalized.order_by,
                    limit=normalized.limit,
                    continuation_token=token,
                ),
            )
            results.extend(page.items)
            token = page.continuation_token
            if token is None:
                return tuple(results)

    def export_collection(
        self,
        collection: str,
        request: CollectionExportRequest | Mapping[str, Any] | None = None,
    ) -> CollectionExportEnvelope | None:
        _validate_collection_name(collection)
        normalized = CollectionExportRequest.from_value(request)
        max_records = (
            normalized.max_records
            if normalized.max_records is not None
            else MAX_COLLECTION_SNAPSHOT_RECORDS
        )
        if max_records <= 0:
            raise RecordValidationError(
                "Collection export maxRecords must be greater than zero."
            )
        if max_records > MAX_COLLECTION_SNAPSHOT_RECORDS:
            raise RecordValidationError(
                "Collection export maxRecords cannot exceed "
                f"{MAX_COLLECTION_SNAPSHOT_RECORDS}."
            )
        policy = self.get_collection_policy(collection)
        if policy is None:
            return None
        source_query = normalized.query or QueryEnvelope()
        page = self.query_records_page(
            collection,
            QueryEnvelope(
                partition_keys=source_query.partition_keys,
                filter=source_query.filter,
                vector=source_query.vector,
                lexical=source_query.lexical,
                order_by=source_query.order_by,
                limit=max_records,
                continuation_token=source_query.continuation_token,
            ),
        )
        truncated = page.continuation_token is not None
        if truncated and normalized.fail_on_limit_exceeded:
            raise RecordValidationError(
                f"Collection export exceeded maxRecords ({max_records}). Increase "
                "maxRecords or set failOnLimitExceeded to false to return a "
                "truncated snapshot."
            )
        envelope = CollectionExportEnvelope(
            collection=collection,
            policy=policy,
            records=page.items,
            query=normalized.query,
            max_records=max_records,
            record_count=len(page.items),
            truncated=truncated,
            continuation_token=page.continuation_token,
            exported_at=_normalize_clock_value(self._clock()),
        )
        return envelope.with_hash(compute_collection_snapshot_hash(envelope))

    def import_collection(
        self,
        target_collection: str,
        request: CollectionImportRequest | Mapping[str, Any],
    ) -> CollectionImportResult:
        _validate_collection_name(target_collection)
        normalized = CollectionImportRequest.from_value(request)
        snapshot = normalized.snapshot
        source_collection = (
            snapshot.collection.strip()
            if snapshot.collection.strip()
            else snapshot.policy.name
        )
        if not source_collection.strip():
            raise RecordValidationError(
                "Collection import snapshot requires a source collection name."
            )
        if (
            not normalized.allow_collection_rename
            and source_collection != target_collection
        ):
            raise RecordValidationError(
                f"Collection import snapshot is for {source_collection!r}, but target "
                f"collection is {target_collection!r}. Set allowCollectionRename to "
                "true to import under a different collection name."
            )
        if not snapshot.policy.name.strip():
            raise RecordValidationError(
                "Collection import snapshot requires a collection policy."
            )
        if (
            snapshot.record_count is not None
            and snapshot.record_count != len(snapshot.records)
        ):
            raise RecordValidationError(
                f"Collection import snapshot recordCount is {snapshot.record_count}, "
                f"but records contains {len(snapshot.records)} item(s)."
            )
        if snapshot.truncated and not normalized.allow_partial_snapshot:
            raise RecordValidationError(
                "Collection import snapshot is truncated. Set allowPartialSnapshot "
                "to true to import a partial snapshot intentionally."
            )

        actual_hash = compute_collection_snapshot_hash(snapshot)
        expected_hash = (
            normalized.expected_content_hash.strip()
            if normalized.expected_content_hash
            and normalized.expected_content_hash.strip()
            else snapshot.content_hash
        )
        comparison = build_hash_comparison(expected_hash, actual_hash)
        if comparison.compared and not comparison.matches:
            raise RecordValidationError(
                "Collection import content hash mismatch. Expected "
                f"{comparison.expected_hash}, actual {comparison.actual_hash}."
            )

        target_policy = renamed_policy(snapshot.policy, target_collection)
        policy_status = "created"
        existing = self.get_collection_policy(target_collection)
        if existing is not None:
            if normalized.replace_existing:
                self.delete_collection(target_collection)
                policy_status = "replaced"
            else:
                if not _policies_equivalent(existing, target_policy):
                    raise CollectionPolicyConflictError(
                        f"Collection {target_collection!r} already exists with a "
                        "different policy. Set replaceExisting to true to replace it."
                    )
                policy_status = "existing_equivalent"
        self.create_collection(target_policy)
        records = self.upsert_records(
            target_collection,
            snapshot.records,
            continue_on_error=normalized.continue_on_error,
        )
        return CollectionImportResult(
            collection=target_collection,
            source_collection=source_collection,
            policy_status=policy_status,
            record_count=len(snapshot.records),
            content_hash=actual_hash,
            content_hash_comparison=comparison,
            records=records,
        )

    def _search_vector_records_page(
        self,
        collection: str,
        query: QueryEnvelope,
        vector_search: VectorSearchOptions,
    ) -> RecordSearchResult:
        validate_page_limit(vector_search.top, "Vector top")
        validate_page_limit(query.limit, "Search page size")
        if not vector_search.field.strip():
            raise QueryValidationError("Vector search field is required.")
        query_vector = _float32_values(
            vector_search.value,
            "Search vector",
        )
        with self._connection() as connection:
            policy = self._get_policy(connection, collection)
            field_policy = next(
                (
                    item
                    for item in policy.vector_policies
                    if item.name == vector_search.field
                ),
                None,
            )
            if field_policy is None:
                raise RecordValidationError(
                    f"Vector field {vector_search.field!r} is not defined in policy "
                    f"for collection {collection!r}."
                )
            if len(query_vector) != field_policy.dimensions:
                raise RecordValidationError(
                    f"Search vector for field {vector_search.field!r} has dimensions "
                    f"{len(query_vector)}, but policy expects {field_policy.dimensions}."
                )
            plan = build_vector_candidate_plan(
                collection,
                query,
                policy.indexed_metadata,
                vector_search.field,
            )
            matches: list[VyralRecordMatch] = []
            for row in connection.execute(plan.sql, plan.parameters):
                record = VyralRecord.from_value(json.loads(row["record_json"]))
                stored_dimensions = int(row["dimensions"])
                if stored_dimensions != field_policy.dimensions:
                    raise RecordStoreError(
                        f"Stored vector for record {record.id!r} has dimensions "
                        f"{stored_dimensions}, but policy expects {field_policy.dimensions}."
                    )
                vector_data = bytes(row["vector_data"])
                if len(vector_data) != stored_dimensions * 4:
                    raise RecordStoreError(
                        f"Stored vector for record {record.id!r} has an invalid byte length."
                    )
                stored_vector = struct.unpack(
                    f"<{stored_dimensions}f",
                    vector_data,
                )
                score = _similarity_score(
                    field_policy.distance_function,
                    query_vector,
                    stored_vector,
                )
                if (
                    vector_search.min_score is not None
                    and score < vector_search.min_score
                ):
                    continue
                matches.append(
                    VyralRecordMatch(
                        record=record,
                        score=score,
                        diagnostics=_vector_diagnostics(
                            collection,
                            record,
                            vector_search.field,
                            field_policy.distance_function,
                            score,
                        ),
                    )
                )
        matches.sort(
            key=lambda match: (
                -match.score,
                match.record.partition_key,
                match.record.id,
            )
        )
        ranked = matches[: vector_search.top]
        candidate_count = len(matches)
        ranked = [
            VyralRecordMatch(
                record=match.record,
                score=match.score,
                diagnostics=_ranked_diagnostics(
                    match.diagnostics,
                    rank=index + 1,
                    candidate_count=candidate_count,
                    returned_count=len(ranked),
                    mode="vector",
                    match=match,
                ),
            )
            for index, match in enumerate(ranked)
        ]
        offset = decode_continuation_token(query.continuation_token)
        page_size = query.limit or vector_search.top
        page_items = tuple(ranked[offset : offset + page_size])
        continuation = (
            encode_continuation_token(offset + len(page_items))
            if offset + len(page_items) < len(ranked)
            else None
        )
        return RecordSearchResult(
            items=page_items,
            continuation_token=continuation,
        )

    @staticmethod
    def _get_policy(
        connection: sqlite3.Connection,
        collection: str,
    ) -> RecordCollectionPolicy:
        row = connection.execute(
            "SELECT policy_json FROM vyral_py_collections WHERE name=?",
            (collection,),
        ).fetchone()
        if row is None:
            raise CollectionNotFoundError(f"Collection {collection!r} does not exist.")
        return RecordCollectionPolicy.from_value(json.loads(row["policy_json"]))

    @staticmethod
    def _replace_vectors(
        connection: sqlite3.Connection,
        collection: str,
        record: VyralRecord,
    ) -> None:
        connection.execute(
            """
            DELETE FROM vyral_py_vectors
            WHERE collection=? AND partition_key=? AND record_id=?
            """,
            (collection, record.partition_key, record.id),
        )
        for name, vector in (record.vectors or {}).items():
            data = struct.pack(f"<{len(vector.values)}f", *vector.values)
            connection.execute(
                """
                INSERT INTO vyral_py_vectors(
                    collection, partition_key, record_id, vector_name, vector_data, dimensions
                )
                VALUES (?, ?, ?, ?, ?, ?)
                """,
                (
                    collection,
                    record.partition_key,
                    record.id,
                    name,
                    data,
                    len(vector.values),
                ),
            )

    @staticmethod
    def _replace_metadata_index(
        connection: sqlite3.Connection,
        collection: str,
        record: VyralRecord,
        policy: RecordCollectionPolicy,
    ) -> None:
        connection.execute(
            """
            DELETE FROM vyral_py_metadata_index
            WHERE collection=? AND partition_key=? AND record_id=?
            """,
            (collection, record.partition_key, record.id),
        )
        material = record.to_dict()
        for path in policy.indexed_metadata:
            found, value = _resolve_json_pointer(material, path)
            if not found:
                continue
            value_text = value if isinstance(value, str) else None
            value_number = (
                float(value)
                if isinstance(value, (int, float)) and not isinstance(value, bool)
                else None
            )
            value_bool = int(value) if isinstance(value, bool) else None
            connection.execute(
                """
                INSERT INTO vyral_py_metadata_index(
                    collection, partition_key, record_id, path,
                    value_text, value_number, value_bool, value_json
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    collection,
                    record.partition_key,
                    record.id,
                    path,
                    value_text,
                    value_number,
                    value_bool,
                    _json(value),
                ),
            )

    @staticmethod
    def _replace_lexical_index(
        connection: sqlite3.Connection,
        collection: str,
        record: VyralRecord,
    ) -> None:
        connection.execute(
            """
            DELETE FROM vyral_py_record_fts
            WHERE collection=? AND partition_key=? AND record_id=?
            """,
            (collection, record.partition_key, record.id),
        )
        connection.execute(
            """
            INSERT INTO vyral_py_record_fts(collection, partition_key, record_id, text)
            VALUES (?, ?, ?, ?)
            """,
            (
                collection,
                record.partition_key,
                record.id,
                _lexical_index_text(record),
            ),
        )


def _json(value: object) -> str:
    try:
        return json.dumps(
            value,
            ensure_ascii=False,
            separators=(",", ":"),
            sort_keys=True,
            allow_nan=False,
        )
    except (TypeError, ValueError) as exc:
        raise RecordValidationError("Record material must be finite JSON data.") from exc


def _database_datetime(value: datetime | None) -> str:
    if value is None:
        raise RecordValidationError("Stored timestamps are required.")
    return value.astimezone(timezone.utc).isoformat(timespec="microseconds")


def _stored_datetime(value: str) -> datetime:
    parsed = parse_iso_datetime(value)
    if parsed.tzinfo is None:
        raise RecordStoreError("Stored timestamp is missing its UTC offset.")
    return parsed.astimezone(timezone.utc)


def _normalize_clock_value(value: datetime) -> datetime:
    if not isinstance(value, datetime):
        raise RecordStoreError("The record-store clock must return a datetime.")
    if value.tzinfo is None:
        raise RecordStoreError("The record-store clock must return an offset-aware datetime.")
    return value.astimezone(timezone.utc)


def _validate_collection_name(collection: str) -> None:
    if not isinstance(collection, str) or not collection.strip():
        raise RecordValidationError("Collection is required.")
    if len(collection) > 255:
        raise RecordValidationError("Collection name cannot exceed 255 characters.")
    if _COLLECTION_PATTERN.fullmatch(collection) is None:
        raise RecordValidationError(
            "Collection name must start and end with an ASCII letter or digit and can only "
            "contain ASCII letters, digits, '-', '_', and '.'."
        )


def _validate_record_id(record_id: str) -> None:
    if not isinstance(record_id, str) or not record_id.strip():
        raise RecordValidationError("Record id is required.")
    if len(record_id.encode("utf-8")) > _MAX_RECORD_ID_BYTES:
        raise RecordValidationError(
            f"Record id cannot exceed {_MAX_RECORD_ID_BYTES} UTF-8 bytes."
        )
    if any(character in record_id for character in ("/", "\\", "?", "#")):
        raise RecordValidationError("Record id cannot contain '/', '\\', '?', or '#'.")


def _validate_partition_key(partition_key: str) -> None:
    if not isinstance(partition_key, str) or not partition_key.strip():
        raise RecordValidationError("Record partitionKey is required.")
    if len(partition_key.encode("utf-8")) > _MAX_PARTITION_KEY_BYTES:
        raise RecordValidationError(
            f"Record partitionKey cannot exceed {_MAX_PARTITION_KEY_BYTES} UTF-8 bytes."
        )


def _validate_record_identity(record: VyralRecord) -> None:
    _validate_record_id(record.id)
    _validate_partition_key(record.partition_key)


def _valid_path_segment(segment: str) -> bool:
    return bool(segment) and all(character.isalnum() or character in "_-" for character in segment)


def _validate_json_pointer(path: str) -> None:
    if not isinstance(path, str) or not path.strip() or not path.startswith("/"):
        raise RecordValidationError("Indexed metadata path must be a JSON pointer.")
    segments = [segment for segment in path.split("/") if segment]
    decoded = [segment.replace("~1", "/").replace("~0", "~") for segment in segments]
    if not decoded or not all(_valid_path_segment(segment) for segment in decoded):
        raise RecordValidationError(
            f"Indexed metadata path {path!r} contains unsupported segment characters."
        )


def _validate_policy(policy: RecordCollectionPolicy) -> None:
    _validate_collection_name(policy.name)
    if policy.partition_key_path != "/partitionKey":
        raise RecordValidationError("Collection partition key path must be '/partitionKey'.")
    names = [item.name for item in policy.vector_policies]
    paths = [item.path for item in policy.vector_policies]
    if len(names) != len(set(names)):
        raise RecordValidationError("Vector policy names must be unique within a collection.")
    if len(paths) != len(set(paths)):
        raise RecordValidationError("Vector policy paths must be unique within a collection.")
    if len(policy.indexed_metadata) != len(set(policy.indexed_metadata)):
        raise RecordValidationError(
            "Indexed metadata paths must be unique within a collection."
        )
    for path in policy.indexed_metadata:
        _validate_json_pointer(path)
    for vector in policy.vector_policies:
        _validate_vector_policy(vector)


def _validate_vector_policy(policy: VectorFieldPolicy) -> None:
    if not policy.name.strip():
        raise RecordValidationError("Vector policy name is required.")
    if not _valid_path_segment(policy.name):
        raise RecordValidationError(
            f"Vector policy name {policy.name!r} contains unsupported characters."
        )
    expected_path = f"/vectors/{policy.name}/values"
    if policy.path != expected_path:
        raise RecordValidationError(
            f"Vector policy {policy.name!r} path must be {expected_path!r}."
        )
    if policy.dimensions <= 0:
        raise RecordValidationError(
            f"Vector policy {policy.name!r} dimensions must be greater than zero."
        )
    if policy.datatype.lower() not in _SUPPORTED_DATATYPES:
        raise RecordValidationError(
            f"Vector policy {policy.name!r} datatype {policy.datatype!r} is not supported."
        )
    if policy.distance_function.lower() not in _SUPPORTED_DISTANCES:
        raise RecordValidationError(
            f"Vector policy {policy.name!r} distance function "
            f"{policy.distance_function!r} is not supported."
        )
    if policy.index_type.lower() not in _SUPPORTED_INDEX_TYPES:
        raise RecordValidationError(
            f"Vector policy {policy.name!r} index type {policy.index_type!r} is not supported."
        )


def _policy_vector_key(policy: VectorFieldPolicy) -> tuple[object, ...]:
    return (
        policy.name,
        policy.path,
        policy.dimensions,
        policy.datatype.lower(),
        policy.distance_function.lower(),
        policy.index_type.lower(),
    )


def _policies_equivalent(
    left: RecordCollectionPolicy,
    right: RecordCollectionPolicy,
) -> bool:
    return (
        left.name == right.name
        and left.partition_key_path == right.partition_key_path
        and sorted(_policy_vector_key(item) for item in left.vector_policies)
        == sorted(_policy_vector_key(item) for item in right.vector_policies)
        and set(left.indexed_metadata) == set(right.indexed_metadata)
    )


def _validate_vectors(
    collection: str,
    policy: RecordCollectionPolicy,
    vectors: Mapping[str, VyralVector] | None,
) -> Mapping[str, VyralVector] | None:
    if vectors is None:
        return None
    policies = {item.name: item for item in policy.vector_policies}
    normalized: dict[str, VyralVector] = {}
    for name, vector in vectors.items():
        field_policy = policies.get(name)
        if field_policy is None:
            raise RecordValidationError(
                f"Vector field {name!r} is not defined in policy for collection {collection!r}."
            )
        if vector.datatype.lower() != field_policy.datatype.lower():
            raise RecordValidationError(
                f"Vector field {name!r} datatype does not match the collection policy."
            )
        if vector.distance_function.lower() != field_policy.distance_function.lower():
            raise RecordValidationError(
                f"Vector field {name!r} distance function does not match the collection policy."
            )
        if len(vector.values) != field_policy.dimensions:
            raise RecordValidationError(
                f"Vector field {name!r} has dimensions {len(vector.values)}, "
                f"but policy expects {field_policy.dimensions}."
            )
        if vector.dimensions not in (0, field_policy.dimensions):
            raise RecordValidationError(
                f"Vector field {name!r} declares dimensions {vector.dimensions}, "
                f"but policy expects {field_policy.dimensions}."
            )
        if not all(math.isfinite(value) for value in vector.values):
            raise RecordValidationError(f"Vector field {name!r} contains a non-finite value.")
        values = _float32_values(vector.values, f"Vector field {name!r}")
        normalized[name] = vector.normalized(values, field_policy.dimensions)
    return normalized


def _float32(value: float, label: str) -> float:
    if not math.isfinite(value):
        raise RecordValidationError(f"{label} contains a non-finite value.")
    try:
        normalized = float(struct.unpack("<f", struct.pack("<f", value))[0])
    except (OverflowError, struct.error) as exc:
        raise RecordValidationError(f"{label} contains a value outside float32 range.") from exc
    if not math.isfinite(normalized):
        raise RecordValidationError(f"{label} contains a value outside float32 range.")
    return normalized


def _float32_values(values: Sequence[float], label: str) -> tuple[float, ...]:
    return tuple(_float32(float(value), label) for value in values)


def _similarity_score(
    distance_function: str,
    query: Sequence[float],
    stored: Sequence[float],
) -> float:
    if len(query) != len(stored):
        raise RecordStoreError(
            f"Vector dimensions differ: {len(query)} != {len(stored)}."
        )

    def dot(left: Sequence[float], right: Sequence[float]) -> float:
        total = 0.0
        for first, second in zip(left, right):
            total = _float32(
                total + _float32(first * second, "Vector score"),
                "Vector score",
            )
        return total

    normalized = distance_function.lower()
    if normalized == "dotproduct":
        return dot(query, stored)
    if normalized == "euclidean":
        total = 0.0
        for first, second in zip(query, stored):
            difference = _float32(first - second, "Vector score")
            total = _float32(
                total + _float32(difference * difference, "Vector score"),
                "Vector score",
            )
        return _float32(1.0 / (1.0 + math.sqrt(total)), "Vector score")
    if normalized == "cosine":
        numerator = dot(query, stored)
        query_norm = math.sqrt(max(0.0, dot(query, query)))
        stored_norm = math.sqrt(max(0.0, dot(stored, stored)))
        if query_norm == 0.0 or stored_norm == 0.0:
            return 0.0
        return _float32(numerator / (query_norm * stored_norm), "Vector score")
    raise RecordValidationError(
        f"Vector distance function {distance_function!r} is not supported."
    )


def _result_identity(collection: str, record: VyralRecord) -> JSONObject:
    return {
        "collection": collection,
        "partitionKey": record.partition_key,
        "id": record.id,
        "type": record.type,
        "etag": record.etag,
        "revision": record.revision,
    }


def _vector_normalization(distance_function: str) -> str:
    normalized = distance_function.lower()
    if normalized == "cosine":
        return "clamp((score+1)/2,0,1)"
    if normalized == "euclidean":
        return "clamp(local_similarity,0,1)"
    if normalized == "dotproduct":
        return "score<=0?0:score/(1+score)"
    return "clamp(score,0,1)"


def _vector_diagnostics(
    collection: str,
    record: VyralRecord,
    vector_field: str,
    distance_function: str,
    score: float,
) -> RetrievalDiagnostics:
    kind = distance_function.lower()
    return RetrievalDiagnostics(
        result_identity=_result_identity(collection, record),
        score_components={"vector": score},
        score_normalization={
            "finalScoreKind": "vector.raw_similarity",
            "vectorScoreKind": f"vector.similarity.{kind}",
            "lexicalScoreKind": None,
            "hybridFusion": None,
            "vectorDistanceFunction": distance_function,
            "vectorNormalization": _vector_normalization(distance_function),
            "weights": {},
            "parameters": {},
        },
        candidate_sources=("vector",),
        reason_codes=(
            "result.identity.record",
            "mode.vector",
            "candidate.source.vector",
            "score.vector.raw_similarity",
        ),
        details={
            "vectorField": vector_field,
            "vectorDistanceFunction": distance_function,
            "vectorIndexProvider": "sqlite-flat-scan",
            "vectorIndexUsed": False,
            "vectorIndexAvailable": False,
            "vectorIndexQuantized": False,
            "vectorIndexReason": "local_exact_scan",
        },
    )


def _ranked_diagnostics(
    diagnostics: RetrievalDiagnostics | None,
    *,
    rank: int,
    candidate_count: int,
    returned_count: int,
    mode: str,
    match: VyralRecordMatch,
) -> RetrievalDiagnostics:
    source = diagnostics or RetrievalDiagnostics()
    reason_codes = list(source.reason_codes)
    for code in (
        f"mode.{mode}",
        "rank.final.assigned",
        "rank.tie_break.applied",
    ):
        if code not in reason_codes:
            reason_codes.append(code)
    details = dict(source.details)
    details.update(
        {
            "rank": rank,
            "tieBreakOrder": "score desc, partitionKey asc, id asc",
            "tieBreakKey": {
                "score": match.score,
                "partitionKey": match.record.partition_key,
                "id": match.record.id,
            },
        }
    )
    counts = dict(source.candidate_counts)
    counts.update(
        {
            "searchCandidatePool": candidate_count,
            "returnedCandidates": returned_count,
        }
    )
    return RetrievalDiagnostics(
        result_identity=source.result_identity,
        score_components=source.score_components,
        score_normalization=source.score_normalization,
        candidate_sources=source.candidate_sources,
        candidate_counts=counts,
        reason_codes=tuple(reason_codes),
        matched_fields=source.matched_fields,
        matched_terms=source.matched_terms,
        details=details,
        schema_version=source.schema_version,
    )


def _normalize_etag(value: str | None) -> str | None:
    normalized = value.strip() if value is not None else ""
    if not normalized:
        return None
    if len(normalized) >= 2 and normalized[0] == '"' and normalized[-1] == '"':
        return normalized[1:-1]
    return normalized


def _ensure_precondition(
    precondition: RecordWritePrecondition | None,
    *,
    exists: bool,
    current_etag: str | None,
    current_revision: int | None,
) -> None:
    if precondition is None or not precondition.has_conditions:
        return
    if (
        _normalize_etag(precondition.if_match) is not None
        and _normalize_etag(precondition.if_none_match) is not None
    ):
        raise RecordPreconditionFailedError(
            "Record write precondition failed: If-Match and If-None-Match cannot both be supplied."
        )
    expected = _normalize_etag(precondition.expected_etag)
    if_match = _normalize_etag(precondition.if_match)
    if expected is not None and if_match is not None and expected != if_match:
        raise RecordPreconditionFailedError(
            "Record write precondition failed: expectedEtag and If-Match disagree."
        )
    match = expected or if_match
    if match == "*" and not exists:
        raise RecordPreconditionFailedError(
            "Record write precondition failed: record does not exist."
        )
    if match not in (None, "*") and (
        not exists or _normalize_etag(current_etag) != match
    ):
        raise RecordPreconditionFailedError(
            "Record write precondition failed: current ETag does not match."
        )
    if precondition.expected_revision is not None and (
        not exists or current_revision != precondition.expected_revision
    ):
        raise RecordPreconditionFailedError(
            "Record write precondition failed: current revision does not match."
        )
    none_match = _normalize_etag(precondition.if_none_match)
    if none_match == "*" and exists:
        raise RecordPreconditionFailedError(
            "Record write precondition failed: record already exists."
        )
    if (
        none_match not in (None, "*")
        and exists
        and _normalize_etag(current_etag) == none_match
    ):
        raise RecordPreconditionFailedError(
            "Record write precondition failed: current ETag matches If-None-Match."
        )


def _resolve_json_pointer(root: object, path: str) -> tuple[bool, object]:
    value = root
    for raw_segment in (segment for segment in path.split("/") if segment):
        segment = raw_segment.replace("~1", "/").replace("~0", "~")
        if not isinstance(value, Mapping) or segment not in value:
            return False, None
        value = value[segment]
    return True, value


def _append_lexical_values(value: object, output: list[str]) -> None:
    if isinstance(value, str):
        if value.strip():
            output.append(value)
    elif value is True:
        output.append("True")
    elif value is False:
        output.append("False")
    elif isinstance(value, (int, float)):
        output.append(str(value))
    elif isinstance(value, list):
        for item in value:
            _append_lexical_values(item, output)
    elif isinstance(value, Mapping):
        for key, item in value.items():
            if key != "vectors":
                _append_lexical_values(item, output)


def _lexical_index_text(record: VyralRecord) -> str:
    values = [
        value
        for value in (record.id, record.partition_key, record.type, record.schema_version)
        if value is not None and value.strip()
    ]
    _append_lexical_values(record.to_dict(), values)
    return f" {_VALUE_BOUNDARY_TOKEN} ".join(values)


def _record_identity_for_error(
    record: VyralRecord | Mapping[str, Any],
) -> tuple[str | None, str | None]:
    if isinstance(record, VyralRecord):
        return record.id, record.partition_key
    if not isinstance(record, Mapping):
        return None, None
    record_id = record.get("id")
    partition_key = record.get("partitionKey")
    return (
        record_id if isinstance(record_id, str) else None,
        partition_key if isinstance(partition_key, str) else None,
    )
