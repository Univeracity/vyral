"""Bounded, read-only source search using a locally installed ripgrep.

This integration is experimental and intentionally sits outside Vyral's stable
wire contract. It lets an application evaluate source-native retrieval before
copying an already-searchable corpus into a durable retrieval index.
"""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import subprocess
from threading import Event, Thread
from time import perf_counter
from typing import Any, cast
from urllib.parse import quote


_BASELINE_EXCLUDED_GLOBS = (
    "**/.git/**",
    "**/.env",
    "**/.env.*",
    "**/*.key",
    "**/*.pem",
    "**/*.p12",
    "**/*.pfx",
    "**/*.kdbx",
    "**/id_rsa",
    "**/id_ed25519",
    "**/credentials.json",
    "**/secrets.json",
    "**/secrets.yaml",
    "**/secrets.yml",
)
_SENSITIVE_FILENAMES = frozenset(
    {
        ".env",
        "credentials",
        "credentials.json",
        "id_ed25519",
        "id_rsa",
        "secrets",
        "secrets.json",
        "secrets.yaml",
        "secrets.yml",
    }
)
_SENSITIVE_SUFFIXES = frozenset({".key", ".kdbx", ".p12", ".pem", ".pfx"})


class RipgrepIntegrationError(RuntimeError):
    """Base error for the experimental ripgrep integration."""


class RipgrepNotAvailableError(RipgrepIntegrationError):
    """Raised when a suitable ripgrep executable cannot be resolved."""


class RipgrepSearchError(RipgrepIntegrationError):
    """Raised when ripgrep cannot safely complete a search."""


class RipgrepSearchLimitError(RipgrepSearchError):
    """Raised when ripgrep exceeds a configured output boundary."""


@dataclass(frozen=True)
class RipgrepAdapterOptions:
    """Static policy for one allowlisted source root."""

    include_globs: tuple[str, ...]
    exclude_globs: tuple[str, ...] = ()
    executable: str = "rg"
    max_results: int = 50
    max_query_chars: int = 512
    max_line_chars: int = 2_000
    max_file_bytes: int = 8 * 1024 * 1024
    max_output_bytes: int = 1024 * 1024
    timeout_seconds: float = 5.0

    def __post_init__(self) -> None:
        if not self.include_globs:
            raise ValueError("ripgrep include_globs must contain at least one glob")
        for pattern in self.include_globs:
            _validate_glob(pattern, "include")
            if pattern.startswith("!"):
                raise ValueError("ripgrep include globs must not start with '!'")
        for pattern in self.exclude_globs:
            _validate_glob(pattern, "exclude")
            if pattern.startswith("!"):
                raise ValueError("ripgrep exclude globs must not start with '!'")
        if not self.executable.strip():
            raise ValueError("ripgrep executable must not be empty")
        _positive(self.max_results, "ripgrep max_results")
        _positive(self.max_query_chars, "ripgrep max_query_chars")
        _positive(self.max_line_chars, "ripgrep max_line_chars")
        _positive(self.max_file_bytes, "ripgrep max_file_bytes")
        _positive(self.max_output_bytes, "ripgrep max_output_bytes")
        if (
            isinstance(self.timeout_seconds, bool)
            or not isinstance(self.timeout_seconds, (int, float))
            or not math.isfinite(self.timeout_seconds)
            or self.timeout_seconds <= 0
        ):
            raise ValueError("ripgrep timeout_seconds must be greater than zero")


@dataclass(frozen=True)
class RipgrepSearchRequest:
    """One fixed-string search within the adapter's static policy."""

    query: str
    limit: int = 20
    case_sensitive: bool = False


@dataclass(frozen=True)
class RipgrepSourceMatch:
    relative_path: str
    line_number: int
    byte_column: int
    line_text: str
    matched_text: str
    source_uri: str
    source_revision: str

    def to_dict(self) -> dict[str, object]:
        return {
            "relativePath": self.relative_path,
            "lineNumber": self.line_number,
            "byteColumn": self.byte_column,
            "lineText": self.line_text,
            "matchedText": self.matched_text,
            "sourceUri": self.source_uri,
            "sourceRevision": self.source_revision,
        }


