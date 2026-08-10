from __future__ import annotations

from base64 import b64decode, b64encode
from dataclasses import dataclass, field
from datetime import datetime, timezone
from hashlib import sha256
from importlib import import_module
from io import BytesIO
import json
import os
from pathlib import Path
import tempfile
from threading import Lock, RLock
from typing import Any, BinaryIO, Mapping, Protocol, cast

from .._datetime import parse_iso_datetime
from ..async_runtime import RuntimeExecutor
from .models import JSONObject

class _FcntlModule(Protocol):
    LOCK_EX: int
    LOCK_UN: int

    def flock(self, file_descriptor: int, operation: int) -> None: ...


class _MsvcrtModule(Protocol):
    LK_LOCK: int
    LK_UNLCK: int

    def locking(
        self,
        file_descriptor: int,
        mode: int,
        byte_count: int,
    ) -> None: ...


def _optional_module(name: str) -> Any | None:
    try:
        return import_module(name)
    except ModuleNotFoundError:
        return None


_fcntl = cast(_FcntlModule | None, _optional_module("fcntl"))
_msvcrt = cast(_MsvcrtModule | None, _optional_module("msvcrt"))


DEFAULT_OBJECT_LIST_LIMIT = 100
MAX_OBJECT_LIST_LIMIT = 5000
METADATA_SUFFIX = ".metadata.json"
TEMP_DIRECTORY_NAME = ".vyral-tmp"


@dataclass(frozen=True)
class ObjectWriteRequest:
    container: str
    key: str
    content: bytes | BinaryIO
    content_type: str | None = None
    metadata: Mapping[str, str] | None = None
    if_match: str | None = None
    if_none_match: str | None = None


@dataclass(frozen=True)
class ObjectReadRequest:
    container: str
    key: str


@dataclass(frozen=True)
class ObjectDeleteRequest:
    container: str
    key: str
    if_match: str | None = None


@dataclass(frozen=True)
class ObjectListRequest:
    container: str
    prefix: str | None = None
    limit: int | None = None
    continuation_token: str | None = None


@dataclass(frozen=True)
class ObjectInfo:
    container: str
    key: str
    content_type: str | None
    content_length: int
    etag: str
    content_hash: str
    metadata: Mapping[str, str]
    updated_at: datetime

    def to_dict(self) -> JSONObject:
        return {
            "container": self.container,
            "key": self.key,
            "contentType": self.content_type,
            "contentLength": self.content_length,
            "etag": self.etag,
            "contentHash": self.content_hash,
            "metadata": dict(self.metadata),
            "updatedAt": _format_datetime(self.updated_at),
        }

    @classmethod
    def from_dict(cls, value: Mapping[str, Any]) -> ObjectInfo:
        metadata = value.get("metadata", {})
        if not isinstance(metadata, Mapping):
            raise TypeError("Object metadata sidecar metadata must be an object.")
        return cls(
            container=_required_text(value.get("container"), "object container"),
            key=_required_text(value.get("key"), "object key"),
            content_type=_optional_text(value.get("contentType"), "contentType"),
            content_length=_required_int(
                value.get("contentLength"),
                "contentLength",
            ),
            etag=_required_text(value.get("etag"), "etag"),
            content_hash=_required_text(value.get("contentHash"), "contentHash"),
            metadata={
                str(key): _required_text(item, f"metadata {key}")
                for key, item in metadata.items()
            },
            updated_at=_parse_datetime(value.get("updatedAt"), "updatedAt"),
        )


@dataclass
class ObjectResult:
    container: str
    key: str
    content_type: str | None
    content_length: int
    etag: str
    content_hash: str
    metadata: Mapping[str, str]
    updated_at: datetime
    content: BinaryIO

    @property
    def info(self) -> ObjectInfo:
        return ObjectInfo(
            container=self.container,
            key=self.key,
            content_type=self.content_type,
            content_length=self.content_length,
            etag=self.etag,
            content_hash=self.content_hash,
            metadata=self.metadata,
            updated_at=self.updated_at,
        )

    def read(self) -> bytes:
        return self.content.read()

    def close(self) -> None:
        self.content.close()

    def __enter__(self) -> ObjectResult:
        return self

    def __exit__(
        self,
        exc_type: object,
        exc: object,
        traceback: object,
    ) -> None:
        self.close()


@dataclass(frozen=True)
class ObjectListResult:
    items: tuple[ObjectInfo, ...]
    continuation_token: str | None = None

    def to_dict(self) -> JSONObject:
        return {
            "items": [item.to_dict() for item in self.items],
            "continuationToken": self.continuation_token,
        }


