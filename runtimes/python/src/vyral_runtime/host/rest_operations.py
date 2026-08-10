from __future__ import annotations

import asyncio
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, fields, is_dataclass
from datetime import datetime, timezone
from email import policy as email_policy
from email.parser import BytesParser
from hashlib import sha256
import json
from typing import Any, cast
from urllib.parse import quote
from uuid import uuid4

from .._datetime import parse_iso_datetime
from .._version import CONTRACT_VERSION, RUNTIME_VERSION
from ..admission import create_admission_receipt
from ..canonical import (
    CanonicalArchiveRestoreRequest,
    CanonicalDocumentQuery,
    CanonicalMigration,
    CanonicalOutboxLeaseRequest,
    CanonicalOutboxLeaseRenewalRequest,
    CanonicalOutboxNackRequest,
    CanonicalOutboxQuery,
    CanonicalOutboxReplayRequest,
    CanonicalRestoreRequest,
    CanonicalTransactionRequest,
)
from ..contracts import JSONValue
from ..embeddings import EmbeddingProviderRegistry
from ..execution import (
    BUILTIN_JOB_PLUGIN_ID,
    ExecutionArtifactWrite,
    ExecutionCheckpointWrite,
    ExecutionExternalEventRequest,
    ExecutionHistoryQuery,
    ExecutionMaintenanceDispatchReconcileRequest,
    ExecutionMaintenancePruneRequest,
    ExecutionRun,
    ExecutionRunQuery,
    ExecutionRunRequest,
    ExecutionRunResult,
    ExecutionRunUpdate,
    ExecutionScope,
    ExecutionTraceEvent,
    ExecutionWorkerLease,
    ExecutionWorkerWaitRequest,
    RuntimeJobHandlerIds,
    TERMINAL_RUN_STATUSES,
)
from ..graph import (
    get_graph_provider_shape,
    list_graph_provider_shapes,
)
from ..local import (
    CollectionExportRequest,
    ObjectDeleteRequest,
    ObjectListRequest,
    ObjectReadRequest,
    ObjectWriteRequest,
    RecordCollectionPolicy,
    RecordWritePrecondition,
    TraceExportRequest,
    TracePruneRequest,
    VyralRecord,
)
from ..retrieval import get_retrieval_profiles
from ..runtime import VyralRuntime
from .rest_registry import (
    REST_OPERATION_FAMILIES,
    RestOperationFamily,
    rest_operation_family,
)


class RestNotFoundError(LookupError):
    pass


class RestOperationUnavailableError(RuntimeError):
    pass


@dataclass(frozen=True)
class _ExecutionWorkerLeaseCredentials:
    lease_key: str
    lease_token: str
    worker_id: str


@dataclass(frozen=True)
class RestOperationResult:
    status: int = 200
    body: object | None = None
    content_type: str = "application/json"
    headers: tuple[tuple[bytes, bytes], ...] = ()


_EMBEDDING_HANDLERS = frozenset({RuntimeJobHandlerIds.EMBEDDINGS})
_RECORD_HANDLERS = frozenset(
    {
        RuntimeJobHandlerIds.COLLECTION_IMPORT,
        RuntimeJobHandlerIds.RECORD_BATCH_UPSERT,
    }
)
_RAG_HANDLERS = frozenset(
    {
        RuntimeJobHandlerIds.RAG_INGEST_TEXT,
        RuntimeJobHandlerIds.RAG_INGEST_BATCH,
    }
)
_RETRIEVAL_HANDLERS = frozenset(
    {
        RuntimeJobHandlerIds.RETRIEVAL_EVALUATE,
        RuntimeJobHandlerIds.RETRIEVAL_COMPARE,
    }
)
_GRAPH_HANDLERS = frozenset(
    {
        RuntimeJobHandlerIds.GRAPH_IMPORT,
        RuntimeJobHandlerIds.GRAPH_INSPECT,
        RuntimeJobHandlerIds.GRAPH_DOCTOR,
    }
)

_JOB_STARTS = {
    "startEmbeddingJob": RuntimeJobHandlerIds.EMBEDDINGS,
    "startCollectionImportJob": RuntimeJobHandlerIds.COLLECTION_IMPORT,
    "startRecordBatchUpsertJob": RuntimeJobHandlerIds.RECORD_BATCH_UPSERT,
    "startRagIngestionTextJob": RuntimeJobHandlerIds.RAG_INGEST_TEXT,
    "startRagIngestionBatchJob": RuntimeJobHandlerIds.RAG_INGEST_BATCH,
    "startRetrievalEvaluationJob": RuntimeJobHandlerIds.RETRIEVAL_EVALUATE,
    "startRetrievalEvaluationComparisonJob": (
        RuntimeJobHandlerIds.RETRIEVAL_COMPARE
    ),
    "startGraphImportJob": RuntimeJobHandlerIds.GRAPH_IMPORT,
    "startGraphInspectionJob": RuntimeJobHandlerIds.GRAPH_INSPECT,
    "startGraphDoctorJob": RuntimeJobHandlerIds.GRAPH_DOCTOR,
}
_AGGREGATE_JOB_STARTS = {
    "importCollection": RuntimeJobHandlerIds.COLLECTION_IMPORT,
    "upsertRecords": RuntimeJobHandlerIds.RECORD_BATCH_UPSERT,
    "importGraphEnvelope": RuntimeJobHandlerIds.GRAPH_IMPORT,
    "ingestRagText": RuntimeJobHandlerIds.RAG_INGEST_TEXT,
    "ingestRagTextBatch": RuntimeJobHandlerIds.RAG_INGEST_BATCH,
}
_COLLECTION_LIFECYCLE_STARTS = {
    "createCollection": RuntimeJobHandlerIds.COLLECTION_CREATE,
    "deleteCollection": RuntimeJobHandlerIds.COLLECTION_DELETE,
}
if frozenset(_JOB_STARTS) != REST_OPERATION_FAMILIES[
    RestOperationFamily.JOB_START
]:
    raise RuntimeError(
        "REST job-start registry and handler mapping are inconsistent."
    )


