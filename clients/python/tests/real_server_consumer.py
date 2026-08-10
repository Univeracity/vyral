from __future__ import annotations

import io
import os
import asyncio

from vyral_client import AsyncVyralClient, VyralClient, VyralClientError


base_url = os.environ["VYRAL_TEST_BASE_URL"]
api_key = os.environ["VYRAL_TEST_API_KEY"]
collection = "sdk-real-server"

try:
    VyralClient(base_url).list_collections()
except VyralClientError as error:
    assert error.status == 401, error
else:
    raise AssertionError("private route accepted an unauthenticated Python SDK request")

client = VyralClient(
    base_url,
    api_key=api_key,
    correlation_id="python-built-package",
    timeout=10,
    max_retries=1,
)
assert client.health()["status"] == "ok"
assert client.openapi_contract()["openapi"] == "3.1.0"
assert "VyralRecord" in client.get_public_schema_contract()["$defs"]


async def verify_async_extra() -> None:
    async with AsyncVyralClient(base_url, api_key=api_key, timeout=10, max_retries=1) as async_client:
        assert (await async_client.health())["status"] == "ok"
        schemas = await async_client.request_json("GET", "/contracts/schemas/vyral-public.schema.json")
        assert "ExecutionRun" in schemas["$defs"]


asyncio.run(verify_async_extra())

create_run = client.create_collection({
    "name": collection,
    "partitionKeyPath": "/partitionKey",
    "indexedMetadata": ["/metadata/source"],
    "vectorPolicies": [],
}, idempotency_key="python-built-package:create")
assert client.wait_execution_run(create_run["id"])["status"] == "succeeded"
record = {
    "id": "record-1",
    "partitionKey": "tenant-a",
    "type": "consumer.event",
    "metadata": {"source": "python"},
    "content": {"message": "built wheel"},
}
assert client.upsert_record(collection, record)["id"] == "record-1"
assert client.get_record(collection, "tenant-a", "record-1")["content"]["message"] == "built wheel"
assert [item["id"] for item in client.iter_records(collection, {"limit": 10}, max_pages=2)] == ["record-1"]

assert client.put_object("sdk-artifacts", "python/raw.txt", b"python-object", content_type="text/plain")["key"] == "python/raw.txt"
assert client.get_object("sdk-artifacts", "python/raw.txt") == b"python-object"
client.delete_object("sdk-artifacts", "python/raw.txt")

artifact_run = client.ingest_record_artifact(
    {
        "collection": collection,
        "record": {
            "id": "artifact-1",
            "partitionKey": "tenant-a",
            "type": "consumer.artifact",
            "content": {"source": "python-wheel"},
        },
        "artifact": {
            "container": "sdk-artifacts",
            "key": "python/artifact.json",
            "contentType": "application/json",
        },
    },
    io.BytesIO(b'{"source":"python-wheel"}'),
    filename="artifact.json",
    content_type="application/json",
    idempotency_key="python-built-package:artifact",
)
receipt = client.wait_execution_run(artifact_run["id"])["result"]
assert receipt["accepted"] is True
assert client.get_object("sdk-artifacts", "python/artifact.json") == b'{"source":"python-wheel"}'

event = client.raise_execution_event("sdk-event-run", {"name": "approved", "payload": {"source": "python"}})
assert event["runId"] == "sdk-event-run"
assert event["name"] == "approved"

delete_run = client.delete_collection(
    collection, idempotency_key="python-built-package:delete"
)
assert client.wait_execution_run(delete_run["id"])["status"] == "succeeded"
print("python-built-sdk-real-server=ok")
