#!/usr/bin/env python3
from __future__ import annotations

import argparse
import ast
import json
import re
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
OPENAPI_PATH = ROOT / "src/Vyral.Server/contracts/vyral.openapi.json"
CATALOG_PATH = ROOT / "contracts/public-sdk-surface.json"
PYTHON_CLIENT_PATH = ROOT / "clients/python/src/vyral_client/client.py"
PYTHON_CONTRACTS_PATH = ROOT / "clients/python/src/vyral_client/contracts.py"
PYTHON_STUB_PATH = ROOT / "clients/python/src/vyral_client/client.pyi"
PYTHON_INIT_PATH = ROOT / "clients/python/src/vyral_client/__init__.py"
PYTHON_INIT_STUB_PATH = ROOT / "clients/python/src/vyral_client/__init__.pyi"
JAVASCRIPT_CLIENT_PATH = ROOT / "clients/javascript/src/index.js"
JAVASCRIPT_DECLARATION_PATH = ROOT / "clients/javascript/src/index.d.ts"


def ref_name(reference: str) -> str:
    return reference.rsplit("/", 1)[-1]


def nullable(schema: dict[str, Any], rendered: str, null_value: str) -> str:
    is_nullable = schema.get("nullable") is True
    schema_type = schema.get("type")
    if isinstance(schema_type, list) and "null" in schema_type:
        is_nullable = True
    return f"{rendered} | {null_value}" if is_nullable else rendered


def python_type(schema: Any) -> str:
    if not isinstance(schema, dict) or not schema:
        return "JSONValue"
    if "$ref" in schema:
        return nullable(schema, ref_name(schema["$ref"]), "None")
    for keyword in ("oneOf", "anyOf"):
        if keyword in schema:
            variants = " | ".join(dict.fromkeys(python_type(item) for item in schema[keyword])) or "JSONValue"
            return nullable(schema, variants, "None")
    if "allOf" in schema:
        references = [item for item in schema["allOf"] if isinstance(item, dict) and "$ref" in item]
        if len(references) == 1:
            return nullable(schema, ref_name(references[0]["$ref"]), "None")
        return nullable(schema, "dict[str, JSONValue]", "None")
    enum = schema.get("enum")
    if isinstance(enum, list) and enum:
        values = ", ".join(repr(value) for value in enum)
        return nullable(schema, f"Literal[{values}]", "None")
    schema_type = schema.get("type")
    if isinstance(schema_type, list):
        non_null = [item for item in schema_type if item != "null"]
        if len(non_null) == 1:
            return python_type({**schema, "type": non_null[0], "nullable": "null" in schema_type})
    if schema_type == "string":
        return nullable(schema, "str", "None")
    if schema_type == "null":
        return "None"
    if schema_type == "integer":
        return nullable(schema, "int", "None")
    if schema_type == "number":
        return nullable(schema, "float", "None")
    if schema_type == "boolean":
        return nullable(schema, "bool", "None")
    if schema_type == "array":
        return nullable(schema, f"list[{python_type(schema.get('items', {}))}]", "None")
    if schema_type == "object" or "properties" in schema:
        additional = schema.get("additionalProperties")
        value_type = python_type(additional) if isinstance(additional, dict) else "JSONValue"
        return nullable(schema, f"dict[str, {value_type}]", "None")
    return nullable(schema, "JSONValue", "None")


