# ADR-0004: Functions Flex Consumption and Durable Task Scheduler

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | platform, team-checkout |

## Context

Seven function apps (event processors, webhooks, the saga) need VNet integration (data behind private endpoints), a
fixed outbound address (PSP, carrier), a managed identity and a near-zero cost in dev. The saga needs a durable Durable
Functions backend that survives timers of up to 15 minutes and waiting for an operator for days.

## Decision

1. All functions on the **Flex Consumption** plan (Linux, .NET 10 isolated worker), with VNet integration in
   `snet-func`, a NAT Gateway and, in prod, one always-ready instance for the `http` group (PSP and carrier webhooks,
   the saga operator API, clickstream).
2. Durable Functions backend: **Durable Task Scheduler** (Consumption in dev and test, Dedicated in prod), accessed
   only with a managed identity that has a role on the task hub.

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **Flex Consumption** (chosen) | VNet and scale to zero at the same time; per-function scaling; always-ready instead of "always pay"; the package in Storage with an identity | no slots (rolling update); Linux only; a newer service (regional limits) |
| Elastic Premium (EP1) | slots, no cold start, maturity | at least one instance always paid, ×7 apps ×3 environments |
| Consumption (Y1) | the cheapest | no VNet integration; the Linux Consumption plan is being retired |
| Functions in Container Apps | one platform with the APIs | limited trigger and Durable support; custom KEDA scalers |

| Durable backend | Pros | Cons |
|---|---|---|
| **Durable Task Scheduler** (chosen) | managed service, instance dashboard, higher throughput, Entra ID only | a new service; the cost of Dedicated in prod; no private endpoint in this project (IP allowlist) |
| Azure Storage | the default, best documented | tables and queues in a Storage account as a bottleneck; keys or many roles on the account; no dashboard |
| MSSQL | transactions, SQL queries over the history | another database to run and scale |

## Consequences

- **No slots:** a function deployment is a rolling update, and two code versions run side by side for a few minutes.
  Contracts and documents must be backward compatible, and orchestrations versioned (`defaultVersion` in the saga's
  host.json, [saga.md](../architecture/saga.md#orchestration-versioning)).
- **Cold start:** in dev and test the first invocation after a pause waits a few seconds. In prod the HTTP functions
  have a warm instance; queue and Change Feed triggers may start cold, which for asynchronous processing means only a
  few seconds of delay.
- **The scale cap** (`maximumInstanceCount`) protects dependencies: 100 outbox relay instances would be 100 concurrent
  Change Feed readers and Service Bus senders.
- **Code deployment through SCM** requires a private endpoint on every app and a runner in the VNet
  ([ADR-0011](0011-network-topology.md), decision D6 in the overview).

## In the repository

[function-app-flex.bicep](../../infra/modules/compute/function-app-flex.bicep),
[durable-task-scheduler.bicep](../../infra/modules/compute/durable-task-scheduler.bicep),
[the saga's host.json](../../src/Services/OrderSaga/ShopFlow.OrderSaga.Functions/host.json),
[order-saga README](../../src/Services/OrderSaga/ShopFlow.OrderSaga.Functions/README.md).
