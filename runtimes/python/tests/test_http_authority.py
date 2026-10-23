from __future__ import annotations

from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime.host._http_authority import (  # noqa: E402
    host_allowed,
    parse_http_authority,
)


class HttpAuthorityTests(unittest.TestCase):
    def test_loopback_hosts_and_ports_are_normalized(self) -> None:
        allowed = frozenset({"localhost", "127.0.0.1", "[::1]"})
        for host in (
            "localhost",
            "LOCALHOST:5220",
            "127.0.0.1:5220",
            "[::1]:5220",
            "::1",
        ):
            with self.subTest(host=host):
                self.assertTrue(host_allowed(host, allowed))

    def test_url_syntax_whitespace_and_invalid_ports_are_rejected(self) -> None:
        for host in (
            "evil@localhost",
            "localhost/path",
            "localhost?target=evil",
            "localhost#evil",
            "localhost:invalid",
            "localhost:0",
            "localhost:65536",
            " localhost",
            "localhost ",
        ):
            with self.subTest(host=host):
                self.assertIsNone(parse_http_authority(host))

    def test_explicit_allowlist_port_is_not_widened(self) -> None:
        allowed = frozenset({"runtime.example:8443"})
        self.assertTrue(host_allowed("runtime.example:8443", allowed))
        self.assertFalse(host_allowed("runtime.example:443", allowed))


if __name__ == "__main__":
    unittest.main()
