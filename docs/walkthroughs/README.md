# Walkthroughs: what executes where

Each walkthrough follows one concrete scenario through the system, hop by hop: which deployable unit runs, on which
Azure service, which class handles it, what is stored, which message goes where, which identity is allowed to do it,
and what happens when the hop fails. Read them next to the code; every step links to the file that implements it.

| # | Scenario | Main patterns | Units involved |
|---|---|---|---|
| [01](01-place-order.md) | A customer places an order (happy path) | idempotent API, transactional outbox, orchestrated saga, competing consumers, sessions | orders-api, orders-processors, order-saga, inventory-api, inventory-worker, payments-processor, shipping-processor |
| [02](02-payment-declined-compensation.md) | The card is declined (also 3-D Secure and timeouts) | compensating transaction, state-tolerant compensation, webhook + inbox | order-saga, payments-processor, inventory-worker, orders-api |
| [03](03-customer-cancellation.md) | The customer cancels while the saga is running | optimistic concurrency (ETag), race resolution, compensation | orders-api, order-saga, all participants |
| [04](04-shipping-delivery-invoice.md) | After confirmation: invoice, dispatch, delivery | choreography, Blob + Event Grid as a file outbox, valet key (SAS), partner webhook | payments-processor, shipping-processor, orders-processors, notifications-processor |
| [05](05-poison-message-and-replay.md) | A message keeps failing and lands in the DLQ | retry vs. dead letter, failure classification, DLQ replay | any consumer, `shopflow-dlq` |
| [06](06-catalog-price-change.md) | Back-office changes a price | optimistic concurrency, cache-aside with HybridCache, SQL outbox + poller | inventory-api, inventory-worker, orders-api |
| [07](07-my-orders-read-model.md) | The customer opens "My orders" | CQRS, order-tolerant projection, read-your-writes | readmodels-projections, readmodels-api |
| [08](08-clickstream-analytics.md) | Clickstream and revenue metrics | event streaming, partitioning, checkpointing, exactly-once aggregation | analytics-processor, APIM |
| [09](09-release-pipeline.md) | A merge to main reaches production | build once deploy many, OIDC, slots/revisions/rolling update | GitHub Actions, all units |
| [10](10-contract-v2-rollout.md) | Rolling out a breaking contract change (`OrderSubmitted` v2) | versioned contracts, upcaster, parallel publishing, feature flag | orders-processors, order-saga, readmodels-projections |

## How to read the step tables

| Column | Meaning |
|---|---|
| **Where** | deployable unit and its Azure hosting (see the [deployable units table](../architecture/00-overview.md)) |
| **What happens** | the behavior of this hop in one or two sentences |
| **Code** | the class (and method) that implements it; open it next to the walkthrough |
| **Data / message** | what is read or written (container, table, blob) and what is sent (contract → entity) |

Abbreviations: **SB** Service Bus, **CF** Cosmos DB Change Feed, **MI** managed identity, **DTS** Durable Task Scheduler.
Entity names come from [messaging-topology.json](../../infra/modules/messaging/messaging-topology.json); resource names
follow the naming convention, e.g. `sbns-shopflow-prod-weu`.

## Learning path

1. Read [00-overview.md](../architecture/00-overview.md) for the big picture and
   [patterns.md](../architecture/patterns.md) for the vocabulary.
2. Walk through [01](01-place-order.md) with the code open. It touches almost every building block.
3. Continue with the failure scenarios [02](02-payment-declined-compensation.md), [03](03-customer-cancellation.md)
   and [05](05-poison-message-and-replay.md): they show why the happy path is built the way it is.
4. Each walkthrough ends with the tests that exercise it. Run them locally (free) and change something to see a test fail.

To trace a scenario that is not covered here, use the `/trace-flow` skill in Claude Code.
