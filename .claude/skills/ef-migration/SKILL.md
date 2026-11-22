---
name: ef-migration
description: Creates and reviews an EF Core migration for the Inventory SQL database the safe way (expand/contract, no data loss, permissions, no hand-edited designer files) without connecting to any database. Use when the Inventory model changes or CI reports pending model changes.
argument-hint: "<MigrationName>"
---

# EF Core migration (Inventory, Azure SQL)

Only `ShopFlow.Inventory.Infrastructure` uses EF Core. A design-time factory
(`Persistence/DesignTimeInventoryDbContextFactory.cs`) builds the context without a real connection, so these
commands are offline and free.

## 1. Change the model

Entity configuration lives in `Persistence/Configurations/*.cs` (`IEntityTypeConfiguration<T>`). Keep the domain
persistence-ignorant (private setters, backing fields), `rowversion` as the concurrency token, CHECK constraints for
invariants.

## 2. Is the change backward compatible?

The running application version keeps working against the new schema until its deployment finishes
(migrations run as a Container Apps job **before** the new code). Allowed in one release:

- add a nullable column or a column with a default, add a table, add an index (consider `ONLINE = ON` in prod),
- widen a column.

Needs expand/contract over two or more releases: rename (add new + backfill + switch reads + drop old later), drop a
column or table, narrowing a type, making a column NOT NULL without a default.

## 3. Create the migration

```bash
dotnet tool restore
dotnet ef migrations add <MigrationName> --project src/Services/Inventory/ShopFlow.Inventory.Infrastructure --output-dir Persistence/Migrations
dotnet ef migrations has-pending-model-changes --project src/Services/Inventory/ShopFlow.Inventory.Infrastructure
dotnet ef migrations script --idempotent --project src/Services/Inventory/ShopFlow.Inventory.Infrastructure --output artifacts/migrations.sql
```

Review the generated `Up`/`Down` and the SQL script: no unexpected `DROP`, data-preserving column changes, seed data
(`HasData`) changes produce `UpdateData` and not delete/insert cascades.

## 4. Rules

- Never edit `*.Designer.cs` or `InventoryDbContextModelSnapshot.cs` by hand (the `protect-generated-files` hook
  blocks it). To redo a migration that was never deployed: `dotnet ef migrations remove`, then add again.
- Permissions for application identities belong in a migration (see `ApplicationRoleGrants`) or
  `infra/sql/grant-managed-identities.sql`, never `db_owner` for the application.
- Never run `dotnet ef database update` against Azure, and do not add a connection string with credentials anywhere.

## 5. Verify

`dotnet build ShopFlow.slnx`, Inventory unit tests, and the integration tests if Docker is available
(`SHOPFLOW_RUN_INTEGRATION_TESTS=1 dotnet test --project src/Services/Inventory/tests/ShopFlow.Inventory.IntegrationTests`).
Then `/verify`.
