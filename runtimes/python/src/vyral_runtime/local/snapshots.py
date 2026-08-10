from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
from datetime import datetime
from hashlib import sha256
import json
import math
import re
import struct
from typing import Any, Mapping

from .models import (
    JSONObject,
    RecordBatchResult,
    RecordCollectionPolicy,
    VyralRecord,
    _format_datetime,
    _integer,
    _mapping,
    _optional_integer,
    _optional_text,
    _parse_datetime,
)
from .query_models import QueryEnvelope


MAX_COLLECTION_SNAPSHOT_RECORDS = 10_000


def _boolean(value: object, name: str, default: bool) -> bool:
    if value is None:
        return default
    if not isinstance(value, bool):
        raise TypeError(f"{name} must be a boolean")
    return value


@dataclass(frozen=True)
class CollectionExportRequest:
    query: QueryEnvelope | None = None
    max_records: int | None = None
    fail_on_limit_exceeded: bool = True

    @classmethod
    def from_value(
        cls,
        value: CollectionExportRequest | Mapping[str, Any] | None,
    ) -> CollectionExportRequest:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        item = _mapping(value, "collection export request")
        raw_query = item.get("query")
        return cls(
            query=QueryEnvelope.from_value(raw_query) if raw_query is not None else None,
            max_records=_optional_integer(
                item.get("maxRecords"),
                "collection export maxRecords",
            ),
            fail_on_limit_exceeded=_boolean(
                item.get("failOnLimitExceeded"),
                "collection export failOnLimitExceeded",
                True,
            ),
        )

    def to_dict(self) -> JSONObject:
        return {
            "query": self.query.to_dict() if self.query is not None else None,
            "maxRecords": self.max_records,
            "failOnLimitExceeded": self.fail_on_limit_exceeded,
        }


@dataclass(frozen=True)
class CollectionExportEnvelope:
    collection: str
    policy: RecordCollectionPolicy
    records: tuple[VyralRecord, ...]
    query: QueryEnvelope | None = None
    max_records: int | None = None
    record_count: int | None = None
    truncated: bool = False
    continuation_token: str | None = None
    exported_at: datetime | None = None
    content_hash: str | None = None

    @classmethod
    def from_value(
        cls,
        value: CollectionExportEnvelope | Mapping[str, Any],
    ) -> CollectionExportEnvelope:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "collection export snapshot")
        raw_policy = _mapping(item.get("policy"), "collection export policy")
        raw_records = item.get("records")
        if not isinstance(raw_records, (list, tuple)):
            raise TypeError("collection export records must be an array")
        raw_query = item.get("query")
        return cls(
            collection=_optional_text(
                item.get("collection"),
                "collection export collection",
            )
            or "",
            policy=RecordCollectionPolicy.from_value(raw_policy),
            records=tuple(VyralRecord.from_value(record) for record in raw_records),
            query=QueryEnvelope.from_value(raw_query) if raw_query is not None else None,
            max_records=_optional_integer(
                item.get("maxRecords"),
                "collection export maxRecords",
            ),
            record_count=_optional_integer(
                item.get("recordCount"),
                "collection export recordCount",
            ),
            truncated=_boolean(
                item.get("truncated"),
                "collection export truncated",
                False,
            ),
            continuation_token=_optional_text(
                item.get("continuationToken"),
                "collection export continuationToken",
            ),
            exported_at=_parse_datetime(
                item.get("exportedAt"),
                "collection export exportedAt",
            ),
            content_hash=_optional_text(
                item.get("contentHash"),
                "collection export contentHash",
            ),
        )

    def with_hash(self, content_hash: str) -> CollectionExportEnvelope:
        return CollectionExportEnvelope(
            collection=self.collection,
            policy=self.policy,
            records=self.records,
            query=self.query,
            max_records=self.max_records,
            record_count=self.record_count,
            truncated=self.truncated,
            continuation_token=self.continuation_token,
            exported_at=self.exported_at,
            content_hash=content_hash,
        )

    def to_dict(self) -> JSONObject:
        return {
            "collection": self.collection,
            "policy": self.policy.to_dict(),
            "records": [record.to_dict() for record in self.records],
            "query": self.query.to_dict() if self.query is not None else None,
            "maxRecords": self.max_records,
            "recordCount": self.record_count,
            "truncated": self.truncated,
            "continuationToken": self.continuation_token,
            "exportedAt": _format_datetime(self.exported_at),
            "contentHash": self.content_hash,
        }


