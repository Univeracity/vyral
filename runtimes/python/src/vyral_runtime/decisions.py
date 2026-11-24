"""Portable, provider-neutral branch policy evaluation for ``ai.judge`` results."""

from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
import json
import math
import re
from typing import Any, Mapping, Sequence


POLICY_SCHEMA = "vyral.ai-decision-policy.v1"
RECEIPT_SCHEMA = "vyral.ai-decision-receipt.v1"
LEDGER_ENTRY_SCHEMA = "vyral.ai-decision-ledger-entry.v1"
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
_UTC_TIMESTAMP_PATTERN = re.compile(
    r"(?P<date>\d{4}-\d{2}-\d{2})T"
    r"(?P<time>\d{2}:\d{2}:\d{2})"
    r"(?:\.(?P<fraction>\d{1,7}))?"
    r"(?P<offset>Z|\+00:00|-00:00)\Z"
)
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
_RECEIPT_KEYS = frozenset(
    {
        "schema",
        "policyId",
        "policyVersion",
        "policyHash",
        "questionId",
        "answerKind",
        "outcome",
        "reason",
        "escalationRoute",
        "provider",
        "providerRunStatus",
        "providerStatus",
        "modelId",
        "validationStatus",
        "choice",
        "value",
        "calibrated",
        "confidence",
        "labelMassCoverage",
        "rotationAgreement",
        "evidence",
        "evaluatedAtUtc",
    }
)
_RECEIPT_REQUIRED_KEYS = frozenset(
    {
        "schema",
        "policyId",
        "policyVersion",
        "policyHash",
        "questionId",
        "answerKind",
        "outcome",
        "reason",
        "provider",
        "providerRunStatus",
        "evidence",
        "evaluatedAtUtc",
    }
)
_RECEIPT_EVIDENCE_KEYS = frozenset(
    {
        "traceId",
        "adapterId",
        "configHash",
        "inputHash",
        "outputHash",
        "answerHash",
    }
)
_DECISION_REASONS = frozenset(
    {
        "accepted",
        "rejected",
        "no_rule_match",
        "provider_not_succeeded",
        "invalid_provider_evidence",
        "invalid_provider_output",
        "answer_missing",
        "answer_ambiguous",
        "answer_shape_mismatch",
        "uncalibrated",
        "confidence_unavailable",
        "confidence_below_minimum",
        "label_mass_coverage_unavailable",
        "label_mass_coverage_below_minimum",
        "rotation_agreement_unavailable",
        "rotation_agreement_below_minimum",
        "validation_status_not_allowed",
    }
)
_LEDGER_ENTRY_KEYS = frozenset(
    {
        "schema",
        "sequence",
        "recordedAtUtc",
        "receipt",
        "receiptHash",
        "previousEntryHash",
        "entryHash",
    }
)
_MAX_SAFE_SEQUENCE = 9_007_199_254_740_991


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


