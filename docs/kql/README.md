# KQL queries

Queries against the Log Analytics workspace `log-shopflow-{env}-weu` (Application Insights is workspace-based, so
application telemetry is in the `App*` tables). Every file contains several queries separated by a comment; select one
in the portal and run it (Shift+Enter).

| File | Question | Used in |
|---|---|---|
| [order-trace.kql](order-trace.kql) | what happened to order X, from `POST /orders` to delivery | support, every runbook |
| [errors-by-service.kql](errors-by-service.kql) | which service fails; before or after a deployment | the `failed-requests` alert, [deployment-rollback](../runbooks/deployment-rollback.md) |
| [messaging.kql](messaging.kql) | throughput and processing outcome per message type, who still reads the old contract version | workbook, [ADR-0012](../adr/0012-contract-versioning.md) |
| [dead-letters.kql](dead-letters.kql) | what the application moved to the DLQ and why; the history of one message | [dlq-growth](../runbooks/dlq-growth.md) |
| [outbox.kql](outbox.kql) | publishing lag, skipped documents | the `outbox-lag`, `outbox-malformed` alerts |
| [queues.kql](queues.kql) | backlogs and consumer throughput | the `sb-backlog` alert |
| [cosmos-ru.kql](cosmos-ru.kql) | RU cost per container, operation and piece of code; 429s; hot partitions | the `cosmos-429` alert, [cost.md](../cost.md) |
| [saga-funnel.kql](saga-funnel.kql) | how many orders were confirmed, rejected, cancelled; which sagas wait for an operator | workbook, [payment-failure](../runbooks/payment-failure.md) |
| [payments.kql](payments.kql) | PSP calls and webhooks | the `payment-provider-errors` alert |

## Where the data comes from

| Table | Source | Key columns |
|---|---|---|
| `AppRequests` | Server and Consumer spans (HTTP, message processing, function invocation) | `AppRoleName`, `Name`, `Success`, `ResultCode`, `DurationMs`, `OperationId`, `AppVersion`, `Properties` |
| `AppDependencies` | Client and Producer spans (HTTP, Cosmos, Service Bus, SQL) | `DependencyType`, `Target`, `Name`, `Success`, `ResultCode` |
| `AppTraces`, `AppExceptions` | `ILogger` through OpenTelemetry | `Message`, `SeverityLevel` (0–4), `Properties` (log scope, e.g. `CorrelationId`) |
| `AppMetrics` | `Meter`s with the `ShopFlow.` prefix | `Name`, `Sum`, `ItemCount`, `Max`, `Properties` (dimensions) |
| `CDBDataPlaneRequests`, `CDBPartitionKeyRUConsumption` | Cosmos DB diagnostic settings (resource-specific tables) | `DatabaseName`, `CollectionName`, `OperationName`, `RequestCharge`, `StatusCode` |
| `AzureMetrics` | platform metrics through diagnostic settings | no dimensions (e.g. no Service Bus `EntityName`) |

## Pitfalls

- **The `Type` column** in every table is the table name. The dependency kind is in `DependencyType`.
- **Sampling** in prod (25% of traces): `count()` on `AppRequests` undercounts. Sum `ItemCount`. Metrics in
  `AppMetrics` are not sampled and are fit for counting.
- **Attributes and dimensions** have dotted names (`shopflow.message.type`), so read them with
  `tostring(Properties["shopflow.message.type"])`, not `Properties.shopflow...`.
- **Metric and attribute names are a contract** ([MessagingTelemetry.cs](../../src/BuildingBlocks/ShopFlow.Messaging/Diagnostics/MessagingTelemetry.cs)):
  a change in code requires a change here, in [alerts.bicep](../../infra/modules/operations/alerts.bicep) and in the workbook.
- **Platform metrics sent through diagnostic settings lose their dimensions.** The backlog per queue is in Metrics
  Explorer, in the workbook (a metrics query) or in `az servicebus queue list`, not in `AzureMetrics`.
