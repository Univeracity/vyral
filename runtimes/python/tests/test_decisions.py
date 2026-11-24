from __future__ import annotations

from datetime import datetime, timezone
import json
from pathlib import Path
import sys
import unittest

from jsonschema import Draft202012Validator

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime.decisions import (  # noqa: E402
    DecisionPolicyError,
    compute_decision_policy_hash,
    evaluate_decision_policy,
    validate_decision_policy,
)


EVALUATED_AT = datetime(2026, 10, 23, 5, 30, tzinfo=timezone.utc)


class DecisionPolicyTests(unittest.TestCase):
    def test_calibrated_choice_accepts_with_cross_language_policy_hash(
        self,
    ) -> None:
        receipt = evaluate_decision_policy(
            choice_policy(),
            provider_result(
                {
                    "questionId": "reuse",
                    "choice": "same_item",
                    "probabilities": {
                        "same_item": 0.94,
                        "different_item": 0.06,
                    },
                    "confidence": 0.94,
                    "calibrated": True,
                }
            ),
            evaluated_at=EVALUATED_AT,
        )

        self.assertEqual("accept", receipt["outcome"])
        self.assertEqual("accepted", receipt["reason"])
        self.assertIsNone(receipt["escalationRoute"])
        self.assertEqual(
            "sha256:0bafeabfa2a825d058d609434ec600f7ea9675153362c0b1d2aed03cbef872b3",
            receipt["policyHash"],
        )
        self.assertEqual("2026-10-23T05:30:00Z", receipt["evaluatedAtUtc"])

    def test_uncalibrated_and_deadband_results_escalate(self) -> None:
        uncalibrated = evaluate_decision_policy(
            choice_policy(),
            provider_result(
                {
                    "questionId": "reuse",
                    "choice": "same_item",
                    "confidence": 0.99,
                    "calibrated": False,
                }
            ),
            evaluated_at=EVALUATED_AT,
        )
        self.assertEqual("uncalibrated", uncalibrated["reason"])

        deadband = evaluate_decision_policy(
            probability_policy(),
            provider_result(
                {
                    "questionId": "supported",
                    "probability": 0.55,
                    "confidence": 0.55,
                    "calibrated": True,
                }
            ),
            evaluated_at=EVALUATED_AT,
        )
        self.assertEqual("escalate", deadband["outcome"])
        self.assertEqual("no_rule_match", deadband["reason"])

        invalid_score_policy = probability_policy()
        invalid_score_policy["answerKind"] = "score"
        invalid_score_policy["questionId"] = "quality"
        invalid_score_policy["accept"]["threshold"] = 2
        invalid_score_policy["reject"]["threshold"] = 1
        invalid_shape = evaluate_decision_policy(
            invalid_score_policy,
            provider_result(
                {
                    "questionId": "quality",
                    "score": -0.1,
                    "calibrated": False,
                }
            ),
            evaluated_at=EVALUATED_AT,
        )
        self.assertEqual("answer_shape_mismatch", invalid_shape["reason"])

    def test_failed_provider_result_produces_a_bounded_receipt(self) -> None:
        result = provider_result({"questionId": "reuse"})
        result["status"] = "TimedOut"
        result["output"] = {}
        receipt = evaluate_decision_policy(
            choice_policy(), result, evaluated_at=EVALUATED_AT
        )
        self.assertEqual("provider_not_succeeded", receipt["reason"])
        self.assertIsNone(receipt["evidence"]["answerHash"])

    def test_invalid_policy_is_rejected_before_hash_or_evaluation(self) -> None:
        policy = probability_policy()
        policy["accept"]["threshold"] = 0.4
        policy["reject"]["threshold"] = 0.6
        self.assertTrue(
            any(
                "must not overlap" in error
                for error in validate_decision_policy(policy)
            )
        )
        with self.assertRaises(DecisionPolicyError):
            compute_decision_policy_hash(policy)

        malformed_evidence = choice_policy()
        malformed_evidence["evidence"]["requireCalibrated"] = "yes"
        self.assertIn(
            "evidence.requireCalibrated must be a boolean",
            validate_decision_policy(malformed_evidence),
        )

        malformed_choices = choice_policy()
        malformed_choices["accept"]["values"] = [{}]
        self.assertTrue(validate_decision_policy(malformed_choices))

    def test_malformed_provider_metrics_escalate_without_comparison(self) -> None:
        result = provider_result(
            {
                "questionId": "reuse",
                "choice": "same_item",
                "confidence": "certain",
                "calibrated": True,
            }
        )

        receipt = evaluate_decision_policy(
            choice_policy(), result, evaluated_at=EVALUATED_AT
        )

        self.assertEqual("escalate", receipt["outcome"])
        self.assertEqual("invalid_provider_output", receipt["reason"])
        self.assertIsNone(receipt["evidence"]["answerHash"])

    def test_malformed_trace_hash_escalates_without_copying_it(self) -> None:
        result = provider_result(
            {
                "questionId": "reuse",
                "choice": "same_item",
                "confidence": 0.94,
                "calibrated": True,
            }
        )
        result["trace"]["inputHash"] = "not-a-sha256-digest"

        receipt = evaluate_decision_policy(
            choice_policy(), result, evaluated_at=EVALUATED_AT
        )

        self.assertEqual("escalate", receipt["outcome"])
        self.assertEqual("invalid_provider_evidence", receipt["reason"])
        self.assertIsNone(receipt["evidence"]["inputHash"])
        self.assertIsNone(receipt["evidence"]["answerHash"])

    def test_canonical_number_hashes_match_dotnet(self) -> None:
        cases = (
            (
                1.0,
                0.0,
                "sha256:f0fb947a86f2de6b54cb7d861aa8cdc9c173589502e09b62214f5e8e6e868ad4",
            ),
            (
                0.000001,
                0.0000001,
                "sha256:7e392c129d679769469aef4f83602862b37097ccf1f3e94c42f5ed824700270c",
            ),
            (
                1e20,
                1.0,
                "sha256:f3c6db3fb5d1fbb735cb97f43204a7e077295fb8bd4bb6f89b1e27edfdf7c46b",
            ),
            (
                0.30000000000000004,
                0.2,
                "sha256:dc93dd956e257d8f41407261ab38c53e554c5f4b717155fa015e58fc46578e79",
            ),
        )
        for accept, reject, expected in cases:
            with self.subTest(accept=accept, reject=reject):
                policy = probability_policy()
                policy["answerKind"] = "score"
                policy["id"] = "x"
                policy["version"] = "1"
                policy["questionId"] = "q"
                policy["accept"]["threshold"] = accept
                policy["reject"]["threshold"] = reject
                policy["escalationRoute"] = "human"
                self.assertEqual(expected, compute_decision_policy_hash(policy))

    def test_canonical_unicode_ordering_matches_dotnet(self) -> None:
        policy = choice_policy()
        policy["id"] = "unicode"
        policy["version"] = "1"
        policy["questionId"] = "q"
        policy["accept"]["values"] = ["😀"]
        policy["reject"]["values"] = ["\ue000"]
        policy["evidence"]["minimumConfidence"] = None
        policy["escalationRoute"] = "human"
        receipt = evaluate_decision_policy(
            policy,
            provider_result(
                {
                    "questionId": "q",
                    "choice": "😀",
                    "probabilities": {"\ue000": 0.4, "😀": 0.6},
                    "calibrated": True,
                }
            ),
            evaluated_at=EVALUATED_AT,
        )

        self.assertEqual(
            "sha256:624834f6b16ad8bf10b7bef8c8eab645e95f48387d9307ac941bf0f51818975d",
            receipt["policyHash"],
        )
        self.assertEqual(
            "sha256:9c3452fa261ecef3eb5a67c6b80d28c510116b21423f7f1bc34185f594ad1565",
            receipt["evidence"]["answerHash"],
        )

    def test_python_evaluation_matches_the_portable_decision_fixture(self) -> None:
        root = Path(__file__).resolve().parents[3] / "conformance/ai-decision/v1"
        manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
        policy = json.loads((root / "policy.json").read_text(encoding="utf-8"))
        result = json.loads(
            (root / "provider-result.json").read_text(encoding="utf-8")
        )
        expected = json.loads((root / "receipt.json").read_text(encoding="utf-8"))
        actual = evaluate_decision_policy(
            policy,
            result,
            evaluated_at=EVALUATED_AT,
        )
        self.assertEqual(expected, actual)
        self.assertEqual(manifest["expectedPolicyHash"], actual["policyHash"])
        self.assertEqual(
            manifest["expectedAnswerHash"], actual["evidence"]["answerHash"]
        )

    def test_public_schema_accepts_fixtures_and_rejects_unknown_members(self) -> None:
        root = Path(__file__).resolve().parents[3]
        fixture_root = root / "conformance/ai-decision/v1"
        bundle = json.loads(
            (root / "contracts/schemas/vyral-public.schema.json").read_text(
                encoding="utf-8"
            )
        )
        policy = json.loads(
            (fixture_root / "policy.json").read_text(encoding="utf-8")
        )
        receipt = json.loads(
            (fixture_root / "receipt.json").read_text(encoding="utf-8")
        )
        policy_schema = dict(bundle)
        policy_schema["$ref"] = "#/$defs/AiDecisionPolicy"
        receipt_schema = dict(bundle)
        receipt_schema["$ref"] = "#/$defs/AiDecisionReceipt"
        Draft202012Validator(policy_schema).validate(policy)
        Draft202012Validator(receipt_schema).validate(receipt)

        policy["provider"] = "a provider must not own this policy"
        self.assertTrue(
            list(Draft202012Validator(policy_schema).iter_errors(policy))
        )
        self.assertIn(
            "policy contains unknown properties",
            validate_decision_policy(policy),
        )


