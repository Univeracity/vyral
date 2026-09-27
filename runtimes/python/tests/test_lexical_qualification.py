from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
from pathlib import Path
import sqlite3
from tempfile import TemporaryDirectory
from threading import Event
import unittest
from unittest.mock import patch

from vyral_runtime import LexicalSearchOptions, SQLiteRecordStore, VyralRecord, VyralRuntime, VyralVector
from vyral_runtime.local import lexical


class LexicalQualificationTests(unittest.TestCase):
    def records(self) -> tuple[VyralRecord, ...]:
        return (
            VyralRecord(id="a", partition_key="p", content={"text": "browser network diagnostics"}),
            VyralRecord(id="b", partition_key="p", content={"text": "browser browser network"}),
            VyralRecord(id="c", partition_key="p", content={"text": "network latency"}),
        )

    def test_cached_scores_and_diagnostics_equal_uncached_across_queries_and_options(self) -> None:
        records = self.records()
        cache = lexical.PreparedLexicalCache()
        for query in ("browser network", '"network diagnostics"', "brows netw", "latency"):
            for overrides in (
                {}, {"matchMode": "all"}, {"prefixMatching": True}, {"scoring": "coverage"},
                {"requiredPhraseGroups": [["browser network"]]},
                {"fieldBoosts": {"/content/text": 2.0}, "phraseBoost": 0.0},
            ):
                options = LexicalSearchOptions.from_value({"fields": ["/content/text"], **overrides})
                expected = lexical.score_many(records, query, options)
                for _ in range(2):
                    actual = lexical.score_many(records, query, options, prepared_cache=cache)
                    self.assertEqual(expected, actual)
                    for baseline, cached in zip(expected, actual):
                        self.assertIs(cached.record, baseline.record)
                        self.assertEqual(
                            baseline.diagnostics(collection="items", candidate_source="sqlite_fts5",
                                                 candidate_count=3, fts_expression=query,
                                                 required_phrase_groups=()).to_dict(),
                            cached.diagnostics(collection="items", candidate_source="sqlite_fts5",
                                               candidate_count=3, fts_expression=query,
                                               required_phrase_groups=()).to_dict(),
                        )

    def test_cache_reuses_fields_but_recomputes_candidate_statistics(self) -> None:
        records = self.records()
        options = LexicalSearchOptions(fields=("/content/text",))
        cache = lexical.PreparedLexicalCache()
        with patch.object(lexical, "_prepare_document", wraps=lexical._prepare_document) as prepare:
            small = lexical.score_many(records[:2], "browser", options, prepared_cache=cache)
            large = lexical.score_many(records, "browser", options, prepared_cache=cache)
            self.assertEqual(3, prepare.call_count)
        self.assertEqual(2, small[0].corpus_document_count)
        self.assertEqual(3, large[0].corpus_document_count)
        self.assertNotEqual(small[0].term_idf, large[0].term_idf)
        self.assertEqual(lexical.score_many(records, "browser", options), large)

    def test_same_revision_mutation_and_arbitrary_fields_never_return_an_old_record(self) -> None:
        cache = lexical.PreparedLexicalCache()
        record = self.records()[0]
        fields = ("/content/text",)
        original = cache.prepare(record, fields)
        current = replace(record, sources=({"uri": "source://new"},))
        self.assertIs(cache.prepare(current, fields).record, current)
        record.content["text"] = "changed content"  # frozen record, mutable JSON payload
        self.assertNotEqual(original.fields, cache.prepare(record, fields).fields)
        with_vector = replace(record, vectors={"probe": VyralVector((0.25, 0.5))})
        vector_fields = ("/vectors/probe/values",)
        absent = cache.prepare(record, vector_fields)
        present = cache.prepare(with_vector, vector_fields)
        self.assertNotEqual(absent.fields, present.fields)
        self.assertEqual(lexical._document(record, fields), cache.prepare(record, fields))
        self.assertEqual(lexical._document(record, ("/id",)), cache.prepare(record, ("/id",)))
        with patch.object(lexical, "LEXICAL_ANALYZER_ID", "test.other-analyzer"):
            cache.prepare(record, fields)
        self.assertTrue(any(key[0] == "test.other-analyzer" for key in cache._entries))
        self.assertTrue(all(len(value) == 3 for value in cache._entries.values()))

    def test_cache_bounds_disabled_mode_eviction_and_large_field_bypass(self) -> None:
        cache = lexical.PreparedLexicalCache(max_entries=1, max_bytes=8192)
        for record in self.records():
            self.assertEqual(lexical._document(record, ("/content/text",)),
                             cache.prepare(record, ("/content/text",)))
            self.assertLessEqual(len(cache._entries), 1)
            self.assertLessEqual(cache._bytes, 8192)
        huge = replace(self.records()[0], content={"text": "browser " * 10000})
        cache.prepare(huge, ("/content/text",))
        self.assertLessEqual(cache._bytes, 8192)
        for limits in ((0, 8192), (1, 0)):
            disabled = lexical.PreparedLexicalCache(*limits)
            disabled.prepare(huge, ("/content/text",))
            self.assertFalse(disabled._entries)
        for invalid in (-1, True, 1.5):
            with self.assertRaises(ValueError):
                lexical.PreparedLexicalCache(max_entries=invalid)  # type: ignore[arg-type]
        cache.clear()
        self.assertEqual(0, cache._bytes)
        self.assertFalse(cache._entries)

    def test_concurrent_clear_cannot_repopulate_an_inflight_preparation(self) -> None:
        cache = lexical.PreparedLexicalCache()
        entered, release = Event(), Event()
        original = lexical._prepare_document

        def paused(*args: object) -> lexical._Document:
            entered.set()
            self.assertTrue(release.wait(5))
            return original(*args)  # type: ignore[arg-type]

        with ThreadPoolExecutor(max_workers=4) as pool:
            with patch.object(lexical, "_prepare_document", side_effect=paused):
                pending = pool.submit(cache.prepare, self.records()[0], ("/content/text",))
                self.assertTrue(entered.wait(5))
                cache.clear()
                release.set()
                pending.result(5)
            self.assertFalse(cache._entries)
            records = [self.records()[0] for _ in range(20)]
            results = list(pool.map(lambda record: cache.prepare(record, ("/content/text",)), records))
        self.assertEqual(1, len(cache._entries))
        self.assertTrue(all(result.record is record for result, record in zip(results, records)))

    def test_unicode_pipeline_preserves_strict_scorer_semantics_and_records_analyzers(self) -> None:
        self.assertEqual(("café", "naïve", "résumé"), lexical.tokenize("CAFÉ naïve résumé"))
        self.assertEqual(("cafe",), lexical.tokenize("Cafe\u0301"))
        self.assertEqual(("straße",), lexical.tokenize("Straße"))  # lower, not casefold
        composed = VyralRecord(id="composed", partition_key="p", content={"text": "Café"})
        decomposed = VyralRecord(id="decomposed", partition_key="p", content={"text": "Cafe\u0301"})
        options = LexicalSearchOptions(fields=("/content/text",))
        for query, expected in (("CAFÉ", ["composed"]), ("Cafe\u0301", ["decomposed"]),
                                ("CAFE", ["decomposed"])):
            matches = lexical.score_many((composed, decomposed), query, options)
            self.assertEqual(expected, [match.record.id for match in matches if match.score > 0])
        with TemporaryDirectory() as root:
            store = SQLiteRecordStore(Path(root) / "records.sqlite")
            store.create_collection({"name": "items"})
            store.upsert_record("items", VyralRecord(id="accent", partition_key="p",
                                                     content={"text": "Café naïve résumé"}))
            for query in ("cafe", "Café", "naive", "naïve", "resume", "résumé", "CAFÉ"):
                with sqlite3.connect(store.database_path) as connection:
                    candidates = connection.execute(
                        "SELECT record_id FROM vyral_py_record_fts WHERE vyral_py_record_fts MATCH ?",
                        (query,),
                    ).fetchall()
                self.assertEqual([("accent",)], candidates)
                results = store.search_records("items", {"lexical": {"query": query,
                                                                        "fields": ["/content/text"]}})
                self.assertEqual(bool(query.lower() in ("café", "naïve", "résumé")), bool(results))
                if results:
                    details = results[0].diagnostics.details
                    self.assertEqual(lexical.LEXICAL_ANALYZER_ID, details["lexicalAnalyzer"])
                    self.assertEqual(lexical.FTS_CANDIDATE_ANALYZER_ID, details["lexicalCandidateAnalyzer"])

    def test_score_saturation_keeps_distinct_raw_scores_and_stable_ties(self) -> None:
        options = LexicalSearchOptions(fields=("/content/text",), phrase_boost=1.0)
        scores = lexical.score_many(self.records(), '"browser"', options)
        self.assertEqual(1.0, scores[0].score)
        self.assertEqual(1.0, scores[1].score)
        self.assertNotEqual(scores[0].raw_score, scores[1].raw_score)
        with TemporaryDirectory() as root:
            store = SQLiteRecordStore(Path(root) / "ties.sqlite")
            store.create_collection({"name": "items"})
            for record in reversed(self.records()):
                store.upsert_record("items", record)
            matches = store.search_records("items", {"lexical": {
                "query": '"browser"', "fields": ["/content/text"], "phraseBoost": 1.0,
            }})
            self.assertEqual(["a", "b"], [match.record.id for match in matches])

    def test_store_mutation_deletion_cross_store_refresh_and_runtime_close(self) -> None:
        with TemporaryDirectory() as root:
            runtime = VyralRuntime(root)
            store = runtime.records
            store.create_collection({"name": "items"})
            store.upsert_record("items", self.records()[0])
            request = {"lexical": {"query": "browser", "fields": ["/content/text"]}}
            store.search_records("items", request)
            self.assertTrue(store._lexical_cache._entries)
            other = SQLiteRecordStore(store.database_path)
            other.upsert_record("items", replace(self.records()[0], content={"text": "browser fresh"}))
            self.assertEqual("browser fresh", store.search_records("items", request)[0].record.content["text"])
            store.delete_record("items", "p", "a")
            self.assertFalse(store._lexical_cache._entries)
            self.assertFalse(store.search_records("items", request))
            store.upsert_record("items", self.records()[0])
            store.search_records("items", request)
            store.upsert_record("items", self.records()[1])
            self.assertFalse(store._lexical_cache._entries)
            store.search_records("items", request)
            runtime.close()
            self.assertFalse(store._lexical_cache._entries)
            store.search_records("items", request)
            store.delete_collection("items")
            self.assertFalse(store._lexical_cache._entries)


if __name__ == "__main__":
    unittest.main()
