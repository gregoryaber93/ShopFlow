using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopFlow.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The application's database roles and their permissions on schemas.
    /// </summary>
    /// <remarks>
    /// WHY: permissions are granted to roles, not users. Role names are the same in every environment, so the grants are
    /// part of the migrations: a schema change and the permission change for it go through the same pull request and the
    /// same deployment. Users (managed identities, whose names contain the environment) and their role membership are
    /// created once by an administrator: infra/sql/grant-managed-identities.sql.
    /// <para>
    /// AZURE: migrations run as a Container Apps job in the VNet with the id-shopflow-inventory-migrations-* identity, a
    /// member of db_owner. The database has no public endpoint, so the pipeline runner never connects to it.
    /// </para>
    /// </remarks>
    public partial class ApplicationRoleGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The administrator script also creates the roles (to add members before the first migration), hence the IF.
            migrationBuilder.Sql("""
                IF DATABASE_PRINCIPAL_ID(N'app_inventory_api') IS NULL CREATE ROLE app_inventory_api;
                IF DATABASE_PRINCIPAL_ID(N'app_inventory_worker') IS NULL CREATE ROLE app_inventory_worker;
                """);

            // inventory-api: catalog (reads and price changes by back-office), stock receipts, writing the outbox in the same
            // transaction as the state change.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON SCHEMA::catalog TO app_inventory_api;
                GRANT SELECT ON SCHEMA::ref TO app_inventory_api;
                GRANT SELECT, INSERT, UPDATE ON SCHEMA::inventory TO app_inventory_api;
                GRANT SELECT, INSERT ON SCHEMA::messaging TO app_inventory_api;
                """);

            // inventory-worker: reservations (including expiry), outbox (relay and clean-up), inbox.
            // It does not change prices: read-only access to the catalog.
            migrationBuilder.Sql("""
                GRANT SELECT ON SCHEMA::catalog TO app_inventory_worker;
                GRANT SELECT ON SCHEMA::ref TO app_inventory_worker;
                GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::inventory TO app_inventory_worker;
                GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::messaging TO app_inventory_worker;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The roles stay: they may have members added by the administrator, and DROP ROLE fails while a role has members.
            migrationBuilder.Sql("""
                REVOKE SELECT, INSERT, UPDATE ON SCHEMA::catalog FROM app_inventory_api;
                REVOKE SELECT ON SCHEMA::ref FROM app_inventory_api;
                REVOKE SELECT, INSERT, UPDATE ON SCHEMA::inventory FROM app_inventory_api;
                REVOKE SELECT, INSERT ON SCHEMA::messaging FROM app_inventory_api;
                REVOKE SELECT ON SCHEMA::catalog FROM app_inventory_worker;
                REVOKE SELECT ON SCHEMA::ref FROM app_inventory_worker;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON SCHEMA::inventory FROM app_inventory_worker;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON SCHEMA::messaging FROM app_inventory_worker;
                """);
        }
    }
}
