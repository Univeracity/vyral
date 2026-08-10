from __future__ import annotations

from dataclasses import dataclass, replace
from datetime import datetime, timezone
from hashlib import sha256
import json
import re
from time import perf_counter
from typing import Any, Callable, Mapping, Protocol, Sequence
from uuid import uuid4

from ..async_runtime import RuntimeExecutor
from ..embeddings import (
    EmbeddingProvider,
    EmbeddingProviderOptions,
    EmbeddingProviderRegistry,
    normalize_embedding_purpose,
    prepare_embedding_text,
)
from ..local import (
    FilterNode,
    QueryEnvelope,
    SQLiteRecordStore,
    TraceRecord,
    VectorFieldPolicy,
    VyralRecord,
    VyralVector,
)
from ..local.models import JSONObject
from .models import (
    MAX_RAG_BATCH_ITEMS,
    MAX_RAG_CHUNK_CHARS,
    MAX_RAG_CHUNKS,
    MAX_RAG_TEXT_LENGTH,
    RagIngestActionSummary,
    RagIngestChunkResult,
    RagIngestHashComparison,
    RagIngestStaleDeleteResult,
    RagIngestTextBatchItemResult,
    RagIngestTextBatchRequest,
    RagIngestTextBatchResult,
    RagIngestTextRequest,
    RagIngestTextResult,
)


_UNICODE_ESCAPE = re.compile(r"\\u([0-9a-fA-F]{4})")
_VALID_SCOPES = frozenset({"request", "partition", "collection"})


@dataclass(frozen=True)
class _Chunk:
    char_start: int
    char_end: int
    text: str


@dataclass(frozen=True)
class _ReusableVector:
    record_id: str
    partition_key: str
    values: tuple[float, ...] | None
    generated_at: datetime | None


@dataclass(frozen=True)
class _Duplicate:
    record_id: str
    partition_key: str
    etag: str | None
    revision: int | None


class TraceWriter(Protocol):
    def write_trace(self, trace: TraceRecord) -> None: ...


