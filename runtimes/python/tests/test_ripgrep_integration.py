from __future__ import annotations

import hashlib
from pathlib import Path
import shutil
import tempfile
import unittest

from vyral_runtime.integrations.ripgrep import (
    RipgrepAdapterOptions,
    RipgrepNotAvailableError,
    RipgrepSearchAdapter,
    RipgrepSearchLimitError,
    RipgrepSearchRequest,
)


@unittest.skipUnless(shutil.which("rg"), "ripgrep is not installed")
class RipgrepIntegrationTests(unittest.TestCase):
    def test_search_is_bounded_cited_and_filters_sensitive_paths(self) -> None:
        with tempfile.TemporaryDirectory(prefix="vyral-ripgrep-") as temporary:
            root = Path(temporary) / "source"
            root.mkdir()
            python_source = root / "worker.py"
            python_source.write_text(
                "def preserve_receipt():\n"
                "    return 'accepted work survives restart'\n",
                encoding="utf-8",
            )
            (root / "notes.md").write_text(
                "Accepted work survives restart through a durable receipt.\n",
                encoding="utf-8",
            )
            (root / "secrets.md").write_text(
                "accepted work survives restart: do-not-return\n",
                encoding="utf-8",
            )
            (root / ".env").write_text(
                "MESSAGE=accepted work survives restart\n",
                encoding="utf-8",
            )

            adapter = RipgrepSearchAdapter(
                root,
                RipgrepAdapterOptions(include_globs=("*.py", "*.md", ".env")),
            )
            result = adapter.search("accepted work survives restart")

            self.assertTrue(result.executable_version.startswith("ripgrep "))
            self.assertEqual(1, result.filtered_sensitive_paths)
            self.assertEqual({"notes.md", "worker.py"}, {
                match.relative_path for match in result.matches
            })
            self.assertFalse(result.truncated)
            self.assertNotIn(str(root), str(result.to_dict()))
            for match in result.matches:
                self.assertTrue(match.source_uri.startswith("vyral-source://ripgrep/"))
                self.assertIn("#L", match.source_uri)
                expected = hashlib.sha256(
                    (root / match.relative_path).read_bytes()
                ).hexdigest()
                self.assertEqual(f"sha256:{expected}", match.source_revision)
                self.assertGreaterEqual(match.line_number, 1)
                self.assertGreaterEqual(match.byte_column, 1)

    def test_request_limit_and_case_policy_are_enforced(self) -> None:
        with tempfile.TemporaryDirectory(prefix="vyral-ripgrep-") as temporary:
            root = Path(temporary) / "source"
            root.mkdir()
            (root / "a.md").write_text("Needle\n", encoding="utf-8")
            (root / "b.md").write_text("needle\n", encoding="utf-8")
            adapter = RipgrepSearchAdapter(
                root,
                RipgrepAdapterOptions(include_globs=("*.md",), max_results=2),
            )

            insensitive = adapter.search(RipgrepSearchRequest("needle", limit=1))
            self.assertEqual(1, len(insensitive.matches))
            self.assertEqual("a.md", insensitive.matches[0].relative_path)
            self.assertTrue(insensitive.truncated)

            sensitive = adapter.search(
                RipgrepSearchRequest("Needle", limit=2, case_sensitive=True)
            )
            self.assertEqual(("a.md",), tuple(
                match.relative_path for match in sensitive.matches
            ))

            with self.assertRaisesRegex(ValueError, "between 1 and 2"):
                adapter.search(RipgrepSearchRequest("needle", limit=3))
            with self.assertRaisesRegex(ValueError, "control characters"):
                adapter.search("needle\nsecond-pattern")

    def test_static_policy_rejects_unsafe_roots_and_globs(self) -> None:
        with self.assertRaisesRegex(ValueError, "at least one"):
            RipgrepAdapterOptions(include_globs=())
        with self.assertRaisesRegex(ValueError, "within the source root"):
            RipgrepAdapterOptions(include_globs=("../*.md",))
        with self.assertRaisesRegex(ValueError, "must not start"):
            RipgrepAdapterOptions(include_globs=("!*.key",))
        with self.assertRaisesRegex(ValueError, "too broad"):
            RipgrepSearchAdapter(
                Path.home(),
                RipgrepAdapterOptions(include_globs=("*.md",)),
            )

    def test_output_and_executable_boundaries_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory(prefix="vyral-ripgrep-") as temporary:
            root = Path(temporary) / "source"
            root.mkdir()
            (root / "large.md").write_text(
                "needle with enough output to exceed the boundary\n" * 200,
                encoding="utf-8",
            )
            adapter = RipgrepSearchAdapter(
                root,
                RipgrepAdapterOptions(
                    include_globs=("*.md",),
                    max_output_bytes=256,
                ),
            )
            with self.assertRaisesRegex(
                RipgrepSearchLimitError,
                "output boundary",
            ):
                adapter.search("needle")

            with self.assertRaises(RipgrepNotAvailableError):
                RipgrepSearchAdapter(
                    root,
                    RipgrepAdapterOptions(
                        include_globs=("*.md",),
                        executable=str(root / "missing-rg"),
                    ),
                )

            alias = Path(temporary) / "source-link"
            try:
                alias.symlink_to(root, target_is_directory=True)
            except OSError:
                return
            with self.assertRaisesRegex(ValueError, "symbolic link"):
                RipgrepSearchAdapter(
                    alias,
                    RipgrepAdapterOptions(include_globs=("*.md",)),
                )


if __name__ == "__main__":
    unittest.main()
