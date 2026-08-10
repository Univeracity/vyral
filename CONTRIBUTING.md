# Contributing

Start with a focused issue or proposal that states the portable contract behavior, provider-specific
behavior, compatibility implications, and verification approach. New provider features must not
silently widen the provider-agnostic contract.

**Adapter authors** (storage, execution, AI provider targets, canonical store, projections): follow
[adapter contributor guide](docs/contributing/adapter-contributor.md) for contracts, conformance fixtures, live gates,
qualification levels, and the PR checklist.

**Execution plugin authors** (portable handlers, not provider adapters): see
[design/execution-runtime-plugin-authoring.md](design/execution-runtime-plugin-authoring.md) and
[src/Vyral.Execution/README.md](src/Vyral.Execution/README.md).

Public API and adapter changes must use the maturity labels and 0.x compatibility rules in
[stability policy](docs/reference/stability.md).

Before submitting a change:

1. Keep credentials, generated artifacts, local databases, and private operational notes out of
   Git.
2. Add deterministic conformance coverage for portable behavior; add an opt-in live gate for any
   managed-service behavior.
3. Run the relevant tests and `scripts/verify-release-artifacts.sh` when a change affects a
   published package, client, container, or release workflow.
4. Update consumer-facing documentation and call out intentional capability limits.

By contributing, you agree to follow the repository's [Code of Conduct](CODE_OF_CONDUCT.md) and
license your contribution under the repository's Apache-2.0 license.
