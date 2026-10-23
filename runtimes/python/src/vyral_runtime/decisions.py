"""Portable, provider-neutral branch policy evaluation for ``ai.judge`` results."""

from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
import json
import math
import re
from typing import Any, Mapping


POLICY_SCHEMA = "vyral.ai-decision-policy.v1"
RECEIPT_SCHEMA = "vyral.ai-decision-receipt.v1"
_ANSWER_KINDS = frozenset({"choice", "probability", "score"})
_NUMERIC_OPERATORS = frozenset({"at_least", "at_most"})
_PROVIDER_RUN_STATUSES = frozenset(
    {
        "Succeeded",
        "Failed",
        "TimedOut",
        "Rejected",
        "Unsupported",
        "NotConfigured",
        "Cancelled",
    }
)
_MAX_IDENTIFIER_LENGTH = 256
_MAX_VALUES = 128
_SHA256_PATTERN = re.compile(r"sha256:[0-9a-f]{64}\Z")
_POLICY_KEYS = frozenset(
    {
        "schema",
        "id",
        "version",
        "questionId",
        "answerKind",
        "accept",
        "reject",
        "evidence",
        "escalationRoute",
    }
)
_PREDICATE_KEYS = frozenset({"operator", "values", "threshold"})
_EVIDENCE_KEYS = frozenset(
    {
        "requireCalibrated",
        "minimumConfidence",
        "minimumLabelMassCoverage",
        "minimumRotationAgreement",
        "allowedValidationStatuses",
    }
)


class DecisionPolicyError(ValueError):
    """Raised when an application decision policy is structurally invalid."""


def validate_decision_policy(
    policy: Mapping[str, Any] | None,
    capability: str = "ai.judge",
) -> tuple[str, ...]:
    """Return deterministic validation errors without running provider work."""
    errors: list[str] = []
    if policy is None or not isinstance(policy, Mapping):
        return ("policy is required",)
    if set(policy).difference(_POLICY_KEYS):
        errors.append("policy contains unknown properties")
    if capability.casefold() != "ai.judge":
        errors.append("decision policies require capability ai.judge")
    _require_text(policy.get("schema"), "schema", errors)
    if policy.get("schema") != POLICY_SCHEMA:
        errors.append(f"schema must be {POLICY_SCHEMA}")
    for name in ("id", "version", "questionId", "escalationRoute"):
        _require_text(policy.get(name), name, errors)
    accept = policy.get("accept")
    reject = policy.get("reject")
    evidence = policy.get("evidence")
    if not isinstance(accept, Mapping):
        errors.append("accept is required")
    if not isinstance(reject, Mapping):
        errors.append("reject is required")
    if not isinstance(evidence, Mapping):
        errors.append("evidence is required")
    if errors:
        return tuple(errors)

    kind = policy.get("answerKind")
    if kind not in _ANSWER_KINDS:
        errors.append("answerKind must be choice, probability, or score")
    assert isinstance(accept, Mapping)
    assert isinstance(reject, Mapping)
    assert isinstance(evidence, Mapping)
    if "operator" not in accept:
        errors.append("accept.operator is required")
    if "operator" not in reject:
        errors.append("reject.operator is required")
    if set(accept).difference(_PREDICATE_KEYS):
        errors.append("accept contains unknown properties")
    if set(reject).difference(_PREDICATE_KEYS):
        errors.append("reject contains unknown properties")
    if set(evidence).difference(_EVIDENCE_KEYS):
        errors.append("evidence contains unknown properties")
    _validate_predicate(accept, "accept", kind, errors)
    _validate_predicate(reject, "reject", kind, errors)
    if kind == "choice":
        accept_values = accept.get("values", [])
        reject_values = reject.get("values", [])
        if (
            isinstance(accept_values, list)
            and isinstance(reject_values, list)
            and all(isinstance(value, str) for value in accept_values)
            and all(isinstance(value, str) for value in reject_values)
        ):
            if set(accept_values).intersection(reject_values):
                errors.append("accept and reject choice values must be disjoint")
    elif _finite_number(accept.get("threshold")) and _finite_number(
        reject.get("threshold")
    ):
        if accept.get("operator") == reject.get("operator"):
            errors.append(
                "numeric accept and reject predicates must use opposing operators"
            )
        else:
            lower = (
                accept["threshold"]
                if accept.get("operator") == "at_least"
                else reject["threshold"]
            )
            upper = (
                accept["threshold"]
                if accept.get("operator") == "at_most"
                else reject["threshold"]
            )
            if lower <= upper:
                errors.append(
                    "numeric accept and reject ranges must not overlap"
                )

    for name in (
        "minimumConfidence",
        "minimumLabelMassCoverage",
        "minimumRotationAgreement",
    ):
        value = evidence.get(name)
        if value is not None and (
            not _finite_number(value) or value < 0 or value > 1
        ):
            errors.append(f"evidence.{name} must be in [0, 1]")
    require_calibrated = evidence.get("requireCalibrated", True)
    if not isinstance(require_calibrated, bool):
        errors.append("evidence.requireCalibrated must be a boolean")
    statuses = evidence.get("allowedValidationStatuses", [])
    if not isinstance(statuses, list) or len(statuses) > _MAX_VALUES:
        errors.append(
            f"evidence.allowedValidationStatuses cannot exceed {_MAX_VALUES} values"
        )
    elif any(not _valid_text(item) for item in statuses) or len(
        set(statuses)
    ) != len(statuses):
        errors.append(
            "evidence.allowedValidationStatuses must contain unique non-empty values"
        )
    return tuple(errors)


