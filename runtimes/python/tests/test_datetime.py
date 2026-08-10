from __future__ import annotations

from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime._datetime import parse_iso_datetime  # noqa: E402


class PortableDateTimeTests(unittest.TestCase):
    def test_fractional_seconds_are_consistent_across_supported_python(self) -> None:
        short = parse_iso_datetime("2026-07-30T12:00:00.12Z")
        long = parse_iso_datetime(
            "2026-07-30T12:00:00.123456789+00:00"
        )

        self.assertEqual(120_000, short.microsecond)
        self.assertEqual(123_456, long.microsecond)
        self.assertEqual(0, short.utcoffset().total_seconds())  # type: ignore[union-attr]

    def test_invalid_timestamp_is_rejected(self) -> None:
        with self.assertRaises(ValueError):
            parse_iso_datetime("not-a-timestamp")


if __name__ == "__main__":
    unittest.main()