class RestOperationDispatcher:
    def __init__(self, runtime: VyralRuntime) -> None:
        self.runtime = runtime
        self._dispatch_tasks: set[asyncio.Task[None]] = set()

    async def reconcile(self) -> None:
        await self.runtime.execution.reconcile_dispatch(
            ExecutionMaintenanceDispatchReconcileRequest(
                dry_run=False,
                limit=self.runtime.execution.options.max_active_runs,
            )
        )

    async def shutdown(self) -> None:
        if self._dispatch_tasks:
            for task in self._dispatch_tasks:
                task.cancel()
            await asyncio.gather(
                *self._dispatch_tasks, return_exceptions=True
            )

    async def dispatch(
        self,
        operation_id: str,
        path: Mapping[str, str],
        query: Mapping[str, str],
        headers: Mapping[str, str],
        body: object | None,
        raw_body: bytes,
    ) -> RestOperationResult:
        if operation_id in _COLLECTION_LIFECYCLE_STARTS:
            return await self._start_collection_lifecycle(
                operation_id, path, query, headers, body
            )
        if operation_id in _AGGREGATE_JOB_STARTS and not (
            operation_id == "ingestRagText"
            and _rag_request_is_dry_run(body)
        ):
            return await self._start_job(
                operation_id, path, query, headers, body
            )
        family = rest_operation_family(operation_id)
        if family is RestOperationFamily.SERVER:
            return await self._server(operation_id)
        if family is RestOperationFamily.CANONICAL:
            return await self._canonical(
                operation_id, path, query, body
            )
        if family is RestOperationFamily.RECORDS:
            return await self._records(operation_id, path, query, body)
        if family is RestOperationFamily.GRAPH:
            return await self._graph(operation_id, path, body)
        if family is RestOperationFamily.AI:
            return await self._ai(operation_id, path, body)
        if family is RestOperationFamily.EXECUTION:
            return await self._execution(
                operation_id, path, query, headers, body
            )
        if family is RestOperationFamily.EXTERNAL_WORKER:
            return await self._worker(operation_id, body)
        if family is RestOperationFamily.JOB_START:
            return await self._start_job(
                operation_id, path, query, headers, body
            )
        if family is RestOperationFamily.JOBS:
            return await self._jobs(operation_id, path, query)
        if family is RestOperationFamily.OBJECTS:
            return await self._objects(
                operation_id, path, query, headers, raw_body
            )
        if family is RestOperationFamily.TRACES:
            return await self._traces(
                operation_id, path, query, body
            )
        if family is RestOperationFamily.ARTIFACT_INGEST:
            return await self._ingest_artifact(query, headers, raw_body)
        if family is RestOperationFamily.PROVIDERS:
            return await self._providers(operation_id, path)
        raise RestOperationUnavailableError(
            f"Operation {operation_id!r} has no Python implementation."
        )

    async def _server(
        self, operation_id: str
    ) -> RestOperationResult:
        if operation_id == "getServerInfo":
            return RestOperationResult(
                body=f"Vyral Python runtime {RUNTIME_VERSION}",
                content_type="text/plain; charset=utf-8",
            )
        if operation_id == "getOpenApiContract":
            return RestOperationResult(body=self.runtime.contracts.openapi)
        if operation_id == "getPublicSchemaContract":
            return RestOperationResult(
                body=self.runtime.contracts.schema,
                content_type="application/schema+json",
            )
        health = await self._health()
        if operation_id == "getHealth":
            return RestOperationResult(body=health)
        readiness = cast(
            dict[str, Any],
            (await self.runtime.areadiness()).to_dict(),
        )
        checks = cast(list[dict[str, Any]], readiness["checks"])
        failed = [
            item for item in checks
            if item["status"] == "failed"
        ]
        warnings = cast(list[str], readiness["warnings"])
        return RestOperationResult(
            body={
                "status": (
                    "failed"
                    if failed
                    else ("warning" if warnings else "ok")
                ),
                "ready": not failed,
                "summary": (
                    "Python local runtime is operational."
                    if not failed
                    else "Python local runtime readiness checks failed."
                ),
                "checkedAt": _now(),
                "health": health,
                "checks": [
                    {
                        "id": item["id"],
                        "status": (
                            "ok"
                            if item["status"] == "passed"
                            else item["status"]
                        ),
                        "message": item["message"],
                        "details": item.get("details") or {},
                    }
                    for item in checks
                ],
                "blockers": readiness["blockers"],
                "warnings": warnings,
                "embedding": health["embedding"],
                "providers": {
                    "providerCount": 0,
                    "capabilityCount": 0,
                    "callableCapabilityCount": 0,
                    "readyCapabilityCount": 0,
                    "unvalidatedCapabilityCount": 0,
                    "networkProviderCount": 0,
                    "authProviderCount": 0,
                },
                "operationalLimits": {
                    "maxRequestBodyBytes": 67_108_864,
                    "maxActiveExecutionRuns": (
                        self.runtime.execution.options.max_active_runs
                    ),
                    "topology": "local-single-node",
                },
            }
        )

    async def _health(self) -> dict[str, Any]:
        provider = self.runtime.embeddings.provider
        return {
            "status": "ok",
            "service": "vyral-python",
            "version": RUNTIME_VERSION,
            "contractPath": "/openapi/vyral.json",
            "schemaContractPath": (
                "/contracts/schemas/vyral-public.schema.json"
            ),
            "storage": {
                "recordStore": "sqlite",
                "objectStore": "local-file",
                "traceStore": "sqlite",
                "canonicalStore": "sqlite",
            },
            "embedding": {
                "provider": provider.provider_id,
                "modelId": provider.model_id,
                "dimensions": provider.dimensions,
                "runtime": {"kind": "python"},
            },
            "security": {
                "apiKeyRequired": False,
                "apiKeyHeader": "X-Api-Key",
                "providerRunLimits": {
                    "providersEnabled": False,
                },
            },
            "checkedAt": _now(),
        }

    async def _canonical(
        self,
        operation: str,
        path: Mapping[str, str],
        query: Mapping[str, str],
        body: object | None,
    ) -> RestOperationResult:
        store = self.runtime.async_canonical
        if operation == "listCanonicalMigrations":
            return _json_result(await store.list_migrations())
        if operation == "applyCanonicalMigrations":
            values = _sequence(body, "migrations")
            await store.apply_migrations(
                tuple(
                    CanonicalMigration.from_value(_mapping(item, "migration"))
                    for item in values
                )
            )
            return RestOperationResult(204)
        if operation == "preflightCanonicalStore":
            diagnostics = self.runtime.canonical.diagnostics()
            return RestOperationResult(
                body={
                    "status": (
                        "ok" if diagnostics.get("healthy") else "failed"
                    ),
                    "summary": "Python SQLite CanonicalStore preflight.",
                    "generatedAt": _now(),
                    "checks": [
                        {
                            "id": "canonical.sqlite",
                            "status": (
                                "ok"
                                if diagnostics.get("healthy")
                                else "failed"
                            ),
                            "message": "SQLite CanonicalStore diagnostics.",
                            "details": diagnostics,
                        }
                    ],
                    "storage": diagnostics,
                }
            )
        if operation == "probeCanonicalStoreDataPlane":
            return _json_result(await store.run_data_plane_preflight())
        tenant = _required(path, "tenantId")
        if operation == "commitCanonicalTransaction":
            request = _with(_mapping(body, "transaction"), tenantId=tenant)
            return _json_result(await store.commit(request))
        if operation in {
            "getCanonicalDocument",
            "readCanonicalDocument",
        }:
            if operation == "getCanonicalDocument":
                document_type = _required(path, "documentType")
                document_id = _required(path, "id")
                include_deleted = _boolean(
                    query.get("includeDeleted"), False
                )
            else:
                read_request = _mapping(
                    body, "canonical document read"
                )
                _same(read_request, "tenantId", tenant)
                document_type = _text(
                    read_request, "documentType"
                )
                document_id = _text(read_request, "id")
                include_deleted = _boolean(
                    read_request.get("includeDeleted"), False
                )
            result = await store.get_document(
                tenant,
                document_type,
                document_id,
                include_deleted=include_deleted,
            )
            return _found(result, "Canonical document")
        if operation == "queryCanonicalDocuments":
            request = _with(
                _mapping(body, "canonical document query"),
                tenantId=tenant,
            )
            return _json_result(
                await store.query_documents(
                    CanonicalDocumentQuery.from_value(request)
                )
            )
        if operation in {
            "listCanonicalDocumentRevisions",
            "queryCanonicalDocumentRevisions",
        }:
            if operation == "listCanonicalDocumentRevisions":
                document_type = _required(path, "documentType")
                document_id = _required(path, "id")
                limit = _integer(query.get("limit"), 100)
            else:
                revision_request = _mapping(body, "revision query")
                _same(revision_request, "tenantId", tenant)
                document_type = _text(
                    revision_request, "documentType"
                )
                document_id = _text(revision_request, "id")
                limit = _integer(
                    revision_request.get("limit"), 100
                )
            return _json_result(
                await store.get_revisions(
                    tenant,
                    document_type,
                    document_id,
                    limit=limit or 100,
                )
            )
        if operation == "leaseCanonicalOutbox":
            request = _with(
                _mapping(body, "outbox lease"), tenantId=tenant
            )
            return _json_result(
                await store.lease_outbox(
                    CanonicalOutboxLeaseRequest.from_value(request)
                )
            )
        if operation == "queryCanonicalOutbox":
            request = _with(
                _mapping(body, "outbox query"), tenantId=tenant
            )
            return _json_result(
                await store.query_outbox(
                    CanonicalOutboxQuery.from_value(request)
                )
            )
        event_id = _required(path, "eventId")
        if operation == "renewCanonicalOutboxLease":
            request = _with(
                _mapping(body, "outbox lease renewal"),
                tenantId=tenant,
                eventId=event_id,
            )
            return _json_result(
                await store.renew_outbox_lease(
                    CanonicalOutboxLeaseRenewalRequest.from_value(
                        request
                    )
                )
            )
        if operation == "acknowledgeCanonicalOutbox":
            acknowledgement = _mapping(
                body, "outbox acknowledgement"
            )
            await store.acknowledge_outbox(
                tenant,
                event_id,
                _text(acknowledgement, "leaseToken"),
            )
            return RestOperationResult(204)
        if operation == "releaseCanonicalOutbox":
            request = _with(
                _mapping(body, "outbox release"),
                tenantId=tenant,
                eventId=event_id,
            )
            await store.nack_outbox(
                CanonicalOutboxNackRequest.from_value(request)
            )
            return RestOperationResult(204)
        if operation == "replayCanonicalOutbox":
            request = _with(
                _mapping(body, "outbox replay"),
                tenantId=tenant,
                eventId=event_id,
            )
            await store.replay_outbox(
                CanonicalOutboxReplayRequest.from_value(request)
            )
            return RestOperationResult(204)
        if operation == "exportCanonicalTenant":
            return _json_result(await store.export_tenant(tenant))
        if operation == "restoreCanonicalTenant":
            request = _with(
                _mapping(body, "canonical restore"), tenantId=tenant
            )
            if "archive" in request:
                await store.restore_tenant_archive(
                    CanonicalArchiveRestoreRequest.from_value(request)
                )
            else:
                await store.restore_tenant(
                    CanonicalRestoreRequest.from_value(request)
                )
            return RestOperationResult(204)
        raise RestOperationUnavailableError(operation)

    async def _records(
        self,
        operation: str,
        path: Mapping[str, str],
        query: Mapping[str, str],
        body: object | None,
    ) -> RestOperationResult:
        records = self.runtime.async_records
        if operation == "listCollections":
            names = await records.list_collections()
            policies = [
                await records.get_collection_policy(name)
                for name in names
            ]
            return _json_result(
                tuple(item for item in policies if item is not None)
            )
        if operation == "createCollection":
            policy = RecordCollectionPolicy.from_value(
                _mapping(body, "collection policy")
            )
            await records.create_collection(policy)
            return RestOperationResult(201, policy.to_dict())
        collection = _required(path, "collection")
        if operation == "getCollection":
            return _found(
                await records.get_collection_policy(collection),
                "Collection",
            )
        if operation == "deleteCollection":
            await records.delete_collection(collection)
            return RestOperationResult(204)
        if operation in {
            "exportCollection",
            "exportCollectionBounded",
        }:
            request: object | None = (
                body
                if operation == "exportCollectionBounded"
                else {
                    "maxRecords": (
                        _integer(query["maxRecords"], None)
                        if "maxRecords" in query
                        else None
                    ),
                    "failOnLimitExceeded": _boolean(
                        query.get("failOnLimitExceeded"), True
                    ),
                }
            )
            return _found(
                await records.export_collection(
                    collection,
                    CollectionExportRequest.from_value(
                        cast(Mapping[str, Any] | None, request)
                    ),
                ),
                "Collection",
            )
        if operation == "inspectCollection":
            from .mcp import StatelessMcpApplication

            inspector = StatelessMcpApplication(self.runtime)
            return RestOperationResult(
                body=await inspector._inspect_collection(
                    {
                        "collection": collection,
                        "anomalyLimit": _integer(
                            query.get("anomalyLimit"), 50
                        ),
                    }
                )
            )
        if operation == "getRecord":
            return _found(
                await records.get_record(
                    collection,
                    _required(path, "pk"),
                    _required(path, "id"),
                ),
                "Record",
            )
        if operation == "deleteRecord":
            await records.delete_record(
                collection,
                _required(path, "pk"),
                _required(path, "id"),
            )
            return RestOperationResult(204)
        if operation == "upsertRecord":
            request = _mapping(body, "record upsert")
            raw_record = request.get("record", request)
            raw_precondition = request.get("precondition")
            value = await records.upsert_record(
                collection,
                VyralRecord.from_value(
                    _mapping(raw_record, "record")
                ),
                (
                    RecordWritePrecondition.from_value(
                        _mapping(raw_precondition, "precondition")
                    )
                    if raw_precondition is not None
                    else None
                ),
            )
            return RestOperationResult(201, value.to_dict())
        if operation == "queryRecords":
            return _json_result(
                await records.query_records_page(
                    collection, _mapping(body, "record query")
                )
            )
        if operation == "searchRecords":
            return _json_result(
                await records.search_records_page(
                    collection, _mapping(body, "record search")
                )
            )
        raise RestOperationUnavailableError(operation)

    async def _graph(
        self,
        operation: str,
        path: Mapping[str, str],
        body: object | None,
    ) -> RestOperationResult:
        if operation == "listGraphProviderShapes":
            return RestOperationResult(
                body=list(list_graph_provider_shapes())
            )
        if operation == "getGraphProviderShape":
            value = get_graph_provider_shape(
                _required(path, "providerId")
            )
            return _found(value, "Graph provider shape")
        collection = _required(path, "collection")
        request = _mapping(body, "graph request")
        if operation == "preflightGraphImport":
            return _json_result(
                await self.runtime.graph.apreflight_import(
                    collection, request
                )
            )
        if operation == "exportGraphEnvelope":
            return _found(
                await self.runtime.graph.aexport_envelope(
                    collection, request
                ),
                "Graph",
            )
        if operation == "traverseGraph":
            return _found(
                await self.runtime.graph.atraverse(
                    collection, request
                ),
                "Graph traversal",
            )
        if operation == "inspectGraphCollection":
            return _found(
                await self.runtime.graph.ainspect(
                    collection, request
                ),
                "Graph collection",
            )
        if operation == "doctorGraphCollection":
            return _found(
                await self.runtime.graph.adoctor(
                    collection, request
                ),
                "Graph collection",
            )
        raise RestOperationUnavailableError(operation)

    async def _ai(
        self,
        operation: str,
        path: Mapping[str, str],
        body: object | None,
    ) -> RestOperationResult:
        if operation == "listRetrievalProfiles":
            return RestOperationResult(
                body=list(get_retrieval_profiles())
            )
        if operation == "listEmbeddingProviders":
            return _json_result(
                EmbeddingProviderRegistry().get_providers()
            )
        if operation == "listEmbeddingProviderGuidance":
            return RestOperationResult(
                body=[
                    _embedding_guidance(item.to_dict())
                    for item in EmbeddingProviderRegistry().get_providers()
                ]
            )
        if operation == "getEmbeddingProviderDoctor":
            provider = self.runtime.embeddings.provider
            return RestOperationResult(
                body={
                    "provider": provider.provider_id,
                    "status": "ok",
                    "summary": "Active Python embedding provider is ready.",
                    "generatedAt": _now(),
                    "checks": [
                        {
                            "id": "embedding.active",
                            "status": "ok",
                            "message": "Provider is initialized.",
                            "details": {
                                "modelId": provider.model_id,
                                "dimensions": provider.dimensions,
                            },
                        }
                    ],
                    "metadata": {"runtime": "python"},
                }
            )
        request = _mapping(body, "AI request")
        if operation == "generateEmbeddings":
            return _json_result(
                await self.runtime.embeddings.aembed(request)
            )
        if operation == "retrieve":
            return _json_result(
                await self.runtime.retrieval.asearch(request)
            )
        if operation == "evaluateRetrieval":
            return _json_result(
                await self.runtime.retrieval_evaluation.aevaluate(
                    request
                )
            )
        if operation == "compareRetrievalEvaluations":
            return _json_result(
                await self.runtime.retrieval_evaluation.acompare(request)
            )
        if operation == "buildRagContext":
            return _json_result(
                await self.runtime.rag_context.abuild_context(request)
            )
        if operation == "evaluateRagContext":
            return _json_result(
                await self.runtime.rag_context.aevaluate_context(request)
            )
        if operation == "buildRagPrompt":
            return _json_result(
                await self.runtime.rag_prompts.abuild_prompt(request)
            )
        if operation == "ingestRagText":
            return _json_result(
                await self.runtime.rag_ingestion.aingest_text(
                    _required(path, "collection"), request
                )
            )
        raise RestOperationUnavailableError(operation)

    async def _execution(
        self,
        operation: str,
        path: Mapping[str, str],
        query: Mapping[str, str],
        headers: Mapping[str, str],
        body: object | None,
    ) -> RestOperationResult:
        execution = self.runtime.execution
        if operation == "getExecutionRuntime":
            return _json_result(
                {
                    "status": await execution.get_adapter_status(),
                    "plugins": execution.list_plugins(),
                    "handlers": execution.list_handlers(),
                }
            )
        if operation == "getEffectiveExecutionRuntime":
            return _json_result(
                {
                    "status": await execution.get_adapter_status(),
                    "scope": {
                        "sharedExecution": False,
                        "scopeRequired": False,
                        "productId": None,
                        "tenantId": None,
                    },
                    "handlers": execution.list_handlers(),
                }
            )
        if operation == "getExecutionRuntimeMaintenance":
            return _json_result(
                await execution.get_maintenance_status()
            )
        if operation == "pruneExecutionRuntimeMaintenance":
            request = _mapping(body, "maintenance prune")
            return _json_result(
                await execution.prune(
                    ExecutionMaintenancePruneRequest(
                        dry_run=_boolean(
                            request.get("dryRun"), True
                        ),
                        retain_terminal_runs=_integer(
                            request.get("retainTerminalRuns"), None
                        ),
                    )
                )
            )
        if operation == "reconcileExecutionRuntimeDispatch":
            request = _mapping(body, "dispatch reconciliation")
            return _json_result(
                await execution.reconcile_dispatch(
                    ExecutionMaintenanceDispatchReconcileRequest(
                        dry_run=_boolean(
                            request.get("dryRun"), False
                        ),
                        limit=_integer(request.get("limit"), None),
                    )
                )
            )
        if operation == "listExecutionRuns":
            values: dict[str, Any] = dict(query)
            tags = {
                name[4:]: value
                for name, value in query.items()
                if name.startswith("tag.")
            }
            values = {
                name: value
                for name, value in values.items()
                if not name.startswith("tag.")
            }
            if tags:
                values["tags"] = tags
            if "limit" in values:
                values["limit"] = _integer(values["limit"], None)
            if "includeResult" in values:
                values["includeResult"] = _boolean(
                    values["includeResult"], True
                )
            runs = await execution.list_runs(
                ExecutionRunQuery.from_value(values)
            )
            return RestOperationResult(
                body=[_public_execution_run(run) for run in runs]
            )
        if operation == "startExecutionRun":
            request = dict(_mapping(body, "execution run"))
            header_key = _idempotency_key(headers)
            body_key = request.get("idempotencyKey")
            if body_key is not None and not isinstance(body_key, str):
                raise TypeError("idempotencyKey must be a string.")
            if (
                header_key is not None
                and body_key is not None
                and header_key != body_key.strip()
            ):
                raise ValueError(
                    "Idempotency-Key must match request.idempotencyKey "
                    "when both are supplied."
                )
            request["idempotencyKey"] = header_key or body_key
            run = await execution.start_run(
                ExecutionRunRequest.from_value(request)
            )
            self._track_dispatch()
            status_uri = f"/execution/runs/{quote(run.id, safe='')}"
            return _admission_result(
                _public_execution_run(run), run, status_uri
            )
        run_id = _required(path, "runId")
        if operation == "getExecutionRun":
            found_run = await execution.get_run(
                run_id,
                include_result=_boolean(
                    query.get("includeResult"), True
                ),
            )
            return _found(
                (
                    None
                    if found_run is None
                    else _public_execution_run(found_run)
                ),
                "Execution run",
            )
        if operation == "cancelExecutionRun":
            cancelled_run = await execution.cancel_run(run_id)
            return _found(
                (
                    None
                    if cancelled_run is None
                    else _public_execution_run(cancelled_run)
                ),
                "Execution run",
            )
        if operation == "raiseExecutionEvent":
            request = _mapping(body, "execution event")
            request = _with(request, runId=run_id)
            return _json_result(
                await execution.raise_event(
                    ExecutionExternalEventRequest(
                        name=_text(request, "name"),
                        run_id=run_id,
                        payload=cast(
                            JSONValue, request.get("payload")
                        ),
                    )
                )
            )
        if operation == "getExecutionRunHistory":
            return _json_result(
                await execution.get_history(
                    run_id,
                    ExecutionHistoryQuery(
                        limit=_integer(query.get("limit"), None)
                    ),
                )
            )
        if operation == "listExecutionRunArtifacts":
            return _json_result(
                await execution.list_artifacts(run_id)
            )
        if operation == "getExecutionRunArtifact":
            return _found(
                await execution.get_artifact(
                    run_id, _required(path, "artifactRef")
                ),
                "Execution artifact",
            )
        if operation == "getExecutionRunCheckpoint":
            return _found(
                await execution.get_checkpoint(
                    run_id, _required(path, "key")
                ),
                "Execution checkpoint",
            )
        raise RestOperationUnavailableError(operation)

    async def _worker(
        self, operation: str, body: object | None
    ) -> RestOperationResult:
        request = _mapping(body, "external worker request")
        execution = self.runtime.execution
        if operation == "leaseExternalExecutionRun":
            raw_handlers = request.get("handlerIds")
            if not isinstance(raw_handlers, Sequence) or isinstance(
                raw_handlers, (str, bytes)
            ):
                raise TypeError("handlerIds must be an array.")
            if not all(isinstance(item, str) for item in raw_handlers):
                raise TypeError("handlerIds must contain strings.")
            leased = await execution.lease_next(
                cast(str | None, request.get("runId")),
                float(request.get("ttlSeconds", 60.0)),
                worker_id=_text(request, "workerId"),
                handler_ids=cast(Sequence[str], raw_handlers),
            )
            return (
                RestOperationResult(204)
                if leased is None
                else _json_result(_public_execution_lease(leased))
            )
        credentials = _ExecutionWorkerLeaseCredentials(
            lease_key=_text(request, "leaseKey"),
            lease_token=_text(request, "leaseToken"),
            worker_id=_text(request, "workerId"),
        )
        if operation == "heartbeatExternalExecutionLease":
            return _json_result(
                _public_execution_lease(
                    await execution.heartbeat(
                        credentials, float(request["ttlSeconds"])
                    )
                )
            )
        if operation == "reportExternalExecutionLease":
            return _json_result(
                _public_execution_run(
                    await execution.report(
                        credentials,
                        ExecutionRunUpdate.from_value(
                            _mapping(request.get("update"), "update")
                        ),
                    )
                )
            )
        if operation == "recordExternalExecutionLeaseEvent":
            event = _mapping(
                request.get("event", request), "worker event"
            )
            await execution.record_event(
                credentials,
                _text(event, "type"),
                message=cast(str | None, event.get("message")),
                severity=cast(str, event.get("severity", "info")),
                details=cast(
                    dict[str, JSONValue] | None,
                    event.get("details"),
                ),
            )
            return RestOperationResult(204)
        if operation == "putExternalExecutionLeaseArtifact":
            return _json_result(
                await execution.put_artifact(
                    credentials,
                    ExecutionArtifactWrite.from_value(
                        _mapping(
                            request.get("artifact"), "artifact"
                        )
                    ),
                )
            )
        if operation == "putExternalExecutionLeaseCheckpoint":
            return _json_result(
                await execution.checkpoint(
                    credentials,
                    ExecutionCheckpointWrite.from_value(
                        _mapping(
                            request.get("checkpoint"), "checkpoint"
                        )
                    ),
                )
            )
        if operation == "getExternalExecutionLeaseCheckpoint":
            return _found(
                await execution.get_external_checkpoint(
                    credentials, _text(request, "key")
                ),
                "Execution checkpoint",
            )
        if operation == "waitExternalExecutionLease":
            response = await execution.wait(
                credentials,
                ExecutionWorkerWaitRequest.from_value(request),
            )
            value = response.to_dict()
            value["run"] = _public_execution_run(response.run)
            return _json_result(value)
        if operation == "completeExternalExecutionLease":
            return _json_result(
                _public_execution_run(
                    await execution.complete(
                        credentials,
                        ExecutionRunResult.from_value(
                            _mapping(request.get("result"), "result")
                        ),
                    )
                )
            )
        raise RestOperationUnavailableError(operation)

    async def _start_job(
        self,
        operation: str,
        path: Mapping[str, str],
        query: Mapping[str, str],
        headers: Mapping[str, str],
        body: object | None,
    ) -> RestOperationResult:
        request = _mapping(body, "job request")
        handler_id = (_JOB_STARTS | _AGGREGATE_JOB_STARTS)[operation]
        payload: dict[str, JSONValue] = {
            "request": cast(dict[str, JSONValue], dict(request))
        }
        collection = path.get("collection")
        if collection is not None:
            payload["collection"] = collection
        tags = (
            {
                "vyral.admission.operation-id": operation,
                "vyral.job": "aggregate-mutation",
            }
            if operation in _AGGREGATE_JOB_STARTS
            else {}
        )
        run = await self.runtime.execution.start_run(
            ExecutionRunRequest(
                handler_id,
                plugin_id=BUILTIN_JOB_PLUGIN_ID,
                payload=payload,
                idempotency_key=_idempotency_key(headers),
                scope=_execution_scope(query),
                tags=tags,
            )
        )
        self._track_dispatch()
        projected = _job_projection(run, handler_id)
        admission = cast(Mapping[str, Any], projected["admission"])
        return _admission_result(
            projected,
            run,
            str(admission["statusUri"]),
        )

    async def _start_collection_lifecycle(
        self,
        operation: str,
        path: Mapping[str, str],
        query: Mapping[str, str],
        headers: Mapping[str, str],
        body: object | None,
    ) -> RestOperationResult:
        handler_id = _COLLECTION_LIFECYCLE_STARTS[operation]
        payload: dict[str, JSONValue]
        if operation == "createCollection":
            payload = {
                "request": cast(
                    dict[str, JSONValue],
                    dict(_mapping(body, "collection policy")),
                )
            }
        else:
            payload = {"collection": _required(path, "collection")}
        run = await self.runtime.execution.start_run(
            ExecutionRunRequest(
                handler_id,
                plugin_id=BUILTIN_JOB_PLUGIN_ID,
                payload=payload,
                idempotency_key=_idempotency_key(headers),
                scope=_execution_scope(query),
                tags={
                    "vyral.admission.operation-id": operation,
                    "vyral.job": "collection-management",
                },
            )
        )
        self._track_dispatch()
        projected = _public_execution_run(run)
        admission = cast(Mapping[str, Any], projected["admission"])
        return _admission_result(
            projected,
            run,
            str(admission["statusUri"]),
        )

    async def _jobs(
        self,
        operation: str,
        path: Mapping[str, str],
        query: Mapping[str, str],
    ) -> RestOperationResult:
        handlers, id_name, action = _job_route(operation)
        if not handlers:
            if action == "list":
                return RestOperationResult(body=[])
            raise RestNotFoundError("Provider run job was not found.")
        if action == "list":
            runs: list[ExecutionRun] = []
            for handler in handlers:
                runs.extend(
                    await self.runtime.execution.list_runs(
                        ExecutionRunQuery(
                            handler_id=handler,
                            status=query.get("status"),
                            include_result=_boolean(
                                query.get("includeResult"), True
                            ),
                            limit=_integer(query.get("limit"), None),
                        )
                    )
                )
            runs.sort(
                key=lambda run: (run.created_at_utc, run.id),
                reverse=True,
            )
            limit = _integer(query.get("limit"), 100) or 100
            return RestOperationResult(
                body=[
                    _job_projection(run, run.handler_id)
                    for run in runs[:limit]
                ]
            )
        job_id = _required(path, id_name)
        run = await self.runtime.execution.get_run(job_id)
        if run is None or run.handler_id not in handlers:
            raise RestNotFoundError("Job was not found.")
        if action == "cancel":
            cancelled = await self.runtime.execution.cancel_run(job_id)
            if cancelled is None:
                raise RestNotFoundError("Job was not found.")
            run = cancelled
        return RestOperationResult(
            body=_job_projection(run, run.handler_id)
        )

    async def _objects(
        self,
        operation: str,
        path: Mapping[str, str],
        query: Mapping[str, str],
        headers: Mapping[str, str],
        raw_body: bytes,
    ) -> RestOperationResult:
        container = _required(path, "container")
        if operation == "listObjects":
            return _json_result(
                await self.runtime.objects.alist_objects(
                    ObjectListRequest(
                        container,
                        prefix=query.get("prefix"),
                        limit=_integer(query.get("limit"), None),
                        continuation_token=query.get(
                            "continuationToken"
                        ),
                    )
                )
            )
        key = _required(path, "key")
        if operation == "putObject":
            metadata = {
                name[len("x-vyral-meta-") :]: value
                for name, value in headers.items()
                if name.startswith("x-vyral-meta-")
            }
            value = await self.runtime.objects.aput_object(
                ObjectWriteRequest(
                    container,
                    key,
                    raw_body,
                    content_type=headers.get("content-type"),
                    metadata=metadata,
                    if_match=headers.get("if-match"),
                    if_none_match=headers.get("if-none-match"),
                )
            )
            return _json_result(value)
        if operation == "getObject":
            result = await self.runtime.objects.aget_object(
                ObjectReadRequest(container, key)
            )
            if result is None:
                raise RestNotFoundError("Object was not found.")
            try:
                content = result.read()
                info = result.info
            finally:
                result.close()
            return RestOperationResult(
                body=content,
                content_type=(
                    info.content_type or "application/octet-stream"
                ),
                headers=(
                    (b"etag", info.etag.encode("ascii")),
                    (
                        b"x-vyral-content-hash",
                        info.content_hash.encode("ascii"),
                    ),
                ),
            )
        await self.runtime.objects.adelete_object(
            ObjectDeleteRequest(
                container, key, if_match=headers.get("if-match")
            )
        )
        return RestOperationResult(204)

    async def _traces(
        self,
        operation: str,
        path: Mapping[str, str],
        query: Mapping[str, str],
        body: object | None,
    ) -> RestOperationResult:
        traces = self.runtime.traces
        if operation == "listTraces":
            return _json_result(
                await traces.alist_traces(
                    operation=query.get("operation"),
                    limit=_integer(query.get("limit"), None),
                )
            )
        if operation == "summarizeTraces":
            return _json_result(
                await traces.asummarize_traces(
                    query.get("operation")
                )
            )
        if operation == "getTrace":
            return _found(
                await traces.aget_trace(_required(path, "id")),
                "Trace",
            )
        request = _mapping(body, "trace request")
        if operation == "pruneTraces":
            return _json_result(
                await traces.aprune_traces(
                    TracePruneRequest(
                        operation=cast(
                            str | None, request.get("operation")
                        ),
                        older_than=_datetime(
                            request.get("olderThan")
                        ),
                        keep_latest=_integer(
                            request.get("keepLatest"), None
                        ),
                        limit=_integer(request.get("limit"), None),
                        dry_run=_boolean(
                            request.get("dryRun"), False
                        ),
                    )
                )
            )
        if operation == "exportTraces":
            return _json_result(
                await traces.aexport_traces(
                    TraceExportRequest(
                        operation=cast(
                            str | None, request.get("operation")
                        ),
                        limit=_integer(request.get("limit"), None),
                        fail_on_unsafe_content=_boolean(
                            request.get("failOnUnsafeContent"), False
                        ),
                    )
                )
            )
        raise RestOperationUnavailableError(operation)

    async def _ingest_artifact(
        self,
        query: Mapping[str, str],
        headers: Mapping[str, str],
        raw_body: bytes,
    ) -> RestOperationResult:
        manifest, artifact_content = _multipart_artifact(
            headers, raw_body
        )
        collection = _text(manifest, "collection")
        record = VyralRecord.from_value(
            _mapping(manifest.get("record"), "record")
        )
        descriptor = _mapping(
            manifest.get("artifact"), "artifact descriptor"
        )
        container = _text(descriptor, "container")
        key = _text(descriptor, "key")
        content_type = descriptor.get("contentType")
        if content_type is not None and not isinstance(content_type, str):
            raise TypeError("artifact contentType must be a string.")
        raw_metadata = descriptor.get("metadata", {})
        metadata = _mapping(raw_metadata, "artifact metadata")
        if not all(
            isinstance(name, str) and isinstance(value, str)
            for name, value in metadata.items()
        ):
            raise TypeError(
                "artifact metadata must contain string values."
            )
        if manifest.get("externalContext") is not None:
            raise RestOperationUnavailableError(
                "External-context proof verification is not configured."
            )
        if len(artifact_content) == 0:
            raise ValueError("Artifact content is required.")
        if len(artifact_content) > 16 * 1024 * 1024:
            raise ValueError(
                "Artifact cannot exceed 16777216 bytes."
            )
        if (
            await self.runtime.async_records.get_collection_policy(
                collection
            )
            is None
        ):
            raise RestNotFoundError(
                f"Collection {collection!r} was not found."
            )
        expected_hash = "sha256:" + sha256(
            artifact_content
        ).hexdigest()
        wire_manifest: dict[str, JSONValue] = {
            "collection": collection,
            "record": cast(JSONValue, record.to_dict()),
            "artifact": {
                "container": container,
                "key": key,
                "contentType": cast(JSONValue, content_type),
                "metadata": cast(JSONValue, dict(metadata)),
            },
        }
        idempotency_key = _idempotency_key(headers)
        staging_identity = (
            sha256(idempotency_key.encode("utf-8")).hexdigest()
            if idempotency_key is not None
            else uuid4().hex
        )
        staging_container = "vyral-admission-staging"
        staging_key = f"record-artifact/{staging_identity}.bin"
        staging_created = False
        staging_etag: str | None = None
        try:
            staged_info = await self.runtime.objects.aput_object(
                ObjectWriteRequest(
                    staging_container,
                    staging_key,
                    artifact_content,
                    content_type="application/octet-stream",
                    metadata={
                        "admission_staging": "true",
                        "content_hash": expected_hash,
                    },
                    if_none_match="*",
                )
            )
            staging_created = True
            staging_etag = staged_info.etag
        except ValueError:
            existing = await self.runtime.objects.aget_object(
                ObjectReadRequest(staging_container, staging_key)
            )
            if existing is None:
                raise
            try:
                if existing.info.content_hash != expected_hash:
                    raise ValueError(
                        "Idempotency-Key already has different staged "
                        "artifact content."
                    )
            finally:
                existing.close()
        try:
            run = await self.runtime.execution.start_run(
                ExecutionRunRequest(
                    RuntimeJobHandlerIds.ARTIFACT_RECORD_INGEST,
                    plugin_id=BUILTIN_JOB_PLUGIN_ID,
                    payload={
                        "manifest": wire_manifest,
                        "stagingContainer": staging_container,
                        "stagingKey": staging_key,
                        "contentHash": expected_hash,
                    },
                    idempotency_key=idempotency_key,
                    scope=_execution_scope(query),
                    tags={
                        "vyral.job": "artifact-record-ingestion",
                        "vyral.admission.operation-id": (
                            "ingestRecordArtifact"
                        ),
                    },
                )
            )
        except Exception:
            if staging_created:
                await self.runtime.objects.adelete_object(
                    ObjectDeleteRequest(
                        staging_container, staging_key, staging_etag
                    )
                )
            raise
        if (
            staging_created
            and run.admission_replayed
            and run.status in TERMINAL_RUN_STATUSES
        ):
            await self.runtime.objects.adelete_object(
                ObjectDeleteRequest(
                    staging_container, staging_key, staging_etag
                )
            )
        self._track_dispatch()
        status_uri = f"/execution/runs/{quote(run.id, safe='')}"
        return _admission_result(
            _public_execution_run(run), run, status_uri
        )

    async def _providers(
        self, operation: str, path: Mapping[str, str]
    ) -> RestOperationResult:
        generated = _now()
        if operation == "listProviders":
            return RestOperationResult(body=[])
        if operation == "getProviderCapabilityMatrix":
            return RestOperationResult(
                body={
                    "generatedAt": generated,
                    "capabilityIds": [],
                    "items": [],
                    "disabledProviders": [],
                    "failureClasses": [],
                    "operationalLimits": {
                        "runtime": "python",
                        "providersEnabled": False,
                    },
                    "notes": [
                        "No optional provider adapters are registered."
                    ],
                }
            )
        if operation == "listProviderReadiness":
            return RestOperationResult(
                body={
                    "generatedAt": generated,
                    "items": [],
                    "disabledProviders": [],
                }
            )
        if operation in {
            "listProviderDoctor",
            "listProviderQuotas",
        }:
            return RestOperationResult(body=[])
        if "provider" in path:
            raise RestNotFoundError(
                f"Provider {path['provider']!r} is not registered."
            )
        raise RestOperationUnavailableError(
            "No optional provider adapter is registered for this operation."
        )

    def _track_dispatch(self) -> None:
        task = asyncio.create_task(self._dispatch_once())
        self._dispatch_tasks.add(task)
        task.add_done_callback(self._dispatch_tasks.discard)

    async def _dispatch_once(self) -> None:
        await self.runtime.execution.dispatch_ready_runs(
            recover_interrupted_runs=True
        )