@dataclass(frozen=True)
class FileObjectStoreDiagnostics:
    root_exists: bool
    healthy: bool
    container_count: int
    object_count: int
    metadata_sidecar_count: int
    missing_metadata_count: int
    orphan_metadata_count: int
    temporary_file_count: int
    content_bytes: int
    temporary_bytes: int

    def to_dict(self) -> JSONObject:
        return {
            "rootExists": self.root_exists,
            "healthy": self.healthy,
            "containerCount": self.container_count,
            "objectCount": self.object_count,
            "metadataSidecarCount": self.metadata_sidecar_count,
            "missingMetadataCount": self.missing_metadata_count,
            "orphanMetadataCount": self.orphan_metadata_count,
            "temporaryFileCount": self.temporary_file_count,
            "contentBytes": self.content_bytes,
            "temporaryBytes": self.temporary_bytes,
        }


class _FileLock:
    def __init__(self, path: Path, thread_lock: RLock) -> None:
        self._path = path
        self._thread_lock = thread_lock
        self._file: BinaryIO | None = None

    def __enter__(self) -> _FileLock:
        self._thread_lock.acquire()
        try:
            self._file = self._path.open("a+b")
            _lock_process_file(self._file)
            return self
        except BaseException:
            if self._file is not None:
                self._file.close()
                self._file = None
            self._thread_lock.release()
            raise

    def __exit__(
        self,
        exc_type: object,
        exc: object,
        traceback: object,
    ) -> None:
        try:
            if self._file is not None:
                _unlock_process_file(self._file)
        finally:
            try:
                if self._file is not None:
                    self._file.close()
                    self._file = None
            finally:
                self._thread_lock.release()


def _lock_process_file(file: BinaryIO) -> None:
    if _fcntl is not None:
        _fcntl.flock(file.fileno(), _fcntl.LOCK_EX)
        return
    if _msvcrt is not None:
        file.seek(0, os.SEEK_END)
        if file.tell() == 0:
            file.write(b"\0")
            file.flush()
        file.seek(0)
        _msvcrt.locking(file.fileno(), _msvcrt.LK_LOCK, 1)
        return
    raise RuntimeError(
        "The local object store has no supported process-locking backend."
    )


def _unlock_process_file(file: BinaryIO) -> None:
    if _fcntl is not None:
        _fcntl.flock(file.fileno(), _fcntl.LOCK_UN)
        return
    if _msvcrt is not None:
        file.seek(0)
        _msvcrt.locking(file.fileno(), _msvcrt.LK_UNLCK, 1)
        return
    raise RuntimeError(
        "The local object store has no supported process-locking backend."
    )


