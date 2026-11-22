-- Users of the sqldb-inventory database for the managed identities, and their role membership.
-- Run by an administrator (a member of the server's Entra ID administrators group) once per environment and when an identity is added,
-- from a network that can see the server's private endpoint (e.g. Azure Bastion or a VPN):
--   sqlcmd -S <server>.database.windows.net -d sqldb-inventory --authentication-method ActiveDirectoryDefault \
--          -v Environment=prod Region=weu -i grant-managed-identities.sql
--
-- AZURE: CREATE USER ... FROM EXTERNAL PROVIDER creates a user linked to an Entra ID identity (no password).
-- The server checks that the identity exists in Entra ID with its system identity (Directory Readers role).
-- WHY: this script contains only what depends on the environment (identity names). The roles' permissions on schemas live
-- in the EF Core migration (ApplicationRoleGrants), so they change together with the schema and the pipeline deploys them.
-- The script does not depend on schemas, so it works before the first migration. It is idempotent.

DECLARE @api sysname = N'id-shopflow-inventory-api-$(Environment)-$(Region)';
DECLARE @worker sysname = N'id-shopflow-inventory-worker-$(Environment)-$(Region)';
DECLARE @migrations sysname = N'id-shopflow-inventory-migrations-$(Environment)-$(Region)';
DECLARE @sql nvarchar(max);

-- Roles (the ApplicationRoleGrants migration also creates them if they are missing).
IF DATABASE_PRINCIPAL_ID(N'app_inventory_api') IS NULL CREATE ROLE app_inventory_api;
IF DATABASE_PRINCIPAL_ID(N'app_inventory_worker') IS NULL CREATE ROLE app_inventory_worker;

-- Identity → role. The migrations job is a member of db_owner: it creates schemas, tables and roles and grants permissions.
DECLARE @members TABLE (identityName sysname, roleName sysname);
INSERT INTO @members VALUES
    (@api, N'app_inventory_api'),
    (@worker, N'app_inventory_worker'),
    (@migrations, N'db_owner');

DECLARE @identity sysname, @role sysname;
DECLARE members CURSOR LOCAL FAST_FORWARD FOR SELECT identityName, roleName FROM @members;
OPEN members;
FETCH NEXT FROM members INTO @identity, @role;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF DATABASE_PRINCIPAL_ID(@identity) IS NULL
    BEGIN
        SET @sql = N'CREATE USER ' + QUOTENAME(@identity) + N' FROM EXTERNAL PROVIDER;';
        EXEC sp_executesql @sql;
    END

    IF IS_ROLEMEMBER(@role, @identity) = 0
    BEGIN
        SET @sql = N'ALTER ROLE ' + QUOTENAME(@role) + N' ADD MEMBER ' + QUOTENAME(@identity) + N';';
        EXEC sp_executesql @sql;
    END

    FETCH NEXT FROM members INTO @identity, @role;
END
CLOSE members;
DEALLOCATE members;
