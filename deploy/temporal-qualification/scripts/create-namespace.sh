#!/bin/sh
set -eu

: "${TEMPORAL_ADDRESS:?TEMPORAL_ADDRESS is required}"
: "${TEMPORAL_NAMESPACE:?TEMPORAL_NAMESPACE is required}"

attempt=1
while ! temporal operator cluster health --address "$TEMPORAL_ADDRESS" >/dev/null 2>&1; do
  if [ "$attempt" -ge 60 ]; then
    echo "Temporal qualification namespace initialization timed out."
    exit 1
  fi
  attempt=$((attempt + 1))
  sleep 2
done

if temporal operator namespace describe \
  --namespace "$TEMPORAL_NAMESPACE" \
  --address "$TEMPORAL_ADDRESS" >/dev/null 2>&1; then
  exit 0
fi

temporal operator namespace create \
  --namespace "$TEMPORAL_NAMESPACE" \
  --retention 1d \
  --address "$TEMPORAL_ADDRESS" >/dev/null
