# Security Policy

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability or include credentials, access tokens,
customer records, or other sensitive material in a report. Use the repository's private security
advisory form instead:

<https://github.com/univeracity/vyral/security/advisories/new>

Include the affected version or commit, a minimal reproduction, impact, and any mitigation already
identified. Reports are acknowledged after review; disclosure timing is coordinated with affected
users when a fix is needed.

## Supported security posture

The maintained release line is the latest published version. Managed-cloud adapters require
deployment-owned least-privilege identities, secret rotation, encryption, monitoring, and backup
policies. Vyral does not accept credentials or provider connection strings in issue reports,
examples, or test fixtures.

## Continuous reassessment

The current server and external-worker container surfaces are rebuilt and scanned daily against
fresh vulnerability intelligence, even when their source has not changed. High or critical
findings and embedded secrets fail the scheduled gate, produce retained machine-readable evidence,
and are uploaded to GitHub code scanning for maintainer triage. Every pull request runs the same
focused gate before merge.

This recurring scan complements the release-integrity gate: release integrity evaluates a commit
at integration time, while continuous reassessment detects security intelligence that changes
after an otherwise unchanged release. Suspected vulnerabilities should still be reported privately
through the advisory form above rather than a public issue.

Critical workflow completions also feed a separate CI-health control. Failures are surfaced
immediately, while a three-hour audit verifies that the current default-branch commit has passing
CI, CodeQL, and release-integrity jobs and that scheduled CodeQL and container reassessment remain
fresh. The audit checks the expected job conclusions as well as the workflow conclusion so an
automation pause or silently skipped job cannot be mistaken for passing evidence.

Maintainers may configure `VYRAL_CI_ALERT_URL` as a private generic webhook and
`VYRAL_CI_HEARTBEAT_URL` as an external dead-man endpoint. Alert payloads contain only the
repository, workflow name, attention status, and GitHub run URL; findings and scan evidence remain
inside GitHub. The heartbeat is sent only after the complete schedule-health policy passes.
