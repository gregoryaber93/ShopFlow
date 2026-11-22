using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Inventory.Domain.Stock;

public sealed class Warehouse
{
    private Warehouse()
    {
    }

    public Warehouse(string code, string name, int priority)
    {
        Code = code;
        Name = name;
        Priority = priority;
        IsActive = true;
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>A lower value = the warehouse picked first when reserving.</summary>
    public int Priority { get; private set; }

    public bool IsActive { get; private set; }
}

public sealed record StockLevelLowDomainEvent(string Sku, string WarehouseCode, int Available, int ReorderThreshold, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Stock level of one SKU in one warehouse.
/// </summary>
/// <remarks>
/// <c>OnHand</c> is the stock physically on the shelf, <c>Reserved</c> is the part assigned to orders in progress.
/// The difference is available for sale. The database also has a CHECK constraint (Reserved ≤ OnHand), so even a bug
/// in the code cannot save an oversell.
/// <para>
/// AZURE/SQL: the row has a <c>rowversion</c> column that SQL Server changes on every write. EF Core adds it to the
/// WHERE condition of the UPDATE. When two reservations change the same popular product at the same time, one of them
/// updates zero rows and gets a concurrency conflict; its message goes back to the queue and on retry reads the fresh state.
/// </para>
/// </remarks>
public sealed class StockItem : Entity<long>
{
    private StockItem()
        : base(0)
    {
    }

    public StockItem(string warehouseCode, string sku, int onHand, int reorderThreshold)
        : base(0)
    {
        WarehouseCode = warehouseCode;
        Sku = sku.ToUpperInvariant();
        OnHand = onHand;
        ReorderThreshold = reorderThreshold;
    }

    public string WarehouseCode { get; private set; } = string.Empty;

    public string Sku { get; private set; } = string.Empty;

    public int OnHand { get; private set; }

    public int Reserved { get; private set; }

    public int ReorderThreshold { get; private set; }

    public int Available => OnHand - Reserved;

    internal bool CanReserve(int quantity) => Available >= quantity;

    /// <returns>A low-stock event if the reservation crossed the replenishment threshold.</returns>
    internal StockLevelLowDomainEvent? Reserve(int quantity, DateTimeOffset now)
    {
        if (!CanReserve(quantity))
        {
            throw new DomainException("stock.insufficient", $"Only {Available} of {Sku} available in {WarehouseCode}, {quantity} requested.");
        }

        var wasAboveThreshold = Available > ReorderThreshold;
        Reserved += quantity;

        return wasAboveThreshold && Available <= ReorderThreshold
            ? new StockLevelLowDomainEvent(Sku, WarehouseCode, Available, ReorderThreshold, now)
            : null;
    }

    internal void Release(int quantity) => Reserved = Math.Max(0, Reserved - quantity);

    /// <summary>The order was confirmed: the stock stops being "reserved" and leaves the sellable stock permanently.</summary>
    internal void Commit(int quantity)
    {
        OnHand -= quantity;
        Reserved = Math.Max(0, Reserved - quantity);
    }

    /// <summary>Return of committed goods (compensation after confirmation).</summary>
    internal void Restock(int quantity) => OnHand += quantity;

    public void Receive(int quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException("stock.invalid_receipt", "Received quantity must be positive.");
        }

        OnHand += quantity;
    }
}
