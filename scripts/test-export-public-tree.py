#!/usr/bin/env python3
"""Regression checks for the source-only public export boundary."""

from __future__ import annotations

import importlib.util
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "scripts/export-public-tree.py"
SPEC = importlib.util.spec_from_file_location(
    "vyral_export_public_tree", MODULE_PATH
)
if SPEC is None or SPEC.loader is None:
    raise SystemExit("Unable to load the public export policy.")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def main() -> int:
    public_docs = set(MODULE.PUBLIC_DOC_FILES)
    missing_docs = sorted(
        path for path in public_docs if not (ROOT / path).is_file()
    )
    if missing_docs:
        raise SystemExit(
            "Public documentation allowlist names missing files: "
            + ", ".join(missing_docs)
        )
    if not all(MODULE.is_public(path) for path in public_docs):
        raise SystemExit("A public documentation allowlist entry is not exportable.")

    public_designs = set(MODULE.PUBLIC_DESIGN_FILES)
    missing_designs = sorted(
        path for path in public_designs if not (ROOT / path).is_file()
    )
    if missing_designs:
        raise SystemExit(
            "Public design allowlist names missing files: "
            + ", ".join(missing_designs)
        )
    if not all(MODULE.is_public(path) for path in public_designs):
        raise SystemExit("A public design allowlist entry is not exportable.")

    non_public_paths = {
        "design/cloudflare-adapter-readiness.md",
        "design/contract-v1.md",
        "design/contract-v2.md",
        "design/contract-v3.md",
        "design/contract-v4.md",
        "design/contract-v5.md",
        "design/contract-v6.md",
        "design/contract-v7.md",
        "design/contract-v8.md",
        "design/next-advancement-arcs.md",
        "design/python-runtime-security-review.md",
        "design/turboquant-native-dotnet.md",
        "docs/project-assessment-2026-08-01.md",
        "docs/open-source-prep.md",
        "docs/vyral-strategic-assessment.md",
    }
    leaked = sorted(
        path for path in non_public_paths if MODULE.is_public(path)
    )
    if leaked:
        raise SystemExit(
            "Process-oriented documentation paths are exportable: "
            + ", ".join(leaked)
        )

    if not MODULE.is_public("ROADMAP.md"):
        raise SystemExit("The public roadmap is absent from the export.")
    if not MODULE.is_public(".gitattributes"):
        raise SystemExit("The cross-platform line-ending policy is absent from the export.")
    print(
        "public-export-policy-test=ok "
        f"docs={len(public_docs)} designs={len(public_designs)} "
        f"non-public-probes={len(non_public_paths)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
