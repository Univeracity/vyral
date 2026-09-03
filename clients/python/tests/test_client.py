from __future__ import annotations

import asyncio
import hashlib
import json
import io
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "src"))

import vyral_client.client as client_module
from vyral_client import (
    AsyncVyralClient,
    VyralClient,
    VyralClientError,
    TERMINAL_EXECUTION_RUN_STATUSES,
    build_graph_assertion,
    build_evidence_brief_transaction,
    build_graph_edge,
    build_graph_envelope,
    build_graph_expansion_options,
    build_graph_doctor_request,
    build_graph_inspection_request,
    build_graph_node,
    build_graph_review,
    build_graph_scope,
    build_graph_source_span,
    build_graph_traversal_request,
    build_provider_chat_request,
    build_provider_extract_request,
    build_provider_rerank_request,
    build_provider_review_request,
    build_provider_scaffold_request,
    build_provider_tool_plan_request,
    build_rag_collection_policy,
    build_rag_context_request,
    build_rag_text_ingestion_request,
    build_retrieval_evaluation_case,
    build_retrieval_evaluation_comparison_request,
    build_retrieval_evaluation_expected_match,
    build_retrieval_evaluation_hard_negative,
    build_retrieval_evaluation_request,
    build_retrieval_evaluation_variant,
    build_retrieval_profile_request,
    build_rerank_options,
    build_verified_retrieval_request,
    compare_rag_ingest_results,
    get_provider_run_rejection,
    is_execution_run_terminal,
    is_provider_run_output_usable,
    is_provider_run_succeeded,
    stamp_graph_node_metadata,
    summarize_rag_ingest_result,
)


class FakeResponse:
    def __init__(self, body: bytes):
        self._body = body

    def __enter__(self) -> "FakeResponse":
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def read(self) -> bytes:
        return self._body


class FakeAsyncResponse:
    def __init__(self, body: bytes, status_code: int = 200, headers: dict[str, str] | None = None):
        self.content = body
        self.status_code = status_code
        self.headers = headers or {}
        self.closed = False

    async def aread(self) -> bytes:
        return self.content

    async def aclose(self) -> None:
        self.closed = True


class FakeAsyncTransport:
    def __init__(self, responses: list[FakeAsyncResponse]):
        self.responses = responses
        self.requests: list[dict[str, object]] = []

    async def request(self, method: str, url: str, **kwargs: object) -> FakeAsyncResponse:
        self.requests.append({"method": method, "url": url, **kwargs})
        return self.responses.pop(0)


def evidence_brief_fixture() -> dict[str, object]:
    return {
        "schema": "vyral.evidence-brief.v1",
        "id": "brief-rates-2026-07-21",
        "question": "What rate was published as of 2026-07-21?",
        "asOfUtc": "2026-07-21T12:00:00Z",
        "factAnchors": [{
            "id": "rate-published",
            "statement": "The official schedule lists the rate as 4.25 percent.",
            "sourceSnapshotIds": ["official-schedule"],
            "citationIds": ["official-schedule-page-4"],
        }],
        "sourceSnapshots": [{
            "id": "official-schedule",
            "kind": "web",
            "uri": "https://example.test/rates/schedule",
            "contentHash": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "capturedAtUtc": "2026-07-21T11:59:00Z",
        }],
        "citations": [{
            "id": "official-schedule-page-4",
            "sourceSnapshotId": "official-schedule",
            "factAnchorIds": ["rate-published"],
            "counterEvidenceIds": [],
            "displayText": "Official rate schedule, page 4",
        }],
        "counterEvidence": [],
        "uncertainties": [],
        "retrievalTraces": [{
            "traceId": "trace-rates-2026-07-21",
            "retrievedAtUtc": "2026-07-21T11:58:00Z",
            "queryHash": "sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            "matches": [{
                "collection": "rates-public",
                "recordId": "schedule-2026-07-page-4",
                "rank": 1,
                "sourceSnapshotIds": ["official-schedule"],
            }],
        }],
    }


