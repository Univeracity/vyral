from __future__ import annotations

from collections.abc import Awaitable, Callable
from datetime import datetime, timezone
from hashlib import sha256
from typing import Any, Mapping, Protocol, cast
from urllib.parse import quote

from ..contracts import JSONValue
from ..embeddings import EmbeddingService
from ..graph import GraphService
from ..local import (
    AsyncSQLiteRecordStore,
    FileObjectStore,
    ObjectDeleteRequest,
    ObjectReadRequest,
    ObjectWriteRequest,
    RecordCollectionPolicy,
    VyralRecord,
)
from ..rag import RagIngestionService
from ..retrieval import RetrievalEvaluationService
from .models import (
    ExecutionArtifactWrite,
    ExecutionCheckpointWrite,
    ExecutionHandlerDescriptor,
    ExecutionPluginDescriptor,
    ExecutionRunResult,
    ExecutionRunUpdate,
)
from .worker import (
    DelegateExecutionHandler,
    ExecutionRunContext,
    StaticExecutionPlugin,
)


BUILTIN_JOB_PLUGIN_ID = "vyral.runtime.jobs"


class RuntimeJobHandlerIds:
    ARTIFACT_RECORD_INGEST = "vyral.artifacts.record-ingest"
    COLLECTION_CREATE = "vyral.collections.create"
    COLLECTION_DELETE = "vyral.collections.delete"
    EMBEDDINGS = "vyral.embeddings.generate"
    RECORD_BATCH_UPSERT = "vyral.records.batch-upsert"
    COLLECTION_IMPORT = "vyral.records.import-collection"
    RAG_INGEST_TEXT = "vyral.rag.ingest-text"
    RAG_INGEST_BATCH = "vyral.rag.ingest-batch"
    RETRIEVAL_EVALUATE = "vyral.retrieval.evaluate"
    RETRIEVAL_COMPARE = "vyral.retrieval.compare"
    GRAPH_IMPORT = "vyral.graph.import"
    GRAPH_INSPECT = "vyral.graph.inspect"
    GRAPH_DOCTOR = "vyral.graph.doctor"


class _WireResult(Protocol):
    def to_dict(self) -> Mapping[str, Any]: ...


JobOperation = Callable[
    [Mapping[str, Any]], Awaitable[JSONValue]
]