def choice_policy() -> dict[str, object]:
    return {
        "schema": "vyral.ai-decision-policy.v1",
        "id": "catalog-item-reuse",
        "version": "1.0.0",
        "questionId": "reuse",
        "answerKind": "choice",
        "accept": {
            "operator": "in",
            "values": ["same_item"],
            "threshold": None,
        },
        "reject": {
            "operator": "in",
            "values": ["different_item"],
            "threshold": None,
        },
        "evidence": {
            "requireCalibrated": True,
            "minimumConfidence": 0.8,
            "minimumLabelMassCoverage": None,
            "minimumRotationAgreement": None,
            "allowedValidationStatuses": [],
        },
        "escalationRoute": "manual-review",
    }


def probability_policy() -> dict[str, object]:
    return {
        "schema": "vyral.ai-decision-policy.v1",
        "id": "claim-support",
        "version": "1.0.0",
        "questionId": "supported",
        "answerKind": "probability",
        "accept": {"operator": "at_least", "values": [], "threshold": 0.8},
        "reject": {"operator": "at_most", "values": [], "threshold": 0.2},
        "evidence": {
            "requireCalibrated": True,
            "minimumConfidence": None,
            "minimumLabelMassCoverage": None,
            "minimumRotationAgreement": None,
            "allowedValidationStatuses": [],
        },
        "escalationRoute": "manual-review",
    }


def provider_result(answer: dict[str, object]) -> dict[str, object]:
    return {
        "status": "Succeeded",
        "provider": "local-onnx-nli",
        "capability": "ai.judge",
        "operation": "run",
        "mode": "mechanics",
        "providerStatus": "ok",
        "output": {
            "answers": [answer],
            "validationStatus": "provider_json",
            "modelId": "onnx-nli-fixture",
        },
        "trace": {
            "traceId": "trace-1",
            "adapterId": "onnx-nli",
            "modelId": "onnx-nli-fixture",
            "configHash": "sha256:" + "a" * 64,
            "inputHash": "sha256:" + "b" * 64,
            "outputHash": "sha256:" + "c" * 64,
        },
    }


if __name__ == "__main__":
    unittest.main()
