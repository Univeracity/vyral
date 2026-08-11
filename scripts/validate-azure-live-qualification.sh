#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

require() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "$name is required." >&2
    exit 2
  fi
}

for name in \
  VYRAL_AZURE_LIVE_RESOURCE_GROUP \
  VYRAL_AZURE_LIVE_FIXTURE_RESOURCE_GROUP \
  VYRAL_AZURE_LIVE_COSMOS_RESOURCE_GROUP \
  VYRAL_AZURE_LIVE_STORAGE_ACCOUNT \
  VYRAL_AZURE_LIVE_COSMOS_ACCOUNT \
  VYRAL_AZURE_COSMOS_DATABASE; do
  require "$name"
done

receipt="${VYRAL_AZURE_LIVE_RECEIPT_PATH:-artifacts/qualification/azure-live-gate.json}"
mkdir -p "$(dirname "$receipt")"

# The managed identity can read only the reusable test fixtures and create or
# delete resources in its disposable group. Credentials remain process-local.
blob_connection="$(az storage account show-connection-string \
  --resource-group "$VYRAL_AZURE_LIVE_FIXTURE_RESOURCE_GROUP" \
  --name "$VYRAL_AZURE_LIVE_STORAGE_ACCOUNT" \
  --query connectionString --output tsv --only-show-errors)"
cosmos_endpoint="$(az cosmosdb show \
  --resource-group "$VYRAL_AZURE_LIVE_COSMOS_RESOURCE_GROUP" \
  --name "$VYRAL_AZURE_LIVE_COSMOS_ACCOUNT" \
  --query documentEndpoint --output tsv --only-show-errors)"
cosmos_key="$(az cosmosdb keys list \
  --resource-group "$VYRAL_AZURE_LIVE_COSMOS_RESOURCE_GROUP" \
  --name "$VYRAL_AZURE_LIVE_COSMOS_ACCOUNT" \
  --query primaryMasterKey --output tsv --only-show-errors)"

export VYRAL_AZURE_BLOB_CONNECTION_STRING="$blob_connection"
export VYRAL_AZURE_BLOB_CONTAINER_PREFIX="vyral-it"
export VYRAL_AZURE_COSMOS_CONNECTION_STRING="AccountEndpoint=${cosmos_endpoint};AccountKey=${cosmos_key};"
export VYRAL_AZURE_COSMOS_CONTAINER_PREFIX="vyral-it"
export VYRAL_AZURE_LIVE_RECEIPT_PATH="$receipt"
unset blob_connection cosmos_endpoint cosmos_key

exec scripts/validate-azure-durable-functions-live.sh
