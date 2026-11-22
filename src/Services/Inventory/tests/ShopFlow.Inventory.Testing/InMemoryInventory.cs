using System.Collections.Concurrent;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Domain.Catalog;
using ShopFlow.Inventory.Domain.Stock;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Messaging.Outbox;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Inventory.Testing;

/// <summary>Inventory "database" state shared by the test's DI scopes.</summary>
public sealed class InMemoryInventoryState
{
    public List<Warehouse> Warehouses { get; } = [];

    public List<StockItem> Stock { get; } = [];

    public List<Reservation> Reservations { get; } = [];

    public Dictionary<string, (Product Product, int Version)> Products { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<StockReceipt> Receipts { get; } = [];

    public ConcurrentQueue<OutboundMessage> Published { get; } = new();

    public HashSet<InboxEntry> Inbox { get; } = [];

    public InMemoryInventoryState WithWarehouse(string code, int priority)
    {
        Warehouses.Add(new Warehouse(code, code, priority));
        return this;
    }

    public InMemoryInventoryState WithStock(string warehouseCode, string sku, int onHand, int reorderThreshold = 0)
    {
        Stock.Add(new StockItem(warehouseCode, sku, onHand, reorderThreshold));
        return this;
    }

    public InMemoryInventoryState WithProduct(string sku, decimal price)
    {
        Products[sku] = (Product.Create(sku, $"Product {sku}", null, 1, new Money(price, "PLN"), DateTimeOffset.UnixEpoch), 1);
        return this;
    }

    public StockItem StockOf(string warehouseCode, string sku) =>
        Stock.Single(item => item.WarehouseCode == warehouseCode && item.Sku == sku.ToUpperInvariant());

    public IReadOnlyList<string> PublishedTypes() => [.. Published.Select(message => message.Envelope.MessageType)];

    public void ClearPublished() => Published.Clear();
}

/// <summary>
/// In-memory Inventory ports with the same rules as the EF Core adapter: outbox and inbox written on SaveChanges,
/// one reservation per order, product versioning.
/// </summary>
/// <remarks>
/// Simplification: domain objects are shared with the state, so a change is visible even before SaveChanges and is not
/// rolled back after an error. Atomicity and rowversion are covered by the SQL Server integration tests.
/// </remarks>
public sealed class InMemoryInventoryStore(
    InMemoryInventoryState state,
    IPendingOutbox outbox,
    IMessageContextAccessor context) : IStockRepository, IProductRepository, IInventoryUnitOfWork
{
    private readonly List<Product> _loadedProducts = [];

    public Task<Reservation?> GetReservationByOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(state.Reservations.SingleOrDefault(reservation => reservation.OrderId == orderId));

    public Task<Reservation?> GetReservationAsync(Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(state.Reservations.SingleOrDefault(reservation => reservation.Id == reservationId));

    public Task<IReadOnlyList<Warehouse>> GetWarehousesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Warehouse>>([.. state.Warehouses.OrderBy(warehouse => warehouse.Priority)]);

    public Task<IReadOnlyList<StockItem>> GetStockAsync(IReadOnlyCollection<string> skus, string? warehouseCode, CancellationToken cancellationToken)
    {
        var keys = skus.Select(sku => sku.ToUpperInvariant()).ToHashSet();
        return Task.FromResult<IReadOnlyList<StockItem>>([.. state.Stock.Where(item => keys.Contains(item.Sku) && (warehouseCode is null || item.WarehouseCode == warehouseCode))]);
    }

    public Task<IReadOnlyList<Guid>> FindExpiredReservationIdsAsync(DateTimeOffset now, int max, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(
            [.. state.Reservations.Where(reservation => reservation.Status == ReservationStatus.Active && reservation.ExpiresAt <= now).Take(max).Select(reservation => reservation.Id)]);

    public Task<bool> ReceiptExistsAsync(Guid receiptId, CancellationToken cancellationToken) =>
        Task.FromResult(state.Receipts.Any(receipt => receipt.ReceiptId == receiptId));

    public void Add(Reservation reservation)
    {
        if (state.Reservations.Any(existing => existing.OrderId == reservation.OrderId))
        {
            throw new InvalidOperationException($"Unique index violation: reservation for order {reservation.OrderId} exists.");
        }

        state.Reservations.Add(reservation);
    }

    public void Add(StockItem stockItem) => state.Stock.Add(stockItem);

    public void Add(StockReceipt receipt) => state.Receipts.Add(receipt);

    public Task<Product?> GetAsync(string sku, CancellationToken cancellationToken)
    {
        if (!state.Products.TryGetValue(sku, out var entry))
        {
            return Task.FromResult<Product?>(null);
        }

        entry.Product.SetConcurrencyToken($"\"v{entry.Version}\"");
        _loadedProducts.Add(entry.Product);
        return Task.FromResult<Product?>(entry.Product);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (context.Current is { } incoming && !state.Inbox.Add(incoming.InboxEntry))
        {
            throw new DuplicateMessageException(incoming.InboxEntry);
        }

        foreach (var message in outbox.Drain())
        {
            state.Published.Enqueue(message);
        }

        foreach (var product in _loadedProducts)
        {
            var version = state.Products[product.Sku].Version + 1;
            state.Products[product.Sku] = (product, version);
            product.SetConcurrencyToken($"\"v{version}\"");
        }

        return Task.CompletedTask;
    }
}

/// <summary>Read side of the catalog backed by data set up by the test.</summary>
public sealed class FakeCatalogReader : ICatalogReader
{
    public List<ProductView> Products { get; } = [];

    public List<PriceView> Prices { get; } = [];

    public List<string> Invalidated { get; } = [];

    public Task<ProductView?> GetProductAsync(string sku, CancellationToken cancellationToken) =>
        Task.FromResult(Products.SingleOrDefault(product => string.Equals(product.Sku, sku, StringComparison.OrdinalIgnoreCase)));

    public Task<PagedResult<ProductSummary>> ListProductsAsync(int? categoryId, int page, int pageSize, CancellationToken cancellationToken) =>
        Task.FromResult(new PagedResult<ProductSummary>(
            [.. Products.Select(product => new ProductSummary(product.Sku, product.Name, product.Price, product.Available > 0))],
            page,
            pageSize,
            Products.Count));

    public Task<IReadOnlyList<PriceView>> GetPricesAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PriceView>>([.. Prices.Where(price => skus.Contains(price.Sku, StringComparer.OrdinalIgnoreCase))]);

    public Task<IReadOnlyList<CountryView>> GetCountriesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CountryView>>([new CountryView("PL", "Polska")]);

    public Task InvalidateProductAsync(string sku, CancellationToken cancellationToken)
    {
        Invalidated.Add(sku);
        return Task.CompletedTask;
    }
}
