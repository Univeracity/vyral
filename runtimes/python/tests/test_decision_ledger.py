from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import unittest

from jsonschema import Draft202012Validator

from vyral_runtime.decision_ledger import (
    DecisionLedger,
    DecisionLedgerError,
)
from vyral_runtime.decisions import (
    DecisionPolicyError,
    compute_decision_receipt_hash,
    create_decision_ledger_entry,
    replay_decision_receipt,
    validate_decision_ledger_entry,
    validate_decision_receipt,
    verify_decision_ledger_chain,
)


ROOT = Path(__file__).resolve().parents[3]
FIXTURE = ROOT / "conformance" / "ai-decision" / "v1"
LEDGER_FIXTURE = ROOT / "conformance" / "ai-decision-ledger" / "v1"


def _json(name: str) -> dict[str, object]:
    value = json.loads((FIXTURE / name).read_text(encoding="utf-8"))
    assert isinstance(value, dict)
    return value


class DecisionLedgerContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self.receipt = _json("receipt.json")

    def test_receipt_hash_is_stable_across_equivalent_utc_forms(self) -> None:
        first = compute_decision_receipt_hash(self.receipt)
        equivalent = dict(self.receipt)
        equivalent["evaluatedAtUtc"] = "2026-10-23T05:30:00.000000+00:00"
        self.assertEqual(first, compute_decision_receipt_hash(equivalent))

    def test_receipt_validation_rejects_unknown_fields_and_bad_branch(self) -> None:
        invalid = dict(self.receipt)
        invalid["unknown"] = True
        invalid["escalationRoute"] = "manual-review"
        errors = validate_decision_receipt(invalid)
        self.assertIn("receipt contains unknown properties", errors)
        self.assertIn(
            "accept outcome cannot include escalationRoute", errors
        )

    def test_entry_hashes_and_chain_links_are_verified(self) -> None:
        first = create_decision_ledger_entry(
            self.receipt,
            sequence=0,
            previous_entry_hash=None,
            recorded_at=datetime(
                2026, 10, 23, 6, 30, tzinfo=timezone.utc
            ),
        )
        second = create_decision_ledger_entry(
            self.receipt,
            sequence=1,
            previous_entry_hash=str(first["entryHash"]),
            recorded_at=datetime(
                2026, 10, 23, 6, 31, tzinfo=timezone.utc
            ),
        )
        self.assertEqual((), validate_decision_ledger_entry(first))
        self.assertEqual((), verify_decision_ledger_chain([first, second]))
        mutated = json.loads(json.dumps(second))
        mutated["receipt"]["outcome"] = "reject"
        self.assertIn(
            "entry[1].receipt.reject outcome requires reason rejected",
            verify_decision_ledger_chain([first, mutated]),
        )

    def test_python_hashes_match_portable_ledger_fixture(self) -> None:
        manifest = json.loads(
            (LEDGER_FIXTURE / "manifest.json").read_text(encoding="utf-8")
        )
        entry = json.loads(
            (LEDGER_FIXTURE / "entry.json").read_text(encoding="utf-8")
        )
        self.assertEqual((), validate_decision_ledger_entry(entry))
        self.assertEqual(manifest["receiptHash"], entry["receiptHash"])
        self.assertEqual(manifest["entryHash"], entry["entryHash"])
        bundle = json.loads(
            (
                ROOT
                / "contracts"
                / "schemas"
                / "vyral-public.schema.json"
            ).read_text(encoding="utf-8")
        )
        schema = {
            **bundle["$defs"]["AiDecisionLedgerEntry"],
            "$defs": bundle["$defs"],
        }
        Draft202012Validator(schema).validate(entry)

    def test_non_genesis_entry_requires_previous_hash(self) -> None:
        with self.assertRaisesRegex(
            DecisionPolicyError, "non-genesis entry"
        ):
            create_decision_ledger_entry(
                self.receipt,
                sequence=1,
                previous_entry_hash=None,
            )

    def test_replay_matches_exact_receipt_and_reports_policy_change(self) -> None:
        policy = _json("policy.json")
        result = _json("provider-result.json")
        matching = replay_decision_receipt(self.receipt, policy, result)
        self.assertTrue(matching["matches"])
        changed = json.loads(json.dumps(policy))
        changed["version"] = "1.0.1"
        mismatch = replay_decision_receipt(self.receipt, changed, result)
        self.assertFalse(mismatch["matches"])


