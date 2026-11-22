using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Persistence.SqlServer.Messaging;

namespace ShopFlow.Inventory.Infrastructure.Persistence;

/// <summary>
/// Creates the context for EF Core tooling (dotnet ef migrations add, migrations bundle) without starting the application.
/// </summary>
/// <remarks>
/// WHY: without this factory the tools would try to build the application host, which connects to App Configuration
/// and Key Vault on startup. Generating migrations does not connect to the database: it compares the model with the last
/// migration, so the connection string here is a formality. The outbox dependencies have empty implementations.
/// <para>
/// AZURE: the pipeline builds a "migration bundle" from the migrations (a self-contained executable, the
/// inventory-migrations image) and runs it as a Container Apps job in the VNet, because the Azure SQL server has no
/// public endpoint (.github/actions/run-migrations). The bundle connects with the job's identity (Active Directory
/// Managed Identity), the only one with DDL permissions (db_owner). Application identities do not have them, so the
/// application does not migrate the database at startup: two replicas starting at the same time would run the same
/// migrations.
/// </para>
/// </remarks>
public sealed class DesignTimeInventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlServer(
                "Server=tcp:design-time,1433;Database=sqldb-inventory;Authentication=Active Directory Default",
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", MessagingModelBuilderExtensions.Schema))
            .Options;

        return new InventoryDbContext(options, new NoPendingOutbox(), new MessageContextAccessor(), new NoOutboxSignal(), TimeProvider.System);
    }

    private sealed class NoPendingOutbox : IPendingOutbox
    {
        public bool HasPending => false;

        public IReadOnlyList<OutboundMessage> Drain() => [];
    }

    private sealed class NoOutboxSignal : IOutboxSignal
    {
        public void Notify()
        {
        }

        public Task WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