class RagIngestionService:
    def __init__(
        self,
        record_store: SQLiteRecordStore,
        embedding_provider: EmbeddingProvider | None = None,
        *,
        embedding_options: EmbeddingProviderOptions | Mapping[str, Any] | None = None,
        clock: Callable[[], datetime] | None = None,
        trace_store: TraceWriter | None = None,
        executor: RuntimeExecutor | None = None,
    ) -> None:
        self.record_store = record_store
        self.embedding_options = EmbeddingProviderOptions.from_value(embedding_options)
        self.embedding_provider = embedding_provider or EmbeddingProviderRegistry().create(
            self.embedding_options
        )
        self._clock = clock or (lambda: datetime.now(timezone.utc))
        self.trace_store = trace_store
        self.executor = executor or RuntimeExecutor()
        self._owns_executor = executor is None

    def ingest_text(
        self,
        collection: str,
        request: RagIngestTextRequest | Mapping[str, Any],
    ) -> RagIngestTextResult:
        started_at = _utc(self._clock())
        started = perf_counter()
        normalized = RagIngestTextRequest.from_value(request)
        _validate_request(normalized)
        policy = self.record_store.get_collection_policy(collection)
        if policy is None:
            raise ValueError(f"Collection {collection!r} does not exist.")
        embedding_field = _resolve_embedding_field(
            collection,
            policy.vector_policies,
            normalized.embedding.field if normalized.embedding is not None else None,
        )
        field_policy = next(
            item for item in policy.vector_policies if item.name == embedding_field
        )
        if field_policy.dimensions != self.embedding_provider.dimensions:
            raise ValueError(
                f"Embedding provider returns {self.embedding_provider.dimensions} "
                f"dimensions, but collection {collection!r} field {embedding_field!r} "
                f"expects {field_policy.dimensions}."
            )

        options = normalized.options
        vector_scope = _normalize_scope(
            options.vector_reuse_scope,
            "vectorReuseScope",
        )
        dedupe_scope = _normalize_scope(
            options.chunk_dedupe_scope,
            "chunkDedupeScope",
        )
        text_hash = "sha256:" + _sha256_hex(normalized.text)
        document_id = (
            normalized.document_id.strip()
            if normalized.document_id is not None and normalized.document_id.strip()
            else "doc-" + text_hash.removeprefix("sha256:")[:16]
        )
        id_prefix = _normalize_id_segment(
            normalized.id_prefix
            if normalized.id_prefix is not None and normalized.id_prefix.strip()
            else document_id
        )
        record_type = normalized.type if normalized.type.strip() else "rag.chunk"
        metadata_hash = _canonical_hash(normalized.metadata)
        source_hash = _canonical_hash(_source_hash_input(normalized))
        chunks = _split_text(
            normalized.text,
            options.chunk_chars,
            options.chunk_overlap_chars,
        )
        if len(chunks) > MAX_RAG_CHUNKS:
            raise ValueError(
                f"RAG text ingestion produced {len(chunks)} chunks, "
                f"but the limit is {MAX_RAG_CHUNKS}."
            )
        embedding_purpose = normalize_embedding_purpose(
            normalized.embedding.purpose
            if normalized.embedding is not None
            and normalized.embedding.purpose
            and normalized.embedding.purpose.strip()
            else "passage"
        )

        existing_records = self._load_scope_records(
            collection,
            normalized.partition_key,
            vector_scope,
            dedupe_scope,
        )
        existing_by_identity = {
            (record.partition_key, record.id): record for record in existing_records
        }
        reusable: dict[str, _ReusableVector] = {}
        duplicates: dict[str, _Duplicate] = {}
        chunk_results: list[RagIngestChunkResult] = []
        current_ids: set[str] = set()
        created_count = 0
        updated_count = 0
        reused_count = 0
        vector_generated_count = 0
        vector_reused_count = 0
        deduplicated_count = 0
        generated_at = _utc(self._clock())

        for index, chunk in enumerate(chunks):
            chunk_hash_hex = _sha256_hex(chunk.text)
            chunk_text_hash = "sha256:" + chunk_hash_hex
            prepared = prepare_embedding_text(
                chunk.text,
                embedding_purpose,
                (
                    normalized.embedding.query_prefix
                    if normalized.embedding is not None
                    else None
                )
                or self.embedding_options.query_prefix,
                (
                    normalized.embedding.passage_prefix
                    if normalized.embedding is not None
                    else None
                )
                or self.embedding_options.passage_prefix,
                (
                    normalized.embedding.symmetric_prefix
                    if normalized.embedding is not None
                    else None
                )
                or self.embedding_options.symmetric_prefix,
            )
            embedding_text_hash = "sha256:" + _sha256_hex(prepared.prepared_text)
            record_id = _chunk_id(id_prefix, index, chunk_hash_hex)
            existing = existing_by_identity.get(
                (normalized.partition_key, record_id)
            )
            if options.skip_unchanged_chunks and self._is_current(
                existing,
                normalized,
                record_type,
                embedding_field,
                field_policy,
                chunk,
                index,
                len(chunks),
                text_hash,
                chunk_text_hash,
                embedding_purpose,
                embedding_text_hash,
                metadata_hash,
                source_hash,
            ):
                assert existing is not None
                current_ids.add(record_id)
                self._cache_reusable(
                    reusable,
                    existing,
                    embedding_field,
                    embedding_text_hash,
                    field_policy,
                )
                _cache_duplicate(
                    duplicates,
                    embedding_field,
                    chunk_text_hash,
                    embedding_text_hash,
                    existing,
                )
                reused_count += 1
                chunk_results.append(
                    _chunk_result(
                        index,
                        existing.id,
                        existing.partition_key,
                        chunk,
                        chunk_text_hash,
                        embedding_text_hash,
                        "reused",
                        "unchanged",
                        etag=existing.etag,
                        revision=existing.revision,
                    )
                )
                continue

            duplicate = (
                self._find_duplicate(
                    existing_records,
                    duplicates,
                    normalized,
                    record_type,
                    dedupe_scope,
                    embedding_field,
                    field_policy,
                    chunk_text_hash,
                    embedding_purpose,
                    embedding_text_hash,
                    record_id,
                )
                if options.deduplicate_existing_chunks
                else None
            )
            if duplicate is not None:
                if duplicate.partition_key == normalized.partition_key:
                    current_ids.add(duplicate.record_id)
                deduplicated_count += 1
                chunk_results.append(
                    _chunk_result(
                        index,
                        duplicate.record_id,
                        duplicate.partition_key,
                        chunk,
                        chunk_text_hash,
                        embedding_text_hash,
                        "deduplicated",
                        "deduplicated",
                        deduplicated_from_id=duplicate.record_id,
                        deduplicated_from_partition_key=duplicate.partition_key,
                        etag=duplicate.etag,
                        revision=duplicate.revision,
                    )
                )
                continue

            reuse = (
                self._find_reusable(
                    existing_records,
                    reusable,
                    normalized.partition_key,
                    vector_scope,
                    embedding_field,
                    field_policy,
                    embedding_purpose,
                    embedding_text_hash,
                )
                if options.reuse_existing_chunk_vectors
                else None
            )
            embedding_action = "reused" if reuse is not None else "generated"
            if reuse is None:
                vector_generated_count += 1
            else:
                vector_reused_count += 1
            action = "created" if existing is None else "updated"
            if existing is None:
                created_count += 1
            else:
                updated_count += 1

            if options.dry_run:
                current_ids.add(record_id)
                if options.reuse_existing_chunk_vectors and reuse is None:
                    reusable.setdefault(
                        _reuse_key(embedding_field, embedding_text_hash),
                        _ReusableVector(
                            record_id,
                            normalized.partition_key,
                            None,
                            generated_at,
                        ),
                    )
                if options.deduplicate_existing_chunks:
                    duplicates.setdefault(
                        _duplicate_key(
                            embedding_field,
                            chunk_text_hash,
                            embedding_text_hash,
                        ),
                        _Duplicate(
                            record_id,
                            normalized.partition_key,
                            None,
                            None,
                        ),
                    )
                chunk_results.append(
                    _chunk_result(
                        index,
                        record_id,
                        normalized.partition_key,
                        chunk,
                        chunk_text_hash,
                        embedding_text_hash,
                        action,
                        embedding_action,
                        reused_vector_from_id=(
                            reuse.record_id if reuse is not None else None
                        ),
                        reused_vector_from_partition_key=(
                            reuse.partition_key if reuse is not None else None
                        ),
                    )
                )
                continue

            vector = (
                reuse.values
                if reuse is not None and reuse.values is not None
                else self.embedding_provider.generate_embedding(prepared.prepared_text)
            )
            if len(vector) != self.embedding_provider.dimensions:
                raise ValueError(
                    f"Embedding provider returned {len(vector)} dimensions, "
                    f"expected {self.embedding_provider.dimensions}."
                )
            record = self._create_chunk_record(
                normalized,
                document_id,
                text_hash,
                record_type,
                metadata_hash,
                source_hash,
                embedding_field,
                field_policy,
                chunk,
                chunk_text_hash,
                embedding_purpose,
                embedding_text_hash,
                prepared.prefix_applied,
                prepared.prefix_length,
                index,
                len(chunks),
                vector,
                (
                    reuse.generated_at
                    if reuse is not None and reuse.generated_at is not None
                    else generated_at
                ),
                reuse,
            )
            stored = self.record_store.upsert_record(collection, record)
            existing_records = (*existing_records, stored)
            existing_by_identity[(stored.partition_key, stored.id)] = stored
            current_ids.add(stored.id)
            self._cache_reusable(
                reusable,
                stored,
                embedding_field,
                embedding_text_hash,
                field_policy,
            )
            _cache_duplicate(
                duplicates,
                embedding_field,
                chunk_text_hash,
                embedding_text_hash,
                stored,
            )
            chunk_results.append(
                _chunk_result(
                    index,
                    stored.id,
                    stored.partition_key,
                    chunk,
                    chunk_text_hash,
                    embedding_text_hash,
                    action,
                    embedding_action,
                    reused_vector_from_id=(
                        reuse.record_id if reuse is not None else None
                    ),
                    reused_vector_from_partition_key=(
                        reuse.partition_key if reuse is not None else None
                    ),
                    etag=stored.etag,
                    revision=stored.revision,
                )
            )

        stale_deletes = self._stale_deletes(
            collection,
            normalized,
            document_id,
            record_type,
            current_ids,
        )
        if options.replace_document_chunks and not options.dry_run:
            for stale in stale_deletes:
                self.record_store.delete_record(
                    collection,
                    stale.partition_key,
                    stale.id,
                )
        elif not options.replace_document_chunks:
            stale_deletes = ()

        manifest_id: str | None = None
        manifest_hash: str | None = None
        manifest_action: str | None = None
        manifest_etag: str | None = None
        manifest_revision: int | None = None
        if options.persist_manifest:
            manifest_id = _normalize_id_segment(
                options.manifest_id
                if options.manifest_id is not None and options.manifest_id.strip()
                else id_prefix + "-manifest"
            )
            manifest_input = self._manifest_hash_input(
                collection,
                normalized,
                document_id,
                text_hash,
                record_type,
                metadata_hash,
                source_hash,
                embedding_field,
                field_policy,
                embedding_purpose,
                chunk_results,
            )
            manifest_hash = _canonical_hash(manifest_input)
            existing_manifest = self.record_store.get_record(
                collection,
                normalized.partition_key,
                manifest_id,
            )
            if (
                options.skip_unchanged_chunks
                and _manifest_current(
                    existing_manifest,
                    manifest_hash,
                    document_id,
                    record_type,
                    self.embedding_provider,
                )
            ):
                assert existing_manifest is not None
                manifest_action = "reused"
                manifest_etag = existing_manifest.etag
                manifest_revision = existing_manifest.revision
            else:
                manifest_action = (
                    "created" if existing_manifest is None else "updated"
                )
                if not options.dry_run:
                    manifest_record = self._manifest_record(
                        normalized,
                        document_id,
                        text_hash,
                        record_type,
                        metadata_hash,
                        source_hash,
                        embedding_field,
                        embedding_purpose,
                        manifest_id,
                        manifest_hash,
                        manifest_input,
                        chunks,
                        chunk_results,
                    )
                    stored_manifest = self.record_store.upsert_record(
                        collection,
                        manifest_record,
                    )
                    manifest_etag = stored_manifest.etag
                    manifest_revision = stored_manifest.revision

        action_summary = _action_summary(chunk_results, stale_deletes)
        result_values: dict[str, object] = {
            "collection": collection,
            "document_id": document_id,
            "partition_key": normalized.partition_key,
            "embedding_field": embedding_field,
            "embedding_provider": self.embedding_provider.provider_id,
            "embedding_model": self.embedding_provider.model_id,
            "embedding_purpose": embedding_purpose,
            "dimensions": self.embedding_provider.dimensions,
            "text_length": len(normalized.text),
            "text_hash": text_hash,
            "chunk_count": len(chunks),
            "dry_run": options.dry_run,
            "deleted_stale_count": len(stale_deletes),
            "created_count": created_count,
            "updated_count": updated_count,
            "reused_count": reused_count,
            "vector_generated_count": vector_generated_count,
            "vector_reused_count": vector_reused_count,
            "deduplicated_count": deduplicated_count,
            "manifest_id": manifest_id,
            "manifest_hash": manifest_hash,
            "manifest_action": manifest_action,
            "manifest_etag": manifest_etag,
            "manifest_revision": manifest_revision,
            "chunks": tuple(chunk_results),
            "stale_deletes": tuple(stale_deletes),
        }
        plan_hash = _canonical_hash(
            self._plan_hash_input(
                normalized,
                field_policy,
                metadata_hash,
                source_hash,
                id_prefix,
                vector_scope,
                dedupe_scope,
                result_values,
            )
        )
        plan_comparison = _hash_comparison(
            "plan",
            options.expected_plan_hash,
            plan_hash,
        )
        manifest_comparison = _hash_comparison(
            "manifest",
            options.expected_manifest_hash,
            manifest_hash,
        )
        trace: JSONObject | None = None
        if options.include_trace:
            duration_ms = (perf_counter() - started) * 1000.0
            trace_record = TraceRecord(
                operation="rag.ingest_text",
                adapter=type(self.record_store).__name__,
                request={
                    "collection": collection,
                    "documentId": document_id,
                    "partitionKey": normalized.partition_key,
                    "contentField": normalized.content_field,
                    "embeddingField": embedding_field,
                    "chunkChars": options.chunk_chars,
                    "chunkOverlapChars": options.chunk_overlap_chars,
                    "dryRun": options.dry_run,
                    "replaceDocumentChunks": options.replace_document_chunks,
                    "skipUnchangedChunks": options.skip_unchanged_chunks,
                    "reuseExistingChunkVectors": (
                        options.reuse_existing_chunk_vectors
                    ),
                    "vectorReuseScope": vector_scope,
                    "deduplicateExistingChunks": (
                        options.deduplicate_existing_chunks
                    ),
                    "chunkDedupeScope": dedupe_scope,
                    "embeddingPurpose": embedding_purpose,
                    "persistManifest": options.persist_manifest,
                    "textLength": len(normalized.text),
                    "textHash": text_hash,
                    "planHash": plan_hash,
                },
                result_summary={
                    "embeddingProvider": self.embedding_provider.provider_id,
                    "embeddingModel": self.embedding_provider.model_id,
                    "embeddingDimensions": self.embedding_provider.dimensions,
                    "embeddingPurpose": embedding_purpose,
                    "chunkCount": len(chunks),
                    "dryRun": options.dry_run,
                    "deletedStaleCount": len(stale_deletes),
                    "createdCount": created_count,
                    "updatedCount": updated_count,
                    "reusedCount": reused_count,
                    "vectorGeneratedCount": vector_generated_count,
                    "vectorReusedCount": vector_reused_count,
                    "deduplicatedCount": deduplicated_count,
                    "planHash": plan_hash,
                    "planHashComparisonStatus": plan_comparison.status,
                    "manifestId": manifest_id,
                    "manifestHash": manifest_hash,
                    "manifestAction": manifest_action,
                    "manifestHashComparisonStatus": manifest_comparison.status,
                },
                started_at=started_at,
                duration_ms=duration_ms,
                created_at=_utc(self._clock()),
            )
            persisted = self.trace_store is not None and not options.dry_run
            if persisted:
                assert self.trace_store is not None
                self.trace_store.write_trace(trace_record)
            trace = {
                "id": trace_record.id,
                "durationMs": duration_ms,
                "operation": "rag.ingest_text",
                "adapter": type(self.record_store).__name__,
                "textHash": text_hash,
                "planHash": plan_hash,
                "planHashComparisonStatus": plan_comparison.status,
                "chunkCount": len(chunks),
                "dryRun": options.dry_run,
                "tracePersisted": persisted,
                "deletedStaleCount": len(stale_deletes),
                "createdCount": created_count,
                "updatedCount": updated_count,
                "reusedCount": reused_count,
                "vectorGeneratedCount": vector_generated_count,
                "vectorReusedCount": vector_reused_count,
                "deduplicatedCount": deduplicated_count,
                "manifestId": manifest_id or "",
                "manifestHash": manifest_hash or "",
                "manifestAction": manifest_action or "",
                "manifestHashComparisonStatus": manifest_comparison.status,
                "actionSummary": action_summary.to_dict(),
                "staleDeleteIds": [item.id for item in stale_deletes],
                "embeddingProvider": self.embedding_provider.provider_id,
                "embeddingModel": self.embedding_provider.model_id,
                "embeddingDimensions": self.embedding_provider.dimensions,
                "embeddingPurpose": embedding_purpose,
            }
        return RagIngestTextResult(
            collection=collection,
            document_id=document_id,
            partition_key=normalized.partition_key,
            embedding_field=embedding_field,
            embedding_provider=self.embedding_provider.provider_id,
            embedding_model=self.embedding_provider.model_id,
            embedding_purpose=embedding_purpose,
            dimensions=self.embedding_provider.dimensions,
            text_length=len(normalized.text),
            text_hash=text_hash,
            plan_hash=plan_hash,
            chunk_count=len(chunks),
            dry_run=options.dry_run,
            deleted_stale_count=len(stale_deletes),
            created_count=created_count,
            updated_count=updated_count,
            reused_count=reused_count,
            vector_generated_count=vector_generated_count,
            vector_reused_count=vector_reused_count,
            deduplicated_count=deduplicated_count,
            manifest_id=manifest_id,
            manifest_hash=manifest_hash,
            manifest_action=manifest_action,
            manifest_etag=manifest_etag,
            manifest_revision=manifest_revision,
            action_summary=action_summary,
            plan_hash_comparison=plan_comparison,
            manifest_hash_comparison=manifest_comparison,
            stale_deletes=tuple(stale_deletes),
            chunks=tuple(chunk_results),
            trace=trace,
        )

    async def aingest_text(
        self,
        collection: str,
        request: RagIngestTextRequest | Mapping[str, Any],
    ) -> RagIngestTextResult:
        return await self.executor.run(lambda: self.ingest_text(collection, request))

    def ingest_text_batch(
        self,
        collection: str,
        request: RagIngestTextBatchRequest | Mapping[str, Any],
    ) -> RagIngestTextBatchResult:
        normalized = RagIngestTextBatchRequest.from_value(request)
        if not normalized.items:
            raise ValueError(
                "RAG text batch ingestion request must include at least one item."
            )
        if len(normalized.items) > MAX_RAG_BATCH_ITEMS:
            raise ValueError(
                "RAG text batch ingestion request supports at most "
                f"{MAX_RAG_BATCH_ITEMS} items."
            )
        items: list[RagIngestTextBatchItemResult] = []
        results: list[RagIngestTextResult] = []
        failed = 0
        stopped = False
        for index, item in enumerate(normalized.items):
            try:
                result = self.ingest_text(collection, item)
            except (TypeError, ValueError) as exc:
                failed += 1
                items.append(
                    RagIngestTextBatchItemResult(
                        index=index,
                        document_id=(
                            item.document_id
                            if item.document_id and item.document_id.strip()
                            else None
                        ),
                        partition_key=(
                            item.partition_key if item.partition_key.strip() else None
                        ),
                        status="failed",
                        error=str(exc),
                    )
                )
                if not normalized.continue_on_error:
                    stopped = index + 1 < len(normalized.items)
                    break
            else:
                results.append(result)
                items.append(
                    RagIngestTextBatchItemResult(
                        index=index,
                        document_id=result.document_id,
                        partition_key=result.partition_key,
                        status="succeeded",
                        result=result,
                    )
                )
        return RagIngestTextBatchResult(
            collection=collection,
            requested=len(normalized.items),
            attempted=len(items),
            succeeded=len(results),
            failed=failed,
            stopped_on_error=stopped,
            text_length=sum(item.text_length for item in results),
            chunk_count=sum(item.chunk_count for item in results),
            deleted_stale_count=sum(item.deleted_stale_count for item in results),
            created_count=sum(item.created_count for item in results),
            updated_count=sum(item.updated_count for item in results),
            reused_count=sum(item.reused_count for item in results),
            vector_generated_count=sum(
                item.vector_generated_count for item in results
            ),
            vector_reused_count=sum(item.vector_reused_count for item in results),
            deduplicated_count=sum(item.deduplicated_count for item in results),
            items=tuple(items),
        )

    async def aingest_text_batch(
        self,
        collection: str,
        request: RagIngestTextBatchRequest | Mapping[str, Any],
    ) -> RagIngestTextBatchResult:
        return await self.executor.run(
            lambda: self.ingest_text_batch(collection, request)
        )

    def close(self) -> None:
        if self._owns_executor:
            self.executor.close()

    def _load_scope_records(
        self,
        collection: str,
        partition_key: str,
        vector_scope: str,
        dedupe_scope: str,
    ) -> tuple[VyralRecord, ...]:
        collection_scope = (
            vector_scope == "collection" or dedupe_scope == "collection"
        )
        return self.record_store.query_all_records(
            collection,
            QueryEnvelope(
                partition_keys=None if collection_scope else (partition_key,),
            ),
        )

    def _is_current(
        self,
        record: VyralRecord | None,
        request: RagIngestTextRequest,
        record_type: str,
        embedding_field: str,
        field_policy: VectorFieldPolicy,
        chunk: _Chunk,
        index: int,
        chunk_count: int,
        document_text_hash: str,
        chunk_text_hash: str,
        embedding_purpose: str,
        embedding_text_hash: str,
        metadata_hash: str,
        source_hash: str,
    ) -> bool:
        if (
            record is None
            or record.type != record_type
            or record.schema_version != request.schema_version
            or not isinstance(record.content, Mapping)
            or record.content.get(request.content_field) != chunk.text
            or not isinstance(record.metadata, Mapping)
        ):
            return False
        expected: Mapping[str, object] = {
            "documentTextHash": document_text_hash,
            "textHash": chunk_text_hash,
            "ingestionMetadataHash": metadata_hash,
            "ingestionSourceHash": source_hash,
            "chunkIndex": index,
            "chunkCount": chunk_count,
            "charStart": chunk.char_start,
            "charEnd": chunk.char_end,
            "embeddingPurpose": embedding_purpose,
            "embeddingTextHash": embedding_text_hash,
            "embeddingProvider": self.embedding_provider.provider_id,
            "embeddingModel": self.embedding_provider.model_id,
        }
        if any(record.metadata.get(key) != value for key, value in expected.items()):
            return False
        vector = (record.vectors or {}).get(embedding_field)
        return (
            vector is not None
            and len(vector.values) == self.embedding_provider.dimensions
            and vector.dimensions == self.embedding_provider.dimensions
            and vector.model == self.embedding_provider.model_id
            and vector.datatype == field_policy.datatype
            and vector.distance_function == field_policy.distance_function
            and vector.source_field == "content." + request.content_field
        )

    def _reusable_from_record(
        self,
        record: VyralRecord,
        embedding_field: str,
        field_policy: VectorFieldPolicy,
        embedding_purpose: str | None,
        embedding_text_hash: str,
    ) -> _ReusableVector | None:
        metadata = record.metadata or {}
        vector = (record.vectors or {}).get(embedding_field)
        if vector is None:
            return None
        if (
            metadata.get("embeddingTextHash") != embedding_text_hash
            or (
                embedding_purpose is not None
                and metadata.get("embeddingPurpose") != embedding_purpose
            )
            or metadata.get("embeddingProvider")
            != self.embedding_provider.provider_id
            or metadata.get("embeddingModel") != self.embedding_provider.model_id
            or len(vector.values) != self.embedding_provider.dimensions
            or vector.dimensions != self.embedding_provider.dimensions
            or vector.model != self.embedding_provider.model_id
            or vector.datatype != field_policy.datatype
            or vector.distance_function != field_policy.distance_function
        ):
            return None
        return _ReusableVector(
            record.id,
            record.partition_key,
            vector.values,
            vector.generated_at,
        )

    def _cache_reusable(
        self,
        cache: dict[str, _ReusableVector],
        record: VyralRecord,
        embedding_field: str,
        embedding_text_hash: str,
        policy: VectorFieldPolicy,
    ) -> None:
        reusable = self._reusable_from_record(
            record,
            embedding_field,
            policy,
            None,
            embedding_text_hash,
        )
        if reusable is not None:
            cache.setdefault(
                _reuse_key(embedding_field, embedding_text_hash),
                reusable,
            )

    def _find_reusable(
        self,
        records: Sequence[VyralRecord],
        cache: Mapping[str, _ReusableVector],
        partition_key: str,
        scope: str,
        embedding_field: str,
        policy: VectorFieldPolicy,
        embedding_purpose: str,
        embedding_text_hash: str,
    ) -> _ReusableVector | None:
        key = _reuse_key(embedding_field, embedding_text_hash)
        cached = cache.get(key)
        if cached is not None:
            return cached
        if scope == "request":
            return None
        candidates = (
            record
            for record in records
            if scope == "collection" or record.partition_key == partition_key
        )
        for record in sorted(
            candidates,
            key=lambda item: (item.partition_key, item.id),
        )[:25]:
            reusable = self._reusable_from_record(
                record,
                embedding_field,
                policy,
                embedding_purpose,
                embedding_text_hash,
            )
            if reusable is not None:
                return reusable
        return None

    def _find_duplicate(
        self,
        records: Sequence[VyralRecord],
        cache: Mapping[str, _Duplicate],
        request: RagIngestTextRequest,
        record_type: str,
        scope: str,
        embedding_field: str,
        policy: VectorFieldPolicy,
        chunk_text_hash: str,
        embedding_purpose: str,
        embedding_text_hash: str,
        intended_id: str,
    ) -> _Duplicate | None:
        key = _duplicate_key(
            embedding_field,
            chunk_text_hash,
            embedding_text_hash,
        )
        cached = cache.get(key)
        if (
            cached is not None
            and (cached.partition_key, cached.record_id)
            != (request.partition_key, intended_id)
        ):
            return cached
        if scope == "request":
            return None
        candidates = (
            record
            for record in records
            if scope == "collection" or record.partition_key == request.partition_key
        )
        for record in sorted(
            candidates,
            key=lambda item: (item.partition_key, item.id),
        )[:50]:
            if (
                (record.partition_key, record.id)
                == (request.partition_key, intended_id)
                or record.type != record_type
                or not isinstance(record.content, Mapping)
                or request.content_field not in record.content
                or (record.metadata or {}).get("textHash") != chunk_text_hash
            ):
                continue
            reusable = self._reusable_from_record(
                record,
                embedding_field,
                policy,
                embedding_purpose,
                embedding_text_hash,
            )
            if reusable is not None:
                return _Duplicate(
                    record.id,
                    record.partition_key,
                    record.etag,
                    record.revision,
                )
        return None

    def _create_chunk_record(
        self,
        request: RagIngestTextRequest,
        document_id: str,
        document_text_hash: str,
        record_type: str,
        metadata_hash: str,
        source_hash: str,
        embedding_field: str,
        policy: VectorFieldPolicy,
        chunk: _Chunk,
        chunk_text_hash: str,
        embedding_purpose: str,
        embedding_text_hash: str,
        prefix_applied: bool,
        prefix_length: int,
        index: int,
        chunk_count: int,
        vector: tuple[float, ...],
        generated_at: datetime,
        reuse: _ReusableVector | None,
    ) -> VyralRecord:
        metadata = dict(request.metadata or {})
        metadata.update(
            {
                "documentId": document_id,
                "documentTextHash": document_text_hash,
                "ingestionMetadataHash": metadata_hash,
                "ingestionSourceHash": source_hash,
                "chunkIndex": index,
                "chunkCount": chunk_count,
                "charStart": chunk.char_start,
                "charEnd": chunk.char_end,
                "textHash": chunk_text_hash,
                "embeddingPurpose": embedding_purpose,
                "embeddingTextHash": embedding_text_hash,
                "embeddingPrefixApplied": prefix_applied,
                "embeddingPrefixLength": prefix_length,
                "embeddingProvider": self.embedding_provider.provider_id,
                "embeddingModel": self.embedding_provider.model_id,
            }
        )
        if reuse is not None:
            metadata["vectorReusedFromId"] = reuse.record_id
            metadata["vectorReusedFromPartitionKey"] = reuse.partition_key
        chunk_hash = chunk_text_hash.removeprefix("sha256:")
        return VyralRecord(
            id=_chunk_id(
                _normalize_id_segment(
                    request.id_prefix
                    if request.id_prefix is not None and request.id_prefix.strip()
                    else document_id
                ),
                index,
                chunk_hash,
            ),
            partition_key=request.partition_key,
            type=record_type,
            schema_version=request.schema_version,
            metadata=metadata,
            content={request.content_field: chunk.text},
            sources=_chunk_sources(request, document_id, chunk, index),
            vectors={
                embedding_field: VyralVector(
                    values=tuple(vector),
                    dimensions=len(vector),
                    model=self.embedding_provider.model_id,
                    datatype=policy.datatype,
                    distance_function=policy.distance_function,
                    generated_at=generated_at,
                    source_field="content." + request.content_field,
                )
            },
        )

    def _stale_deletes(
        self,
        collection: str,
        request: RagIngestTextRequest,
        document_id: str,
        record_type: str,
        current_ids: set[str],
    ) -> tuple[RagIngestStaleDeleteResult, ...]:
        records = self.record_store.query_all_records(
            collection,
            QueryEnvelope(
                partition_keys=(request.partition_key,),
                filter=FilterNode(
                    combine="all",
                    children=(
                        FilterNode(
                            path="/metadata/documentId",
                            op="eq",
                            value=document_id,
                        ),
                        FilterNode(path="/type", op="eq", value=record_type),
                    ),
                ),
            ),
        )
        return tuple(
            RagIngestStaleDeleteResult(
                id=record.id,
                partition_key=record.partition_key,
                chunk_index=_metadata_int(record, "chunkIndex"),
                text_hash=_metadata_str(record, "textHash"),
                etag=record.etag,
                revision=record.revision,
            )
            for record in sorted(
                records,
                key=lambda item: (item.partition_key, item.id),
            )
            if record.id not in current_ids
        )

    def _manifest_hash_input(
        self,
        collection: str,
        request: RagIngestTextRequest,
        document_id: str,
        document_text_hash: str,
        record_type: str,
        metadata_hash: str,
        source_hash: str,
        embedding_field: str,
        policy: VectorFieldPolicy,
        embedding_purpose: str,
        chunks: Sequence[RagIngestChunkResult],
    ) -> JSONObject:
        options = request.options
        return {
            "version": "v1",
            "collection": collection,
            "documentId": document_id,
            "partitionKey": request.partition_key,
            "chunkRecordType": record_type,
            "schemaVersion": request.schema_version,
            "contentField": request.content_field,
            "embeddingField": embedding_field,
            "embeddingProvider": self.embedding_provider.provider_id,
            "embeddingModel": self.embedding_provider.model_id,
            "embeddingPurpose": embedding_purpose,
            "embeddingDimensions": self.embedding_provider.dimensions,
            "vectorPath": policy.path,
            "vectorDatatype": policy.datatype,
            "vectorDistanceFunction": policy.distance_function,
            "vectorIndexType": policy.index_type,
            "textLength": len(request.text),
            "documentTextHash": document_text_hash,
            "chunkChars": options.chunk_chars,
            "chunkOverlapChars": options.chunk_overlap_chars,
            "deduplicateExistingChunks": options.deduplicate_existing_chunks,
            "chunkDedupeScope": (
                _normalize_scope(options.chunk_dedupe_scope, "chunkDedupeScope")
                if options.deduplicate_existing_chunks
                else None
            ),
            "metadataHash": metadata_hash,
            "sourceHash": source_hash,
            "chunks": [
                {
                    "index": chunk.index,
                    "id": chunk.id,
                    "partitionKey": chunk.partition_key,
                    "charStart": chunk.char_start,
                    "charEnd": chunk.char_end,
                    "textLength": chunk.text_length,
                    "textHash": chunk.text_hash,
                    "embeddingTextHash": chunk.embedding_text_hash,
                }
                for chunk in sorted(chunks, key=lambda item: item.index)
            ],
        }

    def _manifest_record(
        self,
        request: RagIngestTextRequest,
        document_id: str,
        document_text_hash: str,
        record_type: str,
        metadata_hash: str,
        source_hash: str,
        embedding_field: str,
        embedding_purpose: str,
        manifest_id: str,
        manifest_hash: str,
        manifest_input: JSONObject,
        chunks: Sequence[_Chunk],
        chunk_results: Sequence[RagIngestChunkResult],
    ) -> VyralRecord:
        by_index = {item.index: item for item in chunk_results}
        return VyralRecord(
            id=manifest_id,
            partition_key=request.partition_key,
            type="rag.manifest",
            schema_version="v1",
            metadata={
                "documentId": document_id,
                "documentTextHash": document_text_hash,
                "manifestHash": manifest_hash,
                "manifestVersion": "v1",
                "chunkRecordType": record_type,
                "chunkCount": len(chunk_results),
                "textLength": len(request.text),
                "ingestionMetadataHash": metadata_hash,
                "ingestionSourceHash": source_hash,
                "embeddingField": embedding_field,
                "embeddingProvider": self.embedding_provider.provider_id,
                "embeddingModel": self.embedding_provider.model_id,
                "embeddingPurpose": embedding_purpose,
                "embeddingDimensions": self.embedding_provider.dimensions,
            },
            content={
                "manifest": {
                    "version": "v1",
                    "manifestHash": manifest_hash,
                    "rawTextIncluded": False,
                    "sourceTextHash": document_text_hash,
                    "ingestionPlan": manifest_input,
                    "chunkSpans": [
                        {
                            "index": index,
                            "charStart": chunk.char_start,
                            "charEnd": chunk.char_end,
                            "textLength": len(chunk.text),
                            "embeddingTextHash": (
                                by_index[index].embedding_text_hash
                                if index in by_index
                                else ""
                            ),
                        }
                        for index, chunk in enumerate(chunks)
                    ],
                }
            },
            sources=_manifest_sources(request, document_id),
        )

    def _plan_hash_input(
        self,
        request: RagIngestTextRequest,
        policy: VectorFieldPolicy,
        metadata_hash: str,
        source_hash: str,
        id_prefix: str,
        vector_scope: str,
        dedupe_scope: str,
        values: Mapping[str, object],
    ) -> JSONObject:
        options = request.options
        chunks = values["chunks"]
        stale = values["stale_deletes"]
        assert isinstance(chunks, tuple)
        assert isinstance(stale, tuple)
        return {
            "version": "rag.ingest.plan.v1",
            "collection": values["collection"],
            "documentId": values["document_id"],
            "partitionKey": values["partition_key"],
            "idPrefix": id_prefix,
            "chunkRecordType": (
                request.type if request.type.strip() else "rag.chunk"
            ),
            "schemaVersion": request.schema_version,
            "contentField": request.content_field,
            "embeddingField": values["embedding_field"],
            "embeddingProvider": values["embedding_provider"],
            "embeddingModel": values["embedding_model"],
            "embeddingPurpose": values["embedding_purpose"],
            "embeddingDimensions": values["dimensions"],
            "vectorPath": policy.path,
            "vectorDatatype": policy.datatype,
            "vectorDistanceFunction": policy.distance_function,
            "vectorIndexType": policy.index_type,
            "textLength": values["text_length"],
            "documentTextHash": values["text_hash"],
            "chunkChars": options.chunk_chars,
            "chunkOverlapChars": options.chunk_overlap_chars,
            "metadataHash": metadata_hash,
            "sourceHash": source_hash,
            "replaceDocumentChunks": options.replace_document_chunks,
            "skipUnchangedChunks": options.skip_unchanged_chunks,
            "reuseExistingChunkVectors": options.reuse_existing_chunk_vectors,
            "vectorReuseScope": vector_scope,
            "deduplicateExistingChunks": options.deduplicate_existing_chunks,
            "chunkDedupeScope": (
                dedupe_scope if options.deduplicate_existing_chunks else None
            ),
            "persistManifest": options.persist_manifest,
            "manifestId": values["manifest_id"],
            "manifestHash": values["manifest_hash"],
            "manifestAction": values["manifest_action"],
            "chunkCount": values["chunk_count"],
            "deletedStaleCount": values["deleted_stale_count"],
            "createdCount": values["created_count"],
            "updatedCount": values["updated_count"],
            "reusedCount": values["reused_count"],
            "vectorGeneratedCount": values["vector_generated_count"],
            "vectorReusedCount": values["vector_reused_count"],
            "deduplicatedCount": values["deduplicated_count"],
            "staleDeletes": [
                {
                    "id": item.id,
                    "partitionKey": item.partition_key,
                    "chunkIndex": item.chunk_index,
                    "textHash": item.text_hash,
                }
                for item in sorted(
                    stale,
                    key=lambda item: (item.partition_key, item.id),
                )
            ],
            "chunks": [
                {
                    "index": item.index,
                    "id": item.id,
                    "partitionKey": item.partition_key,
                    "charStart": item.char_start,
                    "charEnd": item.char_end,
                    "textLength": item.text_length,
                    "textHash": item.text_hash,
                    "embeddingTextHash": item.embedding_text_hash,
                    "action": item.action,
                    "embeddingAction": item.embedding_action,
                    "reusedVectorFromId": item.reused_vector_from_id,
                    "reusedVectorFromPartitionKey": (
                        item.reused_vector_from_partition_key
                    ),
                    "deduplicatedFromId": item.deduplicated_from_id,
                    "deduplicatedFromPartitionKey": (
                        item.deduplicated_from_partition_key
                    ),
                }
                for item in sorted(chunks, key=lambda item: item.index)
            ],
        }