def typescript_type(schema: Any) -> str:
    if not isinstance(schema, dict) or not schema:
        return "JsonValue"
    if "$ref" in schema:
        return nullable(schema, ref_name(schema["$ref"]), "null")
    for keyword in ("oneOf", "anyOf"):
        if keyword in schema:
            variants = " | ".join(dict.fromkeys(typescript_type(item) for item in schema[keyword])) or "JsonValue"
            return nullable(schema, variants, "null")
    if "allOf" in schema:
        variants = " & ".join(dict.fromkeys(typescript_type(item) for item in schema["allOf"])) or "JsonObject"
        return nullable(schema, variants, "null")
    enum = schema.get("enum")
    if isinstance(enum, list) and enum:
        values = " | ".join(json.dumps(value) for value in enum)
        return nullable(schema, values, "null")
    schema_type = schema.get("type")
    if isinstance(schema_type, list):
        variants = [typescript_type({**schema, "type": item, "nullable": False}) for item in schema_type]
        return " | ".join(dict.fromkeys(variants))
    if schema_type == "null":
        return "null"
    if schema_type == "string":
        return nullable(schema, "string", "null")
    if schema_type in {"integer", "number"}:
        return nullable(schema, "number", "null")
    if schema_type == "boolean":
        return nullable(schema, "boolean", "null")
    if schema_type == "array":
        return nullable(schema, f"Array<{typescript_type(schema.get('items', {}))}>", "null")
    if schema_type == "object" or "properties" in schema:
        return nullable(schema, "JsonObject", "null")
    return nullable(schema, "JsonValue", "null")


def render_python_contracts(openapi: dict[str, Any]) -> str:
    lines = [
        "# Generated by scripts/generate-sdk-types.py. Do not edit by hand.",
        "from __future__ import annotations",
        "",
        "from typing import Literal, TypeAlias, TypedDict",
        "",
        "JSONPrimitive: TypeAlias = str | int | float | bool | None",
        "JSONValue: TypeAlias = JSONPrimitive | list[\"JSONValue\"] | dict[str, \"JSONValue\"]",
        "JsonObject: TypeAlias = dict[str, JSONValue]",
        "",
    ]
    for name, schema in openapi.get("components", {}).get("schemas", {}).items():
        schema_for_type = dict(schema) if isinstance(schema, dict) else schema
        if isinstance(schema_for_type, dict) and str(schema_for_type.get("$ref", "")).endswith(f"#/$defs/{name}"):
            schema_for_type.pop("$ref")
        properties = schema_for_type.get("properties") if isinstance(schema_for_type, dict) else None
        if isinstance(properties, dict):
            description = str(schema_for_type.get("description") or "").replace('"""', "\\\"\\\"\\\"")
            required = set(schema_for_type.get("required") or [])
            if required:
                lines.append(f"class _{name}Required(TypedDict):")
                for property_name, property_schema in properties.items():
                    if property_name in required:
                        lines.append(f"    {property_name}: {python_type(property_schema)}")
                lines.append("")
                lines.append(f"class {name}(_{name}Required, total=False):")
            else:
                lines.append(f"class {name}(TypedDict, total=False):")
            if description:
                lines.append(f'    """{description}"""')
            optional_properties = [item for item in properties.items() if item[0] not in required]
            if not optional_properties and not description:
                lines.append("    pass")
            for property_name, property_schema in optional_properties:
                lines.append(f"    {property_name}: {python_type(property_schema)}")
            lines.append("")
        else:
            lines.append(f"{name}: TypeAlias = {python_type(schema_for_type)}")
            lines.append("")
    return "\n".join(lines).rstrip() + "\n"


def render_typescript_contracts(openapi: dict[str, Any]) -> list[str]:
    lines = [
        "export type JsonPrimitive = string | number | boolean | null;",
        "export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };",
        "export type JsonObject = { [key: string]: JsonValue };",
        "",
    ]
    for name, schema in openapi.get("components", {}).get("schemas", {}).items():
        schema_for_type = dict(schema) if isinstance(schema, dict) else schema
        if isinstance(schema_for_type, dict) and str(schema_for_type.get("$ref", "")).endswith(f"#/$defs/{name}"):
            schema_for_type.pop("$ref")
        properties = schema_for_type.get("properties") if isinstance(schema_for_type, dict) else None
        if isinstance(properties, dict):
            required = set(schema_for_type.get("required") or [])
            lines.append(f"export interface {name} {{")
            for property_name, property_schema in properties.items():
                marker = "" if property_name in required else "?"
                lines.append(f"  {property_name}{marker}: {typescript_type(property_schema)};")
            if schema_for_type.get("additionalProperties"):
                lines.append("  [key: string]: unknown;")
            lines.append("}")
        else:
            lines.append(f"export type {name} = {typescript_type(schema_for_type)};")
        lines.append("")
    return lines