def validate_decision_receipt(
    receipt: Mapping[str, Any] | None,
) -> tuple[str, ...]:
    """Validate a portable decision receipt without trusting its producer."""
    errors: list[str] = []
    if receipt is None or not isinstance(receipt, Mapping):
        return ("receipt is required",)
    unknown = set(receipt).difference(_RECEIPT_KEYS)
    missing = _RECEIPT_REQUIRED_KEYS.difference(receipt)
    if unknown:
        errors.append("receipt contains unknown properties")
    if missing:
        errors.append("receipt is missing required properties")
    if receipt.get("schema") != RECEIPT_SCHEMA:
        errors.append(f"schema must be {RECEIPT_SCHEMA}")
    for name in ("policyId", "policyVersion", "questionId"):
        _require_text(receipt.get(name), name, errors)
    provider = receipt.get("provider")
    if not isinstance(provider, str) or len(provider) > _MAX_IDENTIFIER_LENGTH:
        errors.append(
            "provider must be a string no longer than "
            f"{_MAX_IDENTIFIER_LENGTH} characters"
        )
    if not _required_hash(receipt.get("policyHash")):
        errors.append("policyHash must be a canonical sha256 digest")
    kind = receipt.get("answerKind")
    if kind not in _ANSWER_KINDS:
        errors.append("answerKind must be choice, probability, or score")
    outcome = receipt.get("outcome")
    if outcome not in {"accept", "reject", "escalate"}:
        errors.append("outcome must be accept, reject, or escalate")
    reason = receipt.get("reason")
    if reason not in _DECISION_REASONS:
        errors.append("reason is not recognized")
    status = receipt.get("providerRunStatus")
    if status not in _PROVIDER_RUN_STATUSES:
        errors.append("providerRunStatus is invalid")

    for name in (
        "escalationRoute",
        "providerStatus",
        "modelId",
        "validationStatus",
        "choice",
    ):
        value = receipt.get(name)
        if value is not None and (
            not isinstance(value, str)
            or not value.strip()
            or len(value) > _MAX_IDENTIFIER_LENGTH
        ):
            errors.append(
                f"{name} must be null or a non-empty string no longer than "
                f"{_MAX_IDENTIFIER_LENGTH} characters"
            )
    route = receipt.get("escalationRoute")
    if outcome == "accept":
        if reason != "accepted":
            errors.append("accept outcome requires reason accepted")
        if route is not None:
            errors.append("accept outcome cannot include escalationRoute")
    elif outcome == "reject":
        if reason != "rejected":
            errors.append("reject outcome requires reason rejected")
        if route is not None:
            errors.append("reject outcome cannot include escalationRoute")
    elif outcome == "escalate":
        if reason in {"accepted", "rejected"}:
            errors.append("escalate outcome cannot use a terminal branch reason")
        if not _valid_text(route):
            errors.append("escalate outcome requires escalationRoute")

    choice = receipt.get("choice")
    value = receipt.get("value")
    if kind == "choice":
        if value is not None:
            errors.append("choice receipt value must be null")
        if outcome in {"accept", "reject"} and not _valid_text(choice):
            errors.append("terminal choice receipt requires choice")
    elif kind in {"probability", "score"}:
        if choice is not None:
            errors.append("numeric receipt choice must be null")
        if value is not None and not _finite_number(value):
            errors.append("numeric receipt value must be null or finite")
        elif _finite_number(value):
            assert isinstance(value, (int, float)) and not isinstance(
                value, bool
            )
            numeric = float(value)
            if kind == "probability" and not 0 <= numeric <= 1:
                errors.append("probability receipt value must be in [0, 1]")
            if kind == "score" and numeric < 0:
                errors.append("score receipt value must be non-negative")
        if outcome in {"accept", "reject"} and not _finite_number(value):
            errors.append("terminal numeric receipt requires value")

    calibrated = receipt.get("calibrated")
    if calibrated is not None and not isinstance(calibrated, bool):
        errors.append("calibrated must be null or a boolean")
    for name in (
        "confidence",
        "labelMassCoverage",
        "rotationAgreement",
    ):
        metric = receipt.get(name)
        if metric is not None and (
            not _finite_number(metric) or not 0 <= float(metric) <= 1
        ):
            errors.append(f"{name} must be null or in [0, 1]")

    evidence = receipt.get("evidence")
    if not isinstance(evidence, Mapping):
        errors.append("evidence is required")
    else:
        if set(evidence).difference(_RECEIPT_EVIDENCE_KEYS):
            errors.append("evidence contains unknown properties")
        for name in ("traceId", "adapterId"):
            observed = evidence.get(name)
            if observed is not None and (
                not isinstance(observed, str)
                or not observed.strip()
                or len(observed) > _MAX_IDENTIFIER_LENGTH
            ):
                errors.append(
                    f"evidence.{name} must be null or a non-empty bounded string"
                )
        for name in (
            "configHash",
            "inputHash",
            "outputHash",
            "answerHash",
        ):
            if not _optional_hash(evidence.get(name)):
                errors.append(
                    f"evidence.{name} must be null or a canonical sha256 digest"
                )
    try:
        _normalize_utc_timestamp(receipt.get("evaluatedAtUtc"))
    except DecisionPolicyError as error:
        errors.append(f"evaluatedAtUtc {error}")
    return tuple(errors)


def compute_decision_receipt_hash(receipt: Mapping[str, Any]) -> str:
    """Hash a normalized, privacy-minimized decision receipt."""
    return _canonical_hash(_normalize_receipt(receipt))