def _job_route(
    operation: str,
) -> tuple[frozenset[str], str, str]:
    if "Embedding" in operation:
        handlers = _EMBEDDING_HANDLERS
        id_name = "jobId"
    elif "RecordImport" in operation:
        handlers = _RECORD_HANDLERS
        id_name = "jobId"
    elif "RagIngestion" in operation:
        handlers = _RAG_HANDLERS
        id_name = "jobId"
    elif "RetrievalEvaluation" in operation:
        handlers = _RETRIEVAL_HANDLERS
        id_name = "jobId"
    elif "Graph" in operation:
        handlers = _GRAPH_HANDLERS
        id_name = "jobId"
    else:
        handlers = frozenset()
        id_name = "jobId"
    if operation.startswith("list"):
        action = "list"
    elif operation.startswith("cancel"):
        action = "cancel"
    else:
        action = "get"
    return handlers, id_name, action


def _execution_scope(
    query: Mapping[str, str],
) -> ExecutionScope | None:
    product = query.get("productId")
    tenant = query.get("tenantId")
    if product is None and tenant is None:
        return None
    if (
        product is None
        or not product.strip()
        or tenant is None
        or not tenant.strip()
    ):
        raise ValueError(
            "productId and tenantId must be supplied together."
        )
    return ExecutionScope(product.strip(), tenant.strip())


