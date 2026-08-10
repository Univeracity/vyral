from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
from importlib import resources
import json
from pathlib import Path
from typing import Any, Mapping, TypeAlias

from ._version import CONTRACT_VERSION


JSONPrimitive: TypeAlias = str | int | float | bool | None
JSONValue: TypeAlias = JSONPrimitive | list["JSONValue"] | dict[str, "JSONValue"]
JSONObject: TypeAlias = dict[str, JSONValue]

_HTTP_METHODS = frozenset({"get", "post", "put", "patch", "delete"})
_STABILITIES = frozenset({"public", "preview", "internal", "deprecated"})
_RESOURCE_FILES = {
    "catalog": "public-sdk-surface.json",
    "schema": "vyral-public.schema.json",
    "openapi": "vyral.openapi.json",
}


class ContractBundleError(RuntimeError):
    """Raised when packaged Vyral contract resources are missing or inconsistent."""


@dataclass(frozen=True)
class ContractBundleSummary:
    contract_version: str
    openapi_version: str
    operation_count: int
    rest_operation_count: int
    schema_count: int
    catalog_sha256: str
    schema_sha256: str
    openapi_sha256: str

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "contractVersion": self.contract_version,
            "openApiVersion": self.openapi_version,
            "operationCount": self.operation_count,
            "restOperationCount": self.rest_operation_count,
            "schemaCount": self.schema_count,
            "catalogSha256": self.catalog_sha256,
            "schemaSha256": self.schema_sha256,
            "openApiSha256": self.openapi_sha256,
        }


@dataclass(frozen=True)
class ContractBundle:
    catalog: Mapping[str, Any]
    schema: Mapping[str, Any]
    openapi: Mapping[str, Any]
    summary: ContractBundleSummary


def _repository_root() -> Path | None:
    for parent in Path(__file__).resolve().parents:
        if (parent / "contracts/public-sdk-surface.json").is_file():
            return parent
    return None


def _read_contract_resource(kind: str) -> bytes:
    name = _RESOURCE_FILES[kind]
    packaged = resources.files("vyral_runtime._contracts").joinpath(name)
    if packaged.is_file():
        return packaged.read_bytes()

    root = _repository_root()
    if root is not None:
        source_paths = {
            "catalog": root / "contracts/public-sdk-surface.json",
            "schema": root / "contracts/schemas/vyral-public.schema.json",
            "openapi": root / "src/Vyral.Server/contracts/vyral.openapi.json",
        }
        return source_paths[kind].read_bytes()

    raise ContractBundleError(
        f"The packaged Vyral contract resource {name!r} is unavailable."
    )


def _parse_document(kind: str, raw: bytes) -> dict[str, Any]:
    try:
        value = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ContractBundleError(f"The {kind} contract is not valid UTF-8 JSON.") from exc
    if not isinstance(value, dict):
        raise ContractBundleError(f"The {kind} contract must be a JSON object.")
    return value


def _openapi_operations(openapi: Mapping[str, Any]) -> dict[str, Mapping[str, Any]]:
    paths = openapi.get("paths")
    if not isinstance(paths, dict):
        raise ContractBundleError("OpenAPI paths must be an object.")

    operations: dict[str, Mapping[str, Any]] = {}
    for path, path_item in paths.items():
        if not isinstance(path, str) or not isinstance(path_item, dict):
            raise ContractBundleError("OpenAPI path entries must be named objects.")
        for method, operation in path_item.items():
            if method not in _HTTP_METHODS:
                continue
            if not isinstance(operation, dict):
                raise ContractBundleError(f"{method.upper()} {path} must be an object.")
            operation_id = operation.get("operationId")
            if not isinstance(operation_id, str) or not operation_id:
                raise ContractBundleError(f"{method.upper()} {path} has no operationId.")
            if operation_id in operations:
                raise ContractBundleError(f"Duplicate OpenAPI operation id {operation_id!r}.")
            operations[operation_id] = operation
    return operations


