from __future__ import annotations

import asyncio
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Iterable, Mapping, TypeVar

from .async_runtime import RuntimeExecutor
from .canonical import AsyncSQLiteCanonicalStore, SQLiteCanonicalStore
from .conformance import run_bundled_goldens
from .contracts import ContractBundle, JSONValue, load_contract_bundle
from .embeddings import (
    EmbeddingProvider,
    EmbeddingProviderOptions,
    EmbeddingProviderRegistry,
    EmbeddingService,
)
from .execution import (
    create_runtime_job_plugin,
    ExecutionHandlerDescriptor,
    ExecutionProductPolicy,
    LocalExecutionRuntime,
    LocalExecutionRuntimeOptions,
    StaticExecutionPlugin,
)
from .local import (
    AsyncSQLiteRecordStore,
    FileObjectStore,
    SQLiteRecordStore,
    SQLiteTraceStore,
)
from .graph import GraphService
from .rag import RagContextService, RagIngestionService, RagPromptService
from .readiness import RuntimeReadiness, get_readiness
from .storage import StorageSchemaReceipt, ensure_storage_schema
from .retrieval import (
    RerankingService,
    RetrievalEvaluationService,
    RetrievalService,
)


T = TypeVar("T")


@dataclass(frozen=True)
class LocalRuntimeConfig:
    """Configuration for one embedded, single-node runtime instance."""

    root_path: Path
    database_name: str = "vyral.sqlite"
    object_directory_name: str = "objects"
    busy_timeout_ms: int = 5_000
    max_workers: int = 4
    max_pending: int = 64
    execution_max_active_runs: int = 100
    execution_max_retained_terminal_runs: int = 500
    embedding_options: EmbeddingProviderOptions = EmbeddingProviderOptions()

    def __post_init__(self) -> None:
        root = Path(self.root_path).expanduser().resolve()
        if not self.database_name.strip():
            raise ValueError("database_name is required.")
        if Path(self.database_name).name != self.database_name:
            raise ValueError("database_name must be a portable file name.")
        if not self.object_directory_name.strip():
            raise ValueError("object_directory_name is required.")
        if Path(self.object_directory_name).name != self.object_directory_name:
            raise ValueError(
                "object_directory_name must be a portable directory name."
            )
        if self.database_name in {".", ".."}:
            raise ValueError("database_name must be a portable file name.")
        if self.object_directory_name in {".", ".."}:
            raise ValueError(
                "object_directory_name must be a portable directory name."
            )
        if isinstance(self.busy_timeout_ms, bool) or self.busy_timeout_ms < 0:
            raise ValueError("busy_timeout_ms must be a non-negative integer.")
        if isinstance(self.max_workers, bool) or self.max_workers <= 0:
            raise ValueError("max_workers must be greater than zero.")
        if isinstance(self.max_pending, bool) or self.max_pending < self.max_workers:
            raise ValueError("max_pending must be at least max_workers.")
        if (
            isinstance(self.execution_max_active_runs, bool)
            or self.execution_max_active_runs <= 0
        ):
            raise ValueError(
                "execution_max_active_runs must be greater than zero."
            )
        if (
            isinstance(
                self.execution_max_retained_terminal_runs, bool
            )
            or self.execution_max_retained_terminal_runs < 0
        ):
            raise ValueError(
                "execution_max_retained_terminal_runs must be "
                "non-negative."
            )
        object.__setattr__(self, "root_path", root)
        object.__setattr__(
            self,
            "embedding_options",
            EmbeddingProviderOptions.from_value(self.embedding_options),
        )

    @classmethod
    def from_value(
        cls,
        value: LocalRuntimeConfig | str | Path | Mapping[str, Any],
    ) -> LocalRuntimeConfig:
        if isinstance(value, cls):
            return value
        if isinstance(value, (str, Path)):
            return cls(root_path=Path(value))
        if not isinstance(value, Mapping):
            raise TypeError("local runtime configuration must be a path or object.")
        allowed = {
            "rootPath",
            "databaseName",
            "objectDirectoryName",
            "busyTimeoutMs",
            "maxWorkers",
            "maxPending",
            "executionMaxActiveRuns",
            "executionMaxRetainedTerminalRuns",
            "embedding",
        }
        unknown = sorted(str(key) for key in value if key not in allowed)
        if unknown:
            raise ValueError(
                "Unknown local runtime configuration fields: " + ", ".join(unknown)
            )
        root = value.get("rootPath")
        if not isinstance(root, (str, Path)):
            raise TypeError("local runtime rootPath must be a path.")
        database_name = value.get("databaseName", "vyral.sqlite")
        object_directory_name = value.get("objectDirectoryName", "objects")
        busy_timeout_ms = value.get("busyTimeoutMs", 5_000)
        max_workers = value.get("maxWorkers", 4)
        max_pending = value.get("maxPending", 64)
        execution_max_active_runs = value.get(
            "executionMaxActiveRuns", 100
        )
        execution_max_retained_terminal_runs = value.get(
            "executionMaxRetainedTerminalRuns", 500
        )
        if not isinstance(database_name, str):
            raise TypeError("local runtime databaseName must be a string.")
        if not isinstance(object_directory_name, str):
            raise TypeError("local runtime objectDirectoryName must be a string.")
        if isinstance(busy_timeout_ms, bool) or not isinstance(busy_timeout_ms, int):
            raise TypeError("local runtime busyTimeoutMs must be an integer.")
        if isinstance(max_workers, bool) or not isinstance(max_workers, int):
            raise TypeError("local runtime maxWorkers must be an integer.")
        if isinstance(max_pending, bool) or not isinstance(max_pending, int):
            raise TypeError("local runtime maxPending must be an integer.")
        if (
            isinstance(execution_max_active_runs, bool)
            or not isinstance(execution_max_active_runs, int)
        ):
            raise TypeError(
                "local runtime executionMaxActiveRuns must be an integer."
            )
        if (
            isinstance(execution_max_retained_terminal_runs, bool)
            or not isinstance(
                execution_max_retained_terminal_runs, int
            )
        ):
            raise TypeError(
                "local runtime executionMaxRetainedTerminalRuns must "
                "be an integer."
            )
        return cls(
            root_path=Path(root),
            database_name=database_name,
            object_directory_name=object_directory_name,
            busy_timeout_ms=busy_timeout_ms,
            max_workers=max_workers,
            max_pending=max_pending,
            execution_max_active_runs=execution_max_active_runs,
            execution_max_retained_terminal_runs=(
                execution_max_retained_terminal_runs
            ),
            embedding_options=EmbeddingProviderOptions.from_value(
                value.get("embedding")
            ),
        )

    @property
    def database_path(self) -> Path:
        return self.root_path / self.database_name

    @property
    def object_root_path(self) -> Path:
        return self.root_path / self.object_directory_name