class FileObjectStore:
    def __init__(
        self,
        root_path: str | Path,
        *,
        executor: RuntimeExecutor | None = None,
    ) -> None:
        if not str(root_path).strip():
            raise ValueError("Root path is required.")
        self.root_path = Path(root_path).resolve()
        self.lock_root_path = Path(str(self.root_path).rstrip(os.sep) + ".vyral-locks")
        self.root_path.mkdir(parents=True, exist_ok=True)
        self.lock_root_path.mkdir(parents=True, exist_ok=True)
        self._guard = Lock()
        self._thread_locks: dict[str, RLock] = {}
        self.executor = executor or RuntimeExecutor()
        self._owns_executor = executor is None

    def put_object(self, request: ObjectWriteRequest) -> ObjectInfo:
        _validate_container(request.container)
        normalized_key = _normalize_key(request.key)
        _validate_metadata(request.metadata)
        full_path = self._resolve_path(request.container, normalized_key)
        full_path.parent.mkdir(parents=True, exist_ok=True)
        with self._lock(full_path):
            existing = self._resolve_info(
                request.container,
                normalized_key,
                full_path,
            )
            _validate_write_conditions(request, existing)
            temp_path = self._temp_path()
            try:
                digest = sha256()
                size = 0
                with temp_path.open("xb") as destination:
                    source: BinaryIO
                    if isinstance(request.content, bytes):
                        source = BytesIO(request.content)
                    elif hasattr(request.content, "read"):
                        source = request.content
                    else:
                        raise TypeError(
                            "Object content must be bytes or a binary stream."
                        )
                    while True:
                        chunk = source.read(1024 * 1024)
                        if not chunk:
                            break
                        if not isinstance(chunk, bytes):
                            raise TypeError("Object content stream must return bytes.")
                        destination.write(chunk)
                        digest.update(chunk)
                        size += len(chunk)
                    destination.flush()
                    os.fsync(destination.fileno())
                content_hash = "sha256:" + digest.hexdigest()
                updated_at = datetime.now(timezone.utc)
                info = ObjectInfo(
                    container=request.container,
                    key=normalized_key,
                    content_type=request.content_type,
                    content_length=size,
                    etag=content_hash,
                    content_hash=content_hash,
                    metadata=dict(request.metadata or {}),
                    updated_at=updated_at,
                )
                os.replace(temp_path, full_path)
                self._write_info(full_path, info)
                return info
            finally:
                temp_path.unlink(missing_ok=True)

    async def aput_object(self, request: ObjectWriteRequest) -> ObjectInfo:
        return await self.executor.run(lambda: self.put_object(request))

    def get_object(self, request: ObjectReadRequest) -> ObjectResult | None:
        full_path = self._resolve_path(request.container, request.key)
        with self._lock(full_path):
            if not full_path.is_file():
                return None
            info = self._resolve_info(request.container, request.key, full_path)
            if info is None:
                raise ValueError(
                    "Object content exists but metadata could not be resolved."
                )
            content = full_path.open("rb")
        return ObjectResult(
            container=info.container,
            key=info.key,
            content_type=info.content_type,
            content_length=info.content_length,
            etag=info.etag,
            content_hash=info.content_hash,
            metadata=info.metadata,
            updated_at=info.updated_at,
            content=content,
        )

    async def aget_object(
        self,
        request: ObjectReadRequest,
    ) -> ObjectResult | None:
        return await self.executor.run(lambda: self.get_object(request))

    def delete_object(self, request: ObjectDeleteRequest) -> None:
        full_path = self._resolve_path(request.container, request.key)
        with self._lock(full_path):
            existing = self._resolve_info(request.container, request.key, full_path)
            if existing is None and not full_path.exists():
                return
            if request.if_match and not _etag_matches(
                request.if_match,
                existing.etag if existing is not None else None,
            ):
                raise ValueError(
                    "Object delete precondition failed: ifMatch did not match "
                    "the current etag."
                )
            full_path.unlink(missing_ok=True)
            self._metadata_path(full_path).unlink(missing_ok=True)

    async def adelete_object(self, request: ObjectDeleteRequest) -> None:
        await self.executor.run(lambda: self.delete_object(request))

    def list_objects(self, request: ObjectListRequest) -> ObjectListResult:
        _validate_container(request.container)
        limit = _list_limit(request.limit)
        prefix = (
            _normalize_key(request.prefix, allow_trailing_slash=True)
            if request.prefix
            else ""
        )
        container_path = self._container_path(request.container)
        if not container_path.exists():
            return ObjectListResult(())
        keys = sorted(
            key
            for path in container_path.rglob("*")
            if path.is_file()
            and not self._is_metadata_sidecar(path)
            for key in (path.relative_to(container_path).as_posix(),)
            if key.startswith(prefix)
        )
        offset = _decode_token(request.continuation_token)
        page_keys = keys[offset : offset + limit]
        items: list[ObjectInfo] = []
        for key in page_keys:
            path = self._resolve_path(request.container, key)
            with self._lock(path):
                info = self._resolve_info(request.container, key, path)
            if info is not None:
                items.append(info)
        next_token = (
            _encode_token(offset + len(page_keys))
            if offset + len(page_keys) < len(keys)
            else None
        )
        return ObjectListResult(tuple(items), next_token)

    async def alist_objects(self, request: ObjectListRequest) -> ObjectListResult:
        return await self.executor.run(lambda: self.list_objects(request))

    def diagnostics(self) -> FileObjectStoreDiagnostics:
        if not self.root_path.exists():
            return FileObjectStoreDiagnostics(
                False, False, 0, 0, 0, 0, 0, 0, 0, 0
            )
        containers = [
            path
            for path in self.root_path.iterdir()
            if path.is_dir() and path.name != TEMP_DIRECTORY_NAME
        ]
        object_count = 0
        sidecars = 0
        missing = 0
        orphan = 0
        temporary = 0
        content_bytes = 0
        temporary_bytes = 0
        for path in self.root_path.rglob("*"):
            if not path.is_file():
                continue
            if self._is_temp_path(path):
                temporary += 1
                temporary_bytes += path.stat().st_size
            elif self._is_metadata_sidecar(path):
                sidecars += 1
                if not Path(str(path)[: -len(METADATA_SUFFIX)]).is_file():
                    orphan += 1
            else:
                object_count += 1
                content_bytes += path.stat().st_size
                if not self._metadata_path(path).is_file():
                    missing += 1
        healthy = missing == 0 and orphan == 0 and temporary == 0
        return FileObjectStoreDiagnostics(
            root_exists=True,
            healthy=healthy,
            container_count=len(containers),
            object_count=object_count,
            metadata_sidecar_count=sidecars,
            missing_metadata_count=missing,
            orphan_metadata_count=orphan,
            temporary_file_count=temporary,
            content_bytes=content_bytes,
            temporary_bytes=temporary_bytes,
        )

    async def adiagnostics(self) -> FileObjectStoreDiagnostics:
        return await self.executor.run(self.diagnostics)

    def close(self) -> None:
        if self._owns_executor:
            self.executor.close()

    def _container_path(self, container: str) -> Path:
        _validate_container(container)
        candidate = (self.root_path / container).resolve()
        _ensure_inside(candidate, self.root_path)
        return candidate

    def _resolve_path(self, container: str, key: str) -> Path:
        container_path = self._container_path(container)
        normalized = _normalize_key(key)
        candidate = (container_path / Path(normalized)).resolve()
        _ensure_inside(candidate, container_path)
        return candidate

    def _metadata_path(self, full_path: Path) -> Path:
        return Path(str(full_path) + METADATA_SUFFIX)

    def _read_info(self, full_path: Path) -> ObjectInfo | None:
        sidecar = self._metadata_path(full_path)
        if not sidecar.is_file():
            return None
        try:
            material = json.loads(sidecar.read_text(encoding="utf-8"))
            if not isinstance(material, Mapping):
                return None
            return ObjectInfo.from_dict(material)
        except (OSError, TypeError, ValueError, json.JSONDecodeError):
            return None

    def _resolve_info(
        self,
        container: str,
        key: str,
        full_path: Path,
    ) -> ObjectInfo | None:
        info = self._read_info(full_path)
        if info is not None:
            return info
        if not full_path.is_file():
            return None
        digest = sha256()
        with full_path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        content_hash = "sha256:" + digest.hexdigest()
        return ObjectInfo(
            container=container,
            key=_normalize_key(key),
            content_type=None,
            content_length=full_path.stat().st_size,
            etag=content_hash,
            content_hash=content_hash,
            metadata={},
            updated_at=datetime.fromtimestamp(
                full_path.stat().st_mtime,
                tz=timezone.utc,
            ),
        )

    def _write_info(self, full_path: Path, info: ObjectInfo) -> None:
        destination = self._metadata_path(full_path)
        temp_path = self._temp_path()
        try:
            with temp_path.open("x", encoding="utf-8") as stream:
                json.dump(info.to_dict(), stream, separators=(",", ":"), ensure_ascii=True)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temp_path, destination)
        finally:
            temp_path.unlink(missing_ok=True)

    def _temp_path(self) -> Path:
        directory = self.root_path / TEMP_DIRECTORY_NAME
        directory.mkdir(parents=True, exist_ok=True)
        descriptor, name = tempfile.mkstemp(prefix="", suffix=".tmp", dir=directory)
        os.close(descriptor)
        path = Path(name)
        path.unlink()
        return path

    def _lock(self, full_path: Path) -> _FileLock:
        identifier = sha256(str(full_path).encode("utf-8")).hexdigest()
        with self._guard:
            thread_lock = self._thread_locks.setdefault(identifier, RLock())
        return _FileLock(self.lock_root_path / (identifier + ".lock"), thread_lock)

    def _is_temp_path(self, path: Path) -> bool:
        try:
            path.resolve().relative_to((self.root_path / TEMP_DIRECTORY_NAME).resolve())
            return True
        except ValueError:
            return False

    def _is_metadata_sidecar(self, path: Path) -> bool:
        if not path.name.endswith(METADATA_SUFFIX):
            return False
        content = Path(str(path)[: -len(METADATA_SUFFIX)])
        if content.is_file():
            return True
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(value, Mapping):
                return False
            info = ObjectInfo.from_dict(value)
            return path.resolve() == self._metadata_path(
                self._resolve_path(info.container, info.key)
            ).resolve()
        except (OSError, TypeError, ValueError, json.JSONDecodeError):
            return False


