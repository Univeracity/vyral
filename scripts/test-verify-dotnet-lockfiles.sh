#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/vyral-dotnet-lock-test.XXXXXX")"
cleanup() {
  rm -rf -- "$work"
}
trap cleanup EXIT
cp "$ROOT/Directory.Build.props" "$work/Directory.Build.props"

write_project() {
  local path="$1"
  local reference="${2:-}"
  mkdir -p "$(dirname "$path")"
  {
    printf '%s\n' '<Project Sdk="Microsoft.NET.Sdk">'
    printf '%s\n' '  <PropertyGroup>'
    printf '%s\n' '    <TargetFramework>net8.0</TargetFramework>'
    printf '%s\n' '    <IsPackable>false</IsPackable>'
    printf '%s\n' '    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>'
    printf '%s\n' '  </PropertyGroup>'
    if [[ -n "$reference" ]]; then
      printf '%s\n' '  <ItemGroup>'
      printf '    <ProjectReference Include="%s" />\n' "$reference"
      printf '%s\n' '  </ItemGroup>'
    fi
    printf '%s\n' '</Project>'
  } > "$path"
}

expect_lock_failure() {
  local project="$1"
  local shape="$2"
  local output
  local status
  set +e
  output="$("$ROOT/scripts/verify-dotnet-lockfiles.sh" "$project" 2>&1)"
  status="$?"
  set -e
  if (( status == 0 )); then
    echo "dotnet-lockfile-test: $shape project-reference drift was accepted" >&2
    exit 1
  fi
  if [[ "$output" != *"NU1004"* ]]; then
    printf 'dotnet-lockfile-test: %s drift failed without NU1004:\n%s\n' "$shape" "$output" >&2
    exit 1
  fi
}

expect_default_restore_failure() {
  local project="$1"
  local output
  local status
  set +e
  output="$(dotnet restore "$project" 2>&1)"
  status="$?"
  set -e
  if (( status == 0 )) || [[ "$output" != *"NU1004"* ]]; then
    printf 'dotnet-lockfile-test: ordinary local restore did not reject drift:\n%s\n' "$output" >&2
    exit 1
  fi
}

# A direct reference added after lock generation must invalidate the consumer lock.
write_project "$work/direct/A/A.csproj"
write_project "$work/direct/B/B.csproj"
dotnet restore "$work/direct/A/A.csproj" --force-evaluate -p:RestoreLockedMode=false >/dev/null
"$ROOT/scripts/verify-dotnet-lockfiles.sh" "$work/direct/A/A.csproj" >/dev/null
write_project "$work/direct/A/A.csproj" '../B/B.csproj'
expect_lock_failure "$work/direct/A/A.csproj" direct
expect_default_restore_failure "$work/direct/A/A.csproj"
"$ROOT/scripts/update-dotnet-lockfiles.sh" "$work/direct/A/A.csproj" >/dev/null
"$ROOT/scripts/verify-dotnet-lockfiles.sh" "$work/direct/A/A.csproj" >/dev/null

# A new reference below an existing project edge must also invalidate the root consumer lock.
write_project "$work/transitive/A/A.csproj" '../B/B.csproj'
write_project "$work/transitive/B/B.csproj"
write_project "$work/transitive/C/C.csproj"
dotnet restore "$work/transitive/A/A.csproj" --force-evaluate -p:RestoreLockedMode=false >/dev/null
"$ROOT/scripts/verify-dotnet-lockfiles.sh" "$work/transitive/A/A.csproj" >/dev/null
write_project "$work/transitive/B/B.csproj" '../C/C.csproj'
expect_lock_failure "$work/transitive/A/A.csproj" transitive

printf 'dotnet-lockfile-guard=ok local-default=locked direct-drift=rejected transitive-drift=rejected\n'
