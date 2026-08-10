from __future__ import annotations

import asyncio
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
import shutil

from .embeddings import EmbeddingProviderRegistry
from .execution import (
    ExecutionRunContext,
    ExecutionRunRequest,
    ExecutionRunResult,
    execution_plugin,
    vyral,
)
from .local import (
    LexicalSearchOptions,
    RecordCollectionPolicy,
    VectorFieldPolicy,
)
from .rag import (
    RagContextRequest,
    RagIngestTextRequest,
    RagIngestionOptions,
)
from .retrieval import EmbeddingOptions, HybridSearchOptions, RetrievalRequest
from .runtime import VyralRuntime


_QUICKSTART_MARKER_NAME = ".vyral-local-quickstart"
_QUICKSTART_MARKER_VALUE = "vyral.local-quickstart.v1\n"
_COLLECTION = "vyral-quickstart"
_PARTITION = "local"
_PLUGIN_ID = "vyral.quickstart"
_HANDLER_ID = "vyral.quickstart.persist"
_IDEMPOTENCY_KEY = "vyral.local-quickstart.persist.v1"
_QUERY = "How does Vyral preserve accepted work across a process restart?"
_DOCUMENTS = (
    (
        "contracts",
        "Stable contracts",
        (
            "Vyral keeps records, retrieval, and durable execution behind "
            "application-owned contracts. Local SQLite and filesystem adapters "
            "let one developer exercise those boundaries without a cloud account."
        ),
        "https://docs.openvyral.com/",
    ),
    (
        "durable-execution",
        "Durable execution",
        (
            "Durable execution accepts work with an idempotency key and persists "
            "the run receipt before a worker executes the handler. Reopening the "
            "same local data directory preserves the run identity, lifecycle, "
            "checkpoint, artifacts, and terminal result across a process restart."
        ),
        "https://docs.openvyral.com/architecture/execution-runtime/",
    ),
    (
        "evidence",
        "Qualification evidence",
        (
            "Vyral separates implementation from qualification. Readiness names "
            "the active local providers and topology, while conformance evidence "
            "limits portability and maturity claims to behavior that was tested."
        ),
        "https://docs.openvyral.com/evidence/qualification/",
    ),
)


@dataclass(frozen=True)
class LocalQuickstartCitation:
    label: str
    uri: str | None
    record_id: str

    def to_dict(self) -> dict[str, object]:
        return {
            "label": self.label,
            "uri": self.uri,
            "recordId": self.record_id,
        }


@dataclass(frozen=True)
class LocalQuickstartResult:
    root_path: str
    runtime_version: str
    contract_version: str
    maturity: str
    full_local_ready: bool
    embedding_provider: str
    embedding_model: str
    embedding_dimensions: int
    embedding_semantic_quality: str
    query: str
    context_text: str
    context_hash: str
    citations: tuple[LocalQuickstartCitation, ...]
    created_chunks: int
    reused_chunks: int
    run_id: str
    admitted_status: str
    admission_replayed: bool
    persisted_status: str
    completed_status: str
    completed_result: object
    dispatched_runs: int

    def to_dict(self) -> dict[str, object]:
        return {
            "rootPath": self.root_path,
            "runtimeVersion": self.runtime_version,
            "contractVersion": self.contract_version,
            "maturity": self.maturity,
            "fullLocalReady": self.full_local_ready,
            "topology": "local-single-node",
            "embedding": {
                "provider": self.embedding_provider,
                "model": self.embedding_model,
                "dimensions": self.embedding_dimensions,
                "semanticQuality": self.embedding_semantic_quality,
                "requiresNetwork": False,
            },
            "retrieval": {
                "query": self.query,
                "contextText": self.context_text,
                "contextHash": self.context_hash,
                "citations": [citation.to_dict() for citation in self.citations],
                "createdChunks": self.created_chunks,
                "reusedChunks": self.reused_chunks,
            },
            "execution": {
                "runId": self.run_id,
                "idempotencyKey": _IDEMPOTENCY_KEY,
                "admittedStatus": self.admitted_status,
                "admissionReplayed": self.admission_replayed,
                "persistedStatusAfterReopen": self.persisted_status,
                "completedStatus": self.completed_status,
                "completedResult": self.completed_result,
                "dispatchedRuns": self.dispatched_runs,
            },
        }


