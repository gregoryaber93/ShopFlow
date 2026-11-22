---
paths:
  - "src/BuildingBlocks/ShopFlow.Persistence.Cosmos/**"
  - "src/BuildingBlocks/ShopFlow.Persistence.SqlServer/**"
  - "src/Services/**/Cosmos/**"
  - "src/Services/**/Persistence/**"
  - "src/Services/**/*Repository*.cs"
  - "src/Services/**/*Store*.cs"
  - "infra/modules/data/**"
  - "infra/sql/**"
---

# Persistence rules (Cosmos DB, Azure SQL, Redis)

Which store a context uses and why: [ADR-0006](../../docs/adr/0006-cosmos-or-sql-per-context.md). Partition keys:
[ADR-0007](../../docs/adr/0007-cosmos-partition-keys.md).

## Cosmos DB

- The partition key is chosen once. A document, its outbox records and its inbox entries live in **one partition**
  so they can be written in one `TransactionalBatch`. Never introduce a cross-partition write that must be atomic.
- Point reads (`ReadItemAsync` with id + partition key) over queries; queries stay within one partition. A
  cross-partition query in a request path needs a `// WHY:` and usually means a read model is missing (CQRS).
- Optimistic concurrency with `IfMatchEtag`; a 412 becomes a `ConcurrencyConflictException`, a 409 on create a
  "document already exists" outcome. Never retry a conflicting write blindly.
- Document schema evolution: a `schemaVersion` field and a mapper that reads old versions (`OrderDocumentMapper`);
  documents are upgraded on write, not by a mass migration.
- Every request charge (RU) is recorded (`CosmosTelemetry`). A new container is declared in `infra/layers/data.bicep`
  with its partition key, TTL and indexing policy, mirrored by the `*Containers` class and the emulator fixture.
- Container and database roles are data-plane roles (`sqlRoleDefinitions`), granted per database in Bicep.

## Azure SQL (EF Core, Inventory)

- The domain stays persistence-ignorant: mapping in `IEntityTypeConfiguration<T>` classes, private setters and
  backing fields; `rowversion` as the concurrency token exposed as an ETag; CHECK constraints for invariants.
- Outbox and inbox tables (`messaging` schema) are written in the same `SaveChanges` as the aggregate.
- Schema changes are migrations created with `dotnet ef migrations add` (use the `/ef-migration` skill), never
  hand-edited `*.Designer.cs` or `*ModelSnapshot.cs`. Migrations must be backward compatible with the running
  version (expand/contract: add a column now, remove it in a later release).
- Permissions for application identities are granted in a migration (`ApplicationRoleGrants`) and
  `infra/sql/grant-managed-identities.sql`; the application never runs as `db_owner`.
- CI runs `dotnet ef migrations has-pending-model-changes`; a model change without a migration fails the build.

## Redis (HybridCache)

- Cache only data that tolerates bounded staleness; keys and TTLs are documented in
  [00-overview.md](../../docs/architecture/00-overview.md). Invalidate by tag on change events.
- Authentication with Entra ID (no access keys). A cache outage must degrade to the database, not fail requests.