def _validate_request(request: RagIngestTextRequest) -> None:
    if not request.partition_key.strip():
        raise ValueError("RAG text ingestion partitionKey is required.")
    if not request.text.strip():
        raise ValueError("RAG text ingestion requires non-empty text.")
    if len(request.text) > MAX_RAG_TEXT_LENGTH:
        raise ValueError(
            f"RAG text ingestion supports at most {MAX_RAG_TEXT_LENGTH} "
            "characters per request."
        )
    if not request.content_field.strip():
        raise ValueError("RAG text ingestion contentField is required.")
    if any(character in request.content_field for character in "/\\."):
        raise ValueError(
            "RAG text ingestion contentField must be a simple content property name."
        )
    options = request.options
    if options.chunk_chars <= 0 or options.chunk_chars > MAX_RAG_CHUNK_CHARS:
        raise ValueError(
            "RAG text ingestion chunkChars must be between 1 and "
            f"{MAX_RAG_CHUNK_CHARS}."
        )
    if options.chunk_overlap_chars < 0:
        raise ValueError(
            "RAG text ingestion chunkOverlapChars cannot be negative."
        )
    if options.chunk_overlap_chars >= options.chunk_chars:
        raise ValueError(
            "RAG text ingestion chunkOverlapChars must be smaller than chunkChars."
        )
    _normalize_scope(options.vector_reuse_scope, "vectorReuseScope")
    _normalize_scope(options.chunk_dedupe_scope, "chunkDedupeScope")
    if request.embedding is not None and request.embedding.purpose:
        normalize_embedding_purpose(request.embedding.purpose)
    stride = options.chunk_chars - options.chunk_overlap_chars
    estimated = (len(request.text) + stride - 1) // stride
    if estimated > MAX_RAG_CHUNKS:
        raise ValueError(
            f"RAG text ingestion would produce approximately {estimated} chunks, "
            f"but the limit is {MAX_RAG_CHUNKS}."
        )


