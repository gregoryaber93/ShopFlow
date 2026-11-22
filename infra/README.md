# Infrastructure (Bicep)

Two levels of deployment:

- **Bootstrap** ([bootstrap/](bootstrap)) is deployed by an administrator (subscription Owner) once and whenever permissions change: the shared image registry, the GitHub Actions identities, their roles, the resource groups, the data group lock, the budget.
- **Environment** ([main.bicep](main.bicep)) is deployed by the pipeline (`.github/workflows/release.yml`) with an identity from the bootstrap: six layers in three existing groups.

```text
bootstrap/shared.bicep (subscription, once)
├── rg-shopflow-shared-weu: registry crshopflowsharedweu, id-shopflow-github-build (AcrPush, OIDC from main)
└── custom roles: ShopFlow Deployment Operator, ShopFlow What-If Reader

bootstrap/environment.bicep (subscription, once per environment)
├── rg-…-core, rg-…-data (CanNotDelete lock), rg-…-apps
├── id-shopflow-github-deploy-<env> (OIDC: environment:<env>), in dev also id-shopflow-github-plan-dev (OIDC: pull_request)
├── permissions: Contributor + constrained RBAC Administrator on the groups, AcrPull to grant in the registry group
└── the environment budget (filtered by the workload and environment tags)

main.bicep (subscription, every release with infrastructure changes)
├── layers/core.bicep        rg-…-core   network (+ the GitHub runners subnet), DNS zones, Log Analytics, App Insights, application identities
├── layers/data.bicep        rg-…-data   Cosmos DB, SQL, Redis, 3× Storage, Key Vault, App Configuration, ACS
├── layers/messaging.bicep   rg-…-data   Service Bus (from the JSON topology), Event Hubs, Event Grid
├── layers/apps.bicep        rg-…-apps   App Service, Container Apps (+ KEDA, the migrations job), 7× Functions Flex, DTS
├── layers/edge.bicep        rg-…-apps   API Management, Front Door Premium + WAF
└── layers/operations.bicep  rg-…-core   alerts, action group, workbook
```

| Directory | Content |
|---|---|
| `bootstrap/` | the administrator's templates and their parameters (`shared.bicepparam`, `<env>.bicepparam`) |
| `shared/` | environment configuration types (`types.bicep`), naming and tagging functions (`naming.bicep`), role identifiers, the list of delegable roles (`roles.bicep`) |
| `modules/` | resource modules; each with diagnostics to Log Analytics and roles at the narrowest scope |
| `modules/messaging/messaging-topology.json` | the Service Bus and Event Hubs topology: read by Bicep, the tests (`ShopFlow.Contracts.Tests`, `ShopFlow.Architecture.Tests`) and `tools/ShopFlow.DlqReplay` |
| `parameters/` | `dev`, `test`, `prod` (`.bicepparam` checked against the `environmentConfig` type); `IMAGE_TAG`, `IMAGE_TAGS` and `APIM_PRIVATE_ONLY` come from the pipeline's environment variables |
| `apim/policies/` | API Management policies (XML loaded with `loadTextContent`) |
| `workbooks/`, `sql/`, `scripts/` | the operations workbook, SQL users for identities, the Entra ID bootstrap |

## What to look at

- **Topology as data.** [servicebus.bicep](modules/messaging/servicebus.bicep) does not contain a single queue name. The entities, rules (including `ReplayTarget IS NULL` for DLQ replay) and per-entity roles follow from the JSON file, which the tests compare with the code.
- **Zero keys.** `disableLocalAuth` / `allowSharedKeyAccess: false` everywhere, and application settings contain only addresses and identity `clientId`s. Third-party secrets are in Key Vault, and applications get them through references in App Configuration.
- **Roles next to the resource, not the application.** Assignments are in the data and messaging layers, on a queue, a container or a database. [data.bicep](layers/data.bicep) shows who can do what.
- **User-assigned identities created before the compute,** so that the roles exist before the application starts.
- **The pipeline does not grant itself permissions.** The bootstrap gives it Contributor on three groups, an RBAC Administrator with a condition (only the roles on the `delegableRoles` list) and a role for deployment objects in the subscription ([environment.bicep](bootstrap/environment.bicep)). A compromised dev pipeline cannot change anything in prod.
- **Infrastructure does not change application versions:** the images of the running applications come in `IMAGE_TAGS` ([apps.bicep](layers/apps.bicep), the `images` variable).
- **Order instead of cycles:** APIM → Front Door → the APIM global policy with the Front Door identifier.
- **Loop arrays known at the start of the deployment:** identifiers computed during the deployment are kept in separate maps (`endpointTargets`, `runtimeFunctionSettings`).
- **Naming:** `resourceName(abbreviation, component, env, region)` in [naming.bicep](shared/naming.bicep); an empty component for resources unique in an environment keeps `kv-shopflow-prod-weu` within the Key Vault 24-character limit.