def success_schema(operation: dict[str, Any]) -> dict[str, Any] | None:
    responses = operation.get("responses", {})
    schemas: list[dict[str, Any]] = []
    for status in ("200", "201", "202", "204"):
        response = responses.get(status)
        if not isinstance(response, dict):
            continue
        content = response.get("content", {})
        for media_type in ("application/json", "application/problem+json", "application/octet-stream"):
            media = content.get(media_type)
            if isinstance(media, dict) and isinstance(media.get("schema"), dict):
                schema = media["schema"]
                if schema not in schemas:
                    schemas.append(schema)
                break
        if status == "204":
            null_schema = {"type": "null"}
            if null_schema not in schemas:
                schemas.append(null_schema)
    if not schemas:
        return None
    return schemas[0] if len(schemas) == 1 else {"oneOf": schemas}


def request_schema(operation: dict[str, Any]) -> dict[str, Any] | None:
    request_body = operation.get("requestBody")
    if not isinstance(request_body, dict):
        return None
    content = request_body.get("content", {})
    for media_type in ("application/json", "multipart/form-data", "application/octet-stream"):
        media = content.get(media_type)
        if isinstance(media, dict) and isinstance(media.get("schema"), dict):
            return media["schema"]
    return None


def operations_by_method(openapi: dict[str, Any], catalog: dict[str, Any], language: str) -> dict[str, dict[str, Any]]:
    operations = {
        operation["operationId"]: operation
        for path_item in openapi["paths"].values()
        for method, operation in path_item.items()
        if method.lower() in {"get", "post", "put", "patch", "delete"}
    }
    return {
        entry["sdk"][language]: operations[entry["restOperationIds"][0]]
        for entry in catalog["operations"]
    }


def ellipsis_function(node: ast.FunctionDef | ast.AsyncFunctionDef) -> ast.FunctionDef | ast.AsyncFunctionDef:
    clone = ast.FunctionDef(
        name=node.name,
        args=node.args,
        body=[ast.Expr(value=ast.Constant(value=Ellipsis))],
        decorator_list=node.decorator_list,
        returns=node.returns,
        type_comment=None,
    ) if isinstance(node, ast.FunctionDef) else ast.AsyncFunctionDef(
        name=node.name,
        args=node.args,
        body=[ast.Expr(value=ast.Constant(value=Ellipsis))],
        decorator_list=node.decorator_list,
        returns=node.returns,
        type_comment=None,
    )
    return ast.fix_missing_locations(clone)


