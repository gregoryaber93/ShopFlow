# CI/CD (GitHub Actions)

```text
pull request ──► ci.yml ─────────────► build, tests, Pact (consumer → provider), EF Core, integration tests
             ├─► security.yml ───────► CodeQL, NuGet audit, dependency review, gitleaks, Trivy, PSRule
             └─► infra-validate.yml ─► bicep build + lint, what-if against dev as a PR comment

push to main ──► release.yml
                   ci.yml ──► _build.yml ──► _deploy.yml (dev) ──► _deploy.yml (test) ──► _deploy.yml (prod)
                              zip packages    automatic            approval               approval
                              images + Trivy
                              + attestation

manual ──────► rollback.yml (slot swap or the previous image)
tag contracts/v* ──► contracts-publish.yml (the ShopFlow.Contracts package with API compatibility validation)
```

| File | Role |
|---|---|
| [filters.yml](filters.yml) | paths → deployable units (integration tests in CI, the deployment plan) |
| [deployables.json](deployables.json) | unit → project or Dockerfile (what to build and where to deploy it) |
| [actions/](actions) | composite actions: `setup-dotnet`, `azure-login`, `bicep-deploy`, `run-migrations`, `deploy-containerapp`, `deploy-appservice-slot`, `deploy-functionapp`, `smoke-test` |
| [scripts/](scripts) | `current-image-tags.sh`: tags of the running images for the infrastructure deployment |

The local equivalent of the `ci.yml` checks that need no cloud is [scripts/verify.ps1](../scripts/verify.ps1) / [scripts/verify.sh](../scripts/verify.sh). Walkthrough of a whole release: [09-release-pipeline.md](../docs/walkthroughs/09-release-pipeline.md).

## Identities and permissions

No Azure secret in GitHub. Every sign-in is OIDC: a GitHub token with a subject (`sub`) is exchanged in Entra ID for a token of a managed identity that trusts only that subject. The administrator creates the identities and roles in [infra/bootstrap](../infra/bootstrap).

| Identity | OIDC subject | What it can do | Who uses it |
|---|---|---|---|
| `id-shopflow-github-build` | `repo:…:ref:refs/heads/main` | AcrPush on the shared registry | `_build.yml` |
| `id-shopflow-github-plan-dev-weu` | `repo:…:pull_request` | Reader on the dev groups, what-if | `infra-validate.yml` |
| `id-shopflow-github-deploy-<env>-weu` | `repo:…:environment:<env>` | Contributor on the 3 environment groups, granting only the listed roles, deployment objects | `_deploy.yml`, `rollback.yml` |

## Repository configuration (once)

Repository variables (Settings → Secrets and variables → Actions → Variables):

| Variable | Value |
|---|---|
| `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | tenant and subscription |
| `AZURE_BUILD_CLIENT_ID` | the `buildClientId` output of `infra/bootstrap/shared.bicep` |
| `AZURE_PLAN_CLIENT_ID` | the `plannerClientId` output of the dev bootstrap |
| `ACR_NAME` | `crshopflowsharedweu` |
| `DEV_APIM_PRIVATE_ONLY` | the same as `APIM_PRIVATE_ONLY` of the dev environment (what-if in PRs) |

GitHub environments `dev`, `test`, `prod`:

| Setting | dev | test | prod |
|---|---|---|---|
| Required reviewers | none | QA team | product owners + on-call |
| Allowed branches | `main` | `main` | `main` |
| `AZURE_CLIENT_ID` variable | the `deployerClientId` output of the environment bootstrap | as for dev | as for dev |
| `PUBLIC_API_URL` variable | `https://api.dev.shopflow.example` | `https://api.test.shopflow.example` | `https://api.shopflow.example` |
| `APIM_PRIVATE_ONLY` variable | `true` after approving Private Link (infra/README, step 9) | as for dev | as for dev |

Runner groups (Organization → Actions → Runner groups): `shopflow-dev`, `shopflow-test`, `shopflow-prod`, each with the network configuration from the `githubRunnersNetworkId` output of that environment's `infra/main.bicep` deployment and available only to this repository.

Branch protection rule for `main`: required statuses `ci-result`, `bicep build and lint` (when `infra/` changes), CodeQL; CODEOWNERS review; merge queue (`merge_group` in `ci.yml`).

## What to look at

- **One job per environment** ([_deploy.yml](workflows/_deploy.yml)): an environment with reviewers asks for approval for every job that refers to it.
- **Differences since the last deployment to the environment**, not since the previous commit: the `deployedCommit` tag on the `rg-…-apps` group.
- **Infrastructure does not roll back applications**: `IMAGE_TAGS` from the running images ([current-image-tags.sh](scripts/current-image-tags.sh)).
- **Network**: App Service and Functions have no public endpoint, so code deployment works only from a runner in the VNet; Container Apps, migrations and Bicep go through Azure Resource Manager.
- **Actions pinned to a major version** (`@v5`). An organization with stricter requirements pins them to a full commit SHA (Dependabot updates the SHA): a tag can be moved to malicious code, as the `tj-actions/changed-files` incident showed in 2025.