def create_decision_ledger_entry(
    receipt: Mapping[str, Any],
    *,
    sequence: int,
    previous_entry_hash: str | None,
    recorded_at: datetime | None = None,
) -> dict[str, Any]:
    """Create one portable hash-linked ledger entry from a receipt."""
    _validate_sequence_and_previous(sequence, previous_entry_hash)
    normalized_receipt = _normalize_receipt(receipt)
    when = recorded_at or datetime.now(timezone.utc)
    recorded_at_utc = _format_utc_timestamp(when)
    entry: dict[str, Any] = {
        "schema": LEDGER_ENTRY_SCHEMA,
        "sequence": sequence,
        "recordedAtUtc": recorded_at_utc,
        "receipt": normalized_receipt,
        "receiptHash": _canonical_hash(normalized_receipt),
        "previousEntryHash": previous_entry_hash,
        "entryHash": "",
    }
    entry["entryHash"] = _canonical_hash(_ledger_entry_material(entry))
    return entry


def validate_decision_ledger_entry(
    entry: Mapping[str, Any] | None,
) -> tuple[str, ...]:
    """Validate one ledger entry, including both canonical hashes."""
    errors: list[str] = []
    if entry is None or not isinstance(entry, Mapping):
        return ("entry is required",)
    if set(entry) != _LEDGER_ENTRY_KEYS:
        errors.append("entry must contain exactly the ledger entry properties")
    if entry.get("schema") != LEDGER_ENTRY_SCHEMA:
        errors.append(f"schema must be {LEDGER_ENTRY_SCHEMA}")
    sequence = entry.get("sequence")
    previous = entry.get("previousEntryHash")
    try:
        _validate_sequence_and_previous(sequence, previous)
    except DecisionPolicyError as error:
        errors.append(str(error))
    recorded = entry.get("recordedAtUtc")
    try:
        normalized_recorded = _normalize_utc_timestamp(recorded)
        if recorded != normalized_recorded:
            errors.append("recordedAtUtc must use canonical UTC form")
    except DecisionPolicyError as error:
        errors.append(f"recordedAtUtc {error}")
    receipt = entry.get("receipt")
    receipt_errors = validate_decision_receipt(
        receipt if isinstance(receipt, Mapping) else None
    )
    errors.extend(f"receipt.{error}" for error in receipt_errors)
    receipt_hash = entry.get("receiptHash")
    entry_hash = entry.get("entryHash")
    if not _required_hash(receipt_hash):
        errors.append("receiptHash must be a canonical sha256 digest")
    if not _required_hash(entry_hash):
        errors.append("entryHash must be a canonical sha256 digest")
    if errors:
        return tuple(errors)
    assert isinstance(receipt, Mapping)
    expected_receipt_hash = compute_decision_receipt_hash(receipt)
    if receipt_hash != expected_receipt_hash:
        errors.append("receiptHash does not match receipt")
    expected_entry_hash = _canonical_hash(_ledger_entry_material(entry))
    if entry_hash != expected_entry_hash:
        errors.append("entryHash does not match entry")
    return tuple(errors)


def verify_decision_ledger_chain(
    entries: Sequence[Mapping[str, Any]],
) -> tuple[str, ...]:
    """Verify a complete zero-based decision-ledger chain."""
    errors: list[str] = []
    previous: str | None = None
    for index, entry in enumerate(entries):
        entry_errors = validate_decision_ledger_entry(entry)
        errors.extend(f"entry[{index}].{error}" for error in entry_errors)
        if entry.get("sequence") != index:
            errors.append(f"entry[{index}].sequence is not contiguous")
        if entry.get("previousEntryHash") != previous:
            errors.append(f"entry[{index}].previousEntryHash does not match")
        observed = entry.get("entryHash")
        previous = observed if isinstance(observed, str) else None
    return tuple(errors)


def replay_decision_receipt(
    receipt: Mapping[str, Any],
    policy: Mapping[str, Any],
    provider_result: Mapping[str, Any],
) -> dict[str, Any]:
    """Re-evaluate caller-supplied evidence and compare the normalized receipt."""
    receipt_errors = validate_decision_receipt(receipt)
    if receipt_errors:
        raise DecisionPolicyError("; ".join(receipt_errors))
    evaluated_at = _parse_utc_timestamp(receipt["evaluatedAtUtc"])
    replayed = evaluate_decision_policy(
        policy,
        provider_result,
        evaluated_at=evaluated_at,
    )
    expected_hash = compute_decision_receipt_hash(receipt)
    actual_hash = compute_decision_receipt_hash(replayed)
    return {
        "matches": expected_hash == actual_hash,
        "expectedReceiptHash": expected_hash,
        "actualReceiptHash": actual_hash,
        "receipt": replayed,
    }


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