def create_runtime_job_plugin(
    *,
    records: AsyncSQLiteRecordStore,
    objects: FileObjectStore,
    embeddings: EmbeddingService,
    retrieval_evaluation: RetrievalEvaluationService,
    rag_ingestion: RagIngestionService,
    graph: GraphService,
) -> StaticExecutionPlugin:
    """Create the portable domain-job projection over local services."""

    async def embedding(payload: Mapping[str, Any]) -> JSONValue:
        return _wire(
            await embeddings.aembed(_request(payload))
        )

    async def record_batch(payload: Mapping[str, Any]) -> JSONValue:
        request = _request(payload)
        records_value = request.get("records")
        if not isinstance(records_value, list):
            raise TypeError(
                "Record batch job request.records must be an array."
            )
        preconditions = request.get("preconditions", ())
        if not isinstance(preconditions, (list, tuple)):
            raise TypeError(
                "Record batch job request.preconditions must be an "
                "array."
            )
        return _wire(
            await records.upsert_records(
                _collection(payload),
                records_value,
                preconditions=preconditions,
                continue_on_error=_bool(
                    request.get("continueOnError"),
                    "continueOnError",
                ),
            )
        )

    async def collection_import(
        payload: Mapping[str, Any],
    ) -> JSONValue:
        return _wire(
            await records.import_collection(
                _collection(payload), _request(payload)
            )
        )

    async def collection_create(payload: Mapping[str, Any]) -> JSONValue:
        policy = RecordCollectionPolicy.from_value(_request(payload))
        await records.create_collection(policy)
        return cast(JSONValue, policy.to_dict())

    async def collection_delete(payload: Mapping[str, Any]) -> JSONValue:
        collection = _collection(payload)
        await records.delete_collection(collection)
        return cast(
            JSONValue,
            {"collection": collection, "deleted": True},
        )

    async def rag_text(payload: Mapping[str, Any]) -> JSONValue:
        return _wire(
            await rag_ingestion.aingest_text(
                _collection(payload), _request(payload)
            )
        )

    async def rag_batch(payload: Mapping[str, Any]) -> JSONValue:
        return _wire(
            await rag_ingestion.aingest_text_batch(
                _collection(payload), _request(payload)
            )
        )

    async def retrieval_evaluate(
        payload: Mapping[str, Any],
    ) -> JSONValue:
        return _wire(
            await retrieval_evaluation.aevaluate(
                _request(payload)
            )
        )

    async def retrieval_compare(
        payload: Mapping[str, Any],
    ) -> JSONValue:
        return _wire(
            await retrieval_evaluation.acompare(
                _request(payload)
            )
        )

    async def graph_import(payload: Mapping[str, Any]) -> JSONValue:
        return _wire(
            await graph.aimport_envelope(
                _collection(payload), _request(payload)
            )
        )

    async def graph_inspect(payload: Mapping[str, Any]) -> JSONValue:
        result = await graph.ainspect(
            _collection(payload), _request(payload)
        )
        if result is None:
            raise LookupError("Graph collection was not found.")
        return _wire(result)

    async def graph_doctor(payload: Mapping[str, Any]) -> JSONValue:
        result = await graph.adoctor(
            _collection(payload), _request(payload)
        )
        if result is None:
            raise LookupError("Graph collection was not found.")
        return _wire(result)

    async def artifact_record_ingest(
        payload: Mapping[str, Any],
    ) -> JSONValue:
        manifest = _mapping_value(payload.get("manifest"), "manifest")
        collection = _required_text(manifest.get("collection"), "collection")
        record = VyralRecord.from_value(
            _mapping_value(manifest.get("record"), "record")
        )
        descriptor = _mapping_value(
            manifest.get("artifact"), "artifact descriptor"
        )
        container = _required_text(descriptor.get("container"), "container")
        key = _required_text(descriptor.get("key"), "key")
        content_type = descriptor.get("contentType")
        if content_type is not None and not isinstance(content_type, str):
            raise TypeError("artifact contentType must be a string.")
        metadata = _mapping_value(
            descriptor.get("metadata", {}), "artifact metadata"
        )
        if not all(
            isinstance(name, str) and isinstance(value, str)
            for name, value in metadata.items()
        ):
            raise TypeError(
                "artifact metadata must contain string values."
            )
        if manifest.get("externalContext") is not None:
            raise ValueError(
                "External-context proof verification is not configured."
            )

        staging_container = _required_text(
            payload.get("stagingContainer"), "stagingContainer"
        )
        staging_key = _required_text(
            payload.get("stagingKey"), "stagingKey"
        )
        expected_hash = _required_text(
            payload.get("contentHash"), "contentHash"
        )
        staged = await objects.aget_object(
            ObjectReadRequest(staging_container, staging_key)
        )
        if staged is None:
            raise ValueError("Staged artifact content is missing.")
        try:
            if staged.info.content_hash != expected_hash:
                raise ValueError(
                    "Staged artifact content hash does not match its "
                    "admission payload."
                )
            artifact_content = await objects.executor.run(staged.read)
            staging_etag = staged.info.etag
        finally:
            staged.close()
        actual_hash = "sha256:" + sha256(artifact_content).hexdigest()
        if actual_hash != expected_hash:
            raise ValueError(
                "Staged artifact bytes do not match their admission hash."
            )
        if await records.get_collection_policy(collection) is None:
            raise LookupError(f"Collection {collection!r} was not found.")

        existing = await objects.aget_object(
            ObjectReadRequest(container, key)
        )
        if existing is not None:
            try:
                info = existing.info
            finally:
                existing.close()
            if info.content_hash != actual_hash:
                raise ValueError(
                    "Artifact object already exists with different content."
                )
        else:
            info = await objects.aput_object(
                ObjectWriteRequest(
                    container,
                    key,
                    artifact_content,
                    content_type=content_type,
                    metadata=cast(Mapping[str, str], metadata),
                    if_none_match="*",
                )
            )
        await records.upsert_record(collection, record)
        try:
            await objects.adelete_object(
                ObjectDeleteRequest(
                    staging_container, staging_key, staging_etag
                )
            )
        except ValueError:
            pass
        record_uri = (
            f"/collections/{quote(collection, safe='')}/records/"
            f"{quote(record.partition_key, safe='')}/"
            f"{quote(record.id, safe='')}"
        )
        return cast(
            JSONValue,
            {
                "accepted": True,
                "collection": collection,
                "recordId": record.id,
                "partitionKey": record.partition_key,
                "recordUri": record_uri,
                "artifact": info.to_dict(),
                "externalContextVerified": False,
                "receivedAt": datetime.now(timezone.utc)
                .isoformat()
                .replace("+00:00", "Z"),
            },
        )

    definitions: tuple[
        tuple[str, str, JobOperation], ...
    ] = (
        (
            RuntimeJobHandlerIds.ARTIFACT_RECORD_INGEST,
            "Ingest an artifact and its record",
            artifact_record_ingest,
        ),
        (
            RuntimeJobHandlerIds.COLLECTION_CREATE,
            "Create a record collection",
            collection_create,
        ),
        (
            RuntimeJobHandlerIds.COLLECTION_DELETE,
            "Delete a record collection",
            collection_delete,
        ),
        (
            RuntimeJobHandlerIds.EMBEDDINGS,
            "Generate embeddings",
            embedding,
        ),
        (
            RuntimeJobHandlerIds.RECORD_BATCH_UPSERT,
            "Batch upsert records",
            record_batch,
        ),
        (
            RuntimeJobHandlerIds.COLLECTION_IMPORT,
            "Import a collection snapshot",
            collection_import,
        ),
        (
            RuntimeJobHandlerIds.RAG_INGEST_TEXT,
            "Ingest RAG text",
            rag_text,
        ),
        (
            RuntimeJobHandlerIds.RAG_INGEST_BATCH,
            "Ingest a RAG text batch",
            rag_batch,
        ),
        (
            RuntimeJobHandlerIds.RETRIEVAL_EVALUATE,
            "Evaluate retrieval",
            retrieval_evaluate,
        ),
        (
            RuntimeJobHandlerIds.RETRIEVAL_COMPARE,
            "Compare retrieval evaluations",
            retrieval_compare,
        ),
        (
            RuntimeJobHandlerIds.GRAPH_IMPORT,
            "Import a graph",
            graph_import,
        ),
        (
            RuntimeJobHandlerIds.GRAPH_INSPECT,
            "Inspect a graph",
            graph_inspect,
        ),
        (
            RuntimeJobHandlerIds.GRAPH_DOCTOR,
            "Diagnose a graph",
            graph_doctor,
        ),
    )
    handlers = tuple(
        DelegateExecutionHandler(
            ExecutionHandlerDescriptor(
                handler_id=handler_id,
                display_name=display_name,
                plugin_id=BUILTIN_JOB_PLUGIN_ID,
                description=(
                    "Built-in durable projection over the corresponding "
                    "embedded Vyral service."
                ),
                max_attempts=3,
                concurrency_key=handler_id,
                tags={"owner": "vyral-runtime"},
            ),
            _job_handler(operation),
        )
        for handler_id, display_name, operation in definitions
    )
    return StaticExecutionPlugin(
        ExecutionPluginDescriptor(
            plugin_id=BUILTIN_JOB_PLUGIN_ID,
            name="Vyral embedded runtime jobs",
            version="1.0.0",
            handlers=tuple(
                handler.descriptor for handler in handlers
            ),
        ),
        handlers,
    )