def compute_decision_policy_hash(policy: Mapping[str, Any]) -> str:
    """Hash the normalized policy using Vyral's bounded canonical JSON subset."""
    errors = validate_decision_policy(policy)
    if errors:
        raise DecisionPolicyError("; ".join(errors))
    return _canonical_hash(_normalize_policy(policy))


def evaluate_decision_policy(
    policy: Mapping[str, Any],
    provider_result: Mapping[str, Any],
    *,
    evaluated_at: datetime | None = None,
) -> dict[str, Any]:
    """Evaluate one policy locally and return a portable decision receipt."""
    capability = provider_result.get("capability")
    errors = validate_decision_policy(
        policy,
        capability if isinstance(capability, str) else "",
    )
    if errors:
        raise DecisionPolicyError("; ".join(errors))
    normalized = _normalize_policy(policy)
    raw_trace = provider_result.get("trace")
    trace = raw_trace if isinstance(raw_trace, Mapping) else {}
    when = evaluated_at or datetime.now(timezone.utc)
    if when.tzinfo is None:
        raise DecisionPolicyError("evaluated_at must include a timezone")
    when = when.astimezone(timezone.utc)
    status = provider_result.get("status")
    if status not in _PROVIDER_RUN_STATUSES:
        raise DecisionPolicyError("provider result status is invalid")
    provider = provider_result.get("provider", "")
    if not isinstance(provider, str):
        raise DecisionPolicyError("provider result provider must be a string")
    provider_status = provider_result.get("providerStatus")
    evidence_invalid = raw_trace is not None and not isinstance(
        raw_trace, Mapping
    )
    if provider_status is not None and not isinstance(provider_status, str):
        provider_status = None
        evidence_invalid = True
    trace_values: dict[str, str | None] = {}
    for name in ("traceId", "adapterId", "modelId"):
        value = trace.get(name)
        if value is not None and not isinstance(value, str):
            evidence_invalid = True
            value = None
        trace_values[name] = value
    for name in ("configHash", "inputHash", "outputHash"):
        value = trace.get(name)
        if not _optional_hash(value):
            evidence_invalid = True
            value = None
        trace_values[name] = value
    receipt: dict[str, Any] = {
        "schema": RECEIPT_SCHEMA,
        "policyId": normalized["id"],
        "policyVersion": normalized["version"],
        "policyHash": _canonical_hash(normalized),
        "questionId": normalized["questionId"],
        "answerKind": normalized["answerKind"],
        "outcome": "escalate",
        "reason": "",
        "escalationRoute": normalized["escalationRoute"],
        "provider": provider,
        "providerRunStatus": status,
        "providerStatus": provider_status,
        "modelId": trace_values["modelId"],
        "validationStatus": None,
        "choice": None,
        "value": None,
        "calibrated": None,
        "confidence": None,
        "labelMassCoverage": None,
        "rotationAgreement": None,
        "evidence": {
            "traceId": trace_values["traceId"],
            "adapterId": trace_values["adapterId"],
            "configHash": trace_values["configHash"],
            "inputHash": trace_values["inputHash"],
            "outputHash": trace_values["outputHash"],
            "answerHash": None,
        },
        "evaluatedAtUtc": when.isoformat().replace("+00:00", "Z"),
    }
    if evidence_invalid:
        return _escalate(receipt, normalized, "invalid_provider_evidence")
    if status != "Succeeded":
        return _escalate(receipt, normalized, "provider_not_succeeded")

    output = provider_result.get("output")
    if not isinstance(output, Mapping) or not _valid_judge_output(output):
        return _escalate(receipt, normalized, "invalid_provider_output")
    answers = output.get("answers", [])
    if not isinstance(answers, list):
        return _escalate(receipt, normalized, "invalid_provider_output")
    matches = [
        answer
        for answer in answers
        if isinstance(answer, Mapping)
        and answer.get("questionId") == normalized["questionId"]
    ]
    receipt["modelId"] = receipt["modelId"] or output.get("modelId")
    receipt["validationStatus"] = output.get(
        "validationStatus", "not_validated"
    )
    if not matches:
        return _escalate(receipt, normalized, "answer_missing")
    if len(matches) != 1:
        return _escalate(receipt, normalized, "answer_ambiguous")

    answer = _normalize_answer(matches[0])
    receipt["evidence"]["answerHash"] = _canonical_hash(answer)
    receipt["calibrated"] = answer["calibrated"]
    receipt["confidence"] = answer["confidence"]
    receipt["labelMassCoverage"] = answer["labelMassCoverage"]
    receipt["rotationAgreement"] = answer["rotationAgreement"]
    kind = normalized["answerKind"]
    if kind == "choice":
        receipt["choice"] = answer["choice"]
    elif kind == "probability":
        receipt["value"] = answer["probability"]
    else:
        receipt["value"] = answer["score"]

    if not _expected_shape(kind, answer):
        return _escalate(receipt, normalized, "answer_shape_mismatch")
    evidence_reason = _evidence_failure(
        normalized["evidence"], output, answer
    )
    if evidence_reason is not None:
        return _escalate(receipt, normalized, evidence_reason)
    if _matches(normalized["accept"], kind, answer):
        return _decide(receipt, "accept", "accepted")
    if _matches(normalized["reject"], kind, answer):
        return _decide(receipt, "reject", "rejected")
    return _escalate(receipt, normalized, "no_rule_match")


