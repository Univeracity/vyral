#!/usr/bin/env python3
"""Regression checks for the GitHub workflow-health policy."""

from __future__ import annotations

import importlib.util
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "scripts/audit-github-workflow-health.py"
SPEC = importlib.util.spec_from_file_location(
    "vyral_audit_github_workflow_health", MODULE_PATH
)
if SPEC is None or SPEC.loader is None:
    raise SystemExit("Unable to load the workflow-health audit.")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def _run(
    *,
    run_id: int,
    name: str,
    created: str,
    conclusion: str | None = "success",
    status: str = "completed",
    event: str = "push",
    sha: str = "head-sha",
) -> dict[str, object]:
    return {
        "id": run_id,
        "name": name,
        "created_at": created,
        "updated_at": created,
        "conclusion": conclusion,
        "status": status,
        "event": event,
        "head_sha": sha,
        "html_url": f"https://github.test/runs/{run_id}",
    }


def _payload(*runs: dict[str, object]) -> dict[str, object]:
    return {"total_count": len(runs), "workflow_runs": list(runs)}


def _job_payload(*names: str, conclusion: str = "success") -> dict[str, object]:
    return {
        "total_count": len(names),
        "jobs": [
            {"id": index, "name": name, "conclusion": conclusion}
            for index, name in enumerate(names, start=1)
        ],
    }


def _snapshot() -> dict[str, object]:
    head_run_time = "2026-08-12T11:00:00Z"
    return {
        "generatedAtUtc": "2026-08-12T12:00:00Z",
        "repository": "Univeracity/vyral",
        "head": {
            "sha": "head-sha",
            "committedAtUtc": "2026-08-12T10:30:00Z",
        },
        "headRuns": {
            "ci.yml": _payload(
                _run(run_id=1, name="CI", created=head_run_time)
            ),
            "codeql.yml": _payload(
                _run(run_id=2, name="CodeQL", created=head_run_time)
            ),
            "release-integrity.yml": _payload(
                _run(run_id=3, name="Release Integrity", created=head_run_time)
            ),
        },
        "scheduleRuns": {
            "container-security.yml": _payload(
                _run(
                    run_id=4,
                    name="Container Security",
                    created="2026-08-12T03:37:00Z",
                    event="schedule",
                )
            ),
            "codeql.yml": _payload(
                _run(
                    run_id=5,
                    name="CodeQL",
                    created="2026-08-11T04:17:00Z",
                    event="schedule",
                )
            ),
        },
        "latestRuns": {
            "container-security.yml": _payload(),
            "codeql.yml": _payload(),
        },
        "runJobs": {
            "1": _job_payload(
                "Select affected CI surfaces",
                "Build and test .NET",
                "Test language clients",
                "Verify contracts and public export",
            ),
            "2": _job_payload(
                "Select CodeQL languages",
                "Analyze csharp",
                "Analyze javascript-typescript",
                "Analyze python",
                "Analyze go",
            ),
            "3": _job_payload("Verify releasable artifacts"),
            "4": _job_payload("Scan current container surfaces"),
            "5": _job_payload(
                "Analyze csharp",
                "Analyze javascript-typescript",
                "Analyze python",
                "Analyze go",
            ),
        },
    }


