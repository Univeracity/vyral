from __future__ import annotations

from dataclasses import dataclass

from ._version import CONTRACT_VERSION, FIXTURE_VERSION, RUNTIME_VERSION
from .conformance import ConformanceError, run_bundled_goldens
from .contracts import (
    ContractBundleError,
    ContractBundleSummary,
    JSONValue,
    load_contract_bundle,
)
from .profiles import ProfileTuple, full_local_ready, profile_statuses
from .execution.conformance import (
    run_bundled_external_worker_scenario,
)
from .execution.native_conformance import (
    run_bundled_native_execution_scenario,
)
from .canonical.conformance import run_bundled_canonical_scenario


@dataclass(frozen=True)
class RuntimeReadiness:
    runtime_version: str
    contract_version: str
    fixture_version: str
    status: str
    maturity: str
    full_local_ready: bool
    contract: ContractBundleSummary | None
    profiles: ProfileTuple
    checks: tuple[dict[str, JSONValue], ...]
    warnings: tuple[str, ...]
    blockers: tuple[str, ...]

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "runtime": "python",
            "runtimeVersion": self.runtime_version,
            "contractVersion": self.contract_version,
            "fixtureVersion": self.fixture_version,
            "status": self.status,
            "maturity": self.maturity,
            "fullLocalReady": self.full_local_ready,
            "contract": self.contract.to_dict() if self.contract is not None else None,
            "profiles": [profile.to_dict() for profile in self.profiles],
            "checks": [dict(check) for check in self.checks],
            "warnings": list(self.warnings),
            "blockers": list(self.blockers),
        }


def get_readiness() -> RuntimeReadiness:
    profiles = profile_statuses()
    checks: list[dict[str, JSONValue]] = []
    warnings: list[str] = []
    blockers: list[str] = []
    contract: ContractBundleSummary | None = None

    try:
        contract = load_contract_bundle().summary
        checks.append(
            {
                "id": "contracts.bundle",
                "status": "passed",
                "message": "Canonical contract resources are internally consistent.",
            }
        )
    except ContractBundleError as exc:
        blockers.append(str(exc))
        checks.append(
            {
                "id": "contracts.bundle",
                "status": "failed",
                "message": str(exc),
            }
        )

    try:
        golden_results = run_bundled_goldens()
        checks.append(
            {
                "id": "conformance.goldens",
                "status": "passed",
                "message": (
                    f"{len(golden_results)} language-neutral golden "
                    "steps passed."
                ),
            }
        )
    except ConformanceError as exc:
        blockers.append(str(exc))
        checks.append(
            {
                "id": "conformance.goldens",
                "status": "failed",
                "message": str(exc),
            }
        )

    try:
        external_results = run_bundled_external_worker_scenario()
        checks.append(
            {
                "id": "conformance.external-worker",
                "status": "passed",
                "message": (
                    f"{len(external_results)} language-neutral external-worker "
                    "lifecycle steps passed."
                ),
            }
        )
    except ConformanceError as exc:
        blockers.append(str(exc))
        checks.append(
            {
                "id": "conformance.external-worker",
                "status": "failed",
                "message": str(exc),
            }
        )

    try:
        canonical_results = run_bundled_canonical_scenario()
        checks.append(
            {
                "id": "conformance.canonical",
                "status": "passed",
                "message": (
                    f"{len(canonical_results)} language-neutral CanonicalStore "
                    "strong-profile steps passed."
                ),
            }
        )
    except ConformanceError as exc:
        blockers.append(str(exc))
        checks.append(
            {
                "id": "conformance.canonical",
                "status": "failed",
                "message": str(exc),
            }
        )

    try:
        native_results = run_bundled_native_execution_scenario()
        checks.append(
            {
                "id": "conformance.native-execution",
                "status": "passed",
                "message": (
                    f"{len(native_results)} language-neutral native durable "
                    "execution lifecycle steps passed."
                ),
            }
        )
    except (ConformanceError, RuntimeError) as exc:
        blockers.append(str(exc))
        checks.append(
            {
                "id": "conformance.native-execution",
                "status": "failed",
                "message": str(exc),
            }
        )

    if not full_local_ready(profiles):
        warnings.append(
            "All portable local subsystems are implemented at prototype maturity; "
            "fullLocalReady remains false until every required profile is promoted "
            "through the supported platform and release qualification matrix."
        )

    return RuntimeReadiness(
        runtime_version=RUNTIME_VERSION,
        contract_version=CONTRACT_VERSION,
        fixture_version=FIXTURE_VERSION,
        status="ok" if not blockers else "blocked",
        maturity="prototype",
        full_local_ready=full_local_ready(profiles),
        contract=contract,
        profiles=profiles,
        checks=tuple(checks),
        warnings=tuple(warnings),
        blockers=tuple(blockers),
    )