def _normalize_policy(policy: Mapping[str, Any]) -> dict[str, Any]:
    evidence = policy["evidence"]
    accept = policy["accept"]
    reject = policy["reject"]
    assert isinstance(evidence, Mapping)
    assert isinstance(accept, Mapping)
    assert isinstance(reject, Mapping)
    return {
        "schema": policy["schema"],
        "id": policy["id"],
        "version": policy["version"],
        "questionId": policy["questionId"],
        "answerKind": policy["answerKind"],
        "accept": _normalize_predicate(accept),
        "reject": _normalize_predicate(reject),
        "evidence": {
            "requireCalibrated": evidence.get("requireCalibrated", True),
            "minimumConfidence": evidence.get("minimumConfidence"),
            "minimumLabelMassCoverage": evidence.get(
                "minimumLabelMassCoverage"
            ),
            "minimumRotationAgreement": evidence.get(
                "minimumRotationAgreement"
            ),
            "allowedValidationStatuses": list(
                evidence.get("allowedValidationStatuses", [])
            ),
        },
        "escalationRoute": policy["escalationRoute"],
    }


def _normalize_predicate(predicate: Mapping[str, Any]) -> dict[str, Any]:
    values = predicate.get("values")
    return {
        "operator": predicate["operator"],
        "values": list(values) if isinstance(values, list) else [],
        "threshold": predicate.get("threshold"),
    }


