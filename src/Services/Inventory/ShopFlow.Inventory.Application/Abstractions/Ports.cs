using ShopFlow.Inventory.Domain.Catalog;
using ShopFlow.Inventory.Domain.Stock;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Inventory.Application.Abstractions;

/// <summary>Writes stock levels and reservations. Changes are committed through <see cref="IInventoryUnitOfWork"/>.</summary>
public interface IStockRepository
{
    Task<Reservation?> GetReservationByOrderAsync(Guid orderId, CancellationToken cancellationToken);

    Task<Reservation?> GetReservationAsync(Guid reservationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Warehouse>> GetWarehousesAsync(CancellationToken cancellationToken);

    /// <param name="skus">SKUs to load.</param>
    /// <param name="warehouseCode"><c>null</c> = all warehouses.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<StockItem>> GetStockAsync(IReadOnlyCollection<string> skus, string? warehouseCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindExpiredReservationIdsAsync(DateTimeOffset now, int max, CancellationToken cancellationToken);

    Task<bool> ReceiptExistsAsync(Guid receiptId, CancellationToken cancellationToken);

    void Add(Reservation reservation);

    void Add(StockItem stockItem);

    void Add(StockReceipt receipt);
}

public interface IProductRepository
{
    Task<Product?> GetAsync(string sku, CancellationToken cancellationToken);
}

/// <summary>
/// Commits changes in one SQL transaction: entities, outbox rows and the inbox entry.
/// After saving, aggregates have an updated <see cref="AggregateRoot{TId}.ConcurrencyToken"/> (new rowversion).
/// </summary>
public interface IInventoryUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record ProductView(
    string Sku,
    string Name,
    string? Description,
    int CategoryId,
    Money Price,
    bool IsActive,
    int Available,
    string ETag);

public sealed record ProductSummary(string Sku, string Name, Money Price, bool InStock);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record PriceView(string Sku, string ProductName, Money UnitPrice, bool IsAvailable);

public sealed record CountryView(string Code, string Name);

/// <summary>Read side of the catalog (no-tracking queries, cached in the implementation).</summary>
public interface ICatalogReader
{
    Task<ProductView?> GetProductAsync(string sku, CancellationToken cancellationToken);

    Task<PagedResult<ProductSummary>> ListProductsAsync(int? categoryId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Prices for checkout in Orders. Always from the database, no cache: the price at purchase must be current.</summary>
    Task<IReadOnlyList<PriceView>> GetPricesAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken);

    Task<IReadOnlyList<CountryView>> GetCountriesAsync(CancellationToken cancellationToken);

    /// <summary>Invalidates a product's cache entries after a change (no-op in the implementation without a cache).</summary>
    Task InvalidateProductAsync(string sku, CancellationToken cancellationToken);
}

public sealed class ReservationOptions
{
    public const string SectionName = "Reservations";

    /// <summary>
    /// Reservation lifetime. Must exceed the sum of the saga's timeouts up to confirmation
    /// (by default 2 + 15 + 5 + 5 = 27 minutes) with headroom for activity retries.
    /// </summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromMinutes(60);

    public int ExpiryBatchSize { get; set; } = 50;

    public TimeSpan ExpiryInterval { get; set; } = TimeSpan.FromMinutes(1);
}
