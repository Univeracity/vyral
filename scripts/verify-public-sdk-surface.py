#!/usr/bin/env python3
from __future__ import annotations

import argparse
import ast
import json
import re
import tomllib
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
OPENAPI_PATH = ROOT / "src/Vyral.Server/contracts/vyral.openapi.json"
CATALOG_PATH = ROOT / "contracts/public-sdk-surface.json"
PYTHON_CLIENT_PATH = ROOT / "clients/python/src/vyral_client/client.py"
PYTHON_TEST_PATH = ROOT / "clients/python/tests/test_client.py"
PYTHON_README_PATH = ROOT / "clients/python/README.md"
JAVASCRIPT_CLIENT_PATH = ROOT / "clients/javascript/src/index.js"
JAVASCRIPT_TEST_PATH = ROOT / "clients/javascript/test/index.test.js"
JAVASCRIPT_README_PATH = ROOT / "clients/javascript/README.md"
PYTHON_PROJECT_PATH = ROOT / "clients/python/pyproject.toml"
JAVASCRIPT_PACKAGE_PATH = ROOT / "clients/javascript/package.json"

HTTP_METHODS = {"get", "post", "put", "patch", "delete"}
STABILITIES = {"public", "preview", "internal", "deprecated"}
AUTHORIZATION_CLASSES = {
    "anonymous",
    "server-authenticated",
    "canonical-policy",
    "execution-policy",
    "execution-maintenance",
    "execution-worker",
    "provider-policy",
}

# These pairs are one semantic SDK operation with a safer/body-capable route and
# a compatibility route. The first operation id is authoritative for the SDK.
ALIASES: dict[str, list[str]] = {
    "readCanonicalDocument": ["getCanonicalDocument"],
    "queryCanonicalDocumentRevisions": ["listCanonicalDocumentRevisions"],
    "exportCollectionBounded": ["exportCollection"],
}

EXCLUDED_OPERATIONS = {
    "getServerInfo": "Human-readable server banner; health and contract discovery are the supported SDK operations.",
}

# Overrides are reviewed language ergonomics. Unlisted operation ids use their
# OpenAPI operation id in JavaScript and its snake_case form in Python.
JAVASCRIPT_METHOD_OVERRIDES = {
    "preflightCanonicalStore": "getCanonicalPreflight",
    "probeCanonicalStoreDataPlane": "probeCanonicalDataPlane",
    "readCanonicalDocument": "getCanonicalDocument",
    "queryCanonicalDocumentRevisions": "listCanonicalDocumentRevisions",
    "getHealth": "health",
    "getReadiness": "readiness",
    "generateEmbeddings": "embedTexts",
    "runProviderCapability": "runProvider",
    "startProviderRunJob": "startProviderJob",
    "listProviderRunJobs": "listProviderJobs",
    "getProviderRunJob": "getProviderJob",
    "cancelProviderRunJob": "cancelProviderJob",
    "getOpenApiContract": "openApiContract",
    "getCollection": "getCollectionPolicy",
    "exportCollectionBounded": "exportCollection",
    "inspectGraphCollection": "inspectGraph",
    "ingestRagTextBatch": "ingestRagTexts",
    "startRagIngestionTextJob": "startRagTextIngestionJob",
    "startRagIngestionBatchJob": "startRagTextBatchIngestionJob",
    "doctorGraphCollection": "doctorGraph",
}
PYTHON_METHOD_OVERRIDES = {
    "getOpenApiContract": "openapi_contract",
}