def render_python_stub(openapi: dict[str, Any], catalog: dict[str, Any]) -> str:
    tree = ast.parse(PYTHON_CLIENT_PATH.read_text(encoding="utf-8"))
    method_operations = operations_by_method(openapi, catalog, "python")
    lines = [
        "# Generated by scripts/generate-sdk-types.py. Do not edit by hand.",
        "from __future__ import annotations",
        "",
        "from typing import Any, BinaryIO, Callable, Iterable, Iterator, Mapping, Sequence",
        "from .contracts import *",
        "",
        "def _parse_problem_body(body: str) -> dict[str, Any] | None: ...",
        "def _header_value(headers: object, *names: str) -> str | None: ...",
        "def _retry_delay_seconds(retry_after: str | None, fallback: float) -> float: ...",
        "",
        "class VyralClientError(RuntimeError):",
        "    status: int",
        "    body: str",
        "    problem: dict[str, Any] | None",
        "    failure_class: str | None",
        "    type: str | None",
        "    title: str | None",
        "    detail: str | None",
        "    instance: str | None",
        "    problem_status: int | None",
        "    retry_after: str | None",
        "    correlation_id: str | None",
        "    def __init__(self, status: int, body: str, *, problem: dict[str, Any] | None = ..., failure_class: str | None = ..., retry_after: str | None = ..., correlation_id: str | None = ...) -> None: ...",
        "    @classmethod",
        "    def timeout(cls, detail: str) -> VyralClientError: ...",
        "    @classmethod",
        "    def cancelled(cls, detail: str = ...) -> VyralClientError: ...",
        "    def is_missing_collection(self) -> bool: ...",
        "    def is_auth_error(self) -> bool: ...",
        "    def is_validation_error(self) -> bool: ...",
        "    def is_timeout(self) -> bool: ...",
        "    def is_cancelled(self) -> bool: ...",
        "    def is_transient(self) -> bool: ...",
        "",
    ]

    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and not node.name.startswith("_"):
            lines.extend(ast.unparse(ellipsis_function(node)).splitlines())
            lines.append("")
        elif isinstance(node, (ast.Assign, ast.AnnAssign)):
            target = node.targets[0] if isinstance(node, ast.Assign) and len(node.targets) == 1 else getattr(node, "target", None)
            if (
                isinstance(target, ast.Name)
                and target.id.isupper()
                and not target.id.startswith("_")
            ):
                value = node.value
                annotation = "Any"
                if isinstance(value, ast.Constant) and isinstance(value.value, str):
                    annotation = "str"
                elif isinstance(value, ast.Set):
                    annotation = "set[str]"
                elif isinstance(value, (ast.Tuple, ast.List)):
                    annotation = "tuple[str, ...]"
                elif isinstance(value, ast.Dict):
                    annotation = "dict[str, Any]"
                lines.append(f"{target.id}: {annotation}")
                lines.append("")

    client = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "VyralClient")
    lines.extend([
        "class VyralClient:",
        "    base_url: str",
        "    timeout: float",
        "    api_key: str | None",
        "    bearer_token: str | None",
        "    default_headers: Mapping[str, str] | None",
        "    correlation_id: str | None",
        "    max_retries: int",
        "    retry_backoff_seconds: float",
        "    cancellation_check: Callable[[], bool] | None",
        "    def __init__(self, base_url: str = ..., timeout: float = ..., api_key: str | None = ..., bearer_token: str | None = ..., default_headers: Mapping[str, str] | None = ..., correlation_id: str | None = ..., max_retries: int = ..., retry_backoff_seconds: float = ..., cancellation_check: Callable[[], bool] | None = ...) -> None: ...",
    ])
    for method in client.body:
        if not isinstance(method, (ast.FunctionDef, ast.AsyncFunctionDef)) or method.name.startswith("_"):
            continue
        clone = ellipsis_function(method)
        operation = method_operations.get(method.name)
        if operation is not None:
            response = success_schema(operation)
            if response is not None and method.returns is not None:
                existing = ast.unparse(method.returns)
                response_type = python_type(response)
                if "None" in existing and "None" not in response_type:
                    response_type += " | None"
                clone.returns = ast.parse(response_type, mode="eval").body
            request = request_schema(operation)
            if request is not None:
                for argument in [*clone.args.posonlyargs, *clone.args.args, *clone.args.kwonlyargs]:
                    if argument.arg in {"request", "policy", "record"} and argument.annotation is not None:
                        request_type = python_type(request)
                        if "None" in ast.unparse(argument.annotation) and "None" not in request_type:
                            request_type += " | None"
                        argument.annotation = ast.parse(request_type, mode="eval").body
        rendered = ast.unparse(ast.fix_missing_locations(clone)).splitlines()
        lines.extend(f"    {line}" for line in rendered)
    return "\n".join(lines).rstrip() + "\n"


def render_python_init_stub() -> str:
    tree = ast.parse(PYTHON_INIT_PATH.read_text(encoding="utf-8"))
    exported = next(
        ast.literal_eval(node.value)
        for node in tree.body
        if isinstance(node, ast.Assign)
        and any(isinstance(target, ast.Name) and target.id == "__all__" for target in node.targets)
    )
    lines = ["# Generated by scripts/generate-sdk-types.py. Do not edit by hand."]
    lines.extend(
        f"from .{'async_client' if name == 'AsyncVyralClient' else 'client'} import {name} as {name}"
        for name in exported
    )
    lines.append("")
    return "\n".join(lines)


