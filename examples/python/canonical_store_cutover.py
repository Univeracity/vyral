from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
import tempfile


sys.path.insert(
    0,
    str(Path(__file__).resolve().parents[2] / "runtimes/python/src"),
)

from vyral_runtime.canonical import (  # noqa: E402
    CanonicalArchiveRestoreRequest,
    CanonicalDocument,
    CanonicalMutation,
    CanonicalOutboxWrite,
    CanonicalTransactionRequest,
    SQLiteCanonicalStore,
)


_TENANT_ID = "tenant-cutover-demo"
_REQUEST = CanonicalTransactionRequest(
    tenant_id=_TENANT_ID,
    idempotency_key="catalog:seed:v1",
    correlation_id="cutover-demo",
    actor="example",
    mutations=(
        CanonicalMutation(
            document=CanonicalDocument(
                tenant_id=_TENANT_ID,
                document_type="catalog.item",
                id="item-001",
                schema_version="v1",
                data={
                    "name": "Portable record",
                    "status": "ready",
                },
                indexes={"status": "ready"},
            )
        ),
    ),
    outbox=(
        CanonicalOutboxWrite(
            topic="catalog.changed",
            key="item-001",
            payload={"id": "item-001", "operation": "upsert"},
        ),
    ),
)


def run_cutover() -> dict[str, object]:
    with tempfile.TemporaryDirectory(
        prefix="vyral-canonical-cutover-"
    ) as temporary:
        root = Path(temporary)
        source = SQLiteCanonicalStore(root / "source.sqlite")
        target = SQLiteCanonicalStore(root / "target.sqlite")

        admitted = source.commit(_REQUEST)
        source_replay = source.commit(_REQUEST)
        archive = source.export_tenant_archive(
            _TENANT_ID,
            chunk_bytes=512,
        )

        target.restore_tenant_archive(
            CanonicalArchiveRestoreRequest(
                archive=archive,
                expected_content_hash=archive.content_hash,
            )
        )
        restored = target.get_document(
            _TENANT_ID,
            "catalog.item",
            "item-001",
        )
        target_replay = target.commit(_REQUEST)
        target_snapshot = target.export_tenant(_TENANT_ID)

        if restored is None:
            raise RuntimeError("The target omitted the migrated document.")
        if restored.data != {
            "name": "Portable record",
            "status": "ready",
        }:
            raise RuntimeError("The target changed the migrated document.")
        if target.get_document(
            "tenant-not-exported",
            "catalog.item",
            "item-001",
        ) is not None:
            raise RuntimeError("The cutover crossed a tenant boundary.")
        if not source_replay.replayed or not target_replay.replayed:
            raise RuntimeError(
                "The cutover did not preserve idempotent admission."
            )
        if target_replay.transaction_id != admitted.transaction_id:
            raise RuntimeError(
                "The target changed the canonical transaction identity."
            )

        return {
            "schemaVersion": "vyral.canonical-cutover-example.v1",
            "source": {
                "adapter": "sqlite",
                "tenantId": _TENANT_ID,
                "transactionId": admitted.transaction_id,
                "idempotentReplay": source_replay.replayed,
            },
            "transfer": {
                "profile": archive.profile,
                "archiveContentHash": archive.content_hash,
                "snapshotContentHash": archive.snapshot_content_hash,
                "chunkCount": len(archive.chunks),
                "hashVerifiedRestore": True,
            },
            "target": {
                "adapter": "sqlite",
                "documentCount": len(target_snapshot.documents),
                "revisionCount": len(target_snapshot.revisions),
                "outboxEventCount": len(target_snapshot.outbox),
                "transactionCount": len(target_snapshot.transactions),
                "transactionId": target_replay.transaction_id,
                "idempotentReplay": target_replay.replayed,
                "tenantIsolationPreserved": True,
            },
            "claim": (
                "The canonical archive preserved one tenant's document, "
                "revision, outbox event, transaction identity, and replay "
                "semantics across two independent stores."
            ),
        }


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Run a hash-verified CanonicalStore cutover between independent "
            "local adapters."
        )
    )
    parser.add_argument("--json", action="store_true")
    arguments = parser.parse_args()
    result = run_cutover()
    if arguments.json:
        print(json.dumps(result, indent=2, sort_keys=True))
        return

    source = result["source"]
    transfer = result["transfer"]
    target = result["target"]
    assert isinstance(source, dict)
    assert isinstance(transfer, dict)
    assert isinstance(target, dict)
    print(
        "source: "
        f"transaction={source['transactionId']} "
        f"replay={str(source['idempotentReplay']).lower()}"
    )
    print(
        "transfer: "
        f"chunks={transfer['chunkCount']} hash=verified"
    )
    print(
        "target: "
        f"documents={target['documentCount']} "
        f"outbox={target['outboxEventCount']} "
        f"replay={str(target['idempotentReplay']).lower()}"
    )
    print(result["claim"])


if __name__ == "__main__":
    main()
