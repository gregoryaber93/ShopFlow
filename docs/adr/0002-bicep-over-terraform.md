# ADR-0002: Bicep instead of Terraform

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | platform |

## Context

All infrastructure is in Azure, in one region, in three subscriptions. The project uses services that are new or have
fast-changing APIs (Flex Consumption, Durable Task Scheduler, Managed Redis, `GitHub.Network/networkSettings`). The
platform team is small and does not want to maintain a backend for a state file.

## Decision

Bicep with `.bicepparam` files, deployed with `az deployment sub create`. The environment configuration is typed
(user-defined types in [types.bicep](../../infra/shared/types.bicep)), and names and tags are computed by functions
([naming.bicep](../../infra/shared/naming.bicep)). Changes are checked by what-if in the pull request, the linter and PSRule.

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **Bicep** (chosen) | no state file (Azure is the state); new API versions available from the day they ship; ARM-side what-if; `readEnvironmentVariable()` in parameters | Azure only; what-if is sometimes noisy; an incremental deployment does not delete resources removed from the code |
| Terraform (azurerm + azapi) | mature ecosystem, `plan` with an exact diff, community modules, multi-cloud | a state file to secure (it contains secret outputs), state locking; azurerm supports new services with a delay (leaving azapi) |
| Pulumi (C#) | the same language as the application | state and the managed service licence; a smaller Azure community |
| ARM JSON | native | unreadable; Bicep compiles to ARM |

## Consequences

- **No "destroy":** a resource removed from Bicep stays in Azure. Clean-up is manual or through deployment stacks
  (`az stack sub create --action-on-unmanage deleteResources`), considered as the next step.
- **What-if is a hint, not a contract:** it shows property changes ARM will ignore anyway (noise) and does not see side
  effects (e.g. an application restart after a settings change). A reviewer looks first at `Delete` and `Modify` of
  data resources (pull request template).
- **Bootstrap separately:** resources that need higher permissions than the pipeline (custom roles, OIDC identities,
  the budget) live in [infra/bootstrap](../../infra/bootstrap) and are deployed by an administrator.
- **Testability:** PSRule for Azure checks Well-Architected rules against expanded `.bicepparam` files
  ([ps-rule.yaml](../../ps-rule.yaml)); the linter with the rules from [bicepconfig.json](../../infra/bicepconfig.json) blocks the pull request.

## In the repository

- [infra/main.bicep](../../infra/main.bicep) → [layers](../../infra/layers) → [modules](../../infra/modules).
- [infra-validate.yml](../../.github/workflows/infra-validate.yml): build, lint, what-if as a PR comment.
- Messaging topology as data: [`messaging-topology.json`](../../infra/modules/messaging/messaging-topology.json) read through `loadJsonContent()`.