def _job_projection(
    run: ExecutionRun, handler_id: str
) -> dict[str, JSONValue]:
    payload = (
        cast(Mapping[str, Any], run.payload)
        if isinstance(run.payload, Mapping)
        else {}
    )
    request = (
        cast(Mapping[str, Any], payload.get("request"))
        if isinstance(payload.get("request"), Mapping)
        else {}
    )
    result = (
        cast(Mapping[str, Any], run.result)
        if isinstance(run.result, Mapping)
        else {}
    )
    base: dict[str, JSONValue] = {
        "admission": _admission_receipt(
            run,
            _job_operation_id(handler_id, run),
            _job_status_uri(run, handler_id),
        ),
        "id": run.id,
        "status": (
            "running"
            if run.status in {"waiting", "running"}
            else run.status
        ),
        "requestHash": run.payload_hash,
        "createdAt": _date(run.created_at_utc),
        "startedAt": _date(run.started_at_utc),
        "completedAt": _date(run.completed_at_utc),
        "durationMs": run.duration_ms,
        "cancellationRequested": run.cancellation_requested,
        "requested": run.requested or _request_count(handler_id, request),
        "attempted": run.attempted or 0,
        "succeeded": run.succeeded or 0,
        "failed": run.failed or 0,
        "progress": run.progress or 0.0,
        "failureClass": run.failure_class,
        "error": run.error,
    }
    if handler_id == RuntimeJobHandlerIds.EMBEDDINGS:
        options = request
        base.update(
            {
                "provider": str(
                    result.get(
                        "provider",
                        options.get("provider", "local-token-hash"),
                    )
                ),
                "modelId": str(
                    result.get(
                        "modelId",
                        options.get(
                            "modelId",
                            "local-token-hash-embedding-v1",
                        ),
                    )
                ),
                "dimensions": int(
                    result.get(
                        "dimensions", options.get("dimensions", 384)
                    )
                ),
                "purpose": str(
                    result.get(
                        "purpose", options.get("purpose", "symmetric")
                    )
                ),
                "currentIndex": (
                    (run.attempted - 1)
                    if run.attempted is not None and run.attempted > 0
                    else None
                ),
            }
        )
        if run.result is not None:
            base["result"] = run.result
        return base
    collection = str(payload.get("collection", ""))
    base["collection"] = collection
    if handler_id in _RECORD_HANDLERS:
        result_name = (
            "importResult"
            if handler_id == RuntimeJobHandlerIds.COLLECTION_IMPORT
            else "batchResult"
        )
        base.update(
            {
                "kind": (
                    "collection_import"
                    if handler_id
                    == RuntimeJobHandlerIds.COLLECTION_IMPORT
                    else "batch_upsert"
                ),
                "sourceCollection": cast(
                    JSONValue,
                    result.get("sourceCollection"),
                ),
                "stoppedOnError": bool(
                    result.get("stoppedOnError", False)
                ),
            }
        )
        if run.result is not None:
            base[result_name] = run.result
    elif handler_id in _RAG_HANDLERS:
        is_batch = handler_id == RuntimeJobHandlerIds.RAG_INGEST_BATCH
        result_name = "batchResult" if is_batch else "textResult"
        base.update(
            {
                "kind": "batch" if is_batch else "text",
                "currentIndex": None,
                "currentDocumentId": None,
                "currentPartitionKey": None,
                "textLength": int(result.get("textLength", 0)),
                "chunkCount": int(result.get("chunkCount", 0)),
                "deletedStaleCount": int(
                    result.get("deletedStaleCount", 0)
                ),
                "createdCount": int(result.get("createdCount", 0)),
                "updatedCount": int(result.get("updatedCount", 0)),
                "reusedCount": int(result.get("reusedCount", 0)),
                "vectorGeneratedCount": int(
                    result.get("vectorGeneratedCount", 0)
                ),
                "vectorReusedCount": int(
                    result.get("vectorReusedCount", 0)
                ),
                "deduplicatedCount": int(
                    result.get("deduplicatedCount", 0)
                ),
            }
        )
        if run.result is not None:
            base[result_name] = run.result
    elif handler_id in _RETRIEVAL_HANDLERS:
        is_compare = handler_id == RuntimeJobHandlerIds.RETRIEVAL_COMPARE
        result_name = (
            "result" if is_compare else "evaluationResult"
        )
        base.update(
            {
                "kind": "comparison" if is_compare else "evaluation",
                "casesAttempted": run.attempted or 0,
                "casesSucceeded": run.succeeded or 0,
                "casesFailed": run.failed or 0,
                "currentCaseIndex": None,
                "currentCaseName": None,
                "variantsRequested": (
                    len(cast(Sequence[object], request.get("variants", ())))
                    if is_compare
                    and isinstance(request.get("variants"), Sequence)
                    else 0
                ),
                "variantsAttempted": 0,
                "variantsSucceeded": 0,
                "variantsFailed": 0,
                "currentVariantId": None,
                "currentVariantIndex": None,
            }
        )
        if run.result is not None:
            base[result_name] = run.result
    elif handler_id in _GRAPH_HANDLERS:
        kind = {
            RuntimeJobHandlerIds.GRAPH_IMPORT: "import",
            RuntimeJobHandlerIds.GRAPH_INSPECT: "inspect",
            RuntimeJobHandlerIds.GRAPH_DOCTOR: "doctor",
        }[handler_id]
        result_name = {
            "import": "importResult",
            "inspect": "inspectionResult",
            "doctor": "doctorResult",
        }[kind]
        base.update(
            {
                "kind": kind,
                "graphId": cast(JSONValue, result.get("graphId")),
                "partitionKey": cast(
                    JSONValue, result.get("partitionKey")
                ),
                "recordCount": int(result.get("recordCount", 0)),
                "nodeCount": int(result.get("nodeCount", 0)),
                "edgeCount": int(result.get("edgeCount", 0)),
                "assertionCount": int(
                    result.get("assertionCount", 0)
                ),
                "reviewCount": int(result.get("reviewCount", 0)),
                "projectionCount": int(
                    result.get("projectionCount", 0)
                ),
                "warningCount": int(result.get("warningCount", 0)),
                "errorCount": int(result.get("errorCount", 0)),
                "anomalyCount": int(result.get("anomalyCount", 0)),
            }
        )
        if run.result is not None:
            base[result_name] = run.result
    return base


