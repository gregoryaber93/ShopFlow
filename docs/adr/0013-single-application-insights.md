# ADR-0013: One Application Insights per environment

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | platform |

## Context

One order passes through six processes, three brokers and two databases, and its handling takes from seconds (the
saga) to days (delivery). The on-call engineer must see the whole flow with one query. Telemetry is billed by data
volume and is one of the larger items on the bill in prod.

## Decision

- **One Log Analytics workspace and one (workspace-based) Application Insights resource per environment.**
  Services are told apart by `cloud_RoleName` (`AppRoleName` in the `App*` tables).
- **OpenTelemetry** in every process (APIs, containers, Functions in OpenTelemetry mode) with the Azure Monitor
  exporter authenticated with Entra ID; custom `ActivitySource` and `Meter` with the `ShopFlow.` prefix.
- **Two identifiers:** `traceparent` (a technical trace of one execution) and `CorrelationId` (a business one, constant
  for the whole order, also across traces).
- **Sampling** of traces: 100% in dev and test, 25% in prod; the decision is based on the trace identifier, so a trace
  is kept whole or not at all. Metrics are not sampled.

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **One resource per environment** (chosen) | the application map and the end-to-end trace in one place; one KQL query by `CorrelationId` | one daily cap for everyone; per-service access needs table-level RBAC |
| A resource per service | isolation of cost and access | a distributed trace needs cross-resource queries (`app()`); separate caps and alerts ×12 |
| Managed Prometheus + Grafana | a metrics standard, cheap storage | traces and logs elsewhere anyway; Functions without scraping |
| A third-party APM | a richer UI | cost per host, data outside Azure, another contract |

## Consequences

- Metric and attribute names are **a public monitoring contract**: renaming one breaks the queries in [docs/kql](../kql)
  and the alerts ([MessagingTelemetry.cs](../../src/BuildingBlocks/ShopFlow.Messaging/Diagnostics/MessagingTelemetry.cs)).
- **Cost** is controlled by sampling, the daily cap in dev (5 GB), retention 30/30/90 days and log levels
  ([cost.md](../cost.md#observability)). With 25% sampling the number of requests in `AppRequests` is an estimate:
  sum `ItemCount` instead of `count()`.
- **The connection string is not a secret**: local authentication is disabled (`DisableLocalAuth`), and telemetry
  without an Entra token is rejected.
- A trace breaks at a time boundary (the saga waits an hour for a webhook); `CorrelationId` stitches the whole together
  ([order-trace.kql](../kql/order-trace.kql)).

## In the repository

[ShopFlow.Observability](../../src/BuildingBlocks/ShopFlow.Observability), [observability.bicep](../../infra/modules/monitoring/observability.bicep),
[alerts.bicep](../../infra/modules/operations/alerts.bicep), [workbook](../../infra/workbooks/shopflow-operations.workbook.json), [docs/kql](../kql).
