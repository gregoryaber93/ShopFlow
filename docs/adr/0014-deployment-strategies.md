# ADR-0014: Deployment strategies: slots, revisions, rolling update

| Status | Date | Deciders |
|---|---|---|
| Accepted (extended after the CI/CD stage on 2026-11-22) | 2026-11-14 | platform |

## Context

The three compute platforms ([ADR-0003](0003-compute-per-workload.md)) have three different mechanisms for replacing a
version. A deployment must not interrupt orders in flight, and going back to the previous version must be fast and rehearsed.

## Decision

| Unit | Strategy | Verification | Rollback |
|---|---|---|---|
| `orders-api` (App Service) | zip package to the `staging` slot, smoke test, **swap** | `/health/ready` and smoke on the slot, smoke after the swap | another swap (the previous version waits in the `staging` slot) |
| `inventory-api`, `inventory-worker`, `readmodels-api` (Container Apps) | **a new revision in Single mode** with an immutable image tag (SHA) | readiness probe; the pipeline waits until `latestReadyRevisionName` = the new revision | a new revision with the previous image ([rollback.yml](../../.github/workflows/rollback.yml)) |
| function apps (Flex Consumption) | **rolling update** after `config-zip` | smoke through Front Door after the deployment | deploying the previous package (the `packages` artifact of the previous release) |
| SQL schema | **migration bundle** in a Container Apps Job before the applications | the job execution status | none automatic: expand/contract only, migrate forward |

Common rules:

- **Build once, deploy many**: the same image and the same package go through dev → test → prod.
- **Only the units that changed** since the last successful deployment to a given environment are deployed (the
  `deployedCommit` tag).
- **Backward compatibility is a requirement**, because two versions run for a while (rolling update, the window between
  the migration and the application, messages in queues).

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **The platform's native mechanism** (chosen) | simplest; predictable rollback | three different paths in the pipeline and in the runbook |
| Container Apps in Multiple mode with traffic splitting (canary 10% → 100%) | gradual exposure, fast rollback by weights | a queue worker has no "traffic" to split; the pipeline must drive the weights and clean up revisions |
| Blue-green at the Front Door level (two full stacks) | full version isolation | double the cost; data and brokers are shared anyway |
| Functions on a plan with slots (Elastic Premium) | swap for functions | a fixed cost ×7 ([ADR-0004](0004-functions-flex-consumption-and-dts.md)) |

## Consequences

- **Expand/contract migrations:** a column is removed only in the release in which no running version reads it; the pull
  request template requires a confirmation. An application rollback does not roll back the schema.
- **Orchestrations** have their own versioning (`defaultVersion` in the saga's host.json).
- **Cosmos documents** have a `SchemaVersion`, and the mapper reads older versions.
- **A rollback does not undo infrastructure or data**; this deliberate limitation is described in
  [deployment-rollback.md](../runbooks/deployment-rollback.md).
- The order in one job: infrastructure → migrations → synchronously called APIs → `orders-api` → functions
  ([_deploy.yml](../../.github/workflows/_deploy.yml)).

## In the repository

[_deploy.yml](../../.github/workflows/_deploy.yml), [rollback.yml](../../.github/workflows/rollback.yml),
the actions in [.github/actions](../../.github/actions), [container-app.bicep](../../infra/modules/compute/container-app.bicep),
[app-service.bicep](../../infra/modules/compute/app-service.bicep).