@vyral(
    _HANDLER_ID,
    plugin=_PLUGIN_ID,
    name="Persist accepted local work",
    max_attempts=2,
)
async def _persist_accepted_work(
    context: ExecutionRunContext,
) -> ExecutionRunResult:
    await context.record_event(
        "quickstart.persisted",
        message="The accepted quickstart run resumed from durable local state.",
    )
    return ExecutionRunResult.succeeded_result(
        {
            "message": "accepted work survived the runtime restart",
            "payload": context.run.payload,
        }
    )


_QUICKSTART_PLUGIN = execution_plugin(
    _PLUGIN_ID,
    name="Vyral local quickstart",
    version="1.0.0",
    handlers=(_persist_accepted_work,),
)


async def run_local_quickstart(
    root_path: str | Path,
    *,
    emit: Callable[[str], None] | None = None,
) -> LocalQuickstartResult:
    root = prepare_local_quickstart_root(root_path)
    created_chunks = 0
    reused_chunks = 0

    _emit(emit, f"Local state: {root}")
    with VyralRuntime.open_local(
        root,
        execution_plugins=(_QUICKSTART_PLUGIN,),
    ) as runtime:
        # Readiness executes synchronous wrappers around the bundled async
        # conformance runners. Keep it off this command's event-loop thread.
        readiness = await asyncio.to_thread(runtime.readiness)
        provider = runtime.embeddings.provider
        descriptor = next(
            item
            for item in EmbeddingProviderRegistry().get_providers()
            if item.provider == provider.provider_id
        )
        _emit(
            emit,
            (
                f"Ready: Python {readiness.runtime_version}, contract "
                f"{readiness.contract_version}, maturity {readiness.maturity}, "
                "topology local-single-node"
            ),
        )
        _emit(
            emit,
            (
                f"Embedding: {provider.provider_id}/{provider.model_id} "
                f"({provider.dimensions} dimensions, "
                f"{descriptor.semantic_quality}, no network)"
            ),
        )

        runtime.records.create_collection(
            RecordCollectionPolicy(
                name=_COLLECTION,
                vector_policies=(
                    VectorFieldPolicy(
                        name="contentEmbedding",
                        path="/vectors/contentEmbedding/values",
                        dimensions=provider.dimensions,
                    ),
                ),
                indexed_metadata=("/metadata/topic",),
            )
        )
        for document_id, label, text, uri in _DOCUMENTS:
            ingestion = runtime.rag_ingestion.ingest_text(
                _COLLECTION,
                RagIngestTextRequest(
                    document_id=document_id,
                    partition_key=_PARTITION,
                    text=text,
                    embedding=EmbeddingOptions(
                        field="contentEmbedding",
                        purpose="passage",
                    ),
                    metadata={"topic": document_id},
                    source_uri=uri,
                    source_kind="documentation",
                    source_label=label,
                    options=RagIngestionOptions(
                        chunk_chars=600,
                        chunk_overlap_chars=0,
                        replace_document_chunks=True,
                        skip_unchanged_chunks=True,
                        persist_manifest=True,
                    ),
                ),
            )
            created_chunks += ingestion.created_count
            reused_chunks += ingestion.reused_count

        context = runtime.rag_context.build_context(
            RagContextRequest(
                retrieval=RetrievalRequest(
                    query=_QUERY,
                    collections=(_COLLECTION,),
                    partition_keys=(_PARTITION,),
                    search_mode="hybrid",
                    embedding=EmbeddingOptions(
                        field="contentEmbedding",
                        purpose="query",
                    ),
                    lexical=LexicalSearchOptions(
                        fields=("/content/text",),
                    ),
                    hybrid=HybridSearchOptions(
                        vector_weight=0.4,
                        lexical_weight=0.6,
                        fusion="weighted",
                    ),
                    limit=3,
                    include_trace=True,
                ),
                max_chars=1_500,
                max_chars_per_chunk=520,
                include_citations=True,
                include_context_text=True,
                include_trace=True,
            )
        )
        if not context.citations or not context.context_text:
            raise RuntimeError(
                "The local quickstart did not produce citation-ready context."
            )
        citations = tuple(
            LocalQuickstartCitation(
                citation.source_label or citation.record_id,
                citation.source_uri,
                citation.record_id,
            )
            for citation in context.citations
        )
        _emit(
            emit,
            (
                f"Retrieved {len(context.chunks)} chunks with "
                f"{len(citations)} citations. Token-hash rankings are "
                "model-free local-development evidence, not semantic-model quality."
            ),
        )

        request = ExecutionRunRequest(
            _HANDLER_ID,
            plugin_id=_PLUGIN_ID,
            payload={"source": "vyral-local-quickstart", "version": 1},
            idempotency_key=_IDEMPOTENCY_KEY,
            tags={"surface": "local-quickstart"},
        )
        admitted = await runtime.execution.start_run(request)
        replay = await runtime.execution.start_run(request)
        if admitted.id != replay.id:
            raise RuntimeError(
                "Durable admission did not preserve the idempotent run identity."
            )
        _emit(
            emit,
            (
                f"Accepted receipt: run {admitted.id} is {admitted.status}; "
                "the handler has not been dispatched."
            ),
        )

    _emit(emit, "Closed the first runtime instance.")
    with VyralRuntime.open_local(
        root,
        execution_plugins=(_QUICKSTART_PLUGIN,),
    ) as reopened:
        persisted = await reopened.execution.get_run(admitted.id)
        if persisted is None or persisted.id != admitted.id:
            raise RuntimeError(
                "The admitted run was not present after reopening local state."
            )
        _emit(
            emit,
            (
                f"Reopened the same directory: run {persisted.id} is "
                f"{persisted.status}."
            ),
        )
        dispatched = await reopened.execution.dispatch_ready_runs(
            recover_interrupted_runs=True
        )
        completed = await reopened.execution.get_run(admitted.id)
        if completed is None or completed.status != "succeeded":
            actual = completed.status if completed is not None else "missing"
            raise RuntimeError(
                f"The durable quickstart run did not succeed; status={actual}."
            )
        _emit(
            emit,
            (
                f"Completed: run {completed.id} is {completed.status} with "
                "the same durable identity."
            ),
        )

    return LocalQuickstartResult(
        root_path=str(root),
        runtime_version=readiness.runtime_version,
        contract_version=readiness.contract_version,
        maturity=readiness.maturity,
        full_local_ready=readiness.full_local_ready,
        embedding_provider=provider.provider_id,
        embedding_model=provider.model_id,
        embedding_dimensions=provider.dimensions,
        embedding_semantic_quality=descriptor.semantic_quality,
        query=_QUERY,
        context_text=context.context_text,
        context_hash=context.context_text_hash or "",
        citations=citations,
        created_chunks=created_chunks,
        reused_chunks=reused_chunks,
        run_id=completed.id,
        admitted_status=admitted.status,
        admission_replayed=replay.admission_replayed,
        persisted_status=persisted.status,
        completed_status=completed.status,
        completed_result=completed.result,
        dispatched_runs=dispatched,
    )


