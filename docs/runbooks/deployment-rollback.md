# Runbook: rolling back a deployment

When: after a deployment the failure rate grows (the `failed-requests` alert), the smoke test passed but customers report
a problem, or the new version fills the DLQ. Tool: the [rollback.yml](../../.github/workflows/rollback.yml) workflow.
The deployment strategies this runbook reverses: [ADR-0014](../adr/0014-deployment-strategies.md).

## 1. Is it the deployment?

[errors-by-service.kql](../kql/errors-by-service.kql), section "before and after a deployment": failures per `AppVersion`
(build number `1.0.{run_number}`). If only the new version fails, roll back. If both do, the cause is elsewhere
(a dependency, data, traffic).

Which unit and from which release: the summary of the `deploy ({env})` job in GitHub Actions ("Deployment plan") shows
what was deployed.

## 2. Roll back or fix forward?

| Situation | Decision |
|---|---|
| the bug affects customers, the cause is unknown | **roll back**, then look for the cause |
| the cause is known, the fix is one line, the pipeline to prod takes < 30 min | fixing forward is acceptable (a revert commit or a fix through a pull request) |
| the release contained a schema migration | roll back the application; the schema is not rolled back (expand/contract guarantees the old version works on the new schema) |
| a message contract change (a new `V2`) | roll back the consumer or the publisher carefully: v2 messages already in queues must have a receiver; usually a publishing feature flag instead of a rollback |
| a broken infrastructure change | a revert in Bicep through a pull request; an application rollback does not undo infrastructure |

## 3. Rollback per unit type

### `orders-api` (App Service, swap)

The previous version waits in the `staging` slot since the last deployment. GitHub → Actions → **rollback** → Run workflow:
`environment`, `target: orders-api`, `image-tag` empty. The workflow swaps `staging` → `production` and runs a smoke test.

Manually (from anywhere, through ARM):

```bash
az webapp deployment slot swap -g $RG_APPS -n app-shopflow-orders-$ENV-weu --slot staging --target-slot production
```

Note: a second swap in a row brings the faulty version back. After a rollback the reverted version is in `staging`; the
next normal deployment overwrites it.

### `inventory-api`, `inventory-worker`, `readmodels-api` (Container Apps)

Find the tag of the previous image (commit SHA):

```bash
APP=ca-shopflow-inventory-api-$ENV-weu
az containerapp revision list -g $RG_APPS -n $APP \
  --query "sort_by([], &properties.createdTime)[].{revision:name, created:properties.createdTime, image:properties.template.containers[0].image}" -o table
```

GitHub → Actions → **rollback**: `target: inventory-api`, `image-tag: <sha from the list>`. The workflow creates a new
revision with the previous image, waits for readiness and runs a smoke test. Images stay in the registry (the 30-day
retention policy applies to untagged manifests; images tagged with a SHA remain).

### Functions (Flex Consumption)

Functions have neither slots nor images. A rollback means re-running the `deploy ({env})` job of the last **successful**
release before the faulty one (Actions → release → that run → Re-run jobs → pick the deploy job). The `packages`
artifact is kept for 30 days.

A side effect you need to know about: the `deployedCommit` tag points at the faulty (newer) commit, which is not an
ancestor of the old one, so the job deploys **all units and the infrastructure** from that release. For a release that
contained infrastructure changes a revert through a pull request is better.

### Schema migrations

Not rolled back. The EF Core `Down` migration exists in the code, but running it in production deletes the data in the
columns added in the release. If the migration itself is wrong (e.g. it locks a table), the DBA decides together with the
owning team; the fix is a new forward migration.

## 4. After the rollback

- The **rollback** workflow removes the `deployedCommit` tag. The next release deploys all units: intentionally, because
  the environment no longer matches a single commit.
- Block the faulty release from being promoted to the next environments: reject the pending `test` or `prod` approval in
  the release run.
- Replay the messages the new version sent to the DLQ after the rollback, according to [dlq-growth.md](dlq-growth.md).

## Verification

- The smoke test in the workflow passed.
- The role's failure rate returns to its pre-deployment level ([errors-by-service.kql](../kql/errors-by-service.kql)).
- `AppVersion` in new requests is the previous version.

## Follow-up

A post-mortem: why tests, Pact and the smoke test did not catch the problem. Add the missing test or smoke test check
([smoke.sh](../../.github/actions/smoke-test/smoke.sh)) in the same pull request as the fix.