@dataclass(frozen=True)
class RipgrepSearchResult:
    query: str
    matches: tuple[RipgrepSourceMatch, ...]
    truncated: bool
    filtered_sensitive_paths: int
    duration_ms: float
    executable_version: str

    def to_dict(self) -> dict[str, object]:
        return {
            "query": self.query,
            "matches": [match.to_dict() for match in self.matches],
            "truncated": self.truncated,
            "filteredSensitivePaths": self.filtered_sensitive_paths,
            "durationMs": self.duration_ms,
            "executableVersion": self.executable_version,
        }


class RipgrepSearchAdapter:
    """Search one fixed root without shell expansion or caller-selected paths."""

    def __init__(
        self,
        root_path: str | Path,
        options: RipgrepAdapterOptions,
    ) -> None:
        self._root = _resolve_root(root_path)
        self._options = options
        self._executable = _resolve_executable(options.executable)
        self._version = self._read_version()

    @property
    def root_path(self) -> Path:
        return self._root

    @property
    def executable_version(self) -> str:
        return self._version

    def search(
        self,
        request: RipgrepSearchRequest | str,
    ) -> RipgrepSearchResult:
        selected = (
            RipgrepSearchRequest(query=request)
            if isinstance(request, str)
            else request
        )
        query = _validate_query(selected.query, self._options.max_query_chars)
        if selected.limit <= 0 or selected.limit > self._options.max_results:
            raise ValueError(
                "ripgrep request limit must be between 1 and "
                f"{self._options.max_results}"
            )

        command = [
            str(self._executable),
            "--no-config",
            "--json",
            "--color",
            "never",
            "--line-number",
            "--column",
            "--max-columns",
            str(self._options.max_line_chars),
            "--max-columns-preview",
            "--max-filesize",
            str(self._options.max_file_bytes),
            "--fixed-strings",
            "--case-sensitive" if selected.case_sensitive else "--ignore-case",
        ]
        for pattern in self._options.include_globs:
            command.extend(("--glob", pattern))
        for pattern in (*_BASELINE_EXCLUDED_GLOBS, *self._options.exclude_globs):
            command.extend(("--glob", f"!{pattern}"))
        command.extend(("--file", "-", "."))

        started_at = perf_counter()
        return_code, output, exceeded = _run_bounded_process(
            command,
            cwd=self._root,
            stdin_data=f"{query}\n".encode("utf-8"),
            max_output_bytes=self._options.max_output_bytes,
            timeout_seconds=self._options.timeout_seconds,
        )
        if exceeded:
            raise RipgrepSearchLimitError(
                "ripgrep exceeded the configured output boundary"
            )
        if return_code not in (0, 1):
            raise RipgrepSearchError(
                f"ripgrep exited without a usable result (code {return_code})"
            )

        raw_matches = tuple(
            sorted(
                _parse_match_events(output),
                key=lambda event: (
                    event["path"],
                    event["line_number"],
                    event["byte_column"],
                ),
            )
        )
        revisions: dict[str, str] = {}
        matches: list[RipgrepSourceMatch] = []
        filtered_sensitive_paths = 0
        truncated = False
        for event in raw_matches:
            relative_path = _safe_relative_path(event["path"])
            if _is_sensitive_path(relative_path):
                filtered_sensitive_paths += 1
                continue
            if len(matches) >= selected.limit:
                truncated = True
                break
            source_path = _resolve_match_path(self._root, relative_path)
            revision = revisions.get(relative_path)
            if revision is None:
                revision = _hash_regular_file(
                    source_path,
                    self._options.max_file_bytes,
                )
                revisions[relative_path] = revision
            line_number = event["line_number"]
            matches.append(
                RipgrepSourceMatch(
                    relative_path=relative_path,
                    line_number=line_number,
                    byte_column=event["byte_column"],
                    line_text=event["line_text"][: self._options.max_line_chars],
                    matched_text=event["matched_text"],
                    source_uri=(
                        "vyral-source://ripgrep/"
                        f"{quote(relative_path, safe='/')}#L{line_number}"
                    ),
                    source_revision=f"sha256:{revision}",
                )
            )

        return RipgrepSearchResult(
            query=query,
            matches=tuple(matches),
            truncated=truncated,
            filtered_sensitive_paths=filtered_sensitive_paths,
            duration_ms=round((perf_counter() - started_at) * 1000, 3),
            executable_version=self._version,
        )

    def _read_version(self) -> str:
        return_code, output, exceeded = _run_bounded_process(
            [str(self._executable), "--version"],
            cwd=self._root,
            stdin_data=None,
            max_output_bytes=16 * 1024,
            timeout_seconds=min(self._options.timeout_seconds, 2.0),
        )
        if exceeded or return_code != 0:
            raise RipgrepNotAvailableError(
                "ripgrep did not return a bounded version response"
            )
        first_line = output.decode("utf-8", errors="replace").splitlines()
        if not first_line or not first_line[0].startswith("ripgrep "):
            raise RipgrepNotAvailableError(
                "the configured executable did not identify itself as ripgrep"
            )
        return first_line[0]


