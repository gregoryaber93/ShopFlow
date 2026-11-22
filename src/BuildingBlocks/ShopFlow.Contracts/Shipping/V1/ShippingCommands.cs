using ShopFlow.Contracts.Common;

namespace ShopFlow.Contracts.Shipping.V1;

/// <summary>Order a shipment from the carrier and generate a label. Saga step 3.</summary>
[MessageContract("CreateShipment", 1)]
public sealed record CreateShipment : ICommand, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string OrderNumber { get; init; }

    public required string WarehouseCode { get; init; }

    public required AddressDto Destination { get; init; }

    public required IReadOnlyList<StockLineDto> Lines { get; init; }

    /// <summary>Shipment identifier assigned by the saga (optional, since package version 1.2). See <c>AuthorizePayment.PaymentId</c>.</summary>
    public Guid? ShipmentId { get; init; }
}

/// <summary>Cancel the shipment if it has not been dispatched yet. Compensation for step 3.</summary>
[MessageContract("CancelShipment", 1)]
public sealed record CancelShipment : ICommand, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid ShipmentId { get; init; }

    public required string Reason { get; init; }
}