def _public_execution_run(run: ExecutionRun) -> dict[str, JSONValue]:
    value = run.to_dict()
    value.pop("idempotencyKey", None)
    operation_id = _execution_operation_id(run)
    status_uri = _execution_status_uri(run)
    value["admission"] = _admission_receipt(
        run,
        operation_id,
        status_uri,
    )
    return value


def _public_execution_lease(
    lease: ExecutionWorkerLease,
) -> dict[str, JSONValue]:
    value = lease.to_dict()
    value["run"] = _public_execution_run(lease.run)
    return value


def _admission_receipt(
    run: ExecutionRun,
    operation_id: str,
    status_uri: str,
) -> dict[str, JSONValue]:
    rejected = run.status == "rejected"
    return cast(
        dict[str, JSONValue],
        create_admission_receipt(
            operation_id=operation_id,
            resource_id=run.id,
            request_hash=run.payload_hash,
            idempotency_key=run.idempotency_key,
            replayed=run.admission_replayed,
            admitted_at_utc=run.created_at_utc,
            status_uri=status_uri,
            status="rejected" if rejected else "accepted",
            failure_class=run.failure_class,
            error=run.error,
        ).to_dict(),
    )


def _admission_result(
    body: dict[str, JSONValue],
    run: ExecutionRun,
    status_uri: str,
) -> RestOperationResult:
    if run.status != "rejected":
        return RestOperationResult(
            202,
            body,
            headers=((b"location", status_uri.encode("utf-8")),),
        )
    status = {
        "handler_missing": 400,
        "plugin_mismatch": 400,
        "queue_full": 429,
        "platform": 503,
    }.get(run.failure_class or "", 422)
    admission = cast(dict[str, JSONValue], body["admission"])
    return RestOperationResult(
        status,
        {
            "type": "https://openvyral.com/problems/admission-rejected",
            "title": "Admission rejected",
            "status": status,
            "detail": (
                run.error
                or "The execution runtime did not admit this request."
            ),
            "admission": admission,
        },
        content_type="application/problem+json",
    )