def inspect_local_runtime(root_path: str | Path) -> dict[str, object]:
    root = Path(root_path).expanduser().resolve()
    if not root.is_dir():
        raise ValueError(f"Local runtime directory does not exist: {root}")
    with VyralRuntime.open_local(root) as runtime:
        readiness = runtime.readiness()
        provider = runtime.embeddings.provider
        descriptor = next(
            item
            for item in EmbeddingProviderRegistry().get_providers()
            if item.provider == provider.provider_id
        )
        storage = runtime.records.diagnostics()
        execution = runtime.execution.diagnostics()
        objects = runtime.objects.diagnostics()
        return {
            "rootPath": str(root),
            "topology": "local-single-node",
            "runtime": {
                "version": readiness.runtime_version,
                "contractVersion": readiness.contract_version,
                "status": readiness.status,
                "maturity": readiness.maturity,
                "fullLocalReady": readiness.full_local_ready,
            },
            "providers": {
                "records": {
                    "adapter": "SQLiteRecordStore",
                    "healthy": storage.healthy,
                    "databasePath": str(runtime.config.database_path)
                    if runtime.config is not None
                    else None,
                },
                "objects": {
                    "adapter": "FileObjectStore",
                    "healthy": objects.healthy,
                    "rootPath": str(runtime.config.object_root_path)
                    if runtime.config is not None
                    else None,
                },
                "embeddings": {
                    "provider": provider.provider_id,
                    "model": provider.model_id,
                    "dimensions": provider.dimensions,
                    "semanticQuality": descriptor.semantic_quality,
                    "requiresNetwork": descriptor.requires_network,
                },
                "execution": {
                    "adapter": "python-local-sqlite",
                    "healthy": bool(execution["healthy"]),
                    "activeRuns": execution["activeRuns"],
                },
            },
            "warnings": list(readiness.warnings),
            "blockers": list(readiness.blockers),
        }