@dataclass(frozen=True)
class CollectionImportRequest:
    snapshot: CollectionExportEnvelope
    expected_content_hash: str | None = None
    replace_existing: bool = False
    continue_on_error: bool = False
    allow_collection_rename: bool = False
    allow_partial_snapshot: bool = False

    @classmethod
    def from_value(
        cls,
        value: CollectionImportRequest | Mapping[str, Any],
    ) -> CollectionImportRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "collection import request")
        return cls(
            snapshot=CollectionExportEnvelope.from_value(
                _mapping(item.get("snapshot"), "collection import snapshot")
            ),
            expected_content_hash=_optional_text(
                item.get("expectedContentHash"),
                "collection import expectedContentHash",
            ),
            replace_existing=_boolean(
                item.get("replaceExisting"),
                "collection import replaceExisting",
                False,
            ),
            continue_on_error=_boolean(
                item.get("continueOnError"),
                "collection import continueOnError",
                False,
            ),
            allow_collection_rename=_boolean(
                item.get("allowCollectionRename"),
                "collection import allowCollectionRename",
                False,
            ),
            allow_partial_snapshot=_boolean(
                item.get("allowPartialSnapshot"),
                "collection import allowPartialSnapshot",
                False,
            ),
        )

    def to_dict(self) -> JSONObject:
        return {
            "snapshot": self.snapshot.to_dict(),
            "expectedContentHash": self.expected_content_hash,
            "replaceExisting": self.replace_existing,
            "continueOnError": self.continue_on_error,
            "allowCollectionRename": self.allow_collection_rename,
            "allowPartialSnapshot": self.allow_partial_snapshot,
        }


@dataclass(frozen=True)
class CollectionSnapshotHashComparison:
    expected_hash: str | None
    actual_hash: str | None
    compared: bool
    matches: bool
    status: str
    kind: str = "collectionSnapshot"

    def to_dict(self) -> JSONObject:
        return {
            "kind": self.kind,
            "expectedHash": self.expected_hash,
            "actualHash": self.actual_hash,
            "compared": self.compared,
            "matches": self.matches,
            "status": self.status,
        }


@dataclass(frozen=True)
class CollectionImportResult:
    collection: str
    source_collection: str
    policy_status: str
    record_count: int
    content_hash: str
    content_hash_comparison: CollectionSnapshotHashComparison
    records: RecordBatchResult

    def to_dict(self) -> JSONObject:
        return {
            "collection": self.collection,
            "sourceCollection": self.source_collection,
            "policyStatus": self.policy_status,
            "recordCount": self.record_count,
            "contentHash": self.content_hash,
            "contentHashComparison": self.content_hash_comparison.to_dict(),
            "records": self.records.to_dict(),
        }


@dataclass(frozen=True)
class _Float32:
    value: float


_UNICODE_ESCAPE = re.compile(r"\\u([0-9a-fA-F]{4})")


def _dotnet_string(value: str) -> str:
    encoded = json.dumps(value, ensure_ascii=True, separators=(",", ":"))
    for character, escape in (
        ("<", "\\u003C"),
        (">", "\\u003E"),
        ("&", "\\u0026"),
        ("+", "\\u002B"),
        ("'", "\\u0027"),
        ("`", "\\u0060"),
    ):
        encoded = encoded.replace(character, escape)
    return _UNICODE_ESCAPE.sub(
        lambda match: "\\u" + match.group(1).upper(),
        encoded,
    )


