from __future__ import annotations

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
