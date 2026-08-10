import {
  VyralClient,
  type ArtifactRecordIngestManifest,
  type ExecutionExternalEvent,
  type ExecutionExternalEventRequest,
  type ServerHealthStatus
} from "../../src/index.js";

const client = new VyralClient("http://localhost:5220", {
  apiKey: "test-key",
  timeoutMs: 5_000,
  correlationId: "typescript-consumer"
});

const health: Promise<ServerHealthStatus> = client.health();
void health;

const event: ExecutionExternalEventRequest = {
  name: "approved",
  payload: { actor: "operator" }
};
const raised: Promise<ExecutionExternalEvent> = client.raiseExecutionEvent("run-1", event);
void raised;

const manifest: ArtifactRecordIngestManifest = {
  collection: "events",
  record: {
    id: "event-1",
    partitionKey: "tenant-a",
    type: "consumer.event"
  },
  artifact: {
    container: "artifacts",
    key: "events/event-1.json"
  }
};
client.ingestRecordArtifact(manifest, new Blob(["payload"]), { fileName: "event.json" });

// @ts-expect-error execution event names are required by the contract
const invalidEvent: ExecutionExternalEventRequest = { payload: {} };
void invalidEvent;