MCP_RESOURCES = {"getHealth", "getReadiness", "getOpenApiContract", "getPublicSchemaContract"}
MCP_DEFAULT_TOOLS = {
    "listGraphProviderShapes",
    "getGraphProviderShape",
    "listCollections",
    "getCollection",
    "inspectCollection",
    "getRecord",
    "queryRecords",
    "searchRecords",
    "listRetrievalProfiles",
    "retrieve",
    "buildRagContext",
    "buildRagPrompt",
    "inspectGraphCollection",
    "doctorGraphCollection",
    "traverseGraph",
    "getExecutionRun",
    "getExecutionRunHistory",
}
MCP_TASK_OPERATIONS = {
    "startExecutionRun",
    "startEmbeddingJob",
    "startProviderRunJob",
    "startCollectionImportJob",
    "startRecordBatchUpsertJob",
    "startRagIngestionTextJob",
    "startRagIngestionBatchJob",
    "startRetrievalEvaluationComparisonJob",
    "startRetrievalEvaluationJob",
    "startGraphImportJob",
    "startGraphInspectionJob",
    "startGraphDoctorJob",
}

READ_ONLY_POSTS = {
    "readCanonicalDocument",
    "queryCanonicalDocuments",
    "queryCanonicalDocumentRevisions",
    "queryCanonicalOutbox",
    "getExternalExecutionLeaseCheckpoint",
    "exportCollectionBounded",
    "exportGraphEnvelope",
    "traverseGraph",
    "inspectGraphCollection",
    "preflightGraphImport",
    "doctorGraphCollection",
    "queryRecords",
    "searchRecords",
    "retrieve",
    "evaluateRetrieval",
    "compareRetrievalEvaluations",
    "buildRagContext",
    "evaluateRagContext",
    "buildRagPrompt",
    "exportTraces",
    "generateEmbeddings",
}

REQUIRED_IDEMPOTENCY = {"commitCanonicalTransaction"}
SUPPORTED_IDEMPOTENCY = {
    "ingestRecordArtifact",
    "createCollection",
    "deleteCollection",
    "importCollection",
    "importGraphEnvelope",
    "upsertRecords",
    "ingestRagText",
    "ingestRagTextBatch",
    "runProviderCapability",
    "startExecutionRun",
    "startEmbeddingJob",
    "startProviderRunJob",
    "startCollectionImportJob",
    "startRecordBatchUpsertJob",
    "startRagIngestionTextJob",
    "startRagIngestionBatchJob",
    "startRetrievalEvaluationComparisonJob",
    "startRetrievalEvaluationJob",
    "startGraphImportJob",
    "startGraphInspectionJob",
    "startGraphDoctorJob",
}

RECEIPT_BOUND_OPERATIONS = {
    "ingestRecordArtifact",
    "createCollection",
    "deleteCollection",
    "importCollection",
    "importGraphEnvelope",
    "upsertRecords",
    "ingestRagText",
    "ingestRagTextBatch",
    "runProviderCapability",
    *MCP_TASK_OPERATIONS,
}
MAINTENANCE_OPERATIONS = {
    "probeCanonicalStoreDataPlane",
    "pruneExecutionRuntimeMaintenance",
    "reconcileExecutionRuntimeDispatch",
    "qualifyProvider",
    "pruneTraces",
}
RECEIPT_RESOURCE_SCHEMAS = {
    "ExecutionRun",
    "EmbeddingJob",
    "ProviderRunJob",
    "RecordImportJob",
    "RagIngestionJob",
    "RetrievalEvaluationJob",
    "GraphJob",
}


def camel_to_snake(value: str) -> str:
    return re.sub(r"(?<!^)([A-Z])", r"_\1", value).lower()


def openapi_operations(document: dict[str, Any]) -> list[dict[str, Any]]:
    operations: list[dict[str, Any]] = []
    for path, path_item in document["paths"].items():
        for method, operation in path_item.items():
            if method not in HTTP_METHODS:
                continue
            operation_id = operation.get("operationId")
            if not isinstance(operation_id, str) or not operation_id:
                raise SystemExit(f"{method.upper()} {path} is missing operationId")
            operations.append({
                "operationId": operation_id,
                "method": method.upper(),
                "path": path,
                "operation": operation,
            })
    ids = [item["operationId"] for item in operations]
    duplicates = sorted({operation_id for operation_id in ids if ids.count(operation_id) > 1})
    if duplicates:
        raise SystemExit("Duplicate OpenAPI operation ids: " + ", ".join(duplicates))
    return operations


