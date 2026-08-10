from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Mapping, TypeAlias

from .._datetime import parse_iso_datetime

JSONObject: TypeAlias = dict[str, Any]


def _mapping(value: object, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise TypeError(f"{name} must be a mapping")
    return value


def _optional_mapping(value: object, name: str) -> JSONObject | None:
    if value is None:
        return None
    return deepcopy(dict(_mapping(value, name)))


def _text(value: object, name: str, default: str | None = None) -> str:
    if value is None and default is not None:
        return default
    if not isinstance(value, str):
        raise TypeError(f"{name} must be a string")
    return value


def _optional_text(value: object, name: str) -> str | None:
    if value is None:
        return None
    return _text(value, name)


def _integer(value: object, name: str, default: int = 0) -> int:
    if value is None:
        return default
    if isinstance(value, bool) or not isinstance(value, int):
        raise TypeError(f"{name} must be an integer")
    return value


def _optional_integer(value: object, name: str) -> int | None:
    if value is None:
        return None
    return _integer(value, name)


def _parse_datetime(value: object, name: str) -> datetime | None:
    if value is None:
        return None
    if isinstance(value, datetime):
        parsed = value
    elif isinstance(value, str):
        try:
            parsed = parse_iso_datetime(value)
        except ValueError as exc:
            raise ValueError(f"{name} must be an ISO-8601 timestamp") from exc
    else:
        raise TypeError(f"{name} must be a datetime or ISO-8601 string")
    if parsed.tzinfo is None:
        raise ValueError(f"{name} must include a UTC offset")
    return parsed.astimezone(timezone.utc)


def _format_datetime(value: datetime | None) -> str | None:
    if value is None:
        return None
    normalized = value.astimezone(timezone.utc)
    base = normalized.strftime("%Y-%m-%dT%H:%M:%S")
    fraction = f"{normalized.microsecond:06d}".rstrip("0")
    return base + (f".{fraction}" if fraction else "") + "Z"


@dataclass(frozen=True)
class VectorFieldPolicy:
    name: str
    path: str
    dimensions: int
    datatype: str = "float32"
    distance_function: str = "cosine"
    index_type: str = "flat"

    @classmethod
    def from_value(cls, value: VectorFieldPolicy | Mapping[str, Any]) -> VectorFieldPolicy:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "vector policy")
        return cls(
            name=_text(item.get("name"), "vector policy name"),
            path=_text(item.get("path"), "vector policy path"),
            dimensions=_integer(item.get("dimensions"), "vector policy dimensions"),
            datatype=_text(item.get("datatype"), "vector policy datatype", "float32"),
            distance_function=_text(
                item.get("distanceFunction"),
                "vector policy distanceFunction",
                "cosine",
            ),
            index_type=_text(item.get("indexType"), "vector policy indexType", "flat"),
        )

    def to_dict(self) -> JSONObject:
        return {
            "name": self.name,
            "path": self.path,
            "dimensions": self.dimensions,
            "datatype": self.datatype,
            "distanceFunction": self.distance_function,
            "indexType": self.index_type,
        }


@dataclass(frozen=True)
class RecordCollectionPolicy:
    name: str
    partition_key_path: str = "/partitionKey"
    vector_policies: tuple[VectorFieldPolicy, ...] = ()
    indexed_metadata: tuple[str, ...] = ()

    @classmethod
    def from_value(
        cls,
        value: RecordCollectionPolicy | Mapping[str, Any],
    ) -> RecordCollectionPolicy:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "collection policy")
        raw_vectors = item.get("vectorPolicies", [])
        raw_metadata = item.get("indexedMetadata", [])
        if not isinstance(raw_vectors, (list, tuple)):
            raise TypeError("collection policy vectorPolicies must be an array")
        if not isinstance(raw_metadata, (list, tuple)) or not all(
            isinstance(path, str) for path in raw_metadata
        ):
            raise TypeError("collection policy indexedMetadata must be an array of strings")
        return cls(
            name=_text(item.get("name"), "collection policy name"),
            partition_key_path=_text(
                item.get("partitionKeyPath"),
                "collection policy partitionKeyPath",
                "/partitionKey",
            ),
            vector_policies=tuple(VectorFieldPolicy.from_value(entry) for entry in raw_vectors),
            indexed_metadata=tuple(raw_metadata),
        )

    def to_dict(self) -> JSONObject:
        return {
            "name": self.name,
            "partitionKeyPath": self.partition_key_path,
            "vectorPolicies": [policy.to_dict() for policy in self.vector_policies],
            "indexedMetadata": list(self.indexed_metadata),
        }