def _resolve_embedding_field(
    collection: str,
    policies: Sequence[VectorFieldPolicy],
    requested: str | None,
) -> str:
    if requested is not None and requested.strip():
        if any(item.name == requested for item in policies):
            return requested
        raise ValueError(
            f"Vector field {requested!r} is not defined in policy for "
            f"collection {collection!r}."
        )
    if not policies:
        raise ValueError(
            f"Collection {collection!r} does not define a vector policy "
            "for RAG ingestion."
        )
    return policies[0].name


def _normalize_scope(value: str, name: str) -> str:
    normalized = value.strip().lower() if value.strip() else "partition"
    if normalized not in _VALID_SCOPES:
        raise ValueError(
            f"RAG text ingestion {name} must be 'request', 'partition', "
            "or 'collection'."
        )
    return normalized


def _split_text(text: str, chunk_chars: int, overlap_chars: int) -> tuple[_Chunk, ...]:
    chunks: list[_Chunk] = []
    start = 0
    while start < len(text):
        while start < len(text) and text[start].isspace():
            start += 1
        if start >= len(text):
            break
        hard_end = min(len(text), start + chunk_chars)
        end = (
            hard_end
            if hard_end == len(text)
            else _find_boundary(text, start, hard_end, chunk_chars)
        )
        while end > start and text[end - 1].isspace():
            end -= 1
        if end <= start:
            end = hard_end
        chunks.append(_Chunk(start, end, text[start:end]))
        if end >= len(text):
            break
        next_start = max(start + 1, end - overlap_chars)
        start = end if next_start <= start else next_start
    return tuple(chunks)


