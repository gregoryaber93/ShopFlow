using ShopFlow.Contracts.Common;

namespace ShopFlow.Contracts.Inventory.V1;

[MessageContract("StockReserved", 1)]
public sealed record StockReserved : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid ReservationId { get; init; }

    /// <summary>Warehouse the stock was reserved in. Shipping needs it to dispatch the parcel.</summary>
    public required string WarehouseCode { get; init; }

    public required IReadOnlyList<StockLineDto> Lines { get; init; }

    public required DateTimeOffset ReservedAt { get; init; }

    /// <summary>After this moment an unconsumed reservation expires on its own.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

[MessageContract("StockReservationFailed", 1)]
public sealed record StockReservationFailed : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    /// <summary><c>insufficient_stock</c>, <c>unknown_sku</c>, <c>sku_discontinued</c>.</summary>
    public required string ReasonCode { get; init; }

    public required IReadOnlyList<UnavailableLineDto> UnavailableLines { get; init; }

    public required DateTimeOffset FailedAt { get; init; }
}

public sealed record UnavailableLineDto
{
    public required string Sku { get; init; }

    public required int Requested { get; init; }

    public required int Available { get; init; }
}

[MessageContract("StockReleased", 1)]
public sealed record StockReleased : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid ReservationId { get; init; }

    public required DateTimeOffset ReleasedAt { get; init; }
}

/// <summary>Available stock dropped below the replenishment threshold. Not tied to a specific order.</summary>
[MessageContract("StockLevelLow", 1)]
public sealed record StockLevelLow : IIntegrationEvent
{
    public required string Sku { get; init; }

    public required string WarehouseCode { get; init; }

    public required int Available { get; init; }

    public required int ReorderThreshold { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }
}

[MessageContract("ProductPriceChanged", 1)]
public sealed record ProductPriceChanged : IIntegrationEvent
{
    public required string Sku { get; init; }

    public required MoneyDto OldPrice { get; init; }

    public required MoneyDto NewPrice { get; init; }

    public required DateTimeOffset EffectiveFrom { get; init; }
}
