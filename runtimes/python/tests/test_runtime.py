from __future__ import annotations

import json
from hashlib import sha256
from pathlib import Path
import shutil
import sqlite3
import sys
import tempfile
from typing import Any
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime import (  # noqa: E402
    CONTRACT_VERSION,
    FIXTURE_VERSION,
    RUNTIME_VERSION,
    CanonicalDocument,
    CanonicalMutation,
    CanonicalTransactionRequest,
    ConformanceError,
    LocalRuntimeConfig,
    RuntimeProfileId,
    STORAGE_SCHEMA_COMPONENT,
    STORAGE_SCHEMA_VERSION,
    SQLiteRecordStore,
    StorageSchemaError,
    VyralRuntime,
    canonical_transaction_id,
    get_readiness,
    hash_lease_token,
    load_conformance_manifest,
    load_contract_bundle,
    run_bundled_external_worker_scenario,
    run_bundled_canonical_scenario,
    run_bundled_native_execution_scenario,
    run_bundled_goldens,
    run_bundled_projection_generation_scenario,
    run_bundled_record_store_scenarios,
    sha256_utf8,
)


ROOT = Path(__file__).resolve().parents[3]
FIXTURE_ROOT = ROOT / "conformance/runtime/v1"


class ContractBundleTests(unittest.TestCase):
    def test_canonical_contract_bundle_is_complete_and_consistent(self) -> None:
        bundle = load_contract_bundle()

        self.assertEqual(CONTRACT_VERSION, bundle.summary.contract_version)
        self.assertEqual("3.1.0", bundle.summary.openapi_version)
        self.assertEqual(129, bundle.summary.operation_count)
        self.assertEqual(133, bundle.summary.rest_operation_count)
        self.assertEqual(263, bundle.summary.schema_count)
        self.assertEqual(
            "sha256:" + sha256((ROOT / "contracts/public-sdk-surface.json").read_bytes()).hexdigest(),
            bundle.summary.catalog_sha256,
        )
        self.assertEqual(
            "sha256:" + sha256((ROOT / "contracts/schemas/vyral-public.schema.json").read_bytes()).hexdigest(),
            bundle.summary.schema_sha256,
        )
        self.assertEqual(
            "sha256:" + sha256((ROOT / "src/Vyral.Server/contracts/vyral.openapi.json").read_bytes()).hexdigest(),
            bundle.summary.openapi_sha256,
        )

    def test_runtime_readiness_separates_availability_from_promotion(
        self,
    ) -> None:
        readiness = VyralRuntime().readiness()
        document: Any = readiness.to_dict()

        self.assertEqual(RUNTIME_VERSION, document["runtimeVersion"])
        self.assertEqual(CONTRACT_VERSION, document["contractVersion"])
        self.assertEqual(FIXTURE_VERSION, document["fixtureVersion"])
        self.assertEqual("ok", document["status"])
        self.assertEqual("prototype", document["maturity"])
        self.assertFalse(document["fullLocalReady"])
        self.assertEqual([], document["blockers"])
        self.assertTrue(document["warnings"])
        self.assertEqual(129, document["contract"]["operationCount"])
        self.assertEqual(
            ["passed", "passed", "passed", "passed", "passed"],
            [check["status"] for check in document["checks"]],
        )
        json.dumps(document)

        profiles = {profile["id"]: profile for profile in document["profiles"]}
        self.assertEqual(
            set(RuntimeProfileId),
            {
                RuntimeProfileId(profile["id"])
                for profile in document["profiles"]
                if profile["available"]
            },
        )
        self.assertTrue(
            all(
                profile["maturity"] == "prototype"
                for profile in profiles.values()
            )
        )

    def test_readiness_function_does_not_require_runtime_construction(self) -> None:
        readiness = get_readiness()
        self.assertEqual("ok", readiness.status)
        self.assertFalse(readiness.full_local_ready)

    def test_local_runtime_composes_services_and_diagnostics(self) -> None:
        with tempfile.TemporaryDirectory(prefix="vyral-python-runtime-") as temporary:
            config = LocalRuntimeConfig(
                root_path=Path(temporary),
                max_workers=2,
                max_pending=4,
            )
            with VyralRuntime(config) as runtime:
                self.assertEqual(config, runtime.config)
                self.assertEqual(
                    STORAGE_SCHEMA_VERSION,
                    runtime.storage_schema_receipt.to_version,
                )
                self.assertEqual(config.database_path, runtime.records.database_path)
                self.assertEqual(
                    config.database_path,
                    runtime.canonical.database_path,
                )
                self.assertIs(
                    runtime.async_canonical.store,
                    runtime.canonical,
                )
                self.assertEqual(
                    config.database_path,
                    runtime.execution.options.database_path,
                )
                committed = runtime.canonical.commit(
                    CanonicalTransactionRequest(
                        tenant_id="tenant-a",
                        idempotency_key="runtime-composition",
                        mutations=(
                            CanonicalMutation(
                                document=CanonicalDocument(
                                    tenant_id="tenant-a",
                                    document_type="probe",
                                    id="p-1",
                                    schema_version="v1",
                                    data={"value": "ready"},
                                )
                            ),
                        ),
                    )
                )
                self.assertFalse(committed.replayed)
                self.assertIs(runtime.embeddings.provider, runtime.retrieval.embedding_provider)
                self.assertIs(
                    runtime.retrieval.embedding_provider,
                    runtime.rag_ingestion.embedding_provider,
                )
                self.assertIs(runtime.rag_context.retrieval_service, runtime.retrieval)
                self.assertIs(runtime.rag_prompts.context_service, runtime.rag_context)

                readiness = runtime.readiness()
                checks = {check["id"]: check for check in readiness.checks}
                self.assertEqual(
                    "passed",
                    checks["local.storage-schema"]["status"],
                )
                self.assertEqual("passed", checks["local.sqlite"]["status"])
                self.assertEqual(
                    "passed", checks["local.canonical"]["status"]
                )
                self.assertEqual(
                    "passed", checks["local.execution"]["status"]
                )
                self.assertEqual("passed", checks["local.objects"]["status"])
                self.assertEqual(
                    "passed",
                    checks["local.embedding-provider"]["status"],
                )
                self.assertEqual("ok", readiness.status)
                self.assertFalse(readiness.full_local_ready)

            with self.assertRaisesRegex(RuntimeError, "closed"):
                _ = runtime.records

    def test_contract_only_runtime_rejects_local_service_access(self) -> None:
        runtime = VyralRuntime()
        with self.assertRaisesRegex(RuntimeError, "embedded local configuration"):
            _ = runtime.records
        runtime.close()

    def test_construction_defers_full_golden_execution_by_default(self) -> None:
        with patch("vyral_runtime.runtime.run_bundled_goldens") as goldens:
            runtime = VyralRuntime()
            runtime.close()

        goldens.assert_not_called()

    def test_construction_can_explicitly_verify_bundled_assets(self) -> None:
        with patch("vyral_runtime.runtime.run_bundled_goldens") as goldens:
            runtime = VyralRuntime(verify_assets=True)
            runtime.close()

        goldens.assert_called_once_with()

    def test_verify_assets_must_be_boolean(self) -> None:
        with self.assertRaisesRegex(TypeError, "verify_assets"):
            VyralRuntime(verify_assets="yes")  # type: ignore[arg-type]

    def test_runtime_adopts_legacy_storage_and_preserves_data_on_restart(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-python-storage-upgrade-"
        ) as temporary:
            root = Path(temporary)
            database_path = root / "vyral.sqlite"
            legacy = SQLiteRecordStore(database_path)
            legacy.create_collection(
                {
                    "name": "upgrade-items",
                    "indexedMetadata": [],
                    "vectorPolicies": [],
                }
            )
            legacy.upsert_record(
                "upgrade-items",
                {
                    "id": "before-upgrade",
                    "partitionKey": "tenant-a",
                    "type": "probe",
                    "text": "persisted by the legacy schema",
                },
            )

            with VyralRuntime(root) as upgraded:
                receipt = upgraded.storage_schema_receipt
                self.assertEqual(0, receipt.from_version)
                self.assertEqual(STORAGE_SCHEMA_VERSION, receipt.to_version)
                self.assertEqual((1,), receipt.applied_versions)
                self.assertTrue(receipt.database_preexisting)
                self.assertGreater(receipt.legacy_table_count, 0)
                self.assertIsNotNone(
                    upgraded.records.get_record(
                        "upgrade-items",
                        "tenant-a",
                        "before-upgrade",
                    )
                )

            with VyralRuntime(root) as restarted:
                receipt = restarted.storage_schema_receipt
                self.assertEqual(
                    STORAGE_SCHEMA_VERSION,
                    receipt.from_version,
                )
                self.assertEqual((), receipt.applied_versions)
                self.assertFalse(receipt.upgraded)
                self.assertIsNotNone(
                    restarted.records.get_record(
                        "upgrade-items",
                        "tenant-a",
                        "before-upgrade",
                    )
                )

    def test_runtime_refuses_newer_or_corrupt_storage_schema(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-python-storage-future-"
        ) as temporary:
            root = Path(temporary)
            with VyralRuntime(root):
                pass
            database_path = root / "vyral.sqlite"
            with sqlite3.connect(database_path) as connection:
                connection.execute(
                    """
                    UPDATE vyral_py_runtime_schema
                    SET schema_version = ?
                    WHERE component = ?
                    """,
                    (
                        STORAGE_SCHEMA_VERSION + 1,
                        STORAGE_SCHEMA_COMPONENT,
                    ),
                )
            with self.assertRaisesRegex(
                StorageSchemaError,
                "supports at most",
            ):
                VyralRuntime(root)

            with sqlite3.connect(database_path) as connection:
                connection.execute(
                    """
                    UPDATE vyral_py_runtime_schema
                    SET schema_version = ?,
                        migrated_by_runtime_version = ''
                    WHERE component = ?
                    """,
                    (
                        STORAGE_SCHEMA_VERSION,
                        STORAGE_SCHEMA_COMPONENT,
                    ),
                )
            with self.assertRaisesRegex(
                StorageSchemaError,
                "empty runtime version",
            ):
                VyralRuntime(root)

    def test_local_runtime_configuration_rejects_unsafe_or_unbounded_values(
        self,
    ) -> None:
        with self.assertRaisesRegex(ValueError, "portable file name"):
            LocalRuntimeConfig(Path("."), database_name="../runtime.sqlite")
        with self.assertRaisesRegex(ValueError, "max_pending"):
            LocalRuntimeConfig(Path("."), max_workers=4, max_pending=3)
        with self.assertRaisesRegex(ValueError, "Unknown"):
            LocalRuntimeConfig.from_value(
                {"rootPath": ".", "surprise": True}
            )


