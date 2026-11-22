using Microsoft.EntityFrameworkCore;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Domain.Catalog;
using ShopFlow.Inventory.Domain.Stock;

namespace ShopFlow.Inventory.Infrastructure.Persistence;

/// <summary>HTTP ETag from the rowversion column: <c>"AAAAAAAAB9E="</c> (quoted Base64).</summary>
internal static class RowVersionETag
{
    public static string? Format(byte[]? rowVersion) =>
        rowVersion is { Length: > 0 } ? $"\"{Convert.ToBase64String(rowVersion)}\"" : null;
}

internal sealed class StockRepository(InventoryDbContext db) : IStockRepository
{
    public Task<Reservation?> GetReservationByOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Reservations.SingleOrDefaultAsync(reservation => reservation.OrderId == orderId, cancellationToken);

    public Task<Reservation?> GetReservationAsync(Guid reservationId, CancellationToken cancellationToken) =>
        db.Reservations.SingleOrDefaultAsync(reservation => reservation.Id == reservationId, cancellationToken);

    public async Task<IReadOnlyList<Warehouse>> GetWarehousesAsync(CancellationToken cancellationToken) =>
        await db.Warehouses.AsNoTracking()
            .Where(warehouse => warehouse.IsActive)
            .OrderBy(warehouse => warehouse.Priority)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<StockItem>> GetStockAsync(IReadOnlyCollection<string> skus, string? warehouseCode, CancellationToken cancellationToken)
    {
        string[] normalized = [.. skus.Select(sku => sku.ToUpperInvariant()).Distinct()];

        // Tracked entities: stock changes will be saved in SaveChanges with a rowversion condition.
        return await db.StockItems
            .Where(item => normalized.Contains(item.Sku) && (warehouseCode == null || item.WarehouseCode == warehouseCode))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> FindExpiredReservationIdsAsync(DateTimeOffset now, int max, CancellationToken cancellationToken) =>
        await db.Reservations.AsNoTracking()
            .Where(reservation => reservation.Status == ReservationStatus.Active && reservation.ExpiresAt <= now)
            .OrderBy(reservation => reservation.ExpiresAt)
            .Select(reservation => reservation.Id)
            .Take(max)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<bool> ReceiptExistsAsync(Guid receiptId, CancellationToken cancellationToken) =>
        db.StockReceipts.AnyAsync(receipt => receipt.ReceiptId == receiptId, cancellationToken);

    public void Add(Reservation reservation) => db.Reservations.Add(reservation);

    public void Add(StockItem stockItem) => db.StockItems.Add(stockItem);

    public void Add(StockReceipt receipt) => db.StockReceipts.Add(receipt);
}

internal sealed class ProductRepository(InventoryDbContext db) : IProductRepository
{
    public async Task<Product?> GetAsync(string sku, CancellationToken cancellationToken)
    {
        var product = await db.Products
            .SingleOrDefaultAsync(item => item.Id == sku.ToUpperInvariant(), cancellationToken)
            .ConfigureAwait(false);

        product?.SetConcurrencyToken(RowVersionETag.Format(db.Entry(product).Property<byte[]>(InventoryDbContext.RowVersion).CurrentValue));
        return product;
    }
}

/// <summary>
/// SaveChanges with the outbox and inbox (MessagingDbContext) plus refreshing the ETags of saved aggregates.
/// </summary>
internal sealed class InventoryUnitOfWork(InventoryDbContext db) : IInventoryUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // AZURE/SQL: after an UPDATE EF Core reads the new rowversion value (OUTPUT inserted.RowVersion),
        // so the new ETag is available without an extra query.
        foreach (var entry in db.ChangeTracker.Entries<Product>())
        {
            entry.Entity.SetConcurrencyToken(RowVersionETag.Format(entry.Property<byte[]>(InventoryDbContext.RowVersion).CurrentValue));
        }
    }
}
