from __future__ import annotations

from enum import Enum
from types import MappingProxyType
from typing import Mapping


class RestOperationFamily(str, Enum):
    """Stable implementation-owner groups for public REST operations."""

    SERVER = "server"
    CANONICAL = "canonical"
    RECORDS = "records"
    GRAPH = "graph"
    AI = "ai"
    EXECUTION = "execution"
    EXTERNAL_WORKER = "external-worker"
    JOB_START = "job-start"
    JOBS = "jobs"
    OBJECTS = "objects"
    TRACES = "traces"
    ARTIFACT_INGEST = "artifact-ingest"
    PROVIDERS = "providers"


REST_OPERATION_FAMILIES: Mapping[
    RestOperationFamily, frozenset[str]
] = MappingProxyType(
    {
        RestOperationFamily.SERVER: frozenset(
            {
                "getHealth",
                "getOpenApiContract",
                "getPublicSchemaContract",
                "getReadiness",
                "getServerInfo",
            }
        ),
        RestOperationFamily.CANONICAL: frozenset(
            {
                "acknowledgeCanonicalOutbox",
                "applyCanonicalMigrations",
                "commitCanonicalTransaction",
                "exportCanonicalTenant",
                "getCanonicalDocument",
                "leaseCanonicalOutbox",
                "listCanonicalDocumentRevisions",
                "listCanonicalMigrations",
                "preflightCanonicalStore",
                "probeCanonicalStoreDataPlane",
                "queryCanonicalDocumentRevisions",
                "queryCanonicalDocuments",
                "queryCanonicalOutbox",
                "readCanonicalDocument",
                "releaseCanonicalOutbox",
                "renewCanonicalOutboxLease",
                "replayCanonicalOutbox",
                "restoreCanonicalTenant",
            }
        ),
        RestOperationFamily.RECORDS: frozenset(
            {
                "createCollection",
                "deleteCollection",
                "deleteRecord",
                "exportCollection",
                "exportCollectionBounded",
                "getCollection",
                "getRecord",
                "importCollection",
                "inspectCollection",
                "listCollections",
                "queryRecords",
                "searchRecords",
                "upsertRecord",
                "upsertRecords",
            }
        ),
        RestOperationFamily.GRAPH: frozenset(
            {
                "doctorGraphCollection",
                "exportGraphEnvelope",
                "getGraphProviderShape",
                "importGraphEnvelope",
                "inspectGraphCollection",
                "listGraphProviderShapes",
                "preflightGraphImport",
                "traverseGraph",
            }
        ),
        RestOperationFamily.AI: frozenset(
            {
                "buildRagContext",
                "buildRagPrompt",
                "compareRetrievalEvaluations",
                "evaluateRagContext",
                "evaluateRetrieval",
                "generateEmbeddings",
                "getEmbeddingProviderDoctor",
                "ingestRagText",
                "ingestRagTextBatch",
                "listEmbeddingProviderGuidance",
                "listEmbeddingProviders",
                "listRetrievalProfiles",
                "retrieve",
            }
        ),
        RestOperationFamily.EXECUTION: frozenset(
            {
                "cancelExecutionRun",
                "getEffectiveExecutionRuntime",
                "getExecutionRun",
                "getExecutionRunArtifact",
                "getExecutionRunCheckpoint",
                "getExecutionRunHistory",
                "getExecutionRuntime",
                "getExecutionRuntimeMaintenance",
                "listExecutionRunArtifacts",
                "listExecutionRuns",
                "pruneExecutionRuntimeMaintenance",
                "raiseExecutionEvent",
                "reconcileExecutionRuntimeDispatch",
                "startExecutionRun",
            }
        ),
        RestOperationFamily.EXTERNAL_WORKER: frozenset(
            {
                "completeExternalExecutionLease",
                "getExternalExecutionLeaseCheckpoint",
                "heartbeatExternalExecutionLease",
                "leaseExternalExecutionRun",
                "putExternalExecutionLeaseArtifact",
                "putExternalExecutionLeaseCheckpoint",
                "recordExternalExecutionLeaseEvent",
                "reportExternalExecutionLease",
                "waitExternalExecutionLease",
            }
        ),
        RestOperationFamily.JOB_START: frozenset(
            {
                "startCollectionImportJob",
                "startEmbeddingJob",
                "startGraphDoctorJob",
                "startGraphImportJob",
                "startGraphInspectionJob",
                "startRagIngestionBatchJob",
                "startRagIngestionTextJob",
                "startRecordBatchUpsertJob",
                "startRetrievalEvaluationComparisonJob",
                "startRetrievalEvaluationJob",
            }
        ),
        RestOperationFamily.JOBS: frozenset(
            {
                "cancelEmbeddingJob",
                "cancelGraphJob",
                "cancelProviderRunJob",
                "cancelRagIngestionJob",
                "cancelRecordImportJob",
                "cancelRetrievalEvaluationJob",
                "getEmbeddingJob",
                "getGraphJob",
                "getProviderRunJob",
                "getRagIngestionJob",
                "getRecordImportJob",
                "getRetrievalEvaluationJob",
                "listEmbeddingJobs",
                "listGraphJobs",
                "listProviderRunJobs",
                "listRagIngestionJobs",
                "listRecordImportJobs",
                "listRetrievalEvaluationJobs",
            }
        ),
        RestOperationFamily.OBJECTS: frozenset(
            {"deleteObject", "getObject", "listObjects", "putObject"}
        ),
        RestOperationFamily.TRACES: frozenset(
            {
                "exportTraces",
                "getTrace",
                "listTraces",
                "pruneTraces",
                "summarizeTraces",
            }
        ),
        RestOperationFamily.ARTIFACT_INGEST: frozenset(
            {"ingestRecordArtifact"}
        ),
        RestOperationFamily.PROVIDERS: frozenset(
            {
                "getProvider",
                "getProviderCapabilityMatrix",
                "getProviderDoctor",
                "getProviderQuota",
                "getProviderReadiness",
                "listProviderDoctor",
                "listProviderModels",
                "listProviderQualifications",
                "listProviderQuotas",
                "listProviderReadiness",
                "listProviders",
                "qualifyProvider",
                "runProviderCapability",
                "startProviderRunJob",
            }
        ),
    }
)


def _build_registry() -> Mapping[str, RestOperationFamily]:
    registry: dict[str, RestOperationFamily] = {}
    for family, operation_ids in REST_OPERATION_FAMILIES.items():
        for operation_id in operation_ids:
            previous = registry.get(operation_id)
            if previous is not None:
                raise RuntimeError(
                    f"REST operation {operation_id!r} belongs to both "
                    f"{previous.value!r} and {family.value!r}."
                )
            registry[operation_id] = family
    return MappingProxyType(registry)


REST_OPERATION_REGISTRY = _build_registry()


def rest_operation_family(
    operation_id: str,
) -> RestOperationFamily | None:
    """Return the single implementation owner for an operation id."""

    return REST_OPERATION_REGISTRY.get(operation_id)
