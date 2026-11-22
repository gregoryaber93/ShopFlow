---
paths:
  - ".github/**"
---

# GitHub Actions rules

Workflows are read and edited here, never triggered: no `gh workflow run`, no `git push`. Overview and identity model:
[.github/README.md](../../.github/README.md); a release step by step:
[09-release-pipeline.md](../../docs/walkthroughs/09-release-pipeline.md).

- **No secrets for Azure.** Sign-in is OIDC (`azure/login` through the `azure-login` composite action) with the
  identity matching the job: build → `id-shopflow-github-build`, PR what-if → plan identity, deploy → the environment's
  deploy identity. A new job gets `permissions:` with the minimum (`contents: read`, `id-token: write` only if it signs in).
- **One job per environment** in `_deploy.yml`; environments with reviewers gate production. Deployments compute
  changes since the last deployment to that environment (`deployedCommit` tag), not since the previous commit.
- **Build once, deploy the same artifact** (image digest or zip) to dev, test and prod. Images are scanned (Trivy) and
  attested before push.
- Reuse composite actions in `.github/actions/` instead of copying steps. Inputs have `description`s.
- `ci.yml` stays the source of truth for local verification: when a check is added there, mirror it in
  `scripts/verify.ps1` and `scripts/verify.sh` (if it needs no cloud).
- Actions are pinned to a major version (`@v5`); `dependabot.yml` keeps them current. Third-party actions not owned by
  GitHub or Microsoft should be pinned to a commit SHA.
- Shell steps: `set -euo pipefail` in scripts, quote variables, never echo secrets or tokens; use `$GITHUB_OUTPUT` and
  `$GITHUB_STEP_SUMMARY`, not deprecated `set-output`.
- Path filters (`filters.yml`) and `deployables.json` must list a new deployable unit, otherwise it is never built or
  deployed. Update `CODEOWNERS` for a new area.
