from __future__ import annotations

from datetime import datetime
import re


_FRACTION = re.compile(
    r"(?P<head>[T ]\d{2}:\d{2}:\d{2})"
    r"[\.,](?P<fraction>\d+)"
    r"(?P<tail>Z|[+-]\d{2}:\d{2})?$"
)


def parse_iso_datetime(value: str) -> datetime:
    """Parse ISO/RFC 3339 text consistently on Python 3.10 through 3.12."""

    match = _FRACTION.search(value)
    material = value
    if match is not None:
        fraction = match.group("fraction")
        normalized = fraction[:6].ljust(6, "0")
        material = (
            value[: match.start()]
            + match.group("head")
            + "."
            + normalized
            + (match.group("tail") or "")
        )
    if material.endswith("Z"):
        material = material[:-1] + "+00:00"
    return datetime.fromisoformat(material)


__all__ = ["parse_iso_datetime"]