class DecisionLedgerStorageTests(unittest.TestCase):
    def setUp(self) -> None:
        self.receipt = _json("receipt.json")
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "nested" / "decisions.sqlite"

    def test_append_reopen_list_show_and_checkpoint_verify(self) -> None:
        with DecisionLedger(self.path) as ledger:
            first = ledger.append(
                self.receipt,
                recorded_at=datetime(
                    2026, 10, 23, 6, 30, tzinfo=timezone.utc
                ),
            )
        with DecisionLedger(self.path) as ledger:
            second = ledger.append(
                self.receipt,
                recorded_at=datetime(
                    2026, 10, 23, 6, 31, tzinfo=timezone.utc
                ),
            )
            self.assertEqual(first["entryHash"], second["previousEntryHash"])
            self.assertEqual(second, ledger.get(sequence=1))
            self.assertEqual(second, ledger.get(entry_hash=second["entryHash"]))
            summaries = ledger.list_entries(policy_id="catalog-item-reuse")
            self.assertEqual([1, 0], [item["sequence"] for item in summaries])
            verified = ledger.verify(
                expected_head=str(second["entryHash"]), expected_count=2
            )
            self.assertTrue(verified["valid"])
            mismatch = ledger.verify(expected_head="sha256:" + "0" * 64)
            self.assertFalse(mismatch["valid"])
        if os.name != "nt":
            self.assertEqual(0o600, self.path.stat().st_mode & 0o777)

    def test_append_refuses_a_mutated_existing_chain(self) -> None:
        with DecisionLedger(self.path) as ledger:
            ledger.append(self.receipt)
        with sqlite3.connect(self.path) as connection:
            encoded = connection.execute(
                "SELECT entry_json FROM vyral_decision_ledger_entries"
            ).fetchone()[0]
            value = json.loads(encoded)
            value["receipt"]["policyVersion"] = "tampered"
            connection.execute(
                "UPDATE vyral_decision_ledger_entries SET entry_json = ?",
                (json.dumps(value),),
            )
        with DecisionLedger(self.path) as ledger:
            verification = ledger.verify()
            self.assertFalse(verification["valid"])
            with self.assertRaises(DecisionLedgerError):
                ledger.append(self.receipt)

    def test_column_and_entry_json_mismatch_fails_closed(self) -> None:
        with DecisionLedger(self.path) as ledger:
            ledger.append(self.receipt)
        with sqlite3.connect(self.path) as connection:
            connection.execute(
                "UPDATE vyral_decision_ledger_entries SET policy_id = ?",
                ("different-policy",),
            )
        with DecisionLedger(self.path) as ledger:
            verification = ledger.verify()
            self.assertFalse(verification["valid"])
            self.assertIn(
                "ledger column policy_id does not match entry JSON",
                verification["errors"],
            )

    def test_refuses_unrelated_sqlite_file(self) -> None:
        self.path.parent.mkdir(parents=True)
        with sqlite3.connect(self.path) as connection:
            connection.execute("CREATE TABLE unrelated (id INTEGER)")
        with self.assertRaisesRegex(DecisionLedgerError, "unknown"):
            DecisionLedger(self.path)

    def test_refuses_unexpected_triggers_in_ledger_schema(self) -> None:
        with DecisionLedger(self.path):
            pass
        with sqlite3.connect(self.path) as connection:
            connection.execute(
                """
                CREATE TRIGGER unexpected_trigger
                AFTER INSERT ON vyral_decision_ledger_entries
                BEGIN
                    DELETE FROM vyral_decision_ledger_entries;
                END
                """
            )
        with self.assertRaisesRegex(DecisionLedgerError, "schema object"):
            DecisionLedger(self.path, create=False)

    def test_existing_only_open_does_not_create_a_missing_ledger(self) -> None:
        with self.assertRaisesRegex(DecisionLedgerError, "does not exist"):
            DecisionLedger(self.path, create=False)
        self.assertFalse(self.path.exists())

    @unittest.skipIf(os.name == "nt", "symlink semantics vary on Windows")
    def test_refuses_a_broken_final_symlink_without_creating_target(self) -> None:
        self.path.parent.mkdir(parents=True)
        target = self.path.parent / "redirected.sqlite"
        self.path.symlink_to(target)

        with self.assertRaisesRegex(DecisionLedgerError, "symbolic link"):
            DecisionLedger(self.path)

        self.assertFalse(target.exists())

    def test_requires_exactly_one_lookup_identity(self) -> None:
        with DecisionLedger(self.path) as ledger:
            with self.assertRaisesRegex(DecisionLedgerError, "exactly one"):
                ledger.get()
            with self.assertRaisesRegex(DecisionLedgerError, "exactly one"):
                ledger.get(sequence=0, entry_hash="sha256:" + "0" * 64)

    def test_concurrent_appenders_receive_contiguous_sequences(self) -> None:
        with DecisionLedger(self.path):
            pass

        def append(_: int) -> int:
            with DecisionLedger(self.path) as ledger:
                return int(ledger.append(self.receipt)["sequence"])

        with ThreadPoolExecutor(max_workers=4) as executor:
            sequences = list(executor.map(append, range(12)))
        self.assertEqual(list(range(12)), sorted(sequences))
        with DecisionLedger(self.path, create=False) as ledger:
            verification = ledger.verify(expected_count=12)
            self.assertTrue(verification["valid"])


if __name__ == "__main__":
    unittest.main()
