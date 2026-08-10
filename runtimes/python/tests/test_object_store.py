from __future__ import annotations

from io import BytesIO
from pathlib import Path
from tempfile import TemporaryDirectory
from threading import Barrier, Thread
import unittest

from vyral_runtime import (
    FileObjectStore,
    ObjectDeleteRequest,
    ObjectListRequest,
    ObjectReadRequest,
    ObjectWriteRequest,
)


class FileObjectStoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = TemporaryDirectory()
        self.root = Path(self.temporary_directory.name) / "objects"
        self.store = FileObjectStore(self.root)

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def test_round_trip_stream_content_metadata_and_hash(self) -> None:
        content = b"portable object content \xcf\x80"
        info = self.store.put_object(
            ObjectWriteRequest(
                container="artifacts",
                key="runs/run-1/result.bin",
                content=BytesIO(content),
                content_type="application/octet-stream",
                metadata={"runId": "run-1", "_portable": "true"},
            )
        )
        result = self.store.get_object(
            ObjectReadRequest("artifacts", "runs/run-1/result.bin")
        )

        self.assertIsNotNone(result)
        assert result is not None
        with result:
            self.assertEqual(content, result.read())
        self.assertEqual(len(content), info.content_length)
        self.assertEqual(info.etag, info.content_hash)
        self.assertTrue(info.etag.startswith("sha256:"))
        self.assertEqual("run-1", info.metadata["runId"])
        self.assertEqual(info, result.info)
        self.assertTrue(self.store.diagnostics().healthy)

    def test_write_and_delete_preconditions_are_atomic(self) -> None:
        first = self.store.put_object(
            ObjectWriteRequest("artifacts", "item.txt", b"first")
        )
        with self.assertRaisesRegex(ValueError, "ifNoneMatch"):
            self.store.put_object(
                ObjectWriteRequest(
                    "artifacts",
                    "item.txt",
                    b"second",
                    if_none_match="*",
                )
            )
        with self.assertRaisesRegex(ValueError, "ifMatch"):
            self.store.put_object(
                ObjectWriteRequest(
                    "artifacts",
                    "item.txt",
                    b"second",
                    if_match="sha256:wrong",
                )
            )
        second = self.store.put_object(
            ObjectWriteRequest(
                "artifacts",
                "item.txt",
                b"second",
                if_match=first.etag,
            )
        )
        self.assertNotEqual(first.etag, second.etag)

        with self.assertRaisesRegex(ValueError, "ifMatch"):
            self.store.delete_object(
                ObjectDeleteRequest(
                    "artifacts",
                    "item.txt",
                    if_match=first.etag,
                )
            )
        self.store.delete_object(
            ObjectDeleteRequest(
                "artifacts",
                "item.txt",
                if_match=second.etag,
            )
        )
        self.store.delete_object(ObjectDeleteRequest("artifacts", "item.txt"))
        self.assertIsNone(
            self.store.get_object(ObjectReadRequest("artifacts", "item.txt"))
        )

    def test_listing_is_bounded_sorted_prefixed_and_portably_paged(self) -> None:
        for key in (
            "z.txt",
            "prefix/c.txt",
            "prefix/a.txt",
            "prefix/b.txt",
            "other.txt",
        ):
            self.store.put_object(
                ObjectWriteRequest("artifacts", key, key.encode("utf-8"))
            )

        first = self.store.list_objects(
            ObjectListRequest("artifacts", prefix="prefix/", limit=2)
        )
        second = self.store.list_objects(
            ObjectListRequest(
                "artifacts",
                prefix="prefix/",
                limit=2,
                continuation_token=first.continuation_token,
            )
        )

        self.assertEqual(
            ("prefix/a.txt", "prefix/b.txt"),
            tuple(item.key for item in first.items),
        )
        self.assertEqual("Mg==", first.continuation_token)
        self.assertEqual(("prefix/c.txt",), tuple(item.key for item in second.items))
        self.assertIsNone(second.continuation_token)

    def test_names_metadata_limits_and_traversal_are_rejected(self) -> None:
        invalid_writes = (
            ObjectWriteRequest("AB", "a", b"x"),
            ObjectWriteRequest("Uppercase", "a", b"x"),
            ObjectWriteRequest("bad--name", "a", b"x"),
            ObjectWriteRequest("valid-name", "../secret", b"x"),
            ObjectWriteRequest("valid-name", "/absolute", b"x"),
            ObjectWriteRequest("valid-name", "a//b", b"x"),
            ObjectWriteRequest(
                "valid-name",
                "a",
                b"x",
                metadata={"vyral_secret": "x"},
            ),
            ObjectWriteRequest(
                "valid-name",
                "a",
                b"x",
                metadata={"bad-key": "x"},
            ),
        )
        for request in invalid_writes:
            with self.subTest(request=request):
                with self.assertRaises((TypeError, ValueError)):
                    self.store.put_object(request)
        for limit in (0, 5001):
            with self.assertRaises(ValueError):
                self.store.list_objects(
                    ObjectListRequest("artifacts", limit=limit)
                )
        self.store.put_object(
            ObjectWriteRequest("artifacts", "token-check", b"x")
        )
        with self.assertRaises(ValueError):
            self.store.list_objects(
                ObjectListRequest(
                    "artifacts",
                    continuation_token="not-base64!",
                )
            )

    def test_missing_sidecar_recovers_content_info_and_reports_diagnostics(self) -> None:
        info = self.store.put_object(
            ObjectWriteRequest("artifacts", "recover.bin", b"recover")
        )
        sidecar = (
            self.root
            / "artifacts"
            / ("recover.bin" + ".metadata.json")
        )
        sidecar.unlink()

        recovered = self.store.get_object(
            ObjectReadRequest("artifacts", "recover.bin")
        )
        self.assertIsNotNone(recovered)
        assert recovered is not None
        with recovered:
            self.assertEqual(b"recover", recovered.read())
        self.assertEqual(info.content_hash, recovered.content_hash)
        self.assertEqual({}, recovered.metadata)
        diagnostics = self.store.diagnostics()
        self.assertFalse(diagnostics.healthy)
        self.assertEqual(1, diagnostics.missing_metadata_count)

    def test_competing_if_none_match_writes_have_one_winner(self) -> None:
        barrier = Barrier(2)
        outcomes: list[str] = []

        def write(value: bytes) -> None:
            barrier.wait()
            try:
                self.store.put_object(
                    ObjectWriteRequest(
                        "artifacts",
                        "race.bin",
                        value,
                        if_none_match="*",
                    )
                )
            except ValueError:
                outcomes.append("rejected")
            else:
                outcomes.append("written")

        threads = [
            Thread(target=write, args=(b"one",)),
            Thread(target=write, args=(b"two",)),
        ]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()

        self.assertEqual(["rejected", "written"], sorted(outcomes))


if __name__ == "__main__":
    unittest.main()
