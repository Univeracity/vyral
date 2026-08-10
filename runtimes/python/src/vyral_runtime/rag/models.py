from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Mapping

from ..local.models import JSONObject
from ..retrieval import EmbeddingOptions


MAX_RAG_TEXT_LENGTH = 2_000_000
MAX_RAG_CHUNK_CHARS = 50_000
MAX_RAG_CHUNKS = 2_000
MAX_RAG_BATCH_ITEMS = 100


def _text(value: object, name: str, default: str | None = None) -> str:
    if value is None and default is not None:
        return default
    if not isinstance(value, str):
        raise TypeError(f"{name} must be a string")
    return value


def _optional_text(value: object, name: str) -> str | None:
    return None if value is None else _text(value, name)


def _integer(value: object, name: str, default: int) -> int:
    if value is None:
        return default
    if isinstance(value, bool) or not isinstance(value, int):
        raise TypeError(f"{name} must be an integer")
    return value


def _boolean(value: object, name: str, default: bool) -> bool:
    if value is None:
        return default
    if not isinstance(value, bool):
        raise TypeError(f"{name} must be a boolean")
    return value


def _mapping(value: object, name: str) -> JSONObject | None:
    if value is None:
        return None
    if not isinstance(value, Mapping):
        raise TypeError(f"{name} must be an object")
    return {str(key): item for key, item in value.items()}


@dataclass(frozen=True)
class RagIngestionOptions:
    chunk_chars: int = 1200
    chunk_overlap_chars: int = 150
    dry_run: bool = False
    replace_document_chunks: bool = False
    skip_unchanged_chunks: bool = False
    reuse_existing_chunk_vectors: bool = False
    vector_reuse_scope: str = "partition"
    deduplicate_existing_chunks: bool = False
    chunk_dedupe_scope: str = "partition"
    persist_manifest: bool = False
    manifest_id: str | None = None
    expected_plan_hash: str | None = None
    expected_manifest_hash: str | None = None
    include_trace: bool = False

    @classmethod
    def from_value(
        cls,
        value: RagIngestionOptions | Mapping[str, Any] | None,
    ) -> RagIngestionOptions:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("RAG ingestion options must be an object")
        return cls(
            chunk_chars=_integer(
                value.get("chunkChars"),
                "RAG ingestion chunkChars",
                1200,
            ),
            chunk_overlap_chars=_integer(
                value.get("chunkOverlapChars"),
                "RAG ingestion chunkOverlapChars",
                150,
            ),
            dry_run=_boolean(value.get("dryRun"), "RAG ingestion dryRun", False),
            replace_document_chunks=_boolean(
                value.get("replaceDocumentChunks"),
                "RAG ingestion replaceDocumentChunks",
                False,
            ),
            skip_unchanged_chunks=_boolean(
                value.get("skipUnchangedChunks"),
                "RAG ingestion skipUnchangedChunks",
                False,
            ),
            reuse_existing_chunk_vectors=_boolean(
                value.get("reuseExistingChunkVectors"),
                "RAG ingestion reuseExistingChunkVectors",
                False,
            ),
            vector_reuse_scope=_text(
                value.get("vectorReuseScope"),
                "RAG ingestion vectorReuseScope",
                "partition",
            ),
            deduplicate_existing_chunks=_boolean(
                value.get("deduplicateExistingChunks"),
                "RAG ingestion deduplicateExistingChunks",
                False,
            ),
            chunk_dedupe_scope=_text(
                value.get("chunkDedupeScope"),
                "RAG ingestion chunkDedupeScope",
                "partition",
            ),
            persist_manifest=_boolean(
                value.get("persistManifest"),
                "RAG ingestion persistManifest",
                False,
            ),
            manifest_id=_optional_text(
                value.get("manifestId"),
                "RAG ingestion manifestId",
            ),
            expected_plan_hash=_optional_text(
                value.get("expectedPlanHash"),
                "RAG ingestion expectedPlanHash",
            ),
            expected_manifest_hash=_optional_text(
                value.get("expectedManifestHash"),
                "RAG ingestion expectedManifestHash",
            ),
            include_trace=_boolean(
                value.get("includeTrace"),
                "RAG ingestion includeTrace",
                False,
            ),
        )