@dataclass(frozen=True)
class VyralVector:
    values: tuple[float, ...]
    dimensions: int = 0
    model: str | None = None
    datatype: str = "float32"
    distance_function: str = "cosine"
    generated_at: datetime | None = None
    source_field: str | None = None

    @classmethod
    def from_value(cls, value: VyralVector | Mapping[str, Any]) -> VyralVector:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "vector")
        raw_values = item.get("values")
        if not isinstance(raw_values, (list, tuple)):
            raise TypeError("vector values must be an array")
        values: list[float] = []
        for entry in raw_values:
            if isinstance(entry, bool) or not isinstance(entry, (int, float)):
                raise TypeError("vector values must contain only numbers")
            values.append(float(entry))
        return cls(
            values=tuple(values),
            dimensions=_integer(item.get("dimensions"), "vector dimensions"),
            model=_optional_text(item.get("model"), "vector model"),
            datatype=_text(item.get("datatype"), "vector datatype", "float32"),
            distance_function=_text(
                item.get("distanceFunction"),
                "vector distanceFunction",
                "cosine",
            ),
            generated_at=_parse_datetime(item.get("generatedAt"), "vector generatedAt"),
            source_field=_optional_text(item.get("sourceField"), "vector sourceField"),
        )

    def normalized(self, values: tuple[float, ...], dimensions: int) -> VyralVector:
        return VyralVector(
            values=values,
            dimensions=dimensions,
            model=self.model,
            datatype=self.datatype,
            distance_function=self.distance_function,
            generated_at=self.generated_at,
            source_field=self.source_field,
        )

    def to_dict(self) -> JSONObject:
        return {
            "values": list(self.values),
            "dimensions": self.dimensions,
            "model": self.model,
            "datatype": self.datatype,
            "distanceFunction": self.distance_function,
            "generatedAt": _format_datetime(self.generated_at),
            "sourceField": self.source_field,
        }


