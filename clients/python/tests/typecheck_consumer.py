from vyral_client import VyralClient
from vyral_client.contracts import (
    ArtifactRecordIngestManifest,
    ExecutionExternalEvent,
    ExecutionExternalEventRequest,
    ServerHealthStatus,
)


client = VyralClient("http://localhost:5220", api_key="test-key")
health: ServerHealthStatus = client.health()

event: ExecutionExternalEventRequest = {
    "name": "approved",
    "payload": {"actor": "operator"},
}
raised: ExecutionExternalEvent = client.raise_execution_event("run-1", event)

manifest: ArtifactRecordIngestManifest = {
    "collection": "events",
    "record": {
        "id": "event-1",
        "partitionKey": "tenant-a",
        "type": "consumer.event",
    },
    "artifact": {
        "container": "artifacts",
        "key": "events/event-1.json",
    },
}
client.ingest_record_artifact(manifest, b"payload", filename="event.json")

assert health is not None
assert raised is not None