def _validate_container(container: str) -> None:
    if not container.strip():
        raise ValueError("Container is required.")
    if not 3 <= len(container) <= 63:
        raise ValueError("Container must be between 3 and 63 characters.")
    if not container[0].isalnum() or not container[-1].isalnum():
        raise ValueError("Container must start and end with a letter or digit.")
    if any(
        not (
            "a" <= character <= "z"
            or "0" <= character <= "9"
            or character == "-"
        )
        for character in container
    ):
        raise ValueError(
            "Container can only contain lowercase letters, digits, and '-'."
        )
    if "--" in container:
        raise ValueError("Container cannot contain consecutive '-' characters.")


def _normalize_key(key: str, allow_trailing_slash: bool = False) -> str:
    if not key.strip():
        raise ValueError("Object key is required.")
    if key.startswith(("/", "\\")):
        raise ValueError(
            "Object key must be relative and cannot contain traversal segments."
        )
    normalized = key.replace("\\", "/").lstrip("/")
    validation = normalized.rstrip("/") if allow_trailing_slash else normalized
    if any(segment in {"", ".", ".."} for segment in validation.split("/")):
        raise ValueError(
            "Object key must be relative and cannot contain traversal segments."
        )
    return normalized


def _validate_metadata(metadata: Mapping[str, str] | None) -> None:
    if metadata is None:
        return
    for key, value in metadata.items():
        if not key.strip():
            raise ValueError("Object metadata keys are required.")
        if key.lower().startswith("vyral_"):
            raise ValueError(
                f"Object metadata key {key!r} uses the reserved 'vyral_' prefix."
            )
        if not (
            (key[0].isalpha() or key[0] == "_")
            and all(character.isalnum() or character == "_" for character in key)
        ):
            raise ValueError(
                f"Object metadata key {key!r} must start with a letter or '_' "
                "and contain only letters, digits, and '_'."
            )
        if not isinstance(value, str):
            raise TypeError(f"Object metadata value for key {key!r} must be a string.")