def main() -> int:
    workflow = (ROOT / ".github/workflows/ci-feedback.yml").read_text(
        encoding="utf-8"
    )
    for required_hook in (
        "workflow_run:",
        'cron: "13 */3 * * *"',
        "scripts/audit-github-workflow-health.py",
        "github.event.repository.default_branch",
        "VYRAL_CI_ALERT_URL",
        "VYRAL_CI_HEARTBEAT_URL",
        "steps.health.outputs.status == 'passed'",
        "retention-days: 30",
    ):
        if required_hook not in workflow:
            raise SystemExit(
                f"CI feedback workflow is missing {required_hook!r}."
            )
    for monitored_workflow in (
        "CI",
        "CodeQL",
        "Container Security",
        "Release Integrity",
        "Python Runtime Qualification",
        "Temporal Container Qualification",
    ):
        if f"      - {monitored_workflow}\n" not in workflow:
            raise SystemExit(
                f"CI feedback no longer watches {monitored_workflow!r}."
            )

    healthy = MODULE.evaluate_snapshot(_snapshot())
    if healthy["status"] != "passed" or healthy["summary"] != {
        "checkCount": 5,
        "passedCount": 5,
        "pendingCount": 0,
        "failedCount": 0,
    }:
        raise SystemExit(f"Healthy workflow snapshot was rejected: {healthy}")

    failed_head = _snapshot()
    failed_head["head"]["committedAtUtc"] = "2026-08-12T08:00:00Z"
    failed_head["headRuns"]["release-integrity.yml"] = _payload(
        _run(
            run_id=6,
            name="Release Integrity",
            created="2026-08-12T08:05:00Z",
            conclusion="failure",
        )
    )
    failed_head["runJobs"]["6"] = _job_payload(
        "Verify releasable artifacts", conclusion="failure"
    )
    failed = MODULE.evaluate_snapshot(failed_head)
    if failed["status"] != "failed" or failed["summary"]["failedCount"] != 1:
        raise SystemExit("A failed current-head gate did not degrade CI health.")

    pending_head = _snapshot()
    pending_head["headRuns"]["ci.yml"] = _payload(
        _run(
            run_id=7,
            name="CI",
            created="2026-08-12T11:30:00Z",
            conclusion=None,
            status="in_progress",
        )
    )
    pending_head["runJobs"]["7"] = _job_payload(
        "Select affected CI surfaces",
        "Build and test .NET",
        "Test language clients",
        "Verify contracts and public export",
        conclusion="failure",
    )
    pending = MODULE.evaluate_snapshot(pending_head)
    if pending["status"] != "pending" or pending["summary"]["pendingCount"] != 1:
        raise SystemExit("A fresh in-progress gate must remain pending, not fail.")

    stale_schedule = _snapshot()
    stale_schedule["scheduleRuns"]["container-security.yml"] = _payload(
        _run(
            run_id=8,
            name="Container Security",
            created="2026-08-10T03:37:00Z",
            event="schedule",
        )
    )
    stale_schedule["runJobs"]["8"] = _job_payload(
        "Scan current container surfaces"
    )
    stale = MODULE.evaluate_snapshot(stale_schedule)
    if stale["status"] != "failed" or stale["summary"]["failedCount"] != 1:
        raise SystemExit("A stale scheduled security gate was accepted.")

    bootstrap = _snapshot()
    bootstrap["scheduleRuns"]["container-security.yml"] = _payload()
    bootstrap["latestRuns"]["container-security.yml"] = _payload(
        _run(
            run_id=9,
            name="Container Security",
            created="2026-08-12T11:00:00Z",
        )
    )
    bootstrap["runJobs"]["9"] = _job_payload(
        "Scan current container surfaces"
    )
    bootstrapped = MODULE.evaluate_snapshot(bootstrap)
    bootstrap_check = next(
        check
        for check in bootstrapped["checks"]
        if check["name"] == "schedule:Container Security"
    )
    if bootstrapped["status"] != "passed" or (
        bootstrap_check["evidence"]["source"] != "bootstrap"
    ):
        raise SystemExit("A fresh first-window run did not bootstrap the schedule.")

    skipped_jobs = _snapshot()
    skipped_jobs["runJobs"]["4"] = _job_payload(
        "Scan current container surfaces", conclusion="skipped"
    )
    skipped = MODULE.evaluate_snapshot(skipped_jobs)
    if skipped["status"] != "failed" or skipped["summary"]["failedCount"] != 1:
        raise SystemExit("A silently skipped scheduled gate was accepted.")

    event = {
        "repository": {"full_name": "Univeracity/vyral"},
        "workflow_run": _run(
            run_id=10,
            name="CodeQL",
            created="2026-08-12T11:00:00Z",
            conclusion="timed_out",
        ),
    }
    immediate = MODULE.evaluate_workflow_run(event)
    if immediate["status"] != "failed":
        raise SystemExit("An immediate timed-out workflow did not request attention.")
    event["workflow_run"]["conclusion"] = "cancelled"
    ignored = MODULE.evaluate_workflow_run(event)
    if ignored["status"] != "ignored":
        raise SystemExit("Concurrency cancellation should not create alert noise.")

    print("github-workflow-health-test=ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