def _positive(value: int, name: str) -> None:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise ValueError(f"{name} must be greater than zero")


def _validate_glob(pattern: str, kind: str) -> None:
    if not isinstance(pattern, str) or not pattern.strip():
        raise ValueError(f"ripgrep {kind} glob must not be empty")
    if "\x00" in pattern or "\\" in pattern:
        raise ValueError(f"ripgrep {kind} glob contains an unsupported character")
    candidate = PurePosixPath(pattern)
    if candidate.is_absolute() or ".." in candidate.parts:
        raise ValueError(f"ripgrep {kind} glob must stay within the source root")


def _validate_query(query: str, max_chars: int) -> str:
    if not isinstance(query, str) or not query.strip():
        raise ValueError("ripgrep query must not be empty")
    if len(query) > max_chars:
        raise ValueError(f"ripgrep query cannot exceed {max_chars} characters")
    if any(ord(character) < 32 or ord(character) == 127 for character in query):
        raise ValueError("ripgrep query must not contain control characters")
    return query


def _resolve_root(root_path: str | Path) -> Path:
    requested = Path(root_path).expanduser()
    if requested.is_symlink():
        raise ValueError("ripgrep source root must not be a symbolic link")
    root = requested.resolve()
    if not root.is_dir():
        raise ValueError(f"ripgrep source root is not a directory: {root}")
    anchor = Path(root.anchor)
    if root == anchor or root == Path.home().resolve():
        raise ValueError("ripgrep source root is too broad")
    return root


def _resolve_executable(executable: str) -> Path:
    candidate = Path(executable).expanduser()
    located = (
        str(candidate)
        if candidate.is_absolute()
        else shutil.which(executable)
    )
    if located is None:
        raise RipgrepNotAvailableError("ripgrep executable was not found")
    resolved = Path(located).resolve()
    if not resolved.is_file() or not os.access(resolved, os.X_OK):
        raise RipgrepNotAvailableError(
            "ripgrep executable is not a runnable regular file"
        )
    return resolved


def _run_bounded_process(
    command: list[str],
    *,
    cwd: Path,
    stdin_data: bytes | None,
    max_output_bytes: int,
    timeout_seconds: float,
) -> tuple[int, bytes, bool]:
    environment = dict(os.environ)
    environment.pop("RIPGREP_CONFIG_PATH", None)
    process = subprocess.Popen(
        command,
        cwd=cwd,
        env=environment,
        stdin=subprocess.PIPE if stdin_data is not None else subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
    )
    assert process.stdout is not None
    stdout = process.stdout
    chunks: list[bytes] = []
    output_size = 0
    exceeded = Event()
    read_failed = Event()

    def drain_stdout() -> None:
        nonlocal output_size
        try:
            while True:
                chunk = stdout.read(64 * 1024)
                if not chunk:
                    return
                remaining = max_output_bytes - output_size
                if remaining <= 0 or len(chunk) > remaining:
                    if remaining > 0:
                        chunks.append(chunk[:remaining])
                        output_size += remaining
                    exceeded.set()
                    process.kill()
                    return
                chunks.append(chunk)
                output_size += len(chunk)
        except OSError:
            read_failed.set()
            process.kill()

    reader = Thread(target=drain_stdout, name="vyral-ripgrep-stdout", daemon=True)
    reader.start()
    if stdin_data is not None:
        assert process.stdin is not None
        try:
            process.stdin.write(stdin_data)
            process.stdin.close()
        except BrokenPipeError:
            pass
    try:
        return_code = process.wait(timeout=timeout_seconds)
    except subprocess.TimeoutExpired as error:
        process.kill()
        process.wait()
        reader.join(timeout=1.0)
        stdout.close()
        raise RipgrepSearchError(
            "ripgrep exceeded the configured timeout"
        ) from error
    reader.join(timeout=1.0)
    if reader.is_alive() or read_failed.is_set():
        process.kill()
        stdout.close()
        raise RipgrepSearchError("ripgrep output could not be read safely")
    stdout.close()
    return return_code, b"".join(chunks), exceeded.is_set()


