# Release Process

Each release candidate must be built from a reviewed commit in protected CI. Before publication:

Pull requests run a fast release-boundary gate covering publication authorization, versions, public
SDK and schema policy, secret and ownership scans, and the deterministic public export. They do not
claim releasable-artifact evidence. Full package consumers, SBOM generation, runtime security,
container qualification, vulnerability scanning, and deterministic regression suites run again on
canonical `main`, release tags, and explicit release rehearsals. Only that full canonical run may be
retained as release evidence.

1. Create both ignored operator denylists described below and run
   `scripts/verify-public-export.sh --release` from a clean commit, then run
   `scripts/verify-release-artifacts.sh`; retain the public-export manifest, generated package artifacts, CycloneDX
   SBOM, and `qualification/adapter-qualification.json` as build evidence.
2. Review dependency, secret, and release-ownership scan results, the SBOM and third-party
   notices, package metadata, adapter qualification matrix, and clean consumer validation for
   .NET, JavaScript, Python, and Go.
3. Record contract additions, behavior changes, qualification changes, migrations, and known
   limits in the release notes. Package availability or an advertised capability must not be
   presented as `live_qualified` without a current live receipt in the release artifact.
   The canonical .NET MCP host additionally requires a passing frozen `2026-07-28` requirements
   receipt, two-process round-robin/failover/recovery evidence, and the production-container MCP
   receipt whose loaded image config digest matches the retained attested OCI archive. The
   execution-smoke worker requires the equivalent packaged-container receipt. Both loaded images
   must also pass the release workflow's pinned Trivy HIGH/CRITICAL vulnerability and embedded-secret
   gate, with the JSON results retained alongside the receipts. The official
   runner's extension and pending scenarios remain visible evidence but are
   not part of the frozen conformance score. For the pinned alpha.11 runner, the eight failing task
   diagnostics are generic core-schema rejections of the task extension envelope after their
   functional checks pass; the ninth is an unimplemented pending JSON-Schema diagnostic fixture.
   The Python runtime must likewise retain a passing frozen `2026-07-28` requirements receipt
   produced from its packaged wheel; conformance diagnostics must remain explicitly test-only
   and `VYRAL_MCP_CONFORMANCE_DIAGNOSTICS` must not be present in a release deployment.
4. Publish packages only through a trusted-publishing or OIDC-backed registry configuration. Do
   not place long-lived registry tokens in the repository or workflow files.
5. Attach provenance/attestations and SBOMs to the published release; publish container images with
   build provenance and SBOM attestations enabled.
6. Before any visibility change, scan every reachable Git ref as well as the current tree. Create
   an ignored, one-pattern-per-line `.release-history-denylist` containing the private vocabulary
   that must never become public, then run:

   ```bash
   VYRAL_PUBLIC_HISTORY_DENYLIST_FILE=.release-history-denylist \
   scripts/scan-release-history.sh
   ```

   An ignore rule only protects future untracked files. If this gate finds a credential-shaped
   historical blob or a denylisted identifier in prior content, a commit message, or a path,
   rewrite or split the public history and re-run the gate; do not publish the denylist or the
   matching value.
   Prefer a fresh public repository for a formerly private history, or obtain the hosting
   provider's confirmation that rewritten objects have been purged. A force-push alone does not
   prove old objects are no longer retrievable before visibility changes.
7. Before every release, create an ignored `.release-ownership-denylist` containing sibling
   repository names, consumer deployment identities, and private defaults known to the release
   operator, one regular expression per line. Run the current-tree ownership gate without
   publishing the policy:

   ```bash
   VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE=.release-ownership-denylist \
   scripts/scan-release-ownership.sh
   ```

   Both private operator denylists are also applied to the one-commit public
   tree by `scripts/verify-public-export.sh --release`; release mode refuses a
   dirty tree or a missing policy. The export is selected from the
   Git index by an explicit path allowlist, rejects generated/private artifacts,
   and must reproduce byte-for-byte across two independent builds. Use
   `--allow-dirty` only to rehearse local changes; release evidence must come
   from the default clean-tree mode.

   The always-on CI gate catches developer-local absolute paths and concrete cloud identities.
   The operator policy is still required because private vocabulary cannot be inferred safely
   from public source.

The version lines and maturity promises are defined in the [stability policy](../reference/stability.md) and enforced by
`scripts/verify-version-policy.py`. Source versions do not prove registry publication.

The exact authorized first cohort is recorded in
[`packaging/publication-cohort.json`](../../packaging/publication-cohort.json).
The manual [`Publish first cohort`](../../.github/workflows/publish-first-cohort.yml)
workflow is the only source path allowed to publish it. It accepts only the
reviewed `v0.3.0` cohort, and before packaging requires a GitHub-verified signed
annotated tag at current `main` plus a successful canonical Release Integrity
push run for that commit. Each registry job uses its own protected environment
and least-privilege identity. It has no automatic trigger.

Before dispatching it, configure the exact publisher tuple in the cohort
manifest at each registry: `Univeracity/vyral`, workflow file
`publish-first-cohort.yml`, and its named environment. NuGet, PyPI, and npm use
GitHub Actions OIDC trusted publishing; NuGet additionally needs the
`NUGET_USERNAME` environment variable for its short-lived-key exchange. The
npm publisher must permit `npm publish` for that trusted publisher; its
 isolated job supplies Node `22.14.0` and npm `11.5.1`, the minimum supported
 by npm's OIDC flow. The
container job uses only the repository-scoped `GITHUB_TOKEN` with
`packages: write`. This source authorization is not a claim that any package is
already available: absent registry trust or an environment approval, the manual
job fails closed and publishes nothing.

Prepare release notes before the dispatch and publish the GitHub release only
after every authorized registry job has succeeded. The
The separate Python HTTP client (`vyral-client`), provider-specific packages,
Temporal packages, and prototype integrations remain outside this cohort.

Before the first public release, the repository owner must also configure the hosted controls that
cannot be represented in source: protected release branches, required release-integrity,
dependency-review, and CodeQL checks, Dependency Graph/Dependabot alerts, secret scanning with
push protection, private vulnerability reporting, and registry trusted publishing. These controls
are operational prerequisites, not claims made by a local build.
Audit the currently configured GitHub subset without mutating repository settings:

```bash
python3 scripts/audit-github-launch-controls.py \
  --output artifacts/qualification/github-launch-controls.json
```

Use `--allow-incomplete` only to capture a pre-launch gap report; it does not
turn pending or plan-unavailable controls into passing evidence. Registry
trusted publishers and release-environment approvals remain explicit operator
checks because GitHub repository APIs cannot prove their external registry state.

The release is incomplete if any artifact lacks its license, source repository metadata, README,
or matching symbol package. For a security-sensitive correction, follow [SECURITY.md](../../SECURITY.md)
and publish an explicit remediation note.
