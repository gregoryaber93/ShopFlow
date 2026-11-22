using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShopFlow.Inventory.Domain.Catalog;
using ShopFlow.Inventory.Domain.Stock;

namespace ShopFlow.Inventory.Infrastructure.Persistence.Configurations;

internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.ToTable("Warehouses", "inventory");
        builder.HasKey(warehouse => warehouse.Code);
        builder.Property(warehouse => warehouse.Code).HasMaxLength(16).IsUnicode(false);
        builder.Property(warehouse => warehouse.Name).HasMaxLength(100);

        builder.HasData(
            new Warehouse("WAW-1", "Warsaw Okecie", priority: 1),
            new Warehouse("KRK-1", "Krakow Balice", priority: 2));
    }
}

internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable("StockItems", "inventory", table =>
        {
            // WHY: the last line of defence against overselling. The domain checks availability, but if a code bug or a manual
            // UPDATE tried to reserve more than is on the shelf, the database would reject the write.
            table.HasCheckConstraint("CK_StockItems_Reserved", "[Reserved] >= 0 AND [Reserved] <= [OnHand]");
            table.HasCheckConstraint("CK_StockItems_OnHand", "[OnHand] >= 0");
        });

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).UseIdentityColumn();
        builder.Property(item => item.WarehouseCode).HasMaxLength(16).IsUnicode(false);
        builder.Property(item => item.Sku).HasMaxLength(64).IsUnicode(false);
        builder.Ignore(item => item.Available);
        builder.Property<byte[]>(InventoryDbContext.RowVersion).IsRowVersion();

        builder.HasIndex(item => new { item.WarehouseCode, item.Sku }).IsUnique();
        builder.HasIndex(item => item.Sku).IncludeProperties(item => new { item.OnHand, item.Reserved });

        builder.HasOne<Warehouse>().WithMany().HasForeignKey(item => item.WarehouseCode).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Product>().WithMany().HasForeignKey(item => item.Sku).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("Reservations", "inventory");
        builder.HasKey(reservation => reservation.Id);
        builder.Property(reservation => reservation.Id).ValueGeneratedNever();
        builder.Ignore(reservation => reservation.DomainEvents);
        builder.Ignore(reservation => reservation.ConcurrencyToken);

        builder.Property(reservation => reservation.Status).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(reservation => reservation.WarehouseCode).HasMaxLength(16).IsUnicode(false);
        builder.Property<byte[]>(InventoryDbContext.RowVersion).IsRowVersion();

        // At most one reservation (or tombstone) per order: this index settles the race between two concurrent commands
        // for the same order.
        builder.HasIndex(reservation => reservation.OrderId).IsUnique();

        // Filtered index for the expiry process's query: it covers only active reservations,
        // so it stays small despite millions of closed ones.
        builder.HasIndex(reservation => reservation.ExpiresAt)
            .HasDatabaseName("IX_Reservations_ActiveExpiry")
            .HasFilter("[Status] = 'Active'");

        builder.OwnsMany(reservation => reservation.Lines, lines =>
        {
            lines.ToTable("ReservationLines", "inventory");
            lines.WithOwner().HasForeignKey("ReservationId");
            lines.HasKey("ReservationId", nameof(ReservationLine.Sku));
            lines.Property(line => line.Sku).HasMaxLength(64).IsUnicode(false);
        });

        builder.Navigation(reservation => reservation.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class StockReceiptConfiguration : IEntityTypeConfiguration<StockReceipt>
{
    public void Configure(EntityTypeBuilder<StockReceipt> builder)
    {
        builder.ToTable("StockReceipts", "inventory");
        builder.HasKey(receipt => receipt.ReceiptId);
        builder.Property(receipt => receipt.ReceiptId).ValueGeneratedNever();
        builder.Property(receipt => receipt.WarehouseCode).HasMaxLength(16).IsUnicode(false);
        builder.Property(receipt => receipt.Sku).HasMaxLength(64).IsUnicode(false);
        builder.Property(receipt => receipt.ReceivedBy).HasMaxLength(128);
    }
}
