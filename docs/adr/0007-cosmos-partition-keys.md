# ADR-0007: Partition keys in Cosmos DB

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | architecture, team-checkout |

## Context

A Cosmos DB partition key is chosen once: changing it requires a new container and a data migration. Three things depend
on it: what can be written atomically (`TransactionalBatch` works only within one logical partition), which queries are
cheap (within one partition), and whether traffic spreads evenly (a hot partition is capped at 10,000 RU/s per physical
partition; a logical partition holds up to 20 GB).

## Decision

| Container | Key | Why |
|---|---|---|
| `orders/orders` | `/orderId` | the aggregate, outbox and inbox in one partition → one `TransactionalBatch`; high cardinality |
| `orders/carts` | `/customerId` | one cart per customer, always read by customer |
| `orders/idempotency` | `/scopeKey` = `{customerId}:{Idempotency-Key}` | a key must not collide between customers |
| `saga/saga-state`, `payments/payments`, `shipping/shipments` | `/orderId` | like `orders`: document + outbox + inbox atomically |
| `readmodels/customer-orders` | `/customerId` | "my orders" is a single-partition query |
| `notifications/notification-log` | `/customerId` (alerts: `operations`) | a customer's history; send idempotency in the same partition |
| `analytics/aggregates` | `/metricId` = `{metric}:{window}` | the counters of one window in one partition; later minutes are new partitions |

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **Key from the dominant query and the transaction boundary** (chosen) | an atomic outbox; cheap reads | the write and read models are partitioned differently → CQRS |
| `/customerId` for orders | "a customer's orders" in one partition | the order's outbox in the customer's partition; a B2B customer with thousands of orders → a hot partition and the 20 GB limit |
| Hierarchical key `/customerId/orderId` | both queries in one container | `TransactionalBatch` needs the full key; a more complex migration |
| `/id` (random key) | perfect distribution | no transaction with the outbox; every query other than by `id` crosses partitions |

## Consequences

- **CQRS has a concrete reason here:** "a customer's orders" on the write model would be a cross-partition query, so the
  read model is partitioned by `customerId`, and the projector needs `customerId` in every order event (hence that field
  in the contracts).
- **Every event is a separate outbox document**, because the Change Feed in its default mode shows only the latest
  version of a document.
- **Optimistic concurrency** (`ETag` / `If-Match`) instead of locks: `OrderCommandExecutor` retries the
  read-modify-write on 412, and the API returns 412 to the client when the client's `If-Match` is stale.
- **Hot partition risk** in `aggregates` under heavy traffic within one minute: watch 429s per partition
  ([cosmos-ru.kql](../kql/cosmos-ru.kql)); the fix would be a shard suffix in `metricId`.

## In the repository

[data.bicep](../../infra/layers/data.bicep) (container definitions), [OrdersContainers.cs](../../src/Services/Orders/ShopFlow.Orders.Infrastructure/Cosmos/OrdersContainers.cs),
[CosmosUnitOfWork.cs](../../src/BuildingBlocks/ShopFlow.Persistence.Cosmos/CosmosUnitOfWork.cs).
