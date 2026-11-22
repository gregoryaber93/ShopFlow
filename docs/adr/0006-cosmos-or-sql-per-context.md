# ADR-0006: Cosmos DB or Azure SQL per context

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | architecture, context owners |

## Context

Every context owns its data ([00-overview.md](../architecture/00-overview.md), principle 1). The choice of store depends
on the shape of the data and the queries. An order, a payment and a shipment are documents changed as a whole and always
read by the order identifier. The catalog and stock levels are relational data: products, categories, prices over time,
stock per warehouse and SKU, reservations spanning several rows at once, back-office queries across many dimensions.

## Decision

- **Cosmos DB (NoSQL API)**: Orders, Saga (projection), Payments, Shipping, Notifications, Customer Read Models,
  Analytics. One account per environment, a separate database per context, data-plane RBAC at the database level.
- **Azure SQL Database**: Inventory together with the Catalog (`sqldb-inventory`, schemas `catalog`, `inventory`,
  `ref`, `messaging`), EF Core migrations.

The Catalog is part of Inventory (decision D2): they share the `Product/Sku` model and the database.

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **Store per context** (chosen) | documents where the aggregate is a document; relations where the queries are relational | two data models, two outbox variants, two skill sets in the team |
| Everything in Cosmos DB | one SDK, Change Feed everywhere | reserving many SKUs across many partitions without a transaction; back-office queries expensive in RU |
| Everything in Azure SQL | transactions, mature tooling | vertical scaling of order writes; outbox only through polling; per-customer queries on the write model |
| A separate Catalog service (an eighth context) | cleaner boundaries | an extra service and database; prices and stock are needed together anyway |

## Consequences

- Inventory shows the **second outbox variant** (a table in the same EF Core transaction + a poller), the other contexts
  the Cosmos variant with the Change Feed ([ADR-0009](0009-outbox-and-inbox.md)).
- SQL has strong consistency: a missing reservation really is missing, so an `OrderConfirmed` without a reservation goes
  straight to the DLQ (in Cosmos with Session consistency it would be a retry).
- Cost in dev: SQL serverless with auto-pause after 60 min, Cosmos serverless ([cost.md](../cost.md)).
- Schema migrations need expand/contract discipline, because the old and the new application versions run together
  ([ADR-0014](0014-deployment-strategies.md)).

## In the repository

[data.bicep](../../infra/layers/data.bicep), [cosmos.bicep](../../infra/modules/data/cosmos.bicep), [sql.bicep](../../infra/modules/data/sql.bicep),
`InventoryDbContext` and the migrations in `src/Services/Inventory/ShopFlow.Inventory.Infrastructure/Persistence`.
