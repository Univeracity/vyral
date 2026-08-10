from __future__ import annotations

import asyncio
import json
from pathlib import Path
import sys
import tempfile
from typing import Any, Mapping
import unittest
from urllib.parse import urlencode

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime import (  # noqa: E402
    ApiKeyAuthorizer,
    RestApplicationConfig,
    RestApiKeyAuthorizer,
    VyralRestApplication,
    VyralRuntime,
)
from vyral_runtime.host.rest_operations import (  # noqa: E402
    RestOperationUnavailableError,
)
from vyral_runtime.host.rest_registry import (  # noqa: E402
    REST_OPERATION_FAMILIES,
    REST_OPERATION_REGISTRY,
    RestOperationFamily,
)
from vyral_runtime.execution import ExecutionHandlerDescriptor  # noqa: E402


async def _request(
    app: VyralRestApplication,
    method: str,
    path: str,
    *,
    body: object | None = None,
    raw_body: bytes | None = None,
    content_type: str | None = None,
    query: Mapping[str, object] | None = None,
    headers: Mapping[str, str] | None = None,
) -> tuple[int, dict[str, str], object | bytes | None]:
    if raw_body is not None:
        encoded = raw_body
    elif body is not None:
        encoded = json.dumps(body, separators=(",", ":")).encode(
            "utf-8"
        )
    else:
        encoded = b""
    selected_headers = {
        "accept": "application/json",
        "content-length": str(len(encoded)),
    }
    if content_type is not None:
        selected_headers["content-type"] = content_type
    elif body is not None:
        selected_headers["content-type"] = "application/json"
    selected_headers.update(
        {name.lower(): value for name, value in (headers or {}).items()}
    )
    sent: list[Mapping[str, Any]] = []
    delivered = False

    async def receive() -> Mapping[str, Any]:
        nonlocal delivered
        if delivered:
            return {"type": "http.disconnect"}
        delivered = True
        return {
            "type": "http.request",
            "body": encoded,
            "more_body": False,
        }

    async def send(value: Mapping[str, Any]) -> None:
        sent.append(value)

    await app(
        {
            "type": "http",
            "http_version": "1.1",
            "method": method,
            "path": path,
            "query_string": urlencode(query or {}).encode("ascii"),
            "headers": [
                (name.encode("ascii"), value.encode("latin-1"))
                for name, value in selected_headers.items()
            ],
        },
        receive,
        send,
    )
    start = next(
        item for item in sent if item["type"] == "http.response.start"
    )
    response_headers = {
        bytes(name).decode("ascii").lower(): bytes(value).decode(
            "latin-1"
        )
        for name, value in start.get("headers", ())
    }
    response_body = b"".join(
        bytes(item.get("body", b""))
        for item in sent
        if item["type"] == "http.response.body"
    )
    if not response_body:
        result: object | bytes | None = None
    elif response_headers.get("content-type", "").startswith(
        (
            "application/json",
            "application/problem+json",
            "application/schema+json",
        )
    ):
        result = json.loads(response_body)
    else:
        result = response_body
    return int(start["status"]), response_headers, result


class _Authorizer:
    def __init__(self) -> None:
        self.calls: list[
            tuple[
                str,
                str,
                Mapping[str, str],
                Mapping[str, str],
                Mapping[str, str],
                object | None,
            ]
        ] = []

    async def authorize(
        self,
        operation_id: str,
        authorization_class: str,
        headers: Mapping[str, str],
        path_parameters: Mapping[str, str],
        query: Mapping[str, str],
        body: object | None,
    ) -> None:
        self.calls.append(
            (
                operation_id,
                authorization_class,
                headers,
                path_parameters,
                query,
                body,
            )
        )


class RestHostTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(
            prefix="vyral-python-rest-"
        )
        self.runtime = VyralRuntime(Path(self.temporary.name))
        self.app = VyralRestApplication(self.runtime)

    async def asyncTearDown(self) -> None:
        await self.app._dispatcher.shutdown()
        self.runtime.close()
        self.temporary.cleanup()

    async def test_routes_exactly_cover_authoritative_openapi(self) -> None:
        expected = {
            operation["operationId"]
            for path in self.runtime.contracts.openapi["paths"].values()
            for method, operation in path.items()
            if method in {"get", "post", "put", "patch", "delete"}
        }
        self.assertEqual(133, len(expected))
        self.assertEqual(expected, set(self.app.operation_ids))

    async def test_operation_registry_has_one_owner_for_every_route(
        self,
    ) -> None:
        expected = set(self.app.operation_ids)

        self.assertEqual(expected, set(REST_OPERATION_REGISTRY))
        self.assertEqual(
            set(RestOperationFamily),
            set(REST_OPERATION_FAMILIES),
        )
        self.assertEqual(
            len(expected),
            sum(
                len(operation_ids)
                for operation_ids in REST_OPERATION_FAMILIES.values()
            ),
        )
        for operation_id, family in REST_OPERATION_REGISTRY.items():
            with self.subTest(operation_id=operation_id):
                self.assertIn(
                    operation_id,
                    REST_OPERATION_FAMILIES[family],
                )

    async def test_every_openapi_operation_reaches_an_implementation(
        self,
    ) -> None:
        for operation_id in self.app.operation_ids:
            with self.subTest(operation_id=operation_id):
                try:
                    await self.app._dispatcher.dispatch(
                        operation_id,
                        {},
                        {},
                        {},
                        None,
                        b"",
                    )
                except RestOperationUnavailableError as error:
                    self.assertNotEqual(
                        operation_id, str(error)
                    )
                    self.assertNotIn(
                        "has no Python implementation", str(error)
                    )
                except Exception:
                    # Missing fixture inputs are expected here. Reaching an
                    # operation-specific validator proves the route is wired.
                    pass

    async def test_external_worker_lease_projects_public_run_receipt(
        self,
    ) -> None:
        handler_id = "python.external.callback"
        self.runtime.execution.register_external_handler(
            ExecutionHandlerDescriptor(
                handler_id=handler_id,
                plugin_id="python.external",
                display_name="Python external callback",
            )
        )
        status, _, accepted = await _request(
            self.app,
            "POST",
            "/execution/runs",
            body={
                "handlerId": handler_id,
                "idempotencyKey": "python-worker-secret",
                "payload": {"callbackId": "cb-python"},
            },
        )
        self.assertEqual(202, status)
        assert isinstance(accepted, dict)

        status, _, lease = await _request(
            self.app,
            "POST",
            "/execution/workers/leases",
            body={
                "workerId": "python-worker",
                "handlerIds": [handler_id],
            },
        )
        self.assertEqual(200, status)
        assert isinstance(lease, dict)
        leased_run = lease["run"]
        assert isinstance(leased_run, dict)
        self.assertEqual(accepted["id"], leased_run["id"])
        self.assertEqual(
            "startExecutionRun",
            leased_run["admission"]["operationId"],
        )
        self.assertNotIn("idempotencyKey", leased_run)

        status, _, heartbeat = await _request(
            self.app,
            "POST",
            "/execution/workers/leases/heartbeat",
            body={
                "leaseKey": lease["leaseKey"],
                "leaseToken": lease["leaseToken"],
                "workerId": lease["workerId"],
                "ttlSeconds": 60,
            },
        )
        self.assertEqual(200, status)
        assert isinstance(heartbeat, dict)
        heartbeat_run = heartbeat["run"]
        assert isinstance(heartbeat_run, dict)
        self.assertEqual(accepted["id"], heartbeat_run["id"])

        status, _, completed = await _request(
            self.app,
            "POST",
            "/execution/workers/leases/complete",
            body={
                "leaseKey": lease["leaseKey"],
                "leaseToken": lease["leaseToken"],
                "workerId": lease["workerId"],
                "result": {
                    "status": "succeeded",
                    "result": {"callbackId": "cb-python"},
                },
            },
        )
        self.assertEqual(200, status)
        assert isinstance(completed, dict)
        self.assertEqual(accepted["id"], completed["id"])
        self.assertEqual(
            accepted["id"], completed["admission"]["resourceId"]
        )
        self.assertNotIn("idempotencyKey", completed)

    async def test_health_contract_and_method_routing(self) -> None:
        status, headers, health = await _request(
            self.app, "GET", "/health"
        )
        self.assertEqual(200, status)
        self.assertEqual("application/json", headers["content-type"])
        assert isinstance(health, dict)
        self.assertEqual("ok", health["status"])
        self.assertEqual("vyral-python", health["service"])
        self.assertEqual(
            "local-token-hash", health["embedding"]["provider"]
        )
        self.assertEqual(
            {
                "recordStore",
                "objectStore",
                "traceStore",
                "canonicalStore",
            },
            set(health["storage"]),
        )

        status, headers, problem = await _request(
            self.app, "POST", "/health"
        )
        self.assertEqual(405, status)
        self.assertEqual("GET", headers["allow"])
        assert isinstance(problem, dict)
        self.assertEqual(405, problem["status"])

    async def test_execution_discovery_matches_public_envelopes(
        self,
    ) -> None:
        status, _, runtime = await _request(
            self.app, "GET", "/execution/runtime"
        )
        self.assertEqual(200, status)
        assert isinstance(runtime, dict)
        self.assertEqual(
            {"status", "plugins", "handlers"}, set(runtime)
        )
        self.assertEqual(
            "python-local-sqlite",
            runtime["status"]["adapter"]["adapterId"],
        )
        self.assertGreaterEqual(len(runtime["plugins"]), 1)
        self.assertGreaterEqual(len(runtime["handlers"]), 10)

        status, _, effective = await _request(
            self.app,
            "GET",
            "/execution/runtime/effective",
            query={
                "productId": "ignored-in-local-mode",
                "tenantId": "ignored-in-local-mode",
            },
        )
        self.assertEqual(200, status)
        assert isinstance(effective, dict)
        self.assertEqual(
            {"status", "scope", "handlers"}, set(effective)
        )
        self.assertEqual(
            {
                "sharedExecution": False,
                "scopeRequired": False,
                "productId": None,
                "tenantId": None,
            },
            effective["scope"],
        )
        self.assertEqual(
            runtime["handlers"], effective["handlers"]
        )

    async def test_record_lifecycle_and_authorization_boundary(
        self,
    ) -> None:
        authorizer = _Authorizer()
        app = VyralRestApplication(
            self.runtime, authorizer=authorizer
        )
        status, _, created = await _request(
            app,
            "POST",
            "/collections",
            body={"name": "documents"},
            headers={"authorization": "Bearer test"},
        )
        self.assertEqual(202, status)
        assert isinstance(created, dict)
        self.assertEqual(
            "createCollection", created["admission"]["operationId"]
        )
        if app._dispatcher._dispatch_tasks:
            await asyncio.gather(*app._dispatcher._dispatch_tasks)
        policy = await self.runtime.async_records.get_collection_policy(
            "documents"
        )
        self.assertIsNotNone(policy)

        record = {
            "id": "record-1",
            "partitionKey": "tenant-a",
            "type": "document",
            "content": {"text": "portable runtime"},
        }
        status, _, stored = await _request(
            app,
            "POST",
            "/collections/documents/records",
            body=record,
        )
        self.assertEqual(201, status)
        self.assertEqual(record["id"], stored["id"])

        status, _, fetched = await _request(
            app,
            "GET",
            "/collections/documents/records/tenant-a/record-1",
        )
        self.assertEqual(200, status)
        self.assertEqual(record["content"], fetched["content"])

        self.assertEqual(
            ["createCollection", "upsertRecord", "getRecord"],
            [call[0] for call in authorizer.calls],
        )
        self.assertTrue(
            all(call[1] != "public" for call in authorizer.calls)
        )
        self.assertEqual(
            "Bearer test",
            authorizer.calls[0][2]["authorization"],
        )

    async def test_collection_lifecycle_returns_durable_admission(
        self,
    ) -> None:
        request = {"name": "durable-collection"}
        status, headers, admitted = await _request(
            self.app,
            "POST",
            "/collections",
            body=request,
            headers={"idempotency-key": "collection-create-1"},
        )
        self.assertEqual(202, status)
        assert isinstance(admitted, dict)
        run_id = admitted["id"]
        self.assertEqual(
            f"/execution/runs/{run_id}", headers["location"]
        )
        self.assertEqual(
            "createCollection",
            admitted["admission"]["operationId"],
        )

        status, _, replay = await _request(
            self.app,
            "POST",
            "/collections",
            body=request,
            headers={"idempotency-key": "collection-create-1"},
        )
        self.assertEqual(202, status)
        assert isinstance(replay, dict)
        self.assertEqual(run_id, replay["id"])
        self.assertTrue(replay["admission"]["replayed"])
        if self.app._dispatcher._dispatch_tasks:
            await asyncio.gather(*self.app._dispatcher._dispatch_tasks)

        status, _, _ = await _request(
            self.app, "GET", "/collections/durable-collection"
        )
        self.assertEqual(200, status)
        status, _, deletion = await _request(
            self.app,
            "DELETE",
            "/collections/durable-collection",
            headers={"idempotency-key": "collection-delete-1"},
        )
        self.assertEqual(202, status)
        assert isinstance(deletion, dict)
        self.assertEqual(
            "deleteCollection",
            deletion["admission"]["operationId"],
        )
        if self.app._dispatcher._dispatch_tasks:
            await asyncio.gather(*self.app._dispatcher._dispatch_tasks)
        status, _, _ = await _request(
            self.app, "GET", "/collections/durable-collection"
        )
        self.assertEqual(404, status)

    async def test_api_key_policy_keeps_anonymous_routes_public(
        self,
    ) -> None:
        policy = ApiKeyAuthorizer("test-secret")
        app = VyralRestApplication(
            self.runtime,
            authorizer=RestApiKeyAuthorizer(policy),
        )
        status, _, _ = await _request(app, "GET", "/health")
        self.assertEqual(200, status)

        status, _, problem = await _request(
            app, "GET", "/collections"
        )
        self.assertEqual(401, status)
        assert isinstance(problem, dict)
        self.assertEqual("Unauthorized", problem["title"])

        status, _, collections = await _request(
            app,
            "GET",
            "/collections",
            headers={"x-vyral-api-key": "test-secret"},
        )
        self.assertEqual(200, status)
        self.assertEqual([], collections)

        status, _, collections = await _request(
            app,
            "GET",
            "/collections",
            headers={"authorization": "Bearer test-secret"},
        )
        self.assertEqual(200, status)
        self.assertEqual([], collections)

        status, _, problem = await _request(
            app,
            "GET",
            "/collections",
            headers={
                "x-vyral-api-key": "test-secret",
                "authorization": "Bearer conflicting-secret",
            },
        )
        self.assertEqual(401, status)
        assert isinstance(problem, dict)
        self.assertEqual("Unauthorized", problem["title"])

    async def test_canonical_transaction_is_available_over_rest(
        self,
    ) -> None:
        transaction = {
            "tenantId": "tenant-a",
            "idempotencyKey": "rest-canonical-1",
            "mutations": [
                {
                    "kind": "upsert",
                    "document": {
                        "tenantId": "tenant-a",
                        "documentType": "probe",
                        "id": "doc-1",
                        "schemaVersion": "v1",
                        "data": {"value": "ready"},
                    },
                }
            ],
        }
        status, _, committed = await _request(
            self.app,
            "POST",
            "/canonical/tenants/tenant-a/transactions",
            body=transaction,
        )
        self.assertEqual(200, status)
        assert isinstance(committed, dict)
        self.assertFalse(committed["replayed"])

        status, _, document = await _request(
            self.app,
            "POST",
            "/canonical/tenants/tenant-a/documents/read",
            body={
                "tenantId": "tenant-a",
                "documentType": "probe",
                "id": "doc-1",
                "includeDeleted": False,
            },
        )
        self.assertEqual(200, status)
        self.assertEqual({"value": "ready"}, document["data"])

    async def test_binary_object_round_trip_supports_nested_keys(
        self,
    ) -> None:
        content = b"portable-object"
        status, _, info = await _request(
            self.app,
            "PUT",
            "/objects/artifacts/nested/result.bin",
            raw_body=content,
            content_type="application/octet-stream",
            headers={"x-vyral-meta-source": "rest-test"},
        )
        self.assertEqual(200, status)
        assert isinstance(info, dict)
        self.assertEqual("nested/result.bin", info["key"])
        self.assertEqual(
            {"source": "rest-test"}, info["metadata"]
        )

        status, headers, loaded = await _request(
            self.app,
            "GET",
            "/objects/artifacts/nested/result.bin",
        )
        self.assertEqual(200, status)
        self.assertEqual(content, loaded)
        self.assertIn("etag", headers)

    async def test_embedding_job_uses_durable_execution(self) -> None:
        request = {
            "texts": ["portable", "runtime"],
            "purpose": "symmetric",
        }
        status, response_headers, job = await _request(
            self.app,
            "POST",
            "/embeddings/jobs",
            body=request,
            headers={"idempotency-key": "embedding-rest-1"},
        )
        self.assertEqual(202, status)
        assert isinstance(job, dict)
        self.assertEqual(2, job["requested"])
        job_id = job["id"]
        self.assertEqual(
            f"/embeddings/jobs/{job_id}",
            response_headers["location"],
        )
        admission = job["admission"]
        self.assertEqual("vyral.admission.v1", admission["version"])
        self.assertEqual("startEmbeddingJob", admission["operationId"])
        self.assertEqual("accepted", admission["status"])
        self.assertEqual(job_id, admission["resourceId"])
        self.assertFalse(admission["replayed"])
        self.assertNotEqual(
            "embedding-rest-1", admission["idempotencyKeyHash"]
        )

        status, _, replay = await _request(
            self.app,
            "POST",
            "/embeddings/jobs",
            body=request,
            headers={"idempotency-key": "embedding-rest-1"},
        )
        self.assertEqual(202, status)
        assert isinstance(replay, dict)
        self.assertEqual(job_id, replay["id"])
        self.assertEqual(
            admission["admissionId"], replay["admission"]["admissionId"]
        )
        self.assertTrue(replay["admission"]["replayed"])

        if self.app._dispatcher._dispatch_tasks:
            await asyncio.gather(
                *self.app._dispatcher._dispatch_tasks
            )
        status, _, completed = await _request(
            self.app,
            "GET",
            f"/embeddings/jobs/{job_id}",
        )
        self.assertEqual(200, status)
        assert isinstance(completed, dict)
        self.assertEqual("succeeded", completed["status"])
        self.assertEqual(2, len(completed["result"]["items"]))

        status, _, without_results = await _request(
            self.app,
            "GET",
            "/embeddings/jobs",
            query={"includeResult": "false"},
        )
        self.assertEqual(200, status)
        assert isinstance(without_results, list)
        self.assertNotIn("result", without_results[0])

        status, _, with_results = await _request(
            self.app,
            "GET",
            "/embeddings/jobs",
            query={"includeResult": "true"},
        )
        self.assertEqual(200, status)
        assert isinstance(with_results, list)
        self.assertIn("result", with_results[0])

    async def test_execution_rejection_is_not_reported_as_accepted(
        self,
    ) -> None:
        status, headers, problem = await _request(
            self.app,
            "POST",
            "/execution/runs",
            body={"handlerId": "missing.handler", "payload": {}},
            headers={"idempotency-key": "missing-handler-1"},
        )

        self.assertEqual(400, status)
        self.assertNotIn("location", headers)
        self.assertEqual("application/problem+json", headers["content-type"])
        assert isinstance(problem, dict)
        self.assertEqual("Admission rejected", problem["title"])
        self.assertEqual("rejected", problem["admission"]["status"])
        self.assertEqual(
            "handler_missing", problem["admission"]["failureClass"]
        )

    async def test_aggregate_record_write_returns_durable_admission(
        self,
    ) -> None:
        await _request(
            self.app,
            "POST",
            "/collections",
            body={"name": "aggregate-records"},
        )
        if self.app._dispatcher._dispatch_tasks:
            await asyncio.gather(*self.app._dispatcher._dispatch_tasks)
        request = {
            "records": [
                {
                    "id": "record-1",
                    "partitionKey": "tenant-a",
                    "type": "test.record",
                }
            ]
        }
        status, headers, admitted = await _request(
            self.app,
            "POST",
            "/collections/aggregate-records/records/batch",
            body=request,
            headers={"idempotency-key": "aggregate-records-1"},
        )
        self.assertEqual(202, status)
        assert isinstance(admitted, dict)
        run_id = admitted["id"]
        self.assertEqual(
            f"/record-import/jobs/{run_id}", headers["location"]
        )
        self.assertEqual(
            "upsertRecords", admitted["admission"]["operationId"]
        )
        self.assertFalse(admitted["admission"]["replayed"])

        status, _, replay = await _request(
            self.app,
            "POST",
            "/collections/aggregate-records/records/batch",
            body=request,
            headers={"idempotency-key": "aggregate-records-1"},
        )
        self.assertEqual(202, status)
        assert isinstance(replay, dict)
        self.assertEqual(run_id, replay["id"])
        self.assertTrue(replay["admission"]["replayed"])

        if self.app._dispatcher._dispatch_tasks:
            await asyncio.gather(*self.app._dispatcher._dispatch_tasks)
        status, _, job = await _request(
            self.app, "GET", f"/record-import/jobs/{run_id}"
        )
        self.assertEqual(200, status)
        assert isinstance(job, dict)
        self.assertEqual("succeeded", job["status"])
        self.assertEqual("upsertRecords", job["admission"]["operationId"])
        status, _, execution_run = await _request(
            self.app, "GET", f"/execution/runs/{run_id}"
        )
        self.assertEqual(200, status)
        assert isinstance(execution_run, dict)
        self.assertEqual(
            "upsertRecords",
            execution_run["admission"]["operationId"],
        )

    async def test_multipart_record_artifact_ingestion_is_atomic_at_boundary(
        self,
    ) -> None:
        await _request(
            self.app,
            "POST",
            "/collections",
            body={"name": "consumer-results"},
        )
        if self.app._dispatcher._dispatch_tasks:
            await asyncio.gather(*self.app._dispatcher._dispatch_tasks)
        manifest = {
            "collection": "consumer-results",
            "record": {
                "id": "result-1",
                "partitionKey": "consumer-a",
                "type": "consumer.result",
                "content": {"summary": "generic contract"},
            },
            "artifact": {
                "container": "consumer-artifacts",
                "key": "results/2026/result-1.json",
                "contentType": "application/json",
                "metadata": {"schema": "consumer.result"},
            },
        }
        artifact = b'{"raw":true}'
        boundary = "vyral-rest-test-boundary"
        multipart = (
            f"--{boundary}\r\n"
            'Content-Disposition: form-data; name="manifest"\r\n'
            "Content-Type: application/json\r\n\r\n"
        ).encode("ascii")
        multipart += json.dumps(manifest).encode("utf-8")
        multipart += (
            f"\r\n--{boundary}\r\n"
            'Content-Disposition: form-data; name="artifact"; '
            'filename="result.json"\r\n'
            "Content-Type: application/json\r\n\r\n"
        ).encode("ascii")
        multipart += artifact
        multipart += f"\r\n--{boundary}--\r\n".encode("ascii")

        status, headers, accepted = await _request(
            self.app,
            "POST",
            "/ingest/record-artifact",
            raw_body=multipart,
            content_type=(
                f"multipart/form-data; boundary={boundary}"
            ),
            headers={"idempotency-key": "artifact-rest-1"},
        )
        self.assertEqual(202, status)
        assert isinstance(accepted, dict)
        run_id = accepted["id"]
        self.assertEqual(
            f"/execution/runs/{run_id}",
            headers["location"],
        )
        self.assertEqual(
            "ingestRecordArtifact",
            accepted["admission"]["operationId"],
        )
        self.assertFalse(accepted["admission"]["replayed"])
        self.assertNotIn("idempotencyKey", accepted)

        status, _, replay = await _request(
            self.app,
            "POST",
            "/ingest/record-artifact",
            raw_body=multipart,
            content_type=(
                f"multipart/form-data; boundary={boundary}"
            ),
            headers={"idempotency-key": "artifact-rest-1"},
        )
        self.assertEqual(202, status)
        assert isinstance(replay, dict)
        self.assertEqual(run_id, replay["id"])
        self.assertTrue(replay["admission"]["replayed"])

        if self.app._dispatcher._dispatch_tasks:
            await asyncio.gather(
                *self.app._dispatcher._dispatch_tasks
            )
        status, _, completed = await _request(
            self.app,
            "GET",
            f"/execution/runs/{run_id}",
        )
        self.assertEqual(200, status)
        assert isinstance(completed, dict)
        self.assertEqual("succeeded", completed["status"])
        self.assertEqual(
            "ingestRecordArtifact",
            completed["admission"]["operationId"],
        )
        receipt = completed["result"]
        assert isinstance(receipt, dict)
        self.assertTrue(receipt["accepted"])
        self.assertEqual(
            "/collections/consumer-results/records/"
            "consumer-a/result-1",
            receipt["recordUri"],
        )

        status, _, loaded = await _request(
            self.app,
            "GET",
            "/objects/consumer-artifacts/results/2026/result-1.json",
        )
        self.assertEqual(200, status)
        self.assertEqual({"raw": True}, loaded)
        status, _, record = await _request(
            self.app,
            "GET",
            "/collections/consumer-results/records/"
            "consumer-a/result-1",
        )
        self.assertEqual(200, status)
        self.assertEqual(
            {"summary": "generic contract"}, record["content"]
        )

    async def test_problem_details_hide_internal_exceptions(self) -> None:
        status, headers, problem = await _request(
            self.app,
            "GET",
            "/collections/missing",
        )
        self.assertEqual(404, status)
        self.assertEqual(
            "application/problem+json", headers["content-type"]
        )
        assert isinstance(problem, dict)
        self.assertEqual("Not Found", problem["title"])

        status, _, problem = await _request(
            self.app,
            "POST",
            "/collections",
            raw_body=b"{invalid",
            content_type="application/json",
        )
        self.assertEqual(400, status)
        assert isinstance(problem, dict)
        self.assertNotIn("Traceback", problem["detail"])

    async def test_limits_and_origin_are_fail_closed(self) -> None:
        app = VyralRestApplication(
            self.runtime,
            RestApplicationConfig(max_request_body_bytes=128),
        )
        status, _, _ = await _request(
            app,
            "GET",
            "/health",
            headers={"origin": "https://attacker.invalid"},
        )
        self.assertEqual(403, status)

        status, _, _ = await _request(
            app,
            "GET",
            "/health",
            headers={"host": "attacker.invalid"},
        )
        self.assertEqual(403, status)

        status, _, _ = await _request(
            app,
            "GET",
            "/health",
            headers={"host": "127.0.0.1:5220"},
        )
        self.assertEqual(200, status)

        status, _, _ = await _request(
            app,
            "POST",
            "/collections",
            raw_body=b"x" * 129,
            content_type="application/json",
        )
        self.assertEqual(413, status)


if __name__ == "__main__":
    unittest.main()
