from __future__ import annotations

from typing import Any, Mapping, Sequence

from ..async_runtime import RuntimeExecutor
from .models import (
    RecordBatchResult,
    RecordCollectionPolicy,
    RecordWritePrecondition,
    VyralRecord,
)
from .query_models import (
    QueryEnvelope,
    RecordQueryResult,
    RecordSearchResult,
    VyralRecordMatch,
)
from .record_store import SQLiteRecordStore, SQLiteStorageDiagnostics
from .snapshots import (
    CollectionExportEnvelope,
    CollectionExportRequest,
    CollectionImportRequest,
    CollectionImportResult,
)


class AsyncSQLiteRecordStore:
    """Async facade that submits complete storage operations to one executor."""

    def __init__(
        self,
        store: SQLiteRecordStore,
        *,
        executor: RuntimeExecutor | None = None,
    ) -> None:
        self.store = store
        self.executor = executor or RuntimeExecutor()
        self._owns_executor = executor is None

    async def create_collection(
        self,
        policy: RecordCollectionPolicy | Mapping[str, Any],
    ) -> None:
        await self.executor.run(lambda: self.store.create_collection(policy))

    async def list_collections(self) -> tuple[str, ...]:
        return await self.executor.run(self.store.list_collections)

    async def get_collection_policy(
        self,
        collection: str,
    ) -> RecordCollectionPolicy | None:
        return await self.executor.run(
            lambda: self.store.get_collection_policy(collection)
        )

    async def delete_collection(self, collection: str) -> None:
        await self.executor.run(lambda: self.store.delete_collection(collection))

    async def upsert_record(
        self,
        collection: str,
        record: VyralRecord | Mapping[str, Any],
        precondition: RecordWritePrecondition | Mapping[str, Any] | None = None,
    ) -> VyralRecord:
        return await self.executor.run(
            lambda: self.store.upsert_record(
                collection,
                record,
                precondition,
            )
        )

    async def upsert_records(
        self,
        collection: str,
        records: Sequence[VyralRecord | Mapping[str, Any]],
        *,
        preconditions: Sequence[
            RecordWritePrecondition | Mapping[str, Any] | None
        ] = (),
        continue_on_error: bool = False,
    ) -> RecordBatchResult:
        return await self.executor.run(
            lambda: self.store.upsert_records(
                collection,
                records,
                preconditions=preconditions,
                continue_on_error=continue_on_error,
            )
        )

    async def get_record(
        self,
        collection: str,
        partition_key: str,
        record_id: str,
    ) -> VyralRecord | None:
        return await self.executor.run(
            lambda: self.store.get_record(
                collection,
                partition_key,
                record_id,
            )
        )

    async def delete_record(
        self,
        collection: str,
        partition_key: str,
        record_id: str,
    ) -> None:
        await self.executor.run(
            lambda: self.store.delete_record(
                collection,
                partition_key,
                record_id,
            )
        )

    async def query_records_page(
        self,
        collection: str,
        query: QueryEnvelope | Mapping[str, Any] | None = None,
    ) -> RecordQueryResult:
        return await self.executor.run(
            lambda: self.store.query_records_page(collection, query)
        )

    async def query_all_records(
        self,
        collection: str,
        query: QueryEnvelope | Mapping[str, Any] | None = None,
    ) -> tuple[VyralRecord, ...]:
        return await self.executor.run(
            lambda: self.store.query_all_records(collection, query)
        )

    async def search_records_page(
        self,
        collection: str,
        query: QueryEnvelope | Mapping[str, Any],
    ) -> RecordSearchResult:
        return await self.executor.run(
            lambda: self.store.search_records_page(collection, query)
        )

    async def search_all_records(
        self,
        collection: str,
        query: QueryEnvelope | Mapping[str, Any],
    ) -> tuple[VyralRecordMatch, ...]:
        return await self.executor.run(
            lambda: self.store.search_all_records(collection, query)
        )

    async def export_collection(
        self,
        collection: str,
        request: CollectionExportRequest | Mapping[str, Any] | None = None,
    ) -> CollectionExportEnvelope | None:
        return await self.executor.run(
            lambda: self.store.export_collection(collection, request)
        )

    async def import_collection(
        self,
        target_collection: str,
        request: CollectionImportRequest | Mapping[str, Any],
    ) -> CollectionImportResult:
        return await self.executor.run(
            lambda: self.store.import_collection(target_collection, request)
        )

    async def diagnostics(self) -> SQLiteStorageDiagnostics:
        return await self.executor.run(self.store.diagnostics)

    def close(self) -> None:
        if self._owns_executor:
            self.executor.close()