def operation_group(path: str) -> str:
    if path.startswith("/canonical/"):
        return "canonical"
    if path == "/ingest/record-artifact":
        return "artifacts"
    if path.startswith("/execution/"):
        return "execution"
    if path.startswith("/embedding"):
        return "embeddings"
    if path.startswith("/provider"):
        return "providers"
    if "/graph/" in path or path.startswith("/graph/"):
        return "graph"
    if "/rag/" in path or path.startswith("/rag/"):
        return "rag"
    if path.startswith("/retrieval/") or path == "/search":
        return "retrieval"
    if path.startswith("/objects/"):
        return "objects"
    if path.startswith("/traces"):
        return "traces"
    if "/records" in path or path.endswith("/query") or path.endswith("/search"):
        return "records"
    if path.startswith("/collections") or path.startswith("/record-import"):
        return "collections"
    return "server"


def authorization_class(operation_id: str, path: str, operation: dict[str, Any]) -> str:
    if operation.get("security") == []:
        return "anonymous"
    if path.startswith("/canonical/"):
        return "canonical-policy"
    if path.startswith("/execution/workers/"):
        return "execution-worker"
    if path.startswith("/execution/runtime/maintenance"):
        return "execution-maintenance"
    if (
        path.startswith("/execution/")
        or path.startswith("/record-import/")
        or operation_id in {"importCollection", "upsertRecords"}
    ):
        return "execution-policy"
    if path.startswith("/provider"):
        return "provider-policy"
    return "server-authenticated"


def mutation_class(operation_id: str, method: str) -> str:
    if method == "GET" or operation_id in READ_ONLY_POSTS:
        return "read"
    if method == "DELETE":
        return "delete"
    return "write"


def idempotency_class(operation_id: str, mutation: str) -> str:
    if mutation == "read":
        return "not-applicable"
    if operation_id in REQUIRED_IDEMPOTENCY:
        return "required"
    if operation_id in SUPPORTED_IDEMPOTENCY:
        return "supported"
    return "unsupported"


def completion_boundary(operation_id: str, mutation: str) -> str:
    if mutation == "read":
        return "non-mutating"
    if operation_id in RECEIPT_BOUND_OPERATIONS:
        return "receipt-bound"
    if operation_id in MAINTENANCE_OPERATIONS:
        return "maintenance"
    return "atomic"


def collect_schema_refs(value: Any) -> set[str]:
    refs: set[str] = set()
    if isinstance(value, dict):
        for key, child in value.items():
            if key == "$ref" and isinstance(child, str):
                refs.add(child)
            else:
                refs.update(collect_schema_refs(child))
    elif isinstance(value, list):
        for child in value:
            refs.update(collect_schema_refs(child))
    return refs


def mcp_mapping(operation_id: str) -> dict[str, Any]:
    if operation_id in MCP_RESOURCES:
        return {
            "exposure": "resource",
            "id": f"vyral://{camel_to_snake(operation_id.removeprefix('get'))}/v1",
            "defaultEnabled": True,
        }
    if operation_id in MCP_DEFAULT_TOOLS:
        return {
            "exposure": "tool",
            "id": f"vyral_{camel_to_snake(operation_id)}_v1",
            "defaultEnabled": True,
        }
    if operation_id in MCP_TASK_OPERATIONS:
        return {
            "exposure": "task",
            "id": f"vyral_{camel_to_snake(operation_id)}_v1",
            "defaultEnabled": False,
        }
    return {"exposure": "none", "id": None, "defaultEnabled": False}


