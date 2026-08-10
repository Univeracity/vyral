from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from vyral_runtime import (
    EmbeddingOptions,
    LocalTokenHashEmbeddingProvider,
    RecordCollectionPolicy,
    SQLiteRecordStore,
    SQLiteTraceStore,
    VectorFieldPolicy,
)
from vyral_runtime.rag import (
    RagIngestTextRequest,
    RagIngestionOptions,
    RagIngestionService,
)


class RagIngestionServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = TemporaryDirectory()
        self.database_path = Path(self.temporary_directory.name) / "rag.sqlite"
        self.store = SQLiteRecordStore(
            self.database_path,
            clock=lambda: datetime(2026, 7, 30, 12, 0, tzinfo=timezone.utc),
        )
        self.store.create_collection(
            RecordCollectionPolicy(
                name="knowledge",
                vector_policies=(
                    VectorFieldPolicy(
                        name="contentEmbedding",
                        path="/vectors/contentEmbedding/values",
                        dimensions=64,
                    ),
                ),
                indexed_metadata=(
                    "/metadata/documentId",
                    "/metadata/textHash",
                    "/metadata/embeddingTextHash",
                ),
            )
        )
        self.provider = LocalTokenHashEmbeddingProvider(dimensions=64)
        self.traces = SQLiteTraceStore(self.database_path)
        self.service = RagIngestionService(
            self.store,
            self.provider,
            embedding_options={"passagePrefix": "passage: "},
            clock=lambda: datetime(2026, 7, 30, 12, 0, tzinfo=timezone.utc),
            trace_store=self.traces,
        )

    def tearDown(self) -> None:
        self.service.close()
        self.traces.close()
        self.temporary_directory.cleanup()

    def request(
        self,
        text: str,
        *,
        document_id: str = "runtime-guide",
        partition_key: str = "tenant-a",
        options: RagIngestionOptions | None = None,
    ) -> RagIngestTextRequest:
        return RagIngestTextRequest(
            document_id=document_id,
            partition_key=partition_key,
            text=text,
            embedding=EmbeddingOptions(
                field="contentEmbedding",
                purpose="passage",
            ),
            metadata={"title": "Runtime guide"},
            source_uri="https://example.test/runtime",
            source_kind="documentation",
            source_label="Runtime guide",
            options=options or RagIngestionOptions(chunk_chars=80, chunk_overlap_chars=10),
        )

    def test_dry_run_plan_matches_commit_and_manifest_is_raw_text_free(self) -> None:
        text = (
            "Vyral provides a Python runtime for local data and retrieval. "
            "MCP requests are stateless and self describing at the gateway. "
            "Durable execution state remains in the runtime."
        )
        options = RagIngestionOptions(
            chunk_chars=72,
            chunk_overlap_chars=8,
            dry_run=True,
            replace_document_chunks=True,
            persist_manifest=True,
            include_trace=True,
        )
        plan = self.service.ingest_text(
            "knowledge",
            self.request(text, options=options),
        )

        self.assertTrue(plan.dry_run)
        self.assertGreater(plan.chunk_count, 1)
        self.assertEqual(plan.chunk_count, plan.created_count)
        self.assertEqual(plan.chunk_count, plan.vector_generated_count)
        self.assertEqual(0, len(self.store.query_all_records("knowledge")))
        self.assertIsNotNone(plan.manifest_hash)
        self.assertIsNotNone(plan.trace)
        assert plan.trace is not None
        self.assertFalse(plan.trace["tracePersisted"])
        self.assertEqual(0, len(self.traces.list_traces()))

        commit_options = RagIngestionOptions(
            chunk_chars=72,
            chunk_overlap_chars=8,
            replace_document_chunks=True,
            persist_manifest=True,
            include_trace=True,
            expected_plan_hash=plan.plan_hash,
            expected_manifest_hash=plan.manifest_hash,
        )
        committed = self.service.ingest_text(
            "knowledge",
            self.request(text, options=commit_options),
        )

        self.assertEqual(plan.plan_hash, committed.plan_hash)
        self.assertEqual("matched", committed.plan_hash_comparison.status)
        self.assertEqual("matched", committed.manifest_hash_comparison.status)
        self.assertEqual(1, len(self.traces.list_traces()))
        self.assertEqual(committed.chunk_count + 1, len(self.store.query_all_records("knowledge")))
        manifest = self.store.get_record(
            "knowledge",
            "tenant-a",
            committed.manifest_id or "",
        )
        self.assertIsNotNone(manifest)
        assert manifest is not None
        serialized = str(manifest.to_dict())
        self.assertNotIn(text, serialized)
        self.assertIsNotNone(manifest.content)
        assert manifest.content is not None
        manifest_content = manifest.content["manifest"]
        self.assertNotIn("rawText", manifest_content)
        self.assertFalse(manifest_content["rawTextIncluded"])
        self.assertEqual("rag.manifest", manifest.type)

    def test_skip_unchanged_and_passage_prefix_metadata_are_stable(self) -> None:
        text = "Portable retrieval uses deterministic embeddings and stable record IDs."
        first = self.service.ingest_text("knowledge", self.request(text))
        second = self.service.ingest_text(
            "knowledge",
            self.request(
                text,
                options=RagIngestionOptions(
                    chunk_chars=80,
                    chunk_overlap_chars=10,
                    skip_unchanged_chunks=True,
                ),
            ),
        )

        self.assertEqual(1, first.created_count)
        self.assertEqual(1, second.reused_count)
        self.assertEqual("unchanged", second.chunks[0].embedding_action)
        self.assertEqual(
            first.chunks[0].embedding_text_hash,
            second.chunks[0].embedding_text_hash,
        )
        record = self.store.get_record(
            "knowledge",
            "tenant-a",
            first.chunks[0].id,
        )
        self.assertIsNotNone(record)
        assert record is not None
        self.assertIsNotNone(record.metadata)
        assert record.metadata is not None
        self.assertTrue(record.metadata["embeddingPrefixApplied"])
        self.assertEqual(len("passage: "), record.metadata["embeddingPrefixLength"])
        self.assertNotEqual(
            record.metadata["textHash"],
            record.metadata["embeddingTextHash"],
        )

    def test_vector_reuse_and_deduplication_have_explicit_scopes(self) -> None:
        text = "The same reusable chunk appears in more than one source document."
        first = self.service.ingest_text(
            "knowledge",
            self.request(text, document_id="first"),
        )
        reused = self.service.ingest_text(
            "knowledge",
            self.request(
                text,
                document_id="second",
                options=RagIngestionOptions(
                    chunk_chars=500,
                    chunk_overlap_chars=0,
                    reuse_existing_chunk_vectors=True,
                ),
            ),
        )
        deduplicated = self.service.ingest_text(
            "knowledge",
            self.request(
                text,
                document_id="third",
                options=RagIngestionOptions(
                    chunk_chars=500,
                    chunk_overlap_chars=0,
                    deduplicate_existing_chunks=True,
                ),
            ),
        )
        cross_partition = self.service.ingest_text(
            "knowledge",
            self.request(
                text,
                document_id="fourth",
                partition_key="tenant-b",
                options=RagIngestionOptions(
                    chunk_chars=500,
                    chunk_overlap_chars=0,
                    reuse_existing_chunk_vectors=True,
                ),
            ),
        )
        cross_collection_scope = self.service.ingest_text(
            "knowledge",
            self.request(
                text,
                document_id="fifth",
                partition_key="tenant-c",
                options=RagIngestionOptions(
                    chunk_chars=500,
                    chunk_overlap_chars=0,
                    reuse_existing_chunk_vectors=True,
                    vector_reuse_scope="collection",
                ),
            ),
        )

        self.assertEqual("generated", first.chunks[0].embedding_action)
        self.assertEqual("reused", reused.chunks[0].embedding_action)
        self.assertEqual(first.chunks[0].id, reused.chunks[0].reused_vector_from_id)
        self.assertEqual("deduplicated", deduplicated.chunks[0].action)
        self.assertEqual(first.chunks[0].id, deduplicated.chunks[0].id)
        self.assertEqual("generated", cross_partition.chunks[0].embedding_action)
        self.assertEqual("reused", cross_collection_scope.chunks[0].embedding_action)

    def test_replace_document_chunks_deletes_stale_records(self) -> None:
        long_text = (
            "First section has enough detail to form a chunk. "
            "Second section adds additional material for another chunk. "
            "Third section is removed from the replacement."
        )
        first = self.service.ingest_text(
            "knowledge",
            self.request(
                long_text,
                options=RagIngestionOptions(
                    chunk_chars=55,
                    chunk_overlap_chars=0,
                    persist_manifest=True,
                ),
            ),
        )
        replacement = self.service.ingest_text(
            "knowledge",
            self.request(
                "A short replacement.",
                options=RagIngestionOptions(
                    chunk_chars=120,
                    chunk_overlap_chars=0,
                    replace_document_chunks=True,
                    persist_manifest=True,
                ),
            ),
        )

        self.assertEqual(first.chunk_count, replacement.deleted_stale_count)
        self.assertEqual(
            set(first.action_summary.created_ids),
            set(replacement.action_summary.stale_delete_ids),
        )
        for stale_id in replacement.action_summary.stale_delete_ids:
            self.assertIsNone(
                self.store.get_record("knowledge", "tenant-a", stale_id)
            )

    def test_batch_accounting_and_validation_are_bounded(self) -> None:
        batch = self.service.ingest_text_batch(
            "knowledge",
            {
                "items": [
                    {
                        "documentId": "valid",
                        "partitionKey": "tenant-a",
                        "text": "Valid batch text.",
                        "embedding": {"field": "contentEmbedding"},
                        "options": {"chunkChars": 100, "chunkOverlapChars": 0},
                    },
                    {
                        "documentId": "invalid",
                        "partitionKey": "",
                        "text": "Invalid batch text.",
                    },
                    {
                        "documentId": "skipped",
                        "partitionKey": "tenant-a",
                        "text": "This item is not attempted.",
                    },
                ],
                "continueOnError": False,
            },
        )

        self.assertEqual(3, batch.requested)
        self.assertEqual(2, batch.attempted)
        self.assertEqual(1, batch.succeeded)
        self.assertEqual(1, batch.failed)
        self.assertTrue(batch.stopped_on_error)
        self.assertEqual(("succeeded", "failed"), tuple(item.status for item in batch.items))

        invalid_options = (
            RagIngestionOptions(chunk_chars=0),
            RagIngestionOptions(chunk_chars=10, chunk_overlap_chars=10),
            RagIngestionOptions(vector_reuse_scope="global"),
            RagIngestionOptions(chunk_dedupe_scope="global"),
        )
        for options in invalid_options:
            with self.subTest(options=options):
                with self.assertRaises(ValueError):
                    self.service.ingest_text(
                        "knowledge",
                        self.request("valid text", options=options),
                    )


if __name__ == "__main__":
    unittest.main()
