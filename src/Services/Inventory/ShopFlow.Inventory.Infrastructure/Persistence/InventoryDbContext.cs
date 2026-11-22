using Microsoft.EntityFrameworkCore;
using ShopFlow.Inventory.Domain.Catalog;
using ShopFlow.Inventory.Domain.Stock;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Persistence.SqlServer;
using ShopFlow.Persistence.SqlServer.Messaging;

namespace ShopFlow.Inventory.Infrastructure.Persistence;

/// <summary>
/// The Inventory database (Azure SQL Database, <c>sqldb-inventory</c>): catalog, stock levels, reservations, reference data,
/// and the outbox and inbox (messaging schema).
/// </summary>
/// <remarks>
/// AZURE: one database, four schemas. A schema is a permission boundary in SQL: the database role of the inventory-api
/// identity gets SELECT on catalog and ref and write access only where the API actually writes (the
/// ApplicationRoleGrants migration). Splitting into several databases would gain nothing here, would raise the cost
/// (every provisioned database is a separate bill) and would make a single "reservation + outbox" transaction impossible.
/// </remarks>
public sealed class InventoryDbContext(
    DbContextOptions<InventoryDbContext> options,
    IPendingOutbox pendingOutbox,
    IMessageContextAccessor contextAccessor,
    IOutboxSignal outboxSignal,
    TimeProvider timeProvider)
    : MessagingDbContext(options, pendingOutbox, contextAccessor, outboxSignal, timeProvider)
{
    public const string RowVersion = "RowVersion";

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    public DbSet<StockItem> StockItems => Set<StockItem>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    public DbSet<StockReceipt> StockReceipts => Set<StockReceipt>();

    public DbSet<Country> Countries => Set<Country>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
        modelBuilder.AddShopFlowMessagingTables();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // By default EF creates nvarchar(max) and decimal(18,2) with a warning. Explicit conventions give every text column a
        // limit (indexability, query plans) and amounts a fixed precision.
        configurationBuilder.Properties<string>().HaveMaxLength(256);
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }
}
