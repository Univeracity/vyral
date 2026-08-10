from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime import (
    ADMISSION_VERSION,
    create_admission_receipt,
)


class AdmissionReceiptTests(unittest.TestCase):
    def test_receipt_is_deterministic_and_does_not_reflect_raw_key(
        self,
    ) -> None:
        arguments = {
            "operation_id": "importCollection",
            "resource_id": "run-portable-admission-1",
            "request_hash": "sha256:request",
            "idempotency_key": "consumer-secret-key",
            "replayed": False,
            "admitted_at_utc": datetime(
                2026, 8, 1, 12, 0, tzinfo=timezone.utc
            ),
            "status_uri": (
                "/record-import/jobs/run-portable-admission-1"
            ),
        }

        first = create_admission_receipt(**arguments)
        replay = create_admission_receipt(
            **{**arguments, "replayed": True}
        )

        self.assertEqual(ADMISSION_VERSION, first.version)
        self.assertEqual(first.admission_id, replay.admission_id)
        self.assertFalse(first.replayed)
        self.assertTrue(replay.replayed)
        self.assertNotIn("consumer-secret-key", str(first.to_dict()))
        self.assertEqual(64, len(first.idempotency_key_hash or ""))

    def test_rejected_receipt_carries_failure_details(self) -> None:
        receipt = create_admission_receipt(
            operation_id="startExecutionRun",
            resource_id="run-rejected",
            request_hash="sha256:rejected",
            idempotency_key=None,
            replayed=False,
            admitted_at_utc=datetime(
                2026, 8, 1, 12, 0, tzinfo=timezone.utc
            ),
            status_uri="/execution/runs/run-rejected",
            status="rejected",
            failure_class="handler_missing",
            error="Execution handler is not registered.",
        )

        self.assertEqual("rejected", receipt.status)
        self.assertEqual("handler_missing", receipt.failure_class)
        self.assertIsNone(receipt.idempotency_key_hash)

    def test_invalid_status_and_naive_timestamp_are_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "Unknown admission status"):
            create_admission_receipt(
                operation_id="startExecutionRun",
                resource_id="run-invalid",
                request_hash="sha256:invalid",
                idempotency_key=None,
                replayed=False,
                admitted_at_utc=datetime(
                    2026, 8, 1, 12, 0, tzinfo=timezone.utc
                ),
                status_uri="/execution/runs/run-invalid",
                status="pending",
            )
        with self.assertRaisesRegex(ValueError, "include an offset"):
            create_admission_receipt(
                operation_id="startExecutionRun",
                resource_id="run-invalid",
                request_hash="sha256:invalid",
                idempotency_key=None,
                replayed=False,
                admitted_at_utc=datetime(2026, 8, 1, 12, 0),
                status_uri="/execution/runs/run-invalid",
            )


if __name__ == "__main__":
    unittest.main()