def _find_boundary(text: str, start: int, hard_end: int, chunk_chars: int) -> int:
    minimum = start + max(1, chunk_chars // 2)
    if hard_end - minimum <= 0:
        return hard_end
    double_newline = text.rfind("\n\n", minimum, hard_end)
    if double_newline >= minimum:
        return double_newline
    newline = text.rfind("\n", minimum, hard_end)
    if newline >= minimum:
        return newline
    for index in range(hard_end - 1, minimum - 1, -1):
        if (
            text[index] in ".!?"
            and index + 1 < len(text)
            and text[index + 1].isspace()
        ):
            return index + 1
    for index in range(hard_end - 1, minimum - 1, -1):
        if text[index].isspace():
            return index
    return hard_end


def _normalize_id_segment(value: str) -> str:
    normalized = "".join(
        character
        if character.isascii()
        and (character.isalnum() or character in "-_.")
        else "-"
        for character in value.strip()
    ).strip("-_.")
    if not normalized:
        normalized = "doc"
    normalized = normalized[:128].rstrip("-_.")
    return normalized or "doc"


def _chunk_id(prefix: str, index: int, chunk_hash: str) -> str:
    return f"{prefix}-chunk-{index:04d}-{chunk_hash[:12]}"


def _sha256_hex(value: str) -> str:
    return sha256(value.encode("utf-8")).hexdigest()


def _dotnet_string(value: str) -> str:
    encoded = json.dumps(value, ensure_ascii=True, separators=(",", ":"))
    for character, escape in (
        ("<", "\\u003C"),
        (">", "\\u003E"),
        ("&", "\\u0026"),
        ("+", "\\u002B"),
        ("'", "\\u0027"),
        ("`", "\\u0060"),
    ):
        encoded = encoded.replace(character, escape)
    return _UNICODE_ESCAPE.sub(
        lambda match: "\\u" + match.group(1).upper(),
        encoded,
    )


def _canonical_json(value: object) -> str:
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, str):
        return _dotnet_string(value)
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        if value != value or value in (float("inf"), float("-inf")):
            raise ValueError("RAG canonical JSON numbers must be finite.")
        return repr(value).replace("e", "E")
    if isinstance(value, (list, tuple)):
        return "[" + ",".join(_canonical_json(item) for item in value) + "]"
    if isinstance(value, Mapping):
        entries: list[str] = []
        for key in sorted(value):
            if not isinstance(key, str):
                raise TypeError("RAG canonical JSON object keys must be strings.")
            entries.append(
                _dotnet_string(key) + ":" + _canonical_json(value[key])
            )
        return "{" + ",".join(entries) + "}"
    raise TypeError(
        f"RAG canonical JSON value {type(value).__name__!r} is not supported."
    )


