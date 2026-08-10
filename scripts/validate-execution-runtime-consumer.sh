#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

python3 scripts/verify-public-sdk-surface.py
python3 scripts/verify-public-contract-schemas.py
python3 scripts/generate-sdk-types.py
dotnet test tests/Vyral.Tests.Conformance/Vyral.Tests.Conformance.csproj --no-restore
dotnet test tests/Vyral.Tests.Local/Vyral.Tests.Local.csproj --no-restore --filter ExecutionRuntime
dotnet test tests/Vyral.Tests.Azure/Vyral.Tests.Azure.csproj --no-restore --filter AzureDurableExecution
dotnet run --project samples/Vyral.Execution.LocalSample/Vyral.Execution.LocalSample.csproj -- --once
if command -v jq >/dev/null 2>&1; then
  jq empty src/Vyral.Server/contracts/vyral.openapi.json
else
  python3 -m json.tool src/Vyral.Server/contracts/vyral.openapi.json >/dev/null
fi
git diff --check