def _parse_match_events(output: bytes) -> tuple[dict[str, Any], ...]:
    matches: list[dict[str, Any]] = []
    for raw_line in output.splitlines():
        try:
            envelope = json.loads(raw_line)
        except (json.JSONDecodeError, UnicodeDecodeError) as error:
            raise RipgrepSearchError("ripgrep returned malformed JSON") from error
        if not isinstance(envelope, dict) or envelope.get("type") != "match":
            continue
        data = envelope.get("data")
        if not isinstance(data, dict):
            raise RipgrepSearchError("ripgrep returned an invalid match event")
        path = _nested_text(data, "path")
        line_text = _nested_text(data, "lines").rstrip("\r\n")
        line_number = data.get("line_number")
        submatches = data.get("submatches")
        if (
            isinstance(line_number, bool)
            or not isinstance(line_number, int)
            or line_number <= 0
            or not isinstance(submatches, list)
            or not submatches
            or not isinstance(submatches[0], dict)
        ):
            raise RipgrepSearchError("ripgrep returned an invalid match position")
        first = submatches[0]
        start = first.get("start")
        matched = first.get("match")
        if isinstance(start, bool) or not isinstance(start, int) or start < 0:
            raise RipgrepSearchError("ripgrep returned an invalid byte column")
        if not isinstance(matched, dict) or not isinstance(matched.get("text"), str):
            raise RipgrepSearchError("ripgrep returned a non-text match")
        matches.append(
            {
                "path": path,
                "line_number": line_number,
                "byte_column": start + 1,
                "line_text": line_text,
                "matched_text": matched["text"],
            }
        )
    return tuple(matches)


def _nested_text(data: dict[str, Any], field: str) -> str:
    value = data.get(field)
    if not isinstance(value, dict) or not isinstance(value.get("text"), str):
        raise RipgrepSearchError(f"ripgrep returned a non-text {field}")
    return cast(str, value["text"])


def _safe_relative_path(value: str) -> str:
    normalized = value.removeprefix("./").replace("\\", "/")
    candidate = PurePosixPath(normalized)
    if not normalized or candidate.is_absolute() or ".." in candidate.parts:
        raise RipgrepSearchError("ripgrep returned a path outside the source root")
    return candidate.as_posix()


def _is_sensitive_path(relative_path: str) -> bool:
    for component in PurePosixPath(relative_path).parts:
        lowered = component.casefold()
        if (
            lowered in _SENSITIVE_FILENAMES
            or lowered.startswith(".env.")
            or lowered.startswith("secrets.")
            or Path(lowered).suffix in _SENSITIVE_SUFFIXES
        ):
            return True
    return False


def _resolve_match_path(root: Path, relative_path: str) -> Path:
    candidate = root.joinpath(*PurePosixPath(relative_path).parts)
    if candidate.is_symlink():
        raise RipgrepSearchError("ripgrep matched a symbolic link")
    try:
        resolved = candidate.resolve(strict=True)
        resolved.relative_to(root)
    except (FileNotFoundError, ValueError) as error:
        raise RipgrepSearchError(
            "ripgrep matched a path outside the source root or a changed source"
        ) from error
    return resolved


def _hash_regular_file(path: Path, max_file_bytes: int) -> str:
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        file_descriptor = os.open(path, flags)
    except OSError as error:
        raise RipgrepSearchError("a matched source could not be opened safely") from error
    digest = hashlib.sha256()
    try:
        with os.fdopen(file_descriptor, "rb") as source:
            details = os.fstat(source.fileno())
            if not stat.S_ISREG(details.st_mode) or details.st_size > max_file_bytes:
                raise RipgrepSearchError(
                    "a matched source is not a bounded regular file"
                )
            while chunk := source.read(64 * 1024):
                digest.update(chunk)
    except OSError as error:
        raise RipgrepSearchError("a matched source changed during hashing") from error
    return digest.hexdigest()


__all__ = [
    "RipgrepAdapterOptions",
    "RipgrepIntegrationError",
    "RipgrepNotAvailableError",
    "RipgrepSearchAdapter",
    "RipgrepSearchError",
    "RipgrepSearchLimitError",
    "RipgrepSearchRequest",
    "RipgrepSearchResult",
    "RipgrepSourceMatch",
]
