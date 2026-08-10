#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# A privacy rule in .gitignore is ineffective if the file was committed before the rule existed.
# Reject that state without echoing potentially sensitive file names into build logs.
if git ls-files -ci --exclude-standard | rg -q .; then
  printf '%s\n' 'Tracked files match a repository ignore rule; remove them from the release candidate before publishing.' >&2
  exit 1
fi

# This is a deliberately narrow offline guard, not a substitute for a hosted secret scanner. It
# blocks credential shapes that should never enter a release candidate while avoiding generated
# fixtures and example placeholders. It only scans tracked, non-binary files.
patterns=(
  'AKIA[0-9A-Z]{16}'
  'ASIA[0-9A-Z]{16}'
  'gh[pousr]_[A-Za-z0-9_]{20,}'
  'github_pat_[A-Za-z0-9_]{20,}'
  'AIza[0-9A-Za-z_-]{35}'
  '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE'" KEY-----"
  'AccountKey=[A-Za-z0-9+/]{40,}={0,2}'
)

matches="$(python3 - "${patterns[@]}" <<'PY'
from pathlib import Path
import re
import subprocess
import sys

patterns = [re.compile(pattern) for pattern in sys.argv[1:]]
tracked = subprocess.run(
    ["git", "ls-files", "-z"],
    check=True,
    stdout=subprocess.PIPE,
).stdout.split(b"\0")

matches: set[tuple[str, int]] = set()
for encoded_path in tracked:
    if not encoded_path:
        continue
    path = Path(encoded_path.decode(errors="surrogateescape"))
    if not path.is_file():
        continue
    content = path.read_bytes()
    if b"\0" in content:
        continue
    text = content.decode("utf-8", errors="ignore")
    for pattern in patterns:
        for match in pattern.finditer(text):
            line = text.count("\n", 0, match.start()) + 1
            matches.add((path.as_posix(), line))

for path, line in sorted(matches):
    print(f"{path}:{line}: [redacted]")
PY
)"

if [[ -n "$matches" ]]; then
  printf '%s\n' "$matches" >&2
  printf '%s\n' 'Potential credential material detected in tracked files.' >&2
  exit 1
fi

printf '%s\n' 'release-secret-scan=ok'