def _canonical_hash(value: object) -> str:
    material = value if value is not None else {}
    return "sha256:" + sha256(_canonical_json(material).encode("utf-8")).hexdigest()


def _source_hash_input(request: RagIngestTextRequest) -> JSONObject:
    return {
        "sourceUri": request.source_uri.strip() if request.source_uri and request.source_uri.strip() else None,
        "sourceKind": request.source_kind.strip() if request.source_kind and request.source_kind.strip() else None,
        "sourceId": request.source_id.strip() if request.source_id and request.source_id.strip() else None,
        "sourceLabel": request.source_label.strip() if request.source_label and request.source_label.strip() else None,
        "sources": list(request.sources) if request.sources is not None else None,
    }


def _source_base(
    request: RagIngestTextRequest,
    document_id: str,
) -> tuple[JSONObject, ...] | None:
    if request.sources:
        return tuple(dict(item) for item in request.sources)
    if not any((request.source_uri, request.source_id, request.source_label)):
        return None
    return (
        {
            "id": request.source_id.strip() if request.source_id and request.source_id.strip() else document_id,
            "kind": request.source_kind.strip() if request.source_kind and request.source_kind.strip() else "document",
            "uri": request.source_uri.strip() if request.source_uri and request.source_uri.strip() else document_id,
            "label": request.source_label.strip() if request.source_label and request.source_label.strip() else None,
            "span": None,
        },
    )