def render_catalog(openapi: dict[str, Any]) -> str:
    discovered = openapi_operations(openapi)
    by_id = {item["operationId"]: item for item in discovered}
    aliases = {alias for values in ALIASES.values() for alias in values}
    missing_configuration = sorted(
        (aliases | set(EXCLUDED_OPERATIONS) | set(ALIASES)) - set(by_id)
    )
    if missing_configuration:
        raise SystemExit("Catalog configuration names missing OpenAPI operations: " + ", ".join(missing_configuration))

    entries: list[dict[str, Any]] = []
    for item in discovered:
        operation_id = item["operationId"]
        if operation_id in EXCLUDED_OPERATIONS or operation_id in aliases:
            continue
        rest_ids = [operation_id, *ALIASES.get(operation_id, [])]
        javascript_method = JAVASCRIPT_METHOD_OVERRIDES.get(operation_id, operation_id)
        mutation = mutation_class(operation_id, item["method"])
        entries.append({
            "id": operation_id,
            "group": operation_group(item["path"]),
            "restOperationIds": rest_ids,
            "stability": "public",
            "authorizationClass": authorization_class(operation_id, item["path"], item["operation"]),
            "mutation": mutation,
            "completionBoundary": completion_boundary(operation_id, mutation),
            "idempotency": idempotency_class(operation_id, mutation),
            "sdk": {
                "python": PYTHON_METHOD_OVERRIDES.get(operation_id, camel_to_snake(javascript_method)),
                "javascript": javascript_method,
            },
            "contractRefs": [f"openapi:{rest_id}" for rest_id in rest_ids],
            "schemaRefs": sorted(collect_schema_refs(item["operation"])),
            "mcp": mcp_mapping(operation_id),
            "headerSafeFields": [],
            "evidenceProfile": "public-sdk",
        })

    catalog = {
        "$schema": "./public-sdk-surface.schema.json",
        "catalogVersion": openapi["info"]["version"],
        "openApi": "../src/Vyral.Server/contracts/vyral.openapi.json",
        "openApiVersion": openapi["openapi"],
        "evidenceProfiles": {
            "public-sdk": {
                "server": "tests/Vyral.Tests.Local/ServerWorkflowTests.cs",
                "pythonWire": "clients/python/tests/test_client.py",
                "pythonDocs": "clients/python/README.md",
                "javascriptWire": "clients/javascript/test/index.test.js",
                "javascriptDocs": "clients/javascript/README.md",
            }
        },
        "excludedRestOperations": [
            {"operationId": operation_id, "reason": reason}
            for operation_id, reason in sorted(EXCLUDED_OPERATIONS.items())
        ],
        "operations": entries,
    }
    return json.dumps(catalog, indent=2, ensure_ascii=False) + "\n"


def public_python_methods() -> set[str]:
    tree = ast.parse(PYTHON_CLIENT_PATH.read_text(encoding="utf-8"))
    client = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "VyralClient")
    return {
        node.name
        for node in client.body
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and not node.name.startswith("_")
    }


def javascript_method_blocks() -> dict[str, str]:
    source = JAVASCRIPT_CLIENT_PATH.read_text(encoding="utf-8")
    client_source = source[source.index("export class VyralClient {"):]
    headers = list(re.finditer(r"^  (?:async )?(?:\*\s*)?([A-Za-z][A-Za-z0-9]*)\(", client_source, re.MULTILINE))
    return {
        header.group(1): client_source[header.start():headers[index + 1].start() if index + 1 < len(headers) else len(client_source)]
        for index, header in enumerate(headers)
        if header.group(1) != "constructor"
    }


def public_javascript_methods() -> set[str]:
    return set(javascript_method_blocks())