def _idempotency_key(headers: Mapping[str, str]) -> str | None:
    value = (
        headers.get("idempotency-key")
        or headers.get("x-idempotency-key")
    )
    return value.strip() if value and value.strip() else None


def _rag_request_is_dry_run(body: object | None) -> bool:
    if not isinstance(body, Mapping):
        return False
    options = body.get("options")
    return isinstance(options, Mapping) and options.get("dryRun") is True


def _job_operation_id(handler_id: str, run: ExecutionRun | None = None) -> str:
    if run is not None:
        tagged = run.tags.get("vyral.admission.operation-id")
        if tagged in _AGGREGATE_JOB_STARTS:
            return tagged
    for operation_id, candidate in _JOB_STARTS.items():
        if candidate == handler_id:
            return operation_id
    raise RestOperationUnavailableError(
        f"Execution handler {handler_id!r} is not a public job handler."
    )


def _execution_operation_id(run: ExecutionRun) -> str:
    tagged = run.tags.get("vyral.admission.operation-id")
    if tagged in {
        *_AGGREGATE_JOB_STARTS,
        *_COLLECTION_LIFECYCLE_STARTS,
        "ingestRecordArtifact",
    }:
        return tagged
    if run.handler_id == RuntimeJobHandlerIds.ARTIFACT_RECORD_INGEST:
        return "ingestRecordArtifact"
    try:
        return _job_operation_id(run.handler_id)
    except RestOperationUnavailableError:
        return "startExecutionRun"


