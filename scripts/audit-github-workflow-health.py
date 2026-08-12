#!/usr/bin/env python3
"""Audit critical GitHub workflow results and scheduled-run liveness."""

from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import subprocess
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
SCHEMA_VERSION = "1.0"
HEAD_GRACE = timedelta(hours=2)
HEAD_POLICIES = (
    (
        "CI",
        "ci.yml",
        (
            "Select affected CI surfaces",
            "Build and test .NET",
            "Test language clients",
            "Verify contracts and public export",
        ),
    ),
    (
        "CodeQL",
        "codeql.yml",
        (
            "Select CodeQL languages",
            "Analyze csharp",
            "Analyze javascript-typescript",
            "Analyze python",
            "Analyze go",
        ),
    ),
    ("Release Integrity", "release-integrity.yml", ("Verify releasable artifacts",)),
)
SCHEDULE_POLICIES = (
    (
        "Container Security",
        "container-security.yml",
        timedelta(hours=36),
        ("Scan current container surfaces",),
    ),
    (
        "CodeQL",
        "codeql.yml",
        timedelta(days=8),
        (
            "Analyze csharp",
            "Analyze javascript-typescript",
            "Analyze python",
            "Analyze go",
        ),
    ),
)
PASSING_CONCLUSIONS = {"success", "neutral"}
IGNORED_IMMEDIATE_CONCLUSIONS = {"cancelled", "skipped"}


def _run(*arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        arguments,
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )


def _api(path: str) -> Any:
    result = _run("gh", "api", path)
    if result.returncode != 0:
        message = result.stderr.strip() or "GitHub API request failed."
        raise RuntimeError(f"Unable to read {path!r}: {message}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise RuntimeError(f"GitHub returned invalid JSON for {path!r}.") from error


def _parse_time(value: Any) -> datetime | None:
    if not isinstance(value, str) or not value.strip():
        return None
    normalized = value.strip()
    if normalized.endswith("Z"):
        normalized = normalized[:-1] + "+00:00"
    try:
        parsed = datetime.fromisoformat(normalized)
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def _runs(payload: Any) -> list[dict[str, Any]]:
    if not isinstance(payload, dict):
        return []
    values = payload.get("workflow_runs", [])
    if not isinstance(values, list):
        return []
    return [value for value in values if isinstance(value, dict)]


def _jobs(payload: Any) -> list[dict[str, Any]]:
    if not isinstance(payload, dict):
        return []
    values = payload.get("jobs", [])
    if not isinstance(values, list):
        return []
    return [value for value in values if isinstance(value, dict)]


def _job_evidence(
    run: dict[str, Any] | None,
    expected_names: tuple[str, ...],
    run_jobs: dict[str, Any],
) -> tuple[bool, dict[str, Any]]:
    run_id = run.get("id") if run is not None else None
    jobs = _jobs(run_jobs.get(str(run_id))) if run_id is not None else []
    conclusions = {
        str(job.get("name")): job.get("conclusion")
        for job in jobs
        if job.get("name")
    }
    missing = [name for name in expected_names if name not in conclusions]
    not_passing = [
        name
        for name in expected_names
        if name in conclusions and conclusions[name] not in PASSING_CONCLUSIONS
    ]
    return not missing and not not_passing, {
        "expected": list(expected_names),
        "observed": {
            name: conclusions[name]
            for name in expected_names
            if name in conclusions
        },
        "missing": missing,
        "notPassing": not_passing,
    }


def _run_evidence(run: dict[str, Any] | None) -> dict[str, Any]:
    if run is None:
        return {}
    return {
        "databaseId": run.get("id"),
        "event": run.get("event"),
        "status": run.get("status"),
        "conclusion": run.get("conclusion"),
        "headSha": run.get("head_sha"),
        "createdAtUtc": run.get("created_at"),
        "updatedAtUtc": run.get("updated_at"),
        "url": run.get("html_url"),
    }


def _check(
    name: str,
    status: str,
    message: str,
    evidence: dict[str, Any],
) -> dict[str, Any]:
    return {
        "name": name,
        "status": status,
        "message": message,
        "evidence": evidence,
    }


def _newest(runs: list[dict[str, Any]]) -> dict[str, Any] | None:
    if not runs:
        return None
    return max(
        runs,
        key=lambda run: _parse_time(run.get("created_at"))
        or datetime.min.replace(tzinfo=timezone.utc),
    )


def evaluate_snapshot(snapshot: dict[str, Any]) -> dict[str, Any]:
    now = _parse_time(snapshot.get("generatedAtUtc"))
    if now is None:
        raise ValueError("Snapshot generatedAtUtc must be an ISO-8601 timestamp.")
    head = snapshot.get("head")
    if not isinstance(head, dict) or not isinstance(head.get("sha"), str):
        raise ValueError("Snapshot head.sha is required.")
    head_sha = str(head["sha"])
    head_time = _parse_time(head.get("committedAtUtc"))
    if head_time is None:
        raise ValueError("Snapshot head.committedAtUtc is required.")
    head_age = max(now - head_time, timedelta(0))

    head_runs = snapshot.get("headRuns")
    schedule_runs = snapshot.get("scheduleRuns")
    latest_runs = snapshot.get("latestRuns")
    run_jobs = snapshot.get("runJobs")
    if not isinstance(head_runs, dict):
        raise ValueError("Snapshot headRuns must be an object.")
    if not isinstance(schedule_runs, dict):
        raise ValueError("Snapshot scheduleRuns must be an object.")
    if not isinstance(latest_runs, dict):
        raise ValueError("Snapshot latestRuns must be an object.")
    if not isinstance(run_jobs, dict):
        raise ValueError("Snapshot runJobs must be an object.")

    checks: list[dict[str, Any]] = []
    for display_name, workflow_file, expected_jobs in HEAD_POLICIES:
        run = _newest(_runs(head_runs.get(workflow_file)))
        jobs_passed, jobs = _job_evidence(run, expected_jobs, run_jobs)
        evidence = {
            "workflowFile": workflow_file,
            "requiredHeadSha": head_sha,
            "headAgeSeconds": round(head_age.total_seconds()),
            "run": _run_evidence(run),
            "jobs": jobs,
        }
        if (
            run is not None
            and run.get("conclusion") in PASSING_CONCLUSIONS
            and jobs_passed
        ):
            checks.append(
                _check(
                    f"head:{display_name}",
                    "passed",
                    "The current default-branch commit has a passing run.",
                    evidence,
                )
            )
        elif head_age <= HEAD_GRACE and (
            run is None or run.get("status") in {"queued", "in_progress", "pending"}
        ):
            checks.append(
                _check(
                    f"head:{display_name}",
                    "pending",
                    "The current commit remains inside the hosted-run grace period.",
                    evidence,
                )
            )
        else:
            checks.append(
                _check(
                    f"head:{display_name}",
                    "failed",
                    "The current default-branch commit lacks a passing run.",
                    evidence,
                )
            )

    for display_name, workflow_file, maximum_age, expected_jobs in SCHEDULE_POLICIES:
        scheduled = _newest(_runs(schedule_runs.get(workflow_file)))
        selected = scheduled
        source = "schedule"
        if selected is None:
            selected = _newest(_runs(latest_runs.get(workflow_file)))
            source = "bootstrap"
        created = _parse_time(selected.get("created_at")) if selected else None
        age = max(now - created, timedelta(0)) if created is not None else None
        jobs_passed, jobs = _job_evidence(selected, expected_jobs, run_jobs)
        evidence = {
            "workflowFile": workflow_file,
            "source": source,
            "maximumAgeSeconds": round(maximum_age.total_seconds()),
            "ageSeconds": round(age.total_seconds()) if age is not None else None,
            "run": _run_evidence(selected),
            "jobs": jobs,
        }
        if (
            selected is not None
            and selected.get("conclusion") in PASSING_CONCLUSIONS
            and jobs_passed
            and age is not None
            and age <= maximum_age
        ):
            message = (
                "The latest scheduled run is passing and fresh."
                if source == "schedule"
                else "A fresh passing run covers the first schedule window."
            )
            checks.append(
                _check(f"schedule:{display_name}", "passed", message, evidence)
            )
        else:
            checks.append(
                _check(
                    f"schedule:{display_name}",
                    "failed",
                    "The scheduled control is missing, stale, or not passing.",
                    evidence,
                )
            )

    failures = sum(check["status"] == "failed" for check in checks)
    pending = sum(check["status"] == "pending" for check in checks)
    status = "failed" if failures else ("pending" if pending else "passed")
    return {
        "schemaVersion": SCHEMA_VERSION,
        "generatedAtUtc": now.isoformat().replace("+00:00", "Z"),
        "repository": snapshot.get("repository"),
        "mode": "full",
        "status": status,
        "summary": {
            "checkCount": len(checks),
            "passedCount": sum(check["status"] == "passed" for check in checks),
            "pendingCount": pending,
            "failedCount": failures,
        },
        "checks": checks,
    }


def evaluate_workflow_run(event: dict[str, Any]) -> dict[str, Any]:
    workflow_run = event.get("workflow_run")
    if not isinstance(workflow_run, dict):
        raise ValueError("workflow_run event payload is required.")
    conclusion = workflow_run.get("conclusion")
    if conclusion in PASSING_CONCLUSIONS:
        status = "passed"
        message = "The monitored workflow completed successfully."
    elif conclusion in IGNORED_IMMEDIATE_CONCLUSIONS:
        status = "ignored"
        message = "The monitored workflow ended without an actionable failure."
    else:
        status = "failed"
        message = "The monitored workflow requires maintainer attention."
    now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    repository = event.get("repository")
    repository_name = (
        repository.get("full_name") if isinstance(repository, dict) else None
    )
    check = _check(
        f"workflow:{workflow_run.get('name') or 'unknown'}",
        status,
        message,
        _run_evidence(workflow_run),
    )
    return {
        "schemaVersion": SCHEMA_VERSION,
        "generatedAtUtc": now,
        "repository": repository_name,
        "mode": "workflow_run",
        "status": status,
        "summary": {
            "checkCount": 1,
            "passedCount": int(status == "passed"),
            "ignoredCount": int(status == "ignored"),
            "failedCount": int(status == "failed"),
        },
        "checks": [check],
    }


def collect_snapshot(repository: str) -> dict[str, Any]:
    repo = _api(f"repos/{repository}")
    if not isinstance(repo, dict) or not repo.get("default_branch"):
        raise RuntimeError("GitHub repository metadata omitted the default branch.")
    branch = str(repo["default_branch"])
    head = _api(f"repos/{repository}/commits/{branch}")
    if not isinstance(head, dict) or not head.get("sha"):
        raise RuntimeError("GitHub commit metadata omitted the default-branch SHA.")
    commit = head.get("commit")
    committer = commit.get("committer") if isinstance(commit, dict) else None
    committed_at = committer.get("date") if isinstance(committer, dict) else None
    if not committed_at:
        raise RuntimeError("GitHub commit metadata omitted the commit timestamp.")
    head_sha = str(head["sha"])

    workflow_files = {
        workflow_file for _, workflow_file, _ in HEAD_POLICIES
    } | {workflow_file for _, workflow_file, _, _ in SCHEDULE_POLICIES}
    head_policy_files = {workflow_file for _, workflow_file, _ in HEAD_POLICIES}
    schedule_policy_files = {
        workflow_file for _, workflow_file, _, _ in SCHEDULE_POLICIES
    }
    head_runs: dict[str, Any] = {}
    schedule_runs: dict[str, Any] = {}
    latest_runs: dict[str, Any] = {}
    for workflow_file in sorted(workflow_files):
        if workflow_file in head_policy_files:
            head_runs[workflow_file] = _api(
                f"repos/{repository}/actions/workflows/{workflow_file}/runs"
                f"?branch={branch}&head_sha={head_sha}&per_page=10"
            )
        if workflow_file in schedule_policy_files:
            schedule_runs[workflow_file] = _api(
                f"repos/{repository}/actions/workflows/{workflow_file}/runs"
                f"?branch={branch}&event=schedule&per_page=1"
            )
            latest_runs[workflow_file] = _api(
                f"repos/{repository}/actions/workflows/{workflow_file}/runs"
                f"?branch={branch}&per_page=1"
            )
    selected_runs: dict[str, dict[str, Any]] = {}
    for payload in list(head_runs.values()) + list(schedule_runs.values()):
        run = _newest(_runs(payload))
        if run is not None and run.get("id") is not None:
            selected_runs[str(run["id"])] = run
    for workflow_file, payload in latest_runs.items():
        if _runs(schedule_runs.get(workflow_file)):
            continue
        run = _newest(_runs(payload))
        if run is not None and run.get("id") is not None:
            selected_runs[str(run["id"])] = run
    run_jobs = {
        run_id: _api(
            f"repos/{repository}/actions/runs/{run_id}/jobs?per_page=100"
        )
        for run_id in sorted(selected_runs)
    }
    return {
        "generatedAtUtc": datetime.now(timezone.utc)
        .isoformat()
        .replace("+00:00", "Z"),
        "repository": repository,
        "head": {"sha": head_sha, "committedAtUtc": committed_at},
        "headRuns": head_runs,
        "scheduleRuns": schedule_runs,
        "latestRuns": latest_runs,
        "runJobs": run_jobs,
    }


def _write_report(report: dict[str, Any], output: Path) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(report, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )


def _write_summary(report: dict[str, Any], path: Path) -> None:
    lines = [
        "## CI feedback",
        "",
        f"Overall status: **{report['status']}**",
        "",
        "| Control | Status | Detail |",
        "| --- | --- | --- |",
    ]
    for check in report["checks"]:
        lines.append(
            f"| `{check['name']}` | {check['status']} | {check['message']} |"
        )
    lines.append("")
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as summary:
        summary.write("\n".join(lines) + "\n")


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("artifacts/ci-feedback/workflow-health.json"),
    )
    parser.add_argument("--summary", type=Path)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--repository")
    source.add_argument("--snapshot", type=Path)
    source.add_argument("--workflow-run-event", type=Path)
    return parser


def main() -> int:
    arguments = _parser().parse_args()
    if arguments.workflow_run_event is not None:
        event = json.loads(arguments.workflow_run_event.read_text(encoding="utf-8"))
        report = evaluate_workflow_run(event)
    else:
        snapshot = (
            collect_snapshot(arguments.repository)
            if arguments.repository is not None
            else json.loads(arguments.snapshot.read_text(encoding="utf-8"))
        )
        report = evaluate_snapshot(snapshot)
    _write_report(report, arguments.output)
    if arguments.summary is not None:
        _write_summary(report, arguments.summary)
    print(
        f"ci-feedback={report['status']} mode={report['mode']} "
        f"checks={report['summary']['checkCount']} "
        f"failures={report['summary']['failedCount']}"
    )
    return 1 if report["status"] == "failed" else 0


if __name__ == "__main__":
    raise SystemExit(main())