def _chunk_sources(
    request: RagIngestTextRequest,
    document_id: str,
    chunk: _Chunk,
    index: int,
) -> tuple[JSONObject, ...] | None:
    sources = _source_base(request, document_id)
    if sources is None:
        return None
    output: list[JSONObject] = []
    for source in sources:
        span_value = source.get("span")
        span = dict(span_value) if isinstance(span_value, Mapping) else {}
        extensions = span.get("extensions")
        normalized_extensions = (
            dict(extensions) if isinstance(extensions, Mapping) else {}
        )
        normalized_extensions["chunkIndex"] = index
        span.update(
            {
                "charStart": chunk.char_start,
                "charEnd": chunk.char_end,
                "extensions": normalized_extensions,
            }
        )
        normalized = dict(source)
        normalized["id"] = source.get("id") or document_id
        normalized["kind"] = source.get("kind") or "document"
        normalized["uri"] = source.get("uri") or document_id
        normalized["span"] = span
        output.append(normalized)
    return tuple(output)


def _manifest_sources(
    request: RagIngestTextRequest,
    document_id: str,
) -> tuple[JSONObject, ...] | None:
    return _source_base(request, document_id)


def _reuse_key(field: str, embedding_hash: str) -> str:
    return field + "\n" + embedding_hash