class VyralClientTests(unittest.TestCase):
    def test_optional_async_transport_supports_retry_multipart_and_execution_events(self) -> None:
        async def exercise() -> None:
            transport = FakeAsyncTransport([
                FakeAsyncResponse(b'{"title":"Unavailable"}', 503, {"Retry-After": "0"}),
                FakeAsyncResponse(b'{"status":"ok"}'),
                FakeAsyncResponse(b'{"id":"run-artifact-1","status":"queued"}', 202),
                FakeAsyncResponse(b'{"name":"approved"}'),
            ])
            client = AsyncVyralClient(
                "https://vyral.local",
                bearer_token="token",
                default_headers={"X-Client": "async-python"},
                correlation_id="corr-async",
                max_retries=1,
                retry_backoff_seconds=0,
                transport=transport,
            )
            self.assertEqual("ok", (await client.health())["status"])
            receipt = await client.ingest_record_artifact(
                {
                    "collection": "events",
                    "record": {"id": "event-1", "partitionKey": "tenant-a", "type": "consumer.event"},
                    "artifact": {"container": "artifacts", "key": "events/event-1.json"},
                },
                io.BytesIO(b"artifact-body"),
                filename="event.json",
                content_type="application/json",
                idempotency_key="artifact-async-1",
            )
            event = await client.raise_execution_event("run/1", {"name": "approved"})
            self.assertEqual("run-artifact-1", receipt["id"])
            self.assertEqual("approved", event["name"])
            self.assertEqual([], transport.responses)
            self.assertEqual(["GET", "GET", "POST", "POST"], [item["method"] for item in transport.requests])
            for request in transport.requests:
                headers = request["headers"]
                self.assertEqual("Bearer token", headers["Authorization"])
                self.assertEqual("async-python", headers["X-Client"])
                self.assertEqual("corr-async", headers["X-Correlation-ID"])
                self.assertIs(False, request["follow_redirects"])
            multipart = transport.requests[2]["files"]
            self.assertEqual(
                "artifact-async-1",
                transport.requests[2]["headers"]["Idempotency-Key"],
            )
            self.assertEqual(None, multipart["manifest"][0])
            self.assertEqual("event.json", multipart["artifact"][0])
            self.assertEqual(
                "https://vyral.local/execution/runs/run%2F1/events",
                transport.requests[3]["url"],
            )

        asyncio.run(exercise())

    def test_remaining_base_surface_methods_use_expected_routes(self) -> None:
        responses = [
            b'["events"]',
            b'{"name":"events"}',
            b'{"id":"import-1"}',
            b'{"id":"event-1"}',
            b'',
            b'{"id":"event-2"}',
            b'{"items":[],"continuationToken":null}',
            b'{"container":"artifacts","key":"logs/event 1.json"}',
            b'artifact-body',
            b'',
            b'{"matches":[]}',
            b'[{"id":"trace-1"}]',
            b'{"id":"trace-1"}',
        ]
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen.append({
                "method": getattr(request, "get_method")(),
                "url": getattr(request, "full_url"),
                "headers": dict(getattr(request, "headers")),
                "body": getattr(request, "data", None),
            })
            return FakeResponse(responses.pop(0))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            self.assertEqual(["events"], client.list_collections())
            self.assertEqual("events", client.get_collection_policy("event streams")["name"])
            self.assertEqual(
                "import-1",
                client.start_collection_import_job(
                    "event streams",
                    {"schema": "vyral.collection-snapshot.v1", "records": []},
                    idempotency_key="import-1",
                )["id"],
            )
            self.assertEqual("event-1", client.get_record("event streams", "tenant/a", "event/1")["id"])
            client.delete_record("event streams", "tenant/a", "event/1")
            self.assertEqual(
                "event-2",
                client.upsert_record(
                    "event streams",
                    {"id": "event-2", "partitionKey": "tenant/a", "type": "consumer.event"},
                )["id"],
            )
            self.assertEqual(
                [],
                client.list_objects("artifacts", prefix="logs/", limit=2, continuation_token="next/1")["items"],
            )
            self.assertEqual(
                "logs/event 1.json",
                client.put_object(
                    "artifacts",
                    "logs/event 1.json",
                    b"artifact-body",
                    content_type="application/json",
                    metadata={"source": "sdk"},
                    if_none_match="*",
                )["key"],
            )
            self.assertEqual(b"artifact-body", client.get_object("artifacts", "logs/event 1.json"))
            client.delete_object("artifacts", "logs/event 1.json", if_match='"etag-1"')
            self.assertEqual([], client.retrieve({"collections": ["events"], "text": "incident"})["matches"])
            self.assertEqual("trace-1", client.list_traces(operation="search", limit=1)[0]["id"])
            self.assertEqual("trace-1", client.get_trace("trace/1")["id"])
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual([], responses)
        self.assertEqual(
            [
                ("GET", "http://vyral.local/collections"),
                ("GET", "http://vyral.local/collections/event%20streams"),
                ("POST", "http://vyral.local/collections/event%20streams/import/jobs"),
                ("GET", "http://vyral.local/collections/event%20streams/records/tenant%2Fa/event%2F1"),
                ("DELETE", "http://vyral.local/collections/event%20streams/records/tenant%2Fa/event%2F1"),
                ("POST", "http://vyral.local/collections/event%20streams/records"),
                ("GET", "http://vyral.local/objects/artifacts?prefix=logs%2F&limit=2&continuationToken=next%2F1"),
                ("PUT", "http://vyral.local/objects/artifacts/logs/event%201.json"),
                ("GET", "http://vyral.local/objects/artifacts/logs/event%201.json"),
                ("DELETE", "http://vyral.local/objects/artifacts/logs/event%201.json"),
                ("POST", "http://vyral.local/search"),
                ("GET", "http://vyral.local/traces?operation=search&limit=1"),
                ("GET", "http://vyral.local/traces/trace/1"),
            ],
            [(item["method"], item["url"]) for item in seen],
        )
        self.assertEqual("import-1", seen[2]["headers"]["Idempotency-key"])
        self.assertEqual("application/json", seen[7]["headers"]["Content-type"])
        self.assertEqual("sdk", seen[7]["headers"]["X-vyral-meta-source"])
        self.assertEqual("*", seen[7]["headers"]["If-none-match"])
        self.assertEqual('"etag-1"', seen[9]["headers"]["If-match"])

    def test_record_artifact_ingest_streams_multipart_parts(self) -> None:
        artifact = io.BytesIO(b"artifact-body")
        receipt = {"id": "run-artifact-1", "status": "queued"}
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            self.assertEqual(0, artifact.tell(), "artifact must not be read before transport sends the request")
            body = b"".join(getattr(request, "data"))
            seen.update({
                "method": getattr(request, "get_method")(),
                "url": getattr(request, "full_url"),
                "content_type": getattr(request, "headers")["Content-type"],
                "idempotency_key": getattr(request, "headers")["Idempotency-key"],
                "body": body,
            })
            return FakeResponse(json.dumps(receipt).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").ingest_record_artifact(
                {
                    "collection": "events",
                    "record": {"id": "event-1", "partitionKey": "tenant-a", "type": "consumer.event"},
                    "artifact": {"container": "consumer-artifacts", "key": "events/event-1.json"},
                },
                artifact,
                filename="event.json",
                content_type="application/json",
                chunk_size=4,
                idempotency_key="artifact-sync-1",
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual(receipt, result)
        self.assertEqual("POST", seen["method"])
        self.assertEqual("http://vyral.local/ingest/record-artifact", seen["url"])
        self.assertEqual("artifact-sync-1", seen["idempotency_key"])
        self.assertRegex(str(seen["content_type"]), r"^multipart/form-data; boundary=vyral-[0-9a-f]{32}$")
        body = seen["body"]
        self.assertIn(b'name="manifest"', body)
        self.assertIn(b'"collection":"events"', body)
        self.assertIn(b'"container":"consumer-artifacts"', body)
        self.assertIn(b'name="artifact"; filename="event.json"', body)
        self.assertIn(b"Content-Type: application/json", body)
        self.assertIn(b"artifact-body", body)

    def test_raise_execution_event_uses_run_route_and_rejects_mismatch(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen.update({
                "method": getattr(request, "get_method")(),
                "url": getattr(request, "full_url"),
                "body": json.loads(getattr(request, "data").decode("utf-8")),
            })
            return FakeResponse(b'{"accepted":true}')

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            result = client.raise_execution_event("run/1", {"name": "approved", "payload": {"by": "operator"}})
            with self.assertRaisesRegex(ValueError, "must match"):
                client.raise_execution_event("run-1", {"runId": "run-2", "name": "approved"})
        finally:
            client_module.urlopen = original_urlopen

        self.assertTrue(result["accepted"])
        self.assertEqual("POST", seen["method"])
        self.assertEqual("http://vyral.local/execution/runs/run%2F1/events", seen["url"])
        self.assertEqual({"name": "approved", "payload": {"by": "operator"}}, seen["body"])

    def test_evidence_brief_helpers_use_canonical_transaction_and_body_read_routes(self) -> None:
        brief = evidence_brief_fixture()
        responses = [
            {"transactionId": "brief-tx", "documents": [{"id": brief["id"]}]},
            {
                "tenantId": "tenant-a",
                "documentType": "vyral.evidence-brief",
                "id": brief["id"],
                "schemaVersion": "vyral.evidence-brief.v1",
                "data": brief,
            },
        ]
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            body = getattr(request, "data", None)
            seen.append({
                "method": getattr(request, "get_method")(),
                "url": getattr(request, "full_url"),
                "body": json.loads(body.decode("utf-8")) if body else None,
            })
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            stored = client.store_evidence_brief("tenant-a", brief, idempotency_key="brief:rates:v1")
            loaded = client.get_evidence_brief("tenant-a", brief["id"])
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("brief-tx", stored["transactionId"])
        self.assertEqual(brief, loaded["brief"] if loaded else None)
        self.assertEqual([
            {"method": "POST", "url": "http://vyral.local/canonical/tenants/tenant-a/transactions"},
            {"method": "POST", "url": "http://vyral.local/canonical/tenants/tenant-a/documents/read"},
        ], [{"method": item["method"], "url": item["url"]} for item in seen])
        transaction = seen[0]["body"]
        self.assertEqual("vyral.evidence-brief", transaction["mutations"][0]["document"]["documentType"])
        self.assertEqual("vyral.evidence-brief.changed", transaction["outbox"][0]["topic"])
        self.assertEqual({
            "tenantId": "tenant-a",
            "documentType": "vyral.evidence-brief",
            "id": brief["id"],
            "includeDeleted": False,
        }, seen[1]["body"])

        invalid = dict(brief)
        invalid["schema"] = "wrong"
        with self.assertRaisesRegex(ValueError, "brief.schema"):
            build_evidence_brief_transaction("tenant-a", "brief:invalid:v1", invalid)

    def test_health_and_openapi_contract_use_status_routes(self) -> None:
        responses = [
            {"status": "ok", "service": "vyral-server"},
            {"status": "warning", "ready": True, "checks": []},
            [{"provider": "deterministic-hash"}],
            [{"provider": "local-token-hash", "realisticForSemanticRetrieval": False}],
            {"provider": "local-token-hash", "status": "ok", "checks": []},
            {"openapi": "3.1.0"},
            {"$schema": "https://json-schema.org/draft/2020-12/schema", "$defs": {}},
        ]
        seen: list[str] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen.append(getattr(request, "full_url"))
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            health = client.health()
            readiness = client.readiness()
            providers = client.list_embedding_providers()
            guidance = client.list_embedding_provider_guidance()
            doctor = client.get_embedding_provider_doctor()
            contract = client.openapi_contract()
            schemas = client.get_public_schema_contract()
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("ok", health["status"])
        self.assertTrue(readiness["ready"])
        self.assertEqual("deterministic-hash", providers[0]["provider"])
        self.assertEqual("local-token-hash", guidance[0]["provider"])
        self.assertEqual("local-token-hash", doctor["provider"])
        self.assertEqual("3.1.0", contract["openapi"])
        self.assertEqual("https://json-schema.org/draft/2020-12/schema", schemas["$schema"])
        self.assertEqual([
            "http://vyral.local/health",
            "http://vyral.local/readiness",
            "http://vyral.local/embedding-providers",
            "http://vyral.local/embedding-providers/guidance",
            "http://vyral.local/embedding-providers/doctor",
            "http://vyral.local/openapi/vyral.json",
            "http://vyral.local/contracts/schemas/vyral-public.schema.json",
        ], seen)

    def test_query_all_records_drains_continuation_tokens(self) -> None:
        responses = [
            {"items": [{"id": "a"}], "continuationToken": "next"},
            {"items": [{"id": "b"}], "continuationToken": None},
        ]
        requests: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            requests.append(json.loads(getattr(request, "data").decode("utf-8")))
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            items = VyralClient("http://vyral.local").query_all_records("chunks", {"limit": 1})
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual(["a", "b"], [item["id"] for item in items])
        self.assertEqual({"limit": 1}, requests[0])
        self.assertEqual({"limit": 1, "continuationToken": "next"}, requests[1])

    def test_record_iterator_enforces_page_and_item_bounds(self) -> None:
        responses = [
            {"items": [{"id": "a"}, {"id": "b"}], "continuationToken": "next"},
            {"items": [{"id": "a"}], "continuationToken": "next"},
        ]
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del request, timeout
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            self.assertEqual(["a"], [item["id"] for item in client.iter_records("chunks", max_items=1)])
            with self.assertRaisesRegex(RuntimeError, "max_pages=1"):
                list(client.iter_records("chunks", max_pages=1))
            with self.assertRaisesRegex(ValueError, "max_pages"):
                list(client.iter_records("chunks", max_pages=0))
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual([], responses)

    def test_canonical_store_helpers_use_tenant_scoped_routes_and_body_lease_tokens(self) -> None:
        responses = [
            [],
            None,
            {"transactionId": "ctx-1", "replayed": False},
            {"id": "claim-1", "revision": 1},
            {"items": [{"id": "claim-1"}], "continuationToken": "next"},
            {"items": [{"id": "claim-2"}], "continuationToken": None},
            [{"revision": 1}],
            [{"event": {"id": "evt-1"}, "leaseToken": "opaque-token"}],
            {"items": [{"id": "evt-1"}], "continuationToken": None},
            {"expiresAtUtc": "2026-07-12T00:00:00Z"},
            None,
            None,
            None,
            {"tenantId": "tenant-a", "contentHash": "sha256:snapshot"},
            None,
        ]
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            data = getattr(request, "data")
            seen.append({
                "method": getattr(request, "method"),
                "url": getattr(request, "full_url"),
                "body": json.loads(data.decode("utf-8")) if data else None,
            })
            response = responses.pop(0)
            return FakeResponse(b"" if response is None else json.dumps(response).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            self.assertEqual([], client.list_canonical_migrations())
            self.assertIsNone(client.apply_canonical_migrations([{"namespace": "client-test", "id": "m1", "checksum": "sha256:1"}]))
            self.assertEqual("ctx-1", client.commit_canonical_transaction("tenant-a", {"tenantId": "tenant-a", "idempotencyKey": "claim-1"})["transactionId"])
            self.assertEqual("claim-1", client.get_canonical_document("tenant-a", "claim/type", "claim/1", include_deleted=True)["id"])
            self.assertEqual(["claim-1", "claim-2"], [item["id"] for item in client.query_all_canonical_documents("tenant-a", {"tenantId": "tenant-a", "limit": 1})])
            self.assertEqual(1, client.list_canonical_document_revisions("tenant-a", "claim/type", "claim/1", limit=2)[0]["revision"])
            self.assertEqual("evt-1", client.lease_canonical_outbox("tenant-a", {"tenantId": "tenant-a", "consumerId": "projector"})[0]["event"]["id"])
            self.assertEqual("evt-1", client.query_canonical_outbox("tenant-a", {"tenantId": "tenant-a", "state": "leased"})["items"][0]["id"])
            self.assertEqual("2026-07-12T00:00:00Z", client.renew_canonical_outbox_lease("tenant-a", "evt-1", {"tenantId": "tenant-a", "eventId": "evt-1", "leaseToken": "opaque-token"})["expiresAtUtc"])
            self.assertIsNone(client.acknowledge_canonical_outbox("tenant-a", "evt-1", "opaque-token"))
            self.assertIsNone(client.release_canonical_outbox("tenant-a", "evt-1", {"tenantId": "tenant-a", "eventId": "evt-1", "leaseToken": "opaque-token"}))
            self.assertIsNone(client.replay_canonical_outbox("tenant-a", "evt-1", {"tenantId": "tenant-a", "eventId": "evt-1"}))
            snapshot = client.export_canonical_tenant("tenant-a")
            self.assertEqual("sha256:snapshot", snapshot["contentHash"])
            self.assertIsNone(client.restore_canonical_tenant("tenant-a", snapshot, expected_content_hash=snapshot["contentHash"]))
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual([
            ("GET", "http://vyral.local/canonical/migrations"),
            ("POST", "http://vyral.local/canonical/migrations"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/transactions"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/documents/read"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/documents/query"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/documents/query"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/documents/revisions"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/outbox/leases"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/outbox/query"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/outbox/evt-1/renew"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/outbox/evt-1/ack"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/outbox/evt-1/nack"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/outbox/evt-1/replay"),
            ("GET", "http://vyral.local/canonical/tenants/tenant-a/export"),
            ("POST", "http://vyral.local/canonical/tenants/tenant-a/restore"),
        ], [(item["method"], item["url"]) for item in seen])
        self.assertEqual({"tenantId": "tenant-a", "documentType": "claim/type", "id": "claim/1", "includeDeleted": True}, seen[3]["body"])
        self.assertEqual({"tenantId": "tenant-a", "documentType": "claim/type", "id": "claim/1", "limit": 2}, seen[6]["body"])
        self.assertEqual({"leaseToken": "opaque-token"}, seen[10]["body"])
        self.assertEqual({"snapshot": {"tenantId": "tenant-a", "contentHash": "sha256:snapshot"}, "expectedContentHash": "sha256:snapshot"}, seen[14]["body"])

    def test_execution_runtime_helpers_use_execution_routes(self) -> None:
        responses = [
            {"adapter": {"adapterId": "local-sqlite"}, "plugins": [], "handlers": []},
            {"adapterId": "local-sqlite", "runtimeKind": "local.sqlite", "rowCounts": {"runs": 2}},
            {"dryRun": True, "retainTerminalRuns": 1, "runs": 1, "runIds": ["run-old"]},
            {"id": "run-1", "status": "queued"},
            [{"id": "run-1", "status": "running"}],
            {"id": "run-1", "status": "running"},
            [{"type": "run.created"}],
            [{"id": "artifact-1", "name": "summary"}],
            {"id": "artifact-1", "name": "summary", "content": "done"},
            {"runId": "run-1", "key": "cursor", "value": {"offset": 2}},
            {"id": "run-1", "status": "cancelled"},
        ]
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen.append({
                "method": getattr(request, "get_method")(),
                "url": getattr(request, "full_url"),
            })
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            runtime = client.get_execution_runtime()
            maintenance = client.get_execution_runtime_maintenance()
            prune = client.prune_execution_runtime_maintenance(retain_terminal_runs=1)
            started = client.start_execution_run({
                "handlerId": "jobs.rag",
                "payload": {"collection": "chunks"},
                "correlationId": "corr-1",
            })
            runs = client.list_execution_runs(
                handler_id="jobs.rag",
                plugin_id="plugin.rag",
                status="running",
                correlation_id="corr-1",
                idempotency_key="idem-1",
                tags={"projectId": "project-a"},
                limit=5,
                include_result=True,
            )
            run = client.get_execution_run("run-1", include_result=False)
            history = client.get_execution_run_history("run-1", limit=2)
            artifacts = client.list_execution_run_artifacts("run-1")
            artifact = client.get_execution_run_artifact("run-1", "summary")
            checkpoint = client.get_execution_run_checkpoint("run-1", "cursor")
            cancelled = client.cancel_execution_run("run-1")
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("local-sqlite", runtime["adapter"]["adapterId"])
        self.assertEqual("local.sqlite", maintenance["runtimeKind"])
        self.assertTrue(prune["dryRun"])
        self.assertEqual("queued", started["status"])
        self.assertEqual("run-1", runs[0]["id"])
        self.assertEqual("running", run["status"])
        self.assertEqual("run.created", history[0]["type"])
        self.assertEqual("artifact-1", artifacts[0]["id"])
        self.assertEqual("done", artifact["content"])
        self.assertEqual(2, checkpoint["value"]["offset"])
        self.assertEqual("cancelled", cancelled["status"])
        self.assertEqual([
            {"method": "GET", "url": "http://vyral.local/execution/runtime"},
            {"method": "GET", "url": "http://vyral.local/execution/runtime/maintenance"},
            {"method": "POST", "url": "http://vyral.local/execution/runtime/maintenance/prune"},
            {"method": "POST", "url": "http://vyral.local/execution/runs"},
            {
                "method": "GET",
                "url": "http://vyral.local/execution/runs?handlerId=jobs.rag&pluginId=plugin.rag&status=running&correlationId=corr-1&idempotencyKey=idem-1&limit=5&tag.projectId=project-a&includeResult=true",
            },
            {"method": "GET", "url": "http://vyral.local/execution/runs/run-1?includeResult=false"},
            {"method": "GET", "url": "http://vyral.local/execution/runs/run-1/history?limit=2"},
            {"method": "GET", "url": "http://vyral.local/execution/runs/run-1/artifacts"},
            {"method": "GET", "url": "http://vyral.local/execution/runs/run-1/artifacts/summary"},
            {"method": "GET", "url": "http://vyral.local/execution/runs/run-1/checkpoints/cursor"},
            {"method": "DELETE", "url": "http://vyral.local/execution/runs/run-1"},
        ], seen)

    def test_wait_execution_run_polls_until_terminal_status(self) -> None:
        responses = [
            {"id": "run-1", "status": "running"},
            {"id": "run-1", "status": "succeeded", "result": {"ok": True}},
        ]
        seen: list[str] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen.append(getattr(request, "full_url"))
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            run = VyralClient("http://vyral.local").wait_execution_run(
                "run-1",
                timeout_seconds=1,
                poll_interval_seconds=0,
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertTrue(is_execution_run_terminal(run))
        self.assertIn("succeeded", TERMINAL_EXECUTION_RUN_STATUSES)
        self.assertEqual({"ok": True}, run["result"])
        self.assertEqual([
            "http://vyral.local/execution/runs/run-1?includeResult=true",
            "http://vyral.local/execution/runs/run-1?includeResult=true",
        ], seen)

    def test_external_execution_worker_helpers_cover_the_portable_protocol(self) -> None:
        responses = [
            {"dryRun": False, "dispatched": 1},
            {"leaseKey": "lease-1", "leaseToken": "token-1", "workerId": "worker-a", "run": {"id": "run-1"}},
            {"leaseKey": "lease-1", "leaseToken": "token-1", "workerId": "worker-a", "run": {"id": "run-1"}},
            {"id": "run-1", "status": "running"},
            None,
            {"id": "artifact-1"},
            {"runId": "run-1", "key": "cursor"},
            {"runId": "run-1", "key": "cursor"},
            {"run": {"id": "run-1"}, "suspended": True},
            {"id": "run-1", "status": "succeeded"},
        ]
        seen: list[str] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen.append(getattr(request, "full_url"))
            body = responses.pop(0)
            return FakeResponse(b"" if body is None else json.dumps(body).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            lease_request = {"leaseKey": "lease-1", "leaseToken": "token-1", "workerId": "worker-a"}
            self.assertEqual(1, client.reconcile_execution_runtime_dispatch(limit=10)["dispatched"])
            self.assertEqual("lease-1", client.lease_external_execution_run({"workerId": "worker-a", "handlerIds": ["handler-a"]})["leaseKey"])
            client.heartbeat_external_execution_lease({**lease_request, "ttlSeconds": 30})
            client.report_external_execution_lease({**lease_request, "update": {"progress": 0.5}})
            self.assertIsNone(client.record_external_execution_lease_event({**lease_request, "type": "log", "severity": "info"}))
            client.put_external_execution_lease_artifact({**lease_request, "artifact": {"name": "summary", "content": {}}})
            client.put_external_execution_lease_checkpoint({**lease_request, "checkpoint": {"key": "cursor", "content": {}}})
            self.assertEqual("cursor", client.get_external_execution_lease_checkpoint({**lease_request, "key": "cursor"})["key"])
            client.wait_external_execution_lease({**lease_request, "kind": "external_event", "name": "approval"})
            client.complete_external_execution_lease({**lease_request, "result": {"status": "succeeded"}})
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual([
            "http://vyral.local/execution/runtime/maintenance/reconcile",
            "http://vyral.local/execution/workers/leases",
            "http://vyral.local/execution/workers/leases/heartbeat",
            "http://vyral.local/execution/workers/leases/reports",
            "http://vyral.local/execution/workers/leases/events",
            "http://vyral.local/execution/workers/leases/artifacts",
            "http://vyral.local/execution/workers/leases/checkpoints",
            "http://vyral.local/execution/workers/leases/checkpoints/read",
            "http://vyral.local/execution/workers/leases/wait",
            "http://vyral.local/execution/workers/leases/complete",
        ], seen)

    def test_collection_snapshot_export_and_import_helpers_use_typed_routes(self) -> None:
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            body = getattr(request, "data", None)
            seen.append({
                "method": getattr(request, "get_method")(),
                "url": getattr(request, "full_url"),
                "body": json.loads(body.decode("utf-8")) if body else None,
            })
            if getattr(request, "get_method")() == "POST" and getattr(request, "full_url").endswith("/import"):
                return FakeResponse(json.dumps({"collection": "chunks-copy", "records": {"succeeded": 2}}).encode("utf-8"))
            if getattr(request, "get_method")() == "POST":
                return FakeResponse(json.dumps({
                    "collection": "chunks",
                    "policy": {"name": "chunks"},
                    "records": [{"id": "a"}],
                    "recordCount": 1,
                    "maxRecords": 1,
                    "truncated": True,
                    "continuationToken": "next",
                    "contentHash": "sha256:bounded",
                }).encode("utf-8"))
            return FakeResponse(json.dumps({
                "collection": "chunks",
                "policy": {"name": "chunks"},
                "records": [{"id": "a"}, {"id": "b"}],
                "recordCount": 2,
                "contentHash": "sha256:abc",
            }).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            export = client.export_collection("chunks")
            bounded = client.export_collection(
                "chunks",
                query={"limit": 10},
                max_records=1,
                fail_on_limit_exceeded=False,
            )
            imported = client.import_collection(
                "chunks-copy",
                export,
                expected_content_hash="sha256:abc",
                allow_collection_rename=True,
                allow_partial_snapshot=True,
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("chunks", export["collection"])
        self.assertEqual(["a", "b"], [record["id"] for record in export["records"]])
        self.assertEqual(2, export["recordCount"])
        self.assertTrue(bounded["truncated"])
        self.assertEqual("chunks-copy", imported["collection"])
        self.assertEqual("GET", seen[0]["method"])
        self.assertEqual("http://vyral.local/collections/chunks/export", seen[0]["url"])
        self.assertEqual("POST", seen[1]["method"])
        self.assertEqual("http://vyral.local/collections/chunks/export", seen[1]["url"])
        self.assertEqual({"limit": 10}, seen[1]["body"]["query"])
        self.assertEqual(1, seen[1]["body"]["maxRecords"])
        self.assertFalse(seen[1]["body"]["failOnLimitExceeded"])
        self.assertEqual("POST", seen[2]["method"])
        self.assertEqual("http://vyral.local/collections/chunks-copy/import", seen[2]["url"])
        self.assertEqual("sha256:abc", seen[2]["body"]["expectedContentHash"])
        self.assertTrue(seen[2]["body"]["allowCollectionRename"])
        self.assertTrue(seen[2]["body"]["allowPartialSnapshot"])

    def test_embed_texts_posts_embedding_request(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({
                "provider": "deterministic-hash",
                "modelId": "deterministic-hash-embedding-v1",
                "dimensions": 2,
                "items": [
                    {"index": 0, "textLength": 5, "values": [1.0, 0.0]},
                    {"index": 1, "textLength": 4, "values": [0.0, 1.0]},
                ],
            }).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").embed_texts(["alpha", "beta"])
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/embeddings", seen["url"])
        self.assertEqual({"texts": ["alpha", "beta"]}, seen["body"])
        self.assertEqual([1.0, 0.0], result["items"][0]["values"])

    def test_embed_texts_posts_embedding_purpose_options(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({
                "provider": "deterministic-hash",
                "modelId": "deterministic-hash-embedding-v1",
                "dimensions": 2,
                "purpose": "query",
                "items": [
                    {
                        "index": 0,
                        "textLength": 5,
                        "preparedTextLength": 12,
                        "prefixApplied": True,
                        "prefixLength": 7,
                        "values": [1.0, 0.0],
                    },
                ],
            }).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").embed_texts(
                ["alpha"],
                purpose="query",
                query_prefix="query: ",
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual({"texts": ["alpha"], "purpose": "query", "queryPrefix": "query: "}, seen["body"])
        self.assertTrue(result["items"][0]["prefixApplied"])

    def test_embed_text_returns_first_embedding_values(self) -> None:
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del request, timeout
            return FakeResponse(json.dumps({
                "provider": "deterministic-hash",
                "modelId": "deterministic-hash-embedding-v1",
                "dimensions": 2,
                "items": [
                    {"index": 0, "textLength": 5, "values": [0.25, 0.75]},
                ],
            }).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            values = VyralClient("http://vyral.local").embed_text("alpha")
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual([0.25, 0.75], values)

    def test_embedding_job_helpers_use_job_routes(self) -> None:
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            method = getattr(request, "get_method")()
            url = getattr(request, "full_url")
            body = getattr(request, "data", None)
            seen.append({
                "method": method,
                "url": url,
                "body": json.loads(body.decode("utf-8")) if body else None,
            })
            if method == "GET" and url.endswith("/embeddings/jobs/embed-1"):
                return FakeResponse(json.dumps({"id": "embed-1", "status": "succeeded", "progress": 1.0}).encode("utf-8"))
            if method == "GET":
                return FakeResponse(json.dumps([{"id": "embed-1", "status": "running"}]).encode("utf-8"))
            return FakeResponse(json.dumps({"id": "embed-1", "status": "queued", "progress": 0.0}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            started = client.start_embedding_job({"texts": ["alpha", "beta"], "purpose": "passage"})
            listed = client.list_embedding_jobs(limit=5, include_result=True)
            fetched = client.get_embedding_job("embed-1")
            cancelled = client.cancel_embedding_job("embed-1")
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("embed-1", started["id"])
        self.assertEqual("embed-1", listed[0]["id"])
        self.assertEqual("succeeded", fetched["status"])
        self.assertEqual("queued", cancelled["status"])
        self.assertEqual([
            {
                "method": "POST",
                "url": "http://vyral.local/embeddings/jobs",
                "body": {"texts": ["alpha", "beta"], "purpose": "passage"},
            },
            {
                "method": "GET",
                "url": "http://vyral.local/embeddings/jobs?limit=5&includeResult=true",
                "body": None,
            },
            {
                "method": "GET",
                "url": "http://vyral.local/embeddings/jobs/embed-1",
                "body": None,
            },
            {
                "method": "DELETE",
                "url": "http://vyral.local/embeddings/jobs/embed-1",
                "body": None,
            },
        ], seen)

    def test_wait_embedding_job_polls_until_terminal_status(self) -> None:
        responses = [
            {"id": "embed-1", "status": "running"},
            {"id": "embed-1", "status": "succeeded"},
        ]
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del request, timeout
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").wait_embedding_job(
                "embed-1",
                timeout_seconds=1,
                poll_interval_seconds=0,
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("succeeded", result["status"])
        self.assertEqual([], responses)

    def test_build_rag_collection_policy_uses_vyral_rag_defaults(self) -> None:
        policy = build_rag_collection_policy("chunks", dimensions=384)

        self.assertEqual({
            "name": "chunks",
            "partitionKeyPath": "/partitionKey",
            "indexedMetadata": [
                "/metadata/documentId",
                "/metadata/topic",
                "/metadata/status",
                "/type",
            ],
            "vectorPolicies": [
                {
                    "name": "contentEmbedding",
                    "path": "/vectors/contentEmbedding/values",
                    "dimensions": 384,
                    "datatype": "float32",
                    "distanceFunction": "cosine",
                    "indexType": "flat",
                }
            ],
        }, policy)

    def test_build_rag_collection_policy_rejects_invalid_dimensions(self) -> None:
        with self.assertRaisesRegex(ValueError, "positive integer"):
            build_rag_collection_policy("chunks", dimensions=0)

    def test_build_verified_retrieval_request_uses_lexical_defaults(self) -> None:
        request = build_verified_retrieval_request(
            "RECORD-000123 update deadline",
            ["pages"],
            partition_keys=["source-a"],
            record_filter={"path": "/metadata/status", "op": "eq", "value": "active"},
            field_boosts={"/metadata/referenceId": 4.0},
        )

        self.assertEqual("lexical", request["searchMode"])
        self.assertEqual(["pages"], request["collections"])
        self.assertEqual(["source-a"], request["partitionKeys"])
        self.assertEqual({"path": "/metadata/status", "op": "eq", "value": "active"}, request["filter"])
        self.assertEqual(8, request["limit"])
        self.assertTrue(request["includeTrace"])
        self.assertIn("/content/text", request["lexical"]["fields"])
        self.assertIn("/metadata/referenceId", request["lexical"]["fields"])
        self.assertEqual(4.0, request["lexical"]["fieldBoosts"]["/metadata/referenceId"])
        self.assertEqual("bm25", request["lexical"]["scoring"])
        self.assertTrue(request["lexical"]["prefixMatching"])

    def test_build_verified_retrieval_request_rejects_empty_collections(self) -> None:
        with self.assertRaisesRegex(ValueError, "collections"):
            build_verified_retrieval_request("query", [])

    def test_build_retrieval_profile_request_carries_profile_and_overrides(self) -> None:
        request = build_retrieval_profile_request(
            "rerankPolish",
            "retention policy",
            ["chunks"],
            partition_keys=["tenant-a"],
            record_filter={"path": "/metadata/status", "op": "eq", "value": "active"},
            rerank={"enabled": True, "candidateLimit": 8},
            limit=6,
            include_trace=True,
        )

        self.assertEqual({
            "profile": "rerankPolish",
            "query": "retention policy",
            "collections": ["chunks"],
            "partitionKeys": ["tenant-a"],
            "filter": {"path": "/metadata/status", "op": "eq", "value": "active"},
            "rerank": {"enabled": True, "candidateLimit": 8},
            "limit": 6,
            "includeTrace": True,
        }, request)

    def test_retrieval_evaluation_builders_shape_cases_and_comparisons(self) -> None:
        retrieval = build_verified_retrieval_request(
            "retention hold",
            ["pages"],
            partition_keys=["source-a"],
            limit=3,
        )
        expected = build_retrieval_evaluation_expected_match(
            "page-1",
            partition_key="source-a",
            collection="pages",
            aliases=["RECORD-000001"],
            relevance=2,
        )
        hard_negative = build_retrieval_evaluation_hard_negative(
            {"id": "page-2", "sourceIds": ["RECORD-000002"]},
            reason="adjacent page",
        )
        evaluation_case = build_retrieval_evaluation_case(
            "retention",
            retrieval,
            expected,
            hard_negatives=[hard_negative],
            k=8,
            metadata={"fixture": "example"},
        )
        evaluation_request = build_retrieval_evaluation_request(
            [evaluation_case],
            default_k=8,
            include_top_results=False,
        )
        evidence = build_retrieval_evaluation_variant("evidence", profile="evidence", include_trace=True)
        rerank = build_retrieval_evaluation_variant(
            "rerank",
            profile="rerankPolish",
            rerank=build_rerank_options(provider="local-token-overlap-reranker", candidate_limit=8),
        )
        comparison = build_retrieval_evaluation_comparison_request(
            [evaluation_case],
            [evidence, rerank],
            include_top_results=True,
            include_case_results=True,
        )

        self.assertEqual({
            "name": "retention",
            "request": retrieval,
            "expected": [
                {
                    "id": "page-1",
                    "partitionKey": "source-a",
                    "collection": "pages",
                    "aliases": ["RECORD-000001"],
                    "relevance": 2,
                }
            ],
            "hardNegatives": [
                {
                    "id": "page-2",
                    "sourceIds": ["RECORD-000002"],
                    "reason": "adjacent page",
                }
            ],
            "k": 8,
            "metadata": {"fixture": "example"},
        }, evaluation_case)
        self.assertEqual({
            "cases": [evaluation_case],
            "continueOnError": True,
            "includeTopResults": False,
            "defaultK": 8,
        }, evaluation_request)
        self.assertEqual({
            "cases": [evaluation_case],
            "variants": [evidence, rerank],
            "continueOnError": True,
            "includeTopResults": True,
            "includeCaseResults": True,
        }, comparison)

    def test_rag_request_builders_use_safe_defaults(self) -> None:
        ingestion = build_rag_text_ingestion_request(
            "retention policy",
            "tenant-a",
            document_id="doc-1",
            metadata={"topic": "retention"},
            chunk_chars=1200,
            skip_unchanged_chunks=True,
            persist_manifest=True,
        )
        self.assertEqual("tenant-a", ingestion["partitionKey"])
        self.assertEqual("doc-1", ingestion["documentId"])
        self.assertEqual({"topic": "retention"}, ingestion["metadata"])
        self.assertEqual(1200, ingestion["options"]["chunkChars"])
        self.assertTrue(ingestion["options"]["skipUnchangedChunks"])
        self.assertTrue(ingestion["options"]["persistManifest"])

        rerank = build_rerank_options(provider="onnx-cross-encoder-reranker", candidate_limit=8)
        context = build_rag_context_request(
            "retention policy",
            ["chunks"],
            partition_keys=["tenant-a"],
            rerank=rerank,
        )
        self.assertEqual("lexical", context["retrieval"]["searchMode"])
        self.assertEqual(["chunks"], context["retrieval"]["collections"])
        self.assertEqual(["tenant-a"], context["retrieval"]["partitionKeys"])
        self.assertIn("/content/text", context["retrieval"]["lexical"]["fields"])
        self.assertEqual("onnx-cross-encoder-reranker", context["retrieval"]["rerank"]["provider"])
        self.assertTrue(context["includeContextText"])

        profiled = build_rag_context_request(
            "retention policy",
            ["chunks"],
            profile="discovery",
            embedding={"field": "contentEmbedding", "purpose": "query"},
            max_citations_per_chunk=2,
        )
        self.assertEqual("discovery", profiled["retrieval"]["profile"])
        self.assertNotIn("searchMode", profiled["retrieval"])
        self.assertEqual("contentEmbedding", profiled["retrieval"]["embedding"]["field"])
        self.assertEqual(2, profiled["maxCitationsPerChunk"])

        graph_expansion = build_graph_expansion_options(
            "graphs",
            graph_id="source-graph",
            seed_node_ids=["passage:introduction"],
            profile={"maxDepth": 1, "direction": "outgoing"},
            max_graph_context_chars=800,
            max_graph_provenance_items=8,
        )
        graphrag = build_rag_context_request(
            "grace",
            ["chunks"],
            graph_expansion=graph_expansion,
        )
        self.assertEqual("graphs", graphrag["graphExpansion"]["collection"])
        self.assertEqual("source-graph", graphrag["graphExpansion"]["graphId"])
        self.assertEqual(["passage:introduction"], graphrag["graphExpansion"]["seedNodeIds"])
        self.assertEqual(800, graphrag["graphExpansion"]["maxGraphContextChars"])
        self.assertTrue(graphrag["graphExpansion"]["includeGraphProvenance"])
        self.assertEqual(8, graphrag["graphExpansion"]["maxGraphProvenanceItems"])

    def test_graph_request_builders_validate_shapes(self) -> None:
        span = build_graph_source_span("record:chunk-1", char_start=0, char_end=12)
        node = build_graph_node("chunk:1", "chunk", label="Chunk 1", source_spans=[span])
        edge = build_graph_edge("edge:1", "chunk:1", "topic:retention", "mentions", source_spans=[span])
        assertion = build_graph_assertion("assertion:1", "edge:1", subject_kind="edge", status="accepted")
        review = build_graph_review("review:1", "assertion:1", "accepted", "tester")
        envelope = build_graph_envelope(
            build_graph_scope("g", namespace="tests", collection="chunks", tenant_id="tenant-a"),
            nodes=[node],
            edges=[edge],
            assertions=[assertion],
            reviews=[review],
        )
        stamped = stamp_graph_node_metadata({"id": "chunk-1", "metadata": {"topic": "retention"}}, "chunk:1")

        self.assertEqual("g", envelope["scope"]["graphId"])
        self.assertEqual("record:chunk-1", envelope["nodes"][0]["sourceSpans"][0]["sourceRef"])
        self.assertEqual("edge:1", envelope["assertions"][0]["subjectId"])
        self.assertEqual("chunk:1", stamped["metadata"]["graphNodeId"])
        self.assertEqual("retention", stamped["metadata"]["topic"])

        traversal = build_graph_traversal_request(
            ["node:a"],
            graph_id="g",
            profile={"maxDepth": 2},
            max_records=100,
        )
        self.assertEqual({
            "startNodeIds": ["node:a"],
            "profile": {"maxDepth": 2},
            "allowPartialGraph": False,
            "graphId": "g",
            "maxRecords": 100,
        }, traversal)

        inspection = build_graph_inspection_request(
            graph_id="g",
            include_anomalies=False,
            anomaly_limit=0,
        )
        self.assertFalse(inspection["includeAnomalies"])
        self.assertEqual(0, inspection["anomalyLimit"])
        doctor = build_graph_doctor_request(
            graph_id="g",
            target_collection="chunks",
            target_partition_keys=["tenant-a"],
            seed_json_pointers=["/metadata/graphNodeId"],
            max_target_records=25,
        )
        self.assertEqual("chunks", doctor["targetCollection"])
        self.assertEqual(["tenant-a"], doctor["targetPartitionKeys"])
        self.assertEqual(["/metadata/graphNodeId"], doctor["seedJsonPointers"])
        self.assertEqual(25, doctor["maxTargetRecords"])
        with self.assertRaisesRegex(ValueError, "start_node_ids"):
            build_graph_traversal_request([])
        with self.assertRaisesRegex(ValueError, "anomaly_limit"):
            build_graph_inspection_request(anomaly_limit=-1)

    def test_provider_request_builders_expose_typed_ai_payloads(self) -> None:
        chat = build_provider_chat_request(
            [{"role": "user", "content": "Summarize."}],
            model_id="gpt-5.3-codex-spark",
            timeout_seconds=30,
        )
        self.assertEqual("ai.chat", chat["capability"])
        self.assertEqual("gpt-5.3-codex-spark", chat["modelId"])
        self.assertEqual("Summarize.", chat["payload"]["messages"][0]["content"])

        extract = build_provider_extract_request(
            "OEM manual text",
            schema={"type": "object"},
            instructions="Return product bullets.",
            provider="codex-cli",
            max_output_bytes=4096,
        )
        self.assertEqual("ai.extract", extract["capability"])
        self.assertEqual("codex-cli", extract["provider"])
        self.assertEqual({"type": "object"}, extract["payload"]["schema"])
        self.assertEqual(4096, extract["maxOutputBytes"])

        rerank = build_provider_rerank_request(
            "retention policy",
            [{"id": "a", "text": "travel"}, {"id": "b", "text": "retention policy"}],
            limit=1,
        )
        self.assertEqual("ai.rerank", rerank["capability"])
        self.assertEqual(1, rerank["payload"]["limit"])

        review = build_provider_review_request(
            prompt="Review this copy.",
            references=[{"id": "record:1", "kind": "record"}],
            max_findings=2,
        )
        self.assertEqual("ai.review", review["capability"])
        self.assertEqual(2, review["payload"]["maxFindings"])

        scaffold = build_provider_scaffold_request(
            "Propose artifacts.",
            allowed_paths=["docs/example.md"],
            max_artifacts=1,
        )
        self.assertEqual("ai.scaffold", scaffold["capability"])
        self.assertEqual(["docs/example.md"], scaffold["payload"]["allowedPaths"])

        tool_plan = build_provider_tool_plan_request(
            "Should I call search?",
            [{"name": "search", "description": "Search local records."}],
        )
        self.assertEqual("ai.toolPlan", tool_plan["capability"])
        self.assertEqual("search", tool_plan["payload"]["tools"][0]["name"])

    def test_provider_result_helpers_treat_rejections_as_unusable(self) -> None:
        rejected = {
            "status": "Rejected",
            "output": {
                "data": {"draftCopy": "parsed but not usable"},
                "rejection": {
                    "source": "provider_policy",
                    "parsedOutputDisposition": "quarantine_for_operator_review",
                    "contentUsable": False,
                },
            },
        }
        succeeded = {"status": "Succeeded", "output": {"data": {"draftCopy": "usable"}}}

        self.assertFalse(is_provider_run_succeeded(rejected))
        self.assertFalse(is_provider_run_output_usable(rejected))
        self.assertEqual("provider_policy", get_provider_run_rejection(rejected)["source"])
        self.assertTrue(is_provider_run_succeeded(succeeded))
        self.assertTrue(is_provider_run_output_usable(succeeded))
        self.assertIsNone(get_provider_run_rejection(succeeded))

    def test_create_rag_collection_discovers_embedding_dimensions(self) -> None:
        responses = [
            {"embedding": {"dimensions": 384}},
            {"name": "chunks"},
        ]
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            body = getattr(request, "data", None)
            seen.append({
                "method": getattr(request, "get_method")(),
                "url": getattr(request, "full_url"),
                "body": json.loads(body.decode("utf-8")) if body else None,
            })
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").create_rag_collection("chunks")
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("chunks", result["name"])
        self.assertEqual("http://vyral.local/health", seen[0]["url"])
        self.assertEqual("http://vyral.local/collections", seen[1]["url"])
        create_body = seen[1]["body"]
        self.assertIsInstance(create_body, dict)
        assert isinstance(create_body, dict)
        vector_policy = create_body["vectorPolicies"][0]
        self.assertEqual(384, vector_policy["dimensions"])
        self.assertEqual("/vectors/contentEmbedding/values", vector_policy["path"])

    def test_graph_helpers_use_graph_routes(self) -> None:
        responses = [
            [{"providerId": "vyral-collection"}],
            {"providerId": "vyral-collection", "kind": "vyral_collection"},
            {"collection": "graphs", "recordCount": 2},
            {"collection": "graphs", "readyToImport": True},
            {"collection": "graphs", "envelope": {"scope": {"graphId": "g"}}},
            {"collection": "graphs", "graphId": "g", "nodeCount": 1},
            {"collection": "graphs", "graphId": "g", "traversalReady": True},
            {"collection": "graphs", "status": "ready", "ready": True},
        ]
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            body = getattr(request, "data", None)
            seen.append({
                "method": getattr(request, "get_method")(),
                "url": getattr(request, "full_url"),
                "body": json.loads(body.decode("utf-8")) if body else None,
            })
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            shapes = client.list_graph_provider_shapes()
            shape = client.get_graph_provider_shape("vyral-collection")
            imported = client.import_graph_envelope(
                "graph/name",
                {"scope": {"graphId": "g"}, "nodes": []},
                replace_existing=True,
            )
            preflight = client.preflight_graph_import(
                "graph/name",
                {"scope": {"graphId": "g"}, "nodes": []},
            )
            exported = client.export_graph_envelope("graph/name", graph_id="g", include_projections=False)
            traversed = client.traverse_graph(
                "graph/name",
                ["node:a"],
                graph_id="g",
                profile={"maxDepth": 1},
            )
            inspected = client.inspect_graph(
                "graph/name",
                graph_id="g",
                include_anomalies=False,
                anomaly_limit=5,
            )
            doctor = client.doctor_graph(
                "graph/name",
                graph_id="g",
                target_collection="chunks",
                target_partition_keys=["tenant-a"],
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("vyral-collection", shapes[0]["providerId"])
        self.assertEqual("vyral_collection", shape["kind"])
        self.assertEqual(2, imported["recordCount"])
        self.assertTrue(preflight["readyToImport"])
        self.assertEqual("g", exported["envelope"]["scope"]["graphId"])
        self.assertEqual(1, traversed["nodeCount"])
        self.assertTrue(inspected["traversalReady"])
        self.assertTrue(doctor["ready"])
        self.assertEqual([
            {"method": "GET", "url": "http://vyral.local/graph/provider-shapes", "body": None},
            {"method": "GET", "url": "http://vyral.local/graph/provider-shapes/vyral-collection", "body": None},
            {
                "method": "POST",
                "url": "http://vyral.local/collections/graph%2Fname/graph/import",
                "body": {
                    "envelope": {"scope": {"graphId": "g"}, "nodes": []},
                    "createCollectionIfMissing": True,
                    "replaceExisting": True,
                    "continueOnError": False,
                    "allowNonGraphPolicy": False,
                },
            },
            {
                "method": "POST",
                "url": "http://vyral.local/collections/graph%2Fname/graph/import/preflight",
                "body": {
                    "envelope": {"scope": {"graphId": "g"}, "nodes": []},
                    "createCollectionIfMissing": True,
                    "replaceExisting": False,
                    "continueOnError": False,
                    "allowNonGraphPolicy": False,
                },
            },
            {
                "method": "POST",
                "url": "http://vyral.local/collections/graph%2Fname/graph/export",
                "body": {
                    "includeProjections": False,
                    "failOnLimitExceeded": True,
                    "graphId": "g",
                },
            },
            {
                "method": "POST",
                "url": "http://vyral.local/collections/graph%2Fname/graph/traverse",
                "body": {
                    "startNodeIds": ["node:a"],
                    "profile": {"maxDepth": 1},
                    "allowPartialGraph": False,
                    "graphId": "g",
                },
            },
            {
                "method": "POST",
                "url": "http://vyral.local/collections/graph%2Fname/graph/inspect",
                "body": {
                    "allowPartialGraph": False,
                    "includeAnomalies": False,
                    "anomalyLimit": 5,
                    "graphId": "g",
                },
            },
            {
                "method": "POST",
                "url": "http://vyral.local/collections/graph%2Fname/graph/doctor",
                "body": {
                    "targetPartitionKeys": ["tenant-a"],
                    "maxTargetRecords": 1000,
                    "allowPartialGraph": False,
                    "includeAnomalies": True,
                    "anomalyLimit": 50,
                    "graphId": "g",
                    "targetCollection": "chunks",
                },
            },
        ], seen)

    def test_rag_ingest_helpers_plan_commit_and_compare(self) -> None:
        responses = [
            {
                "planHash": "sha256:plan",
                "manifestHash": "sha256:manifest",
                "chunks": [{"id": "chunk-1", "action": "created", "embeddingAction": "generated"}],
                "staleDeletes": [{"id": "stale-1"}],
            },
            {
                "planHash": "sha256:plan",
                "manifestHash": "sha256:manifest",
                "actionSummary": {
                    "actionCounts": {"created": 1},
                    "embeddingActionCounts": {"generated": 1},
                    "createdIds": ["chunk-1"],
                    "updatedIds": [],
                    "reusedIds": [],
                    "deduplicatedIds": [],
                    "staleDeleteIds": ["stale-1"],
                },
            },
        ]
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen.append(json.loads(getattr(request, "data").decode("utf-8")))
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            planned = client.plan_rag_text_ingestion("chunks", {"partitionKey": "tenant-a", "text": "alpha"})
            committed = client.commit_rag_text_ingestion(
                "chunks",
                {"partitionKey": "tenant-a", "text": "alpha"},
                planned,
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertTrue(seen[0]["options"]["dryRun"])
        self.assertFalse(seen[1]["options"]["dryRun"])
        self.assertEqual("sha256:plan", seen[1]["options"]["expectedPlanHash"])
        self.assertEqual("sha256:manifest", seen[1]["options"]["expectedManifestHash"])
        self.assertEqual(["chunk-1"], summarize_rag_ingest_result(planned)["createdIds"])

        comparison = compare_rag_ingest_results(planned, committed)
        self.assertEqual("matched", comparison["planHash"]["status"])
        self.assertTrue(comparison["planHash"]["matches"])
        self.assertEqual(["stale-1"], comparison["committedSummary"]["staleDeleteIds"])

    def test_api_key_is_sent_on_requests(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["headers"] = dict(getattr(request, "header_items")())
            seen["redirectable_headers"] = dict(getattr(request, "headers"))
            return FakeResponse(json.dumps({"status": "ok"}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            VyralClient("https://vyral.local", api_key="secret").health()
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("secret", seen["headers"]["X-vyral-api-key"])
        self.assertNotIn("X-vyral-api-key", seen["redirectable_headers"])

    def test_shared_transport_options_retry_only_safe_or_idempotent_requests(self) -> None:
        calls: list[dict[str, object]] = []
        responses: list[object] = [
            client_module.HTTPError(
                "http://vyral.local/health",
                503,
                "Unavailable",
                hdrs={"Retry-After": "0"},
                fp=io.BytesIO(b'{"title":"Unavailable","status":503}'),
            ),
            FakeResponse(b'{"status":"ok"}'),
            client_module.URLError("connection reset"),
            client_module.HTTPError(
                "http://vyral.local/collections/events/import/jobs",
                503,
                "Unavailable",
                hdrs={"Retry-After": "0"},
                fp=io.BytesIO(b'{"title":"Unavailable","status":503}'),
            ),
            FakeResponse(b'{"id":"import-1"}'),
        ]
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            calls.append({
                "method": getattr(request, "get_method")(),
                "headers": dict(getattr(request, "header_items")()),
                "timeout": timeout,
            })
            response = responses.pop(0)
            if isinstance(response, BaseException):
                raise response
            assert isinstance(response, FakeResponse)
            return response

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient(
                "https://vyral.local",
                bearer_token="token",
                default_headers={"X-Client": "python"},
                max_retries=1,
                retry_backoff_seconds=0,
            ).with_options(timeout=4, correlation_id="corr-1", headers={"X-Scope": "sdk"})
            self.assertEqual("ok", client.health()["status"])
            with self.assertRaises(VyralClientError) as unsafe:
                client.start_execution_run({"handlerId": "consumer"})
            self.assertTrue(unsafe.exception.is_transient())
            self.assertEqual(
                "import-1",
                client.start_collection_import_job(
                    "events",
                    {"schema": "vyral.collection-snapshot.v1", "records": []},
                    idempotency_key="import-1",
                )["id"],
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual([], responses)
        self.assertEqual(["GET", "GET", "POST", "POST", "POST"], [call["method"] for call in calls])
        self.assertEqual([4, 4, 4, 4, 4], [call["timeout"] for call in calls])
        for call in calls:
            headers = call["headers"]
            self.assertEqual("Bearer token", headers["Authorization"])
            self.assertEqual("python", headers["X-client"])
            self.assertEqual("sdk", headers["X-scope"])
            self.assertEqual("corr-1", headers["X-correlation-id"])

    def test_credentials_require_https_except_on_loopback(self) -> None:
        for base_url in ("http://vyral.example", "ftp://vyral.example", "https://user:password@vyral.example"):
            with self.subTest(base_url=base_url):
                with self.assertRaises(ValueError):
                    VyralClient(base_url, api_key="secret")

        VyralClient("http://127.0.0.1:5220", api_key="secret")
        VyralClient("http://[::1]:5220", bearer_token="token")

        client = VyralClient("http://vyral.example")
        with self.assertRaises(ValueError):
            client._request("GET", "/health", headers={"Authorization": "Bearer token"})

    def test_shared_transport_cancellation_fails_before_network_io(self) -> None:
        called = False
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            nonlocal called
            del request, timeout
            called = True
            return FakeResponse(b"{}")

        client_module.urlopen = fake_urlopen
        try:
            with self.assertRaises(VyralClientError) as raised:
                VyralClient("http://vyral.local", cancellation_check=lambda: True).health()
        finally:
            client_module.urlopen = original_urlopen

        self.assertFalse(called)
        self.assertTrue(raised.exception.is_cancelled())

    def test_problem_json_errors_expose_details_and_predicates(self) -> None:
        problem = {
            "type": "https://vyral.local/problems/collection-not-found",
            "title": "Collection not found",
            "status": 404,
            "detail": "Collection 'missing' does not exist.",
            "instance": "/collections/missing/query",
        }
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del request, timeout
            raise client_module.HTTPError(
                "http://vyral.local/collections/missing/query",
                404,
                "Not Found",
                hdrs={"Retry-After": "3", "X-Correlation-ID": "corr-error"},
                fp=io.BytesIO(json.dumps(problem).encode("utf-8")),
            )

        client_module.urlopen = fake_urlopen
        try:
            with self.assertRaises(VyralClientError) as raised:
                VyralClient("http://vyral.local").query_records("missing", {"limit": 1})
        finally:
            client_module.urlopen = original_urlopen

        error = raised.exception
        self.assertEqual(404, error.status)
        self.assertEqual(problem, error.problem)
        self.assertEqual("Collection not found", error.title)
        self.assertEqual("Collection 'missing' does not exist.", error.detail)
        self.assertEqual("/collections/missing/query", error.instance)
        self.assertEqual(404, error.problem_status)
        self.assertIn("Collection not found", str(error))
        self.assertTrue(error.is_missing_collection())
        self.assertFalse(error.is_auth_error())
        self.assertFalse(error.is_validation_error())
        self.assertEqual("3", error.retry_after)
        self.assertEqual("corr-error", error.correlation_id)

    def test_client_error_helpers_classify_auth_validation_and_timeout(self) -> None:
        self.assertTrue(VyralClientError(401, '{"title":"Unauthorized"}').is_auth_error())
        self.assertTrue(VyralClientError(403, '{"title":"Forbidden"}').is_auth_error())
        self.assertTrue(VyralClientError(400, '{"title":"Invalid request"}').is_validation_error())
        self.assertTrue(VyralClientError(422, '{"title":"Unprocessable"}').is_validation_error())

        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del request, timeout
            raise TimeoutError("timed out")

        client_module.urlopen = fake_urlopen
        try:
            with self.assertRaises(VyralClientError) as raised:
                VyralClient("http://vyral.local", timeout=0.1).health()
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual(0, raised.exception.status)
        self.assertTrue(raised.exception.is_timeout())
        self.assertEqual("timeout", raised.exception.failure_class)

    def test_provider_helpers_use_provider_routes(self) -> None:
        responses = [
            [{"id": "local-deterministic-ai"}],
            {"items": [{"provider": "local-deterministic-ai", "capabilities": {"ai.chat": {"supported": True}}}]},
            {"profile": {"id": "local-deterministic-ai"}, "capabilities": []},
            [{"provider": "local-deterministic-ai", "status": "warning", "checks": []}],
            {"provider": "local-deterministic-ai", "status": "warning", "checks": []},
            {"provider": "local-deterministic-ai", "status": "succeeded", "items": [{"id": "local-deterministic-ai", "default": True}]},
            {"items": [{"provider": "local-deterministic-ai", "capability": "ai.chat", "ready": False}]},
            {"items": [{"provider": "local-deterministic-ai", "capability": "ai.chat", "ready": False}]},
            [{"provider": "local-deterministic-ai", "status": "unsupported", "items": []}],
            {"provider": "local-deterministic-ai", "status": "unsupported", "items": []},
            [{"provider": "local-deterministic-ai", "capability": "ai.chat", "status": "unvalidated"}],
            [{"provider": "local-deterministic-ai", "capability": "ai.chat", "status": "validated"}],
            {"status": "Succeeded", "provider": "local-deterministic-ai"},
        ]
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen.append({
                "method": getattr(request, "get_method")(),
                "url": getattr(request, "full_url"),
                "body": json.loads(getattr(request, "data").decode("utf-8")) if getattr(request, "data", None) else None,
            })
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            providers = client.list_providers()
            matrix = client.get_provider_capability_matrix()
            provider = client.get_provider("local-deterministic-ai")
            doctor = client.list_provider_doctor()
            provider_doctor = client.get_provider_doctor("local-deterministic-ai")
            models = client.list_provider_models("local-deterministic-ai")
            readiness = client.list_provider_readiness()
            provider_readiness = client.get_provider_readiness("local-deterministic-ai")
            quotas = client.list_provider_quotas()
            provider_quota = client.get_provider_quota("local-deterministic-ai")
            qualifications = client.list_provider_qualifications("local-deterministic-ai")
            qualified = client.qualify_provider("local-deterministic-ai", {"capability": "ai.chat"})
            result = client.run_provider("local-deterministic-ai", {
                "capability": "ai.chat",
                "operation": "run",
                "payload": {"messages": [{"role": "user", "content": "hello"}]},
            })
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("local-deterministic-ai", providers[0]["id"])
        self.assertTrue(matrix["items"][0]["capabilities"]["ai.chat"]["supported"])
        self.assertEqual("local-deterministic-ai", provider["profile"]["id"])
        self.assertEqual("warning", doctor[0]["status"])
        self.assertEqual("warning", provider_doctor["status"])
        self.assertEqual("succeeded", models["status"])
        self.assertEqual("local-deterministic-ai", models["items"][0]["id"])
        self.assertFalse(readiness["items"][0]["ready"])
        self.assertFalse(provider_readiness["items"][0]["ready"])
        self.assertEqual("unsupported", quotas[0]["status"])
        self.assertEqual("unsupported", provider_quota["status"])
        self.assertEqual("unvalidated", qualifications[0]["status"])
        self.assertEqual("validated", qualified[0]["status"])
        self.assertEqual("Succeeded", result["status"])
        self.assertEqual([
            {"method": "GET", "url": "http://vyral.local/providers", "body": None},
            {"method": "GET", "url": "http://vyral.local/providers/capabilities", "body": None},
            {"method": "GET", "url": "http://vyral.local/providers/local-deterministic-ai", "body": None},
            {"method": "GET", "url": "http://vyral.local/providers/doctor", "body": None},
            {"method": "GET", "url": "http://vyral.local/providers/local-deterministic-ai/doctor", "body": None},
            {"method": "GET", "url": "http://vyral.local/providers/local-deterministic-ai/models", "body": None},
            {"method": "GET", "url": "http://vyral.local/providers/readiness", "body": None},
            {"method": "GET", "url": "http://vyral.local/providers/local-deterministic-ai/readiness", "body": None},
            {"method": "GET", "url": "http://vyral.local/providers/quotas", "body": None},
            {"method": "GET", "url": "http://vyral.local/providers/local-deterministic-ai/quota", "body": None},
            {"method": "GET", "url": "http://vyral.local/providers/local-deterministic-ai/qualifications", "body": None},
            {
                "method": "POST",
                "url": "http://vyral.local/providers/local-deterministic-ai/qualify",
                "body": {"capability": "ai.chat"},
            },
            {
                "method": "POST",
                "url": "http://vyral.local/providers/local-deterministic-ai/run",
                "body": {
                    "capability": "ai.chat",
                    "operation": "run",
                    "payload": {"messages": [{"role": "user", "content": "hello"}]},
                },
            },
        ], seen)

    def test_upsert_records_posts_batch_request(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({
                "collection": "chunks",
                "requested": 2,
                "attempted": 2,
                "succeeded": 2,
                "failed": 0,
                "items": [
                    {"index": 0, "id": "a", "partitionKey": "tenant-a", "status": "succeeded"},
                    {"index": 1, "id": "b", "partitionKey": "tenant-a", "status": "succeeded"},
                ],
            }).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").upsert_records(
                "chunks",
                [{"id": "a", "partitionKey": "tenant-a"}, {"id": "b", "partitionKey": "tenant-a"}],
                continue_on_error=True,
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/collections/chunks/records/batch", seen["url"])
        self.assertEqual({
            "records": [{"id": "a", "partitionKey": "tenant-a"}, {"id": "b", "partitionKey": "tenant-a"}],
            "continueOnError": True,
        }, seen["body"])
        self.assertEqual(2, result["succeeded"])

    def test_record_import_jobs_preserve_raw_request_and_idempotency_key(self) -> None:
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen.append({
                "method": getattr(request, "get_method")(),
                "url": getattr(request, "full_url"),
                "body": json.loads(getattr(request, "data").decode("utf-8")) if getattr(request, "data") else None,
                "headers": dict(getattr(request, "header_items")()),
            })
            url = getattr(request, "full_url")
            if "/collections/chunks/records/batch/jobs" in url:
                return FakeResponse(json.dumps({"id": "job-1", "kind": "batch_upsert", "status": "queued"}).encode("utf-8"))
            if url.endswith("/record-import/jobs?includeResult=true&limit=3"):
                return FakeResponse(json.dumps([{"id": "job-1", "status": "succeeded"}]).encode("utf-8"))
            return FakeResponse(json.dumps({"id": "job-1", "status": "succeeded"}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            started = client.start_record_batch_upsert_job(
                "chunks",
                [{"id": "a", "partitionKey": "tenant-a"}],
                preconditions=[{"ifNoneMatch": "*"}],
                continue_on_error=True,
                idempotency_key="record-import-1",
                product_id="product-a",
                tenant_id="tenant-a",
            )
            listed = client.list_record_import_jobs(limit=3, include_result=True)
            completed = client.get_record_import_job("job-1")
            cancelled = client.cancel_record_import_job("job-1")
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("job-1", started["id"])
        self.assertEqual("succeeded", listed[0]["status"])
        self.assertEqual("succeeded", completed["status"])
        self.assertEqual("succeeded", cancelled["status"])
        self.assertEqual(
            "http://vyral.local/collections/chunks/records/batch/jobs?productId=product-a&tenantId=tenant-a",
            seen[0]["url"],
        )
        self.assertEqual("record-import-1", seen[0]["headers"].get("Idempotency-key"))
        self.assertEqual([{"ifNoneMatch": "*"}], seen[0]["body"]["preconditions"])
        self.assertEqual("http://vyral.local/record-import/jobs?includeResult=true&limit=3", seen[1]["url"])
        self.assertEqual("GET", seen[2]["method"])
        self.assertEqual("DELETE", seen[3]["method"])

    def test_search_all_records_drains_continuation_tokens(self) -> None:
        responses = [
            {"items": [{"record": {"id": "a"}, "score": 1.0}], "continuationToken": "next"},
            {"items": [{"record": {"id": "b"}, "score": 0.5}], "continuationToken": None},
        ]
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del request, timeout
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            items = VyralClient("http://vyral.local").search_all_records("chunks", {"limit": 1})
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual(["a", "b"], [item["record"]["id"] for item in items])

    def test_evaluate_retrieval_posts_evaluation_request(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({
                "requested": 1,
                "attempted": 1,
                "succeeded": 1,
                "failed": 0,
                "hitCount": 1,
                "hitRate": 1.0,
                "cases": [{"index": 0, "status": "succeeded", "hit": True}],
            }).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").evaluate_retrieval({
                "cases": [
                    {
                        "name": "retention",
                        "request": {"query": "retention", "collections": ["chunks"], "limit": 3},
                        "expected": [{"id": "chunk-1"}],
                    }
                ]
            })
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/retrieval/evaluate", seen["url"])
        self.assertEqual({
            "cases": [
                {
                    "name": "retention",
                    "request": {"query": "retention", "collections": ["chunks"], "limit": 3},
                    "expected": [{"id": "chunk-1"}],
                }
            ]
        }, seen["body"])
        self.assertEqual(1, result["hitCount"])

    def test_compare_retrieval_evaluations_posts_comparison_request(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({
                "requested": 1,
                "variantsRequested": 2,
                "variantsAttempted": 2,
                "variantsSucceeded": 2,
                "variantsFailed": 0,
                "baselineVariantId": "evidence",
                "variants": [
                    {"id": "evidence", "status": "succeeded", "metrics": {"hitRate": 1.0}},
                    {
                        "id": "rerank",
                        "status": "succeeded",
                        "metrics": {"hitRate": 1.0},
                        "deltaFromBaseline": {"hitRate": 0.0},
                    },
                ],
            }).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").compare_retrieval_evaluations({
                "cases": [
                    {
                        "name": "retention",
                        "request": {"query": "retention", "collections": ["chunks"], "limit": 3},
                        "expected": [{"id": "chunk-1"}],
                    }
                ],
                "variants": [
                    {"id": "evidence", "profile": "evidence"},
                    {"id": "rerank", "profile": "rerankPolish"},
                ],
            })
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/retrieval/evaluate/compare", seen["url"])
        self.assertEqual({
            "cases": [
                {
                    "name": "retention",
                    "request": {"query": "retention", "collections": ["chunks"], "limit": 3},
                    "expected": [{"id": "chunk-1"}],
                }
            ],
            "variants": [
                {"id": "evidence", "profile": "evidence"},
                {"id": "rerank", "profile": "rerankPolish"},
            ],
        }, seen["body"])
        self.assertEqual(2, result["variantsRequested"])

    def test_retrieval_evaluation_job_helpers_use_job_routes(self) -> None:
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            method = getattr(request, "get_method")()
            url = getattr(request, "full_url")
            body = getattr(request, "data", None)
            seen.append({
                "method": method,
                "url": url,
                "body": json.loads(body.decode("utf-8")) if body else None,
            })
            if method == "GET" and url.endswith("/retrieval/evaluate/jobs/eval-1"):
                return FakeResponse(json.dumps({"id": "eval-1", "status": "succeeded", "progress": 1.0}).encode("utf-8"))
            if method == "GET":
                return FakeResponse(json.dumps([{"id": "eval-1", "status": "running"}]).encode("utf-8"))
            return FakeResponse(json.dumps({"id": "eval-1", "status": "queued", "progress": 0.0}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            started_eval = client.start_retrieval_evaluation_job({"cases": []})
            started = client.start_retrieval_evaluation_comparison_job({"cases": [], "variants": []})
            listed = client.list_retrieval_evaluation_jobs(limit=5, include_result=True)
            fetched = client.get_retrieval_evaluation_job("eval-1")
            cancelled = client.cancel_retrieval_evaluation_job("eval-1")
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("eval-1", started_eval["id"])
        self.assertEqual("eval-1", started["id"])
        self.assertEqual("eval-1", listed[0]["id"])
        self.assertEqual("succeeded", fetched["status"])
        self.assertEqual("queued", cancelled["status"])
        self.assertEqual([
            {
                "method": "POST",
                "url": "http://vyral.local/retrieval/evaluate/jobs",
                "body": {"cases": []},
            },
            {
                "method": "POST",
                "url": "http://vyral.local/retrieval/evaluate/compare/jobs",
                "body": {"cases": [], "variants": []},
            },
            {
                "method": "GET",
                "url": "http://vyral.local/retrieval/evaluate/jobs?limit=5&includeResult=true",
                "body": None,
            },
            {
                "method": "GET",
                "url": "http://vyral.local/retrieval/evaluate/jobs/eval-1",
                "body": None,
            },
            {
                "method": "DELETE",
                "url": "http://vyral.local/retrieval/evaluate/jobs/eval-1",
                "body": None,
            },
        ], seen)

    def test_wait_retrieval_evaluation_job_polls_until_terminal_status(self) -> None:
        responses = [
            {"id": "eval-1", "status": "running"},
            {"id": "eval-1", "status": "succeeded"},
        ]
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del request, timeout
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").wait_retrieval_evaluation_job(
                "eval-1",
                timeout_seconds=1,
                poll_interval_seconds=0,
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("succeeded", result["status"])
        self.assertEqual([], responses)

    def test_list_retrieval_profiles_gets_profile_catalog(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            return FakeResponse(json.dumps([{"id": "evidence"}]).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").list_retrieval_profiles()
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/retrieval/profiles", seen["url"])
        self.assertEqual("evidence", result[0]["id"])

    def test_build_rag_context_posts_context_request(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({"chunks": [{"id": "chunk-1"}]}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").build_rag_context({"query": "retention", "collections": ["chunks"]})
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/rag/context", seen["url"])
        self.assertEqual({"query": "retention", "collections": ["chunks"]}, seen["body"])
        self.assertEqual("chunk-1", result["chunks"][0]["id"])

    def test_evaluate_rag_context_posts_evaluation_request(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({
                "requested": 1,
                "attempted": 1,
                "succeeded": 1,
                "passedCount": 1,
                "passRate": 1.0,
                "cases": [{"index": 0, "status": "succeeded", "passed": True}],
            }).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").evaluate_rag_context({
                "cases": [
                    {
                        "name": "retention",
                        "request": {"retrieval": {"query": "retention", "collections": ["chunks"]}},
                        "expectedGraph": {"nodeIds": ["node:a"]},
                    }
                ]
            })
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/rag/context/evaluate", seen["url"])
        self.assertEqual({
            "cases": [
                {
                    "name": "retention",
                    "request": {"retrieval": {"query": "retention", "collections": ["chunks"]}},
                    "expectedGraph": {"nodeIds": ["node:a"]},
                }
            ]
        }, seen["body"])
        self.assertEqual(1, result["passedCount"])

    def test_build_rag_prompt_posts_prompt_request(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({"prompt": "SYSTEM:\n...", "promptHash": "sha256:abc"}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").build_rag_prompt({
                "context": {"query": "retention", "collections": ["chunks"]},
                "template": {"failOnEmptyContext": True},
            })
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/rag/prompt", seen["url"])
        self.assertEqual({
            "context": {"query": "retention", "collections": ["chunks"]},
            "template": {"failOnEmptyContext": True},
        }, seen["body"])
        self.assertEqual("sha256:abc", result["promptHash"])

    def test_ingest_rag_text_posts_collection_ingestion_request(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({"documentId": "doc-1", "chunkCount": 2}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").ingest_rag_text("chunks", {
                "documentId": "doc-1",
                "partitionKey": "tenant-a",
                "text": "alpha beta gamma",
            })
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/collections/chunks/rag/ingest-text", seen["url"])
        self.assertEqual({
            "documentId": "doc-1",
            "partitionKey": "tenant-a",
            "text": "alpha beta gamma",
        }, seen["body"])
        self.assertEqual(2, result["chunkCount"])

    def test_ingest_rag_texts_posts_collection_batch_ingestion_request(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({
                "collection": "chunks",
                "requested": 2,
                "attempted": 2,
                "succeeded": 2,
                "failed": 0,
                "items": [],
            }).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").ingest_rag_texts(
                "chunks",
                [
                    {"documentId": "doc-1", "partitionKey": "tenant-a", "text": "alpha"},
                    {"documentId": "doc-2", "partitionKey": "tenant-a", "text": "beta"},
                ],
                continue_on_error=True,
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/collections/chunks/rag/ingest-text/batch", seen["url"])
        self.assertEqual({
            "items": [
                {"documentId": "doc-1", "partitionKey": "tenant-a", "text": "alpha"},
                {"documentId": "doc-2", "partitionKey": "tenant-a", "text": "beta"},
            ],
            "continueOnError": True,
        }, seen["body"])
        self.assertEqual(2, result["succeeded"])

    def test_prune_traces_posts_prune_request(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({"matchedCount": 3, "deletedCount": 0}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").prune_traces({
                "operation": "provider.run",
                "keepLatest": 10,
                "dryRun": True,
            })
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/traces/prune", seen["url"])
        self.assertEqual({
            "operation": "provider.run",
            "keepLatest": 10,
            "dryRun": True,
        }, seen["body"])
        self.assertEqual(3, result["matchedCount"])

    def test_export_traces_posts_export_request(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            seen["body"] = json.loads(getattr(request, "data").decode("utf-8"))
            return FakeResponse(json.dumps({
                "formatVersion": "vyral.trace-export.v1",
                "traceCount": 1,
                "contentHash": "sha256:abc",
            }).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").export_traces({
                "operation": "provider.run",
                "limit": 10,
                "failOnUnsafeContent": True,
            })
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/traces/export", seen["url"])
        self.assertEqual({
            "operation": "provider.run",
            "limit": 10,
            "failOnUnsafeContent": True,
        }, seen["body"])
        self.assertEqual(1, result["traceCount"])

    def test_summarize_traces_gets_summary(self) -> None:
        seen: dict[str, object] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = getattr(request, "full_url")
            return FakeResponse(json.dumps({
                "totalCount": 1,
                "operations": [{"operation": "provider.run", "count": 1}],
            }).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").summarize_traces("provider.run")
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("http://vyral.local/traces/summary?operation=provider.run", seen["url"])
        self.assertEqual(1, result["totalCount"])

    def test_provider_job_helpers_use_job_routes(self) -> None:
        seen: list[dict[str, object]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            method = getattr(request, "get_method")()
            url = getattr(request, "full_url")
            body = getattr(request, "data", None)
            seen.append({
                "method": method,
                "url": url,
                "body": json.loads(body.decode("utf-8")) if body else None,
            })
            if method == "GET" and url.endswith("/provider-jobs/job-1"):
                return FakeResponse(json.dumps({"id": "job-1", "status": "succeeded"}).encode("utf-8"))
            if method == "GET":
                return FakeResponse(json.dumps([{"id": "job-1"}]).encode("utf-8"))
            return FakeResponse(json.dumps({"id": "job-1", "status": "queued"}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            started = client.start_provider_job("local-deterministic-ai", {"capability": "ai.chat"})
            listed = client.list_provider_jobs("local-deterministic-ai", limit=5, include_result=True)
            fetched = client.get_provider_job("job-1")
            cancelled = client.cancel_provider_job("job-1")
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("job-1", started["id"])
        self.assertEqual("job-1", listed[0]["id"])
        self.assertEqual("succeeded", fetched["status"])
        self.assertEqual("queued", cancelled["status"])
        self.assertEqual([
            {
                "method": "POST",
                "url": "http://vyral.local/providers/local-deterministic-ai/jobs",
                "body": {"capability": "ai.chat"},
            },
            {
                "method": "GET",
                "url": "http://vyral.local/provider-jobs?provider=local-deterministic-ai&limit=5&includeResult=true",
                "body": None,
            },
            {
                "method": "GET",
                "url": "http://vyral.local/provider-jobs/job-1",
                "body": None,
            },
            {
                "method": "DELETE",
                "url": "http://vyral.local/provider-jobs/job-1",
                "body": None,
            },
        ], seen)

    def test_wait_provider_job_polls_until_terminal_status(self) -> None:
        responses = [
            {"id": "job-1", "status": "running"},
            {"id": "job-1", "status": "succeeded"},
        ]
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del request, timeout
            return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").wait_provider_job(
                "job-1",
                timeout_seconds=1,
                poll_interval_seconds=0,
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("succeeded", result["status"])
        self.assertEqual([], responses)

    def test_canonical_preflight_and_effective_execution_discovery_use_safe_routes(self) -> None:
        seen: list[dict[str, str]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen.append({"method": getattr(request, "get_method")(), "url": getattr(request, "full_url")})
            return FakeResponse(json.dumps({"ok": True}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            self.assertTrue(client.get_canonical_preflight()["ok"])
            self.assertTrue(client.probe_canonical_data_plane()["ok"])
            self.assertTrue(client.get_effective_execution_runtime(product_id="product/a", tenant_id="tenant a")["ok"])
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual([
            {"method": "GET", "url": "http://vyral.local/canonical/preflight"},
            {"method": "POST", "url": "http://vyral.local/canonical/preflight/probe"},
            {"method": "GET", "url": "http://vyral.local/execution/runtime/effective?productId=product%2Fa&tenantId=tenant+a"},
        ], seen)

    def test_graph_and_rag_ingestion_job_helpers_preserve_payloads_idempotency_and_polling_routes(self) -> None:
        seen: list[dict[str, object]] = []
        graph_gets = 0
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            nonlocal graph_gets
            del timeout
            method = getattr(request, "get_method")()
            url = getattr(request, "full_url")
            body = getattr(request, "data")
            seen.append({
                "method": method,
                "url": url,
                "body": json.loads(body.decode("utf-8")) if body else None,
                "headers": dict(getattr(request, "header_items")()),
            })
            if url.endswith("/graph/jobs/graph-1") and method == "GET":
                graph_gets += 1
                status = "running" if graph_gets == 1 else "succeeded"
                return FakeResponse(json.dumps({"id": "graph-1", "status": status}).encode("utf-8"))
            if url.endswith("/rag/ingestion/jobs/rag-1") and method == "GET":
                return FakeResponse(json.dumps({"id": "rag-1", "status": "succeeded"}).encode("utf-8"))
            if url.endswith("/record-import/jobs/record-1") and method == "GET":
                return FakeResponse(json.dumps({"id": "record-1", "status": "succeeded"}).encode("utf-8"))
            if method == "GET":
                return FakeResponse(json.dumps([{"id": "job-1", "status": "queued"}]).encode("utf-8"))
            return FakeResponse(json.dumps({"id": "job-1", "status": "queued"}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            client.start_graph_import_job("graphs", {"envelope": {"graph": {"id": "g"}}}, idempotency_key="graph-import-1")
            client.start_graph_inspection_job("graphs", {"graphId": "g"}, idempotency_key="graph-inspect-1")
            client.start_graph_doctor_job("graphs", {"graphId": "g"}, idempotency_key="graph-doctor-1")
            client.list_graph_jobs(limit=3, include_result=True)
            graph = client.wait_graph_job("graph-1", timeout_seconds=1, poll_interval_seconds=0)
            client.cancel_graph_job("graph-1")

            client.start_rag_text_ingestion_job(
                "chunks",
                {"documentId": "doc-1", "partitionKey": "tenant-a", "text": "alpha"},
                idempotency_key="rag-text-1",
            )
            client.start_rag_text_batch_ingestion_job(
                "chunks",
                {"items": [], "continueOnError": True},
                idempotency_key="rag-batch-1",
            )
            client.list_rag_ingestion_jobs(limit=2, include_result=False)
            rag = client.wait_rag_ingestion_job("rag-1", timeout_seconds=1, poll_interval_seconds=0)
            client.cancel_rag_ingestion_job("rag-1")
            record = client.wait_record_import_job("record-1", timeout_seconds=1, poll_interval_seconds=0)
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("succeeded", graph["status"])
        self.assertEqual("succeeded", rag["status"])
        self.assertEqual("succeeded", record["status"])
        self.assertEqual("http://vyral.local/collections/graphs/graph/import/jobs", seen[0]["url"])
        self.assertEqual("graph-import-1", seen[0]["headers"].get("Idempotency-key"))
        self.assertEqual({"envelope": {"graph": {"id": "g"}}}, seen[0]["body"])
        self.assertEqual("http://vyral.local/collections/graphs/graph/inspect/jobs", seen[1]["url"])
        self.assertEqual("graph-inspect-1", seen[1]["headers"].get("Idempotency-key"))
        self.assertEqual("http://vyral.local/collections/graphs/graph/doctor/jobs", seen[2]["url"])
        self.assertEqual("graph-doctor-1", seen[2]["headers"].get("Idempotency-key"))
        self.assertEqual("http://vyral.local/graph/jobs?limit=3&includeResult=true", seen[3]["url"])
        self.assertEqual(2, len([item for item in seen if item["url"].endswith("/graph/jobs/graph-1") and item["method"] == "GET"]))
        self.assertTrue(any(item["url"].endswith("/graph/jobs/graph-1") and item["method"] == "DELETE" for item in seen))
        self.assertTrue(any(item["url"].endswith("/collections/chunks/rag/ingest-text/jobs") and item["headers"].get("Idempotency-key") == "rag-text-1" for item in seen))
        self.assertTrue(any(item["url"].endswith("/collections/chunks/rag/ingest-text/batch/jobs") and item["headers"].get("Idempotency-key") == "rag-batch-1" for item in seen))
        self.assertTrue(any(item["url"].endswith("/rag/ingestion/jobs?limit=2&includeResult=false") for item in seen))
        self.assertTrue(any(item["url"].endswith("/rag/ingestion/jobs/rag-1") and item["method"] == "DELETE" for item in seen))

    def test_delete_collection_uses_delete_route(self) -> None:
        seen: dict[str, str] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["method"] = getattr(request, "get_method")()
            seen["url"] = getattr(request, "full_url")
            return FakeResponse(b"")

        client_module.urlopen = fake_urlopen
        try:
            VyralClient("http://vyral.local").delete_collection("chunk/name")
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("DELETE", seen["method"])
        self.assertEqual("http://vyral.local/collections/chunk%2Fname", seen["url"])

    def test_inspect_collection_uses_inspection_route_with_options(self) -> None:
        seen: dict[str, str] = {}
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["method"] = getattr(request, "get_method")()
            seen["url"] = getattr(request, "full_url")
            return FakeResponse(json.dumps({"collection": "chunks", "recordCount": 2}).encode("utf-8"))

        client_module.urlopen = fake_urlopen
        try:
            result = VyralClient("http://vyral.local").inspect_collection(
                "chunk/name",
                include_anomalies=False,
                anomaly_limit=10,
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual("GET", seen["method"])
        self.assertEqual(
            "http://vyral.local/collections/chunk%2Fname/inspect?includeAnomalies=false&anomalyLimit=10",
            seen["url"],
        )
        self.assertEqual(2, result["recordCount"])

    def test_all_receipt_bound_start_helpers_forward_idempotency_headers(self) -> None:
        seen: list[dict[str, str]] = []
        original_urlopen = client_module.urlopen

        def fake_urlopen(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen.append({
                name.lower(): value
                for name, value in dict(getattr(request, "headers")).items()
            })
            return FakeResponse(b'{"id":"admitted-1"}')

        client_module.urlopen = fake_urlopen
        try:
            client = VyralClient("http://vyral.local")
            client.start_execution_run(
                {"handlerId": "test.handler"},
                idempotency_key="execution-1",
            )
            client.start_embedding_job(
                {"texts": ["alpha"]},
                idempotency_key="embedding-1",
            )
            client.start_provider_job(
                "provider-1",
                {"capability": "ai.chat"},
                idempotency_key="provider-1",
            )
            client.run_provider(
                "provider-1",
                {"capability": "ai.chat"},
                idempotency_key="provider-alias-1",
            )
            client.start_retrieval_evaluation_job(
                {"cases": []},
                idempotency_key="evaluation-1",
            )
            client.start_retrieval_evaluation_comparison_job(
                {"cases": [], "variants": []},
                idempotency_key="comparison-1",
            )
            client.import_collection(
                "records", {}, idempotency_key="import-1"
            )
            client.import_graph_envelope(
                "graph", {}, idempotency_key="graph-1"
            )
            client.upsert_records(
                "records", [], idempotency_key="batch-1"
            )
            client.ingest_rag_text(
                "chunks", {}, idempotency_key="rag-text-1"
            )
            client.ingest_rag_texts(
                "chunks", [], idempotency_key="rag-batch-1"
            )
            client.create_collection(
                {"name": "records"},
                idempotency_key="collection-create-1",
            )
            client.delete_collection(
                "records",
                idempotency_key="collection-delete-1",
            )
        finally:
            client_module.urlopen = original_urlopen

        self.assertEqual(
            [
                "execution-1",
                "embedding-1",
                "provider-1",
                "provider-alias-1",
                "evaluation-1",
                "comparison-1",
                "import-1",
                "graph-1",
                "batch-1",
                "rag-text-1",
                "rag-batch-1",
                "collection-create-1",
                "collection-delete-1",
            ],
            [headers["idempotency-key"] for headers in seen],
        )

    def test_rejected_admission_is_available_on_client_error(self) -> None:
        error = VyralClientError(
            429,
            json.dumps({
                "admission": {
                    "status": "rejected",
                    "failureClass": "queue_full",
                    "resourceId": "run-1",
                }
            }),
        )

        self.assertEqual("run-1", error.admission["resourceId"])
        self.assertEqual("queue_full", error.failure_class)

    def test_ai_metering_fixture_has_portable_canonical_hashes(self) -> None:
        root = pathlib.Path(__file__).resolve().parents[3]
        fixture = json.loads(
            (root / "conformance/ai-metering/v1/receipt.json").read_text(encoding="utf-8")
        )
        review = json.loads(
            (root / "conformance/ai-metering/v1/review.json").read_text(encoding="utf-8")
        )
        manifest = json.loads(
            (root / "conformance/ai-metering/v1/manifest.json").read_text(encoding="utf-8")
        )

        def canonical_hash(value: object) -> str:
            encoded = json.dumps(
                value,
                ensure_ascii=False,
                sort_keys=True,
                separators=(",", ":"),
            ).encode("utf-8")
            return "sha256:" + hashlib.sha256(encoded).hexdigest()

        payload = dict(fixture)
        payload.pop("integrity", None)
        self.assertEqual(manifest["expectedPayloadHash"], canonical_hash(payload))
        self.assertEqual(manifest["expectedEnvelopeHash"], canonical_hash(fixture))
        signature_fixture = manifest["signature"]["fixture"]
        signing_statement = {
            "schema": "vyral.ai-metering-integrity.v1",
            "algorithm": "ES256",
            "evidenceSchema": fixture["schema"],
            "issuer": signature_fixture["issuer"],
            "keyId": signature_fixture["keyId"],
            "payloadHash": manifest["expectedPayloadHash"],
        }
        self.assertEqual(
            signature_fixture["expectedInputHash"], canonical_hash(signing_statement)
        )
        review_payload = dict(review)
        review_payload.pop("integrity", None)
        self.assertEqual(
            manifest["expectedReviewPayloadHash"], canonical_hash(review_payload)
        )
        review_signature_fixture = manifest["reviewSignatureFixture"]
        review_signing_statement = {
            "schema": "vyral.ai-metering-integrity.v1",
            "algorithm": "ES256",
            "evidenceSchema": review["schema"],
            "issuer": review_signature_fixture["issuer"],
            "keyId": review_signature_fixture["keyId"],
            "payloadHash": manifest["expectedReviewPayloadHash"],
        }
        self.assertEqual(
            review_signature_fixture["expectedInputHash"],
            canonical_hash(review_signing_statement),
        )
        self.assertEqual(
            manifest["expectedReviewEnvelopeHash"], canonical_hash(review)
        )

if __name__ == "__main__":
    unittest.main()
