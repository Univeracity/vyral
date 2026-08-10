#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/vyral-aws-opensearch-live-gate-test-XXXXXX")"

cleanup() {
  rm -rf "$WORK"
}
trap cleanup EXIT

mkdir -p "$WORK/bin"

cat > "$WORK/bin/aws" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

if [[ "$1" == "sts" && "$2" == "get-caller-identity" ]]; then
  printf '%s\n' '123456789012'
  exit 0
fi

printf 'unexpected aws call: %s\n' "$*" >&2
exit 91
EOF

cat > "$WORK/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

[[ "$1" == "test" ]]
[[ "$2" == "tests/Vyral.Tests.Aws/Vyral.Tests.Aws.csproj" ]]
[[ "$VYRAL_AWS_OPENSEARCH_ENDPOINT" == "https://search.example.test" ]]
[[ "$VYRAL_AWS_OPENSEARCH_SIGNING_SERVICE" == "aoss" ]]
[[ "$AWS_DEFAULT_REGION" == "us-east-1" ]]
[[ "$AWS_REGION" == "us-east-1" ]]
printf '%s\n' 'fake-aws-opensearch-live-tests=ok'
EOF

chmod +x "$WORK/bin/aws" "$WORK/bin/dotnet"

output="$(
  PATH="$WORK/bin:$PATH" \
  VYRAL_AWS_LIVE_REGION=us-east-1 \
  VYRAL_AWS_OPENSEARCH_ENDPOINT=https://search.example.test \
  VYRAL_AWS_OPENSEARCH_SIGNING_SERVICE=aoss \
  "$ROOT/scripts/validate-aws-opensearch-live.sh"
)"

[[ "$output" == *"fake-aws-opensearch-live-tests=ok"* ]]
[[ "$output" == *"aws-opensearch-live-gate=ok"* ]]
[[ "$output" != *"https://search.example.test"* ]]

printf '%s\n' 'aws-opensearch-live-gate-test=ok'