def _validate_write_conditions(
    request: ObjectWriteRequest,
    existing: ObjectInfo | None,
) -> None:
    if request.if_match and not _etag_matches(
        request.if_match,
        existing.etag if existing is not None else None,
    ):
        raise ValueError(
            "Object write precondition failed: ifMatch did not match the current etag."
        )
    if request.if_none_match:
        if request.if_none_match == "*" and existing is not None:
            raise ValueError(
                "Object write precondition failed: ifNoneMatch '*' found an "
                "existing object."
            )
        if existing is not None and _etag_matches(
            request.if_none_match,
            existing.etag,
        ):
            raise ValueError(
                "Object write precondition failed: ifNoneMatch matched the "
                "current etag."
            )


def _etag_matches(requested: str, current: str | None) -> bool:
    return current is not None if requested == "*" else requested == current


def _list_limit(limit: int | None) -> int:
    if limit is not None and limit <= 0:
        raise ValueError("Object list limit must be greater than zero.")
    selected = limit if limit is not None else DEFAULT_OBJECT_LIST_LIMIT
    if selected > MAX_OBJECT_LIST_LIMIT:
        raise ValueError(
            f"Object list limit cannot exceed {MAX_OBJECT_LIST_LIMIT}."
        )
    return selected


def _encode_token(offset: int) -> str:
    return b64encode(str(offset).encode("utf-8")).decode("ascii")


def _decode_token(token: str | None) -> int:
    if token is None or not token.strip():
        return 0
    try:
        decoded = b64decode(token, validate=True).decode("utf-8")
        offset = int(decoded)
    except (ValueError, UnicodeDecodeError) as exc:
        raise ValueError("Object continuationToken is invalid.") from exc
    if offset < 0:
        raise ValueError("Object continuationToken is invalid.")
    return offset


def _ensure_inside(path: Path, root: Path) -> None:
    try:
        path.relative_to(root)
    except ValueError as exc:
        raise ValueError(
            "Object key must remain inside the configured root."
        ) from exc


def _format_datetime(value: datetime) -> str:
    normalized = value.astimezone(timezone.utc)
    return normalized.isoformat(timespec="microseconds").replace("+00:00", "Z")


def _parse_datetime(value: object, name: str) -> datetime:
    text = _required_text(value, name)
    parsed = parse_iso_datetime(text)
    if parsed.tzinfo is None:
        raise ValueError(f"{name} must include a UTC offset.")
    return parsed.astimezone(timezone.utc)


def _required_text(value: object, name: str) -> str:
    if not isinstance(value, str):
        raise TypeError(f"{name} must be a string.")
    return value


def _optional_text(value: object, name: str) -> str | None:
    return None if value is None else _required_text(value, name)


def _required_int(value: object, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise TypeError(f"{name} must be an integer.")
    return value