def _job_handler(
    operation: JobOperation,
) -> Callable[
    [ExecutionRunContext], Awaitable[ExecutionRunResult]
]:
    async def execute(
        context: ExecutionRunContext,
    ) -> ExecutionRunResult:
        if _cancelled(context):
            return ExecutionRunResult.cancelled_result()
        await context.report(
            ExecutionRunUpdate(
                progress=0.05,
                current_step="validate",
            )
        )
        try:
            payload = _payload(context.run.payload)
            result = await operation(payload)
        except (TypeError, ValueError, LookupError) as exc:
            return ExecutionRunResult.failed_result(
                "validation", str(exc)[:4_096]
            )
        if _cancelled(context):
            return ExecutionRunResult.cancelled_result(result)
        counters = _counters(result)
        await context.report(
            ExecutionRunUpdate(
                progress=0.9,
                current_step="persist-result",
                requested=counters["requested"],
                attempted=counters["attempted"],
                succeeded=counters["succeeded"],
                failed=counters["failed"],
            )
        )
        await context.put_checkpoint(
            ExecutionCheckpointWrite(
                key="completed",
                content={
                    "status": "completed",
                    **{
                        key: value
                        for key, value in counters.items()
                        if value is not None
                    },
                },
            )
        )
        await context.put_artifact(
            ExecutionArtifactWrite(
                name="result",
                kind="json",
                content=result,
                metadata={"role": "execution-result"},
            )
        )
        return ExecutionRunResult.succeeded_result(result)

    return execute


