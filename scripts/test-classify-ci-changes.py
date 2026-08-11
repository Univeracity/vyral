#!/usr/bin/env python3
"""Deterministic safety tests for change-scoped CI selection."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent.parent
MODULE_PATH = ROOT / "scripts" / "classify-ci-changes.py"
SPEC = importlib.util.spec_from_file_location("classify_ci_changes", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Cannot load {MODULE_PATH}")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ClassifyCiChangesTests(unittest.TestCase):
    def test_documentation_only_skips_language_work(self) -> None:
        result = MODULE.classify(["README.md", "docs/reference/stability.md"])
        self.assertFalse(any(result.values()))

    def test_each_language_selects_only_its_surface(self) -> None:
        cases = {
            "src/Vyral.Local/LocalStore.cs": {"dotnet", "csharp"},
            "clients/javascript/src/index.ts": {"javascript"},
            "runtimes/python/src/vyral_runtime/runtime.py": {"python"},
            "clients/go/client.go": {"go"},
        }
        for path, expected in cases.items():
            with self.subTest(path=path):
                result = MODULE.classify([path])
                self.assertEqual(
                    {name for name, enabled in result.items() if enabled},
                    expected,
                )

    def test_central_and_unknown_paths_fail_open(self) -> None:
        for path in (
            "contracts/public-sdk-surface.json",
            ".github/workflows/ci.yml",
            "unexpected/root.file",
            "../outside",
        ):
            with self.subTest(path=path):
                self.assertTrue(all(MODULE.classify([path]).values()))

    def test_non_pull_request_is_always_full(self) -> None:
        self.assertTrue(
            all(MODULE.classify(["README.md"], full=True).values())
        )

    def test_github_output_contains_stable_language_json(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "github-output"
            MODULE._write_github_output(
                str(output),
                MODULE.classify(["clients/python/src/vyral_client/client.py"]),
            )
            text = output.read_text(encoding="utf-8")
        self.assertIn("python=true\n", text)
        self.assertIn("clients=true\n", text)
        self.assertIn(
            'languages={"csharp":false,"javascript":false,"python":true,"go":false}\n',
            text,
        )


if __name__ == "__main__":
    unittest.main()