def transitively_tested_python_methods(test_source: str) -> set[str]:
    tree = ast.parse(PYTHON_CLIENT_PATH.read_text(encoding="utf-8"))
    client = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "VyralClient")
    methods = {
        node.name: node
        for node in client.body
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and not node.name.startswith("_")
    }
    graph: dict[str, set[str]] = {}
    for name, node in methods.items():
        graph[name] = {
            call.func.attr
            for call in ast.walk(node)
            if isinstance(call, ast.Call)
            and isinstance(call.func, ast.Attribute)
            and isinstance(call.func.value, ast.Name)
            and call.func.value.id == "self"
            and call.func.attr in methods
        }
    covered = {name for name in methods if f".{name}(" in test_source}
    pending = list(covered)
    while pending:
        for dependency in graph[pending.pop()]:
            if dependency not in covered:
                covered.add(dependency)
                pending.append(dependency)
    return covered


def transitively_tested_javascript_methods(test_source: str) -> set[str]:
    blocks = javascript_method_blocks()
    graph = {
        name: {
            dependency
            for dependency in blocks
            if re.search(rf"\bthis\.{re.escape(dependency)}\(", block)
        }
        for name, block in blocks.items()
    }
    covered = {name for name in blocks if f".{name}(" in test_source}
    pending = list(covered)
    while pending:
        for dependency in graph[pending.pop()]:
            if dependency not in covered:
                covered.add(dependency)
                pending.append(dependency)
    return covered


