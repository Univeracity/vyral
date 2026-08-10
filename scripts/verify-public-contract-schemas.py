#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
OPENAPI_PATH = ROOT / "src/Vyral.Server/contracts/vyral.openapi.json"
SCHEMA_PATH = ROOT / "contracts/schemas/vyral-public.schema.json"
SCHEMA_ROUTE = "/contracts/schemas/vyral-public.schema.json"
SCHEMA_REFERENCE = "../../../contracts/schemas/vyral-public.schema.json"
SCHEMA_ID = "https://openvyral.com/schemas/vyral-public.schema.json"


def upgrade_schema(value: Any) -> Any:
    if isinstance(value, list):
        return [upgrade_schema(item) for item in value]
    if not isinstance(value, dict):
        return value

    upgraded = {key: upgrade_schema(child) for key, child in value.items() if key != "nullable"}
    nullable = value.get("nullable") is True
    schema_type = upgraded.get("type")
    if nullable and isinstance(schema_type, str):
        upgraded["type"] = [schema_type, "null"]
    elif nullable and isinstance(schema_type, list) and "null" not in schema_type:
        upgraded["type"] = [*schema_type, "null"]
    elif nullable and "$ref" in upgraded:
        reference = upgraded.pop("$ref")
        existing_any_of = upgraded.pop("anyOf", [])
        upgraded["anyOf"] = [{"$ref": reference}, *existing_any_of, {"type": "null"}]

    for keyword, bound in (("exclusiveMinimum", "minimum"), ("exclusiveMaximum", "maximum")):
        exclusive = upgraded.get(keyword)
        if not isinstance(exclusive, bool):
            continue
        upgraded.pop(keyword)
        if exclusive and bound in upgraded:
            upgraded[keyword] = upgraded.pop(bound)
    return upgraded


def walk(value: Any):
    yield value
    if isinstance(value, dict):
        for child in value.values():
            yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


def schema_bundle(openapi: dict[str, Any]) -> dict[str, Any]:
    definitions: dict[str, Any] = {}
    for name, schema in openapi["components"]["schemas"].items():
        definition = dict(schema)
        definition.pop("$ref", None)

        def rewrite_refs(value: Any) -> Any:
            if isinstance(value, list):
                return [rewrite_refs(item) for item in value]
            if not isinstance(value, dict):
                return value
            rewritten = {key: rewrite_refs(child) for key, child in value.items()}
            reference = rewritten.get("$ref")
            if isinstance(reference, str) and reference.startswith("#/components/schemas/"):
                rewritten["$ref"] = reference.replace("#/components/schemas/", "#/$defs/", 1)
            return rewritten

        definitions[name] = rewrite_refs(definition)
    return {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": SCHEMA_ID,
        "title": "Vyral Public Contract Models",
        "description": "Canonical JSON Schema 2020-12 models shared by OpenAPI, SDKs, and MCP.",
        "version": openapi["info"]["version"],
        "$defs": definitions,
    }


def ensure_schema_route(openapi: dict[str, Any]) -> None:
    openapi["paths"].pop("/schemas/vyral-public.schema.json", None)
    openapi["paths"][SCHEMA_ROUTE] = {
        "get": {
            "operationId": "getPublicSchemaContract",
            "summary": "Get the canonical public JSON Schema contract",
            "security": [],
            "responses": {
                "200": {
                    "description": "Canonical JSON Schema 2020-12 bundle",
                    "content": {
                        "application/schema+json": {
                            "schema": {"type": "object"}
                        },
                        "application/json": {
                            "schema": {"type": "object"}
                        },
                    },
                }
            },
        }
    }


def normalize_openapi(openapi: dict[str, Any]) -> dict[str, Any]:
    upgraded = upgrade_schema(openapi)
    upgraded["openapi"] = "3.1.0"
    upgraded["jsonSchemaDialect"] = "https://json-schema.org/draft/2020-12/schema"
    ensure_schema_route(upgraded)
    for name, schema in upgraded["components"]["schemas"].items():
        schema["$ref"] = f"{SCHEMA_REFERENCE}#/$defs/{name}"
    return upgraded


def main() -> None:
    parser = argparse.ArgumentParser(description="Verify the OpenAPI 3.1 and canonical JSON Schema contract pair.")
    parser.add_argument("--write", action="store_true", help="Upgrade/normalize OpenAPI and write the schema bundle.")
    args = parser.parse_args()

    openapi = json.loads(OPENAPI_PATH.read_text(encoding="utf-8"))
    if args.write:
        openapi = normalize_openapi(openapi)
        OPENAPI_PATH.write_text(json.dumps(openapi, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    errors: list[str] = []
    if openapi.get("openapi") != "3.1.0":
        errors.append("OpenAPI version must be 3.1.0")
    if openapi.get("jsonSchemaDialect") != "https://json-schema.org/draft/2020-12/schema":
        errors.append("OpenAPI jsonSchemaDialect must be JSON Schema 2020-12")
    if SCHEMA_ROUTE not in openapi.get("paths", {}):
        errors.append(f"OpenAPI is missing {SCHEMA_ROUTE}")

    for value in walk(openapi):
        if not isinstance(value, dict):
            continue
        if "nullable" in value:
            errors.append("OpenAPI still contains the 3.0 nullable keyword")
            break
        if isinstance(value.get("exclusiveMinimum"), bool) or isinstance(value.get("exclusiveMaximum"), bool):
            errors.append("OpenAPI still contains boolean exclusive bounds")
            break

    for name, schema in openapi.get("components", {}).get("schemas", {}).items():
        expected_ref = f"{SCHEMA_REFERENCE}#/$defs/{name}"
        if schema.get("$ref") != expected_ref:
            errors.append(f"components.schemas.{name} does not reference the canonical bundle")

    expected_bundle = json.dumps(schema_bundle(openapi), indent=2, ensure_ascii=False) + "\n"
    if args.write:
        SCHEMA_PATH.parent.mkdir(parents=True, exist_ok=True)
        SCHEMA_PATH.write_text(expected_bundle, encoding="utf-8")
    actual_bundle = SCHEMA_PATH.read_text(encoding="utf-8") if SCHEMA_PATH.exists() else ""
    if actual_bundle != expected_bundle:
        errors.append("Canonical schema bundle is stale; run scripts/verify-public-contract-schemas.py --write")

    if errors:
        raise SystemExit("Public contract schema verification failed:\n- " + "\n- ".join(errors))
    print(f"public-contract-schemas=ok definitions={len(openapi['components']['schemas'])} dialect=2020-12")


if __name__ == "__main__":
    main()
