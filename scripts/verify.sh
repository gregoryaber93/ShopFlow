#!/usr/bin/env bash
# Local verification: the cloud-free part of .github/workflows/ci.yml (job build-and-test), in the same order,
# plus the repository's English and link checks. Nothing here signs in to Azure or leaves the machine.
#
# Usage: scripts/verify.sh [--quick] [--skip-format] [--configuration Debug|Release]
#   --quick         skip Pact and the EF Core model check
#   --skip-format   skip `dotnet format whitespace --verify-no-changes`
# CI builds Release; the default here is Debug to reuse the IDE's build output.
set -euo pipefail

cd "$(dirname "$0")/.."

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 PACT_DO_NOT_TRACK=true

quick=false
skip_format=false
configuration=Debug
while [ $# -gt 0 ]; do
  case "$1" in
    --quick) quick=true ;;
    --skip-format) skip_format=true ;;
    --configuration) configuration="$2"; shift ;;
    -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

results=()
failed=0

run_step() {
  local name="$1"; shift
  printf '\n==> %s\n' "$name"
  if "$@"; then
    results+=("PASS  $name")
  else
    results+=("FAIL  $name")
    failed=1
  fi
}

skip_step() {
  results+=("SKIP  $1 ($2)")
}

summary() {
  printf '\n==> Summary\n'
  printf '%s\n' "${results[@]}"
  if [ "$failed" -ne 0 ]; then echo "Verification FAILED."; else echo "Verification passed."; fi
}

pact_hash() {
  { cat tests/ShopFlow.Pact.Tests/pacts/*.json 2>/dev/null || true; } | sha256sum | cut -d' ' -f1
}

run_step "restore" dotnet restore ShopFlow.slnx

if $skip_format; then
  skip_step "format" "--skip-format"
else
  run_step "format" dotnet format whitespace ShopFlow.slnx --verify-no-changes --verbosity minimal
fi

run_step "build" dotnet build ShopFlow.slnx --no-restore --configuration "$configuration"
if [ "$failed" -ne 0 ]; then summary; exit 1; fi # Tests on a broken build only add noise.

run_step "tests" dotnet test --solution ShopFlow.slnx --no-build --configuration "$configuration" \
  --filter-not-namespace "ShopFlow.Pact.Tests*" --ignore-exit-code 8

if $quick; then
  skip_step "pact" "--quick"
  skip_step "ef model" "--quick"
else
  # WHY: CI fails when the consumer tests change a committed pact. Comparing a hash before and after works with or
  # without git.
  before="$(pact_hash)"
  run_step "pact consumers" dotnet test --project tests/ShopFlow.Pact.Tests --no-build --configuration "$configuration" \
    --filter-namespace "ShopFlow.Pact.Tests.Consumers"
  if [ "$before" = "$(pact_hash)" ]; then
    results+=("PASS  pact files unchanged")
  else
    results+=("FAIL  pact files unchanged (consumer tests regenerated different pacts: review and keep them with the change)")
    failed=1
  fi
  run_step "pact providers" dotnet test --project tests/ShopFlow.Pact.Tests --no-build --configuration "$configuration" \
    --filter-namespace "ShopFlow.Pact.Tests.Providers"

  run_step "tool restore" dotnet tool restore
  run_step "ef model matches migrations" dotnet ef migrations has-pending-model-changes --no-build \
    --configuration "$configuration" --project src/Services/Inventory/ShopFlow.Inventory.Infrastructure
fi

if command -v node >/dev/null 2>&1; then
  run_step "english" node scripts/check-english.mjs
  run_step "links" node scripts/check-links.mjs
else
  skip_step "english, links" "node not found"
fi

summary
exit "$failed"
