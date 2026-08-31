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
   .NET, JavaScript, Python, and Go. A public `consumer_validation` item must use
   `private_opaque` disclosure, an opaque `urn:vyral:private-consumer-evidence:sha256:...`
   reference, and no consumer command, result path, generation identifier, repository, deployment
   identity, or name. Keep the identity-to-receipt mapping in ignored operator evidence. Naming a
   validating consumer requires separate, explicit publication authorization from that consumer.
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
   A server image that advertises the generic hosted worker must additionally retain a receipt
   proving that `worker/Vyral.HostedWorker.dll` starts as the configured non-root user under the
   documented read-only runtime profile, exposes only the approved hosted-handler catalog, and
   rejects callbacks that lack the dispatch marker or an accepted callback identity.
4. Publish packages through a trusted-publishing or OIDC-backed registry configuration. Do not
   place registry tokens in the repository or workflow files. `vyral-client@0.3.0` has an
   explicitly authorized, capability-scoped exception: after the release tag and canonical evidence
   exist, publish the exact packed archive from the authorized commit with a locally controlled npm
   token. Dispatch the protected publisher with `npm_direct_token_published: true`; it verifies the
   registry version, repository URL, and SHA-512 archive integrity against its independently built
   distribution. This exception does not claim npm OIDC provenance or a trusted-publisher
   relationship. A later npm release requires a new explicit authorization or an account configuration
   that permits npm trusted publishing. Do not place the token in GitHub, the repository, or a
   workflow file.
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

The exact currently authorized package cohort is recorded in
[`packaging/publication-cohort.json`](../../packaging/publication-cohort.json).
The manual [`Publish package release`](../../.github/workflows/publish-first-cohort.yml)
workflow is the only source path allowed to publish that cohort. Its filename is retained because
NuGet and PyPI trusted-publisher identities include the workflow filename. It accepts only the
reviewed `v0.3.1` package patch, and before packaging requires a GitHub-verified signed
annotated tag at current `main` plus a successful canonical Release Integrity
push run for that commit. Each registry job uses its own protected environment
and least-privilege identity. It has no automatic trigger. The patch publishes
`Vyral.Abstractions` and `Vyral.Local` `0.3.1` plus the Python runtime `0.1.2`;
unchanged execution and JavaScript packages are not rebuilt or republished.

The server's `0.3.1` security correction is a deliberately separate,
container-only delivery: [`packaging/container-security-release.json`](../../packaging/container-security-release.json)
and the manual [`Publish server container security patch`](../../.github/workflows/publish-container-security-patch.yml)
workflow authorize only `ghcr.io/univeracity/vyral-server:0.3.1`. It requires a
GitHub-verified signed `server-v0.3.1` tag at current `main` and a successful
Release Integrity run for that commit. It does not republish the unaffected
NuGet, PyPI, or npm artifacts.

The server's current `0.3.3` delivery is likewise container-only:
[`packaging/worker-container-release.json`](../../packaging/worker-container-release.json)
and the manual [`Publish server container`](../../.github/workflows/publish-worker-container.yml)
workflow authorize only `ghcr.io/univeracity/vyral-server:0.3.3`. It requires a
GitHub-verified signed `server-v0.3.3` tag at current `main`, successful canonical
Release Integrity evidence containing the hosted-worker receipt, and a second
MCP and hosted-worker qualification plus pinned Trivy scan against the exact published digest. The
hosted-worker entrypoint is preview and initially hosts only
`vyral.artifacts.record-ingest`; the API server and other capability maturity
boundaries remain unchanged.

Before dispatching it, configure the exact publisher tuple in the cohort
manifest at each trusted registry: `Univeracity/vyral`, workflow file
`publish-first-cohort.yml`, and its named environment. NuGet and PyPI use
GitHub Actions OIDC trusted publishing; NuGet additionally needs the
`NUGET_USERNAME` environment variable for its short-lived-key exchange. The separate
container workflow uses only the repository-scoped `GITHUB_TOKEN` with
`packages: write`. npm remains outside the current patch. This source authorization is not a claim that any package is
already available: absent registry trust or an environment approval, the manual
job fails closed and publishes nothing.

Prepare release notes before the dispatch and publish the GitHub release only
after every authorized registry job has succeeded. The separate Python HTTP client (`vyral-client`), provider-specific packages,
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
