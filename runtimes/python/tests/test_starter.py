from __future__ import annotations

import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest

from vyral_runtime._starter import create_local_starter


class LocalStarterTests(unittest.TestCase):
    def test_generated_application_survives_restart_and_replays(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-local-starter-"
        ) as temporary:
            target = Path(temporary) / "nested" / "vyral_app.py"
            result = create_local_starter(target)

            self.assertEqual(target.resolve(), result.created_path)
            self.assertIn("@vyral(", target.read_text(encoding="utf-8"))
            environment = os.environ.copy()
            source = str(Path(__file__).resolve().parents[1] / "src")
            environment["PYTHONPATH"] = os.pathsep.join(
                value
                for value in (source, environment.get("PYTHONPATH", ""))
                if value
            )
            first = subprocess.run(
                [sys.executable, str(target)],
                check=True,
                capture_output=True,
                text=True,
                env=environment,
                timeout=30,
            )
            second = subprocess.run(
                [sys.executable, str(target)],
                check=True,
                capture_output=True,
                text=True,
                env=environment,
                timeout=30,
            )
            edited = target.read_text(encoding="utf-8")
            edited = edited.replace(
                "RUN_VERSION = 1",
                "RUN_VERSION = 2",
                1,
            ).replace(
                'payload={"name": "Vyral",',
                'payload={"name": "Vyral 2",',
                1,
            )
            with target.open("w", encoding="utf-8", newline="\n") as stream:
                stream.write(edited)
            versioned = subprocess.run(
                [sys.executable, str(target)],
                check=True,
                capture_output=True,
                text=True,
                env=environment,
                timeout=30,
            )

            self.assertIn("status=queued replayed=false", first.stdout)
            self.assertIn("status=queued", first.stdout)
            self.assertIn("status=succeeded dispatched=1", first.stdout)
            self.assertIn('result={"message": "Hello, Vyral!"}', first.stdout)
            self.assertIn("status=succeeded replayed=true", second.stdout)
            self.assertIn("status=succeeded dispatched=0", second.stdout)
            self.assertIn("status=queued replayed=false", versioned.stdout)
            self.assertIn("status=succeeded dispatched=1", versioned.stdout)
            self.assertIn(
                'result={"message": "Hello, Vyral 2!"}',
                versioned.stdout,
            )
            first_run = re.search(r"run=([^ ]+)", first.stdout)
            second_run = re.search(r"run=([^ ]+)", second.stdout)
            versioned_run = re.search(r"run=([^ ]+)", versioned.stdout)
            self.assertIsNotNone(first_run)
            self.assertIsNotNone(second_run)
            self.assertIsNotNone(versioned_run)
            assert (
                first_run is not None
                and second_run is not None
                and versioned_run is not None
            )
            self.assertEqual(first_run.group(1), second_run.group(1))
            self.assertNotEqual(first_run.group(1), versioned_run.group(1))
            self.assertTrue(result.state_root_path.is_dir())

    def test_generator_refuses_overwrite_and_non_python_paths(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-local-starter-boundary-"
        ) as temporary:
            root = Path(temporary)
            target = root / "vyral_app.py"
            target.write_text("keep\n", encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "overwrite"):
                create_local_starter(target)
            with self.assertRaisesRegex(ValueError, "end with .py"):
                create_local_starter(root / "vyral_app.txt")
            self.assertEqual("keep\n", target.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