class VyralRuntime:
    """Composition root for contracts-only or embedded local operation.

    Constructing without a configuration preserves the lightweight contract
    inspection path. Supplying a local configuration opens all currently
    implemented embedded services over one bounded executor and one provider.
    """

    def __init__(
        self,
        config: LocalRuntimeConfig | str | Path | Mapping[str, Any] | None = None,
        *,
        provider_registry: EmbeddingProviderRegistry | None = None,
        embedding_provider: EmbeddingProvider | None = None,
        reranker: RerankingService | None = None,
        execution_plugins: Iterable[StaticExecutionPlugin] = (),
        external_handlers: Iterable[
            ExecutionHandlerDescriptor
        ] = (),
        execution_product_policies: Iterable[
            ExecutionProductPolicy | Mapping[str, Any]
        ] = (),
        register_builtin_jobs: bool = True,
        verify_assets: bool = False,
    ) -> None:
        if not isinstance(register_builtin_jobs, bool):
            raise TypeError("register_builtin_jobs must be a boolean.")
        if not isinstance(verify_assets, bool):
            raise TypeError("verify_assets must be a boolean.")
        self._contracts = load_contract_bundle()
        # Full fixture execution belongs to explicit qualification, readiness,
        # and host construction. Embedded construction stays proportional to
        # opening the requested local services instead of re-running every
        # bundled golden for each notebook/runtime instance.
        if verify_assets:
            run_bundled_goldens()
        selected_execution_plugins = tuple(execution_plugins)
        selected_external_handlers = tuple(external_handlers)
        selected_execution_product_policies = tuple(
            ExecutionProductPolicy.from_value(policy)
            for policy in execution_product_policies
        )
        self._config = (
            LocalRuntimeConfig.from_value(config) if config is not None else None
        )
        self._storage_schema_receipt: StorageSchemaReceipt | None = None
        self._closed = False
        self._executor: RuntimeExecutor | None = None
        self._records: SQLiteRecordStore | None = None
        self._async_records: AsyncSQLiteRecordStore | None = None
        self._canonical: SQLiteCanonicalStore | None = None
        self._async_canonical: AsyncSQLiteCanonicalStore | None = None
        self._objects: FileObjectStore | None = None
        self._traces: SQLiteTraceStore | None = None
        self._execution: LocalExecutionRuntime | None = None
        self._graph: GraphService | None = None
        self._embeddings: EmbeddingService | None = None
        self._retrieval: RetrievalService | None = None
        self._retrieval_evaluation: RetrievalEvaluationService | None = None
        self._rag_ingestion: RagIngestionService | None = None
        self._rag_context: RagContextService | None = None
        self._rag_prompts: RagPromptService | None = None
        if self._config is None:
            if (
                embedding_provider is not None
                or reranker is not None
                or selected_execution_plugins
                or selected_external_handlers
                or selected_execution_product_policies
            ):
                raise ValueError(
                    "A local runtime configuration is required for service adapters."
                )
            return

        self._config.root_path.mkdir(parents=True, exist_ok=True)
        storage_schema_receipt = ensure_storage_schema(
            self._config.database_path,
            busy_timeout_ms=self._config.busy_timeout_ms,
        )
        executor = RuntimeExecutor(
            max_workers=self._config.max_workers,
            max_pending=self._config.max_pending,
        )
        try:
            records = SQLiteRecordStore(
                self._config.database_path,
                busy_timeout_ms=self._config.busy_timeout_ms,
            )
            async_records = AsyncSQLiteRecordStore(records, executor=executor)
            canonical = SQLiteCanonicalStore(
                self._config.database_path,
                busy_timeout_ms=self._config.busy_timeout_ms,
            )
            async_canonical = AsyncSQLiteCanonicalStore(
                canonical,
                executor=executor,
            )
            objects = FileObjectStore(
                self._config.object_root_path,
                executor=executor,
            )
            traces = SQLiteTraceStore(
                self._config.database_path,
                executor=executor,
            )
            execution = LocalExecutionRuntime(
                LocalExecutionRuntimeOptions(
                    database_path=self._config.database_path,
                    artifact_directory=(
                        self._config.root_path
                        / "execution-artifacts"
                    ),
                    max_active_runs=(
                        self._config.execution_max_active_runs
                    ),
                    max_retained_terminal_runs=(
                        self._config
                        .execution_max_retained_terminal_runs
                    ),
                    busy_timeout_ms=self._config.busy_timeout_ms,
                    product_policies=(
                        selected_execution_product_policies
                    ),
                )
            )
            graph = GraphService(records, executor=executor)
            provider = embedding_provider or (
                provider_registry or EmbeddingProviderRegistry()
            ).create(self._config.embedding_options)
            embeddings = EmbeddingService(
                provider,
                provider_options=self._config.embedding_options,
            )
            retrieval = RetrievalService(
                records,
                provider,
                embedding_options=self._config.embedding_options,
                reranker=reranker,
                trace_store=traces,
                executor=executor,
            )
            ingestion = RagIngestionService(
                records,
                provider,
                embedding_options=self._config.embedding_options,
                trace_store=traces,
                executor=executor,
            )
            evaluation = RetrievalEvaluationService(retrieval, executor=executor)
            context = RagContextService(
                retrieval,
                graph_service=graph,
                executor=executor,
            )
            prompts = RagPromptService(context, executor=executor)
            if register_builtin_jobs:
                execution.register_plugin(
                    create_runtime_job_plugin(
                        records=async_records,
                        objects=objects,
                        embeddings=embeddings,
                        retrieval_evaluation=evaluation,
                        rag_ingestion=ingestion,
                        graph=graph,
                    )
                )
            for plugin in selected_execution_plugins:
                execution.register_plugin(plugin)
            for descriptor in selected_external_handlers:
                execution.register_external_handler(descriptor)
        except BaseException:
            executor.close()
            raise
        self._executor = executor
        self._storage_schema_receipt = storage_schema_receipt
        self._records = records
        self._async_records = async_records
        self._canonical = canonical
        self._async_canonical = async_canonical
        self._objects = objects
        self._traces = traces
        self._execution = execution
        self._graph = graph
        self._embeddings = embeddings
        self._retrieval = retrieval
        self._retrieval_evaluation = evaluation
        self._rag_ingestion = ingestion
        self._rag_context = context
        self._rag_prompts = prompts

    @classmethod
    def open_local(
        cls,
        root_path: str | Path,
        **kwargs: Any,
    ) -> VyralRuntime:
        return cls(LocalRuntimeConfig(Path(root_path)), **kwargs)

    @property
    def contracts(self) -> ContractBundle:
        return self._contracts

    @property
    def config(self) -> LocalRuntimeConfig | None:
        return self._config

    @property
    def storage_schema_receipt(self) -> StorageSchemaReceipt:
        return self._required(
            self._storage_schema_receipt,
            "storage schema receipt",
        )

    @property
    def records(self) -> SQLiteRecordStore:
        return self._required(self._records, "records")

    @property
    def async_records(self) -> AsyncSQLiteRecordStore:
        return self._required(self._async_records, "async records")

    @property
    def canonical(self) -> SQLiteCanonicalStore:
        return self._required(self._canonical, "canonical store")

    @property
    def async_canonical(self) -> AsyncSQLiteCanonicalStore:
        return self._required(
            self._async_canonical, "async canonical store"
        )

    @property
    def objects(self) -> FileObjectStore:
        return self._required(self._objects, "objects")

    @property
    def traces(self) -> SQLiteTraceStore:
        return self._required(self._traces, "traces")

    @property
    def execution(self) -> LocalExecutionRuntime:
        return self._required(self._execution, "execution")

    @property
    def graph(self) -> GraphService:
        return self._required(self._graph, "graph")

    @property
    def embeddings(self) -> EmbeddingService:
        return self._required(self._embeddings, "embeddings")

    @property
    def retrieval(self) -> RetrievalService:
        return self._required(self._retrieval, "retrieval")

    @property
    def rag_ingestion(self) -> RagIngestionService:
        return self._required(self._rag_ingestion, "RAG ingestion")

    @property
    def retrieval_evaluation(self) -> RetrievalEvaluationService:
        return self._required(
            self._retrieval_evaluation, "retrieval evaluation"
        )

    @property
    def rag_context(self) -> RagContextService:
        return self._required(self._rag_context, "RAG context")

    @property
    def rag_prompts(self) -> RagPromptService:
        return self._required(self._rag_prompts, "RAG prompts")

    def readiness(self) -> RuntimeReadiness:
        readiness = get_readiness()
        if self._config is None:
            return readiness
        self._ensure_open()
        storage = self.records.diagnostics()
        canonical = self.canonical.diagnostics()
        execution = self.execution.diagnostics()
        objects = self.objects.diagnostics()
        provider = self.embeddings.provider
        checks = list(readiness.checks)
        checks.extend(
            (
                {
                    "id": "local.storage-schema",
                    "status": "passed",
                    "message": (
                        "The composed portable-local storage schema is "
                        "supported and current."
                    ),
                    "details": self.storage_schema_receipt.to_dict(),
                },
                {
                    "id": "local.execution",
                    "status": (
                        "passed"
                        if bool(execution["healthy"])
                        else "failed"
                    ),
                    "message": (
                        "SQLite durable execution state and recovery "
                        "diagnostics are healthy."
                        if bool(execution["healthy"])
                        else "SQLite durable execution diagnostics "
                        "failed."
                    ),
                    "details": execution,
                },
                {
                    "id": "local.canonical",
                    "status": (
                        "passed"
                        if bool(canonical["healthy"])
                        else "failed"
                    ),
                    "message": (
                        "SQLite CanonicalStore integrity, foreign keys, "
                        "and WAL are healthy."
                        if bool(canonical["healthy"])
                        else "SQLite CanonicalStore diagnostics failed."
                    ),
                    "details": canonical,
                },
                {
                    "id": "local.sqlite",
                    "status": "passed" if storage.healthy else "failed",
                    "message": (
                        "SQLite integrity, foreign keys, WAL, and FTS5 are healthy."
                        if storage.healthy
                        else "SQLite local storage diagnostics failed."
                    ),
                    "details": storage.to_dict(),
                },
                {
                    "id": "local.objects",
                    "status": "passed" if objects.healthy else "failed",
                    "message": (
                        "Filesystem object storage is healthy."
                        if objects.healthy
                        else "Filesystem object storage diagnostics failed."
                    ),
                    "details": objects.to_dict(),
                },
                {
                    "id": "local.embedding-provider",
                    "status": "passed",
                    "message": (
                        f"Embedding provider {provider.provider_id!r} is configured "
                        f"with {provider.dimensions} dimensions."
                    ),
                },
            )
        )
        local_blockers = []
        if not storage.healthy:
            local_blockers.append("SQLite local storage diagnostics failed.")
        if not bool(canonical["healthy"]):
            local_blockers.append(
                "SQLite CanonicalStore diagnostics failed."
            )
        if not bool(execution["healthy"]):
            local_blockers.append(
                "SQLite durable execution diagnostics failed."
            )
        if not objects.healthy:
            local_blockers.append("Filesystem object storage diagnostics failed.")
        return replace(
            readiness,
            status="blocked" if readiness.blockers or local_blockers else "ok",
            checks=tuple(checks),
            blockers=readiness.blockers + tuple(local_blockers),
        )

    async def areadiness(self) -> RuntimeReadiness:
        """Run readiness probes without nesting their async fixtures."""
        return await asyncio.to_thread(self.readiness)

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        if self._executor is not None:
            self._executor.close()

    def __enter__(self) -> VyralRuntime:
        self._ensure_open()
        return self

    def __exit__(
        self,
        exc_type: object,
        exc: object,
        traceback: object,
    ) -> None:
        self.close()

    def _ensure_open(self) -> None:
        if self._closed:
            raise RuntimeError("The Vyral runtime is closed.")

    def _required(self, value: T | None, name: str) -> T:
        self._ensure_open()
        if value is None:
            raise RuntimeError(
                f"The {name} service requires an embedded local configuration."
            )
        return value
