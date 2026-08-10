from __future__ import annotations

from contextlib import redirect_stdout
from io import StringIO
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime.host.cli import main  # noqa: E402


class HostCliTests(unittest.TestCase):
    def test_help_discovers_local_single_player_commands(self) -> None:
        output = StringIO()
        with redirect_stdout(output), self.assertRaises(SystemExit) as raised:
            main(["--help"])
        self.assertEqual(0, raised.exception.code)
        self.assertIn("vyral-runtime init", output.getvalue())
        self.assertIn("vyral-runtime quickstart", output.getvalue())
        self.assertIn("vyral-runtime inspect", output.getvalue())

    def test_init_creates_an_editable_application_without_server_extra(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-cli-starter-"
        ) as temporary:
            target = Path(temporary) / "vyral_app.py"
            output = StringIO()
            with redirect_stdout(output):
                status = main(
                    ["init", "--path", str(target), "--json"]
                )

            self.assertEqual(0, status)
            result = json.loads(output.getvalue())
            self.assertEqual(str(target.resolve()), result["createdPath"])
            self.assertEqual("starter.vyral_app", result["appId"])
            self.assertEqual(
                str((target.parent / ".vyral" / "vyral_app").resolve()),
                result["stateRootPath"],
            )
            self.assertEqual(
                ["python", str(target.resolve())],
                result["runArguments"],
            )
            self.assertIn("@vyral(", target.read_text(encoding="utf-8"))

    def test_quickstart_subcommand_does_not_require_server_extra(self) -> None:
        result = SimpleNamespace(
            root_path="/tmp/vyral-demo",
            context_text="Context:\nlocal evidence",
            to_dict=lambda: {"rootPath": "/tmp/vyral-demo"},
        )
        output = StringIO()
        with patch(
            "vyral_runtime.host.cli.run_local_quickstart_sync",
            return_value=result,
        ) as run, redirect_stdout(output):
            status = main(
                [
                    "quickstart",
                    "--root",
                    "/tmp/vyral-demo",
                    "--json",
                ]
            )
        self.assertEqual(0, status)
        run.assert_called_once_with("/tmp/vyral-demo", emit=None)
        self.assertIn('"rootPath": "/tmp/vyral-demo"', output.getvalue())

    def test_inspect_subcommand_summarizes_local_providers(self) -> None:
        inspection = {
            "rootPath": "/tmp/vyral-demo",
            "topology": "local-single-node",
            "runtime": {
                "version": "0.1.1",
                "contractVersion": "0.3.0",
                "maturity": "prototype",
                "fullLocalReady": False,
            },
            "providers": {
                "records": {
                    "adapter": "SQLiteRecordStore",
                    "healthy": True,
                },
                "objects": {
                    "adapter": "FileObjectStore",
                    "healthy": True,
                },
                "embeddings": {"provider": "local-token-hash"},
                "execution": {
                    "adapter": "python-local-sqlite",
                    "healthy": True,
                },
            },
            "warnings": ["prototype evidence only"],
        }
        output = StringIO()
        with patch(
            "vyral_runtime.host.cli.inspect_local_runtime",
            return_value=inspection,
        ), redirect_stdout(output):
            status = main(
                ["inspect", "--root", "/tmp/vyral-demo"]
            )
        self.assertEqual(0, status)
        self.assertIn("Topology: local-single-node", output.getvalue())
        self.assertIn("Embeddings: local-token-hash", output.getvalue())
        self.assertIn("Warning: prototype evidence only", output.getvalue())

    def test_reset_subcommand_uses_owned_reset_boundary(self) -> None:
        output = StringIO()
        with patch(
            "vyral_runtime.host.cli.reset_local_quickstart",
            return_value=Path("/tmp/vyral-demo"),
        ) as reset, redirect_stdout(output):
            status = main(
                [
                    "quickstart",
                    "--root",
                    "/tmp/vyral-demo",
                    "--reset",
                ]
            )
        self.assertEqual(0, status)
        reset.assert_called_once_with("/tmp/vyral-demo")
        self.assertIn("Removed Vyral quickstart state", output.getvalue())

    def test_remote_bind_requires_authentication(self) -> None:
        with tempfile.TemporaryDirectory() as root, patch.dict(
            os.environ, {}, clear=True
        ), self.assertRaises(SystemExit) as raised:
            main(["--root", root, "--host", "0.0.0.0"])
        self.assertEqual(2, raised.exception.code)

    def test_root_is_never_selected_implicitly(self) -> None:
        with patch.dict(
            os.environ, {}, clear=True
        ), self.assertRaises(SystemExit) as raised:
            main([])
        self.assertEqual(2, raised.exception.code)

    def test_port_must_be_in_tcp_range(self) -> None:
        with tempfile.TemporaryDirectory() as root, patch.dict(
            os.environ, {}, clear=True
        ), self.assertRaises(SystemExit) as raised:
            main(["--root", root, "--port", "0"])
        self.assertEqual(2, raised.exception.code)

    def test_wildcard_bind_requires_explicit_allowed_host(self) -> None:
        with tempfile.TemporaryDirectory() as root, patch.dict(
            os.environ, {"VYRAL_API_KEY": "secret"}, clear=True
        ), self.assertRaises(SystemExit) as raised:
            main(["--root", root, "--host", "::"])
        self.assertEqual(2, raised.exception.code)

    def test_blank_allowed_values_fail_closed(self) -> None:
        cases = (
            ("--allowed-host", " "),
            ("--allowed-origin", " "),
        )
        for option, value in cases:
            with (
                self.subTest(option=option),
                tempfile.TemporaryDirectory() as root,
                patch.dict(os.environ, {}, clear=True),
                self.assertRaises(SystemExit) as raised,
            ):
                main(["--root", root, option, value])
            self.assertEqual(2, raised.exception.code)

    def test_server_extra_error_is_actionable(self) -> None:
        with tempfile.TemporaryDirectory() as root, patch.dict(
            os.environ, {}, clear=True
        ), patch(
            "vyral_runtime.host.cli.import_module",
            side_effect=ImportError("uvicorn unavailable"),
        ), self.assertRaisesRegex(RuntimeError, "server.*extra"):
            main(["--root", root])

    def test_valid_network_configuration_reaches_uvicorn(self) -> None:
        application = object()
        run = Mock()
        uvicorn = SimpleNamespace(run=run)
        with tempfile.TemporaryDirectory() as root, patch.dict(
            os.environ, {"VYRAL_API_KEY": "shared-secret"}, clear=True
        ), patch(
            "vyral_runtime.host.cli.import_module",
            return_value=uvicorn,
        ), patch(
            "vyral_runtime.host.cli.create_host_application",
            return_value=application,
        ) as create:
            result = main(
                [
                    "--root",
                    root,
                    "--host",
                    "0.0.0.0",
                    "--port",
                    "8443",
                    "--log-level",
                    "warning",
                    "--allowed-host",
                    "runtime.example",
                    "--allowed-origin",
                    "https://console.example",
                    "--mcp-conformance-diagnostics",
                ]
            )

        self.assertEqual(0, result)
        create.assert_called_once()
        root_argument = create.call_args.args[0]
        self.assertEqual(str(Path(root).resolve()), root_argument)
        self.assertEqual(
            "shared-secret",
            create.call_args.kwargs["api_key"],
        )
        rest_config = create.call_args.kwargs["rest_config"]
        mcp_config = create.call_args.kwargs["mcp_config"]
        self.assertIn("runtime.example", rest_config.allowed_hosts)
        self.assertEqual(
            frozenset({"https://console.example"}),
            rest_config.allowed_origins,
        )
        self.assertEqual(
            rest_config.allowed_hosts,
            mcp_config.allowed_hosts,
        )
        self.assertEqual(
            rest_config.allowed_origins,
            mcp_config.allowed_origins,
        )
        self.assertTrue(mcp_config.enable_conformance_diagnostics)
        run.assert_called_once_with(
            application,
            host="0.0.0.0",
            port=8443,
            log_level="warning",
        )


if __name__ == "__main__":
    unittest.main()
