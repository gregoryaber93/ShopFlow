---
name: verify
description: Runs ShopFlow's local verification - the cloud-free part of CI (format, build, tests, Pact consumers and providers, EF Core model check, English and link checks) - and explains any failure. Use before declaring a task done, after refactoring, or when the user asks to "check", "verify" or "run the tests".
---

# Verify the repository locally

Everything here runs offline (NuGet restore aside) and costs nothing. Never add steps that sign in to Azure.

## Run

Pick the script for the current shell:

- PowerShell: `./scripts/verify.ps1` (options: `-Quick` skips Pact and the EF check, `-SkipFormat`)
- Bash (Git Bash, Linux, macOS): `./scripts/verify.sh` (options: `--quick`, `--skip-format`)

Run it in the background when it may take longer than two minutes (first restore, cold build).

The scripts mirror `.github/workflows/ci.yml` (job `build-and-test`) in the same order:

1. `dotnet restore ShopFlow.slnx`
2. `dotnet format whitespace ShopFlow.slnx --verify-no-changes`
3. `dotnet build ShopFlow.slnx --no-restore` (warnings are errors)
4. `dotnet test --solution ShopFlow.slnx --no-build --filter-not-namespace "ShopFlow.Pact.Tests*" --ignore-exit-code 8`
5. Pact consumers, then a check that `tests/ShopFlow.Pact.Tests/pacts` did not change (only when the folder is a git
   repository), then Pact providers
6. `dotnet ef migrations has-pending-model-changes` for Inventory (after `dotnet tool restore`)
7. `node scripts/check-english.mjs` and `node scripts/check-links.mjs`

## Interpret failures

| Symptom | Likely cause | Fix |
|---|---|---|
| format step lists files | whitespace/style drift | `dotnet format whitespace ShopFlow.slnx`, then re-run |
| build error CAxxxx / IDExxxx | analyzer rule (warnings are errors) | fix the code; suppress only with a justified `// WHY:` |
| `LayeringTests` fail | a forbidden dependency between layers or contexts | move the code behind a port; see `.claude/rules/csharp.md` |
| `Schema_matches_the_approved_snapshot` fails | a contract changed | if deliberate: `SHOPFLOW_ACCEPT_SNAPSHOTS=1` and review the diff; otherwise revert |
| `MessagingTopologyTests` fail | code constants and `messaging-topology.json` diverged | update both sides |
| Pact provider fails | the provider no longer satisfies a consumer | fix the provider, or change the consumer and regenerate the pact |
| pending model changes | EF model changed without a migration | `/ef-migration` |
| exit code 8 | zero tests matched a filter | check the filter; expected only for the filtered-out Pact project |
| integration tests skipped | `SHOPFLOW_RUN_INTEGRATION_TESTS` unset or no Docker | expected locally |

Report the result as: which steps passed, which failed (with the first meaningful error lines), and what you changed
to fix them. Do not claim success for steps that were skipped.
