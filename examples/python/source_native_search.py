from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

sys.path.insert(
    0,
    str(Path(__file__).resolve().parents[2] / "runtimes/python/src"),
)

from vyral_runtime.integrations.ripgrep import (  # noqa: E402
    RipgrepAdapterOptions,
    RipgrepSearchAdapter,
    RipgrepSearchRequest,
)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Run bounded, source-native retrieval through ripgrep."
    )
    parser.add_argument("query")
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument(
        "--include",
        action="append",
        dest="includes",
        default=None,
        help="Allowlisted glob; repeat as needed (defaults to Python and Markdown).",
    )
    parser.add_argument("--limit", type=int, default=10)
    parser.add_argument("--case-sensitive", action="store_true")
    parser.add_argument("--json", action="store_true")
    arguments = parser.parse_args()

    adapter = RipgrepSearchAdapter(
        arguments.root,
        RipgrepAdapterOptions(
            include_globs=tuple(arguments.includes or ("*.py", "*.md")),
        ),
    )
    result = adapter.search(
        RipgrepSearchRequest(
            arguments.query,
            limit=arguments.limit,
            case_sensitive=arguments.case_sensitive,
        )
    )
    if arguments.json:
        print(json.dumps(result.to_dict(), indent=2, sort_keys=True))
        return
    for match in result.matches:
        print(f"{match.source_uri} [{match.source_revision}]")
        print(f"  {match.line_text}")
    print(
        f"matches={len(result.matches)} truncated={str(result.truncated).lower()} "
        f"durationMs={result.duration_ms}"
    )


if __name__ == "__main__":
    main()
