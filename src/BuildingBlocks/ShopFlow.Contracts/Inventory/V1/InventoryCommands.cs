using ShopFlow.Contracts.Common;

namespace ShopFlow.Contracts.Inventory.V1;

/// <summary>Reserve stock for an order. Saga step 1.</summary>
/// <remarks>
/// The operation is idempotent per <see cref="OrderId"/>: a repeated command for an order that
/// already has a reservation re-publishes <see cref="StockReserved"/> instead of reserving twice.
/// </remarks>
[MessageContract("ReserveStock", 1)]
public sealed record ReserveStock : ICommand, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required IReadOnlyList<StockLineDto> Lines { get; init; }
}

/// <summary>Release the order's reservation. Compensation for step 1.</summary>
[MessageContract("ReleaseStock", 1)]
public sealed record ReleaseStock : ICommand, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string Reason { get; init; }
}