def _cancelled(context: ExecutionRunContext) -> bool:
    return bool(
        getattr(context, "cancellation_requested", False)
        or context.run.cancellation_requested
    )


def _payload(value: JSONValue) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise TypeError(
            "Built-in runtime job payload must be an object."
        )
    return cast(Mapping[str, Any], value)


def _request(payload: Mapping[str, Any]) -> Mapping[str, Any]:
    value = payload.get("request")
    if not isinstance(value, Mapping):
        raise TypeError(
            "Built-in runtime job payload.request must be an object."
        )
    return cast(Mapping[str, Any], value)


def _collection(payload: Mapping[str, Any]) -> str:
    value = payload.get("collection")
    if not isinstance(value, str) or not value.strip():
        raise TypeError(
            "Built-in runtime job payload.collection is required."
        )
    return value.strip()


def _mapping_value(value: object, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise TypeError(f"{name} must be an object.")
    return cast(Mapping[str, Any], value)


def _required_text(value: object, name: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise TypeError(f"{name} is required.")
    return value.strip()


def _bool(value: object, name: str) -> bool:
    if value is None:
        return False
    if not isinstance(value, bool):
        raise TypeError(f"{name} must be a boolean.")
    return value


def _wire(value: _WireResult | Mapping[str, Any]) -> JSONValue:
    selected: object = (
        value.to_dict() if hasattr(value, "to_dict") else value
    )
    return cast(JSONValue, selected)


def _counters(result: JSONValue) -> dict[str, int | None]:
    if not isinstance(result, Mapping):
        return {
            "requested": None,
            "attempted": None,
            "succeeded": None,
            "failed": None,
        }

    def integer(name: str) -> int | None:
        value = result.get(name)
        return (
            value
            if isinstance(value, int)
            and not isinstance(value, bool)
            else None
        )

    return {
        "requested": integer("requested"),
        "attempted": integer("attempted"),
        "succeeded": integer("succeeded"),
        "failed": integer("failed"),
    }


__all__ = [
    "BUILTIN_JOB_PLUGIN_ID",
    "RuntimeJobHandlerIds",
    "create_runtime_job_plugin",
]