def _validate_bundle(
    catalog: Mapping[str, Any],
    schema: Mapping[str, Any],
    openapi: Mapping[str, Any],
) -> tuple[int, int, int]:
    catalog_version = catalog.get("catalogVersion")
    schema_version = schema.get("version")
    openapi_version = openapi.get("info", {}).get("version") if isinstance(openapi.get("info"), dict) else None
    versions = {
        "catalog": catalog_version,
        "schema": schema_version,
        "OpenAPI": openapi_version,
    }
    for label, version in versions.items():
        if version != CONTRACT_VERSION:
            raise ContractBundleError(
                f"{label} contract version must be {CONTRACT_VERSION!r}, found {version!r}."
            )

    if openapi.get("openapi") != "3.1.0":
        raise ContractBundleError("The Python runtime requires OpenAPI 3.1.0.")
    if catalog.get("openApiVersion") != "3.1.0":
        raise ContractBundleError("The public SDK catalog must declare OpenAPI 3.1.0.")
    if schema.get("$schema") != "https://json-schema.org/draft/2020-12/schema":
        raise ContractBundleError("The public schema must use JSON Schema 2020-12.")

    definitions = schema.get("$defs")
    if not isinstance(definitions, dict) or not definitions:
        raise ContractBundleError("The public schema has no definitions.")

    openapi_operations = _openapi_operations(openapi)
    catalog_operations = catalog.get("operations")
    if not isinstance(catalog_operations, list) or not catalog_operations:
        raise ContractBundleError("The public SDK catalog has no operations.")

    semantic_ids: set[str] = set()
    catalog_rest_ids: set[str] = set()
    for entry in catalog_operations:
        if not isinstance(entry, dict):
            raise ContractBundleError("Every public SDK operation must be an object.")
        semantic_id = entry.get("id")
        if not isinstance(semantic_id, str) or not semantic_id:
            raise ContractBundleError("Every public SDK operation must have an id.")
        if semantic_id in semantic_ids:
            raise ContractBundleError(f"Duplicate semantic operation id {semantic_id!r}.")
        semantic_ids.add(semantic_id)

        stability = entry.get("stability")
        if stability not in _STABILITIES:
            raise ContractBundleError(
                f"Operation {semantic_id!r} has unknown stability {stability!r}."
            )

        rest_ids = entry.get("restOperationIds")
        if not isinstance(rest_ids, list) or not rest_ids:
            raise ContractBundleError(
                f"Operation {semantic_id!r} has no REST operation ids."
            )
        for rest_id in rest_ids:
            if not isinstance(rest_id, str) or rest_id not in openapi_operations:
                raise ContractBundleError(
                    f"Operation {semantic_id!r} references missing REST operation {rest_id!r}."
                )
            catalog_rest_ids.add(rest_id)

    return len(catalog_operations), len(openapi_operations), len(definitions)


def _digest(raw: bytes) -> str:
    return "sha256:" + sha256(raw).hexdigest()


def load_contract_bundle() -> ContractBundle:
    """Load and fail-closed validate the canonical contract resources."""

    raw = {kind: _read_contract_resource(kind) for kind in _RESOURCE_FILES}
    catalog = _parse_document("catalog", raw["catalog"])
    schema = _parse_document("schema", raw["schema"])
    openapi = _parse_document("OpenAPI", raw["openapi"])
    operation_count, rest_operation_count, schema_count = _validate_bundle(
        catalog,
        schema,
        openapi,
    )
    summary = ContractBundleSummary(
        contract_version=CONTRACT_VERSION,
        openapi_version="3.1.0",
        operation_count=operation_count,
        rest_operation_count=rest_operation_count,
        schema_count=schema_count,
        catalog_sha256=_digest(raw["catalog"]),
        schema_sha256=_digest(raw["schema"]),
        openapi_sha256=_digest(raw["openapi"]),
    )
    return ContractBundle(
        catalog=catalog,
        schema=schema,
        openapi=openapi,
        summary=summary,
    )