def _normalize_answer(answer: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "questionId": answer.get("questionId", ""),
        "choice": answer.get("choice"),
        "probabilities": answer.get("probabilities"),
        "probability": answer.get("probability"),
        "score": answer.get("score"),
        "legend": answer.get("legend"),
        "confidence": answer.get("confidence"),
        "rawClassProbabilities": answer.get("rawClassProbabilities"),
        "labelMassCoverage": answer.get("labelMassCoverage"),
        "rawLabelProbabilities": answer.get("rawLabelProbabilities"),
        "rotationAgreement": answer.get("rotationAgreement"),
        "calibrated": answer.get("calibrated", False),
    }


def _validate_predicate(
    predicate: Mapping[str, Any],
    path: str,
    kind: Any,
    errors: list[str],
) -> None:
    values = predicate.get("values", [])
    if kind == "choice":
        if predicate.get("operator") != "in":
            errors.append(f"{path}.operator must be in for choice answers")
        if not isinstance(values, list) or not 1 <= len(values) <= _MAX_VALUES:
            errors.append(
                f"{path}.values must contain 1 through {_MAX_VALUES} choices"
            )
        elif any(not _valid_text(value) for value in values) or len(
            set(values)
        ) != len(values):
            errors.append(
                f"{path}.values must contain unique non-empty bounded choices"
            )
        if predicate.get("threshold") is not None:
            errors.append(f"{path}.threshold must be null for choice answers")
        return
    if predicate.get("operator") not in _NUMERIC_OPERATORS:
        errors.append(
            f"{path}.operator must be at_least or at_most for numeric answers"
        )
    if values not in ([], None):
        errors.append(f"{path}.values must be empty for numeric answers")
    threshold = predicate.get("threshold")
    if not _finite_number(threshold):
        errors.append(f"{path}.threshold must be a finite number")
        return
    assert isinstance(threshold, (int, float)) and not isinstance(
        threshold, bool
    )
    numeric_threshold = float(threshold)
    if kind == "probability" and not 0 <= numeric_threshold <= 1:
        errors.append(
            f"{path}.threshold must be in [0, 1] for probability answers"
        )
    elif kind == "score" and numeric_threshold < 0:
        errors.append(f"{path}.threshold must be non-negative for score answers")


def _valid_judge_output(output: Mapping[str, Any]) -> bool:
    answers = output.get("answers", [])
    if not isinstance(answers, list):
        return False
    validation_status = output.get("validationStatus", "not_validated")
    model_id = output.get("modelId")
    if not isinstance(validation_status, str):
        return False
    if model_id is not None and not isinstance(model_id, str):
        return False
    return all(
        isinstance(answer, Mapping) and _valid_answer(answer)
        for answer in answers
    )


def _valid_answer(answer: Mapping[str, Any]) -> bool:
    if not _valid_text(answer.get("questionId")):
        return False
    choice = answer.get("choice")
    legend = answer.get("legend")
    calibrated = answer.get("calibrated", False)
    if choice is not None and not isinstance(choice, str):
        return False
    if not isinstance(calibrated, bool):
        return False
    if legend is not None and (
        not isinstance(legend, Mapping)
        or any(
            not isinstance(key, str) or not isinstance(value, str)
            for key, value in legend.items()
        )
    ):
        return False
    for name in ("probability", "score"):
        value = answer.get(name)
        if value is not None and not _finite_number(value):
            return False
    for name in ("confidence", "labelMassCoverage", "rotationAgreement"):
        value = answer.get(name)
        if value is not None and (
            not _finite_number(value) or value < 0 or value > 1
        ):
            return False
    for name in (
        "probabilities",
        "rawClassProbabilities",
        "rawLabelProbabilities",
    ):
        values = answer.get(name)
        if values is not None and (
            not isinstance(values, Mapping)
            or any(
                not _valid_text(key)
                or not _finite_number(value)
                or value < 0
                or value > 1
                for key, value in values.items()
            )
        ):
            return False
    return True


