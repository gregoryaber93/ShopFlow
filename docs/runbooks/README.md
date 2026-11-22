# Runbooks

Procedures for the on-call engineer. Every alert in [alerts.bicep](../../infra/modules/operations/alerts.bicep) points to
a runbook. A runbook always has the same parts: symptoms, customer impact, diagnosis, actions, verification, follow-up.

| Alert (`alert-…-{env}-weu`) | Severity | Runbook |
|---|---|---|
| `sb-deadletters`: a message in a DLQ | 2 | [dlq-growth.md](dlq-growth.md) |
| `sb-backlog`: queue backlog > 1000 for 15 min | 3 | [dlq-growth.md](dlq-growth.md#backlog-without-a-dlq-sb-backlog-alert) |
| `outbox-lag`: events in the outbox for more than 2 min | 2 | [dlq-growth.md](dlq-growth.md#outbox-not-publishing-outbox-lag-and-outbox-malformed-alerts) |
| `outbox-malformed`: a skipped malformed outbox document | 1 | as above |
| `cosmos-429`: sustained 429s in Cosmos DB | 2 | [cosmos-throttling.md](cosmos-throttling.md) |
| `payment-provider-errors`: > 20% failed PSP calls | 1 | [payment-failure.md](payment-failure.md#a-the-payment-provider-does-not-respond-or-rejects-requests) |
| `saga-manual-intervention`: a saga waits for an operator | 1 | [payment-failure.md](payment-failure.md#b-saga-in-the-requiresmanualintervention-state) |
| `failed-requests`: > 5% failed calls of a service | 2 | diagnosis: [errors-by-service.kql](../kql/errors-by-service.kql); after a deployment: [deployment-rollback.md](deployment-rollback.md) |
| `projection-lag`: projection lag p95 > 60 s | 3 | [dlq-growth.md](dlq-growth.md#backlog-without-a-dlq-sb-backlog-alert) (the `readmodels-inbox` queue) |
| budget 80% / 100% (bootstrap) | — | [cost.md](../cost.md) |

Planned procedures: [secret-rotation.md](secret-rotation.md), [deployment-rollback.md](deployment-rollback.md).
With Claude Code: the `incident-responder` agent picks the runbook for an alert and walks through the diagnosis read-only.

## Before you start

- **Access:** roles in production are activated just-in-time in Privileged Identity Management (the on-call group).
  Without activation `az` returns 403 and the tools in the VNet get no token.
- **Network:** the data plane (Service Bus, Cosmos, SQL) has no public endpoint. `az ... list/show` commands go through
  Azure Resource Manager and work from a laptop; `shopflow-dlq` and queries against Cosmos and SQL must run from the VNet
  (Azure Bastion to a tools machine or a Container Apps job).
- **Logs:** Log Analytics `log-shopflow-{env}-weu`, Application Insights `appi-shopflow-{env}-weu`. Queries in
  [docs/kql](../kql); the order's `CorrelationId` is in the `X-Correlation-Id` header of API responses.
- **Names:** `rg-shopflow-{env}-weu-{core|data|apps}`, `sbns-shopflow-{env}-weu`, `cosmos-shopflow-{env}-weu`.
  The examples use `ENV=prod`.

```bash
ENV=prod
RG_DATA=rg-shopflow-$ENV-weu-data
RG_APPS=rg-shopflow-$ENV-weu-apps
SB=sbns-shopflow-$ENV-weu
```

## Rules

1. **Stop the damage first, then look for the cause.** A feature flag, a rollback or scaling is faster than a fix.
2. **Never replay a DLQ blindly.** Cause first, then `--dry-run`, then replay selected messages.
3. **Keep a timeline** in the incident ticket: what you saw, what you did, when. A blameless post-mortem.
4. **Every manual change in Azure goes back into code** (Bicep, `.bicepparam`) within one working day; otherwise the
   next deployment reverts it.