@dataclass(frozen=True)
class RagIngestTextRequest:
    partition_key: str
    text: str
    document_id: str | None = None
    id_prefix: str | None = None
    type: str = "rag.chunk"
    schema_version: str | None = None
    content_field: str = "text"
    embedding: EmbeddingOptions | None = None
    metadata: JSONObject | None = None
    source_uri: str | None = None
    source_kind: str | None = None
    source_id: str | None = None
    source_label: str | None = None
    sources: tuple[JSONObject, ...] | None = None
    options: RagIngestionOptions = field(default_factory=RagIngestionOptions)

    @classmethod
    def from_value(
        cls,
        value: RagIngestTextRequest | Mapping[str, Any],
    ) -> RagIngestTextRequest:
        if isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("RAG text ingestion request must be an object")
        raw_sources = value.get("sources")
        if raw_sources is not None and (
            not isinstance(raw_sources, (list, tuple))
            or not all(isinstance(item, Mapping) for item in raw_sources)
        ):
            raise TypeError("RAG ingestion sources must be an array of objects")
        return cls(
            document_id=_optional_text(
                value.get("documentId"),
                "RAG ingestion documentId",
            ),
            id_prefix=_optional_text(value.get("idPrefix"), "RAG ingestion idPrefix"),
            partition_key=_text(
                value.get("partitionKey"),
                "RAG ingestion partitionKey",
                "",
            ),
            type=_text(value.get("type"), "RAG ingestion type", "rag.chunk"),
            schema_version=_optional_text(
                value.get("schemaVersion"),
                "RAG ingestion schemaVersion",
            ),
            text=_text(value.get("text"), "RAG ingestion text", ""),
            content_field=_text(
                value.get("contentField"),
                "RAG ingestion contentField",
                "text",
            ),
            embedding=EmbeddingOptions.from_value(value.get("embedding")),
            metadata=_mapping(value.get("metadata"), "RAG ingestion metadata"),
            source_uri=_optional_text(
                value.get("sourceUri"),
                "RAG ingestion sourceUri",
            ),
            source_kind=_optional_text(
                value.get("sourceKind"),
                "RAG ingestion sourceKind",
            ),
            source_id=_optional_text(
                value.get("sourceId"),
                "RAG ingestion sourceId",
            ),
            source_label=_optional_text(
                value.get("sourceLabel"),
                "RAG ingestion sourceLabel",
            ),
            sources=(
                tuple({str(key): item for key, item in source.items()} for source in raw_sources)
                if raw_sources is not None
                else None
            ),
            options=RagIngestionOptions.from_value(value.get("options")),
        )


@dataclass(frozen=True)
class RagIngestTextBatchRequest:
    items: tuple[RagIngestTextRequest, ...]
    continue_on_error: bool = False

    @classmethod
    def from_value(
        cls,
        value: RagIngestTextBatchRequest | Mapping[str, Any],
    ) -> RagIngestTextBatchRequest:
        if isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("RAG text batch ingestion request must be an object")
        raw_items = value.get("items", [])
        if not isinstance(raw_items, (list, tuple)):
            raise TypeError("RAG text batch ingestion items must be an array")
        return cls(
            items=tuple(RagIngestTextRequest.from_value(item) for item in raw_items),
            continue_on_error=_boolean(
                value.get("continueOnError"),
                "RAG text batch ingestion continueOnError",
                False,
            ),
        )


@dataclass(frozen=True)
class RagIngestHashComparison:
    kind: str
    expected_hash: str | None
    actual_hash: str | None
    compared: bool
    matches: bool
    status: str

    def to_dict(self) -> JSONObject:
        return {
            "kind": self.kind,
            "expectedHash": self.expected_hash,
            "actualHash": self.actual_hash,
            "compared": self.compared,
            "matches": self.matches,
            "status": self.status,
        }


@dataclass(frozen=True)
class RagIngestChunkResult:
    index: int
    id: str
    partition_key: str
    char_start: int
    char_end: int
    text_length: int
    text_hash: str
    embedding_text_hash: str
    action: str
    embedding_action: str
    reused_vector_from_id: str | None = None
    reused_vector_from_partition_key: str | None = None
    deduplicated_from_id: str | None = None
    deduplicated_from_partition_key: str | None = None
    etag: str | None = None
    revision: int | None = None

    def to_dict(self) -> JSONObject:
        return {
            "index": self.index,
            "id": self.id,
            "partitionKey": self.partition_key,
            "charStart": self.char_start,
            "charEnd": self.char_end,
            "textLength": self.text_length,
            "textHash": self.text_hash,
            "embeddingTextHash": self.embedding_text_hash,
            "action": self.action,
            "embeddingAction": self.embedding_action,
            "reusedVectorFromId": self.reused_vector_from_id,
            "reusedVectorFromPartitionKey": self.reused_vector_from_partition_key,
            "deduplicatedFromId": self.deduplicated_from_id,
            "deduplicatedFromPartitionKey": self.deduplicated_from_partition_key,
            "etag": self.etag,
            "revision": self.revision,
        }


@dataclass(frozen=True)
class RagIngestStaleDeleteResult:
    id: str
    partition_key: str
    chunk_index: int | None
    text_hash: str | None
    etag: str | None
    revision: int | None

    def to_dict(self) -> JSONObject:
        return {
            "id": self.id,
            "partitionKey": self.partition_key,
            "chunkIndex": self.chunk_index,
            "textHash": self.text_hash,
            "etag": self.etag,
            "revision": self.revision,
        }