def _evidence_failure(
    requirements: Mapping[str, Any],
    result: Mapping[str, Any],
    answer: Mapping[str, Any],
) -> str | None:
    allowed = requirements["allowedValidationStatuses"]
    if allowed and result.get("validationStatus", "not_validated") not in allowed:
        return "validation_status_not_allowed"
    if requirements["requireCalibrated"] and not answer["calibrated"]:
        return "uncalibrated"
    checks = (
        ("minimumConfidence", "confidence", "confidence"),
        (
            "minimumLabelMassCoverage",
            "labelMassCoverage",
            "label_mass_coverage",
        ),
        (
            "minimumRotationAgreement",
            "rotationAgreement",
            "rotation_agreement",
        ),
    )
    for requirement, observed, reason in checks:
        minimum = requirements[requirement]
        if minimum is None:
            continue
        if answer[observed] is None:
            return f"{reason}_unavailable"
        if answer[observed] < minimum:
            return f"{reason}_below_minimum"
    return None


def _expected_shape(kind: str, answer: Mapping[str, Any]) -> bool:
    if kind == "choice":
        return _valid_text(answer["choice"])
    if kind == "probability":
        return _finite_number(answer["probability"]) and (
            0 <= answer["probability"] <= 1
        )
    score = answer["score"]
    return _finite_number(score) and float(score) >= 0


def _matches(
    predicate: Mapping[str, Any],
    kind: str,
    answer: Mapping[str, Any],
) -> bool:
    if kind == "choice":
        return answer["choice"] in predicate["values"]
    value = answer["probability"] if kind == "probability" else answer["score"]
    threshold = predicate["threshold"]
    assert _finite_number(value)
    assert _finite_number(threshold)
    numeric_value = float(value)
    numeric_threshold = float(threshold)
    if predicate["operator"] == "at_least":
        return numeric_value >= numeric_threshold
    return numeric_value <= numeric_threshold


def _decide(
    receipt: dict[str, Any], outcome: str, reason: str
) -> dict[str, Any]:
    receipt["outcome"] = outcome
    receipt["reason"] = reason
    receipt["escalationRoute"] = None
    return receipt


def _escalate(
    receipt: dict[str, Any], policy: Mapping[str, Any], reason: str
) -> dict[str, Any]:
    receipt["outcome"] = "escalate"
    receipt["reason"] = reason
    receipt["escalationRoute"] = policy["escalationRoute"]
    return receipt


def _canonical_hash(value: Mapping[str, Any]) -> str:
    encoded = _canonical_json(value).encode("utf-8")
    return "sha256:" + sha256(encoded).hexdigest()


def _canonical_json(value: Any) -> str:
    if isinstance(value, Mapping):
        ordered = sorted(
            value,
            key=lambda item: item.encode(
                "utf-16-be", errors="surrogatepass"
            ),
        )
        return "{" + ",".join(
            json.dumps(key, ensure_ascii=True)
            + ":"
            + _canonical_json(value[key])
            for key in ordered
        ) + "}"
    if isinstance(value, list):
        return "[" + ",".join(_canonical_json(item) for item in value) + "]"
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        if not math.isfinite(value):
            raise DecisionPolicyError("decision evidence numbers must be finite")
        return _canonical_number(value)
    if isinstance(value, str):
        return json.dumps(value, ensure_ascii=True)
    raise DecisionPolicyError(
        f"unsupported decision evidence value: {type(value).__name__}"
    )


def _canonical_number(value: float) -> str:
    text = repr(value)
    if "e" not in text:
        return text[:-2] if text.endswith(".0") else text
    mantissa, exponent = text.split("e", 1)
    if mantissa.endswith(".0"):
        mantissa = mantissa[:-2]
    return f"{mantissa}e{int(exponent)}"


def _optional_hash(value: Any) -> bool:
    return value is None or (
        isinstance(value, str) and _SHA256_PATTERN.fullmatch(value) is not None
    )


def _finite_number(value: Any) -> bool:
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        return False
    try:
        return math.isfinite(value)
    except OverflowError:
        return False


def _valid_text(value: Any) -> bool:
    return (
        isinstance(value, str)
        and bool(value.strip())
        and len(value) <= _MAX_IDENTIFIER_LENGTH
    )


def _require_text(value: Any, path: str, errors: list[str]) -> None:
    if not _valid_text(value):
        errors.append(
            f"{path} must be non-empty and no longer than "
            f"{_MAX_IDENTIFIER_LENGTH} characters"
        )