def _execution_status_uri(run: ExecutionRun) -> str:
    if run.handler_id == RuntimeJobHandlerIds.ARTIFACT_RECORD_INGEST:
        return f"/execution/runs/{quote(run.id, safe='')}"
    try:
        return _job_status_uri(run, run.handler_id)
    except RestOperationUnavailableError:
        return f"/execution/runs/{quote(run.id, safe='')}"


def _job_status_uri(run: ExecutionRun, handler_id: str) -> str:
    escaped = quote(run.id, safe="")
    if handler_id == RuntimeJobHandlerIds.EMBEDDINGS:
        return f"/embeddings/jobs/{escaped}"
    if handler_id in _RECORD_HANDLERS:
        return f"/record-import/jobs/{escaped}"
    if handler_id in _RAG_HANDLERS:
        return f"/rag/ingestion/jobs/{escaped}"
    if handler_id in _RETRIEVAL_HANDLERS:
        return f"/retrieval/evaluate/jobs/{escaped}"
    if handler_id in _GRAPH_HANDLERS:
        return f"/graph/jobs/{escaped}"
    raise RestOperationUnavailableError(
        f"Execution handler {handler_id!r} is not a public job handler."
    )


def _request_count(
    handler_id: str, request: Mapping[str, Any]
) -> int:
    if handler_id == RuntimeJobHandlerIds.EMBEDDINGS:
        texts = request.get("texts")
        return (
            len(cast(Sequence[object], texts))
            if isinstance(texts, (list, tuple))
            else (1 if request.get("text") is not None else 0)
        )
    for name in ("records", "items", "cases"):
        value = request.get(name)
        if isinstance(value, (list, tuple)):
            return len(value)
    return 1