def _duplicate_key(field: str, text_hash: str, embedding_hash: str) -> str:
    return field + "\n" + text_hash + "\n" + embedding_hash


def _cache_duplicate(
    cache: dict[str, _Duplicate],
    field: str,
    text_hash: str,
    embedding_hash: str,
    record: VyralRecord,
) -> None:
    cache.setdefault(
        _duplicate_key(field, text_hash, embedding_hash),
        _Duplicate(
            record.id,
            record.partition_key,
            record.etag,
            record.revision,
        ),
    )


def _chunk_result(
    index: int,
    record_id: str,
    partition_key: str,
    chunk: _Chunk,
    text_hash: str,
    embedding_hash: str,
    action: str,
    embedding_action: str,
    **values: object,
) -> RagIngestChunkResult:
    return RagIngestChunkResult(
        index=index,
        id=record_id,
        partition_key=partition_key,
        char_start=chunk.char_start,
        char_end=chunk.char_end,
        text_length=len(chunk.text),
        text_hash=text_hash,
        embedding_text_hash=embedding_hash,
        action=action,
        embedding_action=embedding_action,
        reused_vector_from_id=_optional_str(values.get("reused_vector_from_id")),
        reused_vector_from_partition_key=_optional_str(
            values.get("reused_vector_from_partition_key")
        ),
        deduplicated_from_id=_optional_str(values.get("deduplicated_from_id")),
        deduplicated_from_partition_key=_optional_str(
            values.get("deduplicated_from_partition_key")
        ),
        etag=_optional_str(values.get("etag")),
        revision=_optional_int(values.get("revision")),
    )


def _optional_str(value: object) -> str | None:
    return value if isinstance(value, str) else None


def _optional_int(value: object) -> int | None:
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def _metadata_int(record: VyralRecord, key: str) -> int | None:
    return _optional_int((record.metadata or {}).get(key))


def _metadata_str(record: VyralRecord, key: str) -> str | None:
    return _optional_str((record.metadata or {}).get(key))


def _manifest_current(
    record: VyralRecord | None,
    manifest_hash: str,
    document_id: str,
    record_type: str,
    provider: EmbeddingProvider,
) -> bool:
    metadata = record.metadata if record is not None else None
    return (
        record is not None
        and record.type == "rag.manifest"
        and record.schema_version == "v1"
        and isinstance(metadata, Mapping)
        and metadata.get("documentId") == document_id
        and metadata.get("manifestHash") == manifest_hash
        and metadata.get("chunkRecordType") == record_type
        and metadata.get("embeddingProvider") == provider.provider_id
        and metadata.get("embeddingModel") == provider.model_id
        and metadata.get("embeddingDimensions") == provider.dimensions
    )


def _hash_comparison(
    kind: str,
    expected: str | None,
    actual: str | None,
) -> RagIngestHashComparison:
    normalized = expected.strip() if expected and expected.strip() else None
    if normalized is None:
        return RagIngestHashComparison(
            kind=kind,
            expected_hash=None,
            actual_hash=actual,
            compared=False,
            matches=False,
            status="not_provided",
        )
    matches = actual is not None and normalized == actual
    return RagIngestHashComparison(
        kind=kind,
        expected_hash=normalized,
        actual_hash=actual,
        compared=True,
        matches=matches,
        status=(
            "actual_missing"
            if actual is None
            else "matched"
            if matches
            else "drifted"
        ),
    )


def _action_summary(
    chunks: Sequence[RagIngestChunkResult],
    stale: Sequence[RagIngestStaleDeleteResult],
) -> RagIngestActionSummary:
    actions = {
        "created": 0,
        "updated": 0,
        "reused": 0,
        "deduplicated": 0,
    }
    embeddings = {
        "generated": 0,
        "reused": 0,
        "unchanged": 0,
        "deduplicated": 0,
    }
    ids: dict[str, list[str]] = {
        "created": [],
        "updated": [],
        "reused": [],
        "deduplicated": [],
    }
    for chunk in sorted(chunks, key=lambda item: item.index):
        actions[chunk.action] = actions.get(chunk.action, 0) + 1
        embeddings[chunk.embedding_action] = (
            embeddings.get(chunk.embedding_action, 0) + 1
        )
        ids.setdefault(chunk.action, []).append(chunk.id)
    return RagIngestActionSummary(
        action_counts=actions,
        embedding_action_counts=embeddings,
        created_ids=tuple(ids["created"]),
        updated_ids=tuple(ids["updated"]),
        reused_ids=tuple(ids["reused"]),
        deduplicated_ids=tuple(ids["deduplicated"]),
        stale_delete_ids=tuple(
            item.id
            for item in sorted(
                stale,
                key=lambda item: (item.partition_key, item.id),
            )
        ),
    )


def _utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        raise ValueError("RAG ingestion clock must return an offset-aware datetime.")
    return value.astimezone(timezone.utc)