def _normalize_receipt(receipt: Mapping[str, Any]) -> dict[str, Any]:
    errors = validate_decision_receipt(receipt)
    if errors:
        raise DecisionPolicyError("; ".join(errors))
    evidence = receipt["evidence"]
    assert isinstance(evidence, Mapping)
    return {
        "schema": RECEIPT_SCHEMA,
        "policyId": receipt["policyId"],
        "policyVersion": receipt["policyVersion"],
        "policyHash": receipt["policyHash"],
        "questionId": receipt["questionId"],
        "answerKind": receipt["answerKind"],
        "outcome": receipt["outcome"],
        "reason": receipt["reason"],
        "escalationRoute": receipt.get("escalationRoute"),
        "provider": receipt["provider"],
        "providerRunStatus": receipt["providerRunStatus"],
        "providerStatus": receipt.get("providerStatus"),
        "modelId": receipt.get("modelId"),
        "validationStatus": receipt.get("validationStatus"),
        "choice": receipt.get("choice"),
        "value": receipt.get("value"),
        "calibrated": receipt.get("calibrated"),
        "confidence": receipt.get("confidence"),
        "labelMassCoverage": receipt.get("labelMassCoverage"),
        "rotationAgreement": receipt.get("rotationAgreement"),
        "evidence": {
            "traceId": evidence.get("traceId"),
            "adapterId": evidence.get("adapterId"),
            "configHash": evidence.get("configHash"),
            "inputHash": evidence.get("inputHash"),
            "outputHash": evidence.get("outputHash"),
            "answerHash": evidence.get("answerHash"),
        },
        "evaluatedAtUtc": _normalize_utc_timestamp(
            receipt["evaluatedAtUtc"]
        ),
    }


def _ledger_entry_material(entry: Mapping[str, Any]) -> dict[str, Any]:
    receipt = entry.get("receipt")
    if not isinstance(receipt, Mapping):
        raise DecisionPolicyError("entry receipt is required")
    return {
        "schema": LEDGER_ENTRY_SCHEMA,
        "sequence": entry.get("sequence"),
        "recordedAtUtc": entry.get("recordedAtUtc"),
        "receipt": _normalize_receipt(receipt),
        "receiptHash": entry.get("receiptHash"),
        "previousEntryHash": entry.get("previousEntryHash"),
    }


def _validate_sequence_and_previous(
    sequence: Any, previous_entry_hash: Any
) -> None:
    if (
        not isinstance(sequence, int)
        or isinstance(sequence, bool)
        or not 0 <= sequence <= _MAX_SAFE_SEQUENCE
    ):
        raise DecisionPolicyError(
            f"sequence must be an integer in [0, {_MAX_SAFE_SEQUENCE}]"
        )
    if sequence == 0:
        if previous_entry_hash is not None:
            raise DecisionPolicyError(
                "genesis entry previousEntryHash must be null"
            )
    elif not _required_hash(previous_entry_hash):
        raise DecisionPolicyError(
            "non-genesis entry previousEntryHash must be a canonical sha256 digest"
        )


def _parse_utc_timestamp(value: Any) -> datetime:
    if not isinstance(value, str):
        raise DecisionPolicyError("must be a UTC RFC 3339 string")
    match = _UTC_TIMESTAMP_PATTERN.fullmatch(value)
    if match is None:
        raise DecisionPolicyError("must be a UTC RFC 3339 string")
    fraction = (match.group("fraction") or "").ljust(7, "0")
    if fraction[6] != "0":
        raise DecisionPolicyError(
            "cannot be more precise than one microsecond"
        )
    normalized = (
        f"{match.group('date')}T{match.group('time')}."
        f"{fraction[:6]}+00:00"
    )
    try:
        parsed = datetime.fromisoformat(normalized)
    except ValueError as error:
        raise DecisionPolicyError("must be a valid UTC timestamp") from error
    return parsed.astimezone(timezone.utc)


def _format_utc_timestamp(value: datetime) -> str:
    if value.tzinfo is None:
        raise DecisionPolicyError("timestamp must include a timezone")
    selected = value.astimezone(timezone.utc)
    return selected.strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def _normalize_utc_timestamp(value: Any) -> str:
    return _format_utc_timestamp(_parse_utc_timestamp(value))


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


def _required_hash(value: Any) -> bool:
    return (
        isinstance(value, str)
        and _SHA256_PATTERN.fullmatch(value) is not None
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