def _embedding_guidance(
    descriptor: Mapping[str, JSONValue],
) -> dict[str, JSONValue]:
    local = bool(descriptor["local"])
    return {
        "provider": descriptor["provider"],
        "displayName": descriptor["displayName"],
        "semanticQuality": descriptor["semanticQuality"],
        "defaultDimensions": descriptor["defaultDimensions"],
        "hardwareProfile": "cpu-only" if local else "remote",
        "requiresModelFiles": False,
        "realisticForSemanticRetrieval": (
            descriptor["provider"] == "local-token-hash"
        ),
        "defaultQueryPrefix": descriptor["defaultQueryPrefix"],
        "defaultPassagePrefix": descriptor["defaultPassagePrefix"],
        "recommendedFor": [
            "local development",
            "deterministic regression tests",
        ],
        "cautions": [
            "Qualify semantic recall before production promotion."
        ],
        "suggestedRetrievalProfiles": [
            "ragBaseline",
            "discovery",
        ],
        "suggestedEvaluationVariants": ["lexical", "hybrid"],
        "selectionNotes": [
            "This provider is bundled with the Python local runtime."
        ],
    }


def _multipart_artifact(
    headers: Mapping[str, str], raw_body: bytes
) -> tuple[Mapping[str, Any], bytes]:
    content_type = headers.get("content-type", "")
    if not content_type.lower().startswith("multipart/form-data"):
        raise ValueError(
            "Record-artifact ingest requires multipart/form-data."
        )
    envelope = (
        b"Content-Type: "
        + content_type.encode("latin-1")
        + b"\r\nMIME-Version: 1.0\r\n\r\n"
        + raw_body
    )
    message = BytesParser(policy=email_policy.default).parsebytes(
        envelope
    )
    if not message.is_multipart():
        raise ValueError("Multipart request body is malformed.")
    parts: dict[str, bytes] = {}
    for item in message.iter_parts():
        name = item.get_param(
            "name", header="content-disposition"
        )
        if not isinstance(name, str) or name not in {
            "manifest",
            "artifact",
        }:
            continue
        if name in parts:
            raise ValueError(
                f"Multipart field {name!r} may appear only once."
            )
        payload = item.get_payload(decode=True)
        if not isinstance(payload, bytes):
            raise ValueError(
                f"Multipart field {name!r} is malformed."
            )
        parts[name] = payload
    if "manifest" not in parts:
        raise ValueError(
            "Record-artifact ingest requires a manifest form field."
        )
    if "artifact" not in parts:
        raise ValueError(
            "Record-artifact ingest requires an artifact file part."
        )
    try:
        manifest_value = json.loads(parts["manifest"])
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(
            "Record-artifact ingest manifest is invalid JSON."
        ) from error
    return (
        _mapping(manifest_value, "record-artifact manifest"),
        parts["artifact"],
    )


def _json_result(
    value: object, status: int = 200
) -> RestOperationResult:
    return RestOperationResult(status, _wire(value))


def _found(
    value: object | None, label: str
) -> RestOperationResult:
    if value is None:
        raise RestNotFoundError(f"{label} was not found.")
    return _json_result(value)


def _wire(value: object) -> object:
    to_dict = getattr(value, "to_dict", None)
    if callable(to_dict):
        return _wire(to_dict())
    if is_dataclass(value) and not isinstance(value, type):
        return {
            field.name: _wire(getattr(value, field.name))
            for field in fields(value)
        }
    if isinstance(value, datetime):
        return _date(value)
    if isinstance(value, Mapping):
        return {
            str(name): _wire(item) for name, item in value.items()
        }
    if isinstance(value, (tuple, list)):
        return [_wire(item) for item in value]
    return value


def _mapping(value: object, label: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise TypeError(f"{label} must be an object.")
    return cast(Mapping[str, Any], value)


def _sequence(value: object, label: str) -> Sequence[object]:
    if (
        not isinstance(value, Sequence)
        or isinstance(value, (str, bytes, bytearray))
    ):
        raise TypeError(f"{label} must be an array.")
    return cast(Sequence[object], value)


def _required(value: Mapping[str, str], name: str) -> str:
    selected = value.get(name)
    if selected is None or not selected.strip():
        raise TypeError(f"{name} is required.")
    return selected


def _text(value: Mapping[str, Any], name: str) -> str:
    selected = value.get(name)
    if not isinstance(selected, str) or not selected.strip():
        raise TypeError(f"{name} must be a non-empty string.")
    return selected.strip()


def _with(
    value: Mapping[str, Any], **fields_to_add: object
) -> dict[str, Any]:
    result = dict(value)
    for name, item in fields_to_add.items():
        existing = result.get(name)
        if existing not in {None, "", item}:
            raise ValueError(
                f"{name} must match the route value."
            )
        result[name] = item
    return result


def _same(
    value: Mapping[str, Any], name: str, expected: str
) -> None:
    selected = value.get(name)
    if selected not in {None, "", expected}:
        raise ValueError(f"{name} must match the route value.")


def _integer(value: object, fallback: int | None) -> int | None:
    if value is None or value == "":
        return fallback
    if isinstance(value, bool):
        raise TypeError("Integer value must not be boolean.")
    try:
        return int(cast(str | int, value))
    except (TypeError, ValueError) as error:
        raise TypeError("Value must be an integer.") from error


def _boolean(value: object, fallback: bool) -> bool:
    if value is None or value == "":
        return fallback
    if isinstance(value, bool):
        return value
    if isinstance(value, str) and value.lower() in {"true", "false"}:
        return value.lower() == "true"
    raise TypeError("Value must be a boolean.")


def _datetime(value: object) -> datetime | None:
    if value is None or value == "":
        return None
    if isinstance(value, datetime):
        selected = value
    elif isinstance(value, str):
        try:
            selected = parse_iso_datetime(value)
        except ValueError as error:
            raise ValueError(
                "Value must be an ISO 8601 date-time."
            ) from error
    else:
        raise TypeError("Value must be an ISO 8601 date-time.")
    if selected.tzinfo is None:
        raise ValueError("Date-time value must include an offset.")
    return selected


def _date(value: datetime | None) -> JSONValue:
    if value is None:
        return None
    return value.isoformat().replace("+00:00", "Z")


def _now() -> str:
    return cast(str, _date(datetime.now(timezone.utc)))


__all__ = [
    "RestNotFoundError",
    "RestOperationDispatcher",
    "RestOperationResult",
    "RestOperationUnavailableError",
]
