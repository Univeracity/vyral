#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACKAGES_DIR="${1:?Directory containing packed Vyral NuGet packages is required}"
if [[ "$PACKAGES_DIR" != /* ]]; then
  PACKAGES_DIR="$ROOT/$PACKAGES_DIR"
fi
if [[ ! -d "$PACKAGES_DIR" ]]; then
  echo "NuGet package directory does not exist: $PACKAGES_DIR" >&2
  exit 1
fi

MCP_PACKAGE="$(find "$PACKAGES_DIR" -maxdepth 1 -type f -name 'Vyral.Mcp.*.nupkg' ! -name '*.symbols.nupkg' -print -quit)"
if [[ -z "$MCP_PACKAGE" ]]; then
  echo "The package directory does not contain Vyral.Mcp." >&2
  exit 1
fi

PACKAGE_VERSION="$(basename "$MCP_PACKAGE")"
PACKAGE_VERSION="${PACKAGE_VERSION#Vyral.Mcp.}"
PACKAGE_VERSION="${PACKAGE_VERSION%.nupkg}"
CONSUMER_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vyral-mcp-package-consumer-XXXXXX")"
cleanup() {
  rm -rf "$CONSUMER_ROOT"
}
trap cleanup EXIT

dotnet new console \
  --name VyralMcpPackageConsumer \
  --framework net10.0 \
  --output "$CONSUMER_ROOT/consumer" \
  --no-restore >/dev/null

dotnet add "$CONSUMER_ROOT/consumer/VyralMcpPackageConsumer.csproj" package Vyral.Mcp \
  --version "$PACKAGE_VERSION" \
  --source "$PACKAGES_DIR" \
  --no-restore >/dev/null

cat > "$CONSUMER_ROOT/consumer/Program.cs" <<'CSHARP'
using Microsoft.Extensions.DependencyInjection;
using Vyral.Mcp;

var options = new VyralMcpOptions();
var services = new ServiceCollection();
_ = services.AddVyralMcp(options);

if (VyralMcpOptions.ProtocolVersion != "2026-07-28" ||
    options.Enabled ||
    options.EndpointPath != "/mcp")
{
    throw new InvalidOperationException("The packed MCP adapter exposed an unexpected public contract.");
}

Console.WriteLine($"mcp-package-consumer=ok version={VyralMcpOptions.ProtocolVersion}");
CSHARP

dotnet restore "$CONSUMER_ROOT/consumer/VyralMcpPackageConsumer.csproj" \
  --source "$PACKAGES_DIR" \
  --source https://api.nuget.org/v3/index.json >/dev/null
dotnet run \
  --project "$CONSUMER_ROOT/consumer/VyralMcpPackageConsumer.csproj" \
  --no-restore