## Deployment

A pull request changing `infra/` gets a what-if comment against dev ([infra-validate.yml](../.github/workflows/infra-validate.yml)). After the merge the pipeline deploys the changes to dev, test and prod in turn ([release.yml](../.github/workflows/release.yml)). Manually, from an administrator account (bootstrap):

```powershell
az deployment sub create --location westeurope --template-file infra/bootstrap/shared.bicep --parameters infra/bootstrap/shared.bicepparam
```

```powershell
az deployment sub create --location westeurope --template-file infra/bootstrap/environment.bicep --parameters infra/bootstrap/dev.bicepparam
```

```powershell
$env:IMAGE_TAG = "<sha>"; az deployment sub create --location westeurope --template-file infra/main.bicep --parameters infra/parameters/dev.bicepparam
```

> These commands create billable Azure resources. In this learning repository they are documentation only; nothing is
> deployed (the Claude Code hook in `.claude/hooks/guard-cloud-commands.mjs` blocks them by default).

## Environment bootstrap (administrator, once)

1. Resource providers (`az provider register --namespace GitHub.Network`, likewise `Microsoft.App`, `Microsoft.DurableTask`), app registrations and the SQL administrators group: [scripts/bootstrap-entra-apps.sh](scripts/bootstrap-entra-apps.sh). The identifiers go into `parameters/<env>.bicepparam`, including `githubBusinessId` (`gh api graphql -f query='{ organization(login: "shopflow-org") { databaseId } }'`).
2. `bootstrap/shared.bicep` (once for all environments), then `bootstrap/environment.bicep` with `bootstrap/<env>.bicepparam`. The outputs (identity clientIds) are GitHub repository and environment variables: [.github/README.md](../.github/README.md).
3. Images: run `release` manually. The `ci` and `build` jobs push images tagged with the SHA. The `deploy (dev)` job waits for a runner group that does not exist yet: cancel it.
4. The first deployment of `main.bicep` from an administrator account with `IMAGE_TAG` = the SHA from step 3. Among other things it creates the subnet and the runner network resource.
5. Runner networking: the `githubRunnersNetworkId` output value → a network configuration in the GitHub organization → the `shopflow-<env>` runner group.
6. SQL: the Directory Readers role for the server identity, then [sql/grant-managed-identities.sql](sql/grant-managed-identities.sql) (identity users and role membership; the role permissions are granted by the `ApplicationRoleGrants` migration).
7. Key Vault secrets: `psp-api-key`, `psp-webhook-secret-current`, `psp-webhook-secret-previous`, `carrier-api-key`, `carrier-webhook-token`.
8. `release` manually with `deploy-all`: migrations, applications, smoke test. From then on a release starts after every merge to main.
9. Approve the Private Link connections from Front Door on APIM and on the documents account, then set the `APIM_PRIVATE_ONLY=true` variable in the GitHub environment and run `release` with `deploy-all`.
10. DNS records for the Front Door domains (`api.`, `files.`) and the ACS e-mail domain (SPF, DKIM, DMARC).

> In this repository Bicep has not been compiled locally (no Bicep CLI). The first compilation and lint run in the
> [infra-validate.yml](../.github/workflows/infra-validate.yml) workflow (`az bicep build`, `az bicep lint`).
> API versions were chosen from the generally available ones; a few newer services (Azure Managed Redis, Durable Task
> Scheduler, KEDA scale rules with an identity, `GitHub.Network/networkSettings`) use the latest known versions and are
> worth verifying at the first compilation. Adding a resource: the `/add-azure-resource` skill; reviewing Bicep: the
> `bicep-reviewer` agent.
