#!/usr/bin/env bash
set -euo pipefail
umask 077

# Qualifies Vyral's derived OpenSearch projection against a caller-provisioned disposable
# OpenSearch domain or collection. The test creates a unique index and removes it in finally; it
# never creates or deletes the surrounding resource, network policy, IAM role, or credentials.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "$1 is required." >&2
    exit 2
  }
}

require_command aws
require_command dotnet

REGION="${VYRAL_AWS_LIVE_REGION:-${AWS_DEFAULT_REGION:-${AWS_REGION:-$(aws configure get region)}}}"
ENDPOINT="${VYRAL_AWS_OPENSEARCH_ENDPOINT:-}"
SIGNING_SERVICE="${VYRAL_AWS_OPENSEARCH_SIGNING_SERVICE:-es}"

if [[ -z "$REGION" || "$REGION" == "None" ]]; then
  echo "Set VYRAL_AWS_LIVE_REGION, AWS_DEFAULT_REGION, AWS_REGION, or an AWS CLI default region." >&2
  exit 2
fi
if [[ -z "$ENDPOINT" ]]; then
  echo "Set VYRAL_AWS_OPENSEARCH_ENDPOINT to a caller-provisioned disposable OpenSearch data-plane endpoint." >&2
  exit 2
fi
if [[ "$ENDPOINT" != https://* ]]; then
  echo "VYRAL_AWS_OPENSEARCH_ENDPOINT must use HTTPS." >&2
  exit 2
fi
if [[ ! "$SIGNING_SERVICE" =~ ^[A-Za-z0-9-]+$ ]]; then
  echo "VYRAL_AWS_OPENSEARCH_SIGNING_SERVICE contains invalid characters." >&2
  exit 2
fi

# This is a read-only identity check. The .NET live test makes signed data-plane requests and
# cleans up its unique derived index even when an assertion fails.
aws sts get-caller-identity --region "$REGION" --output text >/dev/null

echo "aws-opensearch-live-gate=running-projection-qualification region=${REGION} signing-service=${SIGNING_SERVICE}"
AWS_DEFAULT_REGION="$REGION" AWS_REGION="$REGION" \
VYRAL_AWS_OPENSEARCH_ENDPOINT="$ENDPOINT" \
VYRAL_AWS_OPENSEARCH_SIGNING_SERVICE="$SIGNING_SERVICE" \
dotnet test tests/Vyral.Tests.Aws/Vyral.Tests.Aws.csproj --no-restore \
  --filter 'FullyQualifiedName~OpenSearchRecordSearchProjectionLiveTests' \
  --logger 'console;verbosity=minimal'

echo "aws-opensearch-live-gate=ok"