def verify_catalog(catalog: dict[str, Any], openapi: dict[str, Any], expected_text: str) -> None:
    actual_text = CATALOG_PATH.read_text(encoding="utf-8") if CATALOG_PATH.exists() else ""
    if actual_text != expected_text:
        raise SystemExit("Public SDK catalog is stale; run scripts/verify-public-sdk-surface.py --write")

    discovered = openapi_operations(openapi)
    openapi_ids = {item["operationId"] for item in discovered}
    classified_ids: list[str] = []
    python_methods = public_python_methods()
    javascript_methods = public_javascript_methods()
    evidence_cache = {
        "pythonWire": PYTHON_TEST_PATH.read_text(encoding="utf-8"),
        "pythonDocs": PYTHON_README_PATH.read_text(encoding="utf-8"),
        "javascriptWire": JAVASCRIPT_TEST_PATH.read_text(encoding="utf-8"),
        "javascriptDocs": JAVASCRIPT_README_PATH.read_text(encoding="utf-8"),
    }
    tested_python_methods = transitively_tested_python_methods(evidence_cache["pythonWire"])
    tested_javascript_methods = transitively_tested_javascript_methods(evidence_cache["javascriptWire"])
    errors: list[str] = []
    python_version = tomllib.loads(PYTHON_PROJECT_PATH.read_text(encoding="utf-8"))["project"]["version"]
    javascript_version = json.loads(JAVASCRIPT_PACKAGE_PATH.read_text(encoding="utf-8"))["version"]
    contract_versions = {
        "catalog": catalog.get("catalogVersion"),
        "openapi": openapi.get("info", {}).get("version"),
        "python": python_version,
        "javascript": javascript_version,
    }
    if len(set(contract_versions.values())) != 1:
        errors.append(
            "Public contract versions differ: "
            + ", ".join(f"{name}={version}" for name, version in contract_versions.items())
        )

    for entry in catalog.get("operations", []):
        operation_id = entry.get("id", "<missing>")
        rest_ids = entry.get("restOperationIds", [])
        classified_ids.extend(rest_ids)
        if entry.get("stability") not in STABILITIES:
            errors.append(f"{operation_id}: invalid stability")
        if entry.get("authorizationClass") not in AUTHORIZATION_CLASSES:
            errors.append(f"{operation_id}: invalid authorizationClass")
        if entry.get("mutation") not in {"read", "write", "delete"}:
            errors.append(f"{operation_id}: invalid mutation")
        if entry.get("completionBoundary") not in {
            "non-mutating", "atomic", "receipt-bound", "maintenance"
        }:
            errors.append(f"{operation_id}: invalid completionBoundary")
        if entry.get("idempotency") not in {"not-applicable", "required", "supported", "unsupported"}:
            errors.append(f"{operation_id}: invalid idempotency")
        if not entry.get("contractRefs"):
            errors.append(f"{operation_id}: missing contractRefs")
        if entry.get("mcp", {}).get("exposure") not in {"none", "tool", "resource", "task"}:
            errors.append(f"{operation_id}: invalid MCP exposure")
        python_method = entry.get("sdk", {}).get("python")
        javascript_method = entry.get("sdk", {}).get("javascript")
        if python_method not in python_methods:
            errors.append(f"{operation_id}: Python method {python_method!r} is missing")
        if javascript_method not in javascript_methods:
            errors.append(f"{operation_id}: JavaScript method {javascript_method!r} is missing")
        if python_method and python_method not in tested_python_methods:
            errors.append(f"{operation_id}: Python wire test does not call {python_method}")
        if javascript_method and javascript_method not in tested_javascript_methods:
            errors.append(f"{operation_id}: JavaScript wire test does not call {javascript_method}")
        if python_method and python_method not in evidence_cache["pythonDocs"]:
            errors.append(f"{operation_id}: Python docs do not mention {python_method}")
        if javascript_method and javascript_method not in evidence_cache["javascriptDocs"]:
            errors.append(f"{operation_id}: JavaScript docs do not mention {javascript_method}")

        operation = next(
            item["operation"] for item in discovered
            if item["operationId"] == operation_id
        )
        if entry.get("completionBoundary") == "receipt-bound":
            accepted = operation.get("responses", {}).get("202", {})
            accepted_schema = (
                accepted.get("content", {})
                .get("application/json", {})
                .get("schema", {})
                .get("$ref", "")
                .rsplit("/", 1)[-1]
            )
            if accepted_schema not in RECEIPT_RESOURCE_SCHEMAS:
                errors.append(f"{operation_id}: 202 response is not a receipt-bearing resource")
            if (
                accepted.get("headers", {})
                .get("Location", {})
                .get("$ref")
                != "#/components/headers/AdmissionLocation"
            ):
                errors.append(f"{operation_id}: 202 response is missing AdmissionLocation")
            if entry.get("idempotency") not in {"supported", "required"}:
                errors.append(f"{operation_id}: receipt-bound operation lacks idempotency")

    excluded_ids = [item.get("operationId") for item in catalog.get("excludedRestOperations", [])]
    classified_ids.extend(excluded_ids)
    duplicates = sorted({operation_id for operation_id in classified_ids if classified_ids.count(operation_id) > 1})
    if duplicates:
        errors.append("OpenAPI operation ids classified more than once: " + ", ".join(duplicates))
    missing = sorted(openapi_ids - set(classified_ids))
    extra = sorted(set(classified_ids) - openapi_ids)
    if missing:
        errors.append("Unclassified OpenAPI operation ids: " + ", ".join(missing))
    if extra:
        errors.append("Catalog operation ids absent from OpenAPI: " + ", ".join(extra))
    if errors:
        raise SystemExit("Public SDK surface verification failed:\n- " + "\n- ".join(errors))

    print(
        "public-sdk-surface=ok "
        f"semantic-operations={len(catalog['operations'])} rest-operations={len(openapi_ids)} "
        f"excluded={len(excluded_ids)}"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate and verify the public SDK operation catalog.")
    parser.add_argument("--write", action="store_true", help="Write the deterministic catalog before verifying it.")
    args = parser.parse_args()

    openapi = json.loads(OPENAPI_PATH.read_text(encoding="utf-8"))
    expected_text = render_catalog(openapi)
    if args.write:
        CATALOG_PATH.parent.mkdir(parents=True, exist_ok=True)
        CATALOG_PATH.write_text(expected_text, encoding="utf-8")
    catalog = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    verify_catalog(catalog, openapi, expected_text)


if __name__ == "__main__":
    main()