def split_parameters(parameters: str) -> list[tuple[str, bool]]:
    if not parameters.strip():
        return []
    result: list[tuple[str, bool]] = []
    for raw in parameters.split(","):
        name, separator, _default = raw.strip().partition("=")
        result.append((name.strip(), bool(separator)))
    return result


def typescript_parameter_type(name: str) -> str:
    if name == "artifact":
        return "Blob | ArrayBuffer | ArrayBufferView"
    if name == "content":
        return "BodyInit"
    if name in {"texts", "collections", "startNodeIds"}:
        return "string[]"
    if name in {"records", "items", "messages", "candidates", "cases", "variants", "migrations"}:
        return "JsonObject[]"
    if name == "expectedContentHash":
        return "string | null"
    if name == "plannedResult":
        return "JsonObject | null"
    if name == "leaseToken":
        return "string"
    if name == "options":
        return "VyralRequestOptions & Record<string, unknown>"
    if name in {
        "tenantId", "documentType", "id", "eventId", "collection", "providerId", "runId",
        "artifactRef", "key", "provider", "text", "partitionKey", "traceId", "container",
        "query", "profile", "name", "status", "reviewer", "graphId", "sourceId", "targetId",
        "predicate", "subjectId", "graphNodeId", "capability", "prompt", "briefId", "idempotencyKey",
    }:
        return "string"
    return "JsonObject"