@dataclass(frozen=True)
class VyralRecord:
    id: str
    partition_key: str
    type: str = ""
    schema_version: str | None = None
    metadata: JSONObject | None = None
    content: JSONObject | None = None
    sources: tuple[JSONObject, ...] | None = None
    vectors: Mapping[str, VyralVector] | None = None
    etag: str | None = None
    revision: int | None = None
    created_at: datetime | None = None
    updated_at: datetime | None = None
    additional_properties: JSONObject = field(default_factory=dict)

    @classmethod
    def from_value(cls, value: VyralRecord | Mapping[str, Any]) -> VyralRecord:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "record")
        known = {
            "id",
            "partitionKey",
            "type",
            "schemaVersion",
            "metadata",
            "content",
            "sources",
            "vectors",
            "etag",
            "revision",
            "createdAt",
            "updatedAt",
        }
        raw_sources = item.get("sources")
        if raw_sources is not None and not isinstance(raw_sources, (list, tuple)):
            raise TypeError("record sources must be an array")
        sources = (
            tuple(deepcopy(dict(_mapping(source, "record source"))) for source in raw_sources)
            if raw_sources is not None
            else None
        )
        raw_vectors = item.get("vectors")
        if raw_vectors is not None and not isinstance(raw_vectors, Mapping):
            raise TypeError("record vectors must be an object")
        vectors = (
            {
                _text(name, "vector field name"): VyralVector.from_value(vector)
                for name, vector in raw_vectors.items()
            }
            if raw_vectors is not None
            else None
        )
        return cls(
            id=_text(item.get("id"), "record id"),
            partition_key=_text(item.get("partitionKey"), "record partitionKey"),
            type=_text(item.get("type"), "record type", ""),
            schema_version=_optional_text(item.get("schemaVersion"), "record schemaVersion"),
            metadata=_optional_mapping(item.get("metadata"), "record metadata"),
            content=_optional_mapping(item.get("content"), "record content"),
            sources=sources,
            vectors=vectors,
            etag=_optional_text(item.get("etag"), "record etag"),
            revision=_optional_integer(item.get("revision"), "record revision"),
            created_at=_parse_datetime(item.get("createdAt"), "record createdAt"),
            updated_at=_parse_datetime(item.get("updatedAt"), "record updatedAt"),
            additional_properties={
                key: deepcopy(entry) for key, entry in item.items() if key not in known
            },
        )

    def stored(
        self,
        *,
        revision: int,
        created_at: datetime,
        updated_at: datetime,
        vectors: Mapping[str, VyralVector] | None,
    ) -> VyralRecord:
        return VyralRecord(
            id=self.id,
            partition_key=self.partition_key,
            type=self.type,
            schema_version=self.schema_version,
            metadata=deepcopy(self.metadata),
            content=deepcopy(self.content),
            sources=deepcopy(self.sources),
            vectors=deepcopy(vectors),
            etag=f"rev:{revision}",
            revision=revision,
            created_at=created_at,
            updated_at=updated_at,
            additional_properties=deepcopy(self.additional_properties),
        )

    def to_dict(self) -> JSONObject:
        value: JSONObject = {
            "id": self.id,
            "partitionKey": self.partition_key,
            "type": self.type,
            "schemaVersion": self.schema_version,
            "metadata": deepcopy(self.metadata),
            "content": deepcopy(self.content),
            "sources": deepcopy(list(self.sources)) if self.sources is not None else None,
            "vectors": (
                {name: vector.to_dict() for name, vector in self.vectors.items()}
                if self.vectors is not None
                else None
            ),
            "etag": self.etag,
            "revision": self.revision,
            "createdAt": _format_datetime(self.created_at),
            "updatedAt": _format_datetime(self.updated_at),
        }
        value.update(deepcopy(self.additional_properties))
        return value


@dataclass(frozen=True)
class RecordWritePrecondition:
    if_match: str | None = None
    if_none_match: str | None = None
    expected_etag: str | None = None
    expected_revision: int | None = None

    @classmethod
    def from_value(
        cls,
        value: RecordWritePrecondition | Mapping[str, Any] | None,
    ) -> RecordWritePrecondition | None:
        if value is None or isinstance(value, cls):
            return value
        item = _mapping(value, "record precondition")
        return cls(
            if_match=_optional_text(item.get("ifMatch"), "precondition ifMatch"),
            if_none_match=_optional_text(item.get("ifNoneMatch"), "precondition ifNoneMatch"),
            expected_etag=_optional_text(
                item.get("expectedEtag"),
                "precondition expectedEtag",
            ),
            expected_revision=_optional_integer(
                item.get("expectedRevision"),
                "precondition expectedRevision",
            ),
        )

    @property
    def has_conditions(self) -> bool:
        return any(
            value is not None and (not isinstance(value, str) or bool(value.strip()))
            for value in (
                self.if_match,
                self.if_none_match,
                self.expected_etag,
                self.expected_revision,
            )
        )


@dataclass(frozen=True)
class RecordBatchItemResult:
    index: int
    id: str | None
    partition_key: str | None
    status: str
    etag: str | None = None
    revision: int | None = None
    error: str | None = None

    def to_dict(self) -> JSONObject:
        return {
            "index": self.index,
            "id": self.id,
            "partitionKey": self.partition_key,
            "status": self.status,
            "etag": self.etag,
            "revision": self.revision,
            "error": self.error,
        }


@dataclass(frozen=True)
class RecordBatchResult:
    collection: str
    requested: int
    attempted: int
    succeeded: int
    failed: int
    stopped_on_error: bool
    items: tuple[RecordBatchItemResult, ...]

    def to_dict(self) -> JSONObject:
        return {
            "collection": self.collection,
            "requested": self.requested,
            "attempted": self.attempted,
            "succeeded": self.succeeded,
            "failed": self.failed,
            "stoppedOnError": self.stopped_on_error,
            "items": [item.to_dict() for item in self.items],
        }