def _snapshot_vector(value: Mapping[str, Any]) -> JSONObject:
    raw_values = value.get("values")
    if not isinstance(raw_values, list):
        raise TypeError("snapshot vector values must be an array")
    return {
        "values": [_Float32(float(item)) for item in raw_values],
        "dimensions": value.get("dimensions"),
        "model": value.get("model"),
        "datatype": value.get("datatype"),
        "distanceFunction": value.get("distanceFunction"),
        "generatedAt": value.get("generatedAt"),
        "sourceField": value.get("sourceField"),
    }


def _snapshot_record(record: VyralRecord) -> JSONObject:
    material = record.to_dict()
    raw_vectors = material.get("vectors")
    vectors: JSONObject | None = None
    if isinstance(raw_vectors, Mapping):
        vectors = {
            str(name): _snapshot_vector(_mapping(vector, "snapshot vector"))
            for name, vector in raw_vectors.items()
        }
    known: JSONObject = {
        "id": material.get("id"),
        "partitionKey": material.get("partitionKey"),
        "type": material.get("type"),
        "schemaVersion": material.get("schemaVersion"),
        "metadata": material.get("metadata"),
        "content": material.get("content"),
        "sources": material.get("sources"),
        "vectors": vectors,
        "etag": material.get("etag"),
        "revision": material.get("revision"),
        "createdAt": material.get("createdAt"),
        "updatedAt": material.get("updatedAt"),
    }
    for key, value in material.items():
        if key not in known:
            known[key] = value
    return known


def _float32_text(value: float) -> str:
    packed = struct.pack("<f", value)
    normalized = float(struct.unpack("<f", packed)[0])
    if not math.isfinite(normalized):
        raise ValueError("Snapshot float32 values must be finite.")
    if normalized == 0:
        return "-0" if math.copysign(1.0, normalized) < 0 else "0"
    for precision in range(1, 10):
        candidate = format(normalized, f".{precision}g")
        if struct.pack("<f", float(candidate)) == packed:
            return candidate.replace("e", "E")
    raise ValueError("Snapshot float32 value could not be serialized.")


def _dotnet_json(value: object) -> str:
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, _Float32):
        return _float32_text(value.value)
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError("Snapshot numeric values must be finite.")
        return repr(value).replace("e", "E")
    if isinstance(value, str):
        return _dotnet_string(value)
    if isinstance(value, (list, tuple)):
        return "[" + ",".join(_dotnet_json(item) for item in value) + "]"
    if isinstance(value, Mapping):
        entries: list[str] = []
        for key, item in value.items():
            if not isinstance(key, str):
                raise TypeError("Snapshot object keys must be strings.")
            entries.append(_dotnet_json(key) + ":" + _dotnet_json(item))
        return "{" + ",".join(entries) + "}"
    raise TypeError(f"Snapshot value type {type(value).__name__!r} is not JSON.")


def compute_collection_snapshot_hash(snapshot: CollectionExportEnvelope) -> str:
    material: JSONObject = {
        "collection": snapshot.collection,
        "policy": snapshot.policy.to_dict(),
        "records": [_snapshot_record(record) for record in snapshot.records],
    }
    return "sha256:" + sha256(_dotnet_json(material).encode("utf-8")).hexdigest()


def build_hash_comparison(
    expected_hash: str | None,
    actual_hash: str | None,
) -> CollectionSnapshotHashComparison:
    expected = expected_hash.strip() if expected_hash and expected_hash.strip() else None
    actual = actual_hash.strip() if actual_hash and actual_hash.strip() else None
    if expected is None:
        return CollectionSnapshotHashComparison(
            expected_hash=None,
            actual_hash=actual,
            compared=False,
            matches=False,
            status="not_provided",
        )
    matches = expected == actual
    return CollectionSnapshotHashComparison(
        expected_hash=expected,
        actual_hash=actual,
        compared=True,
        matches=matches,
        status="actual_missing" if actual is None else "matched" if matches else "drifted",
    )


def renamed_policy(policy: RecordCollectionPolicy, name: str) -> RecordCollectionPolicy:
    material = deepcopy(policy.to_dict())
    material["name"] = name
    return RecordCollectionPolicy.from_value(material)