def render_typescript_declarations(openapi: dict[str, Any], catalog: dict[str, Any]) -> str:
    source = JAVASCRIPT_CLIENT_PATH.read_text(encoding="utf-8")
    method_operations = operations_by_method(openapi, catalog, "javascript")
    lines = ["// Generated by scripts/generate-sdk-types.py. Do not edit by hand.", ""]
    lines.extend(render_typescript_contracts(openapi))
    lines.extend([
        "export interface VyralRequestOptions {",
        "  headers?: HeadersInit;",
        "  signal?: AbortSignal;",
        "  timeoutMs?: number;",
        "  correlationId?: string;",
        "  idempotencyKey?: string;",
        "}",
        "",
        "export interface VyralClientOptions extends VyralRequestOptions {",
        "  fetch?: typeof fetch;",
        "  apiKey?: string;",
        "  bearerToken?: string;",
        "  maxRetries?: number;",
        "  retryBackoffMs?: number;",
        "}",
        "",
        "export class VyralClientError extends Error {",
        "  readonly status: number;",
        "  readonly body: string;",
        "  readonly problem: ProblemDetails | null;",
        "  readonly admission: AdmissionReceipt | null;",
        "  readonly failureClass?: string;",
        "  readonly type: string | null;",
        "  readonly title: string | null;",
        "  readonly detail: string | null;",
        "  readonly instance: string | null;",
        "  readonly problemStatus: number | null;",
        "  readonly retryAfter: string | null;",
        "  readonly correlationId: string | null;",
        "  constructor(status: number, body: string, options?: Record<string, unknown>);",
        "  static timeout(detail: string): VyralClientError;",
        "  static cancelled(detail?: string): VyralClientError;",
        "  isMissingCollection(): boolean;",
        "  isAuthError(): boolean;",
        "  isValidationError(): boolean;",
        "  isTimeout(): boolean;",
        "  isCancelled(): boolean;",
        "  isTransient(): boolean;",
        "}",
        "",
        "export const TERMINAL_EXECUTION_RUN_STATUSES: readonly [\"succeeded\", \"failed\", \"cancelled\", \"rejected\", \"timed_out\"];",
        "export const RETRIEVAL_PROFILES: Readonly<Record<string, string>>;",
        "export const EVIDENCE_BRIEF_SCHEMA: \"vyral.evidence-brief.v1\";",
        "export const EVIDENCE_BRIEF_DOCUMENT_TYPE: \"vyral.evidence-brief\";",
        "export const EVIDENCE_BRIEF_CHANGED_EVENT_TOPIC: \"vyral.evidence-brief.changed\";",
        "",
    ])

    for match in re.finditer(r"^export function ([A-Za-z][A-Za-z0-9]*)\(([^)]*)\)", source, re.MULTILINE):
        name, raw_parameters = match.groups()
        parameters = []
        for parameter, optional in split_parameters(raw_parameters):
            marker = "?" if optional else ""
            parameters.append(f"{parameter}{marker}: {typescript_parameter_type(parameter)}")
        result = "boolean" if name.startswith("is") else ("ProviderRunRejectionDiagnostic | null" if name == "getProviderRunRejection" else "JsonObject")
        lines.append(f"export function {name}({', '.join(parameters)}): {result};")
    lines.append("")

    client_source = source[source.index("export class VyralClient {"):]
    headers = list(re.finditer(r"^  (?:async )?(?:\*\s*)?([A-Za-z][A-Za-z0-9]*)\(([^)]*)\) \{", client_source, re.MULTILINE))
    lines.extend([
        "export class VyralClient {",
        "  readonly baseUrl: string;",
        "  constructor(baseUrl?: string, options?: VyralClientOptions);",
    ])
    for header in headers:
        name, raw_parameters = header.groups()
        if name == "constructor":
            continue
        if name == "withOptions":
            lines.append("  withOptions(options?: VyralClientOptions): VyralClient;")
            continue
        parameters = []
        operation = method_operations.get(name)
        body_schema = request_schema(operation) if operation is not None else None
        for parameter, optional in split_parameters(raw_parameters):
            marker = "?" if optional else ""
            parameter_type = typescript_parameter_type(parameter)
            if body_schema is not None and parameter in {"request", "query", "policy", "record"}:
                parameter_type = typescript_type(body_schema)
            if name == "ingestRecordArtifact" and parameter == "manifest":
                parameter_type = "ArtifactRecordIngestManifest"
            parameters.append(f"{parameter}{marker}: {parameter_type}")
        response = success_schema(operation) if operation is not None else None
        result = typescript_type(response) if response is not None else "JsonValue"
        iterator_results = {
            "iterateCanonicalDocuments": "CanonicalDocument",
            "iterateRecords": "VyralRecord",
            "iterateSearchRecords": "VyralRecordMatch",
        }
        collected_results = {
            "queryAllCanonicalDocuments": "CanonicalDocument[]",
            "queryAllRecords": "VyralRecord[]",
            "searchAllRecords": "VyralRecordMatch[]",
        }
        if name in iterator_results:
            lines.append(f"  {name}({', '.join(parameters)}): AsyncIterable<{iterator_results[name]}>;")
        else:
            result = collected_results.get(name, result)
            lines.append(f"  {name}({', '.join(parameters)}): Promise<{result}>;")
    lines.append("}")
    lines.append("")
    return "\n".join(lines)


def write_or_check(path: Path, content: str, write: bool, errors: list[str]) -> None:
    if write:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return
    actual = path.read_text(encoding="utf-8") if path.exists() else ""
    if actual != content:
        errors.append(str(path.relative_to(ROOT)))


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate SDK type artifacts from the public OpenAPI contract.")
    parser.add_argument("--write", action="store_true", help="Write generated files instead of checking them.")
    args = parser.parse_args()
    openapi = json.loads(OPENAPI_PATH.read_text(encoding="utf-8"))
    catalog = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    errors: list[str] = []
    outputs = {
        PYTHON_CONTRACTS_PATH: render_python_contracts(openapi),
        PYTHON_STUB_PATH: render_python_stub(openapi, catalog),
        PYTHON_INIT_STUB_PATH: render_python_init_stub(),
        JAVASCRIPT_DECLARATION_PATH: render_typescript_declarations(openapi, catalog),
    }
    for path, content in outputs.items():
        write_or_check(path, content, args.write, errors)
    if errors:
        raise SystemExit("Generated SDK types are stale; run scripts/generate-sdk-types.py --write:\n- " + "\n- ".join(errors))
    print(f"sdk-types=ok schemas={len(openapi.get('components', {}).get('schemas', {}))} files={len(outputs)}")


if __name__ == "__main__":
    main()