@dataclass(frozen=True)
class RagIngestActionSummary:
    action_counts: Mapping[str, int]
    embedding_action_counts: Mapping[str, int]
    created_ids: tuple[str, ...]
    updated_ids: tuple[str, ...]
    reused_ids: tuple[str, ...]
    deduplicated_ids: tuple[str, ...]
    stale_delete_ids: tuple[str, ...]

    def to_dict(self) -> JSONObject:
        return {
            "actionCounts": dict(self.action_counts),
            "embeddingActionCounts": dict(self.embedding_action_counts),
            "createdIds": list(self.created_ids),
            "updatedIds": list(self.updated_ids),
            "reusedIds": list(self.reused_ids),
            "deduplicatedIds": list(self.deduplicated_ids),
            "staleDeleteIds": list(self.stale_delete_ids),
        }


@dataclass(frozen=True)
class RagIngestTextResult:
    collection: str
    document_id: str
    partition_key: str
    embedding_field: str
    embedding_provider: str
    embedding_model: str
    embedding_purpose: str
    dimensions: int
    text_length: int
    text_hash: str
    plan_hash: str
    chunk_count: int
    dry_run: bool
    deleted_stale_count: int
    created_count: int
    updated_count: int
    reused_count: int
    vector_generated_count: int
    vector_reused_count: int
    deduplicated_count: int
    manifest_id: str | None
    manifest_hash: str | None
    manifest_action: str | None
    manifest_etag: str | None
    manifest_revision: int | None
    action_summary: RagIngestActionSummary
    plan_hash_comparison: RagIngestHashComparison
    manifest_hash_comparison: RagIngestHashComparison
    stale_deletes: tuple[RagIngestStaleDeleteResult, ...]
    chunks: tuple[RagIngestChunkResult, ...]
    trace: JSONObject | None = None

    def to_dict(self) -> JSONObject:
        return {
            "collection": self.collection,
            "documentId": self.document_id,
            "partitionKey": self.partition_key,
            "embeddingField": self.embedding_field,
            "embeddingProvider": self.embedding_provider,
            "embeddingModel": self.embedding_model,
            "embeddingPurpose": self.embedding_purpose,
            "dimensions": self.dimensions,
            "textLength": self.text_length,
            "textHash": self.text_hash,
            "planHash": self.plan_hash,
            "chunkCount": self.chunk_count,
            "dryRun": self.dry_run,
            "deletedStaleCount": self.deleted_stale_count,
            "createdCount": self.created_count,
            "updatedCount": self.updated_count,
            "reusedCount": self.reused_count,
            "vectorGeneratedCount": self.vector_generated_count,
            "vectorReusedCount": self.vector_reused_count,
            "deduplicatedCount": self.deduplicated_count,
            "manifestId": self.manifest_id,
            "manifestHash": self.manifest_hash,
            "manifestAction": self.manifest_action,
            "manifestEtag": self.manifest_etag,
            "manifestRevision": self.manifest_revision,
            "actionSummary": self.action_summary.to_dict(),
            "planHashComparison": self.plan_hash_comparison.to_dict(),
            "manifestHashComparison": self.manifest_hash_comparison.to_dict(),
            "staleDeletes": [item.to_dict() for item in self.stale_deletes],
            "chunks": [item.to_dict() for item in self.chunks],
            "trace": self.trace,
        }


@dataclass(frozen=True)
class RagIngestTextBatchItemResult:
    index: int
    document_id: str | None
    partition_key: str | None
    status: str
    result: RagIngestTextResult | None = None
    error: str | None = None

    def to_dict(self) -> JSONObject:
        return {
            "index": self.index,
            "documentId": self.document_id,
            "partitionKey": self.partition_key,
            "status": self.status,
            "result": self.result.to_dict() if self.result is not None else None,
            "error": self.error,
        }


@dataclass(frozen=True)
class RagIngestTextBatchResult:
    collection: str
    requested: int
    attempted: int
    succeeded: int
    failed: int
    stopped_on_error: bool
    text_length: int
    chunk_count: int
    deleted_stale_count: int
    created_count: int
    updated_count: int
    reused_count: int
    vector_generated_count: int
    vector_reused_count: int
    deduplicated_count: int
    items: tuple[RagIngestTextBatchItemResult, ...]

    def to_dict(self) -> JSONObject:
        return {
            "collection": self.collection,
            "requested": self.requested,
            "attempted": self.attempted,
            "succeeded": self.succeeded,
            "failed": self.failed,
            "stoppedOnError": self.stopped_on_error,
            "textLength": self.text_length,
            "chunkCount": self.chunk_count,
            "deletedStaleCount": self.deleted_stale_count,
            "createdCount": self.created_count,
            "updatedCount": self.updated_count,
            "reusedCount": self.reused_count,
            "vectorGeneratedCount": self.vector_generated_count,
            "vectorReusedCount": self.vector_reused_count,
            "deduplicatedCount": self.deduplicated_count,
            "items": [item.to_dict() for item in self.items],
        }
