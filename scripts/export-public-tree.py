#!/usr/bin/env python3
"""Build a deterministic, source-only public tree from an explicit allowlist."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import subprocess
import sys


ROOT = Path(__file__).resolve().parent.parent
MANIFEST = "PUBLIC-EXPORT-MANIFEST.json"
NORMALIZED_MTIME = 315532800  # 1980-01-01; portable to ZIP-based tooling.

ROOT_FILES = {
    ".dockerignore",
    ".gcloudignore",
    ".gitignore",
    "CODE_OF_CONDUCT.md",
    "CONTRIBUTING.md",
    "Directory.Build.props",
    "Dockerfile",
    "LICENSE",
    "README.md",
    "ROADMAP.md",
    "SECURITY.md",
    "THIRD-PARTY-NOTICES.md",
    "TRADEMARKS.md",
    "Vyral.sln",
}

PUBLIC_PREFIXES = {
    ".config",
    ".github",
    "benchmarks",
    "clients",
    "conformance",
    "contracts",
    "deploy",
    "examples",
    "packaging",
    "qualification",
    "runtimes",
    "samples",
    "scripts",
    "src",
    "tests",
    "tools",
    "workers",
}

PUBLIC_DOC_FILES = {
    "docs/README.md",
    "docs/assets/vyral-logo-50.png",
    "docs/concepts/canonical-store.md",
    "docs/contributing/adapter-contributor.md",
    "docs/guides/consumer-handoff.md",
    "docs/maintainers/releasing.md",
    "docs/reference/execution-runtime-limitations.md",
    "docs/reference/stability.md",
    "docs/roman.md",
    "docs/temporal-operator-guide.md",
}

PUBLIC_DESIGN_FILES = {
    "design/admission-contract.md",
    "design/aws-opensearch-record-projection.md",
    "design/execution-runtime-adapter-matrix.md",
    "design/execution-runtime-plugin-authoring.md",
    "design/execution-runtime.md",
    "design/portable-runtime-qualification-and-temporal-adapter.md",
    "design/production-retrieval-adapter.md",
    "design/public-sdk-surface-and-stateless-mcp.md",
    "design/python-runtime.md",
}

FORBIDDEN_PARTS = {
    ".agent-artifacts",
    ".agent-state",
    ".agents",
    ".antigravitycli",
    ".claude",
    ".codex",
    ".git",
    ".mypy_cache",
    ".pytest_cache",
    ".terraform",
    ".venv",
    "Inbox",
    "__pycache__",
    "artifacts",
    "bin",
    "build",
    "dist",
    "node_modules",
    "obj",
}

FORBIDDEN_SUFFIXES = {
    ".a",
    ".dll",
    ".egg-info",
    ".env",
    ".key",
    ".nupkg",
    ".onnx",
    ".p12",
    ".pdb",
    ".pem",
    ".pfx",
    ".pyc",
    ".so",
    ".snupkg",
    ".sqlite",
    ".whl",
}

REQUIRED_FILES = {
    ".github/workflows/ci.yml",
    ".github/workflows/python-runtime-qualification.yml",
    "conformance/invariant.md",
    "deploy/canonical-cutover/cloudbuild-mysql.yaml",
    "deploy/canonical-cutover/cloudbuild-postgres.yaml",
    "docs/README.md",
    "docs/concepts/canonical-store.md",
    "docs/contributing/adapter-contributor.md",
    "docs/guides/consumer-handoff.md",
    "docs/maintainers/releasing.md",
    "docs/reference/execution-runtime-limitations.md",
    "docs/reference/stability.md",
    "LICENSE",
    "packaging/nuget/README.md",
    "README.md",
    "ROADMAP.md",
    "contracts/public-sdk-surface.json",
    "conformance/runtime/v1/manifest.json",
    "conformance/runtime/v1/scenarios/goldens/admission-receipts.json",
    "design/admission-contract.md",
    "design/public-sdk-surface-and-stateless-mcp.md",
    "design/python-runtime.md",
    "qualification/adapter-qualification.json",
    "runtimes/python/pyproject.toml",
    "runtimes/python/LICENSE",
    "scripts/benchmark-python-runtime.py",
    "scripts/audit-github-launch-controls.py",
    "scripts/export-public-tree.py",
    "scripts/mcp-wire-proxy.py",
    "scripts/test-built-sdk-python-runtime.sh",
    "scripts/test-export-public-tree.py",
    "scripts/test-verify-oci-image-identity.py",
    "scripts/test-python-runtime-platform-matrix.py",
    "scripts/verify-python-runtime-external-worker.sh",
    "scripts/verify-python-runtime-install.py",
    "scripts/verify-python-runtime-mcp-conformance.sh",
    "scripts/verify-python-runtime-platform-matrix.py",
    "scripts/verify-python-runtime-security.py",
    "scripts/verify-python-runtime.sh",
    "scripts/verify-python-runtime-upgrade.py",
    "scripts/verify-mcp-conformance.sh",
    "scripts/verify-mcp-container.sh",
    "scripts/verify-mcp-load.py",
    "scripts/verify-execution-smoke-container.sh",
    "scripts/verify-oci-image-identity.py",
    "scripts/verify-public-export.sh",
    "scripts/verify-publication-policy.py",
    "scripts/write-python-runtime-platform-receipt.py",
}


def git_paths(*arguments: str) -> list[str]:
    result = subprocess.run(
        ["git", "-C", str(ROOT), *arguments],
        check=True,
        stdout=subprocess.PIPE,
    )
    return [item.decode("utf-8") for item in result.stdout.split(b"\0") if item]


def is_public(path_text: str) -> bool:
    path = PurePosixPath(path_text)
    if len(path.parts) == 1:
        return path_text in ROOT_FILES
    if path.parts[0] == "docs":
        return path_text in PUBLIC_DOC_FILES
    if path.parts[0] == "design":
        return path_text in PUBLIC_DESIGN_FILES
    if path.parts[0] not in PUBLIC_PREFIXES:
        return False
    if any(part in FORBIDDEN_PARTS or part.startswith(".vyral") for part in path.parts):
        return False
    lower_name = path.name.lower()
    if lower_name == "export.json":
        return False
    if any(lower_name.endswith(suffix) for suffix in FORBIDDEN_SUFFIXES):
        return False
    if lower_name.endswith((".env.example", ".env.sample")):
        return True
    if ".env." in lower_name or lower_name.startswith(".env"):
        return False
    return True


def digest(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="A new directory outside the source repository")
    parser.add_argument(
        "--allow-dirty",
        action="store_true",
        help="Allow modified tracked files (for local rehearsal only)",
    )
    parser.add_argument(
        "--include-untracked",
        action="store_true",
        help="Include allowlisted untracked files (for local rehearsal only)",
    )
    args = parser.parse_args()

    output = args.output.expanduser().resolve()
    if output == ROOT or ROOT in output.parents:
        parser.error("output must be outside the source repository")
    if output.exists():
        parser.error("output must not already exist")

    status = subprocess.run(
        ["git", "-C", str(ROOT), "status", "--porcelain=v1", "--untracked-files=no"],
        check=True,
        stdout=subprocess.PIPE,
        text=True,
    ).stdout
    dirty = bool(status.strip()) or args.include_untracked
    if dirty and not args.allow_dirty:
        parser.error("tracked source is dirty; commit/revert it or use --allow-dirty for a rehearsal")
    if args.include_untracked and not args.allow_dirty:
        parser.error("--include-untracked requires --allow-dirty")

    tracked = set(git_paths("ls-files", "-z", "--cached"))
    candidates = set(tracked)
    if args.include_untracked:
        candidates.update(git_paths("ls-files", "-z", "--others", "--exclude-standard"))

    public_paths = sorted(path for path in candidates if is_public(path))
    missing = sorted(REQUIRED_FILES - set(public_paths))
    if missing:
        parser.error("required public files are absent from the selected Git tree: " + ", ".join(missing))

    output.mkdir(parents=True)
    entries: list[dict[str, object]] = []
    for relative in public_paths:
        source = ROOT / relative
        if not source.exists():
            raise SystemExit(f"Selected public path does not exist in the worktree: {relative}")
        if source.is_symlink():
            raise SystemExit(f"Public exports do not permit symbolic links: {relative}")
        if not source.is_file():
            raise SystemExit(f"Selected public path is not a regular file: {relative}")

        target = output / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        mode = 0o755 if source.stat().st_mode & stat.S_IXUSR else 0o644
        target.chmod(mode)
        os.utime(target, (NORMALIZED_MTIME, NORMALIZED_MTIME))
        entries.append({"path": relative, "mode": f"{mode:o}", "sha256": digest(target)})

    tree_hasher = hashlib.sha256()
    for entry in entries:
        tree_hasher.update(
            f"{entry['mode']} {entry['sha256']} {entry['path']}\n".encode("utf-8")
        )

    manifest = {
        "schemaVersion": 1,
        "sourceDirty": dirty,
        "treeSha256": tree_hasher.hexdigest(),
        "fileCount": len(entries),
        "files": entries,
    }
    manifest_path = output / MANIFEST
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    manifest_path.chmod(0o644)
    os.utime(manifest_path, (NORMALIZED_MTIME, NORMALIZED_MTIME))

    excluded_count = len(tracked - set(public_paths))
    print(
        f"public-export=ok files={len(entries)} excluded-tracked={excluded_count} "
        f"tree-sha256={manifest['treeSha256']} output={output}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