def prepare_local_quickstart_root(root_path: str | Path) -> Path:
    requested = Path(root_path).expanduser()
    if requested.is_symlink():
        raise ValueError(
            "The local quickstart root must not be a symbolic link."
        )
    root = requested.resolve()
    _reject_broad_root(root)
    marker = root / _QUICKSTART_MARKER_NAME
    if root.exists():
        if not root.is_dir():
            raise ValueError(
                f"The local quickstart root is not a directory: {root}"
            )
        entries = tuple(root.iterdir())
        if entries and (
            not marker.is_file()
            or marker.is_symlink()
            or marker.read_text(encoding="utf-8")
            != _QUICKSTART_MARKER_VALUE
        ):
            raise ValueError(
                "The local quickstart requires a dedicated empty directory "
                f"or a directory it previously created: {root}"
            )
    else:
        root.mkdir(parents=True)
    marker.write_text(_QUICKSTART_MARKER_VALUE, encoding="utf-8")
    return root


def reset_local_quickstart(root_path: str | Path) -> Path:
    requested = Path(root_path).expanduser()
    if requested.is_symlink():
        raise ValueError(
            "The local quickstart root must not be a symbolic link."
        )
    root = requested.resolve()
    _reject_broad_root(root)
    marker = root / _QUICKSTART_MARKER_NAME
    if (
        not root.is_dir()
        or not marker.is_file()
        or marker.is_symlink()
        or marker.read_text(encoding="utf-8")
        != _QUICKSTART_MARKER_VALUE
    ):
        raise ValueError(
            "Refusing to reset a directory not created by the Vyral local "
            f"quickstart: {root}"
        )
    shutil.rmtree(root)
    return root


def _reject_broad_root(root: Path) -> None:
    broad = {
        Path(root.anchor),
        Path.home().resolve(),
        Path.cwd().resolve(),
    }
    if root in broad:
        raise ValueError(
            f"Refusing to use a broad local quickstart root: {root}"
        )


def _emit(emit: Callable[[str], None] | None, message: str) -> None:
    if emit is not None:
        emit(message)


def run_local_quickstart_sync(
    root_path: str | Path,
    *,
    emit: Callable[[str], None] | None = None,
) -> LocalQuickstartResult:
    return asyncio.run(run_local_quickstart(root_path, emit=emit))


__all__ = [
    "LocalQuickstartCitation",
    "LocalQuickstartResult",
    "inspect_local_runtime",
    "prepare_local_quickstart_root",
    "reset_local_quickstart",
    "run_local_quickstart",
    "run_local_quickstart_sync",
]