class PrimitiveGoldenTests(unittest.TestCase):
    def test_portable_hashing_primitives(self) -> None:
        self.assertEqual(
            "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            sha256_utf8(""),
        )
        self.assertEqual(
            "sha256:6f570dd20728d6fe3d804ca68460a2fd1973342cdbe809367b3834ddbff6e1b8",
            sha256_utf8("Vyral π"),
        )
        self.assertEqual(
            "ctx_37411805606427a1048f336af169102f",
            canonical_transaction_id(" tenant-a ", " request-1 "),
        )
        self.assertEqual(
            "sha256:2c80af130e7c29586a8e40b306691fd9726d60daa488ff3580121f95a823fc38",
            hash_lease_token("lease-token"),
        )

    def test_portable_hashing_rejects_non_text(self) -> None:
        with self.assertRaises(TypeError):
            sha256_utf8(123)  # type: ignore[arg-type]
        with self.assertRaises(TypeError):
            canonical_transaction_id("tenant-a", None)  # type: ignore[arg-type]


class ConformanceManifestTests(unittest.TestCase):
    def test_manifest_loads_and_runs_all_current_goldens(self) -> None:
        manifest = load_conformance_manifest(FIXTURE_ROOT)
        results = run_bundled_goldens(FIXTURE_ROOT)

        self.assertEqual(FIXTURE_VERSION, manifest.fixture_version)
        self.assertEqual(CONTRACT_VERSION, manifest.contract_version)
        self.assertIn(RuntimeProfileId.CONTRACTS.value, manifest.profiles)
        self.assertEqual(
            [
                "primitives.hashing.v1",
                "admission.receipts.v1",
                "records.core-crud.v1",
                "records.query-semantics.v1",
                "records.snapshot-hash.v1",
                "records.projection-generation.v1",
                "records.projection-generation-lifecycle.v1",
                "embeddings.vectors.v1",
                "rag.ingestion-plan.v1",
                "graph.record-mapping.v1",
                "external-workers.handler-lifecycle.v1",
                "canonical.strong-profile.v1",
                "execution.native-lifecycle.v1",
            ],
            [item.scenario_id for item in manifest.scenarios],
        )
        self.assertEqual(
            [
                "sha256-empty",
                "sha256-unicode",
                "canonical-transaction-id-trims",
                "canonical-lease-token-hash",
                "accepted-receipt",
                "replayed-receipt-keeps-identity",
                "rejected-receipt",
                "snapshot-hash-unicode-float32",
                "descriptor-hash-two-partitions",
                "deterministic-hash-unicode",
                "token-hash-lexical",
                "dry-run-plan-hash-and-chunk-boundaries",
                "graph-envelope-to-records",
            ],
            [result.step_id for result in results],
        )
        self.assertEqual(
            30,
            len(run_bundled_record_store_scenarios(FIXTURE_ROOT)),
        )
        self.assertEqual(
            [
                "publish-generation-a",
                "activate-generation-a",
                "inspect-active-generation-a",
                "search-generation-a-first-page",
                "publish-generation-b",
                "activate-generation-b",
                "continue-retained-generation-a",
                "reject-tampered-continuation",
                "remove-generation-b-partition",
                "fail-closed-on-incomplete-generation-b",
                "inspect-incomplete-generation-b",
                "restore-generation-b-coverage",
                "reject-wrong-descriptor-fence",
                "retire-generation-a",
                "reject-retired-generation-a-continuation",
            ],
            [
                result.step_id
                for result in run_bundled_projection_generation_scenario(
                    FIXTURE_ROOT
                )
            ],
        )
        self.assertEqual(
            [
                "handler-success-side-effects",
                "durable-wait-replay",
                "handler-failure-redacted",
            ],
            [
                result.step_id
                for result in run_bundled_external_worker_scenario(
                    FIXTURE_ROOT
                )
            ],
        )
        self.assertEqual(
            [
                "native-success-idempotency-and-owned-state",
                "native-rejections-and-idempotency-conflict",
                "native-retry-to-success",
                "native-durable-wait-restart",
                "native-terminal-failure-class",
                "native-pending-cancellation-is-stable",
                "native-coordination-and-maintenance",
            ],
            [
                result.step_id
                for result in run_bundled_native_execution_scenario(
                    FIXTURE_ROOT
                )
            ],
        )
        self.assertEqual(
            [
                "atomic-commit-and-idempotent-replay",
                "atomic-fence-and-revision-conflicts",
                "outbox-release-dead-letter-replay-and-ack",
                "hash-verified-snapshot-and-chunked-archive-restore",
                "migrations-range-continuation-and-tenant-isolation",
                "portable-snapshot-and-archive-codec",
            ],
            [
                result.step_id
                for result in run_bundled_canonical_scenario(
                    FIXTURE_ROOT
                )
            ],
        )

    def test_manifest_rejects_tampered_scenario(self) -> None:
        with tempfile.TemporaryDirectory(prefix="vyral-runtime-fixtures-") as temporary:
            copied = Path(temporary) / "v1"
            shutil.copytree(FIXTURE_ROOT, copied)
            scenario = copied / "scenarios/goldens/primitives-hashing.json"
            scenario.write_text(scenario.read_text(encoding="utf-8") + "\n", encoding="utf-8")

            with self.assertRaisesRegex(ConformanceError, "has digest"):
                load_conformance_manifest(copied)

    def test_manifest_rejects_path_escape(self) -> None:
        with tempfile.TemporaryDirectory(prefix="vyral-runtime-fixtures-") as temporary:
            copied = Path(temporary) / "v1"
            shutil.copytree(FIXTURE_ROOT, copied)
            manifest_path = copied / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["scenarios"][0]["path"] = "../escaped.json"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(ConformanceError, "not portable"):
                load_conformance_manifest(copied)

    def test_manifest_rejects_a_newer_minimum_runner(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-runtime-fixtures-"
        ) as temporary:
            copied = Path(temporary) / "v1"
            shutil.copytree(FIXTURE_ROOT, copied)
            manifest_path = copied / "manifest.json"
            manifest = json.loads(
                manifest_path.read_text(encoding="utf-8")
            )
            manifest["minimumRunnerVersion"] = "99.0.0"
            manifest_path.write_text(
                json.dumps(manifest),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(
                ConformanceError,
                "exceeds this runtime",
            ):
                load_conformance_manifest(copied)


if __name__ == "__main__":
    unittest.main()
